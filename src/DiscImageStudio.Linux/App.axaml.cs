using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DiscImageStudio.Linux.Services;
using DiscImageStudio.Linux.ViewModels;

namespace DiscImageStudio.Linux;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public static void ApplyTheme(ThemePreference preference)
    {
        if (Current is { } application)
        {
            application.RequestedThemeVariant = preference switch
            {
                ThemePreference.Dark => ThemeVariant.Dark,
                ThemePreference.Light => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            };
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LinuxSettings settings = SettingsStore.Load();
            ApplyTheme(Program.Launch.Theme ?? settings.Theme);
            MainViewModel model = new(settings);
            MainWindow window = new() { DataContext = model };
            model.Ui = window;
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                if (Program.Launch.ProjectPath is { } project)
                {
                    await model.OpenProjectFileAsync(project, skipConfirm: true);
                }

                if (Program.Launch.ScreenshotDirectory is { } directory)
                {
                    int exitCode = await ScreenshotRunner.RunAsync(window, model, directory);
                    desktop.Shutdown(exitCode);
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
