using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>A CD audio or data-image export, shared by graphical and command-line hosts.
/// With <see cref="MusicTracks"/>, the export is the picture session of a CD-Extra disc:
/// the music forms session 1 and the output ISO is session 2.</summary>
public sealed record CdImageRequest(
    string SourceImage,
    string OutputPath,
    CdDiscParameters Parameters,
    ToneProfile Tone,
    bool Interleave = true,
    bool WriteCue = true,
    string? DataDirectory = null,
    string VolumeLabel = "CD_IMAGE",
    bool FilesAtOuterRing = false,
    IReadOnlyList<string>? MusicTracks = null,
    long? Session2StartLba = null);

public static class CdImageGenerator
{
    /// <summary>Writes the image and recovery sidecars, preserving exceptions for the host.</summary>
    public static DiscImageResult Generate(CdImageRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        cancellationToken.ThrowIfCancellationRequested();
        request.Tone.Validate();
        if (request.MusicTracks is { Count: > 0 } music)
            return GenerateCdExtra(request, music, progress, cancellationToken);
        string? dataDirectory = request.DataDirectory?.Trim();
        if (!string.IsNullOrEmpty(dataDirectory))
        {
            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(dataDirectory, request.Parameters,
                request.VolumeLabel, request.FilesAtOuterRing);
            CdGenerationSummary dataSummary = CdDataImageGenerator.Generate(request.SourceImage,
                request.OutputPath, request.Parameters, plan,
                value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                    $"CD data progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                    value.TotalBytes, value.Elapsed)), cancellationToken, request.Tone);
            return new DiscImageResult(dataSummary.OutputTrack,
                $"CD data image complete: {plan.FileCount} files, {plan.TotalSectors} sectors, "
                + $"files={(request.FilesAtOuterRing ? "outer" : "inner")}, output={dataSummary.OutputTrack}");
        }

        CdGenerationSummary summary = CdTrackGenerator.Generate(request.SourceImage, request.OutputPath,
            request.Parameters, request.Interleave, request.WriteCue,
            value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                $"CD progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                value.TotalBytes, value.Elapsed)), cancellationToken, request.Tone);
        string message =
            $"CD track complete: {summary.Sectors} sectors, {summary.BytesWritten} bytes, "
            + $"interleave={summary.Interleaved}, elapsed={summary.Elapsed.TotalSeconds:F1}s, "
            + $"output={summary.OutputTrack}"
            + (summary.CueSheetPath is null ? string.Empty : $", cue={summary.CueSheetPath}");
        return new DiscImageResult(summary.OutputTrack, message);
    }

    /// <summary>Plans the CD-Extra layout for the music and writes the picture session as an
    /// ISO whose extents start at session 2. Without a measured address the start follows
    /// the Track-at-Once prediction; an external burner must report the same next session
    /// address (for example cdrecord -msinfo) or the export must be repeated with it.</summary>
    public static CdExtraLayout PlanCdExtra(CdDiscParameters parameters, IReadOnlyList<string> musicTracks,
        long? session2StartLba = null)
    {
        ArgumentNullException.ThrowIfNull(musicTracks);
        List<long> sectors = new(musicTracks.Count);
        foreach (string track in musicTracks) sectors.Add(CdMusicFile.Inspect(track).RecordedSectors);
        return CdExtraLayout.Plan(parameters, sectors, session2StartLba);
    }

    private static DiscImageResult GenerateCdExtra(CdImageRequest request, IReadOnlyList<string> music,
        IProgress<DiscJobProgress>? progress, CancellationToken cancellationToken)
    {
        List<CdMusicTrackInfo> tracks = new(music.Count);
        foreach (string path in music)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracks.Add(CdMusicFile.Inspect(path));
        }

        CdExtraLayout layout = CdExtraLayout.Plan(request.Parameters,
            tracks.Select(track => track.RecordedSectors).ToArray(), request.Session2StartLba);
        CdDataLayoutPlan plan = CdDataImageGenerator.PrepareSessionLayout(request.Parameters,
            layout.Session2StartLba, layout.DataTrackSectors, request.DataDirectory, request.VolumeLabel,
            request.FilesAtOuterRing);
        CdGenerationSummary summary = CdDataImageGenerator.Generate(request.SourceImage,
            request.OutputPath, request.Parameters, plan,
            value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                $"CD-Extra picture session progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                value.TotalBytes, value.Elapsed)), cancellationToken, request.Tone);
        CdExtraImageMetadata.Save(summary.OutputTrack, CdExtraImageMetadata.Create(layout, plan, request.Tone));
        TimeSpan duration = TimeSpan.FromSeconds(tracks.Sum(track => track.Duration.TotalSeconds));
        string address = layout.Session2StartMeasured
            ? $"session 2 at measured LBA {layout.Session2StartLba}"
            : $"session 2 at predicted LBA {layout.Session2StartLba} (Track-at-Once; check cdrecord -msinfo)";
        return new DiscImageResult(summary.OutputTrack,
            $"CD-Extra picture session complete: {tracks.Count} audio tracks ({duration:hh\\:mm\\:ss}), "
            + $"{address}, {plan.TrackSectors} sectors, picture {layout.PictureAreaFraction:P1} of the disc area, "
            + $"output={summary.OutputTrack}");
    }
}
