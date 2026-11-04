using System.Numerics;
using DiscImageStudio.Core.Calibration;

internal static class CalibrationAngleSweepReaderTests
{
    private const double Inner = 23.99146063, Outer = 57.96489366, Bearing = 29;
    private enum Scene { Pilots, WideBandsOnly, MissingMiddle, ConflictingMiddle }

    internal static void Run()
    {
        var target = CalibrationTarget.Create(new(CalibrationDiscKind.Dvd, 24.0378339, 57.6530471, 2297888, 133.3),
            artwork: CalibrationArtwork.AngleSweepBands);
        var clicks = new List<CalibrationRingObservation>();
        for (int i = 0; i < 3; i++)
        {
            var range = target.WideBandSectorRanges[i];
            clicks.Add(new(i, Edge(range.Start)));
            clicks.Add(new(i + 3, Edge(range.EndExclusive)));
        }
        double Edge(long address) => .12 + 1.002 * Math.Sqrt(Inner * Inner +
            address / (double)target.Parameters.Sectors * (Outer * Outer - Inner * Inner));
        foreach (Scene scene in Enum.GetValues<Scene>())
        {
            var result = CalibrationAngleSweepReader.Read(target, clicks, 63, RenderSector(target, scene), Bearing);
            if (scene == Scene.Pilots)
            {
                if (result.Corrected is not {} p || Math.Abs(p.InnerRadiusMm - Inner) > .04
                    || Math.Abs(p.OuterRadiusMm - Outer) > .04 || result.StationCount != 3
                    || result.DegreesPerTurnOffset.Count != 3 || result.DegreesPerTurnOffset[2] >= -1
                    || result.DegreesPerTurnOffset[0] <= -1)
                    throw new InvalidOperationException($"Angular reader failed 45 degree DVD regression: {result}");
            }
            else if (result.Succeeded)
                throw new InvalidOperationException($"Angular reader accepted {scene}: {result}");
        }
        Console.WriteLine("calibration-angle-sweep-reader: same-side angular estimate and missing/conflicting evidence passed");
    }

    private static Func<double, double, Vector3> RenderSector(CalibrationTarget target, Scene scene)
    {
        const double fineStep = .0075, sensorStep = .075, sigma = .22;
        const int samples = 535, subsamples = 5600, kernelRadius = 88;
        double radialGrowth = (Outer * Outer - Inner * Inner) / (2 * target.Parameters.TrackLengthMm);
        var random = new Random(4451);
        var sensor = new double[360, samples];
        var kernel = Enumerable.Range(-kernelRadius, 2 * kernelRadius + 1)
            .Select(i => Math.Exp(-.5 * Math.Pow(i * fineStep / sigma, 2))).ToArray();
        double kernelSum = kernel.Sum();
        for (int azimuth = 0; azimuth < 360; azimuth++)
        {
            bool clear = Math.Abs(azimuth - Bearing) <= 22.5;
            var high = new double[subsamples];
            if (clear)
            {
                // Choose the nearest actual turn intersecting this bearing, map it to its
                // DVD sector, and evaluate the square wave. No photo-reader templates here.
                double angle = azimuth * Math.PI / 180 - 1.2;
                for (int i = 0; i < high.Length; i++)
                {
                    double r = (20 + i * fineStep - .12) / 1.002;
                    double turn = angle + Math.Tau * Math.Round(((r - Inner) / radialGrowth - angle) / Math.Tau);
                    double physical = Inner + radialGrowth * turn;
                    double q = (physical * physical - Inner * Inner) / (Outer * Outer - Inner * Inner);
                    long address = (long)Math.Floor(q * target.Parameters.Sectors);
                    q = address / (double)target.Parameters.Sectors;
                    for (int j = 0; j < 3; j++)
                    {
                        var range = target.WideBandSectorRanges[j];
                        if (address < range.Start || address >= range.EndExclusive) continue;
                        double ink = 1;
                        var station = target.AngleSweepStations[j];
                        if (q >= station.StartProgress && q < station.EndProgress && scene != Scene.WideBandsOnly
                            && !(scene == Scene.MissingMiddle && j == 1))
                        {
                            double phase = station.PhaseAt(q);
                            if (scene == Scene.ConflictingMiddle && j == 1)
                                phase += .25 * target.Parameters.TrackLengthMm * (q - station.StartProgress) / (42 * 42);
                            ink = Math.Sin(phase) >= 0 ? 1 : 0;
                        }
                        high[i] = ink;
                        break;
                    }
                }
            }
            for (int pixel = 0; pixel < samples; pixel++)
            {
                double value = 0;
                if (clear)
                    for (int j = -kernelRadius; j <= kernelRadius; j++)
                        value += kernel[j + kernelRadius] * high[Math.Clamp(pixel * 10 + j, 0, high.Length - 1)];
                sensor[azimuth, pixel] = (126 + 14 * value / kernelSum + random.NextDouble() * 2 - 1) / 255;
            }
        }
        return (double x, double y) =>
        {
            double degrees = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
            double radial = (double.Hypot(x, y) - 20) / sensorStep;
            int row = (int)degrees, col = (int)Math.Floor(radial);
            if ((uint)col >= samples - 1) return new Vector3(.5f);
            double u = degrees - row, v = radial - col;
            double SampleRow(int a) => sensor[a, col] * (1 - v) + sensor[a, col + 1] * v;
            return new Vector3((float)((1 - u) * SampleRow(row) + u * SampleRow((row + 1) % 360)));
        };
    }
}
