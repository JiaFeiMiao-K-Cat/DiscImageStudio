using System.Diagnostics;
using DiscImageStudio.Core;
using DiscImageStudio.Iso;
using DiscImageStudio.Core.Calibration;
using DvdImageSolver;
using DvdImageSolver.Encoding;
using DvdImageSolver.Solver;

namespace DiscImageStudio.Dvd;

/// <summary>A half-open, ECC-aligned run of sectors that carries file data instead of drawing.</summary>
public readonly record struct DvdFileSegment(uint StartLba, uint EndLbaExclusive)
{
    public uint Sectors => EndLbaExclusive - StartLba;
}

/// <summary>Blank-area capacity of a picture against the size of the data folder, in 2048-byte sectors.</summary>
public readonly record struct DvdTransparencyCapacity(
    ulong BlankSectors, uint NeededSectors, int BlankRuns, int SegmentsUsed)
{
    private const long SectorBytes = 2048;

    public bool Fits => (ulong)NeededSectors <= BlankSectors;

    public long BlankBytes => checked((long)BlankSectors * SectorBytes);

    public long NeededBytes => checked((long)NeededSectors * SectorBytes);

    /// <summary>Spare bytes when the files fit, otherwise the negative shortfall.</summary>
    public long RemainingBytes => BlankBytes - NeededBytes;
}

public sealed record DvdStreamingOptions(
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double ChannelBitLengthNm = 133.3,
    double StartAngleDegrees = 0,
    byte LuminanceThreshold = 128,
    byte AlphaThreshold = 1,
    int RandomSeed = 1,
    int FastParallelism = 0,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    ToneProfile? Tone = null,
    bool UseSampleReuse = true)
{
    /// <summary>Physical channel-bit length of the DVD scheme; the UI no longer exposes it.</summary>
    public const double StandardChannelBitLengthNm = 133.3;

    public long ContentLength => checked(
        (long)TotalSectors * DvdEccBlockEncoder.PayloadBytesPerSector);

    public void Validate()
    {
        DvdTrackGeometry.ValidatePitchCoefficients(PitchLinear, PitchQuadratic, PitchCubic);
        if (Tone is { } tone)
        {
            tone.Validate();
            if (!tone.IsDvdCompatible)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Tone),
                    "DVD dispersion uses two textures; tone levels must be 2.");
            }
        }
        if (TotalSectors == 0 || TotalSectors % DvdEccBlockEncoder.SectorCount != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TotalSectors),
                "DVD streaming sector count must be a positive multiple of 16.");
        }

        if (!double.IsFinite(InnerRadiusMm)
            || !double.IsFinite(OuterRadiusMm)
            || InnerRadiusMm <= 0
            || OuterRadiusMm <= InnerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OuterRadiusMm),
                "DVD outer radius must be greater than its positive inner radius.");
        }

        if (!double.IsFinite(ChannelBitLengthNm) || ChannelBitLengthNm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChannelBitLengthNm),
                "DVD channel-bit length must be positive.");
        }

        if (!double.IsFinite(StartAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(StartAngleDegrees));
        }

        if (FastParallelism is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FastParallelism),
                "DVD streaming parallelism must be automatic (0) or 1..64.");
        }

        _ = ContentLength;
        // Resolve the complete curve before a hybrid stream writes its filesystem
        // prefix. Positive coefficients alone do not guarantee numerical resolution.
        double trackLengthMm = TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * ChannelBitLengthNm * 1e-6;
        _ = DvdTrackGeometry.Create(InnerRadiusMm, OuterRadiusMm, trackLengthMm,
            PitchLinear, PitchQuadratic, PitchCubic);
    }
}

public sealed record DvdStreamingProgress(
    long CompletedBytes,
    long TotalBytes,
    int CompletedBlocks,
    int TotalBlocks,
    TimeSpan Elapsed)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

public sealed record DvdStreamingSummary(
    string SourceImage,
    uint Sectors,
    long BytesWritten,
    long ControlledWords,
    TimeSpan Elapsed);

public sealed class DvdHybridStreamingPlan
{
    internal DvdHybridStreamingPlan(
        HybridIsoImageWriter.LayoutPlan isoLayout,
        uint drawingStartLba,
        uint drawingEndLbaExclusive,
        IReadOnlyList<DvdFileSegment>? fileSegments = null)
    {
        IsoLayout = isoLayout;
        DrawingStartLba = drawingStartLba;
        DrawingEndLbaExclusive = drawingEndLbaExclusive;
        FileSegments = fileSegments ?? [];
    }

