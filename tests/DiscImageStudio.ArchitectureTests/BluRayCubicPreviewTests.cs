using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Services;

internal static class BluRayCubicPreviewTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-cubic-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const int size = 128;
            string source = Path.Combine(directory, "source.png");
            byte[] sourcePixels = new byte[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++) sourcePixels[y * size + x] = x < size / 2 ? (byte)0 : (byte)255;
            var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, sourcePixels, size);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(source)) encoder.Save(stream);

            var disc = new BluRayDiscParameters(StartAngleDegrees: 41,
                PitchLinear: .24, PitchQuadratic: -.18, PitchCubic: .08);
            string preview = Path.Combine(directory, "matching.png");
            BluRayPreviewRenderer.RenderCalibration(source, preview, disc, disc, 0, disc.TotalSectors, size, false);
            CheckOrientation(Read(preview), size, rotated: false, "matching cubic projection");
            string rotated = Path.Combine(directory, "rotated.png");
            BluRayPreviewRenderer.RenderCalibration(source, rotated, disc, disc with { StartAngleDegrees = 131 },
                0, disc.TotalSectors, size, false);
            CheckOrientation(Read(rotated), size, rotated: true, "rotated cubic projection");

            TestLimitedCapacity(directory);

            // A real, complete ISO exercises inverse spiral projection and the
            // LDC/BIS payload-address lookup together without writing 25 GB.
            disc = disc with { TotalSectors = 2048 };
            string iso = Path.Combine(directory, "readback.iso");
            var mapping = new BluRayImagePayloadMapping(disc,
                static (x, _) => x < 0 ? (byte)0 : (byte)255, new BluRayRunLengthMapping());
            BluRayStreamingGenerator.GenerateFile(iso, new(disc, 0, disc.TotalSectors), mapping);
            string readback = Path.Combine(directory, "readback.png");
            BluRayPreviewRenderer.RenderReadback(iso, readback, disc, size);
            CheckOrientation(Read(readback), size, rotated: false, "cubic ISO readback");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("blu-ray-cubic-preview: passed (matching/rotated projection and actual ISO readback)");
    }

    internal static void RunUsableCapacity()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-usable-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { TestLimitedCapacity(directory); }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("blu-ray-usable-preview: passed (cubic usable-edge image scale, projection parity and limited readback)");
    }

    private static void TestLimitedCapacity(string directory)
    {
        const int size = 128;
        string source = Path.Combine(directory, "usable-ring.png");
        byte[] gray = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double dx = (x + .5 - size / 2.0) / (size / 2.0);
                double dy = (y + .5 - size / 2.0) / (size / 2.0);
                gray[y * size + x] = dx * dx + dy * dy > .82 * .82 ? (byte)0 : (byte)255;
            }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, gray, size);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(source)) encoder.Save(stream);

        // A reduced output range is intentionally a different size from the full
        // cubic geometry. This exercises the costly outer-radius calculation as
        // well as source sampling, rather than the full-capacity fast return.
        var limited = new BluRayDiscParameters(PitchLinear: .24, PitchQuadratic: -.18, PitchCubic: .08,
            UsableSectors: BluRayDiscParameters.SectorsPerLayer / 2);
        string projected = Path.Combine(directory, "usable-projection.png");
        string calibrated = Path.Combine(directory, "usable-calibration.png");
        BluRayPreviewRenderer.RenderProjection(source, projected, limited, size, false, null);
        BluRayPreviewRenderer.RenderCalibration(source, calibrated, limited, limited, 0, limited.OutputSectors, size, false);
        byte[] pixels = Read(projected);
        if (!pixels.AsSpan().SequenceEqual(Read(calibrated)))
            throw new InvalidOperationException("Usable BD matching-geometry projection paths disagree.");
        CheckPixel(pixels, 122, 63, 0, 255, "the source image's outer black ring reaches the usable edge");
        CheckPixel(pixels, 108, 63, 255, 255, "the source image's inner white area retains its position");
        CheckPixel(pixels, 64, 64, 0, 0, "the hub remains transparent");
        CheckPixel(pixels, 0, 0, 0, 0, "pixels outside the usable circle remain transparent");

        // Keep the geometry larger than the ISO, including on the readback path.
        // Both constant and cubic projection must stay within the shorter file.
        foreach (bool cubic in new[] { false, true })
        {
            var small = limited with { TotalSectors = 2048, UsableSectors = 1024,
                PitchLinear = cubic ? .24 : 0, PitchQuadratic = cubic ? -.18 : 0, PitchCubic = cubic ? .08 : 0 };
            string iso = Path.Combine(directory, $"usable-{cubic}.iso");
            BluRayStreamingGenerator.GenerateFile(iso, new(small, 0, small.OutputSectors),
                new BluRayConstantByteMapping(BluRayRunLengthMapping.ThreeTByte));
            string readback = Path.Combine(directory, $"usable-readback-{cubic}.png");
            BluRayPreviewRenderer.RenderReadback(iso, readback, small, size);
            byte[] read = Read(readback);
            int opaque = 0;
            for (int offset = 0; offset < read.Length; offset += 4)
            {
                if (read[offset + 3] == 0) continue;
                opaque++;
                if (read[offset] != 0 || read[offset + 1] != 0 || read[offset + 2] != 0)
                    throw new InvalidOperationException("Limited BD readback misclassified its payload.");
            }
            if (opaque < 5000) throw new InvalidOperationException("Limited BD readback omitted the usable annulus.");
            CheckPixel(read, 64, 64, 0, 0, "limited readback keeps the hub transparent");
            CheckPixel(read, 0, 0, 0, 0, "limited readback keeps the exterior transparent");
        }

        static void CheckPixel(byte[] pixels, int x, int y, byte level, byte alpha, string message)
        {
            int offset = (y * size + x) * 4;
            if (pixels[offset + 3] != alpha || alpha != 0 && pixels[offset] != level)
                throw new InvalidOperationException("Usable BD preview: " + message);
        }
    }

    private static void CheckOrientation(byte[] pixels, int size, bool rotated, string label)
    {
        int compared = 0, matching = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int offset = (y * size + x) * 4;
                if (pixels[offset + 3] == 0 || Math.Abs((rotated ? y : x) - (size - 1) / 2.0) < 12) continue;
                compared++;
                byte expected = (rotated ? y >= size / 2 : x < size / 2) ? (byte)0 : (byte)255;
                if (pixels[offset] == expected) matching++;
            }
        if (compared < 5000 || matching < compared * .99)
            throw new InvalidOperationException($"{label}: {matching}/{compared} source-orientation samples matched.");
    }

    private static byte[] Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
