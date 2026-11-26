using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

/// <summary>Shared, interruptible motion. Final values live beneath short-lived clocks.</summary>
internal static class Motion
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(140));
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(220));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(280));
    public static readonly Duration Exit = new(TimeSpan.FromMilliseconds(160));

    public static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    public static IEasingFunction EaseOut { get; } = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static IEasingFunction EaseIn { get; } = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseIn });
    public static IEasingFunction EaseInOut { get; } = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static T Freeze<T>(T easing) where T : Freezable, IEasingFunction
    {
        easing.Freeze();
        return easing;
    }

    public static DoubleAnimation Create(double from, double to, Duration duration, bool exiting = false) => new(from, to, duration)
    {
        EasingFunction = exiting ? EaseIn : EaseOut,
        FillBehavior = FillBehavior.Stop,
    };

    public static void Animate(TranslateTransform target, DependencyProperty property, double value, Duration duration, bool exiting = false)
    {
        double from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        // An animation invalidates SetCurrentValue's temporary coercion. Store a real local
        // base value so FillBehavior.Stop reveals the destination, not the first frame.
        target.SetValue(property, value);
        if (Enabled && from != value)
            target.BeginAnimation(property, Create(from, value, duration, exiting), HandoffBehavior.SnapshotAndReplace);
    }

    public static void Animate(SolidColorBrush target, Color value, Duration duration)
    {
        Color from = target.Color;
        target.BeginAnimation(SolidColorBrush.ColorProperty, null);
        target.Color = value;
        if (Enabled && from != value)
            target.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, value, duration)
            {
                EasingFunction = EaseOut,
                FillBehavior = FillBehavior.Stop,
            }, HandoffBehavior.SnapshotAndReplace);
    }

    public static void Fade(UIElement target, double value, Duration duration, bool exiting = false, Action? completed = null)
    {
        double from = target.Opacity;
        target.BeginAnimation(UIElement.OpacityProperty, null);
        target.SetValue(UIElement.OpacityProperty, value);
        if (!Enabled || from == value)
        {
            completed?.Invoke();
            return;
        }
        DoubleAnimation animation = Create(from, value, duration, exiting);
        if (completed is not null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static void Enter(UIElement target, double travel = 6)
    {
        TranslateTransform shift = new(0, Enabled ? travel : 0);
        target.RenderTransform = shift;
        target.BeginAnimation(UIElement.OpacityProperty, null);
        target.SetValue(UIElement.OpacityProperty, Enabled ? 0d : 1d);
        Fade(target, 1, Base);
        Animate(shift, TranslateTransform.YProperty, 0, Slow);
    }
}
