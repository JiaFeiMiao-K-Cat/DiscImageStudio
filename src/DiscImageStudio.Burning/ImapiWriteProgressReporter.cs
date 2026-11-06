using System.Diagnostics;
using System.Globalization;

namespace DiscImageStudio.Burning;

/// <summary>Uses device-written LBAs from IMAPI Update events or acknowledged MMC
/// WRITE commands, never producer or IStream read counts. Rates include the effect
/// of recorder caching and are payload bytes/second over a recent 1–2 second
/// window; the configured drive speed is deliberately not an input.</summary>
internal sealed class ImapiWriteProgressReporter
{
    private readonly int _sectorBytes;
    private readonly long _totalBytes;
    private readonly Action<OpticalBurnProgress>? _report;
    private readonly Func<TimeSpan> _elapsed;
    private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();
    private int? _startLba;
    private string? _sampleSource;
    private long _completedBytes;
    private TimeSpan _lastTime;
    private string _stage = "unknown";
    private string _stageMessage = "正在等待刻录机状态…";
    private string? _lastPhaseKey;
    private string? _eventSource;
    private string? _actionSource;
    private int? _currentAction;
    private int? _deviceStartLba;
    private string? _startSource;
    private int? _lastWrittenLba;
    private string? _writtenSource;
    private int? _lastAcceptedLba;
    private long _updateEventCount;
    private TimeSpan? _lastUpdateAt;
    private TimeSpan? _lastDiagnosticAt;
    private EventValue? _lastReadLba;
    private EventValue? _totalSystemBuffer;
    private EventValue? _usedSystemBuffer;
    private EventValue? _freeSystemBuffer;

    private readonly record struct EventValue(int Value, string Source, long EventNumber);

