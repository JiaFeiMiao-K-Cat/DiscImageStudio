using System.Reflection;
using DiscImageStudio.Core.Calibration;
using DvdImageSolver.Solver;

internal static class DvdStableRegionTests
{
    private static readonly Assembly SolverAssembly = typeof(FastDispersionImageWriter).Assembly;

    internal static void Run()
    {
        TestUniformAndColour();
        TestQuantizationBoundaries();
        TestSmoothCells();
        TestReference();
        Console.WriteLine("dvd-stable-regions: passed (original scalar samples throughout open regions, RGB/alpha/half-tie boundaries, nearest binary, smooth cells, frozen reference scale/annulus)");
    }

    private static void TestUniformAndColour()
    {
        foreach (byte gray in new byte[] { 0, 1, 63, 127, 128, 254, 255 })
        {
            object image = Raster(5, 3, (_, _) => (gray, gray, gray, (byte)255));
            var point = Physical(1.23, .47, 5, 3, 58);
            CheckRegion(image, false, point.X, point.Y, 58, preserve: false, required: true);
        }
        foreach (byte alpha in new byte[] { 1, 64, 127, 128, 192, 255 })
        {
            object image = Raster(4, 4, (_, _) => ((byte)29, (byte)83, (byte)173, alpha));
            var point = Physical(1.31, 1.69, 4, 4, 58);
            CheckRegion(image, false, point.X, point.Y, 58, preserve: false, required: true);
        }
        object halfLuma = Raster(4, 4, (_, _) => ((byte)0, (byte)115, (byte)20, (byte)255));
        var halfPoint = Physical(1.23, 1.47, 4, 4, 58);
        Check(!GetRegion(halfLuma, false, halfPoint.X, halfPoint.Y, 58, false).Success,
            "constant RGB with luma 86.5 retains exact scalar evaluation");
        object transparent = Raster(4, 4, (_, _) => ((byte)200, (byte)60, (byte)11, (byte)0));
        Check(!GetRegion(transparent, false, halfPoint.X, halfPoint.Y, 58, false).Success,
            "zero alpha falls back");

        object checker = Raster(4, 4, (x, y) =>
        {
            byte gray = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
            return (gray, gray, gray, (byte)255);
        });
        foreach (double x in new[] { .5 - 1e-8, .5, .5 + 1e-8 })
        {
            var point = Physical(x, 1.13, 4, 4, 58);
            var result = GetRegion(checker, false, point.X, point.Y, 58, true);
            if (x == .5) Check(!result.Success, "nearest half-pixel tie is outside the strict region");
            else CheckRegion(checker, false, point.X, point.Y, 58, true, required: true);
        }
        object narrow = Raster(1, 4, (_, _) => ((byte)100, (byte)100, (byte)100, (byte)255));
        Check(!GetRegion(narrow, false, 0, 0, 58, false).Success, "zero image scale is not inverted");
    }

    private static void TestQuantizationBoundaries()
    {
        object alpha = Raster(2, 2, (x, _) => ((byte)200, (byte)200, (byte)200, (byte)(127 + x)));
        object luma = Raster(2, 2, (x, _) => ((byte)(127 + x), (byte)(127 + x), (byte)(127 + x), (byte)255));
        foreach (object image in new[] { alpha, luma })
        foreach (double x in new[] { .5 - 1e-12, .5, .5 + 1e-12 })
        {
            var point = Physical(x, .31, 2, 2, 58);
            Check(!GetRegion(image, false, point.X, point.Y, 58, false).Success,
                "alpha/luminance half-integer and its rounding neighborhood fall back");
        }
        foreach (object image in new[] { alpha, luma })
        {
            var point = Physical(.23, .41, 2, 2, 58);
            CheckRegion(image, false, point.X, point.Y, 58, false, required: true);
        }
        object smallAlpha = Raster(2, 2, (x, _) => ((byte)80, (byte)140, (byte)200, (byte)x));
        var low = Physical(.25, .67, 2, 2, 58);
        Check(!GetRegion(smallAlpha, false, low.X, low.Y, 58, false).Success, "alpha below one has no division-based region");
    }

    private static void TestSmoothCells()
    {
        object image = Raster(9, 7, (x, y) => ((byte)(24 + x + y), (byte)(69 + x * 2 + y),
            (byte)(151 + x + y * 2), (byte)(90 + x + y)));
        int accepted = 0;
        for (int y = 0; y < 6; y++)
        for (int x = 0; x < 8; x++)
        {
            var point = Physical(x + .27, y + .63, 9, 7, 58);
            if (CheckRegion(image, false, point.X, point.Y, 58, false, required: false)) accepted++;
        }
        Check(accepted > 35, "smooth colour and changing alpha produce stable regions");
    }

