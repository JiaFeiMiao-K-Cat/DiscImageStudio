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
        HResult = inner.HResult;
    }

    public long Lba { get; }
    public int Blocks { get; }
}

/// <summary>
/// Streams one Track-at-Once track with WRITE(10) commands starting at the recorder's next
/// writable address. A WRITE answered with NOT READY / operation in progress (02/04/01,
/// 07, 08) was not executed, so it is repeated after a short pause, as libburn and cdrecord
/// do; every other failure stops the track without a retry.
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
        Action<TimeSpan>? sleep = null)
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
                byte[] cdb = CreateWriteCommand(lba, blocks);
                Stopwatch? notReady = null;
                while (true)
                {
                    MmcCommandResult result;
                    try { result = transport.SendData(cdb, buffer.Pointer, bytes, WriteTimeoutSeconds); }
                    catch (Exception exception) when (exception is COMException or IOException)
                    {
                        throw new CdTaoWriteException(lba, blocks, blockBytes, exception);
                    }
                    if (result.HResult >= 0 && result.HResult != BluRayMmcCommands.CommandHasSenseData) break;
                    MmcCommandException failure = new(0x2a, result);
                    if (failure.IsOperationInProgress)
                    {
                        notReady ??= Stopwatch.StartNew();
                        if (notReady.Elapsed < NotReadyLimit)
                        {
                            sleep(TimeSpan.FromMilliseconds(50));
                            ThrowIfCancelled(isCancellationRequested);
                            continue;
                        }
                    }
                    throw new CdTaoWriteException(lba, blocks, blockBytes, failure);
                }

                accepted += bytes;
                try { reportAcceptedBytes?.Invoke(accepted); }
                catch { /* Progress observers must not interrupt a physical write. */ }
            }
        }
        finally { Marshal.FreeHGlobal(countPointer); }
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
