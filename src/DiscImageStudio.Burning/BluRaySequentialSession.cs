using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

internal sealed record BluRaySequentialMediaInfo(
    int Profile,
    bool DiscIsBlank,
    int FormatStatus,
    bool TrackIsBlank,
    bool NextWritableAddressValid,
    uint TrackStartAddress,
    uint NextWritableAddress,
    uint FreeBlocks,
    uint TrackSize,
    uint MaximumBlocks,
    int TrackNumber,
    int SpareClusters = 0);

internal interface IBluRaySequentialSession : IDisposable
{
    void Acquire();
    BluRaySequentialMediaInfo ReadMedia();
    OpticalWriteSpeed ConfigureWrite(OpticalWriteSpeed? requested);
    void Write(IStream content, uint sectorCount);
    void CancelWrite();
    void Flush();
    void CloseTrack(int trackNumber);
    void CloseDisc();
    void Release();
}

/// <summary>
/// Unformatted BD-R SRM writing using explicit, timeout-controlled WRITE(10)
/// commands rather than formatting the medium with spare areas.
/// The recorder and speed-format COM objects remain owned by the caller.
/// </summary>
internal sealed class DynamicBluRaySequentialSession : IBluRaySequentialSession
{
    private readonly dynamic _recorder;
    private readonly dynamic _speedFormat;
    private readonly BluRayMmcCommands _commands;
    private readonly IMmcCommandTransport _transport;
    private readonly Action<long, long>? _reportWriteProgress;
    private readonly Action<string>? _reportDiagnostics;
    private readonly BluRaySessionCleanup _cleanup;
    private int _cancelRequested;
    private bool _acquired;
    private bool _configured;
    private bool _disposed;

    internal DynamicBluRaySequentialSession(object recorder, object speedFormat,
        Action<long, long>? reportWriteProgress = null, Action<string>? reportCleanupWarning = null,
        Action<string>? reportDiagnostics = null, Action<int, int>? reportRecordedLba = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(speedFormat);
        _recorder = recorder;
        _speedFormat = speedFormat;
        _reportWriteProgress = reportWriteProgress;
        _reportDiagnostics = reportDiagnostics;
        _transport = new ImapiMmcCommandTransport(recorder);
        _commands = new BluRayMmcCommands(_transport, reportRecordedLba);
        _cleanup = new BluRaySessionCleanup(
            acquireExclusive: () => _recorder.AcquireExclusiveAccess(false, "Disc Image Studio"),
            disableMcn: () => _recorder.DisableMcn(),
            preventRemoval: () => _commands.PreventRemoval(true),
            allowRemoval: () => _commands.PreventRemoval(false),
            enableMcn: () => _recorder.EnableMcn(),
            releaseExclusive: () => _recorder.ReleaseExclusiveAccess(),
            // The direct command writer disposes its transfer buffer before
            // returning. There is no additional COM write engine to retain locks.
            releaseWriteEngine: () => { },
            reportCleanupWarning: reportCleanupWarning);
    }

    public void Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acquired || _cleanup.HasPendingLocks)
            throw new InvalidOperationException("蓝光会话已经取得刻录机访问权。");
        _cleanup.Acquire();
        _acquired = true;
    }

    public BluRaySequentialMediaInfo ReadMedia()
    {
        RequireAcquired();
        return _commands.ReadMedia();
    }

    public OpticalWriteSpeed ConfigureWrite(OpticalWriteSpeed? requested)
    {
        RequireAcquired();
        if (_configured) throw new InvalidOperationException("蓝光写入参数已经设置。");
        if (requested is not null)
        {
            requested.Validate();
            _speedFormat.SetWriteSpeed(requested.SectorsPerSecond, requested.RotationTypeIsPureCav);
        }
        OpticalWriteSpeed actual = new(Convert.ToInt32(_speedFormat.CurrentWriteSpeed),
            Convert.ToBoolean(_speedFormat.CurrentRotationTypeIsPureCAV));
        actual.Validate();

        _configured = true;
        return actual;
    }

    public void Write(IStream content, uint sectorCount)
    {
        RequireAcquired();
        if (!_configured)
            throw new InvalidOperationException("必须先设置蓝光顺序写入参数。");

        // The first WRITE initializes a blank BD-R in SRM without allocating
        // spare areas. No FORMAT UNIT, RESERVE TRACK, page 05 or OPC is issued.
        BluRaySequentialWriter.Write(_transport, content, sectorCount, _reportWriteProgress,
            () => Volatile.Read(ref _cancelRequested) != 0, reportDiagnostics: _reportDiagnostics);
    }

    public void CancelWrite()
    {
        Interlocked.Exchange(ref _cancelRequested, 1);
        // Do not cancel cache draining. The caller can still flush written data
        // after cancellation; it must not close the track or disc on that path.
    }

    public void Flush()
    {
        RequireAcquired();
        _commands.SynchronizeCache();
    }

    public void CloseTrack(int trackNumber)
    {
        RequireAcquired();
        _commands.CloseTrack(trackNumber);
    }

    public void CloseDisc()
    {
        RequireAcquired();
        _commands.CloseDisc();
    }

    public void Release()
    {
        _acquired = false;
        _configured = false;
        _cleanup.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _acquired = false;
        _configured = false;
        _cleanup.Dispose();
    }

    private void RequireAcquired()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_acquired) throw new InvalidOperationException("尚未独占并锁定蓝光刻录机。");
    }
}

