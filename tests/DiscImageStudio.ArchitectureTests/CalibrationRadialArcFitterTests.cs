using DiscImageStudio.Core.Calibration;

internal static class CalibrationRadialArcFitterTests
{
    internal static void Run()
    {
        var generated = new CalibrationParameters(CalibrationDiscKind.Cd, 24.783256128745627,
            57.755863305475302, 359848, 1200);
        var target = CalibrationTarget.Create(generated, artwork: CalibrationArtwork.RadialArcDashes);
        var legacy = new CalibrationTarget(9, target.Id, generated, target.Options, target.Cells, target.Artwork);
        var verbatim = generated with { InnerRadiusMm = 24.837775, OuterRadiusMm = 58.020875 };
        var legacyFit = CalibrationRadialArcFitter.Fit(legacy, Measure(legacy, verbatim));
        Require(legacyFit.Succeeded, legacyFit.Message);
        Near(verbatim.InnerRadiusMm, legacyFit.Corrected!.InnerRadiusMm, 1e-5);
        Near(verbatim.OuterRadiusMm, legacyFit.Corrected.OuterRadiusMm, 1e-5);
        var firstPass = generated with { InnerRadiusMm = 24.759121183688983, OuterRadiusMm = 57.770927203914873 };
        foreach (var seed in new[] { firstPass, generated })
        {
            var simulated = CalibrationTarget.Create(seed, artwork: CalibrationArtwork.RadialArcDashes);
            Require(Measure(simulated, verbatim).All(mark => mark.SpanDegrees < 360),
                "all three thick marks stay within one turn for either measured seed on the Verbatim reference");
        }
        foreach (var actual in new[] {
            verbatim,
            generated with { InnerRadiusMm = 24.74, OuterRadiusMm = 57.66 } })
        {
            var observations = Measure(target, actual);
            var fit = CalibrationRadialArcFitter.Fit(target, observations);
            Require(fit.Succeeded, fit.Message);
            Near(actual.InnerRadiusMm, fit.Corrected!.InnerRadiusMm, 1e-5);
            Near(actual.OuterRadiusMm, fit.Corrected.OuterRadiusMm, 1e-5);
            var two = CalibrationRadialArcFitter.Fit(target, [observations[0], observations[2]]);
            Require(two.Succeeded, two.Message);
            Near(actual.InnerRadiusMm, two.Corrected!.InnerRadiusMm, 1e-5);
            Near(actual.OuterRadiusMm, two.Corrected.OuterRadiusMm, 1e-5);
        }
        var sample = Measure(target, verbatim);
        var unchanged = Measure(target, generated);
        Require(CalibrationRadialArcFitter.Fit(target,
            [unchanged[0] with { Direction = CalibrationArcBendDirection.Unknown }, unchanged[2]]).Succeeded,
            "unresolved direction at the original width is accepted");
        Require(CalibrationRadialArcFitter.Fit(target, [sample[0] with { Direction = CalibrationArcBendDirection.Unknown }, sample[2]]).Status
            == CalibrationRadialArcFitStatus.Ambiguous, "unknown direction must not choose a branch");
        Require(CalibrationRadialArcFitter.Fit(target, [sample[0] with { SingleTurnConfirmed = false }, sample[2]]).Status
            == CalibrationRadialArcFitStatus.InsufficientData, "unconfirmed turns cannot be used");
        Require(CalibrationRadialArcFitter.Fit(target, [sample[0] with { MonotonicConfirmed = false }, sample[2]]).Status
            == CalibrationRadialArcFitStatus.InsufficientData, "reversal cannot be assumed away");
        Require(CalibrationRadialArcFitter.Fit(target, [sample[0], sample[1] with { SpanDegrees = sample[1].SpanDegrees + 20 }, sample[2]]).Status
            == CalibrationRadialArcFitStatus.Inconsistent, "conflicting third mark is rejected");
        Require(CalibrationRadialArcFitter.Fit(target, [sample[0] with { ApparentRadiusMm = sample[0].ApparentRadiusMm + 3 }, sample[2]]).Status
            == CalibrationRadialArcFitStatus.Inconsistent, "wrong apparent band is rejected");
        Console.WriteLine("calibration-radial-arc-fitter: independent forward mapping, both directions and ambiguity guards passed");
    }

    private static CalibrationRadialArcObservation[] Measure(CalibrationTarget target, CalibrationParameters actual)
    {
        return target.RadialArcDashes.Select(arc => {
            double sum = 0, previous = 0, radiusSum = 0;
            for (int i = 0; i <= 100; i++)
            {
                double sourceRadius = arc.InnerRadiusMm + (arc.OuterRadiusMm - arc.InnerRadiusMm) * i / 100;
                var p = target.MapPoint(new(sourceRadius, 0), actual, .57);
                double angle = Math.Atan2(p.Y, p.X);
                if (i != 0) sum += Math.IEEERemainder(angle - previous, Math.Tau);
                if (i == 0 || i == 100) radiusSum += Math.Sqrt(p.X * p.X + p.Y * p.Y);
                previous = angle;
            }
            double span = Math.Abs(sum) * 180 / Math.PI + 2 * arc.HalfAngleRadians * 180 / Math.PI;
            return new CalibrationRadialArcObservation(arc.Index, span,
                sum >= 0 ? CalibrationArcBendDirection.Clockwise : CalibrationArcBendDirection.Counterclockwise,
                true, true, true, radiusSum / 2);
        }).ToArray();
    }

    private static void Near(double expected, double actual, double tolerance)
        => Require(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
