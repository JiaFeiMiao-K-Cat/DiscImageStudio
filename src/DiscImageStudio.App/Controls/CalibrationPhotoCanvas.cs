using DiscImageStudio.Localization;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Services;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Controls;

public enum CalibrationPhotoBoundaryKind { OuterEdge, CenterHole }
public sealed record CalibrationPhotoBoundaryPoint(CalibrationPhotoBoundaryKind Boundary, int Index, double X, double Y);
public sealed record CalibrationBoundaryEdit(CalibrationPhotoBoundaryKind Boundary, int Index, double X, double Y);
public sealed record CalibrationTracePoint(int Arm, int Index, double XMm, double YMm);
/// <summary>Index -1 adds a point before InsertBeforeIndex, or at the segment end when null.
/// Existing indices identify a point without changing its neighbours.</summary>
public sealed record CalibrationTraceEdit(int Arm, int Index, double XMm, double YMm, bool Remove = false,
    int? InsertBeforeIndex = null);
public sealed record CalibrationRingArc(int RingIndex, double RadiusMm, bool IsConfirmed,
    IReadOnlyList<CalibrationPoint>? Points = null)
{
    public static string WideEdgeLabel(int id) =>
        L.Format(id < 3 ? "BandEdges.InnerLabel" : "BandEdges.OuterLabel", id % 3 + 1);
    public string ShortEdgeLabel => L.Format(RingIndex < 3 ? "BandEdges.InnerBadge" : "BandEdges.OuterBadge", RingIndex % 3 + 1);
}
/// <summary>A null radius returns the guide to its unmeasured state.</summary>
public sealed record CalibrationRingEdit(int RingIndex, double? RadiusMm);

