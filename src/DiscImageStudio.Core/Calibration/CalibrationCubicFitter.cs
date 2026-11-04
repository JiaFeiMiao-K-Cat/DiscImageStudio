namespace DiscImageStudio.Core.Calibration;

public static partial class CalibrationCrossFitter
{
    private sealed record CubicSolution(CalibrationParameters Parameters, double Rotation,
        double Cost, double Rms, double[] Residuals);

    private static SolveOutcome FitCubic(CalibrationTarget target, Datum[] data, double coverage,
        CalibrationFitOptions options, CalibrationFitCandidate[] baseline, List<string> warnings,
        double[] baselineResiduals, double delta, bool boundaryLimited, CancellationToken token)
    {
        SolveOutcome Baseline() => new(new CalibrationFitResult(true, baseline[0].Parameters,
            baseline[0].RotationRadians, baseline[0].RmsErrorMm, baseline, warnings)
            { BoundaryLimited = boundaryLimited }, baselineResiduals);
        // Three shape terms plus two radii and rotation need substantially more support
        // than the constant-pitch model. Repeated points on different arms do not add radii.
        int radialLevels = data.Select(d => Math.Round(d.Radius, 4)).Distinct().Count();
        if (radialLevels < 12 || coverage < 0.6)
        {
            warnings.Add("三次轨距拟合需要至少 12 个不同半径的点且覆盖至少 60% 绘图区；本次保持轨距系数，只拟合内外半径。");
            return Baseline();
        }
        // Do not invent additional coefficients for a curve already explained to sub-micron
        // precision by the supplied pitch profile. This also preserves exact legacy fits.
        if (baseline[0].RmsErrorMm < 1e-6) return Baseline();

        CalibrationParameters center = options.SearchCenter ?? target.Parameters;
        var generated = new CalibrationGeometry(target.Parameters);
        int traces = data.Max(d => d.Trace) + 1;
        double lowerInner = Math.Max(0.1, center.InnerRadiusMm - options.RadiusRangeMm);
        double upperInner = center.InnerRadiusMm + options.RadiusRangeMm;
        double lowerOuter = Math.Max(lowerInner + 0.1, center.OuterRadiusMm - options.RadiusRangeMm);
        double upperOuter = center.OuterRadiusMm + options.RadiusRangeMm;
        CubicSolution Evaluate(CalibrationParameters parameters)
        {
            CubicSolution Invalid() => new(parameters, 0, double.PositiveInfinity, double.PositiveInfinity, []);
            if (Math.Abs(parameters.InnerRadiusMm - center.InnerRadiusMm) > options.RadiusRangeMm
                || Math.Abs(parameters.OuterRadiusMm - center.OuterRadiusMm) > options.RadiusRangeMm
                || Math.Abs(parameters.PitchLinear - center.PitchLinear) > options.PitchCoefficientRange
                || Math.Abs(parameters.PitchQuadratic - center.PitchQuadratic) > options.PitchCoefficientRange
                || Math.Abs(parameters.PitchCubic - center.PitchCubic) > options.PitchCoefficientRange)
                return Invalid();
            CalibrationGeometry geometry;
            try { parameters.Validate(); geometry = new(parameters); }
            catch (ArgumentException) { return Invalid(); }
            var model = new CalibrationCrossModel(generated, geometry, target.Parameters.Kind,
                target.Parameters == parameters);
            var angles = new double[data.Length];
            var means = new double[traces];
            var meanWeights = new double[traces];
            for (int i = 0; i < data.Length; i++)
            {
                Datum point = data[i];
                if (!model.TryAtRadius(point.Radius, out CalibrationCrossSample predicted)) return Invalid();
                angles[i] = point.Angle - predicted.Phase;
                double weight = point.Weight * point.AngularScale * point.AngularScale;
                means[point.Trace] += weight * angles[i]; meanWeights[point.Trace] += weight;
            }
            double cosine = 0, sine = 0;
            for (int trace = 0; trace < traces; trace++)
            {
                means[trace] /= meanWeights[trace];
                cosine += Math.Cos(means[trace]); sine += Math.Sin(means[trace]);
            }
            double rotation = Math.Atan2(sine, cosine);
            for (int i = 0; i < angles.Length; i++)
                angles[i] += 2 * Math.PI * Math.Round((rotation - means[data[i].Trace]) / (2 * Math.PI));
            for (int iteration = 0; iteration < 12; iteration++)
            {
                double sum = 0, weights = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    Datum point = data[i];
                    double error = Math.Abs((angles[i] - rotation) * point.AngularScale);
                    double weight = point.Weight * point.AngularScale * point.AngularScale
                        * (error <= delta ? 1 : delta / error);
                    sum += weight * angles[i]; weights += weight;
                }
                double updated = sum / weights;
                if (Math.Abs(updated - rotation) < 1e-12) break;
                rotation = updated;
            }
            var residuals = new double[data.Length];
            double cost = 0, squared = 0, weightSum = 0;
            for (int i = 0; i < data.Length; i++)
            {
                double residual = (angles[i] - rotation) * data[i].AngularScale;
                residuals[i] = residual;
                double error = Math.Abs(residual);
                cost += data[i].Weight * (error <= delta ? 0.5 * error * error : delta * (error - delta / 2));
                squared += data[i].Weight * residual * residual; weightSum += data[i].Weight;
            }
            return new(parameters, rotation, cost / weightSum, Math.Sqrt(squared / weightSum), residuals);
        }

