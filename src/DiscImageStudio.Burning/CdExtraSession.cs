using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

internal sealed record CdExtraMediaInfo(
    int Profile,
    int DiscStatus,
    int LastSessionState,
    long AtipLeadoutStart,
    CdTaoFeature TaoFeature,
    int WriteParametersPageLength);

/// <summary>Device seam of the CD-Extra recorder; tests replace it with a script.</summary>
internal interface ICdExtraSession : IDisposable
{
    void Acquire();
    CdExtraMediaInfo ReadMedia();
    OpticalWriteSpeed ConfigureWrite(OpticalWriteSpeed? requested);
    /// <summary>Writes one audio track at the next writable address and completes it.
    /// Returns the track's start LBA.</summary>
    long WriteAudioTrack(IStream pcm, long sectors, bool testWrite);
    /// <summary>CLOSE SESSION; the write parameters already decide whether the disc stays
    /// appendable (audio session) or is finalized (picture session).</summary>
    void CloseSession(bool finalize);
    /// <summary>The next writable address and free blocks of the open second session.</summary>
    (long NextWritableAddress, long FreeBlocks) ReadNextSessionStart();
    /// <summary>Writes the Mode 1 data track and completes it. Returns its start LBA.</summary>
    long WriteDataTrack(IStream data, long sectors, long expectedStartLba, bool testWrite);
    void Flush();
    void CancelWrite();
    void Release();
}

/// <summary>
/// Real recorder access for CD-Extra: explicit IMAPI exclusive access held across both
/// sessions (no media-change notification, removal prevented), Track-at-Once write
/// parameters and WRITE(10) commands through IDiscRecorder2Ex.
/// </summary>
internal sealed class DynamicCdExtraSession : ICdExtraSession
{
    private const int CdRecordableProfile = 0x0009;
    private const int CdRewritableProfile = 0x000a;
    private readonly Func<OpticalWriteSpeed?, OpticalWriteSpeed> _configureSpeed;
    private readonly IMmcCommandTransport _transport;
    private readonly CdMmcCommands _commands;
    private readonly BluRaySessionCleanup _cleanup;
    private readonly Action<long>? _reportAcceptedBytes;
    private readonly Action<TimeSpan>? _sleep;
    private CdExtraMediaInfo? _media;
    private byte[]? _writeParametersPage;
    private int _cancelRequested;
    private bool _acquired;
    private bool _disposed;

    internal DynamicCdExtraSession(object recorder, object speedFormat, Action<long>? reportAcceptedBytes = null,
        Action<string>? reportCleanupWarning = null)
        : this(new ImapiMmcCommandTransport(recorder ?? throw new ArgumentNullException(nameof(recorder))),
            CreateSpeedConfiguration(speedFormat ?? throw new ArgumentNullException(nameof(speedFormat))),
            commands => CreateRecorderCleanup(recorder, commands, reportCleanupWarning),
            reportAcceptedBytes)
    {
    }

    /// <summary>Command-level seam: the transport carries every MMC command, so tests can
    /// run the real Track-at-Once sequence against a scripted recorder.</summary>
    internal DynamicCdExtraSession(IMmcCommandTransport transport,
        Func<OpticalWriteSpeed?, OpticalWriteSpeed> configureSpeed,
        Func<CdMmcCommands, BluRaySessionCleanup> createCleanup,
        Action<long>? reportAcceptedBytes = null,
        Action<TimeSpan>? sleep = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(configureSpeed);
        ArgumentNullException.ThrowIfNull(createCleanup);
        _transport = transport;
        _configureSpeed = configureSpeed;
        _reportAcceptedBytes = reportAcceptedBytes;
        _sleep = sleep;
        _commands = new CdMmcCommands(transport, sleep);
        _cleanup = createCleanup(_commands);
    }

    private static Func<OpticalWriteSpeed?, OpticalWriteSpeed> CreateSpeedConfiguration(object speedFormat)
    {
        dynamic format = speedFormat;
        return requested =>
        {
            if (requested is not null)
            {
                requested.Validate();
                format.SetWriteSpeed(requested.SectorsPerSecond, requested.RotationTypeIsPureCav);
            }
            OpticalWriteSpeed actual = new(Convert.ToInt32(format.CurrentWriteSpeed),
                Convert.ToBoolean(format.CurrentRotationTypeIsPureCAV));
            actual.Validate();
            return actual;
        };
    }

    private static BluRaySessionCleanup CreateRecorderCleanup(object recorder, CdMmcCommands commands,
        Action<string>? reportCleanupWarning)
    {
        dynamic dynamicRecorder = recorder;
        return new BluRaySessionCleanup(
            acquireExclusive: () => dynamicRecorder.AcquireExclusiveAccess(false, "Disc Image Studio"),
            disableMcn: () => dynamicRecorder.DisableMcn(),
            preventRemoval: () => commands.PreventRemoval(true),
            allowRemoval: () => commands.PreventRemoval(false),
            enableMcn: () => dynamicRecorder.EnableMcn(),
            releaseExclusive: () => dynamicRecorder.ReleaseExclusiveAccess(),
            releaseWriteEngine: () => { },
            reportCleanupWarning: reportCleanupWarning);
    }

