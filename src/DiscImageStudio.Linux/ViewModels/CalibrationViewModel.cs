using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Linux.Services;
using DiscImageStudio.Linux.Services.Calibration;

namespace DiscImageStudio.Linux.ViewModels;

public enum CalibrationBoundaryKind
{
    OuterEdge,
    CenterHole,
}

/// <summary>A draggable outline point in source-photo pixels.</summary>
public sealed record CalibrationBoundaryPoint(CalibrationBoundaryKind Boundary, int Index, double X, double Y);

/// <summary>A traced point in rectified disc millimetres (origin at the centre, +y down).
/// Index is a stable editing identity; the list itself is kept in radial order.</summary>
public sealed record CalibrationTracePoint(int Index, double XMm, double YMm);

public sealed record CalibrationCandidate(string Label, CalibrationParameters Parameters, double RotationRadians,
    double RmsErrorMm, double MaxErrorMm)
{
    public override string ToString() => Label;
}

public sealed record CalibrationSearchRange(string Label, double RadiusRangeMm, bool Auto = false)
{
    public override string ToString() => Label;
}

public sealed record PhotoDiscSize(string Label, double OuterRadiusMm)
{
    public override string ToString() => Label;
}

/// <summary>Assisted calibration, ported from the Windows calibration page: create and burn a
/// target, rectify a photo of the burned disc from its physical outer edge and centre hole,
/// trace one radial curve and fit the disc's real inner/outer radii (and DVD cubic pitch).
/// The maths lives in DiscImageStudio.Core.Calibration; sessions use the same
/// .calibration.json format as Windows.</summary>
public partial class CalibrationViewModel : ObservableObject
{
    public const double HoleRadiusMm = CalibrationImaging.HoleRadiusMm;
    private const string IdleAssessment = "All traced points are used. Check the result against the photographed curve.";

    private readonly MainViewModel _main;
    private ProjectViewModel? _project;
    private CalibrationTarget? _target;
    private CalibrationParameters? _searchCenter;
    private CalibrationFitOptions _loadedFitOptions = new();
    private CalibrationFitResult? _fit;
    private CalibrationPhotoTransform? _photoTransform;
    private BgraImage? _photo;
    private string? _photoPng;
    private string? _sessionPath;
    private bool _cdInterleave = true;
    private bool _installing;
    private bool _restoringPhoto;
    private bool _previewReady;
    private long _inputRevision;
    private long _photoRevision;
    private double _fitRangeMm = 0.02;
    private bool _fitRangeExpanded;
    private CancellationTokenSource? _previewCancellation;

    [ObservableProperty] private Bitmap? _targetImage;
    [ObservableProperty] private double _canvasOuterRadiusMm = 60;
    [ObservableProperty] private string _outputPath = string.Empty;

