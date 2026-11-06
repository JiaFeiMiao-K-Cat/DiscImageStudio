using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Controls;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class CalibrationFitLocalizationTests
{
    private const string CoverageReason = "边界连续可见时，沿同一条边界至少描 6 点并覆盖 55% 半径范围；也可描两段各覆盖 10% 半径范围的长边界，每段至少 6 点。只有短片段时需至少 4 段，每段至少 3 点。";

    internal static void Run()
    {
        WpfTestHost.Run(() => Wait(CheckWorkflow()));
        Console.WriteLine("calibration-fit-localization: English boundary failure, sidebar, dock, log and language-switch state passed");
    }

    private static async Task CheckWorkflow()
    {
        string originalLanguage = L.CurrentLanguage;
        var directory = Directory.CreateTempSubdirectory("disc-calibration-fit-language-");
        ShellViewModel? shell = null;
        try
        {
            L.Initialize(L.English);
            shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
            shell.NavigateTo(Workspace.Calibration);
            var model = shell.Calibration;
            model.SelectedDiscType = "DVD";
            model.Parameters.DvdTotalSectors = "2297888";
            model.Parameters.DvdInnerRadius = "24";
            model.Parameters.DvdOuterRadius = "58";
            model.Parameters.DvdPitchLinear = model.Parameters.DvdPitchQuadratic = model.Parameters.DvdPitchCubic = "0";
            model.SelectIntermediateModeCommand.Execute(null);
            model.CreateTargetCommand.Execute(null);
            var target = model.Target!;
            string path = Path.Combine(directory.FullName, "boundary.calibration.json");
            CalibrationSessionJson.Save(path, new(target, [], new(RadiusRangeMm: .01, AutoRange: false),
                Photo: MakePhoto(target)));
            await model.LoadSessionFromAsync(path);

            // Six points enable the operation, but this short section fails the
            // fitter's actual radial-coverage rule shown in the reported screenshot.
            var mapping = CalibrationCrossPrediction.CreateMapping(target, target.Parameters, .37);
            for (int index = 0; index < 6; index++)
            {
                var point = mapping(30 + index * .2);
                model.TraceEditedCommand.Execute(new(0, -1, point.X, point.Y));
            }
            Require(model.SolveCommand.CanExecute(null), "a six-point section enables the calculation");
            await model.SolveCommand.ExecuteAsync(null);
            string english = L.T(CoverageReason);
            Require(english != CoverageReason && english.Contains("at least 6 points", StringComparison.Ordinal)
                && english.Contains("55%", StringComparison.Ordinal) && english.Contains("10%", StringComparison.Ordinal)
                && english.Contains("at least 4 segments with at least 3 points each", StringComparison.Ordinal),
                "the translated reason preserves all point and coverage requirements");
            Require(model.FitAssessment.Contains(english, StringComparison.Ordinal)
                && model.LastStatusMessage.Contains(english, StringComparison.Ordinal)
                && shell.JobStatusText == model.LastStatusMessage
                && shell.LogText.Contains(english, StringComparison.Ordinal)
                && !shell.LogText.Contains(CoverageReason, StringComparison.Ordinal),
                "the real failure is English in the sidebar, bottom status and task log");
            Require(model.LastStatusMessage.Contains("6", StringComparison.Ordinal)
                && model.CoarseCandidates.Count == 0 && !model.ApplyParametersCommand.CanExecute(null),
                "the failed calculation retains the point count and has no applicable candidate");

            var points = model.TracePoints.ToArray();
            var fitField = typeof(CalibrationViewModel).GetField("_fit", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var fit = (CalibrationFitResult)fitField.GetValue(model)!;
            Require(fit.Warnings.Contains(CoverageReason), "the solver retains its raw diagnostic for internal decisions");
            string historicalLog = shell.LogText;
            L.SetLanguage(L.Chinese);
            shell.Relocalize();
            Require(model.FitAssessment.Contains(CoverageReason, StringComparison.Ordinal)
                && model.LastStatusMessage.Contains(CoverageReason, StringComparison.Ordinal)
                && shell.JobStatusText == model.LastStatusMessage,
                "language switching recomposes both the sidebar and the current bottom status");
            L.SetLanguage(L.English);
            shell.Relocalize();
            Require(model.FitAssessment.Contains(english, StringComparison.Ordinal)
                && shell.JobStatusText.Contains(english, StringComparison.Ordinal)
                && ReferenceEquals(fitField.GetValue(model), fit) && model.TracePoints.SequenceEqual(points)
                && shell.LogText == historicalLog,
                "switching back preserves solver state, traced points and historical log entries");
            SaveSnapshot(shell);

            shell.BeginDockJob("Later task", determinate: false);
            shell.CompleteDockJob("Later result");
            L.SetLanguage(L.Chinese);
            shell.Relocalize();
            Require(shell.JobStatusText == "Later result",
                "relocalizing an old fit cannot overwrite the result of a later task");
        }
        finally
        {
            shell?.Calibration.OnWindowClosed();
            shell?.LivePreview.OnWindowClosed();
            L.Initialize(originalLanguage);
            directory.Delete(recursive: true);
        }
    }

    private static CalibrationPhotoSession MakePhoto(CalibrationTarget target)
    {
        const int size = 320;
        const double canvas = 63;
        var image = CalibrationPatternRenderer.Render(target, canvas, size, target.Parameters, .37);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        CalibrationPoint[] Boundary(double radius) => Enumerable.Range(0, 8).Select(index => new CalibrationPoint(
            (size - 1) / 2.0 + radius * size / (2 * canvas) * Math.Cos(index * Math.PI / 4),
            (size - 1) / 2.0 + radius * size / (2 * canvas) * Math.Sin(index * Math.PI / 4))).ToArray();
        return new(Convert.ToBase64String(stream.ToArray()), Boundary(60), Boundary(7.5), 60, true, []);
    }

    private static void SaveSnapshot(ShellViewModel shell)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISC_IMAGE_STUDIO_UI_SNAPSHOTS"))) return;
        var page = new CalibrationView(shell.Calibration);
        var dock = new JobDock { DataContext = shell };
        var layout = new Grid { DataContext = shell };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(page);
        Grid.SetRow(dock, 1);
        layout.Children.Add(dock);
        var host = new Window { DataContext = shell, Content = layout };
        try
        {
            shell.IsJobDockExpanded = true;
            const double width = 1600, height = 1200;
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            dock.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            layout.Measure(new Size(width, height));
            layout.Arrange(new Rect(0, 0, width, height));
            layout.UpdateLayout();
            UiLayoutSnapshot.Save(layout, "calibration-boundary-failure-en");
        }
        finally
        {
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            dock.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            host.Content = null;
            host.Close();
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
        if (!condition) throw new InvalidOperationException("Calibration fit localization: " + message);
    }
}
