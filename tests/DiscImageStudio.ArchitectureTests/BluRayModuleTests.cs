using System.IO;
using System.Text.Json;
using DiscImageStudio.BluRay;
using DiscImageStudio.BluRay.Encoding;
using DiscImageStudio.Core;

internal static class BluRayModuleTests
{
    private const string RitekPresetId = "bd-r-ritek-10x";
    private const double RitekInnerRadiusMm = 23.98822105616562;
    private const double RitekOuterRadiusMm = 58.023234731100764;
    // Preserved from the saved user preset "Verbatim 64099"
    // (user-bd-20261028-123134-08af), without re-fitting its calibration record.
    private const string VerbatimPresetId = "bd-r-verbatim-64099";
    private const double VerbatimInnerRadiusMm = 23.987042157339335;
    private const double VerbatimOuterRadiusMm = 57.987960341783236;

    internal static void Run()
    {
        TestParametersAndGeometry();
        TestCalibratedPresetCommands();
        TestStreaming();
        TestAtomicFilesAndCommands();
        TestPictureCommands();
        Console.WriteLine("blu-ray-module: passed");
    }

    private static void TestParametersAndGeometry()
    {
        BluRayDiscParameters disc = new();
        disc.Validate();
        Equal(3, BluRayDiscPreset.BuiltIn.Count, "generic and calibrated single-layer presets");
        True(BluRayDiscPreset.BuiltIn.All(preset => preset.LayerCount == 1), "all builtins retain single-layer support");
        Equal(new BluRayDiscParameters(), BluRayDiscPreset.BuiltIn[0].CreateParameters(), "generic geometry remains the first builtin");
        BluRayDiscPreset ritek = BluRayDiscPreset.Find(RitekPresetId);
        Equal("铼德 BD-R 10x", ritek.DisplayName, "Ritek calibrated builtin name");
        Equal(new BluRayDiscParameters(12_219_392, 1, RitekInnerRadiusMm, RitekOuterRadiusMm, 0),
            ritek.CreateParameters(), "Ritek geometry preserves the source JSON values exactly");
        True(!ritek.InvertMapping && !ritek.IsCustom && !ritek.IsUserPreset, "Ritek is a normal-polarity builtin");
        BluRayDiscPreset verbatim = BluRayDiscPreset.Find(VerbatimPresetId);
        Equal("Verbatim BD-R (64099)", verbatim.DisplayName, "Verbatim calibrated builtin name");
        Equal(new BluRayDiscParameters(12_219_392, 1, VerbatimInnerRadiusMm, VerbatimOuterRadiusMm, 0),
            verbatim.CreateParameters(), "Verbatim geometry preserves the saved user parameters exactly");
        True(!verbatim.InvertMapping && !verbatim.IsCustom && !verbatim.IsUserPreset,
            "Verbatim is a normal-polarity builtin");
        Equal(25_025_314_816L, disc.ContentLength, "nominal BD-R byte capacity");
        Equal(0x100000U, disc.GetPhysicalSectorNumber(0), "first physical sector");
        Equal(0xCA73FFU, disc.GetPhysicalSectorNumber(disc.TotalSectors - 1), "last physical sector");
        Throws<ArgumentOutOfRangeException>(() => disc.GetPhysicalSectorNumber(disc.TotalSectors));
        Throws<ArgumentOutOfRangeException>(() => (disc with { LayerCount = 2 }).Validate());
        Throws<ArgumentException>(() => BluRayDiscPreset.Find("bd-r-dl-50gb"));
        (disc with { TotalSectors = 32 }).Validate();
        Throws<ArgumentOutOfRangeException>(() => (disc with { TotalSectors = 0 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (disc with { TotalSectors = 63 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (disc with { TotalSectors = BluRayDiscParameters.SectorsPerLayer + 32 }).Validate());
        foreach (uint total in new[] { 32U, 64U, 128U, 12_000_000U })
        {
            BluRayTrackGeometry custom = new(disc with { TotalSectors = total });
            Near(58, custom.AtSector(total).RadiusMm, 1e-10, "custom capacity reaches outer radius");
            Near(geometryLength(total), custom.TrackLengthPerLayerMm, 1e-9, "custom track length uses total sectors");
            double growth = custom.EffectiveTrackPitchMicrometers / (2000 * Math.PI);
            double primitive(double r) => (r * Math.Sqrt(r * r + growth * growth)
                + growth * growth * Math.Asinh(r / growth)) / (2 * growth);
            BluRayTrackPoint middle = custom.AtSector(total / 2.0);
            Near(custom.TrackLengthPerLayerMm / 2, primitive(middle.RadiusMm) - primitive(24),
                Math.Max(1e-7, custom.TrackLengthPerLayerMm * 1e-12), "custom midpoint preserves exact arc length");
        }
        static double geometryLength(uint sectors) => sectors / 32.0
            * BluRayDiscParameters.ChannelBitsPerRecordingUnit * BluRayDiscParameters.ChannelBitLengthNm * 1e-6;
        Throws<ArgumentOutOfRangeException>(() => (disc with { InnerRadiusMm = double.NaN }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (disc with { OuterRadiusMm = 61 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (disc with { InnerRadiusMm = 58 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => new BluRayStreamingOptions(disc, uint.MaxValue - 31, 64).Validate());
        Throws<ArgumentOutOfRangeException>(() => new BluRayStreamingOptions(disc, 1, 32).Validate());
        Throws<ArgumentOutOfRangeException>(() => new BluRayStreamingOptions(disc, 0, 33).Validate());
        Throws<ArgumentOutOfRangeException>(() => new BluRayStreamingOptions(disc, disc.TotalSectors - 32, 64).Validate());
        new BluRayStreamingOptions(disc, disc.TotalSectors - 32, 32).Validate();

        BluRayTrackGeometry geometry = new(disc);
        Near(24, geometry.AtSector(0).RadiusMm, 1e-12, "inner boundary");
        Near(58, geometry.AtSector(disc.TotalSectors).RadiusMm, 1e-12, "outer boundary");
        True(geometry.AtSector(32).RadiusMm < 24.001, "short range retains full-disc geometry");
        True(geometry.AtSector(1).AngleRadians < geometry.AtSector(0).AngleRadians,
            "outward track proceeds clockwise on the data-side image");
        True(geometry.EffectiveTrackPitchMicrometers is > 0.30 and < 0.34, "derived pitch includes recording unit overhead");
        True(geometry.AtSector(disc.TotalSectors / 2).RadiusMm > 40, "arc length grows with annular area");
        double lastRadius = 0;
        for (int i = 0; i <= 100; i++)
        {
            BluRayTrackPoint point = geometry.AtLayerFraction(0, i / 100.0);
            True(point.RadiusMm > lastRadius, "radius monotonicity");
            lastRadius = point.RadiusMm;
            // Independent monotonic bisection reference for the optimized inverse.
            double growth = geometry.EffectiveTrackPitchMicrometers / (2000 * Math.PI);
            double lo = disc.InnerRadiusMm, hi = disc.OuterRadiusMm;
            double primitive(double r) => (r * Math.Sqrt(r * r + growth * growth)
                + growth * growth * Math.Log((r + Math.Sqrt(r * r + growth * growth)) / growth)) / (2 * growth);
            double targetArc = geometry.TrackLengthPerLayerMm * i / 100;
            double initialArc = primitive(lo);
            for (int step = 0; step < 70; step++)
            {
                double middle = (lo + hi) / 2;
                if (primitive(middle) - initialArc < targetArc) lo = middle; else hi = middle;
            }
            Near((lo + hi) / 2, point.RadiusMm, 1e-10, "exact inverse matches bisection reference");
        }
        Near(Math.PI / 2, new BluRayTrackGeometry(disc with { StartAngleDegrees = 90 }).AtSector(0).AngleRadians,
            1e-12, "start angle");
        Throws<ArgumentOutOfRangeException>(() => geometry.AtSector(double.NaN));
        Throws<ArgumentOutOfRangeException>(() => geometry.AtLayerFraction(1, 0));
    }

    private static void TestCalibratedPresetCommands()
    {
        BluRayDiscModule module = new();
        TestVerbatimPresetCommands(module);
        foreach (DiscCommandDescriptor command in module.Descriptor.Commands.Where(command => command.Name is "bd-info" or "bd-probe" or "bd-generate"))
            True(command.Options.Single(option => option.Name == "preset").Choices!.Contains(RitekPresetId),
                command.Name + " advertises the calibrated builtin to CLI hosts");
        using (JsonDocument info = JsonDocument.Parse(Run(module, "bd-info", "--preset", RitekPresetId).Summary!))
        {
            JsonElement parameters = info.RootElement.GetProperty("parameters");
            Equal(RitekPresetId, info.RootElement.GetProperty("preset").GetString()!, "CLI reports the selected calibrated builtin");
            Equal(RitekInnerRadiusMm, parameters.GetProperty("InnerRadiusMm").GetDouble(), "CLI default inner radius follows selected preset");
            Equal(RitekOuterRadiusMm, parameters.GetProperty("OuterRadiusMm").GetDouble(), "CLI default outer radius follows selected preset");
            Equal(12_219_392U, parameters.GetProperty("TotalSectors").GetUInt32(), "CLI calibrated full capacity");
            Equal(0d, parameters.GetProperty("StartAngleDegrees").GetDouble(), "CLI calibrated zero angle");
            True(!info.RootElement.GetProperty("luminanceMapping").GetProperty("invert").GetBoolean(), "CLI calibrated normal polarity");
        }
        using (JsonDocument info = JsonDocument.Parse(Run(module, "bd-info", "--preset", RitekPresetId,
            "--r0", "25.123456789", "--total-sectors", "1024", "--invert", "true").Summary!))
        {
            JsonElement parameters = info.RootElement.GetProperty("parameters");
            Equal(25.123456789, parameters.GetProperty("InnerRadiusMm").GetDouble(), "explicit inner radius overrides the calibrated preset");
            Equal(RitekOuterRadiusMm, parameters.GetProperty("OuterRadiusMm").GetDouble(), "omitted outer radius keeps the calibrated preset default");
            Equal(1024U, parameters.GetProperty("TotalSectors").GetUInt32(), "explicit capacity overrides the calibrated preset");
            True(info.RootElement.GetProperty("luminanceMapping").GetProperty("invert").GetBoolean(), "explicit polarity overrides the calibrated preset");
        }
        using (JsonDocument info = JsonDocument.Parse(Run(module, "bd-info").Summary!))
        {
            JsonElement parameters = info.RootElement.GetProperty("parameters");
            Equal(24d, parameters.GetProperty("InnerRadiusMm").GetDouble(), "unselected CLI keeps its generic inner default");
            Equal(58d, parameters.GetProperty("OuterRadiusMm").GetDouble(), "unselected CLI keeps its generic outer default");
        }
    }

    private static void TestVerbatimPresetCommands(BluRayDiscModule module)
    {
        foreach (DiscCommandDescriptor command in module.Descriptor.Commands.Where(command => command.Name is "bd-info" or "bd-probe" or "bd-generate"))
            True(command.Options.Single(option => option.Name == "preset").Choices!.Contains(VerbatimPresetId),
                command.Name + " advertises the Verbatim 64099 preset to CLI hosts");
        using (JsonDocument info = JsonDocument.Parse(Run(module, "bd-info", "--preset", VerbatimPresetId).Summary!))
        {
            JsonElement parameters = info.RootElement.GetProperty("parameters");
            Equal(VerbatimPresetId, info.RootElement.GetProperty("preset").GetString()!, "CLI reports the Verbatim preset");
            Equal(VerbatimInnerRadiusMm, parameters.GetProperty("InnerRadiusMm").GetDouble(), "CLI Verbatim saved inner radius");
            Equal(VerbatimOuterRadiusMm, parameters.GetProperty("OuterRadiusMm").GetDouble(), "CLI Verbatim saved outer radius");
            Equal(12_219_392U, parameters.GetProperty("TotalSectors").GetUInt32(), "CLI Verbatim full capacity");
            Equal(1, parameters.GetProperty("LayerCount").GetInt32(), "CLI Verbatim single layer");
            Equal(0d, parameters.GetProperty("StartAngleDegrees").GetDouble(), "CLI Verbatim zero start angle");
            True(!info.RootElement.GetProperty("luminanceMapping").GetProperty("invert").GetBoolean(),
                "CLI Verbatim preserves recorded polarity");
        }
        using (JsonDocument info = JsonDocument.Parse(Run(module, "bd-info", "--preset", VerbatimPresetId,
            "--r0", "25.123456789", "--total-sectors", "1024", "--invert", "true").Summary!))
        {
            JsonElement parameters = info.RootElement.GetProperty("parameters");
            Equal(25.123456789, parameters.GetProperty("InnerRadiusMm").GetDouble(), "explicit inner radius overrides Verbatim");
            Equal(VerbatimOuterRadiusMm, parameters.GetProperty("OuterRadiusMm").GetDouble(), "omitted outer radius keeps Verbatim default");
            Equal(1024U, parameters.GetProperty("TotalSectors").GetUInt32(), "explicit capacity overrides Verbatim");
            True(info.RootElement.GetProperty("luminanceMapping").GetProperty("invert").GetBoolean(),
                "explicit polarity overrides Verbatim");
        }
        using (JsonDocument info = JsonDocument.Parse(Run(module, "bd-info", "--preset", VerbatimPresetId,
            "--r1", "57.125", "--start-angle", "37.5").Summary!))
        {
            JsonElement parameters = info.RootElement.GetProperty("parameters");
            Equal(VerbatimInnerRadiusMm, parameters.GetProperty("InnerRadiusMm").GetDouble(), "omitted inner radius keeps Verbatim default");
            Equal(57.125, parameters.GetProperty("OuterRadiusMm").GetDouble(), "explicit outer radius overrides Verbatim");
            Equal(37.5, parameters.GetProperty("StartAngleDegrees").GetDouble(), "explicit start angle overrides Verbatim");
        }
    }

    private static void TestStreaming()
    {
        BluRayDiscParameters disc = new();
        BluRayStreamingOptions options = new(disc, 0, 64);
        using MemoryStream output = new();
        Throws<InvalidOperationException>(() => BluRayStreamingGenerator.GenerateToStream(output, options, null));
        Equal(0L, output.Length, "missing mapping writes nothing");
        List<BluRayStreamingProgress> updates = [];
        BluRayStreamingSummary summary = BluRayStreamingGenerator.GenerateToStream(output, options,
            new BluRayConstantByteMapping(0xA6), new InlineProgress<BluRayStreamingProgress>(updates.Add));
        Equal(64L * 2048, summary.BytesWritten, "exact range length");
        byte[] payload = output.ToArray();
        for (int sector = 0; sector < 64; sector++)
        {
            byte[] frame = new byte[2052];
            BluRayEdc.WriteDataFrame(payload.AsSpan(sector * 2048, 2048), frame);
            // Independent bit-register implementation of §13.4, not the generator's helper.
            uint state = 0x8000U | ((disc.GetPhysicalSectorNumber((uint)sector) >> 5) & 0x7FFF);
            for (int i = 0; i < 2048; i++)
            {
                Equal((byte)0xA6, (byte)(frame[i] ^ (byte)state), "forward scrambled payload is explicit probe value");
                for (int bit = 0; bit < 8; bit++)
                {
                    uint feedback = ((state >> 15) ^ (state >> 14) ^ (state >> 12) ^ (state >> 3)) & 1;
                    state = ((state << 1) | feedback) & 0xFFFF;
                }
            }
        }
        True(payload.AsSpan(0, 2048).SequenceEqual(payload.AsSpan(2048, 2048)), "seed resets each sector");
        True(!payload.AsSpan(0, 2048).SequenceEqual(payload.AsSpan(32 * 2048, 2048)), "next cluster uses next seed");
        Equal(0L, updates[0].CompletedBytes, "initial progress");
        Equal(summary.BytesWritten, updates[^1].CompletedBytes, "final progress");

        using ForwardOnlyStream sequential = new();
        BluRayStreamingGenerator.GenerateToStream(sequential, new(disc, 0, 32), new BluRayConstantByteMapping(3));
        Equal(65536L, sequential.BytesWritten, "no seek is required");
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        using MemoryStream untouched = new();
        Throws<OperationCanceledException>(() => BluRayStreamingGenerator.GenerateToStream(untouched,
            options, new BluRayConstantByteMapping(0), cancellationToken: cancel.Token));
        Equal(0L, untouched.Length, "pre-cancel does not write");
    }

    private static void TestAtomicFilesAndCommands()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "probe.bin");
            byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(file, original);
            BluRayStreamingOptions options = new(new(), 0, 64);
            using CancellationTokenSource cancel = new();
            Throws<OperationCanceledException>(() => BluRayStreamingGenerator.GenerateFile(file, options,
                new BluRayConstantByteMapping(42), new InlineProgress<BluRayStreamingProgress>(p =>
                {
                    if (p.CompletedBytes > 0) cancel.Cancel();
                }), cancel.Token));
            True(File.ReadAllBytes(file).SequenceEqual(original), "cancel preserves old final output");
            Throws<IOException>(() => BluRayStreamingGenerator.GenerateFile(file, options, new ThrowingMapping()));
            True(File.ReadAllBytes(file).SequenceEqual(original), "mapper failure preserves old final output");
            True(Directory.GetFiles(directory).Length == 1, "partial files cleaned");

            BluRayDiscModule module = new();
            DiscJobResult blocked = Run(module, "bd-generate", "--input", "source.png", "--output", file, "--total-sectors", "1024");
            Equal(2, blocked.ExitCode, "host image loader required");
            True(blocked.Summary!.Contains("image loader", StringComparison.Ordinal), "missing loader is actionable");
            True(File.ReadAllBytes(file).SequenceEqual(original), "missing loader preserves file");
            Throws<ArgumentException>(() => Run(module, "bd-generate", "--output", file));
            Throws<ArgumentException>(() => Run(module, "bd-generate", "--input", file, "--output", file));
            Throws<ArgumentException>(() => Run(module, "bd-generate", "--input", "source.png", "--output", file,
                "--sectors", "31"));
            Throws<ArgumentException>(() => Run(module, "bd-probe", "--output", file, "--byte", "256"));
            Throws<ArgumentException>(() => Run(module, "bd-probe", "--output", file, "--byte", "0", "--bogus", "1"));
            Throws<ArgumentException>(() => Run(module, "bd-info", "--preset", "bd-r-dl-50gb"));
            Throws<ArgumentException>(() => Run(module, "bd-info", "--r0", "24", "--r0", "25"));
            True(File.ReadAllBytes(file).SequenceEqual(original), "validation preserves file");

            string info = Path.Combine(directory, "parameters.json");
            True(Run(module, "bd-info", "--output", info).Succeeded, "info command succeeds");
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(info)))
            {
                Equal("enabled", document.RootElement.GetProperty("mappingStatus").GetString()!, "mapping metadata");
                JsonElement mapping = document.RootElement.GetProperty("luminanceMapping");
                Equal("3T/6T run-count bias", mapping.GetProperty("strategy").GetString()!, "run-count mapping metadata");
                Equal(128, mapping.GetProperty("threshold").GetInt32(), "mapping threshold metadata");
                Equal($"0x{BluRayRunLengthMapping.ThreeTByte:X2}",
                    mapping.GetProperty("dark").GetProperty("scrambledByte").GetString()!, "dark byte metadata");
                Equal($"0x{BluRayRunLengthMapping.SixTByte:X2}",
                    mapping.GetProperty("light").GetProperty("scrambledByte").GetString()!, "light byte metadata");
                Equal(1, document.RootElement.GetProperty("parameters").GetProperty("LayerCount").GetInt32(), "single-layer metadata");
            }
            using (JsonDocument custom = JsonDocument.Parse(Run(module, "bd-info", "--total-sectors", "128", "--invert", "true").Summary!))
            {
                Equal(128U, custom.RootElement.GetProperty("parameters").GetProperty("TotalSectors").GetUInt32(), "custom capacity metadata");
                JsonElement mapping = custom.RootElement.GetProperty("luminanceMapping");
                True(mapping.GetProperty("invert").GetBoolean(), "selected polarity metadata");
                Equal("6T", mapping.GetProperty("dark").GetProperty("run").GetString()!, "inverted dark mapping metadata");
                Equal("0x44", mapping.GetProperty("dark").GetProperty("scrambledByte").GetString()!, "inverted dark byte metadata");
            }
            True(Run(module, "bd-probe", "--output", file, "--byte", "0xA6").Succeeded, "probe command succeeds");
            Equal(65536L, new FileInfo(file).Length, "probe cluster bytes");
            string encoded = Path.Combine(directory, "ecc.bin");
            True(Run(module, "bd-encode", "--input", file, "--output", encoded).Succeeded, "ECC command succeeds");
            Equal(76880L, new FileInfo(encoded).Length, "ECC cluster bytes");
            True(File.ReadAllBytes(encoded).SequenceEqual(BluRayEccEncoder.Encode(File.ReadAllBytes(file), 0x100000)),
                "module and direct encoder agree");
            Throws<ArgumentException>(() => Run(module, "bd-encode", "--input", file, "--output", file));
            Throws<ArgumentException>(() => Run(module, "bd-encode", "--input", file, "--output", encoded, "--psn", "0x1358C00"));
            File.WriteAllBytes(file, original);
            Throws<ArgumentException>(() => Run(module, "bd-encode", "--input", file, "--output", encoded));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void TestPictureCommands()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-picture-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.png");
            string output = Path.Combine(directory, "picture.iso");
            byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(source, original);
            File.WriteAllBytes(output, original);
            int loads = 0;
            byte luminance = 0;
            BluRayDiscModule module = new((path, token) =>
            {
                Equal(source, path, "image loader receives input path");
                token.ThrowIfCancellationRequested();
                loads++;
                return new(1, 1, [luminance]);
            });
            string[] Args(params string[] extra) => ["--input", source, "--output", output, .. extra];
            DiscCommandDescriptor command = module.Descriptor.Commands.Single(c => c.Name == "bd-generate");
            True(command.Capabilities.HasFlag(DiscModuleCapabilities.DataImageGeneration), "picture capability enabled");
            foreach (string[] invalid in new[]
            {
                Args("--start-sector", "1", "--sectors", "32"),
                Args("--sectors", "0"), Args("--sectors", "33"), Args("--sectors", "-32"),
                Args("--start-sector", uint.MaxValue.ToString()), Args("--invert", "yes"),
                Args("--r0", "58"), Args("--r1", "NaN"), Args("--unknown", "1"),
                Args("--sectors", "32", "--sectors", "64"),
                new[] { "--input", source, "--output", source, "--sectors", "32" },
                new[] { "--input", source, "--output", output, "--invert" },
            })
                Throws<ArgumentException>(() => Run(module, "bd-generate", invalid));
            Equal(0, loads, "invalid picture requests never load image");
            True(File.ReadAllBytes(source).SequenceEqual(original), "validation preserves source");
            True(File.ReadAllBytes(output).SequenceEqual(original), "validation preserves output");

            DiscModuleCatalog catalog = new([module]);
            DiscJobResult result = catalog.ExecuteAsync(new("bd-generate", Args("--total-sectors", "1024")))
                .GetAwaiter().GetResult();
            True(result.Succeeded, "catalog routes picture generation");
            Equal(Path.GetFullPath(output), result.OutputPath!, "picture output path");
            Equal(1, loads, "image loads once");
            AssertUniformScrambledOutput(output, BluRayRunLengthMapping.ThreeTByte);

            True(Run(module, "bd-generate", Args("--total-sectors", "1024", "--invert", "true")).Succeeded,
                "inverted picture succeeds");
            AssertUniformScrambledOutput(output, BluRayRunLengthMapping.SixTByte);
            luminance = 255;
            True(Run(module, "bd-generate", Args("--total-sectors", "1024")).Succeeded, "light picture succeeds");
            AssertUniformScrambledOutput(output, BluRayRunLengthMapping.SixTByte);
            True(Run(module, "bd-generate", Args("--total-sectors", "1024", "--invert", "true")).Succeeded,
                "inverted light picture succeeds");
            AssertUniformScrambledOutput(output, BluRayRunLengthMapping.ThreeTByte);

            True(module.ExecuteAsync(new("bd-generate",
                ["--input", source, "--total-sectors", "1024"], output)).GetAwaiter().GetResult().Succeeded,
                "custom-capacity full ISO and requested output path");
            AssertUniformScrambledOutput(output, BluRayRunLengthMapping.SixTByte);
            string innerDirectory = Path.Combine(directory, "udf-files");
            Directory.CreateDirectory(innerDirectory);
            True(Run(module, "bd-generate", Args("--total-sectors", "1024", "--data-dir", innerDirectory)).Succeeded,
                "explicitly selected folder retains a UDF volume");
            var udfPlan = BluRayIsoGenerator.PrepareDrawing(new(TotalSectors: 1024), innerDirectory);
            UdfFilesystemTests.Inspect(File.ReadAllBytes(output), 1024, "BD_IMAGE",
                udfPlan.DrawingStartSector, udfPlan.DrawingEndSectorExclusive);

            File.WriteAllBytes(output, original);
            using CancellationTokenSource cancel = new();
            Throws<OperationCanceledException>(() => module.ExecuteAsync(new("bd-generate", Args("--total-sectors", "1024")),
                new InlineProgress<DiscJobProgress>(p =>
                {
                    if (p.CompletedUnits > 0) cancel.Cancel();
                }), cancel.Token).GetAwaiter().GetResult());
            True(File.ReadAllBytes(output).SequenceEqual(original), "picture cancellation preserves old output");
            int beforeCancelledLoad = loads;
            Throws<OperationCanceledException>(() => module.ExecuteAsync(new("bd-generate", Args("--total-sectors", "1024")),
                cancellationToken: cancel.Token).GetAwaiter().GetResult());
            Equal(beforeCancelledLoad, loads, "pre-cancel does not load image");

            BluRayDiscModule failedLoader = new((_, _) => throw new IOException("test image decoding failure"));
            Throws<IOException>(() => Run(failedLoader, "bd-generate", Args("--total-sectors", "1024")));
            True(File.ReadAllBytes(output).SequenceEqual(original), "image loading failure preserves output");
            True(File.ReadAllBytes(source).SequenceEqual(original), "generation preserves source");
            // Source, generated ISO and its recovery sidecar. A failed or cancelled run
            // must not add further partial artifacts.
            Equal(3, Directory.GetFiles(directory).Length, "picture partial files cleaned");
            True(File.Exists(BluRayIsoMetadata.PathFor(output)), "picture sidecar survives the run");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertUniformScrambledOutput(string path, byte expected)
    {
        byte[] payload = File.ReadAllBytes(path);
        Equal(1024 * 2048, payload.Length, "pure picture image covers every user-data sector");
        BluRayDiscParameters disc = new();
        foreach (uint startSector in new uint[] { 0, 1024 - 32 })
        for (uint sector = 0; sector < 32; sector++)
        {
            // Independently apply the scrambler register to inspect the generated payload domain.
            uint state = 0x8000U | ((disc.GetPhysicalSectorNumber(startSector + sector) >> 5) & 0x7FFF);
            for (int index = 0; index < 2048; index++)
            {
                Equal(expected, (byte)(payload[(startSector + sector) * 2048 + index] ^ (byte)state), "picture scrambled byte");
                for (int bit = 0; bit < 8; bit++)
                {
                    uint feedback = ((state >> 15) ^ (state >> 14) ^ (state >> 12) ^ (state >> 3)) & 1;
                    state = ((state << 1) | feedback) & 0xFFFF;
                }
            }
        }
    }

    private static DiscJobResult Run(BluRayDiscModule module, string command, params string[] args)
        => module.ExecuteAsync(new(command, args)).GetAwaiter().GetResult();
    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: {expected} != {actual}"); }
    private static void True(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance, string message)
        => True(Math.Abs(expected - actual) <= tolerance, message);
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    { public void Report(T value) => callback(value); }
    private sealed class ThrowingMapping : IBluRayPayloadMapping
    { public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination) => throw new IOException("test mapper failure"); }
    private sealed class ForwardOnlyStream : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
    }
}
