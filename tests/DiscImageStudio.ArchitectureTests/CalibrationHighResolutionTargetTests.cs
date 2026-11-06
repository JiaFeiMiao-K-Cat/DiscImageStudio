using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class CalibrationHighResolutionTargetTests
{
    internal static void Run(string directory)
    {
        CheckExport(directory);
        CheckBdOrientation();
    }

    private static void CheckExport(string directory)
    {
        var p = new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 58, 2_297_888, 133.3);
        var line = CalibrationTarget.Create(p, artwork: CalibrationArtwork.SingleRadialLine);
        var image = CalibrationPatternRenderer.RenderEncodingImage(line);
        string filename = Path.Combine(directory, "single-radius.png");
        CalibrationPatternRenderer.SavePng(image, filename);
        using var stream = File.OpenRead(filename);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
        Check(frame.PixelWidth == 8192 && frame.PixelHeight == 8192, "the exported pattern is 8192 px square");
        var row = new byte[frame.PixelWidth];
        frame.CopyPixels(new Int32Rect(0, 1000, row.Length, 1), row, row.Length, 0);
        const double centre = 4095.5;
        double halfWidthPx = centre * CalibrationTarget.SingleRadialLineWidthMm / (2 * p.OuterRadiusMm);
        Check(Array.IndexOf(row, (byte)0) == (int)Math.Ceiling(centre - halfWidthPx)
            && Array.LastIndexOf(row, (byte)0) == (int)Math.Floor(centre + halfWidthPx),
            "radial edges resolve to their high-resolution physical coordinates");
        frame.CopyPixels(new Int32Rect(0, 7191, row.Length, 1), row, row.Length, 0);
        Check(row.All(b => b == 255), "the opposite radius has no ink");
    }

    private static void CheckBdOrientation()
    {
        var p = new CalibrationParameters(CalibrationDiscKind.BluRay, 24, 58, 512, 74.5);
        var disc = CalibrationViewModel.BluRayParameters(p);
        var raster = new BluRayGray8Raster(2400, 2400, CalibrationReferencePattern.ReadGrayPixels());
        var tone = new BluRayRunLengthMapping();
        var imageMapping = new BluRayImagePayloadMapping(disc, raster, tone);
        foreach (var artwork in new[] { CalibrationArtwork.ReferenceCross, CalibrationArtwork.SingleRadialLine,
            CalibrationArtwork.HalfDisc, CalibrationArtwork.WavyBoundary })
        {
            var target = CalibrationTarget.Create(p, artwork: artwork);
            var writer = CalibrationViewModel.BluRayPatternMapping(target);
            var payload = new byte[2048];
            var original = new byte[2048];
            int dark = 0, light = 0;
            for (uint sector = 0; sector < disc.TotalSectors; sector += 11)
            {
                var address = new BluRaySectorContext(sector, disc.GetPhysicalSectorNumber(sector), disc);
                writer.FillScrambledPayload(address, payload);
                if (artwork == CalibrationArtwork.ReferenceCross)
                {
                    imageMapping.FillScrambledPayload(address, original);
                    Check(payload.AsSpan().SequenceEqual(original), "BD reference sectors match the ordinary original-PNG encoder");
                }
                else
                {
                    for (int offset = 0; offset < payload.Length; offset += 13)
                    {
                        var position = imageMapping.GetPosition(sector, offset);
                        bool onLine = artwork == CalibrationArtwork.HalfDisc ? position.TrackPoint.Xmm < 0
                            : position.TrackPoint.Ymm > 0 && Math.Abs(position.TrackPoint.Xmm) <= CalibrationTarget.SingleRadialLineWidthMm / 2;
                        if (artwork == CalibrationArtwork.WavyBoundary)
                        {
                            double x = position.TrackPoint.Xmm, y = position.TrackPoint.Ymm;
                            double radius = Math.Sqrt(x * x + y * y);
                            double phase = Math.PI / 2 * Math.Sin(6 * Math.PI * (radius - p.InnerRadiusMm) / (p.OuterRadiusMm - p.InnerRadiusMm));
                            onLine = x * Math.Cos(phase) - y * Math.Sin(phase) < 0;
                        }
                        byte expected = onLine ? BluRayRunLengthMapping.ThreeTByte : BluRayRunLengthMapping.SixTByte;
                        Check(payload[offset] == expected, "BD bytes follow the physical line or half-disc geometry");
                        if (onLine) dark++; else light++;
                    }
                }
            }
            if (artwork != CalibrationArtwork.ReferenceCross) Check(dark > 100 && light > 100, "geometry test traverses both tones");
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("High-resolution calibration: " + label);
    }
}