    /// <summary>True when files sit in blank parts of the picture, interleaved with the drawing.</summary>
    public bool FilesInTransparentRegion => FileSegments.Count > 0;

    /// <summary>ECC-aligned, ascending sector runs inside the drawing range that carry file data.</summary>
    public IReadOnlyList<DvdFileSegment> FileSegments { get; }

    internal HybridIsoImageWriter.LayoutPlan IsoLayout { get; }

    public string SourceDirectory => IsoLayout.SourceDirectory;

    public uint TotalSectors => IsoLayout.VolumeSectors;

    public uint FilesystemEndLbaExclusive => IsoLayout.DataEndLbaExclusive;

    public uint DrawingStartLba { get; }

    public uint DrawingEndLbaExclusive { get; }

    public bool FilesAtOuterRing => IsoLayout.FilesAtOuterRing;

    public uint DrawingSectors => checked(DrawingEndLbaExclusive - DrawingStartLba);

    public int FileCount => IsoLayout.FileCount;

    public int DirectoryCount => IsoLayout.DirectoryCount;

    public long FileBytes => IsoLayout.FileBytes;
}

public static class DvdStreamingGenerator
{
    private const uint PsnOffset = 0x30000;

    public static DvdHybridStreamingPlan PrepareHybrid(
        string dataDirectory,
        DvdStreamingOptions options,
        string volumeLabel = "DVD_IMAGE",
        bool filesAtOuterRing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        HybridIsoImageWriter.LayoutPlan isoLayout = HybridIsoImageWriter.Plan(
            dataDirectory,
            options.TotalSectors,
            volumeLabel,
            filesAtOuterRing);
        uint drawingStartLba = AlignToEccBlock(filesAtOuterRing
            ? isoLayout.MetadataEndLbaExclusive : isoLayout.DataEndLbaExclusive, PsnOffset);
        uint drawingEndLba = filesAtOuterRing ? isoLayout.FileDataStartLba / 16 * 16 : options.TotalSectors;
        if (drawingEndLba < drawingStartLba || drawingEndLba - drawingStartLba < DvdEccBlockEncoder.SectorCount)
        {
            throw new ArgumentException(
                "The filesystem leaves no complete ECC Block for drawing in the selected file layout.",
                nameof(dataDirectory));
        }

        return new DvdHybridStreamingPlan(isoLayout, drawingStartLba, drawingEndLba);
    }

    /// <summary>
    /// Estimates how much of the data folder fits into the blank parts of the picture.
    /// Uses the same scan and headroom as <see cref="PrepareHybridInTransparency"/>.
    /// </summary>
    public static DvdTransparencyCapacity EstimateTransparentCapacity(
        string dataDirectory,
        string imagePath,
        DvdStreamingOptions options,
        string volumeLabel = "DVD_IMAGE",
        CancellationToken cancellationToken = default,
        bool whiteIsBlank = true,
        bool backgroundIsBlank = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        HybridIsoImageWriter.LayoutPlan probe = HybridIsoImageWriter.Plan(
            dataDirectory, options.TotalSectors, volumeLabel);
        uint scanFrom = AlignToEccBlock(probe.MetadataEndLbaExclusive, PsnOffset);
        uint fileSectors = probe.DataEndLbaExclusive - probe.MetadataEndLbaExclusive;
        uint needed = fileSectors == 0 ? 0 : checked(fileSectors + 32);
        IReadOnlyList<DvdFreeRegion> runs = DvdTransparencyScanner.FindFreeRuns(
            imagePath, CreateImageMapping(options), scanFrom, cancellationToken,
            whiteIsBlank ? DvdTransparencyScanner.DefaultWhiteLuminance : DvdTransparencyScanner.TransparentOnly,
            backgroundIsBlank);
        ulong blank = DvdTransparencyScanner.TotalSectors(runs);
        int segmentsUsed = needed == 0
            ? 0
            : DvdTransparencyScanner.ChooseSegments(runs, needed)?.Count ?? runs.Count;
        return new DvdTransparencyCapacity(blank, needed, runs.Count, segmentsUsed);
    }

