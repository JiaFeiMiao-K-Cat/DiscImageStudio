namespace DiscImageStudio.Core.Calibration;

/// <summary>A dark, half-open interval in thousandths of the drawable track.</summary>
public readonly record struct CalibrationAddressBand(int StartPermille, int EndPermille)
{
    public double StartProgress => StartPermille / 1000.0;
    public double EndProgress => EndPermille / 1000.0;

    public (long Start, long EndExclusive) ByteRange(long totalBytes) => UnitRange(totalBytes);

    internal (long Start, long EndExclusive) UnitRange(long units) =>
        (checked(units * StartPermille + 999) / 1000, checked(units * EndPermille + 999) / 1000);
}

/// <summary>Dark bands at fixed elapsed track addresses. The spaces between them
/// are light; both sides of a transition share one physical edge.</summary>
public static class CalibrationWideBandPattern
{
    /// <summary>Original schema 11 intervals. For a saved or new target use its WideAddressBands.</summary>
    public static IReadOnlyList<CalibrationAddressBand> Bands { get; } =
        Array.AsReadOnly<CalibrationAddressBand>([new(50, 78), new(280, 320), new(860, 920)]);

    private static readonly IReadOnlyList<CalibrationAddressBand> AlternatingBands =
        Array.AsReadOnly<CalibrationAddressBand>([new(35, 170), new(305, 510), new(705, 955)]);

    public static bool Supports(CalibrationParameters parameters)
    {
        if (parameters.Kind == CalibrationDiscKind.Cd) return CalibrationClockRulerPattern.Supports(parameters);
        if (parameters.Kind is not (CalibrationDiscKind.Dvd or CalibrationDiscKind.BluRay)
            || parameters.Sectors <= 0 || parameters.Sectors > uint.MaxValue || parameters.OuterRadiusMm > 60)
            return false;
        long unit = parameters.Kind == CalibrationDiscKind.BluRay ? CalibrationParameters.BluRaySectorsPerCluster : 1;
        long end = parameters.EffectiveDrawingEndSectorExclusive;
        long length = end - parameters.DrawingStartSector;
        // At least one complete unit of light must remain after the 95.5% edge.
        // A short isolated patch cannot safely estimate both full-track endpoints.
        return parameters.OutputSectors % (parameters.Kind == CalibrationDiscKind.Dvd ? 16 : unit) == 0
            && parameters.DrawingStartSector % unit == 0 && end % unit == 0
            && length / unit >= 23 && length >= .30 * parameters.Sectors;
    }

    internal static IReadOnlyList<(long Start, long EndExclusive)> SectorRanges(CalibrationParameters p,
        IReadOnlyList<CalibrationAddressBand> bands)
    {
        long unit = p.Kind == CalibrationDiscKind.BluRay ? CalibrationParameters.BluRaySectorsPerCluster : 1;
        long end = p.EffectiveDrawingEndSectorExclusive;
        long units = (end - p.DrawingStartSector) / unit;
        return Array.AsReadOnly(bands.Select(band =>
        {
            var range = band.UnitRange(units);
            return (p.DrawingStartSector + unit * range.Start, p.DrawingStartSector + unit * range.EndExclusive);
        }).ToArray());
    }

    internal static IReadOnlyList<CalibrationAddressBand> ForVersion(int schemaVersion) => schemaVersion switch
    {
        11 => Bands,
        12 or 13 => AlternatingBands,
        _ => throw new ArgumentOutOfRangeException(nameof(schemaVersion)),
    };

    public static bool SupportsAngleSweep(CalibrationParameters p)
    {
        if (!Supports(p) || p.InnerRadiusMm < 7.5 || p.OuterRadiusMm > 60 || p.OuterRadiusMm <= p.InnerRadiusMm)
            return false;
        double q0 = p.DrawingStartSector / (double)p.Sectors;
        double qLength = p.EffectiveDrawingEndSectorExclusive / (double)p.Sectors - q0;
        // Bound the radial guard strips, the space for the optical lobe, and the number
        // of carrier cycles. CD/DVD/BD recordable presets lie comfortably inside this range.
        double r2 = p.InnerRadiusMm * p.InnerRadiusMm;
        double span = p.OuterRadiusMm * p.OuterRadiusMm - r2;
        double turns = p.TrackLengthMm / (Math.PI * (p.InnerRadiusMm + p.OuterRadiusMm));
        if (!double.IsFinite(turns) || turns * qLength < 300 || turns > 300_000) return false;
        foreach (var band in AlternatingBands)
        {
            double inner = Math.Sqrt(r2 + span * (q0 + qLength * band.StartProgress));
            double outer = Math.Sqrt(r2 + span * (q0 + qLength * band.EndProgress));
            // 72% is modulated. The reader needs roughly 3 mm after allowing
            // for the transition blur at the ends of the modulation window.
            if (outer - inner < 4.15) return false;
        }
        return true;
    }

    internal static IReadOnlyList<CalibrationRing> AddressRings(CalibrationTarget target, CalibrationGeometry geometry) =>
        Array.AsReadOnly(target.WideAddressBands.Select((_, i) =>
        {
            var (start, end) = target.WideBandProgress(i);
            double inner = geometry.AtProgress(start).Radius;
            double outer = geometry.AtProgress(end).Radius;
            return new CalibrationRing(i, (inner + outer) / 2, outer - inner);
        }).ToArray());
}