/// <summary>
/// Tracks only this session's confirmed IMAPI acquisitions and its uncertain MMC
/// removal lock. Failed unlocks remain pending. Disposal releases the write engine
/// before one final retry, without repeating successful, reference-counted releases.
/// Delegates keep this lifecycle testable without COM or a physical recorder.
/// </summary>
internal sealed class BluRaySessionCleanup : IDisposable
{
    private readonly Action _acquireExclusive;
    private readonly Action _disableMcn;
    private readonly Action _preventRemoval;
    private readonly Action _allowRemoval;
    private readonly Action _enableMcn;
    private readonly Action _releaseExclusive;
    private readonly Action _releaseWriteEngine;
    private readonly Action<string>? _reportCleanupWarning;
    private bool _exclusive;
    private bool _mcnDisabled;
    private bool _removalPrevented;
    private bool _releaseAttempted;
    private bool _disposed;

    internal BluRaySessionCleanup(Action acquireExclusive, Action disableMcn, Action preventRemoval,
        Action allowRemoval, Action enableMcn, Action releaseExclusive, Action releaseWriteEngine,
        Action<string>? reportCleanupWarning = null)
    {
        _acquireExclusive = acquireExclusive ?? throw new ArgumentNullException(nameof(acquireExclusive));
        _disableMcn = disableMcn ?? throw new ArgumentNullException(nameof(disableMcn));
        _preventRemoval = preventRemoval ?? throw new ArgumentNullException(nameof(preventRemoval));
        _allowRemoval = allowRemoval ?? throw new ArgumentNullException(nameof(allowRemoval));
        _enableMcn = enableMcn ?? throw new ArgumentNullException(nameof(enableMcn));
        _releaseExclusive = releaseExclusive ?? throw new ArgumentNullException(nameof(releaseExclusive));
        _releaseWriteEngine = releaseWriteEngine ?? throw new ArgumentNullException(nameof(releaseWriteEngine));
        _reportCleanupWarning = reportCleanupWarning;
    }

    internal bool HasPendingLocks => _exclusive || _mcnDisabled || _removalPrevented;

    internal void Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HasPendingLocks) throw new InvalidOperationException("蓝光会话仍有未释放的刻录机访问权。");
        _releaseAttempted = false;
        try
        {
            // Exclusive access is never forced. Its release and MCN's paired enable
            // are owed only after the corresponding acquisition reports success.
            _acquireExclusive();
            _exclusive = true;
            _disableMcn();
            _mcnDisabled = true;
            // A failed PREVENT transport can still have locked the device.
            _removalPrevented = true;
            _preventRemoval();
        }
        catch
        {
            try { Release(); } catch { /* Keep the acquisition exception and pending flags. */ }
            throw;
        }
    }

    internal void Release()
    {
        if (_disposed) return;
        ReleaseCore(retry: false);
    }

    private void ReleaseCore(bool retry)
    {
        _releaseAttempted = true;
        Exception? first = null;
        ReleaseOne(ref _removalPrevented, _allowRemoval, "解除盘片弹出保护", retry, ref first);
        ReleaseOne(ref _mcnDisabled, _enableMcn, "恢复介质变化通知", retry, ref first);
        ReleaseOne(ref _exclusive, _releaseExclusive, "释放刻录机独占访问", retry, ref first);
        if (first is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }

    private void ReleaseOne(ref bool pending, Action release, string operation, bool retry, ref Exception? first)
    {
        if (!pending) return;
        try
        {
            release();
            pending = false;
        }
        catch (Exception exception)
        {
            first ??= exception;
            ReportFailure(operation, exception, retry);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Release may already have run in the burn or failed-acquisition path.
        // Do not waste its only disposal retry while the engine still owns COM references.
        if (!_releaseAttempted)
        {
            try { ReleaseCore(retry: false); } catch { }
        }
        try { _releaseWriteEngine(); }
        catch (Exception exception) { ReportFailure("释放蓝光写入引擎", exception, retry: false); }
        if (HasPendingLocks)
        {
            try { ReleaseCore(retry: true); } catch { }
        }
    }

    private void ReportFailure(string operation, Exception exception, bool retry)
    {
        try
        {
            _reportCleanupWarning?.Invoke(
                $"蓝光清理{(retry ? "重试" : string.Empty)}：{operation}失败（HRESULT 0x{exception.HResult:X8}）：{exception.Message}");
        }
        catch { /* Diagnostics must never interrupt remaining unlocks or replace a write failure. */ }
    }
}
