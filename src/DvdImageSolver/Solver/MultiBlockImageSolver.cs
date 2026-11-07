using System.Diagnostics;
using DiscImageStudio.Core;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed record MultiBlockSolveOptions(
    uint StartLba,
    uint FillSectors,
    uint PsnOffset,
    int IterationsPerBlock,
    int MutationBytes,
    int RandomSeed,
    double InitialTemperature,
    double FinalTemperature,
    bool ZeroInitialPayload,
    ImageMappingOptions ImageMapping,
    SolverAlgorithm Algorithm = SolverAlgorithm.Full,
    int BridgeBytes = 2,
    int BeamWidth = 8,
    int RetryBeamWidth = 32,
    double RetryFrameFraction = 0.25,
    int BatchFrames = 8,
    int BatchCandidates = 4,
    int SearchLanes = 1,
    int FastParallelism = 0,
    bool PreserveExistingIso = false,
    bool UseSampleReuse = true,
    bool ReadCompatibility = false,
    string ReadCompatibilityMode = DvdReadCompatibilityModes.Current)
{
    /// <summary>Use four-double raster sampling in fast output when supported; false retains the scalar reference path.</summary>
    public bool UseSimdSampling { get; init; } = true;
}

public sealed record MultiBlockProgress(
    int CompletedBlocks,
    int TotalBlocks,
    uint BlockLba,
    int Score,
    int ScoreMaximum,
    string ScoreMetric,
    ModulationBoundary FinalBoundary,
    TimeSpan Elapsed)
{
    public int? GrayScore { get; init; }

    public int? GrayScoreMaximum { get; init; }

    public string? GrayScoreMetric { get; init; }

    public int? ControlledScore { get; init; }

    public int? ControlledScoreMaximum { get; init; }

    public int? ControlledGrayScore { get; init; }

    public int? ControlledGrayScoreMaximum { get; init; }
}

public sealed record MultiBlockSolveResult(
    int CompletedBlocks,
    uint FilledSectors,
    long TotalScore,
    long TotalScoreMaximum,
    string ScoreMetric,
    ModulationBoundary FinalBoundary,
    TimeSpan Elapsed,
    bool SparseIso,
    uint IsoSectors)
{
    public long? TotalGrayScore { get; init; }

    public long? TotalGrayScoreMaximum { get; init; }

    public string? GrayScoreMetric { get; init; }

    public long? TotalControlledScore { get; init; }

    public long? TotalControlledScoreMaximum { get; init; }

    public long? TotalControlledGrayScore { get; init; }

    public long? TotalControlledGrayScoreMaximum { get; init; }
}

