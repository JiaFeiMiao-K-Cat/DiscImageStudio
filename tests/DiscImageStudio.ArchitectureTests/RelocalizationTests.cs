using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

/// <summary>Language switches re-read catalog text only. The running project keeps its
/// geometry, a completed ISO read-back keeps its token and file legend, and preview chrome
/// that was constructed before the switch refreshes its text when it loads.</summary>
internal static class RelocalizationTests
{
    internal static void Run()
    {
        TestPresetGeometryStability();
        TestPreviewStateStability();
        TestSearchRangeSelection();
        TestCalibrationSelectionStability();
        TestToneStatusStability();
        TestBurnStatusStability();
        TestControlLoadedRefresh();
        Console.WriteLine("relocalization: passed (geometry preserved, completed and running read-backs untouched, " +
            "failed previews cleared and reworded, search ranges listed, control text refreshed on load)");
    }

    private static void TestPresetGeometryStability()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-relocalize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string presetPath = Path.Combine(directory, DiscPresetJsonStore.FileName);
            DiscPresetJsonDocument initial = DiscPresetJsonStore.CreateInitialUserDocument();
            DiscPresetJsonStore.Save(presetPath, initial);

            DiscParametersState state = new(presetPath);
            state.LoadPresets();
            CdDiscPreset custom = state.CdPresets.Single(preset => preset.Id == "user-custom-cd");
            state.ApplyCdDiscPreset(custom);
            Require(state.CdInnerRadius == "24.5" && state.CdOuterRadius == "56.8",
                "the selected user preset supplies the running geometry");

            // The user edits the preset file while the project is open: same id, new geometry
            // and new text. A language switch must only pick up the text.
            DiscPresetJsonStore.Save(presetPath, initial with
            {
                CdPresets = initial.CdPresets.Select(preset => preset.Id == "user-custom-cd"
                    ? preset with
                    {
                        DisplayName = "外部改名的 CD 参数",
                        Description = "外部改写的说明。",
                        InnerRadiusMm = 25.25,
                        OuterRadiusMm = 57.1,
                    }
                    : preset).ToArray(),
            });

            List<string> notifications = [];
            state.PropertyChanged += (_, change) => notifications.Add(change.PropertyName ?? string.Empty);
            state.Relocalize();

