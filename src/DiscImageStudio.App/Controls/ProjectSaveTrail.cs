using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace DiscImageStudio.Controls;

/// <summary>Saved-project acknowledgement: one solid line segment runs once around the
/// project header's rounded rectangle. The head follows a cubic Bezier timing curve and the
/// tail length follows the same curve's instantaneous speed, so the segment stretches
/// through the fast middle of the lap and contracts as it eases into the start again. The
/// stroke keeps one alpha along its whole length: no fade-out tail, no halo, no glow and no
/// head dot. Presentation only: never hit-testable, and the caller may skip it when the
/// header is not on screen.</summary>
public sealed class ProjectSaveTrail : FrameworkElement
{
    private const double LapDurationMs = 1150;
    private const double FadeInFraction = 0.07;
    private const double FadeOutFraction = 0.18;
    private const double PathSampleStep = 1.5;

    // Cubic Bezier(0.55, 0, 0.2, 1): slow start, fast middle, long settle at the end. The
    // same curve drives progress and the derivative that scales the trail length.
    private const double EaseX1 = 0.55;
    private const double EaseY1 = 0;
    private const double EaseX2 = 0.2;
    private const double EaseY2 = 1;

    private static readonly double PeakSpeed = ComputeMaximumSpeed();
    private static readonly Brush DefaultStroke = CreateDefaultStroke();

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ProjectSaveTrail),
        new FrameworkPropertyMetadata(DefaultStroke, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(ProjectSaveTrail),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CoreThicknessProperty = DependencyProperty.Register(
        nameof(CoreThickness), typeof(double), typeof(ProjectSaveTrail),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrailLengthProperty = DependencyProperty.Register(
        nameof(TrailLength), typeof(double), typeof(ProjectSaveTrail),
        new FrameworkPropertyMetadata(62.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Stopwatch _clock = new();
    private readonly List<Point> _path = [];
    private readonly List<double> _distances = [];
    private bool _rendering;
    private bool _playPending;
    private bool _loop;
    private bool _hasSnapshot;
    private double _snapshotProgress;
    private double _totalLength;
    private Size _builtSize;
    private double _builtRadius = double.NaN;
    private Pen? _pen;
    private Color _penColor;
    private double _penThickness = double.NaN;

    public ProjectSaveTrail()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        SizeChanged += (_, _) => OnSizeChanged();
        Unloaded += (_, _) => Stop();
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double CoreThickness
    {
        get => (double)GetValue(CoreThicknessProperty);
        set => SetValue(CoreThicknessProperty, value);
    }

    /// <summary>Length of the segment at full speed; the curve scales it down as the head
    /// slows near the start and the end of the lap.</summary>
    public double TrailLength
    {
        get => (double)GetValue(TrailLengthProperty);
        set => SetValue(TrailLengthProperty, value);
    }

    /// <summary>Runs the lap once; a second call while running restarts it. With
    /// <paramref name="loop"/> the lap repeats seamlessly until <see cref="StopLoop"/>, which
    /// turns the same stroke into a waiting signal ("still working") instead of an
    /// acknowledgement ("done").</summary>
    public void Play(bool loop = false)
    {
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            _loop = loop;
            _playPending = true;
            return;
        }

        _playPending = false;
        _loop = loop;
        _hasSnapshot = false;
        EnsurePath();
        _clock.Restart();
        if (!_rendering)
        {
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }

        InvalidateVisual();
    }

    /// <summary>Ends a looping lap. The current lap keeps running and fades out on its own
    /// schedule, so ending a wait never blinks the stroke away mid-corner.</summary>
    public void StopLoop() => _loop = false;

    public void Stop()
    {
        _loop = false;
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }

        _clock.Stop();
    }

    /// <summary>Snapshot hook: paints one exact frame of the lap without running the clock,
    /// so the CLI can capture the trail deterministically.</summary>
    internal void ShowAt(double progress)
    {
        _playPending = false;
        Stop();
        _hasSnapshot = true;
        _snapshotProgress = Math.Clamp(progress, 0, 1);
        InvalidateVisual();
    }

    internal void ClearSnapshot()
    {
        _hasSnapshot = false;
        InvalidateVisual();
    }

    private void OnSizeChanged()
    {
        _builtSize = default;
        if (_playPending)
        {
            Play(_loop);
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double progress = _clock.Elapsed.TotalMilliseconds / LapDurationMs;
        if (progress >= 1)
        {
            if (!_loop)
            {
                Stop();
                InvalidateVisual();
                return;
            }

            // Restarting the clock continues the head across the seam; the tail is carried
            // over the wrap when drawing, so the stroke does not collapse here.
            _clock.Restart();
            progress = 0;
        }

        _snapshotProgress = progress;
        _hasSnapshot = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            return;
        }

        if (!_hasSnapshot)
        {
            return;
        }

        EnsurePath();
        if (_totalLength <= 0 || _path.Count < 2)
        {
            return;
        }

        DrawTrail(drawingContext, _snapshotProgress);
    }

    /// <summary>Visible pieces of the lap for one frame. A looping lap wraps the tail across
    /// the seam: the segment behind the head belongs to the end of the path while the head
    /// has not yet travelled its own length. Clamping the tail to zero there would drop that
    /// piece and make the stroke collapse and regrow once per lap.</summary>
    internal static (double Tail, double Head)[] TrailSegments(
        double progress, double totalLength, double baseTrailLength, bool loop)
    {
        double head = EaseProgress(progress) * totalLength;
        double tail = head - TrailLengthAt(progress, baseTrailLength);
        if (loop && tail < 0 && totalLength > 0)
        {
            return [(tail + totalLength, totalLength), (0, head)];
        }

        return [(Math.Max(0, tail), head)];
    }

    private void DrawTrail(DrawingContext drawingContext, double progress)
    {
        // A looping lap is a wait, not a confirmation: it must not fade in and out at the seam
        // where one lap hands over to the next.
        double fade = _loop ? 1.0 : Fade(progress);
        if (fade <= 0)
        {
            return;
        }

        EnsurePen();
        drawingContext.PushOpacity(fade);
        foreach ((double tail, double head) in TrailSegments(progress, _totalLength, TrailLength, _loop))
        {
            if (head - tail < 0.5)
            {
                continue;
            }

            drawingContext.DrawGeometry(null, _pen!, BuildTrailGeometry(tail, head));
        }

        drawingContext.Pop();
    }

    /// <summary>The segment follows the sampled path instead of joining tail and head with a
    /// straight chord, so it stays on the rounded rectangle while the head turns a corner.
    /// </summary>
    private StreamGeometry BuildTrailGeometry(double tail, double head)
    {
        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(PointAt(tail), isFilled: false, isClosed: false);

            int found = _distances.BinarySearch(tail);
            int first = found >= 0 ? found + 1 : ~found;
            for (int index = first; index < _distances.Count && _distances[index] < head; index++)
            {
                context.LineTo(_path[index], isStroked: true, isSmoothJoin: true);
            }

            context.LineTo(PointAt(head), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private void EnsurePen()
    {
        Color color = ResolveColor();
        double thickness = CoreThickness;
        if (_pen is not null && _penColor == color && Math.Abs(_penThickness - thickness) < 0.001)
        {
            return;
        }

        _penColor = color;
        _penThickness = thickness;
        SolidColorBrush brush = new(color);
        brush.Freeze();
        _pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        _pen.Freeze();
    }

    private static double Fade(double progress)
    {
        double rise = Math.Clamp(progress / FadeInFraction, 0, 1);
        double fall = Math.Clamp((1 - progress) / FadeOutFraction, 0, 1);
        return Math.Min(rise, fall);
    }

    private static double ComputeMaximumSpeed()
    {
        double max = 0;
        for (int step = 0; step <= 100; step++)
        {
            double p = step / 100.0;
            double speed = (CubicBezier(Math.Min(1, p + 0.01))
                - CubicBezier(Math.Max(0, p - 0.01))) / 0.02;
            max = Math.Max(max, speed);
        }

        return max;
    }

    private void EnsurePath()
    {
        double radius = Math.Clamp(CornerRadius, 0, Math.Min(RenderSize.Width, RenderSize.Height) / 2);
        if (_builtSize == RenderSize && Math.Abs(_builtRadius - radius) < 0.01 && _path.Count > 0)
        {
            return;
        }

        _builtSize = RenderSize;
        _builtRadius = radius;
        _path.Clear();
        _distances.Clear();

        double inset = 1.5;
        double left = inset;
        double top = inset;
        double right = RenderSize.Width - inset;
        double bottom = RenderSize.Height - inset;
        if (right <= left || bottom <= top)
        {
            _totalLength = 0;
            return;
        }

        double r = Math.Min(radius, Math.Min(right - left, bottom - top) / 2);
        AddLine(new Point(left + r, top), new Point(right - r, top));
        AddArc(new Point(right - r, top + r), r, -90, 0);
        AddLine(new Point(right, top + r), new Point(right, bottom - r));
        AddArc(new Point(right - r, bottom - r), r, 0, 90);
        AddLine(new Point(right - r, bottom), new Point(left + r, bottom));
        AddArc(new Point(left + r, bottom - r), r, 90, 180);
        AddLine(new Point(left, bottom - r), new Point(left, top + r));
        AddArc(new Point(left + r, top + r), r, 180, 270);

        // Close the final pixel back onto the first point so the lap is seamless.
        if (_path.Count > 0)
        {
            AddPoint(_path[0]);
        }

        _distances.Clear();
        double distance = 0;
        _distances.Add(0);
        for (int index = 1; index < _path.Count; index++)
        {
            distance += (PointAtRaw(index) - PointAtRaw(index - 1)).Length;
            _distances.Add(distance);
        }

        _totalLength = distance;
    }

    private void AddLine(Point from, Point to)
    {
        if (_path.Count == 0)
        {
            AddPoint(from);
        }

        double length = (to - from).Length;
        int steps = Math.Max(1, (int)Math.Ceiling(length / PathSampleStep));
        for (int step = 1; step <= steps; step++)
        {
            AddPoint(new Point(
                from.X + (to.X - from.X) * step / steps,
                from.Y + (to.Y - from.Y) * step / steps));
        }
    }

    private void AddArc(Point center, double radius, double startDegrees, double endDegrees)
    {
        if (radius <= 0)
        {
            return;
        }

        double sweep = endDegrees - startDegrees;
        double length = Math.Abs(sweep) * Math.PI / 180.0 * radius;
        int steps = Math.Max(1, (int)Math.Ceiling(length / PathSampleStep));
        for (int step = 1; step <= steps; step++)
        {
            double angle = (startDegrees + sweep * step / steps) * Math.PI / 180.0;
            AddPoint(new Point(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle)));
        }
    }

    private void AddPoint(Point point)
    {
        if (_path.Count > 0 && (_path[^1] - point).Length < 0.01)
        {
            return;
        }

        _path.Add(point);
    }

    private Point PointAt(double distance)
    {
        if (_distances.Count == 0)
        {
            return default;
        }

        if (distance <= 0)
        {
            return _path[0];
        }

        if (distance >= _totalLength)
        {
            return _path[^1];
        }

        int index = _distances.BinarySearch(distance);
        if (index < 0)
        {
            index = ~index;
        }

        if (index <= 0)
        {
            return _path[0];
        }

        if (index >= _distances.Count)
        {
            return _path[^1];
        }

        double previous = _distances[index - 1];
        double span = _distances[index] - previous;
        double fraction = span <= 0 ? 0 : (distance - previous) / span;
        Point from = _path[index - 1];
        Point to = _path[index];
        return new Point(
            from.X + (to.X - from.X) * fraction,
            from.Y + (to.Y - from.Y) * fraction);
    }

    private Point PointAtRaw(int index) => _path[index];

    private Color ResolveColor()
        => Stroke is SolidColorBrush solid ? solid.Color : Color.FromRgb(0x17, 0xC9, 0x64);

    private static Brush CreateDefaultStroke()
    {
        SolidColorBrush brush = new(Color.FromRgb(0x17, 0xC9, 0x64));
        brush.Freeze();
        return brush;
    }

    /// <summary>The head's position along the lap at a linear time fraction, 0..1.</summary>
    internal static double EaseProgress(double progress)
        => CubicBezier(Math.Clamp(progress, 0, 1));

    /// <summary>Segment length at a linear time fraction. The same Bezier's derivative is
    /// the head speed, so the segment contracts while the head eases in and out and
    /// stretches through the fast middle of the lap.</summary>
    internal static double TrailLengthAt(double progress, double baseTrailLength)
    {
        double p = Math.Clamp(progress, 0, 1);
        double speed = Math.Max(0, (CubicBezier(Math.Min(1, p + 0.01))
            - CubicBezier(Math.Max(0, p - 0.01))) / 0.02);
        double speedRatio = Math.Clamp(speed / PeakSpeed, 0, 1);
        return baseTrailLength * (0.35 + 0.65 * speedRatio);
    }

    private static double CubicBezier(double x)
    {
        double low = 0;
        double high = 1;
        double t = x;
        for (int iteration = 0; iteration < 18; iteration++)
        {
            double estimate = Bezier(t, EaseX1, EaseX2) - x;
            if (Math.Abs(estimate) < 1e-6)
            {
                break;
            }

            if (estimate < 0)
            {
                low = t;
            }
            else
            {
                high = t;
            }

            t = (low + high) / 2;
        }

        return Bezier(t, EaseY1, EaseY2);
    }

    private static double Bezier(double t, double control1, double control2)
    {
        double inverse = 1 - t;
        return 3 * inverse * inverse * t * control1
            + 3 * inverse * t * t * control2
            + t * t * t;
    }
}
