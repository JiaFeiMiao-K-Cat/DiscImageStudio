using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;

namespace DiscImageStudio.Services;

/// <summary>Marks occupied filesystem space on preview PNGs only. The image payload and
/// burn streams never pass through this presentation layer.</summary>
internal static class FileRegionPreviewOverlay
{
    internal static void ApplyCd(string path, CdDiscParameters actual,
        uint drawingStartLba, uint drawingEndLbaExclusive, CancellationToken token = default)
    {
        actual.Validate();
        ValidateRange(checked((uint)actual.OutputSectors), drawingStartLba, drawingEndLbaExclusive);
        Apply(path, actual.InnerRadiusMm, actual.OutputOuterRadiusMm,
            actual.RadiusFromByte((long)drawingStartLba * CdDiscParameters.BytesPerSector),
            actual.RadiusFromByte((long)drawingEndLbaExclusive * CdDiscParameters.BytesPerSector),
            dvdPixelCoordinates: false, token, actual.PreviewImageOuterRadiusMm);
    }

    internal static void ApplyDvd(string path, DvdStreamingOptions actual,
        uint drawingStartLba, uint drawingEndLbaExclusive, CancellationToken token = default)
    {
        actual.Validate();
        ValidateRange(actual.OutputSectors, drawingStartLba, drawingEndLbaExclusive);
        double length = actual.TotalSectors * 38688.0 * actual.ChannelBitLengthNm * 1e-6;
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(actual.InnerRadiusMm,
            actual.OuterRadiusMm, length, actual.PitchLinear, actual.PitchQuadratic, actual.PitchCubic);
        Apply(path, actual.InnerRadiusMm, actual.OutputOuterRadiusMm,
            geometry.RadiusAtArcLength(length * ((double)drawingStartLba / actual.TotalSectors)),
            geometry.RadiusAtArcLength(length * ((double)drawingEndLbaExclusive / actual.TotalSectors)),
            dvdPixelCoordinates: true, token);
    }

    internal static void ApplyBluRay(string path, BluRayDiscParameters actual,
        uint drawingStartSector, uint drawingEndSectorExclusive, CancellationToken token = default)
    {
        actual.Validate();
        ValidateRange(actual.OutputSectors, drawingStartSector, drawingEndSectorExclusive);
        BluRayTrackGeometry geometry = new(actual);
        Apply(path, actual.InnerRadiusMm, actual.OutputOuterRadiusMm,
            geometry.AtSector(drawingStartSector).RadiusMm,
            geometry.AtSector(drawingEndSectorExclusive).RadiusMm,
            dvdPixelCoordinates: false, token);
    }

    /// <summary>Opaque colours of the CD-Extra bands; the legends use the same values.</summary>
    internal static readonly Color MusicColor = Color.FromRgb(0xF5, 0xA6, 0x23);
    internal static readonly Color SessionGapColor = Color.FromRgb(0x7A, 0x7A, 0x85);
    internal static readonly Color FileColor = Color.FromRgb(0x33, 0x85, 0xFF);

    /// <summary>CD-Extra preview bands: the music session (amber), the drive-written session
    /// gap (grey) and the picture session's filesystem or file data (blue). The drawing range
    /// stays as rendered.</summary>
    internal static void ApplyCdExtra(string path, CdDiscParameters actual, CdExtraLayout layout,
        CdDataLayoutPlan dataPlan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(dataPlan);
        ApplyCdExtra(path, actual, layout.MusicEndLbaExclusive, layout.Session2StartLba,
            dataPlan.DrawingStartLba, dataPlan.DrawingEndLbaExclusive,
            dataPlan.VolumeStartLba + dataPlan.TrackSectors, token);
    }

    /// <summary>Outer-music preview band: the music (amber) around the picture. The preview
    /// renders only the picture, so the band shows where the disc carries music instead.</summary>
    internal static void ApplyOuterMusic(string path, CdDiscParameters actual, CdExtraLayout layout,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        actual.Validate();
        if (!layout.MusicAtOuterRing) throw new ArgumentException("The layout keeps the music on the inner rings.", nameof(layout));
        double Radius(long lba) => actual.RadiusFromByte(lba * CdDiscParameters.BytesPerSector);
        List<(double Inner, double Outer, Color Colour)> bands =
        [
            (Radius(layout.MusicStartLba), Radius(layout.MusicEndLbaExclusive), MusicColor),
        ];
        ApplyBands(path, actual.InnerRadiusMm, actual.OutputOuterRadiusMm, bands, token,
            actual.PreviewImageOuterRadiusMm);
    }

