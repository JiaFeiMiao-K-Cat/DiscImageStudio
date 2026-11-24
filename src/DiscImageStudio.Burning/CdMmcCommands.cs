using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

/// <summary>READ DISC INFORMATION fields used by CD Track-at-Once recording (MMC-5 Table 376).</summary>
internal sealed record CdDiscInformation(
    int DiscStatus,
    int LastSessionState,
    int Sessions,
    int FirstTrackInLastSession,
    int LastTrackInLastSession,
    int DiscType)
{
    internal const int Empty = 0;
    internal const int Appendable = 1;
    internal const int Complete = 2;

    // State of Last Session, bits 3-2 of byte 2.
    internal const int SessionEmpty = 0;
    internal const int SessionIncomplete = 1;
    internal const int SessionComplete = 3;

    /// <summary>A multi-session close has finished and another session may follow. The
    /// tested recorder (HP BD-RE BU20N) then reports the new, empty session (0); cdrecord and
    /// libburn name 3 "complete", which a recorder may report for the session it has just
    /// closed. Both mean session 1 is closed; 1 (incomplete) means it is still open.</summary>
    internal bool IsAppendableAfterClose => DiscStatus == Appendable
        && LastSessionState is SessionEmpty or SessionComplete;
}

/// <summary>READ TRACK INFORMATION fields (MMC-5 Table 499).</summary>
internal sealed record CdTrackInformation(
    int TrackNumber,
    int SessionNumber,
    int TrackMode,
    int DataMode,
    bool Blank,
    bool NextWritableAddressValid,
    long TrackStartAddress,
    long NextWritableAddress,
    long FreeBlocks,
    long TrackSize);

/// <summary>CD Track at Once feature 002Dh (MMC-5 Table 146).</summary>
internal sealed record CdTaoFeature(int Profile, bool Current, bool BufferUnderrunFree, bool TestWrite, int DataTypes)
{
    internal bool SupportsDataBlockType(int type) => type is >= 0 and < 16 && (DataTypes & (1 << type)) != 0;
}

/// <summary>Write Parameters mode page 05h (MMC-5 7.5.4, Table 663) for Track-at-Once.</summary>
internal sealed record CdWriteParameters(
    int WriteType,
    int MultiSession,
    int TrackMode,
    int DataBlockType,
    int SessionFormat,
    bool BufferUnderrunFree,
    bool TestWrite,
    int AudioPauseLength = 150)
{
    internal const int TrackAtOnce = 1;
    /// <summary>11b: the next session is allowed; B0 = next possible program area.</summary>
    internal const int MultiSessionNextAllowed = 3;
    /// <summary>00b: no B0 pointer and no next session; closing finalizes the disc.</summary>
    internal const int MultiSessionNone = 0;
    /// <summary>Q control 0000: two-channel audio, no pre-emphasis, copy prohibited.</summary>
    internal const int AudioTrackMode = 0;
    /// <summary>Q control 0100: data track recorded uninterrupted, copy prohibited.</summary>
    internal const int DataTrackMode = 4;
    internal const int RawAudioBlock = 0;
    internal const int Mode1Block = 8;
    internal const int CdDaOrCdRomSession = 0x00;
    internal const int MinimumPageLength = 0x32;

    internal static CdWriteParameters AudioSession(bool bufferUnderrunFree, bool testWrite)
        => new(TrackAtOnce, MultiSessionNextAllowed, AudioTrackMode, RawAudioBlock, CdDaOrCdRomSession,
            bufferUnderrunFree, testWrite);

    internal static CdWriteParameters FinalDataSession(bool bufferUnderrunFree, bool testWrite)
        => new(TrackAtOnce, MultiSessionNone, DataTrackMode, Mode1Block, CdDaOrCdRomSession,
            bufferUnderrunFree, testWrite);

    /// <summary>The MODE SELECT(10) parameter list: an 8-byte header with no block
    /// descriptors, followed by the drive's own page 05h with these fields applied. The
    /// drive's page length and vendor bytes are kept; MCN, ISRC and sub-header are cleared.</summary>
    internal byte[] BuildModeSelectData(ReadOnlySpan<byte> sensedPage)
    {
        if (sensedPage.Length < 2 || (sensedPage[0] & 0x3f) != 0x05
            || sensedPage[1] < MinimumPageLength || sensedPage[1] + 2 > sensedPage.Length)
            throw new IOException("光驱返回的写入参数模式页 05h 无效。");
        int pageLength = sensedPage[1] + 2;
        byte[] data = new byte[8 + pageLength];
        Span<byte> page = data.AsSpan(8);
        sensedPage[..pageLength].CopyTo(page);
        page[0] = 0x05; // PS is reserved in MODE SELECT.
        page[2] = (byte)((BufferUnderrunFree ? 0x40 : 0) | (TestWrite ? 0x10 : 0) | (WriteType & 0x0f));
        page[3] = (byte)(((MultiSession & 3) << 6) | (TrackMode & 0x0f));
        page[4] = (byte)(DataBlockType & 0x0f);
        page[5] = 0; // Link size is not used for CD Track-at-Once.
        page[7] = 0;
        page[8] = (byte)SessionFormat;
        page.Slice(10, 4).Clear();
        BinaryPrimitives.WriteUInt16BigEndian(page.Slice(14, 2), (ushort)AudioPauseLength);
        page.Slice(16, 36).Clear(); // MCN, ISRC and the XA sub-header are not written.
        return data;
    }
}

