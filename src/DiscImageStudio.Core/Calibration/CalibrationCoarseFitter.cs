namespace DiscImageStudio.Core.Calibration;

/// <summary>One half-black coarse-calibration candidate: the fitted actual radii plus the
/// free overall rotation. The constant-pitch approximation is only used to bring the
/// accumulated turns back into the fine-fit search range.</summary>
public sealed record CalibrationCoarseCandidate(
    CalibrationParameters Parameters, double RotationRadians,
    double RmsErrorMm, double MaxErrorMm, int OutlierCount);

public sealed record CalibrationCoarseFitResult(
    bool Succeeded, IReadOnlyList<string> Warnings, int BestIndex,
    IReadOnlyList<CalibrationCoarseCandidate> Candidates);

/// <summary>
/// Issue #49 coarse calibration: a disc engraved with a left-black / right-white pattern has
/// a visible boundary through the centre. The constant-pitch model relates each observed
/// boundary point's radius and angle to the generated geometry, so ri/ro can be recovered
/// without knowing how many turns the real spiral accumulated.
/// </summary>
public static class CalibrationCoarseFitter
{
    public const int MinimumPointCount = 8;
    public const double MinimumCoverage = 0.3;
    public const double RadiusSearchRangeMm = 1.5;
    public const int MaximumCandidateCount = 5;

    private const double HuberDeltaMm = 0.8;
    private const double GridStepMm = 0.05;
    private const double AmbiguityAngle = Math.PI * 0.995;

