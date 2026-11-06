using System.Numerics;

namespace DiscImageStudio.Core.Calibration;

public static partial class CalibrationAngleSweepReader
{
    private const int TrimSamples = 3;
    private sealed record Trace(double[] Cosine, double[] Sine, double CosineWeight, double SineWeight,
        double Energy, double SineEnergy);
    private readonly record struct Score(double Correlation, double AngularGain, double Amplitude);

    private static Trace? ExtractPhoto(double lo, int n, (double Sin, double Cos)[] directions,
        Func<double, double, Vector3> sample)
    {
        var rows = new Vector3[Rays][];
        Vector3 energy = Vector3.Zero;
        for (int j = 0; j < Rays; j++)
        {
            var (sin, cos) = directions[j];
            var row = rows[j] = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                double r = lo + k * Step;
                row[k] = sample(r * cos, r * sin);
            }
            var channelSamples = new double[n];
            for (int component = 0; component < 3; component++)
            {
                for (int k = 0; k < n; k++) channelSamples[k] = row[k][component];
                HighPass(channelSamples);
                for (int k = 0; k < n; k++)
                {
                    row[k][component] = (float)channelSamples[k];
                    energy[component] += (float)Square(channelSamples[k]);
                }
            }
        }
        // Marks can be conspicuous in a colour channel while luminance is almost constant.
        int channel = energy.Y > energy.X ? 1 : 0;
        if (energy.Z > energy[channel]) channel = 2;
        var cosineProjection = new double[n];
        var sineProjection = new double[n];
        double power = 0, cosineWeight = 0, sineWeight = 0;
        for (int j = 0; j < Rays; j++)
        {
            var (s, c) = Math.SinCos((j / (Rays - 1.0) - .5) * Math.PI / 4);
            cosineWeight += c * c;
            sineWeight += s * s;
            for (int k = 0; k < n; k++)
            {
                double y = rows[j][k][channel];
                cosineProjection[k] += y * c;
                sineProjection[k] += y * s;
                if (k >= TrimSamples && k < n - TrimSamples) power += y * y;
            }
        }
        double slopePower = 0;
        for (int k = TrimSamples; k < n - TrimSamples; k++) slopePower += Square(sineProjection[k]);
        int usableSamples = n - 2 * TrimSamples;
        return power > usableSamples * Rays * Square(.001) && slopePower > usableSamples * Square(.0008 * sineWeight)
            ? new(cosineProjection, sineProjection, cosineWeight, sineWeight, power, slopePower) : null;
    }

    private static void HighPass(Span<double> values)
    {
        Span<double> tmp = stackalloc double[values.Length], background = stackalloc double[values.Length];
        Box(values, tmp);
        Box(tmp, background);
        for (int i = 0; i < values.Length; i++) values[i] -= background[i];
    }

    private static void Box(ReadOnlySpan<double> input, Span<double> output)
    {
        const int half = 11;
        double sum = 0;
        for (int i = -half; i <= half; i++) sum += input[Math.Clamp(i, 0, input.Length - 1)];
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = sum / (2 * half + 1);
            sum += input[Math.Min(i + half + 1, input.Length - 1)] - input[Math.Max(i - half, 0)];
        }
    }

    // A quadratic-phase signal convolved with a Gaussian has a closed form. The
    // envelope and radial reversal survive blur while its fast tails fade. The two
    // quadratures accommodate arbitrary rotation, bright/dark reversal and reflection.
    private static Score BestTemplate(Trace trace, int n, double offset, double curvature)
    {
        Span<double> a = stackalloc double[n], b = stackalloc double[n];
        var best = new Score();
        ReadOnlySpan<double> blurs = [.11, .20, .30];
        foreach (double sigma in blurs)
        {
            double z = curvature * Square(sigma), divisor = 1 + z * z;
            double decay = -.5 * Square(curvature * sigma) / divisor;
            double omega = .5 * curvature / divisor;
            for (int k = 0; k < n; k++)
            {
                double x2 = Square(offset + Step * k);
                (double s, double c) = Math.SinCos(omega * x2);
                double amplitude = Math.Exp(decay * x2);
                a[k] = amplitude * c; b[k] = amplitude * s;
            }
            HighPass(a);
            HighPass(b);
            // Dot products of radial cosine/sine quadratures and of their two angular
            // projections reduce the full photo fit to a two-variable least-squares solve.
            double aa = 0, ab = 0, bb = 0, aCos = 0, bSin = 0, bCos = 0, aSin = 0;
            for (int k = TrimSamples; k < n - TrimSamples; k++)
            {
                aa += a[k] * a[k];
                bb += b[k] * b[k];
                ab += a[k] * b[k];
                aCos += a[k] * trace.Cosine[k];
                bSin += b[k] * trace.Sine[k];
                bCos += b[k] * trace.Cosine[k];
                aSin += a[k] * trace.Sine[k];
            }
            double uu = aa * trace.CosineWeight + bb * trace.SineWeight, vv = bb * trace.CosineWeight + aa * trace.SineWeight;
            double uv = ab * (trace.CosineWeight - trace.SineWeight), det = uu * vv - uv * uv;
            if (det <= 1e-12) continue;
            for (int hand = -1; hand <= 1; hand += 2)
            {
                double yu = aCos - hand * bSin, yv = bCos + hand * aSin;
                double u = (yu * vv - yv * uv) / det, v = (yv * uu - yu * uv) / det;
                double corr = Math.Sqrt(Math.Max(0, u * yu + v * yv) / trace.Energy);
                if (corr <= best.Correlation) continue;
                double s2 = trace.SineWeight;
                // Exclude pure ring texture: the sign and strength of angular variation
                // within this very window must agree with the fitted radial signature.
                double angularGain = (2 * hand * s2 * (v * aSin - u * bSin) - s2 * s2 * (v * v * aa + u * u * bb - 2 * u * v * ab)) / trace.SineEnergy;
                best = new(corr, angularGain, double.Hypot(u, v));
            }
        }
        return best;
    }
}
