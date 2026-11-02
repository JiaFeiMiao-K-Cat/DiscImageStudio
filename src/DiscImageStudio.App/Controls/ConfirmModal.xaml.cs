using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

public partial class ConfirmModal : UserControl
{
    private TaskCompletionSource<bool>? _pending;
    private TaskCompletionSource<bool?>? _pendingThreeWay;

    public ConfirmModal()
    {
        InitializeComponent();
        IsVisibleChanged += (_, args) =>
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
        Root.Visibility = Visibility.Visible;
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        Root.Visibility = Visibility.Visible;
        _pendingThreeWay = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _pendingThreeWay.Task;
    }

    private void Complete(bool? confirmed)
    {
        Root.Visibility = Visibility.Collapsed;
        TaskCompletionSource<bool>? pending = _pending;
        TaskCompletionSource<bool?>? threeWay = _pendingThreeWay;
        _pending = null;
        _pendingThreeWay = null;
        pending?.TrySetResult(confirmed ?? false);
        threeWay?.TrySetResult(confirmed);
    }

    /// Lets the host window route Escape to the open modal; returns false when hidden.
    /// Escape refuses the burn confirm and cancels the save dialog alike.
    public bool HandleKeyDown(Key key)
    {
        if (_pending is null && _pendingThreeWay is null)
        {
            return false;
        }

        Complete(confirmed: _pendingThreeWay is not null ? null : false);
        return true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Complete(confirmed: true);

    private void Neutral_Click(object sender, RoutedEventArgs e) => Complete(confirmed: false);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(confirmed: null);
}
