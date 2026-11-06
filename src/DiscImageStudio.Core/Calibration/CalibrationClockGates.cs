using System.Numerics;
using static DiscImageStudio.Core.Calibration.CalibrationClockReader;

namespace DiscImageStudio.Core.Calibration;

/// <summary>Locate the six known sector addresses from the signed RGB contrast between each
/// black plateau and its white shoulders. No predicted inner/outer radii are used for indexing.</summary>
internal static class CalibrationClockGates
{
    private const int Half = 55;
    internal static readonly double[] Addresses = CalibrationClockRulerPattern.Stations.ToArray();
    private const double Ink = CalibrationClockRulerPattern.InkHalfWidth, White = CalibrationClockRulerPattern.GutterHalfWidth;

    internal static double[]? Find(Vector3[][] rays, CancellationToken token)
    {
        var response = new double[rays[0].Length];
        var kernels = new Dictionary<(int, int), float[]>();
        var strengths = new double[(Rays + 1) / 2];
        for (int r = Half + 1; r < response.Length - Half - 1; r += 2)
        {
            if ((r & 63) == 0) token.ThrowIfCancellationRequested();
            // Approximate stripe *width* only for detection. The spacing is subsequently fit
            // to the recorded q addresses, and the width/blur are refined from the photo.
            double dr = (58 * 58 - 24 * 24) / (2 * Radius(r));
            var key = ((int)Math.Round(Ink * dr / StepMm), (int)Math.Round(White * dr / StepMm));
            if (!kernels.TryGetValue(key, out var kernel))
            {
                var model = new double[2 * Half + 1];
                for (int j = 0; j < model.Length; j++)
                {
                    double x = j - Half;
                    model[j] = .5 * (Cdf((x + key.Item2) / 4) - Cdf((x - key.Item2) / 4))
                        - (Cdf((x + key.Item1) / 4) - Cdf((x - key.Item1) / 4));
                }
                Detrend(model);
                double energy = Dot(model, model);
                kernel = model.Select(value => (float)(value / energy)).ToArray();
                kernels[key] = kernel;
            }
            for (int a = 0; a < Rays; a += 2)
            {
                Vector3 contrast = Vector3.Zero;
                for (int j = 0; j < kernel.Length; j++) contrast += rays[a][r - Half + j] * kernel[j];
                strengths[a / 2] = contrast.Length() / Math.Sqrt(3);
            }
            response[r] = Median(strengths);
        }
        double threshold = Math.Max(.08, .25 * response.Max());
        var peaks = new List<int>();
        foreach (int r in Enumerable.Range(Half + 2, response.Length - 2 * Half - 4)
            .Where(r => response[r] >= threshold && response[r] > response[r - 2] && response[r] >= response[r + 2])
            .OrderByDescending(r => response[r]))
        {
            if (peaks.All(k => Math.Abs(k - r) * StepMm >= 1.1)) peaks.Add(r);
            if (peaks.Count == 12) break;
        }
        if (peaks.Count < Addresses.Length) return null;
        peaks.Sort();
        int[] trial = new int[Addresses.Length];
        int[]? best = null;
        double bestScore = double.NegativeInfinity, squaredRange = 0;
        Search(0, 0);
        if (best is null) return null;
        var gates = new double[Addresses.Length];
        for (int i = 0; i < gates.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            gates[i] = Refine(rays, best[i], squaredRange);
            if (!double.IsFinite(gates[i])) return null;
        }
        return gates;

        void Search(int depth, int start)
        {
            if (depth < trial.Length)
            {
                for (int i = start; i <= peaks.Count - (trial.Length - depth); i++)
                {
                    trial[depth] = peaks[i];
                    Search(depth + 1, i + 1);
                }
                return;
            }
            double[] squared = trial.Select(k => Radius(k) * Radius(k)).ToArray();
            var (slope, offset) = Line(Addresses, squared);
            if (slope <= 0 || offset <= 0) return;
            double rms = Math.Sqrt(trial.Select((k, i) => Math.Pow(Radius(k) - Math.Sqrt(offset + slope * Addresses[i]), 2)).Average());
            double score = trial.Average(k => response[k]) - .8 * rms;
            if (rms <= .13 && score > bestScore) { bestScore = score; squaredRange = slope; best = (int[])trial.Clone(); }
        }
    }

