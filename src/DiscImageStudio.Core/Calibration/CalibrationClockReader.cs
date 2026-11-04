using System.Numerics;

namespace DiscImageStudio.Core.Calibration;

public enum CalibrationClockStatus { InsufficientEvidence, ConflictingSectors, Success }

public sealed record CalibrationClockSector(double CenterDegrees, double InnerRadiusMm, double OuterRadiusMm,
    double GateRmsMm, double ClockRmsMm, int OrderCount)
{
    /// <summary>Angular weight in the combined estimate after apportioning shared coverage.</summary>
    public double ContributionDegrees { get; init; }
}

public sealed record CalibrationClockResult(CalibrationClockStatus Status, CalibrationParameters? Corrected,
    IReadOnlyList<CalibrationClockSector> Sectors)
{
    public bool Succeeded => Corrected is not null;
    /// <summary>Union of the accepted 45-degree sampling windows, including the diagnostic
    /// windows on a conflict. This describes coverage of the fits, not a per-pixel visibility mask.</summary>
    public double CoverageDegrees => Math.Min(360, Sectors.Sum(s => s.ContributionDegrees));
}

/// <summary>Read independent 45-degree regions of a rectified CD. RGB samples are normalized to [0,1],
/// at physical canvas coordinates in mm, with positive y down. Each window estimates both endpoints
/// independently, so a clear region never needs supporting evidence from the opposite side.</summary>
public static class CalibrationClockReader
{
    internal const int Rays = 49;
    internal const double StartMm = 21.5, StepMm = .02;
    internal static double Radius(int index) => StartMm + index * StepMm;

    public static CalibrationClockResult Read(CalibrationTarget target, double canvasRadiusMm,
        Func<double, double, Vector3> samplePhoto, double? preferredAngleDegrees = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(samplePhoto);
        token.ThrowIfCancellationRequested();
        if (!target.HasClockRuler || !double.IsFinite(canvasRadiusMm) || canvasRadiusMm < 60)
            return new(CalibrationClockStatus.InsufficientEvidence, null, []);
        double first = preferredAngleDegrees is { } a && double.IsFinite(a) ? ((a % 360) + 360) % 360 : 0;
        var readings = new List<CalibrationClockSector>();
        // Half-overlapping windows cover a clear patch that straddles any one partition boundary.
        // Do not stop after a successful preferred window: another usable region can test the fit.
        for (int i = 0; i < 16; i++)
        {
            token.ThrowIfCancellationRequested();
            double degrees = (first + i * 22.5) % 360;
            var polar = new Vector3[Rays][];
            for (int j = 0; j < Rays; j++)
            {
                token.ThrowIfCancellationRequested();
                double angle = (degrees - 22 + 44.0 * j / (Rays - 1)) * Math.PI / 180;
                var (sin, cos) = Math.SinCos(angle);
                var ray = new Vector3[1916]; // 21.50 through 59.80 mm on a standard 120 mm disc.
                for (int k = 0; k < ray.Length; k++) ray[k] = samplePhoto(Radius(k) * cos, Radius(k) * sin);
                polar[j] = ray;
            }
            var gates = CalibrationClockGates.Find(polar, token);
            if (gates is null) continue;
            var reading = CalibrationClockSectorReader.Read(polar, gates, degrees, target.Parameters, token);
            if (reading is not null) readings.Add(reading);
        }
        return Combine(target.Parameters, readings);
    }

    internal static CalibrationClockResult Combine(CalibrationParameters original, List<CalibrationClockSector> readings)
    {
        if (readings.Count == 0) return new(CalibrationClockStatus.InsufficientEvidence, null, []);
        var weighted = ApportionCoverage(readings);
        // Redundant well-resolved angles must agree before they are averaged. A discrepancy of
        // this size is several times the endpoint spread in the simulated clear-photo controls.
        const double maxDifferenceMm = .035;
        if (readings.Max(v => v.InnerRadiusMm) - readings.Min(v => v.InnerRadiusMm) > maxDifferenceMm
            || readings.Max(v => v.OuterRadiusMm) - readings.Min(v => v.OuterRadiusMm) > maxDifferenceMm)
            return new(CalibrationClockStatus.ConflictingSectors, null, weighted);

        double coverage = weighted.Sum(v => v.ContributionDegrees);
        var corrected = original with
        {
            InnerRadiusMm = weighted.Sum(v => v.InnerRadiusMm * v.ContributionDegrees) / coverage,
            OuterRadiusMm = weighted.Sum(v => v.OuterRadiusMm * v.ContributionDegrees) / coverage
        };
        return new(CalibrationClockStatus.Success, corrected, weighted);
    }

    private static IReadOnlyList<CalibrationClockSector> ApportionCoverage(List<CalibrationClockSector> readings)
    {
        // Within each elementary arc the covering set is constant. Split its length equally
        // among those windows, so new coverage adds weight and shared coverage counts only once.
        var boundaries = new SortedSet<double> { 0, 360 };
        foreach (var sector in readings)
        {
            boundaries.Add((sector.CenterDegrees - 22.5 + 360) % 360);
            boundaries.Add((sector.CenterDegrees + 22.5) % 360);
        }
        double[] weights = new double[readings.Count];
        double start = 0;
        foreach (double end in boundaries)
        {
            double length = end - start, middle = (start + end) / 2;
            start = end;
            if (length < 1e-10) continue;
            var indices = Enumerable.Range(0, readings.Count).Where(i =>
                Math.Abs(Math.IEEERemainder(middle - readings[i].CenterDegrees, 360)) < 22.5).ToArray();
            foreach (int i in indices) weights[i] += length / indices.Length;
        }
        return Array.AsReadOnly(readings.Select((r, i) => r with { ContributionDegrees = weights[i] }).ToArray());
    }

    internal static double Median(Span<double> values)
    {
        values.Sort();
        return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }

    internal static (double Slope, double Offset) Line(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        double sx = 0, sy = 0, xx = 0, xy = 0;
        for (int i = 0; i < x.Length; i++) { sx += x[i]; sy += y[i]; xx += x[i] * x[i]; xy += x[i] * y[i]; }
        double slope = (xy - sx * sy / x.Length) / (xx - sx * sx / x.Length);
        return (slope, (sy - slope * sx) / x.Length);
    }

    // Orthogonal polynomials on an evenly spaced, centered grid. The residual response is
    // invariant to the local background level and slope (and optionally its curvature).
    internal static void Detrend(Span<double> samples, bool quadratic = false)
    {
        int n = samples.Length;
        double middle = (n - 1) / 2.0, meanSquare = (n * (double)n - 1) / 12;
        double sum = 0, slope = 0, curvature = 0, norm = 0;
        for (int i = 0; i < n; i++)
        {
            double x = i - middle, q = x * x - meanSquare;
            sum += samples[i]; slope += samples[i] * x;
            if (quadratic) { curvature += samples[i] * q; norm += q * q; }
        }
        slope /= n * meanSquare;
        curvature = quadratic ? curvature / norm : 0;
        for (int i = 0; i < n; i++)
        {
            double x = i - middle;
            samples[i] -= sum / n + slope * x + curvature * (x * x - meanSquare);
        }
    }

    // Standard normal CDF, maximum absolute error < 8e-8.
    internal static double Cdf(double x)
    {
        double z = Math.Abs(x);
        double t = 1 / (1 + .2316419 * z);
        double tail = .3989422804014327 * Math.Exp(-.5 * z * z) * t *
            (.319381530 + t * (-.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        return x >= 0 ? 1 - tail : tail;
    }

    internal static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
