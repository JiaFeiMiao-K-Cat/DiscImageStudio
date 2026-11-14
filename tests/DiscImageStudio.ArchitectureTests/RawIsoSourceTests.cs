using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Iso;
using DvdImageSolver.Solver;

internal static class RawIsoSourceTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"raw-iso-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.iso");
            byte[] sourceBytes = CreateIso(source, 24);
            VerifyInspection(source);
            VerifyOverlay(source, sourceBytes);
            VerifyCdGeneration(root, source, sourceBytes);
            VerifyDvdGeneration(root, source, sourceBytes);
            VerifyBluRayGeneration(root, source, sourceBytes);
            VerifyCueExport(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("raw-iso-source: passed (validation, exact source prefix, CD/DVD/BD generation, CUE/BIN export)");
    }

    private static void VerifyInspection(string source)
    {
        RawIsoImageInfo info = RawIsoImage.Inspect(source, 24);
        Require(info.SectorCount == 24 && info.FileSystem == "ISO9660", "valid ISO descriptor is detected");
        Throws<ArgumentException>(() => RawIsoImage.Inspect(source, 23), "target capacity is enforced");
        string truncated = source + ".bad.iso";
        File.WriteAllBytes(truncated, [1]);
        Throws<InvalidDataException>(() => RawIsoImage.Inspect(truncated), "partial logical sectors are rejected");
        string invalid = source + ".invalid.iso";
        File.WriteAllBytes(invalid, new byte[24 * RawIsoImage.LogicalSectorBytes]);
        Throws<InvalidDataException>(() => RawIsoImage.Inspect(invalid), "unrecognized images are rejected");
    }

    private static void VerifyOverlay(string source, byte[] sourceBytes)
    {
        byte[] generated = Enumerable.Repeat((byte)0xA5, 32 * RawIsoImage.LogicalSectorBytes).ToArray();
        using MemoryStream output = new();
        using (RawIsoPrefixOverlayStream overlay = RawIsoImage.OverlayPrefix(output, source, 32))
        {
            Require(!overlay.CanSeek, "ISO overlay is forward-only");
            overlay.Write(generated);
            overlay.Complete();
        }

        byte[] result = output.ToArray();
        Require(result.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes), "source bytes are copied exactly at LBA 0");
        Require(result.AsSpan(sourceBytes.Length).SequenceEqual(generated.AsSpan(sourceBytes.Length)),
            "generated artwork remains unchanged after the source prefix");
    }

    private static void VerifyCdGeneration(string root, string source, byte[] sourceBytes)
    {
        string artwork = WritePng(root, "artwork.png");
        CdDiscParameters parameters = new(25, 58, 40);
        CdDataLayoutPlan plan = CdDataImageGenerator.PrepareRawImageLayout(source, parameters);
        string outputPath = Path.Combine(root, "cd.iso");
        CdDataImageGenerator.Generate(artwork, outputPath, parameters, plan, useSimdSampling: false);
        byte[] result = File.ReadAllBytes(outputPath);
        Require(result.Length == 40 * RawIsoImage.LogicalSectorBytes, "CD stream has target capacity");
        Require(result.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes), "CD output retains the original ISO prefix");
        Require(plan.DrawingStartLba == 24 && plan.DrawingEndLbaExclusive == 40,
            "CD artwork starts after every source sector");
        Require(CdDataImageMetadata.TryLoad(outputPath)?.SourceImageSectors == 24,
            "CD sidecar distinguishes source sectors from artwork");
    }

    private static void VerifyDvdGeneration(string root, string source, byte[] sourceBytes)
    {
        string artwork = WritePng(root, "dvd-artwork.png");
        string output = Path.Combine(root, "dvd.iso");
        DvdStreamingOptions options = new(64, 24, 58, FastParallelism: 1);
        DvdImageGenerator.Generate(new(artwork, output, options, DataImagePath: source));
        byte[] result = File.ReadAllBytes(output);
        Require(result.Length == 64 * RawIsoImage.LogicalSectorBytes, "DVD output has target capacity");
        Require(result.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes), "DVD output retains the original ISO prefix");
        Require(result.AsSpan(sourceBytes.Length, 8 * RawIsoImage.LogicalSectorBytes)
            .ToArray().All(value => value == 0),
            "DVD generation leaves the partial source ECC block untouched");
        DvdImageMetadata metadata = DvdImageMetadata.TryLoad(output)
            ?? throw new InvalidOperationException("DVD raw ISO sidecar is missing or invalid.");
        Require(metadata.SourceImageSectors == 24 && metadata.DrawingStartLba == 32
            && metadata.DrawingEndLbaExclusive == 64,
            "DVD artwork begins after the non-ECC-aligned ISO prefix");

        var range = IsoReadbackRenderer.ReadDrawingRange(output, 64);
        Require(range == (32u, 32u), "DVD readback excludes source ISO sectors and partial ECC block");
        string preview = Path.Combine(root, "dvd-readback.png");
        IsoReadbackSummary readback = IsoReadbackRenderer.Render(output, preview,
            new IsoReadbackOptions(64, range.StartLba, range.FillSectors, 0x30000,
                24, 58, 133.3, true, 128, 16));
        Require(readback.ClassifiedSamples > 0, "DVD readback classifies generated artwork");
        byte[] previewPixels = RasterCodec.LoadBgra32(preview).Pixels;
        Require(Enumerable.Range(0, previewPixels.Length / 4)
            .Any(index => previewPixels[index * 4] < 64 && previewPixels[index * 4 + 3] != 0),
            "DVD readback visibly renders dark artwork pixels");

        string insufficient = Path.Combine(root, "dvd-insufficient.iso");
        Throws<ArgumentException>(() => DvdImageGenerator.Generate(
            new(artwork, insufficient, new DvdStreamingOptions(32, 24, 58), DataImagePath: source)),
            "a source ISO leaving no complete ECC block is rejected");
        Require(!File.Exists(insufficient), "insufficient artwork space creates no output");
    }

    private static void VerifyBluRayGeneration(string root, string source, byte[] sourceBytes)
    {
        string artwork = WritePng(root, "bd-artwork.png");
        string output = Path.Combine(root, "bd.iso");
        BluRayDiscParameters parameters = new(TotalSectors: 64);
        BluRayImageGenerator generator = new((_, _) => new BluRayGray8Raster(8, 8, new byte[64]));
        generator.Generate(new(artwork, output, parameters, ToneProfile.LegacyBinary, DataImagePath: source));
        byte[] result = File.ReadAllBytes(output);
        Require(result.Length == 64 * RawIsoImage.LogicalSectorBytes, "BD output has target capacity");
        Require(result.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes), "BD output retains the original ISO prefix");
        Require(BluRayIsoMetadata.TryLoad(output)?.SourceImageSectors == 24,
            "BD sidecar distinguishes source sectors from artwork");
    }

    private static void VerifyCueExport(string root)
    {
        string sourceDirectory = Path.Combine(root, "cue-source");
        Directory.CreateDirectory(sourceDirectory);
        byte[] track = Enumerable.Range(0, 2048).Select(index => (byte)(index * 13)).ToArray();
        File.WriteAllBytes(Path.Combine(sourceDirectory, "track.bin"), track);
        string cue = Path.Combine(sourceDirectory, "source.cue");
        File.WriteAllText(cue,
            "FILE \"track.bin\" BINARY\r\n"
            + "  TRACK 01 MODE1/2352\r\n"
            + "    INDEX 01 00:00:00\r\n"
            + "  TRACK 02 AUDIO\r\n"
            + "    INDEX 01 00:00:10\r\n");
        CueBinImageInfo info = CueBinImage.Inspect(cue);
        Require(info.BinaryFiles.Count == 1 && info.TotalBinaryBytes == track.Length, "CUE references are inspected");

        string exportDirectory = Path.Combine(root, "cue-export");
        string outputCue = Path.Combine(exportDirectory, "export.cue");
        CueBinImage.Export(cue, outputCue);
        Require(File.ReadAllBytes(Path.Combine(exportDirectory, "export-01.bin")).SequenceEqual(track),
            "binary tracks export byte-for-byte");
        string exportedCue = File.ReadAllText(outputCue);
        Require(exportedCue.Contains("FILE \"export-01.bin\" BINARY", StringComparison.Ordinal)
            && exportedCue.Contains("TRACK 02 AUDIO", StringComparison.Ordinal),
            "CUE references are rewritten while track structure is retained");
    }

    private static byte[] CreateIso(string path, int sectors)
    {
        byte[] bytes = new byte[sectors * RawIsoImage.LogicalSectorBytes];
        new Random(781).NextBytes(bytes);
        Span<byte> primary = bytes.AsSpan(16 * RawIsoImage.LogicalSectorBytes, RawIsoImage.LogicalSectorBytes);
        primary[0] = 1;
        "CD001"u8.CopyTo(primary[1..]);
        primary[6] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(primary[80..], checked((uint)sectors));
        BinaryPrimitives.WriteUInt32BigEndian(primary[84..], checked((uint)sectors));
        Span<byte> terminator = bytes.AsSpan(17 * RawIsoImage.LogicalSectorBytes, RawIsoImage.LogicalSectorBytes);
        terminator[0] = 255;
        "CD001"u8.CopyTo(terminator[1..]);
        terminator[6] = 1;
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    private static string WritePng(string root, string name)
    {
        string path = Path.Combine(root, name);
        byte[] pixels = new byte[64 * 64];
        for (int index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index * 19);
        BitmapSource bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Gray8, null, pixels, 64);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
