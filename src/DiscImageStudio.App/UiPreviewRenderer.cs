using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio;

internal static class UiPreviewRenderer
{
    internal static int Render(
        string outputPath,
        Workspace workspace = Workspace.Home,
        string? previewPath = null,
        string? liveInputPath = null,
        string? liveDisc = null,
        string? liveMode = null,
        int width = 1366,
        int height = 768,
        string? calibrationSessionPath = null,
        string? calibrationView = null,
        string? projectPath = null,
        string? projectDialog = null,
        bool aboutDialog = false,
        bool presetDialog = false,
        bool expandJobDock = false,
        string? burnSource = null,
        bool expandDisclosure = false,
        int scrollOffset = 0,
        bool homeHover = false,
        bool projectUpdate = false,
        bool projectSave = false,
        bool tonePreview = false,
        string? burnProgress = null)
    {
        if (width < 800 || height < 600 || width > 4096 || height > 2160)
            throw new ArgumentOutOfRangeException(nameof(width), "Snapshot dimensions must be 800–4096 × 600–2160.");
        Application application = AppHost.CreateApplication(ShutdownMode.OnExplicitShutdown);
        if (projectDialog is not null)
        {
            return RenderProjectDialog(outputPath, projectDialog, width, height, application);
        }
        if (aboutDialog)
        {
            return RenderAboutDialog(outputPath, width, height, application);
        }
        if (presetDialog)
        {
            return RenderPresetSaveDialog(outputPath, application);
        }
        if (tonePreview)
        {
            // The tone module lives in the design page's right rail; its preview only renders
            // while that workspace is active.
            workspace = Workspace.Design;
        }
        MainWindow window = new()
        {
            Width = width,
            Height = height,
            // The app ships maximized; snapshots need the exact requested client size, so
            // the XAML default is overridden here.
            WindowState = WindowState.Normal,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
        };
        window.ConfigureSnapshot(
            workspace,
            previewPath,
            liveInputPath,
            liveDisc,
            liveMode,
            projectPath,
            expandJobDock,
            burnSource);
        if (burnProgress is not null) window.ShowBurnProgressForSnapshot(burnProgress);
        window.Show();
        window.UpdateLayout();
        if (homeHover)
        {
            window.ShowHomeHoverForSnapshot();
            window.UpdateLayout();
        }
        if (!string.IsNullOrWhiteSpace(calibrationSessionPath))
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                Task loading = window.LoadCalibrationSnapshotAsync(calibrationSessionPath);
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (!loading.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(60))
                {
                    DispatcherFrame frame = new();
                    DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start();
                    Dispatcher.PushFrame(frame);
                }
                if (!loading.IsCompleted) throw new TimeoutException("Calibration snapshot did not finish within the timeout.");
                loading.GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            window.UpdateLayout();
        }
        if (!string.IsNullOrWhiteSpace(calibrationView))
        {
            window.ConfigureCalibrationSnapshotView(calibrationView);
            window.UpdateLayout();
        }
        if (!string.IsNullOrWhiteSpace(liveInputPath))
        {
            WaitForLivePreview(window, TimeSpan.FromSeconds(30));
            window.UpdateLayout();
        }
        if (tonePreview)
        {
            WaitForTonePreview(window, TimeSpan.FromSeconds(90));
            window.UpdateLayout();
        }

        if (expandDisclosure)
        {
            window.ExpandDisclosureForSnapshot();
        }
        if (projectUpdate)
        {
            window.PlayProjectUpdateForSnapshot();
            window.UpdateLayout();
        }
        if (projectSave)
        {
            window.PlayProjectSaveForSnapshot();
            window.UpdateLayout();
        }

        window.ScrollWorkspace(scrollOffset);
        return SaveWindowBitmap(window, outputPath, width, height, application);
    }

    /// <summary>Renders the project dialog standalone (create or edit mode) so the snapshot
    /// CLI can smoke-test its XAML without opening the main window.</summary>
    private static int RenderProjectDialog(
        string outputPath,
        string mode,
        int width,
        int height,
        Application application)
    {
        if (mode is not ("new" or "edit" or "bd"))
        {
            throw new ArgumentException("Project dialog snapshot mode must be new, edit or bd.");
        }

        DiscParametersState state = new();
        state.TryLoadPresets();
        OpticalDiscFamily family = mode switch
        {
            "edit" => OpticalDiscFamily.Dvd,
            "bd" => OpticalDiscFamily.BluRay,
            _ => OpticalDiscFamily.CompactDisc,
        };
        ProjectDialogViewModel viewModel = new(
            state,
            new ProjectDialogRequest(EditMode: mode == "edit", PreselectFamily: family));
        if (mode == "edit")
        {
            viewModel.Family = OpticalDiscFamily.Dvd;
            viewModel.Name = "示例工程";
            viewModel.InnerRadiusText = "24.0";
            viewModel.OuterRadiusText = "58.0";
            viewModel.SectorsText = "2295104";
            viewModel.PitchLinearText = "0.001";
        }

        Window window = new ProjectDialogWindow(viewModel)
        {
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        return SaveWindowBitmap(window, outputPath, width, height, application);
    }

    /// <summary>Renders the about dialog standalone so the snapshot CLI covers its XAML too.</summary>
    private static int RenderAboutDialog(
        string outputPath,
        int width,
        int height,
        Application application)
    {
        Window window = new AboutWindow(new AboutViewModel())
        {
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        return SaveWindowBitmap(window, outputPath, width, height, application);
    }

    /// <summary>Renders the preset-save dialog standalone so the snapshot CLI covers its XAML
    /// too. The window sizes itself to content, so the bitmap takes the laid-out size.</summary>
    private static int RenderPresetSaveDialog(
        string outputPath,
        Application application)
    {
        Window window = new PresetSaveWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        return SaveWindowBitmap(
            window,
            outputPath,
            (int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight),
            application);
    }

    private static int SaveWindowBitmap(
        Window window,
        string outputPath,
        int width,
        int height,
        Application application)
    {
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream output = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        window.Close();
        application.Shutdown();
        Console.WriteLine($"UI snapshot complete: output={fullPath}");
        return 0;
    }

    private static void WaitForLivePreview(MainWindow window, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!window.IsLivePreviewReady && stopwatch.Elapsed < timeout)
        {
            DispatcherFrame frame = new();
            DispatcherTimer timer = new()
            {
                Interval = TimeSpan.FromMilliseconds(50),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        if (!window.IsLivePreviewReady)
        {
            throw new TimeoutException("Live preview did not finish within the snapshot timeout.");
        }
    }

    /// <summary>Pumps the dispatcher until the design page's tone module has a rendered disc
    /// preview; both the composite and the tone projection are produced by debounced timers,
    /// which need the frame loop to run.</summary>
    private static void WaitForTonePreview(MainWindow window, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!window.IsTonePreviewReady && stopwatch.Elapsed < timeout)
        {
            DispatcherFrame frame = new();
            DispatcherTimer timer = new()
            {
                Interval = TimeSpan.FromMilliseconds(50),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        if (!window.IsTonePreviewReady)
        {
            throw new TimeoutException("Tone preview did not finish within the snapshot timeout.");
        }
    }

}
