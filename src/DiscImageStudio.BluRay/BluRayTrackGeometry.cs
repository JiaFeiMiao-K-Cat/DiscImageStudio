using System.Runtime.Intrinsics;
using DiscImageStudio.Core;

namespace DiscImageStudio.BluRay;

public readonly record struct BluRayTrackPoint(int Layer, double RadiusMm, double AngleRadians)
{
    public double Xmm => RadiusMm * Math.Cos(AngleRadians);
    public double Ymm => RadiusMm * Math.Sin(AngleRadians);
}

/// <summary>Nominal recording-unit geometry with optional cubic radial pitch. Includes run-in/run-out
/// spacing (ISO/IEC 30190 §14), but does not predict drive defect replacement.</summary>
public sealed class BluRayTrackGeometry
{
    private readonly BluRayDiscParameters _disc;
    private readonly double _growth;
    private readonly double _innerPrimitive;
    private readonly double _angleOffset;
    private readonly DvdTrackGeometry? _variablePitch;
    private readonly bool _variablePixelReuse;
    private readonly double _interpolationMargin;
    private readonly double _interpolationTangentMargin;
    private readonly double _interpolationCurvatureBound;

    public BluRayTrackGeometry(BluRayDiscParameters disc)
    {
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
        _disc = disc;
        TrackLengthPerLayerMm = disc.TotalSectors
            / (double)BluRayDiscParameters.SectorsPerCluster
            * BluRayDiscParameters.ChannelBitsPerRecordingUnit
            * BluRayDiscParameters.ChannelBitLengthNm * 1e-6;
        _angleOffset = (disc.StartAngleDegrees % 360) * Math.PI / 180;
        if (disc.HasVariablePitch)
        {
            _variablePitch = DvdTrackGeometry.Create(disc.InnerRadiusMm, disc.OuterRadiusMm,
                TrackLengthPerLayerMm, disc.PitchLinear, disc.PitchQuadratic, disc.PitchCubic);
            _growth = _variablePitch.RadialGrowthPerRadianMm;
            _variablePixelReuse = _variablePitch.TryGetFastSamplingBounds(out _interpolationMargin,
                out _interpolationTangentMargin, out double acceleration);
            _interpolationCurvatureBound = acceleration / 2;
            return;
        }
        double lo = 1e-15, hi = 1;
        while (Primitive(disc.OuterRadiusMm, hi) - Primitive(disc.InnerRadiusMm, hi) > TrackLengthPerLayerMm)
            hi *= 2;
        for (int i = 0; i < 100; i++)
        {
            double b = (lo + hi) / 2;
            double length = Primitive(disc.OuterRadiusMm, b) - Primitive(disc.InnerRadiusMm, b);
            if (length > TrackLengthPerLayerMm) lo = b; else hi = b;
        }
        _growth = (lo + hi) / 2;
        _innerPrimitive = Primitive(disc.InnerRadiusMm, _growth);
    }

    public double TrackLengthPerLayerMm { get; }
    public double EffectiveTrackPitchMicrometers => 2 * Math.PI * _growth * 1000;

    // Reuse is limited to optical-disc-scale, thin spirals. The budget covers
    // both endpoint coordinate roundoff (radius error amplified by r/b in the
    // angle) and channel-to-arc conversion. This is machine epsilon, NOT
    // double.Epsilon. If the source pixels are too small for the budget, callers
    // retain independent scalar sampling instead of weakening it.
    internal bool SupportsPixelReuse => (_variablePitch is null || _variablePixelReuse) && _disc.InnerRadiusMm >= 10
        && _growth is >= 1e-6 and <= 1e-3;
    internal double PixelReuseSafetyMarginMm =>
        8192 * 2.2204460492503131e-16 * _disc.OuterRadiusMm * _disc.OuterRadiusMm / _growth
        * Math.Max(1, _disc.OuterRadiusMm / _disc.InnerRadiusMm)
        + 256 * 2.2204460492503131e-16 * TrackLengthPerLayerMm + 1e-8 + _interpolationMargin;
    internal double PixelReuseCurvatureBound => _variablePitch is null
        ? 1 / _disc.InnerRadiusMm : _interpolationCurvatureBound;
    internal double PixelReuseTangentMargin => _interpolationTangentMargin;
    internal double MillimetresPerChannelBit => TrackLengthPerLayerMm / _disc.TotalSectors
        / (BluRayDiscParameters.ChannelBitsPerRecordingUnit / 32.0);
    internal (double X, double Y) UnitTangent(double x, double y, double radius)
    {
        if (_variablePitch is not null) return (y / radius, -x / radius);
        double length = Math.Sqrt(radius * radius + _growth * _growth);
        return ((_growth * (x / radius) + y) / length,
            (_growth * (y / radius) - x) / length);
    }

