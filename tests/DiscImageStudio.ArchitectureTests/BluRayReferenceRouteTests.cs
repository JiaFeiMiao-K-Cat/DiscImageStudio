using System.IO;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class BluRayReferenceRouteTests
{
    internal static void Run()
    {
        foreach (var disc in new[]
        {
            new BluRayDiscParameters(),
            new BluRayDiscParameters(PitchLinear: .18, PitchQuadratic: -.12, PitchCubic: .06),
            new BluRayDiscParameters(TotalSectors: 512),
        })
        foreach (bool invert in new[] { false, true })
        foreach (bool restricted in new[] { false, true })
        {
            uint start = restricted ? 32U : 0;
            uint end = restricted ? disc.TotalSectors - 32 : disc.TotalSectors;
            var target = Target(disc, start, end, invert);
            var mapping = CalibrationViewModel.BluRayPatternMapping(target);
            Require(((IBluRayBatchPayloadMapping)mapping).CanGenerateInParallel(disc),
                "the app reference route retains parallel generation");
            if (disc.TotalSectors == BluRayDiscParameters.SectorsPerLayer)
                Require(((IBluRayBatchPayloadMapping)mapping).TryFillScrambledCluster(disc, 6_119_680,
                    new byte[BluRayDiscParameters.PayloadBytesPerCluster], default),
                    "standard and cubic reference targets use raster cluster reuse");
            foreach (uint first in new[] { 0U, start, (disc.TotalSectors / 64) * 32, end - 32, disc.TotalSectors - 32 }.Distinct())
                Compare(disc, first, 32, mapping, Scalar(target), "reference range/edge/polarity");
        }
        CheckRadialClipping();
        CheckLegacyAndProcedural();
        CheckUdfRoutes();
        Console.WriteLine("blu-ray-reference-routes: passed (raster reuse, scalar bytes, radial/range edges, legacy orientation, UDF)");
    }

    private static void CheckRadialClipping()
    {
        var disc = new BluRayDiscParameters(PitchLinear: .18, PitchQuadratic: -.12, PitchCubic: .06);
        const uint first = 6_119_680;
        var positions = new BluRayImagePayloadMapping(disc, static (_, _) => 0, new ScalarTone(false));
        var a = positions.GetPosition(first + 8, 731).TrackPoint;
        var b = positions.GetPosition(first + 24, 731).TrackPoint;
        // LDC interleaving scatters adjacent host sectors along the cluster.
        double squaredA = a.Xmm * a.Xmm + a.Ymm * a.Ymm;
        double squaredB = b.Xmm * b.Xmm + b.Ymm * b.Ymm;
        double lower = Math.Min(squaredA, squaredB);
        double upper = Math.Max(squaredA, squaredB);
        foreach (double edge in new[] { Math.BitDecrement(lower), lower, Math.BitIncrement(lower) })
        foreach (bool invert in new[] { false, true })
        {
            var optimized = new BluRayPatternRangeMapping(disc, new BluRayGray8Raster(1, 1, [0]),
                0, disc.TotalSectors, invert, edge, upper);
            var scalar = new BluRayImagePayloadMapping(disc, (x, y) =>
            {
                double squared = x * x + y * y;
                return squared < edge || squared > upper ? (byte)255 : (byte)0;
            }, new ScalarTone(invert));
            Compare(disc, first, 32, optimized, scalar, "exact squared-radius boundaries inside a reused cluster");
        }
    }

    private static void CheckLegacyAndProcedural()
    {
        var disc = new BluRayDiscParameters(TotalSectors: 512);
        var current = Target(disc, 0, disc.TotalSectors, false);
        var legacy = new CalibrationTarget(6, current.Id, current.Parameters, current.Options, current.Cells);
        foreach (var target in new[] { legacy,
            CalibrationTarget.Create(current.Parameters, artwork: CalibrationArtwork.HalfDisc),
            CalibrationTarget.Create(current.Parameters, artwork: CalibrationArtwork.WavyBoundary) })
        {
            var mapping = CalibrationViewModel.BluRayPatternMapping(target);
            Require(!((IBluRayBatchPayloadMapping)mapping).TryFillScrambledCluster(disc, 0,
                new byte[BluRayDiscParameters.PayloadBytesPerCluster], default),
                "legacy and analytic targets retain their procedural route");
            Compare(disc, 0, 32, mapping, Scalar(target), "legacy orientation and analytic sampler");
        }
    }

    private static void CheckUdfRoutes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-reference-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "sample.txt"), "reference route");
            var disc = new BluRayDiscParameters(TotalSectors: 1024,
                PitchLinear: .18, PitchQuadratic: -.12, PitchCubic: .06);
            foreach (bool outer in new[] { false, true })
            {
                var plan = BluRayIsoGenerator.PrepareDrawing(disc, directory, "REFERENCE", filesAtOuterRing: outer);
                var target = Target(disc, plan.DrawingStartSector, plan.DrawingEndSectorExclusive, outer);
                using MemoryStream actual = new(), expected = new();
                BluRayIsoGenerator.GenerateToStream(actual, plan, CalibrationViewModel.BluRayPatternMapping(target));
                BluRayIsoGenerator.GenerateToStream(expected, plan, Scalar(target));
                Require(actual.ToArray().AsSpan().SequenceEqual(expected.ToArray()),
                    "inner/outer UDF file regions retain exact reference bytes");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static CalibrationTarget Target(BluRayDiscParameters disc, uint start, uint end, bool invert)
        => CalibrationTarget.Create(new(CalibrationDiscKind.BluRay, disc.InnerRadiusMm, disc.OuterRadiusMm,
            disc.TotalSectors, BluRayDiscParameters.ChannelBitLengthNm,
            disc.PitchLinear, disc.PitchQuadratic, disc.PitchCubic,
            BluRayInvertMapping: invert, DrawingStartSector: start, DrawingEndSectorExclusive: end),
            artwork: CalibrationArtwork.ReferenceCross);

    private static IBluRayPayloadMapping Scalar(CalibrationTarget target)
    {
        var disc = CalibrationViewModel.BluRayParameters(target.Parameters);
        Func<double, double, byte> sampler = target.SchemaVersion >= 14 ? (x, y) => target.Sample(x, -y) : target.Sample;
        return new ScalarRange(new(disc, sampler, new ScalarTone(target.Parameters.BluRayInvertMapping)),
            (uint)target.Parameters.DrawingStartSector,
            target.Parameters.DrawingEndSectorExclusive == 0 ? disc.TotalSectors : (uint)target.Parameters.DrawingEndSectorExclusive);
    }

    private static void Compare(BluRayDiscParameters disc, uint first, uint count,
        IBluRayPayloadMapping actualMapping, IBluRayPayloadMapping expectedMapping, string label)
    {
        using MemoryStream actual = new(), expected = new();
        BluRayStreamingGenerator.GenerateToStream(actual, new(disc, first, count), actualMapping);
        BluRayStreamingGenerator.GenerateToStream(expected, new(disc, first, count), expectedMapping);
        Require(actual.ToArray().AsSpan().SequenceEqual(expected.ToArray()), label);
    }

    private sealed class ScalarRange(BluRayImagePayloadMapping image, uint start, uint end) : IBluRayPayloadMapping
    {
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            if (context.Sector < start || context.Sector >= end) destination.Clear();
            else image.FillScrambledPayload(context, destination);
        }
    }

    private sealed class ScalarTone(bool invert) : IBluRayLuminanceMapping
    {
        private readonly BluRayRunLengthMapping _mapping = new(invert);
        public byte Map(byte luminance, BluRayPayloadBytePosition position) => _mapping.Map(luminance, position);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("BD reference route: " + message);
    }
}
