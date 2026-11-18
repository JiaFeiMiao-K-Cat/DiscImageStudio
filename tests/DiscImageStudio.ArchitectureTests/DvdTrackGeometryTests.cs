using DiscImageStudio.Core;

internal static class DvdTrackGeometryTests
{
    private const double Inner = 24;
    private const double Outer = 58;
    private const double Length = 2_295_104d * 38_688 * 133.3e-6;

    internal static void Run()
    {
        TestCreateRejectsInvalidGeometry();
        TestArcLengthRange();
        TestNominalDvdPitch();
        foreach (var (linear, quadratic, cubic) in new[] { (0.0, 0.0, 0.0), (0.18, -0.12, 0.06), (-0.4, 0.3, 0.2) })
        {
            DvdTrackGeometry geometry = DvdTrackGeometry.Create(Inner, Outer, Length, linear, quadratic, cubic);
            string label = geometry.IsConstantPitch ? "constant pitch" : $"cubic ({linear}, {quadratic}, {cubic})";
            TestEndpoints(geometry, label);
            TestMonotonicRoundTrip(geometry, label);
            TestExtendedProgressRoundTrip(geometry, label);
        }
        TestExtendedRejectsNonpositivePitch();
        TestShortConstantSpiral();
        Console.WriteLine("dvd-track-geometry: passed (argument validation, endpoints, monotonic round trips, extended progress and short spirals)");
    }

    private static void TestCreateRejectsInvalidGeometry()
    {
        Throws(() => DvdTrackGeometry.Create(0, Outer, Length), "zero inner radius");
        Throws(() => DvdTrackGeometry.Create(-1, Outer, Length), "negative inner radius");
        Throws(() => DvdTrackGeometry.Create(double.NaN, Outer, Length), "NaN inner radius");
        Throws(() => DvdTrackGeometry.Create(Inner, Inner, Length), "outer radius equal to inner radius");
        Throws(() => DvdTrackGeometry.Create(Inner, Inner - 1, Length), "outer radius below inner radius");
        Throws(() => DvdTrackGeometry.Create(Inner, double.PositiveInfinity, Length), "infinite outer radius");
        // A spiral cannot be shorter than the straight radial path between its ends.
        Throws(() => DvdTrackGeometry.Create(Inner, Outer, Outer - Inner), "track length equal to radial distance");
        Throws(() => DvdTrackGeometry.Create(Inner, Outer, double.NaN), "NaN track length");
        Throws(() => DvdTrackGeometry.Create(Inner, Outer, double.PositiveInfinity), "infinite track length");
        Throws(() => DvdTrackGeometry.ValidatePitchCoefficients(-1, 0, 0), "pitch reaching zero at the outer edge");
        Throws(() => DvdTrackGeometry.ValidatePitchCoefficients(0, 0, double.PositiveInfinity), "infinite cubic pitch");
        DvdTrackGeometry.ValidatePitchCoefficients(-0.9, 0, 0);
    }

    private static void TestArcLengthRange()
    {
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(Inner, Outer, Length);
        Throws(() => geometry.RadiusAtArcLength(-1e-9), "negative arc length");
        Throws(() => geometry.RadiusAtArcLength(Length * (1 + 1e-12)), "arc length beyond the track");
        Throws(() => geometry.RadiusAtArcLength(double.NaN), "NaN arc length");
        Require(!geometry.TryProgressAtRadiusExtended(0, out _), "zero radius has no extended progress");
        Require(!geometry.TryProgressAtRadiusExtended(-1, out _), "negative radius has no extended progress");
        Require(!geometry.TryProgressAtRadiusExtended(double.NaN, out _), "NaN radius has no extended progress");
        Require(!geometry.TryAtProgressExtended(double.NaN, out _), "NaN progress has no point");
        Require(!geometry.TryAtProgressExtended(double.PositiveInfinity, out _), "infinite progress has no point");
    }

    private static void TestNominalDvdPitch()
    {
        // A single-layer DVD has a nominal 0.74 µm track pitch.
        double pitchMicrometres = DvdTrackGeometry.Create(Inner, Outer, Length).TrackPitchMm * 1000;
        Require(pitchMicrometres > 0.735 && pitchMicrometres < 0.745,
            $"DVD-5 geometry solves to a 0.74 µm pitch, actual {pitchMicrometres:R}");
    }

    private static void TestEndpoints(DvdTrackGeometry geometry, string label)
    {
        Near(Inner, geometry.RadiusAtArcLength(0), 1e-12, $"{label}: track starts at the inner radius");
        Near(Outer, geometry.RadiusAtArcLength(Length), 1e-9, $"{label}: track ends at the outer radius");
        Near(0, geometry.ArcLengthAtRadius(Inner), 1e-6, $"{label}: inner radius has zero arc length");
        Near(Length, geometry.ArcLengthAtRadius(Outer), Length * 1e-12, $"{label}: outer radius has the full arc length");
        Near(0, geometry.AngleAtRadius(Inner), 1e-12, $"{label}: inner radius has zero angle");
        Near(geometry.TotalAngleRadians, geometry.AngleAtRadius(Outer), geometry.TotalAngleRadians * 1e-12,
            $"{label}: outer radius has the total angle");
        Near(0, geometry.ArcLengthAtAngle(-1), 1e-6, $"{label}: negative angles clamp to the start");
        Near(Length, geometry.ArcLengthAtAngle(geometry.TotalAngleRadians + 1), Length * 1e-12,
            $"{label}: angles beyond the end clamp to the track length");
        var start = geometry.AtArcLengthFast(0);
        var end = geometry.AtArcLengthFast(Length);
        Near(Inner, start.Radius, 1e-12, $"{label}: fast start radius");
        Near(Outer, end.Radius, 1e-9, $"{label}: fast end radius");
        Near(geometry.TotalAngleRadians, end.Angle, geometry.TotalAngleRadians * 1e-12, $"{label}: fast end angle");
    }

