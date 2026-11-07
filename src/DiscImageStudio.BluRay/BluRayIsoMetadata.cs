using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscImageStudio.Core;

namespace DiscImageStudio.BluRay;

/// <summary>Sidecar next to a generated BD ISO: the geometry, drawing bounds, polarity and
/// tone needed to reopen the produced image without the original project. Written by the
/// generation module so CLI and GUI outputs carry the same recovery data.</summary>
public sealed record BluRayIsoMetadata(
    string SourceImage,
    string OutputIso,
    long IsoBytes,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    bool InvertMapping,
    ToneProfile Tone,
    uint DrawingStartSector,
    uint DrawingEndSectorExclusive,
    double DrawingStartRadiusMm,
    double DrawingEndRadiusMm,
    string VolumeLabel,
    string? DataDirectory,
    DateTimeOffset? UdfTimestamp,
    DateTimeOffset GeneratedUtc,
    bool FilesAtOuterRing = false,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    uint? UsableSectors = null)
{
    public const string FormatName = "bd-r-image-v1";

    [JsonPropertyName("format")]
    public string Format { get; init; } = FormatName;

    [JsonIgnore]
    public uint OutputSectors => UsableSectors ?? TotalSectors;

    [JsonIgnore]
    public long ContentLength => checked((long)OutputSectors * BluRayDiscParameters.BytesPerSector);

    public static BluRayIsoMetadata FromPlan(BluRayIsoPlan plan, string sourceImage,
        string outputIso, bool invertMapping, ToneProfile tone, DateTimeOffset generatedUtc)
        => new(
            Path.GetFullPath(sourceImage),
            Path.GetFullPath(outputIso),
            plan.ContentLength,
            plan.Disc.TotalSectors,
            plan.Disc.InnerRadiusMm,
            plan.Disc.OuterRadiusMm,
            invertMapping,
            tone.Normalized(),
            plan.DrawingStartSector,
            plan.DrawingEndSectorExclusive,
            plan.DrawingStartRadiusMm,
            plan.DrawingEndRadiusMm,
            plan.VolumeLabel ?? "BD_IMAGE",
            plan.DataDirectory,
            plan.UdfTimestamp,
            generatedUtc,
            plan.FilesAtOuterRing,
            plan.Disc.PitchLinear, plan.Disc.PitchQuadratic, plan.Disc.PitchCubic, plan.Disc.UsableSectors);

    public static string PathFor(string isoPath) => isoPath + ".json";

    public void Save(string isoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        Validate();
        string path = PathFor(Path.GetFullPath(isoPath));
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine);
    }

    public static BluRayIsoMetadata? TryLoad(string isoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        string path = PathFor(isoPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            BluRayIsoMetadata? metadata = JsonSerializer.Deserialize<BluRayIsoMetadata>(
                File.ReadAllText(path), JsonOptions);
            if (metadata is not { Format: FormatName, TotalSectors: > 0 })
            {
                return null;
            }

            metadata.Validate();
            return metadata;
        }
        catch (Exception exception) when (exception is JsonException
                                              or IOException
                                              or NotSupportedException
                                              or InvalidDataException)
        {
            return null;
        }
    }

    private void Validate()
    {
        if (TotalSectors == 0 || TotalSectors > BluRayDiscParameters.SectorsPerLayer
            || TotalSectors % BluRayDiscParameters.SectorsPerCluster != 0)
        {
            throw new InvalidDataException("BD sidecar sectors must be a 32-aligned single-layer size.");
        }

        if (OutputSectors == 0 || OutputSectors > TotalSectors || OutputSectors % BluRayDiscParameters.SectorsPerCluster != 0)
            throw new InvalidDataException("BD sidecar output sectors must be a 32-aligned range within the geometry.");
        if (!double.IsFinite(InnerRadiusMm) || !double.IsFinite(OuterRadiusMm)
            || InnerRadiusMm <= 0 || OuterRadiusMm <= InnerRadiusMm || OuterRadiusMm > 60)
        {
            throw new InvalidDataException("BD sidecar radii are outside the supported data zone.");
        }

        if (DrawingStartSector >= DrawingEndSectorExclusive
            || DrawingEndSectorExclusive > OutputSectors)
        {
            throw new InvalidDataException("BD sidecar drawing bounds are invalid.");
        }

        Tone.Validate("tone");
        try
        {
            DvdTrackGeometry.ValidatePitchCoefficients(PitchLinear, PitchQuadratic, PitchCubic);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("BD sidecar pitch coefficients are invalid.", exception);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
