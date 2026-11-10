using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>A CD audio or data-image export, shared by graphical and command-line hosts.
/// With <see cref="MusicTracks"/>, the export is the picture session of a CD-Extra disc:
/// the music forms session 1 and the output ISO is session 2. With
/// <see cref="MusicAtOuterRing"/> it is instead one WAV for a Disc-at-Once audio disc, the
/// picture in the pre-gap of track 1 and the music behind it, with a CUE sheet.</summary>
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
    long? Session2StartLba = null,
    bool MusicAtOuterRing = false);

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
        {
            return request.MusicAtOuterRing
                ? GenerateHiddenPicture(request, music, progress, cancellationToken)
                : GenerateCdExtra(request, music, progress, cancellationToken);
        }
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

    /// <summary>Plans a disc with the music on the outer rings from the music files.</summary>
    public static CdExtraLayout PlanOuterMusic(CdDiscParameters parameters, IReadOnlyList<string> musicTracks)
    {
        ArgumentNullException.ThrowIfNull(musicTracks);
        List<long> sectors = new(musicTracks.Count);
        foreach (string track in musicTracks) sectors.Add(CdMusicFile.Inspect(track).RecordedSectors);
        return CdExtraLayout.PlanOuterMusic(parameters, sectors);
    }

    /// <summary>Generator parameters for the picture of an outer-music disc: the output ends
    /// where the music starts, and the source-image scale is pinned so that limit does not
    /// rescale the picture.</summary>
    public static CdDiscParameters HiddenPictureParameters(CdDiscParameters parameters, CdExtraLayout layout)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(layout);
        if (!layout.MusicAtOuterRing)
            throw new ArgumentException("The layout keeps the music on the inner rings.", nameof(layout));
        return parameters with
        {
            UsableSectors = layout.PictureSectors,
            SourceImageOuterRadiusMm = parameters.SamplingImageOuterRadiusMm,
        };
    }

    /// <summary>Writes the digital silence that ends the pre-gap of an outer-music disc,
    /// between the picture and index 1 of track 1 (see
    /// <see cref="CdExtraLayout.HiddenPictureGuardSectors"/>).</summary>
    public static void WriteHiddenPictureGuard(CdExtraLayout layout, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(output);
        if (!layout.MusicAtOuterRing)
            throw new ArgumentException("The layout keeps the music on the inner rings.", nameof(layout));
        byte[] silence = new byte[CdDiscParameters.BytesPerSector];
        for (long sector = layout.PictureEndLbaExclusive; sector < layout.MusicStartLba; sector++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(silence);
        }
    }

    /// <summary>Writes one WAV for a Disc-at-Once audio disc: the picture and a silent guard
    /// as the pre-gap (index 0) of track 1, then the music tracks without gaps, and a CUE
    /// sheet that starts track 1 at the music. Players begin at index 1, so the picture is not played
    /// unless a listener rewinds before the first track.</summary>
    private static DiscImageResult GenerateHiddenPicture(CdImageRequest request, IReadOnlyList<string> music,
        IProgress<DiscJobProgress>? progress, CancellationToken cancellationToken)
    {
        if (request.Session2StartLba is not null)
            throw new ArgumentException("A session 2 start applies only to music on the inner rings (CD-Extra).");
        if (!string.IsNullOrWhiteSpace(request.DataDirectory))
        {
            throw new ArgumentException("Disc files cannot be combined with music on the outer rings: the disc is "
                + "an audio disc. Put the music on the inner rings (CD-Extra) to add files.");
        }

        string output = Path.GetFullPath(request.OutputPath);
        if (!Path.GetExtension(output).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Music on the outer rings is written as one .wav with a CUE sheet; "
                + "use the inner position for a CD-Extra picture session .iso.", nameof(request));
        }

        string source = Path.GetFullPath(request.SourceImage);
        if (source.Equals(output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output must not overwrite the source image.", nameof(request));

        List<CdMusicTrackInfo> tracks = new(music.Count);
        foreach (string path in music)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFullPath(path).Equals(output, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output must not overwrite a music file.", nameof(request));
            tracks.Add(CdMusicFile.Inspect(path));
        }

        CdExtraLayout layout = CdExtraLayout.PlanOuterMusic(request.Parameters,
            tracks.Select(track => track.RecordedSectors).ToArray());
        CdDiscParameters picture = HiddenPictureParameters(request.Parameters, layout);
        long totalBytes = checked(layout.MusicEndLbaExclusive * CdDiscParameters.BytesPerSector);
        CdWaveFile.ValidateLength(totalBytes);
        ToneProfile tone = request.Tone.Normalized();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (FileStream file = new(output, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.SequentialScan))
        {
            CdWaveFile.WriteHeader(file, totalBytes);
            CdTrackGenerator.GenerateToStream(source, file, picture, request.Interleave,
                value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                    $"CD picture progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                    value.TotalBytes, value.Elapsed)), cancellationToken, output, CdAudioByteOrder.LittleEndian, tone);
            WriteHiddenPictureGuard(layout, file, cancellationToken);
            foreach (CdMusicTrackInfo track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CdMusicFile.WritePaddedPcm(track, file, cancellationToken);
            }
        }

        // The sidecar describes the whole file, so the read-back preview shows the disc as
        // recorded: the picture and the music ring around it.
        CdDiscParameters parameters = request.Parameters;
        CdTrackMetadata.Save(output, new CdTrackMetadata(source, output, parameters.InnerRadiusMm,
            parameters.OuterRadiusMm, parameters.Sectors, parameters.LinearVelocityMmPerSecond,
            parameters.StartAngleRadians * 180.0 / Math.PI, parameters.ImageOuterRadiusMm, request.Interleave,
            tone.DitherStrength, tone.Levels, tone.Brightness, tone.SrgbLinearization, parameters.PitchLinear,
            parameters.PitchQuadratic, parameters.PitchCubic, layout.MusicEndLbaExclusive,
            picture.SourceImageOuterRadiusMm));
        string? cue = request.WriteCue
            ? CdWaveFile.WriteCue(output, layout.Tracks.Select(track => track.StartLba).ToArray())
            : null;
        TimeSpan duration = TimeSpan.FromSeconds(tracks.Sum(track => track.Duration.TotalSeconds));
        return new DiscImageResult(output,
            $"CD picture + music track complete: picture in the pre-gap of track 1 ({layout.PictureSectors} sectors, "
            + $"{layout.PictureAreaFraction:P1} of the disc area), {tracks.Count} audio tracks ({duration:hh\\:mm\\:ss}) "
            + $"from LBA {layout.MusicStartLba}, {layout.MusicEndLbaExclusive} sectors, output={output}"
            + (cue is null ? string.Empty : $", cue={cue}"));
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
