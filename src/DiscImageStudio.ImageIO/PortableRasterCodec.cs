using StbImageSharp;
using StbImageWriteSharp;
using WriteComponents = StbImageWriteSharp.ColorComponents;

namespace DiscImageStudio.ImageIO;

public static partial class RasterCodec
{
    private static ImageInfo ReadHeader(Stream stream, long maxPixels)
    {
        ImageInfo info = ImageInfo.FromStream(stream)
            ?? throw new InvalidDataException("Unsupported or invalid image. Use a PNG, JPEG, BMP or GIF.");
        CheckDimensions(info.Width, info.Height, maxPixels);
        return info;
    }

    private static BgraRaster Decode(Stream stream, long maxPixels)
    {
        _ = ReadHeader(stream, maxPixels);
        stream.Position = 0;
        ImageResult image = ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        SwapRedAndBlue(image.Data);
        return new(image.Width, image.Height, image.Data);
    }

    private static void WritePng(string path, int width, int height, byte[] pixels, bool grayscale)
    {
        if (!grayscale)
        {
            pixels = (byte[])pixels.Clone();
            SwapRedAndBlue(pixels);
        }
        using FileStream output = File.Create(path);
        new ImageWriter().WritePng(pixels, width, height,
            grayscale ? WriteComponents.Grey : WriteComponents.RedGreenBlueAlpha, output);
    }

    private static void SwapRedAndBlue(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }
}
