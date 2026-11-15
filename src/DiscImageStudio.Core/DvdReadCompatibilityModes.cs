namespace DiscImageStudio.Core;

/// <summary>Versioned drawing-byte transforms recorded with DVD images and calibration targets.</summary>
public static class DvdReadCompatibilityModes
{
    public const string Off = "off";
    public const string LegacyBridge = "bridge-9b-v1";
    public const string Periodic9B16 = "periodic-9b-16-v1";
    public const string WriteSafeLight = "write-safe-light-v1";
    public const string WriteSafeStrong = "write-safe-strong-v1";
    public const string WriteSafeZone = "write-safe-zone-v1";
    public const string Current = WriteSafeLight;

    public static bool IsSupported(string? mode) => mode is LegacyBridge or Periodic9B16 || IsWriteSafe(mode);

    public static bool IsWriteSafe(string? mode) => mode is WriteSafeLight or WriteSafeStrong or WriteSafeZone;
}
