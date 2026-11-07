using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

internal static class ImageJobWorkflowTests
{
    internal static void Run()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { RunOnSta(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("image-job-workflow: passed (typed results, original errors, partial files, cancellation and late progress; no hardware)");
    }

    private static void RunOnSta()
    {
        string directory = Path.Combine(Path.GetTempPath(), "image-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher, new NoRecorder());
        try
        {
            try
            {
                Wait(shell.RunImageJobAsync("invalid path", "invalid\0.iso", (_, _) =>
                    throw new InvalidOperationException("invalid path must not invoke the engine")));
                throw new InvalidOperationException("invalid output path was accepted");
            }
            catch (ArgumentException) { }
            Require(!shell.IsBusy && !shell.CancelJobCommand.CanExecute(null),
                "invalid output path cannot leave the shell busy");
            string output = Path.Combine(directory, "image with spaces.iso");
            IProgress<DiscJobProgress>? completedProgress = null;
            Task<bool> success = shell.RunImageJobAsync("typed image", output, (progress, token) =>
            {
                completedProgress = progress;
                token.ThrowIfCancellationRequested();
                File.WriteAllBytes(output, [1, 2, 3]);
                progress.Report(new("fixture", "generate", "image generated", 1));
                return new(output, "typed result summary");
            });
            Wait(success);
            Require(success.Result && shell.JobHasResult && shell.HasJobOutput && !shell.IsBusy,
                "successful internal operation publishes its artifact");
            Require(shell.LogText.Contains("typed result summary", StringComparison.Ordinal), "result summary is logged");
            string completedStatus = shell.JobStatusText;
            completedProgress!.Report(new("fixture", "generate", "stale progress", 0));
            Drain();
            Require(shell.JobStatusText == completedStatus && shell.JobProgressValue == 100,
                "queued progress cannot overwrite a completed operation");

            string partial = Path.Combine(directory, "partial.iso");
            Task<bool> failed = shell.RunImageJobAsync("failing image", partial, (_, _) =>
            {
                File.WriteAllBytes(partial, [9]);
                ThrowGenerationFailure();
                throw new InvalidOperationException("unreachable");
            });
            Wait(failed);
            Require(!failed.Result && !shell.HasJobOutput && !shell.JobHasResult && !shell.IsBusy,
                "failure does not advertise a partial or previous artifact");
            Require(File.Exists(partial) && shell.LogText.Contains("ThrowGenerationFailure", StringComparison.Ordinal)
                && shell.LogText.Contains("nested-image-failure", StringComparison.Ordinal)
                && shell.LogText.Contains("request-marker", StringComparison.Ordinal)
                && shell.LogText.Contains("HRESULT=", StringComparison.Ordinal),
                "log preserves exception stack, inner cause and diagnostic context");
            Require(!shell.LogText.Contains("未生成", StringComparison.Ordinal),
                "a failure does not falsely claim that the partial file does not exist");

            Task<bool> canceled = shell.RunImageJobAsync("cancel image", output, (_, token) =>
            {
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("cancellation did not reach the internal operation");
                token.ThrowIfCancellationRequested();
                return new(output);
            });
            Require(shell.CancelJobCommand.CanExecute(null), "typed image job exposes cancellation");
            bool ranWhileBusy = false;
            Task<bool> refused = shell.RunImageJobAsync("concurrent image", output, (_, _) =>
            {
                ranWhileBusy = true;
                return new(output);
            });
            Require(refused.IsCompletedSuccessfully && !refused.Result && !ranWhileBusy,
                "busy gating prevents a second generation");
            shell.CancelJobCommand.Execute(null);
            Wait(canceled);
            Require(!canceled.Result && !shell.IsBusy && !shell.JobHasResult,
                "cancellation is reported separately from failure and success");

            string missing = Path.Combine(directory, "missing.iso");
            Task<bool> noArtifact = shell.RunImageJobAsync("missing image", missing, (_, _) => new(missing));
            Wait(noArtifact);
            Require(!noArtifact.Result && !shell.HasJobOutput
                && shell.LogText.Contains(nameof(FileNotFoundException), StringComparison.Ordinal),
                "an engine result without its artifact cannot be shown as successful");

            string invalidImage = Path.Combine(directory, "invalid.png");
            File.WriteAllText(invalidImage, "not an image");
            shell.State.DvdImagePath = invalidImage;
            shell.State.DvdTotalSectors = "32";
            shell.State.DvdUsableSectors = "32";
            shell.State.DvdInnerRadius = shell.State.DvdActualInnerRadius = "24";
            shell.State.DvdOuterRadius = shell.State.DvdActualOuterRadius = "58";
            shell.State.DvdPitchLinear = shell.State.DvdPitchQuadratic = shell.State.DvdPitchCubic = "0";
            shell.State.DvdActualPitchLinear = shell.State.DvdActualPitchQuadratic = shell.State.DvdActualPitchCubic = "0";
            shell.State.DvdDataDirectory = string.Empty;
            shell.State.DvdPreviewSize = "256";
            shell.State.DvdSamplesPerSector = "1";
            Task preview = (Task)typeof(LivePreviewViewModel).GetMethod("RefreshDvdCalibrationAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell.LivePreview, [0, CancellationToken.None])!;
            Wait(preview);
            Require(shell.LogText.Contains("CalibrationPreviewRenderer.Render", StringComparison.Ordinal)
                && !shell.LogText.Contains("failed with exit code", StringComparison.Ordinal),
                "DVD GUI preview logs the renderer exception rather than a CLI exit code");
        }
        finally
        {
            shell.LivePreview.OnWindowClosed();
            SynchronizationContext.SetSynchronizationContext(previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ThrowGenerationFailure()
    {
        IOException error = new("image-write-failed", new InvalidDataException("nested-image-failure"));
        error.Data["operation"] = "request-marker";
        throw error;
    }

    private static void Wait(Task task)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(20) };
        timeout.Tick += (_, _) => frame.Continue = false;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        timeout.Start();
        try { if (!task.IsCompleted) Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Require(task.IsCompleted, "operation completed within timeout");
        task.GetAwaiter().GetResult();
        Drain();
    }

    private static void Drain()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Image job workflow: " + message);
    }

    private sealed class NoRecorder : IOpticalDiscBurner
    {
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No recorder access is allowed.");
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No recorder access is allowed.");
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No recorder access is allowed.");
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No recorder access is allowed.");
    }
}
