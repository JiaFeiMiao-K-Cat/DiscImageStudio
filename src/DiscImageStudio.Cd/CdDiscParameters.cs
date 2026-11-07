using System.Runtime.CompilerServices;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Cd;

public sealed record CdDiscParameters(
    double InnerRadiusMm,
    double OuterRadiusMm,
    long Sectors,
    double LinearVelocityMmPerSecond = 1200.0,
    double StartAngleRadians = 0.0,
    double ImageOuterRadiusMm = 57.5,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    long? UsableSectors = null,
    double? SourceImageOuterRadiusMm = null)
{
    // Cache by object identity: record copies made with `with` must get geometry
    // for their new values, without cache fields affecting record equality.
    private static readonly ConditionalWeakTable<CdDiscParameters, DvdTrackGeometry> Geometries = new();

    internal DvdTrackGeometry? VariablePitchGeometry => PitchLinear == 0 && PitchQuadratic == 0 && PitchCubic == 0
        ? null
        : Geometries.GetValue(this, static p => DvdTrackGeometry.Create(
            p.InnerRadiusMm, p.OuterRadiusMm, p.TotalTimeSeconds * p.LinearVelocityMmPerSecond,
            p.PitchLinear, p.PitchQuadratic, p.PitchCubic));

    public const int BytesPerSector = 2352;

    /// <summary>ECMA-130 CLV scanning speed; the UI no longer exposes it as a parameter.</summary>
    public const double StandardLinearVelocityMmPerSecond = 1200.0;

    /// <summary>Radius on the source image canvas that maps to the disc outer edge.</summary>
    public const double StandardImageOuterRadiusMm = 57.5;

    public static CdDiscParameters FromCalibration(CalibrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var p = target.Parameters;
        if (p.Kind != CalibrationDiscKind.Cd)
            throw new ArgumentException("Expected a CD calibration target.", nameof(target));
        return new(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.LinearDensity,
            ImageOuterRadiusMm: p.OuterRadiusMm,
            PitchLinear: p.PitchLinear, PitchQuadratic: p.PitchQuadratic, PitchCubic: p.PitchCubic,
            UsableSectors: p.UsableSectors);
    }

    public long OutputSectors => UsableSectors ?? Sectors;
    public long TotalBytes => checked(OutputSectors * BytesPerSector);
    public long GeometryBytes => checked(Sectors * BytesPerSector);
    public double OutputOuterRadiusMm => RadiusFromByte(TotalBytes);
    public double PreviewImageOuterRadiusMm => UsableSectors.HasValue ? OutputOuterRadiusMm : ImageOuterRadiusMm;
    public double SamplingImageOuterRadiusMm => SourceImageOuterRadiusMm ?? PreviewImageOuterRadiusMm;

    public double TotalTimeSeconds => Sectors / 75.0;

    private double RadiusSquaredRange =>
        (OuterRadiusMm * OuterRadiusMm) - (InnerRadiusMm * InnerRadiusMm);

    public void Validate()
    {
        if (Sectors <= 0 || OutputSectors <= 0 || OutputSectors > Sectors)
        {
            throw new ArgumentOutOfRangeException(nameof(Sectors), "CD sector count must be positive.");
        }

        if (!double.IsFinite(InnerRadiusMm)
            || !double.IsFinite(OuterRadiusMm)
            || InnerRadiusMm <= 0
            || OuterRadiusMm <= InnerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OuterRadiusMm),
                "CD outer radius must be greater than its positive inner radius.");
        }

        if (!double.IsFinite(LinearVelocityMmPerSecond) || LinearVelocityMmPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LinearVelocityMmPerSecond),
                "CD linear velocity must be positive.");
        }

        if (!double.IsFinite(StartAngleRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(StartAngleRadians));
        }

        if (!double.IsFinite(ImageOuterRadiusMm) || ImageOuterRadiusMm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ImageOuterRadiusMm),
                "Image outer radius must be positive.");
        }

        if (SourceImageOuterRadiusMm is { } sourceRadius && (!double.IsFinite(sourceRadius) || sourceRadius <= 0))
            throw new ArgumentOutOfRangeException(nameof(SourceImageOuterRadiusMm), "Source image radius must be positive and finite.");
        _ = GeometryBytes;
        _ = TotalBytes;
        DvdTrackGeometry.ValidatePitchCoefficients(PitchLinear, PitchQuadratic, PitchCubic);
        _ = VariablePitchGeometry;
    }

    internal double ArcLengthFromByte(long globalByte) => globalByte <= 0 ? 0
        : globalByte >= GeometryBytes ? TotalTimeSeconds * LinearVelocityMmPerSecond
        : Math.Clamp(globalByte * LinearVelocityMmPerSecond / (75.0 * BytesPerSector),
            0, TotalTimeSeconds * LinearVelocityMmPerSecond);

    public double RadiusFromByte(long globalByte)
    {
        if (VariablePitchGeometry is { } geometry)
            return geometry.RadiusAtArcLengthFast(ArcLengthFromByte(globalByte));
        double fraction = Math.Clamp((double)globalByte / GeometryBytes, 0.0, 1.0);
        return Math.Sqrt(
            (InnerRadiusMm * InnerRadiusMm) + (fraction * RadiusSquaredRange));
    }

    public double ThetaFromRadius(double radiusMm)
        => VariablePitchGeometry is { } geometry
            ? StartAngleRadians + geometry.AngleAtRadius(radiusMm)
            : StartAngleRadians
            + ((2.0 * LinearVelocityMmPerSecond * TotalTimeSeconds / RadiusSquaredRange)
                * (radiusMm - InnerRadiusMm));

    public (double RadiusMm, double ThetaRadians) PolarFromByte(long globalByte)
    {
        if (VariablePitchGeometry is { } geometry)
        {
            var point = geometry.AtArcLengthFast(ArcLengthFromByte(globalByte));
            return (point.Radius, StartAngleRadians + point.Angle);
        }
        double radius = RadiusFromByte(globalByte);
        return (radius, ThetaFromRadius(radius));
    }

    public (double X, double Y) ImagePointFromByte(long globalByte, int imageSize)
        => PointFromByte(globalByte, imageSize, PreviewImageOuterRadiusMm);

    public (double X, double Y) SourceImagePointFromByte(long globalByte, int imageSize)
        => PointFromByte(globalByte, imageSize, SamplingImageOuterRadiusMm);

    private (double X, double Y) PointFromByte(long globalByte, int imageSize, double imageOuterRadiusMm)
    {
        (double radius, double theta) = PolarFromByte(globalByte);
        double imageRadius = imageSize / 2.0;
        double pixelRadius = imageRadius * radius / imageOuterRadiusMm;
        double centre = imageSize / 2.0;
        return (
            centre + (pixelRadius * Math.Cos(theta)),
            centre + (pixelRadius * Math.Sin(theta)));
    }
}
