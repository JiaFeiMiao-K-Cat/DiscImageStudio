using System.Numerics;
using DiscImageStudio.Core.Calibration;

internal static class CalibrationClockReaderTests
{
    internal static void Run()
    {
        var initial = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200);
        var target = CalibrationTarget.Create(initial, artwork: CalibrationArtwork.ClockRuler);
        var first = new OpticalModel(25.10, 57.35);
        var second = new OpticalModel(25.12, 57.368);
        var incompatible = new OpticalModel(25.36, 57.62);

        // A single readable window wraps across zero. With more illuminated surface the
        // device may obtain several overlapping readings as well as a distant observation.
        var single = Read(null, firstWidth: 44.75, preferred: 355);
        Check(single.Succeeded && single.Sectors.Count == 1 && Math.Abs(single.CoverageDegrees - 45) < 1e-7
            && Math.Abs(single.Corrected!.InnerRadiusMm - 25.10) < .03
            && Math.Abs(single.Corrected.OuterRadiusMm - 57.35) < .03,
            $"Single clock window: {single.Status}, {single.Sectors.Count} windows / {single.CoverageDegrees}°, {single.Corrected}.");
        var combined = Read(second, firstWidth: 67.5);
        Check(combined.Succeeded && combined.Sectors.Count >= 3 && combined.Sectors.Any(s =>
            Math.Abs(Math.IEEERemainder(s.CenterDegrees - 130, 360)) < 25), "A disjoint clock region was lost.");
        Check(combined.Sectors.Any(s => combined.Sectors.Any(other => other != s &&
            Math.Abs(Math.IEEERemainder(s.CenterDegrees - other.CenterDegrees, 360)) < 44.9)),
            "The extra visible part of a partially overlapping window was discarded.");
        VerifyCoverage(combined);
        var conflict = Read(incompatible, firstWidth: 67.5);
        Check(conflict.Status == CalibrationClockStatus.ConflictingSectors && conflict.Corrected is null,
            "Contradictory independent regions produced a usable calibration.");
        var noPilot = Read(null, false);
        Check(!noPilot.Succeeded && noPilot.Status == CalibrationClockStatus.InsufficientEvidence, "Address bands alone passed as a clock.");
        var previous = CalibrationTarget.Create(initial, artwork: CalibrationArtwork.FineCross);
        Check(!CalibrationClockReader.Read(previous, 63, (_, _) => throw new InvalidOperationException()).Succeeded,
            "A fine cross target was read as a clock target.");

        using var cancel = new CancellationTokenSource();
        int sampleCount = 0;
        try
        {
            CalibrationClockReader.Read(target, 63, (_, _) =>
            {
                if (++sampleCount == 500) cancel.Cancel();
                return Vector3.One;
            }, token: cancel.Token);
            throw new InvalidOperationException("Clock photo reading ignored cancellation.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }

        CalibrationClockResult Read(OpticalModel? other, bool includePilot = true, double firstWidth = 45, double preferred = 6.25) =>
            CalibrationClockReader.Read(target, 63, (x, y) =>
            {
                double r = Math.Sqrt(x * x + y * y), phi = Math.Atan2(y, x), angle = phi * 180 / Math.PI;
                var model = Math.Abs(Math.IEEERemainder(angle - 355, 360)) <= firstWidth / 2 ? first :
                    Math.Abs(angle - 130) <= 22.5 ? other : null;
                return model?.Sample(r, phi, includePilot) ?? new Vector3(.5f);
            }, preferred);
    }

    private static void VerifyCoverage(CalibrationClockResult result)
    {
        // The fixture places every boundary on the 0.25° grid. Independently integrate on
        // cell midpoints around the seam, assigning one vote per direction rather than per window.
        var weights = new double[result.Sectors.Count];
        double union = 0;
        for (double a = .125; a < 360; a += .25)
        {
            var covering = Enumerable.Range(0, weights.Length).Where(i =>
                Math.Abs(Math.IEEERemainder(a - result.Sectors[i].CenterDegrees, 360)) < 22.5).ToArray();
            if (covering.Length == 0) continue;
            union += .25;
            foreach (int i in covering) weights[i] += .25 / covering.Length;
        }
        Check(Math.Abs(result.CoverageDegrees - union) < 1e-7 && union < weights.Length * 45,
            "Overlapping azimuth was counted more than once.");
        Check(weights.Select((w, i) => Math.Abs(w - result.Sectors[i].ContributionDegrees)).All(d => d < 1e-7),
            "Readings were given incorrect angular weights.");
        double inner = result.Sectors.Select((s, i) => weights[i] * s.InnerRadiusMm).Sum() / union;
        double outer = result.Sectors.Select((s, i) => weights[i] * s.OuterRadiusMm).Sum() / union;
        Check(Math.Abs(inner - result.Corrected!.InnerRadiusMm) < 1e-8 && Math.Abs(outer - result.Corrected.OuterRadiusMm) < 1e-8,
            "Additional angular coverage did not contribute its proportional weight.");
    }

    // Idealized optical image of the address plateaux and the narrow stationary clock
    // responses, generated independently of the reader and sampled in rectified mm.
    private sealed class OpticalModel
    {
        private readonly (double Background, double Pilot, int Order)[] profile = new (double, double, int)[16000];

        public OpticalModel(double inner, double outer)
        {
            const double lambda = 14, scale = .994, localOffset = .08;
            double squaredRange = outer * outer - inner * inner;
            for (int i = 0; i < profile.Length; i++)
            {
                double r = (i * .004 - localOffset) / scale;
                double background = .5, unmasked = 1;
                foreach (double q in new[] { .05, .18, .35, .55, .74, .92 })
                {
                    double Edge(double dq) => Math.Sqrt(inner * inner + (q + dq) * squaredRange);
                    double Step(double radius) => .5 + .5 * Math.Tanh((r - radius) / .11);
                    double band = Step(Edge(-.006)) - Step(Edge(.006));
                    double wide = Step(Edge(-.0095)) - Step(Edge(.0095));
                    background -= band - .5 * wide;
                    unmasked -= wide;
                }
                int m = (int)Math.Round(r * Math.Tau / lambda);
                double delta = r - m * lambda / Math.Tau;
                double pilot = r > inner && r < outer ? .14 * unmasked * Math.Exp(-.5 * delta * delta / (.095 * .095)) : 0;
                profile[i] = (background, pilot, m);
            }
        }

        public Vector3 Sample(double r, double angle, bool includePilot)
        {
            var (baseLevel, pilot, m) = profile[(int)Math.Round(r / .004)];
            if (includePilot) baseLevel += pilot * Math.Cos(m * angle + .37 * m);
            // The three channels have different optical response, including a polarity reversal.
            return new Vector3((float)(.85 * baseLevel), (float)(.66 - .62 * baseLevel), (float)(.33 * baseLevel));
        }
    }

    private static void Check(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }
}
