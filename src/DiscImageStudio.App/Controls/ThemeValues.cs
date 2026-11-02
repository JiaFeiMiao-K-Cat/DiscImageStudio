using System.Windows;
using System.Windows.Media;

namespace DiscImageStudio.Controls;

/// <summary>Reads the shared theme values for code-driven visuals (hover animation colors,
/// canvas palettes). Every lookup falls back to a literal when no Application exists, which
/// happens in design-time hosts and headless tests, so callers never have to null-check.</summary>
internal static class ThemeValues
{
    /// Accepts either palette key form: callers name Color tokens and Brush tokens alike.
    public static Color Color(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) switch
        {
            Color color => color,
            SolidColorBrush brush => brush.Color,
            _ => fallback,
        };

    public static Brush Brush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    /// <summary>Scalar metrics (sidebar widths) for animations, which cannot bind resources
    /// the way a XAML property does.</summary>
    public static double Double(string key, double fallback) =>
        Application.Current?.TryFindResource(key) is double value ? value : fallback;

    /// <summary>The same color with a replacement alpha, for idle states that must fade in
    /// place: ColorAnimation interpolates every ARGB channel, so an idle color of
    /// Colors.Transparent (white at alpha 0) would flash gray mid-flight.</summary>
    public static Color WithAlpha(Color color, byte alpha) =>
        System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B);
}
