using DiscImageStudio.Core.Calibration;

namespace DvdImageSolver.Solver;

/// <summary>Strict open rectangle in physical millimetres, with mathematical Y up.</summary>
internal readonly record struct SampleRectangle(double MinX, double MaxX, double MinY, double MaxY)
{
    internal bool Contains(double x, double y) => x > MinX && x < MaxX && y > MinY && y < MaxY;

    internal static bool TryFromPixels(double left, double right, double top, double bottom,
        double centreX, double centreY, double scale, double xMm, double yMm, out SampleRectangle region)
    {
        region = default;
        if (!double.IsFinite(scale) || scale <= 0) return false;
        double minX = (left - centreX) / scale, maxX = (right - centreX) / scale;
        double minY = (centreY - bottom) / scale, maxY = (centreY - top) / scale;
        double extent = Math.Max(Math.Max(Math.Abs(minX), Math.Abs(maxX)),
            Math.Max(Math.Abs(minY), Math.Abs(maxY)));
        // Cover both this inverse transform and the original forward mapping.
        // The caller separately accounts for geometry/trajectory position error.
        const double epsilon = 2.2204460492503131e-16;
        double guard = 128 * epsilon * (extent + (Math.Abs(centreX) + Math.Abs(centreY) + 1) / scale);
        if (!double.IsFinite(guard)) return false;
        region = new(minX + guard, maxX - guard, minY + guard, maxY - guard);
        return region.Contains(xMm, yMm);
    }
}

internal static class BilinearSampleStability
{
    // Original scalar arithmetic has positive weights and byte-valued channels.
    // With A>=1 and the restricted region's A>=Aanchor/2, rounding error in
    // A/Y and the premultiplied-gradient calculation is far below this budget.
    private const double RoundingBudget = 1e-7;

    internal static bool TryGetRadius(double alpha, double luminance, double alphaGradient,
        double premultipliedLuminanceGradient, out double radius)
    {
        radius = 0;
        if (!double.IsFinite(alpha) || !double.IsFinite(luminance) || alpha < 1) return false;
        double alphaMargin = 0.5 - Math.Abs(alpha - Math.Round(alpha)) - RoundingBudget;
        double luminanceMargin = 0.5 - Math.Abs(luminance - Math.Round(luminance)) - RoundingBudget;
        if (alphaMargin <= 0 || luminanceMargin <= 0) return false;
        double minimumAlpha = alpha / 2;
        // For Y=L/A, |dY| <= (|dL|+255|dA|)/minimumAlpha.
        // The gradients sum the maximum absolute x/y derivatives over the cell,
        // so the result is valid for an axis-aligned square of this pixel radius.
        double luminanceGradient = (premultipliedLuminanceGradient + 255 * alphaGradient + RoundingBudget) / minimumAlpha;
        radius = luminanceMargin / luminanceGradient;
        if (alphaGradient > 0)
            radius = Math.Min(radius, Math.Min(alphaMargin, minimumAlpha - RoundingBudget) / alphaGradient);
        return double.IsFinite(radius) && radius > 0;
    }
}

/// <summary>The exact frozen DVD reference image, including its opaque white annulus clipping.</summary>
internal sealed class CalibrationReferenceImageSampler
{
    private static readonly Lazy<byte[]> Pixels = new(CalibrationReferencePattern.ReadGrayPixels);
    private readonly CalibrationTarget _target;
    private readonly double _scale;
    private readonly double _innerRadius;
    private readonly double _outerRadius;

    internal CalibrationReferenceImageSampler(CalibrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Parameters.Kind != CalibrationDiscKind.Dvd || !target.UsesReferencePattern)
            throw new ArgumentException("Expected a DVD reference artwork target.", nameof(target));
        _target = target;
        _scale = (CalibrationReferencePattern.PixelSize - 1) / 2.0 / target.Parameters.OuterRadiusMm;
        _innerRadius = target.DrawingInnerRadiusMm;
        _outerRadius = target.DrawingOuterRadiusMm;
    }

    internal PixelSample SampleDisc(double xMm, double yMm) => new(_target.Sample(xMm, -yMm), 255);

    internal bool TryGetStableRegion(double xMm, double yMm, out PixelSample sample, out SampleRectangle region)
    {
        sample = default;
        region = default;
        const int size = CalibrationReferencePattern.PixelSize;
        const double centre = (size - 1) / 2.0;
        double x = centre + xMm * _scale, y = centre - yMm * _scale;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= size - 1 || y >= size - 1)
            return false;
        double radius = Math.Sqrt(xMm * xMm + yMm * yMm);
        const double epsilon = 2.2204460492503131e-16;
        double radialGuard = 128 * epsilon * Math.Max(1, _outerRadius);
        double room = Math.Min(radius - _innerRadius, _outerRadius - radius) - radialGuard;
        if (!double.IsFinite(room) || room <= 0) return false;

        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), index = y0 * size + x0;
        byte[] pixels = Pixels.Value;
        byte gray = pixels[index];
        double left = x0, right = x0 + 1, top = y0, bottom = y0 + 1;
        if (pixels[index + 1] != gray || pixels[index + size] != gray || pixels[index + size + 1] != gray)
        {
            double fx = x - x0, fy = y - y0;
            double a00 = ((1 - fx) * (1 - fy)) * 255, a10 = (fx * (1 - fy)) * 255;
            double a01 = ((1 - fx) * fy) * 255, a11 = (fx * fy) * 255;
            double totalAlpha = a00 + a10 + a01 + a11;
            double value = (pixels[index] * a00 + pixels[index + 1] * a10
                + pixels[index + size] * a01 + pixels[index + size + 1] * a11) * (1.0 / totalAlpha);
            double luminance = (0.2126 * value) + (0.7152 * value) + (0.0722 * value);
            double gx = Math.Max(Math.Abs(pixels[index + 1] - pixels[index]),
                Math.Abs(pixels[index + size + 1] - pixels[index + size]));
            double gy = Math.Max(Math.Abs(pixels[index + size] - pixels[index]),
                Math.Abs(pixels[index + size + 1] - pixels[index + 1]));
            if (!BilinearSampleStability.TryGetRadius(totalAlpha, luminance, 0, 255 * (gx + gy), out double delta))
                return false;
            left = Math.Max(left, x - delta); right = Math.Min(right, x + delta);
            top = Math.Max(top, y - delta); bottom = Math.Min(bottom, y + delta);
            gray = (byte)Math.Clamp((int)Math.Round(luminance), 0, 255);
        }
        if (!SampleRectangle.TryFromPixels(left, right, top, bottom, centre, centre, _scale, xMm, yMm, out region))
            return false;

        // A square's corners move at most sqrt(2)*half. Taking half=room/2
        // keeps the complete rectangle inside the nominal annulus, which is
        // narrower than the original squared-radius test with its 1e-10 margin.
        double half = room / 2;
        region = new(Math.Max(region.MinX, xMm - half), Math.Min(region.MaxX, xMm + half),
            Math.Max(region.MinY, yMm - half), Math.Min(region.MaxY, yMm + half));
        if (!region.Contains(xMm, yMm)) return false;
        // Constant gray, or a mixed cell restricted away from quantization
        // boundaries, retains the original opaque bilinear result.
        sample = new(gray, 255);
        return true;
    }
}
