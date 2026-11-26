using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

/// <summary>CD-TEXT ready for the lead-in of an audio session: complete 18-byte packs of
/// block 0 (header, payload, CRC), built by the application.</summary>
public sealed record CdTextLeadIn(byte[] Packs)
{
    public const int PackBytes = 18;
    public int PackCount => Packs.Length / PackBytes;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Packs);
        if (Packs.Length == 0 || Packs.Length % PackBytes != 0 || PackCount > 256)
            throw new ArgumentOutOfRangeException(nameof(Packs), "CD-TEXT must be 1..256 packs of 18 bytes.");
    }

    /// <summary>The packs as R-W subchannel symbols: each 18-byte pack becomes 24 bytes with
    /// six significant bits (MMC-3 Annex J; libburn doc/cookbook.txt).</summary>
    internal byte[] ToSubchannel()
    {
        byte[] result = new byte[Packs.Length / 3 * 4];
        for (int source = 0, target = 0; source < Packs.Length; source += 3, target += 4)
        {
            int value = Packs[source] << 16 | Packs[source + 1] << 8 | Packs[source + 2];
            result[target] = (byte)(value >> 18 & 0x3f);
            result[target + 1] = (byte)(value >> 12 & 0x3f);
            result[target + 2] = (byte)(value >> 6 & 0x3f);
            result[target + 3] = (byte)(value & 0x3f);
        }
        return result;
    }
}

/// <summary>CD Mastering feature 002Eh (MMC-5 Table 147): Session-at-Once recording.</summary>
internal sealed record CdSaoCapability(bool Present, bool Current, bool SessionAtOnce, bool RawSubcode,
    bool TestWrite, bool BufferUnderrunFree, int MaximumCueSheetLength)
{
    internal static CdSaoCapability None { get; } = new(false, false, false, false, false, false, 0);

    /// <summary>CD-TEXT is written by the host into the lead-in of an SAO session.</summary>
    internal bool CanWriteCdText => Current && SessionAtOnce;

    internal string Describe() => Present
        ? $"SAO={SessionAtOnce}；Current={Current}；R-W={RawSubcode}；TestWrite={TestWrite}；BUF={BufferUnderrunFree}；MaxCue={MaximumCueSheetLength}"
        : "光驱未报告 CD Mastering（002Eh）功能";
}

/// <summary>
/// The audio session written Session-at-Once so its lead-in can carry CD-TEXT. Track
/// addresses are chosen to equal the Track-at-Once layout the rest of the application plans
/// with (<c>CdExtraLayout</c>): track 1 starts at LBA 0 behind a 150-block pre-gap, every
/// later track has a 152-block INDEX 00 pre-gap (the TAO run-out plus pause), and two blocks of
/// silence close the last track, so the lead-out (and with it the second session of a
/// CD-Extra disc) starts where a TAO recording would put it. All gaps are digital silence.
/// </summary>
internal sealed record CdSaoLayout(IReadOnlyList<long> TrackSectors, bool HasCdText, int TrailingSilence = CdSaoLayout.TrailingSilenceSectors)
{
    internal const int FirstPregapSectors = 150;
    internal const int LaterPregapSectors = 152;
    internal const int TrailingSilenceSectors = 2;
    internal const int LeadInBlockBytes = 96;

    internal IReadOnlyList<long> TrackStarts
    {
        get
        {
            long[] starts = new long[TrackSectors.Count];
            long next = 0;
            for (int i = 0; i < starts.Length; i++)
            {
                if (i > 0) next += LaterPregapSectors;
                starts[i] = next;
                next += TrackSectors[i];
            }
            return starts;
        }
    }

    internal long LeadoutLba => TrackStarts[^1] + TrackSectors[^1] + TrailingSilence;

