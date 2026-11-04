using System.Numerics;
using static DiscImageStudio.Core.Calibration.CalibrationClockReader;

namespace DiscImageStudio.Core.Calibration;

internal static class CalibrationClockSectorReader
{
    private sealed record Mark(int Order, double Nominal, double Apparent);

    public static CalibrationClockSector? Read(Vector3[][] rays, double[] gates, double degrees,
        CalibrationParameters parameters, CancellationToken token)
    {
        var squared = Line(CalibrationClockGates.Addresses, gates.Select(v => v * v).ToArray());
        if (squared.Slope <= 0 || squared.Offset <= 0) return null;
        double idealInner = Math.Sqrt(squared.Offset), idealOuter = Math.Sqrt(squared.Slope + squared.Offset);
        var signal = Normalize(rays, gates, squared.Slope, token);
        double pitch = CalibrationClockRulerPattern.PeriodLengthMm(parameters) / Math.Tau;
        var marks = new List<Mark>();
        for (int order = Math.Max(1, (int)Math.Floor((idealInner - 1.5) / pitch));
            order * pitch < idealOuter + 1.5; order++)
        {
            token.ThrowIfCancellationRequested();
            double nominal = order * pitch;
            double observed = ReadPeak(signal, squared.Slope, squared.Offset, nominal, order);
            if (double.IsFinite(observed)) marks.Add(new(order, nominal, observed));
        }
        return Fit(degrees, gates, marks);
    }

    // Infer a local RGB response separately along each ray using both polarisations of the
    // address marker. A rainbow can change the contrast sign between two neighboring radii.
    private static double[][] Normalize(Vector3[][] rays, double[] gates, double squaredRange, CancellationToken token)
    {
        var levels = new Vector3[gates.Length, Rays];
        var confidence = new double[gates.Length, Rays];
        for (int k = 0; k < gates.Length; k++)
        {
            token.ThrowIfCancellationRequested();
            int first = (int)Math.Ceiling((gates[k] - .84 - StartMm) / StepMm);
            int last = (int)Math.Floor((gates[k] + .84 - StartMm) / StepMm);
            int length = last - first + 1;
            var response = new double[length];
            var shoulder = new double[length];
            CalibrationClockGates.GateShape(response, shoulder, Radius(first), gates[k], squaredRange, .1);
            for (int i = 0; i < length; i++) response[i] = .5 * (response[i] - shoulder[i]);
            Detrend(response, true);
            double norm = Dot(response, response);
            var color = new double[length];
            for (int a = 0; a < Rays; a++)
            {
                double noise = .00012;
                Vector3 level = default;
                for (int c = 0; c < 3; c++)
                {
                    for (int i = 0; i < length; i++) color[i] = rays[a][first + i][c];
                    Detrend(color, true);
                    double amp = Dot(color, response) / norm;
                    level[c] = (float)amp;
                    for (int i = 0; i < length; i++)
                    {
                        double residual = color[i] - amp * response[i];
                        noise += residual * residual / length;
                    }
                }
                levels[k, a] = level;
                confidence[k, a] = level.Length() / Math.Sqrt(noise);
            }
        }
        var result = new double[Rays][];
        for (int a = 0; a < Rays; a++)
        {
            token.ThrowIfCancellationRequested();
            var ray = rays[a];
            // Three moving averages approximate the 0.48 mm Gaussian background filter
            // in linear time. Its bandwidth is much wider than a stationary clock stripe.
            Vector3[] background = ray;
            for (int pass = 0; pass < 3; pass++) background = RunningMean(background, 24);
            var values = new double[ray.Length];
            int k = 0;
            for (int i = 0; i < ray.Length; i++)
            {
                double r = Radius(i);
                while (k < gates.Length - 2 && r > gates[k + 1]) k++;
                double t = Math.Clamp((r - gates[k]) / (gates[k + 1] - gates[k]), 0, 1);
                Vector3 level = Vector3.Lerp(levels[k, a], levels[k + 1, a], (float)t);
                double snr = (1 - t) * confidence[k, a] + t * confidence[k + 1, a];
                double power = level.LengthSquared();
                double weight = Math.Clamp((Math.Sqrt(power) - .018) / .035, 0, 1) * Math.Clamp((snr - 1.8) / 2, 0, 1);
                values[i] = weight * Vector3.Dot(level, ray[i] - background[i]) / (power + .0005);
            }
            result[a] = values;
        }
        return result;
    }

