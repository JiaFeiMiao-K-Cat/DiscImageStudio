using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;

internal static class CalibrationHalfDiscArtworkTests
{
    internal static void Run()
    {
        foreach (var p in new[]
        {
            new CalibrationParameters(CalibrationDiscKind.Cd, 24, 58, 359848, 1200),
            new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 38, 714544, 133.3, .0002, -.00013, .00003),
            new CalibrationParameters(CalibrationDiscKind.BluRay, 24, 58, 12219392, 74.5,
                DrawingStartSector: 32000, DrawingEndSectorExclusive: 11520000),
        })
        {
            var target = CalibrationTarget.Create(p, artwork: CalibrationArtwork.HalfDisc);
            var restored = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(target, []))).Target;
            Check(restored.HasHalfDisc && restored.SchemaVersion == 15 && restored.VisibleRings.Count == 0
                && restored.TraceArmCount == 2 && restored.Parameters == p, "half-disc snapshot roundtrip");
            for (int ring = 0; ring < 7; ring++)
            {
                double r = target.DrawingInnerRadiusMm + (.05 + .9 * ring / 6) * (target.DrawingOuterRadiusMm - target.DrawingInnerRadiusMm);
                int black = 0;
                for (int bearing = 0; bearing < 360; bearing++)
                {
                    double a = (bearing + .5) * Math.PI / 180;
                    byte value = restored.Sample(r * Math.Cos(a), r * Math.Sin(a));
                    Check(value == (bearing is >= 90 and < 270 ? 0 : 255), "source has two wide halves, no thin line or rings");
                    if (value == 0) black++;
                }
                Check(black == 180, "50 percent black at every drawing radius");
                var first = CalibrationCrossPrediction.CreateMapping(target, p, armIndex: 0)(r);
                var second = CalibrationCrossPrediction.CreateMapping(target, p, armIndex: 1)(r);
                Check(Math.Abs(first.X) < 1e-10 && first.Y < 0 && Math.Abs(second.X) < 1e-10 && second.Y > 0,
                    "two predicted edges match the source diameter");
            }
            Check(target.Sample(-target.DrawingInnerRadiusMm + .01, 0) == 255
                && target.Sample(-target.DrawingOuterRadiusMm - .01, 0) == 255, "clipped drawing interval");
            Throws(() => CalibrationCrossFitter.ValidateTraces(target, [new(0, [new(0, -30)])]),
                "ordinary continuous trace cannot silently reinterpret disconnected half-disc segments");
            Throws(() => CalibrationCrossPrediction.CreateMapping(target, p, armIndex: 2), "only two half-disc boundaries");
        }
        var dvd = CalibrationTarget.Create(new(CalibrationDiscKind.Dvd, 24, 58, 2297888, 133.3), artwork: CalibrationArtwork.HalfDisc);
        var image = CalibrationPatternRenderer.RenderEncodingImage(dvd);
        Check(image.PixelWidth == 8192 && image.PixelHeight == 8192, "new middle target exports at 8192 square");
        var row = new byte[8192];
        image.CopyPixels(new System.Windows.Int32Rect(0, 4096, 8192, 1), row, 8192, 0);
        Check(row[1000] == 0 && row[7191] == 255 && row[4096] == 255, "export keeps left black, right white and clear hole");
        CheckWavyBoundary();
        Console.WriteLine("half-disc artwork: immutable v15, two boundaries, CD/DVD/BD drawing intervals and 8192 export passed");
    }

    private static void CheckWavyBoundary()
    {
        foreach (var p in new[]
        {
            new CalibrationParameters(CalibrationDiscKind.Cd, 24, 58, 359848, 1200),
            new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 58, 2297888, 133.3, -.000039, .000070, -.000047),
            new CalibrationParameters(CalibrationDiscKind.BluRay, 24, 58, 12219392, 74.5,
                DrawingStartSector: 32000, DrawingEndSectorExclusive: 11520000),
        })
        {
            var target = CalibrationTarget.Create(p, artwork: CalibrationArtwork.WavyBoundary);
            var restored = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(target, []))).Target;
            Check(restored.SchemaVersion == 16 && restored.HasBoundaryPattern && restored.HasWavyBoundary
                && !restored.HasHalfDisc && restored.VisibleRings.Count == 0 && restored.ControlPoints.Count == 0,
                "new source is frozen as v16 without imaginary straight control spines");
            var actual = p with { InnerRadiusMm = p.InnerRadiusMm + .004, OuterRadiusMm = p.OuterRadiusMm - .003 };
            var map = target.CreateMapping(actual, .27);
            var observed = CalibrationCrossPrediction.CreateMapping(target, actual, .27);
            for (int station = 1; station < 64; station++)
            {
                double u = station / 64d;
                double radius = target.DrawingInnerRadiusMm + u * (target.DrawingOuterRadiusMm - target.DrawingInnerRadiusMm);
                // Independent source construction, before physical track distortion.
                double phase = Math.PI / 2 * Math.Sin(6 * Math.PI * u);
                double alpha = -Math.PI / 2 + phase;
                var source = new CalibrationPoint(radius * Math.Cos(alpha), radius * Math.Sin(alpha));
                var physical = map(source);
                var predicted = observed(Math.Sqrt(physical.X * physical.X + physical.Y * physical.Y));
                Check(Math.Sqrt(Math.Pow(physical.X - predicted.X, 2) + Math.Pow(physical.Y - predicted.Y, 2)) < .00001,
                    "prediction applies known wave exactly once at source radius, including DVD pitch and BD drawing bounds");
                Check(restored.Sample(radius * Math.Cos(alpha - .01), radius * Math.Sin(alpha - .01)) == 0
                    && restored.Sample(radius * Math.Cos(alpha + .01), radius * Math.Sin(alpha + .01)) == 255,
                    "burn sampler crosses the same analytic boundary as the fit");
            }
            var outline = CalibrationCrossPrediction.GetOutline(target, p, target.DrawingInnerRadiusMm, target.DrawingOuterRadiusMm);
            Check(outline.Count > 170 && outline.Min(q => q.X) < -25 && outline.Max(q => q.X) > 25,
                "matched geometry still draws the designed swings without aliasing coincident endpoint bearings");
        }
        var wavy = CalibrationTarget.Create(new(CalibrationDiscKind.Dvd, 24, 58, 2297888, 133.3), artwork: CalibrationArtwork.WavyBoundary);
        var image = CalibrationPatternRenderer.RenderEncodingImage(wavy);
        Check(image.PixelWidth == 8192 && image.PixelHeight == 8192, "wavy boundary exports at 8192 square");
        Console.WriteLine("wavy boundary artwork: fixed v16, exact source-phase prediction, all-media drawing intervals and 8192 export passed");
    }

    private static void Throws(Action action, string label)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException(label);
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Half-disc artwork: " + label);
    }
}
