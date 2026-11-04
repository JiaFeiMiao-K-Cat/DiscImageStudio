namespace DiscImageStudio.Core.Calibration;

public enum CalibrationArtwork
{
    SweptReference, FineCross, ClockRuler, RadialArcDashes, WideAddressBands, AngleSweepBands,
    ReferenceCross, SingleRadialLine, HalfDisc, WavyBoundary
}

/// <summary>Version 7 artwork in physical drawing coordinates. Two gaps sweep the bearing
/// of a 120-degree sector so a local stretch of its edge remains resolved under track shear.</summary>
internal sealed class CalibrationAngularPattern
{
    private static readonly double[] Stations = [.05, .13, .27, .395, .58, .68, .86, .95];
    private static readonly double[] WidthsAt34Mm = [.65, 1, .8, .65, 1, .8, .65, 1];
    private readonly double[] _inkStart2, _inkEnd2, _marginStart2, _marginEnd2;
    private readonly double _sweepRate;
    private readonly double _crossHalfWidth;
    private readonly bool _drawSector;
    public IReadOnlyList<CalibrationRing> Rings { get; }

    public CalibrationAngularPattern(double inner, double outer, double radiansPerMm, double crossWidth, CalibrationArtwork artwork)
    {
        double span = outer - inner;
        _sweepRate = .012 * radiansPerMm;
        _crossHalfWidth = crossWidth / 2;
        _drawSector = artwork == CalibrationArtwork.SweptReference;
        var rings = Stations.Select((position, i) =>
            new CalibrationRing(i, inner + span * position, WidthsAt34Mm[i] * span / 34)).ToArray();
        Rings = Array.AsReadOnly(rings);
        static double Squared(double r) => r * r;
        _inkStart2 = rings.Select(r => Squared(r.InnerRadiusMm)).ToArray();
        _inkEnd2 = rings.Select(r => Squared(r.RadiusMm + .5 * r.WidthMm)).ToArray();
        _marginStart2 = rings.Select(r => Squared(r.RadiusMm - .68 * r.WidthMm)).ToArray();
        _marginEnd2 = rings.Select(r => Squared(r.RadiusMm + .68 * r.WidthMm)).ToArray();
    }

    public double SectorAngle(double radius)
    {
        if (radius >= Rings[1].RadiusMm && radius <= Rings[2].RadiusMm) return Sweep(radius, 1);
        if (radius >= Rings[5].RadiusMm && radius <= Rings[6].RadiusMm) return Sweep(radius, 5);
        return 0;
    }

    private double Sweep(double radius, int first)
    {
        double start = Rings[first].RadiusMm, end = Rings[first + 1].RadiusMm;
        double fromMiddle = radius - (start + end) / 2;
        return _sweepRate * fromMiddle * fromMiddle / (end - start);
    }

    public byte Sample(double x, double y, double radius2)
    {
        bool cross = Math.Min(Math.Abs(x), Math.Abs(y)) <= _crossHalfWidth;
        for (int i = 0; i < _marginEnd2.Length; i++)
        {
            if (radius2 > _marginEnd2[i]) continue;
            if (radius2 >= _marginStart2[i])
                return (radius2 >= _inkStart2[i] && radius2 <= _inkEnd2[i]) || cross ? (byte)0 : (byte)255;
            break;
        }
        if (!_drawSector) return cross ? (byte)0 : (byte)255;
        double radius = Math.Sqrt(radius2);
        double angle = SectorAngle(radius);
        double projection = x;
        if (angle != 0)
        {
            var (sine, cosine) = Math.SinCos(angle);
            projection = x * cosine + y * sine;
        }
        // Reverse the thin arm's polarity in the dark sector, keeping the same radial centreline.
        bool sector = projection >= .5 * radius;
        return sector ^ cross ? (byte)0 : (byte)255;
    }
}
