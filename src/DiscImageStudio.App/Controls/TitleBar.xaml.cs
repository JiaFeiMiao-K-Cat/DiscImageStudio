using DiscImageStudio.Localization;
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

    public static readonly DependencyProperty ShowSettingsButtonProperty = DependencyProperty.Register(
        nameof(ShowSettingsButton), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(false, static (sender, _) => ((TitleBar)sender).ApplyWindowButtonVisibility()));

    public static readonly RoutedEvent SettingsRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(SettingsRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TitleBar));

    /// <summary>Whether the chrome carries the application-menu button (settings).
    /// The window that hosts the title bar decides what the menu contains.</summary>
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

    /// <summary>Shows the direct settings gear. Only the main window opts in.</summary>
    public bool ShowSettingsButton
    {
        get => (bool)GetValue(ShowSettingsButtonProperty);
        set => SetValue(ShowSettingsButtonProperty, value);
    }

    /// <summary>Raised when the settings gear is clicked; the host opens the settings
    /// dialog directly.</summary>
    public event RoutedEventHandler SettingsRequested
    {
        add => AddHandler(SettingsRequestedEvent, value);
        remove => RemoveHandler(SettingsRequestedEvent, value);
    }

    private void ApplyWindowButtonVisibility()
    {
        SettingsButton.Visibility = ShowSettingsButton ? Visibility.Visible : Visibility.Collapsed;
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
        L.LanguageChanged += Language_LanguageChanged;
        UpdateGlyph();
    }

    private void DetachWindow()
    {
        if (_window is not null)
        {
            _window.StateChanged -= Window_StateChanged;
            _window = null;
        }

        L.LanguageChanged -= Language_LanguageChanged;
    }

    private void Language_LanguageChanged(object? sender, EventArgs e) => UpdateGlyph();

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateGlyph();

    private void UpdateGlyph()
    {
        bool maximized = _window?.WindowState == WindowState.Maximized;
        MaximizeButton.ToolTip = maximized ? L.T("还原") : L.T("最大化");
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

    private void Settings_Click(object sender, RoutedEventArgs e)
        => RaiseEvent(new RoutedEventArgs(SettingsRequestedEvent, SettingsButton));
}
