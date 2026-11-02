using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

/// <summary>Shell-level job strip: name, status, progress and result actions in the collapsed
/// row, the run log in the expanded row. It replaces the separate log page — engine output is
/// application-global, so it belongs to the chrome rather than to one workspace the user has to
/// navigate to and back from.</summary>
public partial class JobDock : UserControl
{
    private ShellViewModel? _shell;
    private bool _isAttached;

    public JobDock()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachShell();
        Loaded += (_, _) =>
        {
            AttachShell();
            ThemeResolver.Changed -= ThemeResolver_Changed;
            ThemeResolver.Changed += ThemeResolver_Changed;
            LogTextBox.Text = _shell?.LogText ?? string.Empty;
            _isAttached = true;
            UpdateStatusIcon();
            ScrollToEnd();
        };
        Unloaded += (_, _) =>
        {
            ThemeResolver.Changed -= ThemeResolver_Changed;
            _isAttached = false;
        };
    }

    // The status glyph holds a brush resolved from the palette; re-resolve it on a switch.
    private void ThemeResolver_Changed(object? sender, EventArgs e) => UpdateStatusIcon();

    private void AttachShell()
    {
        if (ReferenceEquals(_shell, DataContext))
        {
            return;
        }

        if (_shell is not null)
        {
            _shell.LogAppended -= OnLogAppended;
            _shell.LogCleared -= OnLogCleared;
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }

        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
        {
            _shell.LogAppended += OnLogAppended;
            _shell.LogCleared += OnLogCleared;
            _shell.PropertyChanged += OnShellPropertyChanged;
        }
    }

    /// Presentation glue only: append engine output lines and keep the view pinned to bottom.
    private void OnLogAppended(string text)
    {
        if (!_isAttached)
        {
            return;
        }

        LogTextBox.AppendText(text);
        ScrollToEnd();
    }

    private void OnLogCleared()
    {
        if (_isAttached)
        {
            LogTextBox.Clear();
        }
    }

    /// The status glyph mirrors the strip: running work, a finished result, or idle.
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsJobRunning)
            or nameof(ShellViewModel.JobHasResult)
            or nameof(ShellViewModel.IsJobDockExpanded))
        {
            UpdateStatusIcon();
            if (e.PropertyName is nameof(ShellViewModel.IsJobDockExpanded) && _shell?.IsJobDockExpanded == true)
            {
                // Re-render on expansion: the box measures nothing while collapsed, so it needs
                // the whole text again to scroll to the tail correctly.
                LogTextBox.Text = _shell.LogText;
                ScrollToEnd();
            }
        }
    }

    private void UpdateStatusIcon()
    {
        if (_shell is null)
        {
            return;
        }

        (StatusIcon.Kind, StatusIcon.Foreground) = _shell.IsJobRunning
            ? (PackIconLucideKind.LoaderCircle, (Brush)FindResource("PrimaryBrush"))
            : _shell.JobHasResult
                ? (PackIconLucideKind.CircleCheck, (Brush)FindResource("SuccessBrush"))
                : (PackIconLucideKind.CircleDashed, (Brush)FindResource("TextMutedBrush"));
    }

    // The box can only scroll to text it has already measured, so wait for the layout pass.
    private void ScrollToEnd()
        => Dispatcher.InvokeAsync(LogTextBox.ScrollToEnd, DispatcherPriority.Background);
}
