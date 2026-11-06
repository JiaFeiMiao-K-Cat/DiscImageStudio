using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

public static class CdDaoDetailedDiagnosticsTests
{
    internal static void Run()
    {
        TestCachedFailureBeforeCleanup();
        TestSuccessfulBurn();
        TestUnavailableDiagnostics();
        TestPartialDeviceProperties();
        Console.WriteLine("cd-dao-detailed-diagnostics: passed (pre-cleanup context, original HRESULT, optional failures, capacity/speed and partial COM properties; no hardware)");
    }

    private static void TestCachedFailureBeforeCleanup()
    {
        foreach (OpticalBurnMediaKind kind in new[] { OpticalBurnMediaKind.CdAudio, OpticalBurnMediaKind.CdData })
        {
            OpticalBurnRequest request = Request(kind);
            using GeneratedContentComStream content = Content(request);
            FakeSession session = new() { FailWrite = true };
            using FakeImage image = new();
            List<string> messages = [];
            Exception? observed = null;
            bool observedBeforeRelease = false;
            string? observedContent = null;
            string? observedContext = null;
            string? observedSense = null;
            session.OnRelease = () => content.Read(new byte[32], 32, IntPtr.Zero);
            Exception? caught = null;
            try
            {
                WindowsImapiBurner.BurnCdDao(session, image, content, request, CancellationToken.None,
                    reportFailure: exception =>
                    {
                        observed = exception;
                        observedBeforeRelease = !session.Calls.Contains("release");
                        observedContent = exception.Data["Burn.ContentAtFailure"] as string;
                        observedContext = exception.Data["Burn.CdDaoContext"] as string;
                        observedSense = exception.Data["Burn.SenseStatus"] as string;
                    },
                    reportDiagnostic: messages.Add);
            }
            catch (COMException exception) { caught = exception; }

            Require(ReferenceEquals(caught, session.Failure) && ReferenceEquals(observed, session.Failure)
                && caught?.HResult == unchecked((int)0xC0AA0301), "original device exception reaches both observer and caller");
            Require(observedBeforeRelease && !string.IsNullOrEmpty(observedContent)
                && !string.IsNullOrEmpty(observedContext) && !string.IsNullOrEmpty(observedSense),
                "failure callback sees complete diagnostics before any recovery");
            Require(Equals(session.Failure.Data["Burn.ContentAtFailure"], observedContent)
                && observedContent != content.GetDiagnosticSummary(),
                "cleanup reading the source does not replace the saved source snapshot");
            string context = observedContext ?? string.Empty;
            Require(context.Contains("WriteStarted=True") && context.Contains("WriteReturned=False")
                && context.Contains("OperationElapsedMs=") && context.Contains("ElapsedMs="),
                "failure distinguishes a started write from a successfully returned write and records elapsed time");
            Require(context.Contains("TrackSectors=32") && context.Contains("StartOfLeadout=32")
                && context.Contains("LastPossibleStartOfLeadout=100") && context.Contains("LeadoutMarginSectors=68"),
                "capacity evidence is retained at failure");
            Require(context.Contains("Requested=750 sectors/s") && context.Contains("Actual=780 sectors/s")
                && context.Contains("Raw P-W") && context.Contains("mock prepared media"),
                "requested/actual speed, subchannel and cached media information remain available");
            string sense = observedSense ?? string.Empty;
            Require(sense.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                && !sense.Contains("5/64/00", StringComparison.Ordinal),
                "sense is explicitly unavailable, not inferred from a shared HRESULT");
            Require(session.Calls.Count(call => call == "release") == 1 && session.DiagnosticReads == 1
                && session.Calls.IndexOf("diagnostic") < session.Calls.IndexOf("write"),
                "media is inspected before writing and never queried again during failure or cleanup");
            Require(image.SectorType == 3 && session.SectorType == 3,
                "diagnostics retain the same raw P-W type for image and recorder");
        }
    }

    private static void TestSuccessfulBurn()
    {
        OpticalBurnRequest request = Request(OpticalBurnMediaKind.CdAudio);
        using GeneratedContentComStream content = Content(request);
        FakeSession session = new();
        using FakeImage image = new();
        List<string> messages = [];
        int failures = 0;
        OpticalWriteSpeed actual = WindowsImapiBurner.BurnCdDao(session, image, content, request,
            CancellationToken.None, reportFailure: _ => failures++, reportDiagnostic: messages.Add);
        Require(actual.SectorsPerSecond == 780 && failures == 0 && session.Failure.Data.Count == 0,
            "a successful burn does not invent failure context");
        Require(session.Calls.Count(call => call == "write") == 1 && session.Calls.Count(call => call == "release") == 1,
            "successful diagnostic collection preserves one write and one release");
        Require(messages.Any(message => message.Contains("LeadoutMarginSectors=68"))
            && messages.Any(message => message.Contains("Requested=750 sectors/s") && message.Contains("Actual=780 sectors/s"))
            && messages.Any(message => message.Contains("ElapsedMs="))
            && !messages.Any(message => message.Contains("CD DAO 失败：") || message.Contains("CD DAO Sense：")),
            "success logs useful capacity, speeds and timing without false error or sense messages");
    }

