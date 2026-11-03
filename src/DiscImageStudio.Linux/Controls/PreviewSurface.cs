using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.Controls;

/// <summary>Shows an engine preview fitted to the control, with wheel zoom around the pointer,
/// drag to pan and double-click to fit. Zoomed past 1:1 it switches to nearest-neighbour so
/// individual samples stay visible. File bands from <see cref="PreviewInfo"/> are overlaid
/// in yellow, like the Windows previews.</summary>
public sealed class PreviewSurface : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<PreviewSurface, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<PreviewInfo?> InfoProperty =
        AvaloniaProperty.Register<PreviewSurface, PreviewInfo?>(nameof(Info));

    public static readonly StyledProperty<bool> InteractiveProperty =
        AvaloniaProperty.Register<PreviewSurface, bool>(nameof(Interactive), true);

    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragFrom;

    static PreviewSurface()
    {
        AffectsRender<PreviewSurface>(SourceProperty, InfoProperty);
        ClipToBoundsProperty.OverrideDefaultValue<PreviewSurface>(true);
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public PreviewInfo? Info
    {
        get => GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }

    public bool Interactive
    {
        get => GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
    }

    public void Fit()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    private Rect ImageRect()
    {
        if (Source is not { } bitmap)
        {
            return default;
        }

        double side = Math.Min(Bounds.Width, Bounds.Height) * _zoom;
        double aspect = bitmap.Size.Width / Math.Max(1, bitmap.Size.Height);
        double width = aspect >= 1 ? side : side * aspect;
        double height = aspect >= 1 ? side / aspect : side;
        return new Rect((Bounds.Width - width) / 2 + _pan.X, (Bounds.Height - height) / 2 + _pan.Y, width, height);
    }

    public override void Render(DrawingContext context)
    {
        if (Source is not { } bitmap)
        {
            return;
        }

        Rect target = ImageRect();
        double scale = target.Width / Math.Max(1, bitmap.Size.Width);
        RenderOptions options = new()
        {
            BitmapInterpolationMode = scale > 1.5 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality,
        };
        // The engines paint the hub and the corners; only the recorded ring is meaningful.
        Geometry? discRing = null;
        if (Info is { ImageRadiusMm: > 0, OuterRadiusMm: > 0 } disc)
        {
            double perMm = target.Width / 2 / disc.ImageRadiusMm;
            double outer = disc.OuterRadiusMm * perMm, inner = disc.InnerRadiusMm * perMm;
            Point c = target.Center;
            GeometryGroup annulus = new() { FillRule = FillRule.EvenOdd };
            annulus.Children.Add(new EllipseGeometry(new Rect(c.X - outer, c.Y - outer, outer * 2, outer * 2)));
            annulus.Children.Add(new EllipseGeometry(new Rect(c.X - inner, c.Y - inner, inner * 2, inner * 2)));
            discRing = annulus;
        }

        using (context.PushRenderOptions(options))
        using (discRing is null ? default : context.PushGeometryClip(discRing))
        {
            context.DrawImage(bitmap, new Rect(bitmap.Size), target);
        }

        if (discRing is not null)
        {
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), 1), discRing);
        }

        if (Info is { FileBands.Count: > 0, ImageRadiusMm: > 0 } info)
        {
            Point center = target.Center;
            double pixelsPerMm = target.Width / 2 / info.ImageRadiusMm;
            IBrush fill = new SolidColorBrush(Color.FromArgb(80, 250, 204, 21));
            Pen edge = new(new SolidColorBrush(Color.FromArgb(170, 250, 204, 21)), 1);
            foreach (RadialBand band in info.FileBands)
            {
                double a = band.InnerRadiusMm * pixelsPerMm, b = band.OuterRadiusMm * pixelsPerMm;
                GeometryGroup ring = new() { FillRule = FillRule.EvenOdd };
                ring.Children.Add(new EllipseGeometry(new Rect(center.X - b, center.Y - b, b * 2, b * 2)));
                ring.Children.Add(new EllipseGeometry(new Rect(center.X - a, center.Y - a, a * 2, a * 2)));
                context.DrawGeometry(fill, edge, ring);
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!Interactive || Source is null)
        {
            return;
        }

        Point point = e.GetPosition(this);
        double oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * Math.Pow(1.2, e.Delta.Y), 0.5, 24);
        Point center = new(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);
        Vector fromCenter = point - center;
        _pan += fromCenter - fromCenter * (_zoom / oldZoom);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Interactive)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            Fit();
            e.Handled = true;
            return;
        }

        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is { } from)
        {
            Point point = e.GetPosition(this);
            _pan += point - from;
            _dragFrom = point;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is not null)
        {
            _dragFrom = null;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty && change.OldValue is null)
        {
            Fit();
        }
    }
}
