using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

/// <summary>Text that copies itself on click or with Enter/Space once focused. The hover
/// underline and hand cursor are the only affordances, so values can be copied without a
/// button reserving layout space; keyboard focus reuses the underline. The hit test also
/// swallows the click: the drive card is itself a button, and copying a value must not
/// toggle the details flyout.</summary>
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
        MouseEnter += (_, _) => UpdateUnderline();
        MouseLeave += (_, _) => UpdateUnderline();
        GotKeyboardFocus += (_, _) => UpdateUnderline();
        LostKeyboardFocus += (_, _) => UpdateUnderline();
        // Enter and Space are the keyboard equivalents of the click; marking the key
        // handled also stops Space from activating an enclosing button (drive card).
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                Copy();
            }
        };
        Unloaded += (_, _) =>
        {
            CopiedChip.BeginAnimation(OpacityProperty, null);
            CopiedPopup.IsOpen = false;
        };
    }

    private void UpdateUnderline()
        => ValueText.TextDecorations = IsMouseOver || IsKeyboardFocusWithin
            ? TextDecorations.Underline
            : null;

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
        // The keyframes supply the visible hold; Stop must expose the final transparent base.
        CopiedChip.Opacity = 0;
        DoubleAnimationUsingKeyFrames fade = new() { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(750))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Motion.Enabled ? 950 : 750)))
        {
            EasingFunction = Motion.Enabled ? Motion.EaseOut : null,
        });
        fade.Completed += (_, _) => CopiedPopup.IsOpen = false;
        CopiedChip.BeginAnimation(OpacityProperty, fade);
    }
}
