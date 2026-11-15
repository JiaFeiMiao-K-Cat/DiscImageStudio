using DiscImageStudio.Core;

namespace DiscImageStudio.Localization;

internal static class DvdReadCompatibilityText
{
    internal static string Summary(bool enabled, string mode) => enabled
        ? L.Format("DvdReadCompatibility.Enabled", L.T(mode switch
        {
            DvdReadCompatibilityModes.WriteSafeLight => "DvdReadCompatibility.Light",
            DvdReadCompatibilityModes.WriteSafeStrong => "DvdReadCompatibility.Strong",
            DvdReadCompatibilityModes.WriteSafeZone => "DvdReadCompatibility.Zone",
            DvdReadCompatibilityModes.LegacyBridge => "DvdReadCompatibility.LegacyBridge",
            DvdReadCompatibilityModes.Periodic9B16 => "DvdReadCompatibility.LegacyPeriodic",
            _ => mode,
        }))
        : L.T("DvdReadCompatibility.Disabled");
}
