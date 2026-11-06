using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;
using DvdImageSolver.Solver;

internal static class CalibrationEncoderTests
{
    private const int ImageSize = 257;

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-calibration-encoder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string blackPath = Path.Combine(directory, "black.png");
            string quadrantPath = Path.Combine(directory, "quadrant.png");
            WritePng(blackPath, (_, _) => 0);
            WritePng(quadrantPath, QuadrantPixel);
            TestCd(directory, blackPath, quadrantPath);
            TestDvd(directory, blackPath, quadrantPath);
            DvdCalibrationBandEncodingTests.Run();
            CalibrationAngleSweepEncodingTests.Run();
            TestCurrentTargets(directory);
            TestExactReferenceImage(directory);
            CalibrationHighResolutionTargetTests.Run(directory);
            TestCancellation();
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                File.Delete(file);
            }

            Directory.Delete(directory);
        }

        Console.WriteLine("calibration-encoders: passed (exact reference pixels, circle and cross targets, file/stream parity, cancellation)");
    }

    // These baselines describe the stored v6 target_slim.png record, even when
    // Create() starts stamping a different set of engraving rules.
    private static CalibrationTarget LegacyTarget(CalibrationParameters parameters) =>
        new(6, Guid.NewGuid().ToString("N"), parameters, new CalibrationPatternOptions(), []);

    private static void TestExactReferenceImage(string directory)
    {
        byte[] original = CalibrationReferencePattern.ReadPngBytes();
        Require(Convert.ToHexString(SHA256.HashData(original)).Equals(
                "c98ffe87db7cf9c3f132352814e1ce611c44f704de5b7212da4083188257c829", StringComparison.OrdinalIgnoreCase),
            "the embedded PNG is byte-for-byte the user's target_slim.png");
        string originalPath = Path.Combine(directory, "target_slim.png");
        File.WriteAllBytes(originalPath, original);
        using var source = new MemoryStream(original);
        BitmapDecoder decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        Require(frame.PixelWidth == 2400 && frame.PixelHeight == 2400, "the reference retains its original 2400-pixel dimensions");
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        byte[] colors = new byte[2400 * 2400 * 4];
        bitmap.CopyPixels(colors, 2400 * 4, 0);
        byte[] gray = CalibrationReferencePattern.ReadGrayPixels();
        Require(gray.Any(value => value is > 0 and < 255), "the original antialiasing is retained without thresholding");
        for (int index = 0; index < gray.Length; index++)
            Require(gray[index] == colors[index * 4] && gray[index] == colors[index * 4 + 1]
                    && gray[index] == colors[index * 4 + 2], "every grayscale pixel equals the original PNG channels");

        Type renderer = typeof(CalibrationPhotoService).Assembly.GetType("DiscImageStudio.Services.CalibrationPatternRenderer", throwOnError: true)!;
        MethodInfo render = renderer.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo export = renderer.GetMethod("RenderEncodingImage", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (CalibrationDiscKind kind in Enum.GetValues<CalibrationDiscKind>())
        {
            CalibrationParameters parameters = new(kind, 23.9968875, 57.9779875,
                kind == CalibrationDiscKind.Cd ? 8 : kind == CalibrationDiscKind.BluRay ? 64 : 16,
                kind == CalibrationDiscKind.Cd ? 1200 : kind == CalibrationDiscKind.BluRay ? 74.5 : 133.33);
            CalibrationTarget target = LegacyTarget(parameters);
            double centre = kind == CalibrationDiscKind.Cd ? 1200 : 1199.5;
            BitmapSource exported = (BitmapSource)export.Invoke(null, [target, CancellationToken.None])!;
            byte[] actual = new byte[gray.Length];
            exported.CopyPixels(actual, 2400, 0);
            for (int y = 0; y < 2400; y++)
            for (int x = 0; x < 2400; x++)
            {
                // Independently invert the ordinary encoder's pixel-coordinate mapping.
                double scaleCentre = kind == CalibrationDiscKind.BluRay ? 1200 : centre;
                double xm = (x - centre) * parameters.OuterRadiusMm / scaleCentre;
                double ym = (y - centre) * parameters.OuterRadiusMm / scaleCentre;
                double r2 = xm * xm + ym * ym;
                byte expected = r2 >= parameters.InnerRadiusMm * parameters.InnerRadiusMm - 1e-10
                    && r2 <= parameters.OuterRadiusMm * parameters.OuterRadiusMm + 1e-10 ? gray[y * 2400 + x] : (byte)255;
                Require(actual[y * 2400 + x] == expected, "PNG export keeps source pixels and crops in the encoder's coordinate system");
            }
            string exportedPath = Path.Combine(directory, $"reference-export-{kind}.png");
            renderer.GetMethod("SavePng", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [exported, exportedPath]);
            using (FileStream file = File.OpenRead(exportedPath))
            {
                BitmapSource reopened = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                byte[] reopenedPixels = new byte[gray.Length];
                reopened.CopyPixels(reopenedPixels, 2400, 0);
                Require(reopenedPixels.SequenceEqual(actual), "saving the cropped PNG preserves its pixels");
            }
            // A larger physical display canvas must not affect the picture's encoding scale.
            const int previewSize = 600;
            BitmapSource preview = (BitmapSource)render.Invoke(null, [target, 60.0, previewSize, null, 0.0, CancellationToken.None])!;
            byte[] previewPixels = new byte[previewSize * previewSize];
            preview.CopyPixels(previewPixels, previewSize, 0);
            for (int y = 0; y < previewSize; y++)
            for (int x = 0; x < previewSize; x++)
            {
                double xm = (x + 0.5) * 120 / previewSize - 60;
                double ym = (y + 0.5) * 120 / previewSize - 60;
                byte sampled = target.Sample(xm, ym);
                byte expectedPreview = BinaryGrayscale.Quantize(sampled);
                Require(previewPixels[y * previewSize + x] == expectedPreview,
                    "the physical preview uses its medium's encoded levels without an extra canvas scale");
            }
            CalibrationTarget smaller = LegacyTarget(parameters with { OuterRadiusMm = 50 });
            Require(Math.Abs(smaller.RingRadiiMm[4] / target.RingRadiiMm[4] - 50 / parameters.OuterRadiusMm) < 1e-12
                && Math.Abs(smaller.RingThicknessMm / target.RingThicknessMm - 50 / parameters.OuterRadiusMm) < 1e-12,
                "outer radius scales both circle positions and stroke width");
            CalibrationTarget innerOnly = LegacyTarget(parameters with { InnerRadiusMm = 30 });
            Require(innerOnly.RingRadiiMm.SequenceEqual(target.RingRadiiMm), "changing only the inner radius crops without scaling");
            Require(target.Sample(0, -29) == 0 && innerOnly.Sample(0, -29) == 255, "the captured inner boundary removes the cross inside it");
            Require(target.Sample(0, -(parameters.OuterRadiusMm + 0.001)) == 255,
                "the captured outer boundary removes the cross outside it");

            // Exercise all three image-coordinate conventions on the current sampler as well.
            // On BD the usable ring excludes a reserved cluster inside the full geometry.
            var current = CalibrationTarget.Create(kind == CalibrationDiscKind.BluRay
                ? parameters with { DrawingStartSector = 32, DrawingEndSectorExclusive = 64 }
                : parameters);
            var currentImage = (BitmapSource)export.Invoke(null, [current, CancellationToken.None])!;
            byte[] currentPixels = new byte[currentImage.PixelWidth * currentImage.PixelHeight];
            currentImage.CopyPixels(currentPixels, currentImage.PixelWidth, 0);
            Require(currentImage.PixelWidth == 2400 && currentImage.PixelHeight == 2400,
                "new target export has the ordinary encoder's native resolution");
            int inkSamples = 0;
            double scale = parameters.OuterRadiusMm / (kind == CalibrationDiscKind.Dvd ? 1199.5 : 1200.0);
            for (int y = 7; y < currentImage.PixelHeight; y += 19)
            for (int x = 3; x < currentImage.PixelWidth; x += 17)
            {
                double xm = (x - centre) * scale, ym = (y - centre) * scale;
                double radius = Math.Sqrt(xm * xm + ym * ym);
                byte expected = radius < current.DrawingInnerRadiusMm || radius > current.DrawingOuterRadiusMm
                    ? (byte)255 : current.Sample(xm, ym);
                Require(currentPixels[y * currentImage.PixelWidth + x] == expected,
                    "new target PNG uses the current artwork, pixel convention and recorded drawing range");
                if (expected == 0) inkSamples++;
            }
            Require(inkSamples > 100, "the exported recording band contains ink");
        }
        TestReferenceMatchesOrdinaryImageEncoding(originalPath);
    }

    private static void TestReferenceMatchesOrdinaryImageEncoding(string originalPath)
    {
        // These baselines load the PNG through the pre-existing image APIs, not through
        // ReferencePattern or a callback that repeats the implementation under test.
        CalibrationParameters cdParameters = new(CalibrationDiscKind.Cd, 23.9968875, 57.9779875, 32, 1200);
        CalibrationTarget cdTarget = LegacyTarget(cdParameters);
        CdDiscParameters cd = new(cdParameters.InnerRadiusMm, cdParameters.OuterRadiusMm, cdParameters.Sectors,
            cdParameters.LinearDensity, 0, cdParameters.OuterRadiusMm);
        foreach (bool interleave in new[] { false, true })
        {
            using MemoryStream ordinary = new(), calibration = new(), fine = new();
            CdTrackGenerator.GenerateToStream(originalPath, ordinary, cd, interleave, audioByteOrder: CdAudioByteOrder.LittleEndian);
            CdTrackGenerator.GeneratePatternToStream(cdTarget.Sample, calibration, cd, interleave, audioByteOrder: CdAudioByteOrder.LittleEndian);
            var fineTarget = CalibrationTarget.Create(cdParameters, artwork: CalibrationArtwork.ReferenceCross);
            CdTrackGenerator.GeneratePatternToStream(fineTarget, fine, interleave, audioByteOrder: CdAudioByteOrder.LittleEndian);
            Require(ordinary.ToArray().SequenceEqual(calibration.ToArray()),
                "the actual reference CD bytes match the ordinary PNG encoder including its nearest-pixel and centre conventions");
            Require(ordinary.ToArray().SequenceEqual(fine.ToArray()), "the new CD fine target engraves the original PNG");
        }

        const uint sectors = 2_297_888;
        CalibrationParameters dvdParameters = new(CalibrationDiscKind.Dvd, 23.9968875, 57.9779875, sectors, 133.33);
        CalibrationTarget dvdTarget = LegacyTarget(dvdParameters);
        CalibrationTarget fineDvd = CalibrationTarget.Create(dvdParameters, artwork: CalibrationArtwork.ReferenceCross);
        var mapping = new ImageMappingOptions(sectors, dvdParameters.InnerRadiusMm, dvdParameters.OuterRadiusMm,
            dvdParameters.LinearDensity, 0, true, 128, 1);
        var blocks = new HashSet<uint> { 0, sectors - 16 };
        foreach (double radius in dvdTarget.RingRadiiMm.Where(radius => radius > dvdParameters.InnerRadiusMm))
        {
            double fraction = (radius * radius - dvdParameters.InnerRadiusMm * dvdParameters.InnerRadiusMm)
                / (dvdParameters.OuterRadiusMm * dvdParameters.OuterRadiusMm - dvdParameters.InnerRadiusMm * dvdParameters.InnerRadiusMm);
            blocks.Add((uint)(fraction * sectors) / 16 * 16);
        }
        double firstSample = double.PositiveInfinity, lastSample = 0;
        foreach (uint startLba in blocks)
        {
            var options = new MultiBlockSolveOptions(startLba, 16, 0x30000, 1, 4, 1, 64, 0.25, false,
                mapping, Algorithm: SolverAlgorithm.Dispersion, FastParallelism: 1);
            using MemoryStream ordinary = new(), calibration = new();
            FastDispersionImageWriter.WriteStream(originalPath, ordinary, options);
            FastDispersionImageWriter.WritePatternStream((x, y) =>
            {
                double radius = Math.Sqrt(x * x + y * y);
                firstSample = Math.Min(firstSample, radius);
                lastSample = Math.Max(lastSample, radius);
                Require(radius >= dvdParameters.InnerRadiusMm - 1e-10 && radius <= dvdParameters.OuterRadiusMm + 1e-10,
                    "real first, last and intermediate DVD payload sectors only sample the generation annulus");
                return dvdTarget.Sample(x, y);
            }, calibration, options);
            Require(ordinary.ToArray().SequenceEqual(calibration.ToArray()),
                $"the 43533 reference DVD block at LBA {startLba} matches the ordinary PNG encoder byte for byte");
            using var fine = new MemoryStream();
            FastDispersionImageWriter.WritePatternStream(fineDvd.Sample, fine, options);
            Require(ordinary.ToArray().SequenceEqual(fine.ToArray()), $"new DVD fine target engraves the original PNG at LBA {startLba}");
        }
        Require(firstSample - dvdParameters.InnerRadiusMm < 0.00001 && dvdParameters.OuterRadiusMm - lastSample < 0.00001,
            "actual first and last payload sampling approaches both captured annulus boundaries");
        Console.WriteLine($"calibration-reference-mapping: ordinary PNG parity; 43533 payload radii {firstSample:F10}..{lastSample:F10} mm");
    }

    private static void TestCurrentTargets(string directory)
    {
        foreach (CalibrationDiscKind kind in new[] { CalibrationDiscKind.Cd, CalibrationDiscKind.Dvd })
        {
            CalibrationParameters parameters = kind == CalibrationDiscKind.Cd
                ? new(kind, 24.5, 56.8, 8, 1200)
                : new(kind, 24.5, 56.8, 16, 133.3);
            CalibrationTarget target = CalibrationTarget.Create(parameters);
            Require(target.SchemaVersion == 7 && target.VisibleRings.Count == 8,
                "new records identify the combined coarse and fine engraving");
            foreach (double radius in target.RingRadiiMm.Where(radius => radius >= parameters.InnerRadiusMm && radius <= parameters.OuterRadiusMm))
            {
                foreach (double angle in new[] { -2.7, -0.6, 0.8, 2.8 })
                    Require(target.Sample(radius * Math.Cos(angle), radius * Math.Sin(angle)) == 0,
                        "concentric guides have no angular gaps");
            }
            double between = (target.VisibleRings[0].RadiusMm + target.VisibleRings[1].RadiusMm) / 2;
            int dark = Enumerable.Range(0, 360).Count(i =>
            {
                double angle = i * Math.PI / 180;
                return target.Sample(between * Math.Cos(angle), between * Math.Sin(angle)) == 0;
            });
            Require(dark is > 50 and < 250, "a gap between full rings retains a broad light/dark angular reference");
            Require(target.Sample(0, 0) == 255 && target.Sample(target.DrawingOuterRadiusMm + .01, 0) == 255,
                "new target excludes both the hole and the region beyond the recording");

            string json = CalibrationSessionJson.Serialize(new(target, []));
            CalibrationTarget restored = CalibrationSessionJson.Deserialize(json).Target;
            Require(restored.SchemaVersion == 7 && restored.Id == target.Id && restored.VisibleRings.SequenceEqual(target.VisibleRings),
                "saving and reopening retains the complete circle and cross snapshot");
            JsonNode previousVersion = JsonNode.Parse(json)!;
            previousVersion["target"]!["schemaVersion"] = 5;
            bool previousVersionRejected = false;
            try { CalibrationSessionJson.Deserialize(previousVersion.ToJsonString()); }
            catch (ArgumentException error) when (error.Message.Contains("不支持", StringComparison.Ordinal))
            {
                previousVersionRejected = true;
            }
            Require(previousVersionRejected, "the comb schema is explicitly rejected rather than reinterpreted");

            if (kind == CalibrationDiscKind.Cd)
            {
                CdDiscParameters cd = new(parameters.InnerRadiusMm, parameters.OuterRadiusMm, parameters.Sectors,
                    parameters.LinearDensity, StartAngleRadians: 0, ImageOuterRadiusMm: parameters.OuterRadiusMm);
                foreach (bool interleave in new[] { false, true })
                {
                    string path = Path.Combine(directory, $"circle-cross-{interleave}.wav");
                    CdTrackGenerator.GeneratePattern(restored.Sample, path, cd, interleave);
                    using MemoryStream stream = new();
                    using (ForwardOnlyWriteStream output = new(stream))
                    {
                        CdTrackGenerator.GeneratePatternToStream(target.Sample, output, cd, interleave,
                            audioByteOrder: CdAudioByteOrder.LittleEndian);
                    }
                    Require(File.ReadAllBytes(path).AsSpan(44).SequenceEqual(stream.ToArray()),
                        "the saved circle and cross WAV exactly matches its original snapshot's stream");
                }
            }
            else
            {
                DvdStreamingOptions dvd = new(checked((uint)parameters.Sectors), parameters.InnerRadiusMm,
                    parameters.OuterRadiusMm, parameters.LinearDensity, 0, FastParallelism: 1);
                string path = Path.Combine(directory, "circle-cross.iso");
                using (FileStream file = File.Create(path))
                {
                    DvdStreamingGenerator.GeneratePattern(restored.Sample, file, dvd);
                }
                using MemoryStream stream = new();
                using (ForwardOnlyWriteStream output = new(stream))
                {
                    DvdStreamingGenerator.GeneratePattern(target.Sample, output, dvd);
                }
                Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(stream.ToArray()),
                    "the saved circle and cross DVD file exactly matches its original snapshot's stream");
            }
        }
    }

    private static void TestCd(string directory, string blackPath, string quadrantPath)
    {
        CdDiscParameters parameters = new(24.5, 24.8, 8, 1200, Math.PI / 3, 58);
        Func<double, double, byte> quadrant = (x, y) => SampleCdQuadrant(x, y, parameters.ImageOuterRadiusMm);
        // These sub-pixel rings deliberately produce asymmetric audio samples and
        // also exercise detail that an intermediate preview PNG could not retain.
        Func<double, double, byte> fineRings = (x, y) =>
            (long)Math.Floor(Math.Sqrt(x * x + y * y) * 10_000) % 2 == 0 ? (byte)0 : (byte)255;
        foreach (bool interleave in new[] { false, true })
        {
            using MemoryStream image = new();
            using MemoryStream pattern = new();
            CdTrackGenerator.GenerateToStream(blackPath, image, parameters, interleave);
            CdTrackGenerator.GeneratePatternToStream((_, _) => 0, pattern, parameters, interleave);
            Require(image.ToArray().SequenceEqual(pattern.ToArray()), "CD constant matches PNG bytes");

            image.SetLength(0);
            pattern.SetLength(0);
            CdTrackGenerator.GenerateToStream(quadrantPath, image, parameters, interleave);
            CdGenerationSummary summary = CdTrackGenerator.GeneratePatternToStream(
                quadrant, pattern, parameters, interleave);
            Require(image.ToArray().SequenceEqual(pattern.ToArray()), "CD quadrant matches PNG mapping");
            Require(summary.SourceImage == "calibration", "CD summary identifies analytic pattern");
            Require(summary.BytesWritten == parameters.TotalBytes, "CD summary has complete sectors");

            string rawPath = Path.Combine(directory, $"pattern-{interleave}.raw");
            string wavePath = Path.Combine(directory, $"pattern-{interleave}.wav");
            pattern.SetLength(0);
            CdTrackGenerator.GeneratePatternToStream(fineRings, pattern, parameters, interleave);
            CdTrackGenerator.GeneratePattern(fineRings, rawPath, parameters, interleave);
            CdGenerationSummary wave = CdTrackGenerator.GeneratePattern(fineRings, wavePath, parameters, interleave);
            Require(File.ReadAllBytes(rawPath).SequenceEqual(pattern.ToArray()), "CD raw file matches stream");
            Require(wave.CueSheetPath is not null && File.Exists(wave.CueSheetPath), "CD pattern WAV has CUE");
            using MemoryStream pcm = new();
            using (ForwardOnlyWriteStream output = new(pcm))
            {
                CdTrackGenerator.GeneratePatternToStream(
                    fineRings, output, parameters, interleave, audioByteOrder: CdAudioByteOrder.LittleEndian);
            }

            Require(File.ReadAllBytes(wavePath).AsSpan(44).SequenceEqual(pcm.ToArray()), "CD little-endian stream matches WAV data");
            byte[] raw = pattern.ToArray();
            byte[] littleEndian = pcm.ToArray();
            Require(raw.Where((value, index) => value != littleEndian[index]).Any(), "CD test distinguishes byte orders");
            for (int index = 0; index < raw.Length; index += 2)
            {
                Require(raw[index] == littleEndian[index + 1] && raw[index + 1] == littleEndian[index], "CD audio sample byte order");
            }
        }

        bool first = true;
        using MemoryStream orientation = new();
        CdTrackGenerator.GeneratePatternToStream((x, y) =>
        {
            if (first)
            {
                Require(Math.Abs(x) < 1e-9 && y > 24, "CD positive start angle points down at 90 degrees");
                first = false;
            }

            return 255;
        }, orientation, parameters with { StartAngleRadians = Math.PI / 2 }, false);
    }

    private static void TestDvd(string directory, string blackPath, string quadrantPath)
    {
        DvdStreamingOptions options = new(16, 24.5, 24.8, 133.3, 60, FastParallelism: 1);
        using MemoryStream image = new();
        using MemoryStream pattern = new();
        DvdStreamingGenerator.Generate(blackPath, image, options);
        DvdStreamingGenerator.GeneratePattern((_, _) => 0, pattern, options);
        Require(image.ToArray().SequenceEqual(pattern.ToArray()), "DVD constant matches PNG bytes");

        Func<double, double, byte> quadrant = (x, y) => SampleDvdQuadrant(x, y, options.OuterRadiusMm);
        image.SetLength(0);
        pattern.SetLength(0);
        DvdStreamingGenerator.Generate(quadrantPath, image, options);
        DvdStreamingSummary summary;
        using (ForwardOnlyWriteStream output = new(pattern))
        {
            summary = DvdStreamingGenerator.GeneratePattern(quadrant, output, options);
        }

        Require(image.ToArray().SequenceEqual(pattern.ToArray()), "DVD quadrant matches PNG mapping and start-angle sign");
        Require(summary.SourceImage == "calibration", "DVD summary identifies analytic pattern");
        Require(summary.BytesWritten == options.ContentLength && pattern.Length == 16 * 2048, "DVD emits 16 complete sectors");
        Require(summary.ControlledWords == 16 * 2048, "DVD pattern controls all payload words");
        string outputPath = Path.Combine(directory, "pattern.iso");
        using (FileStream file = File.Create(outputPath))
        {
            DvdStreamingGenerator.GeneratePattern(quadrant, file, options);
        }

        Require(File.ReadAllBytes(outputPath).SequenceEqual(pattern.ToArray()), "DVD file matches forward-only stream");

        bool first = true;
        using MemoryStream orientation = new();
        DvdStreamingGenerator.GeneratePattern((x, y) =>
        {
            if (first)
            {
                Require(y < -24 && Math.Abs(x) < 1, "DVD positive start angle points up at 90 degrees");
                first = false;
            }

            return 255;
        }, orientation, options with { StartAngleDegrees = 90 });
    }

    private static void TestCancellation()
    {
        CdDiscParameters cd = new(24.5, 24.8, 8, 1200);
        DvdStreamingOptions dvd = new(16, 24.5, 24.8, 133.3, 0, FastParallelism: 1);
        using CancellationTokenSource preCancelled = new();
        preCancelled.Cancel();
        using MemoryStream cdOutput = new();
        using MemoryStream dvdOutput = new();
        ThrowsCancelled(() => CdTrackGenerator.GeneratePatternToStream((_, _) => 0, cdOutput, cd, false,
            cancellationToken: preCancelled.Token));
        ThrowsCancelled(() => DvdStreamingGenerator.GeneratePattern((_, _) => 0, dvdOutput, dvd,
            cancellationToken: preCancelled.Token));
        Require(cdOutput.Length == 0 && dvdOutput.Length == 0, "pre-cancelled generation writes nothing");

        using CancellationTokenSource duringCd = new();
        ThrowsCancelled(() => CdTrackGenerator.GeneratePatternToStream((_, _) =>
        {
            duringCd.Cancel();
            return 0;
        }, cdOutput, cd, true, cancellationToken: duringCd.Token));
        Require(cdOutput.Length < cd.TotalBytes, "CD cancellation stops before completing the track");
        using CancellationTokenSource duringDvd = new();
        ThrowsCancelled(() => DvdStreamingGenerator.GeneratePattern((_, _) =>
        {
            duringDvd.Cancel();
            return 0;
        }, dvdOutput, dvd, cancellationToken: duringDvd.Token));
        Require(dvdOutput.Length == 0, "cancelled DVD block is not emitted");
    }

    private static byte SampleCdQuadrant(double x, double y, double outerRadiusMm)
    {
        double centre = ImageSize / 2.0;
        int pixelX = (int)Math.Round(centre + x * centre / outerRadiusMm);
        int pixelY = (int)Math.Round(centre + y * centre / outerRadiusMm);
        return pixelX < 0 || pixelY < 0 || pixelX >= ImageSize || pixelY >= ImageSize
            ? (byte)255
            : QuadrantPixel(pixelX, pixelY);
    }

    private static byte SampleDvdQuadrant(double x, double y, double outerRadiusMm)
    {
        double centre = (ImageSize - 1) / 2.0;
        double pixelX = centre + x * centre / outerRadiusMm;
        double pixelY = centre + y * centre / outerRadiusMm;
        // This opaque black/white fixture is a subset of the DVD palette. Its
        // pixels must survive sampling without synthesizing intermediate grays.
        int nearestX = Math.Clamp((int)Math.Floor(pixelX + 0.5), 0, ImageSize - 1);
        int nearestY = Math.Clamp((int)Math.Floor(pixelY + 0.5), 0, ImageSize - 1);
        return QuadrantPixel(nearestX, nearestY);
    }

    private static byte QuadrantPixel(int x, int y)
        => x >= ImageSize / 2 && y >= ImageSize / 2 ? (byte)0 : (byte)255;

    private static void WritePng(string path, Func<int, int, byte> pixel)
    {
        int stride = ImageSize * 4;
        byte[] pixels = new byte[stride * ImageSize];
        for (int y = 0; y < ImageSize; y++)
        {
            for (int x = 0; x < ImageSize; x++)
            {
                int offset = y * stride + x * 4;
                byte value = pixel(x, y);
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(ImageSize, ImageSize, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static void ThrowsCancelled(Action action)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("Calibration encoder did not honor cancellation.");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Calibration encoder: {name}.");
        }
    }
}
