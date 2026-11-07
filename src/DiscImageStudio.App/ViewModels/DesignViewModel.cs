using DiscImageStudio.Localization;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Cd;
using DiscImageStudio.Dvd;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>Design page state: the layer stack, the selection, the geometry snapshot the
/// canvas draws with, and the composite pipeline that turns layers into the source picture
/// the export/burn/preview pages consume. The composite renders 450 ms after the last edit
/// (same debounce as the live preview) at preview quality; export quality renders on demand.</summary>
public partial class DesignViewModel : ObservableObject
{
    public const double DiscRadiusMm = 60.0;
    public const double HubRadiusMm = 15.0;
    private const int PreviewCompositeSize = 2048;
    private static readonly TimeSpan CompositeDebounce = TimeSpan.FromMilliseconds(450);

    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly DispatcherTimer _compositeTimer;
    private long _compositeRevision;
    private long _previewRenderedRevision = -1;
    private long _exportRenderedRevision = -1;
    private bool _suppressLayerEvents;

    public DesignViewModel(ShellViewModel shell, DiscParametersState state, Dispatcher dispatcher)
    {
        _shell = shell;
        _state = state;
        _compositeTimer = new DispatcherTimer { Interval = CompositeDebounce };
        _compositeTimer.Tick += (_, _) =>
        {
            _compositeTimer.Stop();
            _ = RenderPreviewCompositeAsync();
        };
        ImportImagesCommand = new RelayCommand(ImportImages, () => !_shell.IsBusy);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => SelectedLayer is not null && !_shell.IsBusy);
        MoveSelectedUpCommand = new RelayCommand(MoveSelectedUp, () => CanMoveSelectedUp && !_shell.IsBusy);
        MoveSelectedDownCommand = new RelayCommand(MoveSelectedDown, () => CanMoveSelectedDown && !_shell.IsBusy);
        ToggleVisibilityCommand = new RelayCommand<DesignLayerViewModel>(layer =>
        {
            if (layer is not null)
            {
                layer.IsVisible = !layer.IsVisible;
            }
        });
        Layers.CollectionChanged += Layers_CollectionChanged;
        Tone = new ToneViewModel(shell, state, this);
    }

    /// <summary>The right rail's tone module: its preview projects the same composite this
    /// page renders through the engines' tone mapping.</summary>
    public ToneViewModel Tone { get; }

    /// <summary>The design page itself builds its text from XAML; only the tone module caches
    /// a status line.</summary>
    internal void Relocalize() => Tone.Relocalize();

    /// <summary>Disc files are shared with export, preview and burn; they are not image layers.</summary>
    public DiscParametersState State => _state;

    public RelayCommand BrowseDataCommand => _shell.Export.BrowseDataCommand;

    public ObservableCollection<DesignLayerViewModel> Layers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanMoveSelectedUp), nameof(CanMoveSelectedDown), nameof(HasSelectionVisibility))]
    private DesignLayerViewModel? _selectedLayer;

    public bool HasLayers => Layers.Count > 0;

    /// <summary>Bumped on every design edit; lets callers (the burn confirmation) notice that
    /// the composed pixels changed even when the file path did not.</summary>
    public long CompositeRevision => _compositeRevision;

    /// <summary>Workspace navigation for the page header's "下一步" action.</summary>
    public IRelayCommand<Workspace> NavigateCommand => _shell.NavigateCommand;

    public bool HasSelection => SelectedLayer is not null;

    public System.Windows.Visibility HasSelectionVisibility => HasSelection
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    private bool CanMoveSelectedUp => SelectedLayer is not null && Layers.IndexOf(SelectedLayer) > 0;

    private bool CanMoveSelectedDown => SelectedLayer is not null
        && Layers.IndexOf(SelectedLayer) < Layers.Count - 1;

    public RelayCommand ImportImagesCommand { get; }

    public RelayCommand RemoveSelectedCommand { get; }

    public RelayCommand MoveSelectedUpCommand { get; }

    public RelayCommand MoveSelectedDownCommand { get; }

    public RelayCommand<DesignLayerViewModel> ToggleVisibilityCommand { get; }

    /// <summary>Set by the shell when a project opens; the composite's target State path and
    /// the canvas mapping depend on the family.</summary>
    public OpticalDiscFamily Family { get; private set; } = OpticalDiscFamily.CompactDisc;

    public bool IsCd => Family == OpticalDiscFamily.CompactDisc;

    public bool IsDvd => Family == OpticalDiscFamily.Dvd;

    public bool IsBluRay => Family == OpticalDiscFamily.BluRay;

    /// <summary>Family chip in the page header.</summary>
    public string FamilyLabel => Family switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };

    /// <summary>The artwork canvas ends at the last usable sector on the saved physical geometry.</summary>
    public double CanvasOuterRadiusMm => ContentOuterRadiusMm;

    // Geometry snapshot: parsed from State with last-valid-value fallback so the canvas and
    // composer keep working while a field is mid-edit.
    public double ContentInnerRadiusMm { get; private set; } = 24.5;
    public double ContentOuterRadiusMm { get; private set; } = 56.8;

    /// <summary>User-visible edits (layer changes, structural changes). Marks the project dirty.</summary>
    public event Action? DesignEdited;

    /// <summary>Anything the canvas draws changed (edits, selection, geometry).</summary>
    public event Action? CanvasChanged;

    /// <summary>The debounced preview composite finished. Null clears the State source path
    /// (no visible layers). The shell performs the State write so dirty marking stays in one place.</summary>
    public event Action<string?>? CompositeRendered;

    private void Layers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasLayers));
        if (!_suppressLayerEvents)
        {
            DesignEdited?.Invoke();
            CanvasChanged?.Invoke();
            NotifySelectionCommands();
            ScheduleComposite();
        }
    }

    private void SubscribeLayer(DesignLayerViewModel layer)
    {
        layer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not string propertyName)
            {
                return;
            }

            // Selection-independent visuals always repaint the canvas.
            CanvasChanged?.Invoke();
            if (propertyName is nameof(DesignLayerViewModel.TranslateXMm)
                or nameof(DesignLayerViewModel.TranslateYMm)
                or nameof(DesignLayerViewModel.RotationDeg)
                or nameof(DesignLayerViewModel.Scale)
                or nameof(DesignLayerViewModel.IsVisible)
                or nameof(DesignLayerViewModel.Name)
                or nameof(DesignLayerViewModel.ImagePath))
            {
                if (!_suppressLayerEvents)
                {
                    DesignEdited?.Invoke();
                    ScheduleComposite();
                    NotifySelectionCommands();
                }
            }
        };
    }

    private void NotifySelectionCommands()
    {
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedUpCommand.NotifyCanExecuteChanged();
        MoveSelectedDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLayerChanged(DesignLayerViewModel? value)
    {
        // Selection changes decide the three action buttons' CanExecute, so they must be
        // re-queried right here: no collection change fires for a pure selection move, and
        // the buttons would otherwise sit stale until the next incidental command-manager
        // pass (visible as dead clicks after importing or selecting a hidden layer).
        NotifySelectionCommands();
        CanvasChanged?.Invoke();
    }

    // ---- Layer operations ----

    private void ImportImages()
    {
        string[]? picked = _shell.Dialogs?.PickImages();
        if (picked is null || picked.Length == 0)
        {
            return;
        }

        // Fit rule uses the content ring captured now; edits to geometry do not re-fit.
        DesignLayerViewModel? firstAdded = null;
        List<string> failed = [];
        foreach (string path in picked)
        {
            DesignLayerViewModel layer = new()
            {
                ImagePath = path,
                Name = Path.GetFileName(path),
            };
            if (!layer.LoadImage(ContentOuterRadiusMm))
            {
                failed.Add(path);
                continue;
            }

            SubscribeLayer(layer);
            Layers.Insert(0, layer); // new layers land on top
            firstAdded ??= layer;
        }

        if (firstAdded is not null)
        {
            SelectedLayer = firstAdded;
        }

        if (failed.Count > 0)
        {
            _shell.ShowToast(
                L.T("图片无法导入"),
                string.Join("\n", failed),
                ToastKind.Warning);
        }
    }

    private void RemoveSelected()
    {
        if (SelectedLayer is not DesignLayerViewModel layer)
        {
            return;
        }

        int index = Layers.IndexOf(layer);
        Layers.Remove(layer);
        SelectedLayer = Layers.Count == 0
            ? null
            : Layers[Math.Min(index, Layers.Count - 1)];
    }

    private void MoveSelectedUp()
    {
        if (SelectedLayer is not DesignLayerViewModel layer)
        {
            return;
        }

        int index = Layers.IndexOf(layer);
        if (index > 0)
        {
            Layers.Move(index, index - 1);
        }
    }

    private void MoveSelectedDown()
    {
        if (SelectedLayer is not DesignLayerViewModel layer)
        {
            return;
        }

        int index = Layers.IndexOf(layer);
        if (index >= 0 && index < Layers.Count - 1)
        {
            Layers.Move(index, index + 1);
        }
    }

    /// <summary>Moves a layer to an insertion slot expressed in the list's pre-drag order. The
    /// slot counts the gaps between rows (0 before the first row, Count after the last), so a
    /// drop below the source lands one position earlier once that row itself leaves the
    /// sequence — the same convention the up/down buttons use.</summary>
    public void MoveLayer(DesignLayerViewModel layer, int slotIndex)
    {
        if (_shell.IsBusy)
        {
            return;
        }

        int from = Layers.IndexOf(layer);
        if (from < 0)
        {
            return;
        }

        int target = slotIndex > from ? slotIndex - 1 : slotIndex;
        target = Math.Clamp(target, 0, Layers.Count - 1);
        if (target == from)
        {
            return;
        }

        Layers.Move(from, target);
        SelectedLayer = layer;
    }

    // ---- Geometry snapshot ----

    /// <summary>Re-reads the radii from State and refreshes the canvas mapping. Called by the
    /// shell when geometry-affecting State properties change.</summary>
    public void RefreshGeometry()
    {
        (string innerText, string outerText, double defaultInner, double defaultOuter) = Family switch
        {
            OpticalDiscFamily.Dvd => (_state.DvdInnerRadius, _state.DvdOuterRadius, 24.0, 58.0),
            OpticalDiscFamily.BluRay => (_state.BdInnerRadius, _state.BdOuterRadius, 24.0, 58.0),
            _ => (_state.CdInnerRadius, _state.CdOuterRadius, 24.5, 56.8),
        };
        ContentInnerRadiusMm = ParseMm(innerText, defaultInner);
        ContentOuterRadiusMm = ParseMm(outerText, defaultOuter);
        if (ContentOuterRadiusMm <= ContentInnerRadiusMm)
        {
            ContentOuterRadiusMm = defaultOuter;
        }

        // The physical track still spans the saved geometry. Only the writable
        // portion is shown as the artwork canvas and used as the source-image scale.
        try
        {
            long usable = _state.GetUsableSectors(Family);
            ContentOuterRadiusMm = Family switch
            {
                OpticalDiscFamily.Dvd => new DvdStreamingOptions(
                    checked((uint)ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, "Total sectors")),
                    ContentInnerRadiusMm, ContentOuterRadiusMm,
                    PitchLinear: ParameterParser.ParseDouble(_state.DvdPitchLinear, "Pitch"),
                    PitchQuadratic: ParameterParser.ParseDouble(_state.DvdPitchQuadratic, "Pitch"),
                    PitchCubic: ParameterParser.ParseDouble(_state.DvdPitchCubic, "Pitch"),
                    UsableSectors: checked((uint)usable)).OutputOuterRadiusMm,
                OpticalDiscFamily.BluRay => new BluRayDiscParameters(
                    checked((uint)ParameterParser.ParsePositiveLong(_state.BdTotalSectors, "Total sectors")), 1,
                    ContentInnerRadiusMm, ContentOuterRadiusMm,
                    PitchLinear: ParameterParser.ParseDouble(_state.BdPitchLinear, "Pitch"),
                    PitchQuadratic: ParameterParser.ParseDouble(_state.BdPitchQuadratic, "Pitch"),
                    PitchCubic: ParameterParser.ParseDouble(_state.BdPitchCubic, "Pitch"),
                    UsableSectors: checked((uint)usable)).OutputOuterRadiusMm,
                _ => new CdDiscParameters(ContentInnerRadiusMm, ContentOuterRadiusMm,
                    ParameterParser.ParsePositiveLong(_state.CdSectors, "Total sectors"),
                    PitchLinear: ParameterParser.ParseDouble(_state.CdPitchLinear, "Pitch"),
                    PitchQuadratic: ParameterParser.ParseDouble(_state.CdPitchQuadratic, "Pitch"),
                    PitchCubic: ParameterParser.ParseDouble(_state.CdPitchCubic, "Pitch"),
                    UsableSectors: usable).OutputOuterRadiusMm,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            // Fields can be temporarily incomplete while a project or preset is applied.
        }
        OnPropertyChanged(nameof(CanvasOuterRadiusMm));
        CanvasChanged?.Invoke();
        ScheduleComposite();
    }

    public void SetDiscFamily(OpticalDiscFamily family)
    {
        Family = family;
        OnPropertyChanged(nameof(Family));
        OnPropertyChanged(nameof(IsCd));
        OnPropertyChanged(nameof(IsDvd));
        OnPropertyChanged(nameof(IsBluRay));
        OnPropertyChanged(nameof(FamilyLabel));
        OnPropertyChanged(nameof(CanvasOuterRadiusMm));
        RefreshGeometry();
    }

    private static double ParseMm(string raw, double fallback)
        => double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value) && value > 0
            ? value
            : double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                && double.IsFinite(value) && value > 0
                ? value
                : fallback;

    // ---- Project load / capture ----

    /// <summary>Decodes a project's layer images off the UI thread, ahead of
    /// <see cref="LoadLayers"/>. Each large photo costs about a tenth of a second to decode
    /// plus its thumbnail; done inline, a two-dozen-layer project froze the window for seconds
    /// while opening. Distinct paths decode once, in parallel. The result belongs to the
    /// caller: keeping it off this view model means a superseded open cannot leak its images
    /// into the next project.</summary>
    public async Task<Dictionary<string, (BitmapSource? Image, BitmapSource? Thumbnail)>> PreloadLayerImagesAsync(
        IReadOnlyList<ProjectLayer> layers)
    {
        string[] paths = layers
            .Select(layer => layer.ImagePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return [];
        }

        return await Task.Run(() =>
        {
            Dictionary<string, (BitmapSource? Image, BitmapSource? Thumbnail)> map =
                new(StringComparer.OrdinalIgnoreCase);
            object gate = new();
            Parallel.ForEach(
                paths,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
                path =>
                {
                    (BitmapSource? Image, BitmapSource? Thumbnail) entry =
                        (DesignComposer.DecodeFrozenImage(path), DesignComposer.DecodeThumbnail(path));
                    lock (gate)
                    {
                        map[path] = entry;
                    }
                });
            return map;
        });
    }

    public void LoadLayers(
        IReadOnlyList<ProjectLayer> layers,
        IReadOnlyDictionary<string, (BitmapSource? Image, BitmapSource? Thumbnail)>? preloadedImages = null)
    {
        _suppressLayerEvents = true;
        try
        {
            SelectedLayer = null;
            Layers.Clear();
            List<string> unreadable = [];
            // Stored bottom-first so reopening reproduces the exact z-order; the panel and
            // composer expect topmost-first, so the load reverses.
            foreach (ProjectLayer layer in layers.AsEnumerable().Reverse())
            {
                DesignLayerViewModel viewModel = new()
                {
                    Name = layer.Name,
                    ImagePath = layer.ImagePath,
                    IsVisible = layer.Visible,
                    TranslateXMm = layer.TranslateXMm,
                    TranslateYMm = layer.TranslateYMm,
                    RotationDeg = layer.RotationDeg,
                    Scale = layer.Scale,
                };
                (BitmapSource? Image, BitmapSource? Thumbnail)? preloaded = null;
                if (layer.ImagePath is { Length: > 0 } path
                    && preloadedImages is not null
                    && preloadedImages.TryGetValue(path, out (BitmapSource? Image, BitmapSource? Thumbnail) cached))
                {
                    preloaded = cached;
                }

                if (viewModel.LoadImage(ContentOuterRadiusMm, preloaded))
                {
                    SubscribeLayer(viewModel);
                    Layers.Add(viewModel);
                }
                else
                {
                    unreadable.Add(layer.ImagePath);
                }
            }

            if (unreadable.Count > 0)
            {
                _shell.ShowToast(
                    L.T("部分图层图片无法读取"),
                    string.Join("\n", unreadable),
                    ToastKind.Warning);
            }
        }
        finally
        {
            _suppressLayerEvents = false;
        }

        // Opening a project lands on the top layer instead of an empty property panel: the
        // panel is the page's main editor, so it should already show something to edit.
        SelectedLayer ??= Layers.FirstOrDefault();
        OnPropertyChanged(nameof(HasLayers));
        CanvasChanged?.Invoke();
        ScheduleComposite();
    }

    public void ClearLayers()
    {
        _suppressLayerEvents = true;
        try
        {
            SelectedLayer = null;
            Layers.Clear();
        }
        finally
        {
            _suppressLayerEvents = false;
        }

        _compositeRevision++;
        _previewRenderedRevision = -1;
        _exportRenderedRevision = -1;
        OnPropertyChanged(nameof(HasLayers));
        CanvasChanged?.Invoke();
        CompositeRendered?.Invoke(null);
    }

    /// <summary>Stored bottom-first; panel order is the reverse. Rebase each scale onto
    /// the current canvas diameter so reopening preserves its physical size after
    /// geometry or usable-capacity edits, without changing the live layer.</summary>
    public IReadOnlyList<ProjectLayer> CaptureLayers()
        => Layers
            .AsEnumerable()
            .Reverse()
            .Select(layer => new ProjectLayer(
                layer.Id,
                layer.Name,
                layer.ImagePath,
                layer.IsVisible,
                layer.TranslateXMm,
                layer.TranslateYMm,
                layer.RotationDeg,
                layer.Scale * (Math.Max(layer.BaseWidthMm, layer.BaseHeightMm) / (2 * ContentOuterRadiusMm))))
            .ToList();

    // ---- Composite pipeline ----

    /// <summary>Schedules the debounced preview composite.</summary>
    public void ScheduleComposite()
    {
        _compositeRevision++;
        _compositeTimer.Stop();
        _compositeTimer.Start();
    }

    private List<DesignLayerRenderItem> BuildRenderItems()
    {
        List<DesignLayerRenderItem> items = [];
        foreach (DesignLayerViewModel layer in Layers)
        {
            if (layer.IsVisible && layer.HasImage)
            {
                items.Add(new DesignLayerRenderItem(
                    layer.Image!,
                    layer.BaseWidthMm,
                    layer.BaseHeightMm,
                    layer.TranslateXMm,
                    layer.TranslateYMm,
                    layer.RotationDeg,
                    layer.Scale));
            }
        }

        return items;
    }

    private async Task RenderPreviewCompositeAsync()
    {
        long revision = _compositeRevision;
        List<DesignLayerRenderItem> items = BuildRenderItems();
        string? outputPath = null;
        if (items.Count > 0)
        {
            try
            {
                outputPath = TempFiles.GetCacheFilePath(
                    Family switch
                    {
                        OpticalDiscFamily.Dvd => "design-composite-dvd.png",
                        OpticalDiscFamily.BluRay => "design-composite-bd.png",
                        _ => "design-composite-cd.png",
                    });
                await StaWorker.RunAsync(() =>
                {
                    DesignComposer.Render(items, CanvasOuterRadiusMm, PreviewCompositeSize, outputPath);
                    return 0;
                });
            }
            catch (Exception exception)
            {
                _shell.AppendLog(JobDiagnostics.FormatLog(L.T("设计合成失败"), exception));
                _shell.ShowToast(L.T("设计合成失败"), exception.Message, ToastKind.Error);
                return;
            }
        }

        if (revision != _compositeRevision)
        {
            return;
        }

        _previewRenderedRevision = revision;
        CompositeRendered?.Invoke(outputPath);
    }

    /// <summary>Renders (or reuses) the export-quality composite for a generation job.
    /// Throws when there is nothing to compose.</summary>
    public async Task<string> EnsureExportCompositeAsync()
    {
        if (!HasLayers)
        {
            throw new ArgumentException(L.T("请先在设计页导入图片。"));
        }

        if (_exportRenderedRevision == _compositeRevision && _lastExportPath is not null && File.Exists(_lastExportPath))
        {
            return _lastExportPath;
        }

        List<DesignLayerRenderItem> items = BuildRenderItems();
        string outputPath = TempFiles.GetCacheFilePath(
            Family switch
            {
                OpticalDiscFamily.Dvd => "design-composite-dvd-export.png",
                OpticalDiscFamily.BluRay => "design-composite-bd-export.png",
                _ => "design-composite-cd-export.png",
            });
        await StaWorker.RunAsync(() =>
        {
            DesignComposer.Render(items, CanvasOuterRadiusMm, RingImageQuality.GenerationSize, outputPath);
            return 0;
        });

        _exportRenderedRevision = _compositeRevision;
        _lastExportPath = outputPath;
        return outputPath;
    }

    private string? _lastExportPath;

    /// <summary>Renders a small square thumbnail for the project file. Returns null when
    /// there is nothing to show.</summary>
    public async Task<string?> RenderThumbnailAsync()
    {
        if (!HasLayers)
        {
            return null;
        }

        string? outputPath = null;
        try
        {
            List<DesignLayerRenderItem> items = BuildRenderItems();
            outputPath = TempFiles.GetCacheFilePath("design-thumbnail.png");
            await StaWorker.RunAsync(() =>
            {
                DesignComposer.Render(items, CanvasOuterRadiusMm, ProjectDocument.ThumbnailDecodeSize, outputPath);
                return 0;
            });
            return Convert.ToBase64String(await File.ReadAllBytesAsync(outputPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _shell.AppendLog(JobDiagnostics.FormatLog($"生成可选工程缩略图：{outputPath}", exception, warning: true));
            return null;
        }
    }
}
