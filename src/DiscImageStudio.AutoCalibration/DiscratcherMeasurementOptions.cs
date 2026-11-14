namespace DiscImageStudio.AutoCalibration;

public static class DiscratcherMeasurementOptions
{
    public static bool IsValid(int maxTargets, int reps, double rangeLow, double rangeHigh) =>
        maxTargets is >= 16 and <= 72 && maxTargets % 4 == 0
        && reps is >= 16 and <= 60 && reps % 4 == 0
        && IsWholePercentInRange(rangeLow, 1, 10)
        && IsWholePercentInRange(rangeHigh, 80, 98)
        && rangeHigh > rangeLow;

    private static bool IsWholePercentInRange(double fraction, int minimumPercent, int maximumPercent)
    {
        if (!double.IsFinite(fraction))
        {
            return false;
        }

        double percent = fraction * 100;
        return percent >= minimumPercent && percent <= maximumPercent
            && Math.Abs(percent - Math.Round(percent)) < 0.000001;
    }
}
