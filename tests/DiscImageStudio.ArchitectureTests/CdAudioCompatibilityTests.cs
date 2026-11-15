using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

internal static class CdAudioCompatibilityTests
{
    internal static void Run()
    {
        True(
            new DiscParametersState().CdInterleave,
            "a new CD parameter state enables delay interleave by default");
        TestAudioFormatRestore();
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-audio-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "asymmetric.png");
            WritePatternPng(sourcePath);
            CdDiscParameters parameters = new(
                InnerRadiusMm: 24.5,
                OuterRadiusMm: 24.8,
                Sectors: 8,
                LinearVelocityMmPerSecond: 1200,
                ImageOuterRadiusMm: 58);
            foreach (bool interleave in new[] { false, true })
            {
                TestFileAndStreamingFormats(directory, sourcePath, parameters, interleave);
            }

            TestCommandOutput(directory, sourcePath, parameters);
            TestReadbackInvertsInterleave(directory, sourcePath);
            TestWarpProjectsGeneratedOntoMeasured(directory, sourcePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestFileAndStreamingFormats(
        string directory,
        string sourcePath,
        CdDiscParameters parameters,
        bool interleave)
    {
        string name = interleave ? "interleaved" : "plain";
        string rawPath = Path.Combine(directory, name + ".raw");
        string wavPath = Path.Combine(directory, name + " 音轨.WAV");
        CdGenerationSummary rawSummary = CdTrackGenerator.Generate(
            sourcePath, rawPath, parameters, interleave);
        CdTrackGenerator.Generate(sourcePath, wavPath, parameters, interleave);
        byte[] raw = File.ReadAllBytes(rawPath);
        byte[] wav = File.ReadAllBytes(wavPath);
        Equal(parameters.TotalBytes, raw.LongLength, name + " raw byte length");
        Equal(raw.LongLength, rawSummary.BytesWritten, name + " raw summary length");
        ValidateWaveHeader(wav, raw.Length, name);
        ValidateCueFile(wavPath);
        True(!File.Exists(Path.ChangeExtension(rawPath, ".cue")), name + " legacy raw does not create a cue");
        byte[] pcm = wav[44..];
        AssertSwappedPairs(raw, pcm, name);

        using MemoryStream legacyStream = new();
        CdTrackGenerator.GenerateToStream(sourcePath, legacyStream, parameters, interleave);
        True(raw.SequenceEqual(legacyStream.ToArray()), name + " default stream preserves raw format");

        using MemoryStream pcmStream = new();
        CdGenerationSummary streamSummary;
        using (ForwardOnlyWriteStream output = new(pcmStream))
        {
            streamSummary = CdTrackGenerator.GenerateToStream(
                sourcePath,
                output,
                parameters,
                interleave,
                audioByteOrder: CdAudioByteOrder.LittleEndian);
        }

        Equal(parameters.TotalBytes, pcmStream.Length, name + " forward stream has no WAV header");
        Equal(pcmStream.Length, streamSummary.BytesWritten, name + " forward stream summary length");
        True(pcm.SequenceEqual(pcmStream.ToArray()), name + " forward PCM stream matches WAV data");

        foreach (int byteStep in new[] { 1, 3, 48 })
        {
            string rawPreview = Path.Combine(directory, $"{name}-raw-{byteStep}.png");
            string wavPreview = Path.Combine(directory, $"{name}-wav-{byteStep}.png");
            CdTrackGenerator.PreviewTrack(rawPath, rawPreview, parameters, 256, byteStep, interleave);
            CdTrackGenerator.PreviewTrack(wavPath, wavPreview, parameters, 256, byteStep, interleave);
            True(
                ReadPngPixels(rawPreview).SequenceEqual(ReadPngPixels(wavPreview)),
                $"{name} WAV preview normalizes PCM byte order at step {byteStep}");
        }

        if (interleave)
        {
            TestWaveChunkParsing(directory, rawPath, wav, parameters);
        }
    }

    private static void TestCommandOutput(
        string directory,
        string sourcePath,
        CdDiscParameters parameters)
    {
        string outputPath = Path.Combine(directory, "command.wav");
        DiscJobResult result = new CdDiscModule().ExecuteAsync(new DiscJobRequest(
            "cd-generate",
            [
                "--input", sourcePath,
                "--output", outputPath,
                "--r0", parameters.InnerRadiusMm.ToString(CultureInfo.InvariantCulture),
                "--r1", parameters.OuterRadiusMm.ToString(CultureInfo.InvariantCulture),
                "--sectors", parameters.Sectors.ToString(CultureInfo.InvariantCulture),
                "--velocity", parameters.LinearVelocityMmPerSecond.ToString(CultureInfo.InvariantCulture),
                "--outer", parameters.ImageOuterRadiusMm.ToString(CultureInfo.InvariantCulture),
                "--interleave", "true",
            ])).GetAwaiter().GetResult();
        True(result.Succeeded, "CD command generates WAV successfully");
        ValidateCueFile(outputPath);
        True(
            File.ReadAllBytes(Path.Combine(directory, "interleaved 音轨.WAV"))
                .SequenceEqual(File.ReadAllBytes(outputPath)),
            "CD command WAV matches file generation API");

        // The CUE sheet is opt-out: skipping it must change nothing about the WAV itself.
        string noCuePath = Path.Combine(directory, "command no cue.wav");
        DiscJobResult noCueResult = new CdDiscModule().ExecuteAsync(new DiscJobRequest(
            "cd-generate",
            [
                "--input", sourcePath,
                "--output", noCuePath,
                "--r0", parameters.InnerRadiusMm.ToString(CultureInfo.InvariantCulture),
                "--r1", parameters.OuterRadiusMm.ToString(CultureInfo.InvariantCulture),
                "--sectors", parameters.Sectors.ToString(CultureInfo.InvariantCulture),
                "--velocity", parameters.LinearVelocityMmPerSecond.ToString(CultureInfo.InvariantCulture),
                "--outer", parameters.ImageOuterRadiusMm.ToString(CultureInfo.InvariantCulture),
                "--interleave", "true",
                "--cue", "false",
            ])).GetAwaiter().GetResult();
        True(noCueResult.Succeeded, "CD command generates WAV without a cue sheet");
        True(
            !File.Exists(Path.ChangeExtension(noCuePath, ".cue")),
            "CD command with --cue false writes no cue sheet");
        True(
            File.ReadAllBytes(outputPath).SequenceEqual(File.ReadAllBytes(noCuePath)),
            "skipping the cue sheet leaves the WAV bytes unchanged");
    }

    /// <summary>
    /// The delay interleave moves every logical byte forward in the file, so reading an
    /// interleaved track back with the flag it was generated with must reproduce the plain
    /// track's disc pattern. The delay line cannot recover the last frames of the track; those
    /// samples may only be missing, never wrong, because a wrong sample would mean the read-back
    /// is not the inverse of the generator.
    /// </summary>
    private static void TestReadbackInvertsInterleave(string directory, string sourcePath)
    {
        CdDiscParameters parameters = new(
            InnerRadiusMm: 24.5,
            OuterRadiusMm: 56.8,
            Sectors: 8,
            LinearVelocityMmPerSecond: 1200,
            ImageOuterRadiusMm: 58);
        string plainPath = Path.Combine(directory, "readback-plain.raw");
        string interleavedPath = Path.Combine(directory, "readback-interleaved.raw");
        CdTrackGenerator.Generate(sourcePath, plainPath, parameters, interleave: false);
        CdTrackGenerator.Generate(sourcePath, interleavedPath, parameters, interleave: true);

        CdTrackMetadata metadata = CdTrackMetadata.TryLoad(interleavedPath)
            ?? throw new InvalidOperationException("interleaved sidecar was not written");
        True(metadata.Interleaved, "track sidecar records the interleave setting");
        Equal(parameters.Sectors, metadata.Sectors, "track sidecar records the sector count");
        Equal(parameters.InnerRadiusMm, metadata.InnerRadiusMm, "track sidecar records the inner radius");
        Equal(parameters.OuterRadiusMm, metadata.OuterRadiusMm, "track sidecar records the outer radius");
        True(CdTrackMetadata.TryLoad(plainPath) is { Interleaved: false }, "plain sidecar records no interleave");

        const int size = 900;
        byte[] expected = Preview(plainPath, parameters, size, deinterleave: false);
        byte[] actual = Preview(interleavedPath, parameters, size, deinterleave: true);
        byte[] fileOrder = Preview(interleavedPath, parameters, size, deinterleave: false);

        // The delay line cannot recover the last frames of the track, so differences are
        // allowed only inside the pixel footprint of those final bytes.
        HashSet<int> tailPixels = TrailingSectorPixels(parameters, size);
        int unexplained = 0;
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            if (expected[offset] == actual[offset])
            {
                continue;
            }

            if (!tailPixels.Contains(offset / 4))
            {
                unexplained++;
            }
        }

        Equal(0, unexplained, "interleaved read-back reproduces the plain track sample for sample");

        int scrambled = 0;
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            if (expected[offset] != fileOrder[offset])
            {
                scrambled++;
            }
        }

