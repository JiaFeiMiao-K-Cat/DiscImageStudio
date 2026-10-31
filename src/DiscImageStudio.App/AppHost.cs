using System.Windows;
using DiscImageStudio.Services;

namespace DiscImageStudio;

internal static class AppHost
{
    internal static int Run()
    {
        AppSettingsStore store = new();
        AppSettings settings = store.Load();
        Application application = CreateApplication(ShutdownMode.OnMainWindowClose, settings.Theme);
        // New jobs must honor the saved cache folder; previously written files keep their
        // paths, which is why switching never migrates old cache.
        TempFiles.TryConfigureCacheDirectory(settings.CacheDirectory);
        MainWindow window = new(settings, store);
        return application.Run(window);
    }

    /// <summary>Installs the design-token theme at the application scope so every window,
    /// control template, and embedded UserControl can resolve resource lookups. The light
    /// palette overlays Tokens when requested; Controls is added last so its references see
    /// the finished order Tokens -> Light (optional) -> Controls.</summary>
    internal static Application CreateApplication(ShutdownMode shutdownMode, AppTheme theme = AppTheme.Dark)
    {
        Application application = new()
        {
            ShutdownMode = shutdownMode,
        };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Themes/Tokens.xaml", UriKind.Relative),
        });
        ThemeResolver.Apply(application, theme);
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Themes/Controls.xaml", UriKind.Relative),
        });
        return application;
    }
}
