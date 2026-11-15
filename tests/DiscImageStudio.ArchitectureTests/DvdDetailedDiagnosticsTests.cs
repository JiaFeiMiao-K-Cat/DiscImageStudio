using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

public static class DvdDetailedDiagnosticsTests
{
    internal static void Run()
    {
        TestSuccessfulBurnOrder();
        TestCachedWriteFailure();
        TestFailureBeforeCancellationCompletes();
        TestPrewriteFailures();
        TestInvalidMedia();
        TestUnavailableDiagnostics();
        TestDynamicAdapter();
        Console.WriteLine("dvd-detailed-diagnostics: passed (write order, pre-write failures, cached source/context, original HRESULT and optional diagnostics; no hardware)");
    }

    private static void TestSuccessfulBurnOrder()
    {
        foreach (bool requestedSpeed in new[] { true, false })
        {
            OpticalBurnRequest request = Request() with { WriteSpeed = requestedSpeed ? new(750, false) : null };
            using GeneratedContentComStream content = Content(request);
            FakeSession session = new();
            List<string> messages = [];
            int failures = 0;
            OpticalWriteSpeed? reportedSpeed = null;
            session.OnWrite = () => Require(!messages.Any(message => message.Contains("WriteReturned=True")),
                "a write still in progress is never reported as returned");
            OpticalWriteSpeed actual = WindowsImapiBurner.BurnData(session, content, request, CancellationToken.None,
                reportWriteSpeed: speed => reportedSpeed = speed,
                reportFailure: _ => failures++, reportDiagnostic: messages.Add);

            string[] expected = requestedSpeed
                ? ["state", "capacity", "configure", "verification", "set-speed", "actual-speed", "write"]
                : ["state", "capacity", "configure", "verification", "actual-speed", "write"];
            Require(session.Calls.Where(call => call != "diagnostic").SequenceEqual(expected),
                "capacity checks, configuration, verification, speed and a single write preserve their original order");
            Require(session.DiagnosticReads == 1 && session.Calls.IndexOf("diagnostic") < session.Calls.IndexOf("write"),
                "optional settings are read once before writing");
            Require(actual == new OpticalWriteSpeed(780, true) && reportedSpeed == actual && failures == 0,
                "the actual device speed is returned and reported without a false failure");
            Require(session.RequestedSpeed == request.WriteSpeed,
                "requested speed reaches the session unchanged, including automatic selection");
            Require(messages.Any(message => message.Contains("FreeSectors=100") && message.Contains("RequiredSectors=32"))
                && messages.Any(message => message.Contains("Actual=780 sectors/s"))
                && messages.Any(message => message.Contains(requestedSpeed ? "Requested=750 sectors/s" : "Requested=auto/fallback"))
                && messages.Any(message => message.Contains("WriteReturned=True") && message.Contains("ElapsedMs=")),
                "successful diagnostics include capacity, requested/actual speed, duration and returned-write status");
        }
    }

