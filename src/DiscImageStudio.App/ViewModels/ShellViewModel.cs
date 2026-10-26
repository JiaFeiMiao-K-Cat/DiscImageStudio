using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// Workspace shell: the home start screen (no sidebar) and the project workspaces
/// (sidebar with design/preview/export/burn/calibration). All workspaces share one
/// <see cref="DiscParametersState"/>; an open <see cref="ProjectDocument"/> fills it on open
/// and captures it on save. Long-running work reports into the job dock, which lives in the
/// window chrome, so a job never navigates the user away from the surface they were using.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly StringBuilder _log = new();
    private readonly string? _presetLoadWarning;
    private readonly RecentProjectStore _recentProjects = new();
    private bool _isBusy;
    private bool _isWindowLoaded;
    private string? _lastOutputPath;
    private bool _isApplyingProject;
    private bool _isWritingComposite;

    public ShellViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        State = new DiscParametersState();
        State.PropertyChanged += State_PropertyChanged;
        _presetLoadWarning = State.TryLoadPresets();
        State.SelectDefaultPresets();
        Design = new DesignViewModel(this, State, dispatcher);
        LivePreview = new LivePreviewViewModel(_dispatcher, this, State);
        Home = new HomeViewModel(this, State, _recentProjects);
        Calibration = new CalibrationViewModel(this, State);
        Burn = new BurnViewModel(this, State, Design);
        Export = new ExportViewModel(this, State, Design);
        About = new AboutViewModel();
        Design.CompositeRendered += Design_CompositeRendered;
        Design.DesignEdited += () => MarkProjectDirty();
        OpenOutputCommand = new RelayCommand(OpenOutput, () => OpenOutputEnabled);
        CancelJobCommand = new RelayCommand(CancelJob, () => IsJobRunning && _jobCancellation is not null);
        PreviewLastOutputCommand = new RelayCommand(PreviewLastOutput, () => CanPreviewLastOutput);
        ToggleJobDockCommand = new RelayCommand(() => IsJobDockExpanded = !IsJobDockExpanded);
        ClearLogCommand = new RelayCommand(ClearLog, () => HasLogContent);
        BrowseCdTrackCommand = new RelayCommand(() => BrowseCdTrack(), () => !IsBusy);
        BrowseDvdIsoCommand = new RelayCommand(() => BrowseDvdIso(), () => !IsBusy);
        BrowseBluRayIsoCommand = new RelayCommand(() => BrowseBluRayIso(), () => !IsBusy);
        NewProjectCommand = new AsyncRelayCommand(() => RequestNewProjectAsync(null, null), () => !IsBusy);
        OpenProjectCommand = new AsyncRelayCommand(OpenProjectAsync, () => !IsBusy);
        EditProjectCommand = new AsyncRelayCommand(EditProjectAsync, () => !IsBusy && IsProjectOpen);
        SaveProjectCommand = new AsyncRelayCommand(SaveProjectAsync, () => IsProjectOpen && !IsBusy);
        SaveProjectAsCommand = new AsyncRelayCommand(SaveProjectAsAsync, () => IsProjectOpen && !IsBusy);
        BackHomeCommand = new AsyncRelayCommand(BackHomeAsync);
        NavigateCommand = new RelayCommand<Workspace>(NavigateTo);
    }

    public DiscParametersState State { get; }

    public DesignViewModel Design { get; }

    public LivePreviewViewModel LivePreview { get; }

    public HomeViewModel Home { get; }

    /// <summary>The export workspace: writes the design composite to a file. Its only job is
    /// the file destination; disc writing lives in the burn workspace next door.</summary>
    public ExportViewModel Export { get; }

    /// <summary>The burn workspace: streams the design composite or the calibration target to
    /// a blank disc. It reads the same shared state the export workspace writes with, so the
    /// two delivery surfaces can never present different material for one project.</summary>
    public BurnViewModel Burn { get; }

    public CalibrationViewModel Calibration { get; }

    public AboutViewModel About { get; }

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    /// <summary>Installed by the window: Win32 file dialogs modal to the main window.</summary>
    internal FileDialogService? Dialogs { get; set; }

    /// <summary>Installed by the window; presents the in-app burn confirmation modal.</summary>
    internal Func<string, string, string, Task<bool>>? ConfirmHandler { get; set; }

    /// <summary>Installed by the window; presents the save/discard/cancel modal. True saves,
    /// false discards, null cancels.</summary>
    internal Func<string, string, string, string, Task<bool?>>? SaveConfirmationHandler { get; set; }

    /// <summary>Installed by the window; opens ProjectDialogWindow modally. Null cancels.</summary>
    internal Func<ProjectDialogRequest, ProjectDialogResult?>? ShowProjectDialogHandler { get; set; }

    [ObservableProperty]
    private Workspace _currentWorkspace = Workspace.Home;

    [ObservableProperty]
    private bool _openOutputEnabled;

    [ObservableProperty]
    private bool _isProjectOpen;

    [ObservableProperty]
    private bool _isDirty;

    // ---- Job dock: the shell-level surface for running work ----

    /// <summary>True while any job or burn occupies the shell; the dock shows its progress.</summary>
    [ObservableProperty]
    private bool _isJobRunning;

    /// <summary>True when the last job wrote a file that can be revealed or previewed.</summary>
    [ObservableProperty]
    private bool _jobHasResult;

    /// <summary>True when that file is a disc artifact the read-back preview can open.</summary>
    [ObservableProperty]
    private bool _canPreviewLastOutput;

    /// <summary>Task name, e.g. "正在生成 DVD ISO…"; shown in the collapsed dock strip.</summary>
    [ObservableProperty]
    private string _jobTitle = "就绪";

    /// <summary>True once the dock's run log holds anything. The log affordances follow it, so
    /// a fresh project shows a status strip instead of buttons that cannot do anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(DockToggleVisibility))]
    private bool _hasLogContent;

    [ObservableProperty]
    private string _jobStatusText = "尚未运行任务。";

    [ObservableProperty]
    private double _jobProgressValue;

    /// <summary>CLI jobs report no percentage, so their bar sweeps; work that does report a
    /// fraction (the streaming burn) switches the same bar to determinate.</summary>
    [ObservableProperty]
    private bool _isJobProgressIndeterminate = true;

    [ObservableProperty]
    private bool _isJobDockExpanded;

    public ProjectDocument? CurrentProject { get; private set; }

    public string? ProjectFilePath { get; private set; }

    /// <summary>Title-bar text: the project name plus the unsaved dot.</summary>
    public string ProjectTitleDisplay => !IsProjectOpen
        ? string.Empty
        : CurrentProject!.Name + (IsDirty ? " •" : string.Empty);

    // The job dock's buttons rely on their commands' own CanExecute rather than a second
    // IsEnabled binding, so the enablement has to reach the commands.
    partial void OnOpenOutputEnabledChanged(bool value) => OpenOutputCommand.NotifyCanExecuteChanged();

    partial void OnCanPreviewLastOutputChanged(bool value)
    {
        PreviewLastOutputCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPreviewLastOutputVisibility));
    }

    /// <summary>The read-back action only appears for artifacts the preview can actually open.</summary>
    public Visibility CanPreviewLastOutputVisibility =>
        CanPreviewLastOutput ? Visibility.Visible : Visibility.Collapsed;

    // Null-conditional for the same reason as SetBusy: the calibration photo tests build a
    // shell directly and can log before the constructor has created the command.
    partial void OnHasLogContentChanged(bool value) => ClearLogCommand?.NotifyCanExecuteChanged();

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(ProjectTitleDisplay));
        SaveProjectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsProjectOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentProject));
        OnPropertyChanged(nameof(ProjectTitleDisplay));
        OnPropertyChanged(nameof(IsProjectMode));
        EditProjectCommand.NotifyCanExecuteChanged();
        SaveProjectCommand.NotifyCanExecuteChanged();
        SaveProjectAsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The sidebar (workspace navigation) is visible exactly when a workspace shows,
    /// which in practice means a project is open.</summary>
    public bool IsProjectMode => CurrentWorkspace != Workspace.Home;

    partial void OnIsJobDockExpandedChanged(bool value) => OnPropertyChanged(nameof(DockToggleVisibility));

    partial void OnCurrentWorkspaceChanged(Workspace value) => OnPropertyChanged(nameof(IsProjectMode));

    /// <summary>Progress bar visibility in the job dock: running work or a finished result.</summary>
    public Visibility JobProgressVisibility =>
        IsJobRunning || JobHasResult ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsJobRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(JobProgressVisibility));
        OnPropertyChanged(nameof(CancelJobVisibility));
        CancelJobCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>The cancel action exists only when the running module accepts a token.</summary>
    public Visibility CancelJobVisibility =>
        IsJobRunning && _jobCancellation is not null ? Visibility.Visible : Visibility.Collapsed;

    partial void OnJobHasResultChanged(bool value)
    {
        OnPropertyChanged(nameof(JobProgressVisibility));
        OnPropertyChanged(nameof(ResultActionsVisibility));
    }

    /// <summary>Result/reveal actions only exist once a job has produced something.</summary>
    public Visibility ResultActionsVisibility =>
        JobHasResult ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Clear log" only exists once there is a log to read.</summary>
    public Visibility LogActionsVisibility =>
        HasLogContent ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The expander follows the log, but stays put while the dock is open: an open dock
    /// must always offer the way to close it again, even with an empty log.</summary>
    public Visibility DockToggleVisibility =>
        HasLogContent || IsJobDockExpanded ? Visibility.Visible : Visibility.Collapsed;

    public bool IsBusy => _isBusy;

    public bool IsWindowLoaded => _isWindowLoaded;

    /// <summary>Reveals the last job's output folder; shared by the job dock.</summary>
    public RelayCommand OpenOutputCommand { get; }

    public RelayCommand CancelJobCommand { get; }

    private CancellationTokenSource? _jobCancellation;

    private void CancelJob()
    {
        if (_jobCancellation is not { } cancellation)
        {
            return;
        }

        JobStatusText = "正在取消…";
        cancellation.Cancel();
    }

    /// <summary>Opens the last disc artifact in the read-back preview workspace.</summary>
    public RelayCommand PreviewLastOutputCommand { get; }

    /// <summary>Expands or collapses the job dock's log surface.</summary>
    public RelayCommand ToggleJobDockCommand { get; }

    /// <summary>Clears the job dock's run log.</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>Live preview page: picks the generated CD track to simulate.</summary>
    public RelayCommand BrowseCdTrackCommand { get; }

    /// <summary>Live preview page: picks the generated DVD ISO to simulate.</summary>
    public RelayCommand BrowseDvdIsoCommand { get; }

    /// <summary>Live preview page: picks the generated BD ISO to read back.</summary>
    public RelayCommand BrowseBluRayIsoCommand { get; }

    public IAsyncRelayCommand NewProjectCommand { get; }

    public IAsyncRelayCommand OpenProjectCommand { get; }

    public IAsyncRelayCommand EditProjectCommand { get; }

    public IAsyncRelayCommand SaveProjectCommand { get; }

    public IAsyncRelayCommand SaveProjectAsCommand { get; }

    public IAsyncRelayCommand BackHomeCommand { get; }

    /// <summary>Workspace navigation for page headers ("下一步：…") and any other in-page
    /// affordance that moves the user along the design → preview → export → burn path.</summary>
    public IRelayCommand<Workspace> NavigateCommand { get; }

    /// Fired on every busy transition so page commands can refresh CanExecute.
    public event Action? BusyChanged;

    public event Action<string>? LogAppended;

    public event Action? LogCleared;

    /// <summary>Raised when a calibration result has been written into the shared project
    /// parameters. The window plays the project-header acknowledgement; the fitter stays
    /// independent of any window chrome.</summary>
    public event Action? CalibrationParametersApplied;

    /// <summary>Raised after a successful save (Ctrl+S / 另存为). The window runs the
    /// project-header green trail; failures toast from the save path instead.</summary>
    public event Action? ProjectSaved;

    internal void NotifyCalibrationParametersApplied() => CalibrationParametersApplied?.Invoke();

    /// <summary>The entire log so far. It lives here rather than only in the log TextBox because
    /// the log page is one of several: output produced while that page is hidden has to survive,
    /// so the view re-renders this text every time the page becomes visible again.</summary>
    public string LogText => _log.ToString();

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        => ConfirmHandler?.Invoke(title, message, confirmLabel) ?? Task.FromResult(false);

    /// <summary>Save / discard / cancel, PR-style. True saves, false discards, null cancels.</summary>
    public Task<bool?> ConfirmUnsavedChangesAsync(string context)
        => SaveConfirmationHandler?.Invoke(
            "有未保存的更改",
            $"{context}之前要保存对「{CurrentProject?.Name}」的更改吗？",
            "保存",
            "不保存")
        ?? Task.FromResult<bool?>(false);

    public void MarkWindowLoaded()
    {
        _isWindowLoaded = true;
        if (_presetLoadWarning is not null)
        {
            ShowToast("预设加载失败", _presetLoadWarning, ToastKind.Warning);
        }

        LivePreview.OnWindowLoaded();
        Home.RefreshRecentProjects();
    }

    /// <summary>Back home leaves the open project behind (the sidebar hides), so it belongs
    /// to the same unsaved-changes family as opening another project or closing the window.</summary>
    private async Task BackHomeAsync()
    {
        if (!await EnsureNoUnsavedChangesAsync("返回主页"))
        {
            return;
        }

        NavigateTo(Workspace.Home);
    }

    public void NavigateTo(Workspace workspace)
    {
        SelectWorkspace(workspace);
        switch (workspace)
        {
            case Workspace.Home:
                Home.RefreshRecentProjects();
                break;
            case Workspace.Preview:
                LivePreview.Schedule();
                break;
            case Workspace.Burn:
                Burn.UpdateSourceSummary();
                if (Burn.Devices is null || Burn.Devices.Count == 0)
                {
                    _ = Burn.RefreshDevicesAsync();
                }

                break;
            case Workspace.Calibration:
                Calibration.EnsureTarget();
                break;
        }
    }

    /// <summary>Plain workspace switch without navigation side effects (job flow, snapshots).</summary>
    public void SelectWorkspace(Workspace workspace) => CurrentWorkspace = workspace;

    // ---- Project lifecycle ----

    /// <summary>Opens the project dialog (create mode) and, on confirm, saves and opens the
    /// new project. Optionally preselects a family/template from a home tile.</summary>
    private async Task RequestNewProjectAsync(OpticalDiscFamily? preselectFamily, string? presetId)
    {
        if (_isBusy)
        {
            ShowToast("操作未执行", "已有任务正在运行。", ToastKind.Info);
            return;
        }

        if (!await EnsureNoUnsavedChangesAsync("新建工程"))
        {
            return;
        }

        ProjectDialogResult? result = ShowProjectDialogHandler?.Invoke(
            new ProjectDialogRequest(EditMode: false, preselectFamily, presetId));
        if (result is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        string family = result.Family switch
        {
            OpticalDiscFamily.Dvd => ProjectDocument.DvdFamily,
            OpticalDiscFamily.BluRay => ProjectDocument.BluRayFamily,
            _ => ProjectDocument.CdFamily,
        };
        bool compactDisc = result.Family == OpticalDiscFamily.CompactDisc;
        bool bluRay = result.Family == OpticalDiscFamily.BluRay;
        ProjectDocument document = new(
            ProjectJsonStore.CurrentSchemaVersion,
            result.Name,
            family,
            new ProjectGeometry(
                result.InnerRadiusMm,
                result.OuterRadiusMm,
                result.Sectors,
                result.PitchLinear,
                result.PitchQuadratic,
                result.PitchCubic),
            [],
            new ProjectOutputs(
                Path.Combine(
                    Path.GetDirectoryName(result.SavePath) ?? string.Empty,
                    result.Name + (compactDisc ? ".wav" : ".iso")),
                WriteCue: compactDisc,
                DataDirectory: string.Empty,
                VolumeLabel: bluRay ? "BD_IMAGE" : "DISC_IMAGE",
                Interleave: compactDisc),
            CalibrationSessionPath: null,
            ThumbnailPngBase64: null,
            now,
            now,
            Tone: ToneProfile.Photo,
            BluRay: bluRay ? new ProjectBluRaySettings(result.BluRayInvertMapping) : null);
        try
        {
            ProjectJsonStore.Save(result.SavePath, document);
        }
        catch (Exception exception)
        {
            ShowToast("无法创建工程", exception.Message, ToastKind.Error);
            return;
        }

        CloseCurrentProject();
        OpenDocument(result.SavePath, document);
        ShowToast("工程已创建", result.Name, ToastKind.Success);
    }

    /// <summary>Home page template cards: opens the create dialog with a family/template
    /// preselected.</summary>
    internal Task RequestNewProjectFromTemplateAsync(OpticalDiscFamily? preselectFamily, string? presetId)
        => RequestNewProjectAsync(preselectFamily, presetId);

    private async Task OpenProjectAsync()
    {
        if (_isBusy)
        {
            ShowToast("操作未执行", "已有任务正在运行。", ToastKind.Info);
            return;
        }

        string? picked = Dialogs?.PickOpen("打开工程", ProjectJsonStore.FileFilter);
        if (picked is null)
        {
            return;
        }

        await OpenProjectFromAsync(picked);
    }

    /// <summary>Every path that replaces the open project (file picker, recent-project cards)
    /// funnels through here, so the unsaved-changes check has exactly one home.</summary>
    internal async Task OpenProjectFromAsync(string path)
    {
        if (!await EnsureNoUnsavedChangesAsync("打开其他工程"))
        {
            return;
        }

        ProjectDocument document;
        try
        {
            document = ProjectJsonStore.Load(path);
        }
        catch (Exception exception)
        {
            ShowToast("无法打开工程", $"{exception.Message}\n\n文件位置：\n{path}", ToastKind.Error);
            return;
        }

        CloseCurrentProject();
        OpenDocument(path, document);
        await Task.CompletedTask;
    }

    /// <summary>Snapshot/CLI entry: opens a project synchronously, throwing on load errors so
    /// the ui-snapshot run fails loudly instead of rendering the wrong page.</summary>
    internal void OpenProjectForSnapshot(string path)
    {
        ProjectDocument document = ProjectJsonStore.Load(path);
        CloseCurrentProject();
        OpenDocument(path, document);
    }

    private void OpenDocument(string path, ProjectDocument document)
    {
        _isApplyingProject = true;
        try
        {
            CurrentProject = document;
            ProjectFilePath = Path.GetFullPath(path);
            IsProjectOpen = true;
            IsDirty = false;
            Design.SetDiscFamily(document.Family);
            LivePreview.SetDiscType(document.Family, clearResult: true);
            ProjectSession.ApplyToState(document, State);
            Calibration.ClearTargetForProjectChange();
            Design.LoadLayers(document.Layers);
        }
        finally
        {
            _isApplyingProject = false;
        }

        _recentProjects.Record(new RecentProjectEntry(
            document.Name,
            document.DiscFamily,
            ProjectFilePath,
            document.ThumbnailPngBase64,
            DateTimeOffset.Now));
        Home.RefreshRecentProjects();
        NavigateTo(Workspace.Design);
    }

    private async Task EditProjectAsync()
    {
        if (!IsProjectOpen)
        {
            return;
        }

        ProjectDialogResult? result = ShowProjectDialogHandler?.Invoke(
            new ProjectDialogRequest(EditMode: true, PreselectFamily: CurrentProject!.Family));
        if (result is null)
        {
            return;
        }

        _isApplyingProject = true;
        try
        {
            // Geometry writes flow through State so every page (and the dirty marker) sees
            // them; the name lives only in the document.
            switch (CurrentProject!.Family)
            {
                case OpticalDiscFamily.Dvd:
                    State.DvdInnerRadius = ProjectSession.Format(result.InnerRadiusMm);
                    State.DvdOuterRadius = ProjectSession.Format(result.OuterRadiusMm);
                    State.DvdTotalSectors = result.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    State.DvdPitchLinear = ProjectSession.Format(result.PitchLinear);
                    State.DvdPitchQuadratic = ProjectSession.Format(result.PitchQuadratic);
                    State.DvdPitchCubic = ProjectSession.Format(result.PitchCubic);
                    break;
                case OpticalDiscFamily.BluRay:
                    State.BdInnerRadius = ProjectSession.Format(result.InnerRadiusMm);
                    State.BdOuterRadius = ProjectSession.Format(result.OuterRadiusMm);
                    State.BdTotalSectors = result.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    State.CdInnerRadius = ProjectSession.Format(result.InnerRadiusMm);
                    State.CdOuterRadius = ProjectSession.Format(result.OuterRadiusMm);
                    State.CdSectors = result.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }

            CurrentProject = CurrentProject with { Name = result.Name };
        }
        finally
        {
            _isApplyingProject = false;
        }

        OnPropertyChanged(nameof(CurrentProject));
        MarkProjectDirty();
        await Task.CompletedTask;
    }

    public async Task SaveProjectAsync()
    {
        if (!IsProjectOpen || CurrentProject is null)
        {
            return;
        }

        try
        {
            await SaveToAsync(ProjectFilePath!);
        }
        catch (Exception exception)
        {
            ShowToast("无法保存工程", exception.Message, ToastKind.Error);
        }
    }

    private async Task SaveProjectAsAsync()
    {
        if (!IsProjectOpen || CurrentProject is null)
        {
            return;
        }

        string? picked = Dialogs?.PickSave(
            ProjectJsonStore.FileFilter,
            ProjectJsonStore.FileExtension,
            Path.GetFileName(ProjectFilePath!));
        if (picked is null)
        {
            return;
        }

        try
        {
            await SaveToAsync(picked);
        }
        catch (Exception exception)
        {
            ShowToast("无法保存工程", exception.Message, ToastKind.Error);
        }
    }

    private async Task SaveToAsync(string path)
    {
        ProjectDocument current = CurrentProject!;
        ProjectDocument document = current with
        {
            Geometry = ProjectSession.CaptureGeometry(current, State),
            Outputs = ProjectSession.CaptureOutputs(current, State),
            Tone = ProjectSession.CaptureTone(current, State),
            BluRay = ProjectSession.CaptureBluRay(current, State),
            Layers = Design.CaptureLayers(),
            ThumbnailPngBase64 = await Design.RenderThumbnailAsync() ?? current.ThumbnailPngBase64,
            ModifiedUtc = DateTimeOffset.Now,
        };
        ProjectJsonStore.Save(path, document);
        CurrentProject = document;
        ProjectFilePath = Path.GetFullPath(path);
        IsDirty = false;
        _recentProjects.Record(new RecentProjectEntry(
            document.Name,
            document.DiscFamily,
            ProjectFilePath,
            document.ThumbnailPngBase64,
            DateTimeOffset.Now));
        Home.RefreshRecentProjects();
        ProjectSaved?.Invoke();
    }

    /// <summary>True when the caller may proceed (saved, discarded, or nothing unsaved).</summary>
    private async Task<bool> EnsureNoUnsavedChangesAsync(string context)
    {
        if (!IsProjectOpen || !IsDirty)
        {
            return true;
        }

        bool? decision = await ConfirmUnsavedChangesAsync(context);
        if (decision is null)
        {
            return false;
        }

        if (decision is true)
        {
            await SaveProjectAsync();
            return !IsDirty; // a failed save keeps the project open
        }

        return true;
    }

    private void CloseCurrentProject()
    {
        IsProjectOpen = false;
        CurrentProject = null;
        ProjectFilePath = null;
        IsDirty = false;
        Design.ClearLayers();
        Calibration.ClearTargetForProjectChange();
        State.CdImagePath = string.Empty;
        State.DvdImagePath = string.Empty;
        State.BdImagePath = string.Empty;
    }

    private void MarkProjectDirty()
    {
        if (IsProjectOpen && !_isApplyingProject)
        {
            IsDirty = true;
        }
    }

    private void Design_CompositeRendered(string? compositePath)
    {
        if (!IsProjectOpen)
        {
            return;
        }

        _isWritingComposite = true;
        try
        {
            switch (CurrentProject!.Family)
            {
                case OpticalDiscFamily.Dvd:
                    State.DvdImagePath = compositePath ?? string.Empty;
                    break;
                case OpticalDiscFamily.BluRay:
                    State.BdImagePath = compositePath ?? string.Empty;
                    break;
                default:
                    State.CdImagePath = compositePath ?? string.Empty;
                    break;
            }
        }
        finally
        {
            _isWritingComposite = false;
        }
    }

    // State properties that flow into the project file; only their edits mark it dirty.
    // Read-back paths and preview-only helpers stay out on purpose: generating an artifact
    // must not turn the project into "unsaved".
    private static readonly string[] ProjectDirtyProperties =
    [
        nameof(DiscParametersState.CdInnerRadius),
        nameof(DiscParametersState.CdOuterRadius),
        nameof(DiscParametersState.CdSectors),
        nameof(DiscParametersState.CdImagePath),
        nameof(DiscParametersState.CdOutputPath),
        nameof(DiscParametersState.CdWriteCue),
        nameof(DiscParametersState.CdInterleave),
        nameof(DiscParametersState.DvdInnerRadius),
        nameof(DiscParametersState.DvdOuterRadius),
        nameof(DiscParametersState.DvdTotalSectors),
        nameof(DiscParametersState.DvdPitchLinear),
        nameof(DiscParametersState.DvdPitchQuadratic),
        nameof(DiscParametersState.DvdPitchCubic),
        nameof(DiscParametersState.DvdImagePath),
        nameof(DiscParametersState.DvdOutputPath),
        nameof(DiscParametersState.DvdDataDirectory),
        nameof(DiscParametersState.DvdFilesAtOuterRing),
        nameof(DiscParametersState.DvdVolumeLabel),
        nameof(DiscParametersState.BdInnerRadius),
        nameof(DiscParametersState.BdOuterRadius),
        nameof(DiscParametersState.BdTotalSectors),
        nameof(DiscParametersState.BdImagePath),
        nameof(DiscParametersState.BdOutputPath),
        nameof(DiscParametersState.BdDataDirectory),
        nameof(DiscParametersState.BdFilesAtOuterRing),
        nameof(DiscParametersState.BdVolumeLabel),
        nameof(DiscParametersState.BdInvertMapping),
        nameof(DiscParametersState.ToneDitherStrength),
        nameof(DiscParametersState.ToneLevels),
        nameof(DiscParametersState.ToneBrightness),
        nameof(DiscParametersState.ToneSrgbLinearization),
    ];

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Any parameter edit refreshes the burn summary and schedules the debounced live
        // preview; both were driven by TextChanged/SelectionChanged before the merge.
        Burn?.UpdateSourceSummary();
        LivePreview?.Schedule();
        if (e.PropertyName is nameof(DiscParametersState.CdInnerRadius)
            or nameof(DiscParametersState.CdOuterRadius)
            or nameof(DiscParametersState.DvdInnerRadius)
            or nameof(DiscParametersState.DvdOuterRadius)
            or nameof(DiscParametersState.BdInnerRadius)
            or nameof(DiscParametersState.BdOuterRadius))
        {
            Design?.RefreshGeometry();
        }

        if (IsProjectOpen
            && !_isApplyingProject
            && !_isWritingComposite
            && Array.IndexOf(ProjectDirtyProperties, e.PropertyName) >= 0)
        {
            IsDirty = true;
        }
    }

    /// <summary>Window close with unsaved changes: resolves through the save modal and may
    /// veto the close.</summary>
    public async Task HandleWindowClosingAsync(CancelEventArgs args)
    {
        if (!IsProjectOpen || !IsDirty)
        {
            return;
        }

        bool? decision = await ConfirmUnsavedChangesAsync("关闭");
        if (decision is null)
        {
            args.Cancel = true;
            return;
        }

        if (decision is true)
        {
            await SaveProjectAsync();
            args.Cancel = IsDirty; // a failed save vetoes the close so nothing is lost
        }
    }

    public void AppendLog(string text)
    {
        _log.Append(text);
        HasLogContent = true;
        LogAppended?.Invoke(text);
    }

    internal void RegisterOutput(string path)
    {
        _lastOutputPath = Path.GetFullPath(path);
        OpenOutputEnabled = true;
        OpenOutputCommand.NotifyCanExecuteChanged();
        JobHasResult = true;
        CanPreviewLastOutput = IsDiscArtifact(_lastOutputPath);
        JobStatusText = "完成：" + Path.GetFileName(_lastOutputPath);
    }

    public void ClearLog()
    {
        _log.Clear();
        HasLogContent = false;
        LogCleared?.Invoke();
    }

    public void ShowToast(string title, string message, ToastKind kind)
        => ShowToast(title, message, kind, kind is ToastKind.Error or ToastKind.Warning
            ? TimeSpan.FromSeconds(6)
            : TimeSpan.FromSeconds(2));

    public void ShowToast(string title, string message, ToastKind kind, TimeSpan timeout)
    {
        ToastItem toast = new(_dispatcher, title, message, kind, timeout);
        // The toast host plays the exit animation on Closed; the drop from the collection
        // waits just past that animation so the card leaves visibly instead of vanishing.
        toast.Closed += async (_, _) =>
        {
            await Task.Delay(220);
            Toasts.Remove(toast);
        };
        Toasts.Add(toast);
        toast.BeginLifetime();
    }

    public void ShowValidationError(Exception exception)
        => ShowToast("请检查输入", exception.Message, ToastKind.Warning);

    private void OpenOutput()
    {
        if (string.IsNullOrWhiteSpace(_lastOutputPath))
        {
            return;
        }

        string? directory = Directory.Exists(_lastOutputPath)
            ? _lastOutputPath
            : Path.GetDirectoryName(_lastOutputPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            OpenWithSystemHandler(directory);
        }
    }

    /// <summary>Points the job dock's "open output location" action at a file written outside a
    /// job, such as the live preview's export.</summary>
    internal void SetLastOutputPath(string path)
    {
        _lastOutputPath = Path.GetFullPath(path);
        OpenOutputEnabled = true;
        JobHasResult = true;
        CanPreviewLastOutput = IsDiscArtifact(_lastOutputPath);
    }

    /// <summary>Browse for the generated CD track used by the live read-back preview.</summary>
    internal bool BrowseCdTrack()
    {
        string? picked = Dialogs?.PickOpen("选择生成的 CD 音轨", "WAV 音轨|*.wav|RAW 音轨|*.raw|所有文件|*.*");
        if (picked is null)
        {
            return false;
        }

        State.CdTrackPath = picked;
        return true;
    }

    /// <summary>Browse for the generated DVD ISO used by the live read-back preview.</summary>
    internal bool BrowseDvdIso()
    {
        string? picked = Dialogs?.PickOpen("选择生成的 DVD ISO", "ISO 镜像|*.iso|所有文件|*.*");
        if (picked is null)
        {
            return false;
        }

        State.DvdIsoPath = picked;
        return true;
    }

    /// <summary>Browse for the generated BD ISO used by the live read-back preview.</summary>
    internal bool BrowseBluRayIso()
    {
        string? picked = Dialogs?.PickOpen("选择生成的蓝光 ISO", "ISO 镜像|*.iso|所有文件|*.*");
        if (picked is null)
        {
            return false;
        }

        State.BdIsoPath = picked;
        return true;
    }

    /// <summary>Hands a file or folder to the system default handler (Explorer for folders).
    /// The path travels as the FileName, so no shell command string is ever built.</summary>
    private static void OpenWithSystemHandler(string path)
    {
        using Process process = new();
        process.StartInfo.FileName = path;
        process.StartInfo.UseShellExecute = true;
        process.Start();
    }

    /// Streams a disc job through UnifiedCommandRunner on an STA thread: busy gating, console
    /// redirection and result reporting into the job dock. The dock stays where the user is,
    /// so a job never navigates away from the workspace it was started from.
    internal async Task RunDiscJobAsync(
        string status,
        string[] arguments,
        string outputPath)
    {
        if (_isBusy)
        {
            ShowToast("操作未执行", "已有任务正在运行。", ToastKind.Info);
            return;
        }

        JobTitle = status;
        JobStatusText = "正在运行…";
        JobProgressValue = 0;
        IsJobProgressIndeterminate = true;
        JobHasResult = false;
        bool canCancel = UnifiedCommandRunner.SupportsCancellation(arguments[0]);
        using CancellationTokenSource? cancellation = canCancel ? new CancellationTokenSource() : null;
        _jobCancellation = cancellation;
        OnPropertyChanged(nameof(CancelJobVisibility));
        CancelJobCommand.NotifyCanExecuteChanged();
        SetBusy(true);

        LogAppended?.Invoke($"\n[{DateTime.Now:HH:mm:ss}] {status}\n");
        Progress<DiscJobProgress> progress = new(value =>
        {
            IsJobProgressIndeterminate = false;
            JobProgressValue = Math.Clamp((value.Fraction ?? 0) * 100, 0, 100);
            JobStatusText = value.Message;
        });
        int exitCode = -1;
        bool canceled = false;
        try
        {
            DiscJobResult result = await StaWorker.RunAsync(
                () => UnifiedCommandRunner.RunJob(arguments, progress, cancellation?.Token ?? default));
            AppendLog(result.Summary ?? string.Empty);
            exitCode = result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception exception)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 任务异常：{exception.Message}\n");
        }
        finally
        {
            _jobCancellation = null;
            OnPropertyChanged(nameof(CancelJobVisibility));
            CancelJobCommand.NotifyCanExecuteChanged();
            SetBusy(false);
        }

        if (canceled)
        {
            IsJobProgressIndeterminate = false;
            JobProgressValue = 0;
            JobStatusText = "已取消";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 任务已取消\n");
            ShowToast("任务已取消", "未完成的输出文件可能留在磁盘上。", ToastKind.Info);
        }
        else if (exitCode == 0)
        {
            _lastOutputPath = Path.GetFullPath(outputPath);
            OpenOutputEnabled = true;
            JobProgressValue = 100;
            JobHasResult = true;
            CanPreviewLastOutput = IsDiscArtifact(_lastOutputPath);
            JobStatusText = "完成：" + Path.GetFileName(_lastOutputPath);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 完成：{_lastOutputPath}\n");
        }
        else
        {
            JobStatusText = $"失败：退出码 {exitCode}";
            IsJobDockExpanded = true;
            AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] 任务失败：退出码 {exitCode}，未生成 {Path.GetFullPath(outputPath)}\n");
            ShowToast("任务未完成", "详情见窗口底部的任务日志。", ToastKind.Warning);
        }
    }

    // ---- Job dock reporting for work that does not run through the CLI job runner ----

    /// <summary>Claims the dock for a task the shell starts itself (the streaming burn), so the
    /// running task, its percentage and its outcome appear in the one status surface instead of
    /// a second copy inside a page.</summary>
    internal void BeginDockJob(string title, bool determinate)
    {
        JobTitle = title;
        JobStatusText = "正在运行…";
        JobProgressValue = 0;
        IsJobProgressIndeterminate = !determinate;
        JobHasResult = false;
    }

    internal void ReportDockJob(string status, double percent)
    {
        JobStatusText = status;
        JobProgressValue = Math.Clamp(percent, 0, 100);
    }

    internal void CompleteDockJob(string status)
    {
        JobStatusText = status;
        JobProgressValue = 100;
        JobHasResult = true;
    }

    /// <summary>Ends a dock job without a result: cancelled by the user or failed. A failure
    /// opens the log, because that is where the reason was written.</summary>
    internal void AbortDockJob(string status, bool expandLog)
    {
        JobStatusText = status;
        JobProgressValue = 0;
        JobHasResult = false;
        if (expandLog)
        {
            IsJobDockExpanded = true;
        }
    }

    internal void SetBusy(bool busy)
    {
        if (_isBusy == busy)
        {
            return;
        }

        _isBusy = busy;
        IsJobRunning = busy;
        OnPropertyChanged(nameof(IsBusy));
        // Commands can be absent on a half-initialized shell (the calibration photo tests
        // construct one directly); the null-conditional keeps those paths alive.
        BrowseCdTrackCommand?.NotifyCanExecuteChanged();
        BrowseDvdIsoCommand?.NotifyCanExecuteChanged();
        BrowseBluRayIsoCommand?.NotifyCanExecuteChanged();
        NewProjectCommand?.NotifyCanExecuteChanged();
        OpenProjectCommand?.NotifyCanExecuteChanged();
        EditProjectCommand?.NotifyCanExecuteChanged();
        SaveProjectCommand?.NotifyCanExecuteChanged();
        SaveProjectAsCommand?.NotifyCanExecuteChanged();
        BusyChanged?.Invoke();
    }

    internal static bool IsPngPath(string path)
        => string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>Disc artifacts the read-back preview can open as its input.</summary>
    internal static bool IsDiscArtifact(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".raw" or ".iso";

    /// <summary>Opens the last generated artifact in the read-back preview workspace.</summary>
    private void PreviewLastOutput()
    {
        if (string.IsNullOrWhiteSpace(_lastOutputPath))
        {
            return;
        }

        switch (CurrentProject?.Family)
        {
            case OpticalDiscFamily.Dvd:
                State.DvdIsoPath = _lastOutputPath;
                break;
            case OpticalDiscFamily.BluRay:
                State.BdIsoPath = _lastOutputPath;
                break;
            default:
                State.CdTrackPath = _lastOutputPath;
                break;
        }

        LivePreview.SetPreviewMode(calibration: false, clearResult: true);
        NavigateTo(Workspace.Preview);
    }
}
