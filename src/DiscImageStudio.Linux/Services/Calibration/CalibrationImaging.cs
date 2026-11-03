using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using SkiaSharp;

namespace DiscImageStudio.Linux.Services.Calibration;

/// <summary>A straight-alpha BGRA32 raster: the format the rectifier reads and the session
/// stores. Calibration geometry works in its pixel coordinates (pixel centres at integers).</summary>
public sealed class BgraImage
{
    public BgraImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Invalid image buffer.");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>A copy Avalonia can draw; safe to call from any thread.</summary>
    public Bitmap ToBitmap()
    {
        GCHandle handle = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(),
                new PixelSize(Width, Height), new Vector(96, 96), Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }
}

public sealed record CalibrationPhotoContours(IReadOnlyList<CalibrationPoint> Outer, IReadOnlyList<CalibrationPoint> Hole);

/// <summary>Skia port of the Windows CalibrationPhotoService and CalibrationPatternRenderer:
/// bounded photo decoding, the suggested starting outlines, perspective resampling of the
/// photo onto the disc plane, and the target pattern previews and PNG export.</summary>
public static class CalibrationImaging
{
    public const int PhotoMaxDimension = 1600;
    public const int RectifiedSize = 1400;

    /// <summary>The physical centre hole of a standard disc: 15 mm across.</summary>
    public const double HoleRadiusMm = 7.5;
    public const int TargetPreviewSize = 900;

    public static BgraImage LoadPhoto(string path, int maxDimension = PhotoMaxDimension, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Choose a photo of the disc.", nameof(path));
        }

        FileInfo file = new(path);
        if (file.Length > 256L * 1024 * 1024)
        {
            throw new ArgumentException("The photo is too large; use an image under 256 MB.");
        }

        using SKCodec codec = SKCodec.Create(path)
            ?? throw new ArgumentException("This file is not a readable image (PNG, JPEG, WebP, BMP or GIF).");
        int width = codec.Info.Width, height = codec.Info.Height;
        if (width < 16 || height < 16)
        {
            throw new ArgumentException("The photo is too small.");
        }

        double factor = Math.Min(1.0, (double)maxDimension / Math.Max(width, height));
        int targetWidth = Math.Max(1, (int)Math.Round(width * factor));
        int targetHeight = Math.Max(1, (int)Math.Round(height * factor));

        // JPEG can decode directly at a reduced scale, which keeps 50 MP phone photos cheap.
        SKSizeI decodeSize = factor < 1 ? codec.GetScaledDimensions((float)factor) : codec.Info.Size;
        if (decodeSize.Width < targetWidth || decodeSize.Height < targetHeight)
        {
            decodeSize = codec.Info.Size;
        }