        True(
            scrambled > 100,
            "reading an interleaved track in file order renders a different picture");
    }

    /// <summary>
    /// The calibration preview's two parameter sets are one projection: sampling the source at
    /// the generated geometry and drawing at the measured geometry has to reproduce, pixel for
    /// pixel, a track generated with the first geometry and read back under the second. Moving
    /// the measured geometry then moves the drawing, which is what that set is for.
    /// </summary>
    private static void TestWarpProjectsGeneratedOntoMeasured(string directory, string sourcePath)
    {
        const int size = 900;
        const int byteStep = CdDiscParameters.BytesPerSector / 16;
        CdDiscParameters generated = new(
            InnerRadiusMm: 24.5,
            OuterRadiusMm: 56.8,
            Sectors: 8,
            LinearVelocityMmPerSecond: 1200,
            ImageOuterRadiusMm: 58);
        CdDiscParameters measured = generated with { InnerRadiusMm = 24.3, OuterRadiusMm = 56.6 };
        string trackPath = Path.Combine(directory, "warp-track.raw");
        CdTrackGenerator.Generate(sourcePath, trackPath, generated, interleave: false);

        string identicalPath = Path.Combine(directory, "warp-identical.png");
        string measuredPath = Path.Combine(directory, "warp-measured.png");
        CdTrackGenerator.PreviewWarp(sourcePath, identicalPath, generated, generated, size, 16);
        CdTrackGenerator.PreviewWarp(sourcePath, measuredPath, generated, measured, size, 16);

        True(
            ReadPngPixels(identicalPath)
                .SequenceEqual(Preview(trackPath, generated, size, deinterleave: false, byteStep)),
            "warp with one geometry matches the read-back of the track it describes");
        True(
            ReadPngPixels(measuredPath)
                .SequenceEqual(Preview(trackPath, measured, size, deinterleave: false, byteStep)),
            "warp onto measured geometry matches the read-back under that geometry");
        True(
            MeanDarkRadius(measuredPath, size) < MeanDarkRadius(identicalPath, size),
            "a smaller measured geometry pulls the projection inwards");

        // At this resolution many differently colored audio bytes land in one pixel.
        // Source projection and readback must use the same averaging, not last-write wins.
        foreach (CdDiscParameters actual in new[] { generated, measured })
        {
            string densePath = Path.Combine(directory, "warp-dense.png");
            CdTrackGenerator.PreviewWarp(sourcePath, densePath, generated, actual, 64,
                CdDiscParameters.BytesPerSector);
            byte[] dense = ReadPngPixels(densePath);
            True(dense.Where((_, index) => index % 4 == 0).Any(level => level is not (0 or 255)),
                "dense preview exercises overlapping black and white samples");
            True(dense.SequenceEqual(Preview(trackPath, actual, 64, deinterleave: false)),
                "dense source projection and generated readback average the same samples");
        }
    }

    private static double MeanDarkRadius(string pngPath, int size)
    {
        byte[] pixels = ReadPngPixels(pngPath);
        double centre = size / 2.0;
        double sum = 0;
        int count = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (pixels[((y * size) + x) * 4] >= 128)
                {
                    continue;
                }

                sum += Math.Sqrt(((x - centre) * (x - centre)) + ((y - centre) * (y - centre)));
                count++;
            }
        }

        True(count > 0, "the projection draws at least one dark sample");
        return sum / count;
    }

    /// <summary>
    /// Pixels covered by the last two sectors of the track: the delay line's look-ahead is
    /// under one sector, so anything it drops falls inside this footprint.
    /// </summary>
    private static HashSet<int> TrailingSectorPixels(CdDiscParameters parameters, int size)
    {
        HashSet<int> pixels = [];
        long first = Math.Max(0, parameters.TotalBytes - (2 * CdDiscParameters.BytesPerSector));
        for (long byteIndex = first; byteIndex < parameters.TotalBytes; byteIndex++)
        {
            (double pixelX, double pixelY) = parameters.ImagePointFromByte(byteIndex, size);
            int x = (int)Math.Round(pixelX);
            int y = (int)Math.Round(pixelY);
            if ((uint)x < (uint)size && (uint)y < (uint)size)
            {
                pixels.Add((y * size) + x);
            }
        }

        return pixels;
    }

    private static byte[] Preview(
        string trackPath,
        CdDiscParameters parameters,
        int size,
        bool deinterleave,
        int byteStep = 1)
    {
        string path = Path.Combine(
            Path.GetDirectoryName(trackPath)!,
            $"{Path.GetFileNameWithoutExtension(trackPath)}-{deinterleave}-{size}-{byteStep}.png");
        CdTrackGenerator.PreviewTrack(trackPath, path, parameters, size, byteStep, deinterleave);
        return ReadPngPixels(path);
    }

    private static void ValidateCueFile(string wavPath)
    {
        string[] lines = File.ReadAllText(Path.ChangeExtension(wavPath, ".cue"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Equal(3, lines.Length, "WAV cue line count");
        Equal($"FILE \"{Path.GetFileName(wavPath)}\" WAVE", lines[0], "WAV cue declares container format");
        Equal("TRACK 01 AUDIO", lines[1], "WAV cue declares audio track");
        Equal("INDEX 01 00:00:00", lines[2], "WAV cue track begins at the data start");
    }

    private static void TestWaveChunkParsing(
        string directory,
        string rawPath,
        byte[] wav,
        CdDiscParameters parameters)
    {
        using MemoryStream decorated = new();
        decorated.Write(wav.AsSpan(0, 12));
        WriteWaveChunk(decorated, "JUNK", [0x01, 0x02, 0x03]);
        decorated.Write(wav.AsSpan(12));
        WriteWaveChunk(decorated, "LIST", Enumerable.Repeat((byte)0x10, 2048).ToArray());
        byte[] decoratedBytes = decorated.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            decoratedBytes.AsSpan(4, 4), checked((uint)(decoratedBytes.Length - 8)));
        string decoratedPath = Path.Combine(directory, "metadata.wav");
        File.WriteAllBytes(decoratedPath, decoratedBytes);

        // The larger geometry would expose trailing metadata if the reader failed
        // to honor the data chunk's declared size.
        CdDiscParameters largerGeometry = parameters with { Sectors = parameters.Sectors + 1 };
        string rawPreview = Path.Combine(directory, "raw-larger-geometry.png");
        string decoratedPreview = Path.Combine(directory, "metadata-preview.png");
        CdTrackGenerator.PreviewTrack(rawPath, rawPreview, largerGeometry, 256, 1, deinterleave: true);
        CdTrackGenerator.PreviewTrack(decoratedPath, decoratedPreview, largerGeometry, 256, 1, deinterleave: true);
        True(
            ReadPngPixels(rawPreview).SequenceEqual(ReadPngPixels(decoratedPreview)),
            "WAV reader skips odd-sized unknown chunks and excludes trailing metadata");

        foreach ((int offset, uint value, bool shortValue, string name) in new[]
        {
            (20, 3U, true, "floating-point format"),
            (22, 1U, true, "mono audio"),
            (24, 48000U, false, "non-CD sample rate"),
            (34, 8U, true, "8-bit samples"),
        })
        {
            byte[] invalid = (byte[])wav.Clone();
            if (shortValue)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(offset, 2), (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(offset, 4), value);
            }

            string invalidPath = Path.Combine(directory, "unsupported.wav");
            File.WriteAllBytes(invalidPath, invalid);
            Throws<InvalidDataException>(
                () => CdTrackGenerator.PreviewTrack(
                    invalidPath,
                    Path.Combine(directory, "unsupported-preview.png"),
                    parameters,
                    256,
                    1,
                    deinterleave: true),
                "WAV preview rejects " + name);
        }

        // WAVE_FORMAT_EXTENSIBLE carrying 16-bit stereo PCM (what many editors write) reads
        // back exactly like the plain PCM header; other sub-formats and layouts stay rejected.
        string extensiblePath = Path.Combine(directory, "extensible.wav");
        File.WriteAllBytes(extensiblePath, ToExtensible(wav, PcmSubFormat, validBits: 16, channelMask: 3));
        string extensiblePreview = Path.Combine(directory, "extensible-preview.png");
        CdTrackGenerator.PreviewTrack(extensiblePath, extensiblePreview, largerGeometry, 256, 1, deinterleave: true);
        True(
            ReadPngPixels(rawPreview).SequenceEqual(ReadPngPixels(extensiblePreview)),
            "WAV reader accepts WAVE_FORMAT_EXTENSIBLE 16-bit PCM");
        foreach ((byte[] subFormat, ushort validBits, uint channelMask, string name) in new[]
        {
            (FloatSubFormat, (ushort)16, 3U, "an extensible float sub-format"),
            (PcmSubFormat, (ushort)20, 3U, "20 valid bits in an extensible header"),
            (PcmSubFormat, (ushort)16, 0x33U, "a surround channel mask"),
        })
        {
            string invalidPath = Path.Combine(directory, "unsupported-extensible.wav");
            File.WriteAllBytes(invalidPath, ToExtensible(wav, subFormat, validBits, channelMask));
            Throws<InvalidDataException>(
                () => CdTrackGenerator.PreviewTrack(
                    invalidPath,
                    Path.Combine(directory, "unsupported-extensible-preview.png"),
                    parameters,
                    256,
                    1,
                    deinterleave: true),
                "WAV preview rejects " + name);
        }
    }

    // KSDATAFORMAT_SUBTYPE_PCM and _IEEE_FLOAT in file byte order.
    private static readonly byte[] PcmSubFormat =
        [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];
    private static readonly byte[] FloatSubFormat =
        [0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    /// <summary>The same WAV with its 16-byte PCM fmt chunk replaced by a 40-byte
    /// WAVE_FORMAT_EXTENSIBLE one (cbSize 22, valid bits, channel mask, sub-format).</summary>
    private static byte[] ToExtensible(byte[] wav, byte[] subFormat, ushort validBits, uint channelMask)
    {
        using MemoryStream output = new();
        output.Write(wav.AsSpan(0, 12));
        byte[] format = new byte[40];
        wav.AsSpan(20, 16).CopyTo(format);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(0, 2), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(16, 2), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(18, 2), validBits);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(20, 4), channelMask);
        subFormat.CopyTo(format.AsSpan(24));
        WriteWaveChunk(output, "fmt ", format);
        output.Write(wav.AsSpan(36));
        byte[] result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)(result.Length - 8)));
        return result;
    }

    /// <summary>A RAW (or WAV) choice survives a detour through a data-disc ISO, and a newly
    /// opened project takes its own format instead of the previous project's.</summary>
    private static void TestAudioFormatRestore()
    {
        DiscParametersState state = new() { CdOutputPath = "track.raw" };
        state.CdDataDirectory = "files";
        Equal("track.iso", state.CdOutputPath, "disc files switch the CD output to an ISO");
        state.CdDataDirectory = string.Empty;
        Equal("track.raw", state.CdOutputPath, "removing the files restores the RAW output");
        state.CdOutputPath = "track.wav";
        state.CdDataDirectory = "files";
        state.CdDataDirectory = string.Empty;
        Equal("track.wav", state.CdOutputPath, "and the WAV output when that was chosen");

        state.CdOutputPath = "old.raw";
        state.ResetCdAudioFormat("new.iso");
        state.CdOutputPath = "new.iso";
        state.CdDataDirectory = "files";
        state.CdDataDirectory = string.Empty;
        Equal("new.wav", state.CdOutputPath, "a project opened as an ISO does not inherit the previous RAW choice");
    }

    private static void WriteWaveChunk(Stream output, string id, byte[] payload)
    {
        output.Write(Encoding.ASCII.GetBytes(id));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)payload.Length));
        output.Write(length);
        output.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            output.WriteByte(0);
        }
    }

    private static void ValidateWaveHeader(byte[] wav, int dataBytes, string name)
    {
        Equal(dataBytes + 44, wav.Length, name + " WAV has a 44-byte PCM header");
        Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4), name + " RIFF signature");
        Equal((uint)(wav.Length - 8), UInt32(wav, 4), name + " RIFF size");
        Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4), name + " WAVE signature");
        Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4), name + " format chunk");
        Equal(16U, UInt32(wav, 16), name + " PCM format size");
        Equal((ushort)1, UInt16(wav, 20), name + " integer PCM format");
        Equal((ushort)2, UInt16(wav, 22), name + " stereo channels");
        Equal(44100U, UInt32(wav, 24), name + " sample rate");
        Equal(176400U, UInt32(wav, 28), name + " byte rate");
        Equal((ushort)4, UInt16(wav, 32), name + " sample frame size");
        Equal((ushort)16, UInt16(wav, 34), name + " sample bit depth");
        Equal("data", Encoding.ASCII.GetString(wav, 36, 4), name + " data chunk");
        Equal((uint)dataBytes, UInt32(wav, 40), name + " PCM data size");
    }

    private static void AssertSwappedPairs(byte[] raw, byte[] pcm, string name)
    {
        Equal(raw.Length, pcm.Length, name + " payload length is unchanged");
        int asymmetricPairs = 0;
        for (int offset = 0; offset < raw.Length; offset += 2)
        {
            if (raw[offset] != raw[offset + 1])
            {
                asymmetricPairs++;
            }

            if (raw[offset] != pcm[offset + 1] || raw[offset + 1] != pcm[offset])
            {
                throw new InvalidOperationException(
                    $"{name} PCM byte order: pair at byte {offset} was not swapped exactly once.");
            }
        }

        True(asymmetricPairs > 10, name + " input exposes swaps using unequal byte pairs");
    }

    private static uint UInt32(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static ushort UInt16(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static void WritePatternPng(string path)
    {
        const int size = 256;
        const int stride = size * 4;
        byte[] pixels = new byte[size * stride];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte level = (byte)((((x * 17) + (y * 31) + ((x * y) % 23)) % 4) * 85);
                int offset = (y * stride) + (x * 4);
                pixels[offset] = level;
                pixels[offset + 1] = level;
                pixels[offset + 2] = level;
                pixels[offset + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static byte[] ReadPngPixels(string path)
    {
        using FileStream input = File.OpenRead(path);
        BitmapFrame frame = BitmapFrame.Create(
            input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap converted = new(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void Equal<T>(T expected, T actual, string name)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}.");
        }
    }

    private static void True(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{name}: condition was false.");
        }
    }

    private static void Throws<TException>(Action action, string name)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}.");
    }
}
