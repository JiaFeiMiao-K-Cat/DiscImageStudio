using System.IO;
using System.Runtime.InteropServices;
using DiscImageStudio.Burning;

internal static class BluRaySessionCleanupTests
{
    internal static void Run()
    {
        TestSuccessfulCleanupIsNotRepeated();
        TestOnlyFailedUnlocksRetryAfterEngineRelease();
        TestMultipleFailuresReportAndContinue();
        TestFailedAcquisitionKeepsPendingCleanup();
        TestDisposalPreservesOriginalWriteFailureAndIsBounded();
        TestOuterComCleanupPreservesWriteFailureAndReleasesBothObjects();
        TestOuterComCleanupCannotReportFailedReleaseAsSuccess();
        TestOuterComCleanupPreservesUnchangedFailure();
        TestOptionalEjectFollowsFormatRelease();
        TestEjectSkipsPendingWarningsAndLateCancellation();
        TestEjectFailurePreservesBurnSuccess();
        Console.WriteLine("blu-ray-session-cleanup: passed (paired releases, retained failures, post-engine retry, outer COM releases, original exceptions, diagnostics, optional eject; no hardware)");
    }

    private static void TestSuccessfulCleanupIsNotRepeated()
    {
        Fixture fixture = new();
        fixture.Cleanup.Acquire();
        fixture.Cleanup.Release();
        Require(!fixture.Cleanup.HasPendingLocks, "successful unlocks clear their outstanding lock state");
        fixture.Cleanup.Dispose();
        fixture.Cleanup.Dispose();
        fixture.Cleanup.Release();
        Require(fixture.Operations.SequenceEqual(new[]
            { "acquire", "disable-mcn", "prevent", "allow", "enable-mcn", "exclusive", "engine" }),
            "successful reference-counted releases are not repeated during disposal or after disposal");
        Require(fixture.Warnings.Count == 0, "successful cleanup does not emit failure diagnostics");

        Fixture unused = new();
        unused.Cleanup.Dispose();
        Require(unused.Operations.SequenceEqual(new[] { "engine" }),
            "an unacquired session never releases locks belonging to another owner");
    }

    private static void TestOnlyFailedUnlocksRetryAfterEngineRelease()
    {
        foreach (string blocked in new[] { "allow", "enable-mcn", "exclusive" })
        {
            Fixture fixture = new();
            COMException failure = new("Lock still held by engine: " + blocked, unchecked((int)0x800700AA));
            fixture.Failure = (operation, _) => operation == blocked && !fixture.EngineReleased ? failure : null;
            fixture.Cleanup.Acquire();
            COMException actual = Throws<COMException>(fixture.Cleanup.Release);
            Require(ReferenceEquals(actual, failure) && fixture.Cleanup.HasPendingLocks,
                "a failed unlock preserves both its original exception and its outstanding state");
            fixture.Cleanup.Dispose();
            Require(fixture.Operations.SequenceEqual(new[]
                { "acquire", "disable-mcn", "prevent", "allow", "enable-mcn", "exclusive", "engine", blocked }),
                "disposal releases the engine before retrying only the failed unlock: " + blocked);
            Require(!fixture.Cleanup.HasPendingLocks && fixture.Count(blocked) == 2,
                "the retained lock can be cleared by the bounded post-engine retry");
            Require(fixture.Warnings.Count == 1 && fixture.Warnings[0].Contains("0x800700AA", StringComparison.Ordinal),
                "cleanup diagnostics retain the HRESULT even when the later retry succeeds");
        }
    }

