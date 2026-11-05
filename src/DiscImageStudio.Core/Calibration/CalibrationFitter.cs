namespace DiscImageStudio.Core.Calibration;

public sealed record CalibrationObservation(
    string CellId, int CornerIndex, double X, double Y, double Weight = 1);

// FitDvdPitch is retained as the serialized/API name for existing calibration
// sessions. It enables cubic-pitch fitting for CD, DVD and Blu-ray alike.
public sealed record CalibrationFitOptions(
    CalibrationParameters? SearchCenter = null, double RadiusRangeMm = 0.02,
    double HuberDeltaMm = 0.8, int MaxCandidates = 8,
    bool FitDvdPitch = true, double PitchCoefficientRange = 0.05,
    bool AdaptiveRobustScale = false, bool? AutoRange = null)
{
    public void Validate(CalibrationTarget target)
    {
        if (!double.IsFinite(RadiusRangeMm) || RadiusRangeMm is <= 0 or > 5
            || !double.IsFinite(HuberDeltaMm) || HuberDeltaMm is <= 0 or > 10
            || MaxCandidates is < 1 or > 20
            || !double.IsFinite(PitchCoefficientRange) || PitchCoefficientRange is <= 0 or > 10)
            throw new ArgumentException("搜索半径范围应在 0–5 mm 内，控制点容差应在 0–10 mm 内，轨距系数搜索范围应在 0–10 内。");
        if (SearchCenter is not null) target.ValidateCompatible(SearchCenter);
    }
}

/// <summary>MaxErrorMm is the largest single-point curve distance and OutlierCount counts
/// points beyond three robust scales; both let the UI name why a fit is not applicable
/// instead of showing one averaged number.</summary>
public sealed record CalibrationFitCandidate(
    CalibrationParameters Parameters, double RotationRadians, double RmsErrorMm, double RobustCost = 0,
    double MaxErrorMm = 0, int OutlierCount = 0);

public sealed record CalibrationFitResult(
    bool Succeeded, CalibrationParameters? BestParameters, double RotationRadians,
    double RmsErrorMm, IReadOnlyList<CalibrationFitCandidate> Candidates,
    IReadOnlyList<string> Warnings)
{
    public bool IsReliable => Succeeded && Warnings.Count == 0;

    // Coordinate observations do not record the number of turns along visible edges.
    public bool RequiresShapeConfirmation => Succeeded;

    /// <summary>True when the best parameters sit on the searched radius box. The caller can
    /// re-run with a wider box instead of presenting a clamped answer as a measurement.</summary>
    public bool BoundaryLimited { get; init; }
}

/// <summary>Fits one physical track model to all observed features; features are never warped independently.</summary>
public static class CalibrationFitter
{
    private sealed record Datum(double Progress, double AngleOffset, double X, double Y, double Weight);
    private sealed record Solution(double Inner, double Outer, double Rotation, double Cost, double Rms);

