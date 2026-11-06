using System.Dynamic;
using DiscImageStudio.Burning;

internal static class CdProgressTelemetryTests
{
    internal static void Run()
    {
        TestLeadInSnapshotsAreThrottledAndDoNotAdvancePayload();
        TestOptionalTelemetryAndFailureSnapshot();
        TestObserverFailureDoesNotInterruptUpdates();
        Console.WriteLine("cd-progress-telemetry: passed (throttled lead-in snapshots, cached IMAPI buffers, event age, optional properties and observer isolation; no hardware)");
    }

    private static void TestLeadInSnapshotsAreThrottledAndDoNotAdvancePayload()
    {
        List<OpticalBurnProgress> reports = [];
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.CdAudio, 10000L * 2352,
            reports.Add, () => TimeSpan.FromSeconds(seconds));

        reporter.ReportRawCdUpdate(Event(-11634, -11634));
        Require(reports.Count(x => x.Stage == "diagnostic") == 1,
            "the first lead-in event publishes just its phase snapshot");
        for (int index = 1; index <= 19; index++)
        {
            seconds = index / 10.0;
            reporter.ReportRawCdUpdate(Event(-11634, -11634 + index));
        }
        Require(reports.Count(x => x.Stage == "diagnostic") == 1,
            "frequent updates in the first two seconds do not flood the log");
        seconds = 2;
        reporter.ReportRawCdUpdate(Event(-11634, -11000));
        Require(reports.Count(x => x.Stage == "diagnostic") == 2,
            "an arriving event after two seconds produces one new snapshot");
        string periodic = reports.Last(x => x.Stage == "diagnostic").Message;
        Require(periodic.Contains("LastWrittenLba=-11000", StringComparison.Ordinal)
            && periodic.Contains("UpdateEventCount=21", StringComparison.Ordinal),
            "periodic snapshots include the most recent address and event count");
        seconds = 2.1;
        reporter.ReportRawCdUpdate(Event(-11634, -10999));
        Require(reports.Count(x => x.Stage == "diagnostic") == 2,
            "an event immediately after the periodic snapshot is still throttled");
        seconds = 8;
        reporter.ReportRawCdUpdate(Event(-11634, -10387));
        Require(reports.Count(x => x.Stage == "diagnostic") == 3,
            "a delayed event produces one snapshot, not catch-up timer messages");
        Require(reports.All(x => x.CompletedBytes == 0 && x.BytesPerSecond is null),
            "negative lead-in LBAs and image reads never become payload progress or throughput");

