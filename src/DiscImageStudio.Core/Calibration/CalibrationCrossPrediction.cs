namespace DiscImageStudio.Core.Calibration;

/// <summary>Forward prediction of the observed curve at measured radii, including valid continuation beyond the generation annulus.</summary>
public static class CalibrationCrossPrediction
{
    public static Func<double, CalibrationPoint> CreateMapping(CalibrationTarget target,
        CalibrationParameters actual, double rotationRadians = 0, int armIndex = 0)
    {
        CalibrationCrossModel model = CreateModel(target, actual, rotationRadians, armIndex);
        double startAngle = -Math.PI / 2 + armIndex * (target.HasBoundaryPattern ? Math.PI : Math.PI / 2) + rotationRadians;
        return radius => ToPoint(Sample(model, radius), startAngle);
    }

    /// <summary>
    /// Trace the complete observed-radius interval. A 0.2 mm radial step and 0.01 mm
    /// chord tolerance preserve subtle bends; an unwrapped 5-degree bound also prevents
    /// coincident endpoints from hiding turns. Budget exhaustion is explicit.
    /// </summary>
    public static IReadOnlyList<CalibrationPoint> GetOutline(CalibrationTarget target,
        CalibrationParameters actual, double minimumObservedRadiusMm, double maximumObservedRadiusMm,
        double rotationRadians = 0, int armIndex = 0, int maxPoints = 20000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(minimumObservedRadiusMm) || !double.IsFinite(maximumObservedRadiusMm)
            || minimumObservedRadiusMm <= 0 || maximumObservedRadiusMm <= minimumObservedRadiusMm)
            throw new ArgumentException("预测曲线需要有限、递增且大于零的观测半径区间。");
        if (maxPoints is < 2 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        CalibrationCrossModel model = CreateModel(target, actual, rotationRadians, armIndex);
        double startAngle = -Math.PI / 2 + armIndex * (target.HasBoundaryPattern ? Math.PI : Math.PI / 2) + rotationRadians;
        var points = new List<CalibrationPoint>();
        CalibrationCrossSample first = Sample(model, minimumObservedRadiusMm);
        CalibrationCrossSample last = Sample(model, maximumObservedRadiusMm);

        void Segment(CalibrationCrossSample a, CalibrationCrossSample b, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double span = b.Radius - a.Radius;
            CalibrationCrossSample middle = Sample(model, a.Radius + span / 2);
            CalibrationPoint pa = ToPoint(a, startAngle), pb = ToPoint(b, startAngle);
            double variation = model.DerivativeBound(a, b) * span;
            bool split = span > 0.2 || variation > Math.PI / 36;
            if (!split)
            {
                CalibrationCrossSample q1 = Sample(model, a.Radius + span / 4);
                CalibrationCrossSample q3 = Sample(model, a.Radius + span * 0.75);
                split = ChordDistance(ToPoint(middle, startAngle), pa, pb) > 0.01
                    || ChordDistance(ToPoint(q1, startAngle), pa, pb) > 0.01
                    || ChordDistance(ToPoint(q3, startAngle), pa, pb) > 0.01;
            }
            if (split)
            {
                if (depth >= 48 || points.Count >= maxPoints - 1)
                    throw OutlineLimit();
                Segment(a, middle, depth + 1);
                Segment(middle, b, depth + 1);
            }
            else
            {
                if (points.Count >= maxPoints - 1) throw OutlineLimit();
                points.Add(pa);
            }
        }
        Segment(first, last, 0);
        points.Add(ToPoint(last, startAngle));
        return points;
    }

    private static CalibrationCrossModel CreateModel(CalibrationTarget target, CalibrationParameters actual,
        double rotationRadians, int armIndex)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(actual);
        target.ValidateCompatible(actual);
        if (!double.IsFinite(rotationRadians) || armIndex is < 0 or > 3 || (target.HasSingleRadialLine && armIndex != 0)
            || (target.HasBoundaryPattern && armIndex > 1))
            throw new ArgumentException("曲线旋转或臂编号无效。");
        return new(new(target.Parameters), new(actual), target.Parameters.Kind, target.Parameters == actual, target);
    }

    private static CalibrationCrossSample Sample(CalibrationCrossModel model, double radius)
        => model.TryAtRadius(radius, out CalibrationCrossSample sample) ? sample
            : throw new ArgumentException("该观测半径在当前参数下没有有效的正半径曲线延拓，请检查照片尺度、盘心或参数范围。");

    private static CalibrationPoint ToPoint(CalibrationCrossSample sample, double startAngle)
        => new(sample.Radius * Math.Cos(startAngle + sample.Phase), sample.Radius * Math.Sin(startAngle + sample.Phase));

    private static double ChordDistance(CalibrationPoint p, CalibrationPoint a, CalibrationPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / Math.Max(1e-30, dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
    }

    private static InvalidOperationException OutlineLimit()
        => new("完整曲线采样超出点数上限；请缩小预测范围或提高采样上限后再确认形状。");
}

