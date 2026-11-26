using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

/// <summary>Reveals measured content without restarting on its own animated resize.</summary>
public class RevealPanel : ContentControl
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(RevealPanel),
        new PropertyMetadata(false, static (sender, _) => ((RevealPanel)sender).UpdateExpansion()));

    private bool? _lastExpanded;
    private bool _transitioning;
    private int _version;

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    static RevealPanel() => DefaultStyleKeyProperty.OverrideMetadata(
        typeof(RevealPanel), new FrameworkPropertyMetadata(typeof(RevealPanel)));

    public RevealPanel()
    {
        Height = 0;
        Opacity = 0;
        ClipToBounds = true;
        SizeChanged += (_, _) => { if (!_transitioning) UpdateExpansion(); };
        Loaded += (_, _) => UpdateExpansion();
    }

    private void UpdateExpansion()
    {
        bool expanded = IsExpanded;
        bool changed = _lastExpanded is { } previous && previous != expanded;
        if (_transitioning && !changed) return;
        double from = double.IsNaN(Height) ? ActualHeight : Height;
        double target = expanded ? MeasureContentHeight() : 0;
        bool animate = Motion.Enabled && IsLoaded && changed;
        _lastExpanded = expanded;
        int version = ++_version;
        _transitioning = true;
        BeginAnimation(HeightProperty, null);
        Height = target;
        if (!animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = expanded ? 1 : 0;
            Height = expanded ? double.NaN : 0;
            _transitioning = false;
            return;
        }

        Motion.Fade(this, expanded ? 1 : 0, Motion.Base, exiting: !expanded);
        DoubleAnimation height = Motion.Create(from, target, expanded ? Motion.Slow : Motion.Base);
        height.Completed += (_, _) =>
        {
            if (version != _version) return;
            // Return to normal layout once open; translated text and resized content can grow.
            BeginAnimation(HeightProperty, null);
            Height = IsExpanded ? double.NaN : 0;
            _transitioning = false;
        };
        BeginAnimation(HeightProperty, height, HandoffBehavior.SnapshotAndReplace);
    }

    private double MeasureContentHeight()
    {
        if (Content is not UIElement content) return 0;
        content.Measure(new Size(ActualWidth > 0 ? ActualWidth : double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(content.DesiredSize.Height, 1);
    }
}
