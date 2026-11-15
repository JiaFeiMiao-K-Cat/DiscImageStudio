using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class SvgImportTests
{
    internal static void Run()
    {
        TestVectorDecodeAndRendering();
        TestDragDropAndSafety();
        TestMtaPreloadAndProjectRoundtrip();
        Console.WriteLine("svg-import: vector decode, viewBox aspect, drag-drop, safety, MTA preload and project roundtrip passed");
    }

    private static void TestVectorDecodeAndRendering()
    {
        string root = CreateTempDirectory();
        try
        {
            string viewBoxSvgPath = Path.Combine(root, "viewbox.svg");
            File.WriteAllText(viewBoxSvgPath, """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100">
                  <defs>
                    <linearGradient id="g" x1="0%" y1="0%" x2="100%" y2="0%">
                      <stop offset="0%" stop-color="#ff0000"/>
                      <stop offset="100%" stop-color="#0000ff"/>
                    </linearGradient>
                  </defs>
                  <rect x="20" y="20" width="60" height="60" fill="#ff0000"/>
                  <rect x="20" y="82" width="60" height="12" fill="url(#g)"/>
                  <text x="110" y="45" font-size="18" font-family="Arial" fill="#00aa00">SVG</text>
                </svg>
                """);

            ImageSource? decoded = DesignComposer.DecodeFrozenImage(viewBoxSvgPath);
            Require(decoded is DrawingImage && decoded is not BitmapSource,
                "svg decodes as a DrawingImage rather than pre-rasterizing into a BitmapSource");
            Require(decoded!.IsFrozen, "decoded svg DrawingImage is frozen for cross-thread sharing");
            Require(decoded.Width > 0 && decoded.Height > 0
                && Math.Abs(decoded.Width / decoded.Height - 2.0) < 1e-6,
                $"svg preserves explicit 2:1 viewBox aspect ratio (got {decoded.Width}x{decoded.Height})");

            ImageSource? thumbnail = DesignComposer.DecodeThumbnail(viewBoxSvgPath);
            Require(thumbnail is DrawingImage && thumbnail.IsFrozen,
                "svg thumbnail uses frozen vector DrawingImage");

            // Render onto a 512x512 disc composite and verify viewBox whitespace is preserved.
            string compositePath = Path.Combine(root, "composite.png");
            DesignComposer.Render(
                [new DesignLayerRenderItem(decoded, 80, 40, 0, 0, 0, 1)],
                57.5,
                512,
                compositePath);
            BitmapSource composite = DecodePng(compositePath)!;
            Require(HasAlpha(composite), "svg composite keeps transparent background");

            // Left side (x in [20, 80] of 200 -> left of disc center, y in [20, 80] of 100 -> vertically centered):
            // pixel at (210, 256) sits inside the red rectangle, whereas (310, 280) sits in empty transparent viewBox margin.
            Color redPixel = Sample(composite, 210, 256);
            Color rightMargin = Sample(composite, 310, 280);
            Require(redPixel.A > 200 && redPixel.R > 200 && redPixel.G < 40 && redPixel.B < 40,
                $"off-center svg rectangle renders on left half (got ARGB={redPixel.A},{redPixel.R},{redPixel.G},{redPixel.B})");
            Require(rightMargin.A == 0,
                $"transparent viewBox margin on right half stays uncropped and transparent (got A={rightMargin.A})");

            // SVG without viewBox still preserves width/height aspect ratio.
            string sizedSvgPath = Path.Combine(root, "no-viewbox.svg");
            File.WriteAllText(sizedSvgPath, """
                <svg xmlns="http://www.w3.org/2000/svg" width="160" height="80">
                  <rect x="10" y="10" width="40" height="40" fill="#0080ff"/>
                </svg>
                """);
            ImageSource? sized = DesignComposer.DecodeFrozenImage(sizedSvgPath);
            Require(sized is DrawingImage && sized.IsFrozen, "svg without viewBox still decodes as frozen DrawingImage");
            Require(Math.Abs(sized!.Width / sized.Height - 2.0) < 1e-6,
                $"svg without viewBox preserves width/height 2:1 aspect (got {sized.Width}x{sized.Height})");

            // Empty / invalid / non-renderable SVG variants fail cleanly.
            string emptySvgPath = Path.Combine(root, "empty.svg");
            File.WriteAllText(emptySvgPath, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"/>");
            Require(DesignComposer.DecodeFrozenImage(emptySvgPath) is null, "empty svg without artwork returns null");

            string malformedSvgPath = Path.Combine(root, "malformed.svg");
            File.WriteAllText(malformedSvgPath, "<svg><rect");
            Require(DesignComposer.DecodeFrozenImage(malformedSvgPath) is null, "malformed xml svg returns null");

            string deepNestSvgPath = Path.Combine(root, "deep-nest.svg");
            int overflowDepth = DesignComposer.MaxSvgElementDepth + 16;
            string openGroups = string.Concat(Enumerable.Repeat("<g>", overflowDepth));
            string closeGroups = string.Concat(Enumerable.Repeat("</g>", overflowDepth));
            File.WriteAllText(deepNestSvgPath,
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\">{openGroups}<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"#000\"/>{closeGroups}</svg>");
            Require(DesignComposer.DecodeFrozenImage(deepNestSvgPath) is null,
                "excessively nested svg groups fail cleanly instead of overflowing the stack");

            string wideNestSvgPath = Path.Combine(root, "wide-nest.svg");
            int overflowCount = DesignComposer.MaxSvgElementCount + 1;
            string tinyRects = string.Concat(Enumerable.Repeat(
                "<rect x=\"0\" y=\"0\" width=\"1\" height=\"1\" fill=\"#000\"/>", overflowCount));
            File.WriteAllText(wideNestSvgPath,
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\">{tinyRects}</svg>");
            Require(DesignComposer.DecodeFrozenImage(wideNestSvgPath) is null,
                "excessively many svg elements fail cleanly before expanding a huge drawing tree");

            string filter = L.T("图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.svg|所有文件|*.*");
            Require(filter.Contains("*.svg", StringComparison.OrdinalIgnoreCase),
                "localized artwork filter includes *.svg");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestDragDropAndSafety()
    {
        string root = CreateTempDirectory();
        try
        {
            string svgUpper = Path.Combine(root, "VECTOR.SVG");
            File.WriteAllText(svgUpper, """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100">
                  <rect x="10" y="10" width="80" height="80" fill="#2266cc"/>
                </svg>
                """);
            string pngPath = Path.Combine(root, "raster.png");
            WriteSolidPng(pngPath, 40, 40, Color.FromArgb(255, 200, 40, 40));
            string brokenSvg = Path.Combine(root, "broken.svg");
            File.WriteAllText(brokenSvg, "not valid svg xml");
            string textPath = Path.Combine(root, "readme.txt");
            File.WriteAllText(textPath, "ignore me");

            // Safety checks: XXE and external <image href="file:///..."> must be blocked/ignored.
            string secretTextPath = Path.Combine(root, "secret.txt");
            File.WriteAllText(secretTextPath, "CONFIDENTIAL_SECRET");
            string secretPngPath = Path.Combine(root, "secret.png");
            WriteSolidPng(secretPngPath, 100, 100, Color.FromArgb(255, 0, 255, 0));

            string externalOnlySvg = Path.Combine(root, "external-image.svg");
            File.WriteAllText(externalOnlySvg, $"""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                  <image href="{new Uri(secretPngPath).AbsoluteUri}" x="0" y="0" width="100" height="100"/>
                </svg>
                """);
            Require(DesignComposer.DecodeFrozenImage(externalOnlySvg) is null,
                "svg with only an external file image is ignored safely and returns null");

            string xxeSvg = Path.Combine(root, "xxe.svg");
            File.WriteAllText(xxeSvg, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <!DOCTYPE svg [
                  <!ENTITY xxe SYSTEM "{new Uri(secretTextPath).AbsoluteUri}">
                ]>
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                  <rect x="0" y="0" width="20" height="20" fill="#112233"/>
                  <text x="10" y="50">&xxe;</text>
                </svg>
                """);
            // With DtdProcessing.Ignore + entity char limit, XXE never reads local files; either safely rejects
            // undeclared entity or renders without throwing.
            ImageSource? xxeDecoded = DesignComposer.DecodeFrozenImage(xxeSvg);
            Require(xxeDecoded is null || xxeDecoded.IsFrozen,
                "xxe svg never throws an unhandled exception or leaks external files");

            WpfTestHost.Run(() =>
            {
                ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
                DesignView view = new(shell.Design);
                try
                {
                    DataObject files = new(DataFormats.FileDrop, new[] { svgUpper, pngPath, textPath, brokenSvg });
                    DragEventArgs hover = Raise(view, files, DragDrop.PreviewDragEnterEvent);
                    Require(hover.Handled && hover.Effects == DragDropEffects.Copy,
                        "drag hover accepts uppercase .SVG alongside PNG");

                    DragEventArgs drop = Raise(view, files, DragDrop.PreviewDropEvent);
                    Require(drop.Handled && drop.Effects == DragDropEffects.Copy,
                        "drop handles mixed SVG + PNG payload");
                    Require(!shell.Design.ImportImagesCommand.CanExecute(null),
                        "async drop decode disables concurrent import command until completion");
                    Require(Raise(view, files, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None,
                        "concurrent drag hover is rejected while async import is active");
                    Wait(shell.Design.ActiveImportTask);
                    Require(shell.Design.ImportImagesCommand.CanExecute(null),
                        "import command re-enables once async decode completes");
                    Require(shell.Design.Layers.Select(layer => layer.ImagePath).SequenceEqual([pngPath, svgUpper]),
                        "valid PNG and uppercase .SVG become layers in picker stacking order");

                    DesignLayerViewModel svgLayer = shell.Design.Layers.Single(layer => layer.ImagePath == svgUpper);
                    Require(svgLayer.Image is DrawingImage && svgLayer.Image is not BitmapSource,
                        "imported .SVG layer keeps vector DrawingImage instead of raster BitmapSource");
                    Require(svgLayer.BaseWidthMm > 0 && Math.Abs(svgLayer.BaseWidthMm - 2.0 * svgLayer.BaseHeightMm) < 1e-6,
                        "imported .SVG layer retains exact 2:1 physical aspect ratio");
                    Require(shell.Toasts.Count == 1 && shell.Toasts[0].Message.Contains(brokenSvg),
                        "broken .svg displays failure toast without blocking valid SVG/PNG layers");

                    // Clearing layers while a background import is decoding discards stale layers cleanly.
                    Task supersededImport = shell.Design.ImportImagesAsync([svgUpper]);
                    shell.Design.ClearLayers();
                    Wait(supersededImport);
                    Require(shell.Design.Layers.Count == 0,
                        "ClearLayers cancels application of superseded async image imports");
                }
                finally
                {
                    shell.Design.ClearLayers();
                    shell.LivePreview.OnWindowClosed();
                }
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestMtaPreloadAndProjectRoundtrip()
    {
        string root = CreateTempDirectory();
        try
        {
            string svgPath = Path.Combine(root, "artwork.svg");
            File.WriteAllText(svgPath, """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100">
                  <defs>
                    <linearGradient id="discGrad" x1="0%" y1="0%" x2="100%" y2="100%">
                      <stop offset="0%" stop-color="#e04040"/>
                      <stop offset="100%" stop-color="#4040e0"/>
                    </linearGradient>
                  </defs>
                  <rect x="15" y="15" width="90" height="70" rx="8" fill="url(#discGrad)"/>
                  <text x="120" y="60" font-size="22" font-family="Arial" fill="#202020">DISC</text>
                </svg>
                """);

            ShellViewModel? shell = null;
            byte[] expectedPixels = [];
            IReadOnlyList<ProjectLayer> savedLayers = [];
            double expectedBaseWidth = 0;
            double expectedBaseHeight = 0;
            ProjectDocument? loadedDocument = null;

            WpfTestHost.Run(() =>
            {
                shell = new(Dispatcher.CurrentDispatcher);
                DesignViewModel design = shell.Design;
                Wait(design.ImportImagesAsync([svgPath]));
                Require(design.Layers.Count == 1, "svg imports for roundtrip");
                DesignLayerViewModel layer = design.Layers[0];
                layer.TranslateXMm = 3.5;
                layer.TranslateYMm = -2.0;
                layer.RotationDeg = 27.5;
                layer.Scale = 0.75;
                expectedBaseWidth = layer.BaseWidthMm;
                expectedBaseHeight = layer.BaseHeightMm;

                expectedPixels = RenderLayers(design, Path.Combine(root, "before.png"));
                savedLayers = design.CaptureLayers();
                ProjectDocument document = new(
                    ProjectJsonStore.CurrentSchemaVersion,
                    "SVG Test Project",
                    ProjectDocument.DvdFamily,
                    new ProjectGeometry(24.0, 58.0, 2_295_104),
                    savedLayers,
                    new ProjectOutputs(Path.Combine(root, "out.iso"), false, root, "SVG_DISC", false),
                    CalibrationSessionPath: null,
                    ThumbnailPngBase64: null,
                    CreatedUtc: DateTimeOffset.UtcNow,
                    ModifiedUtc: DateTimeOffset.UtcNow);
                string projPath = Path.Combine(root, "svg-test.discproj");
                ProjectJsonStore.Save(projPath, document);
                loadedDocument = ProjectJsonStore.Load(projPath);
            });

            // Exercise PreloadLayerImagesAsync on genuine MTA worker threads (not the STA UI thread).
            Dictionary<string, (ImageSource? Image, ImageSource? Thumbnail)> preloaded =
                Task.Run(() => shell!.Design.PreloadLayerImagesAsync(loadedDocument!.Layers))
                    .GetAwaiter().GetResult();

            Require(preloaded.TryGetValue(svgPath, out var entry)
                && entry.Image is DrawingImage
                && entry.Image.IsFrozen,
                "PreloadLayerImagesAsync decodes SVG with <text> and <linearGradient> on MTA threads");

            WpfTestHost.Run(() =>
            {
                try
                {
                    DesignViewModel design = shell!.Design;
                    for (int round = 0; round < 3; round++)
                    {
                        design.LoadLayers(loadedDocument!.Layers, round == 0 ? preloaded : null);
                        Require(design.Layers.Count == 1, "reloaded svg layer exists");
                        DesignLayerViewModel layer = design.Layers[0];
                        Require(layer.Image is DrawingImage && layer.Image is not BitmapSource,
                            "reloaded svg stays vector DrawingImage");
                        if (round == 0)
                        {
                            Require(ReferenceEquals(layer.Image, entry.Image),
                                "LoadLayers reuses preloaded frozen DrawingImage without re-decoding");
                        }

                        Require(Math.Abs(layer.BaseWidthMm - expectedBaseWidth) < 1e-9
                            && Math.Abs(layer.BaseHeightMm - expectedBaseHeight) < 1e-9,
                            "roundtrip preserves svg physical base dimensions");
                        byte[] actualPixels = RenderLayers(design, Path.Combine(root, $"after-{round}.png"));
                        Require(expectedPixels.SequenceEqual(actualPixels),
                            $"round {round} composite pixels match original SVG render");
                    }
                }
                finally
                {
                    shell!.Design.ClearLayers();
                    shell.LivePreview.OnWindowClosed();
                }
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] RenderLayers(DesignViewModel design, string outputPath)
    {
        DesignComposer.Render(
            design.Layers.Select(layer => new DesignLayerRenderItem(
                layer.Image!, layer.BaseWidthMm, layer.BaseHeightMm,
                layer.TranslateXMm, layer.TranslateYMm, layer.RotationDeg, layer.Scale)).ToArray(),
            design.CanvasOuterRadiusMm,
            512,
            outputPath);
        BitmapSource bitmap = DecodePng(outputPath)!;
        byte[] pixels = new byte[512 * 512 * 4];
        bitmap.CopyPixels(pixels, 512 * 4, 0);
        return pixels;
    }

    private static BitmapSource? DecodePng(string path)
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

    private static void WriteSolidPng(string path, int width, int height, Color color)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
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

    private static DragEventArgs Raise(UIElement target, IDataObject data, RoutedEvent routedEvent,
        DragDropEffects allowed = DragDropEffects.Copy)
    {
        DragEventArgs args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [data, DragDropKeyStates.LeftMouseButton, allowed, target, new Point()], null)!;
        args.RoutedEvent = routedEvent;
        target.RaiseEvent(args);
        return args;
    }

    private static void Wait(Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        DispatcherFrame frame = new();
        task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        if (!task.IsCompleted)
        {
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"svg-import-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
