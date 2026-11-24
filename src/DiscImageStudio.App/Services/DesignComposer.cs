using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using SharpVectors.Converters;
using SharpVectors.Dom;
using SharpVectors.Renderers.Wpf;

namespace DiscImageStudio.Services;

/// <summary>One layer flattened for rendering: physical placement in millimetres relative to
/// the disc centre plus the decoded image source. <paramref name="BaseWidthMm"/>/
/// <paramref name="BaseHeightMm"/> are the layer's physical size at scale 1.</summary>
public sealed record DesignLayerRenderItem(
    ImageSource Image,
    double BaseWidthMm,
    double BaseHeightMm,
    double TranslateXMm,
    double TranslateYMm,
    double RotationDeg,
    double Scale);

/// <summary>Renders the layer stack into the square source picture the generation pipeline
/// consumes: the square's centre is the disc centre and its side spans
/// 2 × <c>canvasOuterRadiusMm</c>. Everything here runs on an STA thread (the caller's UI
/// thread or a StaWorker thread) because DrawingVisual/RenderTargetBitmap require one.</summary>
internal static class DesignComposer
{
    /// <summary>Upper bound on the decoded side of a layer image; anything larger is
    /// downscaled once at load, which stays invisible at the 8192 export size.</summary>
    internal const int MaxDecodeWidth = 4096;

    /// <summary>Width of the decode used for the layer panel's thumbnails.</summary>
    internal const int ThumbnailDecodeWidth = 96;

    public static void Render(
        IReadOnlyList<DesignLayerRenderItem> layers,
        double canvasOuterRadiusMm,
        int outputSize,
        string outputPath)
    {
        if (canvasOuterRadiusMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasOuterRadiusMm));
        }

        double pixelsPerMm = outputSize / (2.0 * canvasOuterRadiusMm);
        double center = outputSize / 2.0;

        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, outputSize, outputSize));
            // The layer list is ordered topmost-first, so painting runs bottom-up.
            for (int index = layers.Count - 1; index >= 0; index--)
            {
                DesignLayerRenderItem item = layers[index];
                double widthPixels = item.BaseWidthMm * item.Scale * pixelsPerMm;
                double heightPixels = item.BaseHeightMm * item.Scale * pixelsPerMm;
                if (widthPixels <= 0 || heightPixels <= 0)
                {
                    continue;
                }

                TransformGroup transform = new();
                transform.Children.Add(new RotateTransform(item.RotationDeg, widthPixels / 2, heightPixels / 2));
                transform.Children.Add(new TranslateTransform(
                    center + item.TranslateXMm * pixelsPerMm - widthPixels / 2,
                    center + item.TranslateYMm * pixelsPerMm - heightPixels / 2));
                context.PushTransform(transform);
                context.DrawImage(item.Image, new Rect(0, 0, widthPixels, heightPixels));
                context.Pop();
            }
        }

        RenderTargetBitmap bitmap = new(outputSize, outputSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    /// <summary>Upper bound on imported SVG file size to guard XML parsing memory.</summary>
    internal const long MaxSvgFileBytes = 32L * 1024 * 1024;

    /// <summary>Maximum XML element nesting depth allowed in SVG inputs to guard against
    /// recursive DOM/drawing-group traversal stack overflows.</summary>
    internal const int MaxSvgElementDepth = 256;

    /// <summary>Maximum XML element count allowed in SVG inputs to bound drawing-tree
    /// expansion from many tiny elements (the file-size cap alone does not).</summary>
    internal const int MaxSvgElementCount = 100_000;

    internal static bool IsSupportedImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".svg";

    internal static bool IsSvgPath(string path) =>
        string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes an image frozen for cross-thread reads. Raster inputs are capped at
    /// <see cref="MaxDecodeWidth"/>; SVG inputs decode to a frozen vector <see cref="DrawingImage"/>
    /// without pre-rasterizing. Any decode failure returns null; WPF reports damaged
    /// image data as OutOfMemoryException, so that is caught on purpose.</summary>
    public static ImageSource? DecodeFrozenImage(string path)
    {
        if (IsSvgPath(path))
        {
            return DecodeFrozenSvg(path);
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = MaxDecodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException
                                              or OutOfMemoryException
                                              or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Small decode for the layer panel's list row. Kept next to the full decode so
    /// both can run on the same background thread while a project is opening.</summary>
    public static ImageSource? DecodeThumbnail(string path)
    {
        if (IsSvgPath(path))
        {
            return DecodeFrozenSvg(path);
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = ThumbnailDecodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // Thumbnails are cosmetic; this replaces the layer view model's old catch-all, so
            // a damaged file or an exotic codec must not fail the whole project open.
            return null;
        }
    }

    private static DrawingImage? DecodeFrozenSvg(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= 0 || stream.Length > MaxSvgFileBytes)
            {
                return null;
            }

            XmlReaderSettings xmlSettings = new()
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 1024,
                MaxCharactersInDocument = MaxSvgFileBytes,
            };

            WpfDrawingSettings drawingSettings = new()
            {
                IncludeRuntime = false,
                TextAsGeometry = true,
                EnsureViewboxSize = true,
                ExternalResourcesAccessMode = ExternalResourcesAccessModes.Ignore,
                OptimizePath = true,
            };

            using (XmlReader depthProbe = XmlReader.Create(stream, xmlSettings, string.Empty))
            {
                int elementCount = 0;
                while (depthProbe.Read())
                {
                    if (depthProbe.NodeType == XmlNodeType.Element)
                    {
                        elementCount++;
                        if (depthProbe.Depth > MaxSvgElementDepth || elementCount > MaxSvgElementCount)
                        {
                            return null;
                        }
                    }
                }
            }

            stream.Position = 0;

            using XmlReader xmlReader = XmlReader.Create(stream, xmlSettings, string.Empty);
            using FileSvgReader reader = new(drawingSettings);
            DrawingGroup? drawing = reader.Read(xmlReader);
            if (drawing is null)
            {
                return null;
            }

            Rect bounds = drawing.Bounds;
            if (bounds.IsEmpty
                || bounds.Width <= 0
                || bounds.Height <= 0
                || !double.IsFinite(bounds.Width)
                || !double.IsFinite(bounds.Height)
                || !HasVisibleDrawing(drawing))
            {
                return null;
            }

            DrawingImage image = new(drawing);
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // SharpVectors can throw a wide surface of internal exceptions on malformed or edge-case
            // SVGs; treat any decode failure cleanly at this boundary so the caller reports a toast.
            return null;
        }
    }

    // EnsureViewboxSize emits a transparent bounds spacer even when an SVG has no rendered geometry,
    // so filter for at least one non-transparent drawing primitive before accepting the file.
    private static bool HasVisibleDrawing(Drawing drawing) => drawing switch
    {
        DrawingGroup group => group.Children.Any(HasVisibleDrawing),
        GeometryDrawing geometry =>
            geometry.Brush is not (null or SolidColorBrush { Color.A: 0 })
            || geometry.Pen is { Brush: not (null or SolidColorBrush { Color.A: 0 }) },
        GlyphRunDrawing or ImageDrawing => true,
        _ => false,
    };
}
