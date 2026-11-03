using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

/// <summary>An open .discproj: geometry, tone, outputs and the layer stack, plus the cached
/// composites the previews and generator read. Files written here open in the Windows app
/// and vice versa; both go through <see cref="ProjectJsonStore"/>.</summary>
public partial class ProjectViewModel : ObservableObject
{
    private readonly SemaphoreSlim _compositeGate = new(1, 1);
    private readonly ProjectDocument _origin;
    private long _revision;
    private long _previewRevision = -1;
    private long _exportRevision = -1;
    private string? _previewCompositePath;
    private string? _retiredPreviewCompositePath;
    private string? _exportCompositePath;
    private bool _suppressDirty;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isDirty;

    [ObservableProperty] private double _innerRadiusMm;
    [ObservableProperty] private double _outerRadiusMm;
    [ObservableProperty] private long _sectors;
    [ObservableProperty] private double _pitchLinear;
    [ObservableProperty] private double _pitchQuadratic;
    [ObservableProperty] private double _pitchCubic;

    [ObservableProperty] private double _ditherPercent;
    [ObservableProperty] private bool _fourLevels;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private bool _srgbLinearization;
    [ObservableProperty] private bool _invertMapping;

    [ObservableProperty] private string _outputPath;
    [ObservableProperty] private string _dataDirectory;
    [ObservableProperty] private string _volumeLabel;
    [ObservableProperty] private bool _filesAtOuterRing;
    [ObservableProperty] private bool _interleave;
    [ObservableProperty] private bool _writeCue;

    [ObservableProperty] private LayerViewModel? _selectedLayer;

    public ProjectViewModel(ProjectDocument document, string? filePath)
    {
        _origin = document;
        Family = document.Family;
        _name = document.Name;
        _filePath = filePath;
        _innerRadiusMm = document.Geometry.InnerRadiusMm;
        _outerRadiusMm = document.Geometry.OuterRadiusMm;
        _sectors = document.Geometry.Sectors;
        _pitchLinear = document.Geometry.PitchLinear;
        _pitchQuadratic = document.Geometry.PitchQuadratic;
        _pitchCubic = document.Geometry.PitchCubic;
        // Files written before tone mapping keep their hard threshold, as on Windows.
        ToneProfile tone = (document.Tone ?? ToneProfile.LegacyBinary).Normalized();
        _ditherPercent = tone.DitherStrength * 100.0;
        _fourLevels = tone.Levels == ToneProfile.FourLevels && Family == OpticalDiscFamily.CompactDisc;
        _brightness = tone.Brightness;
        _srgbLinearization = tone.SrgbLinearization;
        _invertMapping = document.BluRay?.InvertMapping ?? false;
        _outputPath = document.Outputs.OutputPath;
        _dataDirectory = document.Outputs.DataDirectory ?? string.Empty;
        _volumeLabel = document.Outputs.VolumeLabel ?? string.Empty;
        _filesAtOuterRing = document.Outputs.FilesAtOuterRing;
        _interleave = document.Outputs.Interleave;
        _writeCue = document.Outputs.WriteCue;

        foreach (ProjectLayer layer in document.Layers)
        {
            AddLayerCore(LayerViewModel.FromDocument(layer), Layers.Count);
        }

        Layers.CollectionChanged += Layers_CollectionChanged;
        PropertyChanged += Self_PropertyChanged;
    }

    public ObservableCollection<LayerViewModel> Layers { get; } = [];

    public OpticalDiscFamily Family { get; }

    public bool IsCd => Family == OpticalDiscFamily.CompactDisc;

    public bool IsDvd => Family == OpticalDiscFamily.Dvd;

    public bool IsBluRay => Family == OpticalDiscFamily.BluRay;

    public string FamilyLabel => DiscFamilies.Label(Family);

    public string FamilyIcon => DiscFamilies.Icon(Family);

    public bool HasLayers => Layers.Count > 0;

    public bool HasVisibleImages => Layers.Any(layer => layer.IsVisible && layer.HasImage);

    public bool HasFiles => !string.IsNullOrWhiteSpace(DataDirectory);

    public bool FilesAtInnerRing
    {
        get => !FilesAtOuterRing;
        set => FilesAtOuterRing = !value;
    }

    public string LayerCountText => Layers.Count == 1 ? "1 layer" : $"{Layers.Count} layers";

    public string DisplayTitle => (IsDirty ? "• " : string.Empty) + Name;

    public string? Folder => FilePath is null ? null : Path.GetDirectoryName(FilePath);

