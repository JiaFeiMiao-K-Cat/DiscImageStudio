using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DiscImageStudio.Linux.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Linux;
using DiscImageStudio.Linux.Services;
using DiscImageStudio.Linux.Services.Calibration;
using DiscImageStudio.Linux.ViewModels;
using SkiaSharp;

namespace DiscImageStudio.LinuxTests;

/// <summary>The calibration page, end to end: a synthetic photo of a burned target, taken at
/// an angle, of a disc whose drive wrote a slightly different geometry than the project's.
/// Rectifying, tracing and fitting through the view model must recover that geometry.</summary>
internal static partial class Program
{
    // Set DIS_TEST_SCREENSHOTS=dir to keep the synthetic photo and capture the page.
    private static readonly string? ScreenshotDirectory = Environment.GetEnvironmentVariable("DIS_TEST_SCREENSHOTS");

    private static readonly CalibrationPhotoTransform Camera =
        new([6.4, 0.9, 610, -0.5, 5.6, 455, 0.0009, -0.0006, 1]);

    private static Task OnUi(Func<Task> work) => Dispatcher.UIThread.InvokeAsync(work);

    private static void CalibrationMessages()
    {
        // Messages Core really produces for common input mistakes.
        CalibrationPoint[] few = [new(1, 1), new(2, 3), new(5, 1)];
        AssertEnglish(CalibrationText.En(CalibrationPhotoRectifier.Fit(few, few, 60, 7.5).Message), "too few outline points");
        CalibrationTarget target = CalibrationGenerator.CreateTarget(Settings(OpticalDiscFamily.CompactDisc, 359849, 22.5, 58.5));
        CalibrationFitResult fit = CalibrationCrossFitter.Fit(target, [new CalibrationCrossTrace(0, [new(0, -30), new(0, -40)])]);
        Assert(!fit.Succeeded && fit.Warnings.Count > 0, "two points cannot be fitted");
        foreach (string warning in fit.Warnings)
        {
            AssertEnglish(CalibrationText.En(warning), "fit warning");
        }

        try
        {
            CalibrationCrossPrediction.GetOutline(target, target.Parameters, 30, 20);
            Assert(false, "a decreasing radius range is rejected");
        }
        catch (ArgumentException exception)
        {
            AssertEnglish(CalibrationText.En(exception.Message), "prediction range");
        }

        // Every fixed Chinese message in Core's calibration code has an English version.
        if (RepositoryRoot() is not { } root)
        {
            Console.WriteLine("  (source tree not found; skipped the message scan)");
            return;
        }

        List<string> missing = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "DiscImageStudio.Core", "Calibration"), "*.cs"))
        {
            string code = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(code, "(?<![$@\\w])\"((?:[^\"\\\\\\n]|\\\\.)*[\\u4e00-\\u9fff](?:[^\"\\\\\\n]|\\\\.)*)\""))
            {
                // Prefixes Core matches its own warnings against are not messages.
                string before = code[..match.Index].TrimEnd();
                if (before.EndsWith("StartsWith(", StringComparison.Ordinal) || before.EndsWith("Contains(", StringComparison.Ordinal))
                {
                    continue;
                }

                string literal = Regex.Unescape(match.Groups[1].Value);
                if (HasCjk(CalibrationText.En(literal)))
                {
                    missing.Add($"{Path.GetFileName(file)}: {literal}");
                }
            }
        }

        Assert(missing.Count == 0, "untranslated calibration messages:\n  " + string.Join("\n  ", missing.Distinct()));
    }

    private static void AssertEnglish(string text, string what)
        => Assert(text.Length > 0 && !HasCjk(text), $"{what} is not translated: {text}");

    private static bool HasCjk(string text) => text.Any(c => c is >= '　' and <= '鿿' or >= '＀' and <= '￯');

    private static string? RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiscImageStudio.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static void CalibrationTargets()
    {
        DiscSettings cd = Settings(OpticalDiscFamily.CompactDisc, 359849, 22.5, 58.5);
        CalibrationTarget cdTarget = CalibrationGenerator.CreateTarget(cd);
        Equal(CalibrationDiscKind.Cd, cdTarget.Parameters.Kind, "CD kind");
        Equal(22.5, cdTarget.Parameters.InnerRadiusMm, "CD inner");
        Equal(58.5, cdTarget.Parameters.OuterRadiusMm, "CD outer");
        Equal(359849L, cdTarget.Parameters.Sectors, "CD sectors");
        Equal(".wav", CalibrationGenerator.DefaultExtension(CalibrationDiscKind.Cd), "CD file type");

        DiscSettings dvd = Settings(OpticalDiscFamily.Dvd, 2295104, 24, 58) with { PitchLinear = -3.9e-05, PitchQuadratic = 6.9e-05, PitchCubic = -4.6e-05 };
        CalibrationTarget dvdTarget = CalibrationGenerator.CreateTarget(dvd);
        Equal(CalibrationDiscKind.Dvd, dvdTarget.Parameters.Kind, "DVD kind");
        Equal(-3.9e-05, dvdTarget.Parameters.PitchLinear, "DVD pitch is kept");
        Equal(".iso", CalibrationGenerator.DefaultExtension(CalibrationDiscKind.Dvd), "DVD file type");

        DiscSettings bd = Settings(OpticalDiscFamily.BluRay, 12219392, 24, 58) with { InvertMapping = true };
        CalibrationTarget bdTarget = CalibrationGenerator.CreateTarget(bd);
        Equal(CalibrationDiscKind.BluRay, bdTarget.Parameters.Kind, "BD kind");
        Assert(bdTarget.Parameters.BluRayInvertMapping, "BD polarity is kept");
        Equal(OpticalDiscFamily.BluRay, CalibrationGenerator.Family(CalibrationDiscKind.BluRay), "BD family");
        Equal("/x/calibration-1.calibration.json", CalibrationGenerator.SessionPathFor("/x/calibration-1.iso"), "record next to the file");
    }

    /// <summary>Generates a short target for each family and checks the file, the CUE and the
    /// record. Narrow bands keep the files small while the geometry stays physical.</summary>
    private static async Task CalibrationFiles(string work)
    {
        (DiscSettings Settings, string Name, long Bytes)[] cases =
        [
            (Settings(OpticalDiscFamily.CompactDisc, 3000, 24.5, 25.0), "cd-target.wav", 44 + 3000L * 2352),
            (Settings(OpticalDiscFamily.Dvd, 160, 24, 24.03), "dvd-target.iso", 160L * 2048),
            (Settings(OpticalDiscFamily.BluRay, 1024, 24, 24.01), "bd-target.iso", 1024L * 2048),
        ];
        foreach ((DiscSettings settings, string name, long bytes) in cases)
        {
            CalibrationTarget target = CalibrationGenerator.CreateTarget(settings);
            CalibrationSession session = new(target, [], new CalibrationFitOptions(), true, null);
            string output = Path.Combine(work, name);
            double last = 0;
            await Task.Run(() => CalibrationGenerator.Generate(session, output, p => last = Math.Max(last, p), CancellationToken.None));
            Equal(bytes, new FileInfo(output).Length, $"{name} size");
            Assert(last >= 0.99, $"{name} progress reaches the end ({last})");
            CalibrationSession restored = CalibrationSessionJson.Load(CalibrationGenerator.SessionPathFor(output));
            Equal(target.Id, restored.Target.Id, $"{name} record keeps the target");
            Assert(!Directory.EnumerateDirectories(work, ".calibration-*").Any(), "staging folder removed");
            if (settings.Family == OpticalDiscFamily.CompactDisc)
            {
                Assert(File.ReadAllText(Path.Combine(work, "cd-target.cue")).Contains("FILE \"cd-target.wav\" WAVE"), "CUE references the final WAV");
            }
        }
    }

    /// <summary>The whole page workflow through the view model, on the UI thread.</summary>
    private static async Task CalibrationEndToEnd(string work)
    {
        LinuxSettings settings = new();
        MainViewModel main = new(settings);
        if (ScreenshotDirectory is not null)
        {
            Directory.CreateDirectory(ScreenshotDirectory);
        }

        MainWindow window = new() { DataContext = main, Width = 1440, Height = 900 };
        main.Ui = window;
        window.Show();

        ProjectDocument document = ProjectViewModel.CreateDocument("Calibration", OpticalDiscFamily.CompactDisc, 22.5, 58.5, 359849,
            0, 0, 0, false, Path.Combine(work, "calibration-project.wav"));
        main.Project = new ProjectViewModel(document, null);
        main.NavigateCommand.Execute(AppPage.Calibrate);
        CalibrationViewModel page = main.Calibration;
        Assert(page.HasTarget, "opening the page creates a target from the project");
        Assert(page.StepOneExpanded && page.StepOneState == "Ready", "nothing is burned yet, so step 1 stays open");
        CalibrationTarget target = page.Target!;
        Equal(22.5, target.Parameters.InnerRadiusMm, "target inner radius");
        Capture(window, "calibrate-target.png");

        // What the drive really wrote. A few micrometres already bend each arm of the cross
        // by half a turn, which is what makes the method so sensitive.
        CalibrationParameters actual = target.Parameters with
        {
            InnerRadiusMm = target.Parameters.InnerRadiusMm + 0.003,
            OuterRadiusMm = target.Parameters.OuterRadiusMm - 0.002,
        };
        const double rotation = 0.83;
        string photo = Path.Combine(ScreenshotDirectory ?? work, "calibration-photo.png");
        await Task.Run(() => RenderPhoto(target, actual, rotation, 1200, 900, photo));

        await page.ImportPhotoFromAsync(photo);
        Assert(page.HasPhoto && page.IsPhotoStep, "photo imported");
        Equal(16, page.BoundaryPoints.Count, "eight suggested points per outline");

        // The canvas itself: dragging an outline point moves it by the same photo distance.
        window.UpdateLayout();
        CalibrationCanvas canvas = window.GetVisualDescendants().OfType<CalibrationCanvas>().Single();
        Assert(canvas.IsEffectivelyVisible && canvas.Bounds.Width > 100, "canvas laid out");
        CalibrationBoundaryPoint grabbed = page.BoundaryPoints[0];
        Point from = OnWindow(canvas, window, canvas.WorldToView(new Point(grabbed.X, grabbed.Y)));
        Point to = from + new Vector(24, -12);
        Point expected = canvas.ViewToWorld(canvas.WorldToView(new Point(grabbed.X, grabbed.Y)) + new Vector(24, -12));
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(from + new Vector(10, -5), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
        CalibrationBoundaryPoint dropped = page.BoundaryPoints.Single(p => p.Boundary == grabbed.Boundary && p.Index == grabbed.Index);
        Assert(Math.Abs(dropped.X - expected.X) < 0.01 && Math.Abs(dropped.Y - expected.Y) < 0.01,
            $"dragged outline point lands under the pointer: {dropped.X:F2},{dropped.Y:F2} vs {expected.X:F2},{expected.Y:F2}");
        window.MouseWheel(OnWindow(canvas, window, new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2)), new Vector(0, 1), RawInputModifiers.None);
        Equal("120%", canvas.ViewDescription, "wheel zooms");
        window.KeyPress(Key.D0, RawInputModifiers.None, PhysicalKey.Digit0, "0");
        Equal("100%", canvas.ViewDescription, "0 fits the view again");

        // Drag every suggested point onto the true edges, as the user would.
        foreach (CalibrationBoundaryPoint point in page.BoundaryPoints.ToArray())
        {
            double radius = point.Boundary == CalibrationBoundaryKind.OuterEdge ? 60 : CalibrationViewModel.HoleRadiusMm;
            double angle = 2 * Math.PI * point.Index / 8 + 0.3;
            CalibrationPoint source = Camera.DiscToSource(new CalibrationPoint(radius * Math.Cos(angle), radius * Math.Sin(angle)));
            page.EditBoundary(point.Boundary, point.Index, source.X, source.Y);
        }

        Capture(window, "calibrate-outline.png");
        Assert(page.RectifyCommand.CanExecute(null), "rectify is available");
        await page.RectifyCommand.ExecuteAsync(null);
        Assert(page.IsRectificationReady && page.PreviewRectification, $"rectified: {main.JobStatus}");
        Assert(page.RectifiedBitmap is not null && page.BoundaryContours.Count == 2, "rectified image and outlines");
        Capture(window, "calibrate-rectified.png");

        page.ConfirmRectificationCommand.Execute(null);
        Assert(page.IsTraceStep && page.StepThreeExpanded, "tracing step opens");

        // The rectified frame matches the disc up to a rotation, so trace through the photo.
        CalibrationPhotoTransform rectified = RectifiedTransform(page);
        Func<double, CalibrationPoint> arm = CalibrationCrossPrediction.CreateMapping(target, actual, rotation);
        double low = target.DrawingInnerRadiusMm + 0.6, high = target.DrawingOuterRadiusMm - 0.6;
        for (int i = 0; i < 28; i++)
        {
            CalibrationPoint disc = arm(low + (high - low) * i / 27.0);
            CalibrationPoint clicked = rectified.SourceToDisc(Camera.DiscToSource(disc));
            page.EditTrace(-1, clicked.X, clicked.Y, remove: false);
        }

        Equal(28, page.TracePoints.Count, "trace points");

        // Clicking the rectified photo adds a point; Delete removes the selected one.
        window.UpdateLayout();
        Point empty = OnWindow(canvas, window, canvas.WorldToView(new Point(0, 40)));
        window.MouseDown(empty, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(empty, MouseButton.Left, RawInputModifiers.None);
        Equal(29, page.TracePoints.Count, "a click adds a trace point");
        Assert(page.TracePoints.Any(p => Math.Abs(p.XMm) < 0.05 && Math.Abs(p.YMm - 40) < 0.05), "the point lands under the pointer");
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Equal(28, page.TracePoints.Count, "Delete removes the selected point");
        page.EditTrace(-1, 0.5, 0.5, remove: false);
        Equal(28, page.TracePoints.Count, "a click inside the hole is ignored");
        CalibrationTracePoint moved = page.TracePoints[5];
        page.EditTrace(moved.Index, moved.XMm + 3, moved.YMm, remove: false);
        Assert(page.TracePoints.Any(p => p.Index == moved.Index && Math.Abs(p.XMm - moved.XMm - 3) < 1e-12), "a point can be moved");
        page.EditTrace(moved.Index, 0, 0, remove: true);
        Assert(page.TracePoints.All(p => p.Index != moved.Index), "the moved point is removed");
        Equal(27, page.TracePoints.Count, "a point can be moved and removed");

        Assert(page.SolveCommand.CanExecute(null), "fit is available");
        await page.SolveCommand.ExecuteAsync(null);
        Assert(page.SelectedCandidate is not null, $"fit produced a candidate: {page.FitAssessment}");
        CalibrationCandidate best = page.SelectedCandidate!;
        Assert(Math.Abs(best.Parameters.InnerRadiusMm - actual.InnerRadiusMm) < 1e-4,
            $"inner radius recovered: {best.Parameters.InnerRadiusMm} vs {actual.InnerRadiusMm}");
        Assert(Math.Abs(best.Parameters.OuterRadiusMm - actual.OuterRadiusMm) < 1e-4,
            $"outer radius recovered: {best.Parameters.OuterRadiusMm} vs {actual.OuterRadiusMm}");
        Assert(CalibrationViewModel.ResidualAccepted(best) && !page.FitRejected, $"residual accepted: {page.FitAssessment}");
        await WaitFor(() => page.PredictionContours.Count == 1, "predicted curve drawn");
        Equal(0, page.OutlierIndices.Count, "no outliers on an exact trace");
        AssertEnglish(page.FitAssessment, "assessment");
        Capture(window, "calibrate-fit.png");
        if (ScreenshotDirectory is not null && Avalonia.Application.Current is { } application)
        {
            application.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Capture(window, "calibrate-fit-dark.png");
            application.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        }

        Assert(!page.ApplyCommand.CanExecute(null), "applying needs the curve confirmed");
        page.ContourConfirmed = true;
        Assert(page.ApplyCommand.CanExecute(null), "apply is available once confirmed");
        page.ApplyCommand.Execute(null);
        Equal(best.Parameters.InnerRadiusMm, main.Project!.InnerRadiusMm, "project inner radius updated");
        Equal(best.Parameters.OuterRadiusMm, main.Project.OuterRadiusMm, "project outer radius updated");
        Assert(main.Project.IsDirty, "applying marks the project unsaved");

        // The record keeps photo, outlines and points; reopening lands on the tracing step.
        string record = Path.Combine(work, "photo.calibration.json");
        CalibrationSessionJson.Save(record, page.Session());
        MainViewModel second = new(settings);
        second.Project = new ProjectViewModel(document, null);
        second.NavigateCommand.Execute(AppPage.Calibrate);
        await second.Calibration.LoadSessionAsync(record);
        CalibrationViewModel reopened = second.Calibration;
        Equal(target.Id, reopened.Target?.Id, "record target");
        Assert(reopened.IsTraceStep && reopened.RectifiedBitmap is not null, $"record reopens on the tracing step: {second.JobStatus}");
        Equal(27, reopened.TracePoints.Count, "record trace points");
        Equal(16, reopened.BoundaryPoints.Count, "record outline points");
        await reopened.SolveCommand.ExecuteAsync(null);
        Assert(reopened.SelectedCandidate is { } again && Math.Abs(again.Parameters.OuterRadiusMm - best.Parameters.OuterRadiusMm) < 1e-6,
            "the reopened record fits the same result");

        // A target of another family cannot be applied to this project.
        main.Project = new ProjectViewModel(ProjectViewModel.CreateDocument("DVD", OpticalDiscFamily.Dvd, 24, 58, 2295104, 0, 0, 0, false,
            Path.Combine(work, "dvd.iso")), null);
        Assert(page.Target?.Parameters.Kind == CalibrationDiscKind.Dvd && page.TracePoints.Count == 0,
            "a project opened on this page gets its own target");
        await page.LoadSessionAsync(record);
        Assert(page.HasFamilyMismatch && !page.ApplyCommand.CanExecute(null), "CD record on a DVD project cannot be applied");

        // Burn hand-off keeps the target's family even when the project differs.
        main.Burn.UseSource(Path.Combine(work, "cd-target.wav"), OpticalDiscFamily.CompactDisc);
        Equal(BurnMaterial.CdAudio, main.Burn.Material, "CD target burns as audio on a DVD project");
        window.Close();
    }

    private static Point OnWindow(Visual control, Visual window, Point point)
        => control.TranslatePoint(point, window) ?? throw new InvalidOperationException("not in the window");

    private static CalibrationPhotoTransform RectifiedTransform(CalibrationViewModel page)
    {
        CalibrationPoint[] Outline(CalibrationBoundaryKind kind) => page.BoundaryPoints.Where(p => p.Boundary == kind)
            .OrderBy(p => p.Index).Select(p => new CalibrationPoint(p.X, p.Y)).ToArray();
        CalibrationPhotoFitResult fit = CalibrationPhotoRectifier.Fit(Outline(CalibrationBoundaryKind.OuterEdge),
            Outline(CalibrationBoundaryKind.CenterHole), 60, CalibrationViewModel.HoleRadiusMm);
        Assert(fit.Succeeded && fit.Transform is not null, "outline fit");
        Assert(fit.OuterRmsPixels < 0.01 && fit.HoleRmsPixels < 0.01, "exact outlines fit exactly");
        // Same photo point, same radius: only a rotation separates the two frames.
        CalibrationPoint probe = fit.Transform!.SourceToDisc(Camera.DiscToSource(new CalibrationPoint(30, 12)));
        Assert(Math.Abs(Math.Sqrt(probe.X * probe.X + probe.Y * probe.Y) - Math.Sqrt(30 * 30 + 12 * 12)) < 1e-3, "rectified radius matches");
        return fit.Transform;
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert(DateTime.UtcNow < deadline, $"timed out: {what}");
            await Task.Delay(50);
        }
    }

    private static void Capture(Window window, string name)
    {
        if (ScreenshotDirectory is null)
        {
            return;
        }

        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ScreenshotDirectory, name), PngBitmapEncoderOptions.Default);
    }

    /// <summary>Renders what a camera sees: the target as burned with the <paramref name="actual"/>
    /// geometry, seen through the perspective <see cref="Camera"/>.</summary>
    private static void RenderPhoto(CalibrationTarget target, CalibrationParameters actual, double rotation, int width, int height, string path)
    {
        // The burn only moves data along the track, so nominal radius → (observed radius,
        // angular shift) is a 1-D table; invert it by bisection per pixel.
        Func<CalibrationPoint, CalibrationPoint> map = target.CreateMapping(actual, rotation);
        const int count = 8000;
        double first = target.Parameters.InnerRadiusMm, last = target.Parameters.OuterRadiusMm;
        double[] nominal = new double[count], observed = new double[count], shift = new double[count];
        for (int i = 0; i < count; i++)
        {
            nominal[i] = first + (last - first) * i / (count - 1);
            CalibrationPoint mapped = map(new CalibrationPoint(nominal[i], 0));
            observed[i] = Math.Sqrt(mapped.X * mapped.X + mapped.Y * mapped.Y);
            double angle = Math.Atan2(mapped.Y, mapped.X);
            if (i > 0)
            {
                angle += Math.Round((shift[i - 1] - angle) / (2 * Math.PI)) * 2 * Math.PI;
            }

            shift[i] = angle;
        }

        static SKColor Mix(SKColor a, SKColor b, double t) => new(
            (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t));

        SKColor table = new(52, 50, 48), clear = new(150, 156, 162), hub = new(204, 207, 212);
        SKColor blank = new(198, 201, 208), burned = new(168, 160, 176), line = new(70, 74, 86);
        byte[] pixels = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double red = 0, green = 0, blue = 0;
                for (int sample = 0; sample < 4; sample++)
                {
                    CalibrationPoint disc = Camera.SourceToDisc(new CalibrationPoint(x + (sample % 2 - 0.5) * 0.5, y + (sample / 2 - 0.5) * 0.5));
                    double r = Math.Sqrt(disc.X * disc.X + disc.Y * disc.Y);
                    SKColor colour;
                    if (r > 60 || r < CalibrationViewModel.HoleRadiusMm)
                    {
                        colour = Mix(table, new SKColor(70, 66, 62), (double)y / height);
                    }
                    else if (r < 11)
                    {
                        colour = clear;
                    }
                    else if (r < 16.5)
                    {
                        colour = hub;
                    }
                    else if (r < observed[0] || r > observed[^1])
                    {
                        colour = blank;
                    }
                    else
                    {
                        int lowIndex = 0, highIndex = count - 1;
                        while (highIndex - lowIndex > 1)
                        {
                            int middle = (lowIndex + highIndex) / 2;
                            (observed[middle] <= r ? ref lowIndex : ref highIndex) = middle;
                        }

                        double t = (r - observed[lowIndex]) / Math.Max(1e-12, observed[highIndex] - observed[lowIndex]);
                        double source = nominal[lowIndex] + (nominal[highIndex] - nominal[lowIndex]) * t;
                        double angle = Math.Atan2(disc.Y, disc.X) - (shift[lowIndex] + (shift[highIndex] - shift[lowIndex]) * t);
                        byte value = target.Sample(source * Math.Cos(angle), source * Math.Sin(angle));
                        // A little sheen across the disc so the photo is not flat.
                        SKColor bed = Mix(burned, new SKColor(150, 176, 190), 0.5 + 0.5 * Math.Sin(Math.Atan2(disc.Y, disc.X) * 2));
                        colour = value < 128 ? line : bed;
                    }

                    red += colour.Red;
                    green += colour.Green;
                    blue += colour.Blue;
                }

                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(blue / 4);
                pixels[offset + 1] = (byte)(green / 4);
                pixels[offset + 2] = (byte)(red / 4);
                pixels[offset + 3] = 255;
            }
        });

        using SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using FileStream stream = File.Create(path);
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
    }
}