    public static CalibrationCoarseFitResult Fit(
        CalibrationParameters generated, IReadOnlyList<CalibrationPoint> points,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generated);
        generated.Validate();
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < MinimumPointCount)
        {
            return Failed($"粗调分界至少需要 {MinimumPointCount} 个描点，请沿黑白分界从内到外均匀补点。");
        }

        var warnings = new List<string>();
        var observations = new List<(double Radius, double Angle)>();
        foreach (CalibrationPoint? point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                || Math.Abs(point.X) > 1000 || Math.Abs(point.Y) > 1000)
            {
                return Failed("粗调描点坐标无效，请使用矫正后以盘心为原点的毫米坐标。");
            }

            double radius = Math.Sqrt(point.X * point.X + point.Y * point.Y);
            if (radius <= 1e-6)
            {
                return Failed("粗调描点不能落在盘心上，请沿黑白分界布点。");
            }

            observations.Add((radius, Math.Atan2(point.Y, point.X)));
        }

        observations.Sort((first, second) => first.Radius.CompareTo(second.Radius));
        double minimumRadius = observations[0].Radius;
        double maximumRadius = observations[^1].Radius;
        double band = generated.OuterRadiusMm - generated.InnerRadiusMm;
        double coverage = band <= 0 ? 0 : (maximumRadius - minimumRadius) / band;
        if (!double.IsFinite(coverage) || coverage < MinimumCoverage)
        {
            return Failed(
                $"描点径向覆盖只有 {coverage:P0}，粗调至少需要覆盖生成环带宽度的 {MinimumCoverage:P0}；"
                + "请补充更靠内、更靠外的分界点。");
        }

        double[] angles = UnwrapAngles(observations, out bool ambiguous);
        if (ambiguous)
        {
            warnings.Add("相邻描点的转角接近半圈，绕数/方向无法唯一确定；请在这两点之间沿分界补点后复核。");
        }

        if (observations.Count > 1024)
        {
            warnings.Add("描点较多，拟合使用按半径均匀抽取的子集；顺序仍用于角度展开。");
            observations = Sample(observations);
            angles = ResampleAngles(observations, angles);
        }

        double lowerInner = Math.Max(0.5, generated.InnerRadiusMm - RadiusSearchRangeMm);
        double upperInner = generated.InnerRadiusMm + RadiusSearchRangeMm;
        double lowerOuter = Math.Max(lowerInner + 0.5, generated.OuterRadiusMm - RadiusSearchRangeMm);
        double upperOuter = generated.OuterRadiusMm + RadiusSearchRangeMm;
        if (generated.Kind == CalibrationDiscKind.BluRay)
        {
            upperOuter = Math.Min(60, upperOuter);
        }

        if (!(upperInner > lowerInner) || !(upperOuter > lowerOuter))
        {
            return Failed("生成几何的半径搜索范围无效，请检查 r0/r1。");
        }

        double pg = Math.PI * ((generated.OuterRadiusMm * generated.OuterRadiusMm)
            - (generated.InnerRadiusMm * generated.InnerRadiusMm)) / generated.TrackLengthMm;
        double[] radii = observations.Select(value => value.Radius).ToArray();
        double[] normalized = angles;

        double Evaluate(double inner, double outer, out double rotation, out double[] residuals)
        {
            rotation = 0;
            residuals = [];
            if (!double.IsFinite(inner) || !double.IsFinite(outer)
                || inner < lowerInner || inner > upperInner
                || outer < lowerOuter || outer > upperOuter || outer <= inner)
            {
                return double.PositiveInfinity;
            }

            double outerSquared = outer * outer, innerSquared = inner * inner;
            if (outerSquared - innerSquared <= 1e-12)
            {
                return double.PositiveInfinity;
            }

            double pa = Math.PI * (outerSquared - innerSquared) / generated.TrackLengthMm;
            if (!double.IsFinite(pa) || pa <= 0 || !double.IsFinite(pg) || pg <= 0)
            {
                return double.PositiveInfinity;
            }

            residuals = new double[radii.Length];
            double cosine = 0, sine = 0;
            for (int index = 0; index < radii.Length; index++)
            {
                double radius = radii[index];
                double sourceRadius = Math.Sqrt(generated.InnerRadiusMm * generated.InnerRadiusMm
                    + ((radius * radius) - innerSquared)
                    * ((generated.OuterRadiusMm * generated.OuterRadiusMm)
                        - (generated.InnerRadiusMm * generated.InnerRadiusMm)) / (outerSquared - innerSquared));
                if (!double.IsFinite(sourceRadius) || sourceRadius <= 0)
                {
                    return double.PositiveInfinity;
                }

                double delta = 2 * Math.PI * (((sourceRadius - generated.InnerRadiusMm) / pg)
                    - ((radius - inner) / pa));
                residuals[index] = normalized[index] + delta;
                cosine += Math.Cos(residuals[index]);
                sine += Math.Sin(residuals[index]);
            }

            rotation = Math.Atan2(sine, cosine);
            double cost = 0;
            for (int index = 0; index < radii.Length; index++)
            {
                double error = Math.Abs(Normalize(residuals[index] - rotation)) * radii[index];
                cost += error <= HuberDeltaMm
                    ? 0.5 * error * error
                    : HuberDeltaMm * (error - (HuberDeltaMm / 2));
            }

            return double.IsFinite(cost) ? cost / radii.Length : double.PositiveInfinity;
        }

        var evaluated = new List<(double Inner, double Outer, double Cost)>();
        int gridCount = (int)Math.Round((upperInner - lowerInner) / GridStepMm) + 1;
        int outerCount = (int)Math.Round((upperOuter - lowerOuter) / GridStepMm) + 1;
        for (int row = 0; row < gridCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double inner = lowerInner + ((upperInner - lowerInner) * row / Math.Max(1, gridCount - 1));
            for (int column = 0; column < outerCount; column++)
            {
                double outer = lowerOuter + ((upperOuter - lowerOuter) * column / Math.Max(1, outerCount - 1));
                double cost = Evaluate(inner, outer, out _, out _);
                if (double.IsFinite(cost))
                {
                    evaluated.Add((inner, outer, cost));
                }
            }
        }

        if (evaluated.Count == 0)
        {
            return Failed("搜索范围内没有有效的粗调候选，请检查盘片参数与描点。");
        }

        evaluated.Sort((first, second) => first.Cost.CompareTo(second.Cost));
        var seeds = new List<(double Inner, double Outer)>();
        foreach ((double inner, double outer, _) in evaluated)
        {
            if (seeds.Count >= 12)
            {
                break;
            }

            if (seeds.All(seed => Distance(seed.Inner, seed.Outer, inner, outer) > 0.15))
            {
                seeds.Add((inner, outer));
            }
        }

        var refined = new List<(double Inner, double Outer, double Cost)>();
        foreach ((double inner, double outer) in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (double solvedInner, double solvedOuter, double cost) = Refine(
                inner, outer, lowerInner, upperInner, lowerOuter, upperOuter,
                (candidateInner, candidateOuter) => Evaluate(candidateInner, candidateOuter, out _, out _),
                cancellationToken);
            if (double.IsFinite(cost))
            {
                refined.Add((solvedInner, solvedOuter, cost));
            }
        }

        refined.Sort((first, second) => first.Cost.CompareTo(second.Cost));
        var candidates = new List<CalibrationCoarseCandidate>();
        foreach ((double inner, double outer, _) in refined)
        {
            if (candidates.Count >= MaximumCandidateCount)
            {
                break;
            }

            if (candidates.Any(candidate => Distance(
                candidate.Parameters.InnerRadiusMm, candidate.Parameters.OuterRadiusMm, inner, outer) < 0.01))
            {
                continue;
            }

            double fitness = Evaluate(inner, outer, out double rotation, out double[] residuals);
            if (!double.IsFinite(fitness))
            {
                continue;
            }

            (double rms, double maximum, int outliers) = ResidualStats(residuals, radii, rotation);
            candidates.Add(new CalibrationCoarseCandidate(
                generated with { InnerRadiusMm = inner, OuterRadiusMm = outer },
                Normalize(rotation), rms, maximum, outliers));
        }

        if (candidates.Count == 0)
        {
            return Failed("粗调拟合没有得到有效候选，请检查描点顺序与照片矫正。");
        }

        CalibrationCoarseCandidate best = candidates[0];
        bool boundaryLimited = Math.Min(
            Math.Min(best.Parameters.InnerRadiusMm - lowerInner, upperInner - best.Parameters.InnerRadiusMm),
            Math.Min(best.Parameters.OuterRadiusMm - lowerOuter, upperOuter - best.Parameters.OuterRadiusMm)) < 1e-3;
        if (boundaryLimited)
        {
            warnings.Add("最佳结果靠近 ±1.5 mm 搜索边界，粗调结论可能不可靠；请补点或换一张盘面复核。");
        }

        if (best.RmsErrorMm > HuberDeltaMm)
        {
            warnings.Add(
                $"粗调拟合残差较大（平均 {best.RmsErrorMm:0.###} mm）；请检查照片矫正与描点是否沿同一条分界。");
        }

        if (candidates.Count > 1)
        {
            CalibrationCoarseCandidate second = candidates[1];
            if (Math.Abs(second.RmsErrorMm - best.RmsErrorMm) <= Math.Max(0.02, best.RmsErrorMm * 0.25)
                && Distance(best.Parameters.InnerRadiusMm, best.Parameters.OuterRadiusMm,
                    second.Parameters.InnerRadiusMm, second.Parameters.OuterRadiusMm) > 0.1)
            {
                warnings.Add("存在残差接近的多个候选，请对照分界叠加图后再决定是否应用。");
            }
        }

        int bestIndex = ambiguous ? -1 : 0;
        return new CalibrationCoarseFitResult(true, warnings, bestIndex, candidates);
    }

    /// <summary>Boundary polyline of the fitted model across the observed radial interval.</summary>
    public static IReadOnlyList<CalibrationPoint> PredictBoundary(
        CalibrationParameters generated, CalibrationCoarseCandidate candidate,
        double minimumRadiusMm, double maximumRadiusMm, int pointCount = 400)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!double.IsFinite(minimumRadiusMm) || !double.IsFinite(maximumRadiusMm)
            || minimumRadiusMm <= 0 || maximumRadiusMm <= minimumRadiusMm)
        {
            throw new ArgumentException("粗调预测需要有限、递增且大于零的观测半径区间。");
        }

        if (pointCount is < 2 or > 4000)
        {
            throw new ArgumentOutOfRangeException(nameof(pointCount));
        }

        CalibrationParameters actual = candidate.Parameters;
        double pg = Math.PI * ((generated.OuterRadiusMm * generated.OuterRadiusMm)
            - (generated.InnerRadiusMm * generated.InnerRadiusMm)) / generated.TrackLengthMm;
        double pa = Math.PI * ((actual.OuterRadiusMm * actual.OuterRadiusMm)
            - (actual.InnerRadiusMm * actual.InnerRadiusMm)) / generated.TrackLengthMm;
        double innerSquared = actual.InnerRadiusMm * actual.InnerRadiusMm;
        double outerSquared = actual.OuterRadiusMm * actual.OuterRadiusMm;
        double generatedSquaredRange = (generated.OuterRadiusMm * generated.OuterRadiusMm)
            - (generated.InnerRadiusMm * generated.InnerRadiusMm);
        var points = new List<CalibrationPoint>(pointCount);
        for (int index = 0; index < pointCount; index++)
        {
            double radius = minimumRadiusMm + ((maximumRadiusMm - minimumRadiusMm) * index / (pointCount - 1));
            double sourceRadius = Math.Sqrt(generated.InnerRadiusMm * generated.InnerRadiusMm
                + ((radius * radius) - innerSquared) * generatedSquaredRange / (outerSquared - innerSquared));
            if (!double.IsFinite(sourceRadius) || !double.IsFinite(pg) || pg <= 0 || !double.IsFinite(pa) || pa <= 0)
            {
                throw new ArgumentException("该粗调候选在观测半径区间内没有有效的正半径延拓。");
            }

            double delta = 2 * Math.PI * (((sourceRadius - generated.InnerRadiusMm) / pg)
                - ((radius - actual.InnerRadiusMm) / pa));
            double angle = candidate.RotationRadians - delta;
            points.Add(new(radius * Math.Cos(angle), radius * Math.Sin(angle)));
        }

        return points;
    }

    private static (double Inner, double Outer, double Cost) Refine(
        double inner, double outer, double lowerInner, double upperInner,
        double lowerOuter, double upperOuter,
        Func<double, double, double> evaluateCost,
        CancellationToken cancellationToken)
    {
        double bestInner = inner, bestOuter = outer;
        double bestCost = evaluateCost(inner, outer);
        double step = GridStepMm;
        for (int iteration = 0; iteration < 200 && step >= 1e-5; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool improved = false;
            for (int innerIndex = -1; innerIndex <= 1; innerIndex++)
            {
                for (int outerIndex = -1; outerIndex <= 1; outerIndex++)
                {
                    if (innerIndex == 0 && outerIndex == 0)
                    {
                        continue;
                    }

                    double candidateInner = Math.Clamp(
                        bestInner + (innerIndex * step), lowerInner, upperInner);
                    double candidateOuter = Math.Clamp(
                        bestOuter + (outerIndex * step), lowerOuter, upperOuter);
                    double cost = evaluateCost(candidateInner, candidateOuter);
                    if (cost < bestCost - 1e-15)
                    {
                        bestCost = cost;
                        bestInner = candidateInner;
                        bestOuter = candidateOuter;
                        improved = true;
                    }
                }
            }

            if (!improved)
            {
                step /= 2;
            }
        }

        return (bestInner, bestOuter, bestCost);
    }

    private static (double Rms, double Maximum, int Outliers) ResidualStats(
        double[] deltas, double[] radii, double rotation)
    {
        double squared = 0, maximum = 0;
        int outliers = 0;
        for (int index = 0; index < deltas.Length; index++)
        {
            double error = Math.Abs(Normalize(deltas[index] - rotation)) * radii[index];
            squared += error * error;
            maximum = Math.Max(maximum, error);
            if (error > HuberDeltaMm * 3)
            {
                outliers++;
            }
        }

        return (Math.Sqrt(squared / deltas.Length), maximum, outliers);
    }

    private static double[] UnwrapAngles(
        IReadOnlyList<(double Radius, double Angle)> observations, out bool ambiguous)
    {
        ambiguous = false;
        double[] result = new double[observations.Count];
        result[0] = observations[0].Angle;
        for (int index = 1; index < observations.Count; index++)
        {
            double change = Normalize(observations[index].Angle - observations[index - 1].Angle);
            if (Math.Abs(change) >= AmbiguityAngle)
            {
                ambiguous = true;
            }

            result[index] = result[index - 1] + change;
        }

        return result;
    }

    private static List<(double Radius, double Angle)> Sample(
        IReadOnlyList<(double Radius, double Angle)> observations)
    {
        var sampled = new List<(double Radius, double Angle)>(1024);
        for (int slot = 0; slot < 1024; slot++)
        {
            int index = (int)Math.Round(slot * (observations.Count - 1.0) / 1023);
            sampled.Add(observations[index]);
        }

        return sampled;
    }

    private static double[] ResampleAngles(
        IReadOnlyList<(double Radius, double Angle)> observations, double[] unwrapped)
    {
        // Re-unwrap on the sampled subsequence so the angle jump rule matches the retained data.
        double[] result = new double[observations.Count];
        result[0] = unwrapped[0];
        for (int index = 1; index < observations.Count; index++)
        {
            result[index] = result[index - 1]
                + Normalize(observations[index].Angle - observations[index - 1].Angle);
        }

        return result;
    }

    private static double Distance(double firstInner, double firstOuter, double secondInner, double secondOuter)
        => Math.Sqrt(((firstInner - secondInner) * (firstInner - secondInner))
            + ((firstOuter - secondOuter) * (firstOuter - secondOuter)));

    private static double Normalize(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));

    private static CalibrationCoarseFitResult Failed(string message)
        => new(false, [message], -1, []);
}
