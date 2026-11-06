using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DiscImageStudio.Controls;

/// <summary>
/// One tile on the home page's recent-project grid, styled after the Adobe start page: a
/// mini sleeve carrying the project's identity, with the project's disc face sticking
/// halfway out of the sleeve's right edge. Hovering slides the sleeve left and the disc
/// right — a disc easing out of its sleeve — while the face slowly spins; on leave the
/// spin freezes at its current angle instead of snapping back to zero. A project path
/// wider than its column scrolls to its end and back for as long as the tile is hovered,
/// so the tail stays readable inside the sleeve.
/// </summary>
public class RecentProjectCard : Button
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(RecentProjectCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FamilyLabelProperty = DependencyProperty.Register(
        nameof(FamilyLabel), typeof(string), typeof(RecentProjectCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LocationTextProperty = DependencyProperty.Register(
        nameof(LocationText), typeof(string), typeof(RecentProjectCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ThumbnailSourceProperty = DependencyProperty.Register(
        nameof(ThumbnailSource), typeof(ImageSource), typeof(RecentProjectCard), new PropertyMetadata(null));

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(RecentProjectCard),
        new PropertyMetadata(false, static (sender, _) => ((RecentProjectCard)sender).ApplyLoadingState()));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string FamilyLabel
    {
        get => (string)GetValue(FamilyLabelProperty);
        set => SetValue(FamilyLabelProperty, value);
    }

    public string LocationText
    {
        get => (string)GetValue(LocationTextProperty);
        set => SetValue(LocationTextProperty, value);
    }

    public ImageSource? ThumbnailSource
    {
        get => (ImageSource?)GetValue(ThumbnailSourceProperty);
        set => SetValue(ThumbnailSourceProperty, value);
    }

    /// <summary>This project is being opened. The tile then runs the save trail in a loop: on
    /// the start screen there is no project header to carry it, and the tile the user just
    /// clicked is the only place the wait can be shown.</summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    private const string CardTranslatePartName = "PART_CardTranslate";
    private const string DiscTranslatePartName = "PART_DiscTranslate";
    private const string DiscRotatePartName = "PART_DiscRotate";
    private const string LocationViewportPartName = "PART_LocationViewport";
    private const string LocationTextPartName = "PART_LocationText";
    private const string LocationTranslatePartName = "PART_LocationTranslate";
    private const string LoadTrailPartName = "PART_LoadTrail";

    // A fast open must not flash a half-drawn arc, so the wait only shows once it is real.
    private static readonly TimeSpan LoadTrailDelay = TimeSpan.FromMilliseconds(120);

    // Hover offsets: the sleeve steps back 6px while the disc slides out 10px — a mini
    // echo of the full-size sleeve, kept inside the tile's own quarter column.
    private const double CardShiftPixels = -6;
    private const double DiscShiftPixels = 10;

    // 24 deg/s is a slow idle rotation, one turn in 15 seconds.
    private const double SpinDegreesPerSecond = 24;
    // Wall-clock guard: a >0.5 s rendering gap (window drag, machine sleep) must not jump the disc.
    private const double MaxSpinStepSeconds = 0.5;

    private TranslateTransform? _cardTranslate;
    private TranslateTransform? _discTranslate;
    private RotateTransform? _discRotate;
    private Grid? _locationViewport;
    private TextBlock? _locationText;
    private TranslateTransform? _locationTranslate;
    private ProjectSaveTrail? _loadTrail;
    private DispatcherTimer? _loadTrailDelay;
    private TimeSpan? _lastSpinTick;

    public RecentProjectCard()
    {
        Unloaded += static (sender, _) =>
        {
            RecentProjectCard card = (RecentProjectCard)sender;
            card.StopSpin();
            card.StopLocationMarquee();
            card.EndLoadTrail();
        };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _cardTranslate = GetTemplateChild(CardTranslatePartName) as TranslateTransform;
        _discTranslate = GetTemplateChild(DiscTranslatePartName) as TranslateTransform;
        _discRotate = GetTemplateChild(DiscRotatePartName) as RotateTransform;
        _locationViewport = GetTemplateChild(LocationViewportPartName) as Grid;
        _locationText = GetTemplateChild(LocationTextPartName) as TextBlock;
        _locationTranslate = GetTemplateChild(LocationTranslatePartName) as TranslateTransform;
        _loadTrail = GetTemplateChild(LoadTrailPartName) as ProjectSaveTrail;
        ApplyLoadingState();
    }

    private void ApplyLoadingState()
    {
        if (IsLoading)
        {
            BeginLoadTrail();
        }
        else
        {
            EndLoadTrail();
        }
    }

    private void BeginLoadTrail()
    {
        if (_loadTrail is null)
        {
            return;
        }

        _loadTrail.Visibility = Visibility.Visible;
        if (_loadTrailDelay is null)
        {
            _loadTrailDelay = new DispatcherTimer { Interval = LoadTrailDelay };
            _loadTrailDelay.Tick += OnLoadTrailDelayTick;
        }

        _loadTrailDelay.Stop();
        _loadTrailDelay.Start();
    }

    private void OnLoadTrailDelayTick(object? sender, EventArgs e)
    {
        _loadTrailDelay?.Stop();
        if (IsLoading)
        {
            _loadTrail?.Play(loop: true);
        }
    }

    /// <summary>Ends the wait mid-flight: the lap in progress finishes and fades on its own
    /// schedule, so a completed open never blinks the stroke away.</summary>
    private void EndLoadTrail()
    {
        _loadTrailDelay?.Stop();
        _loadTrail?.StopLoop();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!IsEnabled)
        {
            return;
        }

        SlideTo(_cardTranslate, CardShiftPixels, Motion.Slow);
        SlideTo(_discTranslate, DiscShiftPixels, Motion.Slow);
        _lastSpinTick = null;
        CompositionTarget.Rendering += OnRenderingTick;
        StartLocationMarquee();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        StopSpin();
        StopLocationMarquee();
        SlideTo(_cardTranslate, 0, Motion.Base);
        SlideTo(_discTranslate, 0, Motion.Base);
    }

    /// <summary>Scrolls an overflowing path to its end and back for as long as the tile is
    /// hovered. The template hosts the line in a horizontal StackPanel, which measures its
    /// child without a width constraint — that is what makes the whole path exist as one
    /// drawable line under the viewport's clip: a TextBlock measured at the viewport width
    /// formats only that slice, and there would be no tail to scroll to. A path that fits
    /// does not move at all.</summary>
    private void StartLocationMarquee()
    {
        if (_locationViewport is null || _locationText is null || _locationTranslate is null)
        {
            return;
        }

        double overflow = _locationText.DesiredSize.Width - _locationViewport.ActualWidth;
        if (!double.IsFinite(overflow) || overflow <= Marquee.OverflowTolerance)
        {
            return;
        }

        Marquee.Begin(_locationTranslate, overflow);
    }

    /// <summary>Dropping the animation restores the base offset, so the next hover starts at
    /// the path's beginning again.</summary>
    private void StopLocationMarquee() => Marquee.Stop(_locationTranslate);

    private void StopSpin()
    {
        CompositionTarget.Rendering -= OnRenderingTick;
        _lastSpinTick = null;
    }

    private void OnRenderingTick(object? sender, EventArgs e)
    {
        if (_discRotate is null || e is not RenderingEventArgs rendering)
        {
            return;
        }

        if (_lastSpinTick is { } previous)
        {
            double seconds = (rendering.RenderingTime - previous).TotalSeconds;
            if (seconds > 0 && seconds <= MaxSpinStepSeconds)
            {
                _discRotate.Angle = (_discRotate.Angle + SpinDegreesPerSecond * seconds) % 360;
            }
        }

        _lastSpinTick = rendering.RenderingTime;
    }

    private static void SlideTo(TranslateTransform? transform, double offset, Duration duration)
    {
        transform?.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(offset, duration) { EasingFunction = Motion.EaseOut });
    }
}
