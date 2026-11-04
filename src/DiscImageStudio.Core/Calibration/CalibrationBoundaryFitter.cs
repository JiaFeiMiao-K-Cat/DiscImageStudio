namespace DiscImageStudio.Core.Calibration;

/// <summary>One continuous part of either edge of a half-disc target. Segments need not join.</summary>
public sealed record CalibrationBoundarySegment(int Id, IReadOnlyList<CalibrationPoint> Points);

/// <summary>
/// Fits separated half-disc boundaries without inventing turns between them. Angles are unwrapped
/// only inside each continuous segment. All segments share one rotation, with an integer half-turn per
/// segment because either of the two opposite black/white boundaries may be marked.
/// </summary>
public static class CalibrationBoundaryFitter
{
    public const int MinimumSegments = 4;
    public const int MinimumPointsPerSegment = 3;
    public const int MinimumContinuousPoints = 6;
    public const int MinimumExtendedSegments = 2;
    public const double MinimumExtendedCoverage = .10;
    public const double MinimumCoverage = .55;
    public const double MaximumRadiusRangeMm = .05;

    private sealed record Datum(int Segment, double Radius, double Angle, double Scale, double Weight);
    private sealed record Solution(double Inner, double Outer, double Rotation, double Cost, double Rms, double[] Residuals);
    private sealed record FastGroup(double Weight, double Mean, double Inner, double Outer,
        double UU, double UI, double UO, double II, double IO, double OO);

    /// <summary>UI point-count gate only; physical coverage and identifiability are checked by Fit.</summary>
    public static bool HasEnoughPoints(IReadOnlyList<CalibrationBoundarySegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var nonempty = segments.Where(s => s.Points.Count > 0).ToArray();
        return nonempty.Length > 0 && nonempty.All(s => s.Points.Count >= MinimumPointsPerSegment)
            && (nonempty.Length >= MinimumSegments || nonempty.Any(s => s.Points.Count >= MinimumContinuousPoints));
    }

