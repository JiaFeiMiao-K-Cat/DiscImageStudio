using System.IO;
using System.Text.Json;
using DiscImageStudio.Core;

namespace DiscImageStudio.Dvd;

/// <summary>
/// Geometry recorded next to a generated ISO as <c>&lt;iso&gt;.json</c> by the fast dispersion
/// writer. The live preview reads it back so an ISO made by an earlier run, or imported from
/// another machine, is simulated with the disc size and radii it was generated with; the
/// drawing range itself is resolved by the simulate command from the same file.
/// </summary>
public sealed record DvdImageMetadata(
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    bool FilesAtOuterRing = false,
    uint DrawingStartLba = 0,
    uint DrawingEndLbaExclusive = 0,
    bool HasFileSystem = false,
    bool ReadCompatibility = false,
    uint? UsableSectors = null,
    double? SourceImageOuterRadiusMm = null,
    string? ReadCompatibilityMode = null)
{
    public uint OutputSectors => UsableSectors ?? TotalSectors;

    /// <summary>Reads the sidecar of an ISO, or null when it is absent or unreadable.</summary>
    public static DvdImageMetadata? TryLoad(string isoPath)
    {
        string path = isoPath + ".json";
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("imageMapping", out JsonElement mapping)
                || mapping.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            uint totalSectors = GetUInt32(mapping, "totalSectors");
            uint? usableSectors = null;
            if (mapping.TryGetProperty("usableSectors", out JsonElement usable) && usable.ValueKind != JsonValueKind.Null)
            {
                if (usable.ValueKind != JsonValueKind.Number || !usable.TryGetUInt32(out uint available)) return null;
                usableSectors = available;
            }
            if (usableSectors.HasValue && (usableSectors.Value == 0 || usableSectors.Value > totalSectors
                || usableSectors.Value % 16 != 0)) return null;
            double? sourceImageOuterRadiusMm = null;
            if (mapping.TryGetProperty("imageOuterRadiusMm", out JsonElement sourceRadius) && sourceRadius.ValueKind != JsonValueKind.Null)
            {
                if (sourceRadius.ValueKind != JsonValueKind.Number || !sourceRadius.TryGetDouble(out double radius)
                    || !double.IsFinite(radius) || radius <= 0) return null;
                sourceImageOuterRadiusMm = radius;
            }
            double innerRadius = GetDouble(mapping, "innerRadiusMm");
            double outerRadius = GetDouble(mapping, "outerRadiusMm");
            double linear = GetPitchCoefficient(mapping, "pitchLinear");
            double quadratic = GetPitchCoefficient(mapping, "pitchQuadratic");
            double cubic = GetPitchCoefficient(mapping, "pitchCubic");
            if (!TryGetOptionalUInt32(document.RootElement, "startLba", 0, out uint drawingStart)
                || !TryGetOptionalUInt32(document.RootElement, "fillSectors",
                    totalSectors - Math.Min(totalSectors, drawingStart), out uint drawingSectors))
                return null;
            if ((ulong)drawingStart + drawingSectors > (usableSectors ?? totalSectors) || drawingSectors == 0)
                return null;
            bool filesAtOuterRing = document.RootElement.TryGetProperty("filesAtOuterRing", out JsonElement position)
                && position.ValueKind == JsonValueKind.True;
            bool hasFileSystem = document.RootElement.TryGetProperty("iso", out JsonElement iso)
                && iso.ValueKind == JsonValueKind.Object
                && iso.TryGetProperty("mountableFilesystem", out JsonElement mountable)
                && mountable.ValueKind == JsonValueKind.True;
            DvdTrackGeometry.ValidatePitchCoefficients(linear, quadratic, cubic);
            string? readCompatibilityMode = document.RootElement.TryGetProperty("readCompatibility", out JsonElement mode)
                && mode.ValueKind == JsonValueKind.String && DvdReadCompatibilityModes.IsSupported(mode.GetString())
                ? mode.GetString() : null;
            bool readCompatibility = readCompatibilityMode is not null;
            return totalSectors > 0 && double.IsFinite(innerRadius) && double.IsFinite(outerRadius)
                && innerRadius > 0 && outerRadius > innerRadius
                ? new DvdImageMetadata(totalSectors, innerRadius, outerRadius, linear, quadratic, cubic,
                    filesAtOuterRing, drawingStart, drawingStart + drawingSectors, hasFileSystem, readCompatibility,
                    usableSectors, sourceImageOuterRadiusMm, readCompatibilityMode)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static uint GetUInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint result)
            ? result
            : 0u;

    private static double GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result)
            ? result
            : 0;

    private static bool TryGetOptionalUInt32(JsonElement element, string name, uint fallback, out uint result)
    {
        result = fallback;
        return !element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out result);
    }

    private static double GetPitchCoefficient(JsonElement element, string name)
        => !element.TryGetProperty(name, out JsonElement value) ? 0
            : value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) ? result : double.NaN;
}
