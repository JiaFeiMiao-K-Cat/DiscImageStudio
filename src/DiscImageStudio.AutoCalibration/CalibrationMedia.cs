namespace DiscImageStudio.AutoCalibration;

public enum CalibrationMedia
{
    Dvd,
    Cd,
}

public static class CalibrationMediaProfile
{
    public const int SectorBytes = 2048;

    public static CalibrationMedia Parse(string mediaType) => mediaType.Trim().ToUpperInvariant() switch
    {
        "DVD-R" or "DVD+R" or "DVD-RW" or "DVD+RW" or "DVD" => CalibrationMedia.Dvd,
        "CD-R" or "CD-RW" or "CD-ROM" or "CD" => CalibrationMedia.Cd,
        _ => throw new ArgumentException($"不支持的盘片类型：{mediaType}", nameof(mediaType)),
    };

    public static bool IsSupported(string mediaType)
    {
        try
        {
            _ = Parse(mediaType);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static uint SectorBits(CalibrationMedia media) =>
        media == CalibrationMedia.Dvd ? 38_688u : 57_624u;

    public static double BitLengthMm(CalibrationMedia media) =>
        media == CalibrationMedia.Dvd ? 133.3e-6 : 1.2e9 / 4_321_800.0 * 1e-6;

    public static uint BlockSectors(CalibrationMedia media) =>
        media == CalibrationMedia.Dvd ? 16u : 1u;

    public static uint Align(CalibrationMedia media, uint lba)
    {
        uint block = BlockSectors(media);
        return lba / block * block;
    }

    public static double CompletionBit(CalibrationMedia media, uint lba) =>
        media == CalibrationMedia.Dvd
            ? ((lba / 16 + 1) * 16.0) * SectorBits(media)
            : ((lba + 1.0) * 98 + 108) * 588;

    public static CalibrationLadderShape Ladder(CalibrationMedia media) => media switch
    {
        CalibrationMedia.Dvd => new(
            NominalInnerRadiusMm: 24.1,
            NominalPitchMm: 0.74e-3,
            InnerRadiusSigmaMm: 0.6,
            PitchSigmaMm: 0.02e-3,
            ReadSpeedKbps: 5_540,
            RpmLow: 1_500,
            RpmHigh: 12_000,
            SlowestTurnNanoseconds: 60e9 / 1_200,
            MinimumSpacingSectors: 2_000,
            MinimumSectors: 500_000,
            NearTurns: [1.5, -1.5, 3, -3, 6, -6, 12, -12, 24, -24],
            NearMinimumSectors: 32),
        _ => new(
            NominalInnerRadiusMm: 24.9,
            NominalPitchMm: 1.5e-3,
            InnerRadiusSigmaMm: 2,
            PitchSigmaMm: 0.2e-3,
            ReadSpeedKbps: 1_411,
            RpmLow: 400,
            RpmHigh: 6_000,
            SlowestTurnNanoseconds: 60e9 / 600,
            MinimumSpacingSectors: 600,
            MinimumSectors: 50_000,
            NearTurns: [0.5, -0.5, 1.5, -1.5, 3, -3, 6, -6, 12, -12, 24, -24],
            NearMinimumSectors: 4),
    };
}

public sealed record CalibrationLadderShape(
    double NominalInnerRadiusMm,
    double NominalPitchMm,
    double InnerRadiusSigmaMm,
    double PitchSigmaMm,
    ushort ReadSpeedKbps,
    double RpmLow,
    double RpmHigh,
    double SlowestTurnNanoseconds,
    uint MinimumSpacingSectors,
    uint MinimumSectors,
    IReadOnlyList<double> NearTurns,
    uint NearMinimumSectors);
