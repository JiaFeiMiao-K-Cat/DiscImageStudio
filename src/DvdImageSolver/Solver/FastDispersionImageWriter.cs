using System.Diagnostics;
using System.Runtime.Intrinsics;
using DiscImageStudio.Core.Calibration;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

/// <summary>
/// A controllable payload word's centre in drawing coordinates, plus its actual
/// 16-channel-bit slot and unwrapped spiral angle. Non-payload slots are retained
/// in GlobalCodewordIndex; SYNC occupies two such slots.
/// </summary>
public readonly record struct DvdCodewordSample(
    double Xmm, double Ymm, ulong GlobalCodewordIndex, double TrackAngleRadians);

public sealed record FastDvdStreamProgress(
    int CompletedBlocks,
    int TotalBlocks,
    uint BlockLba,
    long BytesWritten,
    long TotalBytes,
    TimeSpan Elapsed)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)BytesWritten / TotalBytes;
}

public sealed record FastDvdStreamSummary(
    int CompletedBlocks,
    uint SectorsWritten,
    long BytesWritten,
    long ControlledWords,
    TimeSpan Elapsed)
{
    public ModulationBoundary? FinalBoundary { get; init; }
}

internal sealed record FastDispersionWriteResult(
    int CompletedBlocks,
    uint FilledSectors,
    long ControlledWords,
    TimeSpan Elapsed,
    bool SparseIso,
    uint IsoSectors);

internal sealed record FastDispersionProgress(
    int CompletedBlocks,
    int TotalBlocks,
    uint BlockLba,
    long ControlledWords,
    TimeSpan Elapsed);

public static class FastDispersionImageWriter
{
    private static readonly int[] PayloadChannelCenters = BuildPayloadChannelCenters();

