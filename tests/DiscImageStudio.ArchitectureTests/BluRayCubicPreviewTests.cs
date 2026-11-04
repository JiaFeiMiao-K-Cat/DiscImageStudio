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
