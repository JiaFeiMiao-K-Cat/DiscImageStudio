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
    void ConfirmTaoWriteFormats(bool testWrite);
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
    (uint? DiscId, string? Audio) ReadPictureIdentity(IReadOnlyList<long> audioStarts);
    void VerifyPicture(Stream source, long start, long sectors, Action<long> progress, CancellationToken token);
    void Flush();
    void CancelWrite();
    void Release();

    // Session-at-Once with CD-TEXT. Defaults report "not supported", so a recorder (or test
    // script) that only knows Track-at-Once keeps using the unchanged TAO sequence.
    CdSaoCapability ReadSaoCapability() => CdSaoCapability.None;
    long ReadLeadInStart() => throw new NotSupportedException();
    /// <summary>Selects SAO write parameters and sends the cue sheet. Nothing is recorded
    /// yet, so a refusal still allows the Track-at-Once path on the same blank disc.</summary>
    void BeginSessionAtOnce(byte[] cueSheet, bool finalize, bool testWrite) => throw new NotSupportedException();
    void WriteSaoLeadIn(long leadInStart, CdTextLeadIn cdText) => throw new NotSupportedException();
    void WriteSaoSilence(long lba, long sectors) => throw new NotSupportedException();
    void WriteSaoAudio(IStream pcm, long lba, long sectors) => throw new NotSupportedException();
    /// <summary>SYNCHRONIZE CACHE ends an SAO session; no CLOSE SESSION follows.</summary>
    void CompleteSessionAtOnce(bool finalize) => throw new NotSupportedException();
    byte[] ReadCdTextPacks() => [];
}

/// <summary>
/// Real recorder access for CD-Extra: explicit IMAPI exclusive access held across both
/// sessions (no media-change notification, removal prevented), Track-at-Once write
/// parameters and WRITE(10) commands through IDiscRecorder2Ex.
/// </summary>
internal sealed partial class DynamicCdExtraSession : ICdExtraSession
{
    private const int CdRecordableProfile = 0x0009;
    private const int CdRewritableProfile = 0x000a;
    private readonly Func<OpticalWriteSpeed?, OpticalWriteSpeed> _configureSpeed;
    private readonly IMmcCommandTransport _transport;
    private readonly CdMmcCommands _commands;
    private readonly BluRaySessionCleanup _cleanup;
    private readonly Action<long>? _reportAcceptedBytes;
    private readonly Action<TimeSpan>? _sleep;
    private readonly Action<string>? _reportDiagnostic;
    private CdExtraMediaInfo? _media;
    private byte[]? _writeParametersPage;
    private int _cancelRequested;
    private bool _acquired;
    private bool _disposed;

    internal DynamicCdExtraSession(object recorder, object speedFormat, Action<long>? reportAcceptedBytes = null,
        Action<string>? reportCleanupWarning = null, Action<string>? reportDiagnostic = null)
        : this(new ImapiMmcCommandTransport(recorder ?? throw new ArgumentNullException(nameof(recorder))),
            CreateSpeedConfiguration(speedFormat ?? throw new ArgumentNullException(nameof(speedFormat))),
            commands => CreateRecorderCleanup(recorder, commands, reportCleanupWarning),
            reportAcceptedBytes, reportDiagnostic: reportDiagnostic)
    {
    }

    /// <summary>Command-level seam: the transport carries every MMC command, so tests can
    /// run the real Track-at-Once sequence against a scripted recorder.</summary>
    internal DynamicCdExtraSession(IMmcCommandTransport transport,
        Func<OpticalWriteSpeed?, OpticalWriteSpeed> configureSpeed,
        Func<CdMmcCommands, BluRaySessionCleanup> createCleanup,
        Action<long>? reportAcceptedBytes = null,
        Action<TimeSpan>? sleep = null, Action<string>? reportDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(configureSpeed);
        ArgumentNullException.ThrowIfNull(createCleanup);
        _transport = transport;
        _configureSpeed = configureSpeed;
        _reportAcceptedBytes = reportAcceptedBytes;
        _sleep = sleep;
        _reportDiagnostic = reportDiagnostic;
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

    public void ConfirmTaoWriteFormats(bool testWrite)
    {
        RequireMedia();
        bool buffer = _media!.TaoFeature.BufferUnderrunFree;
        SelectWriteParameters(CdWriteParameters.FinalDataSession(buffer, testWrite));
        SelectWriteParameters(CdWriteParameters.AudioSession(buffer, testWrite));
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
            _reportAcceptedBytes, () => Volatile.Read(ref _cancelRequested) != 0, _sleep,
            testWrite ? null : (lba, bytes) => _commands.CheckPictureWrite(track, lba, bytes), _reportDiagnostic);
        _reportDiagnostic?.Invoke($"图案轨最后一批已被光驱接收（末扇区 LBA {track.NextWritableAddress + sectors - 1}），正在刷写光驱缓存。");
        _commands.SynchronizeCache(picture: !testWrite);
        _reportDiagnostic?.Invoke("图案轨缓存已刷写完成。");
        return track.NextWritableAddress;
    }

