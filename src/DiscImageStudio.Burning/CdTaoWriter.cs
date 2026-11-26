using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

/// <summary>A failed CD Track-at-Once WRITE, with the command's address and sense data.</summary>
internal sealed class CdTaoWriteException : IOException
{
    internal CdTaoWriteException(long lba, int blocks, int blockBytes, Exception inner)
        : base($"CD 写入命令在 LBA {lba:N0}（{blocks} 块 × {blockBytes} 字节）失败：{inner.Message}", inner)
    {
        Lba = lba;
        Blocks = blocks;
        BlockBytes = blockBytes;
        HResult = inner.HResult;
    }

    public long Lba { get; }
    public int Blocks { get; }
    internal int BlockBytes { get; }
}

/// <summary>
/// Streams one Track-at-Once track with WRITE(10) commands starting at the recorder's next
/// writable address. A WRITE answered with NOT READY / operation in progress (02/04/01,
/// 07, 08) was not executed, so it is repeated after a short pause, as libburn and cdrecord
/// do. For real picture writes the caller can reconcile an uncertain acknowledgement against
/// the open track and its stored bytes.
/// </summary>
internal static class CdTaoWriter
{
    internal const int AudioBlockBytes = 2352;
    internal const int DataBlockBytes = 2048;
    internal const int AudioBlocksPerWrite = 27;
    internal const int DataBlocksPerWrite = 32;
    internal const uint WriteTimeoutSeconds = 200;
    private static readonly TimeSpan NotReadyLimit = TimeSpan.FromSeconds(60);

    internal static int BlocksPerWrite(int blockBytes, MmcWriteTransferLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        int preferred = blockBytes == AudioBlockBytes ? AudioBlocksPerWrite : DataBlocksPerWrite;
        long fit = limits.MaximumPageAlignedBytes / (uint)blockBytes;
        if (fit <= 0)
            throw new NotSupportedException($"刻录适配器单次传输上限 {limits.MaximumPageAlignedBytes:N0} 字节，不足一个 {blockBytes} 字节的 CD 块。");
        return (int)Math.Min(preferred, fit);
    }