    internal ImapiWriteProgressReporter(OpticalBurnMediaKind mediaKind, long totalBytes,
        Action<OpticalBurnProgress>? report, Func<TimeSpan>? elapsed = null)
    {
        _sectorBytes = OpticalBurnMedia.GetSectorBytes(mediaKind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        _totalBytes = totalBytes;
        _report = report;
        long started = Stopwatch.GetTimestamp();
        _elapsed = elapsed ?? (() => Stopwatch.GetElapsedTime(started));
    }

    // Action values and event IDs follow the Windows SDK imapi2.idl:
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/imapi2.idl
    internal void ReportDataUpdate(object eventArgs) => ReportUpdate(eventArgs, rawCd: false, engine: false);
    internal void ReportRawCdUpdate(object eventArgs) => ReportUpdate(eventArgs, rawCd: true, engine: false);
    internal void ReportEngineUpdate(object eventArgs) => ReportUpdate(eventArgs, rawCd: false, engine: true);

    private void ReportUpdate(object eventArgs, bool rawCd, bool engine)
    {
        dynamic args = eventArgs;
        string source = engine ? "IMAPI WriteEngine" : rawCd ? "IMAPI RawCD" : "IMAPI Data";
        _eventSource = source;
        _updateEventCount++;
        _lastUpdateAt = _elapsed();
        int? action = engine ? null : TryReadInt(() => args.CurrentAction);
        int? start = TryReadInt(() => args.StartLba);
        int? last = TryReadInt(() => args.LastWrittenLba);
        // These are cached event properties, not recorder queries. LastReadLba is
        // the image-read position; SystemBuffer values describe IMAPI's internal
        // buffer in bytes, not the drive's hardware buffer or physical writes.
        // https://learn.microsoft.com/windows/win32/api/imapi2/nn-imapi2-iwriteengine2eventargs
        CacheEventValue(ref _lastReadLba, TryReadInt(() => args.LastReadLba), source);
        CacheEventValue(ref _totalSystemBuffer, TryReadInt(() => args.TotalSystemBuffer), source, nonnegative: true);
        CacheEventValue(ref _usedSystemBuffer, TryReadInt(() => args.UsedSystemBuffer), source, nonnegative: true);
        CacheEventValue(ref _freeSystemBuffer, TryReadInt(() => args.FreeSystemBuffer), source, nonnegative: true);
        // Read each optional property separately, including during finalization.
        // An unavailable property must not erase earlier device evidence.
        if (action is not null)
        {
            _currentAction = action;
            _actionSource = source;
        }
        if (start is not null)
        {
            _deviceStartLba = start;
            _startSource = source;
        }
        if (last is not null)
        {
            _lastWrittenLba = last;
            _writtenSource = source;
        }
        if (!engine)
        {
            int? knownAction = action ?? (_actionSource == source ? _currentAction : null);
            int writingAction = rawCd ? 2 : 4;
            if (knownAction != writingAction)
            {
                if (rawCd && knownAction == 3)
                    ReportStage("finalize", "正在结束 CD 原始写入…");
                else if (!rawCd && knownAction == 5)
                    ReportStage("finalize", "正在完成 DVD 写入并关闭刻录会话…");
                else if (!rawCd && knownAction == 6)
                    ReportStage("write-complete", "Windows 刻录接口报告写入完成，正在等待返回…");
                else if (!rawCd && knownAction == 7)
                    ReportStage("verify", "正在验证刻录数据…");
                else if (knownAction is not null)
                    ReportStage("media", "正在准备盘片和写入设备…");
                else
                    ReportStage(_stage, _stageMessage);
                return;
            }
        }
        if (start is null || last is null)
        {
            ReportStage("write", "正在写入，等待刻录机进度…");
            return;
        }
        // Preserve negative raw DAO addresses above, but exclude lead-in from
        // payload progress and rate calculations.
        if (rawCd && last < 0)
        {
            ReportStageCore("media", "正在写入 CD 导入区…", periodicDiagnostic: true);
            return;
        }
        ReportWrittenSectorsCore(rawCd ? Math.Max(0, start.Value) : start.Value, last.Value);
    }

    private static int? TryReadInt(Func<object?> read)
    {
        try { return read() is { } value ? Convert.ToInt32(value) : null; }
        catch { return null; } // Optional telemetry must never interrupt a physical write.
    }

    private void CacheEventValue(ref EventValue? cached, int? value, string source, bool nonnegative = false)
    {
        if (value is { } number && (!nonnegative || number >= 0))
            cached = new(number, source, _updateEventCount);
    }

    internal void ReportAcceptedBytes(long completedBytes, long totalBytes)
    {
        // A successful WRITE confirms acceptance by the recorder, potentially
        // into its cache. Flush/finalization still determine burn completion.
        ReportWrittenSectors(0, checked((int)(completedBytes / _sectorBytes)) - 1,
            acceptedByDevice: true);
    }

    internal void ReportWrittenSectors(int startLba, int lastWrittenLba, bool acceptedByDevice = false)
    {
        _eventSource = acceptedByDevice ? "MMC WRITE acknowledgement" : "Device sectors";
        if (acceptedByDevice) _lastAcceptedLba = lastWrittenLba;
        else
        {
            _deviceStartLba = startLba;
            _startSource = _eventSource;
            _lastWrittenLba = lastWrittenLba;
            _writtenSource = _eventSource;
        }
        ReportWrittenSectorsCore(startLba, lastWrittenLba, acceptedByDevice);
    }

    internal void ReportRecordedSectors(int startLba, int lastWrittenLba)
    {
        // Reuse an already-issued READ TRACK INFORMATION response. This is
        // separate recorded-address evidence, not another throughput sample.
        const string source = "MMC READ TRACK INFORMATION (LRA_V=1)";
        _deviceStartLba = startLba;
        _startSource = source;
        _lastWrittenLba = lastWrittenLba;
        _writtenSource = source;
        Publish(new("diagnostic", GetDiagnosticSummary(), _completedBytes, _totalBytes));
    }

    private void ReportWrittenSectorsCore(int startLba, int lastWrittenLba, bool acceptedByDevice = false)
    {
        TimeSpan now = _elapsed();
        long writtenSectors = Math.Max(0, (long)lastWrittenLba - startLba + 1);
        long completed = Math.Min(writtenSectors, _totalBytes / _sectorBytes) * _sectorBytes;
        if (_sampleSource != _eventSource || _startLba != startLba || completed < _completedBytes || now < _lastTime)
            _samples.Clear();
        _sampleSource = _eventSource;
        _startLba = startLba;
        _completedBytes = completed;
        _lastTime = now;
        _samples.Enqueue((now, completed));
        // Retain only recent device samples. A gap longer than the window starts
        // a new measurement instead of displaying a stale long-term average.
        while (_samples.Count > 1 && now - _samples.Peek().Time > TimeSpan.FromSeconds(2))
            _samples.Dequeue();
        (TimeSpan firstTime, long firstBytes) = _samples.Peek();
        double seconds = (now - firstTime).TotalSeconds;
        double? rate = seconds >= 1 ? (completed - firstBytes) / seconds : null;
        string label = acceptedByDevice ? "刻录机已接收" : "正在刻录";
        SetPhase("write", label);
        Publish(new("write",
            $"{label} {completed / (1024.0 * 1024.0):F1} / {_totalBytes / (1024.0 * 1024.0):F1} MiB…",
            completed, _totalBytes, rate));
    }

    internal void ReportStage(string stage, string message)
        => ReportStageCore(stage, message, periodicDiagnostic: false);

    private void ReportStageCore(string stage, string message, bool periodicDiagnostic)
    {
        _samples.Clear();
        _startLba = null;
        SetPhase(stage, message);
        // Only an arriving event can emit a periodic snapshot. No timer or
        // extra device access runs while WriteMedia blocks or stops reporting.
        if (periodicDiagnostic && _lastDiagnosticAt is { } lastDiagnostic
            && _lastUpdateAt is { } updateAt && updateAt - lastDiagnostic >= TimeSpan.FromSeconds(2))
        {
            PublishDiagnostic();
        }
        Publish(new(stage, message, _completedBytes, _totalBytes));
    }

    private void SetPhase(string stage, string message)
    {
        _stage = stage;
        _stageMessage = message;
        string key = $"{stage}|{message}|{_eventSource}|{_actionSource}|{_currentAction}";
        if (_lastPhaseKey == key) return;
        _lastPhaseKey = key;
        PublishDiagnostic();
    }

    private void PublishDiagnostic()
    {
        _lastDiagnosticAt = _elapsed();
        Publish(new("diagnostic", GetDiagnosticSummary(), _completedBytes, _totalBytes));
    }

    // Keep the last value and its source for each field; values can come from
    // different events. Accepted LBAs describe WRITE acknowledgements, while
    // written LBAs describe device reports. Neither replaces write completion.
    internal string GetDiagnosticSummary()
        => $"刻录状态缓存：Stage={_stage}；EventSource={_eventSource ?? "unknown"}；"
            + $"CurrentAction={Number(_currentAction)}（来源={_actionSource ?? "unknown"}）；"
            + $"StartLba={Number(_deviceStartLba)}（来源={_startSource ?? "unknown"}）；"
            + $"LastWrittenLba={Number(_lastWrittenLba)}"
            + $"（来源={_writtenSource ?? "unknown"}）；LastAcceptedLba={Number(_lastAcceptedLba)}；"
            + $"UpdateEventCount={_updateEventCount.ToString(CultureInfo.InvariantCulture)}；"
            + $"LastUpdateAgeSeconds={LastUpdateAgeSeconds()}；"
            + $"LastReadLba={DescribeEventValue(_lastReadLba)}；"
            + $"TotalSystemBuffer={DescribeEventValue(_totalSystemBuffer, bytes: true)}；"
            + $"UsedSystemBuffer={DescribeEventValue(_usedSystemBuffer, bytes: true)}；"
            + $"FreeSystemBuffer={DescribeEventValue(_freeSystemBuffer, bytes: true)}。";

    private string LastUpdateAgeSeconds()
        => _lastUpdateAt is { } updatedAt
            ? Math.Max(0, (_elapsed() - updatedAt).TotalSeconds).ToString("F3", CultureInfo.InvariantCulture)
            : "unknown";

    private static string DescribeEventValue(EventValue? cached, bool bytes = false)
        => cached is { } item
            ? $"{Number(item.Value)}{(bytes ? " B" : "")}（来源={item.Source}，事件={item.EventNumber.ToString(CultureInfo.InvariantCulture)}）"
            : "unknown";

    private static string Number(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private void Publish(OpticalBurnProgress progress)
    {
        // UI/progress consumers are optional observers. Their failures must not
        // escape a COM Update callback or interrupt a physical write operation.
        try { _report?.Invoke(progress); }
        catch { }
    }
}
