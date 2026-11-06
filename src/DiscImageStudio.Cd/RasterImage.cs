using DiscImageStudio.ImageIO;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Cd;

internal sealed class RasterImage
{
    private readonly byte[] _gray;
    private static readonly Lazy<RasterImage> Reference = new(() =>
        new RasterImage(CalibrationReferencePattern.ReadGrayPixels(), CalibrationReferencePattern.PixelSize));

    private RasterImage(byte[] gray, int size)
    {
        Width = Height = size;
        _gray = gray;
    }

    private RasterImage(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        // CD sampling uses nearest-neighbour RGB luminance and ignores alpha.
        // Resolve that exact integer expression once per source pixel instead
        // of repeating it for every generated audio byte.
        _gray = new byte[checked(width * height)];
        for (int index = 0; index < _gray.Length; index++)
        {
            int offset = index * 4;
            _gray[index] = (byte)(((bgra[offset + 2] * 299)
                + (bgra[offset + 1] * 587) + (bgra[offset] * 114)) / 1000);
        }
    }

    internal int Width { get; }

    internal int Height { get; }

    internal static RasterImage LoadCalibrationReference() => Reference.Value;

    internal static RasterImage Load(string path)
    {
        BgraRaster raster = RasterCodec.LoadBgra32(path);
        return new RasterImage(raster.Width, raster.Height, raster.Pixels);
    }

    internal byte GrayAt(double pixelX, double pixelY, byte outside = 255)
    {
        int x = (int)Math.Round(pixelX);
        int y = (int)Math.Round(pixelY);
        return GrayAtPixel(x, y, outside);
    }

    internal bool TryGrayAtSimd(double pixelX, double pixelY, out byte gray)
    {
        gray = 255;
        // SIMD trig may differ by a few low bits. Recompute the original scalar
        // coordinates near every nearest-neighbour boundary (including edges).
        // CD retains its existing RGB luminance and alpha-ignoring semantics.
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY)
            || Math.Abs(pixelX) >= int.MaxValue || Math.Abs(pixelY) >= int.MaxValue)
            return false;
        double roundedX = Math.Round(pixelX);
        double roundedY = Math.Round(pixelY);
        double guard = Math.Max(Width, Height) * 1e-10;
        if (0.5 - Math.Abs(pixelX - roundedX) <= guard
            || 0.5 - Math.Abs(pixelY - roundedY) <= guard)
            return false;
        gray = GrayAtPixel((int)roundedX, (int)roundedY, 255);
        return true;
    }

    private byte GrayAtPixel(int x, int y, byte outside)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return outside;
        }

        return _gray[(y * Width) + x];
    }
}
