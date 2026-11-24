using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Controls;

/// <summary>Top-right toast stack. Entrance runs from the item template's Loaded trigger;
/// the exit is orchestrated here: the host subscribes to each toast's Closed event as its
/// container is generated, plays the mirror of the entrance (slide out + fade), and the
/// shell — which owns the collection — drops the item slightly later.</summary>
public partial class ToastHost : UserControl
{
    private static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(200));
    private readonly HashSet<ToastItem> _exitHooked = [];

    public ToastHost()
    {
        InitializeComponent();
        ToastList.ItemContainerGenerator.StatusChanged += (_, _) => HookExitAnimations();
    }

    private void CloseToast_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastItem toast)
        {
            toast.Close();
        }
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastItem toast)
        {
            toast.Action?.Invoke();
            toast.Close();
        }
    }

    // Reading pauses: a hovered or keyboard-focused card holds its countdown and resumes
    // it when the pointer or focus leaves. Close() already stopped the timer, so pausing
    // during the exit animation is a no-op.
    private static ToastItem? ToastOf(object sender) => (sender as FrameworkElement)?.DataContext as ToastItem;

    private void ToastCard_MouseEnter(object sender, MouseEventArgs e) => ToastOf(sender)?.PauseLifetime();

    private void ToastCard_MouseLeave(object sender, MouseEventArgs e) => ToastOf(sender)?.ResumeLifetime();

    private void ToastCard_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ToastOf(sender)?.PauseLifetime();

    private void ToastCard_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ToastOf(sender)?.ResumeLifetime();

    private void HookExitAnimations()
    {
        if (ToastList.ItemContainerGenerator.Status
            != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        {
            return;
        }

        for (int index = 0; index < ToastList.Items.Count; index++)
        {
            if (ToastList.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter presenter
                || presenter.DataContext is not ToastItem toast
                || !_exitHooked.Add(toast))
            {
                continue;
            }

            toast.Closed += (_, _) => PlayExit(presenter);
        }
    }

    /// <summary>The mirror of the entrance: slide back toward the edge while fading. The
    /// collection removal happens on the shell side just after this completes.</summary>
    private static void PlayExit(ContentPresenter presenter)
    {
        if (presenter.ContentTemplate?.FindName("ToastCard", presenter) is not Border card)
        {
            return;
        }

        CubicEase easeIn = new() { EasingMode = EasingMode.EaseIn };
        if (card.RenderTransform is TranslateTransform slide)
        {
            slide.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(0, 24, ExitDuration) { EasingFunction = easeIn });
        }

        card.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, ExitDuration) { EasingFunction = easeIn });
    }
}
