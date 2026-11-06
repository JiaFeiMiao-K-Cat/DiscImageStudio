using System.IO;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

internal static class BluRayDetailedDiagnosticsTests
{
    private static readonly OpticalBurnBufferOptions Buffers = new(256 * 1024, 256 * 1024);
    private static readonly BluRaySequentialMediaInfo BlankMedia = new(
        0x41, true, 1, true, true, 0, 0, 12_219_392, 12_219_392, 12_219_392, 1);

    internal static void Run()
    {
        TestSuccessfulBurn();
        TestCachedFailureBeforeCleanup();
        TestRejectedMediaContext();
        TestFinalizationFailures();
        TestCancellation();
        TestDiagnosticCallbackIsolation();
        Console.WriteLine("blu-ray-detailed-diagnostics: passed (cached media/speed/source, stage timing, real sense, finalization/cancellation and isolated observers; no hardware)");
    }

    private static void TestSuccessfulBurn()
    {
        OpticalBurnRequest request = Request();
        using GeneratedContentComStream content = Content(request);
        using FakeSession session = new();
        List<string> messages = [];
        List<string> operations = [];
        int failures = 0;
        OpticalWriteSpeed actual = WindowsImapiBurner.BurnBluRay(session, content, request,
            CancellationToken.None, reportOperation: operations.Add, reportFailure: _ => failures++,
            reportDiagnostic: messages.Add);

        Require(actual == session.ActualSpeed && session.RequestedSpeed == request.WriteSpeed && failures == 0,
            "diagnostics preserve selected/actual speed and do not report a false failure");
        Require(session.Calls.SequenceEqual(new[]
            { "acquire", "read-media", "configure", "write", "flush", "close-track", "close-disc", "release" }),
            "successful logging adds no media probe, retry or other device command");
        Require(messages.Any(message => message.Contains("Requested=4500 sectors/s")
                && message.Contains("Actual=9000 sectors/s"))
            && messages.Any(message => message.Contains("ContentBytes=65536") && message.Contains("TrackSectors=32")),
            "request size and requested/actual speeds are logged");
        AssertMedia(string.Join("\n", messages), BlankMedia);
        Require(operations.All(operation => messages.Any(message => message.StartsWith("BD-R 阶段：")
                && message.Contains($"Operation={operation}；Result=") && message.Contains("ElapsedMs=")))
            && messages.Any(message => message.Contains("Operation=释放 BD-R 刻录机访问权；Result=completed")),
            "every existing operation, including final release, has an elapsed-time outcome");
        Require(!messages.Any(message => message.Contains("BD-R 失败：")) && session.Failure.Data.Count == 0,
            "success has no invented failure context");
    }

    private static void TestCachedFailureBeforeCleanup()
    {
        OpticalBurnRequest request = Request();
        using GeneratedContentComStream content = Content(request);
        using FakeSession session = new() { FailAt = "write" };
        session.Failure.Data["Burn.SenseStatus"] = "device sense 05/64/00";
        session.OnFlush = () => content.Read(new byte[32], 32, IntPtr.Zero);
        Exception? observed = null;
        bool observedBeforeCleanup = false;
        string? savedContent = null;
        string? savedContext = null;
        List<string> messages = [];
        Exception caught = CaptureFailure(() => WindowsImapiBurner.BurnBluRay(session, content, request,
            CancellationToken.None, reportFailure: error =>
            {
                observed = error;
                observedBeforeCleanup = !session.Calls.Contains("flush") && !session.Calls.Contains("release");
                savedContent = error.Data["Burn.ContentAtFailure"] as string;
                savedContext = error.Data["Burn.BluRayContext"] as string;
            }, reportDiagnostic: messages.Add));

        Require(ReferenceEquals(caught, session.Failure) && ReferenceEquals(observed, caught)
            && caught.HResult == unchecked((int)0xC0AA0301), "write failure keeps its original exception and HRESULT");
        Require(observedBeforeCleanup && !string.IsNullOrEmpty(savedContext) && !string.IsNullOrEmpty(savedContent),
            "the failure observer sees complete context before flush or release");
        Require(Equals(caught.Data["Burn.ContentAtFailure"], savedContent)
            && savedContent != content.GetDiagnosticSummary(),
            "cleanup reads cannot replace the source snapshot taken at failure");
        string context = savedContext ?? string.Empty;
        AssertMedia(context, BlankMedia);
        Require(context.Contains("WriteStarted=True") && context.Contains("WriteReturned=False")
            && context.Contains("OperationElapsedMs=") && context.Contains("ElapsedMs=")
            && context.Contains("Requested=4500 sectors/s") && context.Contains("Actual=9000 sectors/s"),
            "failure retains write state, both speeds and elapsed time");
        Require(caught is MmcCommandException { HasValidSense: true, SenseKey: 5,
                AdditionalSenseCode: 0x64, AdditionalSenseQualifier: 0 }
            && Equals(caught.Data["Burn.SenseStatus"], "device sense 05/64/00")
            && !messages.Any(message => message.Contains("Sense") && message.Contains("unavailable")),
            "real MMC sense and existing sense diagnostics are never replaced by generic unavailable text");
        Require(session.Calls.SequenceEqual(new[]
            { "acquire", "read-media", "configure", "write", "flush", "release" }),
            "failure logs use cached media and retain exactly one cleanup flush and release without closing");
    }

