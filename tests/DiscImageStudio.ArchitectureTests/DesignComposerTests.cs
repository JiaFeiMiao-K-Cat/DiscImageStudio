using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class DesignComposerTests
{
    /// <summary>The design page's composite must come out as the square, alpha-carrying
    /// picture the generation engines already accept: side = output size, layers painted
    /// bottom-up, and the mm→px mapping centred on the disc.</summary>
    internal static void Run()
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try
            {
                RunOnSta();
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        Console.WriteLine("design-composer: passed (square alpha composite, centre mapping, rotation)");
    }

    private static void RunOnSta()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string layerPath = Path.Combine(directory, "layer.png");
            WritePng(layerPath, 100, 60, (_, _) => Color.FromArgb(200, 200, 40, 40));

            var layer = Decode(layerPath);
            Require(layer is not null, "layer decodes");

            // One layer, scale 1: the fitted base spans 2 × canvasOuterRadius on its larger
            // side, so at translate 0 the image centre sits on the disc centre.
            var items = new List<DesignLayerRenderItem>
            {
                new(layer!, BaseWidthMm: 115.0, BaseHeightMm: 69.0, TranslateXMm: 0, TranslateYMm: 0,
                    RotationDeg: 0, Scale: 1.0),
            };
            string compositePath = Path.Combine(directory, "composite.png");
            DesignComposer.Render(items, 57.5, 512, compositePath);

            var composite = Decode(compositePath);
            Require(composite is not null, "composite decodes");
            Require(composite!.PixelWidth == 512 && composite.PixelHeight == 512, "composite is square at output size");
            Require(HasAlpha(composite), "composite keeps alpha");

            // Centre pixel: the layer covers it, so it must carry the layer colour.
            Color center = Sample(composite, 256, 256);
            Require(center.R > 150 && center.G < 100 && center.B < 100, "layer painted at disc centre");

            // Rotation: 90° turns the wide footprint tall, so a point right of the centre
            // (inside the unrotated footprint, outside the rotated one) turns transparent
            // while the point above the centre stays covered.
            var rotated = new List<DesignLayerRenderItem>
            {
                new(layer!, 115.0, 69.0, 0, 0, 90, 1.0),
            };
            string rotatedPath = Path.Combine(directory, "rotated.png");
            DesignComposer.Render(rotated, 57.5, 512, rotatedPath);
            var rotatedBitmap = Decode(rotatedPath);
            Require(rotatedBitmap is not null, "rotated composite decodes");
            Require(Sample(rotatedBitmap!, 456, 256).A < 32, "rotation moves the footprint");
            // The source layer is alpha 200; premultiplied output keeps that alpha inside
            // the rotated footprint.
            Require(Sample(rotatedBitmap!, 256, 216).A > 150, "rotation keeps the covered centre");
            VerifyUsableCapacityRoundtrip(directory, layerPath);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static void VerifyUsableCapacityRoundtrip(string directory, string widePath)
    {
        string tallPath = Path.Combine(directory, "tall-layer.png");
        WritePng(tallPath, 60, 100, (x, y) => Color.FromArgb(180, (byte)(x * 3), (byte)(y * 2), 220));
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
        try
        {
            foreach (OpticalDiscFamily family in new[]
                { OpticalDiscFamily.CompactDisc, OpticalDiscFamily.Dvd, OpticalDiscFamily.BluRay })
            {
                DiscParametersState state = shell.State;
                state.CdInnerRadius = state.DvdInnerRadius = state.BdInnerRadius = "24.5";
                state.CdOuterRadius = state.DvdOuterRadius = state.BdOuterRadius = "59.5";
                state.CdSectors = state.DvdTotalSectors = state.BdTotalSectors = "256";
                state.CdUsableSectors = state.DvdUsableSectors = state.BdUsableSectors = "256";
                DesignViewModel design = shell.Design;
                design.SetDiscFamily(family);
                design.LoadLayers([
                    new ProjectLayer("wide", "Wide", widePath, true, -2.5, 1.25, 19, .625),
                    new ProjectLayer("tall", "Tall", tallPath, true, 5.5, -3.75, -11, .375),
                ]);
                var dimensions = design.Layers.ToDictionary(layer => layer.Name,
                    layer => (Width: layer.BaseWidthMm * layer.Scale, Height: layer.BaseHeightMm * layer.Scale));
                double originalCanvas = design.ContentOuterRadiusMm;
                switch (family)
                {
                    case OpticalDiscFamily.Dvd: state.DvdUsableSectors = "128"; break;
                    case OpticalDiscFamily.BluRay: state.BdUsableSectors = "128"; break;
                    default: state.CdUsableSectors = "128"; break;
                }
                design.RefreshGeometry();
                Require(design.ContentOuterRadiusMm < originalCanvas, family + " usable count shrinks the canvas");
                byte[] expected = RenderLayers(design, Path.Combine(directory, family + "-before.png"));
                IReadOnlyList<ProjectLayer> saved = design.CaptureLayers();
                double[] scales = saved.Select(layer => layer.Scale).ToArray();
                for (int round = 0; round < 3; round++)
                {
                    design.LoadLayers(saved);
                    foreach (DesignLayerViewModel layer in design.Layers)
                    {
                        var original = dimensions[layer.Name];
                        Require(Math.Abs(layer.BaseWidthMm * layer.Scale - original.Width) < 1e-9
                            && Math.Abs(layer.BaseHeightMm * layer.Scale - original.Height) < 1e-9,
                            family + " reload preserves the layer's physical dimensions");
                    }
                    byte[] actual = RenderLayers(design, Path.Combine(directory, family + "-after.png"));
                    Require(expected.SequenceEqual(actual), family + " reload preserves every composite pixel");
                    saved = design.CaptureLayers();
                    Require(scales.SequenceEqual(saved.Select(layer => layer.Scale)),
                        family + " repeated save/load does not accumulate scale changes");
                }
                design.ClearLayers();
            }
        }
        finally { shell.LivePreview.OnWindowClosed(); }
    }

    private static byte[] RenderLayers(DesignViewModel design, string path)
    {
        DesignComposer.Render(design.Layers.Select(layer => new DesignLayerRenderItem(
            layer.Image!, layer.BaseWidthMm, layer.BaseHeightMm, layer.TranslateXMm,
            layer.TranslateYMm, layer.RotationDeg, layer.Scale)).ToArray(),
            design.CanvasOuterRadiusMm, 512, path);
        BitmapSource bitmap = Decode(path)!;
        byte[] pixels = new byte[512 * 512 * 4];
        bitmap.CopyPixels(pixels, 512 * 4, 0);
        return pixels;
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            BitmapImage bitmap = new();
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasAlpha(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels.AsEnumerable().Any(value => value < 255);
    }

    private static Color Sample(BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static void WritePng(string path, int width, int height, Func<int, int, Color> paint)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Color color = paint(x, y);
                int offset = y * stride + x * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = color.A;
            }
        }

        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("DesignComposer: " + message);
        }
    }
}
