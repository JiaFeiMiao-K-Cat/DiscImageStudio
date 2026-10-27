using System.Diagnostics;
using System.Runtime.Intrinsics;
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
    TimeSpan Elapsed);

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
        using RawIsoImageWriter iso = new(
            isoOutputPath,
            isoSectors,
            options.PreserveExistingIso);
        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = parallelism,
        };
        int batchCapacity = Math.Min(totalBlocks, Math.Max(1, checked(parallelism * 2)));
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
                int completedBlocks = batchStart + batchOffset + 1;
                progress?.Invoke(new FastDispersionProgress(
                    completedBlocks,
                    totalBlocks,
                    block.Lba,
                    controlledWords,
                    stopwatch.Elapsed));
            }
        }

        bool sparseIso = iso.SparseFile;
        iso.Dispose();
        stopwatch.Stop();
        return new FastDispersionWriteResult(
            totalBlocks,
            options.FillSectors,
            controlledWords,
            stopwatch.Elapsed,
            sparseIso,
            isoSectors);
    }

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
        CancellationToken cancellationToken)
    {
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
                generated[batchOffset] = GenerateBlock(
                    blockIndex, sampler, options, generated[batchOffset].Payloads, cancellationToken);
            });

            for (int batchOffset = 0; batchOffset < batchCount; batchOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GeneratedBlock block = generated[batchOffset];
                output.Write(block.Payloads);
                bytesWritten = checked(bytesWritten + block.Payloads.Length);
                controlledWords += block.ControlledWords;
                int completedBlocks = batchStart + batchOffset + 1;
                progress?.Invoke(new FastDvdStreamProgress(
                    completedBlocks,
                    totalBlocks,
                    block.Lba,
                    bytesWritten,
                    totalBytes,
                    stopwatch.Elapsed));
            }
        }

        output.Flush();
        stopwatch.Stop();
        return new FastDvdStreamSummary(
            totalBlocks,
            options.FillSectors,
            bytesWritten,
            controlledWords,
            stopwatch.Elapsed);
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
            if (useSimd)
            {
                // Payload slots are not uniformly spaced across headers/SYNC/ECC.
                // Use the original physical centre of every lane, including group boundaries.
                long toneIndexBase = checked((((long)lba + sector) * DvdEccBlockEncoder.PayloadBytesPerSector));
                for (int byteIndex = 0; byteIndex < sectorPayload.Length; byteIndex += 4)
                {
                    sampler.SampleFour(blockFirstGlobalBit,
                        PayloadChannelCenters.AsSpan(payloadOffset + byteIndex, 4), samples,
                        toneIndexBase + byteIndex);
                    for (int lane = 0; lane < 4; lane++)
                    {
                        PayloadTarget sample = samples[lane];
                        if (sample.Alpha < options.ImageMapping.AlphaThreshold)
                            continue;
                        sectorPayload[byteIndex + lane] = (byte)(sample.ScrambledByte ^ scrambler[byteIndex + lane]);
                        controlledWords++;
                    }
                }
                continue;
            }
            for (int byteIndex = 0; byteIndex < sectorPayload.Length; byteIndex++)
            {
                int payloadIndex = payloadOffset + byteIndex;
                long toneIndex = checked((((long)lba + sector) * DvdEccBlockEncoder.PayloadBytesPerSector) + byteIndex);
                PayloadTarget sample = sampler.SampleGlobalChannelBit(
                    blockFirstGlobalBit + checked((ulong)PayloadChannelCenters[payloadIndex]),
                    toneIndex);
                if (sample.Alpha < options.ImageMapping.AlphaThreshold)
                {
                    continue;
                }

                sectorPayload[byteIndex] = (byte)(sample.ScrambledByte ^ scrambler[byteIndex]);
                controlledWords++;
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

        // Analytic callbacks share the exact vector geometry while retaining
        // scalar trigonometry, so even coordinate-sensitive callbacks agree.
        // Constant-pitch batching regresses throughput while Asinh stays scalar;
        // retain its original loop until a separately validated path is faster.
        internal bool SupportsSimdSampling => Vector256.IsHardwareAccelerated
            && !_spiral.IsConstantPitch;

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
        }

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
                : _image!.SampleDisc(xMm, yMm, _options.OuterRadiusMm, preserveDiscreteGrayscale: true);
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
