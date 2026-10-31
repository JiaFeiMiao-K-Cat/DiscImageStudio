using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Controls;

/// <summary>Direct-manipulation design canvas: millimetre coordinates centred on the disc,
/// disc outline and content-ring guides, per-layer translate/rotate/scale rendering, a dim
/// mask outside the content ring, and a selection frame with corner scale handles and a
/// rotation handle. Selection state and layer data live in <see cref="DesignViewModel"/>;
/// the canvas only edits the selected layer's transform properties.</summary>
public class DiscDesignCanvas : FrameworkElement
{
    private const double ViewMarginMm = 3.0;
    private const double HandleSize = 8.0;
    private const double RotateHandleDistance = 26.0;
    private const double MinScale = 0.05;
    private const double MinZoom = 0.25;
    private const double MaxZoom = 12.0;
    private const double ZoomStep = 1.25;
    private const double WheelZoomStep = 1.15;

    private DesignViewModel? _design;
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _collectionHandler;
    private DragMode _dragMode;
    private Point _dragStartMm;
    private double _dragStartTranslateX;
    private double _dragStartTranslateY;
    private double _dragStartScale = 1.0;
    private double _dragStartDistanceMm = 1.0;
    private double _dragStartRotationDeg;
    private double _dragStartMouseAngleDeg;
    private int _scaleCornerIndex;
    private bool _isDragging;
    private bool _isPanning;
    private Vector _panOffset;
    private Vector _panStartOffset;
    private Point _panStartMouse;

    private enum DragMode
    {
        None,
        Move,
        Scale,
        Rotate,
    }

