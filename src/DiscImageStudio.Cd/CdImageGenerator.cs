using DiscImageStudio.Core;
using DiscImageStudio.Iso;

namespace DiscImageStudio.Cd;

/// <summary>A CD audio or data-image export, shared by graphical and command-line hosts.</summary>
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
    string? DataImagePath = null);

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
        string? dataDirectory = request.DataDirectory?.Trim();
        string? dataImagePath = request.DataImagePath?.Trim();
        if (!string.IsNullOrEmpty(dataDirectory) && !string.IsNullOrEmpty(dataImagePath))
            throw new ArgumentException("Choose either a data folder or an image source, not both.", nameof(request));
        if (!string.IsNullOrEmpty(dataImagePath)
            && Path.GetExtension(dataImagePath).Equals(".cue", StringComparison.OrdinalIgnoreCase))
        {
            string cueOutput = CueBinImage.Export(dataImagePath, request.OutputPath, cancellationToken);
            return new DiscImageResult(cueOutput,
                $"CD raw tracks exported from CUE/BIN: {cueOutput}. Track data is unchanged; artwork is not merged.");
        }

        if (!string.IsNullOrEmpty(dataImagePath))
        {
            CdDataLayoutPlan rawPlan = CdDataImageGenerator.PrepareRawImageLayout(dataImagePath, request.Parameters);
            CdGenerationSummary rawSummary = CdDataImageGenerator.Generate(request.SourceImage,
                request.OutputPath, request.Parameters, rawPlan,
                value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                    $"CD data progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                    value.TotalBytes, value.Elapsed)), cancellationToken, request.Tone);
            return new DiscImageResult(rawSummary.OutputTrack,
                $"CD ISO source preserved at LBA 0: {rawPlan.SourceSectors} sectors; "
                + $"{rawPlan.DrawingSectors} sectors remain for artwork.");
        }

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
}
