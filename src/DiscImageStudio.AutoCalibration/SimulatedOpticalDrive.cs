namespace DiscImageStudio.AutoCalibration;

public sealed class SimulatedOpticalDrive : ITimedOpticalDrive
{
    private readonly CalibrationSpiral _spiral;
    private readonly CalibrationMedia _media;
    private readonly CalibrationRandom _random;
    private readonly TimeSpan _pace;
    private double _nowNanoseconds = 1e9;
    private uint _headLba;
    private uint? _cachedBlock;
    private bool _disposed;

    public SimulatedOpticalDrive(
        CalibrationSpiralParameters parameters,
        ulong seed,
        bool paced = false,
        double spindlePeriodNanoseconds = 0,
        double jitterDegrees = 0.6,
        double retryChance = 0.01)
    {
        _media = parameters.SectorBits == CalibrationMediaProfile.SectorBits(CalibrationMedia.Cd)
            ? CalibrationMedia.Cd
            : CalibrationMedia.Dvd;
        _spiral = CalibrationSpiral.Create(parameters);
        _random = new(seed);
        _pace = paced ? TimeSpan.FromTicks(250) : TimeSpan.Zero;
        SpindlePeriodNanoseconds = spindlePeriodNanoseconds > 0
            ? spindlePeriodNanoseconds
            : _media == CalibrationMedia.Cd ? 48.34e6 : 25.474e6;
        JitterDegrees = jitterDegrees;
        RetryChance = retryChance;
        SupportsForceUnitAccess = true;
    }

    public double LateFrames { get; set; }
    public double SpindlePeriodNanoseconds { get; }
    public double JitterDegrees { get; }
    public double RetryChance { get; }
    public bool SupportsForceUnitAccess { get; set; }
    public int ExpectedCdUserDataOffset => 16;

    public TimedSectorRead ReadTimed(uint lba, bool forceUnitAccess, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(cancellationToken);
        uint block = lba / CalibrationMediaProfile.BlockSectors(_media);
        bool cacheHit = !(forceUnitAccess && SupportsForceUnitAccess) && _cachedBlock == block;
        if (cacheHit)
        {
            _nowNanoseconds += 1.1e6;
            return new(_nowNanoseconds);
        }

        double distance = Math.Abs((long)lba - _headLba) / (double)_spiral.Parameters.TotalSectors;
        double earliest = _nowNanoseconds + 3e6 + 90e6 * Math.Sqrt(distance);
        double target = _spiral.AngleAtBit(
            CalibrationMediaProfile.CompletionBit(_media, lba) + LateFrames * 588)
            / (2 * Math.PI);
        double phaseNanoseconds = PositiveRemainder(target, 1) * SpindlePeriodNanoseconds;
        double wait = PositiveRemainder(phaseNanoseconds - earliest, SpindlePeriodNanoseconds);
        double end = earliest + wait;
        if (_random.NextUnit() < RetryChance)
        {
            end += SpindlePeriodNanoseconds * 2 + _random.NextUnit() * SpindlePeriodNanoseconds;
        }

        end += _random.NextGaussian() * JitterDegrees / 360.0 * SpindlePeriodNanoseconds;
        _nowNanoseconds = Math.Max(end, _nowNanoseconds);
        _headLba = lba;
        _cachedBlock = block;
        if (_pace > TimeSpan.Zero)
        {
            Thread.Sleep(_pace);
        }

        return new(end);
    }

    public uint ReadCapacity(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(cancellationToken);
        return _spiral.Parameters.TotalSectors;
    }

    public void ReadSector(uint lba, Span<byte> destination, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(cancellationToken);
        if (destination.Length != CalibrationMediaProfile.SectorBytes)
        {
            throw new ArgumentException("扇区缓冲区必须为 2048 字节。", nameof(destination));
        }

        if (lba >= _spiral.Parameters.TotalSectors)
        {
            throw new IOException($"LBA {lba} 超出光盘容量。");
        }

        CalibrationImageGenerator.CreateSector(lba, _spiral.Parameters.TotalSectors).CopyTo(destination);
    }

    public void SetReadSpeed(ushort kilobytesPerSecond, CancellationToken cancellationToken) =>
        ThrowIfUnavailable(cancellationToken);

    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(cancellationToken);
        _nowNanoseconds += duration.TotalNanoseconds;
    }

    public void Dispose() => _disposed = true;

    private static double PositiveRemainder(double value, double divisor)
    {
        double result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private void ThrowIfUnavailable(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

public static class CalibrationReferenceGeometry
{
    public static CalibrationSpiralParameters Dvd { get; } = new(
        TotalSectors: 2_295_104,
        SectorBits: 38_688,
        BitLengthMm: 133.3e-6,
        InnerRadiusMm: 24.173325346,
        OuterRadiusMm: 57.878686008,
        PitchLinear: -1.857160e-5,
        PitchQuadratic: 1.191945e-4,
        PitchCubic: -1.373621e-4);

    public static CalibrationSpiralParameters Cd(uint totalSectors = 359_779) => new(
        TotalSectors: totalSectors,
        SectorBits: 57_624,
        BitLengthMm: CalibrationMediaProfile.BitLengthMm(CalibrationMedia.Cd),
        InnerRadiusMm: 24.84,
        OuterRadiusMm: Math.Sqrt(
            24.84 * 24.84
            + totalSectors * 57_624.0 * CalibrationMediaProfile.BitLengthMm(CalibrationMedia.Cd) * 1.5003e-3 / Math.PI),
        PitchLinear: 0,
        PitchQuadratic: 0,
        PitchCubic: 0);
}