    /// <summary>SEND CUE SHEET entries (MMC-5 6.33): lead-in, then per track INDEX 00 and
    /// INDEX 01, then the lead-out. CTL is 00h (two-channel audio, copy prohibited) as in
    /// the TAO audio parameters; DATA FORM 01h + 40h marks CD-TEXT in the lead-in.</summary>
    internal byte[] BuildCueSheet()
    {
        if (TrackSectors.Count is 0 or > 99) throw new ArgumentOutOfRangeException(nameof(TrackSectors));
        List<byte[]> entries = [[0x01, 0x00, 0x00, (byte)(HasCdText ? 0x41 : 0x01), 0, 0, 0, 0]];
        IReadOnlyList<long> starts = TrackStarts;
        for (int i = 0; i < starts.Count; i++)
        {
            byte track = (byte)(i + 1);
            long pregap = starts[i] - (i == 0 ? FirstPregapSectors : LaterPregapSectors);
            entries.Add(Entry(track, 0, pregap));
            entries.Add(Entry(track, 1, starts[i]));
        }
        entries.Add(Entry(0xaa, 1, LeadoutLba, dataForm: 0x01));
        return entries.SelectMany(entry => entry).ToArray();
    }

    private static byte[] Entry(byte track, byte index, long lba, byte dataForm = 0x00)
    {
        (byte m, byte s, byte f) = Msf(lba);
        return [0x01, track, index, dataForm, 0x00, m, s, f];
    }

    internal static (byte M, byte S, byte F) Msf(long lba)
    {
        long frames = lba + 150;
        if (frames < 0 || frames >= 100 * 60 * 75) throw new ArgumentOutOfRangeException(nameof(lba));
        return ((byte)(frames / 4500), (byte)(frames / 75 % 60), (byte)(frames % 75));
    }
}

/// <summary>Streams SAO blocks with WRITE(10). Lead-in addresses are negative and sent as
/// 32-bit two's complement; a WRITE answered with NOT READY was not executed and is repeated.
/// Unlike the TAO picture writer there is no partial-write recovery: an SAO session cannot be
/// resumed, so any other failure ends the burn before session 1 is closed.</summary>
internal static class CdSaoWriter
{
    internal const int AudioBlockBytes = 2352;
    private const int AudioBlocksPerWrite = 27;
    private const int LeadInBlocksPerWrite = 32;
    private static readonly TimeSpan NotReadyLimit = TimeSpan.FromSeconds(60);

