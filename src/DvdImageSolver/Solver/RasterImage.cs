using DiscImageStudio.ImageIO;

namespace DvdImageSolver.Solver;

internal sealed class RasterImage
{
    private readonly byte[] _bgra;

    private RasterImage(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        _bgra = bgra;
        HasBinaryGrayscalePalette = IsOpaqueBinaryGrayscale(bgra);
    }

    internal int Width { get; }

    internal int Height { get; }

    internal bool HasBinaryGrayscalePalette { get; }

    internal static RasterImage Load(string path)
    {
        var raster = RasterCodec.LoadBgra32(path);
        return new RasterImage(raster.Width, raster.Height, raster.Pixels);
    }

    internal static RasterImage CreateForTest(int width, int height, byte blue, byte green, byte red, byte alpha)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }

        return new RasterImage(width, height, pixels);
    }

    internal PixelSample SampleDisc(
        double xMm, double yMm, double outerRadiusMm, bool preserveDiscreteGrayscale = false)
    {
        double scale = (Math.Min(Width, Height) - 1) / (2.0 * outerRadiusMm);
        double pixelX = ((Width - 1) / 2.0) + (xMm * scale);
        double pixelY = ((Height - 1) / 2.0) - (yMm * scale);
        if (pixelX < 0 || pixelY < 0 || pixelX > Width - 1 || pixelY > Height - 1)
        {
            return default;
        }

        // An already dithered image encodes brightness as the spatial proportion
        // of its discrete pixels. Interpolation would invent intermediate tones
        // and requantization would change those proportions.
        if (preserveDiscreteGrayscale && HasBinaryGrayscalePalette)
        {
            int x = (int)Math.Floor(pixelX + 0.5);
            int y = (int)Math.Floor(pixelY + 0.5);
            return new PixelSample(_bgra[((y * Width) + x) * 4], 255);
        }

        int x0 = (int)Math.Floor(pixelX);
        int y0 = (int)Math.Floor(pixelY);
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, Height - 1);
        double horizontal = pixelX - x0;
        double vertical = pixelY - y0;

        double totalAlpha = 0;
        double weightedBlue = 0;
        double weightedGreen = 0;
        double weightedRed = 0;
        Accumulate(x0, y0, (1 - horizontal) * (1 - vertical));
        Accumulate(x1, y0, horizontal * (1 - vertical));
        Accumulate(x0, y1, (1 - horizontal) * vertical);
        Accumulate(x1, y1, horizontal * vertical);

        if (totalAlpha <= 0)
        {
            return default;
        }

        byte alpha = (byte)Math.Clamp((int)Math.Round(totalAlpha), 0, 255);
        double inverseAlpha = 1.0 / totalAlpha;
        double blue = weightedBlue * inverseAlpha;
        double green = weightedGreen * inverseAlpha;
        double red = weightedRed * inverseAlpha;
        byte luminance = (byte)Math.Clamp(
            (int)Math.Round((0.2126 * red) + (0.7152 * green) + (0.0722 * blue)),
            0,
            255);
        return new PixelSample(luminance, alpha);

        void Accumulate(int x, int y, double weight)
        {
            int offset = ((y * Width) + x) * 4;
            double weightedAlpha = weight * _bgra[offset + 3];
            totalAlpha += weightedAlpha;
            weightedBlue += weightedAlpha * _bgra[offset];
            weightedGreen += weightedAlpha * _bgra[offset + 1];
            weightedRed += weightedAlpha * _bgra[offset + 2];
        }
    }

    /// <summary>
    /// Returns a strict physical rectangle whose original scalar samples all
    /// have the same rounded luminance and alpha. This can cover constant pixels
    /// or a sufficiently small part of a smooth bilinear colour/alpha cell.
    /// </summary>
    internal bool TryGetStableRegion(double xMm, double yMm, double outerRadiusMm,
        bool preserveDiscreteGrayscale, out PixelSample sample, out SampleRectangle region)
    {
        sample = default;
        region = default;
        double scale = (Math.Min(Width, Height) - 1) / (2.0 * outerRadiusMm);
        double centreX = (Width - 1) / 2.0, centreY = (Height - 1) / 2.0;
        double x = centreX + xMm * scale, y = centreY - yMm * scale;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(scale) || scale <= 0
            || x < 0 || y < 0 || x > Width - 1 || y > Height - 1)
            return false;

        double left, right, top, bottom;
        byte gray;
        if (preserveDiscreteGrayscale && HasBinaryGrayscalePalette)
        {
            int pixelX = (int)Math.Floor(x + 0.5), pixelY = (int)Math.Floor(y + 0.5);
            gray = _bgra[((pixelY * Width) + pixelX) * 4];
            left = Math.Max(0, pixelX - 0.5); right = Math.Min(Width - 1, pixelX + 0.5);
            top = Math.Max(0, pixelY - 0.5); bottom = Math.Min(Height - 1, pixelY + 0.5);
        }
        else
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            if (x0 >= Width - 1 || y0 >= Height - 1) return false;
            int offset = ((y0 * Width) + x0) * 4;
            gray = _bgra[offset];
            if (!IsOpaqueGray(_bgra, offset, gray) || !IsOpaqueGray(_bgra, offset + 4, gray)
                || !IsOpaqueGray(_bgra, offset + Width * 4, gray)
                || !IsOpaqueGray(_bgra, offset + Width * 4 + 4, gray))
                return TryGetBilinearRegion(xMm, yMm, x, y, x0, y0, centreX, centreY, scale, out sample, out region);
            left = x0; right = x0 + 1; top = y0; bottom = y0 + 1;
            // All four alpha values are 255 and channels are the same integer.
            // The original positive weighted sums/division/luma operations have
            // error below 64*2^-52*255 (<4e-12), far from a half-integer boundary.
            // This does not apply to constant RGB colours with half-integer luma.
        }
        if (!SampleRectangle.TryFromPixels(left, right, top, bottom, centreX, centreY, scale, xMm, yMm, out region))
            return false;
        sample = new(gray, 255);
        return true;
    }

    private bool TryGetBilinearRegion(double xMm, double yMm, double x, double y, int x0, int y0,
        double centreX, double centreY, double scale, out PixelSample sample, out SampleRectangle region)
    {
        sample = default;
        region = default;
        double horizontal = x - x0, vertical = y - y0;
        double totalAlpha = 0, weightedBlue = 0, weightedGreen = 0, weightedRed = 0;
        int p00 = ((y0 * Width) + x0) * 4, p10 = p00 + 4;
        int p01 = p00 + Width * 4, p11 = p01 + 4;
        // Duplicate the scalar oracle's operation order, including independent
        // RGB normalization. The derivative bound below never replaces it.
        Accumulate(p00, (1 - horizontal) * (1 - vertical));
        Accumulate(p10, horizontal * (1 - vertical));
        Accumulate(p01, (1 - horizontal) * vertical);
        Accumulate(p11, horizontal * vertical);
        if (totalAlpha < 1) return false;
        double inverseAlpha = 1.0 / totalAlpha;
        double blue = weightedBlue * inverseAlpha, green = weightedGreen * inverseAlpha, red = weightedRed * inverseAlpha;
        double luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
        double a00 = _bgra[p00 + 3], a10 = _bgra[p10 + 3], a01 = _bgra[p01 + 3], a11 = _bgra[p11 + 3];
        double l00 = PremultipliedLuminance(p00), l10 = PremultipliedLuminance(p10);
        double l01 = PremultipliedLuminance(p01), l11 = PremultipliedLuminance(p11);
        double alphaGradient = Math.Max(Math.Abs(a10 - a00), Math.Abs(a11 - a01))
            + Math.Max(Math.Abs(a01 - a00), Math.Abs(a11 - a10));
        double luminanceGradient = Math.Max(Math.Abs(l10 - l00), Math.Abs(l11 - l01))
            + Math.Max(Math.Abs(l01 - l00), Math.Abs(l11 - l10));
        if (!BilinearSampleStability.TryGetRadius(totalAlpha, luminance, alphaGradient, luminanceGradient, out double delta))
            return false;
        if (!SampleRectangle.TryFromPixels(Math.Max(x0, x - delta), Math.Min(x0 + 1, x + delta),
            Math.Max(y0, y - delta), Math.Min(y0 + 1, y + delta), centreX, centreY, scale, xMm, yMm, out region))
            return false;
        sample = new((byte)Math.Clamp((int)Math.Round(luminance), 0, 255),
            (byte)Math.Clamp((int)Math.Round(totalAlpha), 0, 255));
        return true;

        void Accumulate(int offset, double weight)
        {
            double weightedAlpha = weight * _bgra[offset + 3];
            totalAlpha += weightedAlpha;
            weightedBlue += weightedAlpha * _bgra[offset];
            weightedGreen += weightedAlpha * _bgra[offset + 1];
            weightedRed += weightedAlpha * _bgra[offset + 2];
        }

        double PremultipliedLuminance(int offset) => _bgra[offset + 3]
            * ((0.2126 * _bgra[offset + 2]) + (0.7152 * _bgra[offset + 1]) + (0.0722 * _bgra[offset]));
    }

    /// <summary>
    /// Nearest-neighbour sampling for SIMD coordinates. Points close to a pixel
    /// decision or image edge are recomputed with the original scalar trig.
    /// </summary>
    internal bool TrySampleBinaryDisc(double xMm, double yMm, double outerRadiusMm, out PixelSample sample)
    {
        sample = default;
        double scale = (Math.Min(Width, Height) - 1) / (2.0 * outerRadiusMm);
        double pixelX = ((Width - 1) / 2.0) + (xMm * scale);
        double pixelY = ((Height - 1) / 2.0) - (yMm * scale);
        // A deliberately conservative guard for double trig rounding, scaled to
        // the raster dimensions. It is not a change to the sampling coordinates.
        double guard = Math.Max(Width, Height) * 1e-10;
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY)
            || Math.Abs(pixelX) <= guard || Math.Abs(pixelY) <= guard
            || Math.Abs(pixelX - (Width - 1)) <= guard || Math.Abs(pixelY - (Height - 1)) <= guard)
            return false;
        if (pixelX < 0 || pixelY < 0 || pixelX > Width - 1 || pixelY > Height - 1)
            return true;

        double roundedX = Math.Floor(pixelX + 0.5);
        double roundedY = Math.Floor(pixelY + 0.5);
        double fractionX = pixelX + 0.5 - roundedX;
        double fractionY = pixelY + 0.5 - roundedY;
        if (fractionX <= guard || 1 - fractionX <= guard
            || fractionY <= guard || 1 - fractionY <= guard)
            return false;
        sample = new PixelSample(_bgra[(((int)roundedY * Width) + (int)roundedX) * 4], 255);
        return true;
    }

    private static bool IsOpaqueBinaryGrayscale(ReadOnlySpan<byte> pixels)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte value = pixels[offset];
            if (pixels[offset + 3] != 255
                || pixels[offset + 1] != value
                || pixels[offset + 2] != value
                || (value != 0 && value != 255))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOpaqueGray(ReadOnlySpan<byte> pixels, int offset, byte gray)
        => pixels[offset] == gray && pixels[offset + 1] == gray
            && pixels[offset + 2] == gray && pixels[offset + 3] == 255;

}

internal readonly record struct PixelSample(byte Luminance, byte Alpha);
