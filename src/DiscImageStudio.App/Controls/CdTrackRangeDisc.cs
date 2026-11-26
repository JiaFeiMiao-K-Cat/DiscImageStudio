using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiscImageStudio.Cd;
using DiscImageStudio.Localization;

namespace DiscImageStudio.Controls;

/// <summary>One music track as the preview disc draws it: its LBA range from the CD-Extra
/// plan and the radii those addresses fall on.</summary>
public sealed record CdTrackRange(int Number, string Title, string? Performer, TimeSpan Duration,
    long StartLba, long Sectors, double InnerRadiusMm, double OuterRadiusMm)
{
    internal static IReadOnlyList<CdTrackRange> FromLayout(CdExtraLayout layout,
        Func<int, (string Title, string? Performer, TimeSpan? Duration)> describe)
        => layout.Tracks.Select((track, index) =>
        {
            (string title, string? performer, TimeSpan? duration) = describe(index);
            return new CdTrackRange(track.Number, title, performer,
                duration ?? TimeSpan.FromSeconds(track.Sectors / 75.0), track.StartLba, track.Sectors,
                layout.RadiusAtLba(track.StartLba), layout.RadiusAtLba(track.EndLbaExclusive));
        }).ToArray();
}

/// <summary>
/// Top view of a CD-Extra disc showing where each music track is burned: alternating amber
/// rings per track (radii from the plan, not from a mock), the session gap and the picture
/// session. Hovering a ring shows its title, performer, duration, sectors and radii;
/// clicking selects it (<see cref="SelectedIndex"/> is two-way, so a list can drive and
/// follow it). Changing the plan morphs the rings instead of jumping.
/// </summary>
public sealed class CdTrackRangeDisc : FrameworkElement
{
    private const double DiscRadiusMm = 60;
    private const double HubRadiusMm = 7.5;
    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(320);
    private static readonly Color AmberA = Color.FromRgb(0xF5, 0xA6, 0x23);
    private static readonly Color AmberB = Color.FromRgb(0xC9, 0x7C, 0x0A);
    private static readonly Color GapColor = Color.FromRgb(0x7A, 0x7A, 0x85);
    private static readonly Typeface Face = new(new FontFamily("Segoe UI, Microsoft YaHei UI, Yu Gothic UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(Face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<CdTrackRange>), typeof(CdTrackRangeDisc),
        new FrameworkPropertyMetadata(null, (d, _) => ((CdTrackRangeDisc)d).StartMorph()));

    public static readonly DependencyProperty Session2InnerRadiusMmProperty = DependencyProperty.Register(
        nameof(Session2InnerRadiusMm), typeof(double), typeof(CdTrackRangeDisc),
        new FrameworkPropertyMetadata(0.0, (d, _) => ((CdTrackRangeDisc)d).StartMorph()));

    public static readonly DependencyProperty Session2OuterRadiusMmProperty = DependencyProperty.Register(
        nameof(Session2OuterRadiusMm), typeof(double), typeof(CdTrackRangeDisc),
        new FrameworkPropertyMetadata(0.0, (d, _) => ((CdTrackRangeDisc)d).StartMorph()));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(CdTrackRangeDisc),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((CdTrackRangeDisc)d).StartHighlight()));

    public static readonly DependencyProperty ModeTextProperty = DependencyProperty.Register(
        nameof(ModeText), typeof(string), typeof(CdTrackRangeDisc),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<CdTrackRange>? Tracks
    {
        get => (IReadOnlyList<CdTrackRange>?)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    /// <summary>Picture session radii; 0 hides it (for example before the plan is known).</summary>
    public double Session2InnerRadiusMm
    {
        get => (double)GetValue(Session2InnerRadiusMmProperty);
        set => SetValue(Session2InnerRadiusMmProperty, value);
    }

    public double Session2OuterRadiusMm
    {
        get => (double)GetValue(Session2OuterRadiusMmProperty);
        set => SetValue(Session2OuterRadiusMmProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>"按 SAO（含 CD-TEXT）" or "按 TAO": how the plan's addresses are written.</summary>
    public string ModeText
    {
        get => (string)GetValue(ModeTextProperty);
        set => SetValue(ModeTextProperty, value);
    }

    // Morph state: radii drawn = lerp(from, to, eased progress). Rings that did not exist
    // before grow from the previous outermost music radius.
    private (double Inner, double Outer)[] _from = [];
    private (double Inner, double Outer)[] _to = [];
    private (double Inner, double Outer) _session2From, _session2To;
    private TimeSpan _morphStart;
    private bool _morphing;
    private double _morph = 1;
    private double[] _highlight = [];
    private int _hover = -1;
    private Point _pointer;
    private TimeSpan _lastFrame;
    private bool _rendering;

    public CdTrackRangeDisc()
    {
        Focusable = true;
        ClipToBounds = false;
        SnapsToDevicePixels = true;
        Unloaded += (_, _) => StopRendering();
        Loaded += (_, _) => { if (_morphing || NeedsHighlightFrames()) StartRendering(); };
    }

    /// <summary>Index under the pointer, for tests and the hover card.</summary>
    internal int HoverIndex => _hover;

    internal bool IsAnimating => _rendering;

    /// <summary>Jumps every animation to its end (tests and reduced-motion).</summary>
    internal void Snap()
    {
        _morph = 1;
        _morphing = false;
        for (int i = 0; i < _highlight.Length; i++) _highlight[i] = i == SelectedIndex ? 1 : 0;
        StopRendering();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width;
        if (!double.IsInfinity(availableSize.Height)) side = Math.Min(side, availableSize.Height);
        return new Size(side, side + 22);
    }

    private void StartMorph()
    {
        (double, double)[] current = CurrentRings();
        IReadOnlyList<CdTrackRange> tracks = Tracks ?? [];
        double grow = current.Length > 0 ? current[^1].Item2 : HubRadiusMm + 15;
        _from = tracks.Select((_, i) => i < current.Length ? current[i] : (grow, grow)).ToArray();
        _to = tracks.Select(t => (t.InnerRadiusMm, t.OuterRadiusMm)).ToArray();
        _session2From = CurrentSession2();
        _session2To = (Session2InnerRadiusMm, Session2OuterRadiusMm);
        if (_session2From.Inner <= 0) _session2From = (_session2To.Inner, _session2To.Inner);
        Array.Resize(ref _highlight, tracks.Count);
        _morph = 0;
        _morphing = true;
        _morphStart = TimeSpan.MinValue;
        if (_hover >= tracks.Count) _hover = -1;
        if (SystemParameters.ClientAreaAnimation && IsLoaded) StartRendering();
        else Snap();
        InvalidateVisual();
    }

    private void StartHighlight()
    {
        if (SystemParameters.ClientAreaAnimation && IsLoaded) StartRendering();
        else Snap();
        InvalidateVisual();
    }

    private (double, double)[] CurrentRings()
    {
        double t = Ease(_morph);
        return _to.Select((to, i) => i < _from.Length
            ? (Lerp(_from[i].Inner, to.Inner, t), Lerp(_from[i].Outer, to.Outer, t)) : to).ToArray();
    }

    private (double Inner, double Outer) CurrentSession2()
    {
        double t = Ease(_morph);
        return (Lerp(_session2From.Inner, _session2To.Inner, t), Lerp(_session2From.Outer, _session2To.Outer, t));
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private bool NeedsHighlightFrames()
    {
        for (int i = 0; i < _highlight.Length; i++)
            if (Math.Abs(_highlight[i] - Target(i)) > 0.001) return true;
        return false;
    }

    private double Target(int index) => index == SelectedIndex ? 1 : index == _hover ? 0.45 : 0;

    private void OnFrame(object? sender, EventArgs e)
    {
        TimeSpan now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;
        if (_morphing)
        {
            if (_morphStart == TimeSpan.MinValue) _morphStart = now;
            _morph = Math.Clamp((now - _morphStart).TotalMilliseconds / MorphDuration.TotalMilliseconds, 0, 1);
            if (_morph >= 1) _morphing = false;
        }
        // Highlights approach their target exponentially: a quick hover sweep never
        // restarts a fade from zero, and reversing mid-way continues from where it is.
        double k = 1 - Math.Exp(-dt * 14);
        for (int i = 0; i < _highlight.Length; i++)
            _highlight[i] += (Target(i) - _highlight[i]) * k;
        if (!_morphing && !NeedsHighlightFrames()) StopRendering();
        InvalidateVisual();
    }

    private (Point Center, double Scale) Frame()
    {
        double side = Math.Min(ActualWidth, Math.Max(0, ActualHeight - 22));
        return (new Point(ActualWidth / 2, side / 2), side / 2 / DiscRadiusMm);
    }

    protected override void OnRender(DrawingContext dc)
    {
        (Point c, double scale) = Frame();
        if (scale <= 0) return;
        Brush surface = TryBrush("Surface2Brush", Color.FromRgb(0x2B, 0x2B, 0x2B));
        Brush divider = TryBrush("DividerBrush", Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        Brush text = TryBrush("TextPrimaryBrush", Colors.White);
        Brush secondary = TryBrush("TextSecondaryBrush", Colors.Gray);
        Color primary = TryColor("PrimaryColor", Color.FromRgb(0x00, 0x78, 0xD4));

        dc.DrawEllipse(surface, new Pen(divider, 1), c, DiscRadiusMm * scale, DiscRadiusMm * scale);

        (double Inner, double Outer)[] rings = CurrentRings();
        (double s2In, double s2Out) = CurrentSession2();
        if (rings.Length > 0 && s2In > 0)
            Annulus(dc, new SolidColorBrush(Color.FromArgb(0x70, GapColor.R, GapColor.G, GapColor.B)), c, scale, rings[^1].Outer, s2In);
        if (s2In > 0 && s2Out > s2In)
            Annulus(dc, new SolidColorBrush(Color.FromArgb(0x90, primary.R, primary.G, primary.B)), c, scale, s2In, s2Out);

        for (int i = 0; i < rings.Length; i++)
        {
            Color baseColor = i % 2 == 0 ? AmberA : AmberB;
            double h = i < _highlight.Length ? _highlight[i] : 0;
            Color fill = Mix(baseColor, Colors.White, 0.28 * h);
            Annulus(dc, new SolidColorBrush(fill), c, scale, rings[i].Inner, rings[i].Outer);
            if (h > 0.05)
            {
                Pen outline = new(new SolidColorBrush(Color.FromArgb((byte)(255 * h), 0xFF, 0xFF, 0xFF)), 1.5);
                dc.DrawEllipse(null, outline, c, rings[i].Inner * scale, rings[i].Inner * scale);
                dc.DrawEllipse(null, outline, c, rings[i].Outer * scale, rings[i].Outer * scale);
            }
        }

        dc.DrawEllipse(TryBrush("BackgroundBrush", Colors.Black), new Pen(divider, 1), c, HubRadiusMm * scale, HubRadiusMm * scale);
        if (rings.Length == 0)
            Text(dc, "—", c, 12, secondary, center: true);

        if (!string.IsNullOrEmpty(ModeText))
            Text(dc, ModeText, new Point(ActualWidth / 2, c.Y + DiscRadiusMm * scale + 4), 11, secondary, center: true, top: true);

        if (_hover >= 0 && Tracks is { } tracks && _hover < tracks.Count)
            HoverCard(dc, tracks[_hover], text, secondary);
    }

    private void HoverCard(DrawingContext dc, CdTrackRange track, Brush text, Brush secondary)
    {
        string title = $"{track.Number:00}  {track.Title}";
        string detail = (string.IsNullOrWhiteSpace(track.Performer) ? string.Empty : track.Performer + "\n")
            + L.F($"{FormatDuration(track.Duration)} · {track.Sectors:N0} 扇区") + "\n"
            + $"LBA {track.StartLba:N0}–{track.StartLba + track.Sectors - 1:N0}\n"
            + string.Format(CultureInfo.InvariantCulture, "{0:F1}–{1:F1} mm", track.InnerRadiusMm, track.OuterRadiusMm);
        FormattedText t1 = Format(title, 12, text, bold: true, maxWidth: 220);
        FormattedText t2 = Format(detail, 11, secondary, maxWidth: 220);
        double w = Math.Max(t1.Width, t2.Width) + 20, h = t1.Height + t2.Height + 18;
        double x = Math.Clamp(_pointer.X + 14, 0, Math.Max(0, ActualWidth - w));
        double y = Math.Clamp(_pointer.Y + 14, 0, Math.Max(0, ActualHeight - h));
        dc.DrawRoundedRectangle(TryBrush("Surface1Brush", Color.FromRgb(0x20, 0x20, 0x20)),
            new Pen(TryBrush("DividerBrush", Colors.Gray), 1), new Rect(x, y, w, h), 6, 6);
        dc.DrawText(t1, new Point(x + 10, y + 8));
        dc.DrawText(t2, new Point(x + 10, y + 10 + t1.Height));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer = e.GetPosition(this);
        int hit = HitTrack(_pointer);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : null;
            StartHighlight();
        }
        else if (hit >= 0) InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        StartHighlight();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        int hit = HitTrack(e.GetPosition(this));
        if (hit >= 0)
        {
            SelectedIndex = hit;
            Focus();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int count = Tracks?.Count ?? 0;
        if (count == 0) return;
        if (e.Key is Key.Right or Key.Down) { SelectedIndex = Math.Min(count - 1, SelectedIndex + 1); e.Handled = true; }
        else if (e.Key is Key.Left or Key.Up) { SelectedIndex = Math.Max(0, SelectedIndex - 1); e.Handled = true; }
    }

    /// <summary>The track whose ring contains <paramref name="point"/>, or −1.</summary>
    internal int HitTrack(Point point)
    {
        (Point c, double scale) = Frame();
        if (scale <= 0) return -1;
        double r = (point - c).Length / scale;
        (double Inner, double Outer)[] rings = CurrentRings();
        for (int i = 0; i < rings.Length; i++)
            if (r >= rings[i].Inner && r < rings[i].Outer) return i;
        return -1;
    }

    /// <summary>Center of track <paramref name="index"/>'s ring on the right-hand side.</summary>
    internal Point RingPoint(int index)
    {
        (Point c, double scale) = Frame();
        (double Inner, double Outer)[] rings = CurrentRings();
        double r = (rings[index].Inner + rings[index].Outer) / 2;
        return new Point(c.X + r * scale, c.Y);
    }

    private static void Annulus(DrawingContext dc, Brush brush, Point c, double scale, double inner, double outer)
    {
        if (outer <= inner) return;
        Geometry ring = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(c, outer * scale, outer * scale), new EllipseGeometry(c, inner * scale, inner * scale));
        dc.DrawGeometry(brush, null, ring);
    }

    private void Text(DrawingContext dc, string value, Point at, double size, Brush brush, bool center = false, bool top = false)
    {
        FormattedText text = Format(value, size, brush, maxWidth: Math.Max(1, ActualWidth));
        dc.DrawText(text, new Point(center ? at.X - text.Width / 2 : at.X, top ? at.Y : at.Y - text.Height / 2));
    }

    private FormattedText Format(string value, double size, Brush brush, bool bold = false, double maxWidth = 0)
    {
        FormattedText text = new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            bold ? BoldFace : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0) { text.MaxTextWidth = maxWidth; text.Trimming = TextTrimming.CharacterEllipsis; }
        return text;
    }

    private Brush TryBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private Color TryColor(string key, Color fallback) => TryFindResource(key) is Color color ? color : fallback;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
