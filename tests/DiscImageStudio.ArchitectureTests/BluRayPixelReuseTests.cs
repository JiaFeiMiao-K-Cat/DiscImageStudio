using System.IO;
using DiscImageStudio.BluRay;

internal static class BluRayPixelReuseTests
{
    internal static void Run()
    {
        TestAlignedPixelAndImageEdges();
        TestAxisAlignedTangents();
        TestTravelAwayFromWhiteMargins();
        TestHighResolutionAndRandomGeometry();
        TestUnusualGeometryFallback();
        Console.WriteLine("blu-ray-pixel-reuse: passed (pixel boundaries, axial tangents, white-margin re-entry, inner/middle/outer clusters, fallback)");
    }

    internal static void RunCubicPitch()
    {
        var large = Checkerboard(8192, 8192);
        foreach (var disc in new[]
        {
            new BluRayDiscParameters(PitchLinear: .25, PitchQuadratic: -.125, PitchCubic: .125),
            new BluRayDiscParameters(PitchLinear: 1e-12),
            new BluRayDiscParameters(PitchLinear: .25001, PitchQuadratic: -.125, PitchCubic: .125),
        })
        {
            TestAlignedPixelAndImageEdges(disc);
            TestTravelAwayFromWhiteMargins(disc);
            foreach (uint first in new[] { 0U, disc.TotalSectors / 64 * 32, disc.TotalSectors - 32 })
            {
                foreach (double perturbation in new[] { -1e-8, 1e-8 })
                {
                    var aligned = AlignToPixelCoordinate(disc, large, first + 17, 1091,
                        horizontal: true, edge: 4109, perturbation);
                    CheckCluster(aligned, large, first, false, "8K cubic pixel boundary");
                }
            }
        }
        Console.WriteLine("blu-ray-cubic-pixel-reuse: passed (8K boundaries, tiny coefficients, reuse-limit boundary, white-margin re-entry)");
    }

    private static void TestAlignedPixelAndImageEdges(BluRayDiscParameters? parameters = null)
    {
        BluRayDiscParameters nominal = parameters ?? new();
        uint middle = (nominal.TotalSectors / 64) * 32;
        uint last = nominal.TotalSectors - 32;
        // The narrow dimension deliberately cuts through the recording annulus,
        // so all four image borders have payload samples on either side.
        var probes = new[]
        {
            (Width: 513, Height: 513, First: 0U, Horizontal: true, Edge: 273d),
            (Width: 513, Height: 513, First: last, Horizontal: false, Edge: 269d),
            (Width: 129, Height: 513, First: 0U, Horizontal: true, Edge: 0d),
            (Width: 129, Height: 513, First: middle, Horizontal: true, Edge: 129d),
            (Width: 513, Height: 129, First: last, Horizontal: false, Edge: 0d),
            (Width: 513, Height: 129, First: middle, Horizontal: false, Edge: 129d),
        };
        for (int index = 0; index < probes.Length; index++)
        {
            var probe = probes[index];
            BluRayGray8Raster raster = Checkerboard(probe.Width, probe.Height);
            uint sector = probe.First + 17;
            const int byteOffset = 1091;
            foreach (double perturbation in new[] { -1e-8, 0d, 1e-8 })
            {
                BluRayDiscParameters aligned = AlignToPixelCoordinate(nominal, raster, sector, byteOffset,
                    probe.Horizontal, probe.Edge, perturbation);
                CheckCluster(aligned, raster, probe.First, invert: (index & 1) != 0,
                    $"{probe.Width}x{probe.Height}, {(probe.Horizontal ? "x" : "y")}={probe.Edge:R}, "
                    + $"delta={perturbation:R}, cluster={probe.First}");
            }
        }
    }

