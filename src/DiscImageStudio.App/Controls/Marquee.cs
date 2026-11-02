using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

/// <summary>Shared timing for an overflowing single line that scrolls inside its viewport:
/// the recent-project path and long preset names use the same scan.</summary>
internal static class Marquee
{
    // 30 px/s reads as a scan rather than a flash; the bounds keep a barely overflowing line
    // from snapping to the end and a very long one from crawling. The dwell is a readable
    // beat at each end: without it the line starts travelling back the instant it reaches
    // the tail, so the tail is never actually legible.
    private const double PixelsPerSecond = 30;
    private const double MinimumSeconds = 1.8;
    private const double MaximumSeconds = 12;
    private const double DwellSeconds = 1.0;

    // Sub-pixel measurement noise must not start a marquee for text that visually fits.
    internal const double OverflowTolerance = 1;

    /// <summary>Two independent eased legs plus a dwell at each end. AutoReverse cannot do
    /// this: it reverses the eased value, so the return leg accelerates into the head and
    /// the next forward leg starts immediately, leaving the head unreadable.</summary>
    internal static void Begin(TranslateTransform target, double overflow)
    {
        double seconds = Math.Clamp(
            overflow / PixelsPerSecond, MinimumSeconds, MaximumSeconds);
        var leg = TimeSpan.FromSeconds(seconds);
        var hold = TimeSpan.FromSeconds(DwellSeconds);
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(leg + leg + hold + hold),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero), Motion.EaseOut));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(leg), Motion.EaseOut));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(leg + hold)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(leg + leg + hold), Motion.EaseOut));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(leg + leg + hold + hold)));
        target.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    /// <summary>Dropping the animation restores the target's base offset, so the next
    /// activation starts at the beginning again.</summary>
    internal static void Stop(TranslateTransform? target)
        => target?.BeginAnimation(TranslateTransform.XProperty, null);
}
