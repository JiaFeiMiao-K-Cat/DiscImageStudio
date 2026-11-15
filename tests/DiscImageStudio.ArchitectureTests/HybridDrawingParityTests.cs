using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

/// <summary>
/// Issue #289 regression: adding sidecar files must not move the drawing at a given
/// absolute LBA. The pure (no filesystem) image and the hybrid image are generated with
/// identical geometry, then their drawing bytes are compared over the shared LBA range.
/// A mismatch would mean the hybrid path rescales, rotates or shears the artwork.
/// </summary>
internal static class HybridDrawingParityTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"hybrid-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string data = Path.Combine(directory, "data");
            Directory.CreateDirectory(data);
            // ~60 KiB of file data so the drawing starts well past LBA 0.
            byte[] fileBytes = Enumerable.Range(0, 61_440).Select(i => (byte)(i * 31 + 7)).ToArray();
            File.WriteAllBytes(Path.Combine(data, "payload.bin"), fileBytes);
            string image = Path.Combine(directory, "source.png");
            WriteOpaqueImage(image);

            DvdParity(directory, data, image);
            BluRayParity(directory, data, image);
            CdParity(directory, data, image);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("hybrid-drawing-parity: passed (pure vs hybrid drawing bytes identical at shared absolute LBA for CD/DVD/BD)");
    }

    private static void DvdParity(string directory, string data, string image)
    {
        // Constant pitch, hard threshold.
        DvdParityCore(directory, data, image, "dvd", new DvdStreamingOptions(1024, 24, 58, FastParallelism: 1));
        // Issue #289 used measured variable pitch plus a dithered photographic tone; exercise
        // the cubic-pitch sampler and the absolute-index dither phase on the same invariant.
        DvdParityCore(directory, data, image, "dvd-pitch", new DvdStreamingOptions(
            2048, 24, 58, FastParallelism: 1,
            PitchLinear: 0.2, PitchQuadratic: -0.1, PitchCubic: 0.05,
            Tone: new ToneProfile(0.9, ToneProfile.TwoLevels, 6.0)));
    }

    private static void DvdParityCore(string directory, string data, string image,
        string prefix, DvdStreamingOptions options)
    {
        string pure = Path.Combine(directory, prefix + "-pure.iso");
        string inner = Path.Combine(directory, prefix + "-inner.iso");
        string outer = Path.Combine(directory, prefix + "-outer.iso");
        DvdImageGenerator.Generate(new DvdImageRequest(image, pure, options));
        DvdImageGenerator.Generate(new DvdImageRequest(image, inner, options, data, "PARITY", false));
        DvdImageGenerator.Generate(new DvdImageRequest(image, outer, options, data, "PARITY", true));

        byte[] pureBytes = File.ReadAllBytes(pure);
        foreach ((string path, bool outerRing) in new[] { (inner, false), (outer, true) })
        {
            DvdImageMetadata metadata = DvdImageMetadata.TryLoad(path)
                ?? throw new InvalidOperationException("Missing DVD parity sidecar: " + path);
            uint start = metadata.DrawingStartLba;
            uint end = metadata.DrawingEndLbaExclusive;
            Require(start > 0 && end > start && end <= options.OutputSectors, "DVD hybrid drawing range is nontrivial");
            byte[] hybridBytes = File.ReadAllBytes(path);
            Require(hybridBytes.Length == pureBytes.Length, "DVD pure and hybrid volumes have equal size");
            RequireSpanEqual(pureBytes, hybridBytes, start, end, 2048,
                $"DVD {(outerRing ? "outer" : "inner")} drawing matches pure at the same absolute LBA");
        }
    }

    private static void BluRayParity(string directory, string data, string image)
    {
        BluRayDiscParameters disc = new(TotalSectors: 4096, InnerRadiusMm: 24, OuterRadiusMm: 58);
        BluRayImageGenerator generator = new(BluRayImageLoader.Load);
        string pure = Path.Combine(directory, "bd-pure.iso");
        string inner = Path.Combine(directory, "bd-inner.iso");
        string outer = Path.Combine(directory, "bd-outer.iso");
        generator.Generate(new BluRayImageRequest(image, pure, disc, ToneProfile.LegacyBinary));
        generator.Generate(new BluRayImageRequest(image, inner, disc, ToneProfile.LegacyBinary,
            DataDirectory: data, VolumeLabel: "PARITY", FilesAtOuterRing: false));
        generator.Generate(new BluRayImageRequest(image, outer, disc, ToneProfile.LegacyBinary,
            DataDirectory: data, VolumeLabel: "PARITY", FilesAtOuterRing: true));

        byte[] pureBytes = File.ReadAllBytes(pure);
        foreach ((string path, bool outerRing) in new[] { (inner, false), (outer, true) })
        {
            BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc, data, "PARITY",
                filesAtOuterRing: outerRing);
            uint start = plan.DrawingStartSector;
            uint end = plan.DrawingEndSectorExclusive;
            Require(start > 0 && end > start && end <= disc.OutputSectors, "BD hybrid drawing range is nontrivial");
            byte[] hybridBytes = File.ReadAllBytes(path);
            Require(hybridBytes.Length == pureBytes.Length, "BD pure and hybrid volumes have equal size");
            RequireSpanEqual(pureBytes, hybridBytes, start, end, 2048,
                $"BD {(outerRing ? "outer" : "inner")} drawing matches pure at the same absolute LBA");
        }
    }

    private static void CdParity(string directory, string data, string image)
    {
        // CD pure export is an audio track, so the filesystem-free reference is the inner
        // hybrid itself; the invariant is that moving files to the outer ring never moves
        // the drawing at a shared absolute LBA.
        CdDiscParameters parameters = new(24, 58, Sectors: 1024);
        string inner = Path.Combine(directory, "cd-inner.iso");
        string outer = Path.Combine(directory, "cd-outer.iso");
        CdImageGenerator.Generate(new CdImageRequest(image, inner, parameters, ToneProfile.LegacyBinary,
            Interleave: false, WriteCue: false, DataDirectory: data, VolumeLabel: "PARITY", FilesAtOuterRing: false));
        CdImageGenerator.Generate(new CdImageRequest(image, outer, parameters, ToneProfile.LegacyBinary,
            Interleave: false, WriteCue: false, DataDirectory: data, VolumeLabel: "PARITY", FilesAtOuterRing: true));

        CdDataImageMetadata innerMeta = CdDataImageMetadata.TryLoad(inner)
            ?? throw new InvalidOperationException("Missing CD inner parity sidecar.");
        CdDataImageMetadata outerMeta = CdDataImageMetadata.TryLoad(outer)
            ?? throw new InvalidOperationException("Missing CD outer parity sidecar.");
        uint start = Math.Max(innerMeta.DrawingStartLba, outerMeta.DrawingStartLba);
        uint end = Math.Min(innerMeta.DrawingEndLbaExclusive, outerMeta.DrawingEndLbaExclusive);
        Require(start > 0 && end > start, "CD hybrid drawing ranges overlap");
        byte[] innerBytes = File.ReadAllBytes(inner);
        byte[] outerBytes = File.ReadAllBytes(outer);
        Require(innerBytes.Length == outerBytes.Length, "CD inner and outer volumes have equal size");
        RequireSpanEqual(innerBytes, outerBytes, start, end, CdDataImageGenerator.BytesPerSector,
            "CD outer drawing matches inner at the same absolute LBA");
    }

    private static void RequireSpanEqual(byte[] a, byte[] b, uint startLba, uint endLbaExclusive,
        int sectorSize, string message)
    {
        int start = checked((int)startLba * sectorSize);
        int end = checked((int)endLbaExclusive * sectorSize);
        if (a.AsSpan(start, end - start).SequenceEqual(b.AsSpan(start, end - start))) return;
        int firstDiff = -1;
        for (int i = start; i < end; i++)
        {
            if (a[i] != b[i]) { firstDiff = i; break; }
        }
        throw new InvalidOperationException(
            $"{message}: first differing byte at offset {firstDiff} (LBA {firstDiff / sectorSize}).");
    }

    private static void WriteOpaqueImage(string path)
    {
        const int size = 64;
        byte[] pixels = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            byte gray = (byte)(i * 37);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = gray;
            pixels[i * 4 + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Hybrid drawing parity: " + message);
    }
}