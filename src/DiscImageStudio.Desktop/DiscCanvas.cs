using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DiscImageStudio.Core;
using SkiaSharp;

namespace DiscImageStudio.Desktop;

public sealed class DiscCanvas : Control, IDisposable
{
    private Bitmap? _bitmap;
    private readonly Dictionary<string, (Bitmap Image, int Width, int Height)> _layerImages = new(StringComparer.Ordinal);
    private Point? _dragStart;
    private ProjectLayer? _original;
    public bool EditLayers { get; init; }
    public ProjectSession? Session { get; set; }
    public int SelectedLayer { get; set; } = -1;
    public event Action<ProjectLayer>? LayerTransformed;
    public void SetImage(string? path)
    {
        _bitmap?.Dispose();
        _bitmap = path is null ? null : new Bitmap(path);
        InvalidateVisual();
    }
    public void RefreshLayerImages()
    {
        var paths = Session?.Document?.Layers.Where(layer => layer.Visible).Select(layer => layer.ImagePath)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (string path in _layerImages.Keys.Where(path => !paths.Contains(path)).ToArray())
        {
            _layerImages[path].Image.Dispose();
            _layerImages.Remove(path);
        }

        try
        {
            foreach (string path in paths)
            {
                if (_layerImages.ContainsKey(path)) continue;
                using var codec = SKCodec.Create(path) ?? throw new InvalidDataException($"无法读取图片：{path}");
                int width = codec.Info.Width, height = codec.Info.Height;
                using var stream = File.OpenRead(path);
                // Keep the editor's textures bounded. Preview and export read the original image.
                const int maxSide = 2048;
                Bitmap image = Math.Max(width, height) <= maxSide ? new Bitmap(stream)
                    : width >= height ? Bitmap.DecodeToWidth(stream, maxSide) : Bitmap.DecodeToHeight(stream, maxSide);
                _layerImages.Add(path, (image, width, height));
            }
        }
        finally { InvalidateVisual(); }
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        foreach (var layer in _layerImages.Values) layer.Image.Dispose();
        _layerImages.Clear();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        bool dark = Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        context.DrawRectangle(Brush.Parse(dark ? "#151C28" : "#F7F9FC"), null, new RoundedRect(Bounds, 10));
        var dotBrush = Brush.Parse(dark ? "#303D55" : "#DCE3EF");
        for (double y = 16; y < Bounds.Height; y += 24)
            for (double x = 16; x < Bounds.Width; x += 24) context.DrawEllipse(dotBrush, null, new Point(x, y), 0.65, 0.65);
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
            if (EditLayers && document is not null)
            {
                using var clip = context.PushClip(rect);
                // Layers are stored topmost first. Draw the bottom layer first, as in export.
                for (int i = document.Layers.Count - 1; i >= 0; i--)
                {
                    var layer = document.Layers[i];
                    if (!layer.Visible || !_layerImages.TryGetValue(layer.ImagePath, out var image)) continue;
                    var (width, height) = DesignComposer.LayerSize(document.Geometry.OuterRadiusMm, layer, image.Width, image.Height);
                    width *= scale;
                    height *= scale;
                    var transform = Matrix.CreateRotation(layer.RotationDeg * Math.PI / 180)
                        * Matrix.CreateTranslation(rect.Center.X + layer.TranslateXMm * scale, rect.Center.Y + layer.TranslateYMm * scale);
                    using var placement = context.PushTransform(transform);
                    context.DrawImage(image.Image, new Rect(-width / 2, -height / 2, width, height));
                }
            }
            else if (_bitmap is not null) context.DrawImage(_bitmap, rect);
        }
        context.DrawEllipse(null, new Pen(Brush.Parse("#BCC2CD"), 1), rect.Center, outer * scale, outer * scale);
        context.DrawEllipse(null, new Pen(Brush.Parse("#BCC2CD"), 1), rect.Center, inner * scale, inner * scale);
        context.DrawEllipse(Brush.Parse("#F4F5F8"), new Pen(Brush.Parse("#D6D9E0"), 1), rect.Center, 7.5 * scale, 7.5 * scale);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!EditLayers || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || Session?.Document is not { } document || SelectedLayer < 0 || SelectedLayer >= document.Layers.Count) return;
        _dragStart = e.GetPosition(this);
        _original = document.Layers[SelectedLayer];
        e.Pointer.Capture(this);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragStart is not { } start || _original is null || Session is null) return;
        var p = e.GetPosition(this);
        double mmPerPixel = Session.CanvasRadius * 2 / Math.Max(10, Math.Min(Bounds.Width, Bounds.Height) - 56);
        LayerTransformed?.Invoke(_original with { TranslateXMm = _original.TranslateXMm + (p.X - start.X) * mmPerPixel,
            TranslateYMm = _original.TranslateYMm + (p.Y - start.Y) * mmPerPixel });
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        _original = null;
        e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragStart = null;
        _original = null;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!EditLayers || e.Delta.Y == 0 || Session?.Document is not { } document
            || SelectedLayer < 0 || SelectedLayer >= document.Layers.Count) return;
        var layer = document.Layers[SelectedLayer];
        LayerTransformed?.Invoke(e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            ? layer with { RotationDeg = layer.RotationDeg + e.Delta.Y * 2 }
            : layer with { Scale = Math.Clamp(layer.Scale * Math.Pow(1.08, e.Delta.Y), 0.01, 100) });
        e.Handled = true;
    }
}
