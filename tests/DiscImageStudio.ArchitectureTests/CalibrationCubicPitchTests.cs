using System.Text.Json.Nodes;
using DiscImageStudio.Core.Calibration;

internal static class CalibrationCubicPitchTests
{
    internal static void Run()
    {
        TestIndependentForwardGeometry();
        foreach (var parameters in MediaParameters())
        {
            TestCubicRecoveryAndPrediction(parameters);
            // Recalibration of a disc generated with an existing correction (#92)
            // must recover absolute physical coefficients, not the residual correction.
            TestCubicRecoveryAndPrediction(parameters with
                { PitchLinear = .02, PitchQuadratic = -.03, PitchCubic = .025 });
        }
        TestSupportGuardsAndFixedCoefficients();
        TestPersistenceAndValidation();
        Console.WriteLine("calibration-cubic-pitch: CD/DVD/BD independent geometry, initial/repeated cubic recovery, prediction, support guards and persistence passed");
    }

    private static CalibrationParameters Parameters(long sectors = 2297888)
        => new(CalibrationDiscKind.Dvd, 24, 58, sectors, 133.33);

    private static IEnumerable<CalibrationParameters> MediaParameters()
        => [Parameters(), new(CalibrationDiscKind.Cd, 24, 58, 359847, 1200),
            new(CalibrationDiscKind.BluRay, 24, 58, 12219392, 74.5)];

    private static void TestIndependentForwardGeometry()
    {
        foreach (var parameters in MediaParameters().Prepend(Parameters(16)))
        {
            var generated = parameters with { PitchLinear = .15, PitchQuadratic = -.2, PitchCubic = .11 };
            var actual = generated with { InnerRadiusMm = 24.001, OuterRadiusMm = 57.999,
                PitchLinear = .1501, PitchQuadratic = -.20014, PitchCubic = .11009 };
            var target = CalibrationTarget.Create(generated);
            var referenceGenerated = new ReferenceGeometry(generated);
            var referenceActual = new ReferenceGeometry(actual);
            var prediction = CalibrationCrossPrediction.CreateMapping(target, actual, .31);
            foreach (double radius in new[] { 23.7, 24.5, 33.2, 47.1, 57.7, 58.3 })
            {
                double source = referenceGenerated.RadiusAtArc(referenceActual.ArcAtRadius(radius));
                double phase = referenceActual.AngleAtRadius(radius) - referenceGenerated.AngleAtRadius(source);
                Near(0, Distance(Polar(radius, -Math.PI / 2 + .31 + phase), prediction(radius)), 2e-6,
                    "nonconstant forward continuation agrees with independent exact numerical integration");
            }
            foreach (double radius in new[] { 24.3, 37.2, 57.6 })
            {
                var source = Polar(radius, -.72);
                var mapped = target.MapPoint(source, actual, .31);
                var restored = target.CreateInverseMapping(actual, .31)(mapped);
                Check(restored is not null, "cubic inverse mapping remains in the recording annulus");
                Near(0, Distance(source, restored!), 2e-6, "cubic forward/inverse correspondence");
            }
        }
    }

