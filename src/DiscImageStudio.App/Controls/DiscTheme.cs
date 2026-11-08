using System.Windows;
using System.Windows.Media;

namespace DiscImageStudio.Controls;

/// <summary>Resolves the canvas palette and typeface from the application theme so the
/// annulus preview, the design canvas and the calibration photo canvas draw with one set of
/// tokens (Themes/Tokens.xaml) instead of private literals. Lookups fall back to frozen
/// defaults when no Application exists, which happens in design-time hosts.</summary>
internal static class DiscTheme
{
    public static Brush Backdrop => Resolve("DiscCanvasBackdropBrush", 0x0D, 0x0D, 0x0F);

    public static Brush DiscBody => Resolve("DiscBodyBrush", 0x1C, 0x1C, 0x20);

    public static Brush Hub => Resolve("DiscHubBrush", 0x0D, 0x0D, 0x0F);

    public static Brush Ring => Resolve("DiscRingBrush", 0x30, 0x33, 0x85, 0xFF);

    public static Brush Guide => Resolve("DiscGuideBrush", 0x55, 0xA1, 0xA1, 0xAA);

    public static Brush Mask => Resolve("DiscMaskBrush", 0x88, 0x00, 0x00, 0x00);

    public static Brush Selection => Resolve("DiscSelectionBrush", 0x33, 0x85, 0xFF);

    /// <summary>Potential file occupancy on the design canvas. Stronger than the interface
    /// soft tint because the ring must stay visible over arbitrary artwork.</summary>
    public static Brush DataRegion => Resolve("DiscDataRegionBrush", 0x66, 0x33, 0x85, 0xFF);

    /// <summary>Footprint marker for a layer whose image file cannot be decoded.</summary>
    public static Brush MissingImage => Resolve("DangerBrush", 0xF3, 0x12, 0x60);

    public static Brush PhotoBackdrop => Resolve("CalibrationPhotoBackdropBrush", 0x13, 0x15, 0x1A);

    public static Brush Caption => Resolve("CalibrationCaptionBrush", 0xDC, 0x13, 0x15, 0x1A);

    public static Brush Edge => Resolve("CalibrationEdgeBrush", 0x74, 0xC0, 0xFF);

    public static Brush HubOutline => Resolve("CalibrationHubOutlineBrush", 0xFF, 0xA5, 0x00);

    public static Brush Trace => Resolve("CalibrationTraceBrush", 0x00, 0xFF, 0xFF);

    public static Brush Prediction => Resolve("CalibrationPredictionBrush", 0xFF, 0x8C, 0x00);

    /// <summary>A traced point that the selected prediction cannot explain within noise.</summary>
    public static Brush Outlier => Resolve("DangerBrush", 0xF3, 0x12, 0x60);

    public static Brush Handle => Resolve("CalibrationHandleBrush", 0xD3, 0xD3, 0xD3);

    public static Brush Outline => Resolve("CalibrationOutlineBrush", 0x00, 0x00, 0x00);

    public static FontFamily Font =>
        Application.Current?.TryFindResource("AppFontFamily") as FontFamily
        ?? new FontFamily("Microsoft YaHei UI, Segoe UI");

    public static Typeface Typeface =>
        new(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static Brush Resolve(string key, byte r, byte g, byte b) =>
        Resolve(key, 0xFF, r, g, b);

    private static Brush Resolve(string key, byte a, byte r, byte g, byte b)
    {
        if (Application.Current?.TryFindResource(key) is Brush themed)
        {
            return themed;
        }

        SolidColorBrush fallback = new(Color.FromArgb(a, r, g, b));
        fallback.Freeze();
        return fallback;
    }
}