    private static void TestMonotonicRoundTrip(DvdTrackGeometry geometry, string label)
    {
        double previousRadius = double.NegativeInfinity, previousAngle = double.NegativeInfinity;
        for (int index = 0; index <= 1000; index++)
        {
            double arc = Length * index / 1000;
            double radius = geometry.RadiusAtArcLength(arc);
            double angle = geometry.AngleAtArcLength(arc);
            Require(radius > previousRadius && angle > previousAngle, $"{label}: radius and angle increase along the track at {arc:R}");
            previousRadius = radius;
            previousAngle = angle;

            Near(arc, geometry.ArcLengthAtRadius(radius), Length * 1e-11, $"{label}: arc → radius → arc at {arc:R}");
            Near(arc, geometry.ArcLengthAtAngle(angle), Length * 1e-11, $"{label}: arc → angle → arc at {arc:R}");
            Near(radius, geometry.RadiusAtArcLengthFast(arc), 1e-9, $"{label}: fast radius agrees at {arc:R}");
            var fast = geometry.AtArcLengthFast(arc);
            Near(radius, fast.Radius, 1e-9, $"{label}: combined fast radius agrees at {arc:R}");
            Near(angle, fast.Angle, 1e-6, $"{label}: combined fast angle agrees at {arc:R}");
        }
    }

    private static void TestExtendedProgressRoundTrip(DvdTrackGeometry geometry, string label)
    {
        // The inner band holds about a fifth of a DVD's track length, so
        // half a track inward would cross the centre and has no point.
        Require(!geometry.TryAtProgressExtended(-0.5, out _), $"{label}: progress past the disc centre is rejected");
        foreach (double progress in new[] { -0.1, -0.05, 0, 0.3, 0.5, 1, 1.05, 1.3 })
        {
            Require(geometry.TryAtProgressExtended(progress, out var point), $"{label}: progress {progress} has a point");
            Require(progress < 0 ? point.Radius < Inner : progress > 1 ? point.Radius > Outer : point.Radius >= Inner && point.Radius <= Outer,
                $"{label}: progress {progress} lands on the matching side of the recorded band, radius {point.Radius:R}");
            Require(geometry.TryProgressAtRadiusExtended(point.Radius, out double roundTrip),
                $"{label}: radius {point.Radius:R} has a progress");
            Near(progress, roundTrip, 1e-11, $"{label}: progress → radius → progress at {progress}");
        }

        // Extending past either edge continues the spiral without a jump.
        const double step = 1e-9;
        Require(geometry.TryAtProgressExtended(1 + step, out var outside), $"{label}: point just beyond the outer edge");
        Require(geometry.TryAtProgressExtended(1, out var edge), $"{label}: point at the outer edge");
        Near(edge.Radius, outside.Radius, 1e-3, $"{label}: radius is continuous at the outer edge");
        Near(edge.Angle, outside.Angle, 1e-1, $"{label}: angle is continuous at the outer edge");
        Require(geometry.TryAtProgressExtended(-step, out var before), $"{label}: point just inside the start");
        Near(Inner, before.Radius, 1e-3, $"{label}: radius is continuous at the inner edge");
    }

    private static void TestExtendedRejectsNonpositivePitch()
    {
        // q(x) = 1 - 0.9x is positive across the recorded band but reaches zero
        // at x = 1/0.9, so the spiral cannot be extended that far outward.
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(Inner, Outer, Length, -0.9);
        double range = Outer - Inner;
        Require(geometry.TryProgressAtRadiusExtended(Inner + 1.05 * range, out double reachable) && reachable > 1,
            "the outward extension is valid while the pitch stays positive");
        Require(!geometry.TryProgressAtRadiusExtended(Inner + 1.2 * range, out _),
            "the outward extension stops where the pitch would become nonpositive");
        Require(geometry.TryProgressAtRadiusExtended(Inner / 2, out double inward) && inward < 0,
            "the inward extension, where the pitch grows, remains valid");
    }

    private static void TestShortConstantSpiral()
    {
        // A spiral only slightly longer than its radial width completes a small
        // fraction of a turn, so the solved growth exceeds the outer radius.
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(1, 2, 1.01);
        Require(geometry.IsConstantPitch && geometry.RadialGrowthPerRadianMm > 2,
            $"a short spiral solves to a growth beyond the outer radius, actual {geometry.RadialGrowthPerRadianMm:R}");
        Require(geometry.TotalAngleRadians < Math.PI / 2, "a short spiral turns less than a quarter revolution");
        TestEndpointsAndRoundTrip(geometry, "short spiral");
    }

    private static void TestEndpointsAndRoundTrip(DvdTrackGeometry geometry, string label)
    {
        Near(geometry.InnerRadiusMm, geometry.RadiusAtArcLength(0), 1e-12, $"{label}: start radius");
        Near(geometry.OuterRadiusMm, geometry.RadiusAtArcLength(geometry.TrackLengthMm), 1e-9, $"{label}: end radius");
        for (int index = 0; index <= 100; index++)
        {
            double arc = geometry.TrackLengthMm * index / 100;
            Near(arc, geometry.ArcLengthAtRadius(geometry.RadiusAtArcLength(arc)), 1e-9, $"{label}: arc round trip at {arc:R}");
        }
    }

    private static void Throws(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(label + ": expected rejection");
    }

    private static void Near(double expected, double actual, double tolerance, string label)
        => Require(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"{label}: expected {expected:R}, actual {actual:R}, tolerance {tolerance:R}");

    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }
}