    private static void TestMultipleFailuresReportAndContinue()
    {
        Fixture fixture = new();
        COMException first = new("Removal timeout", unchecked((int)0x800705B4));
        COMException second = new("Notification remains disabled", unchecked((int)0x800700AA));
        fixture.Failure = (operation, _) => !fixture.EngineReleased ? operation switch
        {
            "allow" => first,
            "enable-mcn" => second,
            _ => null,
        } : null;
        fixture.Cleanup.Acquire();
        Require(ReferenceEquals(Throws<COMException>(fixture.Cleanup.Release), first),
            "Release reports its first failure without replacing it with a subsequent cleanup failure");
        Require(fixture.Count("exclusive") == 1 && fixture.Warnings.Count == 2
            && fixture.Warnings.Any(message => message.Contains("0x800705B4", StringComparison.Ordinal))
            && fixture.Warnings.Any(message => message.Contains("0x800700AA", StringComparison.Ordinal)),
            "all unlock stages run and each cleanup failure is reported");
        fixture.Cleanup.Dispose();
        Require(fixture.Count("allow") == 2 && fixture.Count("enable-mcn") == 2
            && fixture.Count("exclusive") == 1 && !fixture.Cleanup.HasPendingLocks,
            "paired exclusive access is released once while only failed locks are retried");
    }

    private static void TestFailedAcquisitionKeepsPendingCleanup()
    {
        Fixture exclusiveRejected = new();
        COMException rejected = new("Recorder belongs to another application", unchecked((int)0x80070005));
        exclusiveRejected.Failure = (operation, _) => operation == "acquire" ? rejected : null;
        Require(ReferenceEquals(Throws<COMException>(exclusiveRejected.Cleanup.Acquire), rejected),
            "a rejected exclusive acquisition preserves the acquisition error");
        exclusiveRejected.Cleanup.Dispose();
        Require(exclusiveRejected.Operations.SequenceEqual(new[] { "acquire", "engine" }),
            "rejected exclusive acquisition never releases another application's locks");

        Fixture mcnRejected = new();
        mcnRejected.Failure = (operation, _) => operation == "disable-mcn" ? rejected : null;
        Throws<COMException>(mcnRejected.Cleanup.Acquire);
        mcnRejected.Cleanup.Dispose();
        Require(mcnRejected.Operations.SequenceEqual(new[] { "acquire", "disable-mcn", "exclusive", "engine" }),
            "partial acquisition only balances the successfully acquired IMAPI counter");

        Fixture uncertainRemoval = new();
        COMException preventFailure = new("PREVENT command timed out", unchecked((int)0x800705B4));
        uncertainRemoval.Failure = (operation, _) => operation == "prevent" ? preventFailure
            : operation == "allow" && !uncertainRemoval.EngineReleased ? rejected : null;
        Require(ReferenceEquals(Throws<COMException>(uncertainRemoval.Cleanup.Acquire), preventFailure),
            "failed acquisition cleanup cannot mask the original PREVENT transport error");
        Require(uncertainRemoval.Cleanup.HasPendingLocks,
            "an uncertain removal lock remains eligible after the acquisition's first cleanup failed");
        uncertainRemoval.Cleanup.Dispose();
        Require(uncertainRemoval.Operations.SequenceEqual(new[]
            { "acquire", "disable-mcn", "prevent", "allow", "enable-mcn", "exclusive", "engine", "allow" })
            && !uncertainRemoval.Cleanup.HasPendingLocks,
            "the acquisition failure still gets its one pending unlock retry after releasing the engine");
    }

    private static void TestDisposalPreservesOriginalWriteFailureAndIsBounded()
    {
        Fixture fixture = new() { ThrowOnWarning = true };
        COMException cleanupFailure = new("Device remains busy", unchecked((int)0x800700AA));
        fixture.Failure = (operation, _) => operation is "allow" or "enable-mcn" or "exclusive" or "engine"
            ? cleanupFailure : null;
        fixture.Cleanup.Acquire();
        COMException writeFailure = new("First WRITE timeout", unchecked((int)0x800705B4));
        COMException observed = Throws<COMException>(() =>
        {
            using (fixture.Cleanup) throw writeFailure;
        });
        Require(ReferenceEquals(observed, writeFailure) && observed.HResult == unchecked((int)0x800705B4),
            "unlock, engine-release and diagnostic-callback errors never replace the original WRITE HRESULT");
        Require(fixture.Operations.SequenceEqual(new[]
            { "acquire", "disable-mcn", "prevent", "allow", "enable-mcn", "exclusive", "engine", "allow", "enable-mcn", "exclusive" }),
            "implicit disposal tries each pending unlock once before and once after the engine-release attempt");
        Require(fixture.Cleanup.HasPendingLocks && fixture.Warnings.Count == 7
            && fixture.Warnings.Count(message => message.Contains("清理重试", StringComparison.Ordinal)) == 3,
            "permanent failures retain outstanding state and are reported even if diagnostic callbacks throw");
        int calls = fixture.Operations.Count;
        fixture.Cleanup.Dispose();
        fixture.Cleanup.Release();
        Require(fixture.Operations.Count == calls, "the final cleanup retry cannot turn into an unbounded retry loop");
    }

