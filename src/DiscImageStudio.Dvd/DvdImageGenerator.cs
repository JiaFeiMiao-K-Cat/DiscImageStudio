using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiscImageStudio.Iso;
using DvdImageSolver.Solver;

namespace DiscImageStudio.Dvd;

public sealed record DvdImageRequest(
    string SourceImage,
    string OutputPath,
    DvdStreamingOptions Options,
    string? DataDirectory = null,
    string VolumeLabel = "DISC_IMAGE",
    bool FilesAtOuterRing = false,
    string? DataImagePath = null);

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
        string? dataDirectory = request.DataDirectory?.Trim();
        string? dataImagePath = request.DataImagePath?.Trim();
        if (!string.IsNullOrEmpty(dataDirectory) && !string.IsNullOrEmpty(dataImagePath))
            throw new ArgumentException("Choose either a data folder or an image source, not both.", nameof(request));
        bool rawIso = !string.IsNullOrEmpty(dataImagePath);
        bool hybrid = !string.IsNullOrEmpty(dataDirectory);
        if (!hybrid && !rawIso && request.FilesAtOuterRing)
            throw new ArgumentException("Outer-ring file placement requires a data directory.", nameof(request));
        string outputPath = Path.GetFullPath(request.OutputPath);
        RawIsoImageInfo? rawSource = null;
        if (rawIso)
        {
            rawSource = RawIsoImage.Inspect(dataImagePath!, request.Options.OutputSectors);
            if (rawSource.Path.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output must not overwrite the source ISO.", nameof(request));
        }
        progress?.Report(new("dvd", "prepare", "Preparing DVD image…"));
        DvdHybridStreamingPlan? plan = hybrid ? DvdStreamingGenerator.PrepareHybrid(
            dataDirectory!, request.Options, request.VolumeLabel, request.FilesAtOuterRing) : null;
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
        if (rawSource is { } sourceImage)
        {
            RawIsoImage.CopyPrefixToFile(sourceImage.Path, outputPath, request.Options.OutputSectors,
                cancellationToken);
            UpdateRawImageMetadata(outputPath, checked((uint)sourceImage.SectorCount), request.Options.OutputSectors);
            cancellationToken.ThrowIfCancellationRequested();
        }
        string summary = $"DVD image completed: {result.CompletedBlocks} blocks, {result.ControlledWords} controlled words.";
        if (rawIso)
        {
            summary += $" Source ISO sectors preserved at LBA 0; artwork occupies only the remaining sectors.";
        }
        progress?.Report(new("dvd", "complete", summary, 1,
            (long)result.FilledSectors * 2048, (long)result.FilledSectors * 2048, result.Elapsed));
        return new DiscImageResult(outputPath, summary);
    }

    private static void UpdateRawImageMetadata(string outputPath, uint sourceSectors, uint totalSectors)
    {
        string metadataPath = outputPath + ".json";
        JsonObject metadata = JsonNode.Parse(File.ReadAllText(metadataPath))?.AsObject()
            ?? throw new InvalidDataException("DVD generator did not produce image metadata.");
        metadata["startLba"] = sourceSectors;
        metadata["fillSectors"] = totalSectors - sourceSectors;
        metadata["sourceImageSectors"] = sourceSectors;
        File.WriteAllText(metadataPath, metadata.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }) + Environment.NewLine);
    }
}