    private static Vector3[] RunningMean(Vector3[] values, int half)
    {
        var result = new Vector3[values.Length];
        Vector3 sum = default;
        for (int j = -half; j <= half; j++) sum += values[Math.Max(0, j)];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = sum / (2 * half + 1);
            sum += values[Math.Min(values.Length - 1, i + half + 1)] - values[Math.Max(0, i - half)];
        }
        return result;
    }

    private static double ReadPeak(double[][] signal, double squaredRange, double startSquared, double nominal, int order)
    {
        double[] cos = new double[Rays], sin = new double[Rays];
        for (int a = 0; a < Rays; a++)
        {
            double angle = (-22 + 44.0 * a / (Rays - 1)) * Math.PI / 180;
            (sin[a], cos[a]) = Math.SinCos(angle * order);
        }
        Detrend(sin, true); Detrend(cos, true);
        double aa = Dot(cos, cos), ab = Dot(cos, sin), bb = Dot(sin, sin), det = aa * bb - ab * ab;
        if (det <= 1e-8) return double.NaN;
        // Regress the local signal onto sin/cos of the known harmonic order; either phase
        // and both recording-surface orientations have the same magnitude.
        double window = Math.Max(.85, .021 * nominal + .28);
        const int blurLength = 64;
        int first = Math.Max(0, (int)((nominal - window - StartMm) / StepMm) - blurLength - 3);
        int last = Math.Min(signal[0].Length - 1, (int)((nominal + window - StartMm) / StepMm) + blurLength + 3);
        if (last <= first + 2) return double.NaN;
        var harmonics = new Complex[last - first + 1];
        for (int a = 0; a < Rays; a++)
        {
            var weight = new Complex((bb * cos[a] - ab * sin[a]) / det, (aa * sin[a] - ab * cos[a]) / det);
            for (int i = first; i <= last; i++) harmonics[i - first] += weight * signal[a][i];
        }
        var amplitude = new double[harmonics.Length];
        var allowed = new bool[harmonics.Length];
        double inner = Math.Sqrt(startSquared), outer = Math.Sqrt(startSquared + squaredRange);
        for (int k = 0; k < amplitude.Length; k++)
        {
            double r = Radius(first + k);
            if (Math.Abs(r - nominal) > window + StepMm || r <= inner + .3 || r >= outer - .3) continue;
            Complex background = default;
            for (int j = -blurLength; j <= blurLength; j++)
                background += HarmonicKernel[j + blurLength] * harmonics[Math.Clamp(k + j, 0, harmonics.Length - 1)];
            amplitude[k] = (harmonics[k] - background).Magnitude;
            double q = (r * r - startSquared) / squaredRange;
            allowed[k] = Math.Abs(r - nominal) < window && CalibrationClockGates.Addresses.All(address => Math.Abs(address - q) > .011);
        }
        int peak = -1;
        for (int k = 1; k < amplitude.Length - 1; k++)
            if (allowed[k] && amplitude[k] > .028 && amplitude[k] > amplitude[k - 1] && amplitude[k] >= amplitude[k + 1]
                && (peak < 0 || amplitude[k] > amplitude[peak])) peak = k;
        if (peak < 0) return double.NaN;
        int[] near = Enumerable.Range(Math.Max(0, peak - 11), Math.Min(amplitude.Length - 1, peak + 11) - Math.Max(0, peak - 11) + 1)
            .Where(k => allowed[k]).ToArray();
        if (near.Length < 10) return double.NaN;
        // Fit Gaussian peak center, intercept and amplitude, with a free optical width.
        // An isolated high pixel, a diffuse glare band, or an unresolved address edge is insufficient.
        double center = 0, width = .095, step = .025, best = double.PositiveInfinity, height = 0;
        var model = new double[near.Length];
        for (int round = 0; round < 7; round++)
        {
            double previousCenter = center, previousWidth = width;
            for (int m = -1; m <= 1; m++)
            for (int s = -1; s <= 1; s++)
            {
                double mu = previousCenter + m * step, sigma = previousWidth + s * step;
                if (sigma is < .035 or > .19) continue;
                double sum = 0, yy = 0, xx = 0, xy = 0;
                for (int j = 0; j < near.Length; j++)
                {
                    double d = ((near[j] - peak) * StepMm - mu) / sigma;
                    double v = model[j] = Math.Exp(-.5 * d * d), y = amplitude[near[j]];
                    sum += v; xx += v * v; yy += y; xy += v * y;
                }
                double h = (xy - sum * yy / near.Length) / (xx - sum * sum / near.Length);
                if (!double.IsFinite(h) || h < .023 || h > 1.01) continue;
                double offset = (yy - h * sum) / near.Length, loss = 0;
                if (offset is < -.05 or > .15) continue;
                for (int j = 0; j < near.Length; j++) loss += Math.Pow(offset + h * model[j] - amplitude[near[j]], 2);
                if (loss < best) { best = loss; center = mu; width = sigma; height = h; }
            }
            step *= .6;
        }
        return width < .185 && best < near.Length * Math.Pow(.5 * height, 2) ? Radius(first + peak) + center : double.NaN;
    }

    private static readonly double[] HarmonicKernel = CreateKernel();
    private static double[] CreateKernel()
    {
        var kernel = Enumerable.Range(-64, 129).Select(i => Math.Exp(-.5 * i * i / (18.0 * 18))).ToArray();
        double sum = kernel.Sum();
        return kernel.Select(v => v / sum).ToArray();
    }

    private static CalibrationClockSector? Fit(double degrees, double[] gates, List<Mark> marks)
    {
        if (marks.Count < 5) return null;
        Mark[] best = [];
        double bestError = double.PositiveInfinity;
        for (int i = 0; i < marks.Count; i++)
        for (int j = i + 1; j < marks.Count; j++)
        {
            double s = (marks[j].Apparent - marks[i].Apparent) / (marks[j].Nominal - marks[i].Nominal);
            double b = marks[j].Apparent - s * marks[j].Nominal;
            if (Math.Abs(s - 1) > .028 || Math.Abs(b) > .60) continue;
            var support = marks.Where(m => Math.Abs(m.Apparent - (s * m.Nominal + b)) < .065).ToArray();
            if (support.Length < Math.Max(5, best.Length) || support[^1].Nominal - support[0].Nominal < 15) continue;
            double err = Median(support.Select(m => Math.Abs(m.Apparent - (s * m.Nominal + b))).ToArray());
            if (support.Length > best.Length || err < bestError) { best = support; bestError = err; }
        }
        if (best.Length == 0) return null;
        var (scale, offset) = Line(best.Select(m => m.Nominal).ToArray(), best.Select(m => m.Apparent).ToArray());
        double clockRms = Math.Sqrt(best.Average(m => Math.Pow(m.Apparent - (scale * m.Nominal + offset), 2)));
        if (clockRms > .025 || Math.Abs(scale - 1) > .028 || Math.Abs(offset) > .60) return null;
        var radii = gates.Select(r => (r - offset) / scale).ToArray();
        var q = CalibrationClockGates.Addresses;
        var (slope, intercept) = Line(q, radii.Select(v => v * v).ToArray());
        if (slope <= 0 || intercept <= 0) return null;
        // Starting with the squared-radius linear solution, fit residuals in millimetres.
        double inner = Math.Sqrt(intercept), outer = Math.Sqrt(slope + intercept);
        for (int pass = 0; pass < 3; pass++)
        {
            double aa = 0, ab = 0, bb = 0, ar = 0, br = 0;
            for (int i = 0; i < q.Length; i++)
            {
                double model = Math.Sqrt((1 - q[i]) * inner * inner + q[i] * outer * outer);
                double e = radii[i] - model, a = (1 - q[i]) * inner / model, b = q[i] * outer / model;
                double w = 1 / Math.Sqrt(1 + e * e / (.02 * .02));
                aa += w * a * a; ab += w * a * b; bb += w * b * b; ar += w * a * e; br += w * b * e;
            }
            double det = aa * bb - ab * ab;
            inner += (ar * bb - br * ab) / det;
            outer += (br * aa - ar * ab) / det;
        }
        double gateRms = Math.Sqrt(radii.Select((r, i) =>
            Math.Pow(r - Math.Sqrt((1 - q[i]) * inner * inner + q[i] * outer * outer), 2)).Average());
        if (!double.IsFinite(gateRms) || gateRms > .035 || inner < 7.5 || outer <= inner || outer > 60) return null;
        return new(degrees, inner, outer, gateRms, clockRms, best.Length);
    }
}