    private static void TestRejectedMediaContext()
    {
        OpticalBurnRequest request = Request();
        using GeneratedContentComStream content = Content(request);
        BluRaySequentialMediaInfo rejected = new(0x43, false, 2, false, false, 32, 64, 128, 256, 512, 3);
        using FakeSession session = new() { Media = rejected };
        Exception caught = CaptureFailure(() => WindowsImapiBurner.BurnBluRay(session, content, request,
            CancellationToken.None));
        AssertMedia(caught.Data["Burn.BluRayContext"] as string ?? string.Empty, rejected);
        Require(caught is InvalidOperationException && session.Calls.SequenceEqual(new[] { "acquire", "read-media", "release" }),
            "rejected media retains all measured values before validation stops the write");
    }

    private static void TestFinalizationFailures()
    {
        foreach (string stage in new[] { "flush", "close-track", "close-disc", "release" })
        {
            OpticalBurnRequest request = Request();
            using GeneratedContentComStream content = Content(request);
            using FakeSession session = new() { FailAt = stage };
            string[]? callsAtFailure = null;
            Exception caught = CaptureFailure(() => WindowsImapiBurner.BurnBluRay(session, content, request,
                CancellationToken.None, reportFailure: _ => callsAtFailure = session.Calls.ToArray()));
            string context = caught.Data["Burn.BluRayContext"] as string ?? string.Empty;
            string operation = stage switch
            {
                "flush" => "刷新 BD-R 缓存（请等待完成）",
                "close-track" => "关闭 BD-R 轨道（请等待完成）",
                "close-disc" => "封闭 BD-R 盘片（请等待完成）",
                _ => "释放 BD-R 刻录机访问权",
            };
            Require(ReferenceEquals(caught, session.Failure) && Equals(caught.Data["Burn.Operation"], operation)
                && context.Contains("WriteStarted=True") && context.Contains("WriteReturned=True")
                && context.Contains("FlushReturned=" + (stage != "flush")),
                "finalization failure retains its stage and distinguishes a returned write from a completed flush");
            Require(callsAtFailure is not null && callsAtFailure[^1] == stage
                && session.Calls.Count(call => call == "release") == 1
                && session.Calls.Count(call => call == "flush") == (stage == "flush" ? 2 : 1)
                && session.Calls.Count(call => call == "read-media") == 1,
                "failure is captured before subsequent cleanup and existing flush/release behavior is preserved");
            if (stage is "flush" or "close-track")
                Require(!session.Calls.Contains("close-disc"), "a failed finalization stage cannot continue to close the disc");
        }
    }

    private static void TestCancellation()
    {
        foreach (bool throwCancellation in new[] { false, true })
        {
            using CancellationTokenSource cancellation = new();
            OpticalBurnRequest request = Request();
            using GeneratedContentComStream content = Content(request);
            Exception original = throwCancellation
                ? new OperationCanceledException("Write cancelled.", cancellation.Token)
                : new IOException("Device interrupted after cancellation.");
            using FakeSession session = new() { FailAt = "write", Failure = original, OnWrite = cancellation.Cancel };
            Exception caught = CaptureFailure(() => WindowsImapiBurner.BurnBluRay(session, content, request,
                cancellation.Token));
            Require(caught is OperationCanceledException cancelled && cancelled.CancellationToken == cancellation.Token
                && (throwCancellation ? ReferenceEquals(caught, original) : ReferenceEquals(caught.InnerException, original)),
                "cancellation preserves the original exception and token, including the existing wrapper behavior");
            string context = original.Data["Burn.BluRayContext"] as string ?? string.Empty;
            Require(context.Contains("CancellationRequested=True") && context.Contains("WriteStarted=True")
                && context.Contains("WriteReturned=False") && original.Data["Burn.ContentAtFailure"] is string,
                "cancelled writes keep their failure-time state and content snapshot");
            Require(session.Calls.SequenceEqual(new[]
                { "acquire", "read-media", "configure", "write", "cancel", "flush", "release" }),
                "cancelled writes retain one cancellation, drain and release without closing the disc");
        }
    }

