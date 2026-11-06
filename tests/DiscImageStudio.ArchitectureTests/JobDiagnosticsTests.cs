using System.IO;
using System.Runtime.InteropServices;
using DiscImageStudio.Services;

internal static class JobDiagnosticsTests
{
    internal static void Run()
    {
        TestExceptionTree();
        TestCleanupPreservesOutcomes();
        Console.WriteLine("job-diagnostics: passed (nested HRESULTs, stacks, data, cleanup warnings and original outcomes)");
    }

    private static void TestExceptionTree()
    {
        Exception failure = CaptureFailure();
        failure.Data["OutputPath"] = "result.iso";
        failure.Data["Unreadable"] = new BrokenValue();
        var aggregate = new AggregateException("two failures", failure, new IOException("second failure"));
        string text = JobDiagnostics.FormatLog("DVD preview", aggregate);
        Require(text.Contains("DVD preview [ERROR]", StringComparison.Ordinal)
            && text.Contains("System.AggregateException", StringComparison.Ordinal)
            && text.Contains("System.Runtime.InteropServices.COMException", StringComparison.Ordinal)
            && text.Contains("HRESULT=0x80004005", StringComparison.Ordinal)
            && text.Contains(nameof(CaptureFailure), StringComparison.Ordinal)
            && text.Contains("OutputPath: result.iso", StringComparison.Ordinal)
            && text.Contains("InnerExceptions[1]", StringComparison.Ordinal)
            && text.Contains("second failure", StringComparison.Ordinal)
            && text.Contains("ToString failed", StringComparison.Ordinal),
            "formatting retains each failure and survives an unreadable diagnostic value");
    }

    private static Exception CaptureFailure()
    {
        try { throw new COMException("native details", unchecked((int)0x80004005)); }
        catch (COMException exception)
        {
            try { throw new InvalidOperationException("operation failed", exception); }
            catch (InvalidOperationException outer) { return outer; }
        }
    }

    private static void TestCleanupPreservesOutcomes()
    {
        List<string> log = [];
        var cleanup = new IOException("staging directory locked");
        bool published = false;
        try { published = true; }
        finally { JobDiagnostics.TryCleanup(() => throw cleanup, log.Add, "staging cleanup"); }
        Require(published && log.Single().Contains("[WARNING]", StringComparison.Ordinal)
            && log[0].Contains("staging directory locked", StringComparison.Ordinal),
            "cleanup failure is visible without converting a published success into failure");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        foreach (Exception original in new Exception[]
                 { new IOException("primary generation error"), new OperationCanceledException(cancellation.Token) })
        {
            Exception? observed = null;
            try
            {
                try { throw original; }
                finally
                {
                    JobDiagnostics.TryCleanup(() => throw cleanup, log.Add, "staging cleanup");
                    JobDiagnostics.TryCleanup(() => throw cleanup,
                        _ => throw new InvalidOperationException("log observer failed"), "staging cleanup");
                }
            }
            catch (Exception exception) { observed = exception; }
            Require(ReferenceEquals(observed, original), "cleanup preserves the original exception and cancellation identity");
        }
    }

    private sealed class BrokenValue
    {
        public override string ToString() => throw new InvalidOperationException("bad diagnostic value");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Job diagnostics: " + description);
    }
}
