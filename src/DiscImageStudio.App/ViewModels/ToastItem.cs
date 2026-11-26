using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiscImageStudio.ViewModels;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public partial class ToastItem : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _closed;
    private readonly EventHandler _onTick;
    private DateTime _deadline;
    private bool _paused;

    [ObservableProperty]
    private bool _entered;

    public string Title { get; }

    public string Message { get; }

    public ToastKind Kind { get; }

    public TimeSpan Timeout { get; }

    /// <summary>Optional follow-up shown as a button (e.g. open the output folder). Null
    /// keeps the toast a plain notice.</summary>
    public string? ActionLabel { get; }

    public Action? Action { get; }

    public event EventHandler? Closed;

    public ToastItem(Dispatcher dispatcher, string title, string message, ToastKind kind, TimeSpan timeout,
        string? actionLabel = null, Action? action = null)
    {
        _dispatcher = dispatcher;
        Title = title;
        Message = message;
        Kind = kind;
        Timeout = timeout;
        ActionLabel = actionLabel;
        Action = action;
        _onTick = (_, _) => Close();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = timeout,
        };
        _timer.Tick += _onTick;
    }

    public void BeginLifetime()
    {
        _deadline = DateTime.UtcNow + Timeout;
        _timer.Interval = Timeout;
        _timer.Start();
    }

    /// <summary>Freezes the countdown while the user is reading: hovering or keyboard-focusing
    /// the card pauses it, so a two-second Success toast is not dismissed mid-read. The
    /// remaining time is re-armed as the interval and resumed on leave.</summary>
    public void PauseLifetime()
    {
        if (_paused || !_timer.IsEnabled)
        {
            return;
        }

        _timer.Stop();
        TimeSpan remaining = _deadline - DateTime.UtcNow;
        _timer.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        _paused = true;
    }

    public void ResumeLifetime()
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        _timer.Start();
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        _timer.Tick -= _onTick;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
