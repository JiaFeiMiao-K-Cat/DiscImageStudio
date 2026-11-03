using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DiscImageStudio.Linux.Services;
using DiscImageStudio.Linux.ViewModels;

namespace DiscImageStudio.Linux.Controls;

/// <summary>The design canvas: a 120 mm disc with the recordable ring, the layer stack drawn
/// in physical millimetres, and direct manipulation of the selected layer (drag to move,
/// corner handles to scale, the top handle to rotate; Shift snaps rotation to 15°). The
/// wheel zooms around the pointer, right or middle drag pans, double-click resets the view.</summary>
public sealed class DiscCanvas : Control
{
    public const double DiscRadiusMm = 60.0;
    public const double HoleRadiusMm = 7.5;
    private const double HandleSize = 9;
    private const double RotateHandleOffset = 28;

    public static readonly StyledProperty<IEnumerable?> LayersProperty =
        AvaloniaProperty.Register<DiscCanvas, IEnumerable?>(nameof(Layers));

    public static readonly StyledProperty<LayerViewModel?> SelectedLayerProperty =
        AvaloniaProperty.Register<DiscCanvas, LayerViewModel?>(nameof(SelectedLayer), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> InnerRadiusMmProperty =
        AvaloniaProperty.Register<DiscCanvas, double>(nameof(InnerRadiusMm), 24.5);

    public static readonly StyledProperty<double> OuterRadiusMmProperty =
        AvaloniaProperty.Register<DiscCanvas, double>(nameof(OuterRadiusMm), 56.8);

    public static readonly StyledProperty<IReadOnlyList<RadialBand>?> FileBandsProperty =
        AvaloniaProperty.Register<DiscCanvas, IReadOnlyList<RadialBand>?>(nameof(FileBands));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<DiscCanvas, double>(nameof(Zoom), 1.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            coerce: (_, value) => Math.Clamp(double.IsFinite(value) ? value : 1, 0.25, 8));

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private Vector _pan;
    private DragMode _drag;
    private Point _dragStart;
    private Point _lastPoint;
    private double _startScale;
    private double _startRotation;
    private double _startDistance;
    private double _startAngle;
    private INotifyCollectionChanged? _observedCollection;
    private readonly HashSet<LayerViewModel> _observedLayers = [];

    static DiscCanvas()
    {
        AffectsRender<DiscCanvas>(SelectedLayerProperty, InnerRadiusMmProperty, OuterRadiusMmProperty, FileBandsProperty, ZoomProperty);
        FocusableProperty.OverrideDefaultValue<DiscCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<DiscCanvas>(true);
    }

    public DiscCanvas()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    private enum DragMode
    {
        None,
        Move,
        Scale,
        Rotate,
        Pan,
    }

    public IEnumerable? Layers
    {
        get => GetValue(LayersProperty);
        set => SetValue(LayersProperty, value);
    }

    public LayerViewModel? SelectedLayer
    {
        get => GetValue(SelectedLayerProperty);
        set => SetValue(SelectedLayerProperty, value);
    }

    public double InnerRadiusMm
    {
        get => GetValue(InnerRadiusMmProperty);
        set => SetValue(InnerRadiusMmProperty, value);
    }

    public double OuterRadiusMm
    {
        get => GetValue(OuterRadiusMmProperty);
        set => SetValue(OuterRadiusMmProperty, value);
    }

    public IReadOnlyList<RadialBand>? FileBands
    {
        get => GetValue(FileBandsProperty);
        set => SetValue(FileBandsProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public void ResetView()
    {
        _pan = default;
        Zoom = 1;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayersProperty)
        {
            if (_observedCollection is not null)
            {
                _observedCollection.CollectionChanged -= Layers_CollectionChanged;
            }

            _observedCollection = change.NewValue as INotifyCollectionChanged;
            if (_observedCollection is not null)
            {
                _observedCollection.CollectionChanged += Layers_CollectionChanged;
            }

            ObserveLayers();
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (LayerViewModel layer in _observedLayers)
        {
            layer.PropertyChanged -= Layer_PropertyChanged;
        }

        _observedLayers.Clear();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveLayers();
    }

    private void Layers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ObserveLayers();
        InvalidateVisual();
    }

    private void ObserveLayers()
    {
        HashSet<LayerViewModel> current = [.. LayerList()];
        foreach (LayerViewModel removed in _observedLayers.Except(current).ToList())
        {
            removed.PropertyChanged -= Layer_PropertyChanged;
            _observedLayers.Remove(removed);
        }

        foreach (LayerViewModel added in current.Except(_observedLayers).ToList())
        {
            added.PropertyChanged += Layer_PropertyChanged;
            _observedLayers.Add(added);
        }
    }

    private void Layer_PropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private List<LayerViewModel> LayerList() => Layers?.OfType<LayerViewModel>().ToList() ?? [];

    private double PixelsPerMm => Math.Max(0.01, Math.Min(Bounds.Width, Bounds.Height) * 0.94 / (2 * DiscRadiusMm) * Zoom);

    private Point Center => new(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);

    private Point ToScreen(double xMm, double yMm) => new(Center.X + xMm * PixelsPerMm, Center.Y + yMm * PixelsPerMm);

    private Brush ResourceBrush(string key, Color fallback)
        => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is ISolidColorBrush brush
            ? new SolidColorBrush(brush.Color)
            : new SolidColorBrush(fallback);

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        Brush background = ResourceBrush("CanvasBrush", Color.Parse("#0B0B0D"));
        Brush primary = ResourceBrush("PrimaryBrush", Color.Parse("#3385FF"));
        bool dark = ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light;
        context.FillRectangle(background, bounds);

        double ppm = PixelsPerMm;
        Point center = Center;
        double discRadius = DiscRadiusMm * ppm;
        IBrush discFill = new SolidColorBrush(dark ? Color.Parse("#1C1C20") : Color.Parse("#FFFFFF"));
        context.DrawEllipse(discFill, new Pen(new SolidColorBrush(dark ? Color.Parse("#33FFFFFF") : Color.Parse("#26000000")), 1), center, discRadius, discRadius);

        List<LayerViewModel> layers = LayerList();
        for (int index = layers.Count - 1; index >= 0; index--)
        {
            LayerViewModel layer = layers[index];
            if (!layer.IsVisible)
            {
                continue;
            }

            Matrix transform = LayerTransform(layer, out double width, out double height);
            using (context.PushTransform(transform))
            {
                Rect target = new(0, 0, width, height);
                if (layer.DisplayBitmap is Bitmap bitmap)
                {
                    context.DrawImage(bitmap, new Rect(bitmap.Size), target);
                }
                else
                {
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 80, 80)),
                        new Pen(Brushes.IndianRed, 1, dashStyle: DashStyle.Dash), target);
                }
            }
        }

        // Dim everything that will not be recorded: outside the content ring and the hub.
        double inner = Math.Max(0, InnerRadiusMm) * ppm;
        double outer = Math.Max(0, OuterRadiusMm) * ppm;
        GeometryGroup dim = new() { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(bounds));
        dim.Children.Add(new EllipseGeometry(new Rect(center.X - outer, center.Y - outer, outer * 2, outer * 2)));
        dim.Children.Add(new EllipseGeometry(new Rect(center.X - inner, center.Y - inner, inner * 2, inner * 2)));
        context.DrawGeometry(new SolidColorBrush(dark ? Color.FromArgb(185, 11, 11, 13) : Color.FromArgb(185, 228, 228, 232)), null, dim);

        // Files band(s): where the filesystem goes instead of picture data.
        if (FileBands is { Count: > 0 } bands)
        {
            IBrush bandFill = new SolidColorBrush(Color.FromArgb(70, 250, 204, 21));
            foreach (RadialBand band in bands)
            {
                double a = band.InnerRadiusMm * ppm, b = band.OuterRadiusMm * ppm;
                GeometryGroup ring = new() { FillRule = FillRule.EvenOdd };
                ring.Children.Add(new EllipseGeometry(new Rect(center.X - b, center.Y - b, b * 2, b * 2)));
                ring.Children.Add(new EllipseGeometry(new Rect(center.X - a, center.Y - a, a * 2, a * 2)));
                context.DrawGeometry(bandFill, new Pen(new SolidColorBrush(Color.FromArgb(160, 250, 204, 21)), 1), ring);
            }
        }

        Pen guide = new(new SolidColorBrush(Color.FromArgb(150, 51, 133, 255)), 1, dashStyle: new DashStyle([4, 4], 0));
        context.DrawEllipse(null, guide, center, outer, outer);
        context.DrawEllipse(null, guide, center, inner, inner);
        double hole = HoleRadiusMm * ppm;
        context.DrawEllipse(background, new Pen(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), 1), center, hole, hole);

        if (layers.Count == 0)
        {
            FormattedText hint = new("Drop images here or use Import image",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default), 14, ResourceBrush("TextSecondaryBrush", Colors.Gray));
            context.DrawText(hint, new Point(center.X - hint.Width / 2, center.Y + hole + 18));
        }

        if (SelectedLayer is { } selected && layers.Contains(selected))
        {
            Point[] corners = Corners(selected);
            StreamGeometry outline = new();
            using (StreamGeometryContext geometry = outline.Open())
            {
                geometry.BeginFigure(corners[0], false);
                geometry.LineTo(corners[1]);
                geometry.LineTo(corners[2]);
                geometry.LineTo(corners[3]);
                geometry.EndFigure(true);
            }

            Pen selection = new(primary, 1.5);
            context.DrawGeometry(null, selection, outline);
            Point topMid = Mid(corners[0], corners[1]);
            Point rotate = RotateHandle(selected, corners);
            context.DrawLine(selection, topMid, rotate);
            context.DrawEllipse(Brushes.White, selection, rotate, 5.5, 5.5);
            foreach (Point corner in corners)
            {
                context.DrawRectangle(Brushes.White, selection,
                    new Rect(corner.X - HandleSize / 2, corner.Y - HandleSize / 2, HandleSize, HandleSize));
            }
        }
    }

    private Matrix LayerTransform(LayerViewModel layer, out double width, out double height)
    {
        double ppm = PixelsPerMm;
        width = layer.BaseWidthMm * layer.Scale * ppm;
        height = layer.BaseHeightMm * layer.Scale * ppm;
        Point position = ToScreen(layer.TranslateXMm, layer.TranslateYMm);
        return Matrix.CreateTranslation(-width / 2, -height / 2)
               * Matrix.CreateRotation(layer.RotationDeg * Math.PI / 180)
               * Matrix.CreateTranslation(position.X, position.Y);
    }

    /// <summary>Screen corners: top-left, top-right, bottom-right, bottom-left.</summary>
    private Point[] Corners(LayerViewModel layer)
    {
        Matrix transform = LayerTransform(layer, out double width, out double height);
        return
        [
            new Point(0, 0).Transform(transform),
            new Point(width, 0).Transform(transform),
            new Point(width, height).Transform(transform),
            new Point(0, height).Transform(transform),
        ];
    }

    private Point RotateHandle(LayerViewModel layer, Point[] corners)
    {
        Point topMid = Mid(corners[0], corners[1]);
        Point layerCenter = ToScreen(layer.TranslateXMm, layer.TranslateYMm);
        Vector up = topMid - layerCenter;
        double length = Math.Sqrt(up.X * up.X + up.Y * up.Y);
        if (length < 1e-6)
        {
            up = new Vector(0, -1);
            length = 1;
        }

        return topMid + up / length * RotateHandleOffset;
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private bool HitLayer(LayerViewModel layer, Point point)
    {
        Matrix transform = LayerTransform(layer, out double width, out double height);
        if (!transform.TryInvert(out Matrix inverse))
        {
            return false;
        }

        Point local = point.Transform(inverse);
        return local.X >= 0 && local.Y >= 0 && local.X <= width && local.Y <= height;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint pointer = e.GetCurrentPoint(this);
        Point point = pointer.Position;
        _dragStart = _lastPoint = point;
        if (pointer.Properties.IsRightButtonPressed || pointer.Properties.IsMiddleButtonPressed)
        {
            _drag = DragMode.Pan;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!pointer.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2 && FindLayerAt(point) is null)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        if (SelectedLayer is { } selected && LayerList().Contains(selected))
        {
            Point[] corners = Corners(selected);
            Point layerCenter = ToScreen(selected.TranslateXMm, selected.TranslateYMm);
            if (Distance(point, RotateHandle(selected, corners)) <= 9)
            {
                BeginDrag(DragMode.Rotate, selected, point, layerCenter, e);
                return;
            }

            if (corners.Any(corner => Distance(point, corner) <= HandleSize))
            {
                BeginDrag(DragMode.Scale, selected, point, layerCenter, e);
                return;
            }
        }

        LayerViewModel? hit = FindLayerAt(point);
        SelectedLayer = hit;
        if (hit is not null)
        {
            BeginDrag(DragMode.Move, hit, point, ToScreen(hit.TranslateXMm, hit.TranslateYMm), e);
        }
        else
        {
            // Dragging empty canvas pans, which is what most image editors do.
            _drag = DragMode.Pan;
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    private LayerViewModel? FindLayerAt(Point point)
        => LayerList().FirstOrDefault(layer => layer.IsVisible && HitLayer(layer, point));

    private void BeginDrag(DragMode mode, LayerViewModel layer, Point point, Point layerCenter, PointerPressedEventArgs e)
    {
        _drag = mode;
        _startScale = layer.Scale;
        _startRotation = layer.RotationDeg;
        _startDistance = Math.Max(1, Distance(point, layerCenter));
        _startAngle = Math.Atan2(point.Y - layerCenter.Y, point.X - layerCenter.X);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = e.GetPosition(this);
        if (_drag == DragMode.None)
        {
            UpdateCursor(point);
            return;
        }

        LayerViewModel? layer = SelectedLayer;
        switch (_drag)
        {
            case DragMode.Pan:
                _pan += point - _lastPoint;
                InvalidateVisual();
                break;
            case DragMode.Move when layer is not null:
                Vector delta = (point - _lastPoint) / PixelsPerMm;
                layer.TranslateXMm = Math.Round(layer.TranslateXMm + delta.X, 4);
                layer.TranslateYMm = Math.Round(layer.TranslateYMm + delta.Y, 4);
                break;
            case DragMode.Scale when layer is not null:
            {
                Point layerCenter = ToScreen(layer.TranslateXMm, layer.TranslateYMm);
                double factor = Distance(point, layerCenter) / _startDistance;
                layer.Scale = Math.Round(Math.Clamp(_startScale * factor, 0.02, 50), 4);
                break;
            }

            case DragMode.Rotate when layer is not null:
            {
                Point layerCenter = ToScreen(layer.TranslateXMm, layer.TranslateYMm);
                double angle = Math.Atan2(point.Y - layerCenter.Y, point.X - layerCenter.X);
                double degrees = _startRotation + (angle - _startAngle) * 180 / Math.PI;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    degrees = Math.Round(degrees / 15) * 15;
                }

                degrees = ((degrees % 360) + 540) % 360 - 180;
                layer.RotationDeg = Math.Round(degrees, 2);
                break;
            }
        }

        _lastPoint = point;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag != DragMode.None)
        {
            _drag = DragMode.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point point = e.GetPosition(this);
        double oldZoom = Zoom;
        double newZoom = Math.Clamp(oldZoom * Math.Pow(1.15, e.Delta.Y), 0.25, 8);
        if (Math.Abs(newZoom - oldZoom) < 1e-9)
        {
            return;
        }

        // Keep the point under the cursor fixed.
        Point center = Center;
        Vector fromCenter = point - center;
        _pan += fromCenter - fromCenter * (newZoom / oldZoom);
        Zoom = newZoom;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (SelectedLayer is not { } layer)
        {
            return;
        }

        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1.0 : 0.1;
        switch (e.Key)
        {
            case Key.Left:
                layer.TranslateXMm = Math.Round(layer.TranslateXMm - step, 4);
                break;
            case Key.Right:
                layer.TranslateXMm = Math.Round(layer.TranslateXMm + step, 4);
                break;
            case Key.Up:
                layer.TranslateYMm = Math.Round(layer.TranslateYMm - step, 4);
                break;
            case Key.Down:
                layer.TranslateYMm = Math.Round(layer.TranslateYMm + step, 4);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void UpdateCursor(Point point)
    {
        Cursor cursor = ArrowCursor;
        if (SelectedLayer is { } selected && LayerList().Contains(selected))
        {
            Point[] corners = Corners(selected);
            if (Distance(point, RotateHandle(selected, corners)) <= 9)
            {
                cursor = HandCursor;
            }
            else if (corners.Any(corner => Distance(point, corner) <= HandleSize) || HitLayer(selected, point))
            {
                cursor = MoveCursor;
            }
        }

        if (cursor == ArrowCursor && FindLayerAt(point) is not null)
        {
            cursor = HandCursor;
        }

        if (!ReferenceEquals(Cursor, cursor))
        {
            Cursor = cursor;
        }
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
