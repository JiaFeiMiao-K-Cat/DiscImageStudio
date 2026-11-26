using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

/// <summary>
/// A surface that opens out of a source element and closes back into it.
///
/// One number, <see cref="Progress"/>, drives everything: 0 is "folded into the source",
/// 1 is "at rest". Scale, offset, opacity and the backdrop are all derived from it on every
/// frame, so the surface is always somewhere on the single path between the two ends:
/// <list type="bullet">
/// <item>Reversing mid-flight continues from the displayed value (no restart, no jump).</item>
/// <item>The local base value is written to the destination before the clock starts and the
/// clock uses FillBehavior.Stop, so when it ends the destination is what remains — the last
/// frame can never snap back to the first.</item>
/// <item>Closing always returns into the source element that opened the surface, looked up
/// again at close time, so a resized or scrolled window still closes onto the button.</item>
/// <item>Without client-area animation or in high contrast the surface lands directly.</item>
/// </list>
/// </summary>
public class ConnectedSurface : ContentControl
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ConnectedSurface),
        new PropertyMetadata(0d, static (sender, _) => ((ConnectedSurface)sender).ApplyProgress()));

    public static readonly DependencyProperty BackdropProperty = DependencyProperty.Register(
        nameof(Backdrop), typeof(UIElement), typeof(ConnectedSurface),
        new PropertyMetadata(null, static (sender, _) => ((ConnectedSurface)sender).ApplyProgress()));

    /// <summary>Raised once a close has fully settled at zero (not on a reversed close).</summary>
    public event EventHandler? Folded;

    /// <summary>Raised once an open has fully settled at one.</summary>
    public event EventHandler? Unfolded;

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _shift = new();
    private Func<FrameworkElement?>? _source;
    private int _version;
    private bool _target;

    public ConnectedSurface()
    {
        TransformGroup group = new();
        group.Children.Add(_scale);
        group.Children.Add(_shift);
        RenderTransform = group;
        RenderTransformOrigin = new Point(0, 0);
        Visibility = Visibility.Collapsed;
        Opacity = 0;
        Focusable = false;
        SizeChanged += (_, _) => ApplyProgress();
    }

    /// <summary>Total open time; closing uses <see cref="CloseDuration"/>. Both are scaled by
    /// the remaining distance so an interrupted transition never slows down.</summary>
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan CloseDuration { get; set; } = TimeSpan.FromMilliseconds(220);

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Optional dimming layer faded together with the surface.</summary>
    public UIElement? Backdrop
    {
        get => (UIElement?)GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    /// <summary>The requested end state (true while opening or open).</summary>
    public bool IsOpen => _target;

    /// <summary>True from the first frame of an open until a close has settled.</summary>
    public bool IsShown => Visibility == Visibility.Visible;

    /// <summary>True while a clock is running towards either end.</summary>
    public bool IsTransitioning { get; private set; }

    /// <summary>The value currently on screen (animated value if a clock is running).</summary>
    public double DisplayedProgress => Progress;

    public void Open(Func<FrameworkElement?> source)
    {
        _source = source;
        Transition(true);
    }

    public void Close() => Transition(false);

    /// <summary>Lands at either end without a transition (snapshots, reduced motion).</summary>
    public void Snap(bool open, Func<FrameworkElement?>? source = null)
    {
        if (source is not null) _source = source;
        _target = open;
        int version = ++_version;
        BeginAnimation(ProgressProperty, null);
        SetValue(ProgressProperty, open ? 1d : 0d);
        IsTransitioning = false;
        IsHitTestVisible = false;
        if (open) Visibility = Visibility.Visible;
        Settle(version);
    }

    public void Toggle(Func<FrameworkElement?> source)
    {
        if (_target) Close();
        else Open(source);
    }

    private void Transition(bool open)
    {
        double from = Progress;
        double to = open ? 1 : 0;
        _target = open;
        int version = ++_version;
        BeginAnimation(ProgressProperty, null);
        SetValue(ProgressProperty, to);
        if (open)
        {
            Visibility = Visibility.Visible;
        }

        // While it travels the surface lies over its source (the gear, the drive card); it
        // only takes clicks once it rests open, so a second click on the source during the
        // opening reaches the source and reverses the transition instead of being swallowed.
        IsHitTestVisible = false;

        if (!Motion.Enabled || from == to || !IsLoaded && !open)
        {
            IsTransitioning = false;
            Settle(version);
            return;
        }

        TimeSpan full = open ? OpenDuration : CloseDuration;
        double distance = Math.Abs(to - from);
        TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(90, full.TotalMilliseconds * distance));
        DoubleAnimation clock = new(from, to, new Duration(duration))
        {
            EasingFunction = open
                ? Motion.EaseOut
                : Motion.EaseIn,
            FillBehavior = FillBehavior.Stop,
        };
        IsTransitioning = true;
        clock.Completed += (_, _) =>
        {
            if (version != _version) return;
            IsTransitioning = false;
            Settle(version);
        };
        BeginAnimation(ProgressProperty, clock, HandoffBehavior.SnapshotAndReplace);
    }

    private void Settle(int version)
    {
        if (version != _version) return;
        if (_target)
        {
            IsHitTestVisible = true;
            ApplyProgress();
            Unfolded?.Invoke(this, EventArgs.Empty);
            return;
        }

        Visibility = Visibility.Collapsed;
        ApplyProgress();
        Folded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Maps progress onto the source rectangle → final rectangle path.</summary>
    private void ApplyProgress()
    {
        double p = Math.Clamp(Progress, 0, 1);
        if (Backdrop is { } backdrop)
        {
            backdrop.Opacity = p;
            backdrop.Visibility = p > 0 || _target ? Visibility.Visible : Visibility.Collapsed;
            backdrop.IsHitTestVisible = _target;
        }

        // Content fades in over the first 60 % of the open and out over the last 60 % of
        // the close, so the surface reads as "growing out of" the source, not cross-fading.
        Opacity = Math.Clamp(p / 0.6, 0, 1);
        if (p >= 1 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _scale.ScaleX = _scale.ScaleY = 1;
            _shift.X = _shift.Y = 0;
            return;
        }

        Rect from = SourceRect();
        double sx = from.Width / ActualWidth;
        double sy = from.Height / ActualHeight;
        // Keep the aspect change gentle: a 32 px button would otherwise squash a page flat.
        double startScaleX = Math.Clamp(sx, 0.04, 1);
        double startScaleY = Math.Clamp(sy, 0.04, 1);
        double scaleX = Lerp(startScaleX, 1, p);
        double scaleY = Lerp(startScaleY, 1, p);
        _scale.ScaleX = scaleX;
        _scale.ScaleY = scaleY;
        _shift.X = Lerp(from.X, 0, p);
        _shift.Y = Lerp(from.Y, 0, p);
    }

    /// <summary>The source element's bounds in this surface's own coordinate space (where the
    /// surface's resting top-left is 0,0). Falls back to the surface centre when the source
    /// is gone, so a close still converges somewhere sensible.</summary>
    private Rect SourceRect()
    {
        FrameworkElement? source = _source?.Invoke();
        if (source is not null && source.IsVisible && PresentationSource.FromVisual(source) is not null
            && PresentationSource.FromVisual(this) is not null)
        {
            try
            {
                // Measure against the untransformed surface: undo our own transform first.
                GeneralTransform toSurface = source.TransformToVisual(this);
                Rect rect = toSurface.TransformBounds(new Rect(0, 0, source.ActualWidth, source.ActualHeight));
                Matrix own = RenderTransform.Value;
                if (own.HasInverse)
                {
                    // TransformToVisual(this) already maps into the transformed space; map the
                    // corners back through the inverse to get layout-space coordinates.
                    Point topLeft = own.Transform(rect.TopLeft);
                    Point bottomRight = own.Transform(rect.BottomRight);
                    rect = new Rect(topLeft, bottomRight);
                }
                return rect;
            }
            catch (InvalidOperationException)
            {
                // Not in a common visual tree any more; fall through.
            }
        }

        return new Rect(ActualWidth / 2 - 8, ActualHeight / 2 - 8, 16, 16);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