    public static CalibrationFitResult Fit(CalibrationTarget target,
        IReadOnlyList<CalibrationObservation> observations, CalibrationFitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(observations);
        target.Validate();
        options ??= new();
        options.Validate(target);
        ValidateObservations(target, observations);
        cancellationToken.ThrowIfCancellationRequested();
        observations = observations.Where(observation =>
            target.GetGroupRole(observation.CellId) != CalibrationGroupRole.Validation).ToArray();
        if (observations.Count < 4)
            return Failed("至少确认 4 个用于拟合的角点，并选择分布在不同半径的控制块；验证组与未确认角点不参与拟合。");

        var originalGeometry = new CalibrationGeometry(target.Parameters);
        Datum[] data = observations.Select(observation =>
        {
            CalibrationPoint source = target.GetCorner(observation.CellId, observation.CornerIndex);
            double radius = Math.Sqrt(source.X * source.X + source.Y * source.Y);
            return new Datum(originalGeometry.ProgressAtRadius(radius),
                Math.Atan2(source.Y, source.X) - originalGeometry.AngleAtRadius(radius),
                observation.X, observation.Y, observation.Weight);
        }).ToArray();
        double progressSpan = data.Max(datum => datum.Progress) - data.Min(datum => datum.Progress);
        if (progressSpan < 0.12)
            return Failed("控制点集中在相近半径，无法稳定区分内外半径；请补选靠内圈或外圈的角点。");

        CalibrationParameters center = options.SearchCenter ?? target.Parameters;
        double minimumInner = Math.Max(0.1, center.InnerRadiusMm - options.RadiusRangeMm);
        double maximumInner = center.InnerRadiusMm + options.RadiusRangeMm;
        double minimumOuter = Math.Max(minimumInner + 0.1, center.OuterRadiusMm - options.RadiusRangeMm);
        double maximumOuter = center.OuterRadiusMm + options.RadiusRangeMm;
        double delta = options.HuberDeltaMm;
        var starts = new List<Solution>();

        Solution Evaluate(double inner, double outer, double? rotation = null)
        {
            if (inner < minimumInner || inner > maximumInner || outer < minimumOuter
                || outer > maximumOuter || outer <= inner || center.TrackLengthMm <= outer - inner)
                return new(inner, outer, 0, double.PositiveInfinity, double.PositiveInfinity);
            CalibrationGeometry geometry;
            try { geometry = new(center with { InnerRadiusMm = inner, OuterRadiusMm = outer }); }
            catch (ArgumentException)
            {
                return new(inner, outer, 0, double.PositiveInfinity, double.PositiveInfinity);
            }
            double cosine = 0, sine = 0;
            var predicted = new (double Radius, double Angle)[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                (double radius, double theta) = geometry.AtProgress(data[i].Progress);
                predicted[i] = (radius, theta + data[i].AngleOffset);
                double difference = Math.Atan2(data[i].Y, data[i].X) - predicted[i].Angle;
                cosine += data[i].Weight * Math.Cos(difference);
                sine += data[i].Weight * Math.Sin(difference);
            }
            double angle = rotation ?? Math.Atan2(sine, cosine);
            double cost = 0, squared = 0, weights = 0;
            for (int i = 0; i < data.Length; i++)
            {
                double dx = predicted[i].Radius * Math.Cos(predicted[i].Angle + angle) - data[i].X;
                double dy = predicted[i].Radius * Math.Sin(predicted[i].Angle + angle) - data[i].Y;
                double error = Math.Sqrt(dx * dx + dy * dy);
                cost += data[i].Weight * (error <= delta ? 0.5 * error * error : delta * (error - delta / 2));
                squared += data[i].Weight * error * error;
                weights += data[i].Weight;
            }
            return new(inner, outer, NormalizeAngle(angle), cost / weights, Math.Sqrt(squared / weights));
        }

        // Radial distances are extracted from dragged positions; users never enter radius readings.
        // They provide a valuable non-periodic initial estimate before angular fitting.
        (double radialInner, double radialOuter) = EstimateRadii(data, center);
        radialInner = Math.Clamp(radialInner, minimumInner, maximumInner);
        radialOuter = Math.Clamp(radialOuter, minimumOuter, maximumOuter);
        starts.Add(Evaluate(radialInner, radialOuter));
        starts.Add(Evaluate(center.InnerRadiusMm, center.OuterRadiusMm));

        // Choose a search spacing from phase sensitivity, with a bounded work budget.
        // A broad search that cannot sample every winding is explicitly marked uncertain.
        var baseGeometry = new CalibrationGeometry(center);
        const double finiteStep = 1e-5;
        var innerGeometry = new CalibrationGeometry(center with { InnerRadiusMm = center.InnerRadiusMm + finiteStep });
        var outerGeometry = new CalibrationGeometry(center with { OuterRadiusMm = center.OuterRadiusMm + finiteStep });
        double[] innerSlopes = data.Select(d =>
            (innerGeometry.AtProgress(d.Progress).Angle - baseGeometry.AtProgress(d.Progress).Angle) / finiteStep).ToArray();
        double[] outerSlopes = data.Select(d =>
            (outerGeometry.AtProgress(d.Progress).Angle - baseGeometry.AtProgress(d.Progress).Angle) / finiteStep).ToArray();
        int desiredInner = RequiredGridCount(innerSlopes, maximumInner - minimumInner);
        int desiredOuter = RequiredGridCount(outerSlopes, maximumOuter - minimumOuter);
        const int maxGridAxis = 201;
        int innerCount = Math.Clamp(desiredInner, 9, maxGridAxis);
        int outerCount = Math.Clamp(desiredOuter, 9, maxGridAxis);
        bool incompleteSearch = desiredInner > maxGridAxis || desiredOuter > maxGridAxis;
        var grid = new List<Solution>(innerCount * outerCount);
        for (int i = 0; i < innerCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double inner = minimumInner + (maximumInner - minimumInner) * i / (innerCount - 1);
            for (int j = 0; j < outerCount; j++)
            {
                double outer = minimumOuter + (maximumOuter - minimumOuter) * j / (outerCount - 1);
                grid.Add(Evaluate(inner, outer));
            }
        }
        double gridInnerStep = (maximumInner - minimumInner) / (innerCount - 1);
        double gridOuterStep = (maximumOuter - minimumOuter) / (outerCount - 1);
        foreach (Solution solution in grid.OrderBy(solution => solution.Cost))
        {
            if (!double.IsFinite(solution.Cost)) continue;
            if (starts.All(existing => Math.Abs(existing.Inner - solution.Inner) > gridInnerStep * 0.8
                || Math.Abs(existing.Outer - solution.Outer) > gridOuterStep * 0.8))
                starts.Add(solution);
            if (starts.Count >= 80) break;
        }

        var refined = new List<Solution>();
        foreach (Solution start in starts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Solution solution = Refine(start, data, center, delta, Evaluate, cancellationToken);
            if (!double.IsFinite(solution.Cost)) continue;
            int duplicate = refined.FindIndex(existing =>
                Math.Abs(existing.Inner - solution.Inner) < 0.00005
                && Math.Abs(existing.Outer - solution.Outer) < 0.00005);
            if (duplicate < 0) refined.Add(solution);
            else if (solution.Cost < refined[duplicate].Cost) refined[duplicate] = solution;
        }
        refined.Sort((left, right) => left.Cost.CompareTo(right.Cost));
        if (refined.Count == 0) return Failed("搜索范围内没有有效模型，请检查控制点对应关系或扩大搜索范围。");
        Solution best = refined[0];
        var warnings = new List<string>();
        if (progressSpan < 0.4)
            warnings.Add("控制点覆盖的径向范围偏窄，建议补选更靠内、外的角点复核。");
        if (best.Rms > delta * 2)
            warnings.Add("控制点与预测图形偏差较大，请检查分组与弧片身份、控制点对应关系和显示尺度。");
        if (Math.Min(best.Inner - minimumInner, maximumInner - best.Inner) < options.RadiusRangeMm * 0.005
            || Math.Min(best.Outer - minimumOuter, maximumOuter - best.Outer) < options.RadiusRangeMm * 0.005)
            warnings.Add("最佳结果靠近搜索边界，建议扩大搜索范围并补充控制点复核。");
        double nearCost = best.Cost + Math.Max(0.005, best.Cost * 0.25);
        bool ambiguous = refined.Skip(1).Any(candidate => candidate.Cost <= nearCost
            && (Math.Abs(candidate.Inner - best.Inner) > 0.0002 || Math.Abs(candidate.Outer - best.Outer) > 0.0002));
        if (ambiguous)
            warnings.Add("存在拟合程度接近的绕圈候选，暂不能唯一确定参数；请在预测差异明显的另一半径补选角点。");
        if (incompleteSearch)
            warnings.Add("搜索范围包含过多绕圈组合，已进行有限多起点搜索；请缩小范围或增加分散的控制点后复核。");
        CalibrationFitCandidate[] candidates = refined.Take(options.MaxCandidates)
            .Select(solution => new CalibrationFitCandidate(
                center with { InnerRadiusMm = solution.Inner, OuterRadiusMm = solution.Outer },
                solution.Rotation, solution.Rms, solution.Cost)).ToArray();
        return new(true, candidates[0].Parameters, best.Rotation, best.Rms, candidates, warnings);
    }

