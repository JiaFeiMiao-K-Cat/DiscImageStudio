using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

namespace DiscImageStudio;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly WindowWorkArea _workArea;
    private readonly UserControl[] _pages;
    private readonly (SideNavItem Item, Workspace Workspace)[] _navItems;
    private readonly PageHeader?[] _pageHeaders = new PageHeader?[6];
    private bool _isSidebarCollapsed;
    private bool _isManuallyCollapsed;
    private bool _closeApproved;
    private bool _isCloseFlowRunning;
    private int _projectUpdateRevision;

    public MainWindow()
    {
        InitializeComponent();
        _workArea = new WindowWorkArea(this);
        _shell = new ShellViewModel(Dispatcher)
        {
            Dialogs = new FileDialogService(() => this),
        };
        _shell.ConfirmHandler = (title, message, confirmLabel)
            => BurnConfirmModal.ShowAsync(title, message, confirmLabel);
        _shell.SaveConfirmationHandler = (title, message, confirmLabel, neutralLabel)
            => BurnConfirmModal.ShowThreeWayAsync(title, message, confirmLabel, neutralLabel);
        _shell.ShowProjectDialogHandler = ShowProjectDialog;
        _shell.CalibrationParametersApplied += PlayProjectUpdateCelebration;
        _shell.ProjectSaved += PlayProjectSaveTrail;
        DataContext = _shell;

        // Indexed by the Workspace enum, so navigation, the sidebar and the snapshot CLI all
        // share one ordering.
        _pages =
        [
            new HomeView(_shell.Home),
            new DesignView(_shell.Design),
            new LivePreviewView(_shell.LivePreview),
            new ExportView(_shell.Export),
            new BurnView(_shell.Burn),
            new CalibrationView(_shell.Calibration),
        ];
        _navItems =
        [
            (NavDesign, Workspace.Design),
            (NavPreview, Workspace.Preview),
            (NavExport, Workspace.Export),
            (NavBurn, Workspace.Burn),
            (NavCalibration, Workspace.Calibration),
        ];
        foreach ((SideNavItem item, Workspace workspace) in _navItems)
        {
            item.Activated += (_, _) => _shell.NavigateTo(workspace);
        }

        _shell.PropertyChanged += Shell_PropertyChanged;
        UpdateNavActive(Workspace.Home);
        UpdateProjectHeader();
        UpdateStageBars();
        PageHost.Content = _pages[(int)Workspace.Home];

        SizeChanged += (_, _) => UpdateSidebarCollapse();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += (_, _) => WindowCorners.Apply(this);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        if (Application.Current is not null)
        {
            Application.Current.SessionEnding += OnSessionEnding;
        }
    }

    // ---- Snapshot hooks (used by UiPreviewRenderer / CLI ui-snapshot) ----

    internal bool IsLivePreviewReady => _shell.LivePreview.IsLivePreviewReady;

    // An empty project has no composite to project, so the snapshot wait must not sit out
    // its timeout waiting for a preview that will never arrive.
    internal bool IsTonePreviewReady
        => !_shell.IsProjectOpen || !_shell.Design.HasLayers || _shell.Design.Tone.IsTonePreviewReady;

    /// <summary>Snapshot hook: scrolls the workspace's outermost scroll surface so the CLI can
    /// capture the part of a long page that sits below the viewport.</summary>
    internal void ScrollWorkspace(double offset)
    {
        if (offset <= 0 || PageHost is null)
        {
            return;
        }

        ScrollViewer? scroller = FindOuterScrollViewer(PageHost);
        scroller?.ScrollToVerticalOffset(offset);
        UpdateLayout();
    }

    private static ScrollViewer? FindOuterScrollViewer(DependencyObject root)
    {
        return FindDescendant<ScrollViewer>(root);
    }

    internal void ConfigureSnapshot(
        Workspace workspace,
        string? previewPath,
        string? liveInputPath = null,
        string? liveDisc = null,
        string? liveMode = null,
        string? projectPath = null,
        bool expandJobDock = false,
        string? burnSource = null)
    {
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            _shell.OpenProjectForSnapshot(Path.GetFullPath(projectPath));
        }

        _shell.SelectWorkspace(workspace);
        if (workspace == Workspace.Burn)
        {
            bool calibrationSource = string.Equals(burnSource, "calibration", StringComparison.OrdinalIgnoreCase);
            if (calibrationSource)
            {
                // The target has to exist before the burn summary can describe it.
                _shell.Calibration.EnsureTarget();
                _shell.Burn.UseCalibrationPattern = true;
            }

            _shell.Burn.UpdateSourceSummary();
        }
        if (workspace == Workspace.Calibration)
        {
            _shell.Calibration.EnsureTarget();
        }
        if (!string.IsNullOrWhiteSpace(previewPath))
        {
            string fullPath = Path.GetFullPath(previewPath);
            _shell.SelectWorkspace(Workspace.Preview);
            _shell.LivePreview.PrepareResultPreview(fullPath);
            _shell.LivePreview.ShowResultPreview(fullPath);
        }

        if (!string.IsNullOrWhiteSpace(liveInputPath))
        {
            OpticalDiscFamily family = liveDisc?.ToLowerInvariant() switch
            {
                "dvd" => OpticalDiscFamily.Dvd,
                "bd" or "bluray" or "blu-ray" => OpticalDiscFamily.BluRay,
                _ => OpticalDiscFamily.CompactDisc,
            };
            // Omitting --live-mode keeps the page default (source picture); scripts probe the
            // other mode by naming it.
            bool calibration = liveMode is null
                || string.Equals(liveMode, "calibrate", StringComparison.OrdinalIgnoreCase);
            _shell.LivePreview.SetDiscType(family, clearResult: false);
            _shell.LivePreview.SetPreviewMode(calibration, clearResult: false);

            string fullInput = Path.GetFullPath(liveInputPath);
            if (calibration)
            {
                switch (family)
                {
                    case OpticalDiscFamily.Dvd:
                        _shell.State.DvdImagePath = fullInput;
                        break;
                    case OpticalDiscFamily.BluRay:
                        _shell.State.BdImagePath = fullInput;
                        break;
                    default:
                        _shell.State.CdImagePath = fullInput;
                        break;
                }
            }
            else
            {
                switch (family)
                {
                    case OpticalDiscFamily.Dvd:
                        _shell.State.DvdIsoPath = fullInput;
                        break;
                    case OpticalDiscFamily.BluRay:
                        _shell.State.BdIsoPath = fullInput;
                        break;
                    default:
                        _shell.State.CdTrackPath = fullInput;
                        break;
                }
            }

            _shell.SelectWorkspace(Workspace.Preview);
        }

        if (expandJobDock)
        {
            _shell.IsJobDockExpanded = true;
        }
    }

    // Snapshot-only synthetic telemetry. No burner or recorder is created here.
    internal void ShowBurnProgressForSnapshot(string phase)
    {
        if (phase is not ("write" or "buffer" or "finalize"))
            throw new ArgumentException("Burn progress snapshot must be write, buffer or finalize.");
        var kind = _shell.Burn.IsBluRayBurn ? DiscImageStudio.Burning.OpticalBurnMediaKind.BluRayData
            : _shell.Burn.IsDvdBurn ? DiscImageStudio.Burning.OpticalBurnMediaKind.DvdData
            : DiscImageStudio.Burning.OpticalBurnMediaKind.CdAudio;
        long total = kind == DiscImageStudio.Burning.OpticalBurnMediaKind.CdAudio ? 800L * 1024 * 1024
            : kind == DiscImageStudio.Burning.OpticalBurnMediaKind.BluRayData ? 12219392L * 2048 : 2295104L * 2048;
        _shell.SetBusy(true);
        _shell.BeginBurnDockJob(kind, total);
        _shell.ReportBurnProgress(new("buffer", "正在准备防断流缓冲区…", 0, total));
        if (phase != "buffer")
            _shell.ReportBurnProgress(new("write", "刻录机开始写入。", total / 3, total, 8.25 * 1024 * 1024));
        if (phase == "finalize")
            _shell.ReportBurnProgress(new("finalize", "正在刷新缓存并关闭刻录会话…", total / 3, total));
    }

    /// <summary>Snapshot hook: forces the hovered state on the first recent-project card
    /// (card slid left, disc eased out and spinning) so the ui-snapshot CLI can capture the
    /// hover-only motion that a static page render cannot reach.</summary>
    internal void ShowHomeHoverForSnapshot()
    {
        RecentProjectCard? card = FindDescendant<RecentProjectCard>(PageHost);
        if (card is null)
        {
            return;
        }

        card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseEnterEvent,
        });

        PumpDispatcherFor(TimeSpan.FromMilliseconds(450));
    }

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            DispatcherFrame frame = new();
            DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    internal async Task LoadCalibrationSnapshotAsync(string path)
    {
        _shell.SelectWorkspace(Workspace.Calibration);
        await _shell.Calibration.LoadSessionFromAsync(path);
        if (_shell.Calibration.SolveCommand.CanExecute(null))
            await _shell.Calibration.SolveCommand.ExecuteAsync(null);
    }

    internal void ConfigureCalibrationSnapshotView(string mode)
    {
        if (mode is not ("pattern" or "closeup" or "target"))
            throw new ArgumentException("Calibration snapshot view must be pattern, closeup or target.");
        _shell.SelectWorkspace(Workspace.Calibration);
        if (mode == "target")
        {
            // Step one auto-collapses once a target exists; snapshots need it open again to
            // capture the target-generation card.
            _shell.Calibration.StepOneExpanded = true;
            UpdateLayout();
            return;
        }
        var view = (CalibrationView)_pages[(int)Workspace.Calibration];
        // The burn-pattern overlay is view-model state now; no reach-in into the view.
        _shell.Calibration.ShowTargetPreview = mode == "pattern";
        UpdateLayout();
        var canvas = (CalibrationPhotoCanvas)view.FindName("PhotoCanvas");
        canvas.ResetView();
        if (mode != "closeup") return;
        var points = _shell.Calibration.TracePoints;
        if (points.Count < 2) throw new InvalidOperationException("Closeup needs a loaded calibration trace.");
        var minimum = new Point(points.Min(p => p.XMm), points.Min(p => p.YMm));
        var maximum = new Point(points.Max(p => p.XMm), points.Max(p => p.YMm));
        Point a = canvas.WorldToView(minimum), b = canvas.WorldToView(maximum);
        double zoom = Math.Min((canvas.ActualWidth - 90) / Math.Max(1, b.X - a.X),
            (canvas.ActualHeight - 90) / Math.Max(1, b.Y - a.Y));
        canvas.ZoomBy(Math.Clamp(zoom, 1, 8));
        Point center = canvas.WorldToView(new Point((minimum.X + maximum.X) / 2, (minimum.Y + maximum.Y) / 2));
        canvas.PanBy(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2) - center);
    }

    // ---- Window lifecycle ----

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        // Page visual trees exist now (the constructor ran while the window was still
        // unwrapped), so the stage rails can finally be resolved and pushed the current
        // workspace the snapshot/startup flow selected before Show.
        UpdateStageBars();
        _shell.MarkWindowLoaded();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        if (!_shell.IsProjectOpen || !_shell.IsDirty)
        {
            // Nothing unsaved: the close proceeds naturally. Close() must not be called
            // from inside this handler — WPF is still inside the close it is processing.
            return;
        }

        // Veto first, synchronously: the save modal is awaited, and WPF commits the close
        // the moment this handler yields while e.Cancel is still false.
        e.Cancel = true;
        if (_isCloseFlowRunning)
        {
            // A second close request while the save modal is open must not close behind it.
            return;
        }

        _isCloseFlowRunning = true;
        try
        {
            CancelEventArgs decision = new();
            try
            {
                await _shell.HandleWindowClosingAsync(decision);
            }
            catch (InvalidOperationException)
            {
                // Another modal already owns the dialog surface; keep the window open.
                return;
            }

            if (decision.Cancel)
            {
                // Cancelled by the user, or the save failed and nothing may be lost.
                return;
            }
        }
        finally
        {
            _isCloseFlowRunning = false;
        }

        // This Close() runs on the awaited continuation, outside the close WPF was
        // processing, so it re-enters Closing with _closeApproved set and proceeds.
        _closeApproved = true;
        Close();
    }

    // Windows shutdown and logoff tear the session down without raising Closing, so the
    // same unsaved-changes gate has to run here. Cancelling the session end keeps the
    // process alive while the save modal resolves; saving or discarding then closes the
    // window and exits the app normally.
    private async void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (!_shell.IsProjectOpen || !_shell.IsDirty || _isCloseFlowRunning)
        {
            return;
        }

        e.Cancel = true;
        await Dispatcher.InvokeAsync(async () =>
        {
            CancelEventArgs decision = new();
            try
            {
                await _shell.HandleWindowClosingAsync(decision);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (!decision.Cancel)
            {
                _closeApproved = true;
                Close();
            }
        });
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _workArea.Dispose();
        _shell.LivePreview.OnWindowClosed();
        _shell.Calibration.OnWindowClosed();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (BurnConfirmModal.HandleKeyDown(e.Key))
            {
                // The modal consumed the key; pages must not react to it behind its back.
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift)
            && _shell.IsProjectOpen)
        {
            e.Handled = true;
            _ = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                ? _shell.SaveProjectAsCommand.ExecuteAsync(null)
                : _shell.SaveProjectCommand.ExecuteAsync(null);
        }
    }

    // ---- Project header ----

    private void EditProject_Click(object sender, RoutedEventArgs e)
        => _ = _shell.EditProjectCommand.ExecuteAsync(null);

    /// <summary>Snapshot hook: trigger the acknowledgement and advance into the sweep so the
    /// CLI can capture the otherwise transient header animation.</summary>
    internal void PlayProjectUpdateForSnapshot()
    {
        PlayProjectUpdateCelebration();
        PumpDispatcherFor(TimeSpan.FromMilliseconds(320));
    }

    /// <summary>Snapshot hook: paint the save trail at a fixed point in its lap so the CLI
    /// captures the same frame every run.</summary>
    internal void PlayProjectSaveForSnapshot()
    {
        if (!_shell.IsProjectOpen || SidebarBorder.Visibility != Visibility.Visible
            || ProjectHeaderButton.Visibility != Visibility.Visible)
        {
            return;
        }

        ProjectSaveTrail.ShowAt(0.62);
    }

    /// <summary>A successful save acknowledges on the project header itself: one green
    /// segment runs once around the rounded header, its tail stretching with the Bezier
    /// speed of the head. The unsaved mark vanishing alongside is the durable signal; no
    /// toast on top. If the header is hidden (collapsed sidebar) the lap is simply skipped.</summary>
    private void PlayProjectSaveTrail()
    {
        if (!_shell.IsProjectOpen || SidebarBorder.Visibility != Visibility.Visible
            || ProjectHeaderButton.Visibility != Visibility.Visible)
        {
            return;
        }

        // A second save while a lap is running restarts it instead of stacking animations.
        ProjectSaveTrail.Play();
    }

    /// <summary>Applying a calibration rewrites the project's geometry while the user is on
    /// another workspace. The sidebar header carries the acknowledgement: one diagonal light
    /// band sweeps it like a card being tilted, and a short success tag says the parameters
    /// changed. If the header is collapsed, the toast is the only readable surface left.</summary>
    private void PlayProjectUpdateCelebration()
    {
        if (!_shell.IsProjectOpen || SidebarBorder.Visibility != Visibility.Visible
            || ProjectHeaderButton.Visibility != Visibility.Visible)
        {
            _shell.ShowToast("标定参数已应用", "本工程的参数已更新。", ToastKind.Success);
            return;
        }

        int revision = ++_projectUpdateRevision;
        double width = ProjectHeaderHost.ActualWidth > 0 ? ProjectHeaderHost.ActualWidth : 180;
        double band = Math.Max(72, width * 0.55);
        ProjectUpdateShineBand.Width = band;

        ProjectUpdateShine.BeginAnimation(OpacityProperty, null);
        ProjectUpdateShine.Opacity = 1;
        ProjectUpdateShineTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ProjectUpdateShineTranslate.X = -band * 1.5;
        var sweep = new DoubleAnimation(-band * 1.5, width + band * 1.5, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        sweep.Completed += (_, _) =>
        {
            if (revision != _projectUpdateRevision)
            {
                return;
            }

            var fade = new DoubleAnimation(0, Motion.Base) { EasingFunction = Motion.EaseOut };
            fade.Completed += (_, _) =>
            {
                if (revision != _projectUpdateRevision)
                {
                    return;
                }

                ProjectUpdateShine.BeginAnimation(OpacityProperty, null);
                ProjectUpdateShine.Opacity = 0;
                ProjectUpdateShineTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            };
            ProjectUpdateShine.BeginAnimation(OpacityProperty, fade);
        };
        ProjectUpdateShineTranslate.BeginAnimation(TranslateTransform.XProperty, sweep);

        var badge = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromSeconds(2.6)),
        };
        badge.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        badge.KeyFrames.Add(new EasingDoubleKeyFrame(1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), Motion.EaseOut));
        badge.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.0))));
        badge.KeyFrames.Add(new EasingDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.6)), Motion.EaseOut));
        ProjectUpdateBadge.BeginAnimation(OpacityProperty, badge);
    }

    private void NavAbout_Activated(object sender, RoutedEventArgs e)
    {
        AboutWindow window = new(_shell.About) { Owner = this };
        window.ShowDialog();
    }

    private void UpdateProjectHeader()
    {
        SidebarBorder.Visibility = _shell.IsProjectMode ? Visibility.Visible : Visibility.Collapsed;
        // The job dock is chrome for the workspace shell: on the start screen there is no
        // project to work on, so it stays out of the way.
        DockHost.Visibility = _shell.IsProjectMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_shell.IsProjectOpen)
        {
            return;
        }

        ProjectNameText.Text = _shell.CurrentProject!.Name;
        ProjectFamilyText.Text = _shell.CurrentProject.FamilyLabel;
        ProjectDirtyText.Visibility = _shell.IsDirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.CurrentWorkspace)
            or nameof(ShellViewModel.IsProjectOpen)
            or nameof(ShellViewModel.IsDirty)
            or nameof(ShellViewModel.CurrentProject))
        {
            UpdateNavActive(_shell.CurrentWorkspace);
            ShowPage(_shell.CurrentWorkspace);
            UpdateProjectHeader();
            if (e.PropertyName is nameof(ShellViewModel.CurrentWorkspace))
            {
                UpdateStageBars();
            }
        }
    }

    // ---- Stage rails ----

    // Every workspace page declares its own PageHeader; the window (which owns the pages and
    // the shell) is where the current workspace is pushed into them and their clicks routed
    // back to NavigateTo. Resolved lazily and cached once per page.
    private void UpdateStageBars()
    {
        for (int index = 0; index < _pages.Length; index++)
        {
            if (_pageHeaders[index] is null)
            {
                // Pages only grow their visual tree once the window shows and applies
                // templates, so resolution succeeds on the Loaded pass (and on any workspace
                // change after it), not while the constructor runs.
                if (FindDescendant<PageHeader>(_pages[index]) is not { } header)
                {
                    continue;
                }

                header.StageActivated += workspace => _shell.NavigateTo(workspace);
                _pageHeaders[index] = header;
            }

            _pageHeaders[index]!.Current = _shell.CurrentWorkspace;
        }
    }

    /// <summary>Expands every plain Expander on the current page (StepCards keep their
    /// accordion rules) so snapshots can capture folded disclosure bodies.</summary>
    internal void ExpandDisclosureForSnapshot()
    {
        ExpandExpanders(_pages[(int)_shell.CurrentWorkspace]);
        UpdateLayout();
        // The expanders' auto-scroll runs at Loaded priority; pump the queue down to
        // Background so the capture shows the post-scroll state, not the mid-flight one.
        // Window shadows the Dispatcher type with its instance property, so qualify.
        DispatcherFrame pump = new();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            () => System.Windows.Threading.Dispatcher.ExitAllFrames());
        System.Windows.Threading.Dispatcher.PushFrame(pump);
        UpdateLayout();
    }

    private static void ExpandExpanders(DependencyObject root)
    {
        Queue<DependencyObject> pending = new();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Dequeue();
            if (current is Expander expander && expander is not Controls.StepCard)
            {
                expander.IsExpanded = true;
            }

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < count; index++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        Queue<DependencyObject> pending = new();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Dequeue();
            if (current is T match)
            {
                return match;
            }

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < count; index++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }

        return null;
    }

    // ---- Project dialog host ----

    private ProjectDialogResult? ShowProjectDialog(ProjectDialogRequest request)
    {
        ProjectDialogViewModel viewModel = new(_shell.State, request);
        if (request.EditMode && _shell.CurrentProject is { } project)
        {
            // Prefill from State so calibration-updated geometry shows its live values.
            viewModel.Family = project.Family;
            viewModel.Name = project.Name;
            if (project.Family == OpticalDiscFamily.Dvd)
            {
                viewModel.InnerRadiusText = _shell.State.DvdInnerRadius;
                viewModel.OuterRadiusText = _shell.State.DvdOuterRadius;
                viewModel.SectorsText = _shell.State.DvdTotalSectors;
                viewModel.PitchLinearText = _shell.State.DvdPitchLinear;
                viewModel.PitchQuadraticText = _shell.State.DvdPitchQuadratic;
                viewModel.PitchCubicText = _shell.State.DvdPitchCubic;
            }
            else if (project.Family == OpticalDiscFamily.BluRay)
            {
                viewModel.InnerRadiusText = _shell.State.BdInnerRadius;
                viewModel.OuterRadiusText = _shell.State.BdOuterRadius;
                viewModel.SectorsText = _shell.State.BdTotalSectors;
            }
            else
            {
                viewModel.InnerRadiusText = _shell.State.CdInnerRadius;
                viewModel.OuterRadiusText = _shell.State.CdOuterRadius;
                viewModel.SectorsText = _shell.State.CdSectors;
            }
        }

        ProjectDialogWindow window = new(viewModel) { Owner = this };
        window.ShowDialog();
        return window.DialogResult == true ? viewModel.Result : null;
    }

    // ---- Navigation chrome: active states, page swap, entrance animation, scroll reset ----

    private void UpdateNavActive(Workspace workspace)
    {
        foreach ((SideNavItem item, Workspace itemWorkspace) in _navItems)
        {
            item.IsActive = itemWorkspace == workspace;
        }
    }

    private void ShowPage(Workspace workspace)
    {
        UserControl page = _pages[(int)workspace];
        if (ReferenceEquals(PageHost.Content, page))
        {
            return;
        }

        PageHost.Content = page;
        ResetPageScroll(page);
        PlayEntrance(page);
    }

    private static void ResetPageScroll(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset(0);
            scrollViewer.ScrollToHorizontalOffset(0);
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            ResetPageScroll(VisualTreeHelper.GetChild(root, index));
        }
    }

    private static void PlayEntrance(UIElement page)
    {
        TranslateTransform translate = new(0, 8);
        page.RenderTransform = translate;
        page.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, Motion.Base) { EasingFunction = Motion.EaseOut });
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, Motion.Base) { EasingFunction = Motion.EaseOut });
    }

    // ---- Sidebar collapse (window width < 920, or the manual toggle) ----

    private void SidebarCollapseToggle_Click(object sender, RoutedEventArgs e)
    {
        // Narrow windows own the collapsed layout via auto-collapse; manual expand is a no-op there.
        if (ActualWidth < 920)
        {
            return;
        }

        _isManuallyCollapsed = !_isManuallyCollapsed;
        UpdateSidebarCollapse();
    }

    private void UpdateSidebarCollapse()
    {
        bool narrow = ActualWidth < 920;
        bool collapsed = narrow || _isManuallyCollapsed;
        // Auto-collapse owns the narrow layout, so the manual toggle is meaningless there;
        // it comes back once the window widens past the breakpoint.
        SidebarCollapseToggle.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        if (collapsed == _isSidebarCollapsed)
        {
            return;
        }

        double sidebarWidth = ThemeValues.Double("SidebarWidth", 288);
        double sidebarCollapsedWidth = ThemeValues.Double("SidebarCollapsedWidth", 72);
        double from = _isSidebarCollapsed ? sidebarCollapsedWidth : sidebarWidth;
        double to = collapsed ? sidebarCollapsedWidth : sidebarWidth;
        _isSidebarCollapsed = collapsed;
        SidebarBorder.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(from, to, Motion.Fast) { EasingFunction = Motion.EaseOut });
        foreach ((SideNavItem item, _) in _navItems)
        {
            item.IsCollapsed = collapsed;
        }

        NavAbout.IsCollapsed = collapsed;
        ProjectHeaderButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // 16 matches the expanded margin in MainWindow.xaml; a different value here made the
        // whole nav jump on the first toggle.
        TopNavPanel.Margin = collapsed ? new Thickness(0, 20, 0, 0) : new Thickness(0, 16, 0, 0);
        SidebarGrid.Margin = collapsed ? new Thickness(0) : new Thickness(24, 0, 24, 0);
        BackHomeButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        CollapseLeftIcon.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseRightIcon.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        SidebarCollapseToggle.ToolTip = collapsed ? "展开侧边栏" : "收起侧边栏";
    }
}