    public (uint? DiscId, string? Audio) ReadPictureIdentity(IReadOnlyList<long> audioStarts)
    {
        uint? id = _commands.ReadDiscInformation().DiscId;
        if (id.HasValue) return (id, null);
        try { return (null, _commands.AudioFingerprint(audioStarts)); }
        catch (Exception e) when (e is IOException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            _reportDiagnostic?.Invoke($"光驱未提供盘片标识且无法读取音轨指纹，重新打开断点时仅支持已封盘校验：{e.Message}");
            return (null, null);
        }
    }

    public void VerifyPicture(Stream source, long start, long sectors, Action<long> progress, CancellationToken token)
        => _commands.VerifyPicture(source, start, sectors, progress, token);

    public void Flush()
    {
        RequireAcquired();
        _commands.SynchronizeCache();
    }

    public void CancelWrite() => Interlocked.Exchange(ref _cancelRequested, 1);

    public CdSaoCapability ReadSaoCapability()
    {
        RequireAcquired();
        return _commands.ReadSaoFeature();
    }

    public long ReadLeadInStart()
    {
        RequireAcquired();
        return _commands.ReadLeadInStart();
    }

    public void BeginSessionAtOnce(byte[] cueSheet, bool finalize, bool testWrite)
    {
        RequireMedia();
        SelectWriteParameters(CdWriteParameters.AudioSessionAtOnce(_media!.TaoFeature.BufferUnderrunFree, testWrite, finalize));
        _commands.SendCueSheet(cueSheet);
    }

    public void WriteSaoLeadIn(long leadInStart, CdTextLeadIn cdText)
    {
        RequireMedia();
        CdSaoWriter.WriteLeadIn(_transport, leadInStart, cdText.ToSubchannel(),
            () => Volatile.Read(ref _cancelRequested) != 0, _sleep ?? Thread.Sleep);
    }

    public void WriteSaoSilence(long lba, long sectors)
    {
        RequireMedia();
        CdSaoWriter.WriteSilence(_transport, lba, sectors, () => Volatile.Read(ref _cancelRequested) != 0, _sleep ?? Thread.Sleep);
    }

    public void WriteSaoAudio(IStream pcm, long lba, long sectors)
    {
        RequireMedia();
        CdSaoWriter.WriteAudio(_transport, pcm, lba, sectors, () => Volatile.Read(ref _cancelRequested) != 0,
            _sleep ?? Thread.Sleep, _reportAcceptedBytes);
    }

    public void CompleteSessionAtOnce(bool finalize)
    {
        RequireMedia();
        // The recorder writes the lead-in TOC and lead-out while it drains its cache.
        _commands.CompleteSessionAtOnce(finalize);
    }

    public byte[] ReadCdTextPacks()
    {
        RequireAcquired();
        return _commands.ReadCdTextPacks();
    }

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
    {
        byte[] selection = parameters.BuildModeSelectData(_writeParametersPage!);
        if (_media!.TaoFeature.DataTypes != 0)
        {
            _commands.ModeSelectWriteParameters(selection);
            return;
        }

        string format = parameters.DataBlockType == CdWriteParameters.Mode1Block ? "Mode 1 数据" : "CD-DA 音频";
        try
        {
            _commands.ModeSelectWriteParameters(selection);
            byte[] current = _commands.ModeSenseWriteParameters();
            if (!parameters.Matches(current))
                throw new IOException($"05h 模式页的读回值与请求不符（{Convert.ToHexString(current)}）。");
            _writeParametersPage = current;
        }
        catch (Exception e) when (e is IOException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            throw new IOException($"光驱未能确认 {format} 写入参数：{e.Message}", e);
        }
    }

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