    private static void TestCachedWriteFailure()
    {
        OpticalBurnRequest request = Request();
        using GeneratedContentComStream content = Content(request);
        FakeSession session = new() { FailAt = "write" };
        Exception? observed = null;
        string? observedContent = null;
        string? observedContext = null;
        string? observedSense = null;
        bool observedBeforeCleanup = false;
        bool snapshotBeforeCleanup = false;
        int failureReports = 0;
        Exception? caught = null;
        session.OnWriteCleanup = () =>
        {
            snapshotBeforeCleanup = session.Failure.Data["Burn.ContentAtFailure"] is string;
            content.Read(new byte[32], 32, IntPtr.Zero);
        };
        try
        {
            WindowsImapiBurner.BurnData(session, content, request, CancellationToken.None,
                reportFailure: exception =>
                {
                    failureReports++;
                    observed = exception;
                    observedBeforeCleanup = !session.WriteCleanupStarted;
                    observedContent = exception.Data["Burn.ContentAtFailure"] as string;
                    observedContext = exception.Data["Burn.DvdContext"] as string;
                    observedSense = exception.Data["Burn.SenseStatus"] as string;
                });
        }
        catch (COMException exception) { caught = exception; }

        Require(ReferenceEquals(caught, session.Failure) && ReferenceEquals(observed, session.Failure)
            && caught?.HResult == unchecked((int)0xC0AA0301),
            "the original device exception and HRESULT reach observer and caller");
        Require(observedBeforeCleanup && snapshotBeforeCleanup && failureReports == 1 && session.WriteCleanupStarted,
            "failure is cached and reported exactly once before the session enters its write cleanup");
        Require(!string.IsNullOrEmpty(observedContent) && observedContent != content.GetDiagnosticSummary()
            && Equals(observedContent, session.Failure.Data["Burn.ContentAtFailure"]),
            "source diagnostics are cached before session cleanup can consume or dispose the stream");
        string context = observedContext ?? string.Empty;
        Require(context.Contains("WriteStarted=True") && context.Contains("WriteReturned=False")
            && context.Contains("OperationElapsedMs=") && context.Contains("ElapsedMs="),
            "failure distinguishes a started write from a returned write and captures elapsed time");
        Require(context.Contains("MediaState=") && context.Contains("FreeSectors=100") && context.Contains("RequiredSectors=32")
            && context.Contains("Requested=750 sectors/s") && context.Contains("Actual=780 sectors/s")
            && context.Contains("mock DVD settings"),
            "failure retains capacity, speeds and optional settings without querying the device again");
        string sense = observedSense ?? string.Empty;
        Require(sense.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            && !sense.Contains("5/64/00", StringComparison.Ordinal),
            "DVD sense data is explicitly unavailable instead of guessed from HRESULT");
        Require(session.Calls.Count(call => call == "write") == 1 && session.DiagnosticReads == 1,
            "failure recording neither retries the write nor repeats optional COM reads");
    }

    private static void TestFailureBeforeCancellationCompletes()
    {
        OpticalBurnRequest request = Request();
        using GeneratedContentComStream content = Content(request);
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim cancelEntered = new();
        using ManualResetEventSlim cancelExited = new();
        using ManualResetEventSlim releaseCancel = new();
        FakeSession session = new() { FailAt = "write" };
        Task? cancelTask = null;
        bool failureObservedDuringCancel = false;
        Exception? caught = null;
        session.OnCancel = () =>
        {
            cancelEntered.Set();
            try { releaseCancel.Wait(TimeSpan.FromSeconds(5)); }
            finally { cancelExited.Set(); }
        };
        session.OnWrite = () =>
        {
            cancelTask = Task.Run(cancellation.Cancel);
            Require(cancelEntered.Wait(TimeSpan.FromSeconds(5)), "cancellation reaches the device callback");
        };
        try
        {
            WindowsImapiBurner.BurnData(session, content, request, cancellation.Token,
                reportFailure: exception =>
                {
                    failureObservedDuringCancel = !cancelExited.IsSet
                        && exception.Data["Burn.DvdContext"] is string
                        && exception.Data["Burn.ContentAtFailure"] is string;
                    releaseCancel.Set();
                });
        }
        catch (COMException exception) { caught = exception; }
        finally
        {
            releaseCancel.Set();
            cancelTask?.GetAwaiter().GetResult();
        }
        Require(ReferenceEquals(caught, session.Failure) && failureObservedDuringCancel,
            "failure diagnostics do not wait for cancellation registration disposal or a blocked device cancel callback");
    }

    private static void TestPrewriteFailures()
    {
        foreach (string step in new[] { "configure", "verification" })
        {
            OpticalBurnRequest request = Request();
            using GeneratedContentComStream content = Content(request);
            FakeSession session = new() { FailAt = step };
            Exception? observed = null;
            Exception? caught = null;
            try
            {
                WindowsImapiBurner.BurnData(session, content, request, CancellationToken.None,
                    reportFailure: exception => observed = exception);
            }
            catch (COMException exception) { caught = exception; }
            Require(ReferenceEquals(caught, session.Failure) && ReferenceEquals(observed, session.Failure),
                "configuration and verification failures preserve the original exception");
            Require(!session.Calls.Contains("write") && !session.Calls.Contains("set-speed")
                && session.Calls.Contains("verification") == (step == "verification"),
                "an unsuccessful preparation step stops all subsequent device mutations");
            string context = session.Failure.Data["Burn.DvdContext"] as string ?? string.Empty;
            Require(context.Contains("WriteStarted=False") && context.Contains("WriteReturned=False")
                && session.Failure.Data["Burn.ContentAtFailure"] is string,
                "pre-write failures include source context and do not claim a write started");
        }
    }