internal readonly record struct CalibrationCrossSample(double Radius, double SourceRadius, double Phase, double Derivative);

/// <summary>One shared forward phase model for fitting and visible prediction.</summary>
internal sealed class CalibrationCrossModel(CalibrationGeometry generated, CalibrationGeometry actual,
    CalibrationDiscKind kind, bool identical, CalibrationTarget? sourceTarget = null)
{
    internal bool TryAtRadius(double radius, out CalibrationCrossSample sample)
    {
        sample = default;
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(radius * radius)) return false;
        // Identical geometry adds no distortion to the frozen source boundary, even
        // outside [ri,ro]. Avoid cancellation near the positive-radius domain limit.
        if (identical)
        {
            if (!actual.IsConstantPitch && !actual.TryProgressAtRadiusExtended(radius, out _)) return false;
            sample = AddSourceProfile(new(radius, radius, 0, 0), 1); return true;
        }
        if (!actual.TryProgressAtRadiusExtended(radius, out double progress)
            || !generated.TryAtProgressExtended(progress, out var source)) return false;
        double ba = actual.RadialGrowthAtRadius(radius), bg = generated.RadialGrowthAtRadius(source.Radius);
        double phase = actual.AngleAtRadius(radius) - source.Angle;
        double ratio = actual.ArcLengthFactor(radius) / generated.ArcLengthFactor(source.Radius);
        double derivative = (1 - ratio) / ba;
        if (!double.IsFinite(phase) || !double.IsFinite(derivative)) return false;
        sample = AddSourceProfile(new(radius, source.Radius, phase, derivative), bg / ba * ratio);
        return true;
    }

    private CalibrationCrossSample AddSourceProfile(CalibrationCrossSample sample, double sourceDerivative)
        => sourceTarget?.HasWavyBoundary != true ? sample : sample with
        {
            Phase = sample.Phase + sourceTarget.BoundaryPhaseAtSourceRadius(sample.SourceRadius),
            Derivative = sample.Derivative + sourceTarget.BoundaryPhaseDerivative(sample.SourceRadius) * sourceDerivative,
        };

    internal double DerivativeBound(CalibrationCrossSample first, CalibrationCrossSample last)
    {
        double profileBound = sourceTarget?.BoundaryPhaseDerivativeBound ?? 0;
        if (identical) return profileBound;
        if (kind == CalibrationDiscKind.Cd && generated.UsesAreaApproximation && actual.UsesAreaApproximation)
        {
            // sourceRadius² = A*observedRadius²+B: observed/source ratio is monotonic.
            double firstRatio = first.Radius / first.SourceRadius, lastRatio = last.Radius / last.SourceRadius;
            double baCd = actual.RadialGrowthAtRadius(first.Radius), bgCd = generated.RadialGrowthAtRadius(first.SourceRadius);
            return Math.Max(Math.Abs(1 - firstRatio), Math.Abs(1 - lastRatio)) / baCd
                + profileBound * bgCd / baCd * Math.Max(firstRatio, lastRatio);
        }
        var ba = actual.RadialGrowthBounds(first.Radius, last.Radius);
        var bg = generated.RadialGrowthBounds(first.SourceRadius, last.SourceRadius);
        // Positive-radius arc-length continuation is monotonic. Bound the exact
        // derivative over the interval, without reducing phase modulo a full turn.
        double actualMinimumSquared = actual.UsesAreaApproximation ? 0 : ba.Minimum * ba.Minimum;
        double actualMaximumSquared = actual.UsesAreaApproximation ? 0 : ba.Maximum * ba.Maximum;
        double sourceMinimumSquared = generated.UsesAreaApproximation ? 0 : bg.Minimum * bg.Minimum;
        double sourceMaximumSquared = generated.UsesAreaApproximation ? 0 : bg.Maximum * bg.Maximum;
        double minimumRatio = Math.Sqrt(first.Radius * first.Radius + actualMinimumSquared)
            / Math.Sqrt(last.SourceRadius * last.SourceRadius + sourceMaximumSquared);
        double maximumRatio = Math.Sqrt(last.Radius * last.Radius + actualMaximumSquared)
            / Math.Sqrt(first.SourceRadius * first.SourceRadius + sourceMinimumSquared);
        return Math.Max(Math.Abs(1 - minimumRatio), Math.Abs(1 - maximumRatio)) / ba.Minimum
            + profileBound * bg.Maximum / ba.Minimum * maximumRatio;
    }
}