    /// <summary>Independent held-out error; these observations never train Fit.</summary>
    public static double? ValidationRms(CalibrationTarget target, IReadOnlyList<CalibrationObservation> observations,
        CalibrationParameters parameters, double rotationRadians = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(observations);
        ValidateObservations(target, observations);
        Func<CalibrationPoint, CalibrationPoint> map = target.CreateMapping(parameters, rotationRadians);
        double squared = 0, weight = 0;
        foreach (CalibrationObservation observation in observations)
        {
            if (target.GetGroupRole(observation.CellId) != CalibrationGroupRole.Validation) continue;
            CalibrationPoint predicted = map(target.GetCorner(observation.CellId, observation.CornerIndex));
            double dx = predicted.X - observation.X, dy = predicted.Y - observation.Y;
            squared += observation.Weight * (dx * dx + dy * dy);
            weight += observation.Weight;
        }
        return weight > 0 ? Math.Sqrt(squared / weight) : null;
    }

    public static void ValidateObservations(CalibrationTarget target, IReadOnlyList<CalibrationObservation> observations)
    {
        if (observations.Count > 2000) throw new ArgumentException("控制点数量超出限制。");
        var identifiers = new HashSet<(string, int)>();
        var physicalPoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (CalibrationObservation observation in observations)
        {
            if (observation is null || !double.IsFinite(observation.X) || !double.IsFinite(observation.Y)
                || !double.IsFinite(observation.Weight) || observation.Weight is <= 0 or > 1000
                || Math.Abs(observation.X) > 1000 || Math.Abs(observation.Y) > 1000
                || observation.X * observation.X + observation.Y * observation.Y < 0.01)
                throw new ArgumentException("控制点坐标或权重无效。");
            CalibrationControlPoint source = target.GetControlPoint(observation.CellId, observation.CornerIndex);
            if (!identifiers.Add((observation.CellId, observation.CornerIndex))
                || !physicalPoints.Add(source.Id))
                throw new ArgumentException("同一角点或共享角点不能重复录入。");
        }
    }