            Require(state.CdInnerRadius == "24.5" && state.CdOuterRadius == "56.8",
                "language switch keeps the running project geometry despite the edited preset file");
            Require(state.SelectedCdPreset.Id == "user-custom-cd" && state.CdPresetHint == "外部改写的说明。"
                && state.SelectedCdPreset.DisplayName == "外部改名的 CD 参数",
                "language switch still refreshes the selected preset text");
            Require(!notifications.Contains(nameof(DiscParametersState.CdInnerRadius))
                && !notifications.Contains(nameof(DiscParametersState.CdOuterRadius))
                && !notifications.Contains(nameof(DiscParametersState.CdSectors)),
                "language switch raises no geometry notifications");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestPreviewStateStability()
    {
        RunOnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "disc-relocalize-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            string originalLanguage = L.CurrentLanguage;
            ShellViewModel? shell = null;
            try
            {
                string source = Path.Combine(directory, "source.png");
                WritePng(source, 128, Colors.Black);
                string files = Path.Combine(directory, "files");
                Directory.CreateDirectory(files);
                File.WriteAllBytes(Path.Combine(files, "payload.bin"), new byte[48 * 2048]);
                CdDiscParameters disc = new(24.5, 56.8, 256);
                CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(files, disc);
                string iso = Path.Combine(directory, "disc.iso");
                CdDataImageGenerator.Generate(source, iso, disc, plan);

                shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
                DiscParametersState state = shell.State;
                LivePreviewViewModel preview = shell.LivePreview;
                preview.SelectedFamily = OpticalDiscFamily.CompactDisc;
                preview.IsCalibrationPreview = false;
                state.CdImagePath = source;
                state.CdSectors = "256";
                state.CdInnerRadius = state.CdActualInnerRadius = "24.5";
                state.CdOuterRadius = state.CdActualOuterRadius = "56.8";
                state.CdPreviewSize = "256";
                state.CdSamplesPerSector = "2";
                state.CdDataDirectory = files;
                state.CdTrackPath = iso;
                shell.MarkWindowLoaded();
                DispatcherTimer timer = Field<DispatcherTimer>(preview, "_livePreviewTimer");
                timer.Stop();

                RunPreview(preview);
                Require(preview.HasResultImage && preview.HasFileRegionOverlay,
                    "the ISO read-back completes with its file legend: " + preview.StatusText);
                CancellationTokenSource token = Field<CancellationTokenSource>(preview, "_livePreviewCancellation");
                BitmapSource image = preview.ResultImage!;
                int revision = Field<int>(preview, "_livePreviewRevision");
                Require(!token.IsCancellationRequested, "the completed read-back token is still live before the switch");

                L.SetLanguage(L.English);
                shell.Relocalize();

                Require(!token.IsCancellationRequested && !timer.IsEnabled
                    && Field<int>(preview, "_livePreviewRevision") == revision
                    && ReferenceEquals(preview.ResultImage, image) && preview.HasFileRegionOverlay,
                    "language switch leaves the completed read-back, its token and file legend untouched");
                Require(preview.ResultPreviewTitle == "Preview - CD"
                    && preview.StatusText == "Updated · Readback simulation · CD data disc · 256 px · 2 samples per sector",
                    "language switch re-words the result title and completion line: " + preview.StatusText);

                // The same switch while a fresh ISO read is still running must leave that read
                // alone; scheduling would cancel its token and hide the previous legend.
                Task inFlight = StartPreview(preview);
                CancellationTokenSource inFlightToken = Field<CancellationTokenSource>(preview, "_livePreviewCancellation");
                int inFlightRevision = Field<int>(preview, "_livePreviewRevision");
                Require(!inFlight.IsCompleted, "the ISO read is still running when the language switches");
                shell.Relocalize();
                Require(!inFlightToken.IsCancellationRequested && !timer.IsEnabled
                    && Field<int>(preview, "_livePreviewRevision") == inFlightRevision,
                    "language switch during a running ISO read does not cancel or reschedule it");
                Wait(inFlight);
                Require(preview.HasResultImage && preview.HasFileRegionOverlay,
                    "the untouched read-back publishes its file legend after the switch");

                // With nothing on screen yet, the empty-state text must not claim the preview
                // is waiting while the first read-back is still running.
                Invoke(preview, "UpdateMode", [true]);
                Require(!preview.HasResultImage, "the result is cleared before the empty-state check");
                Task firstRead = StartPreview(preview);
                L.SetLanguage(L.Chinese);
                shell.Relocalize();
                Require(preview.StatusText.StartsWith("Refreshing", StringComparison.Ordinal),
                    "language switch leaves the running refresh status alone: " + preview.StatusText);
                Wait(firstRead);
                Require(preview.HasResultImage
                    && preview.StatusText.StartsWith("已实时更新", StringComparison.Ordinal),
                    "the refresh that started without a result settles in the new language: " + preview.StatusText);

                // A refresh failure is state, not chrome: it replaces the result on screen
                // instead of leaving the previous picture under the error line, and a language
                // switch re-words the failure without bringing that picture back.
                using (FileStream locked = new(iso, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Wait(StartPreview(preview));
                }

                Require(!preview.HasResultImage && !preview.HasFileRegionOverlay
                    && preview.ResultPreviewPath.Length == 0
                    && !preview.DownloadPreviewCommand.CanExecute(null)
                    && preview.ResultPreviewPlaceholderText.StartsWith("实时预览失败：", StringComparison.Ordinal)
                    && preview.StatusText.StartsWith("实时预览失败：", StringComparison.Ordinal),
                    "the blocked ISO read clears the stale result and its export: " + preview.StatusText);
                L.SetLanguage(L.English);
                shell.Relocalize();
                Require(!preview.HasResultImage
                    && preview.StatusText.StartsWith("Live preview failed:", StringComparison.Ordinal),
                    "language switch re-words the failure without restoring the result: " + preview.StatusText);
                L.SetLanguage(L.Chinese);
                shell.Relocalize();
                Require(!preview.HasResultImage
                    && preview.StatusText.StartsWith("实时预览失败：", StringComparison.Ordinal),
                    "language switch keeps a resultless failure instead of the idle hint: " + preview.StatusText);

                state.CdInnerRadius = "24.5001";
                Require(Field<CancellationTokenSource>(preview, "_livePreviewCancellation").IsCancellationRequested
                    && timer.IsEnabled,
                    "a real geometry edit still cancels and schedules the preview");
                timer.Stop();
            }
            finally
            {
                L.Initialize(originalLanguage);
                shell?.LivePreview.OnWindowClosed();
                SynchronizationContext.SetSynchronizationContext(previous);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    private static void TestSearchRangeSelection()
    {
        RunOnSta(() =>
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            string originalLanguage = L.CurrentLanguage;
            try
            {
                L.Initialize(L.Chinese);
                ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
                CalibrationViewModel calibration = shell.Calibration;
                CalibrationParameters parameters = new(CalibrationDiscKind.Cd, 24.5, 56.8, 6400,
                    CdDiscParameters.StandardLinearVelocityMmPerSecond);
                CalibrationTarget target = CalibrationTarget.Create(parameters);

                // A session saved a manual box outside the built-in list. The choice has to be
                // listed and selected, otherwise the ComboBox starts out with no selection.
                Invoke(calibration, "InstallTarget",
                    [target, new CalibrationFitOptions(RadiusRangeMm: 0.3, AutoRange: false), true]);
                Require(calibration.SelectedCoarseSearchRange.RadiusRangeMm == 0.3
                    && !calibration.SelectedCoarseSearchRange.Auto
                    && calibration.CoarseSearchRangeChoices.Contains(calibration.SelectedCoarseSearchRange),
                    "a saved custom search range is listed and selected");
                Require(calibration.CoarseSearchRangeChoices.Count == 5,
                    "the custom range adds exactly one choice");

                Invoke(calibration, "InstallTarget",
                    [target, new CalibrationFitOptions(RadiusRangeMm: 0.4, AutoRange: false), true]);
                Require(calibration.CoarseSearchRangeChoices.Count == 5
                    && calibration.SelectedCoarseSearchRange.RadiusRangeMm == 0.4
                    && calibration.CoarseSearchRangeChoices.Contains(calibration.SelectedCoarseSearchRange),
                    "a later session replaces the previous custom range instead of accumulating it");

                Invoke(calibration, "InstallTarget",
                    [target, new CalibrationFitOptions(AutoRange: true), true]);
                Require(ReferenceEquals(calibration.SelectedCoarseSearchRange, calibration.CoarseSearchRangeChoices[0])
                    && calibration.CoarseSearchRangeChoices.Count == 4,
                    "an auto session drops the stale custom range and selects the listed default");
            }
            finally
            {
                L.Initialize(originalLanguage);
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
    }

    private static void TestCalibrationSelectionStability()
    {
        RunOnSta(() =>
        {
            string originalLanguage = L.CurrentLanguage;
            ShellViewModel? shell = null;
            try
            {
                L.Initialize(L.Chinese);
                shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
                var calibration = shell.Calibration;
                var target = CalibrationTarget.Create(
                    new(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200),
                    artwork: CalibrationArtwork.AngleSweepBands);
                Invoke(calibration, "InstallTarget", [target, null, true]);
                calibration.SelectedRingIndex = 3;
                calibration.SelectedArcBand = 2;
                var edge = calibration.RingChoices.Single(choice => choice.Index == 3);
                var band = calibration.ArcBandNames[2];
                var direction = calibration.ArcDirections[1];
                string edgeLabel = edge.Label, bandLabel = band.Label, directionLabel = direction.Label;
                string preset = calibration.Parameters.SelectedCdPreset.Id;
                string radius = calibration.Parameters.CdOuterRadius;
                int refreshed = 0;
                edge.PropertyChanged += (_, args) => { if (args.PropertyName == "Label") refreshed++; };
                band.PropertyChanged += (_, args) => { if (args.PropertyName == "Label") refreshed++; };
                direction.PropertyChanged += (_, args) => { if (args.PropertyName == "Label") refreshed++; };

                L.SetLanguage(L.English);
                shell.Relocalize();

                Require(refreshed == 3 && edge.Label != edgeLabel && band.Label != bandLabel
                    && direction.Label != directionLabel,
                    "calibration choices notify their translated labels without replacing items");
                Require(ReferenceEquals(edge, calibration.RingChoices.Single(choice => choice.Index == 3))
                    && ReferenceEquals(band, calibration.ArcBandNames[2])
                    && calibration.SelectedRingIndex == 3 && calibration.SelectedArcBand == 2,
                    "language changes preserve selected calibration measurements");
                Require(ReferenceEquals(target, calibration.Target)
                    && calibration.Parameters.SelectedCdPreset.Id == preset
                    && calibration.Parameters.CdOuterRadius == radius,
                    "language changes preserve the independent calibration preset, geometry and target");
            }
            finally
            {
                shell?.Calibration.OnWindowClosed();
                shell?.LivePreview.OnWindowClosed();
                L.Initialize(originalLanguage);
            }
        });
    }

    private static void TestToneStatusStability()
    {
        RunOnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "disc-relocalize-tone-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            string originalLanguage = L.CurrentLanguage;
            try
            {
                L.Initialize(L.Chinese);
                ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
                ToneViewModel tone = shell.Design.Tone;
                Require(tone.TonePreviewStatus == "导入图片后自动生成预览。",
                    "the tone module starts idle");

                // A missing composite makes the real render fail. The failure text must survive
                // a language switch instead of falling back to the idle hint, and a render that
                // is still running must keep saying so.
                typeof(ToneViewModel).GetField("_compositePath", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(tone, Path.Combine(directory, "missing.png"));
                Invoke(tone, "RefreshTonePreviewNow", []);
                Require(tone.TonePreviewStatus == "正在按当前影调渲染…",
                    "starting a render publishes the rendering status: " + tone.TonePreviewStatus);

                L.SetLanguage(L.English);
                shell.Relocalize();
                Require(tone.TonePreviewStatus == "Rendering with the current tone settings…",
                    "language switch keeps the running render status: " + tone.TonePreviewStatus);

                PumpUntil(() => tone.TonePreviewStatus.StartsWith("Tone preview failed:", StringComparison.Ordinal),
                    "the failing render settles in the switched language: " + tone.TonePreviewStatus);
                L.SetLanguage(L.Chinese);
                shell.Relocalize();
                Require(tone.TonePreviewStatus.StartsWith("影调预览失败：", StringComparison.Ordinal),
                    "language switch re-words the failure instead of hiding it: " + tone.TonePreviewStatus);

                Invoke(tone, "ClearTonePreview", []);
                Require(tone.TonePreviewStatus == "导入图片后自动生成预览。",
                    "clearing the preview returns to the idle hint");
                L.SetLanguage(L.English);
                shell.Relocalize();
                Require(tone.TonePreviewStatus == "Import an image to generate a preview automatically.",
                    "the idle hint follows the language after a clear: " + tone.TonePreviewStatus);
            }
            finally
            {
                L.Initialize(originalLanguage);
                SynchronizationContext.SetSynchronizationContext(previous);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    private static void TestBurnStatusStability()
    {
        RunOnSta(() =>
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            string originalLanguage = L.CurrentLanguage;
            try
            {
                L.Initialize(L.Chinese);
                ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
                BurnViewModel burn = shell.Burn;
                Require(burn.DeviceStatusText == "进入此页面后会检测本机刻录机。",
                    "the burner panel starts with the idle hint");

                // A failed drive probe is state, not chrome: the switch must re-word it
                // instead of falling back to the idle hint, and a probe that is still running
                // must keep saying so.
                SetField(burn, "_deviceStatusError", "boom");
                burn.Relocalize();
                Require(burn.DeviceStatusText == "检测刻录机失败：boom",
                    "device failure text survives a relocalize: " + burn.DeviceStatusText);
                L.SetLanguage(L.English);
                burn.Relocalize();
                Require(burn.DeviceStatusText == "Recorder detection failed: boom",
                    "device failure text is re-worded: " + burn.DeviceStatusText);

                SetField(burn, "_isRefreshingDevices", true);
                burn.Relocalize();
                Require(burn.DeviceStatusText == "Detecting recorders…",
                    "an in-flight device probe keeps its status: " + burn.DeviceStatusText);
                SetField(burn, "_isRefreshingDevices", false);

                // Speed probing follows the same rules once a device is selected. The backing
                // field is set directly so the selection callback does not probe the hardware.
                SetField(burn, "_selectedDevice", new OpticalBurnDevice("test", "Test recorder", []));
                SetField(burn, "_speedStatusError", "speed boom");
                burn.Relocalize();
                Require(burn.SpeedStatusText == "Could not read the disc’s speeds. The burn interface "
                        + "will use its conservative fallback: speed boom",
                    "speed failure text survives a relocalize: " + burn.SpeedStatusText);
                L.SetLanguage(L.Chinese);
                burn.Relocalize();
                Require(burn.SpeedStatusText.StartsWith("未能读取当前盘片速度，将使用刻录接口的保守回退设置：",
                        StringComparison.Ordinal),
                    "speed failure text is re-worded: " + burn.SpeedStatusText);

                SetField(burn, "_isRefreshingSpeeds", true);
                burn.Relocalize();
                Require(burn.SpeedStatusText == "正在读取当前盘片支持的刻录速度…",
                    "an in-flight speed probe keeps its status: " + burn.SpeedStatusText);
                SetField(burn, "_isRefreshingSpeeds", false);

                SetField(burn, "_selectedDevice", null);
                burn.Relocalize();
                Require(burn.SpeedStatusText == "请先选择刻录机。",
                    "losing the device wins over a stale probe state: " + burn.SpeedStatusText);
            }
            finally
            {
                L.Initialize(originalLanguage);
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
    }

    private static void PumpUntil(Func<bool> condition, string message)
    {
        long deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new InvalidOperationException("Relocalization: " + message);
            }

            DispatcherFrame frame = new();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void TestControlLoadedRefresh()
    {
        WpfTestHost.Run(() =>
        {
            string originalLanguage = L.CurrentLanguage;
            try
            {
                RequireLoadRefresh(() => new SourceChip(), chip => chip.ChipText.Text,
                    "来自设计页合成图", "Composite from Design", "the source chip");
                RequireLoadRefresh(() => new PathField(), field => field.BrowseButton.Content as string ?? string.Empty,
                    "浏览", "Browse", "the path field");
                RequireLoadRefresh(() => new GeometrySummary(), summary => summary.TitleText.Text,
                    "盘片几何", "Disc geometry", "the geometry summary");
                RequireLoadRefresh(() => new InterleaveToggle(), toggle => toggle.Toggle.Content as string ?? string.Empty,
                    "启用 CD-DA 延迟交织", "Enable CD-DA delay interleaving", "the interleave toggle");
                RequireLoadRefresh(() => new StageBar(),
                    bar => ((IEnumerable<StageItem>)bar.StageList.ItemsSource).First().Label,
                    "设计", "Design", "the stage bar");

                // The canvas read-only default is language-neutral until load; a localized
                // DependencyProperty default would have kept the first-touch language.
                L.Initialize(L.Chinese);
                CalibrationPhotoCanvas canvas = new();
                Require(canvas.SelectionDescription.Length == 0,
                    "the canvas description default carries no captured language");
                L.SetLanguage(L.English);
                canvas.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Require(canvas.SelectionDescription
                        == "Scroll to zoom, right-drag to pan; drag control points to fit the edge.",
                    "the canvas reads its description in the load language: " + canvas.SelectionDescription);
                canvas.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            }
            finally
            {
                L.Initialize(originalLanguage);
            }
        });
    }

    private static void RequireLoadRefresh<T>(Func<T> create, Func<T, string> read,
        string chinese, string english, string description) where T : FrameworkElement
    {
        L.Initialize(L.Chinese);
        T control = create();
        Require(read(control) == chinese, description + " starts in the construction language");
        L.SetLanguage(L.English);
        Require(read(control) == chinese, description + " ignores the switch while unloaded");
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Require(read(control) == english, description + " refreshes cached text on load");
        L.SetLanguage(L.Chinese);
        Require(read(control) == chinese, description + " follows later switches while loaded");
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }

    private static void RunOnSta(Action action)
    {
        // Language changes refresh every live XAML target in the process. Use the
        // same UI dispatcher as control and snapshot tests, just as the app does.
        WpfTestHost.Run(action);
    }

    private static Task StartPreview(LivePreviewViewModel preview)
    {
        FieldInfo revision = typeof(LivePreviewViewModel)
            .GetField("_livePreviewRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        revision.SetValue(preview, (int)revision.GetValue(preview)! + 1);
        return (Task)Invoke(preview, "RefreshAsync", [])!;
    }

    private static void RunPreview(LivePreviewViewModel preview) => Wait(StartPreview(preview));

    private static void Wait(Task task)
    {
        DispatcherFrame frame = new();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static object? Invoke(object target, string name, object?[] arguments)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static void SetField(object instance, string name, object? value)
        => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static void WritePng(string path, int size, Color color)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }

        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Relocalization: " + message);
    }
}