    private static void TestInvalidMedia()
    {
        foreach ((int state, long capacity) in new[] { (0, 100L), (2, 31L) })
        {
            OpticalBurnRequest request = Request();
            using GeneratedContentComStream content = Content(request);
            FakeSession session = new() { MediaState = state, Capacity = capacity };
            Exception? caught = null;
            try { WindowsImapiBurner.BurnData(session, content, request, CancellationToken.None); }
            catch (InvalidOperationException exception) { caught = exception; }
            Require(caught is not null && !session.Calls.Contains("configure") && !session.Calls.Contains("write"),
                "nonblank or undersized media are rejected before configuration or writing");
            string context = caught?.Data["Burn.DvdContext"] as string ?? string.Empty;
            Require(context.Contains("MediaState=") && context.Contains($"FreeSectors={capacity}")
                && context.Contains("RequiredSectors=32") && context.Contains("WriteStarted=False"),
                "media rejection records the actual capacity and requirements");
        }
    }

    private static void TestUnavailableDiagnostics()
    {
        foreach (bool failWrite in new[] { false, true })
        {
            OpticalBurnRequest request = Request();
            using GeneratedContentComStream content = Content(request);
            FakeSession session = new() { FailAt = failWrite ? "write" : null, FailDiagnosticRead = true };
            Exception? caught = null;
            int reports = 0;
            int failureReports = 0;
            try
            {
                WindowsImapiBurner.BurnData(session, content, request, CancellationToken.None,
                    reportFailure: _ => { failureReports++; throw new InvalidOperationException("Unavailable failure sink."); },
                    reportDiagnostic: _ => { reports++; throw new InvalidOperationException("Unavailable log sink."); });
            }
            catch (COMException exception) { caught = exception; }
            Require(failWrite ? ReferenceEquals(caught, session.Failure) : caught is null,
                "broken optional getters and callbacks neither prevent writing nor replace its original exception");
            Require(reports > 0 && session.DiagnosticReads == 1 && session.Calls.Count(call => call == "write") == 1,
                "optional diagnostics fail independently without causing a retry");
            Require(failureReports == (failWrite ? 1 : 0),
                "a throwing failure observer is not repeated by the outer exception handler");
        }
    }

    private static void TestDynamicAdapter()
    {
        DynamicFormat format = new();
        DynamicDvdDataSession session = new(format);
        Require(session.CurrentMediaStatus == 2 && session.FreeSectorsOnMedia == 100
                && session.TotalSectorsOnMedia == 1000,
            "the adapter preserves the device media state, capacity and total extent");
        session.ConfigureMedia(forceOverwrite: false);
        session.ConfigureMedia(forceOverwrite: true);
        session.SetWriteSpeed(new OpticalWriteSpeed(750, false));
        Require(format.Calls.SequenceEqual(new[]
                {
                    "buffer=False", "close=True", "overwrite=False",
                    "buffer=False", "close=True", "overwrite=True",
                    "speed=750,False",
                })
            && session.CurrentWriteSpeed == new OpticalWriteSpeed(780, true),
            "the adapter preserves setter ordering, overwrite selection, values and actual speed");
        OpticalBurnRequest request = Request();
        using GeneratedContentComStream content = Content(request);
        session.Write(content);
        session.CancelWrite();
        Require(ReferenceEquals(format.WrittenContent, content) && format.Calls.Count(call => call == "write") == 1
            && format.Calls.Count(call => call == "cancel") == 1,
            "the adapter forwards the original stream once and tolerates a cancel failure");

        DynamicFormat failingFormat = new() { FailWrite = true };
        DynamicDvdDataSession failingSession = new(failingFormat);
        Exception? observed = null;
        Exception? caught = null;
        int failureReports = 0;
        try
        {
            failingSession.Write(content, exception =>
            {
                failureReports++;
                observed = exception;
                throw new InvalidOperationException("Unavailable failure sink.");
            });
        }
        catch (COMException exception) { caught = exception; }
        Require(failureReports == 1 && ReferenceEquals(observed, failingFormat.Failure)
            && ReferenceEquals(caught, failingFormat.Failure),
            "the adapter reports failures without a progress subscription and preserves the original error when its observer throws");
    }

