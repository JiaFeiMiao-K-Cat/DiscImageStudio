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
}

internal readonly record struct PixelSample(byte Luminance, byte Alpha);
