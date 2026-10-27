using System.Diagnostics;
using DiscImageStudio.Burning;

namespace DiscImageStudio.ViewModels;

/// <summary>Keeps device-confirmed writing separate from producer/COM transfer diagnostics.</summary>
internal sealed class BurnProgressDisplay
{
    private readonly OpticalBurnMediaKind _mediaKind;
    private readonly Func<TimeSpan> _elapsed;
    private string? _lastPhase;
    private string? _lastMessage;
    private TimeSpan? _lastTransferLog;
    private TimeSpan? _lastWriteTime;
    private double? _bytesPerSecond;
    private bool _finished;

    internal BurnProgressDisplay(OpticalBurnMediaKind mediaKind, long totalBytes, Func<TimeSpan>? elapsed = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        _mediaKind = mediaKind;
        TotalBytes = totalBytes;
        long started = Stopwatch.GetTimestamp();
        _elapsed = elapsed ?? (() => Stopwatch.GetElapsedTime(started));
    }

    internal long TotalBytes { get; }
    internal long WrittenBytes { get; private set; }
    internal double? BytesPerSecond => _lastWriteTime is { } time && _elapsed() - time <= TimeSpan.FromSeconds(3)
        ? _bytesPerSecond : null;
    internal double Percent => 100.0 * WrittenBytes / TotalBytes;
    internal string AmountText => TotalBytes >= 1024L * 1024 * 1024
        ? $"已写入 {WrittenBytes / (1024.0 * 1024 * 1024):0.00} / {TotalBytes / (1024.0 * 1024 * 1024):0.00} GiB"
        : $"已写入 {WrittenBytes / (1024.0 * 1024):0.0} / {TotalBytes / (1024.0 * 1024):0.0} MiB";
    internal string RateText => BytesPerSecond is { } rate ? $"实时 {rate / (1024 * 1024):0.00} MiB/s" : "实时 — MiB/s";
    internal string MultiplierText
    {
        get
        {
            if (BytesPerSecond is not { } rate) return "—×";
            int sectorBytes = _mediaKind == OpticalBurnMediaKind.CdAudio ? 2352 : 2048;
            double multiplier = rate / sectorBytes * new OpticalWriteSpeed(1, false).GetMultiplier(_mediaKind);
            return $"{multiplier:0.0}×";
        }
    }

    /// <returns>A diagnostic line to append to the log, or null for a redundant update.</returns>
    internal string? Apply(OpticalBurnProgress progress)
    {
        if (_finished) return null;
        if (progress.Stage is "write" or "complete")
        {
            WrittenBytes = progress.Stage == "complete" ? TotalBytes
                : Math.Max(WrittenBytes, Math.Clamp(progress.CompletedBytes, 0, TotalBytes));
            _lastWriteTime = _elapsed();
            _bytesPerSecond = progress.Stage == "write" && progress.BytesPerSecond is { } rate
                && double.IsFinite(rate) && rate >= 0 ? rate : null;
            _finished = progress.Stage == "complete";
        }
        else if (progress.Stage is "device" or "media" or "buffer" or "finalize")
        {
            // Preparing or closing the disc must not keep a rate from its writing phase.
            _bytesPerSecond = null;
        }

        if (progress.Stage == "transfer")
        {
            TimeSpan now = _elapsed();
            if (_lastTransferLog is { } last && now - last < TimeSpan.FromSeconds(5)) return null;
            _lastTransferLog = now;
            return progress.Message;
        }
        if (progress.Stage == "diagnostic") return progress.Message;
        bool changed = progress.Stage != _lastPhase
            || (progress.Stage != "write" && progress.Message != _lastMessage);
        _lastPhase = progress.Stage;
        _lastMessage = progress.Message;
        return changed ? progress.Message : null;
    }

    internal void Stop()
    {
        _bytesPerSecond = null;
        _finished = true;
    }
}
