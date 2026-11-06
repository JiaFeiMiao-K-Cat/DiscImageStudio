using System.Numerics;

namespace DiscImageStudio.Core.Calibration;

public enum CalibrationAngleSweepStatus { InsufficientEvidence, ConflictingStations, Success }

public sealed record CalibrationAngleSweepResult(CalibrationAngleSweepStatus Status, CalibrationParameters? Corrected,
    int StationCount, double CoverageDegrees)
{
    public bool Succeeded => Corrected is not null;
    /// <summary>At each of the three measured lock points: extra physical degrees per nominal turn,
    /// relative to the fixed-pitch geometry used when the target was generated.</summary>
    public IReadOnlyList<double> DegreesPerTurnOffset { get; init; } = [];
}

/// <summary>Locate the radial turning point of each chirped angular pilot in a rectified RGB photo.
/// Photo coordinates are millimetres relative to the disc centre; RGB components are in [0,1].
/// Both sides of each dark band register its local photo coordinate to the burned addresses.
/// No absolute angular orientation, fitted camera scale or particular media preset is required.</summary>
public static partial class CalibrationAngleSweepReader
{
    private const int Rays = 17;
    private const double Step = .05;
    private sealed record Observation(double Progress, double Radius, double AngularGain);
    private sealed record Reading(double Inner, double Outer, double Angle, double[] DegreesPerTurn);

    public static CalibrationAngleSweepResult Read(CalibrationTarget target,
        IReadOnlyList<CalibrationRingObservation> observations, double canvasRadiusMm,
        Func<double, double, Vector3> samplePhoto, double? preferredAngleDegrees = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(samplePhoto);
        token.ThrowIfCancellationRequested();
        if (!target.HasAngleSweep || observations.Count != 6 || !double.IsFinite(canvasRadiusMm) || canvasRadiusMm <= 7.5)
            return new(CalibrationAngleSweepStatus.InsufficientEvidence, null, 0, 0);
        var radial = CalibrationRingFitter.Fit(target, observations, token);
        if (!radial.Succeeded || radial.BestParameters is not { } apparent)
            return new(CalibrationAngleSweepStatus.InsufficientEvidence, null, 0, 0);
        var radii = observations.OrderBy(p => p.RingIndex).Select(p => p.RadiusMm).ToArray();
        var firstPoint = observations.SelectMany(o => o.Points ?? []).FirstOrDefault();
        double first = preferredAngleDegrees ?? (firstPoint is { } p ? Math.Atan2(p.Y, p.X) * 180 / Math.PI : 0);
        if (!double.IsFinite(first)) first = 0;
        int maxStations = 0;
        bool inconsistent = false;
        var readings = new List<Reading>();
        var nominal = new CalibrationGeometry(target.ParametersForGeometry(target.Parameters));
        var spanSquared = apparent.OuterRadiusMm * apparent.OuterRadiusMm - apparent.InnerRadiusMm * apparent.InnerRadiusMm;
        for (int sector = 0; sector < 16; sector++)
        {
            double angle = (first + sector * 22.5 + 360) % 360;
            var directions = Enumerable.Range(0, Rays).Select(i =>
                Math.SinCos((angle + (i / (Rays - 1d) - .5) * 45) * Math.PI / 180)).ToArray();
            var points = new List<Observation>(3);
            for (int station = 0; station < target.AngleSweepStations.Count; station++)
            {
                token.ThrowIfCancellationRequested();
                var pilot = target.AngleSweepStations[station];
                var band = target.WideBandProgress(station);
                double? inner = FindEdge(radii[station], directions, samplePhoto);
                double? outer = FindEdge(radii[station + 3], directions, samplePhoto);
                if (inner is not double ri || outer is not double ro || ro <= ri) continue;
                double slope = (ro * ro - ri * ri) / (band.EndExclusive - band.Start);
                double intercept = ri * ri - slope * band.Start;
                var anchor = Locate(pilot, intercept, slope, spanSquared, directions, samplePhoto,
                    target.Parameters.TrackLengthMm, token);
                if (anchor is not null) points.Add(anchor);
            }
            maxStations = Math.Max(maxStations, points.Count);
            // Rotation can hide the angular slope of one pilot in a narrow viewing window.
            // At least one station must resolve it, and collectively they must favor the
            // angular template over a pattern with exactly the same radius on every ray.
            if (points.Count != 3 || !points.Any(p => p.AngularGain > .15) || points.Sum(p => p.AngularGain) < .15) continue;
            // Use the middle station exclusively as a cross-check before it joins the final fit.
            var left = points[0];
            var middle = points[1];
            var right = points[2];
            double EndSquare(double q) => Square(left.Radius) + (q - left.Progress) / (right.Progress - left.Progress)
                * (Square(right.Radius) - Square(left.Radius));
            double check = Math.Sqrt(EndSquare(middle.Progress));
            if (!double.IsFinite(check) || Math.Abs(check - middle.Radius) > .050)
            {
                inconsistent = true;
                continue;
            }
            double meanProgress = points.Average(a => a.Progress);
            double meanSquare = points.Average(a => Square(a.Radius));
            double slopeFit = points.Sum(a => (a.Progress - meanProgress) * (Square(a.Radius) - meanSquare))
                / points.Sum(a => Square(a.Progress - meanProgress));
            double riFit = Math.Sqrt(meanSquare - meanProgress * slopeFit);
            double roFit = Math.Sqrt(meanSquare + (1 - meanProgress) * slopeFit);
            if (!double.IsFinite(riFit) || !double.IsFinite(roFit) || riFit < 7.5 || roFit <= riFit
                || roFit > canvasRadiusMm || roFit > 60) continue;
            double[] degreesPerTurn = points.Select(point =>
                360 * (nominal.AtProgress(point.Progress).Radius / point.Radius - 1)).ToArray();
            readings.Add(new(riFit, roFit, angle, degreesPerTurn));
        }
        if (readings.Count == 0)
            return new(inconsistent ? CalibrationAngleSweepStatus.ConflictingStations : CalibrationAngleSweepStatus.InsufficientEvidence,
                null, maxStations, 0);
        if (readings.Max(p => p.Inner) - readings.Min(p => p.Inner) > .12
            || readings.Max(p => p.Outer) - readings.Min(p => p.Outer) > .12)
            return new(CalibrationAngleSweepStatus.ConflictingStations, null, maxStations, 0);
        int slices = Enumerable.Range(0, 16).Count(i => readings.Any(p =>
            Math.Abs(Math.IEEERemainder(first + i * 22.5 + 11.25 - p.Angle, 360)) < 22.5));
        var corrected = target.Parameters with
        {
            InnerRadiusMm = Median(readings.Select(s => s.Inner)),
            OuterRadiusMm = Median(readings.Select(s => s.Outer))
        };
        return new(CalibrationAngleSweepStatus.Success, corrected, 3, 22.5 * slices)
        {
            DegreesPerTurnOffset = Array.AsReadOnly(Enumerable.Range(0, 3)
                .Select(station => Median(readings.Select(r => r.DegreesPerTurn[station]))).ToArray())
        };
    }

