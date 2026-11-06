namespace DiscImageStudio.Core.Calibration;

/// <summary>A short annular-sector mark centred on the positive X axis.</summary>
public sealed record CalibrationRadialArc(int Index, double RadiusMm, double InnerRadiusMm,
    double OuterRadiusMm, double HalfAngleRadians);

/// <summary>Physical geometry of the short-arc coarse targets, frozen by schema version.</summary>
public static class CalibrationRadialArcPattern
{
    /// <summary>Uniform radial thickness of the original schema 9 marks.</summary>
    public const double RadialThicknessMm = .20;
    /// <summary>Uniform circumferential length of the original schema 9 marks.</summary>
    public const double TangentialLengthMm = 16;
    public const double AngularSpanDegrees = 40;
    public static IReadOnlyList<double> RadialThicknessesMm { get; } = Array.AsReadOnly(new[] { .32, .30, .28 });
    public static IReadOnlyList<double> Stations { get; } = Array.AsReadOnly(new[] { .12, .50, .88 });

    public static bool Supports(CalibrationParameters parameters) => CalibrationClockRulerPattern.Supports(parameters);

    internal static IReadOnlyList<CalibrationRadialArc> Create(int version, double inner, double outer)
    {
        if (version is not (9 or 10)) throw new ArgumentOutOfRangeException(nameof(version));
        return Array.AsReadOnly(Stations.Select((fraction, index) =>
        {
            double radius = inner + (outer - inner) * fraction;
            double thickness = version == 9 ? RadialThicknessMm : RadialThicknessesMm[index];
            double halfAngle = version == 9 ? TangentialLengthMm / (2 * radius)
                : AngularSpanDegrees * Math.PI / 360;
            return new CalibrationRadialArc(index, radius, radius - thickness / 2,
                radius + thickness / 2, halfAngle);
        }).ToArray());
    }

    internal static byte Sample(IReadOnlyList<CalibrationRadialArc> arcs, double x, double radiusSquared)
    {
        if (x <= 0) return 255;
        foreach (CalibrationRadialArc arc in arcs)
            if (radiusSquared >= arc.InnerRadiusMm * arc.InnerRadiusMm
                && radiusSquared <= arc.OuterRadiusMm * arc.OuterRadiusMm
                && x >= Math.Sqrt(radiusSquared) * Math.Cos(arc.HalfAngleRadians))
                return 0;
        return 255;
    }
}
