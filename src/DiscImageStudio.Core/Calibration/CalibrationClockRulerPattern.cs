namespace DiscImageStudio.Core.Calibration;

/// <summary>Frozen logical-byte geometry of the schema 8 CD coarse target.
/// Address bands occupy fractions of the whole track, independent of the estimated radii.</summary>
public static class CalibrationClockRulerPattern
{
    public const int PeriodBytes = 2058;
    public const double InkHalfWidth = .006;
    public const double GutterHalfWidth = .0095;
    public static IReadOnlyList<double> Stations { get; } =
        Array.AsReadOnly(new[] { .05, .18, .35, .55, .74, .92 });

    // The local photo reader is calibrated for full-size 70–85 minute CDs at 1200 mm/s.
    // The byte pattern itself needs neither endpoint estimate; do not impose a tight seed range.
    public static bool Supports(CalibrationParameters parameters) =>
        parameters.Kind == CalibrationDiscKind.Cd && parameters.LinearDensity == 1200
        && parameters.Sectors is >= 315_000 and <= 382_500
        && parameters.OuterRadiusMm is > 40 and <= 60;

    public static double PeriodLengthMm(CalibrationParameters parameters) =>
        PeriodBytes * parameters.LinearDensity / (75 * 2352);

    /// <summary>Probability of a dark logical CD byte at this clock phase. The encoder dithers
    /// the fraction into 0x0D/0xAA; the overview displays its local mean at the resonances.</summary>
    public static double PilotInkFraction(double phaseRadians) =>
        .5 + (Math.Cos(phaseRadians) - Math.Cos(3 * phaseRadians) / 6) / Math.Sqrt(3);

    internal static IReadOnlyList<CalibrationRing> AddressRings(CalibrationGeometry geometry) =>
        Array.AsReadOnly(Stations.Select((q, i) =>
        {
            double low = geometry.AtProgress(q - InkHalfWidth).Radius;
            double high = geometry.AtProgress(q + InkHalfWidth).Radius;
            return new CalibrationRing(i, (low + high) / 2, high - low);
        }).ToArray());
}