        var starts = baseline.Take(3).Select(c => c.Parameters).Append(center).Distinct().ToArray();
        var solutions = new List<CubicSolution>();
        foreach (CalibrationParameters start in starts)
        {
            token.ThrowIfCancellationRequested();
            CubicSolution result = RefineCubic(Evaluate(start), data, delta, Evaluate, token);
            if (!double.IsFinite(result.Cost)) continue;
            int duplicate = solutions.FindIndex(other => ToVector(other.Parameters).Zip(ToVector(result.Parameters))
                .Select((pair, index) => Math.Abs(pair.First - pair.Second) < (index < 2 ? 1e-6 : 1e-8)).All(same => same));
            if (duplicate < 0) solutions.Add(result);
            else if (result.Cost < solutions[duplicate].Cost) solutions[duplicate] = result;
        }
        solutions.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        if (solutions.Count == 0 || solutions[0].Cost >= baseline[0].RobustCost * 0.995)
        {
            warnings.Add("三次轨距没有带来稳定改善，保留原轨距系数；可增加内、中、外圈点位后重试。");
            return Baseline();
        }
        CubicSolution best = solutions[0];
        if (!CubicJacobian(best, Evaluate, out double[][] jacobian)
            || JacobianIndependence(jacobian, data) < 1e-7)
        {
            warnings.Add("当前点位无法稳定区分三个轨距系数与内外半径，本次保留原轨距系数；请扩大径向覆盖并提高点位精度。");
            return Baseline();
        }
        // Radius-only diagnostics describe the discarded baseline, not this candidate.
        warnings.RemoveAll(w => w.StartsWith("曲线偏差较大", StringComparison.Ordinal)
            || w.StartsWith("最佳结果靠近", StringComparison.Ordinal)
            || w.StartsWith("当前曲线对两个半径", StringComparison.Ordinal)
            || w.StartsWith("存在形状接近", StringComparison.Ordinal));
        if (best.Rms > delta * 2)
            warnings.Add("曲线偏差较大，请检查照片矫正、点位和是否始终沿同一条线标点。");
        if (Math.Max(Math.Abs(best.Parameters.InnerRadiusMm - center.InnerRadiusMm),
            Math.Abs(best.Parameters.OuterRadiusMm - center.OuterRadiusMm)) > options.RadiusRangeMm * 0.995)
            warnings.Add("最佳结果靠近搜索边界，建议扩大搜索范围并补充曲线点复核。");
        warnings.Add("已拟合三次轨距；三个形状系数与内外半径可能互相补偿，低残差不表示参数唯一，应用前请核对整条预测曲线。");
        if (Math.Max(Math.Abs(best.Parameters.PitchLinear - center.PitchLinear),
            Math.Max(Math.Abs(best.Parameters.PitchQuadratic - center.PitchQuadratic),
                Math.Abs(best.Parameters.PitchCubic - center.PitchCubic))) > options.PitchCoefficientRange * 0.995)
            warnings.Add("轨距系数接近搜索边界，请复核点位，必要时扩大系数搜索范围。");
        CalibrationFitCandidate[] candidates = solutions.Take(options.MaxCandidates).Select(solution =>
        {
            (double maxError, int outliers) = ResidualStats(solution.Residuals, delta);
            return new CalibrationFitCandidate(solution.Parameters, Normalize(solution.Rotation),
                solution.Rms, solution.Cost, maxError, outliers);
        }).ToArray();
        bool cubicBoundary = Math.Min(best.Parameters.InnerRadiusMm - lowerInner,
                upperInner - best.Parameters.InnerRadiusMm) < options.RadiusRangeMm * 0.005
            || Math.Min(best.Parameters.OuterRadiusMm - lowerOuter,
                upperOuter - best.Parameters.OuterRadiusMm) < options.RadiusRangeMm * 0.005;
        var cubicResult = new CalibrationFitResult(true, best.Parameters, Normalize(best.Rotation),
            best.Rms, candidates, warnings) { BoundaryLimited = cubicBoundary };
        return new(cubicResult, best.Residuals);
    }

    private static CubicSolution RefineCubic(CubicSolution initial, Datum[] data, double delta,
        Func<CalibrationParameters, CubicSolution> evaluate, CancellationToken token)
    {
        CubicSolution current = initial;
        double damping = 1e-5;
        for (int iteration = 0; iteration < 120 && double.IsFinite(current.Cost); iteration++)
        {
            token.ThrowIfCancellationRequested();
            if (!CubicJacobian(current, evaluate, out double[][] jacobian)) break;
            var matrix = new double[5, 5]; var rhs = new double[5]; var scales = new double[5];
            for (int i = 0; i < data.Length; i++)
            {
                double error = Math.Abs(current.Residuals[i]);
                double weight = data[i].Weight * (error <= delta ? 1 : delta / error);
                for (int a = 0; a < 5; a++)
                {
                    rhs[a] -= weight * jacobian[a][i] * current.Residuals[i];
                    for (int b = 0; b <= a; b++) matrix[a, b] += weight * jacobian[a][i] * jacobian[b][i];
                }
            }
            for (int a = 0; a < 5; a++) scales[a] = Math.Sqrt(Math.Max(matrix[a, a], 1e-30));
            for (int a = 0; a < 5; a++)
            {
                rhs[a] /= scales[a];
                for (int b = 0; b <= a; b++) matrix[b, a] = matrix[a, b] /= scales[a] * scales[b];
                matrix[a, a] += damping;
            }
            if (!SolveCubicSystem(matrix, rhs)) break;
            double[] vector = ToVector(current.Parameters);
            CubicSolution proposed = current;
            for (int backtrack = 0; backtrack < 12; backtrack++)
            {
                double fraction = Math.Pow(0.5, backtrack);
                proposed = evaluate(FromVector(current.Parameters,
                    vector.Select((value, i) => value + fraction * rhs[i] / scales[i]).ToArray()));
                if (proposed.Cost < current.Cost) break;
            }
            if (proposed.Cost < current.Cost)
            {
                double improvement = current.Cost - proposed.Cost;
                current = proposed; damping = Math.Max(1e-12, damping / 3);
                if (current.Rms < 1e-7 || improvement < 1e-17) break;
            }
            else { damping *= 10; if (damping > 1e10) break; }
        }
        return current;
    }

    private static bool CubicJacobian(CubicSolution current, Func<CalibrationParameters, CubicSolution> evaluate,
        out double[][] jacobian)
    {
        jacobian = new double[5][];
        double[] vector = ToVector(current.Parameters);
        for (int column = 0; column < 5; column++)
        {
            // Central differences suppress numerical asymmetry in the exact arc integrals.
            double step = column < 2 ? 2e-6 : 2e-7;
            double[] plus = (double[])vector.Clone(), minus = (double[])vector.Clone();
            plus[column] += step; minus[column] -= step;
            CubicSolution forward = evaluate(FromVector(current.Parameters, plus));
            CubicSolution backward = evaluate(FromVector(current.Parameters, minus));
            double span = 2 * step;
            if (!double.IsFinite(forward.Cost)) { forward = current; span -= step; }
            if (!double.IsFinite(backward.Cost)) { backward = current; span -= step; }
            if (span <= 0) return false;
            jacobian[column] = forward.Residuals.Zip(backward.Residuals).Select(p => (p.First - p.Second) / span).ToArray();
        }
        return true;
    }

    private static double JacobianIndependence(double[][] jacobian, Datum[] data)
    {
        var basis = new List<double[]>();
        double independence = 1;
        foreach (double[] column in jacobian)
        {
            double[] values = column.Select((value, i) => value * Math.Sqrt(data[i].Weight)).ToArray();
            double norm = Math.Sqrt(values.Sum(v => v * v));
            if (!double.IsFinite(norm) || norm <= 0) return 0;
            for (int i = 0; i < values.Length; i++) values[i] /= norm;
            // Reorthogonalization prevents an almost dependent fifth parameter from
            // appearing independent solely because of subtraction error.
            for (int pass = 0; pass < 2; pass++)
                foreach (double[] previous in basis)
                {
                    double projection = values.Zip(previous).Sum(p => p.First * p.Second);
                    for (int i = 0; i < values.Length; i++) values[i] -= projection * previous[i];
                }
            double remaining = Math.Sqrt(values.Sum(v => v * v));
            independence = Math.Min(independence, remaining);
            if (remaining < 1e-12) return 0;
            for (int i = 0; i < values.Length; i++) values[i] /= remaining;
            basis.Add(values);
        }
        return independence;
    }

    private static bool SolveCubicSystem(double[,] matrix, double[] rhs)
    {
        for (int pivot = 0; pivot < 5; pivot++)
        {
            int selected = pivot;
            for (int row = pivot + 1; row < 5; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[selected, pivot])) selected = row;
            if (!double.IsFinite(matrix[selected, pivot]) || Math.Abs(matrix[selected, pivot]) < 1e-16) return false;
            for (int column = pivot; column < 5; column++)
                (matrix[pivot, column], matrix[selected, column]) = (matrix[selected, column], matrix[pivot, column]);
            (rhs[pivot], rhs[selected]) = (rhs[selected], rhs[pivot]);
            double diagonal = matrix[pivot, pivot];
            for (int column = pivot; column < 5; column++) matrix[pivot, column] /= diagonal;
            rhs[pivot] /= diagonal;
            for (int row = 0; row < 5; row++)
            {
                if (row == pivot) continue;
                double factor = matrix[row, pivot];
                for (int column = pivot; column < 5; column++) matrix[row, column] -= factor * matrix[pivot, column];
                rhs[row] -= factor * rhs[pivot];
            }
        }
        return rhs.All(double.IsFinite);
    }

    private static double[] ToVector(CalibrationParameters p)
        => [p.InnerRadiusMm, p.OuterRadiusMm, p.PitchLinear, p.PitchQuadratic, p.PitchCubic];
    private static CalibrationParameters FromVector(CalibrationParameters p, double[] values)
        => p with { InnerRadiusMm = values[0], OuterRadiusMm = values[1], PitchLinear = values[2],
            PitchQuadratic = values[3], PitchCubic = values[4] };
}
