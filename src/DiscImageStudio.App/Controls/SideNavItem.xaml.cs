using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DiscImageStudio.Services;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

public partial class SideNavItem : UserControl
{
    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind), typeof(PackIconLucideKind?), typeof(SideNavItem),
        new PropertyMetadata(null, static (sender, _) => ((SideNavItem)sender).UpdateVisual()));

    public static readonly DependencyProperty LabelTextProperty = DependencyProperty.Register(
        nameof(LabelText), typeof(string), typeof(SideNavItem), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(SideNavItem),
        new PropertyMetadata(false, static (sender, _) => ((SideNavItem)sender).UpdateVisual()));

    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
        nameof(IsCollapsed), typeof(bool), typeof(SideNavItem),
        new PropertyMetadata(false, static (sender, _) => ((SideNavItem)sender).UpdateVisual()));

    public static readonly DependencyProperty IndentProperty = DependencyProperty.Register(
        nameof(Indent), typeof(double), typeof(SideNavItem),
        new PropertyMetadata(0.0, static (sender, _) => ((SideNavItem)sender).UpdateVisual(animate: false)));

    public static readonly DependencyProperty LabelFontSizeProperty = DependencyProperty.Register(
        nameof(LabelFontSize), typeof(double), typeof(SideNavItem),
        new PropertyMetadata(16.0, static (sender, args) => ((SideNavItem)sender).LabelTextBlock.FontSize = (double)args.NewValue));

    public static readonly RoutedEvent ActivatedEvent = EventManager.RegisterRoutedEvent(
        nameof(Activated), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SideNavItem));

    private static readonly Duration TransitionDuration = Motion.Fast;
    private static readonly IEasingFunction TransitionEase = Motion.EaseOut;

    // Colors come from the theme tokens (TextPrimary/TextSecondary/Surface2/SurfaceHover) so
    // the sidebar cannot drift from the shared palette; the idle background keeps the hover
    // color's RGB at alpha 0 because a ColorAnimation interpolates every ARGB channel and
    // transparent->dark would pass ~50% white mid-flight (see Motion/ThemeValues).
    private Color _activeTextColor;
    private Color _idleTextColor;
    private Color _activeBackgroundColor;
    private Color _hoverBackgroundColor;
    private Color _idleBackgroundColor;

    private readonly SolidColorBrush _textBrush;
    private readonly SolidColorBrush _backgroundBrush;
    private Color? _textBrushTarget;
    private Color? _backgroundBrushTarget;

    public PackIconLucideKind? IconKind
    {
        get => (PackIconLucideKind?)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public string LabelText
    {
        get => (string)GetValue(LabelTextProperty);
        set => SetValue(LabelTextProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    /// Extra left inset for nested entries; the label margin becomes 12 + Indent.
    public double Indent
    {
        get => (double)GetValue(IndentProperty);
        set => SetValue(IndentProperty, value);
    }

    public double LabelFontSize
    {
        get => (double)GetValue(LabelFontSizeProperty);
        set => SetValue(LabelFontSizeProperty, value);
    }

    public event RoutedEventHandler Activated
    {
        add => AddHandler(ActivatedEvent, value);
        remove => RemoveHandler(ActivatedEvent, value);
    }

    public SideNavItem()
    {
        InitializeComponent();
        // The ToolTip lives in its own popup visual tree, so FindAncestor back to this
        // UserControl cannot resolve there; hand it this item as its DataContext instead.
        NavToolTip.DataContext = this;
        _activeTextColor = ThemeValues.Color("TextPrimaryColor", Color.FromRgb(0xFA, 0xFA, 0xFA));
        _idleTextColor = ThemeValues.Color("TextSecondaryColor", Color.FromRgb(0xA1, 0xA1, 0xAA));
        _activeBackgroundColor = ThemeValues.Color("Surface2Color", Color.FromRgb(0x26, 0x26, 0x2B));
        _hoverBackgroundColor = ThemeValues.Color("SurfaceHoverColor", Color.FromRgb(0x1B, 0x1B, 0x1C));
        _idleBackgroundColor = ThemeValues.WithAlpha(_hoverBackgroundColor, 0);
        _textBrush = new SolidColorBrush(_idleTextColor);
        _backgroundBrush = new SolidColorBrush(_idleBackgroundColor);
        Root.Background = _backgroundBrush;
        Icon.Foreground = _textBrush;
        LabelTextBlock.Foreground = _textBrush;
        // Enter/leave re-run the transition; DP callbacks also re-run UpdateVisual, which
        // reads IsMouseOver live so a pointer resting on the item never falls back to idle.
        MouseEnter += (_, _) => UpdateVisual();
        MouseLeave += (_, _) => UpdateVisual();
        MouseLeftButtonUp += (_, _) => RaiseEvent(new RoutedEventArgs(ActivatedEvent, this));
        Loaded += (_, _) =>
        {
            ThemeResolver.Changed -= ThemeResolver_Changed;
            ThemeResolver.Changed += ThemeResolver_Changed;
            RefreshThemeColors();
            UpdateVisual(animate: false);
        };
        Unloaded += (_, _) => ThemeResolver.Changed -= ThemeResolver_Changed;
    }

    private void ThemeResolver_Changed(object? sender, EventArgs e)
    {
        RefreshThemeColors();
        // Animate: the switch keeps the item's live hover/active state but fades the palette
        // change instead of snapping both brushes at once.
        UpdateVisual(animate: true);
    }

    private void RefreshThemeColors()
    {
        _activeTextColor = ThemeValues.Color("TextPrimaryColor", Color.FromRgb(0xFA, 0xFA, 0xFA));
        _idleTextColor = ThemeValues.Color("TextSecondaryColor", Color.FromRgb(0xA1, 0xA1, 0xAA));
        _activeBackgroundColor = ThemeValues.Color("Surface2Color", Color.FromRgb(0x26, 0x26, 0x2B));
        _hoverBackgroundColor = ThemeValues.Color("SurfaceHoverColor", Color.FromRgb(0x1B, 0x1B, 0x1C));
        _idleBackgroundColor = ThemeValues.WithAlpha(_hoverBackgroundColor, 0);
    }

    private void UpdateVisual(bool animate = true)
    {
        bool hovering = IsMouseOver;
        AnimateBrush(
            _textBrush,
            IsActive || hovering ? _activeTextColor : _idleTextColor,
            ref _textBrushTarget,
            animate);
        AnimateBrush(
            _backgroundBrush,
            IsActive
                ? _activeBackgroundColor
                : hovering ? _hoverBackgroundColor : _idleBackgroundColor,
            ref _backgroundBrushTarget,
            animate);

        // Icon-only entries (with a Lucide kind) flip the label off in the collapsed 72px
        // bar; icon-less entries have nothing to fall back to, so they keep showing their
        // short label text instead and the tooltip stays off.
        PackIconLucideKind? iconKind = IconKind;
        if (iconKind is not null)
        {
            Icon.Kind = iconKind.Value;
        }

        Icon.Visibility = iconKind is not null ? Visibility.Visible : Visibility.Collapsed;
        LabelTextBlock.Visibility = iconKind is null || !IsCollapsed ? Visibility.Visible : Visibility.Collapsed;
        NavToolTip.Visibility = IsCollapsed && iconKind is not null ? Visibility.Visible : Visibility.Collapsed;

        if (IsCollapsed)
        {
            ItemContent.Margin = new Thickness(0);
            ItemContent.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            ItemContent.Margin = new Thickness(12 + Indent, 0, 12, 0);
            ItemContent.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private void AnimateBrush(SolidColorBrush brush, Color target, ref Color? playingTarget, bool animate)
    {
        if (animate)
        {
            // Re-issuing an identical animation restarts it from the live value and reads as
            // a flicker, so only begin when the target color actually changed.
            if (playingTarget == target)
            {
                return;
            }

            playingTarget = target;
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TransitionDuration) { EasingFunction = TransitionEase });
        }
        else
        {
            playingTarget = null;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
        }
    }
}