    internal static FastDispersionWriteResult Write(
        string imagePath,
        string isoOutputPath,
        MultiBlockSolveOptions options,
        Action<FastDispersionProgress>? progress)
    {
        Validate(options);
        RasterImage image = RasterImage.Load(imagePath);
        PayloadImageSampler sampler = new(image, options.ImageMapping);
        int totalBlocks = checked((int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        int parallelism = options.FastParallelism == 0
            ? Environment.ProcessorCount
            : options.FastParallelism;
        uint isoSectors = options.PreserveExistingIso
            ? options.ImageMapping.TotalSectors
            : checked(options.StartLba + options.FillSectors);
        long controlledWords = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        using RawIsoImageWriter iso = new(isoOutputPath, isoSectors, options.PreserveExistingIso);
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = parallelism };
        int batchCapacity = Math.Max(1, Math.Min(totalBlocks, Math.Max(1, checked(parallelism * 2))));
        GeneratedBlock[] generated = CreateBatchBuffers(batchCapacity);
        for (int batchStart = 0; batchStart < totalBlocks; batchStart += batchCapacity)
        {
            int batchCount = Math.Min(batchCapacity, totalBlocks - batchStart);
            Parallel.For(0, batchCount, parallelOptions, batchOffset =>
            {
                int blockIndex = batchStart + batchOffset;
                generated[batchOffset] = GenerateBlock(
                    blockIndex, sampler, options, generated[batchOffset].Payloads);
            });

            for (int batchOffset = 0; batchOffset < batchCount; batchOffset++)
            {
                GeneratedBlock block = generated[batchOffset];
                iso.WriteBlock(block.Lba, block.Payloads);
                controlledWords += block.ControlledWords;
                progress?.Invoke(new FastDispersionProgress(
                    batchStart + batchOffset + 1, totalBlocks, block.Lba,
                    controlledWords, stopwatch.Elapsed));
            }
        }

        bool sparseIso = iso.SparseFile;
        iso.Dispose();
        stopwatch.Stop();
        return new FastDispersionWriteResult(
            totalBlocks, options.FillSectors, controlledWords, stopwatch.Elapsed, sparseIso, isoSectors);
    }

    /// <summary>Fills whole 2048-byte sectors; startLba identifies the first sector.</summary>
    public delegate void SectorFill(uint startLba, Span<byte> sectors);

    public static FastDvdStreamSummary WriteStream(
        string imagePath,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateStream(output, options);
        RasterImage image = RasterImage.Load(imagePath);
        PayloadImageSampler sampler = new(image, options.ImageMapping);
        return WriteSampledStream(sampler, output, options, progress, cancellationToken);
    }


    /// <summary>
    /// Generates a hybrid stream without post-generation sector overwrites. Blocks that intersect
    /// file sectors are solved with the full DVD ECC/EFM encoder while locked file sectors remain
    /// immutable; blocks without files retain the parallel fast dispersion path.
    /// </summary>
    public static FastDvdStreamSummary WriteStreamEccAware(
        string imagePath,
        Stream output,
        MultiBlockSolveOptions options,
        IReadOnlyList<(uint StartLba, uint EndLbaExclusive)> fileSectors,
        SectorFill fill,
        ModulationBoundary initialBoundary,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool replayPhysicalBoundary = false)
    {
        ArgumentNullException.ThrowIfNull(fileSectors);
        ArgumentNullException.ThrowIfNull(fill);
        ValidateStream(output, options);
        RasterImage image = RasterImage.Load(imagePath);
        PayloadImageSampler sampler = new(image, options.ImageMapping);
        int totalBlocks = checked((int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        long totalBytes = checked((long)options.FillSectors * DvdEccBlockEncoder.PayloadBytesPerSector);
        long bytesWritten = 0;
        long controlledWords = 0;
        ModulationBoundary boundary = initialBoundary;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int segmentIndex = 0;
        bool[] mixedBlocks = new bool[totalBlocks];
        uint rangeEnd = checked(options.StartLba + options.FillSectors);
        uint previousEnd = options.StartLba;

        foreach ((uint startLba, uint endLbaExclusive) in fileSectors)
        {
            if (startLba < previousEnd || endLbaExclusive <= startLba || endLbaExclusive > rangeEnd)
                throw new ArgumentException(
                    "File sector intervals must be ascending, disjoint and inside the drawing range.",
                    nameof(fileSectors));
            previousEnd = endLbaExclusive;
            uint clippedStart = startLba;
            uint clippedEnd = endLbaExclusive;
            int firstBlock = checked((int)((clippedStart - options.StartLba) / DvdEccBlockEncoder.SectorCount));
            int lastBlock = checked((int)(((clippedEnd - options.StartLba) + DvdEccBlockEncoder.SectorCount - 1)
                / DvdEccBlockEncoder.SectorCount));
            for (int block = firstBlock; block < lastBlock; block++)
                mixedBlocks[block] = true;
        }

        int blockIndex = 0;
        while (blockIndex < totalBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mixedBlocks[blockIndex])
            {
                int runStart = blockIndex;
                while (blockIndex < totalBlocks && !mixedBlocks[blockIndex]) blockIndex++;
                uint runLba = checked(options.StartLba + ((uint)runStart * DvdEccBlockEncoder.SectorCount));
                uint runSectors = checked((uint)(blockIndex - runStart) * DvdEccBlockEncoder.SectorCount);
                FastDvdStreamSummary fast = WriteSampledStream(
                    sampler,
                    output,
                    options with { StartLba = runLba, FillSectors = runSectors },
                    value => progress?.Invoke(new FastDvdStreamProgress(
                        runStart + value.CompletedBlocks,
                        totalBlocks,
                        value.BlockLba,
                        bytesWritten + value.BytesWritten,
                        totalBytes,
                        stopwatch.Elapsed)),
                    cancellationToken,
                    replayPhysicalBoundary ? boundary : null,
                    replayPhysicalBoundary);
                bytesWritten += fast.BytesWritten;
                controlledWords += fast.ControlledWords;
                if (replayPhysicalBoundary)
                    boundary = fast.FinalBoundary
                        ?? throw new InvalidOperationException("Exact physical boundary replay returned no final boundary.");
                continue;
            }

            uint lba = checked(options.StartLba + ((uint)blockIndex * DvdEccBlockEncoder.SectorCount));
            uint blockEnd = lba + DvdEccBlockEncoder.SectorCount;
            while (segmentIndex < fileSectors.Count && fileSectors[segmentIndex].EndLbaExclusive <= lba)
                segmentIndex++;
            int firstSegment = segmentIndex;
            bool[] locked = new bool[DvdEccBlockEncoder.SectorCount];
            for (int index = firstSegment; index < fileSectors.Count && fileSectors[index].StartLba < blockEnd; index++)
            {
                uint from = Math.Max(fileSectors[index].StartLba, lba);
                uint to = Math.Min(fileSectors[index].EndLbaExclusive, blockEnd);
                for (uint sector = from; sector < to; sector++) locked[sector - lba] = true;
            }

            byte[] payloads = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
            int blockSeed = unchecked(options.RandomSeed + (blockIndex * 1_000_003));
            if (!options.ZeroInitialPayload) new Random(blockSeed).NextBytes(payloads);
            for (int index = firstSegment; index < fileSectors.Count && fileSectors[index].StartLba < blockEnd; index++)
            {
                uint from = Math.Max(fileSectors[index].StartLba, lba);
                uint to = Math.Min(fileSectors[index].EndLbaExclusive, blockEnd);
                fill(from, payloads.AsSpan(
                    checked((int)(from - lba) * DvdEccBlockEncoder.PayloadBytesPerSector),
                    checked((int)(to - from) * DvdEccBlockEncoder.PayloadBytesPerSector)));
            }

            if (!locked.All(value => value))
            {
                ImageConstraint constraint = ImageTargetMapper.Map(image, lba, options.ImageMapping)
                    .MaskPayloadSectors(locked);
                SolverOptions solverOptions = new(
                    Iterations: Math.Max(16, options.IterationsPerBlock),
                    MutationBytes: options.MutationBytes,
                    RandomSeed: blockSeed,
                    InitialTemperature: options.InitialTemperature,
                    FinalTemperature: options.FinalTemperature,
                    Algorithm: SolverAlgorithm.Full,
                    BridgeBytes: options.BridgeBytes,
                    BeamWidth: options.BeamWidth,
                    RetryBeamWidth: options.RetryBeamWidth,
                    RetryFrameFraction: options.RetryFrameFraction,
                    BatchFrames: options.BatchFrames,
                    BatchCandidates: options.BatchCandidates,
                    SearchLanes: options.SearchLanes);
                SolverResult result = SolverEngine.Solve(constraint, payloads, lba, options.PsnOffset, boundary, solverOptions);
                payloads = result.Payloads;
                boundary = result.Encoded.FinalBoundary;
                controlledWords += result.ScoreMaximum;
            }
            else
            {
                // Fully file-owned blocks have no free payload bytes to optimize, but they
                // still advance through the same ECC/EFM encoder and modulation boundary.
                EncodedEccBlock encoded = new DvdEccBlockEncoder().Encode(
                    payloads, lba, options.PsnOffset, boundary);
                boundary = encoded.FinalBoundary;
            }

            output.Write(payloads);
            bytesWritten += payloads.Length;
            blockIndex++;
            progress?.Invoke(new FastDvdStreamProgress(
                blockIndex, totalBlocks, lba, bytesWritten, totalBytes, stopwatch.Elapsed));
        }

        output.Flush();
        stopwatch.Stop();
        FastDvdStreamSummary summary = new(totalBlocks, options.FillSectors, bytesWritten, controlledWords, stopwatch.Elapsed);
        return replayPhysicalBoundary ? summary with { FinalBoundary = boundary } : summary;
    }

    /// <summary>
    /// Encodes an opaque analytic pattern without first rasterizing it. The callback
    /// receives millimetres from the disc centre, with x right and y down, and must
    /// support concurrent calls when parallel generation is enabled.
    /// </summary>
    public static FastDvdStreamSummary WritePatternStream(
        Func<double, double, byte> sampleGray,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampleGray);
        ValidateStream(output, options);
        PayloadImageSampler sampler = new(sampleGray, options.ImageMapping);
        return WriteSampledStream(sampler, output, options, progress, cancellationToken);
    }

    /// <summary>Uses the frozen reference image with its original bilinear sampling
    /// and drawing-annulus clipping, including targets loaded from older sessions.</summary>
    public static FastDvdStreamSummary WriteCalibrationReferenceStream(
        CalibrationTarget target, Stream output, MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        if (!target.UsesReferencePattern || target.Parameters.Kind != CalibrationDiscKind.Dvd
            || target.Parameters.Sectors != options.ImageMapping.TotalSectors)
            throw new ArgumentException("The reference target and output must describe the same DVD sector capacity.", nameof(target));
        ValidateStream(output, options);
        PayloadImageSampler sampler = new(target, options.ImageMapping, options.UseSampleReuse);
        return WriteSampledStream(sampler, output, options, progress, cancellationToken);
    }

    /// <summary>Samples on the physical payload word grid without replaying ECC or NRZI.</summary>
    public static FastDvdStreamSummary WriteCodewordPatternStream(
        Func<DvdCodewordSample, byte> sampleGray,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampleGray);
        ValidateStream(output, options);
        PayloadImageSampler sampler = new(sampleGray, options.ImageMapping);
        return WriteSampledStream(sampler, output, options, progress, cancellationToken);
    }

