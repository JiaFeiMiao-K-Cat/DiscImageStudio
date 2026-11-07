using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Cd;

/// <summary>
/// Generation parameters recorded next to a produced track as <c>&lt;track&gt;.json</c>.
/// The live preview reads them back so a track made by an earlier run, or imported from another
/// machine, is read back with the geometry and interleave it was actually generated with.
/// </summary>
public sealed record CdTrackMetadata(
    string SourceImage,
    string OutputTrack,
    double InnerRadiusMm,
    double OuterRadiusMm,
    long Sectors,
    double LinearVelocityMmPerSecond,
    double StartAngleDegrees,
    double ImageOuterRadiusMm,
    bool Interleaved,
    double ToneDitherStrength = 0,
    int ToneLevels = 2,
    double ToneBrightness = 0,
    bool ToneSrgbLinearization = false,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    long? UsableSectors = null,
    double? SourceImageOuterRadiusMm = null)
{
    internal const string FormatName = "cd-da-track-v1";

    /// <summary>Serialised file marker; the reader rejects anything else at that path.</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = FormatName;

    public CdDiscParameters CreateParameters() => new(
        InnerRadiusMm, OuterRadiusMm, Sectors, LinearVelocityMmPerSecond,
        StartAngleDegrees * Math.PI / 180.0, ImageOuterRadiusMm,
        PitchLinear, PitchQuadratic, PitchCubic, UsableSectors, SourceImageOuterRadiusMm);

    /// <summary>Reads the sidecar of a track, or null when it is absent or unreadable.</summary>
    public static CdTrackMetadata? TryLoad(string trackPath)
    {
        string path = PathFor(trackPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            CdTrackMetadata? metadata = JsonSerializer.Deserialize<CdTrackMetadata>(
                File.ReadAllText(path),
                JsonOptions);
            return metadata is { Format: FormatName, Sectors: > 0 } ? metadata : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string PathFor(string trackPath) => trackPath + ".json";

    internal static void Save(string trackPath, CdTrackMetadata metadata)
        => File.WriteAllText(
            PathFor(trackPath),
            JsonSerializer.Serialize(metadata, JsonOptions) + Environment.NewLine);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
