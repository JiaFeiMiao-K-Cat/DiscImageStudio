using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DiscImageStudio.Controls;

public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty ProjectTitleProperty = DependencyProperty.Register(
        nameof(ProjectTitle), typeof(string), typeof(TitleBar),
        new PropertyMetadata(string.Empty, static (sender, _) =>
        {
            TitleBar bar = (TitleBar)sender;
            bar.ProjectTitleText.Text = bar.ProjectTitle;
            bar.ProjectTitleText.Visibility = string.IsNullOrEmpty(bar.ProjectTitle)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }));

    /// <summary>Project-mode text after the app name (name plus unsaved dot); hidden in the
    /// home mode and in windows without a project.</summary>
    public string ProjectTitle
    {
        get => (string)GetValue(ProjectTitleProperty);
        set => SetValue(ProjectTitleProperty, value);
    }

    public static readonly DependencyProperty ShowMinimizeButtonProperty = DependencyProperty.Register(
        nameof(ShowMinimizeButton), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(true, static (sender, _) => ((TitleBar)sender).ApplyWindowButtonVisibility()));

    public static readonly DependencyProperty ShowMaximizeButtonProperty = DependencyProperty.Register(
        nameof(ShowMaximizeButton), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(true, static (sender, _) => ((TitleBar)sender).ApplyWindowButtonVisibility()));

    public static readonly DependencyProperty ShowAppMenuProperty = DependencyProperty.Register(
        nameof(ShowAppMenu), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(false, static (sender, _) => ((TitleBar)sender).ApplyWindowButtonVisibility()));

    public static readonly RoutedEvent AppMenuRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(AppMenuRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TitleBar));

    /// <summary>Whether the chrome carries a minimize button; dialogs that cannot be
    /// minimized turn it off. Leave the defaults alone for the main window.</summary>
    public bool ShowMinimizeButton
    {
        get => (bool)GetValue(ShowMinimizeButtonProperty);
        set => SetValue(ShowMinimizeButtonProperty, value);
    }

    /// <summary>Whether the chrome carries a maximize/restore button; fixed-size dialogs turn
    /// it off.</summary>
    public bool ShowMaximizeButton
    {
        get => (bool)GetValue(ShowMaximizeButtonProperty);
        set => SetValue(ShowMaximizeButtonProperty, value);
    }

    /// <summary>Whether the chrome carries the application-menu button (settings, about).
    /// The window that hosts the title bar decides what the menu contains.</summary>
    public bool ShowAppMenu
    {
        get => (bool)GetValue(ShowAppMenuProperty);
        set => SetValue(ShowAppMenuProperty, value);
    }

    /// <summary>Raised when the application-menu button is clicked. The window opens the
    /// menu itself because the items belong to the window's commands, and anchors it through
    /// <see cref="AppMenuAnchor"/>.</summary>
    public event RoutedEventHandler AppMenuRequested
    {
        add => AddHandler(AppMenuRequestedEvent, value);
        remove => RemoveHandler(AppMenuRequestedEvent, value);
    }

    /// <summary>The chrome button the application menu anchors under. The event cannot carry
    /// it as its source: WPF rewrites the source of a raised routed event to the raising
    /// element (this title bar), not the button passed to the constructor.</summary>
    public FrameworkElement AppMenuAnchor => AppMenuButton;

    private void ApplyWindowButtonVisibility()
    {
        AppMenuButton.Visibility = ShowAppMenu ? Visibility.Visible : Visibility.Collapsed;
        MinimizeButton.Visibility = ShowMinimizeButton ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.Visibility = ShowMaximizeButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private Window? _window;
    private Path? _maximizeGlyph;
    private Path? _restoreGlyph;

    public TitleBar()
    {
        InitializeComponent();
        MaximizeButton.Loaded += (_, _) =>
        {
            _maximizeGlyph = (Path)MaximizeButton.Template.FindName("MaximizeGlyph", MaximizeButton);
            _restoreGlyph = (Path)MaximizeButton.Template.FindName("RestoreGlyph", MaximizeButton);
            UpdateGlyph();
        };
        Loaded += (_, _) => AttachWindow();
        Unloaded += (_, _) => DetachWindow();
    }

    private void AttachWindow()
    {
        DetachWindow();
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }

        _window.StateChanged += Window_StateChanged;
        UpdateGlyph();
    }

    private void DetachWindow()
    {
        if (_window is not null)
        {
            _window.StateChanged -= Window_StateChanged;
            _window = null;
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateGlyph();

    private void UpdateGlyph()
    {
        bool maximized = _window?.WindowState == WindowState.Maximized;
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        if (_maximizeGlyph is null || _restoreGlyph is null)
        {
            return;
        }

        _maximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        _restoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.WindowState = WindowState.Minimized;
        }
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _window?.Close();

    private void AppMenu_Click(object sender, RoutedEventArgs e)
        => RaiseEvent(new RoutedEventArgs(AppMenuRequestedEvent, AppMenuButton));
}
