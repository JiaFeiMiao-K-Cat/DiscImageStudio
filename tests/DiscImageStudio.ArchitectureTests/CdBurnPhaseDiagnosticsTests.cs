using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

internal static class CdBurnPhaseDiagnosticsTests
{
    internal static void Run()
    {
        foreach (OpticalBurnMediaKind kind in new[] { OpticalBurnMediaKind.CdAudio, OpticalBurnMediaKind.CdData })
        {
            int sectorBytes = kind == OpticalBurnMediaKind.CdAudio ? 2352 : 2048;
            OpticalBurnRequest request = new("fake", kind, 32 * sectorBytes,
                (output, _) => output.Write(new byte[32 * sectorBytes]));
            foreach (string? failure in new string?[] { null, "write", "release" })
            {
                using GeneratedContentComStream content = new(request.ContentLength, request.ProduceContent, null, CancellationToken.None);
                content.WaitUntilPrebuffered(CancellationToken.None);
                FakeSession session = new(failure);
                using FakeImage image = new();
                List<string> operations = [];
                string? callAtSnapshot = null;
                try
                {
                    WindowsImapiBurner.BurnCdDao(session, image, content, request, CancellationToken.None,
                        reportOperation: operations.Add,
                        reportFailure: exception =>
                        {
                            callAtSnapshot = session.Calls[^1];
                            OpticalBurnDiagnostics.CaptureFailure(exception, "later observer", "LastWrittenLba=3");
                        });
                    Require(failure is null, "an injected failure must propagate");
                    Require(operations.TakeLast(2).Select(WindowsImapiBurner.GetBurnOperationStage)
                        .SequenceEqual(new[] { "write-complete", "finalize" }),
                        "CD write return is separate from closing the DAO session");
                }
                catch (COMException exception)
                {
                    Require(ReferenceEquals(exception, session.Failure), "original HRESULT/exception is retained");
                    Require(callAtSnapshot == failure, "snapshot is captured at failure before recovery");
                    string expected = failure == "release" ? "关闭 CD 刻录会话"
                        : kind == OpticalBurnMediaKind.CdAudio ? "写入 CD DAO 音轨" : "写入 CD DAO 数据轨";
                    Require(Equals(exception.Data["Burn.Operation"], expected)
                        && Equals(exception.Data["Burn.ProgressAtFailure"], "LastWrittenLba=3"),
                        "write and ReleaseMedia failures retain different operations and their original sector snapshot");
                }
                Require(session.Calls[^1] == "release" && session.Calls.Count(x => x == "release") == 1,
                    "logging preserves exactly one DAO release attempt");
            }
        }
        Console.WriteLine("cd-burn-phase-diagnostics: passed (audio/data completion, close failure, original exception and pre-cleanup snapshot; no hardware)");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("CD phase diagnostics: " + message);
    }

    private sealed class FakeImage : ICdRawImageSession
    {
        public long StartOfLeadout => 32;
        public IStream CreateImage(IStream content, int sectorType) => content;
        public void Dispose() { }
    }

    private sealed class FakeSession(string? failure) : ICdRawSession
    {
        internal List<string> Calls { get; } = [];
        internal COMException Failure { get; } = new("Injected CD failure.", unchecked((int)0x80004005));
        public bool MediaPhysicallyBlank => true;
        public long LastPossibleStartOfLeadout => 100;
        public IReadOnlyList<int> SupportedSectorTypes => [2];
        public int CurrentWriteSpeed => 1200;
        public bool CurrentRotationTypeIsPureCav => false;
        public void PrepareMedia() => Call("prepare");
        public void SetRequestedSectorType(int sectorType) { }
        public void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav) { }
        public void WriteMedia(IStream content) => Call("write");
        public void CancelWrite() => Call("cancel");
        public void ReleaseMedia() => Call("release");
        private void Call(string name)
        {
            Calls.Add(name);
            if (name == failure) throw Failure;
        }
    }
}