    private static BluRayDiscParameters AlignToPixelCoordinate(BluRayDiscParameters disc,
        BluRayGray8Raster raster, uint sector, int byteOffset, bool horizontal, double edge, double perturbation)
    {
        BluRayTrackPoint initial = Position(disc with { StartAngleDegrees = 0 }, raster, sector, byteOffset);
        double scale = Math.Max(raster.Width, raster.Height) / (2 * disc.OuterRadiusMm);
        double target = edge + perturbation;
        double ratio = horizontal
            ? (target - raster.Width / 2.0) / (initial.RadiusMm * scale)
            : (raster.Height / 2.0 - target) / (initial.RadiusMm * scale);
        Require(Math.Abs(ratio) < 1, "the selected image edge intersects the probe radius");
        double angle = horizontal ? Math.Acos(ratio) : Math.Asin(ratio);
        double rotation = ((angle - initial.AngleRadians) * 180 / Math.PI) % 360;
        BluRayDiscParameters aligned = disc;
        double coordinate = double.NaN;
        double derivative = 0;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            aligned = disc with { StartAngleDegrees = rotation };
            BluRayTrackPoint point = Position(aligned, raster, sector, byteOffset);
            coordinate = PixelCoordinate(raster, point, scale, horizontal);
            derivative = horizontal
                ? -point.RadiusMm * Math.Sin(point.AngleRadians) * scale * Math.PI / 180
                : -point.RadiusMm * Math.Cos(point.AngleRadians) * scale * Math.PI / 180;
            if (Math.Abs(coordinate - target) <= 2e-9) break;
            rotation += (target - coordinate) / derivative;
        }
        // At the outer disc, angle subtraction itself can quantize coordinates
        // by about 1e-8 pixel. Ensure both requested sides are actually exercised
        // without demanding an unrepresentable exact coordinate.
        for (int iteration = 0; perturbation != 0 && iteration < 16
            && Math.Sign(coordinate - edge) != Math.Sign(perturbation); iteration++)
        {
            rotation = aligned.StartAngleDegrees + Math.CopySign(1e-8, perturbation / derivative);
            aligned = disc with { StartAngleDegrees = rotation };
            coordinate = PixelCoordinate(raster, Position(aligned, raster, sector, byteOffset), scale, horizontal);
        }
        Require(Math.Abs(coordinate - edge) < 2e-6, "the adversarial sample stays close to its intended pixel edge");
        Require(perturbation == 0 || Math.Sign(coordinate - edge) == Math.Sign(perturbation),
            "boundary probes cover both the lower and upper pixel decisions");
        return aligned;
    }

    private static void TestAxisAlignedTangents()
    {
        BluRayDiscParameters nominal = new();
        BluRayGray8Raster raster = Checkerboard(1025, 1025);
        uint middle = (nominal.TotalSectors / 64) * 32;
        uint[] starts = [0, middle, nominal.TotalSectors - 32, middle];
        double growth = new BluRayTrackGeometry(nominal).EffectiveTrackPitchMicrometers / (2 * Math.PI * 1000);
        for (int quadrant = 0; quadrant < 4; quadrant++)
        {
            uint sector = starts[quadrant] + 11;
            const int offset = 741;
            double radius = Position(nominal, raster, sector, offset).RadiusMm;
            foreach (double perturbationDegrees in new[] { -1e-6, 0d, 1e-6 })
            {
                double axisAngle = quadrant * Math.PI / 2;
                double perturbation = perturbationDegrees * Math.PI / 180;
                double targetTangent = axisAngle + perturbation;
                // The clockwise spiral's tangent angle is polarAngle-atan2(r,b).
                BluRayDiscParameters aligned = AlignToPolarAngle(nominal, raster, sector, offset,
                    targetTangent + Math.Atan2(radius, growth));
                BluRayTrackPoint point = Position(aligned, raster, sector, offset);
                double actualTangent = Math.Atan2(growth * Math.Sin(point.AngleRadians) - point.RadiusMm * Math.Cos(point.AngleRadians),
                    growth * Math.Cos(point.AngleRadians) + point.RadiusMm * Math.Sin(point.AngleRadians));
                Require(Math.Abs(WrapAngle(actualTangent - targetTangent)) < 2e-9,
                    "the probe tangent is close to its intended axis");
                Require(perturbation == 0 || Math.Sign(WrapAngle(actualTangent - axisAngle)) == Math.Sign(perturbation),
                    "axial tangent probes exercise both signs of the almost-zero component");
                CheckCluster(aligned, raster, starts[quadrant], (quadrant & 1) != 0,
                    $"tangent={quadrant * 90} degrees, delta={perturbationDegrees:R}, cluster={starts[quadrant]}");
            }
        }
    }

    private static void TestTravelAwayFromWhiteMargins(BluRayDiscParameters? parameters = null)
    {
        BluRayDiscParameters nominal = parameters ?? new();
        uint[] starts = [0, (nominal.TotalSectors / 64) * 32, nominal.TotalSectors - 32];
        BluRayGray8Raster probeRaster = Checkerboard(1, 1);
        foreach (uint first in starts)
        {
            double radius = Position(nominal, probeRaster, first, 0).RadiusMm;
            // Put the short image edge at about 65% of the current radius.
            // A cluster starting five degrees beyond a cardinal position first
            // travels farther into white space, turns, and later re-enters the
            // image. This checks long negative-tangent roots and their expiry.
            const int longSide = 1025;
            int shortSide = Math.Max(3, (int)Math.Round(longSide * .65 * radius / nominal.OuterRadiusMm) | 1);
            for (int quadrant = 0; quadrant < 4; quadrant++)
            {
                bool horizontalEdge = (quadrant & 1) == 0;
                BluRayGray8Raster raster = horizontalEdge
                    ? Checkerboard(shortSide, longSide) : Checkerboard(longSide, shortSide);
                BluRayDiscParameters aligned = AlignToPolarAngle(nominal, raster, first, 0,
                    (quadrant * 90 + 5) * Math.PI / 180);
                BluRayTrackPoint point = Position(aligned, raster, first, 0);
                double scale = longSide / (2 * nominal.OuterRadiusMm);
                double x = raster.Width / 2.0 + point.Xmm * scale;
                double y = raster.Height / 2.0 - point.Ymm * scale;
                bool outside = quadrant switch
                {
                    0 => x > raster.Width + 1,
                    1 => y < -1,
                    2 => x < -1,
                    _ => y > raster.Height + 1,
                };
                Require(outside, "the long-travel anchor starts well outside its selected white edge");
                // Compare a nearby public scalar position instead of calling the
                // optimized tangent helper when checking the direction of travel.
                BluRayImagePayloadMapping positions = new(aligned, probeRaster, new ScalarRunLength(false));
                double sectorPosition = positions.GetPosition(first, 0).NominalChannelBitPosition
                    / (BluRayDiscParameters.ChannelBitsPerRecordingUnit / 32.0);
                BluRayTrackPoint next = new BluRayTrackGeometry(aligned).AtSector(sectorPosition + .001);
                bool movingAway = quadrant switch
                {
                    0 => next.Xmm > point.Xmm,
                    1 => next.Ymm > point.Ymm,
                    2 => next.Xmm < point.Xmm,
                    _ => next.Ymm < point.Ymm,
                };
                Require(movingAway, "the initial tangent moves away from the image instead of toward it");
                CheckCluster(aligned, raster, first, (quadrant & 1) != 0,
                    $"white-margin turn/re-entry, quadrant={quadrant + 1}, cluster={first}", requireBothLuminanceSides: true);
            }
        }
    }

    private static BluRayDiscParameters AlignToPolarAngle(BluRayDiscParameters disc, BluRayGray8Raster raster,
        uint sector, int byteOffset, double targetRadians)
    {
        double initialAngle = Position(disc with { StartAngleDegrees = 0 }, raster, sector, byteOffset).AngleRadians;
        double rotation = ((targetRadians - initialAngle) * 180 / Math.PI) % 360;
        BluRayDiscParameters aligned = disc;
        for (int iteration = 0; iteration < 6; iteration++)
        {
            aligned = disc with { StartAngleDegrees = rotation };
            BluRayTrackPoint point = Position(aligned, raster, sector, byteOffset);
            double error = WrapAngle(targetRadians - Math.Atan2(point.Ymm, point.Xmm));
            if (Math.Abs(error) < 1e-11) break;
            rotation += error * 180 / Math.PI;
        }
        BluRayTrackPoint actual = Position(aligned, raster, sector, byteOffset);
        Require(Math.Abs(WrapAngle(targetRadians - Math.Atan2(actual.Ymm, actual.Xmm))) < 2e-9,
            "the selected payload sample has the intended polar angle");
        return aligned;
    }

    private static double WrapAngle(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));

    private static void TestHighResolutionAndRandomGeometry()
    {
        BluRayGray8Raster detailed = Checkerboard(4097, 2049);
        BluRayDiscParameters nominal = new(StartAngleDegrees: 117.25);
        CheckCluster(nominal, detailed, (nominal.TotalSectors / 64) * 32, false, "high-resolution middle annulus");
        CheckCluster(nominal, detailed, nominal.TotalSectors - 32, true, "high-resolution outer annulus");

        Random random = new(743_201);
        for (int index = 0; index < 6; index++)
        {
            uint sectors = index < 3 ? BluRayDiscParameters.SectorsPerLayer : (uint)random.Next(20_000, 300_000) * 32;
            BluRayDiscParameters disc = new(sectors, InnerRadiusMm: 20 + random.NextDouble() * 10,
                OuterRadiusMm: 54 + random.NextDouble() * 6, StartAngleDegrees: random.NextDouble() * 1440 - 720);
            BluRayGray8Raster raster = index switch
            {
                0 => Checkerboard(1, 1025),
                1 => Checkerboard(1025, 1),
                _ => Checkerboard(random.Next(97, 640), random.Next(97, 640)),
            };
            uint first = (uint)random.Next((int)(sectors / 32)) * 32;
            CheckCluster(disc, raster, first, (index & 1) != 0, $"seeded geometry {index}");
        }
    }

    private static void TestUnusualGeometryFallback()
    {
        BluRayGray8Raster raster = Checkerboard(257, 113);
        foreach (BluRayDiscParameters disc in new[]
        {
            new BluRayDiscParameters(TotalSectors: 32, StartAngleDegrees: -83),
            new BluRayDiscParameters(TotalSectors: 640, InnerRadiusMm: 25, OuterRadiusMm: 57),
            new BluRayDiscParameters(InnerRadiusMm: .5, StartAngleDegrees: 1e100),
            new BluRayDiscParameters(InnerRadiusMm: 57.999999, OuterRadiusMm: 58),
        })
        {
            CheckCluster(disc, raster, disc.TotalSectors - 32, true,
                $"fallback sectors={disc.TotalSectors}, radii={disc.InnerRadiusMm:R}..{disc.OuterRadiusMm:R}");
        }
    }

    private static void CheckCluster(BluRayDiscParameters disc, BluRayGray8Raster raster,
        uint firstSector, bool invert, string label, bool requireBothLuminanceSides = false)
    {
        ScalarRunLength scalarStrategy = new(invert);
        using MemoryStream expected = new();
        using MemoryStream actual = new();
        BluRayStreamingOptions options = new(disc, firstSector, 32);
        // A custom strategy forces the independent public scalar position path.
        BluRayStreamingGenerator.GenerateToStream(expected, options,
            new BluRayImagePayloadMapping(disc, raster, scalarStrategy));
        BluRayStreamingGenerator.GenerateToStream(actual, options,
            new BluRayImagePayloadMapping(disc, raster, new BluRayRunLengthMapping(invert)));
        Require(scalarStrategy.Count == BluRayDiscParameters.PayloadBytesPerCluster,
            "the scalar oracle visits every payload byte: " + label);
        Require(!requireBothLuminanceSides || scalarStrategy.DarkCount > 0 && scalarStrategy.DarkCount < scalarStrategy.Count,
            "the long-travel cluster re-enters image content rather than remaining uniformly white: " + label);
        byte[] reference = expected.ToArray(), generated = actual.ToArray();
        Require(reference.Length == BluRayDiscParameters.PayloadBytesPerCluster && generated.Length == reference.Length,
            "both paths emit one complete cluster: " + label);
        for (int index = 0; index < reference.Length; index++)
            if (reference[index] != generated[index])
                throw new InvalidOperationException($"BD pixel reuse: {label}, payload {index}: "
                    + $"scalar={reference[index]:X2}, optimized={generated[index]:X2}");
    }

    private static BluRayTrackPoint Position(BluRayDiscParameters disc, BluRayGray8Raster raster,
        uint sector, int byteOffset)
        => new BluRayImagePayloadMapping(disc, raster, new ScalarRunLength(false)).GetPosition(sector, byteOffset).TrackPoint;

    private static double PixelCoordinate(BluRayGray8Raster raster, BluRayTrackPoint point,
        double scale, bool horizontal)
        => horizontal ? raster.Width / 2.0 + point.Xmm * scale : raster.Height / 2.0 - point.Ymm * scale;

    private static BluRayGray8Raster Checkerboard(int width, int height)
    {
        byte[] pixels = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = ((x ^ y) & 1) == 0 ? (byte)127 : (byte)128;
        return new BluRayGray8Raster(width, height, pixels);
    }

    private sealed class ScalarRunLength(bool invert) : IBluRayLuminanceMapping
    {
        public int Count { get; private set; }
        public int DarkCount { get; private set; }
        public byte Map(byte luminance, BluRayPayloadBytePosition position)
        {
            Count++;
            bool dark = luminance < 128;
            if (dark) DarkCount++;
            return dark != invert ? (byte)0xAA : (byte)0x44;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("BD pixel reuse: " + message);
    }
}