    private static void TestCubicRecoveryAndPrediction(CalibrationParameters generated)
    {
        var actual = generated with { InnerRadiusMm = 24.0012, OuterRadiusMm = 57.9993,
            PitchLinear = generated.PitchLinear + .0002, PitchQuadratic = generated.PitchQuadratic - .00026,
            PitchCubic = generated.PitchCubic + .00018 };
        var target = CalibrationTarget.Create(generated);
        const double rotation = .47;
        var referenceGenerated = new ReferenceGeometry(generated);
        var referenceActual = new ReferenceGeometry(actual);
        CalibrationPoint PhysicalPoint(double radius)
        {
            double source = referenceGenerated.RadiusAtArc(referenceActual.ArcAtRadius(radius));
            return Polar(radius, -Math.PI / 2 + rotation
                + referenceActual.AngleAtRadius(radius) - referenceGenerated.AngleAtRadius(source));
        }
        CalibrationPoint[] points = Enumerable.Range(0, 65)
            .Select(i => PhysicalPoint(24.3 + 33.4 * i / 64.0)).ToArray();
        var fixedFit = CalibrationCrossFitter.Fit(target, [new(0, points)], new(FitDvdPitch: false));
        var saved = new CalibrationSession(target, []);
        var restored = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(saved));
        var fit = CalibrationCrossFitter.Fit(restored.Target, [new(0, points)]);
        Check(fit.Succeeded && fit.BestParameters is not null, "full cubic fit succeeds");
        Check(fit.RmsErrorMm < 2e-5 && fit.RmsErrorMm < fixedFit.RmsErrorMm / 100,
            "cubic physical model resolves curvature missing from constant pitch");
        var recovered = fit.BestParameters!;
        Near(actual.InnerRadiusMm, recovered.InnerRadiusMm, 2e-6, "inner radius recovery");
        Near(actual.OuterRadiusMm, recovered.OuterRadiusMm, 2e-6, "outer radius recovery");
        Near(actual.PitchLinear, recovered.PitchLinear, 2e-7, "linear shape recovery");
        Near(actual.PitchQuadratic, recovered.PitchQuadratic, 2e-7, "quadratic shape recovery");
        Near(actual.PitchCubic, recovered.PitchCubic, 2e-7, "cubic shape recovery");
        var mapping = CalibrationCrossPrediction.CreateMapping(target, recovered, fit.RotationRadians);
        foreach (double radius in new[] { 24.01, 27.18, 39.93, 55.79, 57.99 })
            Near(0, Distance(mapping(radius), PhysicalPoint(radius)), 3e-5, "held-out forward prediction");
        var outline = CalibrationCrossPrediction.GetOutline(target, recovered, 24.01, 57.99, fit.RotationRadians);
        foreach (double radius in Enumerable.Range(0, 301).Select(i => 24.01 + 33.98 * i / 300.0))
            Check(DistanceToPolyline(mapping(radius), outline) < .011, "cubic outline chord tolerance and complete phase");
        Check(fit.Warnings.Any(w => w.Contains("不表示参数唯一", StringComparison.Ordinal)),
            "a single observed curve does not claim unique parameter identification");
        Check(restored.Target.Parameters == generated, "fitting keeps the disc generation parameters frozen");