    /// <summary>
    /// Lays the files out in the blank (transparent, and optionally white) parts of the picture.
    /// A file may be split over many runs, so the picture is never overwritten; the drawing
    /// keeps its full-disc geometry and only skips the sectors that carry file data.
    /// </summary>
    public static DvdHybridStreamingPlan PrepareHybridInTransparency(
        string dataDirectory,
        string imagePath,
        DvdStreamingOptions options,
        string volumeLabel = "DVD_IMAGE",
        CancellationToken cancellationToken = default,
        bool whiteIsBlank = true,
        bool backgroundIsBlank = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        HybridIsoImageWriter.LayoutPlan probe = HybridIsoImageWriter.Plan(
            dataDirectory, options.TotalSectors, volumeLabel);
        uint scanFrom = AlignToEccBlock(probe.MetadataEndLbaExclusive, PsnOffset);
        uint fileSectors = probe.DataEndLbaExclusive - probe.MetadataEndLbaExclusive;
        if (fileSectors == 0)
            return new DvdHybridStreamingPlan(probe, scanFrom, options.TotalSectors);

        // Extra extents enlarge the directory records, so keep a little headroom.
        uint needed = checked(fileSectors + 32);
        IReadOnlyList<DvdFreeRegion> runs = DvdTransparencyScanner.FindFreeRuns(
            imagePath, CreateImageMapping(options), scanFrom, cancellationToken,
            whiteIsBlank ? DvdTransparencyScanner.DefaultWhiteLuminance : DvdTransparencyScanner.TransparentOnly,
            backgroundIsBlank);
        IReadOnlyList<DvdFreeRegion> chosen = DvdTransparencyScanner.ChooseSegments(runs, needed)
            ?? throw new ArgumentException(
                $"The picture leaves only {DvdTransparencyScanner.TotalSectors(runs)} blank sectors "
                + $"but the files need {needed}.",
                nameof(imagePath));
        HybridIsoImageWriter.LayoutPlan isoLayout = HybridIsoImageWriter.Plan(
            dataDirectory, options.TotalSectors, volumeLabel,
            candidateSegments: chosen.Select(run => new Iso9660ImageWriter.IsoSegment(run.StartLba, run.EndLbaExclusive)).ToList());
        uint drawingStartLba = AlignToEccBlock(isoLayout.MetadataEndLbaExclusive, PsnOffset);
                List<DvdFileSegment> segments = [];
                uint previousEnd = drawingStartLba;
                foreach (Iso9660ImageWriter.IsoSegment used in isoLayout.FileSegments)
                {
                    // File data replaces single sectors of the drawn blocks, so no block alignment is needed.
                    if (used.StartLba < previousEnd || used.EndLbaExclusive > options.TotalSectors)
                        throw new InvalidOperationException("File segments overlap the metadata or leave the disc.");
                    segments.Add(new DvdFileSegment(used.StartLba, used.EndLbaExclusive));
                    previousEnd = used.EndLbaExclusive;
                }

                return new DvdHybridStreamingPlan(isoLayout, drawingStartLba, options.TotalSectors, segments);
    }

    public static DvdStreamingSummary Generate(
        string imagePath,
        Stream output,
        DvdStreamingOptions options,
        Action<DvdStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        string sourceFullPath = Path.GetFullPath(imagePath);
        MultiBlockSolveOptions solveOptions = CreateSolveOptions(
            options,
            startLba: 0,
            fillSectors: options.TotalSectors);
        FastDvdStreamSummary summary = FastDispersionImageWriter.WriteStream(
            sourceFullPath,
            output,
            solveOptions,
            value => progress?.Invoke(new DvdStreamingProgress(
                value.BytesWritten,
                value.TotalBytes,
                value.CompletedBlocks,
                value.TotalBlocks,
                value.Elapsed)),
            cancellationToken);
        return new DvdStreamingSummary(
            sourceFullPath,
            summary.SectorsWritten,
            summary.BytesWritten,
            summary.ControlledWords,
            summary.Elapsed);
    }