    /// <summary>Band overlay from recorded addresses, e.g. a CD-Extra sidecar.</summary>
    internal static void ApplyCdExtra(string path, CdDiscParameters actual, long musicEndLbaExclusive,
        long session2StartLba, long drawingStartLba, long drawingEndLbaExclusive, long trackEndLbaExclusive,
        CancellationToken token = default)
    {
        actual.Validate();
        if (musicEndLbaExclusive <= 0 || session2StartLba < musicEndLbaExclusive
            || drawingStartLba < session2StartLba || drawingEndLbaExclusive <= drawingStartLba
            || trackEndLbaExclusive < drawingEndLbaExclusive)
            throw new ArgumentOutOfRangeException(nameof(session2StartLba), "CD-Extra preview bands are out of order.");
        double Radius(long lba) => actual.RadiusFromByte(lba * CdDiscParameters.BytesPerSector);
        double inner = actual.InnerRadiusMm;
        double outer = actual.OutputOuterRadiusMm;
        double musicEnd = Radius(musicEndLbaExclusive);
        double session2 = Radius(session2StartLba);
        double drawingStart = Radius(drawingStartLba);
        double drawingEnd = Radius(drawingEndLbaExclusive);
        double trackEnd = Radius(trackEndLbaExclusive);
        List<(double Inner, double Outer, Color Colour)> bands =
        [
            (inner, musicEnd, MusicColor),
            (musicEnd, session2, SessionGapColor),
            (session2, drawingStart, FileColor),
            (drawingEnd, trackEnd, FileColor),
        ];
        ApplyBands(path, inner, outer, bands, token, actual.PreviewImageOuterRadiusMm);
    }

    private static void ApplyBands(string path, double inner, double outer,
        IReadOnlyList<(double Inner, double Outer, Color Colour)> bands, CancellationToken token,
        double cdImageOuterRadius)
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
        double centre = size / 2.0;
        double mmPerPixel = cdImageOuterRadius * 2 / size;
        double innerSquared = inner * inner, outerSquared = outer * outer;
        (double Inner, double Outer, Color Colour)[] squared = bands
            .Where(band => band.Outer > band.Inner)
            .Select(band => (band.Inner * band.Inner, band.Outer * band.Outer, band.Colour))
            .ToArray();
        for (int y = 0; y < size; y++)
        {
            token.ThrowIfCancellationRequested();
            double dy = (y - centre) * mmPerPixel;
            for (int x = 0; x < size; x++)
            {
                double dx = (x - centre) * mmPerPixel;
                double radiusSquared = dx * dx + dy * dy;
                if (radiusSquared < innerSquared || radiusSquared >= outerSquared) continue;
                foreach ((double bandInner, double bandOuter, Color colour) in squared)
                {
                    if (radiusSquared < bandInner || radiusSquared >= bandOuter) continue;
                    int offset = y * stride + x * 4;
                    pixels[offset] = colour.B;
                    pixels[offset + 1] = colour.G;
                    pixels[offset + 2] = colour.R;
                    pixels[offset + 3] = 0xFF;
                    break;
                }
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

    private static void ValidateRange(uint total, uint start, uint end)
    {
        if (start >= end || end > total)
            throw new ArgumentOutOfRangeException(nameof(start), "Preview drawing range must lie inside the disc.");
    }

    private static void Apply(string path, double inner, double outer,
        double drawingInner, double drawingOuter, bool dvdPixelCoordinates, CancellationToken token,
        double? cdImageOuterRadius = null)
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
        double centre = cdImageOuterRadius.HasValue ? size / 2.0 : (size - 1) / 2.0;
        double mmPerPixel = (cdImageOuterRadius ?? outer) * 2 / (dvdPixelCoordinates ? size - 1 : size);
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
                // Theme blue #3385FF, matching the preview legend and the design guide.
                // File data is deliberately not passed through the black/white classifier.
                pixels[offset] = 0xFF;
                pixels[offset + 1] = 0x85;
                pixels[offset + 2] = 0x33;
                pixels[offset + 3] = 0xFF;
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
