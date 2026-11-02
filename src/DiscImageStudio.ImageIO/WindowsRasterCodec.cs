using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiscImageStudio.ImageIO;

public static partial class RasterCodec
{
    private static BitmapFrame ReadHeader(Stream stream, long maxPixels)
    {
        BitmapDecoder decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0)
            throw new ArgumentException("The source image contains no frames.", "path");
        BitmapFrame frame = decoder.Frames[0];
        CheckDimensions(frame.PixelWidth, frame.PixelHeight, maxPixels);
        return frame;
    }

    private static BgraRaster Decode(Stream stream, long maxPixels)
    {
        BitmapFrame frame = ReadHeader(stream, maxPixels);
        BitmapSource converted = frame.Format == PixelFormats.Bgra32
            ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(frame.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * frame.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return new(frame.PixelWidth, frame.PixelHeight, pixels);
    }

    private static void WritePng(string path, int width, int height, byte[] pixels, bool grayscale)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96,
            grayscale ? PixelFormats.Gray8 : PixelFormats.Bgra32, null, pixels, checked(width * (grayscale ? 1 : 4)));
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }
}