    internal static byte[] CreateWriteCommand(long lba, int blocks)
    {
        if (lba is < int.MinValue or > int.MaxValue || blocks is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(lba));
        byte[] cdb = new byte[10];
        cdb[0] = 0x2a;
        BinaryPrimitives.WriteInt32BigEndian(cdb.AsSpan(2, 4), (int)lba);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), (ushort)blocks);
        return cdb;
    }

    /// <summary>Fills the lead-in from its start to LBA −151 with the CD-TEXT packs, four per
    /// 96-byte block, repeated cyclically.</summary>
    internal static void WriteLeadIn(IMmcCommandTransport transport, long leadInStart, byte[] subchannelPacks,
        Func<bool> cancelled, Action<TimeSpan> sleep)
    {
        if (leadInStart >= -150) throw new ArgumentOutOfRangeException(nameof(leadInStart));
        if (subchannelPacks.Length == 0 || subchannelPacks.Length % 24 != 0) throw new ArgumentException("Invalid subchannel packs.", nameof(subchannelPacks));
        long blocks = -150 - leadInStart;
        int packCount = subchannelPacks.Length / 24;
        long pack = 0;
        WriteBlocks(transport, leadInStart, blocks, CdSaoLayout.LeadInBlockBytes, LeadInBlocksPerWrite,
            (buffer, count) =>
            {
                for (int i = 0; i < count * 4; i++, pack++)
                    subchannelPacks.AsSpan((int)(pack % packCount) * 24, 24).CopyTo(buffer.AsSpan(i * 24));
            }, cancelled, sleep, null);
    }

    internal static void WriteSilence(IMmcCommandTransport transport, long lba, long blocks,
        Func<bool> cancelled, Action<TimeSpan> sleep)
        => WriteBlocks(transport, lba, blocks, AudioBlockBytes, AudioBlocksPerWrite,
            (buffer, count) => buffer.AsSpan(0, count * AudioBlockBytes).Clear(), cancelled, sleep, null);

    internal static void WriteAudio(IMmcCommandTransport transport, IStream content, long lba, long blocks,
        Func<bool> cancelled, Action<TimeSpan> sleep, Action<long>? reportAcceptedBytes)
    {
        content.Stat(out STATSTG stat, 1);
        if (stat.cbSize != blocks * AudioBlockBytes)
            throw new ArgumentException("CD 写入流长度与计划的块数不一致。", nameof(content));
        IntPtr countPointer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            WriteBlocks(transport, lba, blocks, AudioBlockBytes, AudioBlocksPerWrite, (buffer, count) =>
            {
                int length = count * AudioBlockBytes, filled = 0;
                while (filled < length)
                {
                    if (cancelled()) throw new OperationCanceledException("CD 刻录已取消。");
                    byte[] chunk = new byte[length - filled];
                    Marshal.WriteInt32(countPointer, 0);
                    content.Read(chunk, chunk.Length, countPointer);
                    int read = Marshal.ReadInt32(countPointer);
                    if (read <= 0 || read > chunk.Length)
                        throw new EndOfStreamException($"CD 写入流未提供完整写入块：已读取 {filled} / {length} 字节。");
                    chunk.AsSpan(0, read).CopyTo(buffer.AsSpan(filled));
                    filled += read;
                }
            }, cancelled, sleep, reportAcceptedBytes);
        }
        finally { Marshal.FreeHGlobal(countPointer); }
    }

    private static void WriteBlocks(IMmcCommandTransport transport, long lba, long blocks, int blockBytes,
        int preferredBlocks, Action<byte[], int> fill, Func<bool> cancelled, Action<TimeSpan> sleep,
        Action<long>? reportAcceptedBytes)
    {
        if (blocks <= 0) return;
        MmcWriteTransferLimits limits = transport.GetWriteTransferLimits();
        int perWrite = (int)Math.Min(preferredBlocks, Math.Max(1, limits.MaximumPageAlignedBytes / (uint)blockBytes));
        using AlignedMmcWriteBuffer aligned = new(perWrite * blockBytes, limits);
        byte[] buffer = new byte[perWrite * blockBytes];
        long accepted = 0;
        for (long done = 0; done < blocks; done += perWrite)
        {
            if (cancelled()) throw new OperationCanceledException("CD 刻录已取消。");
            int count = (int)Math.Min(perWrite, blocks - done);
            fill(buffer, count);
            int bytes = count * blockBytes;
            Marshal.Copy(buffer, 0, aligned.Pointer, bytes);
            long address = lba + done;
            byte[] cdb = CreateWriteCommand(address, count);
            Stopwatch? busy = null;
            while (true)
            {
                if (cancelled()) throw new OperationCanceledException("CD 刻录已取消。");
                Exception failure;
                try
                {
                    MmcCommandResult result = transport.SendData(cdb, aligned.Pointer, bytes, CdTaoWriter.WriteTimeoutSeconds);
                    if (result.HResult >= 0 && result.HResult != BluRayMmcCommands.CommandHasSenseData) break;
                    failure = new MmcCommandException(0x2a, result);
                }
                catch (Exception exception) when (exception is COMException or IOException or TimeoutException)
                {
                    failure = exception;
                }
                if (failure is MmcCommandException { IsOperationInProgress: true, IsDeferredSense: false })
                {
                    busy ??= Stopwatch.StartNew();
                    if (busy.Elapsed < NotReadyLimit)
                    {
                        sleep(TimeSpan.FromMilliseconds(50));
                        continue;
                    }
                }
                throw new IOException($"SAO 写入命令在 LBA {address:N0}（{count} 块 × {blockBytes} 字节）失败：{failure.Message}", failure);
            }
            accepted += bytes;
            try { reportAcceptedBytes?.Invoke(accepted); }
            catch { /* Progress observers must not interrupt a physical write. */ }
        }
    }
}

