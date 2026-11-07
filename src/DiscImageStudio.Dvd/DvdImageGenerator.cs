using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using System.Text.Json;
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
    /// <summary>Writes a frozen calibration target and the metadata needed to interpret
    /// its drawing bytes. The caller can stage these files together with its calibration record.</summary>
    public static DvdStreamingSummary GenerateCalibration(CalibrationTarget target, string outputPath,
        DvdStreamingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Parameters.Kind != CalibrationDiscKind.Dvd || target.Parameters.Sectors != options.TotalSectors)
            throw new ArgumentException("Calibration target and output must describe the same DVD sector capacity.", nameof(target));
        options = options with
        {
            ReadCompatibility = target.Parameters.DvdReadCompatibility,
            ReadCompatibilityMode = target.EffectiveDvdReadCompatibilityMode,
        };
        options.Validate();
        string path = Path.GetFullPath(outputPath);
        DvdStreamingSummary summary;
        using (FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            summary = DvdStreamingGenerator.GeneratePattern(target, output, options, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = new
        {
            format = "dvd-calibration-payload-v1",
            sourceImage = "calibration",
            startLba = 0,
            fillSectors = options.OutputSectors,
            totalSectors = options.TotalSectors,
            readCompatibility = options.ReadCompatibility ? options.ReadCompatibilityMode : DvdReadCompatibilityModes.Off,
            compatibilityChanges = summary.CompatibilityChanges,
            imageMapping = DvdStreamingGenerator.CreateSolveOptions(options, 0, options.OutputSectors).ImageMapping,
        };
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
        return summary;
    }

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
            plan?.DrawingStartLba ?? 0, plan?.DrawingSectors ?? request.Options.OutputSectors);
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
