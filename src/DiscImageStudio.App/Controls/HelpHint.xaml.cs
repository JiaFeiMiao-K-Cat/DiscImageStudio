using System.Windows;
using System.Windows.Automation;
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
        // Keyboard parity: the hint must be reachable without a mouse, so the badge takes
        // focus and opens the same tooltip the hover path uses.
        Focusable = true;
        IsTabStop = true;
        // Deferred lookup: the shared ring lives in the merged dictionaries, which are
        // not walkable from a constructor before the control enters the tree.
        SetResourceReference(FocusVisualStyleProperty, "FocusRing");
        _glyphBrush = (SolidColorBrush)HelpGlyph.Foreground;
        RefreshThemeColors();
        UpdateHint();
        Root.MouseEnter += (_, _) => SetHovered(true);
        Root.MouseLeave += (_, _) => SetHovered(false);
        GotKeyboardFocus += (_, _) =>
        {
            SetHovered(true);
            if (Root.ToolTip is ToolTip tip) tip.IsOpen = true;
        };
        LostKeyboardFocus += (_, _) =>
        {
            SetHovered(false);
            if (Root.ToolTip is ToolTip tip) tip.IsOpen = false;
        };
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
        if (animate && Motion.Enabled)
        {
            Motion.Animate(_glyphBrush, color, Motion.Fast);
            Motion.Fade(HelpCircle, opacity, Motion.Fast);
            return;
        }

        _glyphBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _glyphBrush.Color = color;
        HelpCircle.BeginAnimation(OpacityProperty, null);
        HelpCircle.Opacity = opacity;
    }

    private void UpdateHint()
    {
        if (string.IsNullOrEmpty(Hint))
        {
            Root.ToolTip = null;
            AutomationProperties.SetName(this, string.Empty);
            AutomationProperties.SetHelpText(this, string.Empty);
            return;
        }

        // One reusable tooltip instance: hovering opens it through the usual service, and
        // keyboard focus opens the very same instance programmatically.
        if (Root.ToolTip is not ToolTip tip)
        {
            tip = new ToolTip();
            Root.ToolTip = tip;
        }

        tip.Content = Hint;
        AutomationProperties.SetName(this, Hint);
        AutomationProperties.SetHelpText(this, Hint);
    }
}