    private static void TestReference()
    {
        Type type = SolverAssembly.GetType("DvdImageSolver.Solver.CalibrationReferenceImageSampler")!;
        byte[] pixels = CalibrationReferencePattern.ReadGrayPixels();
        foreach (double outer in new[] { 41d, 58d })
        {
            CalibrationTarget current = CalibrationTarget.Create(new(CalibrationDiscKind.Dvd,
                24, outer, 2_297_888, 133), artwork: CalibrationArtwork.ReferenceCross);
            CalibrationTarget legacy = new(6, Guid.NewGuid().ToString("N"), current.Parameters, current.Options, current.Cells);
            foreach (CalibrationTarget target in new[] { current, legacy })
            {
                object sampler = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [target], null)!;
                foreach (double r in new[] { 0d, 24 - 1e-8, outer + 1e-8 })
                {
                    Check(Sample(sampler, true, r, 0, outer, false) == (255, 255), "outside reference annulus stays opaque white");
                    Check(!GetRegion(sampler, true, r, 0, outer, false).Success, "outside annulus does not reuse a pixel region");
                }
                int uniform = 0, mixed = 0;
                for (int y = 0; y < 2399 && (uniform < 4 || mixed < 4); y += 7)
                for (int x = 0; x < 2399 && (uniform < 4 || mixed < 4); x++)
                {
                    int index = y * 2400 + x;
                    bool flat = pixels[index] == pixels[index + 1] && pixels[index] == pixels[index + 2400]
                        && pixels[index] == pixels[index + 2401];
                    if (flat ? uniform >= 4 : mixed >= 4) continue;
                    CalibrationPoint p = CalibrationReferencePattern.PixelToDisc(x + .27, y + .63, outer, CalibrationDiscKind.Dvd);
                    double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                    if (r <= 24.01 || r >= outer - .01) continue;
                    if (!CheckRegion(sampler, true, p.X, -p.Y, outer, false, required: false)) continue;
                    if (flat) uniform++; else mixed++;
                }
                Check(uniform == 4 && mixed == 4, "fixed image supports exact uniform and antialiased cells at frozen scale");
                foreach (double r in new[] { 24 + 1e-8, outer - 1e-8 })
                    CheckRegion(sampler, true, r, 0, outer, false, required: false);
            }
        }
    }

    private static bool CheckRegion(object image, bool reference, double x, double y, double outer, bool preserve, bool required)
    {
        Region result = GetRegion(image, reference, x, y, outer, preserve);
        if (!result.Success)
        {
            Check(!required, "expected a nonempty stable region");
            return false;
        }
        Check(x > result.MinX && x < result.MaxX && y > result.MinY && y < result.MaxY,
            "region is a strict open rectangle containing its anchor");
        Check(Sample(image, reference, x, y, outer, preserve) == result.Sample, "anchor equals the unchanged scalar sampler");
        foreach (double fx in new[] { 1e-9, .1, .5, .9, 1 - 1e-9 })
        foreach (double fy in new[] { 1e-9, .1, .5, .9, 1 - 1e-9 })
        {
            double px = result.MinX + (result.MaxX - result.MinX) * fx;
            double py = result.MinY + (result.MaxY - result.MinY) * fy;
            Check(Sample(image, reference, px, py, outer, preserve) == result.Sample,
                "all interior and near-boundary samples have the same rounded luminance/alpha");
        }
        return true;
    }

    private static object Raster(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var p = pixel(x, y);
            int index = (y * width + x) * 4;
            pixels[index] = p.B; pixels[index + 1] = p.G; pixels[index + 2] = p.R; pixels[index + 3] = p.A;
        }
        Type type = SolverAssembly.GetType("DvdImageSolver.Solver.RasterImage")!;
        return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [width, height, pixels], null)!;
    }

    private static (double X, double Y) Physical(double x, double y, int width, int height, double outer)
    {
        double scale = (Math.Min(width, height) - 1) / (2.0 * outer);
        return ((x - (width - 1) / 2.0) / scale, ((height - 1) / 2.0 - y) / scale);
    }

    private static (byte Gray, byte Alpha) Sample(object image, bool reference, double x, double y, double outer, bool preserve)
    {
        object?[] args = reference ? [x, y] : [x, y, outer, preserve];
        object value = image.GetType().GetMethod("SampleDisc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(image, args)!;
        return Pixel(value);
    }

    private static Region GetRegion(object image, bool reference, double x, double y, double outer, bool preserve)
    {
        object?[] args = reference ? [x, y, null, null] : [x, y, outer, preserve, null, null];
        bool success = (bool)image.GetType().GetMethod("TryGetStableRegion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(image, args)!;
        if (!success) return default;
        object rectangle = args[^1]!;
        double Read(string name) => (double)rectangle.GetType().GetProperty(name)!.GetValue(rectangle)!;
        return new(true, Pixel(args[^2]!), Read("MinX"), Read("MaxX"), Read("MinY"), Read("MaxY"));
    }

    private static (byte Gray, byte Alpha) Pixel(object sample) =>
        ((byte)sample.GetType().GetProperty("Luminance")!.GetValue(sample)!,
         (byte)sample.GetType().GetProperty("Alpha")!.GetValue(sample)!);

    private readonly record struct Region(bool Success, (byte Gray, byte Alpha) Sample,
        double MinX, double MaxX, double MinY, double MaxY);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("DVD stable region: " + message);
    }
}