        SKImageInfo decodeInfo = new(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using SKBitmap decoded = new(decodeInfo);
        SKCodecResult result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new ArgumentException($"The photo could not be decoded ({result}).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        SKBitmap final = decoded;
        SKBitmap? resized = null;
        if (decoded.Width != targetWidth || decoded.Height != targetHeight)
        {
            resized = decoded.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                ?? throw new InvalidOperationException("The photo could not be resized.");
            final = resized;
        }

        try
        {
            return FromBitmap(final);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    public static string EncodePngBase64(BgraImage image)
    {
        using SKData data = Encode(image.Pixels, image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        return Convert.ToBase64String(data.AsSpan());
    }

    /// <summary>The photo embedded in a session, with the same size bounds as Windows.</summary>
    public static BgraImage DecodePngBase64(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        using SKBitmap? bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null || bitmap.Width is < 32 or > 2000 || bitmap.Height is < 32 or > 2000)
        {
            throw new ArgumentException("The photo stored in the calibration record has an invalid size. Import the photo again.");
        }

        return FromBitmap(bitmap);
    }

    /// <summary>Starting outlines: an ellipse filling 86% of the photo for the outer edge and
    /// one eighth of it for the hole, as on Windows. They are only a starting point.</summary>
    public static CalibrationPhotoContours SuggestContours(int width, int height, int count = 8)
    {
        double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
        double rx = width * 0.43, ry = height * 0.43;
        CalibrationPoint[] Make(double ratio) => Enumerable.Range(0, count).Select(index =>
        {
            double angle = -Math.PI / 2 + 2 * Math.PI * index / count;
            return new CalibrationPoint(cx + rx * ratio * Math.Cos(angle), cy + ry * ratio * Math.Sin(angle));
        }).ToArray();
        return new CalibrationPhotoContours(Make(1), Make(0.125));
    }

    /// <summary>Resamples the photo onto the disc plane: a square of 2 × canvas radius around
    /// the disc centre, white outside the canvas circle or the photo.</summary>
    public static BgraImage Rectify(BgraImage image, CalibrationPhotoTransform transform, double canvasOuterRadiusMm,
        int pixelSize = RectifiedSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(transform);
        if (!double.IsFinite(canvasOuterRadiusMm) || canvasOuterRadiusMm is <= 0 or > 150)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasOuterRadiusMm), "Invalid rectification canvas radius.");
        }

        if (!transform.IsValidForDisc(canvasOuterRadiusMm))
        {
            throw new ArgumentException("The photo transform degenerates inside the canvas. Adjust the outlines and rectify again.");
        }

        int width = image.Width, height = image.Height, sourceStride = width * 4, stride = pixelSize * 4;
        byte[] source = image.Pixels;
        byte[] pixels = new byte[checked(stride * pixelSize)];
        double[] matrix = transform.DiscToSourceMatrix9;
        double step = 2 * canvasOuterRadiusMm / pixelSize;
        Parallel.For(0, pixelSize, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            double ym = (y + 0.5) * step - canvasOuterRadiusMm;
            for (int x = 0; x < pixelSize; x++)
            {
                double xm = (x + 0.5) * step - canvasOuterRadiusMm;
                double divisor = matrix[6] * xm + matrix[7] * ym + matrix[8];
                int destination = y * stride + x * 4;
                double sx = (matrix[0] * xm + matrix[1] * ym + matrix[2]) / divisor;
                double sy = (matrix[3] * xm + matrix[4] * ym + matrix[5]) / divisor;
                if (xm * xm + ym * ym > canvasOuterRadiusMm * canvasOuterRadiusMm
                    || !double.IsFinite(sx) || !double.IsFinite(sy) || sx < 0 || sy < 0 || sx > width - 1 || sy > height - 1)
                {
                    // Transparent outside the disc and the photo, so the canvas backdrop shows.
                    pixels[destination] = pixels[destination + 1] = pixels[destination + 2] = 255;
                    pixels[destination + 3] = 0;
                    continue;
                }

                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
                double fx = sx - x0, fy = sy - y0;
                for (int channel = 0; channel < 4; channel++)
                {
                    double value = source[y0 * sourceStride + x0 * 4 + channel] * (1 - fx) * (1 - fy)
                        + source[y0 * sourceStride + x1 * 4 + channel] * fx * (1 - fy)
                        + source[y1 * sourceStride + x0 * 4 + channel] * (1 - fx) * fy
                        + source[y1 * sourceStride + x1 * 4 + channel] * fx * fy;
                    pixels[destination + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
        });
        cancellationToken.ThrowIfCancellationRequested();
        return new BgraImage(pixelSize, pixelSize, pixels);
    }

    /// <summary>What the target writes on the disc, on a canvas of 2 × canvas radius, binarised
    /// exactly as the generators binarise it.</summary>
    public static BgraImage RenderTarget(CalibrationTarget target, double canvasRadius, int size = TargetPreviewSize,
        CancellationToken cancellationToken = default)
    {
        CalibrationParameters bounds = target.Parameters;
        byte[] pixels = new byte[checked(size * size * 4)];
        double step = 2 * canvasRadius / size;
        double inner2 = bounds.InnerRadiusMm * bounds.InnerRadiusMm - 1e-10;
        double outer2 = bounds.OuterRadiusMm * bounds.OuterRadiusMm + 1e-10;
        // Drawn as a disc: transparent outside the physical edge and in the centre hole.
        double disc2 = canvasRadius * canvasRadius, hole2 = HoleRadiusMm * HoleRadiusMm;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            double ym = (y + 0.5) * step - canvasRadius;
            for (int x = 0; x < size; x++)
            {
                double xm = (x + 0.5) * step - canvasRadius;
                double r2 = xm * xm + ym * ym;
                byte value = 255;
                if (r2 >= inner2 && r2 <= outer2)
                {
                    value = BinaryGrayscale.Quantize(target.Sample(xm, ym));
                }

                int offset = (y * size + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                pixels[offset + 3] = r2 > disc2 || r2 < hole2 ? (byte)0 : (byte)255;
            }
        });
        return new BgraImage(size, size, pixels);
    }

    /// <summary>The reference pattern's original 2400 × 2400 pixels, cropped to the drawing
    /// band in the encoder's image coordinates (the Windows "Export pattern PNG").</summary>
    public static void ExportPatternPng(CalibrationTarget target, string path, CancellationToken cancellationToken = default)
    {
        const int size = CalibrationReferencePattern.PixelSize;
        byte[] pixels = CalibrationReferencePattern.ReadGrayPixels();
        CalibrationParameters parameters = target.Parameters;
        double inner2 = target.DrawingInnerRadiusMm * target.DrawingInnerRadiusMm;
        double outer2 = target.DrawingOuterRadiusMm * target.DrawingOuterRadiusMm;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int x = 0; x < size; x++)
            {
                CalibrationPoint p = CalibrationReferencePattern.PixelToDisc(x, y, parameters.OuterRadiusMm, parameters.Kind);
                double r2 = p.X * p.X + p.Y * p.Y;
                if (r2 < inner2 - 1e-10 || r2 > outer2 + 1e-10)
                {
                    pixels[y * size + x] = 255;
                }
            }
        });
        using SKData data = Encode(pixels, size, size, SKColorType.Gray8, SKAlphaType.Opaque);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
    }

    private static BgraImage FromBitmap(SKBitmap bitmap)
    {
        SKImageInfo info = new(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        byte[] pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using SKPixmap source = bitmap.PeekPixels();
            if (!source.ReadPixels(info, handle.AddrOfPinnedObject(), bitmap.Width * 4, 0, 0))
            {
                throw new InvalidOperationException("The photo pixels could not be read.");
            }
        }
        finally
        {
            handle.Free();
        }

        return new BgraImage(bitmap.Width, bitmap.Height, pixels);
    }

    private static SKData Encode(byte[] pixels, int width, int height, SKColorType colorType, SKAlphaType alphaType)
    {
        int bytesPerPixel = colorType == SKColorType.Gray8 ? 1 : 4;
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using SKPixmap pixmap = new(new SKImageInfo(width, height, colorType, alphaType), handle.AddrOfPinnedObject(), width * bytesPerPixel);
            return pixmap.Encode(SKPngEncoderOptions.Default) ?? throw new InvalidOperationException("PNG encoding failed.");
        }
        finally
        {
            handle.Free();
        }
    }
}