        seconds = 9;
        reporter.ReportRawCdUpdate(Event(-11634, 99));
        Require(reports[^1].Stage == "write" && reports[^1].CompletedBytes == 100L * 2352,
            "payload progress starts at LBA zero after leaving the lead-in");
        int diagnostics = reports.Count(x => x.Stage == "diagnostic");
        seconds = 12;
        reporter.ReportRawCdUpdate(Event(-11634, 174));
        Require(reports.Count(x => x.Stage == "diagnostic") == diagnostics,
            "the lead-in heartbeat does not add periodic payload-phase logging");
    }

    private static void TestOptionalTelemetryAndFailureSnapshot()
    {
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.CdAudio, 10000L * 2352,
            null, () => TimeSpan.FromSeconds(seconds));
        string initial = reporter.GetDiagnosticSummary();
        Require(initial.Contains("UpdateEventCount=0", StringComparison.Ordinal)
            && initial.Contains("LastUpdateAgeSeconds=unknown", StringComparison.Ordinal)
            && initial.Contains("TotalSystemBuffer=unknown", StringComparison.Ordinal),
            "unavailable telemetry is unknown rather than a fictitious zero");
        seconds = 1;
        CountingEvent first = new();
        reporter.ReportRawCdUpdate(first);
        int reads = first.Reads;
        seconds = 4.5;
        string summary = reporter.GetDiagnosticSummary();
        _ = reporter.GetDiagnosticSummary();
        Require(first.Reads == reads, "failure summaries never query the event object again");
        Require(summary.Contains("LastReadLba=-9000（来源=IMAPI RawCD，事件=1）", StringComparison.Ordinal)
            && summary.Contains("TotalSystemBuffer=4194304 B", StringComparison.Ordinal)
            && summary.Contains("UsedSystemBuffer=1048576 B", StringComparison.Ordinal)
            && summary.Contains("FreeSystemBuffer=3145728 B", StringComparison.Ordinal)
            && summary.Contains("LastUpdateAgeSeconds=3.500", StringComparison.Ordinal),
            "the cached image-read position, buffer bytes and time since the last event remain available");

        seconds = 5;
        dynamic partial = new ExpandoObject();
        partial.CurrentAction = 2;
        partial.StartLba = -11634;
        partial.LastWrittenLba = -10387;
        partial.UsedSystemBuffer = -1;
        partial.FreeSystemBuffer = 2097152;
        reporter.ReportRawCdUpdate(partial);
        string afterPartial = reporter.GetDiagnosticSummary();
        Require(afterPartial.Contains("LastWrittenLba=-10387", StringComparison.Ordinal)
            && afterPartial.Contains("LastReadLba=-9000（来源=IMAPI RawCD，事件=1）", StringComparison.Ordinal)
            && afterPartial.Contains("UsedSystemBuffer=1048576 B（来源=IMAPI RawCD，事件=1）", StringComparison.Ordinal)
            && afterPartial.Contains("FreeSystemBuffer=2097152 B（来源=IMAPI RawCD，事件=2）", StringComparison.Ordinal),
            "partial events preserve earlier valid values and label their event, rejecting negative byte counts");

        seconds = 6;
        reporter.ReportRawCdUpdate(new ThrowingEvent());
        string afterThrow = reporter.GetDiagnosticSummary();
        Require(afterThrow.Contains("LastWrittenLba=-10387", StringComparison.Ordinal)
            && afterThrow.Contains("UpdateEventCount=3", StringComparison.Ordinal)
            && afterThrow.Contains("LastUpdateAgeSeconds=0.000", StringComparison.Ordinal),
            "throwing optional getters cannot escape or erase cached device evidence");
        seconds = 7;
        reporter.ReportEngineUpdate(Event(0, 1));
        string mixed = reporter.GetDiagnosticSummary();
        Require(mixed.Contains("EventSource=IMAPI WriteEngine", StringComparison.Ordinal)
            && mixed.Contains("LastReadLba=-9000（来源=IMAPI RawCD，事件=1）", StringComparison.Ordinal),
            "old optional values retain their real source when a different event source omits them");
    }

    private static void TestObserverFailureDoesNotInterruptUpdates()
    {
        int diagnostics = 0;
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.CdAudio, 10000L * 2352,
            progress =>
            {
                if (progress.Stage == "diagnostic") diagnostics++;
                throw new InvalidOperationException("Injected log consumer failure");
            }, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportRawCdUpdate(Event(-11634, -11634));
        seconds = 2;
        reporter.ReportRawCdUpdate(Event(-11634, -11000));
        seconds = 2.1;
        reporter.ReportRawCdUpdate(Event(-11634, -10999));
        Require(diagnostics == 2
            && reporter.GetDiagnosticSummary().Contains("LastWrittenLba=-10999", StringComparison.Ordinal),
            "a failing observer neither interrupts updates nor causes an unthrottled retry storm");
    }

    private static object Event(int start, int written)
    {
        dynamic value = new ExpandoObject();
        value.CurrentAction = 2;
        value.StartLba = start;
        value.LastWrittenLba = written;
        return value;
    }

    private sealed class CountingEvent : DynamicObject
    {
        internal int Reads { get; private set; }

        public override bool TryGetMember(GetMemberBinder binder, out object? result)
        {
            Reads++;
            result = binder.Name switch
            {
                "CurrentAction" => 2,
                "StartLba" => -11634,
                "LastWrittenLba" => -11000,
                "LastReadLba" => -9000,
                "TotalSystemBuffer" => 4194304,
                "UsedSystemBuffer" => 1048576,
                "FreeSystemBuffer" => 3145728,
                _ => null,
            };
            return result is not null;
        }
    }

    private sealed class ThrowingEvent : DynamicObject
    {
        public override bool TryGetMember(GetMemberBinder binder, out object? result)
            => throw new InvalidOperationException("Injected optional event getter failure");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD progress telemetry: " + message);
    }
}