    private static int RequiredGridCount(double[] slopes, double width)
    {
        double spread = slopes.Max() - slopes.Min();
        double desired = Math.Ceiling(width * spread / 0.7) + 1;
        return desired >= int.MaxValue ? int.MaxValue : (int)desired;
    }

    private static (double, double) EstimateRadii(Datum[] data, CalibrationParameters fallback)
    {
        double s0 = 0, s1 = 0, s2 = 0, t0 = 0, t1 = 0;
        foreach (Datum d in data)
        {
            double squared = d.X * d.X + d.Y * d.Y;
            s0 += d.Weight;
            s1 += d.Weight * d.Progress;
            s2 += d.Weight * d.Progress * d.Progress;
            t0 += d.Weight * squared;
            t1 += d.Weight * d.Progress * squared;
        }
        double determinant = s0 * s2 - s1 * s1;
        if (Math.Abs(determinant) < 1e-12) return (fallback.InnerRadiusMm, fallback.OuterRadiusMm);
        double innerSquared = (t0 * s2 - t1 * s1) / determinant;
        double range = (t1 * s0 - t0 * s1) / determinant;
        return innerSquared > 0 && range > 0
            ? (Math.Sqrt(innerSquared), Math.Sqrt(innerSquared + range))
            : (fallback.InnerRadiusMm, fallback.OuterRadiusMm);
    }