    private static void TestUnavailableDiagnostics()
    {
        foreach (bool failWrite in new[] { false, true })
        {
            OpticalBurnRequest request = Request(OpticalBurnMediaKind.CdAudio);
            using GeneratedContentComStream content = Content(request);
            FakeSession session = new() { FailWrite = failWrite, FailDiagnosticRead = true };
            using FakeImage image = new();
            Exception? caught = null;
            int reports = 0;
            try
            {
                WindowsImapiBurner.BurnCdDao(session, image, content, request, CancellationToken.None,
                    reportDiagnostic: _ => { reports++; throw new InvalidOperationException("Unavailable log sink."); });
            }
            catch (COMException exception) { caught = exception; }
            Require(failWrite ? ReferenceEquals(caught, session.Failure) : caught is null,
                "broken telemetry and logging cannot change a successful write or replace the original write error");
            Require(reports > 0 && session.DiagnosticReads == 1 && session.Calls.Count(call => call == "write") == 1
                && session.Calls.Count(call => call == "release") == 1,
                "throwing optional diagnostics do not suppress writing, cause retries or prevent release");
        }
    }

    private static void TestPartialDeviceProperties()
    {
        Require(BurnDeviceDiagnostics.DescribeMediaType(1).Contains("unspecified"),
            "IMAPI's generic CD-ROM value does not incorrectly identify recordable media as pressed discs");
        string recorder = BurnDeviceDiagnostics.DescribeRecorder(new PartialRecorder());
        Require(recorder.Contains("Vendor=ASUS") && recorder.Contains("Product=unavailable (0x80004005)")
            && recorder.Contains("Firmware=unavailable"),
            "missing or throwing recorder fields leave independent properties usable");
        string completeRecorder = BurnDeviceDiagnostics.DescribeRecorder(new CompleteRecorder());
        Require(completeRecorder.Contains("Firmware=1.02"), "reported firmware revision is included");
        string media = BurnDeviceDiagnostics.DescribePreparedMedia(new PartialMedia());
        Require(media.Contains("CD-RW") && media.Contains("3")
            && media.Contains("RequestedSectorType=unavailable (0x80004001)")
            && media.Contains("BufferUnderrunFreeDisabled=False"),
            "physical disc type and buffer setting survive an unavailable requested-sector property");
        string cdr = BurnDeviceDiagnostics.DescribePreparedMedia(new RecordableMedia());
        Require(cdr.Contains("CD-R") && cdr.Contains("2") && !cdr.Contains("CD-RW"),
            "recordable and rewritable media remain distinguishable");
    }

    private static OpticalBurnRequest Request(OpticalBurnMediaKind kind)
    {
        int sectorBytes = kind == OpticalBurnMediaKind.CdAudio ? 2352 : 2048;
        return new("fake-recorder", kind, 32 * sectorBytes,
            (output, _) => output.Write(new byte[32 * sectorBytes]), new OpticalWriteSpeed(750, false));
    }

    private static GeneratedContentComStream Content(OpticalBurnRequest request)
    {
        GeneratedContentComStream content = new(request.ContentLength, request.ProduceContent, null, CancellationToken.None);
        content.WaitUntilPrebuffered(CancellationToken.None);
        return content;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD detailed diagnostics: " + message);
    }

    private sealed class FakeImage : ICdRawImageSession
    {
        internal int SectorType { get; private set; }
        public long StartOfLeadout => 32;
        public IStream CreateImage(IStream content, int sectorType) { SectorType = sectorType; return content; }
        public void Dispose() { }
    }

    private sealed class FakeSession : ICdRawSession
    {
        internal List<string> Calls { get; } = [];
        internal COMException Failure { get; } = new("Unrecoverable device write error.", unchecked((int)0xC0AA0301));
        internal bool FailWrite { get; init; }
        internal bool FailDiagnosticRead { get; init; }
        internal Action? OnRelease { get; set; }
        internal int DiagnosticReads { get; private set; }
        internal int SectorType { get; private set; }
        public bool MediaPhysicallyBlank => true;
        public long LastPossibleStartOfLeadout => 100;
        public IReadOnlyList<int> SupportedSectorTypes => [1, 2, 3];
        public int CurrentWriteSpeed => 780;
        public bool CurrentRotationTypeIsPureCav => false;
        public void PrepareMedia() => Calls.Add("prepare");
        public void SetRequestedSectorType(int sectorType) => SectorType = sectorType;
        public void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav) { }
        public void WriteMedia(IStream content)
        {
            Calls.Add("write");
            if (FailWrite) throw Failure;
        }
        public void CancelWrite() => Calls.Add("cancel");
        public void ReleaseMedia() { Calls.Add("release"); OnRelease?.Invoke(); }
        public string? GetDiagnosticSummary()
        {
            Calls.Add("diagnostic");
            DiagnosticReads++;
            if (FailDiagnosticRead) throw new COMException("Diagnostic property unavailable.", unchecked((int)0x80004001));
            return "mock prepared media";
        }
    }

    // Public types allow the production assembly's dynamic COM-style access to resolve them.
    public sealed class PartialRecorder
    {
        public string VendorId => " ASUS ";
        public string ProductId => throw new COMException("Product unavailable.", unchecked((int)0x80004005));
    }

    public sealed class CompleteRecorder
    {
        public string VendorId => "ASUS";
        public string ProductId => "SDRW";
        public string ProductRevision => "1.02";
    }

    public sealed class PartialMedia
    {
        public int CurrentPhysicalMediaType => 3;
        public int RequestedSectorType => throw new COMException("Sector type unavailable.", unchecked((int)0x80004001));
        public bool BufferUnderrunFreeDisabled => false;
    }

    public sealed class RecordableMedia
    {
        public int CurrentPhysicalMediaType => 2;
        public int RequestedSectorType => 3;
        public bool BufferUnderrunFreeDisabled => false;
    }
}
