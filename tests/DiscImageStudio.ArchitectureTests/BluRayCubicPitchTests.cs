using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;

internal static class BluRayCubicPitchTests
{
    internal static void Benchmark()
    {
        // Run separately from the regression suite. Reuse each immutable geometry
        // and raster; include streaming, scrambling and normal parallel batching.
        foreach (int size in new[] { 4096, 8192 })
        {
            var raster = Raster(size, size);
            foreach (bool variable in new[] { false, true })
            {
                var disc = new BluRayDiscParameters(StartAngleDegrees: 37.25,
                    PitchLinear: variable ? .24 : 0, PitchQuadratic: variable ? -.18 : 0, PitchCubic: variable ? .08 : 0);
                var mapping = new BluRayImagePayloadMapping(disc, raster, new BluRayRunLengthMapping());
                var options = new BluRayStreamingOptions(disc, disc.TotalSectors / 64 * 32, 2048);
                BluRayStreamingGenerator.GenerateToStream(Stream.Null, options, mapping);
                var milliseconds = new double[5];
                for (int i = 0; i < milliseconds.Length; i++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    BluRayStreamingGenerator.GenerateToStream(Stream.Null, options, mapping);
                    milliseconds[i] = stopwatch.Elapsed.TotalMilliseconds;
                }
                Array.Sort(milliseconds);
                Console.WriteLine($"BD {(variable ? "cubic" : "constant")}, {size}x{size}, 4 MiB, 5 iterations: "
                    + $"median {milliseconds[2]:F2} ms, {4000 / milliseconds[2]:F2} MiB/s");
            }
        }
    }

    internal static void Run()
    {
        TestGeometryAndInverse();
        TestConstantOutputCompatibility();
        TestRasterAndPatternOutput();
        BluRayPixelReuseTests.RunCubicPitch();
        TestMetadataAndModule();
        Console.WriteLine("blu-ray-cubic-pitch: passed (geometry, inverse projection, unchanged constant output, scalar-equivalent raster/pattern output, metadata, CLI)");
    }

    private static void TestGeometryAndInverse()
    {
        foreach (var disc in new[]
        {
            new BluRayDiscParameters(StartAngleDegrees: 37.25, PitchLinear: .24, PitchQuadratic: -.18, PitchCubic: .08),
            new BluRayDiscParameters(128, StartAngleDegrees: -23, PitchLinear: -.5, PitchQuadratic: .8, PitchCubic: -.2),
        })
        {
            var geometry = new BluRayTrackGeometry(disc);
            double expectedLength = disc.TotalSectors / 32.0 * BluRayDiscParameters.ChannelBitsPerRecordingUnit
                * BluRayDiscParameters.ChannelBitLengthNm * 1e-6;
            Require(geometry.TrackLengthPerLayerMm == expectedLength, "recording-unit length includes run-in and run-out");
            var reference = DvdTrackGeometry.Create(disc.InnerRadiusMm, disc.OuterRadiusMm, expectedLength,
                disc.PitchLinear, disc.PitchQuadratic, disc.PitchCubic);
            Require(geometry.AtSector(0).RadiusMm == disc.InnerRadiusMm, "exact inner endpoint");
            Require(geometry.AtSector(disc.TotalSectors).RadiusMm == disc.OuterRadiusMm, "exact outer endpoint");
            double previousRadius = disc.InnerRadiusMm, previousAngle = geometry.AtSector(0).AngleRadians;
            for (int i = 1; i < 1000; i++)
            {
                double sector = disc.TotalSectors * (i / 1000.0);
                var point = geometry.AtSector(sector);
                var expected = reference.AtArcLengthFast(sector / disc.TotalSectors * expectedLength);
                Require(point.RadiusMm > previousRadius && point.AngleRadians < previousAngle,
                    "positive cubic pitch travels outward and clockwise");
                Require(Math.Abs(point.RadiusMm - expected.Radius) < 1e-12
                    && Math.Abs(point.AngleRadians - ((disc.StartAngleDegrees % 360) * Math.PI / 180 - expected.Angle)) < 1e-12,
                    "BD uses the same physical pitch model as calibration");
                Require(geometry.TryGetSectorAtPoint(point.Xmm, point.Ymm, out double recovered)
                    && Math.Abs(recovered - sector) < Math.Max(1e-6, disc.TotalSectors * 2e-11),
                    "inverse projection recovers the same spiral address");
                previousRadius = point.RadiusMm;
                previousAngle = point.AngleRadians;
            }
            Require(!geometry.TryGetSectorAtPoint(0, 0, out _)
                && !geometry.TryGetSectorAtPoint(double.NaN, 0, out _), "inverse excludes invalid and hub coordinates");
            var constant = new BluRayTrackGeometry(disc with { PitchLinear = 0, PitchQuadratic = 0, PitchCubic = 0 });
            Require(Math.Abs(geometry.AtSector(disc.TotalSectors / 2.0).RadiusMm
                - constant.AtSector(disc.TotalSectors / 2.0).RadiusMm) > .01, "cubic correction changes physical positions");
            var preset = new BluRayDiscPreset("cubic", "Cubic", 1, disc);
            Require(preset.CreateParameters() == (disc with { UsableSectors = disc.TotalSectors }),
                "preset preserves calibrated geometry and resolves the full usable capacity");
        }
        foreach (double invalid in new[] { -2d, double.NaN, double.PositiveInfinity })
        {
            try
            {
                new BluRayDiscParameters(PitchLinear: invalid).Validate();
                throw new InvalidOperationException("invalid pitch was accepted");
            }
            catch (ArgumentOutOfRangeException) { }
        }
    }