    private static void TestDiagnosticCallbackIsolation()
    {
        foreach (bool failWrite in new[] { false, true })
        {
            OpticalBurnRequest request = Request();
            using GeneratedContentComStream content = Content(request);
            using FakeSession session = new() { FailAt = failWrite ? "write" : null };
            int reports = 0;
            Exception? caught = null;
            try
            {
                WindowsImapiBurner.BurnBluRay(session, content, request, CancellationToken.None,
                    reportDiagnostic: _ => { reports++; throw new IOException("Log sink unavailable."); });
            }
            catch (Exception exception) { caught = exception; }
            Require(failWrite ? ReferenceEquals(caught, session.Failure) : caught is null,
                "throwing diagnostics cannot turn success into failure or replace a device exception");
            Require(reports > 0 && session.Calls.Count(call => call == "read-media") == 1
                && session.Calls.Count(call => call == "write") == 1 && session.Calls.Count(call => call == "release") == 1,
                "throwing log callbacks cannot add probes/retries or prevent release");
        }
    }

    private static void AssertMedia(string text, BluRaySequentialMediaInfo media)
    {
        string[] fields =
        [
            $"Profile=0x{media.Profile:X4}", $"DiscIsBlank={media.DiscIsBlank}", $"FormatStatus={media.FormatStatus}",
            $"TrackIsBlank={media.TrackIsBlank}", $"NextWritableAddressValid={media.NextWritableAddressValid}",
            $"TrackStartAddress={media.TrackStartAddress}", $"NextWritableAddress={media.NextWritableAddress}",
            $"FreeBlocks={media.FreeBlocks}", $"TrackSize={media.TrackSize}",
            $"MaximumBlocks={media.MaximumBlocks}", $"TrackNumber={media.TrackNumber}",
        ];
        Require(fields.All(field => text.Contains(field, StringComparison.Ordinal)),
            "all measured BD media fields, including blank/format state, addresses and capacities, are retained");
    }

    private static OpticalBurnRequest Request()
        => new("fake-recorder", OpticalBurnMediaKind.BluRayData, 32 * 2048,
            (output, token) => { token.ThrowIfCancellationRequested(); output.Write(new byte[32 * 2048]); },
            WriteSpeed: new(4500, false), BufferOptions: Buffers);

    private static GeneratedContentComStream Content(OpticalBurnRequest request)
    {
        GeneratedContentComStream content = new(request.ContentLength, request.ProduceContent, null,
            CancellationToken.None, Buffers);
        content.WaitUntilPrebuffered(CancellationToken.None);
        return content;
    }

    private static Exception CaptureFailure(Action action)
    {
        try { action(); }
        catch (Exception exception) { return exception; }
        throw new InvalidOperationException("Expected simulated BD failure.");
    }

    private sealed class FakeSession : IBluRaySequentialSession
    {
        public List<string> Calls { get; } = [];
        public BluRaySequentialMediaInfo Media { get; init; } = BlankMedia;
        public string? FailAt { get; init; }
        public Exception Failure { get; init; } = new MmcCommandException(0x2A,
            new(unchecked((int)0xC0AA0301), [0x72, 5, 0x64, 0], []));
        public OpticalWriteSpeed ActualSpeed { get; } = new(9000, false);
        public OpticalWriteSpeed? RequestedSpeed { get; private set; }
        public Action? OnWrite { get; init; }
        public Action? OnFlush { get; set; }
        public void Acquire() => Step("acquire");
        public BluRaySequentialMediaInfo ReadMedia() { Step("read-media"); return Media; }
        public OpticalWriteSpeed ConfigureWrite(OpticalWriteSpeed? requested)
        {
            Step("configure");
            RequestedSpeed = requested;
            return ActualSpeed;
        }
        public void Write(IStream content, uint sectorCount)
        {
            Calls.Add("write");
            OnWrite?.Invoke();
            if (FailAt == "write") throw Failure;
        }
        public void CancelWrite() => Calls.Add("cancel");
        public void Flush() { Step("flush"); OnFlush?.Invoke(); }
        public void CloseTrack(int trackNumber) => Step("close-track");
        public void CloseDisc() => Step("close-disc");
        public void Release() => Step("release");
        public void Dispose() { }
        private void Step(string name) { Calls.Add(name); if (FailAt == name) throw Failure; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("BD detailed diagnostics: " + message);
    }
}
