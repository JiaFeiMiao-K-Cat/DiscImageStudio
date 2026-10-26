using System.Text.Json;
using DiscImageStudio.Core;
using DvdImageSolver.Encoding;
using DvdImageSolver.Solver;

namespace DvdImageSolver;

internal static class Program
{
    private static int Main(string[] args) => RunCommand(args);

    internal static int RunCommand(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "solve" => Solve(CliArguments.Parse(args.AsSpan(1))),
                "encode" => Encode(CliArguments.Parse(args.AsSpan(1))),
                "calibrate" => Calibrate(CliArguments.Parse(args.AsSpan(1))),
                "simulate" => Simulate(CliArguments.Parse(args.AsSpan(1))),
                "selftest" => RunSelfTests(),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static int Solve(CliArguments arguments)
    {
        _ = ReadFilesAtOuterRing(arguments);
        uint fillSectors = arguments.GetUInt("fill-sectors", DvdEccBlockEncoder.SectorCount);
        if (arguments.Has("data-dir")
            || fillSectors != DvdEccBlockEncoder.SectorCount
            || ReadBoolean(arguments, "fast-output", fallback: false))
        {
            return SolveMultipleBlocks(arguments, fillSectors);
        }

        string? outputPath = arguments.Has("output") ? arguments.Require("output") : null;
        string? isoOutputPath = arguments.Has("iso-output") ? arguments.Require("iso-output") : null;
        if (outputPath is null && isoOutputPath is null)
        {
            throw new ArgumentException("Specify --output, --iso-output, or both.");
        }

        if (outputPath is not null
            && isoOutputPath is not null
            && string.Equals(
                Path.GetFullPath(outputPath),
                Path.GetFullPath(isoOutputPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--output and --iso-output must be different files.");
        }

        uint lba = arguments.GetUInt("lba", 0);
        uint psnOffset = arguments.GetUInt("psn-offset", 0x30000);
        int randomSeed = arguments.GetInt("seed", 1);
        ModulationBoundary initial = ReadBoundary(arguments);
        ImageConstraint constraint = ReadConstraint(arguments, lba);
        byte[] initialPayload = LoadOrCreatePayload(arguments, randomSeed);
        SolverAlgorithm algorithm = ReadSolverAlgorithm(arguments);
        ValidateRetiredControlOptions(arguments, algorithm);
        SolverOptions options = new(
            arguments.GetInt("iterations", algorithm switch
            {
                SolverAlgorithm.StateControl => 3,
                SolverAlgorithm.Dispersion => 1,
                _ => 1000,
            }),
            arguments.GetInt("mutation-bytes", 4),
            randomSeed,
            arguments.GetDouble("initial-temperature", 64.0),
            arguments.GetDouble("final-temperature", 0.25),
            algorithm,
            arguments.GetInt("bridge-bytes", 2),
            arguments.GetInt("beam-width", 8),
            arguments.GetInt("retry-beam-width", 32),
            arguments.GetDouble("retry-frame-fraction", 0.25),
            arguments.GetInt("batch-frames", 8),
            arguments.GetInt("batch-candidates", 4),
            arguments.GetInt("search-lanes", 1));

        Console.WriteLine(
            $"ECC Block: LBA {lba}, PSN 0x{checked(lba + psnOffset):X6}, "
            + $"constraints {constraint.ConstrainedBits}, algorithm={AlgorithmName(algorithm)}, "
            + $"iterations {options.Iterations}");
        if (algorithm == SolverAlgorithm.StateControl)
        {
            string adaptive = options.AdaptiveRetryEnabled
                ? $"beam {options.RetryBeamWidth} on failed single frames"
                : "disabled";
            Console.WriteLine(
                $"bridge bytes/edge={options.BridgeBytes}, beam width={options.BeamWidth}, "
                + $"frame fraction/pass={options.RetryFrameFraction:P0}, singleton retry={adaptive}, "
                + $"ECC-closed batch={options.BatchFrames} frames/{options.BatchCandidates} candidates, "
                + $"search lanes={options.SearchLanes}, "
                + "payload candidates=state/level/DSV binary byte pairs, "
                + "scoring=majority error then gray error");
        }
        else if (algorithm == SolverAlgorithm.Dispersion)
        {
            Console.WriteLine(
                DispersionPaletteDescription() + ", "
                + "image sampled at every payload code-word center");
        }
        if (constraint.ImageMapping is { } mapping)
        {
            Console.WriteLine(
                $"image: {mapping.ImageWidth}x{mapping.ImageHeight}, "
                + $"pitch={mapping.TrackPitchMicrometres:F4} um, "
                + $"block radius={mapping.BlockStartRadiusMm:F6}..{mapping.BlockEndRadiusMm:F6} mm");
        }

        if (arguments.Has("constraint-output"))
        {
            string constraintPath = arguments.Require("constraint-output");
            EnsureParentDirectory(constraintPath);
            constraint.SaveText(constraintPath);
        }

        int progressEvery = Math.Max(1, options.Iterations / 20);
        SolverResult result = SolverEngine.Solve(
            constraint,
            initialPayload,
            lba,
            psnOffset,
            initial,
            options,
            (iteration, current, best) =>
            {
                if (iteration == 1 || iteration % progressEvery == 0 || best == 0)
                {
                    Console.WriteLine($"iteration {iteration}: current={current}, best={best}");
                }
            });

        if (outputPath is not null)
        {
            EnsureParentDirectory(outputPath);
            File.WriteAllBytes(outputPath, result.Payloads);
        }

        if (arguments.Has("channel-output"))
        {
            string channelPath = arguments.Require("channel-output");
            EnsureParentDirectory(channelPath);
            EncodedEccBlock channelEncoded = result.Encoded.ChannelLevels.Length == 0
                ? new DvdEccBlockEncoder().Encode(result.Payloads, lba, psnOffset, initial)
                : result.Encoded;
            File.WriteAllBytes(channelPath, BitPacking.PackMsbFirst(channelEncoded.ChannelLevels));
        }

        IsoWriteSummary? iso = WriteIso(arguments, isoOutputPath, result.Payloads, lba, constraint.ImageMapping);
        if (outputPath is not null)
        {
            WriteMetadata(
                outputPath + ".json",
                lba,
                psnOffset,
                initial,
                result.Encoded.FinalBoundary,
                result.Score,
                result.ScoreMaximum,
                result.ScoreMetric,
                result.GrayScore,
                result.GrayScoreMaximum,
                result.GrayScoreMetric,
                result.ControlledScore,
                result.ControlledScoreMaximum,
                result.ControlledScoreMetric,
                result.ControlledGrayScore,
                result.ControlledGrayScoreMaximum,
                result.ControlledGrayScoreMetric,
                result.CompletedIterations,
                result.Elapsed,
                randomSeed,
                options,
                constraint.ImageMapping,
                iso);
        }

        if (isoOutputPath is not null)
        {
            WriteMetadata(
                isoOutputPath + ".json",
                lba,
                psnOffset,
                initial,
                result.Encoded.FinalBoundary,
                result.Score,
                result.ScoreMaximum,
                result.ScoreMetric,
                result.GrayScore,
                result.GrayScoreMaximum,
                result.GrayScoreMetric,
                result.ControlledScore,
                result.ControlledScoreMaximum,
                result.ControlledScoreMetric,
                result.ControlledGrayScore,
                result.ControlledGrayScoreMaximum,
                result.ControlledGrayScoreMetric,
                result.CompletedIterations,
                result.Elapsed,
                randomSeed,
                options,
                constraint.ImageMapping,
                iso);
        }

        string outputs = string.Join(
            ", ",
            new[] { outputPath, isoOutputPath }.Where(path => path is not null));
        string gray = result.GrayScore is { } grayScore
            && result.GrayScoreMaximum is { } grayMaximum
            ? $", {result.GrayScoreMetric ?? "gray-error"}={grayScore}/{grayMaximum}"
            : string.Empty;
        string controlled = result.ControlledScore is { } controlledScore
            && result.ControlledScoreMaximum is { } controlledMaximum
            ? $", controlled-majority-error={controlledScore}/{controlledMaximum}"
                + (result.ControlledGrayScore is { } controlledGrayScore
                    && result.ControlledGrayScoreMaximum is { } controlledGrayMaximum
                    ? $", controlled-gray-error={controlledGrayScore}/{controlledGrayMaximum}"
                    : string.Empty)
            : string.Empty;
        Console.WriteLine(
            $"done: {result.ScoreMetric}={result.Score}/{result.ScoreMaximum}{gray}{controlled}, "
            + $"elapsed={result.Elapsed.TotalSeconds:F2}s, output={outputs}");
        return 0;
    }

    private static int Encode(CliArguments arguments)
    {
        string inputPath = arguments.Require("input");
        string outputPath = arguments.Require("output");
        byte[] payloads = File.ReadAllBytes(inputPath);
        uint lba = arguments.GetUInt("lba", 0);
        uint psnOffset = arguments.GetUInt("psn-offset", 0x30000);
        ModulationBoundary initial = ReadBoundary(arguments);
        DvdEccBlockEncoder encoder = new();
        EncodedEccBlock result = encoder.Encode(payloads, lba, psnOffset, initial);
        EnsureParentDirectory(outputPath);
        File.WriteAllBytes(outputPath, BitPacking.PackMsbFirst(result.ChannelLevels));
        WriteMetadata(
            outputPath + ".json",
            lba,
            psnOffset,
            initial,
            result.FinalBoundary,
            score: null,
            scoreMaximum: null,
            scoreMetric: null,
            grayScore: null,
            grayScoreMaximum: null,
            grayScoreMetric: null,
            controlledScore: null,
            controlledScoreMaximum: null,
            controlledScoreMetric: null,
            controlledGrayScore: null,
            controlledGrayScoreMaximum: null,
            controlledGrayScoreMetric: null,
            iterations: null,
            elapsed: null,
            randomSeed: null,
            solverOptions: null,
            imageMapping: null,
            iso: null);
        Console.WriteLine(
            $"encoded {payloads.Length} payload bytes into {result.ChannelLevels.Length} channel levels "
            + $"({new FileInfo(outputPath).Length} packed bytes)");
        return 0;
    }

    private static int Calibrate(CliArguments arguments)
    {
        string imagePath = arguments.Require("image");
        string outputPath = arguments.Require("output");
        int threshold = ReadByteRange(arguments, "image-threshold", 128, minimum: 0);
        int alphaThreshold = ReadByteRange(arguments, "alpha-threshold", 1, minimum: 1);
        uint totalSectors = ReadTotalSectors(arguments);
        uint startLba = arguments.GetUInt("lba", 0);
        uint fillSectors = arguments.Has("fill-sectors")
            ? arguments.GetUInt("fill-sectors", 0)
            : checked(totalSectors - startLba);
        CalibrationPreviewOptions options = new(
            totalSectors,
            startLba,
            fillSectors,
            RequireDouble(arguments, "generated-inner-radius-mm"),
            RequireDouble(arguments, "generated-outer-radius-mm"),
            RequireDouble(arguments, "actual-inner-radius-mm"),
            RequireDouble(arguments, "actual-outer-radius-mm"),
            arguments.GetDouble("channel-bit-nm", 133.3),
            ReadClockwise(arguments),
            checked((byte)threshold),
            checked((byte)alphaThreshold),
            arguments.GetInt("preview-size", 1024),
            arguments.GetInt("samples-per-sector", 16),
            ReadGeneratedPitch(arguments, "linear"),
            ReadGeneratedPitch(arguments, "quadratic"),
            ReadGeneratedPitch(arguments, "cubic"),
            arguments.GetDouble("actual-pitch-linear", 0),
            arguments.GetDouble("actual-pitch-quadratic", 0),
            arguments.GetDouble("actual-pitch-cubic", 0),
            ReadToneProfile(arguments));
        CalibrationPreviewSummary summary = CalibrationPreviewRenderer.Render(imagePath, outputPath, options);
        WriteJson(outputPath + ".json", summary);
        Console.WriteLine(
            $"preview: {summary.PreviewSize}x{summary.PreviewSize}, "
            + $"generated pitch={summary.GeneratedTrackPitchMicrometres:F4} um, "
            + $"actual pitch={summary.ActualTrackPitchMicrometres:F4} um, output={outputPath}");
        return 0;
    }

    private static int Simulate(CliArguments arguments)
    {
        string isoPath = arguments.Require("iso");
        string outputPath = arguments.Require("output");
        (uint sidecarStartLba, uint sidecarFillSectors, uint sidecarTotalSectors) =
            ReadSolveSummary(isoPath + ".json");
        long isoSectors = new FileInfo(isoPath).Length / DvdEccBlockEncoder.PayloadBytesPerSector;
        uint totalSectors;
        if (arguments.Has("total-sectors") || arguments.Has("total-blocks"))
        {
            totalSectors = ReadTotalSectors(arguments);
        }
        else if (sidecarTotalSectors != 0)
        {
            totalSectors = sidecarTotalSectors;
        }
        else
        {
            totalSectors = checked((uint)isoSectors);
        }

        uint startLba = arguments.GetUInt("lba", sidecarStartLba);
        uint fillSectors;
        if (arguments.Has("fill-sectors"))
        {
            fillSectors = arguments.GetUInt("fill-sectors", 0);
        }
        else if (sidecarFillSectors != 0)
        {
            fillSectors = sidecarFillSectors;
        }
        else
        {
            fillSectors = checked((uint)Math.Min(isoSectors, totalSectors) - startLba);
        }

        IsoReadbackOptions options = new(
            totalSectors,
            startLba,
            fillSectors,
            arguments.GetUInt("psn-offset", 0x30000),
            RequireDouble(arguments, "inner-radius-mm"),
            RequireDouble(arguments, "outer-radius-mm"),
            arguments.GetDouble("channel-bit-nm", 133.3),
            ReadClockwise(arguments),
            arguments.GetInt("preview-size", 1024),
            arguments.GetInt("samples-per-sector", 16),
            arguments.GetDouble("pitch-linear", 0),
            arguments.GetDouble("pitch-quadratic", 0),
            arguments.GetDouble("pitch-cubic", 0));
        IsoReadbackSummary summary = IsoReadbackRenderer.Render(isoPath, outputPath, options);
        WriteJson(outputPath + ".json", summary);
        Console.WriteLine(
            $"readback: {summary.PreviewSize}x{summary.PreviewSize}, "
            + $"classified={summary.ClassifiedSamples}/{summary.TotalSamples}, "
            + $"pitch={summary.TrackPitchMicrometres:F4} um, output={outputPath}");
        return 0;
    }

    /// <summary>Reads the solve metadata sidecar so a generated ISO defaults to the
    /// drawing range and disc size it was produced with; missing fields fall back to 0.</summary>
    private static (uint StartLba, uint FillSectors, uint TotalSectors) ReadSolveSummary(
        string sidecarPath)
    {
        if (!File.Exists(sidecarPath))
        {
            return (0, 0, 0);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            return (
                GetUIntProperty(document.RootElement, "startLba"),
                GetUIntProperty(document.RootElement, "fillSectors"),
                GetUIntProperty(document.RootElement, "totalSectors"));
        }
        catch (JsonException)
        {
            return (0, 0, 0);
        }
    }

    private static uint GetUIntProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetUInt32(out uint result)
            ? result
            : 0u;

    private static int SolveMultipleBlocks(CliArguments arguments, uint fillSectors)
    {
        bool hybridMode = arguments.Has("data-dir");
        bool filesAtOuterRing = ReadFilesAtOuterRing(arguments);
        if (!arguments.Has("image") || arguments.Has("target"))
        {
            throw new ArgumentException("Range or fast output requires --image and does not accept --target.");
        }

        string isoOutputPath = arguments.Require("iso-output");
        string[] unsupported =
        [
            "output",
            "iso-template",
            "iso-sectors",
            "seed-payload",
            "channel-output",
            "constraint-output",
        ];
        string? unsupportedOption = unsupported.FirstOrDefault(arguments.Has);
        if (unsupportedOption is not null)
        {
            throw new ArgumentException($"--{unsupportedOption} is not supported with multi-block --fill-sectors.");
        }

        if (hybridMode && (arguments.Has("lba") || arguments.Has("fill-sectors")))
        {
            throw new ArgumentException(
                "Hybrid --data-dir mode calculates --lba and --fill-sectors automatically; "
                + "use --drawing-start-lba only when extra inner-ring space is required.");
        }

        if (!hybridMode && arguments.Has("drawing-start-lba"))
        {
            throw new ArgumentException("--drawing-start-lba requires --data-dir.");
        }

        uint psnOffset = arguments.GetUInt("psn-offset", 0x30000);
        int randomSeed = arguments.GetInt("seed", 1);
        string initialMode = arguments.Get("initial", "random");
        bool zeroInitial = initialMode.Equals("zero", StringComparison.OrdinalIgnoreCase)
            ? true
            : initialMode.Equals("random", StringComparison.OrdinalIgnoreCase)
                ? false
                : throw new ArgumentException("--initial must be 'random' or 'zero'.");
        SolverAlgorithm algorithm = ReadSolverAlgorithm(
            arguments,
            hybridMode ? "dispersion" : "full");
        bool fastOutput = ReadBoolean(arguments, "fast-output", fallback: hybridMode);
        if (fastOutput && algorithm != SolverAlgorithm.Dispersion)
        {
            throw new ArgumentException("--fast-output true requires --algorithm dispersion.");
        }

        int iterationsPerBlock = arguments.Has("iterations-per-block")
            ? arguments.GetInt("iterations-per-block", 0)
            : arguments.GetInt(
                "iterations",
                algorithm switch
                {
                    SolverAlgorithm.StateControl => 3,
                    SolverAlgorithm.Dispersion => 1,
                    _ => 100,
                });
        ValidateRetiredControlOptions(arguments, algorithm);
        ImageMappingOptions imageMapping = ReadImageMappingOptions(arguments, defaultConstraintStep: 64);
        ModulationBoundary discInitialBoundary = ReadBoundary(arguments);
        ModulationBoundary drawingInitialBoundary = discInitialBoundary;
        HybridIsoWriteSummary? hybridIso = null;
        uint lba;
        if (hybridMode)
        {
            if ((psnOffset & 0xF) != 0)
            {
                throw new ArgumentException(
                    "Hybrid mode requires --psn-offset to be aligned to a 16-sector ECC Block.");
            }

            HybridIsoImageWriter.LayoutPlan isoLayout = HybridIsoImageWriter.Plan(
                arguments.Require("data-dir"),
                imageMapping.TotalSectors,
                arguments.Get("volume-label", "DVD_IMAGE"),
                filesAtOuterRing);
            uint minimumDrawingLba = AlignToEccBlock(filesAtOuterRing
                ? isoLayout.MetadataEndLbaExclusive : isoLayout.DataEndLbaExclusive, psnOffset);
            lba = arguments.Has("drawing-start-lba")
                ? arguments.GetUInt("drawing-start-lba", 0)
                : minimumDrawingLba;
            if (lba < minimumDrawingLba)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(arguments),
                    $"--drawing-start-lba must be at least {minimumDrawingLba} so it does not overwrite files.");
            }

            if (((lba + psnOffset) & 0xF) != 0)
            {
                throw new ArgumentException(
                    "--drawing-start-lba plus --psn-offset must be aligned to 16 sectors.");
            }

            if (lba > imageMapping.TotalSectors)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(arguments),
                    "--drawing-start-lba exceeds the reported disc size.");
            }

            uint drawingEndLba = filesAtOuterRing
                ? isoLayout.FileDataStartLba / DvdEccBlockEncoder.SectorCount * DvdEccBlockEncoder.SectorCount
                : imageMapping.TotalSectors;
            if (drawingEndLba < lba)
                throw new ArgumentException("The selected drawing range overlaps the outer-ring files.");
            fillSectors = checked((drawingEndLba - lba)
                / DvdEccBlockEncoder.SectorCount
                * DvdEccBlockEncoder.SectorCount);
            if (fillSectors < DvdEccBlockEncoder.SectorCount)
            {
                throw new ArgumentException(
                    "The filesystem leaves no complete ECC Block for drawing in the selected file layout.");
            }

            hybridIso = HybridIsoImageWriter.Create(isoLayout, isoOutputPath);

            Console.WriteLine(
                $"hybrid ISO: files={hybridIso.FileCount}, directories={hybridIso.DirectoryCount}, "
                + $"data={hybridIso.FileBytes} bytes, filesystem end LBA={hybridIso.DataEndLbaExclusive}, "
                + $"files={(filesAtOuterRing ? "outer" : "inner")}, drawing LBA {lba}..{lba + fillSectors - 1}");
            if (!fastOutput)
            {
                Console.WriteLine($"replaying inner-ring modulation boundary: 0..{lba - 1}");
                drawingInitialBoundary = ReplayIsoBoundary(
                    isoOutputPath,
                    lba,
                    psnOffset,
                    discInitialBoundary);
                Console.WriteLine(
                    $"drawing boundary: state={drawingInitialBoundary.State}, "
                    + $"DSV={drawingInitialBoundary.Dsv}, level={drawingInitialBoundary.LevelName}");
            }
        }
        else
        {
            lba = arguments.GetUInt("lba", 0);
        }