/// <summary>Photo boundaries use source pixels; corrected-photo traces use disc-centred millimetres, Y down.</summary>
public sealed class CalibrationPhotoCanvas : FrameworkElement
{
    private const FrameworkPropertyMetadataOptions Render = FrameworkPropertyMetadataOptions.AffectsRender;
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(nameof(Image), typeof(BitmapSource), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render, OnViewportChanged));
    public static readonly DependencyProperty FallbackImageProperty = DependencyProperty.Register(nameof(FallbackImage), typeof(BitmapSource), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty IsPhotoStepProperty = DependencyProperty.Register(nameof(IsPhotoStep), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(true, Render, OnViewportChanged));
    public static readonly DependencyProperty IsRectificationPreviewProperty = DependencyProperty.Register(nameof(IsRectificationPreview), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(false, Render, OnViewportChanged));
    public static readonly DependencyProperty IsTargetPreviewProperty = DependencyProperty.Register(nameof(IsTargetPreview), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(false, Render, OnViewportChanged));
    public static readonly DependencyProperty CanvasRadiusMmProperty = DependencyProperty.Register(nameof(CanvasRadiusMm), typeof(double), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(63.0, Render, OnViewportChanged));
    public static readonly DependencyProperty TargetCanvasRadiusMmProperty = DependencyProperty.Register(nameof(TargetCanvasRadiusMm), typeof(double), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(60.0, Render));
    public static readonly DependencyProperty BoundaryPointsProperty = DependencyProperty.Register(nameof(BoundaryPoints), typeof(IEnumerable<CalibrationPhotoBoundaryPoint>), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render, OnCollectionChanged));
    public static readonly DependencyProperty BoundaryContoursProperty = DependencyProperty.Register(nameof(BoundaryContours), typeof(IReadOnlyList<Point[]>), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty TracePointsProperty = DependencyProperty.Register(nameof(TracePoints), typeof(IEnumerable<CalibrationTracePoint>), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render, OnCollectionChanged));
    public static readonly DependencyProperty PredictionContoursProperty = DependencyProperty.Register(nameof(PredictionContours), typeof(IReadOnlyList<Point[]>), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty OutlierTracePointIndicesProperty = DependencyProperty.Register(nameof(OutlierTracePointIndices), typeof(IReadOnlyList<int>), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty SelectedTraceArmProperty = DependencyProperty.Register(nameof(SelectedTraceArm), typeof(int), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(0, Render | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTraceSelectionChanged));
    public static readonly DependencyProperty IsBoundarySegmentModeProperty = DependencyProperty.Register(nameof(IsBoundarySegmentMode), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(false, Render, OnViewportChanged));
    public static readonly DependencyProperty BoundaryEditedCommandProperty = DependencyProperty.Register(nameof(BoundaryEditedCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty TraceEditedCommandProperty = DependencyProperty.Register(nameof(TraceEditedCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty IsCoarseModeProperty = DependencyProperty.Register(nameof(IsCoarseMode), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(false, Render, OnViewportChanged));
    public static readonly DependencyProperty HasRadialArcTargetProperty = DependencyProperty.Register(nameof(HasRadialArcTarget), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(false, Render, OnViewportChanged));
    public static readonly DependencyProperty HasWideBandTargetProperty = DependencyProperty.Register(nameof(HasWideBandTarget), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(false, Render, OnViewportChanged));
    public static readonly DependencyProperty IsBandRayModeProperty = DependencyProperty.Register(nameof(IsBandRayMode), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(false, Render, OnViewportChanged));
    public static readonly DependencyProperty BandRayAngleDegreesProperty = DependencyProperty.Register(nameof(BandRayAngleDegrees), typeof(double?), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty BandRayPointCommandProperty = DependencyProperty.Register(nameof(BandRayPointCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty ArcLengthPointsProperty = DependencyProperty.Register(nameof(ArcLengthPoints), typeof(IReadOnlyList<CalibrationPoint>), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty ArcLengthPointCommandProperty = DependencyProperty.Register(nameof(ArcLengthPointCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty UndoArcLengthPointCommandProperty = DependencyProperty.Register(nameof(UndoArcLengthPointCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty HasClockTargetProperty = DependencyProperty.Register(nameof(HasClockTarget), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(false, Render, OnViewportChanged));
    public static readonly DependencyProperty ClockReadAnglesProperty = DependencyProperty.Register(nameof(ClockReadAngles), typeof(IReadOnlyList<double>), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty PreferredClockAngleProperty = DependencyProperty.Register(nameof(PreferredClockAngle), typeof(double?), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty PreferClockAngleCommandProperty = DependencyProperty.Register(nameof(PreferClockAngleCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty RingArcsProperty = DependencyProperty.Register(nameof(RingArcs), typeof(IEnumerable<CalibrationRingArc>), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render, OnCollectionChanged));
    public static readonly DependencyProperty ThreePointModeProperty = DependencyProperty.Register(nameof(ThreePointMode), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(false, Render, OnViewportChanged));
    public static readonly DependencyProperty SelectedRingIndexProperty = DependencyProperty.Register(nameof(SelectedRingIndex), typeof(int), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(-1, Render));
    public static readonly DependencyProperty RingPointMarkersProperty = DependencyProperty.Register(nameof(RingPointMarkers), typeof(IReadOnlyList<CalibrationPoint>), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty RingPointCommandProperty = DependencyProperty.Register(nameof(RingPointCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty UndoRingPointCommandProperty = DependencyProperty.Register(nameof(UndoRingPointCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty RingEditedCommandProperty = DependencyProperty.Register(nameof(RingEditedCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty RingAngleDegreesProperty = DependencyProperty.Register(nameof(RingAngleDegrees), typeof(double), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(0.0, Render));
    public static readonly DependencyProperty PhysicalRadiusMmProperty = DependencyProperty.Register(nameof(PhysicalRadiusMm), typeof(double), typeof(CalibrationPhotoCanvas), new PropertyMetadata(60.0));
    public static readonly DependencyProperty ShowPredictionProperty = DependencyProperty.Register(nameof(ShowPrediction), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(true, Render));
    private static readonly DependencyPropertyKey ViewDescriptionPropertyKey = DependencyProperty.RegisterReadOnly(nameof(ViewDescription), typeof(string), typeof(CalibrationPhotoCanvas), new PropertyMetadata("100%"));
    public static readonly DependencyProperty ViewDescriptionProperty = ViewDescriptionPropertyKey.DependencyProperty;
    // The default stays empty: metadata is initialized once, so a localized default would
    // keep the language that happened to be active when this type was first touched.
    private static readonly DependencyPropertyKey SelectionDescriptionPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SelectionDescription), typeof(string), typeof(CalibrationPhotoCanvas), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty SelectionDescriptionProperty = SelectionDescriptionPropertyKey.DependencyProperty;

    public BitmapSource? Image { get => (BitmapSource?)GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public BitmapSource? FallbackImage { get => (BitmapSource?)GetValue(FallbackImageProperty); set => SetValue(FallbackImageProperty, value); }
    public bool IsPhotoStep { get => (bool)GetValue(IsPhotoStepProperty); set => SetValue(IsPhotoStepProperty, value); }
    public bool IsRectificationPreview { get => (bool)GetValue(IsRectificationPreviewProperty); set => SetValue(IsRectificationPreviewProperty, value); }
    public bool IsTargetPreview { get => (bool)GetValue(IsTargetPreviewProperty); set => SetValue(IsTargetPreviewProperty, value); }
    public double CanvasRadiusMm { get => (double)GetValue(CanvasRadiusMmProperty); set => SetValue(CanvasRadiusMmProperty, value); }
    public double TargetCanvasRadiusMm { get => (double)GetValue(TargetCanvasRadiusMmProperty); set => SetValue(TargetCanvasRadiusMmProperty, value); }
    public IEnumerable<CalibrationPhotoBoundaryPoint>? BoundaryPoints { get => (IEnumerable<CalibrationPhotoBoundaryPoint>?)GetValue(BoundaryPointsProperty); set => SetValue(BoundaryPointsProperty, value); }
    public IReadOnlyList<Point[]>? BoundaryContours { get => (IReadOnlyList<Point[]>?)GetValue(BoundaryContoursProperty); set => SetValue(BoundaryContoursProperty, value); }
    public IEnumerable<CalibrationTracePoint>? TracePoints { get => (IEnumerable<CalibrationTracePoint>?)GetValue(TracePointsProperty); set => SetValue(TracePointsProperty, value); }
    public IReadOnlyList<Point[]>? PredictionContours { get => (IReadOnlyList<Point[]>?)GetValue(PredictionContoursProperty); set => SetValue(PredictionContoursProperty, value); }
    public IReadOnlyList<int>? OutlierTracePointIndices { get => (IReadOnlyList<int>?)GetValue(OutlierTracePointIndicesProperty); set => SetValue(OutlierTracePointIndicesProperty, value); }
    public int SelectedTraceArm { get => (int)GetValue(SelectedTraceArmProperty); set => SetValue(SelectedTraceArmProperty, value); }
    public bool IsBoundarySegmentMode { get => (bool)GetValue(IsBoundarySegmentModeProperty); set => SetValue(IsBoundarySegmentModeProperty, value); }
    public ICommand? BoundaryEditedCommand { get => (ICommand?)GetValue(BoundaryEditedCommandProperty); set => SetValue(BoundaryEditedCommandProperty, value); }
    public ICommand? TraceEditedCommand { get => (ICommand?)GetValue(TraceEditedCommandProperty); set => SetValue(TraceEditedCommandProperty, value); }
    public bool IsCoarseMode { get => (bool)GetValue(IsCoarseModeProperty); set => SetValue(IsCoarseModeProperty, value); }
    public bool HasRadialArcTarget { get => (bool)GetValue(HasRadialArcTargetProperty); set => SetValue(HasRadialArcTargetProperty, value); }
    public bool HasWideBandTarget { get => (bool)GetValue(HasWideBandTargetProperty); set => SetValue(HasWideBandTargetProperty, value); }
    public bool IsBandRayMode { get => (bool)GetValue(IsBandRayModeProperty); set => SetValue(IsBandRayModeProperty, value); }
    public double? BandRayAngleDegrees { get => (double?)GetValue(BandRayAngleDegreesProperty); set => SetValue(BandRayAngleDegreesProperty, value); }
    public ICommand? BandRayPointCommand { get => (ICommand?)GetValue(BandRayPointCommandProperty); set => SetValue(BandRayPointCommandProperty, value); }
    public IReadOnlyList<CalibrationPoint>? ArcLengthPoints { get => (IReadOnlyList<CalibrationPoint>?)GetValue(ArcLengthPointsProperty); set => SetValue(ArcLengthPointsProperty, value); }
    public ICommand? ArcLengthPointCommand { get => (ICommand?)GetValue(ArcLengthPointCommandProperty); set => SetValue(ArcLengthPointCommandProperty, value); }
    public ICommand? UndoArcLengthPointCommand { get => (ICommand?)GetValue(UndoArcLengthPointCommandProperty); set => SetValue(UndoArcLengthPointCommandProperty, value); }
    public bool HasClockTarget { get => (bool)GetValue(HasClockTargetProperty); set => SetValue(HasClockTargetProperty, value); }
    public IReadOnlyList<double>? ClockReadAngles { get => (IReadOnlyList<double>?)GetValue(ClockReadAnglesProperty); set => SetValue(ClockReadAnglesProperty, value); }
    public double? PreferredClockAngle { get => (double?)GetValue(PreferredClockAngleProperty); set => SetValue(PreferredClockAngleProperty, value); }
    public ICommand? PreferClockAngleCommand { get => (ICommand?)GetValue(PreferClockAngleCommandProperty); set => SetValue(PreferClockAngleCommandProperty, value); }
    public IEnumerable<CalibrationRingArc>? RingArcs { get => (IEnumerable<CalibrationRingArc>?)GetValue(RingArcsProperty); set => SetValue(RingArcsProperty, value); }
    public bool ThreePointMode { get => (bool)GetValue(ThreePointModeProperty); set => SetValue(ThreePointModeProperty, value); }
    public int SelectedRingIndex { get => (int)GetValue(SelectedRingIndexProperty); set => SetValue(SelectedRingIndexProperty, value); }
    public IReadOnlyList<CalibrationPoint>? RingPointMarkers { get => (IReadOnlyList<CalibrationPoint>?)GetValue(RingPointMarkersProperty); set => SetValue(RingPointMarkersProperty, value); }
    public ICommand? RingPointCommand { get => (ICommand?)GetValue(RingPointCommandProperty); set => SetValue(RingPointCommandProperty, value); }
    public ICommand? UndoRingPointCommand { get => (ICommand?)GetValue(UndoRingPointCommandProperty); set => SetValue(UndoRingPointCommandProperty, value); }
    public ICommand? RingEditedCommand { get => (ICommand?)GetValue(RingEditedCommandProperty); set => SetValue(RingEditedCommandProperty, value); }
    public double RingAngleDegrees { get => (double)GetValue(RingAngleDegreesProperty); set => SetValue(RingAngleDegreesProperty, value); }
    public double PhysicalRadiusMm { get => (double)GetValue(PhysicalRadiusMmProperty); set => SetValue(PhysicalRadiusMmProperty, value); }
    public bool ShowPrediction { get => (bool)GetValue(ShowPredictionProperty); set => SetValue(ShowPredictionProperty, value); }
    public string ViewDescription => (string)GetValue(ViewDescriptionProperty);
    public string SelectionDescription => (string)GetValue(SelectionDescriptionProperty);

    private double _zoom = 1;
    private Vector _pan;
    private bool _panning;
    private Point _panStart;
    private Vector _panBefore;
    private CalibrationPhotoBoundaryPoint? _boundaryDrag;
    private CalibrationTracePoint? _traceDrag;
    private CalibrationRingArc? _ringDrag;
    private double _ringDragRadius;
    private int? _selectedRing;
    private const double ArcHalfAngle = Math.PI / 6;
    private Point _dragStart;
    private Point _dragOriginal;
    private Point _dragPosition;
    private bool _hasMoved;
    private (int Arm, int Index)? _selectedTrace;
    private (int Arm, int Index)? _traceContinuation;
    private (int Arm, int Index)? _traceLastExtension;
    private Point? _traceHover;
    private (CalibrationPhotoBoundaryKind Boundary, int Index)? _selectedBoundary;
    private bool ShowingTarget => IsTargetPreview || Image is null;
    private bool ShowingRawPhoto => !ShowingTarget && IsPhotoStep && !IsRectificationPreview;
    private bool ShowingEditablePhoto => !ShowingTarget && !IsPhotoStep && !IsRectificationPreview;
    private bool CanEditTrace => ShowingEditablePhoto && !IsCoarseMode && !HasClockTarget && !HasRadialArcTarget && !HasWideBandTarget;
    private bool CanEditRings => ShowingEditablePhoto && IsCoarseMode && !HasClockTarget && !HasRadialArcTarget && !CanMarkBandRay;
    private bool CanMarkBandRay => ShowingEditablePhoto && IsCoarseMode && HasWideBandTarget && IsBandRayMode;
    private bool ShowingClockPhoto => ShowingEditablePhoto && IsCoarseMode && HasClockTarget;
    private bool CanMarkArcLength => ShowingEditablePhoto && IsCoarseMode && HasRadialArcTarget;
    private bool Dragging => _boundaryDrag is not null || _traceDrag is not null || _ringDrag is not null;
    private Rect WorldBounds
    {
        get
        {
            // Source coordinates address pixel centres, matching the rectifier's sampling convention.
            if (ShowingRawPhoto && Image is { } photo) return new Rect(-.5, -.5, photo.PixelWidth, photo.PixelHeight);
            double radius = ShowingTarget ? TargetCanvasRadiusMm : CanvasRadiusMm;
            radius = double.IsFinite(radius) && radius > 0 ? radius : 63;
            return new Rect(-radius, -radius, radius * 2, radius * 2);
        }
    }
    private Matrix ViewMatrix
    {
        get
        {
            var bounds = WorldBounds;
            double scale = Math.Max(.0001, Math.Min(ActualWidth / bounds.Width, ActualHeight / bounds.Height) * .92 * _zoom);
            var matrix = Matrix.Identity;
            matrix.Translate(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2);
            matrix.Scale(scale, scale);
            matrix.Translate(ActualWidth / 2 + _pan.X, ActualHeight / 2 + _pan.Y);
            return matrix;
        }
    }

    public CalibrationPhotoCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) =>
        {
            ThemeResolver.Changed -= ThemeResolver_Changed;
            ThemeResolver.Changed += ThemeResolver_Changed;
            L.LanguageChanged -= Language_LanguageChanged;
            L.LanguageChanged += Language_LanguageChanged;
            RefreshSelectionDescription();
            InvalidateVisual();
        };
        Unloaded += (_, _) =>
        {
            ThemeResolver.Changed -= ThemeResolver_Changed;
            L.LanguageChanged -= Language_LanguageChanged;
        };
    }

    private void ThemeResolver_Changed(object? sender, EventArgs e) => InvalidateVisual();

    private void Language_LanguageChanged(object? sender, EventArgs e)
    {
        RefreshSelectionDescription();
        InvalidateVisual();
    }

    private void RefreshSelectionDescription()
    {
        if (_boundaryDrag is { } boundary)
        {
            SetValue(SelectionDescriptionPropertyKey, boundary.Boundary == CalibrationPhotoBoundaryKind.OuterEdge
                ? L.T("拖到盘片实体外缘；不要沿刻录区边界。") : L.T("拖到实体内缘；不要沿透明塑料环。"));
        }
        else if (_ringDrag is { } ring)
        {
            UpdateRingDescription(ring.RingIndex, _ringDragRadius);
        }
        else if (_selectedRing is { } index && RingArcs?.FirstOrDefault(arc => arc.RingIndex == index) is { } selectedRing)
        {
            UpdateRingDescription(index, selectedRing.RadiusMm);
        }
        else if (_selectedTrace is not null || CanEditTrace && IsBoundarySegmentMode)
        {
            UpdateTraceDescription();
        }
        else
        {
            SetValue(SelectionDescriptionPropertyKey, DefaultSelectionDescription());
        }
    }

    private string DefaultSelectionDescription() => IsPhotoStep
        ? L.T("滚轮缩放，右键平移；拖动控制点贴合边缘。")
        : HasRadialArcTarget ? L.T(IsCoarseMode ? "Arc.CanvasHint" : "Arc.FineHint")
        : HasClockTarget ? L.T(IsCoarseMode ? "Clock.DirectionHint" : "Clock.FineHint")
        : HasWideBandTarget ? L.T(!IsCoarseMode ? "WideBands.FineHint" : IsBandRayMode
            ? "BandEdges.RayHint" : ThreePointMode ? "BandEdges.PointHint" : "BandEdges.DragHint")
        : IsCoarseMode ? L.T(ThreePointMode ? "Xaml.CoarseRingPointStatus" : "Xaml.CoarseRingStatusHint")
        : IsBoundarySegmentMode ? L.Format("HalfDisc.CanvasHint", SelectedTraceArm + 1)
        : L.T("滚轮缩放，右键平移；点击添加点，拖动已有点修正位置。");

    public Point WorldToView(Point point) => ViewMatrix.Transform(point);
    public Point ViewToWorld(Point point) { var matrix = ViewMatrix; matrix.Invert(); return matrix.Transform(point); }
    public void ResetView()
    {
        _zoom = 1;
        _pan = default;
        SetValue(ViewDescriptionPropertyKey, "100%");
        InvalidateVisual();
    }
    public void PanBy(Vector pixels) { _pan += pixels; InvalidateVisual(); }
    public void ZoomBy(double factor) => ZoomAt(factor, new Point(ActualWidth / 2, ActualHeight / 2));
    private void ZoomAt(double factor, Point screen)
    {
        if (!double.IsFinite(factor) || factor <= 0 || Dragging) return;
        var anchor = ViewToWorld(screen);
        _zoom = Math.Clamp(_zoom * factor, .5, 30);
        _pan += screen - WorldToView(anchor);
        SetValue(ViewDescriptionPropertyKey, $"{_zoom * 100:0}%");
        InvalidateVisual();
    }

    public bool BeginDragAt(Point screen)
    {
        if (Dragging) return false;
        if (ShowingRawPhoto)
        {
            var point = BoundaryPoints?.OrderBy(p => (WorldToView(new Point(p.X, p.Y)) - screen).LengthSquared)
                .FirstOrDefault(p => (WorldToView(new Point(p.X, p.Y)) - screen).Length <= 11);
            if (point is null) return false;
            _boundaryDrag = point;
            _selectedBoundary = (point.Boundary, point.Index);
            _dragOriginal = new Point(point.X, point.Y);
            SetValue(SelectionDescriptionPropertyKey, point.Boundary == CalibrationPhotoBoundaryKind.OuterEdge
                ? L.T("拖到盘片实体外缘；不要沿刻录区边界。") : L.T("拖到实体内缘；不要沿透明塑料环。") );
        }
        else if (CanEditTrace)
        {
            var point = TracePoints?.OrderBy(p => (WorldToView(Position(p)) - screen).LengthSquared)
                .ThenBy(p => p.Arm == SelectedTraceArm ? 0 : 1)
                .FirstOrDefault(p => (WorldToView(Position(p)) - screen).Length <= 10);
            if (point is null) return false;
            _traceDrag = point;
            _selectedTrace = (point.Arm, point.Index);
            SetCurrentValue(SelectedTraceArmProperty, point.Arm);
            _dragOriginal = Position(point);
            UpdateTraceDescription();
        }
        else if (CanEditRings && !ThreePointMode)
        {
            var ring = HitRing(screen);
            if (ring is null || RingEditedCommand?.CanExecute(new CalibrationRingEdit(ring.RingIndex, ring.RadiusMm)) != true) return false;
            _ringDrag = ring;
            _ringDragRadius = ring.RadiusMm;
            _selectedRing = ring.RingIndex;
            UpdateRingDescription(ring.RingIndex, ring.RadiusMm);
        }
        else return false;
        _dragStart = ViewToWorld(screen);
        _dragPosition = _dragOriginal;
        _hasMoved = false;
        InvalidateVisual();
        return true;
    }

    public void DragTo(Point screen)
    {
        if (!Dragging) return;
        if (_ringDrag is { } ring)
        {
            // Keep the centre fixed and preserve the radial grab offset when the click
            // lands a few screen pixels away from the stroke or on its number badge.
            double delta = ((Vector)ViewToWorld(screen)).Length - ((Vector)_dragStart).Length;
            _ringDragRadius = Math.Clamp(ring.RadiusMm + delta, 7.5, PhysicalRadiusMm);
            UpdateRingDescription(ring.RingIndex, _ringDragRadius);
            InvalidateVisual();
            return;
        }
        _dragPosition = _dragOriginal + (ViewToWorld(screen) - _dragStart);
        var bounds = ShowingRawPhoto && Image is { } photo
            ? new Rect(0, 0, photo.PixelWidth - 1, photo.PixelHeight - 1) : WorldBounds;
        _dragPosition = new Point(Math.Clamp(_dragPosition.X, bounds.Left, bounds.Right), Math.Clamp(_dragPosition.Y, bounds.Top, bounds.Bottom));
        _hasMoved |= IsBoundarySegmentMode && _traceDrag is not null
            ? (WorldToView(_dragPosition) - WorldToView(_dragOriginal)).Length > 2
            : (_dragPosition - _dragOriginal).Length > 1e-8;
        InvalidateVisual();
    }

    public void CompleteDrag(bool commit = true)
    {
        var boundary = _boundaryDrag;
        var trace = _traceDrag;
        var ring = _ringDrag;
        var position = _dragPosition;
        bool changed = _hasMoved && commit;
        _boundaryDrag = null;
        _traceDrag = null;
        _ringDrag = null;
        _hasMoved = false;
        if (changed && boundary is not null)
            Execute(BoundaryEditedCommand, new CalibrationBoundaryEdit(boundary.Boundary, boundary.Index, position.X, position.Y));
        if (changed && trace is not null)
        {
            Execute(TraceEditedCommand, new CalibrationTraceEdit(trace.Arm, trace.Index, position.X, position.Y));
            if (IsBoundarySegmentMode)
            {
                _selectedTrace = (trace.Arm, trace.Index);
                ValidateTraceSelection();
                UpdateTraceDescription();
            }
            else SelectTraceNear(trace.Arm, position);
        }
        else if (commit && trace is not null && IsBoundarySegmentMode && IsTraceEndpoint(trace.Arm, trace.Index))
        {
            _traceContinuation = (trace.Arm, trace.Index);
            UpdateTraceDescription();
        }
        if (ring is not null)
        {
            // A click is an explicit confirmation for a guide that already lines up.
            if (commit && (!ring.IsConfirmed || ring.RadiusMm != _ringDragRadius))
                Execute(RingEditedCommand, new CalibrationRingEdit(ring.RingIndex, _ringDragRadius));
            UpdateRingDescription(ring.RingIndex, commit ? _ringDragRadius : ring.RadiusMm);
        }
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    public bool AddTraceAt(Point screen)
    {
        if (!CanEditTrace || Dragging) return false;
        var point = ViewToWorld(screen);
        if (!WorldBounds.Contains(point) || SelectedTraceArm < 0 || (!IsBoundarySegmentMode && SelectedTraceArm > 3)) return false;
        TraceInsertion insertion = FindTraceInsertion(screen);
        bool continueFixedEnd = IsBoundarySegmentMode && _traceContinuation is not null;
        var previousIds = IsBoundarySegmentMode ? (TracePoints ?? []).Select(p => p.Index).ToHashSet() : null;
        var edit = new CalibrationTraceEdit(SelectedTraceArm, -1, point.X, point.Y,
            InsertBeforeIndex: IsBoundarySegmentMode ? insertion.BeforeIndex : null);
        if (TraceEditedCommand?.CanExecute(edit) != true) return false;
        TraceEditedCommand.Execute(edit);
        if (IsBoundarySegmentMode)
        {
            var added = TracePoints?.FirstOrDefault(p => p.Arm == SelectedTraceArm && !previousIds!.Contains(p.Index));
            if (added is null) return false;
            _selectedTrace = (added.Arm, added.Index);
            _traceLastExtension = IsTraceEndpoint(added.Arm, added.Index) ? _selectedTrace : null;
            if (continueFixedEnd && IsTraceEndpoint(added.Arm, added.Index)) _traceContinuation = _selectedTrace;
            UpdateTraceDescription();
        }
        else SelectTraceNear(SelectedTraceArm, point);
        InvalidateVisual();
        return true;
    }

    private readonly record struct TraceInsertion(int? BeforeIndex, CalibrationTracePoint? Previous, CalibrationTracePoint? Next);

    private TraceInsertion FindTraceInsertion(Point screen)
    {
        if (!IsBoundarySegmentMode) return default;
        var points = (TracePoints ?? []).Where(p => p.Arm == SelectedTraceArm).ToArray();
        if (points.Length == 0) return default;
        ValidateTraceSelection();
        if (_traceContinuation == (SelectedTraceArm, points[0].Index)) return new(points[0].Index, null, points[0]);
        if (_traceContinuation == (SelectedTraceArm, points[^1].Index)) return new(null, points[^1], null);
        if (points.Length == 1) return new(null, points[0], null);

        // Insertion is a deliberate click near a displayed edge, measured in screen
        // pixels so zooming cannot silently change the gesture into a global reorder.
        double closest = 7 * 7;
        int insertion = -1;
        for (int i = 1; i < points.Length; i++)
        {
            Point a = WorldToView(Position(points[i - 1])), b = WorldToView(Position(points[i]));
            Vector edge = b - a;
            double t = Vector.Multiply(screen - a, edge) / Math.Max(1e-20, edge.LengthSquared);
            // Leave endpoint handles to their normal select/drag action.
            if (t <= 0 || t >= 1 || (screen - a).Length <= 10 || (screen - b).Length <= 10) continue;
            double distance = (screen - (a + t * edge)).LengthSquared;
            if (distance <= closest) { closest = distance; insertion = i; }
        }
        if (insertion >= 0) return new(points[insertion].Index, points[insertion - 1], points[insertion]);
        Point first = WorldToView(Position(points[0])), last = WorldToView(Position(points[^1]));
        Vector fromFirst = screen - first, fromLast = screen - last;
        static double Alignment(Vector outward, Vector next) => Vector.Multiply(outward, next)
            / Math.Max(1e-20, outward.Length * next.Length);
        double firstAlignment = Alignment(first - WorldToView(Position(points[1])), fromFirst);
        double lastAlignment = Alignment(last - WorldToView(Position(points[^2])), fromLast);
        // A nearby parallel side of a U is not normally the intended continuation.
        // Prefer extending the existing direction, but allow starting the other side
        // of a tip when both choices turn sharply. Preview and explicit selection
        // remain available for genuinely ambiguous geometry.
        bool bothTurn = firstAlignment < .2 && lastAlignment < .2;
        double firstCost = fromFirst.Length / (bothTurn ? 1 : Math.Max(.15, firstAlignment));
        double lastCost = fromLast.Length / (bothTurn ? 1 : Math.Max(.15, lastAlignment));
        // Near-parallel sides may have almost equal costs even though one curves
        // slightly more. Keep tracing the current side until the other end is a
        // clearly better choice, rather than jumping across on small differences.
        if (Math.Abs(firstCost - lastCost) <= Math.Max(1, .15 * Math.Min(firstCost, lastCost)))
        {
            if (_traceLastExtension == (SelectedTraceArm, points[0].Index)) return new(points[0].Index, null, points[0]);
            if (_traceLastExtension == (SelectedTraceArm, points[^1].Index)) return new(null, points[^1], null);
        }
        return firstCost < lastCost ? new(points[0].Index, null, points[0]) : new(null, points[^1], null);
    }

    private bool IsTraceEndpoint(int arm, int index)
    {
        var points = (TracePoints ?? []).Where(p => p.Arm == arm).ToArray();
        return points.Length > 0 && (points[0].Index == index || points[^1].Index == index);
    }

    private void ValidateTraceSelection()
    {
        if (_selectedTrace is { } selected && !(TracePoints ?? []).Any(p => p.Arm == selected.Arm && p.Index == selected.Index))
            _selectedTrace = null;
        if (_traceContinuation is { } endpoint && (endpoint.Arm != SelectedTraceArm || !IsTraceEndpoint(endpoint.Arm, endpoint.Index)))
            _traceContinuation = null;
        if (_traceLastExtension is { } last && (last.Arm != SelectedTraceArm || !IsTraceEndpoint(last.Arm, last.Index)))
            _traceLastExtension = null;
    }

    internal void ClearTraceContinuation()
    {
        _traceContinuation = null;
        UpdateTraceDescription();
        InvalidateVisual();
    }

    internal void PreviewTraceAt(Point? screen)
    {
        _traceHover = screen;
        InvalidateVisual();
    }

    public void RemoveSelectedTracePoint()
    {
        if (!CanEditTrace || Dragging || _selectedTrace is not { } selected) return;
        var point = TracePoints?.FirstOrDefault(p => p.Arm == selected.Arm && p.Index == selected.Index);
        if (point is null) return;
        var edit = new CalibrationTraceEdit(point.Arm, point.Index, point.XMm, point.YMm, true);
        if (TraceEditedCommand?.CanExecute(edit) != true) return;
        TraceEditedCommand.Execute(edit);
        _selectedTrace = null;
        ValidateTraceSelection();
        SetValue(SelectionDescriptionPropertyKey, L.T("已删除此点；点击照片继续描当前曲线。") );
        InvalidateVisual();
    }

    public void RemoveSelectedRing()
    {
        if (!CanEditRings || ThreePointMode || Dragging || _selectedRing is not { } index) return;
        var edit = new CalibrationRingEdit(index, null);
        if (RingEditedCommand?.CanExecute(edit) != true) return;
        RingEditedCommand.Execute(edit);
        _selectedRing = null;
        SetValue(SelectionDescriptionPropertyKey, L.T(HasWideBandTarget ? "BandEdges.DragHint" : "Xaml.CoarseRingStatusHint"));
        InvalidateVisual();
    }

    public bool AddRingPointAt(Point screen)
    {
        if ((!CanMarkBandRay && !(CanEditRings && ThreePointMode)) || Dragging || SelectedRingIndex < 0) return false;
        var world = ViewToWorld(screen);
        double radius = ((Vector)world).Length;
        if (!double.IsFinite(radius) || radius < 7.5 || radius > PhysicalRadiusMm) return false;
        var point = new CalibrationPoint(world.X, world.Y);
        var command = CanMarkBandRay ? BandRayPointCommand : RingPointCommand;
        if (command?.CanExecute(point) != true) return false;
        command.Execute(point);
        return true;
    }

    /// <summary>Mark an end or an intermediate point on the selected short arc.</summary>
    public bool AddArcLengthPointAt(Point screen)
    {
        if (!CanMarkArcLength || Dragging) return false;
        var world = ViewToWorld(screen);
        double radius = ((Vector)world).Length;
        if (!double.IsFinite(radius) || radius < 7.5 || radius > PhysicalRadiusMm) return false;
        var point = new CalibrationPoint(world.X, world.Y);
        if (ArcLengthPointCommand?.CanExecute(point) != true) return false;
        ArcLengthPointCommand.Execute(point);
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(DiscTheme.PhotoBackdrop, null, new Rect(RenderSize));
        var bitmap = ShowingTarget ? FallbackImage : Image;
        var bounds = WorldBounds;
        var viewBounds = new Rect(WorldToView(bounds.TopLeft), WorldToView(bounds.BottomRight));
        if (bitmap is not null) dc.DrawImage(bitmap, viewBounds);
        else DrawPlaceholder(dc, bounds);

        if (ShowingRawPhoto) DrawBoundaries(dc);
        else if (CanMarkArcLength) DrawArcLengthPoints(dc);
        else if (CanMarkBandRay) DrawBandRay(dc);
        else if (ShowingClockPhoto)
        {
            // Show contributing areas on the rim, leaving the measured marks unobstructed.
            double radius = PhysicalRadiusMm + .7;
            foreach (double angle in ClockReadAngles ?? [])
                DrawRingSegment(dc, radius, (angle - 22.5) * Math.PI / 180, Math.PI / 4, new Pen(DiscTheme.Trace, 3));
            if (PreferredClockAngle is { } preferred)
                dc.DrawEllipse(DiscTheme.Handle, new Pen(DiscTheme.Outline, 1), OnRing(radius, preferred * Math.PI / 180), 3.5, 3.5);
        }
        else if (CanEditRings && ThreePointMode) DrawMeasuredPoints(dc);
        else if (CanEditRings) DrawRings(dc);
        else if (CanEditTrace)
        {
            if (ShowPrediction && PredictionContours is { } predictions)
                for (int i = 0; i < predictions.Count; i++) DrawPath(dc, predictions[i], new Pen(DiscTheme.Prediction, 1.8), false);
            foreach (int arm in (TracePoints ?? []).Select(point => point.Arm).Distinct())
            {
                // The VM orders samples along the curve. Index is a stable editing identity,
                // so sorting by it would reconnect inserted samples in the wrong order.
                var points = TracePoints?.Where(p => p.Arm == arm).ToArray() ?? [];
                var brush = DiscTheme.Trace;
                var pen = new Pen(brush, IsBoundarySegmentMode && arm == SelectedTraceArm ? 2 : 1.2) { DashStyle = DashStyles.Dot };
                DrawPath(dc, points.Select(TraceDisplayPosition).ToArray(), pen, false);
                if (IsBoundarySegmentMode && points.Length > 0)
                    DrawText(dc, L.Format("HalfDisc.SegmentBadge", arm + 1),
                        WorldToView(TraceDisplayPosition(points[0])) + new Vector(8, 8), brush, 11);
                foreach (var point in points)
                {
                    bool selected = _selectedTrace == (point.Arm, point.Index);
                    bool outlier = OutlierTracePointIndices?.Contains(point.Index) == true;
                    Brush pointBrush = outlier ? DiscTheme.Outlier : brush;
                    var screen = WorldToView(TraceDisplayPosition(point));
                    dc.DrawEllipse(pointBrush, new Pen(DiscTheme.Outline, 1.4), screen, selected ? 6 : 4, selected ? 6 : 4);
                    if (IsBoundarySegmentMode && arm == SelectedTraceArm
                        && (point.Index == points[0].Index || point.Index == points[^1].Index))
                    {
                        bool fixedEnd = _traceContinuation == (point.Arm, point.Index);
                        dc.DrawEllipse(null, new Pen(fixedEnd ? DiscTheme.Prediction : brush, fixedEnd ? 2.3 : 1.3),
                            screen, fixedEnd ? 10 : 7.5, fixedEnd ? 10 : 7.5);
                        if (fixedEnd) DrawText(dc, L.T("HalfDisc.ContinueBadge"), screen + new Vector(10, 7), DiscTheme.Prediction, 11);
                    }
                    if (selected) DrawText(dc, L.F($"点 {point.Index + 1}"), screen + new Vector(9, -16), pointBrush, 12);
                }
            }
            DrawTraceInsertionPreview(dc);
        }
        if (ShowingTarget && bitmap is null)
            DrawCaption(dc, L.T("CalibrationArt.Empty"));
        else if (ShowingTarget && HasClockTarget)
            DrawCaption(dc, L.T("Clock.PreviewCaption"));
        else if (ShowingTarget)
            DrawCaption(dc, Image is null ? L.T("标靶预览 · 导入照片后开始矫正") : L.T("刻录图案预览 · 关闭预览后继续照片标定"));
        else if (IsRectificationPreview) DrawCaption(dc, L.T("Xaml.RectifiedCalibrationCaption"));
    }

    private double RingAngle => RingAngleDegrees * Math.PI / 180;

    private void DrawTraceInsertionPreview(DrawingContext dc)
    {
        if (!IsBoundarySegmentMode || Dragging || _panning || _traceHover is not { } screen) return;
        Point world = ViewToWorld(screen);
        double radius = ((Vector)world).Length;
        if (!WorldBounds.Contains(world) || radius < 7.5 || radius > PhysicalRadiusMm
            || (TracePoints ?? []).Any(p => (WorldToView(Position(p)) - screen).Length <= 10)) return;
        var edit = FindTraceInsertion(screen);
        var pen = new Pen(DiscTheme.Trace, 1.6) { DashStyle = DashStyles.Dash };
        if (edit.Previous is { } previous) dc.DrawLine(pen, WorldToView(Position(previous)), screen);
        if (edit.Next is { } next) dc.DrawLine(pen, screen, WorldToView(Position(next)));
        dc.DrawEllipse(null, new Pen(DiscTheme.Trace, 1.5), screen, 4.5, 4.5);
    }
    private Point OnRing(double radius, double angle) =>
        WorldToView(new Point(radius * Math.Cos(angle), radius * Math.Sin(angle)));

    private Point RingBadge(CalibrationRingArc arc, double radius) =>
        OnRing(radius, RingAngle + (arc.RingIndex % 2 == 0 ? .22 : -.22));

    private CalibrationRingArc? HitRing(Point screen)
    {
        if (RingArcs is not { } arcs) return null;
        var badge = arcs.MinBy(arc => (RingBadge(arc, arc.RadiusMm) - screen).LengthSquared);
        if (badge is not null && (RingBadge(badge, badge.RadiusMm) - screen).Length <= 8) return badge;
        Point world = ViewToWorld(screen);
        double angle = Math.Atan2(world.Y, world.X);
        if (Math.Abs(Math.IEEERemainder(angle - RingAngle, Math.PI)) > ArcHalfAngle) return null;
        var closest = arcs.MinBy(arc => (OnRing(arc.RadiusMm, angle) - screen).LengthSquared);
        return closest is not null && (OnRing(closest.RadiusMm, angle) - screen).Length <= 8 ? closest : null;
    }

    private void DrawMeasuredPoints(DrawingContext dc)
    {
        if (RingPointMarkers is not { Count: > 0 } points) return;
        var ring = RingArcs?.FirstOrDefault(r => r.RingIndex == SelectedRingIndex);
        if (ring is { IsConfirmed: true, Points.Count: 3 } && ring.Points.SequenceEqual(points))
        {
            // Leave the unsampled side clear. Removing the largest gap also handles
            // three marks on an arc straddling the -π / π boundary.
            var angles = points.Select(p => Math.Atan2(p.Y, p.X)).Order().ToArray();
            double start = angles[0], sweep = angles[^1] - start, largestGap = 2 * Math.PI - sweep;
            for (int i = 1; i < angles.Length; i++)
            {
                double gap = angles[i] - angles[i - 1];
                if (gap <= largestGap) continue;
                largestGap = gap;
                start = angles[i];
                sweep = 2 * Math.PI - gap;
            }
            const double pad = Math.PI / 36;
            DrawRingSegment(dc, ring.RadiusMm, start - pad, sweep + 2 * pad, new Pen(DiscTheme.Trace, 1.6));
        }
        for (int i = 0; i < points.Count; i++)
        {
            var position = WorldToView(new Point(points[i].X, points[i].Y));
            dc.DrawEllipse(DiscTheme.HubOutline, new Pen(DiscTheme.Outline, 1), position, 3.5, 3.5);
            var label = Text((i + 1).ToString(CultureInfo.CurrentCulture), 11, DiscTheme.Handle);
            Point at = position + new Vector(5, -label.Height);
            dc.DrawRoundedRectangle(DiscTheme.Caption, null, new Rect(at.X - 2, at.Y, label.Width + 4, label.Height), 2, 2);
            dc.DrawText(label, at);
        }
    }

    private void DrawArcLengthPoints(DrawingContext dc)
    {
        if (ArcLengthPoints is not { Count: > 0 } points) return;
        if (CalibrationArcLengthMark.TrySpan(points, out double signedDegrees))
        {
            double first = Math.Atan2(points[0].Y, points[0].X);
            double last = Math.Atan2(points[2].Y, points[2].X);
            double start = signedDegrees >= 0 ? first : last;
            double sweep = Math.Abs(signedDegrees) * Math.PI / 180;
            double radius = points.Average(p => double.Hypot(p.X, p.Y));
            DrawRingSegment(dc, radius, start, sweep, new Pen(DiscTheme.Trace, 1.6));
        }
        for (int i = 0; i < points.Count; i++)
        {
            var position = WorldToView(new Point(points[i].X, points[i].Y));
            dc.DrawEllipse(DiscTheme.HubOutline, new Pen(DiscTheme.Outline, 1), position, 3.5, 3.5);
            var label = Text((i + 1).ToString(CultureInfo.CurrentCulture), 11, DiscTheme.Handle);
            Point at = position + new Vector(5, -label.Height);
            dc.DrawRoundedRectangle(DiscTheme.Caption, null, new Rect(at.X - 2, at.Y, label.Width + 4, label.Height), 2, 2);
            dc.DrawText(label, at);
        }
    }

    private void DrawRingSegment(DrawingContext dc, double radius, double start, double sweep, Pen pen)
    {
        double pixels = (OnRing(radius, 0) - WorldToView(new Point())).Length;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(OnRing(radius, start), false, false);
            path.ArcTo(OnRing(radius, start + sweep), new Size(pixels, pixels), 0,
                sweep > Math.PI, SweepDirection.Clockwise, true, true);
        }
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawRings(DrawingContext dc)
    {
        if (RingArcs is null) return;
        foreach (var arc in RingArcs)
        {
            double r = _ringDrag?.RingIndex == arc.RingIndex ? _ringDragRadius : arc.RadiusMm;
            bool selected = _selectedRing == arc.RingIndex;
            Brush brush = selected ? DiscTheme.HubOutline : arc.IsConfirmed ? DiscTheme.Trace : DiscTheme.Edge;
            var pen = new Pen(brush, selected ? 2.2 : 1.4);
            if (!arc.IsConfirmed) pen.DashStyle = DashStyles.Dash;
            DrawRingSegment(dc, r, RingAngle - ArcHalfAngle, 2 * ArcHalfAngle, pen);
            DrawRingSegment(dc, r, RingAngle + Math.PI - ArcHalfAngle, 2 * ArcHalfAngle, pen);
            Point badge = RingBadge(arc, r);
            var label = Text(HasWideBandTarget ? arc.ShortEdgeLabel
                : (arc.RingIndex + 1).ToString(CultureInfo.CurrentCulture), 10, brush);
            if (HasWideBandTarget)
                dc.DrawRoundedRectangle(DiscTheme.Caption, new Pen(brush, 1),
                    new Rect(badge.X - label.Width / 2 - 3, badge.Y - label.Height / 2 - 1, label.Width + 6, label.Height + 2), 3, 3);
            else dc.DrawEllipse(DiscTheme.Caption, new Pen(brush, 1), badge, 7.5, 7.5);
            dc.DrawText(label, badge - new Vector(label.Width / 2, label.Height / 2));
        }
    }

    private void DrawBandRay(DrawingContext dc)
    {
        if (BandRayAngleDegrees is not double degrees || RingArcs is null) return;
        double angle = degrees * Math.PI / 180;
        dc.DrawLine(new Pen(DiscTheme.Edge, 1) { DashStyle = DashStyles.Dash },
            OnRing(7.5, angle), OnRing(PhysicalRadiusMm, angle));
        var normal = new Vector(-Math.Sin(angle), Math.Cos(angle));
        foreach (var edge in RingArcs)
        {
            if (edge.Points is not { Count: 1 } points) continue;
            var mark = WorldToView(new Point(points[0].X, points[0].Y));
            dc.DrawEllipse(DiscTheme.Handle, new Pen(DiscTheme.Outline, 1), mark, 3.5, 3.5);
            var label = Text(edge.ShortEdgeLabel, 10, DiscTheme.Trace);
            // Put inner and outer labels on opposite sides of the guide even when it is vertical.
            double offset = 5 + (Math.Abs(normal.X) * label.Width + Math.Abs(normal.Y) * label.Height) / 2;
            var at = mark + normal * (edge.RingIndex < 3 ? -offset : offset) - new Vector(label.Width / 2, label.Height / 2);
            dc.DrawRoundedRectangle(DiscTheme.Caption, null, new Rect(at.X - 2, at.Y, label.Width + 4, label.Height), 2, 2);
            dc.DrawText(label, at);
        }
    }

    private void UpdateRingDescription(int index, double radius) =>
        SetValue(SelectionDescriptionPropertyKey, HasWideBandTarget
            ? L.Format("BandEdges.Selected", CalibrationRingArc.WideEdgeLabel(index), radius)
            : L.Format("Xaml.CoarseRingSelected", index + 1, radius));

    private void DrawBoundaries(DrawingContext dc)
    {
        foreach (var kind in new[] { CalibrationPhotoBoundaryKind.OuterEdge, CalibrationPhotoBoundaryKind.CenterHole })
        {
            Brush brush = kind == CalibrationPhotoBoundaryKind.OuterEdge ? DiscTheme.Edge : DiscTheme.HubOutline;
            var points = BoundaryPoints?.Where(p => p.Boundary == kind).OrderBy(p => p.Index).ToArray() ?? [];
            var outline = BoundaryContours?.ElementAtOrDefault((int)kind);
            if (_boundaryDrag?.Boundary == kind || outline is not { Length: > 2 })
                DrawPath(dc, points.Select(BoundaryDisplayPosition).ToArray(), new Pen(brush, 1.3) { DashStyle = DashStyles.Dash }, true);
            else DrawPath(dc, outline, new Pen(brush, 1.6), true);
            foreach (var point in points)
            {
                bool selected = _selectedBoundary == (point.Boundary, point.Index);
                var screen = WorldToView(BoundaryDisplayPosition(point));
                dc.DrawEllipse(brush, new Pen(DiscTheme.Outline, 1.2), screen, selected ? 6 : 4.5, selected ? 6 : 4.5);
            }
        }
    }

    private void DrawPlaceholder(DrawingContext dc, Rect bounds)
    {
        var center = WorldToView(new Point());
        double radius = Math.Min(bounds.Width, bounds.Height) * .44;
        var pen = new Pen(DiscTheme.Handle, 1.5);
        foreach (double r in new[] { radius, radius / 8 })
        {
            double viewRadius = (WorldToView(new Point(r, 0)) - center).Length;
            dc.DrawEllipse(null, pen, center, viewRadius, viewRadius);
        }
    }

    private void DrawPath(DrawingContext dc, IReadOnlyList<Point> points, Pen pen, bool closed)
    {
        if (points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            bool started = false;
            foreach (var point in points)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) { started = false; continue; }
                var screen = WorldToView(point);
                if (!started) { context.BeginFigure(screen, false, closed); started = true; }
                else context.LineTo(screen, true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawCaption(DrawingContext dc, string text)
    {
        var label = Text(text, 11, DiscTheme.Handle);
        // A compact caption stays in the lower-left margin instead of covering the top disc edge.
        var bounds = new Rect(8, Math.Max(4, ActualHeight - label.Height - 12), label.Width + 12, label.Height + 6);
        dc.DrawRoundedRectangle(DiscTheme.Caption, null, bounds, 4, 4);
        dc.DrawText(label, new Point(bounds.Left + 6, bounds.Top + 3));
    }
    private void DrawText(DrawingContext dc, string text, Point at, Brush brush, double size)
        => dc.DrawText(Text(text, size, brush), at);
    private FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            DiscTheme.Typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private Point BoundaryDisplayPosition(CalibrationPhotoBoundaryPoint point)
        => _boundaryDrag?.Boundary == point.Boundary && _boundaryDrag.Index == point.Index ? _dragPosition : new(point.X, point.Y);
    private Point TraceDisplayPosition(CalibrationTracePoint point)
        => _traceDrag?.Arm == point.Arm && _traceDrag.Index == point.Index ? _dragPosition : Position(point);
    private static Point Position(CalibrationTracePoint point) => new(point.XMm, point.YMm);
    private static void Execute(ICommand? command, object edit) { if (command?.CanExecute(edit) == true) command.Execute(edit); }
    private void SelectTraceNear(int arm, Point position)
    {
        var point = TracePoints?.Where(p => p.Arm == arm).MinBy(p => (Position(p) - position).LengthSquared);
        _selectedTrace = point is null || (Position(point) - position).Length > 1e-6 ? null : (point.Arm, point.Index);
        UpdateTraceDescription();
    }
    private void UpdateTraceDescription() => SetValue(SelectionDescriptionPropertyKey,
        IsBoundarySegmentMode ? L.Format(_traceContinuation is not null ? "HalfDisc.EndpointFixed" : "HalfDisc.CanvasHint", SelectedTraceArm + 1)
            : _selectedTrace is { } point ? L.F($"已选曲线上的点 {point.Index + 1} · 直接拖动修正，Delete 删除")
            : L.T("点击照片上同一条可见曲线，由内向外连续描点。") );
    private static void OnTraceSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (CalibrationPhotoCanvas)d;
        if (canvas._selectedTrace?.Arm != canvas.SelectedTraceArm) canvas._selectedTrace = null;
        canvas._traceContinuation = null;
        canvas._traceLastExtension = null;
        if (canvas.CanEditTrace) canvas.UpdateTraceDescription();
    }
    private static void OnViewportChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (CalibrationPhotoCanvas)d;
        canvas.CompleteDrag(false);
        canvas._selectedTrace = null;
        canvas._traceContinuation = null;
        canvas._traceLastExtension = null;
        canvas._traceHover = null;
        canvas._selectedBoundary = null;
        canvas._selectedRing = null;
        // Ring and cross measurements share the same rectified millimetre coordinates.
        if (e.Property != IsCoarseModeProperty && e.Property != ThreePointModeProperty
            && e.Property != HasClockTargetProperty && e.Property != HasRadialArcTargetProperty
            && e.Property != HasWideBandTargetProperty && e.Property != IsBandRayModeProperty
            && e.Property != IsBoundarySegmentModeProperty) canvas.ResetView();
        canvas.SetValue(SelectionDescriptionPropertyKey, canvas.DefaultSelectionDescription());
    }
    private static void OnCollectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (CalibrationPhotoCanvas)d;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= canvas.PointsChanged;
        if (e.NewValue is INotifyCollectionChanged current) current.CollectionChanged += canvas.PointsChanged;
        if (e.Property == TracePointsProperty) { canvas.ValidateTraceSelection(); canvas.UpdateTraceDescription(); }
    }
    private void PointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ValidateTraceSelection();
        if (CanEditTrace) UpdateTraceDescription();
        InvalidateVisual();
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var position = e.GetPosition(this);
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle || e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space))
        {
            _panning = true;
            _panStart = position;
            _panBefore = _pan;
            CaptureMouse();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            if (CanMarkArcLength) AddArcLengthPointAt(position);
            else if (ShowingClockPhoto)
            {
                Point world = ViewToWorld(position);
                double radius = ((Vector)world).Length;
                if (radius >= 7.5 && radius <= PhysicalRadiusMm)
                    Execute(PreferClockAngleCommand, (Math.Atan2(world.Y, world.X) * 180 / Math.PI + 360) % 360);
            }
            else if (CanMarkBandRay || CanEditRings && ThreePointMode) AddRingPointAt(position);
            else if (BeginDragAt(position)) CaptureMouse();
            else AddTraceAt(position);
            e.Handled = true;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        _traceHover = position;
        if (_panning) { _pan = _panBefore + (position - _panStart); InvalidateVisual(); }
        else if (Dragging) DragTo(position);
        else if (IsBoundarySegmentMode && CanEditTrace) InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _traceHover = null;
        InvalidateVisual();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning) { _panning = false; ReleaseMouseCapture(); }
        else if (Dragging) CompleteDrag();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _panning = false;
        if (Dragging) CompleteDrag(false);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { base.OnMouseWheel(e); ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this)); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (CanMarkArcLength && e.Key is Key.Back or Key.Delete)
        {
            if (UndoArcLengthPointCommand?.CanExecute(null) == true) UndoArcLengthPointCommand.Execute(null);
            e.Handled = true;
        }
        else if (CanEditRings && ThreePointMode && e.Key is Key.Back or Key.Delete)
        {
            if (UndoRingPointCommand?.CanExecute(null) == true) UndoRingPointCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (CanEditRings) RemoveSelectedRing(); else RemoveSelectedTracePoint();
            e.Handled = true;
        }
        if (e.Key == Key.Escape) { CompleteDrag(false); ClearTraceContinuation(); e.Handled = true; }
    }
}
