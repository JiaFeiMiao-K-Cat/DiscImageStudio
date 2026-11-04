namespace DiscImageStudio.Core.Calibration;

/// <summary>
/// Points on one observed arm, ordered from the inside out, after photograph rectification.
/// Coordinates are millimetres relative to the physical disc centre; source radii are unknown.
/// A single arbitrary arm may use ArmIndex 0 because overall rotation is fitted freely.
/// </summary>
public sealed record CalibrationCrossTrace(int ArmIndex, IReadOnlyList<CalibrationPoint> Points);

/// <summary>
/// Fits the continuous phase along radial arms. Only one integer turn offset is allowed per
/// complete trace; individual samples can never jump to another arm or another turn.
/// RmsErrorMm is the locally normal curve residual, rather than an endpoint correspondence error.
/// </summary>
public static partial class CalibrationCrossFitter
{
    private const string NearHalfTurnWarning =
        "有相邻曲线点的转角接近半圈，绕圈判断可能不可靠；请补点后复核。";

    private sealed record Datum(int Trace, double Radius, double Angle, double AngularScale, double Weight);
    private sealed record Solution(double Inner, double Outer, double Rotation, double Cost, double Rms, double[] Residuals);
    private sealed record SolveOutcome(CalibrationFitResult Result, double[] Residuals);

