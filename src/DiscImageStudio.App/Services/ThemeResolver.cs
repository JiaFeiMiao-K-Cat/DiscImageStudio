using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;

namespace DiscImageStudio.Services;

/// <summary>Chooses the concrete palette behind <see cref="AppTheme.System"/> and swaps the
/// light overlay in or out of the application resources. The dark tokens stay in
/// Themes/Tokens.xaml; the light palette is an overlay inserted between Tokens and Controls,
/// so removing it restores the dark values. Controls must reference palette keys with
/// DynamicResource; code-created brushes subscribe to <see cref="Changed"/> and re-read
/// <see cref="ThemeValues"/>/resource lookups.</summary>
internal static class ThemeResolver
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const string LightPaletteName = "Palette.Light.xaml";

    internal static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after the active palette actually changed. Controls that cache colors
    /// in code (custom-drawn canvases, animated brushes) listen for this.</summary>
    internal static event EventHandler? Changed;

    internal static AppTheme Resolve(AppTheme theme) => theme switch
    {
        AppTheme.Light => AppTheme.Light,
        AppTheme.System => SystemPrefersLight() ? AppTheme.Light : AppTheme.Dark,
        _ => AppTheme.Dark,
    };

    /// <summary>Applies the requested theme (resolving the system preference) and returns
    /// the effective one.</summary>
    internal static AppTheme Apply(Application application, AppTheme requested)
    {
        ArgumentNullException.ThrowIfNull(application);
        AppTheme effective = Resolve(requested);
        IList<ResourceDictionary> merged = application.Resources.MergedDictionaries;
        int lightIndex = FindLightPalette(merged);
        bool wasLight = lightIndex >= 0;
        bool wantsLight = effective == AppTheme.Light;

        if (wantsLight && !wasLight)
        {
            // Order matters: Tokens -> Light -> Controls. The overlay must sit after the
            // dark tokens (to win lookups) and before the controls (which reference keys).
            int index = merged.Count == 0 ? 0 : 1;
            merged.Insert(index, new ResourceDictionary
            {
                Source = new Uri("/Themes/" + LightPaletteName, UriKind.Relative),
            });
        }
        else if (!wantsLight && wasLight)
        {
            merged.RemoveAt(lightIndex);
        }

        Current = effective;
        if (wasLight != wantsLight)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }

        return effective;
    }

    private static int FindLightPalette(IList<ResourceDictionary> merged)
    {
        for (int index = 0; index < merged.Count; index++)
        {
            Uri? source = merged[index].Source;
            if (source is not null
                && source.OriginalString.EndsWith(LightPaletteName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) is int value && value != 0;
        }
        catch (Exception exception) when (exception is IOException
                                              or SecurityException
                                              or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
