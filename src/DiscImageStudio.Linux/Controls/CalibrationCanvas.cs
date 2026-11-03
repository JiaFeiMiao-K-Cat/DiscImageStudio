using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using DiscImageStudio.Linux.ViewModels;

namespace DiscImageStudio.Linux.Controls;

/// <summary>The calibration photo canvas, ported from the Windows CalibrationPhotoCanvas.
/// While outlining, world coordinates are raw-photo pixels (pixel centres, so the bounds run
/// from -0.5 to size - 0.5); the rectified photo, tracing and the target preview use disc
/// millimetres with the centre at the origin and +y down. Wheel zooms around the pointer,
/// right/middle drag (or Space + drag) pans, left drag moves a point and a left click on the
/// rectified photo adds a trace point. Delete removes the selected trace point.</summary>
public sealed class CalibrationCanvas : Control
{
    private const double BoundaryHitRadius = 12;
    private const double TraceHitRadius = 11;

    public static readonly StyledProperty<Bitmap?> ImageProperty =
        AvaloniaProperty.Register<CalibrationCanvas, Bitmap?>(nameof(Image));

    public static readonly StyledProperty<Bitmap?> FallbackImageProperty =
        AvaloniaProperty.Register<CalibrationCanvas, Bitmap?>(nameof(FallbackImage));

    public static readonly StyledProperty<bool> IsPhotoStepProperty =
        AvaloniaProperty.Register<CalibrationCanvas, bool>(nameof(IsPhotoStep), true);

    public static readonly StyledProperty<bool> IsRectificationPreviewProperty =
        AvaloniaProperty.Register<CalibrationCanvas, bool>(nameof(IsRectificationPreview));

    public static readonly StyledProperty<bool> IsTargetPreviewProperty =
        AvaloniaProperty.Register<CalibrationCanvas, bool>(nameof(IsTargetPreview));

    public static readonly StyledProperty<double> CanvasRadiusMmProperty =
        AvaloniaProperty.Register<CalibrationCanvas, double>(nameof(CanvasRadiusMm), 63);

    public static readonly StyledProperty<double> TargetCanvasRadiusMmProperty =
        AvaloniaProperty.Register<CalibrationCanvas, double>(nameof(TargetCanvasRadiusMm), 60);

    public static readonly StyledProperty<double> PhysicalRadiusMmProperty =
        AvaloniaProperty.Register<CalibrationCanvas, double>(nameof(PhysicalRadiusMm), 60);

    public static readonly StyledProperty<IReadOnlyList<CalibrationBoundaryPoint>?> BoundaryPointsProperty =
        AvaloniaProperty.Register<CalibrationCanvas, IReadOnlyList<CalibrationBoundaryPoint>?>(nameof(BoundaryPoints));

    public static readonly StyledProperty<IReadOnlyList<Point[]>?> BoundaryContoursProperty =
        AvaloniaProperty.Register<CalibrationCanvas, IReadOnlyList<Point[]>?>(nameof(BoundaryContours));

    public static readonly StyledProperty<IReadOnlyList<CalibrationTracePoint>?> TracePointsProperty =
        AvaloniaProperty.Register<CalibrationCanvas, IReadOnlyList<CalibrationTracePoint>?>(nameof(TracePoints));

    public static readonly StyledProperty<IReadOnlyList<Point[]>?> PredictionContoursProperty =
        AvaloniaProperty.Register<CalibrationCanvas, IReadOnlyList<Point[]>?>(nameof(PredictionContours));

    public static readonly StyledProperty<IReadOnlyList<int>?> OutlierIndicesProperty =
        AvaloniaProperty.Register<CalibrationCanvas, IReadOnlyList<int>?>(nameof(OutlierIndices));

    public static readonly StyledProperty<bool> ShowPredictionProperty =
        AvaloniaProperty.Register<CalibrationCanvas, bool>(nameof(ShowPrediction), true);

    public static readonly DirectProperty<CalibrationCanvas, string> ViewDescriptionProperty =
        AvaloniaProperty.RegisterDirect<CalibrationCanvas, string>(nameof(ViewDescription), o => o.ViewDescription);

    public static readonly DirectProperty<CalibrationCanvas, string> SelectionDescriptionProperty =
        AvaloniaProperty.RegisterDirect<CalibrationCanvas, string>(nameof(SelectionDescription), o => o.SelectionDescription);

