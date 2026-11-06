using System.Numerics;

namespace DiscImageStudio.Core.Calibration;

public enum CalibrationSectorScaleStatus { Unresolved, Disagree, OutsideDisc, Verified }

public sealed record CalibrationSectorScaleResult(
    CalibrationParameters? Corrected, double ApparentOverPhysical, double InnerScale, double OuterScale,
    double InnerCoherence, double OuterCoherence, CalibrationSectorScaleStatus Status = CalibrationSectorScaleStatus.Unresolved)
{
    public bool Succeeded => Corrected is not null;
}

/// <summary>Read the two swept gaps independently. In a thin spiral at fixed linear density,
/// scaling every apparent radius by 1/k scales its unwrapped track angle by k. The unknown
/// rotation of the photograph and the local sign of the optical contrast are free in each gap.</summary>
public static class CalibrationSectorScaleFitter
{
    private const double Range = .015;
    private const double RequiredCoherence = .72;
    private const double MaximumDisagreement = .00055;
    private readonly record struct Sample(double Radius, double ApparentAngle, double BasePhase, Complex Moment);
    private readonly record struct Peak(bool Usable, double Scale, double Coherence);

    /// <param name="readFirstHarmonic">At the apparent radius in mm, return the angular mean
    /// of normalized optical contrast times exp(iθ), with θ increasing toward image Y-down.</param>
    public static CalibrationSectorScaleResult Verify(CalibrationTarget target, CalibrationParameters apparent,
        double mmPerPixel, Func<double, Complex> readFirstHarmonic, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(readFirstHarmonic);
        target.ValidateCompatible(apparent);
        if (!target.HasAngularReference || target.VisibleRings.Count < 8 || !double.IsFinite(mmPerPixel) || mmPerPixel <= 0)
            return new(null, 0, 0, 0, 0, 0);

        var generated = new CalibrationGeometry(target.Parameters);
        var observed = new CalibrationGeometry(apparent);
        // The homothetic-angle approximation above is appropriate for a full optical disc;
        // rule out tiny synthetic track lengths or pathological cubic pitch distributions.
        if (observed.RadialGrowthBounds(apparent.InnerRadiusMm, apparent.OuterRadiusMm).Maximum / apparent.InnerRadiusMm > .0001)
            return new(null, 0, 0, 0, 0, 0);
        Peak inner = SolveGap(1);
        Peak outer = SolveGap(5);
        double scale = .5 * (inner.Scale + outer.Scale);
        var result = new CalibrationSectorScaleResult(null, scale, inner.Scale, outer.Scale, inner.Coherence, outer.Coherence);
        if (!inner.Usable || !outer.Usable) return result;
        if (Math.Abs(inner.Scale - outer.Scale) > MaximumDisagreement)
            return result with { Status = CalibrationSectorScaleStatus.Disagree };
        var corrected = apparent with { InnerRadiusMm = apparent.InnerRadiusMm / scale, OuterRadiusMm = apparent.OuterRadiusMm / scale };
        try { corrected.Validate(); }
        catch (ArgumentException) { return result with { Status = CalibrationSectorScaleStatus.OutsideDisc }; }
        return result with { Corrected = corrected, Status = CalibrationSectorScaleStatus.Verified };

        Peak SolveGap(int index)
        {
            var first = target.VisibleRings[index];
            var last = target.VisibleRings[index + 1];
            // Keep the white gutter and the physical ring edges out of the samples.
            double margin = .18 + Math.Max(2 * mmPerPixel, .12);
            double start = first.RadiusMm + first.WidthMm / 2 + margin;
            double end = last.InnerRadiusMm - margin;
            if (end <= start) return default;
            double apparentStart = observed.AtProgress(generated.ProgressAtRadius(start)).Radius;
            double apparentEnd = observed.AtProgress(generated.ProgressAtRadius(end)).Radius;
            double pixels = (apparentEnd - apparentStart) / mmPerPixel;
            if (pixels < 12) return default;
            int count = Math.Clamp((int)Math.Ceiling(pixels * 2.5), 32, 256);
            var samples = new Sample[count];
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                double source = start + (end - start) * i / (count - 1);
                var at = observed.AtProgress(generated.ProgressAtRadius(source));
                double phase = target.SectorAngleAtRadius(source) + at.Angle - generated.AngleAtRadius(source);
                samples[i] = new(at.Radius, at.Angle, phase, readFirstHarmonic(at.Radius));
            }
            return Match(samples, mmPerPixel, token);
        }
    }

    private static Peak Match(Sample[] samples, double mmPerPixel, CancellationToken token)
    {
        int n = samples.Length;
        double[] x = new double[n], derivative = new double[n], derivativeScale = new double[n];
        var data = new Complex[n];
        double xx = 0;
        Complex mean = Complex.Zero, slope = Complex.Zero;
        for (int i = 0; i < n; i++)
        {
            x[i] = 2.0 * i / (n - 1) - 1;
            xx += x[i] * x[i];
            mean += samples[i].Moment;
            slope += samples[i].Moment * x[i];
            int first = Math.Max(0, i - 1), last = Math.Min(n - 1, i + 1);
            double span = samples[last].Radius - samples[first].Radius;
            derivative[i] = (samples[last].BasePhase - samples[first].BasePhase) / span;
            derivativeScale[i] = (samples[last].ApparentAngle - samples[first].ApparentAngle) / span;
        }
        double energy = 0;
        for (int i = 0; i < n; i++)
        {
            data[i] = samples[i].Moment - mean / n - slope * (x[i] / xx);
            energy += Power(data[i]);
        }
        if (!double.IsFinite(energy) || Math.Sqrt(energy / n) < .008) return default;
        double sweep = samples[^1].ApparentAngle - samples[0].ApparentAngle;
        int steps = Math.Clamp((int)Math.Ceiling(2 * Range * sweep / .7), 240, 5000);
        double step = 2 * Range / steps;
        var scores = new double[steps + 1];
        int best = 0;
        for (int i = 0; i <= steps; i++)
        {
            if ((i & 15) == 0) token.ThrowIfCancellationRequested();
            scores[i] = Score(-Range + i * step);
            if (scores[i] > scores[best]) best = i;
        }
        if (best < 2 || best > steps - 2 || scores[best] < RequiredCoherence)
            return new(false, 1 - Range + best * step, scores[best]);
        for (int i = 0; i <= steps; i++)
        {
            double separation = Math.Abs(i - best) * step;
            if (separation < .00015) continue;
            // A broad shelf can be ambiguous even without a second local maximum.
            if (separation >= MaximumDisagreement && scores[i] >= scores[best] - .035)
                return new(false, 1 - Range + best * step, scores[best]);
            if ((i == 0 || scores[i] >= scores[i - 1]) && (i == steps || scores[i] >= scores[i + 1])
                && scores[i] > scores[best] - .10)
                return new(false, 1 - Range + best * step, scores[best]);
        }
        // A resolved peak has two shoulders. Interpolation here is local; it cannot
        // jump across a missing turn or requisition an angular offset from another gap.
        double curvature = 2 * scores[best] - scores[best - 1] - scores[best + 1];
        if (curvature <= 0) return default;
        double fraction = Math.Clamp((scores[best + 1] - scores[best - 1]) / (2 * curvature), -.5, .5);
        return new(true, 1 - Range + (best + fraction) * step, scores[best]);

        double Score(double delta)
        {
            double top = 0;
            // The radial point-spread function is unknown. Its attenuation is important:
            // a highly sheared, subpixel sector cannot deliver phase observations.
            foreach (double blur in BlurInPixels)
            {
                double sumPower = 0, sumPowerSquared = 0;
                Complex m = Complex.Zero, mx = Complex.Zero, cross = Complex.Zero;
                for (int k = 0; k < n; k++)
                {
                    double omega = (derivative[k] + delta * derivativeScale[k]) * blur * mmPerPixel;
                    double amplitude = Math.Exp(-.5 * omega * omega);
                    double phase = samples[k].BasePhase - samples[n / 2].BasePhase
                        + delta * (samples[k].ApparentAngle - samples[n / 2].ApparentAngle);
                    var (sin, cos) = Math.SinCos(phase);
                    var value = new Complex(amplitude * cos, amplitude * sin);
                    double power = amplitude * amplitude;
                    sumPower += power;
                    sumPowerSquared += power * power;
                    m += value;
                    mx += x[k] * value;
                    cross += data[k] * Complex.Conjugate(value);
                }
                double effectivePixels = sumPower * sumPower / Math.Max(1e-20, sumPowerSquared)
                    * (samples[^1].Radius - samples[0].Radius) / ((n - 1) * mmPerPixel);
                double modelEnergy = sumPower - Power(m) / n - Power(mx) / xx;
                if (effectivePixels < 5 || modelEnergy < .01 * n) continue;
                double amplitudeFit = cross.Magnitude / modelEnergy;
                if (amplitudeFit < .015) continue;
                top = Math.Max(top, cross.Magnitude / Math.Sqrt(energy * modelEnergy));
            }
            return top;
        }
    }

    private static readonly double[] BlurInPixels = [.7, 1.1, 1.7];
    private static double Power(Complex value) => value.Real * value.Real + value.Imaginary * value.Imaginary;
}
