using SkiaSharp;

namespace DiscImageStudio.Linux.Services;

/// <summary>One layer flattened for rendering: physical placement in millimetres relative to
/// the disc centre (+x right, +y down), rotation in degrees clockwise about the layer centre,
/// and the layer's size at scale 1.</summary>
public sealed record LayerRenderItem(
    string ImagePath,
    double BaseWidthMm,
    double BaseHeightMm,
    double TranslateXMm,
    double TranslateYMm,
    double RotationDeg,
    double Scale);

/// <summary>Skia port of the Windows DesignComposer: renders the layer stack into the square
/// source picture the engines consume. The square's centre is the disc centre and its side
/// spans 2 × canvasOuterRadiusMm, so a project composes to the same picture on both systems.</summary>
public static class Compositor
{
    /// <summary>Formal generation size, equal to RingImageQuality.GenerationSize on Windows.</summary>
    public const int GenerationSize = 8192;

    /// <summary>Debounced design composite used by the tone and projection previews.</summary>
    public const int PreviewSize = 2048;

    /// <summary>Same as the Windows thumbnail decode size stored in .discproj files.</summary>
    public const int ThumbnailSize = 256;

    /// <summary>Longest decoded side of a layer image. Larger sources are downscaled once at
    /// load, which stays invisible at the 8192 export size.</summary>
    public const int MaxDecodeSide = 4096;

    // Up to 4096² pixels (64 MiB) each; four covers a typical multi-layer design.
    private const int CacheCapacity = 4;
    private static readonly object CacheLock = new();
    private static readonly LinkedList<(string Key, SKImage Image)> Cache = new();

    /// <summary>Pixel size of an image without decoding it; null when unreadable.</summary>
    public static (int Width, int Height)? ProbeSize(string path)
    {
        try
        {
            using SKCodec? codec = SKCodec.Create(path);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return null;
            }

            return (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static void Render(
        IReadOnlyList<LayerRenderItem> layersTopFirst,
        double canvasOuterRadiusMm,
        int outputSize,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        using SKData png = RenderPng(layersTopFirst, canvasOuterRadiusMm, outputSize, cancellationToken);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = outputPath + ".part";
        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            png.SaveTo(stream);
        }

        File.Move(temporary, outputPath, overwrite: true);
    }

    public static string RenderBase64Png(IReadOnlyList<LayerRenderItem> layersTopFirst, double canvasOuterRadiusMm, int size)
    {
        using SKData png = RenderPng(layersTopFirst, canvasOuterRadiusMm, size, CancellationToken.None);
        return Convert.ToBase64String(png.AsSpan());
    }

    private static SKData RenderPng(
        IReadOnlyList<LayerRenderItem> layersTopFirst,
        double canvasOuterRadiusMm,
        int outputSize,
        CancellationToken cancellationToken)
    {
        if (canvasOuterRadiusMm <= 0 || !double.IsFinite(canvasOuterRadiusMm))
        {
            throw new ArgumentOutOfRangeException(nameof(canvasOuterRadiusMm));
        }

        double pixelsPerMm = outputSize / (2.0 * canvasOuterRadiusMm);
        double center = outputSize / 2.0;
        SKImageInfo info = new(outputSize, outputSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"Cannot allocate a {outputSize}×{outputSize} composite.");
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using SKPaint paint = new() { IsAntialias = true };

        // The layer list is ordered topmost-first, so painting runs bottom-up.
        for (int index = layersTopFirst.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LayerRenderItem item = layersTopFirst[index];
            double width = item.BaseWidthMm * item.Scale * pixelsPerMm;
            double height = item.BaseHeightMm * item.Scale * pixelsPerMm;
            if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height))
            {
                continue;
            }

            SKImage? image = GetImage(item.ImagePath);
            if (image is null)
            {
                continue;
            }

            bool downscaling = width < image.Width || height < image.Height;
            SKSamplingOptions sampling = downscaling
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                : new SKSamplingOptions(SKCubicResampler.Mitchell);
            canvas.Save();
            canvas.Translate(
                (float)(center + item.TranslateXMm * pixelsPerMm - width / 2),
                (float)(center + item.TranslateYMm * pixelsPerMm - height / 2));
            canvas.RotateDegrees((float)item.RotationDeg, (float)(width / 2), (float)(height / 2));
            canvas.DrawImage(image, new SKRect(0, 0, (float)width, (float)height), sampling, paint);
            canvas.Restore();
        }

        cancellationToken.ThrowIfCancellationRequested();
        using SKPixmap pixmap = surface.PeekPixels();
        // Fast zlib level: an 8192² composite is a cache file read once by the engine.
        SKData? encoded = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 2));
        return encoded ?? throw new InvalidOperationException("PNG encoding of the composite failed.");
    }

    /// <summary>Decoded, size-capped layer image shared by renders; keyed on path, size and
    /// modification time so an edited source file is picked up.</summary>
    public static SKImage? GetImage(string path)
    {
        string key;
        try
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                return null;
            }

            key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        lock (CacheLock)
        {
            for (LinkedListNode<(string Key, SKImage Image)>? node = Cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    Cache.Remove(node);
                    Cache.AddFirst(node);
                    return node.Value.Image;
                }
            }
        }

        SKImage? decoded = Decode(path);
        if (decoded is null)
        {
            return null;
        }

        lock (CacheLock)
        {
            Cache.AddFirst((key, decoded));
            while (Cache.Count > CacheCapacity)
            {
                // Evicted images may still be drawn by a render in flight; let the GC
                // finalize them instead of disposing under another thread.
                Cache.RemoveLast();
            }
        }

        return decoded;
    }

    private static SKImage? Decode(string path)
    {
        try
        {
            using SKBitmap? bitmap = SKBitmap.Decode(path);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return null;
            }

            int longest = Math.Max(bitmap.Width, bitmap.Height);
            if (longest <= MaxDecodeSide)
            {
                return SKImage.FromBitmap(bitmap);
            }

            double factor = (double)MaxDecodeSide / longest;
            SKImageInfo target = new(
                Math.Max(1, (int)Math.Round(bitmap.Width * factor)),
                Math.Max(1, (int)Math.Round(bitmap.Height * factor)),
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            using SKBitmap? resized = bitmap.Resize(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return resized is null ? null : SKImage.FromBitmap(resized);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