    internal static byte[] CreateWriteCommand(long lba, int blocks)
    {
        if (lba is < 0 or > int.MaxValue || blocks is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(lba));
        byte[] cdb = new byte[10];
        cdb[0] = 0x2a;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), (uint)lba);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), (ushort)blocks);
        return cdb;
    }

    internal static void Write(IMmcCommandTransport transport, IStream content, long startLba, long blockCount,
        int blockBytes, Action<long>? reportAcceptedBytes = null, Func<bool>? isCancellationRequested = null,
        Action<TimeSpan>? sleep = null, Func<long, ReadOnlyMemory<byte>, int>? checkPictureWrite = null,
        Action<string>? reportRecovery = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(content);
        if (blockBytes is not (AudioBlockBytes or DataBlockBytes))
            throw new ArgumentOutOfRangeException(nameof(blockBytes));
        if (startLba < 0 || blockCount <= 0 || startLba + blockCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(blockCount));
        sleep ??= Thread.Sleep;
        long length = checked(blockCount * blockBytes);
        content.Stat(out STATSTG stat, 1);
        if (stat.cbSize != length)
            throw new ArgumentException("CD 写入流长度与计划的块数不一致。", nameof(content));
        ThrowIfCancelled(isCancellationRequested);

        MmcWriteTransferLimits limits = transport.GetWriteTransferLimits();
        int blocksPerWrite = BlocksPerWrite(blockBytes, limits);
        int bufferBytes = blocksPerWrite * blockBytes;
        using AlignedMmcWriteBuffer buffer = new(bufferBytes, limits);
        byte[] readBuffer = new byte[bufferBytes];
        IntPtr countPointer = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            long accepted = 0;
            for (long block = 0; block < blockCount; block += blocksPerWrite)
            {
                ThrowIfCancelled(isCancellationRequested);
                int blocks = (int)Math.Min(blocksPerWrite, blockCount - block);
                int bytes = blocks * blockBytes;
                ReadBlock(content, readBuffer, bytes, buffer.Pointer, countPointer, isCancellationRequested);
                long lba = startLba + block;
                WriteBatch(transport, lba, blocks, blockBytes, buffer.Pointer, readBuffer, sleep,
                    isCancellationRequested, checkPictureWrite, reportRecovery);

                accepted += bytes;
                try { reportAcceptedBytes?.Invoke(accepted); }
                catch { /* Progress observers must not interrupt a physical write. */ }
            }
        }
        finally { Marshal.FreeHGlobal(countPointer); }
    }

    private static void WriteBatch(IMmcCommandTransport transport, long lba, int blocks, int blockBytes,
        IntPtr buffer, byte[] scratch, Action<TimeSpan> sleep, Func<bool>? cancelled,
        Func<long, ReadOnlyMemory<byte>, int>? checkPictureWrite, Action<string>? reportRecovery)
    {
        byte[] cdb = CreateWriteCommand(lba, blocks);
        Stopwatch? busy = null;
        int retries = 0;
        while (true)
        {
            ThrowIfCancelled(cancelled);
            int bytes = blocks * blockBytes;
            Exception failure;
            try
            {
                var result = transport.SendData(cdb, buffer, bytes, WriteTimeoutSeconds);
                if (result.HResult >= 0 && result.HResult != BluRayMmcCommands.CommandHasSenseData) return;
                failure = new MmcCommandException(0x2a, result);
            }
            catch (Exception exception) when (exception is COMException or IOException or TimeoutException)
            {
                failure = exception;
            }

            // This response gives no reliable completion count. Leave the handle before any retry.
            if (failure.HResult == CdExtraWriteWindow.UnexpectedResponse && checkPictureWrite is not null)
                throw new CdTaoWriteException(lba, blocks, blockBytes, failure);
            if (failure is MmcCommandException { IsOperationInProgress: true, IsDeferredSense: false })
            {
                busy ??= Stopwatch.StartNew();
                if (busy.Elapsed >= NotReadyLimit) throw new CdTaoWriteException(lba, blocks, blockBytes, failure);
                sleep(TimeSpan.FromMilliseconds(50));
                continue;
            }
            if (checkPictureWrite is null || !MayHaveWritten(failure))
                throw new CdTaoWriteException(lba, blocks, blockBytes, failure);

            ThrowIfCancelled(cancelled);
            // IStream may be forward-only, and the adapter may require aligned transfers.
            // Keep this batch, and copy any unwritten suffix back to the aligned base address.
            Marshal.Copy(buffer, scratch, 0, bytes);
            int confirmed;
            try
            {
                confirmed = CheckWhenReady(lba, scratch.AsMemory(0, bytes), checkPictureWrite, sleep, cancelled);
            }
            catch (Exception probe) when (probe is IOException or COMException or TimeoutException)
            {
                throw new CdTaoWriteException(lba, blocks, blockBytes,
                    new IOException($"{failure.Message} 无法确认写入位置：{probe.Message}", probe));
            }

            if (confirmed < 0 || confirmed > blocks)
                throw new CdTaoWriteException(lba, blocks, blockBytes, new IOException("续写位置超出当前批次。", failure));
            try { reportRecovery?.Invoke($"图案写入 LBA {lba}（{blocks} 扇区）：{failure.Message} 已核对 {confirmed} 扇区，下一位置 {lba + confirmed}。"); }
            catch { /* Diagnostic observers must not affect recording. */ }
            lba += confirmed;
            blocks -= confirmed;
            if (blocks == 0) return;
            if (++retries > 2) throw new CdTaoWriteException(lba, blocks, blockBytes, failure);
            Marshal.Copy(scratch, confirmed * blockBytes, buffer, blocks * blockBytes);
            cdb = CreateWriteCommand(lba, blocks);
            sleep(TimeSpan.FromMilliseconds(100 * retries));
        }
    }

    private static bool MayHaveWritten(Exception failure)
    {
        if (failure is MmcCommandException { HasValidSense: true } sense)
            return !sense.IsDeferredSense && sense.SenseKey is 1 or 0x0b; // recovered or aborted command
        return failure is TimeoutException || (uint)failure.HResult is
            0xc0aa020d or 0x80070079 or 0x800705b4 or 0xc0aa0205 or 0xc0aa0207;
    }

    private static int CheckWhenReady(long lba, ReadOnlyMemory<byte> expected,
        Func<long, ReadOnlyMemory<byte>, int> check, Action<TimeSpan> sleep, Func<bool>? cancelled)
    {
        for (int attempt = 1; ; attempt++)
        {
            ThrowIfCancelled(cancelled);
            try { return check(lba, expected); }
            catch (MmcCommandException error) when (attempt < 6 && !error.IsDeferredSense && error.IsOperationInProgress)
            {
                sleep(TimeSpan.FromMilliseconds(200));
            }
        }
    }

    private static void ReadBlock(IStream content, byte[] readBuffer, int length, IntPtr destination,
        IntPtr countPointer, Func<bool>? isCancellationRequested)
    {
        int filled = 0;
        while (filled < length)
        {
            ThrowIfCancelled(isCancellationRequested);
            int requested = length - filled;
            Marshal.WriteInt32(countPointer, 0);
            content.Read(readBuffer, requested, countPointer);
            int read = Marshal.ReadInt32(countPointer);
            if (read <= 0 || read > requested)
                throw new EndOfStreamException($"CD 写入流未提供完整写入块：已读取 {filled} / {length} 字节，本次返回 {read} 字节。");
            Marshal.Copy(readBuffer, 0, IntPtr.Add(destination, filled), read);
            filled += read;
        }
    }

    private static void ThrowIfCancelled(Func<bool>? isCancellationRequested)
    {
        if (isCancellationRequested?.Invoke() == true)
            throw new OperationCanceledException("CD-Extra 刻录已取消。");
    }
}
