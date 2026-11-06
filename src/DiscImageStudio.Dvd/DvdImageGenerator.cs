using DiscImageStudio.Core;
using DvdImageSolver.Solver;

namespace DiscImageStudio.Dvd;

public sealed record DvdImageRequest(
    string SourceImage,
    string OutputPath,
    DvdStreamingOptions Options,
    string? DataDirectory = null,
    string VolumeLabel = "DISC_IMAGE",
    bool FilesAtOuterRing = false);

/// <summary>Generates the normal drawing ISO and its compatible solve sidecar.
/// CLI argument parsing and exit-code handling are deliberately outside this API.</summary>
public static class DvdImageGenerator
{
    public static DiscImageResult Generate(DvdImageRequest request,
        IProgress<DiscJobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentNullException.ThrowIfNull(request.Options);
        request.Options.Validate();
        bool hybrid = !string.IsNullOrWhiteSpace(request.DataDirectory);
        if (!hybrid && request.FilesAtOuterRing)
            throw new ArgumentException("Outer-ring file placement requires a data directory.", nameof(request));
        string outputPath = Path.GetFullPath(request.OutputPath);
        progress?.Report(new("dvd", "prepare", "Preparing DVD image…"));
        DvdHybridStreamingPlan? plan = hybrid ? DvdStreamingGenerator.PrepareHybrid(
            request.DataDirectory!, request.Options, request.VolumeLabel, request.FilesAtOuterRing) : null;
        cancellationToken.ThrowIfCancellationRequested();
        MultiBlockSolveOptions options = DvdStreamingGenerator.CreateSolveOptions(request.Options,
            plan?.DrawingStartLba ?? 0, plan?.DrawingSectors ?? request.Options.TotalSectors);
        // The historical fast CLI records 64 even though this setting is not used
        // by dispersion. Keep the sidecar schema and values identical.
        options = options with
        {
            PreserveExistingIso = hybrid,
            ImageMapping = options.ImageMapping with { SampleEveryChannelBits = 64 },
        };
        FastDispersionWriteResult result = FastDvdImageFileWriter.Write(request.SourceImage,
            outputPath, options, plan?.IsoLayout, value => progress?.Report(new(
                "dvd", "generate", $"Generating DVD image: {value.CompletedBlocks}/{value.TotalBlocks} blocks",
                (double)value.CompletedBlocks / value.TotalBlocks,
                (long)value.CompletedBlocks * 32768, (long)value.TotalBlocks * 32768, value.Elapsed)),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string summary = $"DVD image completed: {result.CompletedBlocks} blocks, {result.ControlledWords} controlled words.";
        progress?.Report(new("dvd", "complete", summary, 1,
            (long)result.FilledSectors * 2048, (long)result.FilledSectors * 2048, result.Elapsed));
        return new DiscImageResult(outputPath, summary);
    }
}