    public double CanvasOuterRadiusMm => Snapshot().CanvasOuterRadiusMm;

    public string GeometrySummary => Family switch
    {
        OpticalDiscFamily.Dvd => $"{InnerRadiusMm:0.###}–{OuterRadiusMm:0.###} mm · {Sectors:N0} sectors"
            + (PitchLinear != 0 || PitchQuadratic != 0 || PitchCubic != 0 ? " · cubic pitch" : string.Empty),
        _ => $"{InnerRadiusMm:0.###}–{OuterRadiusMm:0.###} mm · {Sectors:N0} sectors",
    };

    public string ToneSummary
    {
        get
        {
            string summary = $"Dither {DitherPercent:0}%";
            if (IsCd)
            {
                summary += FourLevels ? " · 4 levels" : " · 2 levels";
            }

            if (Brightness != 0)
            {
                summary += $" · brightness {Brightness:+0;-0}";
            }

            if (SrgbLinearization)
            {
                summary += " · sRGB linear";
            }

            if (IsBluRay && InvertMapping)
            {
                summary += " · 3T/6T swapped";
            }

            return summary;
        }
    }

    /// <summary>Raised when the composite picture changes (layers or geometry).</summary>
    public event Action? CompositeChanged;

    /// <summary>Raised when anything a preview depends on changes, composite included.</summary>
    public event Action? PreviewInputsChanged;

    public ToneProfile Tone => new ToneProfile(
        Math.Clamp(DitherPercent / 100.0, 0, 1),
        FourLevels && IsCd ? ToneProfile.FourLevels : ToneProfile.TwoLevels,
        Math.Clamp(Math.Round(Brightness), ToneProfile.MinimumBrightness, ToneProfile.MaximumBrightness),
        SrgbLinearization).Normalized();

    public DiscSettings Snapshot()
        => new(Family, InnerRadiusMm, OuterRadiusMm, Sectors, PitchLinear, PitchQuadratic, PitchCubic,
            Tone, InvertMapping, DataDirectory ?? string.Empty, VolumeLabel ?? string.Empty,
            FilesAtOuterRing, Interleave, WriteCue);

    public void LoadImages()
    {
        foreach (LayerViewModel layer in Layers)
        {
            layer.LoadImage(OuterRadiusMm);
        }

        BumpComposite();
    }

    public LayerViewModel AddImage(string path)
    {
        string id = Guid.NewGuid().ToString("N");
        LayerViewModel layer = new(id, Path.GetFullPath(path)) { Name = Path.GetFileNameWithoutExtension(path) };
        layer.LoadImage(OuterRadiusMm);
        // New layers go on top, as on Windows.
        Layers.Insert(0, layer);
        SelectedLayer = layer;
        return layer;
    }

    public void RemoveLayer(LayerViewModel layer)
    {
        int index = Layers.IndexOf(layer);
        if (index < 0)
        {
            return;
        }

        Layers.RemoveAt(index);
        SelectedLayer = Layers.Count == 0 ? null : Layers[Math.Min(index, Layers.Count - 1)];
    }

    public void MoveLayer(LayerViewModel layer, int delta)
    {
        int index = Layers.IndexOf(layer);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Layers.Count)
        {
            return;
        }