    private static void TestConstantOutputCompatibility()
    {
        // Captured from the pre-cubic generator. These include the exact geometric
        // branch, image decisions, LDC layout and scrambler output together.
        (uint Total, double Angle, uint First, string Hash)[] fixtures =
        [
            (12219392, 37.25, 0, "EB48104DEC65D58D7F003D35A0E43E10233C4AB73F97AB9462B7B6200AB92833"),
            (12219392, 37.25, 6109696, "0F0993A52564D387462D1E25C04977D597309B9EC5FB0CC3F18A9541E6D939C3"),
            (12219392, 37.25, 12219360, "CA2798D05DD8BEAD022BAFA04D224E959B6B775BC129FA9D33FCE059DCDA1FD9"),
            (128, -23, 0, "3106D4A89C923AD1091F1657D8DE940E7449830A634A6E639C24D381F2A44F69"),
            (128, -23, 64, "07539084A192601D5B2D48F0C481289FA2B612CC86BADF1E9A9E32CA46FBAAE7"),
            (128, -23, 96, "97257F6D4A35D13D30DBAE1739A02765CB0C3891A89BB1C53644A20953539592"),
        ];
        var raster = Raster(513, 257);
        foreach (var fixture in fixtures)
        {
            var disc = new BluRayDiscParameters(fixture.Total, StartAngleDegrees: fixture.Angle);
            byte[] actual = Generate(disc, fixture.First, new BluRayImagePayloadMapping(disc, raster, new BluRayRunLengthMapping()));
            Require(Convert.ToHexString(SHA256.HashData(actual)) == fixture.Hash, "zero coefficients retain legacy bytes");
        }
    }

    private static void TestRasterAndPatternOutput()
    {
        foreach (var shape in new[] { (.24, -.18, .08), (-.5, .8, -.2), (-1.8, 1.1, .4) })
        {
            var disc = new BluRayDiscParameters(StartAngleDegrees: 37.25,
                PitchLinear: shape.Item1, PitchQuadratic: shape.Item2, PitchCubic: shape.Item3);
            foreach (var raster in new[] { Raster(513, 257), Raster(4096, 4096), Raster(8192, 129) })
            {
                var fast = new BluRayImagePayloadMapping(disc, raster, new BluRayRunLengthMapping());
                var scalar = new BluRayImagePayloadMapping(disc, raster, new ScalarRunLength());
                foreach (uint first in new[] { 0U, disc.TotalSectors / 64 * 32, disc.TotalSectors - 32 })
                    Require(Generate(disc, first, fast).AsSpan().SequenceEqual(Generate(disc, first, scalar)),
                        "optimized cubic raster output is byte-identical to independent per-byte sampling");
            }
            uint middle = disc.TotalSectors / 64 * 32;
            static byte Pattern(double x, double y) => (x > 0) == (y > 0) ? (byte)0 : (byte)255;
            var range = new BluRayPatternRangeMapping(disc, Pattern, middle, middle + 32, false, allowParallelSampling: true);
            var scalarPattern = new BluRayImagePayloadMapping(disc, Pattern, new ScalarRunLength());
            Require(Generate(disc, middle, range).AsSpan().SequenceEqual(Generate(disc, middle, scalarPattern)),
                "procedural calibration uses the same cubic positions as image generation");
            Require(Generate(disc, middle - 32, range).AsSpan().SequenceEqual(
                Generate(disc, middle - 32, new BluRayConstantByteMapping(0))), "calibration leaves reserved sectors unchanged");
        }
    }

    private static void TestMetadataAndModule()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-cubic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string iso = Path.Combine(directory, "cubic.iso");
            var module = new BluRayDiscModule((_, _) => Raster(17, 17));
            var result = module.ExecuteAsync(new DiscJobRequest("bd-generate",
                ["--input", "source.png", "--output", iso, "--total-sectors", "128",
                 "--pitch-linear", "0.24", "--pitch-quadratic", "-0.18", "--pitch-cubic", "0.08"])).GetAwaiter().GetResult();
            Require(result.Succeeded && new FileInfo(iso).Length == 128L * 2048, "CLI generates a complete calibrated ISO");
            var metadata = BluRayIsoMetadata.TryLoad(iso)!;
            Require(metadata is not null && metadata.PitchLinear == .24 && metadata.PitchQuadratic == -.18
                && metadata.PitchCubic == .08, "CLI sidecar retains all pitch coefficients");
            string path = BluRayIsoMetadata.PathFor(iso);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (string key in json.Select(pair => pair.Key).Where(key => key.StartsWith("pitch", StringComparison.OrdinalIgnoreCase)).ToArray())
                json.Remove(key);
            File.WriteAllText(path, json.ToJsonString());
            var legacy = BluRayIsoMetadata.TryLoad(iso)!;
            Require(legacy is not null && legacy.PitchLinear == 0 && legacy.PitchQuadratic == 0
                && legacy.PitchCubic == 0, "older sidecars default to constant pitch");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static BluRayGray8Raster Raster(int width, int height)
    {
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)((y * 31 + x * 17) & 255);
        return new(width, height, pixels);
    }

    private static byte[] Generate(BluRayDiscParameters disc, uint first, IBluRayPayloadMapping mapping)
    {
        using var output = new MemoryStream();
        BluRayStreamingGenerator.GenerateToStream(output, new(disc, first, 32), mapping);
        return output.ToArray();
    }

    private sealed class ScalarRunLength : IBluRayLuminanceMapping
    {
        private readonly BluRayRunLengthMapping _mapping = new();
        public byte Map(byte luminance, BluRayPayloadBytePosition position) => _mapping.Map(luminance, position);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blu-ray cubic pitch: " + message + ".");
    }
}