    /// <summary>Checks portable records, including incomplete editing drafts.</summary>
    public static void ValidateSegments(CalibrationTarget target, IReadOnlyList<CalibrationBoundarySegment> segments)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count > 64) throw new ArgumentException("半盘边界片段不能超过 64 段。");
        var ids = new HashSet<int>();
        int count = 0;
        foreach (var segment in segments)
        {
            if (segment is null || segment.Id is < 0 or > 63 || !ids.Add(segment.Id) || segment.Points is null)
                throw new ArgumentException("边界片段编号无效或重复。");
            if (!target.HasBoundaryPattern && segment.Points.Count > 0)
                throw new ArgumentException("此记录不是半黑半白中标图，不能加入边界片段。");
            count += segment.Points.Count;
            if (count > 4000) throw new ArgumentException("边界片段的点数不能超过 4000。");
            foreach (var point in segment.Points)
                if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                    || Math.Abs(point.X) > 1000 || Math.Abs(point.Y) > 1000
                    || point.X * point.X + point.Y * point.Y < .01)
                    throw new ArgumentException("边界点坐标无效，请使用矫正后以盘心为原点的坐标。");
        }
    }

    public static CalibrationFitResult Fit(CalibrationTarget target,
        IReadOnlyList<CalibrationBoundarySegment> segments, CalibrationFitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        ValidateSegments(target, segments);
        options ??= new();
        options.Validate(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!target.HasBoundaryPattern) return Failed("边界拟合需要对应的中标图案记录。");
        if (options.RadiusRangeMm > MaximumRadiusRangeMm)
            return Failed("分段中标的半径搜索范围不能超过 ±0.05 mm，请先完成 v13 粗标。");
        double drawingWidth = target.DrawingOuterRadiusMm - target.DrawingInnerRadiusMm;
        var continuousIds = segments.Where(s => s.Points.Distinct().Count() >= MinimumContinuousPoints)
            .Where(s => s.Points.Max(Radius) - s.Points.Min(Radius) >= drawingWidth * MinimumCoverage)
            .Select(s => s.Id).ToHashSet();
        var extendedIds = segments.Where(s => s.Points.Distinct().Count() >= MinimumContinuousPoints)
            .Where(s => s.Points.Max(Radius) - s.Points.Min(Radius) >= drawingWidth * MinimumExtendedCoverage)
            .Select(s => s.Id).ToHashSet();
        var extendedPieces = segments.Where(s => extendedIds.Contains(s.Id)).ToArray();
        bool extendedInput = extendedPieces.Any(a => extendedPieces.Any(b => a.Id != b.Id
            && (a.Points.Max(Radius) <= b.Points.Min(Radius) || b.Points.Max(Radius) <= a.Points.Min(Radius))));
        var data = Prepare(segments, continuousIds, extendedInput, out string? failure);
        if (failure is not null) return Failed(failure);
        double coverage = (data.Max(d => d.Radius) - data.Min(d => d.Radius)) / drawingWidth;
        if (coverage < MinimumCoverage)
            return Failed("边界片段需覆盖至少 55% 的绘图半径范围，请在更靠内、靠外的位置各补一段。");
        var radialCenters = data.GroupBy(d => d.Segment).Select(g => g.Average(d => d.Radius)).Order().ToArray();
        int separateRadii = 1;
        double previousRadius = radialCenters[0];
        foreach (double radius in radialCenters.Skip(1))
            if (radius - previousRadius >= .5) { separateRadii++; previousRadius = radius; }
        if (continuousIds.Count == 0 && !extendedInput && separateRadii < MinimumSegments)
            return Failed("片段集中在少数相近半径，请选至少 4 个不同半径位置的边界，避免重复描同一小段。");
        data = IndependentWeights(data, out int independentRegions);
        if (continuousIds.Count == 0 && independentRegions < (extendedInput ? MinimumExtendedSegments : MinimumSegments))
            return Failed("多段边界覆盖的是相同的半径范围，请在至少 4 处互不重叠的半径位置选段。");
        // Preserve the written pitch profile. This stage resolves phase winding and radii;
        // fitting extra DVD coefficients from disconnected pieces is deferred to the cross.
        var center = (options.SearchCenter ?? target.Parameters) with
        {
            PitchLinear = target.Parameters.PitchLinear,
            PitchQuadratic = target.Parameters.PitchQuadratic,
            PitchCubic = target.Parameters.PitchCubic,
        };
        var generated = new CalibrationGeometry(target.Parameters);
        double lowerInner = Math.Max(.1, center.InnerRadiusMm - options.RadiusRangeMm);
        double upperInner = center.InnerRadiusMm + options.RadiusRangeMm;
        double lowerOuter = Math.Max(lowerInner + .1, center.OuterRadiusMm - options.RadiusRangeMm);
        double upperOuter = center.OuterRadiusMm + options.RadiusRangeMm;
        double delta = Math.Min(.15, options.HuberDeltaMm);
        int groupCount = data.Max(d => d.Segment) + 1;

        double[]? Phases(double inner, double outer)
        {
            if (inner < lowerInner || inner > upperInner || outer < lowerOuter || outer > upperOuter || outer <= inner)
                return null;
            var actual = center with { InnerRadiusMm = inner, OuterRadiusMm = outer };
            CalibrationGeometry geometry;
            try { geometry = new(actual); }
            catch (ArgumentException) { return null; }
            var model = new CalibrationCrossModel(generated, geometry, target.Parameters.Kind, actual == target.Parameters, target);
            var phases = new double[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                if (!model.TryAtRadius(data[i].Radius, out var sample)) return null;
                phases[i] = sample.Phase;
            }
            return phases;
        }

        Solution Evaluate(double inner, double outer)
        {
            double[]? phases = Phases(inner, outer);
            if (phases is null) return Invalid(inner, outer);
            var angles = new double[data.Length];
            var means = new double[groupCount];
            var weights = new double[groupCount];
            for (int i = 0; i < data.Length; i++)
            {
                var point = data[i];
                angles[i] = point.Angle - phases[i];
                double w = point.Weight * point.Scale * point.Scale;
                means[point.Segment] += angles[i] * w;
                weights[point.Segment] += w;
            }
            double cosine = 0, sine = 0;
            for (int group = 0; group < groupCount; group++)
            {
                means[group] /= weights[group];
                cosine += weights[group] * Math.Cos(2 * means[group]);
                sine += weights[group] * Math.Sin(2 * means[group]);
            }
            double rotation = .5 * Math.Atan2(sine, cosine);
            // A single half-turn integer for the WHOLE segment. Per-point wrapping
            // would hide a wrong slope or an extra winding inside a marked piece.
            for (int i = 0; i < angles.Length; i++)
                angles[i] -= Math.PI * Math.Round((means[data[i].Segment] - rotation) / Math.PI);
            for (int iteration = 0; iteration < 8; iteration++)
            {
                double sum = 0, weight = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    double error = Math.Abs((angles[i] - rotation) * data[i].Scale);
                    double w = data[i].Weight * data[i].Scale * data[i].Scale * (error <= delta ? 1 : delta / error);
                    sum += w * angles[i]; weight += w;
                }
                double updated = sum / weight;
                if (Math.Abs(updated - rotation) < 1e-12) break;
                rotation = updated;
            }
            var residuals = new double[data.Length];
            double cost = 0, squared = 0;
            for (int i = 0; i < data.Length; i++)
            {
                double error = residuals[i] = (angles[i] - rotation) * data[i].Scale;
                double magnitude = Math.Abs(error);
                squared += data[i].Weight * error * error;
                cost += data[i].Weight * (magnitude <= delta ? error * error / 2 : delta * (magnitude - delta / 2));
            }
            return new(inner, outer, rotation, cost, Math.Sqrt(squared), residuals);
        }

        // Enumerate all resolved phase wells in the small coarse-calibrated box.
        // Linearizing geometry is used ONLY to locate starts; every candidate is
        // refined and compared with the exact physical geometry below.
        const double differential = 1e-6;
        double[]? origin = Phases(center.InnerRadiusMm, center.OuterRadiusMm);
        double[]? shiftedInner = Phases(center.InnerRadiusMm + differential, center.OuterRadiusMm);
        double[]? shiftedOuter = Phases(center.InnerRadiusMm, center.OuterRadiusMm + differential);
        if (origin is null || shiftedInner is null || shiftedOuter is null)
            return Failed("当前半径范围没有有效的盘面模型，请检查生成记录和照片轮廓。");
        var derivativeInner = shiftedInner.Select((p, i) => (p - origin[i]) / differential).ToArray();
        var derivativeOuter = shiftedOuter.Select((p, i) => (p - origin[i]) / differential).ToArray();
        foreach (double inner in new[] { lowerInner, upperInner })
            foreach (double outer in new[] { lowerOuter, upperOuter })
            {
                var corner = Phases(inner, outer);
                if (corner is null) return Failed("搜索边界超出有效盘面模型，请缩小中标半径搜索范围。");
                var error = corner.Select((p, i) => p - origin[i]
                    - (inner - center.InnerRadiusMm) * derivativeInner[i]
                    - (outer - center.OuterRadiusMm) * derivativeOuter[i]).ToArray();
                if (error.Max() - error.Min() > Math.PI / 8)
                    return Failed("当前范围的角度变化过大，无法可靠穷举绕圈候选，请缩小中标半径搜索范围。");
            }
        FastGroup[] fast = BuildFastGroups(data, groupCount, origin, derivativeInner, derivativeOuter);
        int columns = GridCount(upperInner - lowerInner, derivativeInner);
        int rows = GridCount(upperOuter - lowerOuter, derivativeOuter);
        if ((long)columns * rows > 800_000)
            return Failed("当前范围包含过多绕圈候选，请缩小中标半径搜索范围后再算。");
        var grid = new double[rows * columns];
        double stepInner = (upperInner - lowerInner) / (columns - 1);
        double stepOuter = (upperOuter - lowerOuter) / (rows - 1);
        for (int row = 0; row < rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double outer = lowerOuter + row * stepOuter - center.OuterRadiusMm;
            for (int column = 0; column < columns; column++)
                grid[row * columns + column] = FastCost(fast,
                    lowerInner + column * stepInner - center.InnerRadiusMm, outer);
        }
        var starts = new List<(double Cost, double Inner, double Outer)>();
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                double cost = grid[row * columns + column];
                bool minimum = true;
                for (int y = Math.Max(0, row - 1); y <= Math.Min(rows - 1, row + 1) && minimum; y++)
                    for (int x = Math.Max(0, column - 1); x <= Math.Min(columns - 1, column + 1); x++)
                        if (grid[y * columns + x] < cost) { minimum = false; break; }
                if (minimum) starts.Add((cost, lowerInner + column * stepInner, lowerOuter + row * stepOuter));
            }
        if (starts.Count > 8192)
            return Failed("当前搜索范围有过多可行绕圈区间，尚未排除歧义，请缩小半径范围或补充较长的边界片段。");
        var solutions = new List<Solution>();
        // Never silently truncate competing wells. The linearized least-squares cost
        // cannot bound the exact robust cost, so every resolved local minimum is refined.
        foreach (var start in starts.OrderBy(s => s.Cost))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var solution = Refine(Evaluate(start.Inner, start.Outer), data, delta, Evaluate, cancellationToken);
            if (!double.IsFinite(solution.Cost)) continue;
            int duplicate = solutions.FindIndex(s => Math.Max(Math.Abs(s.Inner - solution.Inner), Math.Abs(s.Outer - solution.Outer)) < 1e-6);
            if (duplicate < 0) solutions.Add(solution);
            else if (solutions[duplicate].Cost > solution.Cost) solutions[duplicate] = solution;
        }
        solutions.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        if (solutions.Count == 0) return Failed("这些边界片段没有得到有效参数，请检查点是否位于黑白交界。");
        Solution best = solutions[0];
        var candidates = solutions.Take(options.MaxCandidates).Select(s => new CalibrationFitCandidate(
            center with { InnerRadiusMm = s.Inner, OuterRadiusMm = s.Outer }, HalfTurn(s.Rotation), s.Rms, s.Cost,
            s.Residuals.Max(Math.Abs), s.Residuals.Count(e => Math.Abs(e) > 3 * delta))).ToArray();
        bool boundary = Math.Min(Math.Min(best.Inner - lowerInner, upperInner - best.Inner),
            Math.Min(best.Outer - lowerOuter, upperOuter - best.Outer)) <= options.RadiusRangeMm * .002;
        var warnings = new List<string>();
        if (boundary) warnings.Add("最佳候选落在搜索边界，尚不能确认绕圈数，请复核粗标结果或适当调整搜索范围。");
        if (best.Rms > .15 || best.Residuals.Max(Math.Abs) > Math.Max(.3, best.Rms * 4))
            warnings.Add("边界片段与同一个盘面模型不一致，请检查误点、照片矫正和片段是否跨过了另一条边界。");
        // A conservative likelihood gap at the observed click-noise scale, with a
        // 0.02 mm floor. An almost equal wrong winding must not become an applied result.
        double noise = Math.Max(.02, best.Rms);
        double effectiveCount = Math.Min(EffectivePointCount(data), independentRegions * 5d);
        double ambiguityGap = noise * noise * 12 / Math.Max(1, effectiveCount - 3);
        if (solutions.Skip(1).Any(s => s.Rms * s.Rms <= best.Rms * best.Rms + ambiguityGap
            && Math.Max(Math.Abs(s.Inner - best.Inner), Math.Abs(s.Outer - best.Outer)) > .00015))
            warnings.Add("仍有不同绕圈数的候选能解释这些片段，请在不同半径补一段更长的清楚边界后再算。");
        if (warnings.Count > 0)
            return new(false, null, HalfTurn(best.Rotation), best.Rms, candidates, warnings) { BoundaryLimited = boundary };
        return new(true, candidates[0].Parameters, candidates[0].RotationRadians, best.Rms, candidates, []);
    }

    private static Datum[] Prepare(IReadOnlyList<CalibrationBoundarySegment> segments,
        IReadOnlySet<int> continuousIds, bool extendedInput, out string? failure)
    {
        failure = null;
        var complete = segments.Where(s => s.Points.Count > 0).ToArray();
        if ((continuousIds.Count == 0 && !extendedInput && complete.Length < MinimumSegments)
            || complete.Any(s => s.Points.Count < MinimumPointsPerSegment))
        {
            failure = "边界连续可见时，沿同一条边界至少描 6 点并覆盖 55% 半径范围；也可描两段各覆盖 10% 半径范围的长边界，每段至少 6 点。只有短片段时需至少 4 段，每段至少 3 点。";
            return [];
        }
        var data = new List<Datum>();
        for (int group = 0; group < complete.Length; group++)
        {
            // The saved order describes the user's editable polyline, not track
            // progression. Build a private radial order for phase unwrapping and
            // local tangent estimates without changing the visible neighbours.
            // Coordinates break exact radius ties deterministically for reversed
            // or two-ended drawing histories containing the same observations.
            var points = complete[group].Points.Distinct().OrderBy(Radius)
                .ThenBy(point => point.X).ThenBy(point => point.Y).ToArray();
            if (points.Length < MinimumPointsPerSegment)
            {
                failure = "每段至少需要 3 个不同位置的点，请沿同一小段边界分散点选。";
                return [];
            }
            var radii = points.Select(p => Math.Sqrt(p.X * p.X + p.Y * p.Y)).ToArray();
            var angles = new double[points.Length];
            angles[0] = Math.Atan2(points[0].Y, points[0].X) + Math.PI / 2;
            // Every explicitly drawn piece is continuous, even when it covers a
            // narrow radial band but a wide angle. Unwrap adjacent observations
            // within this piece; its total angle need not be below a quarter turn.
            // Separate pieces still receive independent half-turn assignments.
            for (int i = 1; i < points.Length; i++)
            {
                double change = Normalize(Math.Atan2(points[i].Y, points[i].X)
                    - Math.Atan2(points[i - 1].Y, points[i - 1].X));
                if (Math.Abs(change) > Math.PI * .95)
                {
                    failure = "边界上存在接近半圈的点间空隙，请在看得清的同一段交界上补点，看不清连接时请新建片段。";
                    return [];
                }
                angles[i] = angles[i - 1] + change;
            }
            double meanRadius = radii.Average(), meanAngle = angles.Average();
            double xx = 0, xy = 0, yy = 0;
            for (int i = 0; i < points.Length; i++)
            {
                double x = radii[i] - meanRadius, y = meanRadius * (angles[i] - meanAngle);
                xx += x * x; xy += x * y; yy += y * y;
            }
            if (radii.Max() - radii.Min() < .03 || Math.Sqrt((xx + yy) / points.Length) < .3)
            {
                failure = "有片段太短，无法判断边界的局部走向，请把这一段的点拉开一些。";
                return [];
            }
            // Principal tangent in a local polar chart. The observed tangent stays
            // fixed across candidates, so winding faster cannot lower its own penalty.
            double tangent = .5 * Math.Atan2(2 * xy, xx - yy);
            double scale = Math.Max(.025, meanRadius * Math.Abs(Math.Cos(tangent)));
            bool localTangent = points.Length >= MinimumContinuousPoints && radii.Max() - radii.Min() >= 1;
            for (int i = 0; i < points.Length; i++)
                data.Add(new(group, radii[i], angles[i], !localTangent ? scale
                    : LocalScale(radii, angles, i), 1d / (complete.Length * points.Length)));
        }
        return data.ToArray();
    }

    private static double LocalScale(double[] radii, double[] angles, int index)
    {
        // A bent or reversing boundary cannot share one straight tangent for its
        // whole length. Fit a local quadratic in observed polar coordinates so the
        // tangent at a visible extremum is radial, rather than the axis of its U shape.
        // The caller retains the stable whole-piece tangent for sub-mm radial spans;
        // a quadratic there would amplify ordinary hand-picking noise.
        var nearby = Enumerable.Range(0, radii.Length).OrderBy(i => Math.Abs(radii[i] - radii[index])).Take(5).ToArray();
        double meanRadius = nearby.Average(i => radii[i]), meanAngle = nearby.Average(i => angles[i]);
        double xx = 0, xxx = 0, xxxx = 0, xy = 0, xxy = 0;
        foreach (int i in nearby)
        {
            double x = radii[i] - meanRadius, y = angles[i] - meanAngle, x2 = x * x;
            xx += x2; xxx += x2 * x; xxxx += x2 * x2; xy += x * y; xxy += x2 * y;
        }
        double curvatureVariance = xxxx - xx * xx / nearby.Length;
        double determinant = xx * curvatureVariance - xxx * xxx;
        double slope = xy / Math.Max(xx, 1e-15);
        if (determinant > Math.Max(1e-20, xx * curvatureVariance * 1e-8))
        {
            double linear = (xy * curvatureVariance - xxy * xxx) / determinant;
            double quadratic = (xx * xxy - xy * xxx) / determinant;
            slope = linear + 2 * quadratic * (radii[index] - meanRadius);
        }
        return Math.Max(.025, radii[index] / Math.Sqrt(1 + radii[index] * radii[index] * slope * slope));
    }

    private static FastGroup[] BuildFastGroups(Datum[] data, int count, double[] origin, double[] inner, double[] outer)
    {
        var result = new FastGroup[count];
        for (int group = 0; group < count; group++)
        {
            var indices = Enumerable.Range(0, data.Length).Where(i => data[i].Segment == group).ToArray();
            double weight = 0, mean = 0, mi = 0, mo = 0;
            foreach (int i in indices)
            {
                double w = data[i].Weight * data[i].Scale * data[i].Scale;
                weight += w; mean += w * (data[i].Angle - origin[i]); mi -= w * inner[i]; mo -= w * outer[i];
            }
            mean /= weight; mi /= weight; mo /= weight;
            double uu = 0, ui = 0, uo = 0, ii = 0, io = 0, oo = 0;
            foreach (int i in indices)
            {
                double w = data[i].Weight * data[i].Scale * data[i].Scale;
                double u = data[i].Angle - origin[i] - mean, v = -inner[i] - mi, z = -outer[i] - mo;
                uu += w * u * u; ui += w * u * v; uo += w * u * z;
                ii += w * v * v; io += w * v * z; oo += w * z * z;
            }
            result[group] = new(weight, mean, mi, mo, uu, ui, uo, ii, io, oo);
        }
        return result;
    }

    private static Datum[] IndependentWeights(Datum[] data, out int independentRegions)
    {
        var groups = data.GroupBy(d => d.Segment).OrderBy(g => g.Key).Select(g => g.ToArray()).ToArray();
        var low = groups.Select(g => g.Min(p => p.Radius)).ToArray();
        var high = groups.Select(g => g.Max(p => p.Radius)).ToArray();
        var parent = Enumerable.Range(0, groups.Length).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i]; return i; }
        for (int a = 0; a < groups.Length; a++)
            for (int b = a + 1; b < groups.Length; b++)
            {
                double overlap = Math.Min(high[a], high[b]) - Math.Max(low[a], low[b]);
                // Merely touching endpoints does not merge consecutive visible pieces.
                // Repeated/overlapping traces do not create independent winding evidence.
                if (overlap > .25 * Math.Min(high[a] - low[a], high[b] - low[b]))
                    parent[Root(b)] = Root(a);
            }
        var regions = Enumerable.Range(0, groups.Length).GroupBy(Root).ToArray();
        independentRegions = regions.Length;
        int regionCount = independentRegions;
        var groupWeight = new double[groups.Length];
        foreach (var region in regions)
            foreach (int group in region)
                groupWeight[group] = 1d / (regionCount * region.Count() * groups[group].Length);
        return data.Select(d => d with { Weight = groupWeight[d.Segment] }).ToArray();
    }

    private static double EffectivePointCount(Datum[] data)
    {
        // Repeating the same clicks with new segment IDs must leave confidence unchanged.
        // Nearby re-clicks are also correlated at photograph/manual-picking resolution.
        var clusters = new List<(double X, double Y, double Weight)>();
        foreach (var point in data)
        {
            double x = point.Radius * Math.Cos(point.Angle), y = point.Radius * Math.Sin(point.Angle);
            int found = clusters.FindIndex(c => (c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y) <= .2 * .2);
            if (found < 0) clusters.Add((x, y, point.Weight));
            else { var cluster = clusters[found]; clusters[found] = (cluster.X, cluster.Y, cluster.Weight + point.Weight); }
        }
        return 1 / clusters.Sum(c => c.Weight * c.Weight);
    }

    private static double FastCost(FastGroup[] groups, double inner, double outer)
    {
        double cosine = 0, sine = 0, totalWeight = 0, local = 0;
        foreach (var g in groups)
        {
            double mean = g.Mean + g.Inner * inner + g.Outer * outer;
            cosine += g.Weight * Math.Cos(2 * mean); sine += g.Weight * Math.Sin(2 * mean);
            totalWeight += g.Weight;
            local += g.UU + 2 * inner * g.UI + 2 * outer * g.UO + inner * inner * g.II
                + 2 * inner * outer * g.IO + outer * outer * g.OO;
        }
        double rotation = .5 * Math.Atan2(sine, cosine), sum = 0, squared = 0;
        foreach (var g in groups)
        {
            double mean = g.Mean + g.Inner * inner + g.Outer * outer;
            mean -= Math.PI * Math.Round((mean - rotation) / Math.PI);
            sum += g.Weight * mean; squared += g.Weight * mean * mean;
        }
        return Math.Max(0, local + squared - sum * sum / totalWeight);
    }

    private static int GridCount(double width, double[] derivative)
        => Math.Max(17, 1 + (int)Math.Ceiling(width * (derivative.Max() - derivative.Min()) / (Math.PI / 4)));

    private static Solution Refine(Solution seed, Datum[] data, double delta,
        Func<double, double, Solution> evaluate, CancellationToken token)
    {
        var current = seed;
        double damping = 1e-4;
        for (int iteration = 0; iteration < 60 && double.IsFinite(current.Cost); iteration++)
        {
            token.ThrowIfCancellationRequested();
            const double h = 1e-6;
            double hi = h, ho = h;
            var a = evaluate(current.Inner + hi, current.Outer);
            var b = evaluate(current.Inner, current.Outer + ho);
            if (!double.IsFinite(a.Cost)) { hi = -h; a = evaluate(current.Inner + hi, current.Outer); }
            if (!double.IsFinite(b.Cost)) { ho = -h; b = evaluate(current.Inner, current.Outer + ho); }
            if (!double.IsFinite(a.Cost) || !double.IsFinite(b.Cost)) break;
            double ii = 0, io = 0, oo = 0, ix = 0, ox = 0;
            for (int i = 0; i < data.Length; i++)
            {
                double error = current.Residuals[i];
                double w = data[i].Weight * (Math.Abs(error) <= delta ? 1 : delta / Math.Abs(error));
                double di = (a.Residuals[i] - error) / hi, dO = (b.Residuals[i] - error) / ho;
                ii += w * di * di; io += w * di * dO; oo += w * dO * dO;
                ix -= w * di * error; ox -= w * dO * error;
            }
            ii *= 1 + damping; oo *= 1 + damping;
            double determinant = ii * oo - io * io;
            if (!double.IsFinite(determinant) || determinant <= 1e-20) break;
            double si = (ix * oo - ox * io) / determinant, so = (ox * ii - ix * io) / determinant;
            if (Math.Max(Math.Abs(si), Math.Abs(so)) < 1e-9) break;
            var proposed = evaluate(current.Inner + si, current.Outer + so);
            if (proposed.Cost < current.Cost)
            {
                double improvement = current.Cost - proposed.Cost;
                current = proposed; damping = Math.Max(1e-10, damping / 3);
                if (improvement < 1e-16 || Math.Max(Math.Abs(si), Math.Abs(so)) < 1e-10) break;
            }
            else { damping *= 8; if (damping > 1e12) break; }
        }
        return current;
    }

    private static double Normalize(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
    private static double Radius(CalibrationPoint point) => Math.Sqrt(point.X * point.X + point.Y * point.Y);
    private static double HalfTurn(double angle) => .5 * Math.Atan2(Math.Sin(2 * angle), Math.Cos(2 * angle));
    private static Solution Invalid(double inner, double outer) => new(inner, outer, 0, double.PositiveInfinity, double.PositiveInfinity, []);
    private static CalibrationFitResult Failed(string warning) => new(false, null, 0, double.NaN, [], [warning]);
}
