namespace DiscImageStudio.Core.Calibration;

/// <summary>Five alternating dark/light widths, two measured inner-edge spacings, and
/// their predictions from the first and last outer edges. Missing measurements are null.</summary>
public sealed record CalibrationWideBandIntervals(double?[] SegmentWidthsMm,
    double?[] InnerEdgeSpacingsMm, double?[] PredictedInnerEdgeSpacingsMm);

public static partial class CalibrationRingFitter
{
    // Stable observation IDs, in order of increasing radius. Old v11 records use 0, 1 and 2.
    public static IReadOnlyList<int> WideBandEdgeOrder { get; } = Array.AsReadOnly(new[] { 0, 3, 1, 4, 2, 5 });

    public static bool HasRequiredWideBandEdges(CalibrationTarget target, IEnumerable<int> measuredEdgeIds)
    {
        if (!target.HasWideAddressBands) return false;
        var ids = measuredEdgeIds.ToHashSet();
        int count = target.WideAddressBands.Count;
        if (ids.Any(id => (uint)id >= (uint)(2 * count)) || !ids.Contains(0) || !ids.Contains(count - 1))
            return false;
        return (target.SchemaVersion == 11 && ids.All(id => id < count))
            || (ids.Contains(count) && ids.Contains(2 * count - 1));
    }

    private static double WideEdgeProgress(CalibrationTarget target, int id)
    {
        int count = target.WideAddressBands.Count;
        return id < count ? target.WideBandProgress(id).Start : target.WideBandProgress(id - count).EndExclusive;
    }

    // For a constant-pitch optical track, squared radius interpolates the wide edges.
    private static double InterpolateWideRadius(double q, double q0, double r0, double q1, double r1)
    {
        double fraction = (q - q0) / (q1 - q0);
        return Math.Sqrt((1 - fraction) * r0 * r0 + fraction * r1 * r1);
    }

    public static CalibrationWideBandIntervals GetWideBandIntervals(CalibrationTarget target,
        IReadOnlyList<CalibrationRingObservation> observations)
    {
        if (!target.HasWideAddressBands) throw DiagnosticMessage.Argument($"当前标靶没有宽定位环。", nameof(target));
        ValidateObservations(target, observations);
        int count = target.WideAddressBands.Count;
        var radii = new double?[2 * count];
        foreach (var edge in observations)
            radii[edge.RingIndex] = edge.RadiusMm;

        double? Width(int from, int to)
        {
            double? width = radii[to] - radii[from];
            return width > 0 ? width : null;
        }
        var segments = new double?[WideBandEdgeOrder.Count - 1];
        var spacings = new double?[count - 1];
        var predicted = new double?[count - 1];
        for (int i = 0; i < segments.Length; i++)
            segments[i] = Width(WideBandEdgeOrder[i], WideBandEdgeOrder[i + 1]);
        for (int i = 0; i < spacings.Length; i++)
            spacings[i] = Width(i, i + 1);
        if (radii[count] is double firstOuter && radii[^1] is double lastOuter && lastOuter > firstOuter)
        {
            double PredictedInner(int i) => InterpolateWideRadius(target.WideBandProgress(i).Start,
                target.WideBandProgress(0).EndExclusive, firstOuter, target.WideBandProgress(count - 1).EndExclusive, lastOuter);
            // Predict without using any of the inner edges that supplied the measured spacings.
            for (int i = 0; i < predicted.Length; i++)
            {
                double spacing = PredictedInner(i + 1) - PredictedInner(i);
                if (double.IsFinite(spacing) && spacing > 0) predicted[i] = spacing;
            }
        }
        return new(segments, spacings, predicted);
    }
}
