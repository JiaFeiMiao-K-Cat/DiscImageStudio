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
    private Point? _pendingPoint;
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
        _image?.Dispose(); _image = path is null ? null : new Bitmap(path); _zoom = 1; _pan = default;
        ClearGesture();
        InvalidateVisual();
    }
    public void Dispose() => _image?.Dispose();
    public override void Render(DrawingContext context)
    {
        var viewport = new Rect(Bounds.Size);
        context.DrawRectangle(Brush.Parse("#151923"), null, viewport);
        if (_image is null) return;
        var rect = ImageRect;
        using (context.PushClip(viewport))
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
        var current = e.GetCurrentPoint(this);
        var p = current.Position;
        var rect = ImageRect;
        if (current.Properties.IsRightButtonPressed || current.Properties.IsMiddleButtonPressed)
            _panStart = p;
        else if (current.Properties.IsLeftButtonPressed)
        {
            if (!rect.Contains(p)) _panStart = p;
            else
            {
                var pixel = ToPixel(p);
                double threshold = 9 / rect.Width * _image.PixelSize.Width;
                int index = Points.FindIndex(q => double.Hypot(q.X - pixel.X, q.Y - pixel.Y) < threshold);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                {
                    if (index >= 0) { Points.RemoveAt(index); Edited?.Invoke(); InvalidateVisual(); }
                    e.Handled = true;
                    return;
                }

                if (index >= 0) _dragPoint = index;
                else _pendingPoint = p;
            }
        }
        else return;

        e.Pointer.Capture(this);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_pendingPoint is { } pending && ((Vector)(p - pending)).Length >= 4)
        {
            _pendingPoint = null;
            _panStart = pending;
        }
        if (_panStart is { } start)
        {
            _pan += p - start;
            _panStart = p;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_image is null || _dragPoint < 0 || _dragPoint >= Points.Count) return;
        Points[_dragPoint] = ToPixel(p);
        Edited?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_panStart is null && _pendingPoint is null && _dragPoint < 0) return;
        var p = e.GetPosition(this);
        if (_pendingPoint is { } start && ((Vector)(p - start)).Length < 4 && _image is not null && ImageRect.Contains(p))
        {
            Points.Add(ToPixel(p));
            Edited?.Invoke();
            InvalidateVisual();
        }
        ClearGesture();
        e.Pointer.Capture(null);
        e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ClearGesture();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_image is null || e.Delta.Y == 0 || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        _zoom = Math.Clamp(_zoom * Math.Pow(1.15, e.Delta.Y), 0.5, 16);
        InvalidateVisual();
        e.Handled = true;
    }
    private CalibrationPoint ToPixel(Point p)
    {
        var rect = ImageRect;
        double width = _image!.PixelSize.Width, height = _image.PixelSize.Height;
        return new(Math.Clamp((p.X - rect.X) / rect.Width * width, 0, width),
            Math.Clamp((p.Y - rect.Y) / rect.Height * height, 0, height));
    }
    private void ClearGesture()
    {
        _panStart = null;
        _pendingPoint = null;
        _dragPoint = -1;
    }
}