        MultiBlockSolveOptions options = new(
            lba,
            fillSectors,
            psnOffset,
            iterationsPerBlock,
            arguments.GetInt("mutation-bytes", 4),
            randomSeed,
            arguments.GetDouble("initial-temperature", 64.0),
            arguments.GetDouble("final-temperature", 0.25),
            zeroInitial,
            imageMapping,
            algorithm,
            arguments.GetInt("bridge-bytes", 2),
            arguments.GetInt("beam-width", 8),
            arguments.GetInt("retry-beam-width", 32),
            arguments.GetDouble("retry-frame-fraction", 0.25),
            arguments.GetInt("batch-frames", 8),
            arguments.GetInt("batch-candidates", 4),
            arguments.GetInt("search-lanes", 1),
            arguments.GetInt("fast-parallelism", 0),
            PreserveExistingIso: hybridMode);
        int totalBlocks = checked((int)(fillSectors / DvdEccBlockEncoder.SectorCount));
        int progressEvery = Math.Max(1, totalBlocks / 100);
        if (fastOutput)
        {
            return WriteFastDispersionImage(
                arguments,
                isoOutputPath,
                options,
                progressEvery,
                hybridIso);
        }

        Console.WriteLine(
            $"raw ISO fill: LBA {lba}..{checked(lba + fillSectors - 1)}, "
            + $"ECC blocks={totalBlocks}, iterations/block={iterationsPerBlock}, "
            + $"algorithm={AlgorithmName(algorithm)}, "
            + $"constraint step={imageMapping.SampleEveryChannelBits}");
        if (algorithm == SolverAlgorithm.Dispersion)
        {
            Console.WriteLine(
                DispersionPaletteDescription());
        }
        MultiBlockSolveResult result = MultiBlockImageSolver.Solve(
            arguments.Require("image"),
            isoOutputPath,
            drawingInitialBoundary,
            options,
            progress =>
            {
                if (progress.CompletedBlocks == 1
                    || progress.CompletedBlocks == progress.TotalBlocks
                    || progress.CompletedBlocks % progressEvery == 0)
                {
                    Console.WriteLine(
                        $"block {progress.CompletedBlocks}/{progress.TotalBlocks}, LBA={progress.BlockLba}, "
                        + $"{progress.ScoreMetric}={progress.Score}/{progress.ScoreMaximum}"
                        + (progress.GrayScore is { } grayScore
                            && progress.GrayScoreMaximum is { } grayMaximum
                            ? $", {progress.GrayScoreMetric ?? "gray-error"}={grayScore}/{grayMaximum}, "
                            : ", ")
                        + (progress.ControlledScore is { } controlledScore
                            && progress.ControlledScoreMaximum is { } controlledMaximum
                            ? $"controlled-majority={controlledScore}/{controlledMaximum}"
                                + (progress.ControlledGrayScore is { } controlledGrayScore
                                    && progress.ControlledGrayScoreMaximum is { } controlledGrayMaximum
                                    ? $", controlled-gray={controlledGrayScore}/{controlledGrayMaximum}, "
                                    : ", ")
                            : string.Empty)
                        + $"state={progress.FinalBoundary.State}, DSV={progress.FinalBoundary.Dsv}, "
                        + $"elapsed={progress.Elapsed.TotalSeconds:F1}s");
                }
            });