internal sealed partial class CdMmcCommands
{
    internal CdSaoCapability ReadSaoFeature()
    {
        // GET CONFIGURATION, RT = 10b: only the CD Mastering feature (002Eh).
        byte[] cdb = [0x46, 0x02, 0x00, 0x2e, 0, 0, 0, 0, 32, 0];
        byte[] data = ExecuteWhenReady(cdb, 32, 30).Data;
        if (data.Length < 16 || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8, 2)) != 0x002e || data[11] < 4)
            return CdSaoCapability.None;
        byte flags = data[12];
        int cue = data[13] << 16 | data[14] << 8 | data[15];
        return new CdSaoCapability(true, (data[10] & 1) != 0, (flags & 0x20) != 0, (flags & 0x01) != 0,
            (flags & 0x04) != 0, (flags & 0x40) != 0, cue);
    }

    /// <summary>Start Time of Lead-in from the ATIP (READ TOC/PMA/ATIP format 0100b, bytes
    /// 8–10); minute ≥ 90 encodes a negative address: LBA = (M·60+S)·75+F − 450150.</summary>
    internal long ReadLeadInStart()
    {
        const byte allocation = 64;
        byte[] data = ExecuteWhenReady([0x43, 0x02, 0x04, 0, 0, 0, 0, 0, allocation, 0], allocation, 30).Data;
        if (data.Length < 11) throw new IOException("光驱返回的 ATIP 不包含导入区起点。");
        int m = data[8], s = data[9], f = data[10];
        if (m < 90 || m >= 100 || s >= 60 || f >= 75)
            throw new IOException($"光驱返回的导入区起点无效（{m:D2}:{s:D2}:{f:D2}）。");
        long lba = (m * 60L + s) * 75 + f - 450150;
        if (lba >= -150) throw new IOException($"导入区起点 LBA {lba} 不在节目区之前。");
        return lba;
    }

    internal void SendCueSheet(byte[] cueSheet)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        if (cueSheet.Length == 0 || cueSheet.Length % 8 != 0 || cueSheet.Length > 0xffffff)
            throw new ArgumentOutOfRangeException(nameof(cueSheet));
        byte[] cdb = [0x5d, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        cdb[6] = (byte)(cueSheet.Length >> 16);
        cdb[7] = (byte)(cueSheet.Length >> 8);
        cdb[8] = (byte)cueSheet.Length;
        using AlignedMmcWriteBuffer buffer = new(cueSheet.Length, transport.GetWriteTransferLimits());
        Marshal.Copy(cueSheet, 0, buffer.Pointer, cueSheet.Length);
        RetryWhileBusy(() =>
        {
            MmcCommandResult result = transport.SendData(cdb, buffer.Pointer, cueSheet.Length, 60);
            ThrowIfFailed(cdb[0], result);
            return result;
        });
    }

    /// <summary>Ends an SAO session: SYNCHRONIZE CACHE, then wait until the disc reports the
    /// session closed — appendable with room for a next session, or complete when finalized.</summary>
    internal void CompleteSessionAtOnce(bool finalize)
    {
        SynchronizeCache();
        WaitUntilCompleted(() =>
        {
            CdDiscInformation disc = ReadDiscInformation();
            if (finalize) return disc.DiscStatus == CdDiscInformation.Complete;
            if (disc.DiscStatus == CdDiscInformation.Complete)
                throw new IOException("写完第 1 区段后光驱封闭了整张盘，无法再写入图片区段。");
            return disc.IsAppendableAfterClose;
        }, finalize ? "等待光驱确认封盘完成超时。" : "等待光驱确认第 1 区段完成超时。");
    }

    /// <summary>READ TOC/PMA/ATIP format 0101b: the CD-TEXT packs of the lead-in, without
    /// the 4-byte header. Empty when the disc has none.</summary>
    internal byte[] ReadCdTextPacks()
    {
        byte[] head = ExecuteWhenReady([0x43, 0x00, 0x05, 0, 0, 0, 0, 0, 4, 0], 4, 30).Data;
        if (head.Length < 4) return [];
        int length = BinaryPrimitives.ReadUInt16BigEndian(head) + 2;
        if (length <= 4) return [];
        ushort allocation = (ushort)Math.Min(ushort.MaxValue - 1, length);
        byte[] cdb = [0x43, 0x00, 0x05, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), allocation);
        byte[] data = ExecuteWhenReady(cdb, allocation, 30).Data;
        int packs = (Math.Min(data.Length, allocation) - 4) / CdTextLeadIn.PackBytes;
        return packs <= 0 ? [] : data.AsSpan(4, packs * CdTextLeadIn.PackBytes).ToArray();
    }
}