    [ObservableProperty] private bool _stepOneExpanded = true;
    [ObservableProperty] private bool _stepTwoExpanded;
    [ObservableProperty] private bool _stepThreeExpanded;
    [ObservableProperty] private bool _showTargetPreview;
    [ObservableProperty] private bool _showPrediction = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTraceStep), nameof(StepTwoState), nameof(StepThreeState), nameof(CanvasImage))]
    private bool _isPhotoStep = true;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanvasImage), nameof(HasPhoto), nameof(StepOneState), nameof(StepTwoState))] private Bitmap? _photoBitmap;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanvasImage))] private Bitmap? _rectifiedBitmap;
    [ObservableProperty] private bool _isRectificationReady;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanvasImage))] private bool _previewRectification;
    [ObservableProperty] private double _photoCanvasRadiusMm = 63;
    [ObservableProperty] private IReadOnlyList<CalibrationBoundaryPoint> _boundaryPoints = [];
    [ObservableProperty] private IReadOnlyList<Point[]> _boundaryContours = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TraceSummary))] private IReadOnlyList<CalibrationTracePoint> _tracePoints = [];
    [ObservableProperty] private IReadOnlyList<Point[]> _predictionContours = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOutliers))] private IReadOnlyList<int> _outlierIndices = [];
    [ObservableProperty] private string _rectificationSummary = string.Empty;
    [ObservableProperty] private PhotoDiscSize _selectedPhysicalDiscSize;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCandidates))] private IReadOnlyList<CalibrationCandidate> _candidates = [];
    [ObservableProperty] private CalibrationCandidate? _selectedCandidate;
    [ObservableProperty] private CalibrationSearchRange _selectedSearchRange;
    [ObservableProperty] private bool _fitDvdPitch = true;
    [ObservableProperty] private bool _contourConfirmed;
    [ObservableProperty] private string _fitAssessment = IdleAssessment;
    [ObservableProperty] private bool _fitRejected;

    public CalibrationViewModel(MainViewModel main)
    {
        _main = main;
        PhysicalDiscSizes = [new PhotoDiscSize("Standard disc · 12 cm", 60), new PhotoDiscSize("Mini disc · 8 cm", 40)];
        _selectedPhysicalDiscSize = PhysicalDiscSizes[0];
        SearchRanges =
        [
            new CalibrationSearchRange("Automatic (recommended)", 0.02, Auto: true),
            new CalibrationSearchRange("±0.02 mm", 0.02),
            new CalibrationSearchRange("±0.1 mm", 0.1),
            new CalibrationSearchRange("±0.5 mm", 0.5),
        ];
        _selectedSearchRange = SearchRanges[0];
        main.ProjectChanged += OnProjectChanged;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsBusy))
            {
                RefreshCommands();
            }
        };
    }

    public IReadOnlyList<PhotoDiscSize> PhysicalDiscSizes { get; }

    public IReadOnlyList<CalibrationSearchRange> SearchRanges { get; }

    public CalibrationTarget? Target => _target;

    public bool HasTarget => _target is not null;

    public bool HasPhoto => PhotoBitmap is not null;

    public bool IsTraceStep => !IsPhotoStep;

    public bool HasOutliers => OutlierIndices.Count > 0;

    public bool HasCandidates => Candidates.Count > 0;

    public bool IsDvdTarget => _target?.Parameters.Kind == CalibrationDiscKind.Dvd;

    /// <summary>The raw photo while editing outlines; the rectified photo for its preview and
    /// for tracing.</summary>
    public Bitmap? CanvasImage => IsTraceStep || PreviewRectification ? RectifiedBitmap : PhotoBitmap;

    public string StepOneState => _target is null ? "Active" : HasPhoto ? "Done" : "Ready";

    public string StepTwoState => _target is null ? "Pending" : !HasPhoto ? "Next" : IsPhotoStep ? "Active" : "Done";

    public string StepThreeState => IsTraceStep ? "Active" : "Pending";

    public string TraceSummary => TracePoints.Count == 1 ? "1 point traced" : $"{TracePoints.Count} points traced";

    public string SearchRangeHeader => SelectedSearchRange.Auto ? "Search range (automatic)" : $"Search range ({SelectedSearchRange.Label})";

    public string ProjectParametersSummary => _project is null ? string.Empty : DescribeSettings(_project.Snapshot());

    public string TargetSummary
    {
        get
        {
            if (_target is not { } target)
            {
                return "No target yet.";
            }

            CalibrationParameters p = target.Parameters;
            string text = $"Target {target.Id[..8]} · {DiscFamilies.Label(CalibrationGenerator.Family(p.Kind))} · "
                + $"{Mm(p.InnerRadiusMm)}–{Mm(p.OuterRadiusMm)} mm · {p.Sectors:N0} sectors";
            if (p.Kind == CalibrationDiscKind.Dvd && (p.PitchLinear != 0 || p.PitchQuadratic != 0 || p.PitchCubic != 0))
            {
                text += " · cubic pitch";
            }

            return text;
        }
    }

    /// <summary>Applying writes the fitted radii into the open project, which only makes sense
    /// for a target of the project's own disc family.</summary>
    public string? FamilyMismatch => _target is { } target && _project is { } project
        && CalibrationGenerator.Family(target.Parameters.Kind) != project.Family
            ? $"This record is for a {DiscFamilies.Label(CalibrationGenerator.Family(target.Parameters.Kind))} disc, but the project is {project.FamilyLabel}. Open a matching project to apply it."
            : null;

    public bool HasFamilyMismatch => FamilyMismatch is not null;

    public void OnShown()
    {
        if (_target is null && _project is not null)
        {
            CreateTarget();
            // Nothing is burned yet, so the first step stays in front.
            StepOneExpanded = true;
        }
    }

    private void OnProjectChanged()
    {
        if (_project is not null)
        {
            _project.PropertyChanged -= Project_PropertyChanged;
        }

        _project = _main.Project;
        if (_project is not null)
        {
            _project.PropertyChanged += Project_PropertyChanged;
        }

        // A target built from another project's geometry means nothing for this one.
        _previewCancellation?.Cancel();
        _target = null;
        _sessionPath = null;
        OutputPath = string.Empty;
        TargetImage = null;
        ResetPhoto();
        InvalidateFit();
        StepOneExpanded = true;
        NotifyTarget();
        if (_main.CurrentPage == AppPage.Calibrate)
        {
            OnShown();
        }
    }

    private void Project_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectViewModel.GeometrySummary) or nameof(ProjectViewModel.InvertMapping))
        {
            OnPropertyChanged(nameof(ProjectParametersSummary));
        }
    }

    partial void OnStepOneExpandedChanged(bool value)
    {
        if (value)
        {
            StepTwoExpanded = StepThreeExpanded = false;
        }
    }

    partial void OnStepTwoExpandedChanged(bool value)
    {
        if (value)
        {
            StepOneExpanded = StepThreeExpanded = false;
        }
    }

    partial void OnStepThreeExpandedChanged(bool value)
    {
        if (value)
        {
            StepOneExpanded = StepTwoExpanded = false;
        }
    }

    partial void OnIsPhotoStepChanged(bool value)
    {
        ShowTargetPreview = false;
        StepTwoExpanded = value && _target is not null;
        StepThreeExpanded = !value;
        RefreshCommands();
    }

    partial void OnPhotoBitmapChanged(Bitmap? value) => ShowTargetPreview = false;

    partial void OnPreviewRectificationChanged(bool value) => RefreshCommands();

    partial void OnTracePointsChanged(IReadOnlyList<CalibrationTracePoint> value) => RefreshCommands();

    partial void OnOutlierIndicesChanged(IReadOnlyList<int> value) => UpdateFitAssessment();

    partial void OnSelectedPhysicalDiscSizeChanged(PhotoDiscSize value)
    {
        PhotoCanvasRadiusMm = value.OuterRadiusMm + 3;
        if (!_restoringPhoto)
        {
            InvalidatePhotoRectification();
        }
    }

    partial void OnFitDvdPitchChanged(bool value)
    {
        if (!_installing)
        {
            InvalidateFit();
        }
    }

    partial void OnSelectedSearchRangeChanged(CalibrationSearchRange value)
    {
        OnPropertyChanged(nameof(SearchRangeHeader));
        if (!_installing)
        {
            InvalidateFit();
        }
    }

    partial void OnSelectedCandidateChanged(CalibrationCandidate? value)
    {
        ContourConfirmed = false;
        _previewReady = false;
        UpdateFitAssessment();
        if (!_installing)
        {
            _ = ShowCandidateAsync(value);
        }

        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnContourConfirmedChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    // ---------------------------------------------------------------- Step 1: target

    private bool CanCreateTarget() => !_main.IsBusy && _project is not null;

    [RelayCommand(CanExecute = nameof(CanCreateTarget))]
    private void CreateTarget()
    {
        if (_project is null)
        {
            return;
        }

        try
        {
            CalibrationTarget target = CalibrationGenerator.CreateTarget(_project.Snapshot());
            InstallTarget(target, new CalibrationFitOptions(), _project.IsCd ? _project.Interleave : true);
            _sessionPath = null;
            OutputPath = DefaultOutputPath(target);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            _main.ShowToast(ToastKind.Error, "Cannot create the calibration target", CalibrationText.En(exception.Message));
        }
    }

    private void InstallTarget(CalibrationTarget target, CalibrationFitOptions? options, bool interleave)
    {
        _installing = true;
        try
        {
            _previewCancellation?.Cancel();
            ResetPhoto();
            _target = target;
            _cdInterleave = interleave;
            _searchCenter = options?.SearchCenter ?? target.Parameters;
            _loadedFitOptions = options ?? new CalibrationFitOptions();
            FitDvdPitch = _loadedFitOptions.FitDvdPitch;
            double savedRange = options?.RadiusRangeMm ?? 0.02;
            bool autoRange = options?.AutoRange ?? true;
            SelectedSearchRange = autoRange
                ? SearchRanges[0]
                : SearchRanges.FirstOrDefault(r => !r.Auto && r.RadiusRangeMm == savedRange)
                  ?? new CalibrationSearchRange($"±{savedRange.ToString("0.###", CultureInfo.InvariantCulture)} mm", savedRange);
            CanvasOuterRadiusMm = Math.Max(CalibrationReferencePattern.CanvasRadiusMm, target.Parameters.OuterRadiusMm);
            TargetImage = CalibrationImaging.RenderTarget(target, CanvasOuterRadiusMm).ToBitmap();
            InvalidateFit();
            NotifyTarget();
            // The target exists, so photographing the burned disc is the next step.
            StepTwoExpanded = true;
        }
        finally
        {
            _installing = false;
        }
    }

    private void NotifyTarget()
    {
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(IsDvdTarget));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(ProjectParametersSummary));
        OnPropertyChanged(nameof(StepOneState));
        OnPropertyChanged(nameof(StepTwoState));
        OnPropertyChanged(nameof(StepThreeState));
        OnPropertyChanged(nameof(FamilyMismatch));
        OnPropertyChanged(nameof(HasFamilyMismatch));
        RefreshCommands();
    }

    private string DefaultOutputPath(CalibrationTarget target)
        => Path.Combine(_project?.Folder
                ?? (_project is { OutputPath: { } output } && Path.IsPathRooted(output) ? Path.GetDirectoryName(output) : null)
                ?? AppPaths.DocumentsDirectory,
            $"calibration-{target.Id[..8]}{CalibrationGenerator.DefaultExtension(target.Parameters.Kind)}");

    private bool CanUseTarget() => !_main.IsBusy && _target is not null;

    [RelayCommand(CanExecute = nameof(CanUseTarget))]
    private async Task BrowseOutput()
    {
        if (_target is null || _main.Ui is null)
        {
            return;
        }

        bool cd = _target.Parameters.Kind == CalibrationDiscKind.Cd;
        string current = string.IsNullOrWhiteSpace(OutputPath) ? DefaultOutputPath(_target) : OutputPath;
        string? path = await _main.Ui.PickSaveFileAsync("Choose where to write the calibration disc", Path.GetFileName(current),
            cd ? "wav" : "iso", cd ? [FileTypes.Wave, FileTypes.Raw] : [FileTypes.Iso], Path.GetDirectoryName(current));
        if (path is null)
        {
            return;
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (cd && extension is not (".wav" or ".raw"))
        {
            path += ".wav";
        }
        else if (!cd && extension != ".iso")
        {
            path += ".iso";
        }

        OutputPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanUseTarget))]
    private async Task Generate() => await GenerateAsync();

    /// <summary>Generates the target file (if it is not already there) and hands it to the
    /// Burn page. The record is saved next to it first, so the photo step can resume later.</summary>
    [RelayCommand(CanExecute = nameof(CanUseTarget))]
    private async Task BurnTarget()
    {
        if (_target is null)
        {
            return;
        }

        string path = ResolvedOutputPath();
        if (!File.Exists(path) || !File.Exists(CalibrationGenerator.SessionPathFor(path)))
        {
            if (!await GenerateAsync())
            {
                return;
            }
        }

        _main.Burn.UseSource(path, CalibrationGenerator.Family(_target.Parameters.Kind));
        _main.NavigateCommand.Execute(AppPage.Burn);
    }

    private string ResolvedOutputPath()
    {
        if (string.IsNullOrWhiteSpace(OutputPath) && _target is not null)
        {
            OutputPath = DefaultOutputPath(_target);
        }

        return AppPaths.ResolveUserPath(OutputPath, _project?.Folder);
    }

    private async Task<bool> GenerateAsync()
    {
        if (_target is not { } target || _main.Ui is null)
        {
            return false;
        }

        string path = ResolvedOutputPath();
        if (File.Exists(path)
            && await _main.Ui.AskAsync("Replace existing file?", $"{AppPaths.ShortenHome(path)} already exists and will be overwritten.",
                "Replace", null, "Cancel", danger: true) != DialogChoice.Primary)
        {
            return false;
        }

        CalibrationSession session = Session();
        bool ok = await _main.RunJobAsync("Generating calibration disc", async job =>
        {
            job.Log($"Target {target.Id} → {path}");
            await Task.Run(() => CalibrationGenerator.Generate(session, path, fraction => job.Progress(fraction), job.Token), job.Token);
            job.Log("Wrote " + CalibrationGenerator.SessionPathFor(path));
        }, cancellable: true);
        if (ok)
        {
            _sessionPath = CalibrationGenerator.SessionPathFor(path);
            _main.ShowToast(ToastKind.Success, "Calibration disc written",
                "Burn it, photograph the recorded side, then import the photo in step 2. The .calibration.json record next to it restores this target later.");
        }

        return ok;
    }

    [RelayCommand(CanExecute = nameof(CanUseTarget))]
    private async Task ExportPng()
    {
        if (_target is not { } target || _main.Ui is null)
        {
            return;
        }

        string? path = await _main.Ui.PickSaveFileAsync("Export the calibration pattern", $"calibration-{target.Id[..8]}.png", "png",
            [new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
            _project?.Folder);
        if (path is null)
        {
            return;
        }

        CalibrationSession session = Session();
        await _main.RunJobAsync("Exporting calibration pattern", async job =>
        {
            await Task.Run(() =>
            {
                CalibrationImaging.ExportPatternPng(target, path, job.Token);
                CalibrationSessionJson.Save(Path.ChangeExtension(path, ".calibration.json"), session);
            }, job.Token);
        }, cancellable: true);
    }

    [RelayCommand(CanExecute = nameof(CanUseTarget))]
    private async Task SaveSession()
    {
        if (_target is not { } target || _main.Ui is null)
        {
            return;
        }

        string suggested = _sessionPath is not null ? Path.GetFileName(_sessionPath) : $"calibration-{target.Id[..8]}.calibration.json";
        string? path = await _main.Ui.PickSaveFileAsync("Save calibration record", suggested, "json", [CalibrationRecordType],
            _sessionPath is not null ? Path.GetDirectoryName(_sessionPath) : _project?.Folder);
        if (path is null)
        {
            return;
        }

        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            path += ".calibration.json";
        }

        try
        {
            CalibrationSessionJson.Save(path, Session());
            _sessionPath = path;
            _main.ShowToast(ToastKind.Success, "Calibration record saved", "Saved the target, photo, outlines and traced points.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _main.ShowToast(ToastKind.Error, "Cannot save the calibration record", CalibrationText.En(exception.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateTarget))]
    private async Task OpenSession()
    {
        if (_main.Ui is null)
        {
            return;
        }

        IReadOnlyList<string> files = await _main.Ui.PickFilesAsync("Open calibration record", [CalibrationRecordType, FileTypes.All],
            _sessionPath is not null ? Path.GetDirectoryName(_sessionPath) : _project?.Folder, allowMultiple: false);
        if (files.Count == 1)
        {
            await LoadSessionAsync(files[0]);
        }
    }

    private static readonly Avalonia.Platform.Storage.FilePickerFileType CalibrationRecordType =
        new("Calibration record") { Patterns = ["*.calibration.json", "*.json"] };

    internal CalibrationSession Session() => new(
        _target ?? throw new InvalidOperationException("Create a target first."),
        [], FitOptions(), _cdInterleave, PhotoSession());

    private CalibrationPhotoSession? PhotoSession() => _photoPng is null
        ? null
        : new CalibrationPhotoSession(_photoPng,
            Boundary(CalibrationBoundaryKind.OuterEdge), Boundary(CalibrationBoundaryKind.CenterHole),
            SelectedPhysicalDiscSize.OuterRadiusMm, (_photoTransform is not null && TracePoints.Count > 0) || IsTraceStep,
            CrossTraces());

    public async Task LoadSessionAsync(string path)
    {
        await _main.RunJobAsync("Opening calibration record", async job =>
        {
            var prepared = await Task.Run(() =>
            {
                CalibrationSession session = CalibrationSessionJson.Load(path);
                CalibrationGenerator.Validate(session.Target.Parameters);
                BgraImage? image = null, rectified = null;
                CalibrationPhotoFitResult? fit = null;
                if (session.Photo is { } photo)
                {
                    image = CalibrationImaging.DecodePngBase64(photo.ImagePngBase64);
                    if (photo.OuterBoundary.Concat(photo.HoleBoundary).Any(p => p.X >= image.Width || p.Y >= image.Height))
                    {
                        throw new ArgumentException("The calibration outline is outside the photo.");
                    }

                    if (photo.RectificationConfirmed)
                    {
                        fit = CalibrationPhotoRectifier.Fit(photo.OuterBoundary, photo.HoleBoundary, photo.PhysicalOuterRadiusMm, HoleRadiusMm, job.Token);
                        if (!fit.Succeeded || fit.Transform is null)
                        {
                            throw new ArgumentException(CalibrationText.En(fit.Message));
                        }

                        rectified = CalibrationImaging.Rectify(image, fit.Transform, photo.PhysicalOuterRadiusMm + 3, CalibrationImaging.RectifiedSize, job.Token);
                    }
                }

                return (Session: session, Image: image, Rectified: rectified, Fit: fit,
                    ImageBitmap: image?.ToBitmap(), RectifiedBitmap: rectified?.ToBitmap());
            }, job.Token);

            CalibrationSession loaded = prepared.Session;
            InstallTarget(loaded.Target, loaded.FitOptions, loaded.CdInterleave);
            if (loaded.Photo is { } photo)
            {
                _restoringPhoto = true;
                try
                {
                    SelectedPhysicalDiscSize = PhysicalDiscSizes.FirstOrDefault(s => s.OuterRadiusMm == photo.PhysicalOuterRadiusMm)
                        ?? PhysicalDiscSizes[0];
                }
                finally
                {
                    _restoringPhoto = false;
                }

                _photo = prepared.Image;
                _photoPng = photo.ImagePngBase64;
                PhotoBitmap = prepared.ImageBitmap;
                RectifiedBitmap = prepared.RectifiedBitmap;
                _photoTransform = prepared.Fit?.Transform;
                SetBoundaryPoints(photo.OuterBoundary, photo.HoleBoundary);
                IsRectificationReady = _photoTransform is not null;
                if (_photoTransform is not null)
                {
                    BoundaryContours = Contours(_photoTransform, photo.PhysicalOuterRadiusMm);
                }

                TracePoints = photo.Traces.SelectMany(t => t.Points).Select((p, i) => new CalibrationTracePoint(i, p.X, p.Y))
                    .OrderBy(p => p.XMm * p.XMm + p.YMm * p.YMm).ToArray();
                IsPhotoStep = !photo.RectificationConfirmed;
                RectificationSummary = prepared.Fit is { } restored ? RectificationText(restored) : string.Empty;
            }

            _sessionPath = path;
            OutputPath = Path.ChangeExtension(path.EndsWith(".calibration.json", StringComparison.OrdinalIgnoreCase)
                ? path[..^".calibration.json".Length] : path, CalibrationGenerator.DefaultExtension(loaded.Target.Parameters.Kind));
            job.Log($"Restored target {loaded.Target.Id} from {path}");
            RefreshCommands();
        }, cancellable: true);
    }

    // ---------------------------------------------------------------- Step 2: photo

    [RelayCommand(CanExecute = nameof(CanUseTarget))]
    private async Task ImportPhoto()
    {
        if (_main.Ui is null)
        {
            return;
        }

        IReadOnlyList<string> files = await _main.Ui.PickFilesAsync("Import a photo of the burned calibration disc",
            [new Avalonia.Platform.Storage.FilePickerFileType("Photos and scans") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.JPG", "*.JPEG", "*.PNG"] }, FileTypes.All],
            _main.Settings.LastImageDirectory, allowMultiple: false);
        if (files.Count == 1)
        {
            await ImportPhotoFromAsync(files[0]);
        }
    }

    public async Task ImportPhotoFromAsync(string path)
    {
        await _main.RunJobAsync("Reading photo", async job =>
        {
            var result = await Task.Run(() =>
            {
                BgraImage image = CalibrationImaging.LoadPhoto(path, CalibrationImaging.PhotoMaxDimension, job.Token);
                CalibrationPhotoContours contours = CalibrationImaging.SuggestContours(image.Width, image.Height);
                return (Image: image, Contours: contours, Png: CalibrationImaging.EncodePngBase64(image), Bitmap: image.ToBitmap());
            }, job.Token);
            InvalidatePhotoRectification();
            _photo = result.Image;
            _photoPng = result.Png;
            PhotoBitmap = result.Bitmap;
            SetBoundaryPoints(result.Contours.Outer, result.Contours.Hole);
            _main.Settings.LastImageDirectory = Path.GetDirectoryName(path);
            job.Log("Drag the blue points onto the disc's physical outer edge and the orange points onto the centre hole, then calculate the rectification.");
            RefreshCommands();
        }, cancellable: true);
    }

    private bool CanRectify() => CanUseTarget() && _photo is not null && IsPhotoStep && BoundaryPoints.Count >= 10;

    [RelayCommand(CanExecute = nameof(CanRectify))]
    private async Task Rectify()
    {
        if (_photo is not { } image)
        {
            return;
        }

        CalibrationPoint[] outer = Boundary(CalibrationBoundaryKind.OuterEdge);
        CalibrationPoint[] hole = Boundary(CalibrationBoundaryKind.CenterHole);
        double radius = SelectedPhysicalDiscSize.OuterRadiusMm;
        double canvasRadius = PhotoCanvasRadiusMm;
        long revision = _photoRevision;
        await _main.RunJobAsync("Rectifying the photo", async job =>
        {
            CalibrationPhotoFitResult fit = await Task.Run(() => CalibrationPhotoRectifier.Fit(outer, hole, radius, HoleRadiusMm, job.Token), job.Token);
            if (!fit.Succeeded || fit.Transform is null)
            {
                throw new ArgumentException(CalibrationText.En(fit.Message));
            }

            Bitmap corrected = await Task.Run(() => CalibrationImaging.Rectify(image, fit.Transform, canvasRadius, CalibrationImaging.RectifiedSize, job.Token).ToBitmap(), job.Token);
            if (revision != _photoRevision || !ReferenceEquals(image, _photo))
            {
                throw new OperationCanceledException();
            }

            _photoTransform = fit.Transform;
            RectifiedBitmap = corrected;
            BoundaryContours = Contours(fit.Transform, radius);
            RectificationSummary = RectificationText(fit);
            IsRectificationReady = true;
            PreviewRectification = true;
            job.Log("Rectification ready. Check that the disc looks round and face-on, then confirm and trace a curve.");
            RefreshCommands();
        }, cancellable: true);
    }

    private static string RectificationText(CalibrationPhotoFitResult fit)
        => $"Outer edge off by {fit.OuterRmsPixels.ToString("0.0", CultureInfo.InvariantCulture)} px · hole off by {fit.HoleRmsPixels.ToString("0.0", CultureInfo.InvariantCulture)} px";

    private bool CanConfirmRectification() => !_main.IsBusy && IsPhotoStep && IsRectificationReady && _photoTransform is not null;

    [RelayCommand(CanExecute = nameof(CanConfirmRectification))]
    private void ConfirmRectification()
    {
        PreviewRectification = false;
        IsPhotoStep = false;
        RefreshCommands();
    }

    private bool CanGoBackToPhoto() => !_main.IsBusy && IsTraceStep;

    [RelayCommand(CanExecute = nameof(CanGoBackToPhoto))]
    private void BackToPhoto()
    {
        IsPhotoStep = true;
        PreviewRectification = false;
        ContourConfirmed = false;
    }

    /// <summary>Called by the canvas when an outline point is dropped somewhere new.</summary>
    public void EditBoundary(CalibrationBoundaryKind kind, int index, double x, double y)
    {
        if (_photo is not { } image || !IsPhotoStep || PreviewRectification || _main.IsBusy || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }

        if (!BoundaryPoints.Any(p => p.Boundary == kind && p.Index == index))
        {
            return;
        }

        CalibrationBoundaryPoint[] updated = BoundaryPoints.Select(p => p.Boundary == kind && p.Index == index
            ? p with { X = Math.Clamp(x, 0, image.Width - 1), Y = Math.Clamp(y, 0, image.Height - 1) }
            : p).ToArray();
        InvalidatePhotoRectification();
        BoundaryPoints = updated;
    }

    private void SetBoundaryPoints(IReadOnlyList<CalibrationPoint> outer, IReadOnlyList<CalibrationPoint> hole)
        => BoundaryPoints = outer.Select((p, i) => new CalibrationBoundaryPoint(CalibrationBoundaryKind.OuterEdge, i, p.X, p.Y))
            .Concat(hole.Select((p, i) => new CalibrationBoundaryPoint(CalibrationBoundaryKind.CenterHole, i, p.X, p.Y))).ToArray();

    private CalibrationPoint[] Boundary(CalibrationBoundaryKind kind) => BoundaryPoints
        .Where(p => p.Boundary == kind).OrderBy(p => p.Index).Select(p => new CalibrationPoint(p.X, p.Y)).ToArray();

    private static Point[][] Contours(CalibrationPhotoTransform transform, double radius)
        => new[] { radius, HoleRadiusMm }.Select(r => Enumerable.Range(0, 181).Select(i =>
        {
            double angle = i * 2 * Math.PI / 180;
            CalibrationPoint point = transform.DiscToSource(new CalibrationPoint(r * Math.Cos(angle), r * Math.Sin(angle)));
            return new Point(point.X, point.Y);
        }).ToArray()).ToArray();

    private void ResetPhoto()
    {
        _photoRevision++;
        _photoTransform = null;
        _photo = null;
        _photoPng = null;
        PhotoBitmap = null;
        RectifiedBitmap = null;
        BoundaryPoints = [];
        BoundaryContours = [];
        TracePoints = [];
        PredictionContours = [];
        IsRectificationReady = false;
        PreviewRectification = false;
        IsPhotoStep = true;
        RectificationSummary = string.Empty;
        RefreshCommands();
    }

    private void InvalidatePhotoRectification()
    {
        _photoRevision++;
        _photoTransform = null;
        RectifiedBitmap = null;
        IsRectificationReady = false;
        PreviewRectification = false;
        IsPhotoStep = true;
        BoundaryContours = [];
        TracePoints = [];
        InvalidateFit();
        RectificationSummary = string.Empty;
        RefreshCommands();
    }

    // ---------------------------------------------------------------- Step 3: trace and fit

    /// <summary>Called by the canvas: index -1 adds a point, an existing index moves or (with
    /// <paramref name="remove"/>) deletes that point.</summary>
    public void EditTrace(int index, double xMm, double yMm, bool remove)
    {
        if (!IsTraceStep || _main.IsBusy || _photoTransform is null || !double.IsFinite(xMm) || !double.IsFinite(yMm))
        {
            return;
        }

        double radius = Math.Sqrt(xMm * xMm + yMm * yMm);
        if (!remove && (radius < HoleRadiusMm || radius > SelectedPhysicalDiscSize.OuterRadiusMm))
        {
            return;
        }

        List<CalibrationTracePoint> points = [.. TracePoints];
        int at = points.FindIndex(p => p.Index == index);
        if (remove)
        {
            if (at < 0)
            {
                return;
            }

            points.RemoveAt(at);
        }
        else if (at >= 0)
        {
            points[at] = new CalibrationTracePoint(index, xMm, yMm);
        }
        else
        {
            if (index != -1 || points.Count >= 4000)
            {
                return;
            }

            int id = points.Count == 0 ? 0 : points.Max(p => p.Index) + 1;
            points.Add(new CalibrationTracePoint(id, xMm, yMm));
        }

        // Points are not a timed pen stroke: radial order lets a click fill any gap.
        TracePoints = points.OrderBy(p => p.XMm * p.XMm + p.YMm * p.YMm).ToArray();
        InvalidateFit();
    }

    private bool CanClearTraces() => !_main.IsBusy && TracePoints.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearTraces))]
    private void ClearTraces()
    {
        TracePoints = [];
        InvalidateFit();
    }

    private IReadOnlyList<CalibrationCrossTrace> CrossTraces() => TracePoints.Count == 0
        ? []
        : [new CalibrationCrossTrace(0, TracePoints.Select(p => new CalibrationPoint(p.XMm, p.YMm)).ToArray())];

    private CalibrationFitOptions FitOptions() => _loadedFitOptions with
    {
        SearchCenter = _searchCenter,
        RadiusRangeMm = SelectedSearchRange.RadiusRangeMm,
        FitDvdPitch = FitDvdPitch,
        AdaptiveRobustScale = true,
        AutoRange = SelectedSearchRange.Auto,
    };

    private bool CanSolve() => CanUseTarget() && IsTraceStep && _photoTransform is not null && TracePoints.Count >= 6;

    [RelayCommand(CanExecute = nameof(CanSolve))]
    private async Task Solve()
    {
        if (_target is not { } target)
        {
            return;
        }

        InvalidateFit();
        long revision = _inputRevision;
        IReadOnlyList<CalibrationCrossTrace> traces = CrossTraces();
        CalibrationFitOptions options = FitOptions();
        bool auto = SelectedSearchRange.Auto;
        int count = TracePoints.Count;
        await _main.RunJobAsync($"Fitting the disc parameters to {count} points", async job =>
        {
            FitAttempt attempt = await Task.Run(() => FitWithAutoRange(target, traces, options, auto, job.Token), job.Token);
            if (revision != _inputRevision || !ReferenceEquals(target, _target))
            {
                throw new OperationCanceledException();
            }

            CalibrationFitResult result = attempt.Result;
            _fitRangeMm = attempt.RangeMm;
            _fitRangeExpanded = attempt.Expanded;
            _fit = result;
            ContourConfirmed = false;
            Candidates = result.Candidates.Select((c, i) => new CalibrationCandidate(
                $"Candidate {i + 1} · mean error {ErrorMm(c.RmsErrorMm)} mm",
                c.Parameters, c.RotationRadians, c.RmsErrorMm, c.MaxErrorMm)).ToArray();
            _installing = true;
            try
            {
                SelectedCandidate = Candidates.FirstOrDefault();
            }
            finally
            {
                _installing = false;
            }

            await ShowCandidateAsync(SelectedCandidate);
            UpdateFitAssessment();
            foreach (string warning in result.Warnings)
            {
                job.Log("  " + CalibrationText.En(warning));
            }

            if (result.Succeeded && SelectedCandidate is { } best)
            {
                job.Log($"Best: inner {Mm(best.Parameters.InnerRadiusMm)} mm, outer {Mm(best.Parameters.OuterRadiusMm)} mm, mean error {ErrorMm(best.RmsErrorMm)} mm"
                    + (attempt.Expanded ? $", search widened to ±{attempt.RangeMm:0.###} mm" : string.Empty));
            }
        }, cancellable: true);
    }

    /// <summary>Automatic mode widens the radius box until the optimum is interior: a result
    /// clamped to the box edge is not a measurement. Same steps as the Windows page.</summary>
    internal static FitAttempt FitWithAutoRange(CalibrationTarget target, IReadOnlyList<CalibrationCrossTrace> traces,
        CalibrationFitOptions options, bool auto, CancellationToken token)
    {
        if (!auto)
        {
            return new FitAttempt(CalibrationCrossFitter.Fit(target, traces, options, token), options.RadiusRangeMm, false);
        }

        double[] ranges = [options.RadiusRangeMm, 0.1, 0.5, 2, 5];
        CalibrationFitResult? best = null, last = null;
        double bestRange = options.RadiusRangeMm;
        foreach (double range in ranges.Where(range => range >= options.RadiusRangeMm).Distinct().OrderBy(range => range))
        {
            token.ThrowIfCancellationRequested();
            CalibrationFitResult result = CalibrationCrossFitter.Fit(target, traces, options with { RadiusRangeMm = range }, token);
            last = result;
            // BD curves continue past the drawing band, so a narrow box can return an interior
            // optimum with an unusable residual; keep widening while it is that poor.
            bool unusableBluRay = target.Parameters.Kind == CalibrationDiscKind.BluRay && result.RmsErrorMm > 0.8;
            if (result.Succeeded && !result.BoundaryLimited && !unusableBluRay)
            {
                return new FitAttempt(result, range, range > options.RadiusRangeMm);
            }

            if (result.Succeeded && (best is null || result.RmsErrorMm < best.RmsErrorMm))
            {
                best = result;
                bestRange = range;
            }

            if (!result.Succeeded && IsInputFailure(result))
            {
                break;
            }
        }

        CalibrationFitResult fallback = best ?? last ?? CalibrationCrossFitter.Fit(target, traces, options, token);
        return new FitAttempt(fallback, bestRange, bestRange > options.RadiusRangeMm);
    }

    // Core reports these input problems in Chinese; widening the box cannot fix them.
    private static bool IsInputFailure(CalibrationFitResult result) => !result.Succeeded
        && result.Warnings.Any(warning =>
            warning.Contains("至少标出", StringComparison.Ordinal)
            || warning.Contains("覆盖范围太窄", StringComparison.Ordinal)
            || warning.Contains("向内回退", StringComparison.Ordinal)
            || warning.Contains("接近半圈", StringComparison.Ordinal)
            || warning.Contains("转角过大", StringComparison.Ordinal));

    /// <summary>A 0.35 mm mean error is already a visible gap along the whole curve; the max
    /// bound catches a single misplaced click. Same thresholds as Windows.</summary>
    internal static bool ResidualAccepted(CalibrationCandidate candidate)
        => double.IsFinite(candidate.RmsErrorMm) && double.IsFinite(candidate.MaxErrorMm)
           && candidate.RmsErrorMm <= 0.35 && candidate.MaxErrorMm <= 0.8;

    private void UpdateFitAssessment()
    {
        FitRejected = false;
        if (_fit is null)
        {
            FitAssessment = IdleAssessment;
            return;
        }

        if (!_fit.Succeeded)
        {
            FitRejected = true;
            FitAssessment = _fit.Warnings.Count > 0 ? CalibrationText.En(_fit.Warnings[0]) : "The fit failed. All points were kept.";
            return;
        }

        if (SelectedCandidate is not { } candidate)
        {
            FitAssessment = "No candidate to apply.";
            return;
        }

        bool accepted = ResidualAccepted(candidate);
        FitRejected = !accepted;
        string mean = ErrorMm(candidate.RmsErrorMm);
        string max = ErrorMm(candidate.MaxErrorMm);
        string text = accepted
            ? $"Inner {Mm(candidate.Parameters.InnerRadiusMm)} mm · outer {Mm(candidate.Parameters.OuterRadiusMm)} mm\nMean error {mean} mm · max {max} mm. Check the orange curve against your points."
            : $"Not usable: mean error {mean} mm · max {max} mm (limits 0.35 / 0.8 mm). Fix the red points or add more points, then fit again.";
        if (_fitRangeExpanded)
        {
            text += $"\nThe search range was widened automatically to ±{_fitRangeMm.ToString("0.###", CultureInfo.InvariantCulture)} mm.";
        }

        if (_fit.BoundaryLimited)
        {
            text += "\nThe best result is near the edge of the search range; add points or check their positions.";
        }

        if (OutlierIndices.Count > 0)
        {
            text += $"\n{OutlierIndices.Count} point{(OutlierIndices.Count == 1 ? " is" : "s are")} far from the prediction (red).";
        }

        FitAssessment = text;
    }

    private async Task ShowCandidateAsync(CalibrationCandidate? candidate)
    {
        _previewCancellation?.Cancel();
        _previewReady = false;
        PredictionContours = [];
        OutlierIndices = [];
        ApplyCommand.NotifyCanExecuteChanged();
        if (candidate is null || _target is not { } target)
        {
            return;
        }

        IReadOnlyList<CalibrationTracePoint> snapshot = TracePoints;
        double[] radii = snapshot.Select(p => Math.Sqrt(p.XMm * p.XMm + p.YMm * p.YMm)).ToArray();
        if (radii.Length < 2 || !(radii.Max() > radii.Min()))
        {
            return;
        }

        double minimum = radii.Min(), maximum = radii.Max();
        using CancellationTokenSource cancellation = new();
        _previewCancellation = cancellation;
        long revision = _inputRevision;
        try
        {
            var preview = await Task.Run(() =>
            {
                IReadOnlyList<CalibrationPoint> prediction = CalibrationCrossPrediction.GetOutline(target, candidate.Parameters,
                    minimum, maximum, candidate.RotationRadians, cancellationToken: cancellation.Token);
                // A point beyond a small multiple of the mean residual is visibly off the
                // predicted curve, not measurement noise.
                double threshold = Math.Max(0.2, candidate.RmsErrorMm * 2.5);
                int[] outliers = snapshot.Where(p => DistanceToPolyline(p, prediction) > threshold)
                    .Select(p => p.Index).OrderBy(i => i).ToArray();
                return (Contour: prediction.Select(p => new Point(p.X, p.Y)).ToArray(), Outliers: outliers);
            }, cancellation.Token);
            if (revision != _inputRevision || !ReferenceEquals(_target, target) || !ReferenceEquals(SelectedCandidate, candidate))
            {
                return;
            }

            PredictionContours = [preview.Contour];
            OutlierIndices = preview.Outliers;
            _previewReady = true;
            UpdateFitAssessment();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _main.ShowToast(ToastKind.Error, "Cannot draw the predicted curve", CalibrationText.En(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
            }

            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    private static double DistanceToPolyline(CalibrationTracePoint point, IReadOnlyList<CalibrationPoint> line)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < line.Count; i++)
        {
            double ax = line[i - 1].X, ay = line[i - 1].Y;
            double dx = line[i].X - ax, dy = line[i].Y - ay;
            double t = Math.Clamp(((point.XMm - ax) * dx + (point.YMm - ay) * dy) / Math.Max(1e-30, dx * dx + dy * dy), 0, 1);
            double ex = point.XMm - ax - t * dx, ey = point.YMm - ay - t * dy;
            best = Math.Min(best, ex * ex + ey * ey);
        }

        return Math.Sqrt(best);
    }

    private bool CanApply() => !_main.IsBusy && IsTraceStep && _photoTransform is not null && _fit?.Succeeded == true
        && SelectedCandidate is { } selected && ContourConfirmed && _previewReady && ResidualAccepted(selected)
        && Candidates.Contains(selected) && !HasFamilyMismatch && _project is not null;

    /// <summary>Writes the fitted geometry into the project (radii and sector count; DVD
    /// pitch; BD polarity). The target record keeps its original snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!CanApply() || SelectedCandidate?.Parameters is not { } p || _project is null)
        {
            return;
        }

        _project.ApplyGeometry(_project.Name, p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors,
            p.PitchLinear, p.PitchQuadratic, p.PitchCubic, p.BluRayInvertMapping);
        _main.AppendLog($"[{DateTime.Now:HH:mm:ss}] Calibration {_target?.Id}: applied inner {p.InnerRadiusMm:G17} mm, outer {p.OuterRadiusMm:G17} mm"
            + (p.Kind == CalibrationDiscKind.Dvd ? $", pitch {p.PitchLinear:G17} / {p.PitchQuadratic:G17} / {p.PitchCubic:G17}" : string.Empty));
        _main.ShowToast(ToastKind.Success, "Calibration applied to the project",
            $"Inner {Mm(p.InnerRadiusMm)} mm, outer {Mm(p.OuterRadiusMm)} mm. Save the project to keep it; new exports use the calibrated geometry.");
        OnPropertyChanged(nameof(ProjectParametersSummary));
    }

    private void InvalidateFit()
    {
        _inputRevision++;
        _fit = null;
        ContourConfirmed = false;
        _previewCancellation?.Cancel();
        _previewReady = false;
        PredictionContours = [];
        OutlierIndices = [];
        Candidates = [];
        SelectedCandidate = null;
        _fitRangeExpanded = false;
        FitRejected = false;
        FitAssessment = IdleAssessment;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCommands()
    {
        CreateTargetCommand.NotifyCanExecuteChanged();
        BrowseOutputCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        BurnTargetCommand.NotifyCanExecuteChanged();
        ExportPngCommand.NotifyCanExecuteChanged();
        SaveSessionCommand.NotifyCanExecuteChanged();
        OpenSessionCommand.NotifyCanExecuteChanged();
        ImportPhotoCommand.NotifyCanExecuteChanged();
        RectifyCommand.NotifyCanExecuteChanged();
        ConfirmRectificationCommand.NotifyCanExecuteChanged();
        BackToPhotoCommand.NotifyCanExecuteChanged();
        ClearTracesCommand.NotifyCanExecuteChanged();
        SolveCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeSettings(DiscSettings settings)
    {
        string text = $"{DiscFamilies.Label(settings.Family)} · {Mm(settings.InnerRadiusMm)}–{Mm(settings.OuterRadiusMm)} mm · {settings.Sectors:N0} sectors";
        if (settings.Family == OpticalDiscFamily.Dvd && (settings.PitchLinear != 0 || settings.PitchQuadratic != 0 || settings.PitchCubic != 0))
        {
            text += " · cubic pitch";
        }

        return text;
    }

    private static string Mm(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Residuals to the micrometre; a perfect trace reads "&lt; 0.001", not "0".</summary>
    private static string ErrorMm(double value)
        => value < 0.0005 ? "< 0.001" : value.ToString("0.###", CultureInfo.InvariantCulture);

    internal sealed record FitAttempt(CalibrationFitResult Result, double RangeMm, bool Expanded);
}
