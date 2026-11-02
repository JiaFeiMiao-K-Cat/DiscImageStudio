namespace DiscImageStudio.ImageIO;

public sealed record BgraRaster(int Width, int Height, byte[] Pixels);

/// <summary>Reads the first frame as straight BGRA32 and writes a Gray8 or BGRA32 PNG.</summary>
public static partial class RasterCodec
{
    public static BgraRaster LoadBgra32(string path, long maxFileBytes = long.MaxValue, long maxPixels = int.MaxValue / 4)
    {
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        CheckFileSize(stream, maxFileBytes);
        return Decode(stream, maxPixels);
    }

    public static void ValidateSource(string path, long maxFileBytes, long maxPixels)
    {
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        CheckFileSize(stream, maxFileBytes);
        _ = ReadHeader(stream, maxPixels);
    }

    public static void SavePng(string path, int width, int height, byte[] pixels, bool grayscale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != checked(width * height * (grayscale ? 1 : 4)))
            throw new ArgumentException("Pixel buffer length does not match the dimensions and format.", nameof(pixels));
        WritePng(path, width, height, pixels, grayscale);
    }

    private static void CheckFileSize(FileStream stream, long maxBytes)
    {
        if (stream.Length > maxBytes)
            throw new ArgumentException($"The source image must be no larger than {maxBytes / 1_048_576d:G} MiB.", "path");
    }

    private static void CheckDimensions(int width, int height, long maxPixels)
    {
        if (width <= 0 || height <= 0 || (long)width * height > Math.Min(maxPixels, int.MaxValue / 4))
            throw new ArgumentException(FormattableString.Invariant(
                $"The source image must contain between 1 and {Math.Min(maxPixels, int.MaxValue / 4):N0} pixels."), "path");
    }
}