    private static void TestOuterComCleanupPreservesWriteFailureAndReleasesBothObjects()
    {
        BluRayWriteException writeFailure = new(32, 32, 200, TimeSpan.FromSeconds(200),
            new MmcCommandResult(unchecked((int)0x800705B4), [], []));
        COMException formatFailure = new("Format release failed", unchecked((int)0x800700AA));
        COMException recorderFailure = new("Recorder release failed", unchecked((int)0x80004005));
        List<string> operations = [];
        List<string> warnings = ["Earlier session unlock warning"];
        InvalidOperationException observed = Throws<InvalidOperationException>(() =>
            WindowsImapiBurner.CompleteBurnCleanup(writeFailure, warnings,
                () => { operations.Add("format"); throw formatFailure; },
                () => { operations.Add("recorder"); throw recorderFailure; }));
        Require(operations.SequenceEqual(new[] { "format", "recorder" }),
            "a failed format COM release cannot prevent releasing the recorder");
        Require(ReferenceEquals(observed.InnerException, writeFailure)
            && observed.InnerException?.HResult == unchecked((int)0x800705B4)
            && observed.Message.Contains("0x800705B4", StringComparison.Ordinal),
            "outer COM cleanup retains the original raw WRITE exception and HRESULT in its exception chain");
        Require(warnings.Count == 3
            && observed.Message.Contains("Earlier session unlock warning", StringComparison.Ordinal)
            && observed.Message.Contains("0x800700AA", StringComparison.Ordinal)
            && observed.Message.Contains("0x80004005", StringComparison.Ordinal),
            "the final error includes session diagnostics and each subsequent COM release failure");
    }

    private static void TestOuterComCleanupCannotReportFailedReleaseAsSuccess()
    {
        COMException failure = new("Format release failed", unchecked((int)0x800700AA));
        List<string> operations = [];
        InvalidOperationException observed = Throws<InvalidOperationException>(() =>
            WindowsImapiBurner.CompleteBurnCleanup(null, [],
                () => { operations.Add("format"); throw failure; },
                () => operations.Add("recorder"),
                ejectMedia: () => operations.Add("eject")));
        Require(operations.SequenceEqual(new[] { "format", "recorder" })
            && ReferenceEquals(observed.InnerException, failure)
            && observed.Message.Contains("设备未能正常释放", StringComparison.Ordinal),
            "a failed format release prevents eject while still releasing the recorder and reporting the failure");

        operations.Clear();
        WindowsImapiBurner.CompleteBurnCleanup(null, [],
            () => operations.Add("format"), () => operations.Add("recorder"));
        Require(operations.SequenceEqual(new[] { "format", "recorder" }),
            "ordinary completion releases both objects exactly once before returning success");
    }

    private static void TestOuterComCleanupPreservesUnchangedFailure()
    {
        foreach (Exception failure in new Exception[]
            { new IOException("Source ended early"), new OperationCanceledException("Cancelled before the next WRITE") })
        {
            int releases = 0;
            int ejections = 0;
            Exception observed = Throws<Exception>(() => WindowsImapiBurner.CompleteBurnCleanup(
                failure, [], () => releases++, () => releases++, ejectMedia: () => ejections++));
            Require(ReferenceEquals(observed, failure) && releases == 2 && ejections == 0,
                "outer cleanup releases both objects and preserves a write failure or cancellation without ejecting");
        }
    }

