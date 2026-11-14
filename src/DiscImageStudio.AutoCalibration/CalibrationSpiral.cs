namespace DiscImageStudio.AutoCalibration;

public readonly record struct CalibrationSpiralParameters(
    uint TotalSectors,
    uint SectorBits,
    double BitLengthMm,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double PitchLinear,
    double PitchQuadratic,
    double PitchCubic);

public sealed class CalibrationSpiral
{
    public const int DefaultGridSize = 40_001;

    private readonly double[] _bits;
    private readonly double[] _angles;
    private readonly double _radialStep;

    private CalibrationSpiral(CalibrationSpiralParameters parameters, int gridSize, double p0, double[] bits, double[] angles)
    {
        Parameters = parameters;
        P0Mm = p0;
        _bits = bits;
        _angles = angles;
        _radialStep = (parameters.OuterRadiusMm - parameters.InnerRadiusMm) / (gridSize - 1);
    }

    public CalibrationSpiralParameters Parameters { get; }
    public double P0Mm { get; }
    public double TotalBits => _bits[^1];

    public static CalibrationSpiral Create(CalibrationSpiralParameters parameters, int gridSize = DefaultGridSize)
    {
        if (!(parameters.OuterRadiusMm > parameters.InnerRadiusMm && parameters.InnerRadiusMm > 0)
            || !(parameters.BitLengthMm > 0)
            || parameters.TotalSectors == 0
            || parameters.SectorBits == 0
            || gridSize < 2)
        {
            throw new ArgumentException(
                $"无效的螺旋几何：ri={parameters.InnerRadiusMm}，ro={parameters.OuterRadiusMm}",
                nameof(parameters));
        }

        double radialStep = (parameters.OuterRadiusMm - parameters.InnerRadiusMm) / (gridSize - 1);
        var bits = new double[gridSize];
        var angles = new double[gridSize];
        double bitIntegral = 0;
        double angleIntegral = 0;
        double previousBitDensity = 0;
        double previousAngleDensity = 0;
        for (int i = 0; i < gridSize; i++)
        {
            double radius = parameters.InnerRadiusMm + i * radialStep;
            double x = i / (double)(gridSize - 1);
            double shape = 1 + x * (parameters.PitchLinear
                + x * (parameters.PitchQuadratic + x * parameters.PitchCubic));
            if (shape <= 0)
            {
                throw new ArgumentException("轨距形状 s(x) 必须始终大于零。", nameof(parameters));
            }

            double bitDensity = 2 * Math.PI * radius / shape;
            double angleDensity = 2 * Math.PI / shape;
            if (i > 0)
            {
                bitIntegral += 0.5 * (bitDensity + previousBitDensity) * radialStep;
                angleIntegral += 0.5 * (angleDensity + previousAngleDensity) * radialStep;
            }

            previousBitDensity = bitDensity;
            previousAngleDensity = angleDensity;
            bits[i] = bitIntegral;
            angles[i] = angleIntegral;
        }

        double totalTrackBits = (double)parameters.TotalSectors * parameters.SectorBits;
        double p0 = bitIntegral / (totalTrackBits * parameters.BitLengthMm);
        for (int i = 0; i < gridSize; i++)
        {
            bits[i] /= p0 * parameters.BitLengthMm;
            angles[i] /= p0;
        }

        return new(parameters, gridSize, p0, bits, angles);
    }

    public (double RadiusMm, double AngleRadians) Locate(double bitPosition)
    {
        int index = Array.BinarySearch(_bits, bitPosition);
        if (index >= 0)
        {
            return (Parameters.InnerRadiusMm + index * _radialStep, _angles[index]);
        }

        index = ~index - 1;
        index = Math.Clamp(index, 0, _bits.Length - 2);
        double fraction = (bitPosition - _bits[index]) / (_bits[index + 1] - _bits[index]);
        return (
            Parameters.InnerRadiusMm + (index + fraction) * _radialStep,
            _angles[index] + fraction * (_angles[index + 1] - _angles[index]));
    }

    public double AngleAtBit(double bitPosition) => Locate(bitPosition).AngleRadians;

    public (double Bits, double AngleRadians) AtRadius(double radiusMm)
    {
        double gridPosition = Math.Clamp(
            (radiusMm - Parameters.InnerRadiusMm) / _radialStep,
            0,
            _bits.Length - 1);
        int index = Math.Min((int)gridPosition, _bits.Length - 2);
        double fraction = gridPosition - index;
        return (
            _bits[index] + fraction * (_bits[index + 1] - _bits[index]),
            _angles[index] + fraction * (_angles[index + 1] - _angles[index]));
    }

    public double RadiusOfLba(uint lba) =>
        Locate((double)lba * Parameters.SectorBits).RadiusMm;

    public uint LbaAtRadius(double radiusMm) =>
        checked((uint)(AtRadius(radiusMm).Bits / Parameters.SectorBits));

    public double SectorsPerTurn(double radiusMm) =>
        2 * Math.PI * radiusMm / (Parameters.SectorBits * Parameters.BitLengthMm);
}
