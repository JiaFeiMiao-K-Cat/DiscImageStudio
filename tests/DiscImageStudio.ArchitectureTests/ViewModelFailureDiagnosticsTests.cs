using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class ViewModelFailureDiagnosticsTests
{
    internal static void Run()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { TestCapacityFailure(); TestPreviewCacheFailure(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("view-model-failure-diagnostics: passed (failed media read, cancellation, preview cache failure and optional thumbnail; no device access)");
    }

    private static void TestCapacityFailure()
    {
        Reader reader = new();
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher, reader);
        try
        {
            shell.NavigateTo(Workspace.Calibration);
            var model = shell.Calibration;
            model.SelectedDiscType = "DVD";
            model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Require(shell.JobHasResult && model.Parameters.DvdTotalSectors == "2297888", "successful read establishes the baseline");
            var native = new COMException("native capacity details", unchecked((int)0x80004005));
            native.Data["Recorder"] = "fake-recorder";
            reader.Read = () => Task.FromException<long>(new IOException("capacity failed", native));
            model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Require(!shell.IsBusy && !shell.JobHasResult && shell.IsJobDockExpanded,
                "a failed capacity read aborts the dock instead of reporting success");
            Require(model.Parameters.DvdTotalSectors == "2297888" && model.CapacityStatus.Contains("capacity failed", StringComparison.Ordinal),
                "failure retains editable capacity and concise status");
            Require(shell.LogText.Contains("System.IO.IOException", StringComparison.Ordinal)
                && shell.LogText.Contains("native capacity details", StringComparison.Ordinal)
                && shell.LogText.Contains("HRESULT=0x80004005", StringComparison.Ordinal)
                && shell.LogText.Contains("Recorder: fake-recorder", StringComparison.Ordinal),
                "the task log retains native and managed exception details");
            Require(shell.Toasts.Last().Message == "capacity failed", "the toast remains a concise message");

            int toastCount = shell.Toasts.Count;
            shell.ClearLog();
            reader.Read = () => Task.FromCanceled<long>(new CancellationToken(canceled: true));
            model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Require(!shell.JobHasResult && !shell.IsBusy && model.CapacityStatus.Length == 0
                && shell.Toasts.Count == toastCount && !shell.LogText.Contains("[ERROR]", StringComparison.Ordinal),
                "cancellation remains cancellation and does not show an error toast");
        }
        finally
        {
            shell.Calibration.OnWindowClosed();
            foreach (ToastItem toast in shell.Toasts.ToArray()) toast.Close();
        }
    }

    private static void TestPreviewCacheFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "job-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string previousCache = TempFiles.LivePreviewDirectory;
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher, new Reader());
        try
        {
            string source = Path.Combine(root, "tiny.png");
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96,
                PixelFormats.Gray8, null, new byte[] { 0, 255, 255, 0 }, 2)));
            using (FileStream stream = File.Create(source)) encoder.Save(stream);
            DesignLayerViewModel layer = new() { ImagePath = source };
            Require(layer.LoadImage(58), "fixture loads a real image layer");
            shell.Design.Layers.Add(layer);

            // Simulate a cache folder disappearing between configuration and rendering.
            // Only this fixture's empty directory is replaced with a blocking file.
            string cache = Path.GetFullPath(Path.Combine(root, "cache"));
            Require(TempFiles.TryConfigureCacheDirectory(cache) == cache, "fixture selects its private cache");
            Require(Path.GetDirectoryName(cache) == Path.GetFullPath(root), "cache replacement stays within the fixture");
            Directory.Delete(cache);
            File.WriteAllText(cache, "not a directory");

            ToneViewModel tone = shell.Design.Tone;
            typeof(ToneViewModel).GetField("_compositePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tone, source);
            InvokeTask(tone, "RenderTonePreviewAsync", 0, CancellationToken.None);
            Require(!tone.HasTonePreview && shell.LogText.Contains("影调预览", StringComparison.Ordinal)
                && shell.LogText.Contains("System.IO.", StringComparison.Ordinal),
                "tone cache setup failure is caught and logged before rendering begins");

            shell.ClearLog();
            InvokeTask(shell.Design, "RenderPreviewCompositeAsync");
            Require(shell.LogText.Contains("System.IO.", StringComparison.Ordinal)
                && shell.Toasts.Last().Kind == ToastKind.Error,
                "design cache setup failure reaches the visible error path");

            shell.ClearLog();
            string? thumbnail = shell.Design.RenderThumbnailAsync().GetAwaiter().GetResult();
            Require(thumbnail is null && shell.LogText.Contains("[WARNING]", StringComparison.Ordinal)
                && shell.LogText.Contains("System.IO.", StringComparison.Ordinal),
                "optional thumbnail I/O failure degrades to null with a diagnostic warning");
        }
        finally
        {
            TempFiles.TryConfigureCacheDirectory(previousCache);
            shell.Design.ClearLayers();
            if (typeof(DesignViewModel).GetField("_compositeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(shell.Design) is DispatcherTimer timer) timer.Stop();
            shell.Calibration.OnWindowClosed();
            foreach (ToastItem toast in shell.Toasts.ToArray()) toast.Close();
            string resolved = Path.GetFullPath(root);
            string parent = Path.GetFullPath(Path.GetTempPath());
            Require(resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("job-diagnostics-test-", StringComparison.Ordinal), "cleanup stays within fixture scope");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void InvokeTask(object instance, string method, params object[] arguments)
        => ((Task)instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, arguments)!).GetAwaiter().GetResult();

    private sealed class Reader : IOpticalDiscBurner
    {
        internal Func<Task<long>> Read = () => Task.FromResult(2297888L);
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([new("fake-recorder", "Fake recorder", [])]);
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default) => Read();
        public async Task<OpticalMediaCapacity> GetMediaCapacityAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => new(await Read(), 2297280);
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Test must not query burn speeds.");
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request, IProgress<OpticalBurnProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Test must not burn a disc.");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("View model diagnostics: " + description);
    }
}
