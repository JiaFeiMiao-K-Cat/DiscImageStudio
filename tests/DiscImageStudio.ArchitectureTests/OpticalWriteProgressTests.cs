using System.Dynamic;
using System.Runtime.InteropServices;
using DiscImageStudio.Burning;

internal static class OpticalWriteProgressTests
{
    internal static void Run()
    {
        TestDeviceLbasAndRollingRate();
        TestCdPayloadAndLeadIn();
        TestEngineUpdatesAndBounds();
        TestAcknowledgedCommands();
        TestRecordedAddressesPreservePhaseAndRate();
        TestPhaseResetAndUnavailableTelemetry();
        TestDistinctDataPhasesAndFinalLbas();
        TestPartialTelemetryPreservesEvidence();
        TestPhaseDiagnosticsAreNotRepeated();
        TestConsumerFailureIsIsolated();
        Require(new OpticalBurnProgress("buffer", "buffering", 0, 2048).BytesPerSecond is null,
            "existing four-argument progress calls have no fabricated measured speed");
        Console.WriteLine("optical-write-progress: passed (device LBAs, rolling rates, CD/DVD/BD sector sizes, phase reset; no device access)");
    }

    private static void TestDeviceLbasAndRollingRate()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.DvdData, 20_000L * 2048,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportDataUpdate(Event(100, 99, 4, lastReadLba: 19_999));
        Require(updates[^1].Stage == "write" && updates[^1].CompletedBytes == 0
            && updates[^1].BytesPerSecond is null, "initial device telemetry is measuring, even if source reads are far ahead");
        seconds = 1;
        reporter.ReportDataUpdate(Event(100, 1099, 4, lastReadLba: 19_999));
        Require(updates[^1].CompletedBytes == 1000L * 2048, "inclusive last-written LBA is relative to the write origin");
        EqualRate(2_048_000, updates[^1], "DVD rate uses actual written 2048-byte sectors");
        seconds = 2;
        reporter.ReportDataUpdate(Event(100, 2099, 4));
        EqualRate(2_048_000, updates[^1], "steady writing stays stable across the rolling window");
        seconds = 3;
        reporter.ReportDataUpdate(Event(100, 4099, 4));
        EqualRate(3_072_000, updates[^1], "the recent window reacts to acceleration instead of using a whole-burn average");
        seconds = 5;
        reporter.ReportDataUpdate(Event(100, 4099, 4));
        EqualRate(0, updates[^1], "unchanged device LBAs across a full window report a real stall");
        seconds = 8;
        reporter.ReportDataUpdate(Event(100, 5099, 4));
        Require(updates[^1].BytesPerSecond is null, "a long telemetry gap does not reuse an old speed");
    }

    private static void TestCdPayloadAndLeadIn()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.CdAudio, 10_000L * 2352,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportRawCdUpdate(Event(-4500, -500, 2));
        Require(updates[^1].Stage == "media" && updates[^1].CompletedBytes == 0
            && updates[^1].BytesPerSecond is null, "DAO lead-in does not consume audio payload progress");
        string leadIn = reporter.GetDiagnosticSummary();
        Require(leadIn.Contains("StartLba=-4500", StringComparison.Ordinal)
            && leadIn.Contains("LastWrittenLba=-500", StringComparison.Ordinal)
            && leadIn.Contains("IMAPI RawCD", StringComparison.Ordinal),
            "raw negative DAO evidence remains available even though it is excluded from payload progress");
        seconds = 1;
        reporter.ReportRawCdUpdate(Event(-4500, 99, 2));
        Require(updates[^1].CompletedBytes == 100L * 2352 && updates[^1].BytesPerSecond is null,
            "CD audio begins at nonnegative data LBAs and starts a fresh measurement");
        seconds = 2;
        reporter.ReportRawCdUpdate(Event(-4500, 174, 2));
        EqualRate(176_400, updates[^1], "75 CD audio sectors/second yield 176400 payload bytes/second");
        reporter.ReportRawCdUpdate(Event(-4500, 175, 3));
        Require(updates[^1].Stage == "finalize" && updates[^1].BytesPerSecond is null,
            "DAO finishing clears the live rate");
        Require(reporter.GetDiagnosticSummary().Contains("LastWrittenLba=175", StringComparison.Ordinal),
            "DAO finishing retains its final reported device address");
    }

    private static void TestEngineUpdatesAndBounds()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.BluRayData, 1024L * 2048,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportEngineUpdate(Event(0, -1));
        seconds = 1;
        reporter.ReportEngineUpdate(Event(0, 319));
        EqualRate(655_360, updates[^1], "BD write-engine events need no CurrentAction property and use 2048-byte sectors");
        seconds = 2;
        reporter.ReportEngineUpdate(Event(0, int.MaxValue));
        Require(updates[^1].CompletedBytes == 1024L * 2048 && updates[^1].Fraction == 1,
            "out-of-range device counters cannot exceed the requested content");
        seconds = 3;
        reporter.ReportEngineUpdate(Event(0, 100));
        Require(updates[^1].BytesPerSecond is null, "a counter reset cannot produce negative throughput");
        seconds = 4;
        reporter.ReportEngineUpdate(Event(200, 299));
        Require(updates[^1].BytesPerSecond is null, "a changed write origin starts a separate sample window");
        seconds = 1;
        reporter.ReportEngineUpdate(Event(200, 399));
        Require(updates[^1].BytesPerSecond is null, "a clock reset cannot produce invalid throughput");
    }

    private static void TestAcknowledgedCommands()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        const long length = 1024L * 2048;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.BluRayData, length,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportAcceptedBytes(0, length);
        Require(updates[^1].CompletedBytes == 0 && updates[^1].BytesPerSecond is null,
            "BD first command is pending until the recorder acknowledges data");
        seconds = 1;
        reporter.ReportAcceptedBytes(32L * 2048, length);
        EqualRate(65_536, updates[^1], "BD acknowledged-command rate counts exactly the accepted payload");
        Require(updates[^1].Message.Contains("刻录机已接收", StringComparison.Ordinal),
            "BD progress describes device acceptance without promising cache flush");
        string accepted = reporter.GetDiagnosticSummary();
        Require(accepted.Contains("LastAcceptedLba=31", StringComparison.Ordinal)
            && accepted.Contains("LastWrittenLba=unknown", StringComparison.Ordinal),
            "accepted MMC writes never fabricate physical last-written evidence");
        reporter.ReportStage("finalize", "Flushing recorder cache");
        Require(updates[^1].CompletedBytes == 65_536 && updates[^1].BytesPerSecond is null,
            "cache draining retains acknowledged progress but clears transfer speed");
        Require(reporter.GetDiagnosticSummary().Contains("LastAcceptedLba=31", StringComparison.Ordinal),
            "external cache-flush phase retains the last accepted command address");
        reporter.ReportEngineUpdate(Event(0, 15));
        reporter.ReportAcceptedBytes(64L * 2048, length);
        string mixed = reporter.GetDiagnosticSummary();
        Require(mixed.Contains("LastWrittenLba=15", StringComparison.Ordinal)
            && mixed.Contains("LastAcceptedLba=63", StringComparison.Ordinal)
            && mixed.Contains("IMAPI WriteEngine", StringComparison.Ordinal),
            "later command acknowledgements preserve independently reported physical-write evidence");
        Require(updates[^1].BytesPerSecond is null,
            "switching evidence sources starts a new rate window");
    }

    private static void TestDistinctDataPhasesAndFinalLbas()
    {
        List<OpticalBurnProgress> updates = [];
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.DvdData, 4096L * 2048, updates.Add);
        reporter.ReportDataUpdate(Event(20, 100, 4));
        reporter.ReportDataUpdate(Event(20, 200, 5));
        Require(updates[^1].Stage == "finalize", "DVD finalization has its own phase");
        string finalizing = reporter.GetDiagnosticSummary();
        Require(finalizing.Contains("CurrentAction=5", StringComparison.Ordinal)
            && finalizing.Contains("StartLba=20", StringComparison.Ordinal)
            && finalizing.Contains("LastWrittenLba=200", StringComparison.Ordinal),
            "finalization captures final device addresses instead of freezing the previous writing event");
        reporter.ReportDataUpdate(Event(20, 250, 7));
        Require(updates[^1].Stage == "verify" && updates[^1].BytesPerSecond is null,
            "DVD verification is not labeled as closing a session");
        reporter.ReportDataUpdate(Event(20, 260, 6));
        Require(updates[^1].Stage == "write-complete" && !updates.Any(x => x.Stage == "complete"),
            "a device completion event cannot mark the burn successful before Write returns");
        reporter.ReportStage("release", "Releasing device");
        string releasing = reporter.GetDiagnosticSummary();
        Require(releasing.Contains("Stage=release", StringComparison.Ordinal)
            && releasing.Contains("CurrentAction=6", StringComparison.Ordinal)
            && releasing.Contains("LastWrittenLba=260", StringComparison.Ordinal),
            "an external lifecycle phase preserves the previous raw device state");
    }

    private static void TestPartialTelemetryPreservesEvidence()
    {
        List<OpticalBurnProgress> updates = [];
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.DvdData, 4096L * 2048, updates.Add);
        reporter.ReportDataUpdate(Event(10, 20, 4));
        PartialDataEvent partial = new();
        reporter.ReportDataUpdate(partial);
        Require(updates[^1].Stage == "finalize", "a failed LBA property cannot mask a valid finalization action");
        int propertyReads = partial.PropertyReads;
        string summary = reporter.GetDiagnosticSummary();
        _ = reporter.GetDiagnosticSummary();
        Require(partial.PropertyReads == propertyReads,
            "failure summaries use cached values and never call the event object or drive again");
        Require(summary.Contains("StartLba=10", StringComparison.Ordinal)
            && summary.Contains("LastWrittenLba=30", StringComparison.Ordinal)
            && summary.Contains("CurrentAction=5", StringComparison.Ordinal),
            "independent property reads retain old missing evidence and accept available final addresses");
        dynamic missing = new ExpandoObject();
        missing.CurrentAction = "invalid";
        missing.StartLba = null;
        reporter.ReportDataUpdate(missing);
        string afterMissing = reporter.GetDiagnosticSummary();
        Require(updates[^1].Stage == "finalize"
            && afterMissing.Contains("StartLba=10", StringComparison.Ordinal)
            && afterMissing.Contains("LastWrittenLba=30", StringComparison.Ordinal)
            && afterMissing.Contains("CurrentAction=5", StringComparison.Ordinal)
            && afterMissing.Contains("UpdateEventCount=3", StringComparison.Ordinal),
            "invalid or missing properties cannot clear raw evidence or downgrade the known phase");
    }

    private static void TestPhaseDiagnosticsAreNotRepeated()
    {
        List<OpticalBurnProgress> updates = [];
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.DvdData, 4096L * 2048, updates.Add);
        reporter.ReportDataUpdate(Event(0, 100, 4));
        reporter.ReportDataUpdate(Event(0, 200, 4));
        Require(updates.Count(x => x.Stage == "diagnostic") == 1,
            "normal per-sector updates do not spam device snapshots");
        reporter.ReportDataUpdate(Event(0, 250, 5));
        reporter.ReportDataUpdate(Event(0, 260, 5));
        Require(updates.Count(x => x.Stage == "diagnostic") == 2,
            "only phase transitions publish a snapshot while repeated phases still refresh cached addresses");
        Require(updates[^1].Stage == "finalize"
            && updates[^3].Stage == "diagnostic"
            && reporter.GetDiagnosticSummary().Contains("LastWrittenLba=260", StringComparison.Ordinal),
            "diagnostic output precedes the user-facing phase and snapshots continue updating silently");
    }

    private sealed class PartialDataEvent : DynamicObject
    {
        internal int PropertyReads { get; private set; }

        public override bool TryGetMember(GetMemberBinder binder, out object? result)
        {
            PropertyReads++;
            result = null;
            if (binder.Name == "StartLba") throw new COMException("Optional LBA unavailable");
            if (binder.Name == "CurrentAction") result = 5;
            else if (binder.Name == "LastWrittenLba") result = 30;
            else return false;
            return true;
        }
    }

    private static void TestPhaseResetAndUnavailableTelemetry()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.DvdData, 4096L * 2048,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportDataUpdate(Event(0, 0, 4));
        seconds = 1;
        reporter.ReportDataUpdate(Event(0, 100, 4));
        Require(updates[^1].BytesPerSecond > 0, "fixture establishes a measured write rate");
        reporter.ReportDataUpdate(Event(0, 100, 5));
        Require(updates[^1].Stage == "finalize" && updates[^1].BytesPerSecond is null,
            "data-writer finalization never displays the last write rate");
        seconds = 2;
        reporter.ReportDataUpdate(Event(0, 200, 4));
        Require(updates[^1].BytesPerSecond is null, "writing after another phase begins measuring again");
        reporter.ReportDataUpdate(Event(0, 200, 2));
        Require(updates[^1].Stage == "media" && updates[^1].BytesPerSecond is null,
            "device preparation has no live throughput");
        reporter.ReportDataUpdate(new ExpandoObject());
        Require(updates[^1].BytesPerSecond is null, "unsupported event properties remain an unavailable measurement");
        reporter.ReportStage("buffer", "buffering");
        Require(updates[^1].Stage == "buffer" && updates[^1].BytesPerSecond is null,
            "buffering cannot inherit a device write speed");
    }

    private static void TestRecordedAddressesPreservePhaseAndRate()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        const long length = 1024L * 2048;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.BluRayData, length,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportAcceptedBytes(0, length);
        seconds = 1;
        reporter.ReportAcceptedBytes(32L * 2048, length);
        EqualRate(65_536, updates[^1], "fixture has a measured command-acceptance rate");
        int before = updates.Count;
        reporter.ReportRecordedSectors(0, 15);
        string recorded = reporter.GetDiagnosticSummary();
        Require(updates.Count == before + 1 && updates[^1].Stage == "diagnostic"
            && recorded.Contains("Stage=write", StringComparison.Ordinal)
            && recorded.Contains("LastWrittenLba=15", StringComparison.Ordinal)
            && recorded.Contains("LastAcceptedLba=31", StringComparison.Ordinal)
            && recorded.Contains("MMC READ TRACK INFORMATION (LRA_V=1)", StringComparison.Ordinal),
            "valid recorded-address telemetry is diagnostic evidence, distinct from accepted commands");
        seconds = 2;
        reporter.ReportAcceptedBytes(64L * 2048, length);
        EqualRate(65_536, updates[^1], "recorded-address evidence does not reset or alter the command-rate window");
        reporter.ReportStage("finalize", "Closing session");
        reporter.ReportRecordedSectors(0, 63);
        string finalizing = reporter.GetDiagnosticSummary();
        Require(updates[^1].Stage == "diagnostic"
            && finalizing.Contains("Stage=finalize", StringComparison.Ordinal)
            && finalizing.Contains("LastWrittenLba=63", StringComparison.Ordinal)
            && finalizing.Contains("LastAcceptedLba=63", StringComparison.Ordinal),
            "a recorded address during finalization preserves the lifecycle phase and accepted address");
    }

    private static object Event(int startLba, int lastWrittenLba, int? action = null, int lastReadLba = 0)
    {
        dynamic args = new ExpandoObject();
        args.StartLba = startLba;
        args.LastWrittenLba = lastWrittenLba;
        args.LastReadLba = lastReadLba;
        if (action is { } value) args.CurrentAction = value;
        return args;
    }

    private static void TestConsumerFailureIsIsolated()
    {
        int calls = 0;
        int diagnostics = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.DvdData, 4096L * 2048, value =>
        {
            if (value.Stage == "diagnostic") diagnostics++;
            else calls++;
            throw new InvalidOperationException("A broken UI observer must not stop device writing.");
        });
        reporter.ReportDataUpdate(Event(0, 10, 4));
        reporter.ReportDataUpdate(Event(0, 10, 5));
        reporter.ReportRawCdUpdate(Event(0, 10, 2));
        reporter.ReportEngineUpdate(Event(0, 10));
        reporter.ReportStage("buffer", "buffering");
        Require(calls == 5 && diagnostics == 5,
            "both phase diagnostics and progress isolate a throwing observer and remain usable");
    }

    private static void EqualRate(double expected, OpticalBurnProgress actual, string message)
        => Require(actual.BytesPerSecond is { } rate && double.IsFinite(rate) && Math.Abs(rate - expected) < 0.0001, message);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Optical write progress: " + message);
    }
}
