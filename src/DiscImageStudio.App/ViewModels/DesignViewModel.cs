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
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
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
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _compositeTimer;
    private long _compositeRevision;
    private long _previewRenderedRevision = -1;
    private long _exportRenderedRevision = -1;
    private bool _suppressLayerEvents;

    public DesignViewModel(ShellViewModel shell, DiscParametersState state, Dispatcher dispatcher)
    {
        _shell = shell;
        _state = state;
        _dispatcher = dispatcher;
        _compositeTimer = new DispatcherTimer { Interval = CompositeDebounce };
        _shell.PropertyChanged += Shell_PropertyChanged;
        _state.PropertyChanged += State_PropertyChanged;
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
        L.LanguageChanged += (_, _) => RefreshDataRegionLabel();
        RefreshDataRegionLabel();
    }

    /// <summary>The right rail's tone module: its preview projects the same composite this
    /// page renders through the engines' tone mapping.</summary>
    public ToneViewModel Tone { get; }

    /// <summary>The design page itself builds its text from XAML; only the tone module caches
    /// a status line.</summary>
    internal void Relocalize() => Tone.Relocalize();

    /// <summary>Disc files are shared with export, preview and burn; they are not image layers.</summary>
    public DiscParametersState State => _state;

    /// <summary>The disc area the data files occupy, as mm radii, drawn on the canvas as the
    /// yellow guide ring (the designer's twin of the tone mini-preview's data tint): CD uses
    /// the exact layout plan, DVD/BD estimate it from the folder size in 2048-byte sectors.</summary>
    public sealed record DataRegionMm(double InnerRadiusMm, double OuterRadiusMm);

    /// <summary>Draws the data-occupied zone on the canvas so picture layers can be placed
    /// away from the files.</summary>
    [ObservableProperty]
    private bool _showDataRegion;

    /// <summary>Null while unchecked, computing, or when the family has no data folder.</summary>
    [ObservableProperty]
    private DataRegionMm? _dataRegion;

    /// <summary>Checkbox caption: the "(estimated)" variant on DVD/Blu-ray, where the tone
    /// preview has no exact yellow region to mirror.</summary>
    [ObservableProperty]
    private string _showDataRegionLabel = string.Empty;

    [ObservableProperty]
    private string _showDataRegionHint = string.Empty;

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

    /// <summary>CD composes against the scheme's fixed image outer radius; DVD and BD
    /// against the generated outer radius, matching what the generation pages feed the
    /// engines.</summary>
    public double CanvasOuterRadiusMm => IsCd
        ? CdDiscParameters.StandardImageOuterRadiusMm
        : ContentOuterRadiusMm;

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

        CanvasChanged?.Invoke();
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
        RefreshDataRegionLabel();
        RefreshGeometry();
        RecomputeDataRegion();
    }

    /// <summary>CD shows the exact plan (as in the tone preview); DVD/Blu-ray estimate the
    /// zone from the folder size, so the caption says so.</summary>
    private void RefreshDataRegionLabel()
    {
        bool exact = Family == OpticalDiscFamily.CompactDisc;
        ShowDataRegionLabel = L.T(exact ? "Xaml.ShowDataRegion" : "Xaml.ShowDataRegionEstimated");
        ShowDataRegionHint = L.T(exact ? "Xaml.ShowDataRegionHint" : "Xaml.ShowDataRegionHintEstimated");
    }

    // ---- Data region guide ----

    private long _dataRegionRevision;

    partial void OnShowDataRegionChanged(bool value) => RecomputeDataRegion();

    private void Shell_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsProjectOpen)
            or nameof(ShellViewModel.CurrentProject))
        {
            RecomputeDataRegion();
        }
    }

    private void State_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(DiscParametersState.CdDataDirectory) or nameof(DiscParametersState.CdVolumeLabel)
            or nameof(DiscParametersState.CdFilesAtOuterRing)
            or nameof(DiscParametersState.CdInnerRadius) or nameof(DiscParametersState.CdOuterRadius)
            or nameof(DiscParametersState.CdSectors)
            or nameof(DiscParametersState.CdPitchLinear) or nameof(DiscParametersState.CdPitchQuadratic)
            or nameof(DiscParametersState.CdPitchCubic)
            or nameof(DiscParametersState.DvdDataDirectory) or nameof(DiscParametersState.DvdFilesAtOuterRing)
            or nameof(DiscParametersState.DvdTotalSectors)
            or nameof(DiscParametersState.DvdInnerRadius) or nameof(DiscParametersState.DvdOuterRadius)
            or nameof(DiscParametersState.DvdPitchLinear) or nameof(DiscParametersState.DvdPitchQuadratic)
            or nameof(DiscParametersState.DvdPitchCubic)
            or nameof(DiscParametersState.BdDataDirectory) or nameof(DiscParametersState.BdFilesAtOuterRing)
            or nameof(DiscParametersState.BdTotalSectors)
            or nameof(DiscParametersState.BdInnerRadius) or nameof(DiscParametersState.BdOuterRadius)
            or nameof(DiscParametersState.BdPitchLinear) or nameof(DiscParametersState.BdPitchQuadratic)
            or nameof(DiscParametersState.BdPitchCubic))
        {
            RecomputeDataRegion();
        }
    }

    /// <summary>Recomputes the guide ring off the UI thread (the CD plan scans the folder)
    /// and publishes it back on the dispatcher; superseded runs are dropped by revision.</summary>
    private void RecomputeDataRegion()
    {
        if (!ShowDataRegion)
        {
            _dataRegionRevision++;
            DataRegion = null;
            CanvasChanged?.Invoke();
            return;
        }

        long revision = ++_dataRegionRevision;
        _ = Task.Run(async () =>
        {
            DataRegionMm? region = null;
            try
            {
                region = await StaWorker.RunAsync(() => ComputeDataRegion());
            }
            catch
            {
                // A missing folder or invalid geometry simply hides the guide.
            }

            if (revision != _dataRegionRevision)
            {
                return;
            }

            _dispatcher.Invoke(() =>
            {
                if (revision != _dataRegionRevision)
                {
                    return;
                }

                DataRegion = region;
                CanvasChanged?.Invoke();
            });
        });
    }

    private DataRegionMm? ComputeDataRegion() => Family switch
    {
        OpticalDiscFamily.Dvd => ComputeDvdDataRegion(),
        OpticalDiscFamily.BluRay => ComputeBluRayDataRegion(),
        _ => ComputeCdDataRegion(),
    };

    /// <summary>Exact, same plan the tone mini-preview tints with. The plan's LBA span is
    /// the drawing (picture) range: the files occupy everything outside it, inner or outer
    /// depending on the ring choice — the tint is that complement, like the preview.</summary>
    private DataRegionMm? ComputeCdDataRegion()
    {
        string directory = _state.CdDataDirectory.Trim();
        if (directory.Length == 0)
        {
            return null;
        }

        CdDiscParameters parameters = new(
            ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, L.T("内半径")),
            ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, L.T("内半径")),
            ParameterParser.ParsePositiveLong(_state.CdSectors, L.T("扇区数")),
            PitchLinear: ParameterParser.ParseDouble(_state.CdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.CdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.CdPitchCubic, L.T("相对三次项")));
        parameters.Validate();
        CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(
            directory, parameters, _state.CdVolumeLabel.Trim(), _state.CdFilesAtOuterRing);
        double drawingInner = parameters.RadiusFromByte((long)plan.DrawingStartLba * CdDiscParameters.BytesPerSector);
        double drawingOuter = parameters.RadiusFromByte((long)plan.DrawingEndLbaExclusive * CdDiscParameters.BytesPerSector);
        return _state.CdFilesAtOuterRing
            ? new DataRegionMm(drawingOuter, parameters.OuterRadiusMm)
            : new DataRegionMm(parameters.InnerRadiusMm, drawingInner);
    }

    /// <summary>Estimated: the folder's bytes as 2048-byte sectors on the DVD geometry; the
    /// ISO filesystem overhead is small compared to a real data load, and the ring is a
    /// placement guide, not a capacity claim.</summary>
    private DataRegionMm? ComputeDvdDataRegion()
    {
        string directory = _state.DvdDataDirectory.Trim();
        if (directory.Length == 0)
        {
            return null;
        }

        long totalSectors = ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, L.T("总扇区数"));
        if (totalSectors > uint.MaxValue)
        {
            return null;
        }

        double inner = ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, L.T("内半径"));
        double outer = ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, L.T("内半径"));
        double pitchLinear = ParameterParser.ParseDouble(_state.DvdPitchLinear, L.T("相对一次项"));
        double pitchQuadratic = ParameterParser.ParseDouble(_state.DvdPitchQuadratic, L.T("相对二次项"));
        double pitchCubic = ParameterParser.ParseDouble(_state.DvdPitchCubic, L.T("相对三次项"));
        double length = (double)totalSectors * 38688.0 * DvdStreamingOptions.StandardChannelBitLengthNm * 1e-6;
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(
            inner, outer, length, pitchLinear, pitchQuadratic, pitchCubic);
        long dataSectors = Math.Min(totalSectors, FolderSectorCount(directory));
        // The span grows from the ring the files sit on: inward for inner files,
        // outward for outer files (so a little data is a thin ring at that edge).
        double spanStart = _state.DvdFilesAtOuterRing
            ? geometry.RadiusAtArcLength(length * (totalSectors - dataSectors) / totalSectors)
            : geometry.RadiusAtArcLength(length * (double)dataSectors / totalSectors);
        return _state.DvdFilesAtOuterRing
            ? new DataRegionMm(spanStart, outer)
            : new DataRegionMm(inner, spanStart);
    }

    /// <summary>Estimated like the DVD one, on the BluRay sector geometry.</summary>
    private DataRegionMm? ComputeBluRayDataRegion()
    {
        string directory = _state.BdDataDirectory.Trim();
        if (directory.Length == 0)
        {
            return null;
        }

        long totalSectors = ParameterParser.ParsePositiveLong(_state.BdTotalSectors, L.T("蓝光总扇区数"));
        if (totalSectors > uint.MaxValue)
        {
            return null;
        }

        BluRayDiscParameters parameters = new(
            checked((uint)totalSectors),
            1,
            ParameterParser.ParsePositiveDouble(_state.BdInnerRadius, L.T("蓝光内半径")),
            ParameterParser.ParsePositiveDouble(_state.BdOuterRadius, L.T("蓝光外半径")),
            PitchLinear: ParameterParser.ParseDouble(_state.BdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.BdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.BdPitchCubic, L.T("相对三次项")));
        parameters.Validate();
        BluRayTrackGeometry geometry = new(parameters);
        long dataSectors = Math.Min(totalSectors, FolderSectorCount(directory));
        // The span grows from the ring the files sit on: inward for inner files,
        // outward for outer files (so a little data is a thin ring at that edge).
        double spanStart = _state.BdFilesAtOuterRing
            ? geometry.AtSector(totalSectors - dataSectors).RadiusMm
            : geometry.AtSector(dataSectors).RadiusMm;
        return _state.BdFilesAtOuterRing
            ? new DataRegionMm(spanStart, parameters.OuterRadiusMm)
            : new DataRegionMm(parameters.InnerRadiusMm, spanStart);
    }

    /// <summary>Folder bytes rounded up to 2048-byte data sectors; enumeration failures
    /// (missing folder, access denied) surface as the outer catch hiding the guide.</summary>
    private static long FolderSectorCount(string directory)
    {
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(file).Length;
        }

        return Math.Max(0, (bytes + 2047) / 2048);
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

    /// <summary>Stored bottom-first; panel order is the reverse.</summary>
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
                layer.Scale))
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