    /// <summary>
    /// Samples an opaque test pattern on the physical payload word grid. The callback
    /// returns the desired scrambled payload byte Q, before EFMPlus modulation;
    /// the output contains user payload P = Q XOR the sector's scrambler mask.
    /// Values are used directly, without grayscale quantization. Coordinates are
    /// millimetres from the disc centre, with x right and y down. The callback must
    /// support concurrent calls when parallel generation is enabled.
    /// This controls payload words only: ECC, headers, SYNC, EFMPlus state, and NRZI
    /// are not replayed here, and no optical brightness is predicted by this API.
    /// </summary>
    public static FastDvdStreamSummary WriteScrambledCodewordPatternStream(
        Func<DvdCodewordSample, byte> sampleScrambledByte,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampleScrambledByte);
        ValidateStream(output, options);
        PayloadImageSampler sampler = new(
            sampleScrambledByte, options.ImageMapping, samplesScrambledBytes: true);
        return WriteSampledStream(sampler, output, options, progress, cancellationToken);
    }

    private static void ValidateStream(Stream output, MultiBlockSolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("DVD output stream must be writable.", nameof(output));
        }

        Validate(options);
        if (options.PreserveExistingIso)
        {
            throw new ArgumentException(
                "Direct DVD streaming cannot preserve or seek within an existing ISO.",
                nameof(options));
        }
    }

    private static FastDvdStreamSummary WriteSampledStream(
        PayloadImageSampler sampler,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress,
        CancellationToken cancellationToken,
        ModulationBoundary? initialBoundary = null,
        bool replayPhysicalBoundary = false)
        => WriteBlockStream((index, buffer) => GenerateBlock(index, sampler, options, buffer, cancellationToken),
            output, options, progress, cancellationToken, initialBoundary, replayPhysicalBoundary);

    /// <summary>Writes an opaque DVD texture selected once per logical sector, before scrambling.</summary>
    public static FastDvdStreamSummary WriteSectorPatternStream(
        Func<uint, byte> sectorLuminance, Stream output, MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sectorLuminance);
        ValidateStream(output, options);
        GeneratedBlock Fill(int index, byte[] buffer)
        {
            uint lba = checked(options.StartLba + (uint)index * DvdEccBlockEncoder.SectorCount);
            for (uint offset = 0; offset < DvdEccBlockEncoder.SectorCount; offset++)
            {
                uint sector = lba + offset;
                var payload = buffer.AsSpan((int)offset * DvdEccBlockEncoder.PayloadBytesPerSector,
                    DvdEccBlockEncoder.PayloadBytesPerSector);
                payload.Fill(DvdGrayscalePalette.GetScrambledByte(sectorLuminance(sector)));
                DvdDataFrameBuilder.TransformPayloadScrambling(payload, sector + options.PsnOffset);
            }
            return new GeneratedBlock(lba, buffer, buffer.Length);
        }
        return WriteBlockStream(Fill, output, options, progress, cancellationToken);
    }

    private static FastDvdStreamSummary WriteBlockStream(
        Func<int, byte[], GeneratedBlock> generateBlock,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress,
        CancellationToken cancellationToken,
        ModulationBoundary? initialBoundary = null,
        bool replayPhysicalBoundary = false)
    {
        if (replayPhysicalBoundary && initialBoundary is null)
            throw new ArgumentException(
                "An initial modulation boundary is required for exact physical replay.",
                nameof(initialBoundary));
        ModulationBoundary boundary = initialBoundary ?? default;
        int totalBlocks = checked((int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        int parallelism = options.FastParallelism == 0
            ? Environment.ProcessorCount
            : options.FastParallelism;
        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken,
        };
        int batchCapacity = Math.Min(totalBlocks, Math.Max(1, checked(parallelism * 2)));
        GeneratedBlock[] generated = CreateBatchBuffers(batchCapacity);
        long totalBytes = checked((long)options.FillSectors * DvdEccBlockEncoder.PayloadBytesPerSector);
        long bytesWritten = 0;
        long controlledWords = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int batchStart = 0; batchStart < totalBlocks; batchStart += batchCapacity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batchCount = Math.Min(batchCapacity, totalBlocks - batchStart);
            Parallel.For(0, batchCount, parallelOptions, batchOffset =>
            {
                int blockIndex = batchStart + batchOffset;
                generated[batchOffset] = generateBlock(blockIndex, generated[batchOffset].Payloads);
            });

            for (int batchOffset = 0; batchOffset < batchCount; batchOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GeneratedBlock block = generated[batchOffset];
                if (replayPhysicalBoundary)
                {
                    boundary = DvdEccBlockEncoder.ReplayPayloadBoundary(
                        block.Payloads,
                        checked(block.Lba + options.PsnOffset),
                        boundary);
                }
                output.Write(block.Payloads);
                bytesWritten = checked(bytesWritten + block.Payloads.Length);
                controlledWords += block.ControlledWords;
                int completedBlocks = batchStart + batchOffset + 1;
                progress?.Invoke(new FastDvdStreamProgress(
                    completedBlocks, totalBlocks, block.Lba, bytesWritten, totalBytes, stopwatch.Elapsed));
            }
        }

        output.Flush();
        stopwatch.Stop();
        FastDvdStreamSummary summary = new(
            totalBlocks, options.FillSectors, bytesWritten, controlledWords, stopwatch.Elapsed);
        return replayPhysicalBoundary ? summary with { FinalBoundary = boundary } : summary;
    }

    private static GeneratedBlock[] CreateBatchBuffers(int count)
    {
        // Parallel.For joins before ordered synchronous writes. Reuse these
        // bounded slots only after the complete previous batch was consumed.
        GeneratedBlock[] blocks = new GeneratedBlock[count];
        for (int index = 0; index < blocks.Length; index++)
            blocks[index] = new GeneratedBlock(0,
                new byte[DvdEccBlockEncoder.PayloadBytesPerBlock], 0);
        return blocks;
    }

    private static GeneratedBlock GenerateBlock(
        int blockIndex,
        PayloadImageSampler sampler,
        MultiBlockSolveOptions options,
        byte[] payloads,
        CancellationToken cancellationToken = default)
    {
        uint lba = checked(
            options.StartLba + ((uint)blockIndex * DvdEccBlockEncoder.SectorCount));
        if (options.ZeroInitialPayload)
        {
            // Alpha-excluded positions retain their initial payload, never a
            // prior block's data from this reusable slot.
            Array.Clear(payloads);
        }
        else
        {
            int blockSeed = unchecked(options.RandomSeed + (blockIndex * 1_000_003));
            new Random(blockSeed).NextBytes(payloads);
        }

        int controlledWords = 0;
        ulong blockFirstGlobalBit = (ulong)lba * DvdEccBlockEncoder.ChannelBitsPerSector;
        uint firstPsn = checked(lba + options.PsnOffset);
        bool useSimd = options.UseSimdSampling && sampler.SupportsSimdSampling;
        bool useReuse = options.UseSampleReuse && sampler.SupportsSampleReuse;
        PixelSample cachedSample = default;
        double reusableUntil = double.NegativeInfinity;
        int reuseMisses = 0;
        int nextReusePayload = 0;
        int reuseCooldown = 256;
        Span<PayloadTarget> samples = stackalloc PayloadTarget[4];
        for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int payloadOffset = sector * DvdEccBlockEncoder.PayloadBytesPerSector;
            Span<byte> sectorPayload = payloads.AsSpan(
                payloadOffset,
                DvdEccBlockEncoder.PayloadBytesPerSector);
            ReadOnlySpan<byte> scrambler = DvdDataFrameBuilder.PayloadScramblerMask(
                firstPsn + checked((uint)sector));
            long toneIndexBase = checked((((long)lba + sector) * DvdEccBlockEncoder.PayloadBytesPerSector));
            int byteIndex = 0;
            while (byteIndex < sectorPayload.Length)
            {
                if (useReuse && payloadOffset + byteIndex >= nextReusePayload)
                {
                    for (; byteIndex < sectorPayload.Length; byteIndex++)
                    {
                        int payloadIndex = payloadOffset + byteIndex;
                        ulong channel = blockFirstGlobalBit + checked((ulong)PayloadChannelCenters[payloadIndex]);
                        if (channel >= reusableUntil)
                        {
                            cachedSample = sampler.SampleWithRegion(channel, out reusableUntil);
                            ulong nextChannel = payloadIndex + 1 < PayloadChannelCenters.Length
                                ? blockFirstGlobalBit + checked((ulong)PayloadChannelCenters[payloadIndex + 1])
                                : ulong.MaxValue;
                            reuseMisses = nextChannel < reusableUntil ? 0 : reuseMisses + 1;
                        }
                        else reuseMisses = 0;
                        if (reuseMisses == 0) reuseCooldown = 256;
                        // Cache raw samples only: tone phase and scrambling retain
                        // each byte's original absolute index and sector mask.
                        PayloadTarget sample = sampler.MapSample(cachedSample, toneIndexBase + byteIndex);
                        if (sample.Alpha >= options.ImageMapping.AlphaThreshold)
                        {
                            sectorPayload[byteIndex] = (byte)(sample.ScrambledByte ^ scrambler[byteIndex]);
                            controlledWords++;
                        }
                        if (reuseMisses >= 8)
                        {
                            // A short detailed edge must not disable reuse for the
                            // remaining block. Retry after bounded SIMD work; sparse
                            // opportunities back off at most one logical sector.
                            nextReusePayload = payloadIndex + 1 + reuseCooldown;
                            reuseCooldown = Math.Min(2048, reuseCooldown * 2);
                            reuseMisses = 0;
                            reusableUntil = double.NegativeInfinity;
                            byteIndex++;
                            break;
                        }
                    }
                }
                int fallbackEnd = useReuse
                    ? Math.Min(sectorPayload.Length, Math.Max(byteIndex, nextReusePayload - payloadOffset))
                    : sectorPayload.Length;
                if (useSimd)
                {
                    // Every lane keeps its actual centre across header/SYNC/ECC gaps.
                    for (; byteIndex <= fallbackEnd - 4; byteIndex += 4)
                    {
                        sampler.SampleFour(blockFirstGlobalBit,
                            PayloadChannelCenters.AsSpan(payloadOffset + byteIndex, 4), samples,
                            toneIndexBase + byteIndex);
                        for (int lane = 0; lane < 4; lane++)
                        {
                            PayloadTarget sample = samples[lane];
                            if (sample.Alpha < options.ImageMapping.AlphaThreshold) continue;
                            sectorPayload[byteIndex + lane] = (byte)(sample.ScrambledByte ^ scrambler[byteIndex + lane]);
                            controlledWords++;
                        }
                    }
                }
                for (; byteIndex < fallbackEnd; byteIndex++)
                {
                    int payloadIndex = payloadOffset + byteIndex;
                    PayloadTarget sample = sampler.SampleGlobalChannelBit(
                        blockFirstGlobalBit + checked((ulong)PayloadChannelCenters[payloadIndex]),
                        toneIndexBase + byteIndex);
                    if (sample.Alpha < options.ImageMapping.AlphaThreshold) continue;
                    sectorPayload[byteIndex] = (byte)(sample.ScrambledByte ^ scrambler[byteIndex]);
                    controlledWords++;
                }
            }
        }

        return new GeneratedBlock(lba, payloads, controlledWords);
    }

    private static int[] BuildPayloadChannelCenters()
    {
        int[] centers = new int[DvdEccBlockEncoder.PayloadBytesPerBlock];
        for (int payloadIndex = 0; payloadIndex < centers.Length; payloadIndex++)
        {
            centers[payloadIndex] = checked(
                DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex) + 8);
        }

        return centers;
    }

    private static void Validate(MultiBlockSolveOptions options)
    {
        if (options.Algorithm != SolverAlgorithm.Dispersion)
        {
            throw new ArgumentException("Fast output requires --algorithm dispersion.");
        }

        if (options.FastParallelism is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Fast parallelism must be 0 (automatic) or 1..64.");
        }

        if (options.FillSectors < DvdEccBlockEncoder.SectorCount
            || options.FillSectors % DvdEccBlockEncoder.SectorCount != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Fast output sector count must be a positive multiple of 16.");
        }

        if ((ulong)options.StartLba + options.FillSectors > options.ImageMapping.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Filled range exceeds the disc size.");
        }

        uint firstPsn = checked(options.StartLba + options.PsnOffset);
        uint finalPsn = checked(firstPsn + options.FillSectors - 1);
        if ((firstPsn & 0xF) != 0)
        {
            throw new ArgumentException("The first PSN must be aligned to a 16-sector ECC Block.");
        }

        if (finalPsn > 0x00FF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The output exceeds the 24-bit PSN range.");
        }
    }

    private readonly record struct GeneratedBlock(
        uint Lba,
        byte[] Payloads,
        int ControlledWords);

    private readonly record struct PayloadTarget(byte ScrambledByte, byte Alpha);

    private sealed class PayloadImageSampler
    {
        private readonly RasterImage? _image;
        private readonly Func<double, double, byte>? _sampleGray;
        private readonly Func<DvdCodewordSample, byte>? _sampleCodeword;
        private readonly Func<DvdCodewordSample, byte>? _sampleScrambledCodeword;
        private readonly ImageMappingOptions _options;
        private readonly ArchimedeanSpiral _spiral;
        private readonly double _bitLengthMm;
        private readonly double _startAngleRadians;
        private readonly double _direction;
        private readonly CalibrationReferenceImageSampler? _referenceImage;
        private readonly bool _supportsReuseGeometry;
        private readonly double _reusePositionMargin;
        private readonly double _reuseTangentMargin;
        private readonly double _reuseQuadraticBound;

        // Analytic callbacks share the exact vector geometry while retaining
        // scalar trigonometry, so even coordinate-sensitive callbacks agree.
        // Constant-pitch batching regresses throughput while Asinh stays scalar;
        // retain its original loop until a separately validated path is faster.
        internal bool SupportsSimdSampling => Vector256.IsHardwareAccelerated
            && !_spiral.IsConstantPitch;

        internal bool SupportsSampleReuse => _supportsReuseGeometry
            && (_referenceImage is not null || _image is not null);

        internal PayloadImageSampler(RasterImage image, ImageMappingOptions options)
            : this(options)
        {
            _image = image;
        }

        internal PayloadImageSampler(Func<double, double, byte> sampleGray, ImageMappingOptions options)
            : this(options)
        {
            _sampleGray = sampleGray;
        }

        internal PayloadImageSampler(CalibrationTarget target, ImageMappingOptions options, bool useSampleReuse)
            : this(options)
        {
            // Disabling reuse preserves the original procedural oracle as well
            // as its exact scalar-trig/coordinate and callback arithmetic.
            if (useSampleReuse) _referenceImage = new(target);
            else _sampleGray = target.Sample;
        }

        internal PayloadImageSampler(
            Func<DvdCodewordSample, byte> sampleCodeword,
            ImageMappingOptions options,
            bool samplesScrambledBytes = false)
            : this(options)
        {
            if (samplesScrambledBytes)
                _sampleScrambledCodeword = sampleCodeword;
            else
                _sampleCodeword = sampleCodeword;
        }

        private PayloadImageSampler(ImageMappingOptions options)
        {
            _options = options;
            _bitLengthMm = options.ChannelBitLengthNm * 1e-6;
            double trackLengthMm = options.TotalSectors
                * (double)DvdEccBlockEncoder.ChannelBitsPerSector
                * _bitLengthMm;
            _spiral = ArchimedeanSpiral.Create(
                options.InnerRadiusMm,
                options.OuterRadiusMm,
                trackLengthMm,
                options.PitchLinear,
                options.PitchQuadratic,
                options.PitchCubic);
            _startAngleRadians = options.StartAngleDegrees * Math.PI / 180.0;
            _direction = options.Clockwise ? -1.0 : 1.0;
            _supportsReuseGeometry = _spiral.TryGetSampleReuseBounds(_startAngleRadians,
                out _reusePositionMargin, out double tangentError, out double accelerationBound);
            _reuseTangentMargin = tangentError + 4 * _reusePositionMargin / _spiral.InnerRadiusMm
                + 128 * 2.2204460492503131e-16;
            _reuseQuadraticBound = accelerationBound / 2;
        }

        internal PixelSample SampleWithRegion(ulong globalChannelBit, out double reusableUntil)
        {
            var point = _spiral.AtArcLengthFast(globalChannelBit * _bitLengthMm);
            double angle = _startAngleRadians + _direction * point.Angle;
            double x = point.Radius * Math.Cos(angle), y = point.Radius * Math.Sin(angle);
            bool stable;
            PixelSample sample;
            SampleRectangle region;
            if (_referenceImage is not null)
                stable = _referenceImage.TryGetStableRegion(x, y, out sample, out region);
            else
                stable = _image!.TryGetStableRegion(x, y, _options.OuterRadiusMm,
                    preserveDiscreteGrayscale: true, out sample, out region);
            reusableUntil = globalChannelBit;
            if (!stable)
                return SampleRasterPoint(x, y);

            double tangentX = -_direction * y / point.Radius;
            double tangentY = _direction * x / point.Radius;
            // Each signed edge velocity includes the circular-tangent error and
            // anchor rounding. The second-derivative bound encloses the complete
            // intervening arc, even when it curves back toward a boundary.
            double travel = Math.Min(
                Math.Min(SafeTravel(x - region.MinX, -tangentX), SafeTravel(region.MaxX - x, tangentX)),
                Math.Min(SafeTravel(y - region.MinY, -tangentY), SafeTravel(region.MaxY - y, tangentY)));
            if (travel > 0 && double.IsFinite(travel))
                reusableUntil = Math.BitDecrement(globalChannelBit + travel / _bitLengthMm);
            return sample;
        }

        private double SafeTravel(double distance, double outwardTangent)
        {
            distance -= _reusePositionMargin;
            if (distance <= 0) return 0;
            double velocity = outwardTangent + _reuseTangentMargin;
            // Solve k*s² + v*s < distance, avoiding cancellation for either sign.
            double root = Math.Sqrt(velocity * velocity + 4 * _reuseQuadraticBound * distance);
            return velocity >= 0 ? 2 * distance / (root + velocity)
                : (root - velocity) / (2 * _reuseQuadraticBound);
        }

        internal PayloadTarget MapSample(PixelSample sample, long toneIndex)
            => new(ScrambledForLuminance(sample.Luminance, toneIndex), sample.Alpha);

        private PixelSample SampleRasterPoint(double x, double y) => _referenceImage is not null
            ? _referenceImage.SampleDisc(x, y)
            : _image!.SampleDisc(x, y, _options.OuterRadiusMm, preserveDiscreteGrayscale: true);

        internal PayloadTarget SampleGlobalChannelBit(ulong globalChannelBit, long toneIndex)
        {
            double arcLengthMm = (globalChannelBit + 0.0) * _bitLengthMm;
            // The shared geometry keeps the constant-pitch Newton shortcut and
            // uses its precomputed inverse curve for a varying radial pitch.
            (double radiusMm, double trackAngle) = _spiral.AtArcLengthFast(arcLengthMm);
            double polarAngle = _startAngleRadians + (_direction * trackAngle);
            return SamplePoint(radiusMm, polarAngle, trackAngle, globalChannelBit, toneIndex);
        }

        private PayloadTarget SamplePoint(double radiusMm, double polarAngle, double trackAngle,
            ulong globalChannelBit, long toneIndex)
        {
            double xMm = radiusMm * Math.Cos(polarAngle);
            double yMm = radiusMm * Math.Sin(polarAngle);
            if (_sampleScrambledCodeword is not null)
                return new PayloadTarget(_sampleScrambledCodeword(new DvdCodewordSample(
                    xMm, -yMm, globalChannelBit / 16, trackAngle)), 255);
            if (_sampleCodeword is not null)
                return new PayloadTarget(DvdGrayscalePalette.GetScrambledByte(
                    _sampleCodeword(new DvdCodewordSample(
                        xMm, -yMm, globalChannelBit / 16, trackAngle))), 255);
            // RasterImage.SampleDisc flips mathematical y-up into image y-down.
            // Apply that same flip for analytic patterns, including the start angle.
            PixelSample sample = _sampleGray is not null
                ? new PixelSample(_sampleGray(xMm, -yMm), 255)
                : SampleRasterPoint(xMm, yMm);
            return new PayloadTarget(ScrambledForLuminance(sample.Luminance, toneIndex), sample.Alpha);
        }

        private byte ScrambledForLuminance(byte luminance, long toneIndex)
        {
            if (_options.Tone is { IsLegacyBinary: false } tone)
            {
                return DvdToneMapping.UseWhite(
                    luminance, _options.LuminanceThreshold, tone, toneIndex)
                    ? DvdGrayscalePalette.WhiteScrambledByte
                    : DvdGrayscalePalette.BlackScrambledByte;
            }

            return DvdGrayscalePalette.GetScrambledByte(luminance);
        }

        internal void SampleFour(ulong blockFirstGlobalBit, ReadOnlySpan<int> centres,
            Span<PayloadTarget> samples, long toneIndexBase)
        {
            Vector256<double> bits = Vector256.Create(
                (double)(blockFirstGlobalBit + (ulong)centres[0]),
                (double)(blockFirstGlobalBit + (ulong)centres[1]),
                (double)(blockFirstGlobalBit + (ulong)centres[2]),
                (double)(blockFirstGlobalBit + (ulong)centres[3]));
            (Vector256<double> radius, Vector256<double> trackAngle) =
                _spiral.AtArcLengthFast(bits * Vector256.Create(_bitLengthMm));
            Vector256<double> angle = Vector256.Create(_startAngleRadians)
                + Vector256.Create(_direction) * trackAngle;
            if (_image is null || !_image.HasBinaryGrayscalePalette)
            {
                // Keep the original trig for analytic and bilinear colour/alpha sampling:
                // tiny coordinate changes can cross luminance or alpha rounding
                // thresholds even far from a pixel boundary. Geometry is SIMD.
                for (int lane = 0; lane < 4; lane++)
                {
                    samples[lane] = SamplePoint(radius.GetElement(lane), angle.GetElement(lane),
                        trackAngle.GetElement(lane), blockFirstGlobalBit + (ulong)centres[lane],
                        toneIndexBase + lane);
                }
                return;
            }
            // Normal DVD angles fit comfortably in this range. Unusual user
            // rotations retain the scalar argument reduction and its semantics.
            if (!Vector256.LessThanOrEqualAll(Vector256.Abs(angle), Vector256.Create(1_000_000d)))
            {
                for (int lane = 0; lane < 4; lane++)
                    samples[lane] = SampleGlobalChannelBit(
                        blockFirstGlobalBit + (ulong)centres[lane], toneIndexBase + lane);
                return;
            }
            (Vector256<double> sin, Vector256<double> cos) = Vector256.SinCos(angle);
            Vector256<double> x = radius * cos;
            Vector256<double> y = radius * sin;
            for (int lane = 0; lane < 4; lane++)
            {
                if (_image.TrySampleBinaryDisc(x.GetElement(lane), y.GetElement(lane),
                    _options.OuterRadiusMm, out PixelSample sample))
                    samples[lane] = new PayloadTarget(
                        ScrambledForLuminance(sample.Luminance, toneIndexBase + lane), sample.Alpha);
                else
                    samples[lane] = SampleGlobalChannelBit(
                        blockFirstGlobalBit + (ulong)centres[lane], toneIndexBase + lane);
            }
        }
    }
}
