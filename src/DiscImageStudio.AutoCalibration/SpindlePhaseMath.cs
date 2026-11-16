namespace DiscImageStudio.AutoCalibration;

public readonly record struct SpindlePeriodFit(double PeriodNanoseconds, double Coherence, double ReferenceNanoseconds);

public readonly record struct CircularPhaseStatistics(
    double MeanDegrees,
    double StandardDeviationDegrees,
    double StandardErrorDegrees,
    int UsedSamples);

public static class SpindlePhaseMath
{
    public const double SigmaFloorDegrees = 0.20;
    private const double OutlierDegrees = 12.0;
    private const double Tau = Math.PI * 2;

    public static double WrapDegrees(double degrees)
    {
        double wrapped = degrees % 360.0;
        if (wrapped < 0)
        {
            wrapped += 360.0;
        }

        return wrapped > 180.0 ? wrapped - 360.0 : wrapped;
    }

    public static double PhaseDegrees(double timeNanoseconds, SpindlePeriodFit fit) =>
        PositiveRemainder((timeNanoseconds - fit.ReferenceNanoseconds) / fit.PeriodNanoseconds, 1) * 360.0;

    public static SpindlePeriodFit RefinePeriod(
        IReadOnlyList<double> timestamps,
        double priorPeriodNanoseconds,
        double fractionalWindow)
    {
        ValidateTimestamps(timestamps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priorPeriodNanoseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fractionalWindow);

        double span = Math.Max(timestamps[^1] - timestamps[0], 1);
        double frequency = 1.0 / priorPeriodNanoseconds;
        double low = frequency * (1.0 - fractionalWindow);
        double high = frequency * (1.0 + fractionalWindow);
        double step = 1.0 / (10.0 * span);
        for (int level = 0; level < 3; level++)
        {
            frequency = FindPeak(timestamps, low, high, step).Frequency;
            low = frequency - step;
            high = frequency + step;
            step /= 20.0;
        }

        return Finish(timestamps, frequency);
    }

    public static SpindlePeriodFit EstimatePeriod(IReadOnlyList<double> timestamps, double rpmLow, double rpmHigh)
    {
        ValidateTimestamps(timestamps);
        if (!(rpmLow > 0) || !(rpmHigh > rpmLow))
        {
            throw new ArgumentOutOfRangeException(nameof(rpmLow));
        }

        double span = Math.Max(timestamps[^1] - timestamps[0], 1);
        double low = rpmLow / 60e9;
        double high = rpmHigh / 60e9;
        double step = 1.0 / (10.0 * span);
        int samples = checked((int)Math.Ceiling((high - low) / step) + 1);
        double[] coherence = new double[samples];
        double best = 0;
        int firstIndex = 0;
        for (int i = 0; i < samples; i++)
        {
            coherence[i] = Resultant(timestamps, low + i * step).Magnitude;
            best = Math.Max(best, coherence[i]);
        }

        for (int i = 0; i < samples; i++)
        {
            if (coherence[i] >= 0.9 * best)
            {
                firstIndex = i;
                break;
            }
        }

        double first = low + firstIndex * step;
        double localFrequency = first;
        double localCoherence = -1;
        for (int i = 0; i < samples; i++)
        {
            double frequency = low + i * step;
            if (Math.Abs(frequency - first) <= first * 0.02 && coherence[i] > localCoherence)
            {
                localFrequency = frequency;
                localCoherence = coherence[i];
            }
        }

        return RefinePeriod(timestamps, 1.0 / localFrequency, 0.01);
    }

    public static CircularPhaseStatistics CircularStatistics(IReadOnlyList<double> phases)
    {
        if (phases.Count == 0 || phases.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("必须提供有限的相位样本。", nameof(phases));
        }

        double anchor = CircularMean(phases);
        double[] offsets = phases.Select(value => WrapDegrees(value - anchor)).ToArray();
        double center = Median(offsets);
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] -= center;
        }

        double[] absolute = offsets.Select(Math.Abs).ToArray();
        double sigma = Math.Max(1.4826 * Median(absolute), 0.05);
        double[] kept = offsets.Where(offset => Math.Abs(offset) <= 3.0 * sigma).ToArray();
        double count = Math.Max(kept.Length, 1);
        double mean = kept.Sum() / count;
        double variance = kept.Sum(value => (value - mean) * (value - mean)) / Math.Max(count - 1, 1);
        double standardDeviation = Math.Sqrt(variance);
        return new(
            PositiveRemainder(anchor + center + mean, 360),
            standardDeviation,
            Math.Max(standardDeviation / Math.Sqrt(count), SigmaFloorDegrees),
            kept.Length);
    }

    private static SpindlePeriodFit Finish(IReadOnlyList<double> timestamps, double frequency)
    {
        (double _, double mean) = Resultant(timestamps, frequency);
        double[] kept = timestamps
            .Where(time => Math.Abs(WrapDegrees((Tau * frequency * (time - timestamps[0]) - mean) * 180.0 / Math.PI)) <= OutlierDegrees)
            .ToArray();
        IReadOnlyList<double> used = kept.Length >= timestamps.Count * 2 / 3 ? kept : timestamps;
        (double coherence, mean) = Resultant(used, frequency);
        double period = 1.0 / frequency;
        return new(period, coherence, used[0] + mean / (Tau * frequency));
    }

    private static (double Frequency, double Magnitude) FindPeak(
        IReadOnlyList<double> timestamps,
        double low,
        double high,
        double step)
    {
        double bestFrequency = low;
        double bestMagnitude = -1;
        int count = checked((int)Math.Ceiling((high - low) / step));
        for (int i = 0; i <= count; i++)
        {
            double frequency = low + i * step;
            double magnitude = Resultant(timestamps, frequency).Magnitude;
            if (magnitude > bestMagnitude)
            {
                bestFrequency = frequency;
                bestMagnitude = magnitude;
            }
        }

        return (bestFrequency, bestMagnitude);
    }

    private static (double Magnitude, double Angle) Resultant(IReadOnlyList<double> timestamps, double frequency)
    {
        double real = 0;
        double imaginary = 0;
        double origin = timestamps[0];
        foreach (double timestamp in timestamps)
        {
            double angle = Tau * frequency * (timestamp - origin);
            real += Math.Cos(angle);
            imaginary += Math.Sin(angle);
        }

        return (Math.Sqrt(real * real + imaginary * imaginary) / timestamps.Count, Math.Atan2(imaginary, real));
    }

    private static double CircularMean(IReadOnlyList<double> values)
    {
        double sine = 0;
        double cosine = 0;
        foreach (double value in values)
        {
            sine += Math.Sin(value * Math.PI / 180.0);
            cosine += Math.Cos(value * Math.PI / 180.0);
        }

        return Math.Atan2(sine, cosine) * 180.0 / Math.PI;
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }

    private static double PositiveRemainder(double value, double divisor)
    {
        double result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static void ValidateTimestamps(IReadOnlyList<double> timestamps)
    {
        if (timestamps.Count < 2 || timestamps.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("至少需要两个有限的时间戳。", nameof(timestamps));
        }
    }
}
