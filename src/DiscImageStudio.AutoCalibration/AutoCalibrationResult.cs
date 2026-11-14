using System.Collections.Generic;
using DiscImageStudio.Core.Calibration;
using GeometryDiscKind = DiscImageStudio.Core.Calibration.CalibrationDiscKind;

namespace DiscImageStudio.AutoCalibration;

/// <summary>Result of the Discratcher auto-calibration pipeline, mapped onto DiscImageStudio's
/// geometry vocabulary. <paramref name="MediaType"/> keeps Discratcher's own tokens
/// ("dvd", "cd", …) so the converter stays a thin, explicit translation layer.</summary>
public sealed record AutoCalibrationResult(
    string MediaType,
    double InnerRadiusMm,
    double OuterRadiusMm,
    long Sectors,
    double PitchLinear,
    double PitchQuadratic,
    double PitchCubic,
    double RmsResidualDeg);

public sealed record AutoCalibrationWizardResult(
    AutoCalibrationResult Geometry,
    IReadOnlyList<CalibrationSession> Sessions);

/// <summary>Maps Discratcher's raw tokens onto DiscImageStudio's calibration geometry and
/// supplies the per-media physical constants that Discratcher treats as known priors.</summary>
public static class AutoCalibrationGeometry
{
    public static GeometryDiscKind KindFor(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "cd" => GeometryDiscKind.Cd,
        "dvd" => GeometryDiscKind.Dvd,
        "bd" or "bluray" or "blu-ray" => GeometryDiscKind.BluRay,
        _ => throw new ArgumentException($"不支持的盘片类型：{mediaType}", nameof(mediaType)),
    };

    public static double DefaultLinearDensity(GeometryDiscKind kind) => kind switch
    {
        GeometryDiscKind.Dvd => 133.3,
        GeometryDiscKind.BluRay => CalibrationParameters.BluRayChannelBitLengthNm,
        _ => 1200.0,
    };

    public static CalibrationParameters ToParameters(AutoCalibrationResult result)
    {
        GeometryDiscKind kind = KindFor(result.MediaType);
        if (result.Sectors <= 0)
            throw new ArgumentException("自动标定结果的扇区数无效。", nameof(result));
        var parameters = new CalibrationParameters(
            kind, result.InnerRadiusMm, result.OuterRadiusMm, result.Sectors,
            DefaultLinearDensity(kind),
            result.PitchLinear, result.PitchQuadratic, result.PitchCubic);
        parameters.Validate();
        return parameters;
    }
}