        Layers.Move(index, target);
    }

    /// <summary>Applies edited project settings; base sizes follow the new content ring.</summary>
    public void ApplyGeometry(string name, double inner, double outer, long sectors, double linear, double quadratic, double cubic, bool invertMapping)
    {
        Name = name;
        InnerRadiusMm = inner;
        OuterRadiusMm = outer;
        Sectors = sectors;
        PitchLinear = IsDvd ? linear : 0;
        PitchQuadratic = IsDvd ? quadratic : 0;
        PitchCubic = IsDvd ? cubic : 0;
        if (IsBluRay)
        {
            InvertMapping = invertMapping;
        }

        foreach (LayerViewModel layer in Layers)
        {
            layer.Refit(OuterRadiusMm);
        }

        BumpComposite();
    }

    public void ResetTone(ToneProfile tone)
    {
        DitherPercent = tone.DitherStrength * 100;
        FourLevels = tone.Levels == ToneProfile.FourLevels && IsCd;
        Brightness = tone.Brightness;
        SrgbLinearization = tone.SrgbLinearization;
    }

    public ProjectDocument ToDocument()
    {
        ProjectOutputs outputs = new(
            OutputPath?.Trim() ?? string.Empty,
            IsCd && WriteCue,
            DataDirectory?.Trim() ?? string.Empty,
            VolumeLabel?.Trim() ?? string.Empty,
            IsCd && Interleave,
            FilesAtOuterRing);
        string? thumbnail = null;
        List<LayerRenderItem> items = VisibleRenderItems();
        if (items.Count > 0)
        {
            try
            {
                thumbnail = Compositor.RenderBase64Png(items, CanvasOuterRadiusMm, Compositor.ThumbnailSize);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                thumbnail = _origin.ThumbnailPngBase64;
            }
        }

        return new ProjectDocument(
            ProjectJsonStore.CurrentSchemaVersion,
            string.IsNullOrWhiteSpace(Name) ? "Untitled" : Name.Trim(),
            DiscFamilies.ProjectKey(Family),
            new ProjectGeometry(InnerRadiusMm, OuterRadiusMm, Sectors, PitchLinear, PitchQuadratic, PitchCubic),
            Layers.Select(layer => layer.ToDocument()).ToList(),
            outputs,
            _origin.CalibrationSessionPath,
            thumbnail,
            _origin.CreatedUtc,
            DateTimeOffset.UtcNow,
            Tone,
            IsBluRay ? new ProjectBluRaySettings(InvertMapping) : null);
    }

    public void Save(string path)
    {
        ProjectDocument document = ToDocument();
        ProjectJsonStore.Save(path, document);
        FilePath = Path.GetFullPath(path);
        IsDirty = false;
        OnPropertyChanged(nameof(Folder));
    }

    public List<LayerRenderItem> VisibleRenderItems()
        => Layers.Where(layer => layer.IsVisible && layer.HasImage).Select(layer => layer.ToRenderItem()).ToList();

    /// <summary>The debounced 2048 px composite for previews; null without visible images.</summary>
    public Task<string?> EnsurePreviewCompositeAsync(CancellationToken cancellationToken)
        => EnsureCompositeAsync(Compositor.PreviewSize, preview: true, cancellationToken);

    /// <summary>The generation-quality composite (8192 px), rendered only when the design
    /// changed since the last export.</summary>
    public Task<string?> EnsureExportCompositeAsync(CancellationToken cancellationToken)
        => EnsureCompositeAsync(Compositor.GenerationSize, preview: false, cancellationToken);

    private async Task<string?> EnsureCompositeAsync(int size, bool preview, CancellationToken cancellationToken)
    {
        await _compositeGate.WaitAsync(cancellationToken);
        try
        {
            long revision = Interlocked.Read(ref _revision);
            string? existing = preview ? _previewCompositePath : _exportCompositePath;
            long existingRevision = preview ? _previewRevision : _exportRevision;
            if (existingRevision == revision && existing is not null && File.Exists(existing))
            {
                return existing;
            }

            List<LayerRenderItem> items = VisibleRenderItems();
            if (items.Count == 0)
            {
                return null;
            }

            double radius = CanvasOuterRadiusMm;
            string path = AppPaths.NewWorkFile(preview ? "composite-preview" : "composite-export", ".png");
            await Task.Run(() => Compositor.Render(items, radius, size, path, cancellationToken), cancellationToken);
            if (preview)
            {
                // A preview started a moment ago may still be reading the previous composite;
                // only the one before it is certainly unused.
                AppPaths.TryDelete(_retiredPreviewCompositePath);
                _retiredPreviewCompositePath = _previewCompositePath;
                _previewCompositePath = path;
                _previewRevision = revision;
            }
            else
            {
                AppPaths.TryDelete(_exportCompositePath);
                _exportCompositePath = path;
                _exportRevision = revision;
            }

            return path;
        }
        finally
        {
            _compositeGate.Release();
        }
    }

    public void ReleaseCaches()
    {
        AppPaths.TryDelete(_retiredPreviewCompositePath);
        AppPaths.TryDelete(_previewCompositePath);
        AppPaths.TryDelete(_exportCompositePath);
        _retiredPreviewCompositePath = _previewCompositePath = _exportCompositePath = null;
        _previewRevision = _exportRevision = -1;
    }

    /// <summary>Runs without marking the project dirty (used when loading).</summary>
    public void WithoutDirty(Action action)
    {
        _suppressDirty = true;
        try
        {
            action();
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    private void BumpComposite()
    {
        Interlocked.Increment(ref _revision);
        OnPropertyChanged(nameof(HasVisibleImages));
        CompositeChanged?.Invoke();
        PreviewInputsChanged?.Invoke();
    }

    private void MarkDirty()
    {
        if (!_suppressDirty)
        {
            IsDirty = true;
        }
    }

    private void AddLayerCore(LayerViewModel layer, int index)
    {
        Layers.Insert(index, layer);
        layer.PropertyChanged += Layer_PropertyChanged;
    }

    private void Layers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (LayerViewModel layer in e.NewItems)
            {
                layer.PropertyChanged -= Layer_PropertyChanged;
                layer.PropertyChanged += Layer_PropertyChanged;
            }
        }

        if (e.OldItems is not null && e.Action != NotifyCollectionChangedAction.Move)
        {
            foreach (LayerViewModel layer in e.OldItems)
            {
                layer.PropertyChanged -= Layer_PropertyChanged;
            }
        }

        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(LayerCountText));
        MarkDirty();
        BumpComposite();
    }

    private void Layer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LayerViewModel.Name):
                MarkDirty();
                break;
            case nameof(LayerViewModel.IsVisible):
            case nameof(LayerViewModel.TranslateXMm):
            case nameof(LayerViewModel.TranslateYMm):
            case nameof(LayerViewModel.RotationDeg):
            case nameof(LayerViewModel.Scale):
                MarkDirty();
                BumpComposite();
                break;
        }
    }

    private void Self_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsDirty):
            case nameof(Name):
                OnPropertyChanged(nameof(DisplayTitle));
                if (e.PropertyName == nameof(Name))
                {
                    MarkDirty();
                }

                break;
            case nameof(InnerRadiusMm):
            case nameof(OuterRadiusMm):
            case nameof(Sectors):
            case nameof(PitchLinear):
            case nameof(PitchQuadratic):
            case nameof(PitchCubic):
                OnPropertyChanged(nameof(GeometrySummary));
                OnPropertyChanged(nameof(CanvasOuterRadiusMm));
                MarkDirty();
                PreviewInputsChanged?.Invoke();
                break;
            case nameof(DitherPercent):
            case nameof(FourLevels):
            case nameof(Brightness):
            case nameof(SrgbLinearization):
            case nameof(InvertMapping):
                OnPropertyChanged(nameof(ToneSummary));
                MarkDirty();
                PreviewInputsChanged?.Invoke();
                break;
            case nameof(DataDirectory):
                OnPropertyChanged(nameof(HasFiles));
                AdjustOutputExtension();
                MarkDirty();
                PreviewInputsChanged?.Invoke();
                break;
            case nameof(FilesAtOuterRing):
            case nameof(VolumeLabel):
                OnPropertyChanged(nameof(FilesAtInnerRing));
                MarkDirty();
                PreviewInputsChanged?.Invoke();
                break;
            case nameof(OutputPath):
            case nameof(Interleave):
            case nameof(WriteCue):
                MarkDirty();
                break;
        }
    }

    /// <summary>A CD with files is a Mode 1 .iso; without files it is a .wav/.raw track.</summary>
    private void AdjustOutputExtension()
    {
        if (!IsCd || string.IsNullOrWhiteSpace(OutputPath))
        {
            return;
        }

        string extension = Path.GetExtension(OutputPath).ToLowerInvariant();
        if (HasFiles && extension != ".iso")
        {
            OutputPath = Path.ChangeExtension(OutputPath, ".iso");
        }
        else if (!HasFiles && extension == ".iso")
        {
            OutputPath = Path.ChangeExtension(OutputPath, ".wav");
        }
    }

    /// <summary>A fresh project document for the new-project dialog.</summary>
    public static ProjectDocument CreateDocument(
        string name, OpticalDiscFamily family, double inner, double outer, long sectors,
        double linear, double quadratic, double cubic, bool invertMapping, string outputPath)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool isCd = family == OpticalDiscFamily.CompactDisc;
        return new ProjectDocument(
            ProjectJsonStore.CurrentSchemaVersion,
            name,
            DiscFamilies.ProjectKey(family),
            new ProjectGeometry(inner, outer, sectors,
                family == OpticalDiscFamily.Dvd ? linear : 0,
                family == OpticalDiscFamily.Dvd ? quadratic : 0,
                family == OpticalDiscFamily.Dvd ? cubic : 0),
            [],
            new ProjectOutputs(outputPath, WriteCue: isCd, DataDirectory: string.Empty, VolumeLabel: string.Empty, Interleave: isCd),
            CalibrationSessionPath: null,
            ThumbnailPngBase64: null,
            now,
            now,
            ToneProfile.Photo,
            family == OpticalDiscFamily.BluRay ? new ProjectBluRaySettings(invertMapping) : null);
    }
}
