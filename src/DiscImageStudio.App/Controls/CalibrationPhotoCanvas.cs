using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.Controls;

public enum CalibrationPhotoBoundaryKind { OuterEdge, CenterHole }
public sealed record CalibrationPhotoBoundaryPoint(CalibrationPhotoBoundaryKind Boundary, int Index, double X, double Y);
public sealed record CalibrationBoundaryEdit(CalibrationPhotoBoundaryKind Boundary, int Index, double X, double Y);
public sealed record CalibrationTracePoint(int Arm, int Index, double XMm, double YMm);
/// <summary>Index -1 appends a new point; existing indices identify a point in its arm.</summary>
public sealed record CalibrationTraceEdit(int Arm, int Index, double XMm, double YMm, bool Remove = false);

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
    public static readonly DependencyProperty SelectedTraceArmProperty = DependencyProperty.Register(nameof(SelectedTraceArm), typeof(int), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(0, Render | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty BoundaryEditedCommandProperty = DependencyProperty.Register(nameof(BoundaryEditedCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty TraceEditedCommandProperty = DependencyProperty.Register(nameof(TraceEditedCommand), typeof(ICommand), typeof(CalibrationPhotoCanvas));
    public static readonly DependencyProperty ShowPredictionProperty = DependencyProperty.Register(nameof(ShowPrediction), typeof(bool), typeof(CalibrationPhotoCanvas), new FrameworkPropertyMetadata(true, Render));
    private static readonly DependencyPropertyKey ViewDescriptionPropertyKey = DependencyProperty.RegisterReadOnly(nameof(ViewDescription), typeof(string), typeof(CalibrationPhotoCanvas), new PropertyMetadata("100%"));
    public static readonly DependencyProperty ViewDescriptionProperty = ViewDescriptionPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey SelectionDescriptionPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SelectionDescription), typeof(string), typeof(CalibrationPhotoCanvas), new PropertyMetadata("滚轮缩放，右键平移；拖动控制点贴合边缘。"));
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
    public ICommand? BoundaryEditedCommand { get => (ICommand?)GetValue(BoundaryEditedCommandProperty); set => SetValue(BoundaryEditedCommandProperty, value); }
    public ICommand? TraceEditedCommand { get => (ICommand?)GetValue(TraceEditedCommandProperty); set => SetValue(TraceEditedCommandProperty, value); }
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
    private Point _dragStart;
    private Point _dragOriginal;
    private Point _dragPosition;
    private bool _hasMoved;
    private (int Arm, int Index)? _selectedTrace;
    private (CalibrationPhotoBoundaryKind Boundary, int Index)? _selectedBoundary;
    private bool ShowingTarget => IsTargetPreview || Image is null;
    private bool ShowingRawPhoto => !ShowingTarget && IsPhotoStep && !IsRectificationPreview;
    private bool CanEditTrace => !ShowingTarget && !IsPhotoStep && !IsRectificationPreview;
    private bool Dragging => _boundaryDrag is not null || _traceDrag is not null;
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
            InvalidateVisual();
        };
        Unloaded += (_, _) => ThemeResolver.Changed -= ThemeResolver_Changed;
    }

    private void ThemeResolver_Changed(object? sender, EventArgs e) => InvalidateVisual();

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
        if (ShowingRawPhoto)
        {
            var point = BoundaryPoints?.OrderBy(p => (WorldToView(new Point(p.X, p.Y)) - screen).LengthSquared)
                .FirstOrDefault(p => (WorldToView(new Point(p.X, p.Y)) - screen).Length <= 11);
            if (point is null) return false;
            _boundaryDrag = point;
            _selectedBoundary = (point.Boundary, point.Index);
            _dragOriginal = new Point(point.X, point.Y);
            SetValue(SelectionDescriptionPropertyKey, point.Boundary == CalibrationPhotoBoundaryKind.OuterEdge
                ? "拖到盘片实体外缘；不要沿刻录区边界。" : "拖到实体内缘；不要沿透明塑料环。" );
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
        _dragPosition = _dragOriginal + (ViewToWorld(screen) - _dragStart);
        var bounds = ShowingRawPhoto && Image is { } photo
            ? new Rect(0, 0, photo.PixelWidth - 1, photo.PixelHeight - 1) : WorldBounds;
        _dragPosition = new Point(Math.Clamp(_dragPosition.X, bounds.Left, bounds.Right), Math.Clamp(_dragPosition.Y, bounds.Top, bounds.Bottom));
        _hasMoved |= (_dragPosition - _dragOriginal).Length > 1e-8;
        InvalidateVisual();
    }

    public void CompleteDrag(bool commit = true)
    {
        var boundary = _boundaryDrag;
        var trace = _traceDrag;
        var position = _dragPosition;
        bool changed = _hasMoved && commit;
        _boundaryDrag = null;
        _traceDrag = null;
        _hasMoved = false;
        if (changed && boundary is not null)
            Execute(BoundaryEditedCommand, new CalibrationBoundaryEdit(boundary.Boundary, boundary.Index, position.X, position.Y));
        if (changed && trace is not null)
        {
            Execute(TraceEditedCommand, new CalibrationTraceEdit(trace.Arm, trace.Index, position.X, position.Y));
            SelectTraceNear(trace.Arm, position);
        }
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    public bool AddTraceAt(Point screen)
    {
        if (!CanEditTrace || Dragging) return false;
        var point = ViewToWorld(screen);
        if (!WorldBounds.Contains(point) || SelectedTraceArm is < 0 or > 3) return false;
        var edit = new CalibrationTraceEdit(SelectedTraceArm, -1, point.X, point.Y);
        if (TraceEditedCommand?.CanExecute(edit) != true) return false;
        TraceEditedCommand.Execute(edit);
        SelectTraceNear(SelectedTraceArm, point);
        InvalidateVisual();
        return true;
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
        SetValue(SelectionDescriptionPropertyKey, "已删除此点；点击照片继续描当前曲线。" );
        InvalidateVisual();
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
        else if (CanEditTrace)
        {
            if (ShowPrediction && PredictionContours is { } predictions)
                for (int i = 0; i < predictions.Count; i++) DrawPath(dc, predictions[i], new Pen(DiscTheme.Prediction, 1.8), false);
            for (int arm = 0; arm < 4; arm++)
            {
                // The VM orders samples along the curve. Index is a stable editing identity,
                // so sorting by it would reconnect inserted samples in the wrong order.
                var points = TracePoints?.Where(p => p.Arm == arm).ToArray() ?? [];
                var brush = DiscTheme.Trace;
                var pen = new Pen(brush, 1.2) { DashStyle = DashStyles.Dot };
                DrawPath(dc, points.Select(TraceDisplayPosition).ToArray(), pen, false);
                foreach (var point in points)
                {
                    bool selected = _selectedTrace == (point.Arm, point.Index);
                    bool outlier = OutlierTracePointIndices?.Contains(point.Index) == true;
                    Brush pointBrush = outlier ? DiscTheme.Outlier : brush;
                    var screen = WorldToView(TraceDisplayPosition(point));
                    dc.DrawEllipse(pointBrush, new Pen(DiscTheme.Outline, 1.4), screen, selected ? 6 : 4, selected ? 6 : 4);
                    if (selected) DrawText(dc, $"点 {point.Index + 1}", screen + new Vector(9, -16), pointBrush, 12);
                }
            }
        }
        if (ShowingTarget)
            DrawCaption(dc, Image is null ? "标靶预览 · 导入照片后开始矫正" : "刻录图案预览 · 关闭预览后继续照片标定");
        else if (IsRectificationPreview) DrawCaption(dc, "矫正预览 · 确认后开始描线");
    }

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
        for (int i = 1; i <= 5; i++)
        {
            double viewRadius = (WorldToView(new Point(radius * i / 5, 0)) - center).Length;
            dc.DrawEllipse(null, pen, center, viewRadius, viewRadius);
        }
        DrawPath(dc, [new(-radius, 0), new(radius, 0)], pen, false);
        DrawPath(dc, [new(0, -radius), new(0, radius)], pen, false);
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
        _selectedTrace is { } point ? $"已选曲线上的点 {point.Index + 1} · 直接拖动修正，Delete 删除" : "点击照片上同一条可见曲线，由内向外连续描点。" );
    private static void OnViewportChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (CalibrationPhotoCanvas)d;
        canvas.CompleteDrag(false);
        canvas._selectedTrace = null;
        canvas._selectedBoundary = null;
        canvas.ResetView();
        canvas.SetValue(SelectionDescriptionPropertyKey, canvas.IsPhotoStep
            ? "滚轮缩放，右键平移；拖动控制点贴合边缘。"
            : "滚轮缩放，右键平移；点击添加点，拖动已有点修正位置。" );
    }
    private static void OnCollectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (CalibrationPhotoCanvas)d;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= canvas.PointsChanged;
        if (e.NewValue is INotifyCollectionChanged current) current.CollectionChanged += canvas.PointsChanged;
    }
    private void PointsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
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
            if (BeginDragAt(position)) CaptureMouse();
            else AddTraceAt(position);
            e.Handled = true;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (_panning) { _pan = _panBefore + (position - _panStart); InvalidateVisual(); }
        else if (Dragging) DragTo(position);
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
        if (e.Key == Key.Delete) { RemoveSelectedTracePoint(); e.Handled = true; }
        if (e.Key == Key.Escape) { CompleteDrag(false); e.Handled = true; }
    }
}