    private static Solution Refine(Solution initial, Datum[] data, CalibrationParameters parameters,
        double delta, Func<double, double, double?, Solution> evaluate, CancellationToken token)
    {
        Solution current = initial;
        if (!double.IsFinite(current.Cost)) return current;
        double damping = 1e-3;
        for (int iteration = 0; iteration < 70; iteration++)
        {
            token.ThrowIfCancellationRequested();
            double[] values = [current.Inner, current.Outer, current.Rotation];
            double[] residual = Residuals(values, data, parameters);
            double[,] jacobian = new double[residual.Length, 3];
            for (int column = 0; column < 3; column++)
            {
                double step = column < 2 ? 1e-6 : 1e-6;
                double[] shifted = (double[])values.Clone();
                shifted[column] += step;
                double[] difference = Residuals(shifted, data, parameters);
                for (int row = 0; row < residual.Length; row++)
                    jacobian[row, column] = (difference[row] - residual[row]) / step;
            }
            double[,] normal = new double[3, 3];
            double[] rhs = new double[3];
            for (int point = 0; point < data.Length; point++)
            {
                double error = Math.Sqrt(residual[point * 2] * residual[point * 2]
                    + residual[point * 2 + 1] * residual[point * 2 + 1]);
                double weight = data[point].Weight * (error <= delta ? 1 : delta / error);
                for (int offset = 0; offset < 2; offset++)
                {
                    int row = point * 2 + offset;
                    for (int j = 0; j < 3; j++)
                    {
                        rhs[j] -= weight * jacobian[row, j] * residual[row];
                        for (int k = 0; k < 3; k++)
                            normal[j, k] += weight * jacobian[row, j] * jacobian[row, k];
                    }
                }
            }
            for (int j = 0; j < 3; j++) normal[j, j] *= 1 + damping;
            if (!Solve3(normal, rhs, out double[] update)) break;
            Solution proposed = evaluate(current.Inner + update[0], current.Outer + update[1], current.Rotation + update[2]);
            if (proposed.Cost < current.Cost)
            {
                double improvement = current.Cost - proposed.Cost;
                current = proposed;
                damping = Math.Max(1e-10, damping / 3);
                if (improvement < 1e-14 || update.Max(Math.Abs) < 1e-10) break;
            }
            else
            {
                damping *= 8;
                if (damping > 1e10) break;
            }
        }
        return current;
    }

    private static double[] Residuals(double[] values, Datum[] data, CalibrationParameters parameters)
    {
        var geometry = new CalibrationGeometry(parameters with { InnerRadiusMm = values[0], OuterRadiusMm = values[1] });
        var residuals = new double[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            (double radius, double theta) = geometry.AtProgress(data[i].Progress);
            double angle = theta + data[i].AngleOffset + values[2];
            residuals[i * 2] = radius * Math.Cos(angle) - data[i].X;
            residuals[i * 2 + 1] = radius * Math.Sin(angle) - data[i].Y;
        }
        return residuals;
    }

    private static bool Solve3(double[,] matrix, double[] rhs, out double[] solution)
    {
        solution = new double[3];
        for (int pivot = 0; pivot < 3; pivot++)
        {
            int selected = pivot;
            for (int row = pivot + 1; row < 3; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[selected, pivot])) selected = row;
            if (Math.Abs(matrix[selected, pivot]) < 1e-15) return false;
            for (int column = pivot; column < 3; column++)
                (matrix[pivot, column], matrix[selected, column]) = (matrix[selected, column], matrix[pivot, column]);
            (rhs[pivot], rhs[selected]) = (rhs[selected], rhs[pivot]);
            for (int row = pivot + 1; row < 3; row++)
            {
                double ratio = matrix[row, pivot] / matrix[pivot, pivot];
                for (int column = pivot; column < 3; column++) matrix[row, column] -= ratio * matrix[pivot, column];
                rhs[row] -= ratio * rhs[pivot];
            }
        }
        for (int row = 2; row >= 0; row--)
        {
            double value = rhs[row];
            for (int column = row + 1; column < 3; column++) value -= matrix[row, column] * solution[column];
            solution[row] = value / matrix[row, row];
        }
        return solution.All(double.IsFinite);
    }

    private static double NormalizeAngle(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));

    private static CalibrationFitResult Failed(string message) => new(false, null, 0, 0, [], [message]);
}
