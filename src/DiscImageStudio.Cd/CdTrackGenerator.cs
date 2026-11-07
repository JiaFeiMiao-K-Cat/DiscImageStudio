using System.Diagnostics;
using System.IO;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Cd;

public sealed record CdProgress(long CompletedBytes, long TotalBytes, TimeSpan Elapsed)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

public sealed record CdGenerationSummary(
    string SourceImage,
    string OutputTrack,
    long Sectors,
    long BytesWritten,
    bool Interleaved,
    TimeSpan Elapsed)
{
    public string? CueSheetPath { get; init; }
}

public static class CdTrackGenerator
{
    public static CdGenerationSummary Generate(
        string imagePath,
        string outputPath,
        CdDiscParameters parameters,
        bool interleave,
        bool writeCue = true,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ToneProfile? tone = null,
        bool useParallelSampling = true)
    {
        parameters.Validate();
        ToneProfile effectiveTone = (tone ?? ToneProfile.LegacyBinary).Normalized();
        string sourceFullPath = Path.GetFullPath(imagePath);
        string outputFullPath = Path.GetFullPath(outputPath);
        string extension = Path.GetExtension(outputFullPath);
        bool wave = extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
        if (!wave && !extension.Equals(".raw", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("CD output must be .wav (PCM audio + CUE) or .raw (big-endian cdrecord audio).", nameof(outputPath));
        }

        if (wave)
        {
            CdWaveFile.ValidateLength(parameters.TotalBytes);
        }

        if (sourceFullPath.Equals(outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The output must not overwrite the source image.", nameof(outputPath));
        }

        EnsureParentDirectory(outputFullPath);
        using FileStream file = new(
            outputFullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        if (wave)
        {
            CdWaveFile.WriteHeader(file, parameters.TotalBytes);
        }

        CdGenerationSummary summary = GenerateToStream(
            sourceFullPath,
            file,
            parameters,
            interleave,
            progress,
            cancellationToken,
            outputFullPath,
            wave ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian,
            effectiveTone,
            useParallelSampling: useParallelSampling);
        file.Dispose();
        CdTrackMetadata.Save(
            outputFullPath,
            new CdTrackMetadata(
                sourceFullPath,
                outputFullPath,
                parameters.InnerRadiusMm,
                parameters.OuterRadiusMm,
                parameters.Sectors,
                parameters.LinearVelocityMmPerSecond,
                parameters.StartAngleRadians * 180.0 / Math.PI,
                parameters.ImageOuterRadiusMm,
                interleave,
                effectiveTone.DitherStrength,
                effectiveTone.Levels,
                effectiveTone.Brightness,
                effectiveTone.SrgbLinearization,
                parameters.PitchLinear,
                parameters.PitchQuadratic,
                parameters.PitchCubic,
                parameters.UsableSectors,
                parameters.SourceImageOuterRadiusMm));
        return wave && writeCue
            ? summary with { CueSheetPath = CdWaveFile.WriteCue(outputFullPath) }
            : summary;
    }

    public static CdGenerationSummary GenerateToStream(
        string imagePath,
        Stream output,
        CdDiscParameters parameters,
        bool interleave,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string outputDescription = "direct-burn-stream",
        CdAudioByteOrder audioByteOrder = CdAudioByteOrder.BigEndian,
        ToneProfile? tone = null,
        bool useSimdSampling = true,
        bool useParallelSampling = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("CD output stream must be writable.", nameof(output));
        }

        CdAudioSamples.Validate(audioByteOrder);
        parameters.Validate();
        string sourceFullPath = Path.GetFullPath(imagePath);
        RasterImage source = RasterImage.Load(sourceFullPath);
        CdImageSampler sampler = new(source, parameters);
        return GenerateSamplesToStream(
            sampler.Sample,
            sourceFullPath,
            output,
            parameters,
            interleave,
            progress,
            cancellationToken,
            outputDescription,
            audioByteOrder,
            (tone ?? ToneProfile.LegacyBinary).Normalized(),
            useSimdSampling && sampler.SupportsSimd ? sampler.SampleFour : null,
            useParallelSampling);
    }

    /// <summary>
    /// Generates a calibration track directly from an analytic grayscale pattern.
    /// Coordinates are millimetres from the disc centre, with x right and y down.
    /// </summary>
    public static CdGenerationSummary GeneratePattern(
        Func<double, double, byte> sampleGray,
        string outputPath,
        CdDiscParameters parameters,
        bool interleave,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool useSimdSampling = true)
    {
        ArgumentNullException.ThrowIfNull(sampleGray);
        return WritePatternFile(outputPath, parameters, cancellationToken,
            (file, path, order) => GeneratePatternToStream(sampleGray, file, parameters, interleave,
                progress, cancellationToken, path, order, useSimdSampling));
    }

    public static CdGenerationSummary GeneratePattern(CalibrationTarget target, string outputPath, bool interleave,
        Action<CdProgress>? progress = null, CancellationToken cancellationToken = default,
        bool useParallelSampling = true)
    {
        var parameters = CdDiscParameters.FromCalibration(target);
        return WritePatternFile(outputPath, parameters, cancellationToken,
            (file, path, order) => GeneratePatternToStream(target, file, interleave, progress, cancellationToken, path, order,
                useParallelSampling));
    }

    private static CdGenerationSummary WritePatternFile(string outputPath, CdDiscParameters parameters,
        CancellationToken cancellationToken, Func<Stream, string, CdAudioByteOrder, CdGenerationSummary> write)
    {
        parameters.Validate();
        string outputFullPath = Path.GetFullPath(outputPath);
        string extension = Path.GetExtension(outputFullPath);
        bool wave = extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
        if (!wave && !extension.Equals(".raw", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("CD output must be .wav (PCM audio + CUE) or .raw (big-endian cdrecord audio).", nameof(outputPath));
        }

        if (wave)
        {
            CdWaveFile.ValidateLength(parameters.TotalBytes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureParentDirectory(outputFullPath);
        using FileStream file = new(
            outputFullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        if (wave)
        {
            CdWaveFile.WriteHeader(file, parameters.TotalBytes);
        }

        CdGenerationSummary summary = write(file, outputFullPath,
            wave ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian);
        return wave ? summary with { CueSheetPath = CdWaveFile.WriteCue(outputFullPath) } : summary;
    }

    /// <summary>Fixed-address calibration marks use the logical CD-DA byte before interleaving or endian conversion.</summary>
    public static CdGenerationSummary GeneratePatternToStream(CalibrationTarget target, Stream output, bool interleave,
        Action<CdProgress>? progress = null, CancellationToken cancellationToken = default,
        string outputDescription = "direct-burn-stream", CdAudioByteOrder audioByteOrder = CdAudioByteOrder.BigEndian,
        bool useParallelSampling = true, long? usableSectors = null)
    {
        var parameters = CdDiscParameters.FromCalibration(target);
        if (usableSectors.HasValue) parameters = parameters with { UsableSectors = usableSectors };
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("CD output stream must be writable.", nameof(output));
        CdAudioSamples.Validate(audioByteOrder);
        parameters.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!target.HasClockRuler && !target.HasWideAddressBands)
        {
            // Explicit targets own frozen built-in artwork. Arbitrary Func
            // overloads retain their original sequential callback semantics.
            CdImageSampler sampler = target.UsesReferencePattern
                ? new(target, parameters) : new(target.Sample, parameters);
            return GenerateSamplesToStream(sampler.Sample, "calibration", output, parameters, interleave, progress,
                cancellationToken, outputDescription, audioByteOrder, ToneProfile.LegacyBinary,
                sampler.SupportsSimd ? sampler.SampleFour : null, useParallelSampling);
        }
        Func<long, byte> sample = target.HasWideAddressBands ? WideBandSampler(target, parameters.GeometryBytes)
            : new CdClockRulerSampler(parameters.GeometryBytes).SampleGray;
        return GenerateSamplesToStream(sample, "calibration", output, parameters, interleave, progress,
            cancellationToken, outputDescription, audioByteOrder, ToneProfile.LegacyBinary);
    }

    private static Func<long, byte> WideBandSampler(CalibrationTarget target, long totalBytes)
    {
        var ranges = target.WideAddressBands.Select(band => band.ByteRange(totalBytes)).ToArray();
        var changes = target.HasAngleSweep ? new CalibrationAngleSweepSampler(target).Switches
            .Select(s => (Position: (long)Math.Ceiling(s.Progress * totalBytes), s.Luminance)).ToArray() : [];
        // One cursor per job. GenerateSamplesToStream visits every logical byte in order.
        int next = 0;
        int nextChange = 0;
        byte? marker = null;
        return position =>
        {
            while (nextChange < changes.Length && position >= changes[nextChange].Position)
                marker = changes[nextChange++].Luminance;
            if (marker.HasValue) return marker.Value;
            while (next < ranges.Length && position >= ranges[next].EndExclusive) next++;
            return next < ranges.Length && position >= ranges[next].Start ? (byte)0 : (byte)255;
        };
    }

    /// <summary>
    /// Writes the same audio sectors as <see cref="GeneratePattern"/> without a file header.
    /// The callback receives disc coordinates in millimetres, with x right and y down.
    /// </summary>
    public static CdGenerationSummary GeneratePatternToStream(
        Func<double, double, byte> sampleGray,
        Stream output,
        CdDiscParameters parameters,
        bool interleave,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string outputDescription = "direct-burn-stream",
        CdAudioByteOrder audioByteOrder = CdAudioByteOrder.BigEndian,
        bool useSimdSampling = true)
    {
        ArgumentNullException.ThrowIfNull(sampleGray);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("CD output stream must be writable.", nameof(output));
        }

        CdAudioSamples.Validate(audioByteOrder);
        parameters.Validate();
        CdImageSampler sampler = new(sampleGray, parameters);
        return GenerateSamplesToStream(
            sampler.Sample,
            "calibration",
            output,
            parameters,
            interleave,
            progress,
            cancellationToken,
            outputDescription,
            audioByteOrder,
            ToneProfile.LegacyBinary,
            useSimdSampling && sampler.SupportsSimd ? sampler.SampleFour : null);
    }

    private static CdGenerationSummary GenerateSamplesToStream(
        Func<long, byte> sampleGray,
        string summarySource,
        Stream output,
        CdDiscParameters parameters,
        bool interleave,
        Action<CdProgress>? progress,
        CancellationToken cancellationToken,
        string outputDescription,
        CdAudioByteOrder audioByteOrder,
        ToneProfile tone,
        Func<long, uint>? sampleFour = null,
        bool useParallelSampling = false)
    {
        if (useParallelSampling)
            return GenerateParallelSamplesToStream(sampleGray, summarySource, output, parameters, interleave,
                progress, cancellationToken, outputDescription, audioByteOrder, tone, sampleFour);
        long totalBytes = parameters.TotalBytes;
        CddaInterleaver? cddaInterleaver = interleave ? new CddaInterleaver(audioByteOrder) : null;
        byte[]? plainSector = interleave ? null : new byte[CdDiscParameters.BytesPerSector];
        Stopwatch stopwatch = Stopwatch.StartNew();
        long progressInterval = Math.Max(CdDiscParameters.BytesPerSector, totalBytes / 200);
        long nextProgress = progressInterval;
        uint packedSamples = 0;

        for (long globalByte = 0; globalByte < totalBytes; globalByte++)
        {
            if ((globalByte & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte gray;
            if (sampleFour is null)
            {
                gray = sampleGray(globalByte);
            }
            else
            {
                // A CD audio sector has 2352 bytes, so every track ends on a
                // complete four-lane batch. Output/interleave order stays serial.
                if ((globalByte & 3) == 0)
                    packedSamples = sampleFour(globalByte);
                gray = (byte)packedSamples;
                packedSamples >>= 8;
            }
            byte value = CdToneMapping.Map(gray, tone, globalByte);
            if (cddaInterleaver is null)
            {
                int sectorOffset = (int)(globalByte % CdDiscParameters.BytesPerSector);
                plainSector![sectorOffset] = value;
                if (sectorOffset == plainSector.Length - 1)
                {
                    CdAudioSamples.WriteSector(output, plainSector, audioByteOrder);
                }
            }
            else
            {
                cddaInterleaver.Add(value, output);
            }

            long completed = globalByte + 1;
            if (completed >= nextProgress)
            {
                progress?.Invoke(new CdProgress(completed, totalBytes, stopwatch.Elapsed));
                nextProgress = checked(completed + progressInterval);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        cddaInterleaver?.Flush(output);
        output.Flush();
        stopwatch.Stop();
        progress?.Invoke(new CdProgress(totalBytes, totalBytes, stopwatch.Elapsed));
        return new CdGenerationSummary(
            summarySource,
            outputDescription,
            parameters.OutputSectors,
            totalBytes,
            interleave,
            stopwatch.Elapsed);
    }

    private static CdGenerationSummary GenerateParallelSamplesToStream(Func<long, byte> sampleGray,
        string summarySource, Stream output, CdDiscParameters parameters, bool interleave,
        Action<CdProgress>? progress, CancellationToken cancellationToken, string outputDescription,
        CdAudioByteOrder audioByteOrder, ToneProfile tone, Func<long, uint>? sampleFour)
    {
        long totalBytes = parameters.TotalBytes;
        CddaInterleaver? cddaInterleaver = interleave ? new CddaInterleaver(audioByteOrder) : null;
        byte[]? plainSector = interleave ? null : new byte[CdDiscParameters.BytesPerSector];
        int plainPosition = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        long progressInterval = Math.Max(CdDiscParameters.BytesPerSector, totalBytes / 200);
        long nextProgress = progressInterval;

        void Fill(long firstSector, byte[] buffer, int sectors)
        {
            long firstByte = firstSector * CdDiscParameters.BytesPerSector;
            int length = sectors * CdDiscParameters.BytesPerSector;
            if (sampleFour is not null)
            {
                for (int offset = 0; offset < length; offset += 4)
                {
                    if ((offset & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                    uint samples = sampleFour(firstByte + offset);
                    for (int lane = 0; lane < 4; lane++)
                    {
                        buffer[offset + lane] = CdToneMapping.Map((byte)samples, tone, firstByte + offset + lane);
                        samples >>= 8;
                    }
                }
            }
            else
            {
                for (int offset = 0; offset < length; offset++)
                {
                    if ((offset & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                    long position = firstByte + offset;
                    buffer[offset] = CdToneMapping.Map(sampleGray(position), tone, position);
                }
            }
        }

        void Consume(long firstSector, byte[] buffer, int sectors)
        {
            long position = firstSector * CdDiscParameters.BytesPerSector;
            int length = sectors * CdDiscParameters.BytesPerSector;
            for (int offset = 0; offset < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Preserve the original progress positions, even inside sectors,
                // while amortizing cancellation and stateful output across spans.
                int count = (int)Math.Min(length - offset,
                    Math.Min(nextProgress - position, 4096 - (position & 0xFFF)));
                ReadOnlySpan<byte> values = buffer.AsSpan(offset, count);
                if (cddaInterleaver is not null) cddaInterleaver.Add(values, output);
                else
                {
                    while (!values.IsEmpty)
                    {
                        int copied = Math.Min(values.Length, plainSector!.Length - plainPosition);
                        values[..copied].CopyTo(plainSector.AsSpan(plainPosition));
                        plainPosition += copied;
                        values = values[copied..];
                        if (plainPosition == plainSector.Length)
                        {
                            CdAudioSamples.WriteSector(output, plainSector, audioByteOrder);
                            plainPosition = 0;
                        }
                    }
                }
                offset += count;
                position += count;
                if (position >= nextProgress)
                {
                    progress?.Invoke(new CdProgress(position, totalBytes, stopwatch.Elapsed));
                    nextProgress = checked(position + progressInterval);
                }
            }
        }

        CdBlockPipeline.Run(parameters.OutputSectors, CdDiscParameters.BytesPerSector, Fill, Consume, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        cddaInterleaver?.Flush(output);
        output.Flush();
        stopwatch.Stop();
        progress?.Invoke(new CdProgress(totalBytes, totalBytes, stopwatch.Elapsed));
        return new(summarySource, outputDescription, parameters.OutputSectors, totalBytes, interleave, stopwatch.Elapsed);
    }

    /// <summary>
    /// Projects the picture through two parameter sets: the sample is taken where
    /// <paramref name="generated"/> lays the picture out on the disc, and drawn where
    /// <paramref name="actual"/> reads that byte back. Passing the same geometry twice
    /// preserves the picture's binary geometry, so the difference between the two is the
    /// distortion a disc whose measured geometry differs from the generated one would show.
    /// </summary>
    public static void PreviewWarp(
        string imagePath,
        string outputPath,
        CdDiscParameters generated,
        CdDiscParameters actual,
        int outputSize,
        int samplesPerSector,
        CancellationToken cancellationToken = default)
        => PreviewWarp(
            imagePath,
            outputPath,
            generated,
            actual,
            outputSize,
            samplesPerSector,
            ToneProfile.LegacyBinary,
            cancellationToken);

    public static void PreviewWarp(
        string imagePath,
        string outputPath,
        CdDiscParameters generated,
        CdDiscParameters actual,
        int outputSize,
        int samplesPerSector,
        ToneProfile tone,
        CancellationToken cancellationToken = default)
    {
        generated.Validate();
        actual.Validate();
        ValidatePreview(outputSize, samplesPerSector);
        tone = tone.Normalized();
        RasterImage source = RasterImage.Load(Path.GetFullPath(imagePath));
        CdImageSampler sampler = new(source, generated);
        GrayPreview preview = new(outputSize, actual);
        long step = Math.Max(1, CdDiscParameters.BytesPerSector / samplesPerSector);
        long totalBytes = generated.TotalBytes;
        long sampleNumber = 0;
        uint packedSamples = 0;
        long packedGroup = -1;
        for (long globalByte = 0; globalByte < totalBytes; globalByte += step)
        {
            if ((sampleNumber & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte gray;
            if (sampler.SupportsSimd)
            {
                // Generation samples four consecutive bytes per lane; reuse that exact
                // packed group so a sparse preview cannot land on a different side of a
                // pixel boundary than the generated track did.
                long group = globalByte & ~3L;
                if (group != packedGroup)
                {
                    packedSamples = sampler.SampleFour(group);
                    packedGroup = group;
                }

                gray = (byte)(packedSamples >> (int)((globalByte & 3) * 8));
            }
            else
            {
                gray = sampler.Sample(globalByte);
            }

            Splat(actual, outputSize, preview, globalByte, CdToneMapping.Map(gray, tone, globalByte));
            sampleNumber++;
        }

        WritePng(Path.GetFullPath(outputPath), outputSize, preview.CreatePixels());
    }

    /// <summary>
    /// Renders a generated track as the disc would look. The generator's delay interleave moves
    /// each logical byte forward in the file, so <paramref name="deinterleave"/> must match the
    /// interleave setting the track was generated with: with identical geometry the read-back
    /// then reproduces the source image exactly. Without it the raw file order is rendered, which
    /// is only correct for a track generated with the interleave switched off.
    /// </summary>
    public static void PreviewTrack(
        string trackPath,
        string outputPath,
        CdDiscParameters actual,
        int outputSize,
        int byteStep,
        bool deinterleave,
        CancellationToken cancellationToken = default)
    {
        actual.Validate();
        if (outputSize is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSize), "Preview size must be 64..8192.");
        }

        if (byteStep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteStep), "Byte step must be positive.");
        }

        GrayPreview preview = new(outputSize, actual);
        using FileStream input = new(
            Path.GetFullPath(trackPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        Span<byte> signature = stackalloc byte[12];
        int signatureLength = input.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        bool wave = Path.GetExtension(trackPath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || (signatureLength == 12 && signature[..4].SequenceEqual("RIFF"u8)
                && signature[8..].SequenceEqual("WAVE"u8));
        input.Position = 0;
        long audioOffset = 0;
        long audioLength = input.Length;
        if (wave)
        {
            (audioOffset, audioLength) = CdWaveFile.ReadAudioRange(input);
        }

        long maximumBytes = Math.Min(actual.TotalBytes, audioLength);
        if (deinterleave)
        {
            SplatDeinterleaved(
                input,
                audioOffset,
                audioLength,
                maximumBytes,
                wave,
                actual,
                outputSize,
                byteStep,
                preview,
                cancellationToken);
        }
        else
        {
            SplatFileOrder(
                input,
                audioOffset,
                maximumBytes,
                wave,
                actual,
                outputSize,
                byteStep,
                preview,
                cancellationToken);
        }

        WritePng(Path.GetFullPath(outputPath), outputSize, preview.CreatePixels());
    }

    /// <summary>Renders the file's bytes in file order; correct for a track without interleave.</summary>
    private static void SplatFileOrder(
        FileStream input,
        long audioOffset,
        long maximumBytes,
        bool wave,
        CdDiscParameters actual,
        int outputSize,
        int byteStep,
        GrayPreview preview,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 1024];
        long globalOffset = 0;
        input.Position = audioOffset;
        while (globalOffset < maximumBytes)
        {
            int requested = (int)Math.Min(buffer.Length, maximumBytes - globalOffset);
            int bytesRead = input.ReadAtLeast(buffer.AsSpan(0, requested), requested, throwOnEndOfStream: false);
            if (bytesRead == 0)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (wave)
            {
                CdAudioSamples.SwapByteOrder(buffer.AsSpan(0, bytesRead));
            }

            long remainder = globalOffset % byteStep;
            int first = remainder == 0 ? 0 : checked((int)(byteStep - remainder));
            for (int index = first; index < bytesRead; index += byteStep)
            {
                long globalByte = globalOffset + index;
                Splat(actual, outputSize, preview, globalByte, buffer[index]);
            }

            globalOffset += bytesRead;
        }
    }

    /// <summary>
    /// Undoes the generator's delay interleave: the logical byte at index i sits at file index
    /// i + CddaInterleaveTable.FileOffsetFor(i % 24), which needs a bounded look-ahead window.
    /// </summary>
    private static void SplatDeinterleaved(
        FileStream input,
        long audioOffset,
        long audioLength,
        long maximumBytes,
        bool wave,
        CdDiscParameters actual,
        int outputSize,
        int byteStep,
        GrayPreview preview,
        CancellationToken cancellationToken)
    {
        const int WindowBytes = 1024 * 1024;
        int lookAhead = CddaInterleaveTable.MaximumFileOffset;
        byte[] buffer = new byte[WindowBytes + lookAhead];
        for (long windowStart = 0; windowStart < maximumBytes; windowStart += WindowBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int logicalCount = (int)Math.Min(WindowBytes, maximumBytes - windowStart);
            long remaining = audioLength - windowStart;
            int wanted = (int)Math.Min(buffer.Length, Math.Max(0, remaining));
            if (wanted == 0)
            {
                break;
            }

            input.Position = audioOffset + windowStart;
            int bytesRead = input.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            bytesRead &= ~1;
            if (wave)
            {
                CdAudioSamples.SwapByteOrder(buffer.AsSpan(0, bytesRead));
            }

            long first = ((windowStart + byteStep - 1) / byteStep) * byteStep;
            for (long logical = first; logical < windowStart + logicalCount; logical += byteStep)
            {
                long local = logical
                    + CddaInterleaveTable.FileOffsetFor((int)(logical % CddaInterleaveTable.BytesPerFrame))
                    - windowStart;
                if (local >= bytesRead)
                {
                    // The delay line loses the last frames of the track; nothing to recover.
                    continue;
                }

                Splat(actual, outputSize, preview, logical, buffer[local]);
            }
        }
    }

    private static void Splat(
        CdDiscParameters actual,
        int outputSize,
        GrayPreview preview,
        long globalByte,
        byte paletteByte)
    {
        (double pixelX, double pixelY) = actual.ImagePointFromByte(globalByte, outputSize);
        preview.Add(
            (int)Math.Round(pixelX),
            (int)Math.Round(pixelY),
            CdToneMapping.DisplayGray(paletteByte));
    }

    private static byte Sample(
        RasterImage source,
        CdDiscParameters parameters,
        long globalByte)
    {
        (double pixelX, double pixelY) = parameters.SourceImagePointFromByte(globalByte, source.Width);
        return source.GrayAt(pixelX, pixelY);
    }

    /// <summary>Source projections and track readback average every sample that lands in one pixel.</summary>
    private sealed class GrayPreview(int size, CdDiscParameters actual)
    {
        private readonly int[] _levelSums = new int[checked(size * size)];
        private readonly int[] _sampleCounts = new int[checked(size * size)];

        internal void Add(int x, int y, byte level)
        {
            if ((uint)x >= (uint)size || (uint)y >= (uint)size)
            {
                return;
            }

            int pixelIndex = checked((y * size) + x);
            _levelSums[pixelIndex] += level;
            _sampleCounts[pixelIndex]++;
        }

        internal byte[] CreatePixels()
        {
            byte[] pixels = new byte[checked(size * size * 4)];
            Array.Fill(pixels, (byte)255);
            for (int pixelIndex = 0; pixelIndex < _sampleCounts.Length; pixelIndex++)
            {
                int count = _sampleCounts[pixelIndex];
                if (count == 0)
                {
                    continue;
                }

                byte level = (byte)((_levelSums[pixelIndex] + (count / 2)) / count);
                int offset = checked(pixelIndex * 4);
                pixels[offset] = level;
                pixels[offset + 1] = level;
                pixels[offset + 2] = level;
            }

            CdPreviewMask.Apply(pixels, size, actual);
            return pixels;
        }
    }

    private static void WritePng(string outputPath, int size, byte[] pixels)
    {
        EnsureParentDirectory(outputPath);
        RasterCodec.SavePng(outputPath, size, size, pixels, grayscale: false);
    }

    private static void ValidatePreview(int outputSize, int samplesPerSector)
    {
        if (outputSize is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSize), "Preview size must be 64..8192.");
        }

        if (samplesPerSector is < 1 or > CdDiscParameters.BytesPerSector)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplesPerSector),
                $"Samples per sector must be 1..{CdDiscParameters.BytesPerSector}.");
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
