using System.Runtime.Intrinsics;
using DiscImageStudio.Core;

namespace DvdImageSolver.Solver;

// Existing callers keep this name; cubic coefficients enable a varying radial pitch.
internal sealed class ArchimedeanSpiral(DvdTrackGeometry geometry)
{
    internal double TrackLengthMm => geometry.TrackLengthMm;
    internal bool IsConstantPitch => geometry.IsConstantPitch;
    internal double InnerRadiusMm => geometry.InnerRadiusMm;
    internal double OuterRadiusMm => geometry.OuterRadiusMm;
    internal double RadialGrowthPerRadianMm => geometry.RadialGrowthPerRadianMm;
    internal double TrackPitchMm => geometry.TrackPitchMm;
    internal double TotalAngleRadians => geometry.TotalAngleRadians;
    internal static ArchimedeanSpiral Create(double innerRadiusMm, double outerRadiusMm,
        double trackLengthMm, double pitchLinear = 0, double pitchQuadratic = 0, double pitchCubic = 0)
        => new(DvdTrackGeometry.Create(innerRadiusMm, outerRadiusMm, trackLengthMm,
            pitchLinear, pitchQuadratic, pitchCubic));
    internal double RadiusAtArcLength(double arcLengthMm) => geometry.RadiusAtArcLength(arcLengthMm);
    internal double RadiusAtArcLengthFast(double arcLengthMm) => geometry.RadiusAtArcLengthFast(arcLengthMm);
    internal double AngleAtArcLength(double arcLengthMm) => geometry.AngleAtArcLength(arcLengthMm);
    internal (double Radius, double Angle) AtArcLengthFast(double arcLengthMm) => geometry.AtArcLengthFast(arcLengthMm);
    internal (Vector256<double> Radius, Vector256<double> Angle) AtArcLengthFast(
        Vector256<double> arcLengthsMm) => geometry.AtArcLengthFast(arcLengthsMm);
    internal double AngleAtRadius(double radiusMm) => geometry.AngleAtRadius(radiusMm);
    internal double ArcLengthAtRadius(double radiusMm) => geometry.ArcLengthAtRadius(radiusMm);
    internal double ArcLengthAtAngle(double angleRadians) => geometry.ArcLengthAtAngle(angleRadians);
    internal double RadialGrowthAtRadius(double radiusMm) => geometry.RadialGrowthAtRadius(radiusMm);

    internal bool TryGetSampleReuseBounds(double startAngle, out double positionMargin,
        out double tangentError, out double accelerationBound)
    {
        positionMargin = 0;
        tangentError = accelerationBound = 0;
        double inner = InnerRadiusMm, outer = OuterRadiusMm, growth = RadialGrowthPerRadianMm;
        if (inner < 10 || growth is < 1e-6 or > 1e-3
            || !double.IsFinite(startAngle) || Math.Abs(startAngle) + TotalAngleRadians > 1_000_000)
            return false;
        const double epsilon = 2.2204460492503131e-16;
        if (!IsConstantPitch)
        {
            if (!geometry.TryGetFastSamplingBounds(out positionMargin, out tangentError, out accelerationBound))
                return false;
            positionMargin += 512 * epsilon * outer * (1 + Math.Abs(startAngle));
        }
        else
        {
            // The thin-spiral seed omits at most b/2*log(ro/ri) of arc length,
            // hence its radial error is <= b²/(2ri)*log(ro/ri). One Newton step
            // leaves <= error²/(2ri), since max F''/min F' <= 1/ri. Convert
            // radial error to position error through theta=(r-ri)/b.
            double seedError = growth * growth / (2 * inner) * Math.Log(outer / inner);
            double inverseError = seedError * seedError / (2 * inner) * (1 + outer / growth);
            // Covers both independently sampled endpoints: primitive cancellation,
            // radius-to-angle amplification, channel-to-arc conversion and trig.
            positionMargin = 2 * inverseError
                + 8192 * epsilon * outer * outer / growth * Math.Max(1, outer / inner)
                + 256 * epsilon * TrackLengthMm
                + 512 * epsilon * outer * (1 + Math.Abs(startAngle) + TotalAngleRadians) + 1e-8;
            // Relative to the unit circular tangent, radial speed is <= b/ri
            // and tangential speed differs by <= b²/(2ri²). The exact constant
            // spiral's curvature is <= 2/ri everywhere in the recorded annulus.
            tangentError = growth / inner + growth * growth / (2 * inner * inner);
            accelerationBound = 2 / inner;
        }
        return double.IsFinite(positionMargin) && positionMargin < .01
            && double.IsFinite(tangentError) && tangentError >= 0
            && double.IsFinite(accelerationBound) && accelerationBound > 0;
    }
}
