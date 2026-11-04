using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Services;

internal static class CalibrationPatternRenderer
{
    internal const int HighResolutionSize = 8192;

    internal static BitmapSource Render(CalibrationTarget target, double canvasRadius, int size,
        CalibrationParameters? actual = null, double rotation = 0, CancellationToken cancellationToken = default)
    {
        Func<CalibrationPoint, CalibrationPoint?>? inverse = actual is null
            ? null : target.CreateInverseMapping(actual, rotation);
        var bounds = actual ?? target.Parameters;
        byte[] pixels = new byte[checked(size * size)];
        double step = 2 * canvasRadius / size;
        var clock = target.HasClockRuler ? ClockRulerPreview(bounds, rotation, step) : null;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            double ym = (y + 0.5) * step - canvasRadius;
            for (int x = 0; x < size; x++)
            {
                double xm = (x + 0.5) * step - canvasRadius;
                double r2 = xm * xm + ym * ym;
                byte value = 255;
                if (r2 >= bounds.InnerRadiusMm * bounds.InnerRadiusMm - 1e-10 && r2 <= bounds.OuterRadiusMm * bounds.OuterRadiusMm + 1e-10)
                {
                    if (clock is not null)
                        value = clock(xm, ym, r2);
                    else if (inverse is null)
                        value = BinaryGrayscale.Quantize(target.Sample(xm, ym));
                    else
                    {
                        CalibrationPoint? p = inverse(new(xm, ym));
                        if (p is not null) value = BinaryGrayscale.Quantize(target.Sample(p.X, p.Y));
                    }
                }
                pixels[y * size + x] = value;
            }
        });
        BitmapSource image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        image.Freeze();
        return image;
    }

    /// <summary>Illustrate the six fixed-address bands and the resonances where an integer number
    /// of clock periods fits one turn. The camera sets their apparent width and contrast; here
    /// they are enhanced and antialiased so they remain legible at page-preview size.</summary>
    private static Func<double, double, double, byte> ClockRulerPreview(CalibrationParameters disc,
        double rotation, double pixelMm)
    {
        double inner = disc.InnerRadiusMm, squaredRange = disc.OuterRadiusMm * disc.OuterRadiusMm - inner * inner;
        double period = CalibrationClockRulerPattern.PeriodLengthMm(disc);
        double spacing = period / Math.Tau;
        double trackAnglePerMm = 2 * disc.TrackLengthMm / squaredRange;
        double clockTurns = disc.TrackLengthMm / period;
        double RadiusAt(double q) => Math.Sqrt(inner * inner + q * squaredRange);
        var bands = CalibrationClockRulerPattern.Stations.Select(q => (
            Inner: RadiusAt(q - CalibrationClockRulerPattern.InkHalfWidth),
            Outer: RadiusAt(q + CalibrationClockRulerPattern.InkHalfWidth),
            GutterLow: RadiusAt(q - CalibrationClockRulerPattern.GutterHalfWidth),
            GutterHigh: RadiusAt(q + CalibrationClockRulerPattern.GutterHalfWidth))).ToArray();
        int maxOrder = (int)Math.Ceiling(disc.OuterRadiusMm / spacing);
        double[] phases = new double[maxOrder + 1];
        for (int order = 1; order <= maxOrder; order++)
        {
            double radius = order * spacing;
            double q = (radius * radius - inner * inner) / squaredRange;
            // Unwrapped clock phase minus the whole turns of the recording spiral.
            phases[order] = Math.IEEERemainder(Math.Tau * clockTurns * q
                - order * trackAnglePerMm * (radius - inner), Math.Tau);
        }
        double sigma = Math.Max(.11, .68 * pixelMm);
        return (x, y, r2) =>
        {
            double r = Math.Sqrt(r2);
            int order = Math.Clamp((int)Math.Round(r / spacing), 0, maxOrder);
            double distance = (r - order * spacing) / sigma;
            double level = 164;
            if (order > 0 && Math.Abs(distance) < 3)
            {
                double phase = order * (Math.Atan2(y, x) - rotation) + phases[order];
                level -= 208 * Math.Exp(-.5 * distance * distance)
                    * (CalibrationClockRulerPattern.PilotInkFraction(phase) - .5);
            }
            foreach (var band in bands)
            {
                double gutter = Coverage(band.GutterLow, band.GutterHigh);
                if (gutter <= 0) continue;
                level += gutter * (251 - level);
                level += Coverage(band.Inner, band.Outer) * (20 - level);
                break;
            }
            return (byte)Math.Clamp((int)Math.Round(level), 0, 255);

            double Coverage(double low, double high) => Math.Clamp(
                (Math.Min(r + pixelMm / 2, high) - Math.Max(r - pixelMm / 2, low)) / pixelMm, 0, 1);
        };
    }

    /// <summary>Export the burned target version in the ordinary encoder's image coordinates.</summary>
    internal static BitmapSource RenderEncodingImage(CalibrationTarget target, CancellationToken cancellationToken = default)
    {
        if (target.HasClockRuler)
            throw new InvalidOperationException("时钟尺预览是示意图。请从标定页生成 CD 音轨或直接刻录。");
        // Existing snapshots keep their original file representation. New planar
        // artwork is sampled on the export grid directly, with the same physical
        // origin and scale that an ordinary image of this size would have on disc.
        int size = target.SchemaVersion >= 14 ? HighResolutionSize : CalibrationReferencePattern.PixelSize;
        bool originalPng = target.SchemaVersion == 6;
        byte[] pixels = originalPng ? CalibrationReferencePattern.ReadGrayPixels() : new byte[size * size];
        CalibrationParameters parameters = target.Parameters;
        double centre = parameters.Kind == CalibrationDiscKind.Cd ? size / 2.0 : (size - 1) / 2.0;
        double scale = parameters.OuterRadiusMm / (parameters.Kind == CalibrationDiscKind.Dvd ? (size - 1) / 2.0 : size / 2.0);
        double inner2 = target.DrawingInnerRadiusMm * target.DrawingInnerRadiusMm;
        double outer2 = target.DrawingOuterRadiusMm * target.DrawingOuterRadiusMm;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            double ym = (y - centre) * scale;
            for (int x = 0; x < size; x++)
            {
                double xm = (x - centre) * scale;
                double r2 = xm * xm + ym * ym;
                if (!originalPng) pixels[y * size + x] = target.Sample(xm, ym);
                else if (r2 < inner2 - 1e-10 || r2 > outer2 + 1e-10) pixels[y * size + x] = 255;
            }
        });
        BitmapSource image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        image.Freeze();
        return image;
    }

    internal static void SavePng(BitmapSource image, string path)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}