    public void Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acquired || _cleanup.HasPendingLocks)
            throw new InvalidOperationException("CD-Extra 会话已经取得刻录机访问权。");
        _cleanup.Acquire();
        _acquired = true;
    }

    public CdExtraMediaInfo ReadMedia()
    {
        RequireAcquired();
        CdTaoFeature feature = _commands.ReadTaoFeature();
        CdDiscInformation disc = _commands.ReadDiscInformation();
        long atip = WindowsImapiBurner.ReadCdAtipSectorCount(_transport);
        _writeParametersPage = _commands.ModeSenseWriteParameters();
        _media = new CdExtraMediaInfo(feature.Profile, disc.DiscStatus, disc.LastSessionState, atip, feature,
            _writeParametersPage.Length);
        return _media;
    }

    public OpticalWriteSpeed ConfigureWrite(OpticalWriteSpeed? requested)
    {
        RequireAcquired();
        return _configureSpeed(requested);
    }

    public long WriteAudioTrack(IStream pcm, long sectors, bool testWrite)
    {
        RequireMedia();
        SelectWriteParameters(CdWriteParameters.AudioSession(_media!.TaoFeature.BufferUnderrunFree, testWrite));
        CdTrackInformation track = RequireWritableTrack();
        CdTaoWriter.Write(_transport, pcm, track.NextWritableAddress, sectors, CdTaoWriter.AudioBlockBytes,
            _reportAcceptedBytes, () => Volatile.Read(ref _cancelRequested) != 0, _sleep);
        _commands.SynchronizeCache();
        return track.NextWritableAddress;
    }

    public void CloseSession(bool finalize)
    {
        RequireMedia();
        _commands.CloseSession(finalize);
    }

    /// <summary>Only for a real burn (a test write never closes session 1). The data-track
    /// write parameters are selected first, so the address and free blocks are reported for
    /// the parameters the picture track will actually be written with.</summary>
    public (long NextWritableAddress, long FreeBlocks) ReadNextSessionStart()
    {
        RequireMedia();
        CdDiscInformation disc = _commands.ReadDiscInformation();
        if (!disc.IsAppendableAfterClose)
            throw new IOException($"第 1 区段关闭后盘片状态异常（DiscStatus={disc.DiscStatus}，LastSessionState={disc.LastSessionState}）。");
        SelectWriteParameters(CdWriteParameters.FinalDataSession(_media!.TaoFeature.BufferUnderrunFree, testWrite: false));
        CdTrackInformation track = RequireWritableTrack();
        return (track.NextWritableAddress, track.FreeBlocks);
    }

    public long WriteDataTrack(IStream data, long sectors, long expectedStartLba, bool testWrite)
    {
        RequireMedia();
        SelectWriteParameters(CdWriteParameters.FinalDataSession(_media!.TaoFeature.BufferUnderrunFree, testWrite));
        CdTrackInformation track = RequireWritableTrack();
        if (!testWrite && track.NextWritableAddress != expectedStartLba)
            throw new IOException($"图片数据轨的起始地址已变化：生成时为 LBA {expectedStartLba:N0}，光驱报告 LBA {track.NextWritableAddress:N0}。");
        if (track.FreeBlocks < sectors)
            throw new IOException($"图片数据轨需要 {sectors:N0} 扇区，光驱报告可写 {track.FreeBlocks:N0} 扇区。");
        CdTaoWriter.Write(_transport, data, track.NextWritableAddress, sectors, CdTaoWriter.DataBlockBytes,
            _reportAcceptedBytes, () => Volatile.Read(ref _cancelRequested) != 0, _sleep);
        _commands.SynchronizeCache();
        return track.NextWritableAddress;
    }

    public void Flush()
    {
        RequireAcquired();
        _commands.SynchronizeCache();
    }

    public void CancelWrite() => Interlocked.Exchange(ref _cancelRequested, 1);

    public void Release()
    {
        _acquired = false;
        _cleanup.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _acquired = false;
        _cleanup.Dispose();
    }

    internal static bool IsRecordableCdProfile(int profile) => profile is CdRecordableProfile or CdRewritableProfile;

    private void SelectWriteParameters(CdWriteParameters parameters)
        => _commands.ModeSelectWriteParameters(parameters.BuildModeSelectData(_writeParametersPage!));

    private CdTrackInformation RequireWritableTrack()
    {
        CdTrackInformation track = _commands.ReadNextWritableTrack();
        if (!track.NextWritableAddressValid || !track.Blank)
            throw new IOException($"光驱未报告可写入的空白轨道（轨道 {track.TrackNumber}，NWA_V={track.NextWritableAddressValid}，Blank={track.Blank}）。");
        return track;
    }

    private void RequireAcquired()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_acquired) throw new InvalidOperationException("尚未独占并锁定 CD 刻录机。");
    }

    private void RequireMedia()
    {
        RequireAcquired();
        if (_media is null || _writeParametersPage is null)
            throw new InvalidOperationException("必须先读取 CD 介质与写入参数。");
    }
}
