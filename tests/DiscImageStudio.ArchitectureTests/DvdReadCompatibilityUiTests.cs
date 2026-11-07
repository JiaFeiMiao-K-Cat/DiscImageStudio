using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.Localization;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class DvdReadCompatibilityUiTests
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("dvd-read-compatibility-ui-");
        try
        {
            CheckProjectPersistence(directory.FullName);
            WpfTestHost.Run(() => Wait(CheckWorkflowsAsync(directory.FullName)));
        }
        finally { directory.Delete(recursive: true); }
        Console.WriteLine("dvd-read-compatibility-ui: project/legacy records, shared controls, confirmation, frozen calibration and language fallback passed");
    }

    private static ProjectDocument Project(string path, string family = ProjectDocument.DvdFamily)
        => new(ProjectJsonStore.CurrentSchemaVersion, "DVD compatibility", family,
            new(24, 58, 16, UsableSectors: 16), [], new(path, false, "", "TEST", false), null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static void CheckProjectPersistence(string directory)
    {
        string path = Path.Combine(directory, "test.discproj");
        var document = Project(Path.Combine(directory, "test.iso"));
        var state = new DiscParametersState();
        Require(!state.DvdReadCompatibility, "the experimental option starts disabled");
        ProjectSession.ApplyToState(document, state);
        state.DvdReadCompatibility = true;
        document = document with { Outputs = ProjectSession.CaptureOutputs(document, state) };
        ProjectJsonStore.Save(path, document);
        var restored = ProjectJsonStore.Load(path);
        state.DvdReadCompatibility = false;
        ProjectSession.ApplyToState(restored, state);
        Require(restored.Outputs.DvdReadCompatibility && state.DvdReadCompatibility,
            "project save/load and apply/capture preserve the enabled option");

        var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        legacy["outputs"]!.AsObject().Remove("dvdReadCompatibility");
        File.WriteAllText(path, legacy.ToJsonString());
        var old = ProjectJsonStore.Load(path);
        ProjectSession.ApplyToState(old, state);
        Require(!old.Outputs.DvdReadCompatibility && !state.DvdReadCompatibility,
            "opening an older project resets an enabled option");
        ProjectJsonStore.Save(path, old);
        Require(!File.ReadAllText(path).Contains("dvdReadCompatibility", StringComparison.Ordinal),
            "disabled projects retain their existing JSON shape");
        foreach (string family in new[] { ProjectDocument.CdFamily, ProjectDocument.BluRayFamily })
        {
            state.DvdReadCompatibility = true;
            var other = Project(Path.Combine(directory, "other.iso"), family);
            ProjectSession.ApplyToState(other, state);
            Require(!state.DvdReadCompatibility && !ProjectSession.CaptureOutputs(other, state).DvdReadCompatibility,
                family + " does not capture the DVD option");
        }
    }

    private static async Task CheckWorkflowsAsync(string directory)
    {
        string originalLanguage = L.CurrentLanguage;
        ShellViewModel? shell = null;
        Window? host = null;
        try
        {
            L.SetLanguage(L.English);
            shell = new ShellViewModel(Dispatcher.CurrentDispatcher, new NoDeviceBurner());
            var document = Project(Path.Combine(directory, "view.iso"));
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, document);
            shell.IsProjectOpen = true;
            ProjectSession.ApplyToState(document, shell.State);
            shell.IsDirty = false;
            var export = new ExportView(shell.Export);
            host = new Window { DataContext = shell, Content = export };
            Layout(export);
            var exportToggle = Descendants<DvdReadCompatibilityToggle>(export).Single();
            var checkBox = Descendants<CheckBox>(exportToggle).Single();
            checkBox.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Require(shell.State.DvdReadCompatibility && shell.Burn.DvdReadCompatibility && shell.IsDirty,
                "the export checkbox updates shared state and marks the project dirty");
            UiLayoutSnapshot.Save(export, "dvd-read-compatibility-export-en");

            var burn = new BurnView(shell.Burn);
            host.Content = burn;
            Layout(burn);
            var burnToggle = Descendants<DvdReadCompatibilityToggle>(burn).Single();
            Require(burnToggle.IsChecked && burnToggle.IsEditable, "streaming burn shows the shared selection");
            shell.Burn.ConfirmChecked = true;
            Descendants<CheckBox>(burnToggle).Single().SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            Require(!shell.State.DvdReadCompatibility && !exportToggle.IsChecked && !shell.Burn.ConfirmChecked,
                "editing the burn checkbox updates export and invalidates the previous confirmation");
            shell.State.DvdReadCompatibility = true;
            var options = (DvdStreamingOptions)typeof(BurnViewModel)
                .GetMethod("ReadDvdStreamingOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell.Burn, null)!;
            shell.State.DvdReadCompatibility = false;
            Require(options.ReadCompatibility, "prepared project burn options retain the captured value");

            var calibration = shell.Calibration;
            calibration.SelectedDiscType = "DVD";
            calibration.SelectFineModeCommand.Execute(null);
            calibration.Parameters.DvdTotalSectors = "16";
            calibration.Parameters.DvdUsableSectors = "16";
            calibration.Parameters.DvdInnerRadius = "24";
            calibration.Parameters.DvdOuterRadius = "58";
            calibration.Parameters.DvdPitchLinear = calibration.Parameters.DvdPitchQuadratic = calibration.Parameters.DvdPitchCubic = "0";
            var calibrationPage = new CalibrationView(calibration);
            host.Content = calibrationPage;
            Layout(calibrationPage);
            var encoding = Descendants<Expander>(calibrationPage)
                .Single(expander => expander is not StepCard && Descendants<DvdReadCompatibilityToggle>(expander).Any());
            encoding.IsExpanded = true;
            Layout(calibrationPage);
            var setupToggle = Descendants<DvdReadCompatibilityToggle>(calibrationPage).Single();
            var label = Descendants<TextBlock>(setupToggle).Single(text => text.Text == L.T("DvdReadCompatibility.Label"));
            Require(label.ActualWidth <= setupToggle.ActualWidth - 24 && label.ActualHeight > label.FontSize * 1.5,
                "the complete English checkbox label wraps within the narrow calibration sidebar");
            var peer = new CheckBoxAutomationPeer(Descendants<CheckBox>(setupToggle).Single());
            Require(peer.GetName() == label.Text, "the whole label remains the checkbox's accessible name");
            ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
            Require(calibration.Parameters.DvdReadCompatibility && !shell.State.DvdReadCompatibility,
                "standalone calibration exposes the option without modifying project settings");
            UiLayoutSnapshot.Save(calibrationPage, "dvd-read-compatibility-calibration-en");
            calibration.CreateTargetCommand.Execute(null);
            var target = calibration.Target ?? throw new InvalidOperationException(calibration.LastStatusMessage);
            Require(target.Parameters.DvdReadCompatibility && CalibrationViewModel.DvdOptions(target).ReadCompatibility
                && target.DvdReadCompatibilityMode == DvdReadCompatibilityModes.Current
                && CalibrationViewModel.DvdOptions(target).ReadCompatibilityMode == DvdReadCompatibilityModes.Current,
                "creating a target freezes the selected generation mode");
            string sessionPath = Path.Combine(directory, "target.calibration.json");
            calibration.SaveSessionTo(sessionPath);
            var request = BurnViewModel.CreateCalibrationBurnRequest(target, false, "memory-only", null);
            calibration.Parameters.DvdReadCompatibility = false;
            Require(target.Parameters.DvdReadCompatibility && shell.CalibrationBurn.DvdReadCompatibility,
                "editing next-target setup does not alter the saved target or its burn mode");
            shell.CalibrationBurn.DvdReadCompatibility = false;
            Require(shell.CalibrationBurn.DvdReadCompatibility && !shell.CalibrationBurn.CanEditDvdReadCompatibility,
                "calibration burn cannot edit the frozen generation option");
            using var expected = new MemoryStream();
            using var captured = new MemoryStream();
            DvdStreamingGenerator.GeneratePattern(target, expected, CalibrationViewModel.DvdOptions(target));
            request.ProduceContent(captured, CancellationToken.None);
            Require(expected.ToArray().AsSpan().SequenceEqual(captured.ToArray()),
                "the captured calibration stream matches export after the setup is changed");
            calibration.OutputPath = Path.Combine(directory, "exported-target.iso");
            await calibration.GenerateImageCommand.ExecuteAsync(null);
            Require(File.ReadAllBytes(calibration.OutputPath).AsSpan().SequenceEqual(expected.ToArray())
                && CalibrationSessionJson.Load(Path.ChangeExtension(calibration.OutputPath, ".calibration.json"))
                    .Target.Parameters.DvdReadCompatibility
                && DvdImageMetadata.TryLoad(calibration.OutputPath)?.ReadCompatibility == true,
                "actual calibration export publishes the frozen mode, read-back sidecar and matching payload");
            await calibration.LoadSessionFromAsync(sessionPath);
            Require(calibration.Parameters.DvdReadCompatibility && calibration.Target!.Parameters.DvdReadCompatibility
                && calibration.Target.DvdReadCompatibilityMode == DvdReadCompatibilityModes.Current,
                "loading a calibration record restores the frozen option and setup");

            var calibrationBurn = new BurnView(shell.CalibrationBurn);
            host.Content = calibrationBurn;
            Layout(calibrationBurn);
            var savedToggle = Descendants<DvdReadCompatibilityToggle>(calibrationBurn).Single();
            Require(savedToggle.IsChecked && !savedToggle.IsEditable, "calibration burn displays the saved option read-only");
            UiLayoutSnapshot.Save(calibrationBurn, "dvd-read-compatibility-calibration-burn-en");

            var legacy = JsonNode.Parse(File.ReadAllText(sessionPath))!.AsObject();
            legacy["target"]!.AsObject().Remove("dvdReadCompatibilityMode");
            File.WriteAllText(sessionPath, legacy.ToJsonString());
            await calibration.LoadSessionFromAsync(sessionPath);
            Require(calibration.Target!.Parameters.DvdReadCompatibility
                && calibration.Target.DvdReadCompatibilityMode is null
                && CalibrationViewModel.DvdOptions(calibration.Target).ReadCompatibilityMode == DvdReadCompatibilityModes.LegacyBridge,
                "loading an enabled old target retains the original bridge recipe");
            legacy["target"]!["parameters"]!.AsObject().Remove("dvdReadCompatibility");
            // The saved fit search centre is optional and also follows the legacy default.
            legacy["fitOptions"]?["searchCenter"]?.AsObject().Remove("dvdReadCompatibility");
            File.WriteAllText(sessionPath, legacy.ToJsonString());
            await calibration.LoadSessionFromAsync(sessionPath);
            Require(!calibration.Target!.Parameters.DvdReadCompatibility && !calibration.Parameters.DvdReadCompatibility,
                "legacy calibration records default to disabled and clear a previous selection");

            foreach (string key in new[] { "Label", "Hint", "FrozenHint", "Enabled", "Disabled" })
            {
                string fullKey = "DvdReadCompatibility." + key;
                L.SetLanguage(L.English);
                string english = L.T(fullKey);
                Require(english != fullKey && !english.Any(c => c is >= '\u4e00' and <= '\u9fff'),
                    key + " has an English translation");
                L.SetLanguage(L.Japanese);
                Require(L.T(fullKey) == english, key + " uses English fallback when its locale is missing");
            }
            L.SetLanguage(L.Chinese);
            host.Content = export;
            Layout(export);
            UiLayoutSnapshot.Save(export, "dvd-read-compatibility-export-zh");
        }
        finally
        {
            if (host is not null) { host.Content = null; host.Close(); }
            shell?.Calibration.OnWindowClosed();
            shell?.LivePreview.OnWindowClosed();
            L.SetLanguage(originalLanguage);
        }
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(0, 0, 1200, 900));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var visited = new HashSet<DependencyObject> { root };
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.TryPop(out var parent))
        {
            var children = LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>().ToList();
            if (parent is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                    children.Add(VisualTreeHelper.GetChild(parent, index));
            foreach (var child in children)
            {
                if (!visited.Add(child)) continue;
                if (child is T item) yield return item;
                pending.Push(child);
            }
        }
    }

    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("DVD compatibility UI: " + message);
    }

    private sealed class NoDeviceBurner : IOpticalDiscBurner
    {
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([]);

        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
