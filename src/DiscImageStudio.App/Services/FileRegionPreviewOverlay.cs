using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;

namespace DiscImageStudio.Services;

/// <summary>Marks occupied filesystem space on preview PNGs only. The image payload and
/// burn streams never pass through this presentation layer.</summary>
internal static class FileRegionPreviewOverlay
{
    internal static void ApplyDvd(string path, DvdStreamingOptions actual,
        uint drawingStartLba, uint drawingEndLbaExclusive, CancellationToken token = default)
    {
        actual.Validate();
        ValidateRange(actual.TotalSectors, drawingStartLba, drawingEndLbaExclusive);
        double length = actual.TotalSectors * 38688.0 * actual.ChannelBitLengthNm * 1e-6;
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(actual.InnerRadiusMm,
            actual.OuterRadiusMm, length, actual.PitchLinear, actual.PitchQuadratic, actual.PitchCubic);
        Apply(path, actual.InnerRadiusMm, actual.OuterRadiusMm,
            geometry.RadiusAtArcLength(length * ((double)drawingStartLba / actual.TotalSectors)),
            geometry.RadiusAtArcLength(length * ((double)drawingEndLbaExclusive / actual.TotalSectors)),
            dvdPixelCoordinates: true, token);
    }

    internal static void ApplyBluRay(string path, BluRayDiscParameters actual,
        uint drawingStartSector, uint drawingEndSectorExclusive, CancellationToken token = default)
    {
        actual.Validate();
        ValidateRange(actual.TotalSectors, drawingStartSector, drawingEndSectorExclusive);
        BluRayTrackGeometry geometry = new(actual);
        Apply(path, actual.InnerRadiusMm, actual.OuterRadiusMm,
            geometry.AtSector(drawingStartSector).RadiusMm,
            geometry.AtSector(drawingEndSectorExclusive).RadiusMm,
            dvdPixelCoordinates: false, token);
    }

    private static void ValidateRange(uint total, uint start, uint end)
    {
        if (start >= end || end > total)
            throw new ArgumentOutOfRangeException(nameof(start), "Preview drawing range must lie inside the disc.");
    }

    private static void Apply(string path, double inner, double outer,
        double drawingInner, double drawingOuter, bool dvdPixelCoordinates, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BitmapSource source;
        using (FileStream input = File.OpenRead(path))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(input,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
        }
        if (source.PixelWidth != source.PixelHeight)
            throw new ArgumentException("Disc previews must be square.", nameof(path));
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int size = source.PixelWidth;
        int stride = checked(size * 4);
        byte[] pixels = new byte[checked(stride * size)];
        source.CopyPixels(pixels, stride, 0);
        double centre = (size - 1) / 2.0;
        double mmPerPixel = outer * 2 / (dvdPixelCoordinates ? size - 1 : size);
        double innerSquared = inner * inner, outerSquared = outer * outer;
        double drawingInnerSquared = drawingInner * drawingInner;
        double drawingOuterSquared = drawingOuter * drawingOuter;
        for (int y = 0; y < size; y++)
        {
            token.ThrowIfCancellationRequested();
            double dy = (y - centre) * mmPerPixel;
            for (int x = 0; x < size; x++)
            {
                double dx = (x - centre) * mmPerPixel;
                double radiusSquared = dx * dx + dy * dy;
                if (radiusSquared < innerSquared || radiusSquared >= outerSquared
                    || radiusSquared >= drawingInnerSquared && radiusSquared < drawingOuterSquared)
                    continue;
                int offset = y * stride + x * 4;
                // #FACC15, matching the preview legend. File data is deliberately not
                // passed through the black/white pattern classifier.
                pixels[offset] = 21;
                pixels[offset + 1] = 204;
                pixels[offset + 2] = 250;
                pixels[offset + 3] = 255;
            }
        }
        token.ThrowIfCancellationRequested();
        BitmapSource result = BitmapSource.Create(size, size, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }
}
