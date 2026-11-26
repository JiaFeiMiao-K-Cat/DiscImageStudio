using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

public partial class ConfirmModal : UserControl
{
    private TaskCompletionSource<bool>? _pending;
    private TaskCompletionSource<bool?>? _pendingThreeWay;
    private bool _closing;
    private int _transitionVersion;

    public ConfirmModal()
    {
        InitializeComponent();
        // Initial focus on Cancel — the safe default: Enter on an unexamined dialog
        // refuses, and Tab walks to Confirm for the deliberate yes. The hook sits on the
        // Root, not this control: a collapsed child does not change the UserControl's
        // own IsVisible, so the previous hook never fired and no button ever took focus.
        Root.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => CancelButton.Focus());
            }
        };
    }

    /// <summary>True while a confirmation is awaiting an answer; the window uses this to keep
    /// page-level shortcuts away from the modal's keyboard.</summary>
    internal bool IsOpen => _pending is not null || _pendingThreeWay is not null;

    /// Shows the modal and completes with the user's choice; the default is refusal.
    public Task<bool> ShowAsync(string title, string message, string confirmLabel)
    {
        if (_pending is not null || _pendingThreeWay is not null)
        {
            throw new InvalidOperationException("A confirmation is already pending.");
        }

        TitleText.Text = title;
        MessageText.Text = message;
        HeaderIcon.Kind = PackIconLucideKind.TriangleAlert;
        HeaderIcon.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        ConfirmButton.Content = confirmLabel;
        ConfirmButton.Style = (Style)FindResource("DangerButton");
        NeutralButton.Visibility = Visibility.Collapsed;
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowSurface();
        return _pending.Task;
    }

    /// <summary>Two-button confirmation for a constructive action that is not destructive,
    /// such as downloading an application update.</summary>
    public Task<bool> ShowPrimaryAsync(string title, string message, string confirmLabel)
        => ShowPrimaryAsync(title, message, confirmLabel, PackIconLucideKind.FileDown);

    /// <summary>The constructive confirmation with its own header glyph, e.g. a globe before
    /// sending text to an online service.</summary>
    public Task<bool> ShowPrimaryAsync(string title, string message, string confirmLabel, PackIconLucideKind icon)
    {
        if (_pending is not null || _pendingThreeWay is not null)
        {
            throw new InvalidOperationException("A confirmation is already pending.");
        }

        TitleText.Text = title;
        MessageText.Text = message;
        HeaderIcon.Kind = icon;
        HeaderIcon.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        ConfirmButton.Content = confirmLabel;
        ConfirmButton.Style = (Style)FindResource("PrimaryButton");
        NeutralButton.Visibility = Visibility.Collapsed;
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowSurface();
        return _pending.Task;
    }

    /// <summary>PR-style save dialog: confirm saves (true), the neutral button discards
    /// (false), cancel leaves the choice open (null).</summary>
    public Task<bool?> ShowThreeWayAsync(string title, string message, string confirmLabel, string neutralLabel)
    {
        if (_pending is not null || _pendingThreeWay is not null)
        {
            throw new InvalidOperationException("A confirmation is already pending.");
        }

        TitleText.Text = title;
        MessageText.Text = message;
        // A save prompt is not a destructive warning: swap the red alert triangle for the
        // neutral save glyph so the header matches the primary (not danger) confirm button.
        HeaderIcon.Kind = PackIconLucideKind.Save;
        HeaderIcon.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        ConfirmButton.Content = confirmLabel;
        ConfirmButton.Style = (Style)FindResource("PrimaryButton");
        NeutralButton.Content = neutralLabel;
        NeutralButton.Visibility = Visibility.Visible;
        _pendingThreeWay = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowSurface();
        return _pendingThreeWay.Task;
    }

    private void ShowSurface()
    {
        ++_transitionVersion;
        _closing = false;
        Root.IsHitTestVisible = true;
        DialogCard.IsHitTestVisible = true;
        Root.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsOpen && !_closing) CancelButton.Focus();
        }));
        if (IsLoaded)
        {
            Motion.Enter(DialogCard);
            Backdrop.BeginAnimation(OpacityProperty, null);
            Backdrop.Opacity = Motion.Enabled ? 0 : 1;
            Motion.Fade(Backdrop, 1, Motion.Base);
        }
    }

    private void Complete(bool? confirmed)
    {
        if (_closing || !IsOpen) return;
        if (!IsLoaded || !Motion.Enabled)
        {
            Finish(confirmed);
            return;
        }
        _closing = true;
        int version = ++_transitionVersion;
        DialogCard.IsHitTestVisible = false;
        if (DialogCard.RenderTransform is TranslateTransform shift)
            Motion.Animate(shift, TranslateTransform.YProperty, 4, Motion.Exit, exiting: true);
        Motion.Fade(Backdrop, 0, Motion.Exit, exiting: true);
        Motion.Fade(DialogCard, 0, Motion.Exit, exiting: true, completed: () =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (version == _transitionVersion) Finish(confirmed);
            }));
        });
    }

    private void Finish(bool? confirmed)
    {
        ++_transitionVersion;
        _closing = false;
        Root.Visibility = Visibility.Collapsed;
        DialogCard.BeginAnimation(OpacityProperty, null);
        DialogCard.Opacity = 1;
        Backdrop.BeginAnimation(OpacityProperty, null);
        Backdrop.Opacity = 1;
        TaskCompletionSource<bool>? pending = _pending;
        TaskCompletionSource<bool?>? threeWay = _pendingThreeWay;
        _pending = null;
        _pendingThreeWay = null;
        pending?.TrySetResult(confirmed ?? false);
        threeWay?.TrySetResult(confirmed);
    }

    /// Resolves an outstanding confirmation as refusal. The host window calls this when it
    /// closes so an awaiting command can finish instead of leaking a pending task.
    internal void CancelPending() => Finish(confirmed: null);

    /// Routes the modal-facing keys. Escape resolves the dialog (refusal for the burn
    /// confirm, cancel for the save dialog); Tab cycles focus across the modal's own
    /// buttons so keyboard users can reach Confirm without wandering into the page
    /// behind the overlay. Every other key — notably Enter and Space on the focused
    /// button — falls through to normal routing. Returns true when the key was consumed.
    public bool HandleKeyDown(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (_pending is null && _pendingThreeWay is null)
        {
            return false;
        }

        if (key == Key.Escape)
        {
            Complete(confirmed: _pendingThreeWay is not null ? null : false);
            return true;
        }

        if (key == Key.Tab)
        {
            MoveModalFocus(modifiers.HasFlag(ModifierKeys.Shift));
            return true;
        }

        return false;
    }

    /// Cycles keyboard focus across the visible modal buttons only (Cancel, the
    /// three-way dialog's Discard, Confirm), wrapping at both ends.
    private void MoveModalFocus(bool backwards)
    {
        List<Button> buttons = [CancelButton];
        if (NeutralButton.Visibility == Visibility.Visible)
        {
            buttons.Add(NeutralButton);
        }
        buttons.Add(ConfirmButton);

        Button? focused = Keyboard.FocusedElement as Button;
        int index = focused is null ? -1 : buttons.IndexOf(focused);
        if (index < 0)
        {
            buttons[backwards ? buttons.Count - 1 : 0].Focus();
            return;
        }

        int next = (index + (backwards ? -1 : 1) + buttons.Count) % buttons.Count;
        buttons[next].Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Complete(confirmed: true);

    private void Neutral_Click(object sender, RoutedEventArgs e) => Complete(confirmed: false);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(confirmed: null);
}