    private const string PhotoHelp = "Scroll to zoom, right-drag to pan. Drag the points onto the disc's edges.";
    private const string TraceHelp = "Scroll to zoom, right-drag to pan. Click to add a point; drag a point to move it.";

    private double _zoom = 1;
    private Vector _pan;
    private bool _panning;
    private bool _spaceDown;
    private Point _panStart;
    private Vector _panBefore;
    private CalibrationBoundaryPoint? _boundaryDrag;
    private CalibrationTracePoint? _traceDrag;
    private Point _dragStart;
    private Point _dragOriginal;
    private Point _dragPosition;
    private bool _hasMoved;
    private int? _selectedTrace;
    private (CalibrationBoundaryKind Boundary, int Index)? _selectedBoundary;
    private string _viewDescription = "100%";
    private string _selectionDescription = PhotoHelp;

    static CalibrationCanvas()
    {
        AffectsRender<CalibrationCanvas>(ImageProperty, FallbackImageProperty, IsPhotoStepProperty, IsRectificationPreviewProperty,
            IsTargetPreviewProperty, CanvasRadiusMmProperty, TargetCanvasRadiusMmProperty, PhysicalRadiusMmProperty,
            BoundaryPointsProperty, BoundaryContoursProperty, TracePointsProperty, PredictionContoursProperty,
            OutlierIndicesProperty, ShowPredictionProperty);
        ClipToBoundsProperty.OverrideDefaultValue<CalibrationCanvas>(true);
        FocusableProperty.OverrideDefaultValue<CalibrationCanvas>(true);
    }

    public CalibrationCanvas()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>A boundary point was dropped somewhere new (photo pixels).</summary>
    public event Action<CalibrationBoundaryKind, int, double, double>? BoundaryEdited;

    /// <summary>A trace point was added (index -1), moved, or removed (disc millimetres).</summary>
    public event Action<int, double, double, bool>? TraceEdited;