/// <summary>
/// MMC commands for CD Track-at-Once recording of an Enhanced CD: write parameters, track
/// and disc state, cache synchronisation and session closing. No RESERVE TRACK, OPC or
/// blanking is issued; the recorder writes pre-gaps, run-out blocks, lead-in and lead-out.
/// Every command except TEST UNIT READY is repeated when the drive answers NOT READY /
/// operation in progress (02/04/01, 07, 08): such a command was not executed, and some
/// drives still answer so right after TEST UNIT READY reported GOOD. The same holds for
/// UNIT ATTENTION 06/28/00, which some drives raise once after a session is closed because
/// the table of contents changed (libburn repeats the command in that case too).
/// </summary>
internal sealed class CdMmcCommands(IMmcCommandTransport transport, Action<TimeSpan>? sleep = null)
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(15);
    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;

    internal CdTaoFeature ReadTaoFeature()
    {
        // GET CONFIGURATION, RT = 10b: only the CD Track at Once feature (002Dh).
        byte[] cdb = [0x46, 0x02, 0x00, 0x2d, 0, 0, 0, 0, 32, 0];
        byte[] data = ExecuteWhenReady(cdb, 32, 30).Data;
        if (data.Length < 8) throw new IOException("GET CONFIGURATION 返回的数据被截断。");
        int profile = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));
        if (data.Length < 16 || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8, 2)) != 0x002d)
            return new CdTaoFeature(profile, false, false, false, 0);
        return new CdTaoFeature(profile, (data[10] & 1) != 0, (data[12] & 0x40) != 0, (data[12] & 0x04) != 0,
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(14, 2)));
    }

    internal CdDiscInformation ReadDiscInformation()
    {
        byte[] disc = ExecuteWhenReady(CreateReadCdb(0x51, 64), 64, 30).Data;
        RequireResponseLength(disc, 12, 64, "READ DISC INFORMATION");
        return new CdDiscInformation(disc[2] & 3, (disc[2] >> 2) & 3, disc[4] | disc[9] << 8,
            disc[5] | disc[10] << 8, disc[6] | disc[11] << 8, disc[8]);
    }

    /// <summary>Track number that addresses the invisible (next, not yet written) track.</summary>
    internal const int InvisibleTrack = 0xff;

    internal CdTrackInformation ReadTrackInformation(int trackNumber)
    {
        if (trackNumber is <= 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(trackNumber));
        byte[] cdb = CreateReadCdb(0x52, 48);
        cdb[1] = 1; // Address/number type 01b: logical track number.
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), (uint)trackNumber);
        byte[] track = ExecuteWhenReady(cdb, 48, 30).Data;
        RequireResponseLength(track, 36, 48, "READ TRACK INFORMATION");
        int returned = track[2] | track[32] << 8;
        // The invisible track answers with its real number.
        if (returned != trackNumber && trackNumber != InvisibleTrack)
            throw new IOException($"光驱返回了不同的轨道编号（请求 {trackNumber}，返回 {returned}），已停止刻录操作。");
        if ((track[5] & 0x20) != 0)
            throw new IOException("CD 轨道已标记为损坏，已停止刻录操作。");
        return new CdTrackInformation(returned, track[3] | track[33] << 8, track[5] & 0x0f, track[6] & 0x0f,
            (track[6] & 0x40) != 0, (track[7] & 1) != 0,
            BinaryPrimitives.ReadInt32BigEndian(track.AsSpan(8, 4)),
            BinaryPrimitives.ReadInt32BigEndian(track.AsSpan(12, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(track.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(track.AsSpan(24, 4)));
    }

    /// <summary>The next, not yet written track of the open session: READ DISC
    /// INFORMATION counts it as the last track of the last session. A recorder that
    /// answers with a written track instead is asked for the invisible track (FFh), the
    /// way cdrecord and libburn find it; the caller checks whichever answer comes back.</summary>
    internal CdTrackInformation ReadNextWritableTrack()
    {
        CdDiscInformation disc = ReadDiscInformation();
        if (disc.LastTrackInLastSession <= 0)
            throw new IOException("光驱未报告可写入的 CD 轨道编号。");
        CdTrackInformation track = ReadTrackInformation(disc.LastTrackInLastSession);
        if (track.NextWritableAddressValid && track.Blank) return track;
        try
        {
            CdTrackInformation invisible = ReadTrackInformation(InvisibleTrack);
            return invisible.NextWritableAddressValid && invisible.Blank ? invisible : track;
        }
        catch (MmcCommandException)
        {
            return track;
        }
    }

    internal byte[] ModeSenseWriteParameters()
    {
        const int allocation = 128;
        // MODE SENSE(10), DBD = 1, current values of page 05h.
        byte[] cdb = [0x5a, 0x08, 0x05, 0, 0, 0, 0, 0, allocation, 0];
        byte[] data = ExecuteWhenReady(cdb, allocation, 30).Data;
        if (data.Length < 8) throw new IOException("MODE SENSE 返回的数据被截断。");
        int blockDescriptors = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));
        int offset = 8 + blockDescriptors;
        if (data.Length < offset + 2 || (data[offset] & 0x3f) != 0x05)
            throw new IOException("光驱没有返回写入参数模式页 05h。");
        int pageLength = data[offset + 1] + 2;
        if (data.Length < offset + pageLength)
            throw new IOException("写入参数模式页 05h 被截断。");
        return data.AsSpan(offset, pageLength).ToArray();
    }

    internal void ModeSelectWriteParameters(byte[] parameterList)
    {
        ArgumentNullException.ThrowIfNull(parameterList);
        if (parameterList.Length is < 10 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(parameterList));
        byte[] cdb = [0x55, 0x10, 0, 0, 0, 0, 0, 0, 0, 0]; // PF = 1, SP = 0.
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), (ushort)parameterList.Length);
        using AlignedMmcWriteBuffer buffer = new(parameterList.Length, transport.GetWriteTransferLimits());
        Marshal.Copy(parameterList, 0, buffer.Pointer, parameterList.Length);
        RetryWhileBusy(() =>
        {
            MmcCommandResult result = transport.SendData(cdb, buffer.Pointer, parameterList.Length, 30);
            ThrowIfFailed(cdb[0], result);
            return result;
        });
    }

    internal void PreventRemoval(bool prevent)
        => ExecuteWhenReady([0x1e, 0, 0, 0, prevent ? (byte)1 : (byte)0, 0], 0, 30);

    /// <summary>Drains the recorder cache. In Track-at-Once mode this also completes the
    /// current track (run-out blocks); the drive reports a long write in progress meanwhile.</summary>
    internal void SynchronizeCache()
    {
        ExecuteWhenReady([0x35, 0x02, 0, 0, 0, 0, 0, 0, 0, 0], 0, 120);
        WaitUntilReady();
    }

    /// <summary>CLOSE TRACK SESSION function 010b. With Multi-session 11b in page 05h the
    /// recorder writes the lead-in (with a B0 pointer) and lead-out and the disc stays
    /// appendable; with 00b it finalizes the disc.</summary>
    internal void CloseSession(bool expectFinalized)
    {
        try
        {
            ExecuteWhenReady([0x5b, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0], 0, 120);
        }
        catch (MmcCommandException)
        {
            // As for BD-R: a CLOSE that reports an error may still have closed the session
            // (or the drive closed it on its own). Accept only the exact expected state.
            if (IsSessionClosed(expectFinalized)) return;
            throw;
        }
        WaitUntilReady(CompletionTimeout);
        WaitUntilCompleted(() =>
        {
            CdDiscInformation disc = ReadDiscInformation();
            if (expectFinalized)
            {
                if (disc.DiscStatus == CdDiscInformation.Complete) return true;
                if (disc.DiscStatus != CdDiscInformation.Appendable)
                    throw new IOException($"封盘后光驱报告了意外的盘片状态 {disc.DiscStatus}。");
                return false;
            }
            if (disc.DiscStatus == CdDiscInformation.Complete)
                throw new IOException("关闭第 1 区段时光驱封闭了整张盘，无法再写入图片区段。");
            return disc.IsAppendableAfterClose;
        }, expectFinalized ? "等待光驱确认封盘完成超时。" : "等待光驱确认第 1 区段关闭超时。");
    }

    internal void WaitUntilReady(TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? CompletionTimeout;
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Execute([0, 0, 0, 0, 0, 0], 0, 30);
                return;
            }
            catch (MmcCommandException exception) when (IsNotExecuted(exception))
            {
                if (elapsed.Elapsed >= limit)
                    throw new TimeoutException("等待 CD 刻录机完成写入收尾超时。", exception);
                _sleep(TimeSpan.FromMilliseconds(200));
            }
        }
    }

    private void WaitUntilCompleted(Func<bool> isComplete, string timeoutMessage)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (isComplete()) return;
            }
            catch (MmcCommandException exception) when (IsNotExecuted(exception))
            {
                // A query may still report a long write in progress after TEST UNIT READY.
            }
            if (elapsed.Elapsed >= CompletionTimeout) throw new TimeoutException(timeoutMessage);
            _sleep(TimeSpan.FromMilliseconds(200));
            TimeSpan remaining = CompletionTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException(timeoutMessage);
            WaitUntilReady(remaining);
        }
    }

    private bool IsSessionClosed(bool expectFinalized)
    {
        try
        {
            CdDiscInformation disc = ReadDiscInformation();
            return expectFinalized
                ? disc.DiscStatus == CdDiscInformation.Complete
                : disc.IsAppendableAfterClose;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return false;
        }
    }

    private MmcCommandResult ExecuteWhenReady(byte[] cdb, int allocationLength, uint timeoutSeconds)
        => RetryWhileBusy(() => Execute(cdb, allocationLength, timeoutSeconds));

    /// <summary>Repeats a command the drive did not execute because it was busy, after
    /// TEST UNIT READY reports GOOD again; one deadline for the whole wait.</summary>
    private T RetryWhileBusy<T>(Func<T> command)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return command();
            }
            catch (MmcCommandException exception) when (IsNotExecuted(exception))
            {
                TimeSpan remaining = CompletionTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("CD 刻录机持续报告忙碌，命令未能执行。", exception);
                _sleep(TimeSpan.FromMilliseconds(200));
                WaitUntilReady(remaining);
            }
        }
    }

    /// <summary>The drive refused the command without executing it and will take it again:
    /// a long write is in progress, or it reported the changed table of contents first.</summary>
    private static bool IsNotExecuted(MmcCommandException exception)
        => exception.IsOperationInProgress
            || exception is { SenseKey: 6, AdditionalSenseCode: 0x28, AdditionalSenseQualifier: 0 };

    private MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
    {
        MmcCommandResult result = transport.Execute(cdb, allocationLength, timeoutSeconds);
        ThrowIfFailed(cdb[0], result);
        return result;
    }

    internal static void ThrowIfFailed(byte opcode, MmcCommandResult result)
    {
        // S_IMAPI_COMMAND_HAS_SENSE_DATA is positive but means the command failed.
        if (result.HResult < 0 || result.HResult == BluRayMmcCommands.CommandHasSenseData)
            throw new MmcCommandException(opcode, result);
    }

    private static byte[] CreateReadCdb(byte opcode, ushort allocationLength)
    {
        byte[] cdb = new byte[10];
        cdb[0] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), allocationLength);
        return cdb;
    }

    private static void RequireResponseLength(byte[] bytes, int minimum, int allocationLength, string command)
    {
        if (bytes.Length < minimum)
            throw new IOException($"{command} 返回的数据被截断（{bytes.Length} 字节）。");
        int declared = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2)) + 2;
        if (declared < minimum || bytes.Length < Math.Min(declared, allocationLength))
            throw new IOException($"{command} 返回了无效的长度字段。");
    }
}
