using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiscImageStudio.Controls;

/// <summary>A single-line label that keeps an overflowing text readable: while
/// <see cref="IsActive"/> it scrolls to its tail and back like the home page's project
/// paths, and while the line is clipped it carries the full text as its tooltip.</summary>
public partial class MarqueeText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText),
        new PropertyMetadata(string.Empty, static (sender, _) => ((MarqueeText)sender).RefreshScroll()));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(MarqueeText),
        new PropertyMetadata(false, static (sender, _) => ((MarqueeText)sender).RefreshScroll()));

    // Continuation cue where a clipped line exits, in place of an ellipsis. Masking the text
    // rather than painting a fade in a surface colour keeps it correct on any hovered or
    // selected card background.
    private static readonly Brush EdgeFadeMask = CreateEdgeFadeMask();

    public MarqueeText()
    {
        InitializeComponent();
        Viewport.SizeChanged += (_, _) => RefreshScroll();
        LabelText.SizeChanged += (_, _) => RefreshScroll();
        Unloaded += (_, _) => Marquee.Stop(LabelTranslate);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private void RefreshScroll()
    {
        double textWidth = LabelText.DesiredSize.Width;
        double viewportWidth = Viewport.ActualWidth;
        if (!double.IsFinite(textWidth) || !double.IsFinite(viewportWidth)
            || textWidth <= 0 || viewportWidth <= 0)
        {
            return;
        }

        double overflow = textWidth - viewportWidth;
        bool overflowing = overflow > Marquee.OverflowTolerance;
        ToolTip = overflowing ? Text : null;
        Viewport.OpacityMask = overflowing ? EdgeFadeMask : null;

        if (IsActive && overflowing)
        {
            Marquee.Begin(LabelTranslate, overflow);
            return;
        }

        Marquee.Stop(LabelTranslate);
        // A fitting line keeps the preset cards' centred title; an overflowing one sits at
        // its head until the marquee starts.
        LabelTranslate.X = overflowing ? 0 : (viewportWidth - textWidth) / 2;
    }

    private static Brush CreateEdgeFadeMask()
    {
        LinearGradientBrush mask = new()
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };
        mask.GradientStops.Add(new GradientStop(Colors.White, 0));
        mask.GradientStops.Add(new GradientStop(Colors.White, 0.89));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
        mask.Freeze();
        return mask;
    }
}