    private static double Square(double a) => a * a;
    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    // Refine each band boundary locally. A wrong physical centre can move the edge to
    // different radii on opposite sides; carrying the original six radii across azimuths
    // would turn this directly into a false angular correction.
    private static double? FindEdge(double nominal, (double Sin, double Cos)[] directions,
        Func<double, double, Vector3> sample)
    {
        var found = new List<double>(Rays);
        foreach (var (sin, cos) in directions)
        {
            double peak = 0, radius = nominal;
            for (int i = -14; i <= 14; i++)
            {
                double r = nominal + .05 * i;
                double strength = Vector3.DistanceSquared(sample((r - .13) * cos, (r - .13) * sin), sample((r + .13) * cos, (r + .13) * sin));
                if (strength > peak)
                {
                    peak = strength;
                    radius = r;
                }
            }
            if (peak > Square(.012) && Math.Abs(radius - nominal) < .69) found.Add(radius);
        }
        if (found.Count < (Rays + 1) / 2) return null;
        double result = Median(found);
        return Median(found.Select(r => Math.Abs(r - result))) <= .18 ? result : null;
    }

    private static Observation? Locate(CalibrationAngleSweepStation station, double intercept, double slope, double slopeEstimate,
        (double Sin, double Cos)[] directions, Func<double, double, Vector3> sample, double length, CancellationToken token)
    {
        // The guard strip and the chirp have different mean reflectances. Exclude
        // their blurred transition so it cannot be mistaken for the turning point.
        const double edgeMarginMm = .48;
        double lo = Math.Sqrt(intercept + slope * station.StartProgress) + edgeMarginMm;
        double hi = Math.Sqrt(intercept + slope * station.EndProgress) - edgeMarginMm;
        int n = (int)((hi - lo) / Step) + 1;
        if (!double.IsFinite(lo) || !double.IsFinite(hi) || n < 38 || n > 500) return null;
        var trace = ExtractPhoto(lo, n, directions, sample);
        if (trace is null) return null;
        var candidates = new List<(double Radius, Score Match)>();
        double sweptSlope = (Square(station.EndVirtualRadiusMm) - Square(station.StartVirtualRadiusMm))
            / (station.EndProgress - station.StartProgress);
        for (double center = lo + .10; center < hi - .10; center += .025)
        {
            token.ThrowIfCancellationRequested();
            double q = (Square(center) - intercept) / slope;
            double v = station.VirtualRadiusAt(q);
            // At lock phi' is zero. phi'' in photo millimetres follows from
            // d(theta)/dq=L/r and the two squared-radius slopes.
            double curvature = length * (slopeEstimate - sweptSlope) / (2 * v * v * v) * Square(2 * center / slope);
            var match = BestTemplate(trace, n, lo - center, curvature);
            candidates.Add((center, match));
        }
        if (candidates.Count == 0) return null;
        var best = candidates.MaxBy(s => s.Match.Correlation);
        // Weak, clipped and ambiguous matches cannot supply an independent address.
        // Amplitude .004 is approximately one digital level on an 8-bit photograph.
        if (best.Match.Correlation < .24 || best.Match.AngularGain < -.60 || best.Match.Amplitude < .004
            || best.Radius < lo + .16 || best.Radius > hi - .16) return null;
        if (candidates.Any(s => Math.Abs(s.Radius - best.Radius) > .27 && s.Match.Correlation > best.Match.Correlation * .94))
            return null;
        double lockProgress = (Square(best.Radius) - intercept) / slope;
        return new(lockProgress, station.VirtualRadiusAt(lockProgress), best.Match.AngularGain);
    }
}
