using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Xaml.Styling;

namespace DiscImageStudio.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--render-app-icon", var output]) { AppIconRenderer.Save(output); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DesktopApp>().UsePlatformDetect();
}

public sealed class DesktopApp : Application
{
    public override void Initialize()
    {
        Name = "Disc Image Studio";
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://DiscImageStudio.Desktop/")) { Source = new Uri("avares://DiscImageStudio.Desktop/StudioTheme.axaml") });
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
