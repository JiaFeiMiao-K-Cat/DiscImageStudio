using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

/// <summary>Verifies that preview compositing writes to versioned temporary paths and retains
/// one retired composite so concurrent geometry edits (e.g., toggling OuterRingBleed) never
/// trigger File.Create vs File.OpenRead sharing violations in LivePreviewViewModel.</summary>
internal static class DesignCompositeConcurrencyTests
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
        Console.WriteLine("design-composite-concurrency: passed (versioned composite paths, grace retention, concurrent render/read, canceled failure guard)");
    }

    private static void RunOnSta()
    {
        string root = Path.Combine(Path.GetTempPath(), "design-composite-race-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(root, "data");
        string cacheDir = Path.Combine(root, "cache");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(dataDir, "payload.txt"), "payload");

        string previousCache = TempFiles.LivePreviewDirectory;
        Require(TempFiles.TryConfigureCacheDirectory(cacheDir) == cacheDir, "fixture selects private cache directory");

        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher, new NoRecorder());
        try
        {
            string sourcePath = Path.Combine(root, "artwork.png");
            WriteTestPng(sourcePath);

            string projectPath = Path.Combine(root, "test.discproj");
            ProjectDocument document = new(
                ProjectJsonStore.CurrentSchemaVersion,
                "composite-concurrency-test",
                ProjectDocument.DvdFamily,
                new ProjectGeometry(24.0, 58.0, 2_297_888, 0, 0, 0, 2_297_280),
                [new ProjectLayer("layer-1", "Artwork", sourcePath, Visible: true, 0, 0, 0, 0.85)],
                new ProjectOutputs(Path.Combine(root, "out.iso"), WriteCue: false, dataDir, "DISC_IMAGE", Interleave: false,
                    FilesAtOuterRing: true, OuterRingBleed: true),
                CalibrationSessionPath: null,
                ThumbnailPngBase64: null,
                CreatedUtc: DateTimeOffset.UnixEpoch,
                ModifiedUtc: DateTimeOffset.UnixEpoch,
                Tone: ToneProfile.Photo,
                BluRay: null);
            ProjectJsonStore.Save(projectPath, document);

            shell.OpenProjectForSnapshot(projectPath);
            shell.MarkWindowLoaded();
            shell.State.DvdPreviewSize = "256";
            shell.State.DvdSamplesPerSector = "1";
            StopTimers(shell);
            shell.ClearLog();

            // 1. Versioned composite outputs keep one retired revision alive across publishes.
            Wait(InvokeRenderPreviewCompositeAsync(shell.Design));
            string firstPath = shell.State.DvdImagePath;
            Require(Path.GetFileName(firstPath).StartsWith("design-composite-dvd-", StringComparison.Ordinal)
                && File.Exists(firstPath),
                $"first preview composite exists under a versioned name ({firstPath})");

            Wait(InvokeRenderPreviewCompositeAsync(shell.Design));
            string secondPath = shell.State.DvdImagePath;
            Require(!string.Equals(firstPath, secondPath, StringComparison.Ordinal)
                && File.Exists(secondPath) && File.Exists(firstPath),
                $"second publish advances composite path and retains prior revision ({firstPath} vs {secondPath})");

            Wait(InvokeRenderPreviewCompositeAsync(shell.Design));
            string thirdPath = shell.State.DvdImagePath;
            Require(!string.Equals(secondPath, thirdPath, StringComparison.Ordinal)
                && File.Exists(thirdPath) && File.Exists(secondPath) && !File.Exists(firstPath),
                $"third publish keeps second revision and retires the oldest file (1:{File.Exists(firstPath)}, 2:{File.Exists(secondPath)}, 3:{File.Exists(thirdPath)})");
            StopTimers(shell);

            // 2. Toggle DvdOuterRingBleed and run overlapping composite writes concurrently
            // with DVD calibration preview reads against the current composite.
            MethodInfo refreshDvdCalibration = typeof(LivePreviewViewModel).GetMethod(
                "RefreshDvdCalibrationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo livePreviewRevisionField = typeof(LivePreviewViewModel).GetField(
                "_livePreviewRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;

            for (int iteration = 0; iteration < 3; iteration++)
            {
                shell.State.DvdOuterRingBleed = iteration % 2 == 0;
                StopTimers(shell);
                int previewRevision = (int)livePreviewRevisionField.GetValue(shell.LivePreview)! + 1;
                livePreviewRevisionField.SetValue(shell.LivePreview, previewRevision);

                Task readTask = (Task)refreshDvdCalibration.Invoke(
                    shell.LivePreview, [previewRevision, CancellationToken.None])!;
                Task writeTask1 = InvokeRenderPreviewCompositeAsync(shell.Design);
                Task writeTask2 = InvokeRenderPreviewCompositeAsync(shell.Design);
                Wait(Task.WhenAll(readTask, writeTask1, writeTask2));

                Require(!shell.LogText.Contains("[ERROR]", StringComparison.Ordinal)
                    && !shell.LogText.Contains("IOException", StringComparison.Ordinal),
                    $"concurrent preview read + composite write iteration {iteration} stays free of IOException/errors: {shell.LogText}");
                Require(shell.Toasts.All(toast => toast.Kind != ToastKind.Error),
                    $"concurrent iteration {iteration} raises no error toast");
            }

            // Natural dual-450ms timer flow triggered by DvdOuterRingBleed completes without sharing error.
            string compositeBeforeTimer = shell.State.DvdImagePath;
            shell.State.DvdOuterRingBleed = !shell.State.DvdOuterRingBleed;
            WaitUntil(() => !string.Equals(shell.State.DvdImagePath, compositeBeforeTimer, StringComparison.Ordinal)
                && shell.LivePreview.HasResultImage
                && shell.LivePreview.StatusText.StartsWith("已实时更新", StringComparison.Ordinal),
                "natural dual 450ms timers update composite and live preview without sharing violation");
            Require(!shell.LogText.Contains("[ERROR]", StringComparison.Ordinal)
                && !shell.LogText.Contains("IOException", StringComparison.Ordinal),
                $"natural timer flow stays error-free: {shell.LogText}");

            // Final clean preview against the latest published composite settles cleanly.
            StopTimers(shell);
            int finalRevision = (int)livePreviewRevisionField.GetValue(shell.LivePreview)! + 1;
            livePreviewRevisionField.SetValue(shell.LivePreview, finalRevision);
            Wait((Task)refreshDvdCalibration.Invoke(shell.LivePreview, [finalRevision, CancellationToken.None])!);
            Require(shell.LivePreview.HasResultImage && !shell.LogText.Contains("[ERROR]", StringComparison.Ordinal),
                "final DVD calibration preview renders from versioned composite cleanly");

            // 3. Canceled live preview task encountering missing file does not log error or show failure.
            shell.ClearLog();
            shell.State.DvdImagePath = Path.Combine(root, "deleted-mid-cancel.png");
            StopTimers(shell);
            int canceledRevision = (int)livePreviewRevisionField.GetValue(shell.LivePreview)!;
            using CancellationTokenSource canceled = new();
            canceled.Cancel();
            Wait((Task)refreshDvdCalibration.Invoke(shell.LivePreview, [canceledRevision, canceled.Token])!);
            Require(!shell.LogText.Contains("[ERROR]", StringComparison.Ordinal),
                "canceled preview worker does not log stale error diagnostics");

            // 4. ClearLayers + OnWindowClosed clean up retained temporary composite files.
            string latestCompositeBeforeClear = (string?)typeof(DesignViewModel)
                .GetField("_lastPreviewCompositePath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(shell.Design) ?? string.Empty;
            string previousCompositeBeforeClear = (string?)typeof(DesignViewModel)
                .GetField("_previousPreviewCompositePath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(shell.Design) ?? string.Empty;

            shell.Design.ClearLayers();
            Require(shell.State.DvdImagePath.Length == 0, "ClearLayers clears State.DvdImagePath");
            shell.LivePreview.OnWindowClosed();
            shell.Design.OnWindowClosed();
            shell.Calibration.OnWindowClosed();
            Require((latestCompositeBeforeClear.Length == 0 || !File.Exists(latestCompositeBeforeClear))
                && (previousCompositeBeforeClear.Length == 0 || !File.Exists(previousCompositeBeforeClear)),
                "OnWindowClosed deletes retained preview composite files");
        }
        finally
        {
            shell.LivePreview.OnWindowClosed();
            shell.Design.OnWindowClosed();
            shell.Calibration.OnWindowClosed();
            foreach (ToastItem toast in shell.Toasts.ToArray()) toast.Close();
            TempFiles.TryConfigureCacheDirectory(previousCache);
            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void StopTimers(ShellViewModel shell)
    {
        (typeof(DesignViewModel).GetField("_compositeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(shell.Design) as DispatcherTimer)?.Stop();
        (typeof(DesignViewModel).GetField("_dataRegionTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(shell.Design) as DispatcherTimer)?.Stop();
        (typeof(LivePreviewViewModel).GetField("_livePreviewTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(shell.LivePreview) as DispatcherTimer)?.Stop();
    }

    private static Task InvokeRenderPreviewCompositeAsync(DesignViewModel design)
        => (Task)typeof(DesignViewModel).GetMethod(
            "RenderPreviewCompositeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(design, null)!;

    private static void WaitUntil(Func<bool> condition, string description)
    {
        DispatcherFrame frame = new();
        DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(25) };
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(15) };
        poll.Tick += (_, _) =>
        {
            if (condition())
            {
                frame.Continue = false;
            }
        };
        timeout.Tick += (_, _) => frame.Continue = false;
        poll.Start();
        timeout.Start();
        try { if (!condition()) Dispatcher.PushFrame(frame); }
        finally { poll.Stop(); timeout.Stop(); }
        Require(condition(), description);
        Drain();
    }

    private static void WriteTestPng(string path)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
            64, 64, 96, 96, PixelFormats.Gray8, null, new byte[64 * 64], 64)));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Wait(Task task)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(30) };
        timeout.Tick += (_, _) => frame.Continue = false;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        timeout.Start();
        try { if (!task.IsCompleted) Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Require(task.IsCompleted, "task completed within timeout");
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
        if (!condition) throw new InvalidOperationException("Design composite concurrency: " + message);
    }

    private sealed class NoRecorder : IOpticalDiscBurner
    {
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([]);
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No recorder access.");
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No recorder access.");
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request, IProgress<OpticalBurnProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No recorder access.");
    }
}
