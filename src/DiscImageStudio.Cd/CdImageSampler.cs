using System.Runtime.Intrinsics;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;

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
    private readonly double _imageOuterRadius;
    private readonly DvdTrackGeometry? _geometry;
    private readonly CalibrationTarget? _referenceTarget;
    private readonly double _referenceInnerBoundary;
    private readonly double _referenceOuterBoundary;
    private readonly double _referenceRadiusGuard;
    private readonly double _referencePixelScale;

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

    internal CdImageSampler(CalibrationTarget referenceTarget, CdDiscParameters parameters)
        : this(parameters)
    {
        if (!referenceTarget.UsesReferencePattern)
            throw new ArgumentException("Expected the fixed reference artwork.", nameof(referenceTarget));
        _referenceTarget = referenceTarget;
        _source = RasterImage.LoadCalibrationReference();
        // Scalar fallback keeps the original coordinate arithmetic and annulus test.
        _sampleGray = referenceTarget.Sample;
        _referenceInnerBoundary = referenceTarget.DrawingInnerRadiusMm * referenceTarget.DrawingInnerRadiusMm - 1e-10;
        _referenceOuterBoundary = referenceTarget.DrawingOuterRadiusMm * referenceTarget.DrawingOuterRadiusMm + 1e-10;
        _referenceRadiusGuard = Math.Max(1, parameters.OuterRadiusMm * parameters.OuterRadiusMm) * 1e-10;
        _referencePixelScale = CalibrationReferencePattern.PixelSize / 2.0 / parameters.OuterRadiusMm;
    }

    private CdImageSampler(CdDiscParameters parameters)
    {
        _parameters = parameters;
        _imageOuterRadius = parameters.SamplingImageOuterRadiusMm;
        _geometry = parameters.VariablePitchGeometry;
        _innerSquared = parameters.InnerRadiusMm * parameters.InnerRadiusMm;
        _radiusSquaredRange = (parameters.OuterRadiusMm * parameters.OuterRadiusMm) - _innerSquared;
        _angleScale = 2.0 * parameters.LinearVelocityMmPerSecond * parameters.TotalTimeSeconds / _radiusSquaredRange;
    }

    internal bool SupportsSimd => Vector256.IsHardwareAccelerated
        && double.IsFinite(_innerSquared) && double.IsFinite(_radiusSquaredRange)
        && _radiusSquaredRange > 0 && double.IsFinite(_angleScale);

    internal byte Sample(long globalByte)
    {
        if (_geometry is { } geometry)
        {
            (double radius, double angle) = geometry.AtArcLengthFast(_parameters.ArcLengthFromByte(globalByte));
            angle += _parameters.StartAngleRadians;
            if (_sampleGray is not null)
                return _sampleGray(radius * Math.Cos(angle), radius * Math.Sin(angle));
            RasterImage image = _source!;
            double centre = image.Width / 2.0;
            double pixelRadius = centre * radius / _imageOuterRadius;
            return image.GrayAt(centre + pixelRadius * Math.Cos(angle), centre + pixelRadius * Math.Sin(angle));
        }
        if (_sampleGray is not null)
        {
            (double radius, double theta) = _parameters.PolarFromByte(globalByte);
            return _sampleGray(radius * Math.Cos(theta), radius * Math.Sin(theta));
        }
        RasterImage source = _source!;
        (double x, double y) = _parameters.SourceImagePointFromByte(globalByte, source.Width);
        return source.GrayAt(x, y);
    }

    internal uint SampleFour(long firstByte)
        => SampleFour(firstByte, firstByte + 1, firstByte + 2, firstByte + 3);

    internal uint SampleFour(long firstByte, long secondByte, long thirdByte, long fourthByte)
    {
        // Mode 1 payload order crosses CIRC delay columns. Its four positions
        // need not be consecutive, and out-of-track lanes keep scalar clamping.
        ulong totalBytes = (ulong)_parameters.GeometryBytes;
        if ((ulong)firstByte >= totalBytes || (ulong)secondByte >= totalBytes
            || (ulong)thirdByte >= totalBytes || (ulong)fourthByte >= totalBytes)
            return SampleFourScalar(firstByte, secondByte, thirdByte, fourthByte);
        // Each lane keeps the original operation order. In particular, division
        // is not replaced by multiplication with a cached reciprocal or FMA.
        Vector256<double> indices = Vector256.Create(
            (double)firstByte, (double)secondByte, (double)thirdByte, (double)fourthByte);
        Vector256<double> radius, angle;
        if (_geometry is { } geometry)
        {
            (radius, angle) = geometry.AtArcLengthFast(indices
                * Vector256.Create(_parameters.LinearVelocityMmPerSecond)
                / Vector256.Create(75.0 * CdDiscParameters.BytesPerSector));
            angle += Vector256.Create(_parameters.StartAngleRadians);
        }
        else
        {
            Vector256<double> fraction = indices / Vector256.Create((double)_parameters.GeometryBytes);
            radius = Vector256.Sqrt(Vector256.Create(_innerSquared)
                + fraction * Vector256.Create(_radiusSquaredRange));
            angle = Vector256.Create(_parameters.StartAngleRadians)
                + Vector256.Create(_angleScale) * (radius - Vector256.Create(_parameters.InnerRadiusMm));
        }
        if (_sampleGray is not null && _referenceTarget is null)
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
        if (_referenceTarget is not null)
            return SampleReferenceFour(radius, sin, cos, firstByte, secondByte, thirdByte, fourthByte);
        RasterImage source = _source!;
        double centre = source.Width / 2.0;
        Vector256<double> pixelRadius = Vector256.Create(centre) * radius
            / Vector256.Create(_imageOuterRadius);
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

    private uint SampleReferenceFour(Vector256<double> radius, Vector256<double> sin, Vector256<double> cos,
        long firstByte, long secondByte, long thirdByte, long fourthByte)
    {
        // The reference callback multiplies physical x/y by the image scale.
        // Preserve that order rather than the ordinary raster's pixel-radius order.
        Vector256<double> centre = Vector256.Create(CalibrationReferencePattern.PixelSize / 2.0);
        Vector256<double> scale = Vector256.Create(_referencePixelScale);
        Vector256<double> x = centre + (radius * cos) * scale;
        Vector256<double> y = centre + (radius * sin) * scale;
        ReadOnlySpan<long> positions = stackalloc long[] { firstByte, secondByte, thirdByte, fourthByte };
        uint samples = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            double r = radius.GetElement(lane), squared = r * r;
            byte gray;
            // Near an annulus edge, use the callback's exact x*x+y*y test.
            // Elsewhere this guard also covers the scalar/SIMD trig roundoff.
            if (!double.IsFinite(squared)
                || Math.Abs(squared - _referenceInnerBoundary) <= _referenceRadiusGuard
                || Math.Abs(squared - _referenceOuterBoundary) <= _referenceRadiusGuard)
                gray = Sample(positions[lane]);
            else if (squared < _referenceInnerBoundary || squared > _referenceOuterBoundary)
                gray = 255;
            else if (!_source!.TryGrayAtSimd(x.GetElement(lane), y.GetElement(lane), out gray))
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