        var correctedTarget = CalibrationTarget.Create(actual);
        var straight = Enumerable.Range(0, 33).Select(i => Polar(24.3 + 33.4 * i / 32, -Math.PI / 2 + rotation)).ToArray();
        var correctedFit = CalibrationCrossFitter.Fit(correctedTarget, [new(0, straight)]);
        Check(correctedFit.Succeeded && correctedFit.BestParameters == actual,
            "an already straight corrected disc retains its absolute coefficients on the next pass");
    }

    private static void TestSupportGuardsAndFixedCoefficients()
    {
        var p = Parameters() with { PitchLinear = .02, PitchQuadratic = -.03, PitchCubic = .025 };
        var actual = p with { InnerRadiusMm = 24.0017, OuterRadiusMm = 57.9989 };
        var target = CalibrationTarget.Create(p);
        var mapping = CalibrationCrossPrediction.CreateMapping(target, actual, -.23);
        foreach (int count in new[] { 10, 33 })
        {
            var points = Enumerable.Range(0, count).Select(i => mapping(24.2 + 33.6 * i / (count - 1.0))).ToArray();
            var fit = CalibrationCrossFitter.Fit(target, [new(0, points)]);
            Check(fit.Succeeded && fit.RmsErrorMm < 2e-5, "supplied nonconstant coefficients support ordinary radius refinement");
            Check(fit.BestParameters!.PitchLinear == p.PitchLinear
                && fit.BestParameters.PitchQuadratic == p.PitchQuadratic && fit.BestParameters.PitchCubic == p.PitchCubic,
                "exact supplied profile does not invent shape changes");
            if (count < 12) Check(fit.Warnings.Any(w => w.Contains("至少 12", StringComparison.Ordinal)), "sparse cubic support warning");
        }
        var narrow = Enumerable.Range(0, 20).Select(i => mapping(31 + 9.5 * i / 19.0)).ToArray();
        var narrowFit = CalibrationCrossFitter.Fit(target, [new(0, narrow)]);
        Check(narrowFit.Succeeded && narrowFit.Warnings.Any(w => w.Contains("60%", StringComparison.Ordinal)),
            "adequate point count cannot replace radial coverage");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => CalibrationCrossFitter.Fit(target, [new(0, narrow)],
            cancellationToken: cancellation.Token), "cubic fitting cancellation");
    }

    private static void TestPersistenceAndValidation()
    {
        var p = Parameters() with { PitchLinear = .2, PitchQuadratic = -.3, PitchCubic = .2 };
        var options = new CalibrationFitOptions(SearchCenter: p, FitDvdPitch: false, PitchCoefficientRange: .12);
        var session = new CalibrationSession(CalibrationTarget.Create(p), [], options);
        var roundTrip = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(session));
        Check(roundTrip.Target.Parameters == p && roundTrip.FitOptions == options, "all fitted coefficients and search settings persist");
        var legacy = JsonNode.Parse(CalibrationSessionJson.Serialize(new(CalibrationTarget.Create(Parameters()), [])))!;
        var savedParameters = legacy["target"]!["parameters"]!.AsObject();
        savedParameters.Remove("pitchLinear"); savedParameters.Remove("pitchQuadratic"); savedParameters.Remove("pitchCubic");
        var restored = CalibrationSessionJson.Deserialize(legacy.ToJsonString());
        Check(restored.Target.Parameters == Parameters(), "older sessions default to constant pitch");
        Throws<ArgumentException>(() => (p with { PitchLinear = double.NaN }).Validate(), "nonfinite coefficients rejected");
        Throws<ArgumentException>(() => (p with { PitchLinear = -5, PitchQuadratic = 0, PitchCubic = 0 }).Validate(), "negative interior pitch rejected");
        foreach (var medium in MediaParameters())
        {
            var profile = medium with { PitchLinear = p.PitchLinear, PitchQuadratic = p.PitchQuadratic, PitchCubic = p.PitchCubic };
            profile.Validate();
            var copy = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(CalibrationTarget.Create(profile), [])));
            Check(copy.Target.Parameters == profile, "all media preserve pitch coefficients in calibration records");
        }
    }

    // Independent Simpson integration and bracket inversion, deliberately unrelated
    // to production interpolation tables and Newton inversion.
    private sealed class ReferenceGeometry
    {
        private readonly CalibrationParameters _p;
        private readonly double _b;
        internal ReferenceGeometry(CalibrationParameters p)
        {
            _p = p;
            if (UsesAreaFormula)
            {
                _b = (p.OuterRadiusMm * p.OuterRadiusMm - p.InnerRadiusMm * p.InnerRadiusMm) / (2 * p.TrackLengthMm);
                return;
            }
            double low = 1e-12, high = 100;
            for (int i = 0; i < 80; i++)
            {
                double mid = (low + high) / 2;
                if (Arc(p.InnerRadiusMm, p.OuterRadiusMm, mid) > p.TrackLengthMm) low = mid; else high = mid;
            }
            _b = (low + high) / 2;
        }
        private double Shape(double r)
        {
            double x = (r - _p.InnerRadiusMm) / (_p.OuterRadiusMm - _p.InnerRadiusMm);
            return 1 + _p.PitchLinear * x + _p.PitchQuadratic * x * x + _p.PitchCubic * x * x * x;
        }
        private double Arc(double first, double last, double b)
            => Integrate(first, last, r => Math.Sqrt(1 + Math.Pow(r / (b * Shape(r)), 2)));
        private bool UsesAreaFormula => _p.Kind == CalibrationDiscKind.Cd
            && _p.PitchLinear == 0 && _p.PitchQuadratic == 0 && _p.PitchCubic == 0;
        internal double ArcAtRadius(double r) => UsesAreaFormula
            ? (r * r - _p.InnerRadiusMm * _p.InnerRadiusMm) / (2 * _b) : Arc(_p.InnerRadiusMm, r, _b);
        internal double AngleAtRadius(double r) => Integrate(_p.InnerRadiusMm, r, r => 1 / (_b * Shape(r)));
        internal double RadiusAtArc(double arc)
        {
            double low = 1e-6, high = 120;
            for (int i = 0; i < 55; i++)
            {
                double mid = (low + high) / 2;
                if (ArcAtRadius(mid) < arc) low = mid; else high = mid;
            }
            return (low + high) / 2;
        }
        private static double Integrate(double first, double last, Func<double, double> value)
        {
            const int count = 1024;
            double h = (last - first) / count, sum = value(first) + value(last);
            for (int i = 1; i < count; i++) sum += (i % 2 == 0 ? 2 : 4) * value(first + i * h);
            return sum * h / 3;
        }
    }

    private static CalibrationPoint Polar(double r, double a) => new(r * Math.Cos(a), r * Math.Sin(a));
    private static double Distance(CalibrationPoint a, CalibrationPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static double DistanceToPolyline(CalibrationPoint point, IReadOnlyList<CalibrationPoint> line)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < line.Count; i++)
        {
            double dx = line[i].X - line[i - 1].X, dy = line[i].Y - line[i - 1].Y;
            double t = Math.Clamp(((point.X - line[i - 1].X) * dx + (point.Y - line[i - 1].Y) * dy) / Math.Max(1e-30, dx * dx + dy * dy), 0, 1);
            best = Math.Min(best, Distance(point, new(line[i - 1].X + t * dx, line[i - 1].Y + t * dy)));
        }
        return best;
    }
    private static void Near(double expected, double actual, double tolerance, string message)
        => Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected:G17}, actual {actual:G17}");
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("calibration-cubic-pitch: " + message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("calibration-cubic-pitch: expected " + typeof(T).Name + "; " + message); }
}