        var metadata = new
        {
            format = "dvd-nrzi-disc-fill-v2",
            sourceImage = Path.GetFullPath(arguments.Require("image")),
            isoOutput = Path.GetFullPath(isoOutputPath),
            startLba = lba,
            filesAtOuterRing = hybridIso?.FilesAtOuterRing ?? false,
            fillSectors,
            totalSectors = imageMapping.TotalSectors,
            psnOffset = $"0x{psnOffset:X}",
            blockCount = result.CompletedBlocks,
            algorithm = AlgorithmName(algorithm),
            iterationsPerBlock,
            mutationBytes = algorithm == SolverAlgorithm.Full ? (int?)options.MutationBytes : null,
            bridgeBytes = algorithm == SolverAlgorithm.StateControl ? (int?)options.BridgeBytes : null,
            beamWidth = algorithm == SolverAlgorithm.StateControl ? (int?)options.BeamWidth : null,
            retryBeamWidth = algorithm == SolverAlgorithm.StateControl ? (int?)options.RetryBeamWidth : null,
            retryFrameFraction = algorithm == SolverAlgorithm.StateControl
                ? (double?)options.RetryFrameFraction
                : null,
            adaptiveRetryEnabled = algorithm == SolverAlgorithm.StateControl
                ? (bool?)(options.RetryBeamWidth > options.BeamWidth)
                : null,
            batchFrames = algorithm == SolverAlgorithm.StateControl ? (int?)options.BatchFrames : null,
            batchCandidates = algorithm == SolverAlgorithm.StateControl
                ? (int?)options.BatchCandidates
                : null,
            searchLanes = algorithm == SolverAlgorithm.StateControl
                ? (int?)options.SearchLanes
                : null,
            binaryBytePairMode = algorithm == SolverAlgorithm.StateControl
                ? "efm-state-input-level-and-current-dsv"
                : null,
            binaryBytePairs = algorithm == SolverAlgorithm.StateControl
                ? BinaryBytePairMetadata()
                : null,
            dispersionPool = algorithm == SolverAlgorithm.Dispersion
                ? DispersionPaletteMetadata()
                : null,
            constraintStep = imageMapping.SampleEveryChannelBits,
            initialBoundary = new
            {
                state = drawingInitialBoundary.State,
                dsv = drawingInitialBoundary.Dsv,
                level = drawingInitialBoundary.LevelName,
            },
            discInitialBoundary = hybridIso is null
                ? null
                : (object)new
                {
                    state = discInitialBoundary.State,
                    dsv = discInitialBoundary.Dsv,
                    level = discInitialBoundary.LevelName,
                },
            finalBoundary = new
            {
                state = result.FinalBoundary.State,
                dsv = result.FinalBoundary.Dsv,
                level = result.FinalBoundary.LevelName,
            },
            result.TotalScore,
            result.TotalScoreMaximum,
            result.ScoreMetric,
            result.TotalGrayScore,
            result.TotalGrayScoreMaximum,
            result.GrayScoreMetric,
            result.TotalControlledScore,
            result.TotalControlledScoreMaximum,
            result.TotalControlledGrayScore,
            result.TotalControlledGrayScoreMaximum,
            normalizedScore = result.TotalScoreMaximum == 0
                ? 0
                : (double)result.TotalScore / result.TotalScoreMaximum,
            normalizedGrayScore = result.TotalGrayScore is null
                || result.TotalGrayScoreMaximum is null
                || result.TotalGrayScoreMaximum == 0
                    ? null
                    : (double?)result.TotalGrayScore / result.TotalGrayScoreMaximum,
            normalizedControlledScore = result.TotalControlledScore is null
                || result.TotalControlledScoreMaximum is null
                || result.TotalControlledScoreMaximum == 0
                    ? null
                    : (double?)result.TotalControlledScore / result.TotalControlledScoreMaximum,
            normalizedControlledGrayScore = result.TotalControlledGrayScore is null
                || result.TotalControlledGrayScoreMaximum is null
                || result.TotalControlledGrayScoreMaximum == 0
                    ? null
                    : (double?)result.TotalControlledGrayScore
                        / result.TotalControlledGrayScoreMaximum,
            elapsedSeconds = result.Elapsed.TotalSeconds,
            imageMapping,
            pitchPolynomial = DescribePitchPolynomial(imageMapping),
            iso = new
            {
                mode = hybridIso?.Mode ?? "raw-sector-image",
                logicalSectorBytes = IsoImageWriter.LogicalSectorBytes,
                imageSectors = result.IsoSectors,
                sparseFile = result.SparseIso,
                mountableFilesystem = hybridIso is not null,
                hybrid = hybridIso,
            },
        };
        WriteJson(isoOutputPath + ".json", metadata);
        Console.WriteLine(
            $"done: blocks={result.CompletedBlocks}, "
            + $"{result.ScoreMetric}={result.TotalScore}/{result.TotalScoreMaximum}"
            + (result.TotalGrayScore is { } grayScore
                && result.TotalGrayScoreMaximum is { } grayMaximum
                ? $", {result.GrayScoreMetric ?? "gray-error"}={grayScore}/{grayMaximum}, "
                : ", ")
            + (result.TotalControlledScore is { } controlledScore
                && result.TotalControlledScoreMaximum is { } controlledMaximum
                ? $"controlled-majority={controlledScore}/{controlledMaximum}"
                    + (result.TotalControlledGrayScore is { } controlledGrayScore
                        && result.TotalControlledGrayScoreMaximum is { } controlledGrayMaximum
                        ? $", controlled-gray={controlledGrayScore}/{controlledGrayMaximum}, "
                        : ", ")
                : string.Empty)
            + $"elapsed={result.Elapsed.TotalSeconds:F1}s, output={isoOutputPath}");
        return 0;
    }

    private static bool ReadFilesAtOuterRing(CliArguments arguments)
    {
        bool outer = arguments.Get("file-position", "inner") switch
        {
            "inner" => false,
            "outer" => true,
            _ => throw new ArgumentException("--file-position must be 'inner' or 'outer'."),
        };
        if (outer && !arguments.Has("data-dir"))
            throw new ArgumentException("--file-position outer requires --data-dir.");
        return outer;
    }

    private static int WriteFastDispersionImage(
        CliArguments arguments,
        string isoOutputPath,
        MultiBlockSolveOptions options,
        int progressEvery,
        HybridIsoWriteSummary? hybridIso)
    {
        Console.WriteLine(
            $"fast raw ISO fill: LBA {options.StartLba}.."
            + $"{checked(options.StartLba + options.FillSectors - 1)}, "
            + $"ECC blocks={options.FillSectors / DvdEccBlockEncoder.SectorCount}, "
            + "algorithm=dispersion, ECC/EFM replay=skipped, "
            + $"workers={(options.FastParallelism == 0 ? Environment.ProcessorCount : options.FastParallelism)}");
        Console.WriteLine(
            DispersionPaletteDescription() + ", "
            + "image sampled at every payload code-word center");

        FastDispersionWriteResult result = FastDispersionImageWriter.Write(
            arguments.Require("image"),
            isoOutputPath,
            options,
            progress =>
            {
                if (progress.CompletedBlocks == 1
                    || progress.CompletedBlocks == progress.TotalBlocks
                    || progress.CompletedBlocks % progressEvery == 0)
                {
                    double mebibytes = progress.CompletedBlocks
                        * (double)DvdEccBlockEncoder.PayloadBytesPerBlock
                        / (1024 * 1024);
                    double rate = progress.Elapsed.TotalSeconds <= 0
                        ? 0
                        : mebibytes / progress.Elapsed.TotalSeconds;
                    Console.WriteLine(
                        $"block {progress.CompletedBlocks}/{progress.TotalBlocks}, "
                        + $"LBA={progress.BlockLba}, controlled={progress.ControlledWords}, "
                        + $"rate={rate:F2} MiB/s, elapsed={progress.Elapsed.TotalSeconds:F1}s");
                }
            });

        ImageMappingOptions imageMapping = options.ImageMapping;
        var metadata = new
        {
            format = "dvd-nrzi-disc-fill-v2",
            sourceImage = Path.GetFullPath(arguments.Require("image")),
            isoOutput = Path.GetFullPath(isoOutputPath),
            startLba = options.StartLba,
            filesAtOuterRing = hybridIso?.FilesAtOuterRing ?? false,
            fillSectors = options.FillSectors,
            totalSectors = imageMapping.TotalSectors,
            psnOffset = $"0x{options.PsnOffset:X}",
            blockCount = result.CompletedBlocks,
            algorithm = "dispersion",
            fastOutput = true,
            fastParallelism = options.FastParallelism == 0
                ? Environment.ProcessorCount
                : options.FastParallelism,
            exactEccEfmReplay = false,
            finalBoundary = (object?)null,
            dispersionPool = DispersionPaletteMetadata(),
            constraintStep = imageMapping.SampleEveryChannelBits,
            constraintStepUsed = false,
            totalScore = 0,
            totalScoreMaximum = result.ControlledWords,
            scoreMetric = "payload-transition-class-error",
            normalizedScore = 0.0,
            elapsedSeconds = result.Elapsed.TotalSeconds,
            imageMapping,
            pitchPolynomial = DescribePitchPolynomial(imageMapping),
            iso = new
            {
                mode = hybridIso?.Mode ?? "raw-sector-image",
                logicalSectorBytes = IsoImageWriter.LogicalSectorBytes,
                imageSectors = result.IsoSectors,
                sparseFile = result.SparseIso,
                mountableFilesystem = hybridIso is not null,
                hybrid = hybridIso,
            },
        };
        WriteJson(isoOutputPath + ".json", metadata);
        double totalMebibytes = result.FilledSectors
            * (double)IsoImageWriter.LogicalSectorBytes
            / (1024 * 1024);
        double finalRate = result.Elapsed.TotalSeconds <= 0
            ? 0
            : totalMebibytes / result.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"done: blocks={result.CompletedBlocks}, controlled={result.ControlledWords}, "
            + "payload-transition-class-error=0, boundary=not replayed, "
            + $"rate={finalRate:F2} MiB/s, elapsed={result.Elapsed.TotalSeconds:F1}s, "
            + $"output={isoOutputPath}");
        return 0;
    }

    private static byte[] LoadOrCreatePayload(CliArguments arguments, int randomSeed)
    {
        if (arguments.Has("seed-payload"))
        {
            byte[] payload = File.ReadAllBytes(arguments.Require("seed-payload"));
            if (payload.Length != DvdEccBlockEncoder.PayloadBytesPerBlock)
            {
                throw new ArgumentException(
                    $"Seed payload must contain exactly {DvdEccBlockEncoder.PayloadBytesPerBlock} bytes.");
            }

            return payload;
        }

        byte[] generated = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        string mode = arguments.Get("initial", "random");
        if (mode.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            new Random(randomSeed).NextBytes(generated);
        }
        else if (!mode.Equals("zero", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--initial must be 'random' or 'zero'.");
        }

        return generated;
    }

    private static ImageConstraint ReadConstraint(CliArguments arguments, uint initialLba)
    {
        bool hasTarget = arguments.Has("target");
        bool hasImage = arguments.Has("image");
        if (hasTarget == hasImage)
        {
            throw new ArgumentException("Specify exactly one of --target or --image.");
        }

        if (hasTarget)
        {
            return ImageConstraint.Load(arguments.Require("target"), arguments.GetInt("target-sector", 0));
        }

        ImageMappingOptions options = ReadImageMappingOptions(arguments, defaultConstraintStep: 1);
        return ImageConstraint.LoadImage(arguments.Require("image"), initialLba, options);
    }

    private static ImageMappingOptions ReadImageMappingOptions(
        CliArguments arguments,
        int defaultConstraintStep)
    {
        int threshold = ReadByteRange(arguments, "image-threshold", 128, minimum: 0);
        int alphaThreshold = ReadByteRange(arguments, "alpha-threshold", 1, minimum: 1);
        return new ImageMappingOptions(
            ReadTotalSectors(arguments),
            arguments.GetDouble("inner-radius-mm", 24.0),
            arguments.GetDouble("outer-radius-mm", 58.0),
            arguments.GetDouble("channel-bit-nm", 133.3),
            arguments.GetDouble("start-angle-deg", 0.0),
            ReadClockwise(arguments),
            checked((byte)threshold),
            checked((byte)alphaThreshold),
            arguments.GetInt("constraint-step", defaultConstraintStep),
            arguments.GetDouble("pitch-linear", 0),
            arguments.GetDouble("pitch-quadratic", 0),
            arguments.GetDouble("pitch-cubic", 0),
            ReadToneProfile(arguments));
    }

    /// <summary>Optional tone mapping of the sampled picture. Absent means the historical
    /// hard threshold, so existing command lines produce the same bytes.</summary>
    private static ToneProfile? ReadToneProfile(CliArguments arguments)
    {
        if (!arguments.Has("tone-dither")
            && !arguments.Has("tone-levels")
            && !arguments.Has("tone-brightness")
            && !arguments.Has("tone-gamma"))
        {
            return null;
        }

        ToneProfile tone = new(
            arguments.GetDouble("tone-dither", 0),
            arguments.GetInt("tone-levels", ToneProfile.TwoLevels),
            arguments.GetDouble("tone-brightness", 0),
            arguments.GetBoolean("tone-gamma", false));
        tone.Validate();
        if (!tone.IsDvdCompatible)
        {
            throw new ArgumentException(
                "DVD dispersion uses two textures; --tone-levels must be 2.");
        }

        return tone;
    }

    private static double ReadGeneratedPitch(CliArguments arguments, string order)
    {
        string plain = $"pitch-{order}";
        string generated = $"generated-pitch-{order}";
        if (arguments.Has(plain) && arguments.Has(generated))
            throw new ArgumentException($"Use only one of --{plain} and --{generated}.");
        return arguments.GetDouble(arguments.Has(generated) ? generated : plain, 0);
    }

    private static object DescribePitchPolynomial(ImageMappingOptions options)
    {
        double length = options.TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * options.ChannelBitLengthNm * 1e-6;
        double scale = DiscImageStudio.Core.DvdTrackGeometry.Create(options.InnerRadiusMm,
            options.OuterRadiusMm, length, options.PitchLinear, options.PitchQuadratic, options.PitchCubic).TrackPitchMm * 1000;
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

    private static uint ReadTotalSectors(CliArguments arguments)
    {
        bool hasSectors = arguments.Has("total-sectors");
        bool hasBlocks = arguments.Has("total-blocks");
        if (hasSectors && hasBlocks)
        {
            throw new ArgumentException("Use only one of --total-sectors and --total-blocks.");
        }

        if (!hasSectors && !hasBlocks)
        {
            throw new ArgumentException("Image mapping requires --total-sectors (or its alias --total-blocks).");
        }

        return arguments.GetUInt(hasSectors ? "total-sectors" : "total-blocks", 0);
    }

    private static uint AlignToEccBlock(uint minimumLba, uint psnOffset)
    {
        uint remainder = checked((minimumLba + psnOffset) & 0xF);
        return remainder == 0
            ? minimumLba
            : checked(minimumLba + DvdEccBlockEncoder.SectorCount - remainder);
    }

    private static ModulationBoundary ReplayIsoBoundary(
        string isoPath,
        uint sectorCount,
        uint psnOffset,
        ModulationBoundary initialBoundary)
    {
        if (sectorCount % DvdEccBlockEncoder.SectorCount != 0)
        {
            throw new ArgumentException("Boundary replay sector count must be aligned to 16 sectors.");
        }

        int totalBlocks = checked((int)(sectorCount / DvdEccBlockEncoder.SectorCount));
        int progressEvery = Math.Max(1, totalBlocks / 100);
        byte[] payloads = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        ModulationBoundary boundary = initialBoundary;
        using FileStream iso = new(
            isoPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        for (int blockIndex = 0; blockIndex < totalBlocks; blockIndex++)
        {
            iso.ReadExactly(payloads);
            uint lba = checked((uint)blockIndex * DvdEccBlockEncoder.SectorCount);
            byte[] recordingFrames = DvdEccBlockEncoder.BuildRecordingFrames(
                payloads,
                checked(lba + psnOffset));
            boundary = DvdEccBlockEncoder.ReplayRecordingFrameBoundary(
                recordingFrames,
                boundary);
            int completed = blockIndex + 1;
            if (completed == 1 || completed == totalBlocks || completed % progressEvery == 0)
            {
                Console.WriteLine(
                    $"inner boundary block {completed}/{totalBlocks}, "
                    + $"state={boundary.State}, DSV={boundary.Dsv}, level={boundary.LevelName}");
            }
        }

        return boundary;
    }

    private static IsoWriteSummary? WriteIso(
        CliArguments arguments,
        string? isoOutputPath,
        ReadOnlySpan<byte> payloads,
        uint lba,
        ImageMappingSummary? imageMapping)
    {
        if (isoOutputPath is null)
        {
            if (arguments.Has("iso-template") || arguments.Has("iso-sectors"))
            {
                throw new ArgumentException("--iso-template and --iso-sectors require --iso-output.");
            }

            return null;
        }

        if (arguments.Has("iso-template"))
        {
            if (arguments.Has("iso-sectors"))
            {
                throw new ArgumentException("--iso-sectors cannot be combined with --iso-template.");
            }

            string templatePath = arguments.Require("iso-template");
            uint templateSectors = IsoImageWriter.GetTemplateSectorCount(templatePath);
            if (imageMapping is not null && templateSectors != imageMapping.TotalSectors)
            {
                throw new ArgumentException(
                    $"ISO template has {templateSectors} sectors, but image mapping used "
                    + $"{imageMapping.TotalSectors} sectors.");
            }

            return IsoImageWriter.PatchTemplate(templatePath, isoOutputPath, payloads, lba);
        }

        uint volumeSectors;
        if (arguments.Has("iso-sectors"))
        {
            volumeSectors = arguments.GetUInt("iso-sectors", 0);
            if (imageMapping is not null && volumeSectors != imageMapping.TotalSectors)
            {
                throw new ArgumentException(
                    $"--iso-sectors is {volumeSectors}, but image mapping used "
                    + $"{imageMapping.TotalSectors} sectors.");
            }
        }
        else if (imageMapping is not null)
        {
            volumeSectors = imageMapping.TotalSectors;
        }
        else
        {
            throw new ArgumentException(
                "Minimal ISO output requires --iso-sectors when the constraint does not come from an image.");
        }

        return IsoImageWriter.CreateMinimal(
            isoOutputPath,
            payloads,
            lba,
            volumeSectors,
            arguments.Get("volume-label", "DVD_IMAGE"));
    }

    private static ModulationBoundary ReadBoundary(CliArguments arguments)
    {
        int state = arguments.GetInt("initial-state", 1);
        int dsv = arguments.GetInt("initial-dsv", 0);
        string level = arguments.Get("initial-level", "land");
        bool isLand = level.Equals("land", StringComparison.OrdinalIgnoreCase)
            ? true
            : level.Equals("pit", StringComparison.OrdinalIgnoreCase)
                ? false
                : throw new ArgumentException("--initial-level must be 'pit' or 'land'.");
        return ModulationBoundary.Validate(checked((byte)state), dsv, isLand);
    }

    private static bool ReadClockwise(CliArguments arguments)
    {
        string direction = arguments.Get("spiral-direction", "cw");
        return direction.Equals("cw", StringComparison.OrdinalIgnoreCase)
            ? true
            : direction.Equals("ccw", StringComparison.OrdinalIgnoreCase)
                ? false
                : throw new ArgumentException("--spiral-direction must be 'ccw' or 'cw'.");
    }

    private static SolverAlgorithm ReadSolverAlgorithm(
        CliArguments arguments,
        string fallback = "full")
    {
        string value = arguments.Get("algorithm", fallback);
        return value.ToLowerInvariant() switch
        {
            "full" or "exact" => SolverAlgorithm.Full,
            "state-control" or "control" => SolverAlgorithm.StateControl,
            "dispersion" or "byte-pool" => SolverAlgorithm.Dispersion,
            _ => throw new ArgumentException(
                "--algorithm must be 'full' (alias 'exact'), 'state-control' "
                + "(alias 'control'), or 'dispersion' (alias 'byte-pool')."),
        };
    }

    private static string AlgorithmName(SolverAlgorithm algorithm)
        => algorithm switch
        {
            SolverAlgorithm.StateControl => "state-control",
            SolverAlgorithm.Dispersion => "dispersion",
            _ => "full",
        };

    private static void ValidateRetiredControlOptions(CliArguments arguments, SolverAlgorithm algorithm)
    {
        if (arguments.Has("frame-candidates")
            || arguments.Has("parallel-candidates")
            || arguments.Has("parallelism")
            || arguments.Has("word-candidates"))
        {
            throw new ArgumentException("--frame-candidates, --parallel-candidates, --parallelism and --word-candidates belonged to removed search modes; state-control now uses state/level/current-DSV binary byte pairs and --beam-width.");
        }

        if (algorithm != SolverAlgorithm.StateControl)
        {
            return;
        }

        if (arguments.Has("control-bytes"))
        {
            throw new ArgumentException("--control-bytes belonged to the removed control-pool algorithm; use --bridge-bytes.");
        }

        if (arguments.Has("mutation-bytes"))
        {
            throw new ArgumentException(
                "--mutation-bytes is only used by --algorithm full; state-control uses deterministic codeword selection.");
        }
    }

    private static int ReadByteRange(CliArguments arguments, string key, int fallback, int minimum)
    {
        int value = arguments.GetInt(key, fallback);
        if (value < minimum || value > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), $"--{key} must be {minimum}..255.");
        }

        return value;
    }

    private static bool ReadBoolean(CliArguments arguments, string key, bool fallback)
    {
        if (!arguments.Has(key))
        {
            return fallback;
        }

        string value = arguments.Require(key);
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value == "1"
                ? true
                : value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || value == "0"
                        ? false
                        : throw new ArgumentException(
                            $"--{key} must be true/false, yes/no, or 1/0.");
    }

    private static double RequireDouble(CliArguments arguments, string key)
        => arguments.Has(key)
            ? arguments.GetDouble(key, 0)
            : throw new ArgumentException($"Missing required option --{key}.");

    private static void WriteMetadata(
        string path,
        uint lba,
        uint psnOffset,
        ModulationBoundary initial,
        ModulationBoundary final,
        int? score,
        int? scoreMaximum,
        string? scoreMetric,
        int? grayScore,
        int? grayScoreMaximum,
        string? grayScoreMetric,
        int? controlledScore,
        int? controlledScoreMaximum,
        string? controlledScoreMetric,
        int? controlledGrayScore,
        int? controlledGrayScoreMaximum,
        string? controlledGrayScoreMetric,
        int? iterations,
        TimeSpan? elapsed,
        int? randomSeed,
        SolverOptions? solverOptions,
        ImageMappingSummary? imageMapping,
        IsoWriteSummary? iso)
    {
        var metadata = new
        {
            format = "dvd-nrzi-image-solver-v2",
            initialLba = lba,
            psnOffset = $"0x{psnOffset:X}",
            firstPsn = $"0x{checked(lba + psnOffset):X6}",
            sectorCount = DvdEccBlockEncoder.SectorCount,
            initialBoundary = new { state = initial.State, dsv = initial.Dsv, level = initial.LevelName },
            finalBoundary = new { state = final.State, dsv = final.Dsv, level = final.LevelName },
            score,
            scoreMaximum,
            scoreMetric,
            normalizedScore = score is null || scoreMaximum is null || scoreMaximum == 0
                ? null
                : (double?)score / scoreMaximum,
            grayScore,
            grayScoreMaximum,
            grayScoreMetric,
            normalizedGrayScore = grayScore is null
                || grayScoreMaximum is null
                || grayScoreMaximum == 0
                    ? null
                    : (double?)grayScore / grayScoreMaximum,
            controlledScore,
            controlledScoreMaximum,
            controlledScoreMetric,
            normalizedControlledScore = controlledScore is null
                || controlledScoreMaximum is null
                || controlledScoreMaximum == 0
                    ? null
                    : (double?)controlledScore / controlledScoreMaximum,
            controlledGrayScore,
            controlledGrayScoreMaximum,
            controlledGrayScoreMetric,
            normalizedControlledGrayScore = controlledGrayScore is null
                || controlledGrayScoreMaximum is null
                || controlledGrayScoreMaximum == 0
                    ? null
                    : (double?)controlledGrayScore / controlledGrayScoreMaximum,
            iterations,
            elapsedSeconds = elapsed?.TotalSeconds,
            randomSeed,
            algorithm = solverOptions is null ? null : AlgorithmName(solverOptions.Algorithm),
            mutationBytes = solverOptions?.Algorithm == SolverAlgorithm.Full ? (int?)solverOptions.MutationBytes : null,
            bridgeBytes = solverOptions?.Algorithm == SolverAlgorithm.StateControl ? (int?)solverOptions.BridgeBytes : null,
            beamWidth = solverOptions?.Algorithm == SolverAlgorithm.StateControl ? (int?)solverOptions.BeamWidth : null,
            retryBeamWidth = solverOptions?.Algorithm == SolverAlgorithm.StateControl
                ? (int?)solverOptions.RetryBeamWidth
                : null,
            retryFrameFraction = solverOptions?.Algorithm == SolverAlgorithm.StateControl
                ? (double?)solverOptions.RetryFrameFraction
                : null,
            adaptiveRetryEnabled = solverOptions?.Algorithm == SolverAlgorithm.StateControl
                ? (bool?)solverOptions.AdaptiveRetryEnabled
                : null,
            batchFrames = solverOptions?.Algorithm == SolverAlgorithm.StateControl
                ? (int?)solverOptions.BatchFrames
                : null,
            batchCandidates = solverOptions?.Algorithm == SolverAlgorithm.StateControl
                ? (int?)solverOptions.BatchCandidates
                : null,
            searchLanes = solverOptions?.Algorithm == SolverAlgorithm.StateControl
                ? (int?)solverOptions.SearchLanes
                : null,
            binaryBytePairMode = solverOptions?.Algorithm == SolverAlgorithm.StateControl
                ? "efm-state-input-level-and-current-dsv"
                : null,
            binaryBytePairs = solverOptions?.Algorithm == SolverAlgorithm.StateControl
                ? BinaryBytePairMetadata()
                : null,
            dispersionPool = solverOptions?.Algorithm == SolverAlgorithm.Dispersion
                ? DispersionPaletteMetadata()
                : null,
            channelPacking = "MSB-first, 1=land/white, 0=pit/black",
            imageMapping,
            iso,
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                metadata,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }));
    }

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string DispersionPaletteDescription()
        => "dispersion black/white pool: Q="
            + string.Join(",", Enumerable.Range(0, 2)
                .Select(level => $"0x{DvdGrayscalePalette.GetScrambledByteForLevel(level):X2}"))
            + "; nominal luminance=0,255; threshold=128";

    private static object DispersionPaletteMetadata() => new
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

    private static object[] BinaryBytePairMetadata()
        => EfmPlusBinaryBytePairs.AtZeroDsv
            .Select(entry => (object)new
            {
                state = entry.State,
                inputLevel = entry.InitialIsLand ? "land" : "pit",
                blackByte = $"0x{entry.Pair.BlackByte:X2}",
                whiteByte = $"0x{entry.Pair.WhiteByte:X2}",
                blackWorstLandCount = entry.Pair.BlackWorstLandCount,
                whiteWorstLandCount = entry.Pair.WhiteWorstLandCount,
                referenceDsv = entry.Pair.ReferenceDsv,
                blackWorstAbsoluteOutputDsv = entry.Pair.BlackWorstAbsoluteOutputDsv,
                whiteWorstAbsoluteOutputDsv = entry.Pair.WhiteWorstAbsoluteOutputDsv,
            })
            .ToArray();

    private static void WriteJson(string path, object value)
    {
        EnsureParentDirectory(path);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }));
    }

    private static int RunSelfTests()
    {
        SelfTests.Run();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            DVD NRZI image constraint solver

            Solve one 16-sector ECC Block:
              DvdImageSolver solve (--target TARGET.txt | --image IMAGE.png)
                (--output payload.bin | --iso-output image.iso) [options]

            Encode one 32768-byte payload block:
              DvdImageSolver encode --input payload.bin --output channel.bin [options]

            Render a radius-calibration preview:
              DvdImageSolver calibrate --image IMAGE.png --output PREVIEW.png [options]

            Simulate the read-back of a generated ISO:
              DvdImageSolver simulate --iso IMAGE.iso --output PREVIEW.png [options]

            Common options:
              --lba N                   Initial LBA only; following 15 sectors are consecutive (default 0)
              --psn-offset N            Decimal or 0x-prefixed PSN offset (default 0x30000)
              --initial-state 1..4      Initial EFMPlus state (default 1)
              --initial-dsv N           Initial DSV (default 0)
              --initial-level pit|land Initial physical NRZI level (default land)

            Solve options:
              --image FILE              PNG/JPEG/BMP/TIFF/GIF image mapped onto the disc
              --total-sectors N         Reported count of 2048-byte sectors (required with --image)
              --total-blocks N          Alias for --total-sectors
              --inner-radius-mm N       Track start radius (default 24.0)
              --outer-radius-mm N       Track end radius (default 58.0)
              --pitch-linear N          Linear radial pitch coefficient (default 0)
              --pitch-quadratic N       Quadratic radial pitch coefficient (default 0)
              --pitch-cubic N           Cubic radial pitch coefficient (default 0)
                                        pitch(r)=p0*(1+a1*x+a2*x^2+a3*x^3),
                                        x=(r-inner)/(outer-inner); p0 follows total track length
              --channel-bit-nm N        Physical channel-bit length (default 133.3)
              --start-angle-deg N       Track start angle (default 0)
              --spiral-direction ccw|cw Polar direction (default cw)
              --image-threshold 0..255  Binary full/state-control threshold (default 128)
              --tone-dither 0..1        Mix black/white textures per sample; 0 is the hard threshold
              --tone-levels 2           DVD dispersion has two textures; only 2 is accepted
              --tone-brightness N       Shift the tone ramp by -64..64 (default 0)
              --tone-gamma true|false   Decode sRGB before dithering (default false)
              --alpha-threshold 1..255  Lower alpha is unconstrained (default 1)
              --constraint-output FILE  Save the mapped 619008-symbol target
              --constraint-step N       Constrain every Nth channel bit (single block default 1)
              --fill-sectors N          Fill N consecutive sectors; N must be a multiple of 16
              --data-dir DIR            Hybrid disc: store this directory together with the drawing
              --file-position MODE      inner (default) or outer; outer requires --data-dir
              --drawing-start-lba N     Hybrid override; must follow all files and align to an ECC Block
              --fast-output true|false Direct dispersion ISO output without ECC/EFM replay
                                        (default false; true in hybrid mode)
              --fast-parallelism 0..64 Fast-output workers; 0 selects processor count (default 0)
              --iterations-per-block N  Full/state-control passes; dispersion ignores search (defaults 100/3/1)
              --iso-output FILE         Write a directly burnable 2048-byte/sector ISO image
              --iso-sectors N           Sector count for a new minimal ISO (image mode reuses total sectors)
              --iso-template FILE       Copy an existing ISO and replace sectors LBA..LBA+15
              --volume-label TEXT       Label for a new minimal ISO (default DVD_IMAGE)
              --target-sector 0..15     Placement for a one-sector target (default 0)
              --algorithm full|state-control|dispersion
                                        Bit search, state control, or black/white dispersion texture pool
              --iterations N            Full/state-control passes; dispersion ignores search (defaults 1000/3/1)
              --mutation-bytes N        Bytes changed per full-mode proposal (default 4)
              --bridge-bytes N          Payload bridge bytes at each free-frame edge (default 2)
              --beam-width N            Base EFMPlus prefix paths per Sync Frame (default 8)
              --retry-beam-width N      Beam for failed single-frame retries (default 32; 0 disables)
              --retry-frame-fraction N  Worst-frame fraction attempted per pass; 0 means all (default 0.25)
              --batch-frames N          Frames per ECC-closed candidate batch (default 8)
              --batch-candidates N      Fully rebuilt candidates per batch (default 4)
              --search-lanes 1..8       Deterministic independent restarts; exact best wins (default 1)
              --seed N                  Deterministic random seed (default 1)
              --initial random|zero     Initial payload mode (default random)
              --seed-payload FILE       Initial 32768-byte payload
              --channel-output FILE     Also write packed final NRZI levels

            Target format:
              38688 symbols for one sector or 619008 for a full ECC Block.
              0/P = pit/black, 1/L = land/white, ? = unconstrained; whitespace is ignored.

            State-control chooses payload bytes directly from image-ranked EFMPlus code words and
            keeps a beam of exact dual-stream prefix states. Search is driven by controllable payload
            words; the reported majority/gray scores cover every 16-bit channel word, including
            fixed/EDC/PI/PO and both SYNC words. Stream, DSV and final boundaries remain exact.

            Dispersion maps luminance bins 0..127 and 128..255 to scrambled
            EFMPlus input bytes 0xA5 and 0x92 (5 and 2 transitions), then
            reverses DVD payload scrambling for ISO output. Nominal preview levels are 0 and 255.
            It samples every payload code-word center regardless of --constraint-step and
            --image-threshold. These values are not calibrated optical luminance. Opaque
            black/white images use nearest-neighbor sampling to preserve their dither pattern.
            Other images retain alpha-weighted bilinear sampling. Q=0x66 and 0xB9 are not
            generated; old ISOs retain readback of 66 as black and B9 as nominal dark gray 85.
            With --fast-output true, payload selection is unchanged but final ECC/EFM boundary and
            DSV verification are skipped; the JSON finalBoundary is therefore null.

            Hybrid --data-dir mode builds a mountable ISO9660/Joliet filesystem. Files default to the inner ring;
            --file-position outer reserves the outer ring for files and draws between front metadata and files.
            Drawing boundaries align to ECC Blocks and never overwrite the filesystem.
            It defaults to dispersion plus fast output. --lba and --fill-sectors are
            automatic in this mode; use --drawing-start-lba to leave additional inner-ring space.

            Calibration options:
              --total-sectors N
              --generated-inner-radius-mm N
              --generated-outer-radius-mm N
              --actual-inner-radius-mm N
              --actual-outer-radius-mm N
              --pitch-linear N          Generated linear pitch coefficient (default 0)
              --pitch-quadratic N       Generated quadratic pitch coefficient (default 0)
              --pitch-cubic N           Generated cubic pitch coefficient (default 0)
                                        --generated-pitch-* aliases are also accepted
              --actual-pitch-linear N   Actual linear pitch coefficient (default 0)
              --actual-pitch-quadratic N Actual quadratic pitch coefficient (default 0)
              --actual-pitch-cubic N    Actual cubic pitch coefficient (default 0)
              --channel-bit-nm N        Default 133.3
              --spiral-direction ccw|cw Default cw; no rotation-offset calibration
              --image-threshold 0..255  Accepted for compatibility; black/white preview uses 128
              --alpha-threshold 1..255  Default 1
              --preview-size 64..8192   Square PNG size (default 1024)
              --samples-per-sector 1..4096 Forward geometry samples per sector (default 16)
              --lba N                   First visible sector (default 0)
              --fill-sectors N          Visible sector count (default: remainder of disc)

            Simulation options (source is the generated ISO, not an image):
              --iso FILE                Generated ISO image (required)
              --inner-radius-mm N       Measured inner radius of the burned spiral
              --outer-radius-mm N       Measured outer radius of the burned spiral
              --pitch-linear N          Measured linear pitch coefficient (default 0)
              --pitch-quadratic N       Measured quadratic pitch coefficient (default 0)
              --pitch-cubic N           Measured cubic pitch coefficient (default 0)
              --total-sectors N         Disc sector count; defaults to the solve sidecar
                                        JSON (IMAGE.iso.json) or the ISO file size
              --psn-offset N            Decimal or 0x-prefixed PSN offset (default 0x30000)
              --lba N                   First visible sector; defaults to the sidecar
                                        drawing start (0 when absent)
              --fill-sectors N          Visible sector count; defaults to the sidecar
                                        drawing length (remainder of the ISO when absent)
              --channel-bit-nm N        Default 133.3
              --spiral-direction ccw|cw Default cw
              --preview-size 64..8192   Square PNG size (default 1024)
              --samples-per-sector 1..2048 Payload bytes sampled per sector (default 16)

            """);
    }
}
