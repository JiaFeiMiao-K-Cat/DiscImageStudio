using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;

internal static class CdBinaryTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-binary-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            CdDiscParameters parameters = new(24.5, 56.8, 8, ImageOuterRadiusMm: 58);
            foreach ((byte input, byte palette, byte display) in new (byte, byte, byte)[]
            {
                (0, 0x0D, 0), (63, 0x0D, 0), (64, 0x0D, 0),
                (85, 0x0D, 0), (127, 0x0D, 0),
                (128, 0xAA, 255), (170, 0xAA, 255), (191, 0xAA, 255),
                (192, 0xAA, 255), (255, 0xAA, 255),
            })
            {
                TestLevel(directory, parameters, input, palette, display);
            }
            TestLegacyReadback(directory, parameters);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("cd-binary: passed (128 threshold, PNG/pattern, file/stream, binary previews and legacy gray readback)");
    }

    private static void TestLegacyReadback(string directory, CdDiscParameters parameters)
    {
        foreach ((byte encoded, byte luminance) in new (byte, byte)[] { (0x10, 85), (0x63, 170) })
        {
            Require(CdToneMapping.DisplayGray(encoded) == luminance,
                $"display mapping for 0x{encoded:X2} is {CdToneMapping.DisplayGray(encoded)}");
            string track = Path.Combine(directory, $"legacy-{encoded:X2}.raw");
            string preview = Path.Combine(directory, $"legacy-{encoded:X2}.png");
            File.WriteAllBytes(track, Enumerable.Repeat(encoded, checked((int)parameters.TotalBytes)).ToArray());
            CdTrackGenerator.PreviewTrack(track, preview, parameters, 128, 1, deinterleave: false);
            VerifyPreviewLevel(ReadPixels(preview), parameters, luminance, $"legacy 0x{encoded:X2} readback");
        }
    }

    private static void TestLevel(
        string directory,
        CdDiscParameters parameters,
        byte input,
        byte palette,
        byte display)
    {
        string sourcePath = Path.Combine(directory, $"source-{input}.png");
        WriteUniformPng(sourcePath, input);
        foreach (bool interleave in new[] { false, true })
        {
            string name = $"{input}-{interleave}";
            using MemoryStream rawStream = new(), patternStream = new(), pcmStream = new();
            CdTrackGenerator.GenerateToStream(sourcePath, rawStream, parameters, interleave);
            CdTrackGenerator.GeneratePatternToStream((_, _) => input, patternStream, parameters, interleave);
            CdTrackGenerator.GenerateToStream(sourcePath, pcmStream, parameters, interleave,
                audioByteOrder: CdAudioByteOrder.LittleEndian);
            byte[] raw = rawStream.ToArray();
            Require(raw.LongLength == parameters.TotalBytes, $"{name} emits complete audio sectors");
            Require(raw.SequenceEqual(patternStream.ToArray()), $"{name} PNG and analytic pattern bytes agree");
            // The delay buffer starts empty; two sectors are enough to reach steady output.
            int stableStart = interleave ? 2 * CdDiscParameters.BytesPerSector : 0;
            Require(raw.AsSpan(stableStart).IndexOfAnyExcept(palette) < 0,
                $"gray {input} encodes as 0x{palette:X2} in all steady audio bytes");

            string rawPath = Path.Combine(directory, name + ".raw");
            string wavePath = Path.Combine(directory, name + ".wav");
            string patternPath = Path.Combine(directory, name + "-pattern.wav");
            CdTrackGenerator.Generate(sourcePath, rawPath, parameters, interleave);
            CdTrackGenerator.Generate(sourcePath, wavePath, parameters, interleave);
            CdTrackGenerator.GeneratePattern((_, _) => input, patternPath, parameters, interleave);
            Require(File.ReadAllBytes(rawPath).SequenceEqual(raw), $"{name} RAW file equals its stream");
            byte[] wave = File.ReadAllBytes(wavePath);
            Require(wave.AsSpan(44).SequenceEqual(pcmStream.ToArray()), $"{name} WAV payload equals direct-burn PCM");
            Require(wave.SequenceEqual(File.ReadAllBytes(patternPath)), $"{name} pattern WAV equals PNG WAV");

            string rawPreview = Path.Combine(directory, name + "-raw-preview.png");
            string wavePreview = Path.Combine(directory, name + "-wave-preview.png");
            CdTrackGenerator.PreviewTrack(rawPath, rawPreview, parameters, 128, 1, interleave);
            CdTrackGenerator.PreviewTrack(wavePath, wavePreview, parameters, 128, 1, interleave);
            byte[] readback = ReadPixels(rawPreview);
            Require(readback.SequenceEqual(ReadPixels(wavePreview)), $"{name} RAW and WAV previews agree");
            VerifyPreviewLevel(readback, parameters, display, $"{name} readback");

            if (!interleave)
            {
                string warpPath = Path.Combine(directory, name + "-warp.png");
                CdTrackGenerator.PreviewWarp(sourcePath, warpPath, parameters, parameters, 128,
                    CdDiscParameters.BytesPerSector);
                byte[] warp = ReadPixels(warpPath);
                Require(warp.SequenceEqual(readback), $"gray {input} source projection exactly matches generated readback");
            }
        }
    }

    private static void VerifyPreviewLevel(byte[] pixels, CdDiscParameters parameters, byte expected, string name)
    {
        const int size = 128;
        int painted = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte value = pixels[offset];
            Require(value == expected || value == 255, $"{name} uses display luminance {expected}");
            Require(value == pixels[offset + 1] && value == pixels[offset + 2],
                $"{name} remains grayscale");
            int pixel = offset / 4;
            double x = (pixel % size - size / 2.0) * parameters.ImageOuterRadiusMm / (size / 2.0);
            double y = (pixel / size - size / 2.0) * parameters.ImageOuterRadiusMm / (size / 2.0);
            double radius = Math.Sqrt(x * x + y * y);
            bool recorded = radius >= parameters.InnerRadiusMm && radius < parameters.OuterRadiusMm;
            Require(pixels[offset + 3] == (recorded ? 255 : 0),
                $"{name} is opaque only in the recorded annulus");
            if (recorded && value == expected)
            {
                painted++;
            }
        }

        Require(painted > 0, $"{name} contains its expected level");
    }

    private static void WriteUniformPng(string path, byte gray)
    {
        const int size = 128;
        byte[] pixels = Enumerable.Repeat(gray, size * size).ToArray();
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static byte[] ReadPixels(string path)
    {
        using FileStream file = File.OpenRead(path);
        BitmapFrame frame = BitmapFrame.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap converted = new(frame, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"CD binary grayscale: {name}.");
        }
    }
}
