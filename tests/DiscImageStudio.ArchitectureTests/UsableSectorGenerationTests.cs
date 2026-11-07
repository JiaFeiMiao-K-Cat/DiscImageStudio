using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;

internal static class UsableSectorGenerationTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "usable-generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string image = Path.Combine(directory, "source.png");
            WriteImage(image);
            string files = Path.Combine(directory, "files");
            Directory.CreateDirectory(files);
            File.WriteAllText(Path.Combine(files, "marker.txt"), "usable capacity preserves outer files");
            CheckCd(directory, image, files);
            CheckDvd(directory, image, files);
            CheckBluRay(directory, files);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("usable-sector-generation: passed (unchanged geometry, bounded streams/filesystems, image scale, sidecar round trips)");
    }

    private static byte Pattern(double x, double y) => x + y > 0 ? (byte)0 : (byte)255;

    private static void CheckCd(string directory, string image, string files)
    {
        CdDiscParameters full = new(24, 58, 256, PitchLinear: .001);
        CdDiscParameters limited = full with { UsableSectors = 128 };
        limited.Validate();
        Require(limited.Sectors == 256 && limited.TotalBytes == 128 * 2352, "CD separates geometry from output");
        Require(full.PolarFromByte(64 * 2352) == limited.PolarFromByte(64 * 2352), "CD addresses retain their geometry");
        Require(limited.OutputOuterRadiusMm < full.OuterRadiusMm, "CD effective outer edge follows the old spiral");
        (double x, double y) = limited.ImagePointFromByte(limited.TotalBytes, 128);
        Require(Math.Abs(Math.Sqrt((x - 64) * (x - 64) + (y - 64) * (y - 64)) - 64) < 1e-10,
            "CD source canvas extends to the usable edge");
        using MemoryStream complete = new(), shortened = new();
        CdTrackGenerator.GeneratePatternToStream(Pattern, complete, full, false);
        CdTrackGenerator.GeneratePatternToStream(Pattern, shortened, limited, false);
        Require(shortened.Length == limited.TotalBytes && complete.ToArray().AsSpan(0, (int)shortened.Length).SequenceEqual(shortened.ToArray()),
            "CD analytic calibration truncates output without changing existing bytes");
        using MemoryStream interleaved = new();
        CdTrackGenerator.GeneratePatternToStream(Pattern, interleaved, limited, true);
        Require(interleaved.Length == limited.TotalBytes, "CD interleave flush honors the limited track length");
        CdDiscParameters runtimeLimited = limited with
        {
            UsableSectors = 64,
            SourceImageOuterRadiusMm = limited.SamplingImageOuterRadiusMm,
        };
        using MemoryStream sourceFull = new(), sourceCut = new();
        CdTrackGenerator.GenerateToStream(image, sourceFull, limited, false);
        CdTrackGenerator.GenerateToStream(image, sourceCut, runtimeLimited, false);
        using MemoryStream geometryScaled = new();
        CdTrackGenerator.GenerateToStream(image, geometryScaled,
            limited with { SourceImageOuterRadiusMm = full.OuterRadiusMm }, false);
        Require(!sourceFull.ToArray().AsSpan().SequenceEqual(geometryScaled.ToArray()),
            "CD outer-ring artwork detects clipping when sampled at the geometry edge instead of the usable edge");
        Require(sourceFull.ToArray().AsSpan(0, (int)sourceCut.Length).SequenceEqual(sourceCut.ToArray()),
            "CD runtime capacity truncates the selected source image without rescaling");
        string audio = Path.Combine(directory, "cd.raw");
        CdTrackGenerator.Generate(image, audio, limited, false, writeCue: false);
        Require(CdTrackMetadata.TryLoad(audio)?.CreateParameters() == limited, "CD audio sidecar retains both counts");
        string runtimeAudio = Path.Combine(directory, "cd-runtime.raw");
        CdTrackGenerator.Generate(image, runtimeAudio, runtimeLimited, false, writeCue: false);
        Require(CdTrackMetadata.TryLoad(runtimeAudio)?.CreateParameters() == runtimeLimited,
            "CD sidecar retains the captured source image scale");
        string cli = Path.Combine(directory, "cd-cli.raw");
        CdDiscModule cdModule = new();
        Require(cdModule.Descriptor.Commands.First(c => c.Name == "cd-generate").Options.Any(o => o.Name == "usable-sectors"),
            "CD command metadata exposes the usable limit");
        cdModule.ExecuteAsync(new("cd-generate", ["--input", image, "--output", cli, "--sectors", "256",
            "--usable-sectors", "128", "--interleave", "false"])).GetAwaiter().GetResult();
        Require(new FileInfo(cli).Length == 128 * 2352 && CdTrackMetadata.TryLoad(cli) is { Sectors: 256, UsableSectors: 128 },
            "CD CLI preserves explicit output capacity");
        foreach (bool outer in new[] { false, true })
        {
            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(files, limited, filesAtOuterRing: outer);
            using MemoryStream stream = new();
            CdDataImageGenerator.GenerateToStream(image, stream, limited, plan);
            byte[] bytes = stream.ToArray();
            Require(plan.TotalSectors == 128 && bytes.Length == 128 * 2048
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16 * 2048 + 80)) == 128,
                "CD filesystem volume and output share usable capacity");
            Require(bytes.AsSpan().IndexOf("usable capacity preserves outer files"u8) >= 0, "CD files survive usable-capacity planning");
        }
    }

    private static void CheckDvd(string directory, string image, string files)
    {
        DvdStreamingOptions full = new(256, 24, 58, PitchLinear: .001, FastParallelism: 1);
        DvdStreamingOptions limited = full with { UsableSectors = 128 };
        limited.Validate();
        new DvdStreamingOptions(714546, 24, 38, UsableSectors: 713936).Validate();
        Require(limited.TotalSectors == 256 && limited.ContentLength == 128 * 2048 && limited.OutputOuterRadiusMm < 58,
            "DVD usable capacity leaves full geometry intact");
        using MemoryStream complete = new(), shortened = new();
        DvdStreamingGenerator.GeneratePattern(Pattern, complete, full);
        DvdStreamingGenerator.GeneratePattern(Pattern, shortened, limited);
        Require(shortened.Length == limited.ContentLength && complete.ToArray().AsSpan(0, (int)shortened.Length).SequenceEqual(shortened.ToArray()),
            "DVD calibration payload is an exact prefix of the full geometry");
        DvdStreamingOptions runtimeLimited = limited with
        {
            UsableSectors = 64,
            SourceImageOuterRadiusMm = limited.SamplingImageOuterRadiusMm,
        };
        using MemoryStream sourceFull = new(), sourceCut = new();
        DvdStreamingGenerator.Generate(image, sourceFull, limited);
        DvdStreamingGenerator.Generate(image, sourceCut, runtimeLimited);
        using MemoryStream geometryScaled = new();
        DvdStreamingGenerator.Generate(image, geometryScaled,
            limited with { SourceImageOuterRadiusMm = full.OuterRadiusMm });
        Require(!sourceFull.ToArray().AsSpan().SequenceEqual(geometryScaled.ToArray()),
            "DVD outer-ring artwork detects clipping when sampled at the geometry edge instead of the usable edge");
        Require(sourceFull.ToArray().AsSpan(0, (int)sourceCut.Length).SequenceEqual(sourceCut.ToArray()),
            "DVD runtime capacity truncates the selected source image without rescaling");
        string reduced = Path.Combine(directory, "dvd-runtime.iso");
        DvdImageGenerator.Generate(new(image, reduced, runtimeLimited));
        Require(DvdImageMetadata.TryLoad(reduced)?.SourceImageOuterRadiusMm == limited.OutputOuterRadiusMm,
            "DVD sidecar retains the captured source image scale");
        foreach (bool outer in new[] { false, true })
        {
            string output = Path.Combine(directory, $"dvd-{outer}.iso");
            DvdImageGenerator.Generate(new(image, output, limited, files, FilesAtOuterRing: outer));
            byte[] bytes = File.ReadAllBytes(output);
            Require(bytes.Length == 128 * 2048
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16 * 2048 + 80)) == 128,
                "DVD file length and ISO volume match usable sectors");
            Require(bytes.AsSpan().IndexOf("usable capacity preserves outer files"u8) >= 0, "DVD outer files are planned before truncation");
            Require(DvdImageMetadata.TryLoad(output) is { TotalSectors: 256, UsableSectors: 128, OutputSectors: 128 },
                "DVD sidecar keeps full geometry and output count");
            using MemoryStream streamed = new();
            DvdStreamingGenerator.GenerateHybrid(image, streamed, limited,
                DvdStreamingGenerator.PrepareHybrid(files, limited, filesAtOuterRing: outer));
            Require(streamed.Length == bytes.Length, "DVD file and stream use the same bounded layout");
        }
    }

    private static void CheckBluRay(string directory, string files)
    {
        BluRayDiscParameters full = new(2048, PitchLinear: .001);
        BluRayDiscParameters limited = full with { UsableSectors = 1024 };
        BluRayTrackGeometry original = new(full), clipped = new(limited);
        Require(original.AtSector(512) == clipped.AtSector(512) && limited.OutputOuterRadiusMm < full.OuterRadiusMm,
            "BD usable range does not alter physical addresses");
        byte[] pixels = Enumerable.Range(0, 64 * 64).Select(i => (byte)(i % 64 * 4)).ToArray();
        BluRayGray8Raster raster = new(64, 64, pixels);
        SamplingCheck mapping = new(raster, limited.OutputOuterRadiusMm, full.OuterRadiusMm);
        BluRayImagePayloadMapping imageMapping = new(limited, raster, mapping);
        using MemoryStream raw = new();
        BluRayIsoPlan rawPlan = BluRayIsoGenerator.PrepareDrawing(limited);
        BluRayIsoGenerator.GenerateToStream(raw, rawPlan, imageMapping);
        Require(raw.Length == 1024 * 2048 && mapping.SawChangedScale, "BD image uses the complete source within usable sectors");
        BluRayDiscParameters runtimeLimited = limited with { UsableSectors = 512 };
        using MemoryStream sourceFull = new(), sourceCut = new();
        BluRayIsoGenerator.GenerateToStream(sourceFull, rawPlan,
            new BluRayImagePayloadMapping(limited, raster, new BluRayRunLengthMapping()));
        BluRayIsoGenerator.GenerateToStream(sourceCut, BluRayIsoGenerator.PrepareDrawing(runtimeLimited),
            new BluRayImagePayloadMapping(runtimeLimited, raster, new BluRayRunLengthMapping(), limited.OutputOuterRadiusMm));
        Require(sourceCut.Length == 512 * 2048
            && sourceFull.ToArray().AsSpan(0, (int)sourceCut.Length).SequenceEqual(sourceCut.ToArray()),
            "BD runtime capacity truncates the selected source image without rescaling");
        BluRayDiscModule bdModule = new((_, _) => raster);
        string cli = Path.Combine(directory, "bd-cli.iso");
        bdModule.ExecuteAsync(new("bd-generate", ["--input", "unused.png", "--output", cli,
            "--total-sectors", "2048", "--usable-sectors", "1024"])).GetAwaiter().GetResult();
        Require(new FileInfo(cli).Length == 1024 * 2048 && BluRayIsoMetadata.TryLoad(cli) is { TotalSectors: 2048, UsableSectors: 1024 },
            "BD CLI applies the usable limit to file generation");
        foreach (bool outer in new[] { false, true })
        {
            BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(limited, files, filesAtOuterRing: outer);
            using MemoryStream stream = new();
            BluRayIsoGenerator.GenerateToStream(stream, plan, new BluRayConstantByteMapping(0));
            byte[] bytes = stream.ToArray();
            Require(bytes.Length == 1024 * 2048 && plan.TotalSectors == 1024, "BD UDF fits usable sectors");
            Require(UdfFilesystemTests.Inspect(bytes, 1024, "BD_IMAGE", outer ? null : plan.DrawingStartSector, plan.DrawingEndSectorExclusive).Count == 1,
                "BD UDF including tail anchors remains valid");
            string output = Path.Combine(directory, $"bd-{outer}.iso");
            BluRayIsoMetadata metadata = BluRayIsoMetadata.FromPlan(plan, output, output, false, ToneProfile.LegacyBinary, DateTimeOffset.UtcNow);
            metadata.Save(output);
            Require(BluRayIsoMetadata.TryLoad(output) is { TotalSectors: 2048, UsableSectors: 1024, OutputSectors: 1024 },
                "BD sidecar retains geometry and output separately");
        }
    }

    private sealed class SamplingCheck(BluRayGray8Raster raster, double outputRadius, double geometryRadius) : IBluRayLuminanceMapping
    {
        public bool SawChangedScale { get; private set; }
        public byte Map(byte luminance, BluRayPayloadBytePosition position)
        {
            var point = position.TrackPoint;
            Require(luminance == raster.SampleDiscPoint(point.Xmm, point.Ymm, outputRadius), "BD raster uses the usable radius");
            SawChangedScale |= luminance != raster.SampleDiscPoint(point.Xmm, point.Ymm, geometryRadius);
            return 0;
        }
    }

    private static void WriteImage(string path)
    {
        byte[] pixels = new byte[32 * 32 * 4];
        for (int i = 0; i < 32 * 32; i++)
        {
            double x = (i % 32 + .5 - 16) / 16;
            double y = (i / 32 + .5 - 16) / 16;
            byte gray = (byte)(x * x + y * y >= .85 * .85 ? 0 : 255);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = gray;
            pixels[i * 4 + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Usable sector generation: " + message); }
}