    private static OpticalBurnRequest Request() => new("fake-recorder", OpticalBurnMediaKind.DvdData, 32 * 2048,
        (output, _) => output.Write(new byte[32 * 2048]), new OpticalWriteSpeed(750, false));

    private static GeneratedContentComStream Content(OpticalBurnRequest request)
    {
        GeneratedContentComStream content = new(request.ContentLength, request.ProduceContent, null, CancellationToken.None);
        content.WaitUntilPrebuffered(CancellationToken.None);
        return content;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("DVD detailed diagnostics: " + message);
    }

    private sealed class FakeSession : IDvdDataSession
    {
        internal List<string> Calls { get; } = [];
        internal COMException Failure { get; } = new("Unrecoverable device write error.", unchecked((int)0xC0AA0301));
        internal string? FailAt { get; init; }
        internal bool FailDiagnosticRead { get; init; }
        internal int DiagnosticReads { get; private set; }
        internal int MediaState { get; init; } = 2;
        internal long Capacity { get; init; } = 100;
        internal OpticalWriteSpeed? RequestedSpeed { get; private set; }
        internal Action? OnWrite { get; set; }
        internal Action? OnWriteCleanup { get; set; }
        internal Action? OnCancel { get; set; }
        internal bool WriteCleanupStarted { get; private set; }
        public int CurrentMediaStatus { get { Calls.Add("state"); return MediaState; } }
        public long FreeSectorsOnMedia { get { Calls.Add("capacity"); return Capacity; } }
        public long TotalSectorsOnMedia => 1000;
        public void ConfigureMedia(bool forceOverwrite) => Step("configure");
        public void DisableVerification() => Step("verification");
        public void SetWriteSpeed(OpticalWriteSpeed speed) { Step("set-speed"); RequestedSpeed = speed; }
        public OpticalWriteSpeed CurrentWriteSpeed { get { Step("actual-speed"); return new(780, true); } }
        public void Write(IStream content, Action<Exception>? reportFailure = null)
        {
            try { OnWrite?.Invoke(); Step("write"); }
            catch (Exception exception)
            {
                try { reportFailure?.Invoke(exception); }
                catch { }
                throw;
            }
            finally { WriteCleanupStarted = true; OnWriteCleanup?.Invoke(); }
        }
        public void CancelWrite() { Step("cancel"); OnCancel?.Invoke(); }
        public string? GetDiagnosticSummary()
        {
            Calls.Add("diagnostic");
            DiagnosticReads++;
            if (FailDiagnosticRead) throw new COMException("Diagnostic property unavailable.", unchecked((int)0x80004001));
            return "mock DVD settings";
        }
        private void Step(string step) { Calls.Add(step); if (FailAt == step) throw Failure; }
    }

    // Public so the production assembly's dynamic COM-style calls can resolve these members.
    public sealed class DynamicFormat
    {
        public List<string> Calls { get; } = [];
        public IStream? WrittenContent { get; private set; }
        public bool FailWrite { get; init; }
        public COMException Failure { get; } = new("Unrecoverable device write error.", unchecked((int)0xC0AA0301));
        public int CurrentMediaStatus => 2;
        public long FreeSectorsOnMedia => 100;
        public long TotalSectorsOnMedia => 1000;
        public bool BufferUnderrunFreeDisabled { set => Calls.Add($"buffer={value}"); }
        public bool ForceMediaToBeClosed { set => Calls.Add($"close={value}"); }
        public bool ForceOverwrite { set => Calls.Add($"overwrite={value}"); }
        public int CurrentWriteSpeed => 780;
        public bool CurrentRotationTypeIsPureCAV => true;
        public void SetWriteSpeed(int sectorsPerSecond, bool pureCav) => Calls.Add($"speed={sectorsPerSecond},{pureCav}");
        public void Write(IStream content)
        {
            Calls.Add("write");
            WrittenContent = content;
            if (FailWrite) throw Failure;
        }
        public void CancelWrite() { Calls.Add("cancel"); throw new COMException("Device no longer writing."); }
    }
}
