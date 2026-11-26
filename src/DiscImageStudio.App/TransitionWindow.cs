using DiscImageStudio.Controls;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DiscImageStudio;

/// <summary>Shared entrance and exit motion for the app's secondary windows.</summary>
public class TransitionWindow : Window
{
    private const double Travel = 6;
    private TranslateTransform _offset = new();
    private bool _exiting;
    private bool _closeApproved;
    private bool _closed;

    internal bool AnimateTransitions { get; set; } = true;

    public TransitionWindow() => Loaded += BeginEntrance;

    private bool CanAnimate => AnimateTransitions && Motion.Enabled
        && IsVisible && WindowState != WindowState.Minimized && !Dispatcher.HasShutdownStarted;

    private void BeginEntrance(object sender, RoutedEventArgs e)
    {
        Loaded -= BeginEntrance;
        if (!CanAnimate || _exiting || Content is not UIElement surface)
        {
            return;
        }

        Motion.Enter(surface, Travel);
        _offset = (TranslateTransform)surface.RenderTransform;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        if (_exiting)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
        if (e.Cancel || !CanAnimate || Content is not UIElement surface)
        {
            return;
        }

        // Preserve explicit save/cancel results across WPF's canceled close attempt.
        bool? result = DialogResult;
        e.Cancel = true;
        _exiting = true;
        surface.IsHitTestVisible = false;
        surface.RenderTransform = _offset;
        Motion.Animate(_offset, TranslateTransform.YProperty, Travel, Motion.Exit, exiting: true);
        Motion.Fade(surface, 0, Motion.Exit, exiting: true, completed: () =>
        {
            // A close during the first entrance frame can already have opacity zero.
            // Always leave OnClosing before completing that close, even without a fade.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed) return;
                _closeApproved = true;
                if (result is bool answer && DialogResult != answer)
                    DialogResult = answer;
                else
                    Close();
            }));
        });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_exiting) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

}