    // Smoothed black plateau and white shoulders for a gate defined in progress rather than radius.
    internal static void GateShape(Span<double> band, Span<double> shoulder, double startR, double center,
        double squaredRange, double blur)
    {
        double center2 = center * center;
        double a = Math.Sqrt(center2 - White * squaredRange), b = Math.Sqrt(center2 - Ink * squaredRange);
        double c = Math.Sqrt(center2 + Ink * squaredRange), d = Math.Sqrt(center2 + White * squaredRange);
        for (int i = 0; i < band.Length; i++)
        {
            double r = startR + i * StepMm;
            band[i] = Cdf((r - b) / blur) - Cdf((r - c) / blur);
            shoulder[i] = Cdf((r - a) / blur) - Cdf((r - d) / blur) - band[i];
        }
    }

    private static double Refine(Vector3[][] rays, int peak, double squaredRange)
    {
        const int n = 95, half = n / 2;
        double center = Radius(peak), start = Radius(peak - half);
        var model = new double[n];
        var band = new double[n];
        var shoulder = new double[n];
        GateShape(band, shoulder, start, center, squaredRange, .08);
        Detrend(band);
        double energy = Dot(band, band);
        var slopes = new Vector3[Rays];
        double maxSlope = 0;
        for (int a = 0; a < Rays; a += 2)
        {
            Vector3 v = Vector3.Zero;
            for (int j = 0; j < n; j++) v += rays[a][peak - half + j] * (float)band[j];
            slopes[a] = v / (float)energy;
            maxSlope = Math.Max(maxSlope, Vector3.Abs(slopes[a]).MaxComponent());
        }
        int count = 0;
        for (int a = 0; a < Rays; a += 2)
        {
            for (int color = 0; color < 3; color++)
            {
                double contrast = slopes[a][color];
                if (Math.Abs(contrast) <= Math.Max(.035, .1 * maxSlope)) continue;
                double weight = Math.Sign(contrast) / Math.Max(.07, Math.Abs(contrast));
                for (int j = 0; j < n; j++) model[j] += weight * rays[a][peak - half + j][color];
                count++;
            }
        }
        if (count < 3) return double.NaN;
        for (int j = 0; j < n; j++) model[j] /= count;
        Detrend(model);
        // Variable projection: fit background/ink/shoulder amplitudes linearly for each
        // proposed edge location, optical blur and gate width, then refine the three shape parameters.
        double[] p = [center, 1, .095], steps = [.04, .065, .017];
        double cost = Error();
        for (int step = 0; step < 8; step++)
        {
            for (int k = 0; k < 3; k++)
            {
                double prev = p[k], best = prev;
                for (int direction = -1; direction <= 1; direction += 2)
                {
                    p[k] = prev + direction * steps[k];
                    if (Math.Abs(p[0] - center) > .22 || p[1] is < .75 or > 1.3 || p[2] is < .045 or > .18) continue;
                    double c = Error();
                    if (c < cost) { cost = c; best = p[k]; }
                }
                p[k] = best;
                steps[k] *= .65;
            }
        }
        return p[0];

        double Error()
        {
            GateShape(band, shoulder, start, p[0], p[1] * squaredRange, p[2]);
            Detrend(band); Detrend(shoulder);
            double aa = Dot(band, band), ab = Dot(band, shoulder), bb = Dot(shoulder, shoulder);
            double x = Dot(model, band), y = Dot(model, shoulder), det = aa * bb - ab * ab;
            if (det <= 1e-10) return double.PositiveInfinity;
            double ink = (x * bb - y * ab) / det, gutter = (y * aa - x * ab) / det, sum = 0;
            for (int j = 0; j < n; j++)
            {
                double e = model[j] - ink * band[j] - gutter * shoulder[j];
                sum += Math.Sqrt(.03 * .03 + e * e); // robust soft-L1 shape fit
            }
            return sum;
        }
    }

    private static float MaxComponent(this Vector3 v) => Math.Max(v.X, Math.Max(v.Y, v.Z));
}
