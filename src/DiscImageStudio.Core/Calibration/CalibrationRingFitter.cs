using System.Text.Json.Serialization;

namespace DiscImageStudio.Core.Calibration;

/// <summary>Zero-based index among complete engraved rings, ordered from the centre outwards.</summary>
public sealed record CalibrationRing(int RingIndex, double RadiusMm, double WidthMm)
{
    public double InnerRadiusMm => RadiusMm - WidthMm / 2;
}

/// <summary>Radius of one observed edge on the rectified photo. For wide bands, IDs 0..2
/// remain the historical inner edges; IDs 3..5 are the corresponding outer edges.</summary>
public sealed record CalibrationRingObservation(int RingIndex, double RadiusMm,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalibrationPoint>? Points = null);

public static partial class CalibrationRingFitter
{
    public static int MeasurementCount(CalibrationTarget target) =>
        target.HasWideAddressBands ? 2 * target.VisibleRings.Count : target.VisibleRings.Count;

    /// <summary>The rectified disc centre is already known. Its least-squares radius is
    /// the mean point distance, even when all three clicks are on the same side of the disc.</summary>
    public static double MeasureArc(IReadOnlyList<CalibrationPoint> points, double physicalOuterRadiusMm)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count != 3)
            throw new ArgumentException("请沿同一条圆环的内缘标出三个点。");
        if (!double.IsFinite(physicalOuterRadiusMm) || physicalOuterRadiusMm <= 7.5)
            throw new ArgumentOutOfRangeException(nameof(physicalOuterRadiusMm));
        Span<double> radii = stackalloc double[3];
        Span<double> angles = stackalloc double[3];
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            double radius = p is null ? double.NaN : double.Hypot(p.X, p.Y);
            if (!double.IsFinite(radius) || radius < 7.5 || radius > physicalOuterRadiusMm)
                throw new ArgumentException("圆弧采样点必须位于中心孔与盘片外缘之间。");
            radii[i] = radius;
            angles[i] = Math.Atan2(p!.Y, p.X);
        }
        double largestSeparation = 0;
        for (int i = 1; i < angles.Length; i++)
            for (int j = 0; j < i; j++)
            {
                double angle = Math.Abs(Math.IEEERemainder(angles[i] - angles[j], Math.Tau));
                if (angle < Math.PI / 180)
                    throw new ArgumentException("三个采样点靠得太近，请沿同一条圆弧分散选点。");
                largestSeparation = Math.Max(largestSeparation, angle);
            }
        if (largestSeparation < 6 * Math.PI / 180)
            throw new ArgumentException("三个采样点靠得太近，请沿同一条圆弧分散选点。");
        radii.Sort();
        if (radii[2] - radii[0] > 0.5)
            throw new ArgumentException("三个采样点的半径相差过大，请重新对准同一圈的内缘。");
        return (radii[0] + radii[1] + radii[2]) / 3;
    }

    // Partial drafts, including crossed arcs that are still being edited, can be saved.
    public static void ValidateObservations(CalibrationTarget target, IReadOnlyList<CalibrationRingObservation> observations,
        double physicalOuterRadiusMm = 1000)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(observations);
        if (!double.IsFinite(physicalOuterRadiusMm) || physicalOuterRadiusMm <= 7.5)
            throw new ArgumentOutOfRangeException(nameof(physicalOuterRadiusMm));
        var indices = new HashSet<int>();
        int count = MeasurementCount(target);
        if (observations.Count > count || observations.Any(p => p is null
            || (uint)p.RingIndex >= (uint)count || !indices.Add(p.RingIndex)
            || !double.IsFinite(p.RadiusMm) || p.RadiusMm <= 0 || p.RadiusMm > physicalOuterRadiusMm))
            throw new ArgumentException("圆环编号或实测半径无效。");
        foreach (var p in observations)
        {
            if (p.Points is null) continue;
            double measured;
            if (target.HasWideAddressBands && p.Points.Count == 1)
            {
                var point = p.Points[0];
                measured = point is null ? double.NaN : double.Hypot(point.X, point.Y);
                if (!double.IsFinite(measured) || measured < 7.5 || measured > Math.Min(60, physicalOuterRadiusMm))
                    throw new ArgumentException("圆弧采样点必须位于中心孔与盘片外缘之间。");
            }
            else measured = MeasureArc(p.Points, physicalOuterRadiusMm);
            if (Math.Abs(measured - p.RadiusMm) > 1e-6)
                throw new ArgumentException("保存的圆弧采样点与实测半径不一致。");
        }
    }

    /// <summary>Fit the two full-track endpoint radii while preserving the generation snapshot's
    /// sector count, linear density, DVD pitch shape and BD recording range. Each source circle
    /// fixes a track progress; no angular phase, rotation or turn count is observed.</summary>
    public static CalibrationFitResult Fit(CalibrationTarget target, IReadOnlyList<CalibrationRingObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ValidateObservations(target, observations);
        cancellationToken.ThrowIfCancellationRequested();
        CalibrationParameters parameters = target.ParametersForGeometry(target.Parameters);
        CalibrationRingObservation[] ordered;
        double[] progress;
        if (target.HasWideAddressBands)
        {
            if (!HasRequiredWideBandEdges(target, observations.Select(p => p.RingIndex)))
                return Failed(target.SchemaVersion == 11 && observations.All(p => p.RingIndex < target.VisibleRings.Count)
                    ? "请量取最内和最外两条宽带靠盘心的边缘。"
                    : "请量取第 1、3 条深带各自的内缘和外缘。");
            ordered = observations.OrderBy(p => WideEdgeProgress(target, p.RingIndex)).ToArray();
            progress = ordered.Select(p => WideEdgeProgress(target, p.RingIndex)).ToArray();
        }
        else
        {
            if (observations.Count < 3) return Failed("请至少对准三条圆环，并包含靠内与靠外的圆环。");
            ordered = observations.OrderBy(p => p.RingIndex).ToArray();
            double[] source = ordered.Select(p => target.VisibleRings[p.RingIndex].InnerRadiusMm).ToArray();
            if (source[^1] - source[0] < 0.30 * (parameters.OuterRadiusMm - parameters.InnerRadiusMm))
                return Failed("所选圆环过于集中，请补选靠内或靠外的圆环。");
            progress = source.Select(new CalibrationGeometry(parameters).ProgressAtRadius).ToArray();
        }

        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].RadiusMm <= ordered[i - 1].RadiusMm)
                return Failed(target.HasWideAddressBands ? "交界的径向顺序不符，请核对带号和内外缘。"
                    : "实测圆环的内外顺序不符，请检查编号或重新拖动圆弧。");
        if (target.HasWideAddressBands)
        {
            // Compare every interior observation with the prediction from the extreme
            // measured edges alone. Only absolute edge radii enter the fit below.
            for (int i = 1; i < ordered.Length - 1; i++)
            {
                double predicted = InterpolateWideRadius(progress[i], progress[0], ordered[0].RadiusMm,
                    progress[^1], ordered[^1].RadiusMm);
                if (Math.Abs(ordered[i].RadiusMm - predicted) > .40)
                    return Failed(target.SchemaVersion == 11 && ordered[^1].RingIndex == target.VisibleRings.Count - 1
                        ? "中间带的位置与内外两带相差超过 0.40 mm，请核对圈号、内缘和照片矫正。"
                        : "交界位置与其余边缘推算的位置相差超过 0.40 mm，请核对带号和读边。");
            }
        }
        double[] Residuals(CalibrationParameters candidate)
        {
            var geometry = new CalibrationGeometry(candidate);
            return progress.Select((q, i) => geometry.AtProgress(q).Radius - ordered[i].RadiusMm).ToArray();
        }

        // Only two parameters vary. Using the physical forward model here also works with
        // nonzero saved cubic pitch and with short spirals where the thin-track formula drifts.
        const string invalid = "圆环无法拟合出有效的内外半径，请检查所选圆弧。";
        double[] residual = Residuals(parameters);
        double cost = residual.Sum(r => r * r);
        for (int iteration = 0; iteration < 30; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double band = parameters.OuterRadiusMm - parameters.InnerRadiusMm;
            double h = Math.Min(0.0001, Math.Min(band, parameters.TrackLengthMm - band) / 4);
            // Step inwards at the BD physical limit and keep each finite-difference geometry valid.
            double ho = parameters.Kind == CalibrationDiscKind.BluRay && parameters.OuterRadiusMm + h > 60 ? -h : h;
            double[] ri = Residuals(parameters with { InnerRadiusMm = parameters.InnerRadiusMm + h });
            double[] ro = Residuals(parameters with { OuterRadiusMm = parameters.OuterRadiusMm + ho });
            double aa = 0, ab = 0, bb = 0, ar = 0, br = 0;
            for (int i = 0; i < residual.Length; i++)
            {
                double a = (ri[i] - residual[i]) / h, b = (ro[i] - residual[i]) / ho;
                aa += a * a;
                ab += a * b;
                bb += b * b;
                ar += a * residual[i];
                br += b * residual[i];
            }
            double det = aa * bb - ab * ab;
            if (!double.IsFinite(det) || det <= 1e-12 * aa * bb) return Failed(invalid);
            double innerStep = (ab * br - bb * ar) / det;
            double outerStep = (ab * ar - aa * br) / det;
            double largestStep = Math.Max(Math.Abs(innerStep), Math.Abs(outerStep));
            if (!double.IsFinite(largestStep)) return Failed(invalid);
            if (largestStep < 1e-8) break;

            bool improved = false;
            for (double fraction = 1; fraction >= 1.0 / 1024; fraction /= 2)
            {
                var next = parameters with
                {
                    InnerRadiusMm = parameters.InnerRadiusMm + fraction * innerStep,
                    OuterRadiusMm = parameters.OuterRadiusMm + fraction * outerStep,
                };
                if (next.InnerRadiusMm <= 0 || next.OuterRadiusMm <= next.InnerRadiusMm
                    || next.OuterRadiusMm - next.InnerRadiusMm >= next.TrackLengthMm
                    || (next.Kind == CalibrationDiscKind.BluRay && next.OuterRadiusMm > 60))
                    continue;
                double[] trial = Residuals(next);
                double newCost = trial.Sum(r => r * r);
                if (!double.IsFinite(newCost) || newCost >= cost) continue;
                (parameters, residual, cost) = (next, trial, newCost);
                improved = true;
                break;
            }
            if (!improved)
            {
                // Noisy measurements have a nonzero minimum. The last derivative
                // can request a few nanometres whose cost difference rounds away.
                if (largestStep < 1e-5) break;
                return Failed(invalid);
            }
        }
        double rms = Math.Sqrt(cost / residual.Length);
        if (target.HasWideAddressBands && (parameters.InnerRadiusMm < 7.5 || parameters.OuterRadiusMm > 60 + 1e-8))
            return Failed("宽带反推的半径超出盘片的可测范围，请核对照片尺度和交界。");
        var candidate = new CalibrationFitCandidate(parameters, 0, rms, cost, residual.Max(r => Math.Abs(r)));
        return new(true, parameters, 0, rms, [candidate], []);
    }

    private static CalibrationFitResult Failed(string warning) =>
        new(false, null, 0, double.PositiveInfinity, [], [warning]);
}
