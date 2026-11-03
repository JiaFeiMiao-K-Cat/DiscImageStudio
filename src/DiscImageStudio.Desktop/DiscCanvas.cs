using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DiscImageStudio.Core;

namespace DiscImageStudio.Desktop;

public sealed class DiscCanvas : Control, IDisposable
{
    private Bitmap? _bitmap;
    private Point? _dragStart;
    private ProjectLayer? _original;
    public ProjectSession? Session { get; set; }
    public int SelectedLayer { get; set; } = -1;
    public event Action<ProjectLayer>? LayerDragged;
    public event Action? LayerEditCompleted;
    public void SetImage(string? path)
    {
        _bitmap?.Dispose();
        _bitmap = path is null ? null : new Bitmap(path);
        InvalidateVisual();
    }
    public void Dispose() => _bitmap?.Dispose();
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double side = Math.Max(10, Math.Min(Bounds.Width, Bounds.Height) - 56);
        var rect = new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);
        context.DrawEllipse(Brushes.White, new Pen(Brush.Parse("#D6D9E0"), 1), rect.Center, side / 2, side / 2);
        var document = Session?.Document;
        double radius = Session?.CanvasRadius ?? 57.5;
        double inner = document?.Geometry.InnerRadiusMm ?? 24.5;
        double outer = document?.Geometry.OuterRadiusMm ?? 56.8;
        double scale = side / (radius * 2);
        var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Rect(rect.Center.X - outer * scale, rect.Center.Y - outer * scale, outer * scale * 2, outer * scale * 2)),
            new EllipseGeometry(new Rect(rect.Center.X - inner * scale, rect.Center.Y - inner * scale, inner * scale * 2, inner * scale * 2)));
        using (context.PushGeometryClip(ring))
        {
            context.DrawRectangle(Brush.Parse("#E1E3E9"), null, rect);
            if (_bitmap is not null) context.DrawImage(_bitmap, rect);
        }
        context.DrawEllipse(null, new Pen(Brush.Parse("#BCC2CD"), 1), rect.Center, outer * scale, outer * scale);
        context.DrawEllipse(null, new Pen(Brush.Parse("#BCC2CD"), 1), rect.Center, inner * scale, inner * scale);
        context.DrawEllipse(Brush.Parse("#F4F5F8"), new Pen(Brush.Parse("#D6D9E0"), 1), rect.Center, 7.5 * scale, 7.5 * scale);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Session?.Document is not { } document || SelectedLayer < 0 || SelectedLayer >= document.Layers.Count) return;
        _dragStart = e.GetPosition(this);
        _original = document.Layers[SelectedLayer];
        e.Pointer.Capture(this);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragStart is not { } start || _original is null || Session is null) return;
        var p = e.GetPosition(this);
        double mmPerPixel = Session.CanvasRadius * 2 / Math.Max(10, Math.Min(Bounds.Width, Bounds.Height) - 56);
        LayerDragged?.Invoke(_original with { TranslateXMm = _original.TranslateXMm + (p.X - start.X) * mmPerPixel,
            TranslateYMm = _original.TranslateYMm + (p.Y - start.Y) * mmPerPixel });
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragStart = null;
        _original = null;
        e.Pointer.Capture(null);
        LayerEditCompleted?.Invoke();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Session?.Document is not { } document || SelectedLayer < 0 || SelectedLayer >= document.Layers.Count) return;
        var layer = document.Layers[SelectedLayer];
        LayerDragged?.Invoke(e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            ? layer with { RotationDeg = layer.RotationDeg + e.Delta.Y * 2 }
            : layer with { Scale = Math.Clamp(layer.Scale * Math.Pow(1.08, e.Delta.Y), 0.01, 100) });
        LayerEditCompleted?.Invoke();
        e.Handled = true;
    }
}