    private static void TestOptionalEjectFollowsFormatRelease()
    {
        List<string> operations = [];
        WindowsImapiBurner.CompleteBurnCleanup(null, [],
            () => operations.Add("format"), () => operations.Add("recorder"),
            allowRemoval: () => operations.Add("allow"));
        Require(operations.SequenceEqual(new[] { "allow", "format", "recorder" }),
            "with automatic eject off, successful cleanup just unlocks and releases the recorder");

        operations.Clear();
        WindowsImapiBurner.CompleteBurnCleanup(null, [],
            () => operations.Add("format"), () => operations.Add("recorder"),
            allowRemoval: () => operations.Add("allow"), ejectMedia: () => operations.Add("eject"));
        Require(operations.SequenceEqual(new[] { "allow", "format", "eject", "recorder" }),
            "with automatic eject on, the tray ejects once after unlock and format release while the recorder is still available");
    }

    private static void TestEjectSkipsPendingWarningsAndLateCancellation()
    {
        int ejections = 0;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.CompleteBurnCleanup(
            null, ["Earlier session unlock warning"], () => { }, () => { }, ejectMedia: () => ejections++));
        Require(ejections == 0, "earlier session cleanup warnings prevent automatic eject");

        using var cancellation = new CancellationTokenSource();
        int recorderReleases = 0;
        WindowsImapiBurner.CompleteBurnCleanup(null, [],
            cancellation.Cancel, () => recorderReleases++, ejectMedia: () => ejections++,
            cancellationToken: cancellation.Token);
        Require(ejections == 0 && recorderReleases == 1,
            "cancellation while releasing the format prevents eject and still releases the recorder");
    }

    private static void TestEjectFailurePreservesBurnSuccess()
    {
        List<string> operations = [];
        List<string> cleanupWarnings = [];
        List<string> diagnostics = [];
        WindowsImapiBurner.CompleteBurnCleanup(null, cleanupWarnings,
            () => operations.Add("format"), () => operations.Add("recorder"),
            ejectMedia: () =>
            {
                operations.Add("eject");
                throw new COMException("Tray would not open", unchecked((int)0x800700AA));
            },
            reportEjectDiagnostic: diagnostics.Add);
        Require(operations.SequenceEqual(new[] { "format", "eject", "recorder" }) && cleanupWarnings.Count == 0,
            "an eject failure still releases the recorder and preserves the successful burn result");
        Require(diagnostics.Any(message => message.Contains("0x800700AA", StringComparison.Ordinal)
            && message.Contains("Tray would not open", StringComparison.Ordinal)),
            "the eject diagnostic exposes the recorder's error and HRESULT");
    }

    private sealed class Fixture
    {
        internal Fixture()
        {
            Cleanup = new BluRaySessionCleanup(
                acquireExclusive: () => Invoke("acquire"),
                disableMcn: () => Invoke("disable-mcn"),
                preventRemoval: () => Invoke("prevent"),
                allowRemoval: () => Invoke("allow"),
                enableMcn: () => Invoke("enable-mcn"),
                releaseExclusive: () => Invoke("exclusive"),
                releaseWriteEngine: () => { Invoke("engine"); EngineReleased = true; },
                reportCleanupWarning: message =>
                {
                    Warnings.Add(message);
                    if (ThrowOnWarning) throw new InvalidOperationException("Diagnostic subscriber failed.");
                });
        }

        internal BluRaySessionCleanup Cleanup { get; }
        internal List<string> Operations { get; } = [];
        internal List<string> Warnings { get; } = [];
        internal Func<string, int, Exception?>? Failure { get; set; }
        internal bool EngineReleased { get; private set; }
        internal bool ThrowOnWarning { get; set; }
        internal int Count(string operation) => Operations.Count(value => value == operation);

        private void Invoke(string operation)
        {
            Operations.Add(operation);
            Exception? failure = Failure?.Invoke(operation, Count(operation));
            if (failure is not null) throw failure;
        }
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException("BD session cleanup: expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("BD session cleanup: " + message);
    }
}
