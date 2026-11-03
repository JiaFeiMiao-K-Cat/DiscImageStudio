using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

public enum AppPage
{
    Home,
    Design,
    Preview,
    Export,
    Burn,
    Calibrate,
}

/// <summary>Handle a background job uses to report back to the status bar.</summary>
public sealed class JobContext(MainViewModel owner, CancellationToken token)
{
    public CancellationToken Token { get; } = token;

    public void Log(string line) => owner.AppendLog(line);

    public void Progress(double? fraction, string? status = null) => owner.ReportProgress(fraction, status);
}

/// <summary>Application shell: the open project, page navigation, the single background job
/// slot with its progress, log and cancellation, and notifications.</summary>
public partial class MainViewModel : ObservableObject
{
    private const int MaxLogCharacters = 400_000;
    private readonly StringBuilder _log = new();
    private readonly object _logGate = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _jobClock = new();
    private CancellationTokenSource? _jobCancellation;
    private bool _logDirty;
    private long _lastProgressTicks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(SaveProjectCommand), nameof(SaveProjectAsCommand), nameof(EditProjectCommand), nameof(CloseProjectCommand))]
    private ProjectViewModel? _project;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomePage), nameof(IsDesignPage), nameof(IsPreviewPage), nameof(IsExportPage), nameof(IsBurnPage), nameof(IsCalibratePage),
        nameof(IsProjectPage), nameof(PageTitle), nameof(PageSubtitle), nameof(NextPageText), nameof(HasNextPage))]
    private AppPage _currentPage = AppPage.Home;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _jobTitle = "Ready";
    [ObservableProperty] private string _jobStatus = "No job running.";
    [ObservableProperty] private double _jobProgress;
    [ObservableProperty] private bool _jobIndeterminate;
    [ObservableProperty] private bool _canCancelJob;
    [ObservableProperty] private string _jobElapsed = string.Empty;
    [ObservableProperty] private bool _jobFailed;
    [ObservableProperty] private bool _jobSucceeded;
    [ObservableProperty] private bool _isLogOpen;
    [ObservableProperty] private string _logText = string.Empty;

    public MainViewModel(LinuxSettings settings)
    {
        Settings = settings;
        Home = new HomeViewModel(this);
        Design = new DesignViewModel(this);
        Preview = new PreviewViewModel(this);
        Export = new ExportViewModel(this);
        Burn = new BurnViewModel(this);
        Calibration = new CalibrationViewModel(this);
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => FlushLog());
        _flushTimer.Start();
        _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => JobElapsed = Num.Duration(_jobClock.Elapsed));
        if (PresetCatalog.LoadWarning is { } warning)
        {
            AppendLog("warning: " + warning);
        }
    }

    public LinuxSettings Settings { get; }

    public IUiServices? Ui { get; set; }

    public HomeViewModel Home { get; }

    public DesignViewModel Design { get; }

    public PreviewViewModel Preview { get; }

    public ExportViewModel Export { get; }

    public BurnViewModel Burn { get; }

    public CalibrationViewModel Calibration { get; }

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public bool HasProject => Project is not null;

    public bool IsHomePage => CurrentPage == AppPage.Home;

    public bool IsDesignPage => CurrentPage == AppPage.Design;

    public bool IsPreviewPage => CurrentPage == AppPage.Preview;

    public bool IsExportPage => CurrentPage == AppPage.Export;

    public bool IsBurnPage => CurrentPage == AppPage.Burn;

    public bool IsCalibratePage => CurrentPage == AppPage.Calibrate;

    public bool IsProjectPage => CurrentPage != AppPage.Home;

    public string PageTitle => CurrentPage switch
    {
        AppPage.Design => "Design",
        AppPage.Preview => "Preview",
        AppPage.Export => "Export",
        AppPage.Burn => "Burn",
        AppPage.Calibrate => "Calibrate",
        _ => "Home",
    };

    public string PageSubtitle => CurrentPage switch
    {
        AppPage.Design => "Place images on the disc and tune how grey tones become recordable patterns.",
        AppPage.Preview => "Check the picture through the engine before spending a disc.",
        AppPage.Export => "Generate the audio track or disc image to burn.",
        AppPage.Burn => "Write the generated file to a blank disc with a Linux recording tool.",
        AppPage.Calibrate => "Measure your drive's real disc geometry from a photo of a burned test disc.",
        _ => string.Empty,
    };

    public bool HasNextPage => CurrentPage is AppPage.Design or AppPage.Preview or AppPage.Export;

    public string NextPageText => CurrentPage switch
    {
        AppPage.Design => "Next: Preview",
        AppPage.Preview => "Next: Export",
        AppPage.Export => "Next: Burn",
        _ => string.Empty,
    };

    [RelayCommand]
    private void GoNext()
    {
        if (HasNextPage)
        {
            Navigate(CurrentPage + 1);
        }
    }

    public string WindowTitle => Project is null ? "Disc Image Studio" : $"{Project.DisplayTitle} — Disc Image Studio";

    public static string Version { get; } =
        typeof(MainViewModel).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "dev";

    /// <summary>Raised after a project is opened or closed so pages can reset.</summary>
    public event Action? ProjectChanged;

    partial void OnProjectChanged(ProjectViewModel? oldValue, ProjectViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= Project_PropertyChanged;
            oldValue.ReleaseCaches();
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += Project_PropertyChanged;
        }

        ProjectChanged?.Invoke();
    }

    private void Project_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectViewModel.DisplayTitle))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    [RelayCommand]
    private void Navigate(AppPage page)
    {
        if (page != AppPage.Home && Project is null)
        {
            return;
        }

        CurrentPage = page;
        if (page == AppPage.Preview)
        {
            Preview.OnShown();
        }
        else if (page == AppPage.Burn)
        {
            Burn.OnShown();
        }
        else if (page == AppPage.Export)
        {
            Export.OnShown();
        }
        else if (page == AppPage.Calibrate)
        {
            Calibration.OnShown();
        }
    }

    [RelayCommand]
    public async Task NewProject() => await CreateProjectAsync(null);

    public async Task CreateProjectAsync(DiscPreset? preset)
    {
        if (Ui is null || !await ConfirmDiscardChangesAsync())
        {
            return;
        }

        string folder = Settings.ProjectsDirectory is { Length: > 0 } saved && Directory.Exists(saved)
            ? saved
            : AppPaths.DefaultProjectsDirectory;
        ProjectSetupViewModel setup = new(isEditMode: false, folder);
        if (preset is not null)
        {
            setup.SelectPreset(preset);
        }

        if (await Ui.ShowProjectSetupAsync(setup) is not { Accepted: true } result)
        {
            return;
        }

        try
        {
            string path = result.ProjectFilePath;
            if (File.Exists(path)
                && await Ui.AskAsync("Replace project?", $"{AppPaths.ShortenHome(path)} already exists. Replace it with a new, empty project?",
                    "Replace", null, "Cancel", danger: true) != DialogChoice.Primary)
            {
                return;
            }

            Directory.CreateDirectory(result.Folder);
            string extension = result.Family == OpticalDiscFamily.CompactDisc ? ".wav" : ".iso";
            string output = Path.Combine(result.Folder, ProjectSetupViewModel.SafeFileName(result.Name) + extension);
            ProjectDocument document = ProjectViewModel.CreateDocument(result.Name.Trim(), result.Family,
                result.InnerRadiusMm, result.OuterRadiusMm, result.Sectors,
                result.PitchLinear, result.PitchQuadratic, result.PitchCubic, result.InvertMapping, output);
            ProjectJsonStore.Save(path, document);
            Settings.ProjectsDirectory = result.Folder;
            await OpenProjectFileAsync(path, skipConfirm: true);
            Navigate(AppPage.Design);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            ShowError("Cannot create the project", exception);
        }
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        if (Ui is null)
        {
            return;
        }

        string? start = Project?.Folder ?? Settings.ProjectsDirectory;
        IReadOnlyList<string> files = await Ui.PickFilesAsync("Open project", [FileTypes.Project, FileTypes.All], start, allowMultiple: false);
        if (files.Count == 1)
        {
            await OpenProjectFileAsync(files[0]);
        }
    }

    public async Task OpenProjectFileAsync(string path, bool skipConfirm = false)
    {
        if (!skipConfirm && !await ConfirmDiscardChangesAsync())
        {
            return;
        }

        try
        {
            string full = Path.GetFullPath(path);
            ProjectDocument document = await Task.Run(() => ProjectJsonStore.Load(full));
            ProjectViewModel project = new(document, full);
            int missing = 0;
            await Task.Run(() =>
            {
                foreach (LayerViewModel layer in project.Layers)
                {
                    if (!layer.LoadImage(project.OuterRadiusMm))
                    {
                        missing++;
                    }
                }
            });
            project.WithoutDirty(() => project.SelectedLayer = project.Layers.FirstOrDefault());
            Project = project;
            Settings.TouchRecent(full);
            SettingsStore.Save(Settings);
            Home.RefreshRecent();
            if (CurrentPage == AppPage.Home)
            {
                Navigate(AppPage.Design);
            }

            if (missing > 0)
            {
                ShowToast(ToastKind.Warning, $"{missing} layer image{(missing == 1 ? " is" : "s are")} missing",
                    "The layers are kept; relink by importing the image again.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {
            ShowError("Cannot open the project", exception);
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    public async Task<bool> SaveProject()
    {
        if (Project is null)
        {
            return false;
        }

        if (Project.FilePath is null)
        {
            return await SaveProjectAsCore();
        }

        return SaveTo(Project.FilePath);
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task SaveProjectAs() => await SaveProjectAsCore();

    private async Task<bool> SaveProjectAsCore()
    {
        if (Project is null || Ui is null)
        {
            return false;
        }

        string? path = await Ui.PickSaveFileAsync("Save project as", ProjectSetupViewModel.SafeFileName(Project.Name) + ".discproj",
            "discproj", [FileTypes.Project], Project.Folder ?? Settings.ProjectsDirectory);
        if (path is null)
        {
            return false;
        }

        if (!path.EndsWith(ProjectJsonStore.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            path += ProjectJsonStore.FileExtension;
        }

        return SaveTo(path);
    }

    private bool SaveTo(string path)
    {
        if (Project is null)
        {
            return false;
        }

        try
        {
            Project.Save(path);
            Settings.TouchRecent(path);
            SettingsStore.Save(Settings);
            Home.RefreshRecent();
            ShowToast(ToastKind.Success, "Project saved", AppPaths.ShortenHome(path));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            ShowError("Cannot save the project", exception);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task EditProject()
    {
        if (Project is null || Ui is null)
        {
            return;
        }

        ProjectSetupViewModel setup = ProjectSetupViewModel.ForProject(Project);
        if (await Ui.ShowProjectSetupAsync(setup) is { Accepted: true } result)
        {
            Project.ApplyGeometry(result.Name.Trim(), result.InnerRadiusMm, result.OuterRadiusMm, result.Sectors,
                result.PitchLinear, result.PitchQuadratic, result.PitchCubic, result.InvertMapping);
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task CloseProject()
    {
        if (await ConfirmDiscardChangesAsync())
        {
            Project = null;
            CurrentPage = AppPage.Home;
        }
    }

    /// <summary>True when it is fine to drop the current project (saved, discarded or clean).</summary>
    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (Project is not { IsDirty: true } || Ui is null)
        {
            return true;
        }

        DialogChoice choice = await Ui.AskAsync("Save changes?",
            $"“{Project.Name}” has unsaved changes. Save them before continuing?", "Save", "Don't save", "Cancel");
        return choice switch
        {
            DialogChoice.Primary => await SaveProject(),
            DialogChoice.Secondary => true,
            _ => false,
        };
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        if (Ui is not null)
        {
            await Ui.ShowSettingsAsync();
        }
    }

    [RelayCommand]
    private async Task OpenAbout()
    {
        if (Ui is not null)
        {
            await Ui.ShowAboutAsync();
        }
    }

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand]
    private void ClearLog()
    {
        lock (_logGate)
        {
            _log.Clear();
            _logDirty = true;
        }
    }

    [RelayCommand]
    private async Task CopyLog()
    {
        if (Ui is not null)
        {
            FlushLog();
            await Ui.CopyTextAsync(LogText);
            ShowToast(ToastKind.Info, "Log copied to the clipboard", null);
        }
    }

    [RelayCommand]
    private void CancelJob()
    {
        if (_jobCancellation is { IsCancellationRequested: false } cancellation)
        {
            cancellation.Cancel();
            JobStatus = "Cancelling…";
            AppendLog("Cancellation requested.");
        }
    }

    [RelayCommand]
    private void DismissToast(ToastViewModel? toast)
    {
        if (toast is not null)
        {
            Toasts.Remove(toast);
        }
    }

    /// <summary>Runs work in the single job slot. Returns false when another job is running,
    /// the work failed or it was cancelled; failures are logged and shown.</summary>
    public async Task<bool> RunJobAsync(string title, Func<JobContext, Task> work, bool cancellable)
    {
        if (IsBusy)
        {
            ShowToast(ToastKind.Warning, "A job is already running", "Wait for it to finish or cancel it first.");
            return false;
        }

        using CancellationTokenSource cancellation = new();
        _jobCancellation = cancellation;
        IsBusy = true;
        JobFailed = JobSucceeded = false;
        JobTitle = title;
        JobStatus = "Starting…";
        JobProgress = 0;
        JobIndeterminate = true;
        CanCancelJob = cancellable;
        _jobClock.Restart();
        JobElapsed = "0:00";
        _elapsedTimer.Start();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] {title}");
        try
        {
            await work(new JobContext(this, cancellation.Token));
            JobSucceeded = true;
            JobProgress = 100;
            JobIndeterminate = false;
            JobStatus = $"Finished in {Num.Duration(_jobClock.Elapsed)}.";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Done in {Num.Duration(_jobClock.Elapsed)}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            JobStatus = "Cancelled.";
            JobIndeterminate = false;
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Cancelled.");
            ShowToast(ToastKind.Warning, $"{title} cancelled", "A partially written output may remain on disk.");
            return false;
        }
        catch (Exception exception)
        {
            JobFailed = true;
            JobIndeterminate = false;
            string message = Translator.T(exception.Message);
            JobStatus = message;
            AppendLog($"[{DateTime.Now:HH:mm:ss}] error: {message}");
            ShowToast(ToastKind.Error, $"{title} failed", message);
            return false;
        }
        finally
        {
            _jobClock.Stop();
            _elapsedTimer.Stop();
            JobElapsed = Num.Duration(_jobClock.Elapsed);
            IsBusy = false;
            CanCancelJob = false;
            _jobCancellation = null;
            JobTitle = JobFailed ? "Failed" : JobSucceeded ? "Done" : "Stopped";
        }
    }

    internal void ReportProgress(double? fraction, string? status)
    {
        long now = Environment.TickCount64;
        bool final = fraction is >= 1;
        if (!final && now - Interlocked.Read(ref _lastProgressTicks) < 100)
        {
            return;
        }

        Interlocked.Exchange(ref _lastProgressTicks, now);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsBusy)
            {
                return;
            }

            if (fraction is { } value && double.IsFinite(value))
            {
                JobIndeterminate = false;
                JobProgress = Math.Clamp(value, 0, 1) * 100;
                JobStatus = status ?? $"{JobProgress:0.0}%";
                if (value > 0.01 && value < 1)
                {
                    TimeSpan elapsed = _jobClock.Elapsed;
                    TimeSpan remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - value) / value);
                    JobStatus += $" · about {Num.Duration(remaining)} left";
                }
            }
            else if (status is not null)
            {
                JobStatus = status;
            }
        });
    }

    public void AppendLog(string line)
    {
        lock (_logGate)
        {
            _log.Append(line).Append('\n');
            if (_log.Length > MaxLogCharacters)
            {
                int cut = _log.Length - (MaxLogCharacters * 3 / 4);
                int newline = _log.ToString(cut, Math.Min(4096, _log.Length - cut)).IndexOf('\n');
                _log.Remove(0, cut + Math.Max(0, newline + 1));
            }

            _logDirty = true;
        }
    }

    private void FlushLog()
    {
        string? text = null;
        lock (_logGate)
        {
            if (_logDirty)
            {
                text = _log.ToString();
                _logDirty = false;
            }
        }

        if (text is not null)
        {
            LogText = text;
        }
    }

    public void ShowToast(ToastKind kind, string title, string? message)
    {
        void Add()
        {
            ToastViewModel toast = new(kind, title, message);
            Toasts.Add(toast);
            while (Toasts.Count > 4)
            {
                Toasts.RemoveAt(0);
            }

            TimeSpan lifetime = kind is ToastKind.Error ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(6);
            DispatcherTimer.RunOnce(() => Toasts.Remove(toast), lifetime);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Add();
        }
        else
        {
            Dispatcher.UIThread.Post(Add);
        }
    }

    public void ShowError(string title, Exception exception)
    {
        string message = Translator.T(exception.Message);
        AppendLog($"error: {title}: {message}");
        ShowToast(ToastKind.Error, title, message);
    }

    /// <summary>Opens a folder (or the folder containing a file) in the desktop's file manager.</summary>
    public void RevealInFileManager(string path)
    {
        string target = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", [target]) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowToast(ToastKind.Warning, "Cannot open the file manager", "xdg-open is not available: " + target);
        }
    }
}