internal static class MultiBlockImageSolver
{
    internal static MultiBlockSolveResult Solve(
        string imagePath,
        string isoOutputPath,
        ModulationBoundary initialBoundary,
        MultiBlockSolveOptions options,
        Action<MultiBlockProgress>? progress)
    {
        Validate(options);
        RasterImage image = RasterImage.Load(imagePath);
        int totalBlocks = checked((int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        ModulationBoundary boundary = initialBoundary;
        long totalScore = 0;
        long totalScoreMaximum = 0;
        long totalGrayScore = 0;
        long totalGrayScoreMaximum = 0;
        long totalControlledScore = 0;
        long totalControlledScoreMaximum = 0;
        long totalControlledGrayScore = 0;
        long totalControlledGrayScoreMaximum = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        uint isoSectors = options.PreserveExistingIso
            ? options.ImageMapping.OutputSectors
            : checked(options.StartLba + options.FillSectors);
        using RawIsoImageWriter iso = new(
            isoOutputPath,
            isoSectors,
            options.PreserveExistingIso);
        for (int blockIndex = 0; blockIndex < totalBlocks; blockIndex++)
        {
            uint lba = checked(options.StartLba + ((uint)blockIndex * DvdEccBlockEncoder.SectorCount));
            ImageConstraint constraint = ImageTargetMapper.Map(image, lba, options.ImageMapping);
            int blockSeed = unchecked(options.RandomSeed + (blockIndex * 1_000_003));
            byte[] initialPayload = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
            if (!options.ZeroInitialPayload)
            {
                new Random(blockSeed).NextBytes(initialPayload);
            }

            SolverOptions solverOptions = new(
                options.IterationsPerBlock,
                options.MutationBytes,
                blockSeed,
                options.InitialTemperature,
                options.FinalTemperature,
                options.Algorithm,
                options.BridgeBytes,
                options.BeamWidth,
                options.RetryBeamWidth,
                options.RetryFrameFraction,
                options.BatchFrames,
                options.BatchCandidates,
                options.SearchLanes);
            SolverResult result = SolverEngine.Solve(
                constraint,
                initialPayload,
                lba,
                options.PsnOffset,
                boundary,
                solverOptions);
            iso.WriteBlock(lba, result.Payloads);
            boundary = result.Encoded.FinalBoundary;
            totalScore += result.Score;
            totalScoreMaximum += result.ScoreMaximum;
            totalGrayScore += result.GrayScore ?? 0;
            totalGrayScoreMaximum += result.GrayScoreMaximum ?? 0;
            totalControlledScore += result.ControlledScore ?? 0;
            totalControlledScoreMaximum += result.ControlledScoreMaximum ?? 0;
            totalControlledGrayScore += result.ControlledGrayScore ?? 0;
            totalControlledGrayScoreMaximum += result.ControlledGrayScoreMaximum ?? 0;
            progress?.Invoke(new MultiBlockProgress(
                blockIndex + 1,
                totalBlocks,
                lba,
                result.Score,
                result.ScoreMaximum,
                result.ScoreMetric,
                boundary,
                stopwatch.Elapsed)
            {
                GrayScore = result.GrayScore,
                GrayScoreMaximum = result.GrayScoreMaximum,
                GrayScoreMetric = result.GrayScoreMetric,
                ControlledScore = result.ControlledScore,
                ControlledScoreMaximum = result.ControlledScoreMaximum,
                ControlledGrayScore = result.ControlledGrayScore,
                ControlledGrayScoreMaximum = result.ControlledGrayScoreMaximum,
            });
        }

        stopwatch.Stop();
        bool hasGrayScore = options.Algorithm is SolverAlgorithm.StateControl
            or SolverAlgorithm.Dispersion;
        return new MultiBlockSolveResult(
            totalBlocks,
            options.FillSectors,
            totalScore,
            totalScoreMaximum,
            options.Algorithm switch
            {
                SolverAlgorithm.StateControl => "all-channel-word-majority-error",
                SolverAlgorithm.Dispersion => "payload-transition-class-error",
                _ => "bit-mismatch",
            },
            boundary,
            stopwatch.Elapsed,
            iso.SparseFile,
            isoSectors)
        {
            TotalGrayScore = hasGrayScore
                ? totalGrayScore
                : null,
            TotalGrayScoreMaximum = hasGrayScore
                ? totalGrayScoreMaximum
                : null,
            GrayScoreMetric = options.Algorithm switch
            {
                SolverAlgorithm.StateControl => "all-channel-word-gray-error",
                SolverAlgorithm.Dispersion => "payload-transition-count-error",
                _ => null,
            },
            TotalControlledScore = options.Algorithm == SolverAlgorithm.StateControl
                ? totalControlledScore
                : null,
            TotalControlledScoreMaximum = options.Algorithm == SolverAlgorithm.StateControl
                ? totalControlledScoreMaximum
                : null,
            TotalControlledGrayScore = options.Algorithm == SolverAlgorithm.StateControl
                ? totalControlledGrayScore
                : null,
            TotalControlledGrayScoreMaximum = options.Algorithm == SolverAlgorithm.StateControl
                ? totalControlledGrayScoreMaximum
                : null,
        };
    }

    private static void Validate(MultiBlockSolveOptions options)
    {
        if (options.ReadCompatibility)
            throw new ArgumentException("Read compatibility requires fast dispersion output.", nameof(options));

        if (options.FillSectors < DvdEccBlockEncoder.SectorCount
            || options.FillSectors % DvdEccBlockEncoder.SectorCount != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Range fill sector count must be a positive multiple of 16.");
        }

        if (options.ImageMapping.OutputSectors == 0
            || options.ImageMapping.OutputSectors > options.ImageMapping.TotalSectors
            || !double.IsFinite(options.ImageMapping.SamplingOuterRadiusMm)
            || options.ImageMapping.SamplingOuterRadiusMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Output capacity and image radius must lie inside the disc geometry.");

        if ((ulong)options.StartLba + options.FillSectors > options.ImageMapping.OutputSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Filled range exceeds the reported disc size.");
        }

        uint firstPsn = checked(options.StartLba + options.PsnOffset);
        if ((firstPsn & 0xF) != 0)
        {
            throw new ArgumentException("The first PSN must be aligned to a 16-sector ECC Block.");
        }

        if (options.IterationsPerBlock < 0
            || options.MutationBytes < 1
            || options.BridgeBytes is < 0 or > EfmPlusEncoder.BytesPerSyncFrame
            || options.BeamWidth is < 1 or > 4096
            || options.RetryBeamWidth is < 0 or > 4096
            || double.IsNaN(options.RetryFrameFraction)
            || options.RetryFrameFraction is < 0 or > 1
            || options.BatchFrames is < 1 or > DvdEccBlockEncoder.SyncFrameCount
            || options.BatchCandidates is < 1 or > 64
            || options.SearchLanes is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid solver iteration, mutation, bridge, beam, or adaptive retry option.");
        }
    }
}
