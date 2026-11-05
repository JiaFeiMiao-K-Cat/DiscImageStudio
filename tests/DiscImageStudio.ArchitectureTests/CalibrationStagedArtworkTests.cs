using DiscImageStudio.Core.Calibration;

internal static class CalibrationStagedArtworkTests
{
    public static void Run()
    {
        foreach (var parameters in new[]
        {
            new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200),
            new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 38, 714544, 133.3, .0002, -.00013, .00003),
            new CalibrationParameters(CalibrationDiscKind.BluRay, 24, 58, 12219392, 74.5,
                DrawingStartSector: 32 * 1000, DrawingEndSectorExclusive: 32 * 360000),
        })
        {
            var fine = CalibrationTarget.Create(parameters, artwork: CalibrationArtwork.ReferenceCross);
            var legacy = new CalibrationTarget(6, fine.Id, parameters, fine.Options, []);
            Require(fine.UsesReferencePattern && fine.TraceArmCount == 4 && fine.VisibleRings.SequenceEqual(legacy.VisibleRings),
                "restored cross retains exactly the complete source rings inside the writable annulus");
            // Sample across the antialiased edges of the original PNG, including its clipped rings.
            foreach (double radius in CalibrationReferencePattern.RingRadiiPixels)
            for (int offset = -26; offset <= 26; offset++)
            {
                double pixels = radius + offset * .25;
                var mm = CalibrationReferencePattern.PixelToDisc(1200 + pixels * .6, 1200 + pixels * .8,
                    parameters.OuterRadiusMm, parameters.Kind);
                Require(fine.Sample(mm.X, mm.Y) == legacy.Sample(mm.X, mm.Y), "schema 14 and original cross have identical ink");
            }
            var ray = CalibrationTarget.Create(parameters, artwork: CalibrationArtwork.SingleRadialLine);
            var record = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(ray, [])));
            ray = record.Target;
            Require(ray.HasSingleRadialLine && ray.TraceArmCount == 1 && ray.ControlPoints.Count == 2
                && ray.VisibleRings.Count == 0 && ray.RingThicknessMm == 0, "one physical line survives session round trip");
            double middle = (ray.DrawingInnerRadiusMm + ray.DrawingOuterRadiusMm) / 2;
            Require(ray.Sample(0, -middle) == 0 && ray.Sample(2.999, -middle) == 0 && ray.Sample(3.001, -middle) == 255
                && ray.Sample(0, middle) == 255 && ray.Sample(middle, 0) == 255 && ray.Sample(-middle, 0) == 255,
                "only the upper radius carries a six-millimetre strip");
            Require(ray.Sample(0, -ray.DrawingInnerRadiusMm + .02) == 255
                && ray.Sample(0, -ray.DrawingOuterRadiusMm - .02) == 255, "one line respects actual drawing bounds");
            var actual = parameters with { InnerRadiusMm = parameters.InnerRadiusMm + .003, OuterRadiusMm = parameters.OuterRadiusMm - .007 };
            var map = ray.CreateMapping(actual, rotationRadians: .41);
            var points = Enumerable.Range(0, 65).Select(i => map(new(0, -ray.DrawingInnerRadiusMm
                - (.05 + .9 * i / 64) * (ray.DrawingOuterRadiusMm - ray.DrawingInnerRadiusMm)))).ToArray();
            var options = new CalibrationFitOptions(parameters with { PitchLinear = 0, PitchQuadratic = 0, PitchCubic = 0 });
            var fit = CalibrationCrossFitter.Fit(ray, [new(0, points)], options);
            Require(fit.BestParameters is { } solved && Math.Abs(solved.InnerRadiusMm - actual.InnerRadiusMm) < .00001
                && Math.Abs(solved.OuterRadiusMm - actual.OuterRadiusMm) < .00001 && solved.PitchLinear == parameters.PitchLinear
                && solved.PitchQuadratic == parameters.PitchQuadratic && solved.PitchCubic == parameters.PitchCubic,
                $"{parameters.Kind} recovers radii from the strip centreline and retains the recorded pitch");
            try { CalibrationCrossFitter.ValidateTraces(ray, [new(1, points)]); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("A single-ray session accepted a trace on an unengraved arm");
        }
    }

    private static void Require(bool value, string what)
    {
        if (!value) throw new InvalidOperationException(what);
    }
}
