using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

/// <summary>Design page: the layer stack on the disc canvas, layer properties, the files
/// that ride along on the disc, and the tone module with its live engine preview.</summary>
public partial class DesignViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private ProjectViewModel? _project;
    private int _bandsGeneration;

    [ObservableProperty] private IReadOnlyList<RadialBand> _fileBands = [];

    public DesignViewModel(MainViewModel main)
    {
        _main = main;
        main.ProjectChanged += OnProjectChanged;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsBusy))
            {
                NotifyCommands();
            }
        };
    }

    public MainViewModel Main => _main;

    public ProjectViewModel? Project => _project;

    public PreviewSlot TonePreview { get; } = new(TimeSpan.FromMilliseconds(450));

    public double DitherMaximum => 100;

    private void OnProjectChanged()
    {
        if (_project is not null)
        {
            _project.PreviewInputsChanged -= ScheduleTonePreview;
            _project.PropertyChanged -= Project_PropertyChanged;
        }

        _project = _main.Project;
        OnPropertyChanged(nameof(Project));
        FileBands = [];
        TonePreview.Clear("Import an image to see how the disc will render it.");
        if (_project is not null)
        {
            _project.PreviewInputsChanged += ScheduleTonePreview;
            _project.PropertyChanged += Project_PropertyChanged;
            ScheduleTonePreview();
            RefreshFileBands();
        }

        NotifyCommands();
    }

    private void Project_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectViewModel.SelectedLayer):
            case nameof(ProjectViewModel.HasLayers):
                NotifyCommands();
                break;
            case nameof(ProjectViewModel.DataDirectory):
            case nameof(ProjectViewModel.FilesAtOuterRing):
            case nameof(ProjectViewModel.InnerRadiusMm):
            case nameof(ProjectViewModel.OuterRadiusMm):
            case nameof(ProjectViewModel.Sectors):
                RefreshFileBands();
                break;
        }
    }

    private void NotifyCommands()
    {
        ImportImagesCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        CenterSelectedCommand.NotifyCanExecuteChanged();
        ResetSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection => _project?.SelectedLayer is not null;

    private bool CanImport => _project is not null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    public async Task ImportImages()
    {
        if (_project is null || _main.Ui is null)
        {
            return;
        }

        IReadOnlyList<string> files = await _main.Ui.PickFilesAsync("Import images",
            [FileTypes.Images, FileTypes.All], _main.Settings.LastImageDirectory ?? AppPaths.HomeDirectory, allowMultiple: true);
        ImportFiles(files);
    }

    /// <summary>Adds image layers from the picker or from files dropped on the canvas.</summary>
    public void ImportFiles(IEnumerable<string> files)
    {
        if (_project is null)
        {
            return;
        }

        int failed = 0;
        foreach (string file in files)
        {
            if (Compositor.ProbeSize(file) is null)
            {
                failed++;
                continue;
            }

            _project.AddImage(file);
            _main.Settings.LastImageDirectory = Path.GetDirectoryName(file);
        }

        SettingsStore.Save(_main.Settings);
        if (failed > 0)
        {
            _main.ShowToast(ToastKind.Warning, $"{failed} file{(failed == 1 ? "" : "s")} could not be read as an image",
                "Supported formats: PNG, JPEG, BMP, GIF and WebP.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (_project?.SelectedLayer is { } layer)
        {
            _project.RemoveLayer(layer);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp()
    {
        if (_project?.SelectedLayer is { } layer)
        {
            _project.MoveLayer(layer, -1);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown()
    {
        if (_project?.SelectedLayer is { } layer)
        {
            _project.MoveLayer(layer, +1);
        }
    }

    [RelayCommand]
    private static void ToggleVisibility(LayerViewModel? layer)
    {
        if (layer is not null)
        {
            layer.IsVisible = !layer.IsVisible;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CenterSelected()
    {
        if (_project?.SelectedLayer is { } layer)
        {
            layer.TranslateXMm = 0;
            layer.TranslateYMm = 0;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ResetSelected()
    {
        if (_project?.SelectedLayer is { } layer)
        {
            layer.TranslateXMm = 0;
            layer.TranslateYMm = 0;
            layer.RotationDeg = 0;
            layer.Scale = 1;
        }
    }

    [RelayCommand]
    private async Task BrowseData()
    {
        if (_project is null || _main.Ui is null)
        {
            return;
        }

        string? folder = await _main.Ui.PickFolderAsync("Choose the folder to put on the disc",
            _project.HasFiles ? _project.DataDirectory : _project.Folder);
        if (folder is not null)
        {
            _project.DataDirectory = folder;
        }
    }

    [RelayCommand]
    private void ClearData()
    {
        if (_project is not null)
        {
            _project.DataDirectory = string.Empty;
        }
    }

    [RelayCommand]
    private void PhotoTone() => _project?.ResetTone(ToneProfile.Photo);

    [RelayCommand]
    private void HardThresholdTone() => _project?.ResetTone(ToneProfile.LegacyBinary);

    [RelayCommand]
    private void RefreshTonePreview()
    {
        if (_project is not null)
        {
            _ = TonePreview.RenderNowAsync(token => RenderTone(_project, token));
        }
    }

    private void ScheduleTonePreview()
    {
        ProjectViewModel? project = _project;
        if (project is null)
        {
            return;
        }

        if (!project.HasVisibleImages)
        {
            TonePreview.Clear("Import an image to see how the disc will render it.");
            return;
        }

        TonePreview.Schedule(token => RenderTone(project, token));
    }

    private static async Task<PreviewInfo?> RenderTone(ProjectViewModel project, CancellationToken token)
    {
        DiscSettings settings = project.Snapshot();
        string? composite = await project.EnsurePreviewCompositeAsync(token);
        if (composite is null)
        {
            return null;
        }

        string output = AppPaths.NewWorkFile("tone", ".png");
        (int size, int samples) = settings.Family switch
        {
            OpticalDiscFamily.Dvd => (440, 8),
            OpticalDiscFamily.BluRay => (512, 0),
            _ => (440, 32),
        };
        return await Task.Run(() => PreviewRenderer.RenderProjection(settings, composite, output, size, samples, null, null, token), token);
    }

    private void RefreshFileBands()
    {
        int generation = ++_bandsGeneration;
        if (_project is not { HasFiles: true } project)
        {
            FileBands = [];
            return;
        }

        DiscSettings settings = project.Snapshot();
        _ = Task.Run(() =>
        {
            IReadOnlyList<RadialBand> bands = PreviewRenderer.FileBands(settings);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _bandsGeneration)
                {
                    FileBands = bands;
                }
            });
        });
    }
}