    // This fast path uses the same per-lane arithmetic and transcendental calls
    // as AtLayerFraction. Small/custom or numerically extreme geometries keep
    // the scalar path, including its eight-step inverse and validation.
    internal bool SupportsFourPointSampling => Vector256.IsHardwareAccelerated
        && (_variablePitch is not null || _growth is >= 1e-8 and <= 0.01
        && _disc.InnerRadiusMm >= 1
        && double.IsFinite(_innerPrimitive));

    // Only called for validated payload midpoints strictly inside the disc;
    // public endpoint queries retain AtLayerFraction's exact endpoint override.
    internal (Vector256<double> Radius, Vector256<double> Angle) AtSectorsFour(
        Vector256<double> sectorPositions)
    {
        if (_variablePitch is not null)
        {
            Vector256<double> arcs = (sectorPositions / Vector256.Create((double)_disc.TotalSectors))
                * Vector256.Create(TrackLengthPerLayerMm);
            var point = _variablePitch.AtArcLengthFast(arcs);
            return (point.Radius, Vector256.Create(_angleOffset) - point.Angle);
        }
        Vector256<double> inner = Vector256.Create(_disc.InnerRadiusMm);
        Vector256<double> outer = Vector256.Create(_disc.OuterRadiusMm);
        Vector256<double> growth = Vector256.Create(_growth);
        Vector256<double> growthSquared = Vector256.Create(_growth * _growth);
        Vector256<double> twiceGrowth = Vector256.Create(2 * _growth);
        Vector256<double> target = (sectorPositions / Vector256.Create((double)_disc.TotalSectors))
            * Vector256.Create(TrackLengthPerLayerMm);
        Vector256<double> radius = Vector256.Sqrt(Vector256.Create(_disc.InnerRadiusMm * _disc.InnerRadiusMm)
            + twiceGrowth * target);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            Vector256<double> quotient = radius / growth;
            // Keep Math.Asinh instead of substituting a vector approximation.
            Vector256<double> inverseSinh = Vector256.Create(
                Math.Asinh(quotient.GetElement(0)), Math.Asinh(quotient.GetElement(1)),
                Math.Asinh(quotient.GetElement(2)), Math.Asinh(quotient.GetElement(3)));
            Vector256<double> root = Vector256.Sqrt(radius * radius + growthSquared);
            Vector256<double> primitive = (radius * root + growthSquared * inverseSinh) / twiceGrowth;
            Vector256<double> error = primitive - Vector256.Create(_innerPrimitive) - target;
            radius = Vector256.Min(outer, Vector256.Max(inner, radius - error * growth / root));
        }
        return (radius, Vector256.Create(_angleOffset) - (radius - inner) / growth);
    }

    /// <summary>Position on a particular layer; progress=1 is allowed for its endpoint.
    /// The single layer runs outward and clockwise in fixed disc coordinates when
    /// viewed from the objective/data side, with positive Y up (§9.8 and §15.3).</summary>
    public BluRayTrackPoint AtLayerFraction(int layer, double progress)
    {
        if (layer < 0 || layer >= _disc.LayerCount) throw new ArgumentOutOfRangeException(nameof(layer));
        if (!double.IsFinite(progress) || progress < 0 || progress > 1)
            throw new ArgumentOutOfRangeException(nameof(progress));
        double radialProgress = progress;
        double target = radialProgress * TrackLengthPerLayerMm;
        if (_variablePitch is not null)
        {
            var point = _variablePitch.AtArcLengthFast(target);
            return new BluRayTrackPoint(layer, point.Radius, _angleOffset - point.Angle);
        }
        // The thin-spiral inverse is already close at optical-disc pitch. Newton
        // refinement retains the exact arc model without 60 searches per image byte.
        double radius = Math.Sqrt(_disc.InnerRadiusMm * _disc.InnerRadiusMm + 2 * _growth * target);
        for (int i = 0; i < (_growth <= 0.01 ? 2 : 8); i++)
        {
            double error = Primitive(radius, _growth) - _innerPrimitive - target;
            radius = Math.Clamp(radius - error * _growth / Math.Sqrt(radius * radius + _growth * _growth),
                _disc.InnerRadiusMm, _disc.OuterRadiusMm);
        }
        radius = radialProgress == 0 ? _disc.InnerRadiusMm : radialProgress == 1 ? _disc.OuterRadiusMm : radius;
        // The disc rotates counter-clockwise under the fixed objective; the next
        // material point at that objective therefore has a decreasing polar angle.
        double angle = _angleOffset - (radius - _disc.InnerRadiusMm) / _growth;
        return new BluRayTrackPoint(layer, radius, angle);
    }

    public BluRayTrackPoint AtSector(double sectorPosition)
    {
        if (!double.IsFinite(sectorPosition) || sectorPosition < 0 || sectorPosition > _disc.TotalSectors)
            throw new ArgumentOutOfRangeException(nameof(sectorPosition));
        return AtLayerFraction(0, sectorPosition / _disc.TotalSectors);
    }

    /// <summary>Finds the nearest spiral turn at a point in fixed disc coordinates.
    /// The cubic curve and its inverse tables are reused across preview pixels.</summary>
    public bool TryGetSectorAtPoint(double xMm, double yMm, out double sector)
    {
        sector = 0;
        double radius = Math.Sqrt(xMm * xMm + yMm * yMm);
        if (!double.IsFinite(radius) || radius < _disc.InnerRadiusMm || radius >= _disc.OuterRadiusMm)
            return false;
        if (_variablePitch is not null)
        {
            double angle = _variablePitch.AngleAtRadius(radius);
            double difference = Math.IEEERemainder(Math.Atan2(yMm, xMm) - (_angleOffset - angle), 2 * Math.PI);
            double nearestAngle = angle - difference;
            if (nearestAngle < 0 || nearestAngle >= _variablePitch.TotalAngleRadians) return false;
            sector = Math.Clamp(_variablePitch.ArcLengthAtAngle(nearestAngle)
                * (_disc.TotalSectors / TrackLengthPerLayerMm), 0, Math.BitDecrement((double)_disc.TotalSectors));
            return true;
        }

        double growth = EffectiveTrackPitchMicrometers / (2 * Math.PI * 1000);
        double spiralAngle = _angleOffset - (radius - _disc.InnerRadiusMm) / growth;
        double delta = Math.IEEERemainder(Math.Atan2(yMm, xMm) - spiralAngle, 2 * Math.PI);
        double nearestRadius = radius - delta * growth;
        if (nearestRadius < _disc.InnerRadiusMm || nearestRadius >= _disc.OuterRadiusMm) return false;
        sector = Math.Clamp((Primitive(nearestRadius, growth) - Primitive(_disc.InnerRadiusMm, growth))
            * (_disc.TotalSectors / TrackLengthPerLayerMm), 0, Math.BitDecrement((double)_disc.TotalSectors));
        return true;
    }

    private static double Primitive(double radius, double b)
        => (radius * Math.Sqrt(radius * radius + b * b) + b * b * Math.Asinh(radius / b)) / (2 * b);
}
