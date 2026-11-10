using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

/// <summary>Text that copies itself on click. The hover underline and hand cursor are the
/// only affordances, so values can be copied without a button reserving layout space. The
/// hit test also swallows the click: the drive card is itself a button, and copying a value
/// must not toggle the details flyout.</summary>
public partial class CopyableText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CopyableText), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(CopyableText),
        new PropertyMetadata(TextAlignment.Left));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(CopyableText),
        new PropertyMetadata(TextWrapping.NoWrap));

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming), typeof(TextTrimming), typeof(CopyableText),
        new PropertyMetadata(TextTrimming.None));

    public CopyableText()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Copy();
        };
        MouseEnter += (_, _) => ValueText.TextDecorations = TextDecorations.Underline;
        MouseLeave += (_, _) => ValueText.TextDecorations = null;
        Unloaded += (_, _) =>
        {
            CopiedChip.BeginAnimation(OpacityProperty, null);
            CopiedPopup.IsOpen = false;
        };
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    private void Copy()
    {
        string value = Text;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            Clipboard.SetText(value);
        }
        catch (ExternalException)
        {
            // Another process owns the clipboard; there is nothing useful to show.
            return;
        }

        CopiedPopup.IsOpen = true;
        CopiedChip.BeginAnimation(OpacityProperty, null);
        DoubleAnimationUsingKeyFrames fade = new();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(750))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)))
        {
            EasingFunction = Motion.EaseOut,
        });
        fade.Completed += (_, _) => CopiedPopup.IsOpen = false;
        CopiedChip.BeginAnimation(OpacityProperty, fade);
    }
}
