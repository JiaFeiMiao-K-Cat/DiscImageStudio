using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Desktop;

/// <summary>Photo-space picking with zoom, pan and editable boundary/curve points.</summary>
public sealed class CalibrationCanvas : Control, IDisposable
{
    private Bitmap? _image;
    private double _zoom = 1;
    private Vector _pan;
    private Point? _panStart;
    private int _dragPoint = -1;
    public List<CalibrationPoint> Points { get; set; } = [];
    public IReadOnlyList<CalibrationPoint> Prediction { get; set; } = [];
    public event Action? Edited;
    private Rect ImageRect
    {
        get
        {
            if (_image is null) return new Rect();
            double scale = Math.Min(Bounds.Width / _image.PixelSize.Width, Bounds.Height / _image.PixelSize.Height) * _zoom;
            var size = new Size(_image.PixelSize.Width * scale, _image.PixelSize.Height * scale);
            return new Rect(new Point((Bounds.Width - size.Width) / 2 + _pan.X, (Bounds.Height - size.Height) / 2 + _pan.Y), size);
        }
    }
    public void SetImage(string? path)
    {
        _image?.Dispose(); _image = path is null ? null : new Bitmap(path); _zoom = 1; _pan = default; InvalidateVisual();
    }
    public void Dispose() => _image?.Dispose();
    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brush.Parse("#151923"), null, Bounds);
        if (_image is null) return;
        var rect = ImageRect;
        using (context.PushClip(Bounds))
        {
            context.DrawImage(_image, rect);
            Point Map(CalibrationPoint p) => new(rect.X + p.X / _image.PixelSize.Width * rect.Width, rect.Y + p.Y / _image.PixelSize.Height * rect.Height);
            var pen = new Pen(Brush.Parse("#FFB64D"), 2);
            for (int i = 1; i < Prediction.Count; i++) context.DrawLine(pen, Map(Prediction[i - 1]), Map(Prediction[i]));
            foreach (var p in Points) context.DrawEllipse(Brush.Parse("#42DEEA"), new Pen(Brushes.Black, 1), Map(p), 4, 4);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_image is null) return;
        var p = e.GetPosition(this); var rect = ImageRect;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) { _panStart = p; e.Pointer.Capture(this); return; }
        if (!rect.Contains(p)) return;
        var pixel = new CalibrationPoint((p.X - rect.X) / rect.Width * _image.PixelSize.Width, (p.Y - rect.Y) / rect.Height * _image.PixelSize.Height);
        double threshold = 9 / rect.Width * _image.PixelSize.Width;
        int index = Points.FindIndex(q => Math.Sqrt(Math.Pow(q.X - pixel.X, 2) + Math.Pow(q.Y - pixel.Y, 2)) < threshold);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { if (index >= 0) { Points.RemoveAt(index); Edited?.Invoke(); InvalidateVisual(); } return; }
        if (index < 0) { Points.Add(pixel); index = Points.Count - 1; Edited?.Invoke(); }
        _dragPoint = index; e.Pointer.Capture(this); InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panStart is { } start) { _pan += p - start; _panStart = p; InvalidateVisual(); return; }
        if (_image is null || _dragPoint < 0 || _dragPoint >= Points.Count) return;
        var rect = ImageRect;
        Points[_dragPoint] = new(Math.Clamp((p.X - rect.X) / rect.Width * _image.PixelSize.Width, 0, _image.PixelSize.Width),
            Math.Clamp((p.Y - rect.Y) / rect.Height * _image.PixelSize.Height, 0, _image.PixelSize.Height));
        Edited?.Invoke(); InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _panStart = null; _dragPoint = -1; e.Pointer.Capture(null); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { _zoom = Math.Clamp(_zoom * Math.Pow(1.15, e.Delta.Y), 0.5, 16); InvalidateVisual(); e.Handled = true; }
}