    /// <summary>Checks persisted input, including empty or incomplete editing drafts.</summary>
    public static void ValidateTraces(CalibrationTarget target, IReadOnlyList<CalibrationCrossTrace> traces)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(traces);
        if (target.HasBoundaryPattern && traces.Count > 0)
            throw new ArgumentException("边界中标请使用边界描点，不能把不相连的交界拼成十字线的连续曲线。");
        if (traces.Count > 4) throw new ArgumentException("十字线最多包含四条臂。");
        var arms = new HashSet<int>();
        int count = 0;
        foreach (CalibrationCrossTrace trace in traces)
        {
            if (trace is null || trace.ArmIndex is < 0 or > 3 || (target.HasSingleRadialLine && trace.ArmIndex != 0)
                || !arms.Add(trace.ArmIndex) || trace.Points is null)
                throw new ArgumentException("曲线编号无效或重复。");
            if (!target.HasCrossArms && trace.Points.Count > 0)
                throw new ArgumentException("此标靶没有十字臂，请使用精标盘照片描线。");
            count += trace.Points.Count;
            if (count > 20_000) throw new ArgumentException("曲线点数量超出限制。");
            foreach (CalibrationPoint point in trace.Points)
                if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                    || Math.Abs(point.X) > 1000 || Math.Abs(point.Y) > 1000
                    || point.X * point.X + point.Y * point.Y < 0.01)
                    throw new ArgumentException("曲线点坐标无效，请使用矫正后以盘心为原点的坐标。");
        }
    }

    public static CalibrationFitResult Fit(CalibrationTarget target, IReadOnlyList<CalibrationCrossTrace> traces,
        CalibrationFitOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        options ??= new();
        options.Validate(target);
        ValidateTraces(target, traces);
        cancellationToken.ThrowIfCancellationRequested();
        double band = target.Parameters.OuterRadiusMm - target.Parameters.InnerRadiusMm;
        var inputWarnings = new List<string>();
        Datum[] data = Prepare(traces, band, inputWarnings, out string? inputFailure);
        if (inputFailure is not null) return Failed(inputFailure);
        if (data.Length < 6) return Failed("请沿同一条曲线从内到外至少标出 6 个点，覆盖靠内、中间和靠外的位置。");
        double coverage = (data.Max(point => point.Radius) - data.Min(point => point.Radius)) / band;
        if (coverage < 0.18) return Failed("曲线覆盖范围太窄，无法稳定分离内外半径；请继续标出更靠内、外的曲线。");

        // The first pass uses the caller's tolerance. If the trace is much cleaner than
        // that tolerance, a second pass tightens it to the observed scale. This stops a
        // few misplaced clicks from bending the curve for every other point, while the
        // reported RMS and MaxError still expose what was down-weighted.
        SolveOutcome first = Solve(target, options, data, coverage, inputWarnings, options.HuberDeltaMm, cancellationToken);
        if (!options.AdaptiveRobustScale || !first.Result.Succeeded || first.Residuals.Length == 0)
            return first.Result;
        double median = MedianAbsolute(first.Residuals);
        // Re-fit only when the plain RMS is visibly inflated by a minority of bad clicks.
        // Clean data (RMS near the median) keeps the first, cheaper result unchanged.
        if (first.Result.RmsErrorMm <= median * 2.5) return first.Result;
        double adaptiveDelta = Math.Clamp(3 * 1.4826 * median, 0.01, options.HuberDeltaMm);
        if (adaptiveDelta >= options.HuberDeltaMm * 0.75) return first.Result;
        SolveOutcome tightened = Solve(target, options, data, coverage, inputWarnings, adaptiveDelta, cancellationToken);
        return tightened.Result.Succeeded ? tightened.Result : first.Result;
    }

    /// <summary>One bounded search at a fixed robust scale.</summary>
    private static SolveOutcome Solve(CalibrationTarget target, CalibrationFitOptions options, Datum[] data,
        double coverage, IReadOnlyList<string> inputWarnings, double delta, CancellationToken cancellationToken)
    {
        var warnings = new List<string>(inputWarnings);
        CalibrationParameters center = options.SearchCenter ?? target.Parameters;
        if (target.HasSingleRadialLine)
            center = center with
            {
                PitchLinear = target.Parameters.PitchLinear,
                PitchQuadratic = target.Parameters.PitchQuadratic,
                PitchCubic = target.Parameters.PitchCubic,
            };
        var original = new CalibrationGeometry(target.Parameters);
        double lowerInner = Math.Max(0.1, center.InnerRadiusMm - options.RadiusRangeMm);
        double upperInner = center.InnerRadiusMm + options.RadiusRangeMm;
        double lowerOuter = Math.Max(lowerInner + 0.1, center.OuterRadiusMm - options.RadiusRangeMm);
        double upperOuter = center.OuterRadiusMm + options.RadiusRangeMm;
        int traceCount = data.Max(point => point.Trace) + 1;

        Solution Evaluate(double inner, double outer)
        {
            if (inner < lowerInner || inner > upperInner || outer < lowerOuter || outer > upperOuter
                || outer <= inner || center.TrackLengthMm <= outer - inner)
                return Invalid(inner, outer);
            CalibrationGeometry geometry;
            try { geometry = new(center with { InnerRadiusMm = inner, OuterRadiusMm = outer }); }
            catch (ArgumentException) { return Invalid(inner, outer); }
            var model = new CalibrationCrossModel(original, geometry, target.Parameters.Kind,
                target.Parameters == (center with { InnerRadiusMm = inner, OuterRadiusMm = outer }));
            var angles = new double[data.Length];
            var means = new double[traceCount];
            var meanWeights = new double[traceCount];
            for (int i = 0; i < data.Length; i++)
            {
                Datum point = data[i];
                if (!model.TryAtRadius(point.Radius, out CalibrationCrossSample predicted)) return Invalid(inner, outer);
                // Generation bounds describe what was written, not exact measured
                // photo coordinates. As for CD/DVD, retain every observed point on
                // the positive-radius continuation. A hard support constraint here
                // can force small photo endpoint errors into hundreds of false turns.
                angles[i] = point.Angle - predicted.Phase;
                double weight = point.Weight * point.AngularScale * point.AngularScale;
                means[point.Trace] += weight * angles[i];
                meanWeights[point.Trace] += weight;
            }
            double cosine = 0, sine = 0;
            for (int trace = 0; trace < traceCount; trace++)
            {
                means[trace] /= meanWeights[trace];
                cosine += Math.Cos(means[trace]);
                sine += Math.Sin(means[trace]);
            }
            double rotation = Math.Atan2(sine, cosine);
            // Resolve the arbitrary initial atan2 branch once for the entire arm.
            // Per-point wrapping would erase the very winding information being fitted.
            for (int i = 0; i < angles.Length; i++)
                angles[i] += 2 * Math.PI * Math.Round((rotation - means[data[i].Trace]) / (2 * Math.PI));
            for (int iteration = 0; iteration < 10; iteration++)
            {
                double sum = 0, weightSum = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    Datum point = data[i];
                    double error = Math.Abs((angles[i] - rotation) * point.AngularScale);
                    double weight = point.Weight * point.AngularScale * point.AngularScale
                        * (error <= delta ? 1 : delta / error);
                    sum += weight * angles[i];
                    weightSum += weight;
                }
                double updated = sum / weightSum;
                if (Math.Abs(updated - rotation) < 1e-12) break;
                rotation = updated;
            }
            var residuals = new double[data.Length];
            double cost = 0, squared = 0, weights = 0;
            for (int i = 0; i < data.Length; i++)
            {
                double residual = (angles[i] - rotation) * data[i].AngularScale;
                residuals[i] = residual;
                double error = Math.Abs(residual);
                cost += data[i].Weight * (error <= delta ? 0.5 * error * error : delta * (error - delta / 2));
                squared += data[i].Weight * residual * residual;
                weights += data[i].Weight;
            }
            return new(inner, outer, rotation, cost / weights, Math.Sqrt(squared / weights), residuals);
        }

        // Extract the nonconstant phase shape before the bounded grid search. This
        // approximation supplies initial radii only; Evaluate/Refine always use the
        // exact physical model, including its full observed-radius continuation.
        var shapeSeeds = EstimateShapeSeeds(data, traceCount, target.Parameters, center)
            .Select(seed => Evaluate(Math.Clamp(seed.Inner, lowerInner, upperInner),
                Math.Clamp(seed.Outer, lowerOuter, upperOuter)))
            .Where(seed => double.IsFinite(seed.Cost)).ToArray();

        // An unwrapped trace has a smooth shape objective: do not enumerate wrapped
        // point correspondences. Multiple bounded starts still guard against poor initials.
        int gridSize = options.RadiusRangeMm <= 0.05 ? 9 : 17;
        var seeds = new List<Solution> { Evaluate(center.InnerRadiusMm, center.OuterRadiusMm) };
        for (int row = 0; row < gridSize; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int column = 0; column < gridSize; column++)
                seeds.Add(Evaluate(lowerInner + (upperInner - lowerInner) * row / (gridSize - 1),
                    lowerOuter + (upperOuter - lowerOuter) * column / (gridSize - 1)));
        }
        var solutions = new List<Solution>();
        // The analytical starts must reach exact refinement even when a coarse
        // grid ranks sixteen other approximate locations ahead of them.
        foreach (Solution seed in shapeSeeds.Concat(seeds.Where(seed => double.IsFinite(seed.Cost))
            .OrderBy(seed => seed.Cost).Take(16)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Solution result = Refine(seed, data, delta, Evaluate, cancellationToken);
            int duplicate = solutions.FindIndex(other => Math.Abs(other.Inner - result.Inner) < 1e-6
                && Math.Abs(other.Outer - result.Outer) < 1e-6);
            if (duplicate < 0) solutions.Add(result);
            else if (result.Cost < solutions[duplicate].Cost) solutions[duplicate] = result;
        }
        solutions.Sort((first, second) => first.Cost.CompareTo(second.Cost));
        if (solutions.Count == 0)
            return new(Failed("搜索范围内没有能覆盖全部观测点的有效正半径曲线延拓，请检查照片尺度、盘心或参数范围。"), []);
        Solution best = solutions[0];
        bool boundaryLimited = OnBoundary(best, lowerInner, upperInner, lowerOuter, upperOuter, options.RadiusRangeMm);
        if (target.Parameters.Kind == CalibrationDiscKind.BluRay)
        {
            var bestParameters = center with { InnerRadiusMm = best.Inner, OuterRadiusMm = best.Outer };
            var bestModel = new CalibrationCrossModel(original, new(bestParameters), target.Parameters.Kind,
                target.Parameters == bestParameters);
            if (data.Any(point => bestModel.TryAtRadius(point.Radius, out var predicted)
                && (predicted.SourceRadius < target.DrawingInnerRadiusMm - 1e-8
                    || predicted.SourceRadius > target.DrawingOuterRadiusMm + 1e-8)))
                warnings.Add("部分描点在候选模型下超出原图案环带，已按连续曲线保留；请复核照片尺度、盘心及端点是否沿同一条可见曲线。");
        }
        if (coverage < 0.6) warnings.Add("曲线覆盖的内外范围偏窄，参数可能互相补偿；请补充更靠内、外的点。");
        if (best.Rms > delta * 2) warnings.Add("曲线偏差较大，请检查照片矫正、点位和是否始终沿同一条线标点。");
        if (boundaryLimited)
            warnings.Add("最佳结果靠近搜索边界，建议扩大搜索范围并补充曲线点复核。");
        if (ParameterCorrelation(best, data, Evaluate) > 0.998)
            warnings.Add("当前曲线对两个半径的约束较相似，暂不能仅凭低残差判断参数唯一；请扩大径向覆盖并检查点位精度。");
        if (solutions.Skip(1).Any(solution => solution.Cost <= best.Cost + Math.Max(0.005, best.Cost * 0.25)
            && (Math.Abs(solution.Inner - best.Inner) > 0.0002 || Math.Abs(solution.Outer - best.Outer) > 0.0002)))
            warnings.Add("存在形状接近的参数候选，请对照整条曲线后再应用。");
        if (options.RadiusRangeMm > 0.05)
            warnings.Add("宽范围只进行了有限多起点搜索，候选列表不代表穷尽所有可能参数。");
        warnings.Add("角度展开以相邻点之间没有遗漏整圈为前提；请对照完整曲线，在弯曲或绕行处补点后确认。");
        CalibrationFitCandidate[] candidates = solutions.Take(options.MaxCandidates).Select(solution =>
        {
            (double maxError, int outliers) = ResidualStats(solution.Residuals, delta);
            return new CalibrationFitCandidate(center with { InnerRadiusMm = solution.Inner, OuterRadiusMm = solution.Outer },
                Normalize(solution.Rotation), solution.Rms, solution.Cost, maxError, outliers);
        }).ToArray();
        if (options.FitDvdPitch && !target.HasSingleRadialLine)
            return FitCubic(target, data, coverage, options, candidates, warnings, best.Residuals,
                delta, boundaryLimited, cancellationToken);
        var fitResult = new CalibrationFitResult(true, candidates[0].Parameters, candidates[0].RotationRadians,
            best.Rms, candidates, warnings) { BoundaryLimited = boundaryLimited };
        return new(fitResult, best.Residuals);
    }

    private static bool OnBoundary(Solution solution, double lowerInner, double upperInner,
        double lowerOuter, double upperOuter, double range)
        => Math.Min(solution.Inner - lowerInner, upperInner - solution.Inner) < range * 0.005
        || Math.Min(solution.Outer - lowerOuter, upperOuter - solution.Outer) < range * 0.005;

    private static (double MaxError, int Outliers) ResidualStats(IReadOnlyList<double> residuals, double delta)
    {
        if (residuals.Count == 0) return (0, 0);
        double threshold = Math.Max(0.05, delta * 3);
        double maximum = 0;
        int outliers = 0;
        foreach (double residual in residuals)
        {
            double error = Math.Abs(residual);
            maximum = Math.Max(maximum, error);
            if (error > threshold) outliers++;
        }
        return (maximum, outliers);
    }

    private static double MedianAbsolute(IReadOnlyList<double> residuals)
    {
        if (residuals.Count == 0) return 0;
        double[] magnitudes = residuals.Select(Math.Abs).OrderBy(value => value).ToArray();
        return magnitudes.Length % 2 == 1
            ? magnitudes[magnitudes.Length / 2]
            : (magnitudes[magnitudes.Length / 2 - 1] + magnitudes[magnitudes.Length / 2]) / 2;
    }

    private static IEnumerable<(double Inner, double Outer)> EstimateShapeSeeds(Datum[] data,
        int traceCount, CalibrationParameters generated, CalibrationParameters actual)
    {
        // theta(r) ~= C_trace + alpha*r + beta/r. Remove a separate constant
        // from each trace so its arm identity and initial atan2 branch do not
        // affect the shape estimate. Balance r and 1/r before solving.
        var weights = new double[traceCount];
        var radii = new double[traceCount];
        var inverses = new double[traceCount];
        var angles = new double[traceCount];
        double totalWeight = 0, radiusSum = 0;
        foreach (Datum point in data)
        {
            double weight = point.Weight * point.AngularScale * point.AngularScale;
            weights[point.Trace] += weight;
            radii[point.Trace] += weight * point.Radius;
            inverses[point.Trace] += weight / point.Radius;
            angles[point.Trace] += weight * point.Angle;
            totalWeight += weight;
            radiusSum += weight * point.Radius;
        }
        double radiusScale = radiusSum / totalWeight;
        if (!double.IsFinite(radiusScale) || radiusScale <= 0) yield break;
        for (int trace = 0; trace < traceCount; trace++)
        {
            if (!double.IsFinite(weights[trace]) || weights[trace] <= 0) yield break;
            radii[trace] /= weights[trace];
            inverses[trace] /= weights[trace];
            angles[trace] /= weights[trace];
        }
        double uu = 0, uv = 0, vv = 0, uy = 0;
        foreach (Datum point in data)
        {
            double weight = point.Weight * point.AngularScale * point.AngularScale;
            double u = (point.Radius - radii[point.Trace]) / radiusScale;
            double v = (1 / point.Radius - inverses[point.Trace]) * radiusScale;
            double y = point.Angle - angles[point.Trace];
            uu += weight * u * u; uv += weight * u * v;
            vv += weight * v * v; uy += weight * u * y;
        }
        if (!double.IsFinite(uu) || !double.IsFinite(vv) || uu <= 0 || vv <= 0) yield break;
        // Orthogonalize explicitly rather than subtracting two almost equal
        // normal-equation products when the observed interval is narrow.
        double projection = uv / uu, perpendicularSquared = 0, perpendicularY = 0;
        foreach (Datum point in data)
        {
            double weight = point.Weight * point.AngularScale * point.AngularScale;
            double u = (point.Radius - radii[point.Trace]) / radiusScale;
            double v = (1 / point.Radius - inverses[point.Trace]) * radiusScale - projection * u;
            perpendicularSquared += weight * v * v;
            perpendicularY += weight * v * (point.Angle - angles[point.Trace]);
        }
        if (!double.IsFinite(perpendicularSquared) || perpendicularSquared <= vv * 1e-14) yield break;
        double inverseCoefficient = perpendicularY / perpendicularSquared;
        double alpha = (uy - inverseCoefficient * uv) / (uu * radiusScale);
        double beta = inverseCoefficient * radiusScale;
        double bg = (generated.OuterRadiusMm - generated.InnerRadiusMm)
            * (generated.OuterRadiusMm + generated.InnerRadiusMm) / (2 * generated.TrackLengthMm);
        double eta = actual.TrackLengthMm / generated.TrackLengthMm;
        double discriminant = 1 + 4 * eta * alpha * bg;
        if (!double.IsFinite(alpha) || !double.IsFinite(beta) || !double.IsFinite(discriminant)
            || discriminant < 0) yield break;
        // eta*z^2-z=alpha*bg; the second root can also be positive. Compute
        // it without cancellation, and leave both physical branches to Refine.
        double q = (1 + Math.Sqrt(discriminant)) / 2;
        foreach (double z in new[] { q / eta, -alpha * bg / q })
        {
            if (!double.IsFinite(z) || z <= 0) continue;
            double k = z * z, d = -2 * z * bg * beta;
            double innerSquared = (generated.InnerRadiusMm * generated.InnerRadiusMm - d) / k;
            double outerSquared = (generated.OuterRadiusMm * generated.OuterRadiusMm - d) / k;
            if (!double.IsFinite(innerSquared) || !double.IsFinite(outerSquared)
                || innerSquared <= 0 || outerSquared <= innerSquared) continue;
            yield return (Math.Sqrt(innerSquared), Math.Sqrt(outerSquared));
        }
    }

    private static Datum[] Prepare(IReadOnlyList<CalibrationCrossTrace> traces, double band,
        List<string> warnings, out string? failure)
    {
        failure = null;
        var data = new List<Datum>();
        int traceNumber = 0;
        foreach (CalibrationCrossTrace trace in traces.OrderBy(trace => trace.ArmIndex))
        {
            CalibrationPoint[] points = trace.Points.Where((point, index) => index == 0 || point != trace.Points[index - 1]).ToArray();
            if (points.Length < 2) continue;
            var radii = new double[points.Length];
            var angles = new double[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                radii[i] = Math.Sqrt(points[i].X * points[i].X + points[i].Y * points[i].Y);
                double angle = Math.Atan2(points[i].Y, points[i].X);
                if (i == 0) angles[i] = angle - (-Math.PI / 2 + trace.ArmIndex * Math.PI / 2);
                else
                {
                    if (radii[i] < radii[i - 1] - Math.Max(0.1, band * 0.005))
                    {
                        failure = "曲线出现明显向内回退，请按内到外的顺序沿同一条线标点。";
                        return [];
                    }
                    double change = Normalize(angle - Math.Atan2(points[i - 1].Y, points[i - 1].X));
                    // The shortest path is still the true one right up to half a turn; the
                    // old 0.75 pi bound rejected valid traces whose points happened to land
                    // near that limit. Only a change that rounds to +-pi is genuinely
                    // ambiguous, and that is where unwrapping must stop.
                    if (Math.Abs(change) > Math.PI * 0.995)
                    {
                        failure = "相邻曲线点转角接近半圈，无法判断是否漏掉整圈；请在这两点之间沿实际曲线补点。";
                        return [];
                    }
                    if (Math.Abs(change) > Math.PI * 0.9 && !warnings.Contains(NearHalfTurnWarning))
                        warnings.Add(NearHalfTurnWarning);
                    angles[i] = angles[i - 1] + change;
                }
            }
            if (radii[^1] - radii[0] < band * 0.02) continue;
            if (points.Length > 1024) warnings.Add("曲线点较多，拟合使用均匀抽取的点；完整顺序仍用于角度展开。");
            int count = Math.Min(points.Length, 1024);
            for (int slot = 0; slot < count; slot++)
            {
                int i = (int)Math.Round(slot * (points.Length - 1.0) / (count - 1));
                int before = Math.Max(0, i - 2), after = Math.Min(points.Length - 1, i + 2);
                double meanRadius = 0, meanAngle = 0;
                for (int neighbor = before; neighbor <= after; neighbor++)
                {
                    meanRadius += radii[neighbor];
                    meanAngle += angles[neighbor];
                }
                meanRadius /= after - before + 1;
                meanAngle /= after - before + 1;
                double covariance = 0, variance = 0;
                for (int neighbor = before; neighbor <= after; neighbor++)
                {
                    covariance += (radii[neighbor] - meanRadius) * (angles[neighbor] - meanAngle);
                    variance += (radii[neighbor] - meanRadius) * (radii[neighbor] - meanRadius);
                }
                double slope = covariance / Math.Max(variance, 1e-12);
                // Use the observed tangent, not a candidate's tangent: a wildly winding
                // candidate must not reduce its own penalty merely by increasing its slope.
                double scale = radii[i] / Math.Sqrt(1 + radii[i] * radii[i] * slope * slope);
                int previous = slot == 0 ? 0 : (int)Math.Round((slot - 1) * (points.Length - 1.0) / (count - 1));
                int next = slot == count - 1 ? points.Length - 1 : (int)Math.Round((slot + 1) * (points.Length - 1.0) / (count - 1));
                double weight = Math.Max(1e-8, radii[next] - radii[previous]) / (radii[^1] - radii[0]);
                data.Add(new(traceNumber, radii[i], angles[i], scale, weight));
            }
            traceNumber++;
        }
        return data.ToArray();
    }

    private static Solution Refine(Solution initial, Datum[] data, double delta,
        Func<double, double, Solution> evaluate, CancellationToken token)
    {
        Solution current = initial;
        double damping = 1e-4;
        for (int iteration = 0; iteration < 80; iteration++)
        {
            token.ThrowIfCancellationRequested();
            if (!Jacobian(current, evaluate, out double[] first, out double[] second)) break;
            double a = 0, b = 0, c = 0, x = 0, y = 0;
            for (int i = 0; i < data.Length; i++)
            {
                double error = Math.Abs(current.Residuals[i]);
                double weight = data[i].Weight * (error <= delta ? 1 : delta / error);
                a += weight * first[i] * first[i];
                b += weight * first[i] * second[i];
                c += weight * second[i] * second[i];
                x -= weight * first[i] * current.Residuals[i];
                y -= weight * second[i] * current.Residuals[i];
            }
            a *= 1 + damping;
            c *= 1 + damping;
            double determinant = a * c - b * b;
            if (!double.IsFinite(determinant) || determinant <= 1e-20) break;
            double innerStep = (x * c - y * b) / determinant;
            double outerStep = (y * a - x * b) / determinant;
            Solution proposed = evaluate(current.Inner + innerStep, current.Outer + outerStep);
            if (proposed.Cost < current.Cost)
            {
                double improvement = current.Cost - proposed.Cost;
                current = proposed;
                damping = Math.Max(1e-10, damping / 3);
                if (improvement < 1e-15 || Math.Max(Math.Abs(innerStep), Math.Abs(outerStep)) < 1e-10) break;
            }
            else
            {
                damping *= 8;
                if (damping > 1e12) break;
            }
        }
        return current;
    }

    private static bool Jacobian(Solution current, Func<double, double, Solution> evaluate,
        out double[] first, out double[] second)
    {
        const double step = 1e-6;
        first = new double[current.Residuals.Length];
        second = new double[current.Residuals.Length];
        Solution shiftedInner = evaluate(current.Inner + step, current.Outer);
        Solution shiftedOuter = evaluate(current.Inner, current.Outer + step);
        double innerStep = step, outerStep = step;
        if (!double.IsFinite(shiftedInner.Cost)) { innerStep = -step; shiftedInner = evaluate(current.Inner - step, current.Outer); }
        if (!double.IsFinite(shiftedOuter.Cost)) { outerStep = -step; shiftedOuter = evaluate(current.Inner, current.Outer - step); }
        if (!double.IsFinite(shiftedInner.Cost) || !double.IsFinite(shiftedOuter.Cost)) return false;
        for (int i = 0; i < first.Length; i++)
        {
            first[i] = (shiftedInner.Residuals[i] - current.Residuals[i]) / innerStep;
            second[i] = (shiftedOuter.Residuals[i] - current.Residuals[i]) / outerStep;
        }
        return true;
    }

    private static double ParameterCorrelation(Solution solution, Datum[] data, Func<double, double, Solution> evaluate)
    {
        if (!Jacobian(solution, evaluate, out double[] first, out double[] second)) return 1;
        double a = 0, b = 0, c = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a += data[i].Weight * first[i] * first[i];
            b += data[i].Weight * first[i] * second[i];
            c += data[i].Weight * second[i] * second[i];
        }
        return Math.Abs(b) / Math.Sqrt(Math.Max(1e-30, a * c));
    }

    private static Solution Invalid(double inner, double outer)
        => new(inner, outer, 0, double.PositiveInfinity, double.PositiveInfinity, []);
    private static double Normalize(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
    private static CalibrationFitResult Failed(string reason)
        => new(false, null, 0, double.NaN, [], [reason]);
}
