using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DiscImageStudio.Services;

namespace DiscImageStudio.Controls;

/// Two- or three-option selector rendered as a 12px-radius rounded-rectangle track with a
/// thumb that slides to the checked option. The two-option form keeps the original
/// <see cref="IsSecondSelected"/> bool binding; the three-option form (CD/DVD/BD) binds
/// <see cref="SelectedIndex"/>. A blank <see cref="LabelC"/> collapses the third cell.
public partial class SegmentedControl : UserControl
{
    public static readonly DependencyProperty LabelAProperty = DependencyProperty.Register(
        nameof(LabelA), typeof(string), typeof(SegmentedControl), new PropertyMetadata("CD"));

    public static readonly DependencyProperty LabelBProperty = DependencyProperty.Register(
        nameof(LabelB), typeof(string), typeof(SegmentedControl), new PropertyMetadata("DVD"));

    public static readonly DependencyProperty LabelCProperty = DependencyProperty.Register(
        nameof(LabelC), typeof(string), typeof(SegmentedControl),
        new PropertyMetadata(string.Empty, static (sender, _) => ((SegmentedControl)sender).ApplyLayout(animate: false)));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(SegmentedControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            static (sender, _) => ((SegmentedControl)sender).OnSelectedIndexChanged()));

    public static readonly DependencyProperty IsSecondSelectedProperty = DependencyProperty.Register(
        nameof(IsSecondSelected), typeof(bool), typeof(SegmentedControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            static (sender, _) =>
            {
                SegmentedControl control = (SegmentedControl)sender;
                if (!control._updatingSelection)
                {
                    control.SelectedIndex = control.IsSecondSelected ? 1 : 0;
                }

                control.SyncVisuals();
            }));

    // Motion and color idiom mirrored from SideNavItem: the fast duration for label
    // emphasis, one step slower so the thumb visibly travels the shared track.
    private static readonly Duration SlideDuration = Motion.Base;
    private static readonly Duration ColorDuration = Motion.Fast;
    private static readonly IEasingFunction TransitionEase = Motion.EaseOut;

    private Color _selectedTextColor;
    private Color _hoverTextColor;
    private Color _idleTextColor;
    private readonly SolidColorBrush _labelABrush;
    private readonly SolidColorBrush _labelBBrush;
    private readonly SolidColorBrush _labelCBrush;
    private bool _hoverA;
    private bool _hoverB;
    private bool _hoverC;
    private bool _updatingSelection;

    public SegmentedControl()
    {
        InitializeComponent();
        _selectedTextColor = ThemeValues.Color("OnPrimaryColor", Colors.White);
        _hoverTextColor = ThemeValues.Color("TextPrimaryColor", Color.FromRgb(0xFA, 0xFA, 0xFA));
        _idleTextColor = ThemeValues.Color("TextSecondaryColor", Color.FromRgb(0xA1, 0xA1, 0xAA));
        _labelABrush = new SolidColorBrush(_idleTextColor);
        _labelBBrush = new SolidColorBrush(_idleTextColor);
        _labelCBrush = new SolidColorBrush(_idleTextColor);
        OptionAText.Foreground = _labelABrush;
        OptionBText.Foreground = _labelBBrush;
        OptionCText.Foreground = _labelCBrush;
        Loaded += (_, _) =>
        {
            ThemeResolver.Changed -= ThemeResolver_Changed;
            ThemeResolver.Changed += ThemeResolver_Changed;
            RefreshThemeColors();
            ApplyLayout(animate: false);
        };
        Unloaded += (_, _) => ThemeResolver.Changed -= ThemeResolver_Changed;
        Cells.SizeChanged += (_, _) => ApplyLayout(animate: false);
        ApplyLayout(animate: false);
    }

    private void ThemeResolver_Changed(object? sender, EventArgs e)
    {
        RefreshThemeColors();
        UpdateLabelColors(animate: false);
    }

    private void RefreshThemeColors()
    {
        _selectedTextColor = ThemeValues.Color("OnPrimaryColor", Colors.White);
        _hoverTextColor = ThemeValues.Color("TextPrimaryColor", Color.FromRgb(0xFA, 0xFA, 0xFA));
        _idleTextColor = ThemeValues.Color("TextSecondaryColor", Color.FromRgb(0xA1, 0xA1, 0xAA));
    }

    public string LabelA
    {
        get => (string)GetValue(LabelAProperty);
        set => SetValue(LabelAProperty, value);
    }

    public string LabelB
    {
        get => (string)GetValue(LabelBProperty);
        set => SetValue(LabelBProperty, value);
    }

    public string LabelC
    {
        get => (string)GetValue(LabelCProperty);
        set => SetValue(LabelCProperty, value);
    }

    /// <summary>0 = first option, 1 = second, 2 = third when <see cref="LabelC"/> is set.</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// True when the right-hand option is the active one (the left option is its inverse),
    /// so a "DVD selected" VM flag binds one-to-one.
    public bool IsSecondSelected
    {
        get => (bool)GetValue(IsSecondSelectedProperty);
        set => SetValue(IsSecondSelectedProperty, value);
    }

    private bool HasThird => !string.IsNullOrWhiteSpace(LabelC);

    private bool Realized => IsLoaded && Cells.ActualWidth > 0;

    private int OptionCount => HasThird ? 3 : 2;

    private void OnSelectedIndexChanged()
    {
        int max = OptionCount - 1;
        int index = Math.Clamp(SelectedIndex, 0, max);
        if (index != SelectedIndex)
        {
            SelectedIndex = index;
            return;
        }

        _updatingSelection = true;
        try
        {
            IsSecondSelected = index == 1;
        }
        finally
        {
            _updatingSelection = false;
        }

        SyncVisuals();
    }

    private void SyncVisuals()
    {
        // No GroupName: several instances can stay alive across pages, and a shared group
        // name would let an invisible page uncheck the visible one. State is owned here.
        int index = Math.Clamp(SelectedIndex, 0, OptionCount - 1);
        OptionARadio.IsChecked = index == 0;
        OptionBRadio.IsChecked = index == 1;
        OptionCRadio.IsChecked = index == 2;
        AnimateThumb(index);
        UpdateLabelColors();
    }

    private void ApplyLayout(bool animate)
    {
        bool hasThird = HasThird;
        ThirdColumn.Width = hasThird ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        OptionCRadio.Visibility = hasThird ? Visibility.Visible : Visibility.Collapsed;

        if (!Realized)
        {
            return;
        }

        int index = Math.Clamp(SelectedIndex, 0, OptionCount - 1);
        // Retarget instantly on first layout and on live resizes; only user-driven
        // selection changes travel along the animated slide.
        AnimateThumb(index, animate);
        UpdateLabelColors(animate);
    }

    private void AnimateThumb(int index, bool animate = true)
    {
        double target = OptionCount == 0 ? 0 : Cells.ActualWidth * index / OptionCount;
        if (!animate || !Realized)
        {
            ThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            ThumbShift.X = target;
            return;
        }

        ThumbShift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(target, SlideDuration) { EasingFunction = TransitionEase });
    }

    private void UpdateLabelColors(bool animate = true)
    {
        int index = Math.Clamp(SelectedIndex, 0, OptionCount - 1);
        AnimateColor(_labelABrush, index == 0 ? _selectedTextColor : _hoverA ? _hoverTextColor : _idleTextColor, animate);
        AnimateColor(_labelBBrush, index == 1 ? _selectedTextColor : _hoverB ? _hoverTextColor : _idleTextColor, animate);
        AnimateColor(_labelCBrush, index == 2 ? _selectedTextColor : _hoverC ? _hoverTextColor : _idleTextColor, animate);
    }

    private void AnimateColor(SolidColorBrush brush, Color target, bool animate)
    {
        if (animate && Realized)
        {
            if (brush.Color == target)
            {
                return;
            }

            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, ColorDuration) { EasingFunction = TransitionEase });
        }
        else
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
        }
    }

    private void Select(int index)
    {
        if (SelectedIndex == index)
        {
            return;
        }

        SelectedIndex = index; // DP callback applies visuals
    }

    private void OptionAClick(object sender, RoutedEventArgs e)
    {
        Select(0);
    }

    private void OptionBClick(object sender, RoutedEventArgs e)
    {
        Select(1);
    }

    private void OptionCClick(object sender, RoutedEventArgs e)
    {
        Select(2);
    }

    private void OptionKeyDown(object sender, KeyEventArgs e)
    {
        // Without a radio group, arrow keys do not move the check natively; keep the
        // native arrow gesture across the visible options.
        int current = sender == OptionARadio ? 0 : sender == OptionBRadio ? 1 : 2;
        int next = e.Key switch
        {
            Key.Left => Math.Max(0, current - 1),
            Key.Right => Math.Min(OptionCount - 1, current + 1),
            _ => current,
        };
        if (next == current)
        {
            return;
        }

        e.Handled = true;
        Select(next);
        RadioButton target = next == 0 ? OptionARadio : next == 1 ? OptionBRadio : OptionCRadio;
        target.Focus();
    }

    private void OptionMouseEnter(object sender, MouseEventArgs e)
    {
        _hoverA = sender == OptionARadio;
        _hoverB = sender == OptionBRadio;
        _hoverC = sender == OptionCRadio;
        UpdateLabelColors();
    }

    private void OptionMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender == OptionARadio)
        {
            _hoverA = false;
        }
        else if (sender == OptionBRadio)
        {
            _hoverB = false;
        }
        else
        {
            _hoverC = false;
        }

        UpdateLabelColors();
    }
}
