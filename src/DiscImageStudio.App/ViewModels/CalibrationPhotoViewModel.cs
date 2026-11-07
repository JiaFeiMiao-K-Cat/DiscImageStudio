using DiscImageStudio.Localization;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private CalibrationPhotoTransform? _photoTransform;
    // Kept so the rectification summary can be re-worded after a language switch.
    private CalibrationPhotoFitResult? _rectificationFit;
    private string? _photoPng;
    private bool _restoringPhoto;
    private long _photoRevision;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTraceStep))]
    private bool _isPhotoStep = true;
    public bool IsTraceStep => !IsPhotoStep;
    [ObservableProperty] private BitmapSource? _photoImage;
    [ObservableProperty] private BitmapSource? _rectifiedPhoto;
    [ObservableProperty] private bool _isRectificationReady;
    [ObservableProperty] private bool _previewRectification;
    [ObservableProperty] private double _photoCanvasRadiusMm = 63;
    [ObservableProperty] private IReadOnlyList<CalibrationPhotoBoundaryPoint> _photoBoundaryPoints = [];
    [ObservableProperty] private IReadOnlyList<Point[]> _photoBoundaryContours = [];
    [ObservableProperty] private IReadOnlyList<CalibrationTracePoint> _tracePoints = [];
    [ObservableProperty] private IReadOnlyList<Point[]> _crossPredictionContours = [];
    /// <summary>Trace-point identities whose distance to the selected prediction exceeds the
    /// noise allowance. They stay in the fit (robustly down-weighted) and are painted in the
    /// warning colour so the user can fix the click instead of guessing at a mean error.</summary>
    [ObservableProperty] private IReadOnlyList<int> _outlierTracePointIndices = [];
    public bool HasOutlierPoints => OutlierTracePointIndices.Count > 0;
    partial void OnOutlierTracePointIndicesChanged(IReadOnlyList<int> value)
    {
        OnPropertyChanged(nameof(HasOutlierPoints));
        UpdateFitAssessment();
    }
    [ObservableProperty] private int _selectedTraceArm;
    /// <summary>Outer/inner-edge deviations of the last rectification; empty until a fit
    /// exists. This is the only line under 确认矫正.</summary>
    [ObservableProperty] private string _rectificationSummary = string.Empty;
    public string TraceSummary => HasBoundarySegments
        ? L.Format("HalfDisc.Summary", TracePoints.Select(p => p.Arm).Distinct().Count(), TracePoints.Count)
        : L.F($"已保留 {TracePoints.Count} 点 · 偏离点自动标红");
    public IReadOnlyList<PhotoDiscSize> PhysicalDiscSizeChoices { get; } = PhotoDiscSizes;
    // The default must be the same instance the ComboBox lists: PhotoDiscSize is a reference
    // type, and a separate equal-looking object leaves SelectedIndex at -1. Selecting the
    // listed item later would then look like a fresh value and invalidate the rectification.
    [ObservableProperty] private PhotoDiscSize _selectedPhysicalDiscSize = PhotoDiscSizes[0];
    private static readonly IReadOnlyList<PhotoDiscSize> PhotoDiscSizes = [new(60), new(40)];

    public IAsyncRelayCommand ImportPhotoCommand { get; private set; } = null!;
    public IAsyncRelayCommand RectifyPhotoCommand { get; private set; } = null!;
    public RelayCommand ConfirmRectificationCommand { get; private set; } = null!;
    public RelayCommand BackToPhotoCommand { get; private set; } = null!;
    public RelayCommand ClearSelectedTraceCommand { get; private set; } = null!;
    public RelayCommand ClearTracesCommand { get; private set; } = null!;
    public RelayCommand<CalibrationBoundaryEdit> BoundaryEditedCommand { get; private set; } = null!;
    public RelayCommand<CalibrationTraceEdit> TraceEditedCommand { get; private set; } = null!;

    private void InitializePhotoCommands()
    {
        ImportPhotoCommand = new AsyncRelayCommand(ImportPhotoAsync, () => CanUseTarget() && TargetMatchesStage);
        RectifyPhotoCommand = new AsyncRelayCommand(RectifyPhotoAsync,
            () => CanUseTarget() && TargetMatchesStage && PhotoImage is not null && IsPhotoStep && PhotoBoundaryPoints.Count >= 10);
        ConfirmRectificationCommand = new RelayCommand(ConfirmRectification,
            () => !IsBusy && TargetMatchesStage && IsPhotoStep && IsRectificationReady && _photoTransform is not null);
        BackToPhotoCommand = new RelayCommand(() =>
        {
            IsPhotoStep = true;
            PreviewRectification = false;
            ContourConfirmed = false;
        }, () => !IsBusy && IsTraceStep);
        ClearTracesCommand = new RelayCommand(ClearTraces, () => !IsBusy && IsLineFitStep && TracePoints.Count > 0);
        ClearSelectedTraceCommand = new RelayCommand(ClearTraces, () => !IsBusy && IsLineFitStep && TracePoints.Count > 0);
        BoundaryEditedCommand = new RelayCommand<CalibrationBoundaryEdit>(EditBoundary,
            _ => !IsBusy && IsPhotoStep && !PreviewRectification && PhotoImage is not null);
        TraceEditedCommand = new RelayCommand<CalibrationTraceEdit>(EditTrace,
            _ => !IsBusy && IsLineFitStep && _photoTransform is not null);
        InitializeBoundaryCommands();
    }

    private bool CanSolveTrace() => CanUseTarget() && IsLineFitStep && _photoTransform is not null
        && (HasBoundarySegments ? HasEnoughBoundaryPoints() : TracePoints.Count >= 6);

    // A new (or reset) picture always lands in boundary-editing view: the burn-pattern
    // overlay belongs to inspecting the target, not the photo.
    partial void OnPhotoImageChanged(BitmapSource? value) => ShowTargetPreview = false;

    partial void OnIsPhotoStepChanged(bool value)
    {
        RefreshCommands();
        ShowTargetPreview = false;
        OnPropertyChanged(nameof(StepTwoState));
        OnPropertyChanged(nameof(StepThreeState));
        OnPropertyChanged(nameof(IsRingStep));
        OnPropertyChanged(nameof(IsClockStep));
        OnPropertyChanged(nameof(IsRadialArcStep));
        NotifyTraceStage();
        // The step column discloses one stage at a time: entering the trace step collapses
        // the rectification card and vice versa.
        StepTwoExpanded = IsPhotoStep;
        StepThreeExpanded = IsTraceStep;
    }
    partial void OnPreviewRectificationChanged(bool value) => RefreshPhotoCommands();
    partial void OnTracePointsChanged(IReadOnlyList<CalibrationTracePoint> value)
    {
        OnPropertyChanged(nameof(TraceSummary));
        if (HasBoundarySegments) RefreshBoundarySegments();
        RefreshPhotoCommands();
        SolveCommand?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPhysicalDiscSizeChanged(PhotoDiscSize value)
    {
        PhotoCanvasRadiusMm = value.OuterRadiusMm + 3;
        if (_restoringPhoto) return;
        InvalidatePhotoRectification();
    }

    private void RefreshPhotoCommands()
    {
        ImportPhotoCommand?.NotifyCanExecuteChanged(); RectifyPhotoCommand?.NotifyCanExecuteChanged();
        ConfirmRectificationCommand?.NotifyCanExecuteChanged(); BackToPhotoCommand?.NotifyCanExecuteChanged();
        ClearSelectedTraceCommand?.NotifyCanExecuteChanged(); ClearTracesCommand?.NotifyCanExecuteChanged();
        BoundaryEditedCommand?.NotifyCanExecuteChanged(); TraceEditedCommand?.NotifyCanExecuteChanged();
        AddBoundarySegmentCommand?.NotifyCanExecuteChanged(); DeleteBoundarySegmentCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>Re-reads the photo step's labels and measurement line. The choice labels are
    /// refreshed in place so the ComboBox selection and the imported photo stay untouched.</summary>
    private void RelocalizePhoto()
    {
        foreach (PhotoDiscSize size in PhysicalDiscSizeChoices)
        {
            size.RefreshLabel();
        }

        OnPropertyChanged(nameof(TraceSummary));
        RefreshBoundarySegments();
        if (_rectificationFit is { } fit)
        {
            RectificationSummary = L.F($"外缘偏差 {fit.OuterRmsPixels:0.0} 像素 · 内缘偏差 {fit.HoleRmsPixels:0.0} 像素");
        }
    }

    private void ResetPhoto()
    {
        _photoRevision++;
        _photoTransform = null; _rectificationFit = null; _photoPng = null;
        PhotoImage = null; RectifiedPhoto = null;
        PhotoBoundaryPoints = []; PhotoBoundaryContours = [];
        SetRingObservations();
        TracePoints = []; CrossPredictionContours = [];
        ResetBoundarySegments();
        IsRectificationReady = false; PreviewRectification = false; IsPhotoStep = true;
        RectificationSummary = string.Empty;
        RefreshPhotoCommands();
    }

    private void InvalidatePhotoRectification()
    {
        _photoRevision++;
        _photoTransform = null; _rectificationFit = null; RectifiedPhoto = null;
        IsRectificationReady = false; PreviewRectification = false; IsPhotoStep = true;
        PhotoBoundaryContours = []; TracePoints = [];
        ResetBoundarySegments();
        SetRingObservations();
        InvalidateFit();
        RectificationSummary = string.Empty;
        RefreshCommands();
    }

    private async Task ImportPhotoAsync()
    {
        string? path = _shell.Dialogs?.PickOpen(title: L.T("导入标定盘照片"), filter: L.T("照片或扫描图|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff"));
        if (path is not null) await ImportPhotoFromAsync(path);
    }

    internal async Task ImportPhotoFromAsync(string path)
    {
        await RunAsync(L.T("正在读取照片…"), async token =>
        {
            var result = await Task.Run(() =>
            {
                var image = CalibrationPhotoService.Load(path, cancellationToken: token);
                var contours = CalibrationPhotoService.SuggestContours(image, 8, token);
                return (Image: image, Contours: contours, Png: EncodePhoto(image));
            }, token);
            token.ThrowIfCancellationRequested();
            InvalidatePhotoRectification();
            PhotoImage = result.Image; _photoPng = result.Png;
            SetBoundaryPoints(result.Contours.Outer, result.Contours.Hole);
            _statusMessage = L.T("照片已导入。请把两组控制点拖到光盘实体外缘和实体内缘，然后计算矫正。");
            RefreshCommands();
        });
    }

    private void SetBoundaryPoints(IReadOnlyList<CalibrationPoint> outer, IReadOnlyList<CalibrationPoint> hole)
    {
        PhotoBoundaryPoints = outer.Select((p, i) => new CalibrationPhotoBoundaryPoint(CalibrationPhotoBoundaryKind.OuterEdge, i, p.X, p.Y))
            .Concat(hole.Select((p, i) => new CalibrationPhotoBoundaryPoint(CalibrationPhotoBoundaryKind.CenterHole, i, p.X, p.Y))).ToArray();
    }

    private void EditBoundary(CalibrationBoundaryEdit? edit)
    {
        if (edit is null || PhotoImage is not { } image || !IsPhotoStep || IsBusy
            || !double.IsFinite(edit.X) || !double.IsFinite(edit.Y)) return;
        if (!PhotoBoundaryPoints.Any(p => p.Boundary == edit.Boundary && p.Index == edit.Index)) return;
        var updated = PhotoBoundaryPoints.Select(p => p.Boundary == edit.Boundary && p.Index == edit.Index
            ? p with { X = Math.Clamp(edit.X, 0, image.PixelWidth - 1), Y = Math.Clamp(edit.Y, 0, image.PixelHeight - 1) } : p).ToArray();
        InvalidatePhotoRectification();
        PhotoBoundaryPoints = updated;
    }

    private CalibrationPoint[] Boundary(CalibrationPhotoBoundaryKind kind) => PhotoBoundaryPoints
        .Where(p => p.Boundary == kind).OrderBy(p => p.Index).Select(p => new CalibrationPoint(p.X, p.Y)).ToArray();

    private async Task RectifyPhotoAsync()
    {
        if (PhotoImage is not { } image) return;
        var outer = Boundary(CalibrationPhotoBoundaryKind.OuterEdge);
        var hole = Boundary(CalibrationPhotoBoundaryKind.CenterHole);
        double radius = SelectedPhysicalDiscSize.OuterRadiusMm;
        double canvasRadius = PhotoCanvasRadiusMm;
        long revision = _photoRevision;
        await RunAsync(L.T("正在根据实体边缘矫正照片…"), async token =>
        {
            var fit = await Task.Run(() => CalibrationPhotoRectifier.Fit(outer, hole, radius, 7.5, token), token);
            if (!fit.Succeeded || fit.Transform is null) throw PhotoFitError(fit);
            var corrected = await Task.Run(() => CalibrationPhotoService.Rectify(image, fit.Transform, canvasRadius, cancellationToken: token), token);
            token.ThrowIfCancellationRequested();
            if (revision != _photoRevision || !ReferenceEquals(image, PhotoImage)) throw new OperationCanceledException();
            _photoTransform = fit.Transform;
            _rectificationFit = fit;
            RectifiedPhoto = corrected;
            PhotoBoundaryContours = BoundaryContours(fit.Transform, radius);
            RectificationSummary = L.F($"外缘偏差 {fit.OuterRmsPixels:0.0} 像素 · 内缘偏差 {fit.HoleRmsPixels:0.0} 像素");
            IsRectificationReady = true;
            PreviewRectification = true;
            _statusMessage = L.T("Phases.Rectified");
            RefreshPhotoCommands();
        });
    }

    private static ArgumentException PhotoFitError(CalibrationPhotoFitResult fit)
        => fit.MessageTemplate is { } template
            ? DiagnosticMessage.Argument(template)
            : new ArgumentException(fit.Message);

    private static Point[][] BoundaryContours(CalibrationPhotoTransform transform, double radius) =>
        new[] { radius, 7.5 }.Select(r => Enumerable.Range(0, 181).Select(i =>
        {
            double angle = i * 2 * Math.PI / 180;
            var point = transform.DiscToSource(new(r * Math.Cos(angle), r * Math.Sin(angle)));
            return new Point(point.X, point.Y);
        }).ToArray()).ToArray();

    private void ConfirmRectification()
    {
        if (!IsRectificationReady || _photoTransform is null || RectifiedPhoto is null) return;
        PreviewRectification = false;
        IsPhotoStep = false;
        RefreshCommands();
    }

    private void EditTrace(CalibrationTraceEdit? edit)
    {
        if (edit is null || !IsLineFitStep || IsBusy || _photoTransform is null) return;
        int arm = HasBoundarySegments ? edit.Arm : 0;
        if (HasBoundarySegments && !_boundarySegmentIds.Contains(arm)) return;
        if (!double.IsFinite(edit.XMm) || !double.IsFinite(edit.YMm)) return;
        double radius = Math.Sqrt(edit.XMm * edit.XMm + edit.YMm * edit.YMm);
        if (!edit.Remove && (radius < 7.5 || radius > SelectedPhysicalDiscSize.OuterRadiusMm))
        {
            return;
        }
        var points = TracePoints.ToList();
        int at = points.FindIndex(p => p.Index == edit.Index && p.Arm == arm);
        if (edit.Remove)
        {
            if (at < 0) return;
            points.RemoveAt(at);
        }
        else if (at >= 0) points[at] = new(arm, edit.Index, edit.XMm, edit.YMm);
        else
        {
            if (edit.Index != -1 || points.Count >= 4000) return;
            int id = points.Count == 0 ? 0 : points.Max(p => p.Index) + 1;
            if (HasBoundarySegments)
            {
                int insertAt;
                if (edit.InsertBeforeIndex is int before)
                {
                    insertAt = points.FindIndex(p => p.Arm == arm && p.Index == before);
                    if (insertAt < 0) return; // A stale insertion hint must not connect another segment.
                }
                else
                {
                    int last = points.FindLastIndex(p => p.Arm == arm);
                    insertAt = last < 0 ? points.Count : last + 1;
                }
                points.Insert(insertAt, new(arm, id, edit.XMm, edit.YMm));
            }
            else points.Add(new(arm, id, edit.XMm, edit.YMm));
        }
        // Boundary paths keep their explicit neighbours through every edit. Numerical
        // radial ordering belongs to the fitter, never to the user's visible polyline.
        TracePoints = HasBoundarySegments
            ? _boundarySegmentIds.SelectMany(segment => points.Where(p => p.Arm == segment)).ToArray()
            : points.OrderBy(p => p.Arm).ThenBy(p => p.XMm * p.XMm + p.YMm * p.YMm).ToArray();
        InvalidateFit();
        RefreshCommands();
    }

    private void ClearTraces()
    {
        TracePoints = [];
        ResetBoundarySegments();
        InvalidateFit();
        RefreshCommands();
    }

    private IReadOnlyList<CalibrationCrossTrace> CrossTraces() => HasBoundarySegments || TracePoints.Count == 0 ? [] :
        [new(0, TracePoints.Select(p => new CalibrationPoint(p.XMm, p.YMm)).ToArray())];

    private CalibrationPhotoSession? PhotoSession()
    {
        if (_photoPng is null) return null;
        var rings = RingObservations();
        CalibrationRingArcDraft? draft = HasWideBandTarget ? _oldWideBandDraft : _photoTransform is not null && ThreePointMode
            && CanMeasureAtPhotoSize
            && (IsTraceStep || RingPointMarkers.Count > 0 || rings.Length > 0)
            && (uint)SelectedRingIndex < (uint)RingArcs.Count
            ? new(SelectedRingIndex, !RingArcs[SelectedRingIndex].IsConfirmed ? RingPointMarkers : []) : null;
        bool confirmed = _photoTransform is not null
            && (IsTraceStep || TracePoints.Count > 0 || rings.Length > 0 || draft is not null || HasArcLengthMarks);
        return new(_photoPng, Boundary(CalibrationPhotoBoundaryKind.OuterEdge), Boundary(CalibrationPhotoBoundaryKind.CenterHole),
            SelectedPhysicalDiscSize.OuterRadiusMm,
            confirmed,
            CrossTraces(), IsCoarseMode, rings.Length == 0 ? null : rings, _acceptedRingSeed is not null, draft, PreferredClockAngle,
            HasArcLengthMarks ? new CalibrationArcLengthSession(SelectedArcBand, _arcMarks.ToArray()) : null,
            IsBandRayMode && draft is null && confirmed && CanMeasureAtPhotoSize
                && (uint)SelectedRingIndex < (uint)RingArcs.Count ? SelectedRingIndex : null,
            HasBoundarySegments ? BoundarySegments() : null);
    }

    private static string EncodePhoto(BitmapSource source)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using MemoryStream stream = new();
        encoder.Save(stream);
        int length = checked((int)stream.Length);
        if (4L * ((length + 2L) / 3) > CalibrationPhotoLimits.MaxEncodedPhotoLength)
            throw new ArgumentException(L.T("标定照片为空或过大，请重新导入照片。"));
        return Convert.ToBase64String(stream.GetBuffer(), 0, length);
    }

    private static BitmapSource DecodePhoto(string png)
    {
        using MemoryStream stream = new(Convert.FromBase64String(png));
        // Inspect dimensions before requesting decoded pixels from a saved record.
        var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
        if (!CalibrationPhotoLimits.Contains(header.PixelWidth, header.PixelHeight)
            || header.PixelWidth < 32 || header.PixelHeight < 32)
            throw new ArgumentException(L.T("标定记录中的照片尺寸无效，请重新导入。"));
        stream.Position = 0;
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    internal async Task LoadSessionFromAsync(string path)
    {
        await RunAsync(L.T("正在恢复标定照片与记录…"), async token =>
        {
            var prepared = await Task.Run(() =>
            {
                var session = CalibrationSessionJson.Load(path);
                ValidateOutputParameters(session.Target.Parameters);
                BitmapSource? image = null, rectified = null;
                CalibrationPhotoFitResult? fit = null;
                CalibrationFitResult? ringFit = null;
                if (session.Photo is { } photo)
                {
                    image = DecodePhoto(photo.ImagePngBase64);
                    if (photo.OuterBoundary.Concat(photo.HoleBoundary).Any(p => p.X >= image.PixelWidth || p.Y >= image.PixelHeight))
                        throw new ArgumentException(L.T("标定轮廓超出照片范围。"));
                    if (photo.RectificationConfirmed)
                    {
                        fit = CalibrationPhotoRectifier.Fit(photo.OuterBoundary, photo.HoleBoundary, photo.PhysicalOuterRadiusMm, 7.5, token);
                        if (!fit.Succeeded || fit.Transform is null) throw PhotoFitError(fit);
                        rectified = CalibrationPhotoService.Rectify(image, fit.Transform, photo.PhysicalOuterRadiusMm + 3, cancellationToken: token);
                        if (photo.UseRingSeed)
                        {
                            ringFit = CalibrationRingFitter.Fit(session.Target, photo.RingObservations ?? [], token);
                            if (!RingEstimateAccepted(ringFit, photo.PhysicalOuterRadiusMm))
                                throw new ArgumentException(L.T("Rings.InvalidSavedSeed"));
                        }
                    }
                }
                return (Session: session, Image: image, Rectified: rectified, Fit: fit, RingFit: ringFit);
            }, token);
            token.ThrowIfCancellationRequested();
            var session = prepared.Session;
            InstallTarget(session.Target, session.FitOptions, session.CdInterleave);
            RestoreStage(session);
            WriteSetupParameters(session.Target.Parameters);
            if (session.Target.Parameters.Kind == CalibrationDiscKind.Cd)
                _state.CdInterleave = session.CdInterleave;
            if (session.Photo is { } photo)
            {
                _restoringPhoto = true;
                try { SelectedPhysicalDiscSize = PhysicalDiscSizeChoices.Single(s => s.OuterRadiusMm == photo.PhysicalOuterRadiusMm); }
                finally { _restoringPhoto = false; }
                PhotoImage = prepared.Image; RectifiedPhoto = prepared.Rectified; _photoPng = photo.ImagePngBase64;
                _photoTransform = prepared.Fit?.Transform;
                _rectificationFit = prepared.Fit;
                SetBoundaryPoints(photo.OuterBoundary, photo.HoleBoundary);
                IsRectificationReady = _photoTransform is not null;
                if (_photoTransform is not null) PhotoBoundaryContours = BoundaryContours(_photoTransform, photo.PhysicalOuterRadiusMm);
                if (HasBoundarySegments) RestoreBoundarySegments(photo.BoundarySegments);
                else TracePoints = photo.Traces.SelectMany(t => t.Points).Select((p, i) => new CalibrationTracePoint(0, i, p.X, p.Y))
                    .OrderBy(p => p.XMm * p.XMm + p.YMm * p.YMm).ToArray();
                SetRingObservations(photo.RingObservations);
                if (photo.BandRaySelection is int selectedEdge)
                    SelectedRingIndex = selectedEdge;
                SetPreferredClockAngle(photo.PreferredClockAngleDegrees);
                SetArcLengthMarks(photo.RadialArcLengths);
                if (photo.ArcDraft is { } draft)
                {
                    SelectedRingIndex = draft.RingIndex;
                    if (HasWideBandTarget) _oldWideBandDraft = draft;
                    else
                    {
                        ThreePointMode = true;
                        if (draft.Points.Count > 0) SetRingMarkers(draft.Points);
                    }
                }
                _ringFit = prepared.RingFit;
                _acceptedRingSeed = _ringFit?.BestParameters;
                NotifyRingResult();
                UpdateFitAssessment();
                IsPhotoStep = !photo.RectificationConfirmed;
                RectificationSummary = prepared.Fit is { } restoredFit
                    ? L.F($"外缘偏差 {restoredFit.OuterRmsPixels:0.0} 像素 · 内缘偏差 {restoredFit.HoleRmsPixels:0.0} 像素")
                    : string.Empty;
            }
            if (session.ConfirmedFit is { } confirmedFit)
                await RestoreConfirmedFitAsync(confirmedFit, token);
            token.ThrowIfCancellationRequested();
            _sessionPath = path; OutputPath = string.Empty;
            _statusMessage = L.T("已恢复标定图案、照片和曲线点，可继续编辑或重新反推。");
            RefreshCommands();
        });
    }

    /// <summary>Physical disc size choice for the photo step. The label is computed from the
    /// radius so it follows the language without replacing the items.</summary>
    public sealed class PhotoDiscSize : INotifyPropertyChanged
    {
        public PhotoDiscSize(double outerRadiusMm) => OuterRadiusMm = outerRadiusMm;

        public string Label => OuterRadiusMm >= 60
            ? L.T("标准光盘 · 12 cm")
            : L.T("小光盘 · 8 cm");

        public double OuterRadiusMm { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void RefreshLabel()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }

}
