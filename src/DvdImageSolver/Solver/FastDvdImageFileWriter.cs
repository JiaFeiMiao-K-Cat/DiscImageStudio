using System.Text.Json;
using DiscImageStudio.Core;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

/// <summary>File generation shared by the typed DVD service and the CLI adapter.
/// Exceptions and progress remain in-process; this engine never writes to Console.</summary>
public static class FastDvdImageFileWriter
{
    public static FastDispersionWriteResult Write(
        string imagePath, string outputPath, MultiBlockSolveOptions options,
        HybridIsoImageWriter.LayoutPlan? layout = null,
        Action<FastDispersionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        FastDispersionImageWriter.Validate(options);
        if (options.PreserveExistingIso != (layout is not null))
            throw new ArgumentException("A hybrid file request must include its filesystem layout.", nameof(layout));
        HybridIsoWriteSummary? hybrid = layout is null ? null
            : HybridIsoImageWriter.Create(layout, outputPath, cancellationToken);
        return WritePrepared(imagePath, outputPath, options, hybrid, progress, cancellationToken);
    }

    internal static FastDispersionWriteResult WritePrepared(
        string imagePath, string outputPath, MultiBlockSolveOptions options,
        HybridIsoWriteSummary? hybrid, Action<FastDispersionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        FastDispersionWriteResult result = FastDispersionImageWriter.Write(
            imagePath, outputPath, options, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ImageMappingOptions imageMapping = options.ImageMapping;
        var metadata = new
        {
            format = "dvd-nrzi-disc-fill-v2",
            sourceImage = Path.GetFullPath(imagePath),
            isoOutput = Path.GetFullPath(outputPath),
            startLba = options.StartLba,
            filesAtOuterRing = hybrid?.FilesAtOuterRing ?? false,
            fillSectors = options.FillSectors,
            totalSectors = imageMapping.TotalSectors,
            psnOffset = $"0x{options.PsnOffset:X}",
            blockCount = result.CompletedBlocks,
            algorithm = "dispersion",
            fastOutput = true,
            readCompatibility = options.ReadCompatibility ? options.ReadCompatibilityMode : DvdReadCompatibilityModes.Off,
            compatibilityChanges = result.CompatibilityChanges,
            fastParallelism = options.FastParallelism == 0 ? Environment.ProcessorCount : options.FastParallelism,
            exactEccEfmReplay = false,
            finalBoundary = (object?)null,
            dispersionPool = DispersionPaletteMetadata(),
            constraintStep = imageMapping.SampleEveryChannelBits,
            constraintStepUsed = false,
            totalScore = options.ReadCompatibility ? (long?)null : 0,
            totalScoreMaximum = result.ControlledWords,
            scoreMetric = "payload-transition-class-error",
            normalizedScore = options.ReadCompatibility ? (double?)null : 0.0,
            elapsedSeconds = result.Elapsed.TotalSeconds,
            imageMapping,
            pitchPolynomial = DescribePitchPolynomial(imageMapping),
            iso = new
            {
                mode = hybrid?.Mode ?? "raw-sector-image",
                logicalSectorBytes = IsoImageWriter.LogicalSectorBytes,
                imageSectors = result.IsoSectors,
                sparseFile = result.SparseIso,
                mountableFilesystem = hybrid is not null,
                hybrid,
            },
        };
        File.WriteAllText(outputPath + ".json", JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
        return result;
    }

    internal static object DescribePitchPolynomial(ImageMappingOptions options)
    {
        double length = options.TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * options.ChannelBitLengthNm * 1e-6;
        double scale = DvdTrackGeometry.Create(options.InnerRadiusMm, options.OuterRadiusMm,
            length, options.PitchLinear, options.PitchQuadratic, options.PitchCubic).TrackPitchMm * 1000;
        return new
        {
            variable = "x=(radiusMm-innerRadiusMm)/(outerRadiusMm-innerRadiusMm)",
            unit = "micrometres",
            constant = scale,
            linear = scale * options.PitchLinear,
            quadratic = scale * options.PitchQuadratic,
            cubic = scale * options.PitchCubic,
        };
    }

    internal static object DispersionPaletteMetadata() => new
    {
        domain = "scrambled-main-data-byte-at-efmplus-input",
        blackByte = $"0x{DvdGrayscalePalette.BlackScrambledByte:X2}",
        whiteByte = $"0x{DvdGrayscalePalette.WhiteScrambledByte:X2}",
        blackTransitionsPerCodeword = 5,
        whiteTransitionsPerCodeword = 2,
        luminanceCalibration = "nominal-black-white-not-photometric-measurement",
        rasterSampling = "nearest-for-opaque-binary-grayscale-otherwise-alpha-weighted-bilinear",
        levels = Enumerable.Range(0, 2).Select(level => new
        {
            inputMinimum = Enumerable.Range(0, 256).First(value => BinaryGrayscale.GetLevelIndex((byte)value) == level),
            inputMaximum = Enumerable.Range(0, 256).Last(value => BinaryGrayscale.GetLevelIndex((byte)value) == level),
            previewLuminance = BinaryGrayscale.GetLuminance(level),
            scrambledByte = $"0x{DvdGrayscalePalette.GetScrambledByteForLevel(level):X2}",
            transitionsPerCodeword = level == 0 ? 5 : 2,
        }).ToArray(),
    };
}