    public Bitmap? Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }

    public Bitmap? FallbackImage { get => GetValue(FallbackImageProperty); set => SetValue(FallbackImageProperty, value); }

    public bool IsPhotoStep { get => GetValue(IsPhotoStepProperty); set => SetValue(IsPhotoStepProperty, value); }

    public bool IsRectificationPreview { get => GetValue(IsRectificationPreviewProperty); set => SetValue(IsRectificationPreviewProperty, value); }

    public bool IsTargetPreview { get => GetValue(IsTargetPreviewProperty); set => SetValue(IsTargetPreviewProperty, value); }

    public double CanvasRadiusMm { get => GetValue(CanvasRadiusMmProperty); set => SetValue(CanvasRadiusMmProperty, value); }

    public double TargetCanvasRadiusMm { get => GetValue(TargetCanvasRadiusMmProperty); set => SetValue(TargetCanvasRadiusMmProperty, value); }

    /// <summary>The physical disc radius, drawn as a reference circle on the rectified photo.</summary>
    public double PhysicalRadiusMm { get => GetValue(PhysicalRadiusMmProperty); set => SetValue(PhysicalRadiusMmProperty, value); }

    public IReadOnlyList<CalibrationBoundaryPoint>? BoundaryPoints { get => GetValue(BoundaryPointsProperty); set => SetValue(BoundaryPointsProperty, value); }

    public IReadOnlyList<Point[]>? BoundaryContours { get => GetValue(BoundaryContoursProperty); set => SetValue(BoundaryContoursProperty, value); }

    public IReadOnlyList<CalibrationTracePoint>? TracePoints { get => GetValue(TracePointsProperty); set => SetValue(TracePointsProperty, value); }

    public IReadOnlyList<Point[]>? PredictionContours { get => GetValue(PredictionContoursProperty); set => SetValue(PredictionContoursProperty, value); }

    public IReadOnlyList<int>? OutlierIndices { get => GetValue(OutlierIndicesProperty); set => SetValue(OutlierIndicesProperty, value); }

    public bool ShowPrediction { get => GetValue(ShowPredictionProperty); set => SetValue(ShowPredictionProperty, value); }

    public string ViewDescription
    {
        get => _viewDescription;
        private set => SetAndRaise(ViewDescriptionProperty, ref _viewDescription, value);
    }

    public string SelectionDescription
    {
        get => _selectionDescription;
        private set => SetAndRaise(SelectionDescriptionProperty, ref _selectionDescription, value);
    }

    private bool ShowingTarget => IsTargetPreview || Image is null;

    private bool ShowingRawPhoto => !ShowingTarget && IsPhotoStep && !IsRectificationPreview;

    private bool CanEditTrace => !ShowingTarget && !IsPhotoStep && !IsRectificationPreview;

    private bool Dragging => _boundaryDrag is not null || _traceDrag is not null;

    private Rect WorldBounds
    {
        get
        {
            // Source coordinates address pixel centres, matching the rectifier's sampling convention.
            if (ShowingRawPhoto && Image is { } photo)
            {
                return new Rect(-0.5, -0.5, photo.PixelSize.Width, photo.PixelSize.Height);
            }

            double radius = ShowingTarget ? TargetCanvasRadiusMm : CanvasRadiusMm;
            radius = double.IsFinite(radius) && radius > 0 ? radius : 63;
            return new Rect(-radius, -radius, radius * 2, radius * 2);
        }
    }

    private double Scale
    {
        get
        {
            Rect world = WorldBounds;
            return Math.Max(0.0001, Math.Min(Bounds.Width / world.Width, Bounds.Height / world.Height) * 0.92 * _zoom);
        }
    }

    public Point WorldToView(Point world)
    {
        Rect bounds = WorldBounds;
        double scale = Scale;
        return new Point((world.X - bounds.Center.X) * scale + Bounds.Width / 2 + _pan.X,
            (world.Y - bounds.Center.Y) * scale + Bounds.Height / 2 + _pan.Y);
    }

    public Point ViewToWorld(Point view)
    {
        Rect bounds = WorldBounds;
        double scale = Scale;
        return new Point((view.X - Bounds.Width / 2 - _pan.X) / scale + bounds.Center.X,
            (view.Y - Bounds.Height / 2 - _pan.Y) / scale + bounds.Center.Y);
    }

    public void ResetView()
    {
        _zoom = 1;
        _pan = default;
        ViewDescription = "100%";
        InvalidateVisual();
    }

    public void ZoomBy(double factor) => ZoomAt(factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    private void ZoomAt(double factor, Point screen)
    {
        if (!double.IsFinite(factor) || factor <= 0 || Dragging)
        {
            return;
        }

        Point anchor = ViewToWorld(screen);
        _zoom = Math.Clamp(_zoom * factor, 0.5, 30);
        _pan += screen - WorldToView(anchor);
        ViewDescription = $"{_zoom * 100:0}%";
        InvalidateVisual();
    }

    private bool BeginDragAt(Point screen)
    {
        if (ShowingRawPhoto)
        {
            CalibrationBoundaryPoint? point = BoundaryPoints?
                .Select(p => (Point: p, Distance: Distance(WorldToView(new Point(p.X, p.Y)), screen)))
                .Where(p => p.Distance <= BoundaryHitRadius)
                .OrderBy(p => p.Distance).Select(p => p.Point).FirstOrDefault();
            if (point is null)
            {
                return false;
            }

            _boundaryDrag = point;
            _selectedBoundary = (point.Boundary, point.Index);
            _dragOriginal = new Point(point.X, point.Y);
            SelectionDescription = point.Boundary == CalibrationBoundaryKind.OuterEdge
                ? "Drag onto the disc's physical outer edge, not the edge of the recorded area."
                : "Drag onto the physical edge of the centre hole, not the clear plastic ring.";
        }
        else if (CanEditTrace)
        {
            CalibrationTracePoint? point = TracePoints?
                .Select(p => (Point: p, Distance: Distance(WorldToView(Position(p)), screen)))
                .Where(p => p.Distance <= TraceHitRadius)
                .OrderBy(p => p.Distance).Select(p => p.Point).FirstOrDefault();
            if (point is null)
            {
                return false;
            }

            _traceDrag = point;
            _selectedTrace = point.Index;
            _dragOriginal = Position(point);
            UpdateTraceDescription();
        }
        else
        {
            return false;
        }

        _dragStart = ViewToWorld(screen);
        _dragPosition = _dragOriginal;
        _hasMoved = false;
        InvalidateVisual();
        return true;
    }

    private void DragTo(Point screen)
    {
        if (!Dragging)
        {
            return;
        }

        Point position = _dragOriginal + (ViewToWorld(screen) - _dragStart);
        Rect bounds = ShowingRawPhoto && Image is { } photo
            ? new Rect(0, 0, photo.PixelSize.Width - 1, photo.PixelSize.Height - 1)
            : WorldBounds;
        _dragPosition = new Point(Math.Clamp(position.X, bounds.Left, bounds.Right), Math.Clamp(position.Y, bounds.Top, bounds.Bottom));
        _hasMoved |= Distance(_dragPosition, _dragOriginal) > 1e-8;
        InvalidateVisual();
    }

    private void CompleteDrag(bool commit)
    {
        CalibrationBoundaryPoint? boundary = _boundaryDrag;
        CalibrationTracePoint? trace = _traceDrag;
        Point position = _dragPosition;
        bool changed = _hasMoved && commit;
        _boundaryDrag = null;
        _traceDrag = null;
        _hasMoved = false;
        if (changed && boundary is not null)
        {
            BoundaryEdited?.Invoke(boundary.Boundary, boundary.Index, position.X, position.Y);
        }

        if (changed && trace is not null)
        {
            TraceEdited?.Invoke(trace.Index, position.X, position.Y, false);
            SelectTraceNear(position);
        }

        InvalidateVisual();
    }

    private bool AddTraceAt(Point screen)
    {
        if (!CanEditTrace || Dragging)
        {
            return false;
        }

        Point point = ViewToWorld(screen);
        if (!WorldBounds.Contains(point))
        {
            return false;
        }

        TraceEdited?.Invoke(-1, point.X, point.Y, false);
        SelectTraceNear(point);
        InvalidateVisual();
        return true;
    }

    public void RemoveSelectedTracePoint()
    {
        if (!CanEditTrace || Dragging || _selectedTrace is not { } selected
            || TracePoints?.FirstOrDefault(p => p.Index == selected) is not { } point)
        {
            return;
        }

        TraceEdited?.Invoke(point.Index, point.XMm, point.YMm, true);
        _selectedTrace = null;
        SelectionDescription = "Point removed. Click the photo to keep tracing the curve.";
        InvalidateVisual();
    }

    private void SelectTraceNear(Point position)
    {
        CalibrationTracePoint? point = TracePoints?.MinBy(p => Distance(Position(p), position));
        _selectedTrace = point is null || Distance(Position(point), position) > 1e-6 ? null : point.Index;
        UpdateTraceDescription();
    }

    private void UpdateTraceDescription() => SelectionDescription = _selectedTrace is { } index
        ? $"Point {index + 1} selected · drag to correct it, Delete removes it."
        : "Click along one visible curve, from the inside out.";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImageProperty || change.Property == IsPhotoStepProperty
            || change.Property == IsRectificationPreviewProperty || change.Property == IsTargetPreviewProperty
            || change.Property == CanvasRadiusMmProperty)
        {
            // A different picture or coordinate system: drop the drag and start from a fitted view.
            _boundaryDrag = null;
            _traceDrag = null;
            _hasMoved = false;
            _selectedTrace = null;
            _selectedBoundary = null;
            ResetView();
            SelectionDescription = IsPhotoStep ? PhotoHelp : TraceHelp;
        }
    }

    // ---------------------------------------------------------------- rendering

    private sealed record Palette(IBrush Backdrop, IBrush Edge, IBrush Hub, IBrush Trace, IBrush Prediction, IBrush Outlier,
        IBrush Outline, IBrush Handle, IBrush Caption, IBrush CaptionText);

    private Palette Colors()
    {
        bool dark = ActualThemeVariant != ThemeVariant.Light;
        static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
        return dark
            ? new Palette(B("#13151A"), B("#74C0FF"), B("#F5A524"), B("#22D3EE"), B("#FFA94D"), B("#F31260"),
                B("#0B0B0D"), B("#80FFFFFF"), B("#D9101216"), B("#E6FFFFFF"))
            : new Palette(B("#D9DADE"), B("#0B66D0"), B("#B45309"), B("#0891B2"), B("#C2410C"), B("#E11D48"),
                B("#FFFFFF"), B("#80000000"), B("#E6FFFFFF"), B("#DE000000"));
    }

    public override void Render(DrawingContext context)
    {
        Palette palette = Colors();
        context.FillRectangle(palette.Backdrop, new Rect(Bounds.Size));
        Bitmap? bitmap = ShowingTarget ? FallbackImage : Image;
        Rect world = WorldBounds;
        Rect view = new(WorldToView(world.TopLeft), WorldToView(world.BottomRight));
        if (bitmap is not null)
        {
            double scale = view.Width / Math.Max(1, bitmap.PixelSize.Width);
            RenderOptions options = new()
            {
                BitmapInterpolationMode = scale > 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality,
            };
            using (context.PushRenderOptions(options))
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), view);
            }
        }
        else
        {
            DrawPlaceholder(context, world, palette);
        }

        if (ShowingRawPhoto)
        {
            DrawBoundaries(context, palette);
        }
        else if (!ShowingTarget)
        {
            // Reference circles: on a good rectification the photographed edges sit on them.
            Pen reference = new(palette.Handle, 1, DashStyle.Dash);
            foreach (double radius in new[] { PhysicalRadiusMm, CalibrationViewModel.HoleRadiusMm })
            {
                if (double.IsFinite(radius) && radius > 0)
                {
                    Point centre = WorldToView(default);
                    double r = radius * Scale;
                    context.DrawEllipse(null, reference, centre, r, r);
                }
            }
        }

        if (CanEditTrace)
        {
            DrawTraces(context, palette);
        }

        if (ShowingTarget)
        {
            DrawCaption(context, Image is null
                ? "Target preview · import a photo of the burned disc to continue"
                : "Burn pattern preview · turn the preview off to continue with the photo", palette);
        }
        else if (IsRectificationPreview)
        {
            DrawCaption(context, "Rectified preview · confirm it to start tracing", palette);
        }
    }

    private void DrawTraces(DrawingContext context, Palette palette)
    {
        if (ShowPrediction && PredictionContours is { } predictions)
        {
            Pen predictionPen = new(palette.Prediction, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            foreach (Point[] prediction in predictions)
            {
                DrawPath(context, prediction, predictionPen, closed: false);
            }
        }

        // The view model keeps the points in radial order. Index is a stable editing identity,
        // so sorting by it would reconnect inserted points in the wrong order.
        IReadOnlyList<CalibrationTracePoint> points = TracePoints ?? [];
        DrawPath(context, points.Select(TraceDisplayPosition).ToArray(), new Pen(palette.Trace, 1.2, DashStyle.Dot, PenLineCap.Round), closed: false);
        Pen outline = new(palette.Outline, 1.4);
        HashSet<int> outliers = OutlierIndices is { } list ? [.. list] : [];
        foreach (CalibrationTracePoint point in points)
        {
            bool selected = _selectedTrace == point.Index;
            IBrush brush = outliers.Contains(point.Index) ? palette.Outlier : palette.Trace;
            Point screen = WorldToView(TraceDisplayPosition(point));
            double radius = selected ? 6 : 4;
            context.DrawEllipse(brush, outline, screen, radius, radius);
            if (selected)
            {
                DrawText(context, $"Point {point.Index + 1}", screen + new Vector(9, -18), brush, 12);
            }
        }
    }

    private void DrawBoundaries(DrawingContext context, Palette palette)
    {
        foreach (CalibrationBoundaryKind kind in new[] { CalibrationBoundaryKind.OuterEdge, CalibrationBoundaryKind.CenterHole })
        {
            IBrush brush = kind == CalibrationBoundaryKind.OuterEdge ? palette.Edge : palette.Hub;
            CalibrationBoundaryPoint[] points = BoundaryPoints?.Where(p => p.Boundary == kind).OrderBy(p => p.Index).ToArray() ?? [];
            Point[]? outline = BoundaryContours is { } contours && contours.Count > (int)kind ? contours[(int)kind] : null;
            if (_boundaryDrag?.Boundary == kind || outline is not { Length: > 2 })
            {
                DrawPath(context, points.Select(BoundaryDisplayPosition).ToArray(), new Pen(brush, 1.3, DashStyle.Dash), closed: true);
            }
            else
            {
                DrawPath(context, outline, new Pen(brush, 1.6), closed: true);
            }

            Pen pen = new(palette.Outline, 1.2);
            foreach (CalibrationBoundaryPoint point in points)
            {
                bool selected = _selectedBoundary == (point.Boundary, point.Index);
                double radius = selected ? 6.5 : 5;
                context.DrawEllipse(brush, pen, WorldToView(BoundaryDisplayPosition(point)), radius, radius);
            }
        }
    }

    private void DrawPlaceholder(DrawingContext context, Rect world, Palette palette)
    {
        Point centre = WorldToView(default);
        double radius = Math.Min(world.Width, world.Height) * 0.44;
        Pen pen = new(palette.Handle, 1.5);
        for (int i = 1; i <= 5; i++)
        {
            double r = radius * i / 5 * Scale;
            context.DrawEllipse(null, pen, centre, r, r);
        }

        DrawPath(context, [new Point(-radius, 0), new Point(radius, 0)], pen, closed: false);
        DrawPath(context, [new Point(0, -radius), new Point(0, radius)], pen, closed: false);
    }

    private void DrawPath(DrawingContext context, IReadOnlyList<Point> points, Pen pen, bool closed)
    {
        if (points.Count < 2)
        {
            return;
        }

        StreamGeometry geometry = new();
        using (StreamGeometryContext builder = geometry.Open())
        {
            bool started = false;
            int segment = 0;
            foreach (Point point in points)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                {
                    if (started)
                    {
                        builder.EndFigure(false);
                    }

                    started = false;
                    segment++;
                    continue;
                }

                Point screen = WorldToView(point);
                if (!started)
                {
                    builder.BeginFigure(screen, false);
                    started = true;
                }
                else
                {
                    builder.LineTo(screen);
                }
            }

            if (started)
            {
                // Only an unbroken outline closes back to its start.
                builder.EndFigure(closed && segment == 0);
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawCaption(DrawingContext context, string text, Palette palette)
    {
        FormattedText label = Text(text, 11.5, palette.CaptionText);
        // Kept in the lower-left margin so it never covers the disc edge being checked.
        Rect box = new(8, Math.Max(4, Bounds.Height - label.Height - 14), label.Width + 14, label.Height + 8);
        context.DrawRectangle(palette.Caption, null, box, 5, 5);
        context.DrawText(label, new Point(box.Left + 7, box.Top + 4));
    }

    private static void DrawText(DrawingContext context, string text, Point at, IBrush brush, double size)
        => context.DrawText(Text(text, size, brush), at);

    private static FormattedText Text(string text, double size, IBrush brush)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, brush);

    private Point BoundaryDisplayPosition(CalibrationBoundaryPoint point)
        => _boundaryDrag is { } drag && drag.Boundary == point.Boundary && drag.Index == point.Index ? _dragPosition : new Point(point.X, point.Y);

    private Point TraceDisplayPosition(CalibrationTracePoint point)
        => _traceDrag is { } drag && drag.Index == point.Index ? _dragPosition : Position(point);

    private static Point Position(CalibrationTracePoint point) => new(point.XMm, point.YMm);

    private static double Distance(Point a, Point b)
    {
        Vector delta = a - b;
        return Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint current = e.GetCurrentPoint(this);
        Point position = current.Position;
        PointerUpdateKind kind = current.Properties.PointerUpdateKind;
        bool left = kind == PointerUpdateKind.LeftButtonPressed;
        if (kind is PointerUpdateKind.RightButtonPressed or PointerUpdateKind.MiddleButtonPressed || (left && _spaceDown))
        {
            BeginPan(position, e);
        }
        else if (left)
        {
            if (BeginDragAt(position))
            {
                e.Pointer.Capture(this);
            }
            else if (!AddTraceAt(position))
            {
                // Nothing to grab and nothing to add here: a left drag pans instead.
                BeginPan(position, e);
            }

            e.Handled = true;
        }
    }

    private void BeginPan(Point position, PointerPressedEventArgs e)
    {
        _panning = true;
        _panStart = position;
        _panBefore = _pan;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (_panning)
        {
            _pan = _panBefore + (position - _panStart);
            InvalidateVisual();
        }
        else if (Dragging)
        {
            DragTo(position);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        else if (Dragging)
        {
            CompleteDrag(commit: true);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panning = false;
        if (Dragging)
        {
            CompleteDrag(commit: false);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y != 0)
        {
            ZoomAt(Math.Pow(1.2, Math.Clamp(e.Delta.Y, -3, 3)), e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                RemoveSelectedTracePoint();
                e.Handled = true;
                break;
            case Key.Escape when Dragging:
                CompleteDrag(commit: false);
                e.Handled = true;
                break;
            case Key.Space:
                _spaceDown = true;
                e.Handled = true;
                break;
            case Key.Add or Key.OemPlus:
                ZoomBy(1.25);
                e.Handled = true;
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomBy(1 / 1.25);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                ResetView();
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _spaceDown = false;
    }
}
