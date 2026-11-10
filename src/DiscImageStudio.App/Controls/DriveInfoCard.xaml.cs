using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

public partial class DriveInfoCard : UserControl
{
    private const double RiseDistance = 38;
    private bool _isLoaded;

    public static readonly DependencyProperty HasDiscProperty = DependencyProperty.Register(
        nameof(HasDisc), typeof(bool), typeof(DriveInfoCard),
        new PropertyMetadata(false, static (sender, args) =>
            ((DriveInfoCard)sender).ApplyDiscState(
                (bool)args.NewValue, ((DriveInfoCard)sender)._isLoaded)));

    public DriveInfoCard()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            ApplyDiscState(HasDisc, animated: false);
        };
        Unloaded += (_, _) => _isLoaded = false;
    }

    public bool HasDisc
    {
        get => (bool)GetValue(HasDiscProperty);
        set => SetValue(HasDiscProperty, value);
    }

    /// <summary>Inserts rise from behind the card; removal sinks back the same distance.
    /// The initial state is applied without motion so a snapshot or a project opened with a
    /// loaded disc does not start mid-animation.</summary>
    private void ApplyDiscState(bool present, bool animated)
    {
        DiscHost.BeginAnimation(OpacityProperty, null);
        DiscRise.BeginAnimation(TranslateTransform.YProperty, null);
        if (!animated)
        {
            DiscHost.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
            DiscHost.Opacity = present ? 1 : 0;
            DiscRise.Y = present ? 0 : RiseDistance;
            return;
        }

        if (present)
        {
            DiscHost.Visibility = Visibility.Visible;
            DiscHost.Opacity = 0;
            DiscRise.Y = RiseDistance;
            DiscHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, Motion.Slow) { EasingFunction = Motion.EaseOut });
            DiscRise.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(RiseDistance, 0, Motion.Slow) { EasingFunction = Motion.EaseOut });
            return;
        }

        var fade = new DoubleAnimation(1, 0, Motion.Base) { EasingFunction = Motion.EaseOut };
        fade.Completed += (_, _) =>
        {
            if (!HasDisc)
            {
                DiscHost.Visibility = Visibility.Collapsed;
            }
        };
        DiscHost.BeginAnimation(OpacityProperty, fade);
        DiscRise.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, RiseDistance, Motion.Base)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
    }
}
