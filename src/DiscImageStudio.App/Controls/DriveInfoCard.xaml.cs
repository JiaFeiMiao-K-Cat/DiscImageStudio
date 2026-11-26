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

    /// <summary>The clickable card; the details surface opens out of it and folds back in.</summary>
    internal FrameworkElement Anchor => CardHost;

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
        double opacity = DiscHost.Visibility == Visibility.Visible ? DiscHost.Opacity : 0;
        double offset = DiscRise.Y;
        DiscHost.BeginAnimation(OpacityProperty, null);
        DiscRise.BeginAnimation(TranslateTransform.YProperty, null);
        DiscHost.Opacity = opacity;
        DiscRise.Y = offset;
        if (!animated || !Motion.Enabled)
        {
            DiscHost.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
            DiscHost.Opacity = present ? 1 : 0;
            DiscRise.Y = present ? 0 : RiseDistance;
            return;
        }

        DiscHost.Visibility = Visibility.Visible;
        Motion.Animate(DiscRise, TranslateTransform.YProperty, present ? 0 : RiseDistance,
            present ? Motion.Slow : Motion.Exit, exiting: !present);
        Motion.Fade(DiscHost, present ? 1 : 0, present ? Motion.Base : Motion.Exit,
            exiting: !present, completed: () =>
            {
                if (!HasDisc) DiscHost.Visibility = Visibility.Collapsed;
            });
    }
}
