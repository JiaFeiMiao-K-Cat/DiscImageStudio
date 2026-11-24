using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>
/// Sidecar of a CD-Extra picture-session ISO. The ISO holds only session 2, which starts
/// at <see cref="Session2StartLba"/>; its filesystem extents are absolute. The audio
/// session is recorded from the listed tracks. A separate format string keeps readers of
/// the single-session "cd-rom-mode1-v1" sidecar from assuming the volume starts at LBA 0.
/// </summary>
public sealed record CdExtraImageMetadata(
    CdDiscParameters Parameters,
    ToneProfile Tone,
    IReadOnlyList<CdExtraAudioTrackLayout> Tracks,
    long MusicEndLbaExclusive,
    long Session1LeadoutLba,
    long Session2StartLba,
    bool Session2StartMeasured,
    long TrackSectors,
    long VolumeSectors,
    uint DrawingStartLba,
    uint DrawingEndLbaExclusive,
    bool FilesAtOuterRing)
{
    public const string CurrentFormat = "cd-extra-v1";

    [JsonRequired]
    public string Format { get; init; } = CurrentFormat;

    /// <summary>Sector format of the picture track as written by the drive.</summary>
    public string SectorFormat { get; init; } = "mode1";

    public static CdExtraImageMetadata Create(CdExtraLayout layout, CdDataLayoutPlan plan, ToneProfile tone)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.VolumeStartLba != layout.Session2StartLba)
            throw new ArgumentException("The data plan does not start at the layout's session 2 address.", nameof(plan));
        return new CdExtraImageMetadata(layout.Parameters, tone.Normalized(), layout.Tracks,
            layout.MusicEndLbaExclusive, layout.Session1LeadoutLba, layout.Session2StartLba,
            layout.Session2StartMeasured, plan.TrackSectors, plan.VolumeSectors, plan.DrawingStartLba,
            plan.DrawingEndLbaExclusive, plan.FilesAtOuterRing);
    }

    public static CdExtraImageMetadata? TryLoad(string isoPath)
    {
        try
        {
            string path = isoPath + ".json";
            if (!File.Exists(path)) return null;
            CdExtraImageMetadata? metadata = JsonSerializer.Deserialize<CdExtraImageMetadata>(
                File.ReadAllText(path), JsonOptions);
            if (metadata is not { Format: CurrentFormat, Parameters: not null, Tracks.Count: > 0 }
                || metadata.SectorFormat != "mode1") return null;
            metadata.Parameters.Validate();
            metadata.Tone.Validate();
            bool valid = metadata.MusicEndLbaExclusive > 0
                && metadata.Session1LeadoutLba >= metadata.MusicEndLbaExclusive
                && metadata.Session2StartLba > metadata.Session1LeadoutLba
                && metadata.VolumeSectors > 0 && metadata.TrackSectors >= metadata.VolumeSectors
                && metadata.DrawingStartLba >= metadata.Session2StartLba
                && metadata.DrawingStartLba < metadata.DrawingEndLbaExclusive
                && metadata.DrawingEndLbaExclusive <= metadata.Session2StartLba + metadata.TrackSectors
                && metadata.Session2StartLba + metadata.TrackSectors <= metadata.Parameters.OutputSectors;
            return valid ? metadata : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException
            or OverflowException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static void Save(string isoPath, CdExtraImageMetadata metadata)
        => File.WriteAllText(isoPath + ".json", JsonSerializer.Serialize(metadata, JsonOptions) + Environment.NewLine);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
