using System.IO;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Services;

internal static class CdPreviewMaskTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-preview-mask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.png");
            WriteSource(source);
            CdDiscParameters generated = new(24.5, 59.5, 128);
            CdDiscParameters fitted = generated with { ImageOuterRadiusMm = generated.OuterRadiusMm };
            TestAudio(directory, source, generated, fitted);
            TestData(directory, source, generated, fitted);
            TestLimitedOutput(directory, source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("cd-preview-mask: passed (audio/data source and readback, transparent hole/exterior, opaque unsampled ring, fit-outer without changing source mapping)");
    }

    private static void TestAudio(string directory, string source,
        CdDiscParameters generated, CdDiscParameters fitted)
    {
        string track = Path.Combine(directory, "audio.raw");
        CdTrackGenerator.Generate(source, track, generated, interleave: false);
        foreach ((int size, int samples, CdDiscParameters actual) in new[]
        {
            (512, CdDiscParameters.BytesPerSector, fitted),
            // Deliberately different readback geometry proves the mask follows actual,
            // while sparse samples leave visible white pixels inside the annulus.
            (128, 1, fitted with { InnerRadiusMm = 30, OuterRadiusMm = 54 }),
        })
        {
            string warp = Path.Combine(directory, $"audio-warp-{size}.png");
            string readback = Path.Combine(directory, $"audio-readback-{size}.png");
            CdTrackGenerator.PreviewWarp(source, warp, generated, actual, size, samples);
            CdTrackGenerator.PreviewTrack(track, readback, actual, size,
                CdDiscParameters.BytesPerSector / samples, deinterleave: false);
            BgraRaster projected = RasterCodec.LoadBgra32(warp);
            BgraRaster decoded = RasterCodec.LoadBgra32(readback);
            int opaqueWhite = VerifyMask(projected, size, actual, $"audio {size}");
            Require(projected.Pixels.SequenceEqual(decoded.Pixels), $"audio {size} source/readback parity");
            if (samples == 1)
                Require(opaqueWhite > generated.Sectors, "unsampled audio pixels inside the annulus stay opaque white");
            if (size == 512)
            {
                VerifyOuterRingVisible(projected, actual);
                string wrongMapping = Path.Combine(directory, "audio-wrong-source-mapping.png");
                CdTrackGenerator.PreviewWarp(source, wrongMapping, fitted, fitted, size, samples);
                Require(!projected.Pixels.SequenceEqual(RasterCodec.LoadBgra32(wrongMapping).Pixels),
                    "audio fixture detects accidentally changing the source canvas radius");
            }
        }
        Require(generated.ImageOuterRadiusMm == CdDiscParameters.StandardImageOuterRadiusMm,
            "fit-outer retains the generated source mapping");
    }

    private static void TestData(string directory, string source,
        CdDiscParameters generated, CdDiscParameters actual)
    {
        string files = Path.Combine(directory, "files");
        Directory.CreateDirectory(files);
        File.WriteAllBytes(Path.Combine(files, "sample.bin"), Enumerable.Range(0, 7000).Select(i => (byte)i).ToArray());
        foreach (bool outerFiles in new[] { false, true })
        {
            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(files, generated, filesAtOuterRing: outerFiles);
            string iso = Path.Combine(directory, $"data-{outerFiles}.iso");
            string warp = Path.Combine(directory, $"data-warp-{outerFiles}.png");
            string readback = Path.Combine(directory, $"data-readback-{outerFiles}.png");
            // Match the scalar source preview exactly, independently of SIMD rounding.
            CdDataImageGenerator.Generate(source, iso, generated, plan, useSimdSampling: false);
            CdDataImageGenerator.PreviewWarp(source, warp, generated, actual, 512,
                CdDataImageGenerator.BytesPerSector, ToneProfile.LegacyBinary, plan);
            CdDataImageGenerator.PreviewTrack(iso, readback, actual, 512, 1);
            BgraRaster projected = RasterCodec.LoadBgra32(warp);
            BgraRaster decoded = RasterCodec.LoadBgra32(readback);
            VerifyMask(projected, 512, actual, $"data outer files {outerFiles}");
            Require(projected.Pixels.SequenceEqual(decoded.Pixels),
                $"data outer files {outerFiles} source/readback parity with ISO sidecar ranges");
        }
    }

    private static void TestLimitedOutput(string directory, string source)
    {
        // Half of this track ends at exactly 45.5 mm. Source sampling deliberately
        // retains a different canvas, as a burn truncated to device capacity does.
        CdDiscParameters parameters = new(24.5, 59.5, 128,
            UsableSectors: 64, SourceImageOuterRadiusMm: 61);
        string track = Path.Combine(directory, "limited.raw");
        string warp = Path.Combine(directory, "limited-warp.png");
        string readback = Path.Combine(directory, "limited-readback.png");
        CdTrackGenerator.Generate(source, track, parameters, interleave: false);
        CdTrackGenerator.PreviewWarp(source, warp, parameters, parameters, 256, 32);
        CdTrackGenerator.PreviewTrack(track, readback, parameters, 256,
            CdDiscParameters.BytesPerSector / 32, deinterleave: false);
        BgraRaster projected = RasterCodec.LoadBgra32(warp);
        Require(projected.Pixels.SequenceEqual(RasterCodec.LoadBgra32(readback).Pixels),
            "limited audio projection and readback use the same display canvas");
        VerifyMask(projected, 256, parameters, "limited audio", canvasRadius: 45.5, outputOuter: 45.5);

        FileRegionPreviewOverlay.ApplyCd(warp, parameters, 16, 48);
        BgraRaster overlay = RasterCodec.LoadBgra32(warp);
        double drawingInnerSquared = 24.5 * 24.5 + (59.5 * 59.5 - 24.5 * 24.5) / 8;
        double drawingOuterSquared = 24.5 * 24.5 + (59.5 * 59.5 - 24.5 * 24.5) * 3 / 8;
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 256; x++)
        {
            double radius = Radius(x, y, 256, 45.5);
            double squared = radius * radius;
            bool expectedFile = radius >= 24.5 && radius < 45.5
                && (squared < drawingInnerSquared || squared >= drawingOuterSquared);
            int offset = (y * 256 + x) * 4;
            bool file = overlay.Pixels[offset] == 0xFF && overlay.Pixels[offset + 1] == 0x85
                && overlay.Pixels[offset + 2] == 0x33 && overlay.Pixels[offset + 3] == 0xFF;
            Require(file == expectedFile, $"limited file overlay at ({x}, {y})");
        }
    }

    private static int VerifyMask(BgraRaster image, int size, CdDiscParameters actual, string name,
        double? canvasRadius = null, double? outputOuter = null)
    {
        Require(image.Width == size && image.Height == size, $"{name} output dimensions");
        int opaqueWhite = 0, opaqueDark = 0, transparent = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double radius = Radius(x, y, size, canvasRadius ?? actual.ImageOuterRadiusMm);
            bool recorded = radius >= actual.InnerRadiusMm && radius < (outputOuter ?? actual.OuterRadiusMm);
            int offset = (y * size + x) * 4;
            Require(image.Pixels[offset + 3] == (recorded ? 255 : 0), $"{name} alpha at ({x}, {y})");
            if (!recorded) transparent++;
            else if (image.Pixels[offset] == 255) opaqueWhite++;
            else opaqueDark++;
        }
        Require(transparent > 0 && opaqueWhite > 0 && opaqueDark > 0,
            $"{name} contains transparent exterior/hole, opaque white gaps and sampled drawing");
        return opaqueWhite;
    }

    private static void VerifyOuterRingVisible(BgraRaster image, CdDiscParameters actual)
    {
        int outerSamples = 0;
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            double radius = Radius(x, y, image.Width, actual.ImageOuterRadiusMm);
            int offset = (y * image.Width + x) * 4;
            if (radius > CdDiscParameters.StandardImageOuterRadiusMm && radius < actual.OuterRadiusMm
                && image.Pixels[offset + 3] == 255 && image.Pixels[offset] < 255)
                outerSamples++;
        }
        Require(outerSamples > 0, "fit-outer shows actual drawing beyond the 57.5 mm source canvas radius");
    }

    private static double Radius(int x, int y, int size, double canvasRadius)
    {
        double dx = x - size / 2.0, dy = y - size / 2.0;
        return Math.Sqrt(dx * dx + dy * dy) * canvasRadius / (size / 2.0);
    }

    private static void WriteSource(string path)
    {
        const int size = 128;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            byte gray = (byte)(((x / 7 + y / 11) & 1) == 0 ? 0 : 255);
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = gray;
            pixels[offset + 3] = 255;
        }
        RasterCodec.SavePng(path, size, size, pixels, grayscale: false);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"CD preview mask: {name}.");
    }
}
