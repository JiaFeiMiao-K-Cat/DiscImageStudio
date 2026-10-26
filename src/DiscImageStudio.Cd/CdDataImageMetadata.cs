using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>Geometry and protected filesystem extents of a cooked Mode 1 ISO.</summary>
public sealed record CdDataImageMetadata(
    CdDiscParameters Parameters,
    uint DrawingStartLba,
    uint DrawingEndLbaExclusive,
    bool FilesAtOuterRing,
    ToneProfile Tone)
{
    [JsonRequired]
    public string Format { get; init; } = "cd-rom-mode1-v1";

    public static CdDataImageMetadata? TryLoad(string isoPath)
    {
        try
        {
            CdDataImageMetadata? metadata = JsonSerializer.Deserialize<CdDataImageMetadata>(
                File.ReadAllText(isoPath + ".json"), JsonOptions);
            if (metadata is not { Format: "cd-rom-mode1-v1", Parameters: not null }) return null;
            metadata.Parameters.Validate();
            metadata.Tone.Validate();
            return metadata.DrawingStartLba < metadata.DrawingEndLbaExclusive
                && metadata.DrawingEndLbaExclusive <= metadata.Parameters.Sectors ? metadata : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException
            or OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    internal static void Save(string path, CdDataImageMetadata metadata)
        => File.WriteAllText(path + ".json", JsonSerializer.Serialize(metadata, JsonOptions) + Environment.NewLine);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
