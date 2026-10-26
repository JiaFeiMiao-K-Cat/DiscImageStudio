using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

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
    private long _completedBytes;
    private TimeSpan _lastTime;

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
        int start, last;
        try
        {
            dynamic args = eventArgs;
            if (!engine)
            {
                int action = Convert.ToInt32(args.CurrentAction);
                int writingAction = rawCd ? 2 : 4;
                if (action != writingAction)
                {
                    bool finalizing = action == (rawCd ? 3 : 5) || (!rawCd && action is 6 or 7);
                    ReportStage(finalizing ? "finalize" : "media",
                        finalizing ? "正在刷新缓存并关闭刻录会话…" : "正在准备盘片和写入设备…");
                    return;
                }
            }
            start = Convert.ToInt32(args.StartLba);
            last = Convert.ToInt32(args.LastWrittenLba);
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException
            or InvalidCastException or FormatException or OverflowException)
        {
            // Telemetry is optional. An unavailable event property must not abort
            // a burn or turn stream consumption into fabricated write telemetry.
            ReportStage("write", "正在写入，等待刻录机进度…");
            return;
        }

        if (rawCd)
        {
            // DAO can report a negative lead-in LBA. The request counts audio or
            // ISO payload only, so lead-in sectors must not advance its byte counter.
            if (last < 0)
            {
                ReportStage("media", "正在写入 CD 导入区…");
                return;
            }
            start = Math.Max(0, start);
        }
        ReportWrittenSectors(start, last);
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
        TimeSpan now = _elapsed();
        long writtenSectors = Math.Max(0, (long)lastWrittenLba - startLba + 1);
        long completed = Math.Min(writtenSectors, _totalBytes / _sectorBytes) * _sectorBytes;
        if (_startLba != startLba || completed < _completedBytes || now < _lastTime)
            _samples.Clear();
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
        Publish(new("write",
            $"{label} {completed / (1024.0 * 1024.0):F1} / {_totalBytes / (1024.0 * 1024.0):F1} MiB…",
            completed, _totalBytes, rate));
    }

    internal void ReportStage(string stage, string message)
    {
        _samples.Clear();
        _startLba = null;
        Publish(new(stage, message, _completedBytes, _totalBytes));
    }

    private void Publish(OpticalBurnProgress progress)
    {
        // UI/progress consumers are optional observers. Their failures must not
        // escape a COM Update callback or interrupt a physical write operation.
        try { _report?.Invoke(progress); }
        catch { }
    }
}
