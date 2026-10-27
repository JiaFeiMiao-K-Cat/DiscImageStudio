using System.Runtime.Intrinsics;

namespace DiscImageStudio.Cd;

/// <summary>Shared four-point geometry for raster and analytic samples, preserving scalar decisions.</summary>
internal sealed class CdImageSampler
{
    private readonly RasterImage? _source;
    private readonly Func<double, double, byte>? _sampleGray;
    private readonly CdDiscParameters _parameters;
    private readonly double _innerSquared;
    private readonly double _radiusSquaredRange;
    private readonly double _angleScale;

    internal CdImageSampler(RasterImage source, CdDiscParameters parameters)
        : this(parameters)
    {
        _source = source;
    }

    internal CdImageSampler(Func<double, double, byte> sampleGray, CdDiscParameters parameters)
        : this(parameters)
    {
        _sampleGray = sampleGray;
    }

    private CdImageSampler(CdDiscParameters parameters)
    {
        _parameters = parameters;
        _innerSquared = parameters.InnerRadiusMm * parameters.InnerRadiusMm;
        _radiusSquaredRange = (parameters.OuterRadiusMm * parameters.OuterRadiusMm) - _innerSquared;
        _angleScale = 2.0 * parameters.LinearVelocityMmPerSecond * parameters.TotalTimeSeconds / _radiusSquaredRange;
    }

    internal bool SupportsSimd => Vector256.IsHardwareAccelerated
        && double.IsFinite(_innerSquared) && double.IsFinite(_radiusSquaredRange)
        && _radiusSquaredRange > 0 && double.IsFinite(_angleScale);

    internal byte Sample(long globalByte)
    {
        if (_sampleGray is not null)
        {
            (double radius, double theta) = _parameters.PolarFromByte(globalByte);
            return _sampleGray(radius * Math.Cos(theta), radius * Math.Sin(theta));
        }
        RasterImage source = _source!;
        (double x, double y) = _parameters.ImagePointFromByte(globalByte, source.Width);
        return source.GrayAt(x, y);
    }

    internal uint SampleFour(long firstByte)
        => SampleFour(firstByte, firstByte + 1, firstByte + 2, firstByte + 3);

    internal uint SampleFour(long firstByte, long secondByte, long thirdByte, long fourthByte)
    {
        // Mode 1 payload order crosses CIRC delay columns. Its four positions
        // need not be consecutive, and out-of-track lanes keep scalar clamping.
        ulong totalBytes = (ulong)_parameters.TotalBytes;
        if ((ulong)firstByte >= totalBytes || (ulong)secondByte >= totalBytes
            || (ulong)thirdByte >= totalBytes || (ulong)fourthByte >= totalBytes)
            return SampleFourScalar(firstByte, secondByte, thirdByte, fourthByte);
        // Each lane keeps the original operation order. In particular, division
        // is not replaced by multiplication with a cached reciprocal or FMA.
        Vector256<double> indices = Vector256.Create(
            (double)firstByte, (double)secondByte, (double)thirdByte, (double)fourthByte);
        Vector256<double> fraction = indices / Vector256.Create((double)_parameters.TotalBytes);
        Vector256<double> radius = Vector256.Sqrt(Vector256.Create(_innerSquared)
            + fraction * Vector256.Create(_radiusSquaredRange));
        Vector256<double> angle = Vector256.Create(_parameters.StartAngleRadians)
            + Vector256.Create(_angleScale) * (radius - Vector256.Create(_parameters.InnerRadiusMm));
        if (_sampleGray is not null)
        {
            // Analytic callbacks can distinguish arbitrarily close coordinates.
            // Reuse the exact four-lane geometry, but retain scalar trig and
            // serial callback order instead of the raster's guarded approximation.
            uint analyticSamples = 0;
            for (int lane = 0; lane < 4; lane++)
            {
                double laneRadius = radius.GetElement(lane), laneAngle = angle.GetElement(lane);
                analyticSamples |= (uint)_sampleGray(laneRadius * Math.Cos(laneAngle),
                    laneRadius * Math.Sin(laneAngle)) << (lane * 8);
            }
            return analyticSamples;
        }
        if (!Vector256.LessThanOrEqualAll(Vector256.Abs(angle), Vector256.Create(1_000_000d)))
            return SampleFourScalar(firstByte, secondByte, thirdByte, fourthByte);

        (Vector256<double> sin, Vector256<double> cos) = Vector256.SinCos(angle);
        RasterImage source = _source!;
        double centre = source.Width / 2.0;
        Vector256<double> pixelRadius = Vector256.Create(centre) * radius
            / Vector256.Create(_parameters.ImageOuterRadiusMm);
        Vector256<double> x = Vector256.Create(centre) + pixelRadius * cos;
        Vector256<double> y = Vector256.Create(centre) + pixelRadius * sin;
        ReadOnlySpan<long> positions = stackalloc long[] { firstByte, secondByte, thirdByte, fourthByte };
        uint samples = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            if (!source.TryGrayAtSimd(x.GetElement(lane), y.GetElement(lane), out byte gray))
                gray = Sample(positions[lane]);
            samples |= (uint)gray << (lane * 8);
        }
        return samples;
    }

    private uint SampleFourScalar(long firstByte, long secondByte, long thirdByte, long fourthByte)
        => (uint)Sample(firstByte)
            | ((uint)Sample(secondByte) << 8)
            | ((uint)Sample(thirdByte) << 16)
            | ((uint)Sample(fourthByte) << 24);
}
