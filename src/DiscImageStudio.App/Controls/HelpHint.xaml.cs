using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DiscImageStudio.Services;

namespace DiscImageStudio.Controls;

/// <summary>
/// Hover help hotspot: the "?" badge explains a non-obvious option without spending
/// permanent space on the explanation.
/// </summary>
public partial class HelpHint : UserControl
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(HelpHint),
        new PropertyMetadata(string.Empty, static (sender, _) => ((HelpHint)sender).UpdateHint()));

    private readonly SolidColorBrush _glyphBrush;
    private Color _activeColor;
    private Color _idleColor;
    private bool _hovered;

    public HelpHint()
    {
        InitializeComponent();
        _glyphBrush = (SolidColorBrush)HelpGlyph.Foreground;
        RefreshThemeColors();
        UpdateHint();
        Root.MouseEnter += (_, _) => SetHovered(true);
        Root.MouseLeave += (_, _) => SetHovered(false);
        Loaded += (_, _) =>
        {
            ThemeResolver.Changed -= ThemeResolver_Changed;
            ThemeResolver.Changed += ThemeResolver_Changed;
            RefreshThemeColors();
            ApplyVisual(animate: false);
        };
        Unloaded += (_, _) => ThemeResolver.Changed -= ThemeResolver_Changed;
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    private void ThemeResolver_Changed(object? sender, EventArgs e)
    {
        RefreshThemeColors();
        ApplyVisual(animate: false);
    }

    private void SetHovered(bool hovered)
    {
        _hovered = hovered;
        ApplyVisual(animate: true);
    }

    private void RefreshThemeColors()
    {
        _activeColor = ThemeValues.Color("TextPrimaryColor", Color.FromRgb(0xFA, 0xFA, 0xFA));
        _idleColor = ThemeValues.Color("TextMutedColor", Color.FromRgb(0x8C, 0x8C, 0x92));
    }

    private void ApplyVisual(bool animate)
    {
        Color color = _hovered ? _activeColor : _idleColor;
        double opacity = _hovered ? 1 : 0;
        if (animate)
        {
            _glyphBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(color, Motion.Fast) { EasingFunction = Motion.EaseOut });
            HelpCircle.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(opacity, Motion.Fast) { EasingFunction = Motion.EaseOut });
            return;
        }

        _glyphBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _glyphBrush.Color = color;
        HelpCircle.BeginAnimation(OpacityProperty, null);
        HelpCircle.Opacity = opacity;
    }

    private void UpdateHint()
        => Root.ToolTip = string.IsNullOrEmpty(Hint) ? null : Hint;
}