    public static readonly DependencyProperty ZoomFactorProperty = DependencyProperty.Register(
        nameof(ZoomFactor), typeof(double), typeof(DiscDesignCanvas),
        new PropertyMetadata(1.0, OnZoomFactorChanged, CoerceZoomFactor));
    private static readonly DependencyPropertyKey ZoomPercentPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ZoomPercent), typeof(string), typeof(DiscDesignCanvas), new PropertyMetadata("100%"));

    public DiscDesignCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Arrow;
        DataContextChanged += (_, _) => AttachDesign();
        Loaded += (_, _) =>
        {
            AttachDesign();
            // The palette is resolved inside OnRender, so a theme switch only needs a repaint.
            ThemeResolver.Changed -= ThemeResolver_Changed;
            ThemeResolver.Changed += ThemeResolver_Changed;
            InvalidateVisual();
        };
        Unloaded += (_, _) =>
        {
            ThemeResolver.Changed -= ThemeResolver_Changed;
            DetachDesign();
        };
    }

    private void ThemeResolver_Changed(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Zoom on top of the fit-to-view scale. 1 shows the whole disc; the +/- buttons
    /// and Ctrl/Alt+wheel change it, middle-drag pans, the refresh button or double-clicking
    /// empty backdrop resets it.</summary>
    public double ZoomFactor
    {
        get => (double)GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, value);
    }

    public string ZoomPercent => (string)GetValue(ZoomPercentPropertyKey.DependencyProperty);

    public void ResetView()
    {
        ZoomFactor = 1.0;
        _panOffset = default;
        InvalidateVisual();
    }

    public void ZoomIn() => ZoomAt(ZoomStep, ViewCenter);

    public void ZoomOut() => ZoomAt(1.0 / ZoomStep, ViewCenter);

    private static object CoerceZoomFactor(DependencyObject d, object baseValue)
        => Math.Clamp((double)baseValue, MinZoom, MaxZoom);

    private static void OnZoomFactorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        DiscDesignCanvas canvas = (DiscDesignCanvas)d;
        canvas.SetValue(ZoomPercentPropertyKey, $"{Math.Round(canvas.ZoomFactor * 100)}%");
        canvas.InvalidateVisual();
    }

    private DesignViewModel? Design => _design;

    // ---- Event wiring ----

    private void AttachDesign()
    {
        DetachDesign();
        if (DataContext is not DesignViewModel design)
        {
            return;
        }

        _design = design;
        design.CanvasChanged += InvalidateVisual;
        _collectionHandler = (_, args) =>
        {
            InvalidateVisual();
            if (args.OldItems is not null)
            {
                foreach (DesignLayerViewModel layer in args.OldItems)
                {
                    layer.PropertyChanged -= OnLayerPropertyChanged;
                }
            }

            if (args.NewItems is not null)
            {
                foreach (DesignLayerViewModel layer in args.NewItems)
                {
                    layer.PropertyChanged += OnLayerPropertyChanged;
                }
            }
        };
        design.Layers.CollectionChanged += _collectionHandler;
        foreach (DesignLayerViewModel layer in design.Layers)
        {
            layer.PropertyChanged += OnLayerPropertyChanged;
        }

        InvalidateVisual();
    }

    private void DetachDesign()
    {
        if (_design is not null)
        {
            _design.CanvasChanged -= InvalidateVisual;
            if (_collectionHandler is not null)
            {
                _design.Layers.CollectionChanged -= _collectionHandler;
            }

            foreach (DesignLayerViewModel layer in _design.Layers)
            {
                layer.PropertyChanged -= OnLayerPropertyChanged;
            }
        }

        _collectionHandler = null;
        _design = null;
    }

    private void OnLayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => InvalidateVisual();

    // ---- Coordinate mapping ----

    private double FitScale
    {
        get
        {
            double radiusMm = DesignViewModel.DiscRadiusMm + ViewMarginMm;
            return Math.Min(ActualWidth / (2 * radiusMm), ActualHeight / (2 * radiusMm));
        }
    }

    private double ViewScale => FitScale * ZoomFactor;

    private Point ViewCenterBase => new(ActualWidth / 2, ActualHeight / 2);

    private Point ViewCenter => new(
        ViewCenterBase.X + _panOffset.X,
        ViewCenterBase.Y + _panOffset.Y);

    private Point ScreenFromMm(double xMm, double yMm)
    {
        Point center = ViewCenter;
        double scale = ViewScale;
        return new Point(center.X + xMm * scale, center.Y + yMm * scale);
    }

    private Point MmFromScreen(Point point)
    {
        Point center = ViewCenter;
        double scale = ViewScale;
        return new Point((point.X - center.X) / scale, (point.Y - center.Y) / scale);
    }

    private double ScreenLength(double mm)
    {
        double scale = ViewScale;
        return mm * scale;
    }

    // ---- Geometry helpers (mm space) ----

    private static double SizeWidthMm(DesignLayerViewModel layer) => layer.BaseWidthMm * layer.Scale;

    private static double SizeHeightMm(DesignLayerViewModel layer) => layer.BaseHeightMm * layer.Scale;

    /// <summary>The layer's four corners in mm space, in the order NW, NE, SE, SW.</summary>
    private static Point[] LayerCorners(DesignLayerViewModel layer)
    {
        double halfWidth = SizeWidthMm(layer) / 2;
        double halfHeight = SizeHeightMm(layer) / 2;
        double radians = layer.RotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        (double dx, double dy)[] locals =
        [
            (-halfWidth, -halfHeight),
            (halfWidth, -halfHeight),
            (halfWidth, halfHeight),
            (-halfWidth, halfHeight),
        ];
        return locals
            .Select(local => new Point(
                layer.TranslateXMm + local.dx * cos - local.dy * sin,
                layer.TranslateYMm + local.dx * sin + local.dy * cos))
            .ToArray();
    }

    private static PathGeometry PolygonGeometry(Point[] points)
    {
        PathFigure figure = new() { StartPoint = points[0], IsClosed = true };
        for (int i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment(points[i], isStroked: true));
        }

        return new PathGeometry([figure]);
    }

    private static bool PointInPolygon(Point point, Point[] polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            bool crosses = (polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)
                && point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y)
                    + polygon[i].X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    // ---- Rendering ----

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(DiscTheme.Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_design is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double discRadius = ScreenLength(DesignViewModel.DiscRadiusMm);
        double hubRadius = ScreenLength(DesignViewModel.HubRadiusMm);
        double innerRadius = ScreenLength(_design.ContentInnerRadiusMm);
        double outerRadius = ScreenLength(Math.Min(
            _design.ContentOuterRadiusMm,
            DesignViewModel.DiscRadiusMm));
        Point center = ViewCenter;

        DiscRenderCore.DrawDiscBody(drawingContext, center, discRadius, hubRadius);
        DiscRenderCore.DrawGuideCircles(drawingContext, center, discRadius, outerRadius, innerRadius);
        DiscRenderCore.DrawOriginCross(drawingContext, center, 5);

        // Bottom-up: the layer list stores the panel order with index 0 on top, so painting
        // walks it backwards to match what the exporter composites and the hit test selects.
        for (int index = _design.Layers.Count - 1; index >= 0; index--)
        {
            DrawLayer(drawingContext, _design.Layers[index]);
        }

        DrawDimMask(drawingContext, center, outerRadius, innerRadius);

        if (_design.SelectedLayer is { } selected)
        {
            DrawSelection(drawingContext, selected);
        }

        if (!_design.HasLayers)
        {
            DrawEmptyHint(drawingContext, center);
        }
    }

    private void DrawLayer(DrawingContext context, DesignLayerViewModel layer)
    {
        double widthPx = ScreenLength(SizeWidthMm(layer));
        double heightPx = ScreenLength(SizeHeightMm(layer));
        if (widthPx <= 0 || heightPx <= 0)
        {
            return;
        }

        Point topLeft = ScreenFromMm(
            layer.TranslateXMm - SizeWidthMm(layer) / 2,
            layer.TranslateYMm - SizeHeightMm(layer) / 2);
        if (!layer.IsVisible)
        {
            // Hidden layers stay faintly visible in the editor so they can be re-selected.
            // The outline follows the rotated footprint: an axis-aligned rect would keep
            // sitting at the unrotated position once RotationDeg changes.
            Pen hiddenPen = new(DiscTheme.Guide, 1) { DashStyle = DashStyles.Dash };
            Point[] corners = LayerCorners(layer)
                .Select(corner => ScreenFromMm(corner.X, corner.Y))
                .ToArray();
            context.DrawGeometry(null, hiddenPen, PolygonGeometry(corners));
            return;
        }

        if (layer.HasImage)
        {
            // TransformGroup applies children in order: move the image onto the unrotated
            // rect first, then rotate around that rect's centre. Rotating first made the
            // centre land at C + topLeft - R(topLeft) instead of C, so the image drifted
            // away from the selection frame as soon as RotationDeg != 0.
            TransformGroup transform = new();
            transform.Children.Add(new TranslateTransform(topLeft.X, topLeft.Y));
            transform.Children.Add(new RotateTransform(
                layer.RotationDeg,
                topLeft.X + widthPx / 2,
                topLeft.Y + heightPx / 2));
            context.PushTransform(transform);
            context.DrawImage(layer.Image, new Rect(0, 0, widthPx, heightPx));
            context.Pop();
        }
        else
        {
            // The file went missing or cannot decode; keep the footprint findable, rotated
            // like the layer would have been.
            Pen placeholderPen = new(DiscTheme.MissingImage, 1) { DashStyle = DashStyles.Dash };
            Point[] corners = LayerCorners(layer)
                .Select(corner => ScreenFromMm(corner.X, corner.Y))
                .ToArray();
            context.DrawGeometry(null, placeholderPen, PolygonGeometry(corners));
        }
    }

    /// <summary>Everything outside the content ring is dimmed with one EvenOdd path: the
    /// canvas rectangle, the ring outer circle and the ring inner circle. The ring itself is
    /// crossed twice (bright); the hub and the area past r1 are crossed once or three times
    /// (dimmed).</summary>
    private void DrawDimMask(DrawingContext context, Point center, double outerRadius, double innerRadius)
    {
        PathGeometry geometry = new() { FillRule = FillRule.EvenOdd };
        geometry.Figures.Add(DiscRenderCore.RectangleFigure(new Rect(0, 0, ActualWidth, ActualHeight)));
        geometry.Figures.Add(DiscRenderCore.CircleFigure(center, outerRadius));
        geometry.Figures.Add(DiscRenderCore.CircleFigure(center, innerRadius));
        context.DrawGeometry(DiscTheme.Mask, null, geometry);
    }

    private void DrawSelection(DrawingContext context, DesignLayerViewModel layer)
    {
        Point[] cornersMm = LayerCorners(layer);
        Point[] corners = cornersMm
            .Select(corner => ScreenFromMm(corner.X, corner.Y))
            .ToArray();
        Pen outlinePen = new(DiscTheme.Selection, 1.5);
        context.DrawGeometry(null, outlinePen, PolygonGeometry(corners));

        // Corner scale handles.
        foreach (Point corner in corners)
        {
            Rect handle = new(
                corner.X - HandleSize / 2,
                corner.Y - HandleSize / 2,
                HandleSize,
                HandleSize);
            context.DrawRectangle(Brushes.White, new Pen(DiscTheme.Selection, 1.5), handle);
        }

        // Rotation handle above the (rotated) top edge midpoint.
        Point topMidMm = new(
            (cornersMm[0].X + cornersMm[1].X) / 2,
            (cornersMm[0].Y + cornersMm[1].Y) / 2);
        Point centerMm = new(layer.TranslateXMm, layer.TranslateYMm);
        double dx = topMidMm.X - centerMm.X;
        double dy = topMidMm.Y - centerMm.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0)
        {
            dx = 0;
            dy = -1;
            length = 1;
        }

        double extensionMm = RotateHandleDistance / Math.Max(0.001, ViewScale);
        Point handleMm = new(
            topMidMm.X + dx / length * extensionMm,
            topMidMm.Y + dy / length * extensionMm);
        Point handleTop = ScreenFromMm(topMidMm.X, topMidMm.Y);
        Point handlePoint = ScreenFromMm(handleMm.X, handleMm.Y);
        context.DrawLine(outlinePen, handleTop, handlePoint);
        context.DrawEllipse(
            Brushes.White,
            new Pen(DiscTheme.Selection, 1.5),
            handlePoint,
            HandleSize / 2,
            HandleSize / 2);
    }

    private void DrawEmptyHint(DrawingContext context, Point center)
    {
        FormattedText text = DiscRenderCore.CreateText(
            "点击左侧「导入图片」开始设计",
            14,
            DiscTheme.Guide,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    // ---- Hit testing ----

    private static bool Near(Point a, Point b, double tolerance) =>
        Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;

    private (int CornerIndex, Point ScreenPoint)? HitScaleHandle(Point mouse)
    {
        if (_design?.SelectedLayer is not { } layer)
        {
            return null;
        }

        Point[] cornersMm = LayerCorners(layer);
        for (int i = 0; i < cornersMm.Length; i++)
        {
            Point corner = ScreenFromMm(cornersMm[i].X, cornersMm[i].Y);
            if (Near(mouse, corner, HandleSize))
            {
                return (i, corner);
            }
        }

        return null;
    }

    private Point? RotateHandlePoint(DesignLayerViewModel layer)
    {
        Point[] cornersMm = LayerCorners(layer);
        Point centerMm = new(layer.TranslateXMm, layer.TranslateYMm);
        double dx = (cornersMm[0].X + cornersMm[1].X) / 2 - centerMm.X;
        double dy = (cornersMm[0].Y + cornersMm[1].Y) / 2 - centerMm.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0)
        {
            return null;
        }

        double extensionMm = RotateHandleDistance / Math.Max(0.001, ViewScale);
        Point handleMm = new(
            centerMm.X + (dx / length) * (length + extensionMm),
            centerMm.Y + (dy / length) * (length + extensionMm));
        return ScreenFromMm(handleMm.X, handleMm.Y);
    }

    private DesignLayerViewModel? HitTestLayer(Point mouseMm)
    {
        if (_design is null)
        {
            return null;
        }

        // Topmost first: the collection stores the panel order (index 0 on top). Visible
        // layers win over hidden ones so a click never grabs an invisible layer from under
        // a visible one; the dashed outlines of hidden layers stay clickable, because the
        // editor draws them precisely so they can be re-selected.
        DesignLayerViewModel? hidden = null;
        foreach (DesignLayerViewModel layer in _design.Layers)
        {
            if (!PointInPolygon(mouseMm, LayerCorners(layer)))
            {
                continue;
            }

            if (layer.IsVisible)
            {
                return layer;
            }

            hidden ??= layer;
        }

        return hidden;
    }

    // ---- Mouse interaction ----

    protected override void OnMouseWheel(MouseWheelEventArgs args)
    {
        base.OnMouseWheel(args);
        // Plain wheel keeps scrolling the page; Ctrl/Alt zoom with the cursor as the anchor
        // so the point under the mouse stays put while the fit scale grows beneath it.
        if (_design is null
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            return;
        }

        ZoomAt(args.Delta > 0 ? WheelZoomStep : 1.0 / WheelZoomStep, args.GetPosition(this));
        args.Handled = true;
    }

    /// <summary>Zooms by <paramref name="factor"/> while the mm point under
    /// <paramref name="anchor"/> stays on the same screen pixel. Below fit zoom the disc is
    /// centred again, because panning a shrunken disc away from the middle is never useful.</summary>
    private void ZoomAt(double factor, Point anchor)
    {
        double fit = FitScale;
        if (fit <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        Point anchorMm = MmFromScreen(anchor);
        ZoomFactor *= factor;
        _panOffset = ZoomFactor > 1.0001
            ? ClampPan((Vector)anchor - (Vector)ViewCenterBase - (Vector)anchorMm * (fit * ZoomFactor))
            : default;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.ChangedButton != MouseButton.Middle || _design is null || ZoomFactor <= 1.0001)
        {
            return;
        }

        _isPanning = true;
        _panStartOffset = _panOffset;
        _panStartMouse = args.GetPosition(this);
        Cursor = Cursors.SizeAll;
        CaptureMouse();
        args.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.ChangedButton != MouseButton.Middle || !_isPanning)
        {
            return;
        }

        _isPanning = false;
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
        args.Handled = true;
    }

    /// <summary>The disc's centre must stay within the disc's on-screen radius, so a pan can
    /// never lose the artwork entirely; at fit zoom the pan is always zero.</summary>
    private Vector ClampPan(Vector pan)
    {
        double fit = FitScale;
        if (fit <= 0 || ZoomFactor <= 1.0001)
        {
            return default;
        }

        double maxLength = fit * ZoomFactor * DesignViewModel.DiscRadiusMm;
        return pan.Length > maxLength ? pan * (maxLength / pan.Length) : pan;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        if (_design is null)
        {
            return;
        }

        Focus();
        Point mouse = args.GetPosition(this);
        Point mouseMm = MmFromScreen(mouse);

        // Double-clicking empty backdrop is a shortcut for the fit button.
        if (args.ClickCount == 2
            && _design.SelectedLayer is null
            && !HitScaleHandle(mouse).HasValue
            && HitTestLayer(mouseMm) is null)
        {
            ResetView();
            return;
        }

        // Handles first: they only exist while something is selected.
        if (HitScaleHandle(mouse) is { } scaleHit)
        {
            DesignLayerViewModel scaleLayer = _design.SelectedLayer!;
            _dragMode = DragMode.Scale;
            _scaleCornerIndex = scaleHit.CornerIndex;
            _dragStartMm = mouseMm;
            _dragStartScale = scaleLayer.Scale;
            double dx = mouseMm.X - scaleLayer.TranslateXMm;
            double dy = mouseMm.Y - scaleLayer.TranslateYMm;
            _dragStartDistanceMm = Math.Max(0.001, Math.Sqrt(dx * dx + dy * dy));
            _isDragging = true;
            CaptureMouse();
            return;
        }

        if (_design.SelectedLayer is { } selected && RotateHandlePoint(selected) is { } rotatePoint
            && Near(mouse, rotatePoint, HandleSize + 2))
        {
            _dragMode = DragMode.Rotate;
            _dragStartRotationDeg = selected.RotationDeg;
            _dragStartMouseAngleDeg = AngleFromCenter(selected, mouseMm);
            _isDragging = true;
            CaptureMouse();
            return;
        }

        if (HitTestLayer(mouseMm) is { } hit)
        {
            _design.SelectedLayer = hit;
            _dragMode = DragMode.Move;
            _dragStartMm = mouseMm;
            _dragStartTranslateX = hit.TranslateXMm;
            _dragStartTranslateY = hit.TranslateYMm;
            _isDragging = true;
            CaptureMouse();
            return;
        }

        _design.SelectedLayer = null;
    }

    private static double AngleFromCenter(DesignLayerViewModel layer, Point mouseMm)
    {
        double dx = mouseMm.X - layer.TranslateXMm;
        double dy = mouseMm.Y - layer.TranslateYMm;
        return Math.Atan2(dy, dx) * 180.0 / Math.PI;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (_isPanning)
        {
            _panOffset = ClampPan(
                _panStartOffset + (Vector)(args.GetPosition(this) - _panStartMouse));
            InvalidateVisual();
            return;
        }

        if (!_isDragging || _design?.SelectedLayer is not { } layer)
        {
            UpdateHoverCursor(args);
            return;
        }

        Point mouseMm = MmFromScreen(args.GetPosition(this));
        switch (_dragMode)
        {
            case DragMode.Move:
                layer.TranslateXMm = _dragStartTranslateX + (mouseMm.X - _dragStartMm.X);
                layer.TranslateYMm = _dragStartTranslateY + (mouseMm.Y - _dragStartMm.Y);
                break;
            case DragMode.Scale:
            {
                double dx = mouseMm.X - layer.TranslateXMm;
                double dy = mouseMm.Y - layer.TranslateYMm;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                layer.Scale = Math.Max(MinScale, _dragStartScale * distance / _dragStartDistanceMm);
                break;
            }
            case DragMode.Rotate:
            {
                double delta = AngleFromCenter(layer, mouseMm) - _dragStartMouseAngleDeg;
                // Wrap the delta into (-180, 180] so crossing the ±180° seam does not spin.
                while (delta <= -180)
                {
                    delta += 360;
                }

                while (delta > 180)
                {
                    delta -= 360;
                }

                double angle = _dragStartRotationDeg + delta;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    // Snap to the nearest right angle; normalize 360 back to 0 so the
                    // property panel never shows a redundant full turn.
                    angle = Math.Round(angle / 90.0) * 90.0;
                    if (angle == 360)
                    {
                        angle = 0;
                    }
                }

                layer.RotationDeg = Math.Round(angle, 1);
                break;
            }
        }
    }

    private void UpdateHoverCursor(MouseEventArgs args)
    {
        if (_design is null)
        {
            return;
        }

        Point mouse = args.GetPosition(this);
        if (HitScaleHandle(mouse).HasValue
            || (_design.SelectedLayer is { } selected
                && RotateHandlePoint(selected) is { } rotate
                && Near(mouse, rotate, HandleSize + 2)))
        {
            Cursor = Cursors.SizeAll;
        }
        else
        {
            Cursor = Cursors.Arrow;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonUp(args);
        if (_isDragging)
        {
            _isDragging = false;
            _dragMode = DragMode.None;
            ReleaseMouseCapture();
        }
    }
}
