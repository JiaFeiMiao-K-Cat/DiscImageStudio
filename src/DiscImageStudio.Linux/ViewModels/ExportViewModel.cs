using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

/// <summary>Export page: renders the design at generation quality and runs the family's
/// engine command — the same command line the Windows export page and the CLI use.</summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private ProjectViewModel? _project;
    private string[]? _lastArguments;

    [ObservableProperty] private string? _lastOutput;
    [ObservableProperty] private string? _lastSummary;
    [ObservableProperty] private string _spaceInfo = string.Empty;
    [ObservableProperty] private bool _spaceWarning;

    public ExportViewModel(MainViewModel main)
    {
        _main = main;
        main.ProjectChanged += OnProjectChanged;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsBusy))
            {
                GenerateCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public ProjectViewModel? Project => _project;

    public bool HasLastOutput => LastOutput is not null;

    public string GenerateText => _project is null ? "Generate"
        : _project.IsCd && !_project.HasFiles ? "Generate audio track" : "Generate disc image";

    public string OutputKind => _project switch
    {
        null => string.Empty,
        { IsCd: true, HasFiles: false } => "CD-DA audio track (.wav with CUE sheet, or legacy big-endian .raw)",
        { IsCd: true } => "CD Mode 1 data image (.iso, ISO 9660/Joliet)",
        { IsDvd: true } => "DVD data image (.iso)" + (_project.HasFiles ? ", ISO 9660 with your files" : ", picture only"),
        _ => "BD-R image (.iso)" + (_project.HasFiles ? ", UDF 2.01 with your files" : ", picture only"),
    };

    public string EstimatedSize
    {
        get
        {
            if (_project is null)
            {
                return string.Empty;
            }

            long bytes = _project.IsCd && !_project.HasFiles
                ? (_project.Sectors * 2352) + 44
                : _project.Sectors * 2048;
            return Num.Bytes(bytes);
        }
    }

    public bool CancelSupported => _project is not null
        && EngineJobRunner.SupportsCancellation(_project.IsDvd ? "solve" : _project.IsBluRay ? "bd-generate" : "cd-generate");

    partial void OnLastOutputChanged(string? value) => OnPropertyChanged(nameof(HasLastOutput));

    public void OnShown() => RefreshSpace();

    private void OnProjectChanged()
    {
        if (_project is not null)
        {
            _project.PropertyChanged -= Project_PropertyChanged;
        }

        _project = _main.Project;
        LastOutput = null;
        LastSummary = null;
        _lastArguments = null;
        if (_project is not null)
        {
            _project.PropertyChanged += Project_PropertyChanged;
        }

        NotifyAll();
    }

    private void Project_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectViewModel.OutputPath) or nameof(ProjectViewModel.DataDirectory)
            or nameof(ProjectViewModel.Sectors) or nameof(ProjectViewModel.HasFiles))
        {
            NotifyAll();
        }
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(GenerateText));
        OnPropertyChanged(nameof(OutputKind));
        OnPropertyChanged(nameof(EstimatedSize));
        OnPropertyChanged(nameof(CancelSupported));
        GenerateCommand.NotifyCanExecuteChanged();
        RefreshSpace();
    }

    private void RefreshSpace()
    {
        SpaceInfo = string.Empty;
        SpaceWarning = false;
        if (_project is null || string.IsNullOrWhiteSpace(_project.OutputPath))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_project.OutputPath));
            while (directory is not null && !Directory.Exists(directory))
            {
                directory = Path.GetDirectoryName(directory);
            }

            if (directory is null)
            {
                return;
            }

            long free = new DriveInfo(directory).AvailableFreeSpace;
            long needed = _project.IsCd && !_project.HasFiles ? _project.Sectors * 2352 : _project.Sectors * 2048;
            SpaceInfo = $"{Num.Bytes(free)} free on the destination";
            SpaceWarning = free < needed;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
    }

    [RelayCommand]
    private async Task BrowseOutput()
    {
        if (_project is null || _main.Ui is null)
        {
            return;
        }

        bool track = _project.IsCd && !_project.HasFiles;
        string current = _project.OutputPath ?? string.Empty;
        string extension = track
            ? (Path.GetExtension(current).Equals(".raw", StringComparison.OrdinalIgnoreCase) ? "raw" : "wav")
            : "iso";
        string suggested = Path.GetFileName(current) is { Length: > 0 } name
            ? name
            : ProjectSetupViewModel.SafeFileName(_project.Name) + "." + extension;
        IReadOnlyList<FilePickerFileType> filters = track ? [FileTypes.Wave, FileTypes.Raw] : [FileTypes.Iso];
        string? path = await _main.Ui.PickSaveFileAsync("Choose where to write the output", suggested, extension, filters,
            Path.GetDirectoryName(current) ?? _project.Folder);
        if (path is null)
        {
            return;
        }

        string chosenExtension = Path.GetExtension(path).ToLowerInvariant();
        if (track && chosenExtension is not (".wav" or ".raw"))
        {
            path += ".wav";
        }
        else if (!track && chosenExtension != ".iso")
        {
            path += ".iso";
        }

        _project.OutputPath = path;
    }

    private bool CanGenerate() => _project is not null && !_main.IsBusy;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task Generate()
    {
        ProjectViewModel? project = _project;
        if (project is null || _main.Ui is null)
        {
            return;
        }

        if (!project.HasVisibleImages)
        {
            _main.ShowToast(ToastKind.Warning, "Nothing to draw yet", "Import an image on the Design page first.");
            return;
        }

        string output = (project.OutputPath ?? string.Empty).Trim();
        if (output.Length == 0)
        {
            await BrowseOutput();
            output = (project.OutputPath ?? string.Empty).Trim();
            if (output.Length == 0)
            {
                return;
            }
        }

        output = AppPaths.ResolveUserPath(output, project.Folder);
        if (File.Exists(output)
            && await _main.Ui.AskAsync("Replace existing file?", $"{AppPaths.ShortenHome(output)} already exists and will be overwritten.",
                "Replace", null, "Cancel", danger: true) != DialogChoice.Primary)
        {
            return;
        }

        if (project.HasFiles && !Directory.Exists(AppPaths.ResolveUserPath(project.DataDirectory, project.Folder)))
        {
            _main.ShowToast(ToastKind.Error, "Files folder not found", project.DataDirectory);
            return;
        }

        DiscSettings settings = project.Snapshot();
        string title = settings.Family switch
        {
            OpticalDiscFamily.Dvd => "Generating DVD image",
            OpticalDiscFamily.BluRay => "Generating BD-R image",
            _ => settings.HasFiles ? "Generating CD data image" : "Generating CD audio track",
        };
        string[]? arguments = null;
        DiscJobResult? result = null;
        bool ok = await _main.RunJobAsync(title, async job =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            job.Progress(null, $"Composing the design at {Compositor.GenerationSize} px…");
            string composite = await project.EnsureExportCompositeAsync(job.Token)
                ?? throw new InvalidOperationException("The design has no visible image layers.");
            arguments = settings.BuildGenerateCommand(composite, output);
            job.Log("$ " + EngineJobRunner.ToShellCommand("disc-image-studio", arguments));
            job.Progress(null, "Encoding…");
            result = await EngineJobRunner.RunAsync(arguments, job.Log,
                progress => job.Progress(progress.Fraction, progress.Fraction is null ? progress.Message : null), job.Token);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Summary ?? $"The engine exited with code {result.ExitCode}.");
            }

            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                job.Log(result.Summary);
            }
        }, cancellable: CancelSupported);

        RefreshSpace();
        if (!ok)
        {
            return;
        }

        _lastArguments = arguments;
        LastOutput = result?.OutputPath ?? output;
        LastSummary = result?.Summary;
        _main.ShowToast(ToastKind.Success, "Output written", AppPaths.ShortenHome(LastOutput));
        _main.Preview.ShowReadbackOf(LastOutput);
        _main.Burn.UseSource(LastOutput);
    }

    [RelayCommand]
    private void RevealOutput()
    {
        if (LastOutput is not null)
        {
            _main.RevealInFileManager(LastOutput);
        }
    }

    [RelayCommand]
    private void ShowReadback()
    {
        if (LastOutput is not null)
        {
            _main.Preview.ShowReadbackOf(LastOutput);
            _main.NavigateCommand.Execute(AppPage.Preview);
        }
    }

    [RelayCommand]
    private void GoBurn()
    {
        if (LastOutput is not null)
        {
            _main.Burn.UseSource(LastOutput);
        }

        _main.NavigateCommand.Execute(AppPage.Burn);
    }

    [RelayCommand]
    private async Task CopyCommandLine()
    {
        if (_lastArguments is null || _main.Ui is null)
        {
            return;
        }

        await _main.Ui.CopyTextAsync(EngineJobRunner.ToShellCommand("disc-image-studio", _lastArguments));
        _main.ShowToast(ToastKind.Info, "Command line copied",
            "It reads the cached composite in ~/.cache/DiscImageStudio, which is cleared when the app restarts.");
    }
}
