namespace DiscImageStudio.Core;

/// <summary>Versioned drawing-byte transforms recorded with DVD images and calibration targets.</summary>
public static class DvdReadCompatibilityModes
{
    public const string Off = "off";
    public const string LegacyBridge = "bridge-9b-v1";
    public const string Periodic9B16 = "periodic-9b-16-v1";
    public const string Current = Periodic9B16;

    public static bool IsSupported(string? mode) => mode is LegacyBridge or Periodic9B16;
}