    /// <summary>
    /// Generates calibration payload sectors directly from an analytic grayscale
    /// pattern. Coordinates are millimetres from the disc centre, x right and y down.
    /// The callback must support concurrent calls when parallel generation is enabled.
    /// </summary>
    public static DvdStreamingSummary GeneratePattern(
        Func<double, double, byte> sampleGray,
        Stream output,
        DvdStreamingOptions options,
        Action<DvdStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampleGray);
        return GeneratePattern(sampleGray, null, output, options, progress, cancellationToken);
    }

    public static DvdStreamingSummary GeneratePattern(
        CalibrationTarget target, Stream output, DvdStreamingOptions options,
        Action<DvdStreamingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        if (target.Parameters.Kind != CalibrationDiscKind.Dvd || target.Parameters.Sectors != options.TotalSectors)
            throw new ArgumentException("Calibration target and output must describe the same DVD sector capacity.", nameof(target));
        if (target.UsesReferencePattern)
        {
            ArgumentNullException.ThrowIfNull(output);
            options.Validate();
            MultiBlockSolveOptions solveOptions = CreateSolveOptions(options, 0, options.TotalSectors);
            FastDvdStreamSummary summary = FastDispersionImageWriter.WriteCalibrationReferenceStream(
                target, output, solveOptions,
                value => progress?.Invoke(new DvdStreamingProgress(value.BytesWritten, value.TotalBytes,
                    value.CompletedBlocks, value.TotalBlocks, value.Elapsed)), cancellationToken);
            return new DvdStreamingSummary("calibration", summary.SectorsWritten, summary.BytesWritten,
                summary.ControlledWords, summary.Elapsed);
        }
        if (!target.HasWideAddressBands)
            return GeneratePattern(target.Sample, output, options, progress, cancellationToken);

        var bands = target.WideBandSectorRanges;
        var angleSweep = target.HasAngleSweep ? new CalibrationAngleSweepSampler(target) : null;
        byte SectorLuminance(uint sector)
        {
            if (angleSweep is not null && angleSweep.TryGetLuminance((sector + 0.5) / options.TotalSectors, out var marker))
                return marker;
            for (int i = 0; i < bands.Count; i++)
                if (sector >= bands[i].Start && sector < bands[i].EndExclusive)
                    return 0;
            return 255;
        }
        return GeneratePattern(target.Sample, SectorLuminance, output, options, progress, cancellationToken);
    }

    private static DvdStreamingSummary GeneratePattern(
        Func<double, double, byte> sampleGray, Func<uint, byte>? sectorLuminance,
        Stream output, DvdStreamingOptions options,
        Action<DvdStreamingProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        MultiBlockSolveOptions solveOptions = CreateSolveOptions(
            options,
            startLba: 0,
            fillSectors: options.TotalSectors);
        void Report(FastDvdStreamProgress value) => progress?.Invoke(new DvdStreamingProgress(
            value.BytesWritten, value.TotalBytes, value.CompletedBlocks, value.TotalBlocks, value.Elapsed));
        FastDvdStreamSummary summary = sectorLuminance is null
            ? FastDispersionImageWriter.WritePatternStream(sampleGray, output, solveOptions, Report, cancellationToken)
            : FastDispersionImageWriter.WriteSectorPatternStream(sectorLuminance, output, solveOptions, Report, cancellationToken);
        return new DvdStreamingSummary(
            "calibration",
            summary.SectorsWritten,
            summary.BytesWritten,
            summary.ControlledWords,
            summary.Elapsed);
    }

    public static DvdStreamingSummary GenerateHybrid(
        string imagePath,
        Stream output,
        DvdStreamingOptions options,
        DvdHybridStreamingPlan plan,
        Action<DvdStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);
        options.Validate();
        if (plan.TotalSectors != options.TotalSectors)
        {
            throw new ArgumentException(
                "Hybrid layout and DVD streaming options describe different disc sizes.",
                nameof(plan));
        }

        string sourceFullPath = Path.GetFullPath(imagePath);
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (plan.FilesInTransparentRegion)
            return GenerateHybridInRegion(sourceFullPath, output, options, plan, progress, stopwatch, cancellationToken);
        if (plan.FilesAtOuterRing)
            HybridIsoImageWriter.WriteOuterRingPrefix(plan.IsoLayout, output, plan.DrawingStartLba, cancellationToken);
        else
            HybridIsoImageWriter.WriteSequentialPrefix(plan.IsoLayout, output, plan.DrawingStartLba, cancellationToken);
        long prefixBytes = checked(
            (long)plan.DrawingStartLba * DvdEccBlockEncoder.PayloadBytesPerSector);
        int totalBlocks = checked((int)(options.TotalSectors / DvdEccBlockEncoder.SectorCount));
        progress?.Invoke(new DvdStreamingProgress(
            prefixBytes,
            options.ContentLength,
            checked((int)(plan.DrawingStartLba / DvdEccBlockEncoder.SectorCount)),
            totalBlocks,
            stopwatch.Elapsed));

        MultiBlockSolveOptions solveOptions = CreateSolveOptions(
            options,
            plan.DrawingStartLba,
            plan.DrawingSectors);
        FastDvdStreamSummary drawing = FastDispersionImageWriter.WriteStream(
            sourceFullPath,
            output,
            solveOptions,
            value => progress?.Invoke(new DvdStreamingProgress(
                checked(prefixBytes + value.BytesWritten),
                options.ContentLength,
                checked((int)(plan.DrawingStartLba / DvdEccBlockEncoder.SectorCount)
                    + value.CompletedBlocks),
                totalBlocks,
                stopwatch.Elapsed)),
            cancellationToken);
        if (plan.FilesAtOuterRing)
        {
            HybridIsoImageWriter.WriteOuterRingSuffix(plan.IsoLayout, output, plan.DrawingEndLbaExclusive, cancellationToken);
            progress?.Invoke(new DvdStreamingProgress(options.ContentLength, options.ContentLength,
                totalBlocks, totalBlocks, stopwatch.Elapsed));
        }
        stopwatch.Stop();
        return new DvdStreamingSummary(
            sourceFullPath,
            options.TotalSectors,
            options.ContentLength,
            drawing.ControlledWords,
            stopwatch.Elapsed);
    }

    private static DvdStreamingSummary GenerateHybridInRegion(
        string sourceFullPath,
        Stream output,
        DvdStreamingOptions options,
        DvdHybridStreamingPlan plan,
        Action<DvdStreamingProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        HybridIsoImageWriter.WriteRegionPrefix(plan.IsoLayout, output, plan.DrawingStartLba, cancellationToken);
        int totalBlocks = checked((int)(options.TotalSectors / DvdEccBlockEncoder.SectorCount));
        long prefixBytes = checked((long)plan.DrawingStartLba * DvdEccBlockEncoder.PayloadBytesPerSector);
        int prefixBlocks = checked((int)(plan.DrawingStartLba / DvdEccBlockEncoder.SectorCount));
        List<(uint, uint)> fileSectors = plan.FileSegments.Select(s => (s.StartLba, s.EndLbaExclusive)).ToList();

        using Iso9660ImageWriter.FileRegionWriter files =
            HybridIsoImageWriter.CreateFileRegionWriter(plan.IsoLayout, cancellationToken);
        MultiBlockSolveOptions solveOptions = CreateSolveOptions(
            options, plan.DrawingStartLba, plan.DrawingSectors);
        // File sectors are fixed before solving each mixed ECC block; no payload is overwritten
        // after the block's ECC/EFM calculation.
        FastDvdStreamSummary drawing = FastDispersionImageWriter.WriteStreamEccAware(
            sourceFullPath, output, solveOptions, fileSectors,
            (startLba, sectors) => files.FillSectors(sectors),
            new ModulationBoundary(1, 0, IsLand: true),
            value => progress?.Invoke(new DvdStreamingProgress(
                checked(prefixBytes + value.BytesWritten),
                options.ContentLength,
                checked(prefixBlocks + value.CompletedBlocks),
                totalBlocks,
                stopwatch.Elapsed)),
            cancellationToken,
            replayPhysicalBoundary: true);
        if (!files.Completed)
            throw new InvalidOperationException("File data did not fit in the planned segments.");
        progress?.Invoke(new DvdStreamingProgress(options.ContentLength, options.ContentLength,
            totalBlocks, totalBlocks, stopwatch.Elapsed));
        stopwatch.Stop();
        return new DvdStreamingSummary(
            sourceFullPath, options.TotalSectors, options.ContentLength, drawing.ControlledWords, stopwatch.Elapsed);
    }

    private static ImageMappingOptions CreateImageMapping(DvdStreamingOptions options)
    {
        ImageMappingOptions imageMapping = new(
            options.TotalSectors,
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            options.ChannelBitLengthNm,
            options.StartAngleDegrees,
            Clockwise: true,
            options.LuminanceThreshold,
            options.AlphaThreshold,
            SampleEveryChannelBits: 1,
            PitchLinear: options.PitchLinear,
            PitchQuadratic: options.PitchQuadratic,
            PitchCubic: options.PitchCubic,
            Tone: options.Tone);
        return imageMapping;
    }

    private static MultiBlockSolveOptions CreateSolveOptions(
        DvdStreamingOptions options,
        uint startLba,
        uint fillSectors)
    {
        ImageMappingOptions imageMapping = CreateImageMapping(options);
        return new MultiBlockSolveOptions(
            startLba,
            fillSectors,
            PsnOffset,
            IterationsPerBlock: 1,
            MutationBytes: 4,
            RandomSeed: options.RandomSeed,
            InitialTemperature: 64.0,
            FinalTemperature: 0.25,
            ZeroInitialPayload: false,
            imageMapping,
            Algorithm: SolverAlgorithm.Dispersion,
            FastParallelism: options.FastParallelism,
            UseSampleReuse: options.UseSampleReuse);
    }

    private static uint AlignToEccBlock(uint lba, uint psnOffset)
    {
        uint remainder = (lba + psnOffset) & (DvdEccBlockEncoder.SectorCount - 1);
        return remainder == 0
            ? lba
            : checked(lba + DvdEccBlockEncoder.SectorCount - remainder);
    }
}
