using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using System.Text.Json;
using DvdImageSolver;
using DvdImageSolver.Encoding;
using DvdImageSolver.Solver;

namespace DiscImageStudio.Dvd;

public sealed record DvdImageRequest(
    string SourceImage,
    string OutputPath,
    DvdStreamingOptions Options,
    string? DataDirectory = null,
    string VolumeLabel = "DISC_IMAGE",
    bool FilesAtOuterRing = false,
    bool FilesInBothRings = false,
    uint? LayerBreakLba = null,
    uint? DvdMinusRTotalSectors = null);

/// <summary>Generates the normal drawing ISO and its compatible solve sidecar.
/// CLI argument parsing and exit-code handling are deliberately outside this API.</summary>
public static class DvdImageGenerator
{
    public const uint MaxDvdPlusRLayer0Sectors = HybridIsoImageWriter.MaxDvdPlusRLayer0Sectors;
    public const uint DualLayerCalibrationStartLba = 32;

    /// <summary>Writes a frozen calibration target and the metadata needed to interpret
    /// its drawing bytes. The caller can stage these files together with its calibration record.</summary>
    public static DvdStreamingSummary GenerateCalibration(CalibrationTarget target, string outputPath,
        DvdStreamingOptions options, CancellationToken cancellationToken = default, string? layer1Directory = null)
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
        target.Parameters.Validate();
        uint? layerBreak = target.Parameters.DvdLayerBreakLba;
        uint start = layerBreak.HasValue ? checked((uint)target.Parameters.DrawingStartSector) : 0;
        HybridIsoImageWriter.LayoutPlan? layout = layerBreak.HasValue
            ? PlanDualLayerCalibration(target, options, layer1Directory) : null;
        string path = Path.GetFullPath(outputPath);
        DvdStreamingSummary summary;
        using (FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            summary = WriteCalibration(target, options, layout, output, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = new
        {
            format = "dvd-calibration-payload-v1",
            sourceImage = "calibration",
            startLba = start,
            fillSectors = options.OutputSectors - start,
            layerBreakLba = layerBreak,
            dvdMinusRTotalSectors = target.Parameters.DvdMinusRTotalSectors,
            iso = layout is null ? null : new { mountableFilesystem = true, imageSectors = layout.VolumeSectors },
            totalSectors = options.TotalSectors,
            readCompatibility = options.ReadCompatibility ? options.ReadCompatibilityMode : DvdReadCompatibilityModes.Off,
            compatibilityChanges = summary.CompatibilityChanges,
            imageMapping = DvdStreamingGenerator.CreateSolveOptions(options, start, options.OutputSectors - start).ImageMapping,
        };
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
        return summary;
    }

    public static DvdStreamingSummary GenerateDualLayerCalibrationToStream(CalibrationTarget target,
        DvdStreamingOptions options, string layer1Directory, Stream output, CancellationToken cancellationToken = default)
    {
        target.Parameters.Validate();
        options.Validate();
        var layout = PlanDualLayerCalibration(target, options, layer1Directory);
        return WriteCalibration(target, options, layout, output, cancellationToken);
    }

    private static HybridIsoImageWriter.LayoutPlan PlanDualLayerCalibration(CalibrationTarget target,
        DvdStreamingOptions options, string? layer1Directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layer1Directory);
        if (target.Parameters.DvdLayerBreakLba is not uint split || split != options.OutputSectors
            || target.Parameters.Sectors != options.TotalSectors)
            throw new ArgumentException("DVD DL calibration options must match the frozen target.");
        var layout = HybridIsoImageWriter.PlanDvdDoubleLayer(layer1Directory, split, "DVD_CALIBRATION", target.Parameters.DvdMinusRTotalSectors);
        if (layout.MetadataEndLbaExclusive > target.Parameters.DrawingStartSector)
            throw new ArgumentException("DVD DL calibration metadata exceeds the reserved prefix.");
        return layout;
    }

    private static DvdStreamingSummary WriteCalibration(CalibrationTarget target, DvdStreamingOptions options,
        HybridIsoImageWriter.LayoutPlan? layout, Stream output, CancellationToken cancellationToken)
    {
        if (layout is not null)
            HybridIsoImageWriter.WriteOuterRingPrefix(layout, output, checked((uint)target.Parameters.DrawingStartSector), cancellationToken);
        DvdStreamingSummary summary = DvdStreamingGenerator.GeneratePattern(target, output, options, cancellationToken: cancellationToken);
        if (layout is not null)
            HybridIsoImageWriter.WriteOuterRingSuffix(layout, output, options.OutputSectors, cancellationToken);
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
        if (!hybrid && (request.FilesAtOuterRing || request.FilesInBothRings || request.LayerBreakLba.HasValue))
            throw new ArgumentException("The selected file placement requires a data directory.", nameof(request));
        string outputPath = Path.GetFullPath(request.OutputPath);
        progress?.Report(new("dvd", "prepare", "Preparing DVD image…"));
        DvdStreamingOptions streaming = request.Options;
        DvdHybridStreamingPlan? plan = null;
        if (request.LayerBreakLba is uint split)
        {
            var layout = HybridIsoImageWriter.PlanDvdDoubleLayer(request.DataDirectory!, split, request.VolumeLabel, request.DvdMinusRTotalSectors);
            uint blockSize = DvdEccBlockEncoder.SectorCount;
            uint drawingStart = (layout.MetadataEndLbaExclusive + blockSize - 1) / blockSize * blockSize;
            if (split < drawingStart + blockSize)
                throw new ArgumentException("DVD+R DL Layer 0 leaves no complete ECC block for drawing.");
            plan = new DvdHybridStreamingPlan(layout, drawingStart, split);
            streaming = streaming with
            {
                TotalSectors = request.DvdMinusRTotalSectors.HasValue ? split : MaxDvdPlusRLayer0Sectors,
                UsableSectors = split,
            };
        }
        else if (hybrid)
        {
            plan = DvdStreamingGenerator.PrepareHybrid(request.DataDirectory!, streaming, request.VolumeLabel,
                request.FilesAtOuterRing, request.FilesInBothRings);
        }
        cancellationToken.ThrowIfCancellationRequested();
        MultiBlockSolveOptions options = DvdStreamingGenerator.CreateSolveOptions(streaming,
            plan?.DrawingStartLba ?? 0, plan?.DrawingSectors ?? streaming.OutputSectors);
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
            cancellationToken, request.LayerBreakLba);
        cancellationToken.ThrowIfCancellationRequested();
        string summary = $"DVD image completed: {result.CompletedBlocks} blocks, {result.ControlledWords} controlled words.";
        progress?.Report(new("dvd", "complete", summary, 1,
            (long)result.FilledSectors * 2048, (long)result.FilledSectors * 2048, result.Elapsed));
        return new DiscImageResult(outputPath, summary);
    }
}
