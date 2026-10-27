using System.Globalization;
using DiscImageStudio.Burning;
using DiscImageStudio.ViewModels;

internal static class BurnProgressDisplayTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    internal static void Run()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            TestMediaMultipliers();
            TestDeviceProgressDoesNotFollowTransfer();
            TestUnknownInvalidAndZeroRates();
            TestStageChangesAndTelemetryExpiry();
            TestDiagnosticLogging();
            TestCompletionAndStop();
            TestLateCallbacksAfterCompletionAndStop();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        Console.WriteLine("burn-progress-display: passed (device-only counts, media multipliers, unavailable/stale rates, diagnostic throttling, stop/complete; no device access)");
    }

    private static void TestMediaMultipliers()
    {
        // Independent payload rates: CD is 75 sectors/s (2352 audio, 2048 data),
        // DVD is 1,385,000 B/s and BD is 4,500,000 B/s at 1x.
        (OpticalBurnMediaKind Kind, double BytesPerSecond, string Multiplier, string Rate)[] cases =
        [
            (OpticalBurnMediaKind.CdAudio, 2_822_400, "16.0×", "实时 2.69 MiB/s"),
            (OpticalBurnMediaKind.CdData, 2_457_600, "16.0×", "实时 2.34 MiB/s"),
            (OpticalBurnMediaKind.DvdData, 5_540_000, "4.0×", "实时 5.28 MiB/s"),
            (OpticalBurnMediaKind.BluRayData, 9_000_000, "2.0×", "实时 8.58 MiB/s"),
        ];
        foreach (var sample in cases)
        {
            BurnProgressDisplay display = new(sample.Kind, 512 * MiB, () => TimeSpan.Zero);
            display.Apply(new("write", "device write", 64 * MiB, 512 * MiB, sample.BytesPerSecond));
            Require(display.MultiplierText == sample.Multiplier, sample.Kind + " multiplier uses its payload convention");
            Require(display.RateText == sample.Rate, sample.Kind + " throughput displays binary MiB, not decimal MB");
            Require(display.AmountText == "已写入 64.0 / 512.0 MiB", sample.Kind + " amount and total remain visible");
        }
    }

    private static void TestDeviceProgressDoesNotFollowTransfer()
    {
        double seconds = 1;
        BurnProgressDisplay display = new(OpticalBurnMediaKind.DvdData, 4 * GiB, () => TimeSpan.FromSeconds(seconds));
        Require(display.WrittenBytes == 0 && display.Percent == 0, "new burn starts at zero device-written bytes");
        display.Apply(new("transfer", "all source bytes already read", 4 * GiB, 4 * GiB, 100 * MiB));
        Require(display.WrittenBytes == 0 && display.BytesPerSecond is null,
            "source transfer alone cannot claim disc writes or establish a rate");
        display.Apply(new("write", "device acknowledged", GiB, 4 * GiB, 8 * MiB));
        Require(display.WrittenBytes == GiB && display.Percent == 25,
            "the device counter advances the written fraction");
        Require(display.AmountText == "已写入 1.00 / 4.00 GiB", "large disc amounts use a shared GiB unit");

        foreach (string stage in new[] { "transfer", "diagnostic", "speed", "future-stage" })
        {
            display.Apply(new(stage, "non-writing update", long.MaxValue, 1, 999 * MiB));
            Require(display.WrittenBytes == GiB && display.TotalBytes == 4 * GiB && display.Percent == 25,
                stage + " cannot replace the captured job total or jump its written counter");
            Require(display.BytesPerSecond == 8 * MiB,
                stage + " cannot overwrite the device-measured rate");
        }

        display.Apply(new("write", "older device count", -2048, 0, 4 * MiB));
        Require(display.WrittenBytes == GiB && display.Percent == 25,
            "out-of-order or negative counters do not rewind the progress bar");
        display.Apply(new("write", "counter beyond request", long.MaxValue, long.MaxValue, 4 * MiB));
        Require(display.WrittenBytes == 4 * GiB && display.Percent == 100,
            "oversized device counters remain bounded by the request");
    }

    private static void TestUnknownInvalidAndZeroRates()
    {
        BurnProgressDisplay display = new(OpticalBurnMediaKind.CdData, 100 * MiB, () => TimeSpan.Zero);
        Require(display.RateText == "实时 — MiB/s" && display.MultiplierText == "—×",
            "a missing device measurement is unavailable rather than a fabricated zero");
        foreach (double? invalid in new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1 })
        {
            display.Apply(new("write", "measurement unavailable", 10 * MiB, 100 * MiB, invalid));
            Require(display.BytesPerSecond is null && display.RateText == "实时 — MiB/s" && display.MultiplierText == "—×",
                "missing, non-finite or negative measurements are unavailable");
            Require(display.WrittenBytes == 10 * MiB, "unavailable speed does not hide valid device-written bytes");
        }

        display.Apply(new("write", "device reports no new writes", 10 * MiB, 100 * MiB, 0));
        Require(display.BytesPerSecond == 0 && display.RateText == "实时 0.00 MiB/s" && display.MultiplierText == "0.0×",
            "a measured stall remains a real zero rather than unavailable telemetry");
    }

    private static void TestStageChangesAndTelemetryExpiry()
    {
        double seconds = 0;
        BurnProgressDisplay display = new(OpticalBurnMediaKind.BluRayData, 4 * GiB, () => TimeSpan.FromSeconds(seconds));
        foreach (string stage in new[] { "device", "media", "buffer", "finalize" })
        {
            display.Apply(new("write", "device write", GiB, 4 * GiB, 9_000_000));
            Require(display.BytesPerSecond == 9_000_000, "fixture establishes a measured BD rate");
            display.Apply(new(stage, "phase details", 0, 4 * GiB, 9_000_000));
            Require(display.BytesPerSecond is null && display.MultiplierText == "—×",
                stage + " clears a writing rate even when the event contains one");
            Require(display.WrittenBytes == GiB && display.Percent == 25,
                stage + " retains the last confirmed device count");
        }

        display.Apply(new("write", "fresh sample", GiB, 4 * GiB, 9_000_000));
        seconds = 2.9;
        Require(display.BytesPerSecond == 9_000_000, "a recent device measurement remains usable");
        display.Apply(new("transfer", "producer keeps feeding", 3 * GiB, 4 * GiB));
        seconds = 3.01;
        Require(display.BytesPerSecond is null && display.RateText == "实时 — MiB/s" && display.MultiplierText == "—×",
            "producer activity cannot keep a stale device rate alive past three seconds");
        Require(display.WrittenBytes == GiB, "telemetry expiry preserves confirmed bytes");
        display.Apply(new("write", "fresh sample after gap", 2 * GiB, 4 * GiB, 4_500_000));
        Require(display.BytesPerSecond == 4_500_000 && display.MultiplierText == "1.0×",
            "a new measured sample restores live throughput after a gap");
    }

    private static void TestDiagnosticLogging()
    {
        double seconds = 0;
        BurnProgressDisplay display = new(OpticalBurnMediaKind.DvdData, GiB, () => TimeSpan.FromSeconds(seconds));
        Require(display.Apply(new("buffer", "preparing buffer", 0, GiB)) == "preparing buffer",
            "phase details move into the log");
        Require(display.Apply(new("buffer", "preparing buffer", 0, GiB)) is null,
            "duplicate phase messages do not flood the log");
        Require(display.Apply(new("buffer", "buffer ready", 0, GiB)) == "buffer ready",
            "changed phase details remain available in the log");
        Require(display.Apply(new("write", "device writing started", MiB, GiB, MiB)) == "device writing started",
            "entering the writing phase gets a log entry");
        Require(display.Apply(new("write", "device count changed", 2 * MiB, GiB, MiB)) is null,
            "frequent device counters are displayed without flooding the log");

        Require(display.Apply(new("transfer", "transfer 1", 3 * MiB, GiB)) == "transfer 1",
            "the first transfer diagnostic is logged");
        seconds = 4.99;
        Require(display.Apply(new("transfer", "transfer 2", 4 * MiB, GiB)) is null,
            "transfer counters are throttled for five seconds");
        Require(display.Apply(new("diagnostic", "command retry details", 0, GiB)) == "command retry details",
            "important diagnostics bypass transfer throttling");
        seconds = 5;
        Require(display.Apply(new("transfer", "transfer 3", 5 * MiB, GiB)) == "transfer 3",
            "a new transfer diagnostic is emitted when the interval elapses");
        Require(display.Apply(new("speed", "actual device speed", 0, GiB)) == "actual device speed",
            "the requested/actual device setting is a log detail, not a measured rate");
    }

    private static void TestCompletionAndStop()
    {
        BurnProgressDisplay stopped = new(OpticalBurnMediaKind.DvdData, GiB, () => TimeSpan.Zero);
        stopped.Apply(new("write", "writing", 256 * MiB, GiB, MiB));
        stopped.Stop();
        Require(stopped.WrittenBytes == 256 * MiB && stopped.Percent == 25,
            "failure/cancellation retains how much the device had confirmed");
        Require(stopped.BytesPerSecond is null && stopped.MultiplierText == "—×",
            "stopping clears the live rate");

        BurnProgressDisplay completed = new(OpticalBurnMediaKind.BluRayData, 4 * GiB, () => TimeSpan.Zero);
        completed.Apply(new("write", "writing", 3 * GiB, 4 * GiB, 9_000_000));
        completed.Apply(new("finalize", "closing session", 3 * GiB, 4 * GiB));
        Require(completed.Percent == 75, "finalization does not invent the unwritten remainder");
        completed.Apply(new("complete", "session closed", 4 * GiB, 4 * GiB));
        Require(completed.WrittenBytes == 4 * GiB && completed.Percent == 100,
            "successful completion fills the captured request total");
        Require(completed.AmountText == "已写入 4.00 / 4.00 GiB" && completed.BytesPerSecond is null,
            "completed totals stay visible without a stale writing rate");
        completed.Stop();
        Require(completed.WrittenBytes == 4 * GiB && completed.Percent == 100,
            "subsequent cleanup does not erase the completed count");
    }

    private static void TestLateCallbacksAfterCompletionAndStop()
    {
        var stopped = new BurnProgressDisplay(OpticalBurnMediaKind.DvdData, GiB);
        stopped.Apply(new OpticalBurnProgress("write", "writing", 256 * MiB, GiB, 5_540_000));
        stopped.Stop();
        var stoppedAmount = stopped.AmountText;
        foreach (var stage in new[] { "write", "complete", "speed", "diagnostic" })
        {
            var log = stopped.Apply(new OpticalBurnProgress(stage, "late callback", GiB, GiB, 5_540_000));
            Require(stopped.WrittenBytes == 256 * MiB && stopped.Percent == 25, "late " + stage + " callback cannot advance stopped progress");
            Require(stopped.AmountText == stoppedAmount && stopped.BytesPerSecond is null, "late " + stage + " callback cannot restore stopped rate or alter amount");
            Require(log is null, "late " + stage + " callback cannot emit a stopped-job log");
        }

        var completed = new BurnProgressDisplay(OpticalBurnMediaKind.BluRayData, 4 * GiB);
        completed.Apply(new OpticalBurnProgress("complete", "done", 4 * GiB, 4 * GiB));
        var completeAmount = completed.AmountText;
        foreach (var stage in new[] { "write", "transfer", "finalize", "complete" })
        {
            var log = completed.Apply(new OpticalBurnProgress(stage, "late callback", MiB, 4 * GiB, 9_000_000));
            Require(completed.WrittenBytes == 4 * GiB && completed.Percent == 100, "late " + stage + " callback cannot rewind completed progress");
            Require(completed.AmountText == completeAmount && completed.BytesPerSecond is null, "late " + stage + " callback cannot restore completed rate or alter amount");
            Require(log is null, "late " + stage + " callback cannot emit a completed-job log");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Burn progress display: " + message + ".");
    }
}
