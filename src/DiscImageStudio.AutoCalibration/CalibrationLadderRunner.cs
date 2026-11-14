namespace DiscImageStudio.AutoCalibration;

public sealed record CalibrationLadderOptions(
    int MaxTargets = 56,
    int Repetitions = 40,
    double RangeLow = 0.02,
    double RangeHigh = 0.96,
    ulong Seed = 0x1234)
{
    public bool IsValid =>
        MaxTargets is >= 16 and <= 72 && MaxTargets % 4 == 0
        && Repetitions is >= 16 and <= 60 && Repetitions % 4 == 0
        && IsWholePercent(RangeLow, 1, 10)
        && IsWholePercent(RangeHigh, 80, 98)
        && RangeHigh > RangeLow;

    private static bool IsWholePercent(double fraction, int minimum, int maximum)
    {
        double percent = fraction * 100;
        return double.IsFinite(fraction)
            && percent >= minimum
            && percent <= maximum
            && Math.Abs(percent - Math.Round(percent)) < 1e-6;
    }
}

public sealed record CalibrationRungEvent(
    int RungIndex,
    int TotalRungs,
    uint Lba,
    double RadiusMm,
    double PhaseDegrees,
    double SigmaDegrees,
    long UnwrappedTurns,
    double ConstantPitchResidualDegrees,
    double? CubicPitchResidualDegrees,
    double SpindlePeriodMilliseconds,
    double ChiSquaredPerDegreeOfFreedom);

public sealed record CalibrationMeasurementResult(
    CalibrationMedia Media,
    CalibrationSpiralParameters Geometry,
    uint TotalSectors,
    double BasePitchMicrometres,
    double RmsResidualDegrees,
    double ConstantPitchRmsDegrees,
    double SpindlePeriodMilliseconds,
    bool Suspect,
    string? SpecProblem,
    (double InnerRadiusMm, double OuterRadiusMm) MeasuredSpanMm,
    IReadOnlyList<CalibrationRungEvent> Rungs);

public static class CalibrationLadderRunner
{
    private const double MinimumCoherence = 0.98;
    private const double SafeSigmaDegrees = 25;
    private const double ModelFloorDegrees = 3;
    private const int Retries = 4;
    private const int SettleReads = 16;
    private const int SettleRounds = 30;
    private const double SettledCoherence = 0.99;
    private const double PeriodTolerance = 2e-3;
    private const double MisfitFactor = 2.5;

    public static CalibrationMeasurementResult Run(
        ITimedOpticalDrive drive,
        CalibrationMedia media,
        CalibrationLadderOptions options,
        uint? imageSectors = null,
        Action<CalibrationRungEvent>? onRung = null,
        Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "自动标定高级参数超出允许范围。");
        }

        CalibrationLadderShape shape = CalibrationMediaProfile.Ladder(media);
        uint capacity = drive.ReadCapacity(cancellationToken);
        uint total = imageSectors is { } sectors ? Math.Min(capacity, sectors) : capacity;
        if (total < shape.MinimumSectors)
        {
            throw new InvalidDataException($"这张光盘只有 {total} 个扇区，无法标定，请先刻入校准镜像。");
        }

        uint Align(uint lba) => CalibrationMediaProfile.Align(media, lba);
        double CompletionBit(uint lba) => CalibrationMediaProfile.CompletionBit(media, lba);
        double bitLength = CalibrationMediaProfile.BitLengthMm(media);
        uint low = Align((uint)(total * options.RangeLow));
        uint high = Align((uint)(total * options.RangeHigh));
        uint reference = Align((uint)(total * 0.49));
        double referenceBit = CompletionBit(reference);
        onStatus?.Invoke("等待光驱转速稳定");
        try
        {
            drive.SetReadSpeed(shape.ReadSpeedKbps, cancellationToken);
        }
        catch (IOException exception)
        {
            onStatus?.Invoke($"读速没有设上，测量可能不稳定：{exception.Message}");
        }

        var session = new MeasurementSession(
            drive,
            cancellationToken,
            new CalibrationRandom(options.Seed),
            initialPeriodNanoseconds: 25e6,
            total,
            Align(total - Math.Min(total, 2048)),
            shape);
        foreach (uint lba in new[] { 0u, total / 4, total / 2, total / 2 + total / 4 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.Read(Align(lba));
        }

        session.Settle(reference);
        double turnSectors = SectorsPerTurnNear(media, reference);
        uint[] near = shape.NearTurns
            .Select(turn => Align(
                (uint)Math.Round(reference + turn * turnSectors, MidpointRounding.AwayFromZero)
                + CalibrationMediaProfile.BlockSectors(media) / 2))
            .Where(lba => lba >= low && lba < high && Math.Abs((long)lba - reference) >= shape.NearMinimumSectors)
            .ToArray();

        int gridCount = Math.Max(options.MaxTargets * 2, 40);
        var grid = new uint[gridCount];
        for (int i = 0; i < grid.Length; i++)
        {
            ulong span = high > low + CalibrationMediaProfile.BlockSectors(media)
                ? high - CalibrationMediaProfile.BlockSectors(media) - low
                : 0;
            grid[i] = Align(low + (uint)(span * (uint)i / (uint)(gridCount - 1)));
        }

        var points = new List<CalibrationPhasePoint>(options.MaxTargets);
        var rows = new List<CalibrationRungEvent>(options.MaxTargets);
        var rejected = new List<uint>();
        (double U, double V) nominal = NominalUv(media);
        double u = nominal.U;
        double v = nominal.V;
        ConstantPitchFit? model = null;
        while (rows.Count < options.MaxTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint lba;
            if (rows.Count < near.Length)
            {
                lba = near[rows.Count];
            }
            else
            {
                uint? next = PickNext(grid, points, rejected, model, reference, media);
                if (next is null)
                {
                    break;
                }

                lba = next.Value;
            }

            onStatus?.Invoke($"第 {rows.Count + 1} / {options.MaxTargets} 阶");
            Measurement measured;
            try
            {
                measured = session.Measure(reference, lba, options.Repetitions);
            }
            catch (IOException exception) when (exception.Message == "这个位置读取不稳定，已跳过")
            {
                onStatus?.Invoke(exception.Message);
                rejected.Add(lba);
                if (rejected.Count > options.MaxTargets)
                {
                    throw new IOException("被放弃的阶梯太多，光驱对这张光盘读取不稳定。");
                }

                continue;
            }

            double bitPosition = CompletionBit(lba);
            double predicted = model is { } fitted
                ? fitted.PredictDegrees(bitPosition) + LocalCorrection(fitted, points, bitPosition)
                : CalibrationFitter.ConstantPitchAngleDegrees(bitPosition, u, v)
                    - CalibrationFitter.ConstantPitchAngleDegrees(referenceBit, u, v);
            long turns = (long)Math.Round(
                (predicted - measured.Statistics.MeanDegrees) / 360.0,
                MidpointRounding.AwayFromZero);
            double unwrapped = measured.Statistics.MeanDegrees + 360 * turns;
            var point = new CalibrationPhasePoint(lba, bitPosition, unwrapped, measured.Statistics.StandardErrorDegrees);
            points.Add(point);
            bool enough = points.Count >= 8;
            (CalibrationPrior U, CalibrationPrior V)? priors = enough ? null : GetPriors(media);
            ConstantPitchFit fit = CalibrationFitter.FitConstant(points, referenceBit, (u, v), priors);
            double residual = fit.PredictDegrees(bitPosition) - unwrapped;
            if (Math.Abs(residual) > 45 && points.Count > 4)
            {
                points.RemoveAt(points.Count - 1);
                rejected.Add(lba);
                onStatus?.Invoke("跳过一个不可靠的测量点");
                if (rejected.Count > options.MaxTargets)
                {
                    throw new IOException("被放弃的阶梯太多，光驱对这张光盘读取不稳定。");
                }

                continue;
            }

            u = fit.U;
            v = fit.V;
            double radius = bitLength * Math.Sqrt(fit.U * fit.U + fit.V * bitPosition / Math.PI);
            var row = new CalibrationRungEvent(
                rows.Count,
                options.MaxTargets,
                lba,
                radius,
                measured.Statistics.MeanDegrees,
                measured.Statistics.StandardErrorDegrees,
                turns,
                residual,
                null,
                measured.PeriodNanoseconds / 1e6,
                fit.ChiSquaredPerDegreeOfFreedom);
            onRung?.Invoke(row);
            rows.Add(row);
            model = fit;
        }

        if (points.Count < 12)
        {
            throw new InvalidOperationException($"只测到 {points.Count} 阶，不足以拟合。");
        }

        onStatus?.Invoke("拟合三次轨距");
        CalibrationFinalFit final = FinishFit(points, referenceBit, (u, v), total, media);
        rows.Sort((a, b) => a.RadiusMm.CompareTo(b.RadiusMm));
        var finalRows = new CalibrationRungEvent[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            CalibrationRungEvent row = rows[i];
            CalibrationPhasePoint point = final.Points[i];
            finalRows[i] = row with
            {
                ConstantPitchResidualDegrees = final.Constant.PredictDegrees(point.BitPosition) - point.AngleDegrees,
                CubicPitchResidualDegrees = final.Cubic.ResidualsDegrees[i],
            };
        }

        CalibrationSpiralParameters geometry = final.Cubic.Parameters;
        string? specProblem = GetSpecProblem(media, geometry, final.Cubic.BasePitchMicrometres);
        return new(
            media,
            geometry,
            total,
            final.Cubic.BasePitchMicrometres,
            final.Cubic.RmsDegrees,
            final.Constant.RmsDegrees,
            session.PeriodNanoseconds / 1e6,
            final.Cubic.RmsDegrees > 3 * Math.Max(final.NoiseDegrees, 0.5),
            specProblem,
            (finalRows[0].RadiusMm, finalRows[^1].RadiusMm),
            finalRows);
    }

    private static CalibrationFinalFit FinishFit(
        List<CalibrationPhasePoint> points,
        double referenceBit,
        (double U, double V) initial,
        uint total,
        CalibrationMedia media)
    {
        points.Sort((a, b) => a.BitPosition.CompareTo(b.BitPosition));
        double bitLength = CalibrationMediaProfile.BitLengthMm(media);
        uint sectorBits = CalibrationMediaProfile.SectorBits(media);
        ConstantPitchFit constant = CalibrationFitter.FitConstant(points, referenceBit, initial);
        double inner = constant.U * bitLength;
        double bits = (double)total * sectorBits;
        double outer = bitLength * Math.Sqrt(constant.U * constant.U + constant.V * bits / Math.PI);
        CubicPitchFit cubic = CalibrationFitter.FitCubic(
            points, referenceBit, inner, outer, total, sectorBits, bitLength);
        double noise = points.Average(point => point.SigmaDegrees);
        double bar = MisfitFactor * Math.Max(noise, 0.25);
        CubicPitchFit Free(IReadOnlyList<CalibrationPhasePoint> source, CubicPitchFit fit) =>
            CalibrationFitter.FitCubicScanningInnerRadius(
                source,
                referenceBit,
                fit.Parameters.InnerRadiusMm,
                fit.Parameters.OuterRadiusMm,
                total,
                sectorBits,
                bitLength);

        if (cubic.RmsDegrees > bar)
        {
            CubicPitchFit free = Free(points, cubic);
            if (free.RmsDegrees < cubic.RmsDegrees)
            {
                cubic = free;
            }
        }

        for (int attempt = 0; attempt < 8 && cubic.RmsDegrees > bar; attempt++)
        {
            int[] order = Enumerable.Range(0, points.Count)
                .OrderByDescending(index => Math.Abs(cubic.ResidualsDegrees[index]))
                .Take(6)
                .ToArray();
            (int Index, double Turns, CubicPitchFit Fit)? best = null;
            foreach (int index in order)
            {
                foreach (double delta in new[] { 360.0, -360.0 })
                {
                    CalibrationPhasePoint[] trial = points.ToArray();
                    trial[index] = trial[index] with { AngleDegrees = trial[index].AngleDegrees + delta };
                    CubicPitchFit fitted = Free(trial, cubic);
                    if (fitted.RmsDegrees < 0.7 * cubic.RmsDegrees
                        && (best is null || fitted.RmsDegrees < best.Value.Fit.RmsDegrees))
                    {
                        best = (index, delta, fitted);
                    }
                }
            }

            if (best is not { } repair)
            {
                break;
            }

            points[repair.Index] = points[repair.Index] with
            {
                AngleDegrees = points[repair.Index].AngleDegrees + repair.Turns,
            };
            cubic = repair.Fit;
        }

        return new(points, constant, cubic, noise);
    }

    private static double LocalCorrection(
        ConstantPitchFit model,
        IReadOnlyList<CalibrationPhasePoint> points,
        double bitPosition)
    {
        if (points.Count < 12)
        {
            return 0;
        }

        double Miss(CalibrationPhasePoint point) => point.AngleDegrees - model.PredictDegrees(point.BitPosition);
        CalibrationPhasePoint? left = points.Where(point => point.BitPosition <= bitPosition)
            .OrderByDescending(point => point.BitPosition).Cast<CalibrationPhasePoint?>().FirstOrDefault();
        CalibrationPhasePoint? right = points.Where(point => point.BitPosition >= bitPosition)
            .OrderBy(point => point.BitPosition).Cast<CalibrationPhasePoint?>().FirstOrDefault();
        double Line(CalibrationPhasePoint a, CalibrationPhasePoint b) =>
            Miss(a) + (Miss(b) - Miss(a)) * (bitPosition - a.BitPosition) / (b.BitPosition - a.BitPosition);

        if (left is { } a && right is { } b)
        {
            return a.BitPosition == b.BitPosition ? Miss(a) : Line(a, b);
        }

        if (left is { } onlyLeft || right is { } onlyRight)
        {
            CalibrationPhasePoint anchor = left ?? right!.Value;
            double reach = Math.Abs(bitPosition - anchor.BitPosition);
            double side = bitPosition > anchor.BitPosition ? -1 : 1;
            CalibrationPhasePoint? other = points
                .Where(point => (point.BitPosition - anchor.BitPosition) * side >= reach)
                .OrderBy(point => Math.Abs(point.BitPosition - anchor.BitPosition))
                .Cast<CalibrationPhasePoint?>()
                .FirstOrDefault();
            return other is { } candidate ? Line(anchor, candidate) : Miss(anchor);
        }

        return 0;
    }

    private static uint? PickNext(
        IReadOnlyList<uint> grid,
        IReadOnlyList<CalibrationPhasePoint> points,
        IReadOnlyList<uint> rejected,
        ConstantPitchFit? model,
        uint reference,
        CalibrationMedia media)
    {
        if (model is null)
        {
            return null;
        }

        uint spacing = CalibrationMediaProfile.Ladder(media).MinimumSpacingSectors;
        uint[] free = grid.Where(lba =>
            points.All(point => Math.Abs((long)point.Lba - lba) >= spacing)
            && !rejected.Contains(lba)).ToArray();
        if (free.Length == 0)
        {
            return null;
        }

        double scale = Math.Max(Math.Sqrt(model.ChiSquaredPerDegreeOfFreedom), 1);
        double Sigma(uint lba) =>
            model.PredictSigmaDegrees(CalibrationMediaProfile.CompletionBit(media, lba)) * scale
            + ModelFloorDegrees;
        uint Nearest(uint lba) => points.Select(point => (uint)Math.Abs((long)point.Lba - lba))
            .DefaultIfEmpty(uint.MaxValue).Min();
        uint? edge = free.Where(lba => Sigma(lba) > SafeSigmaDegrees).OrderBy(Nearest)
            .Select(lba => (uint?)lba).FirstOrDefault();
        if (edge is { } unsure)
        {
            bool SameSide(uint lba) => (lba > reference) == (unsure > reference);
            uint reach = points.Where(point => SameSide(point.Lba))
                .Select(point => (uint)Math.Abs((long)point.Lba - reference))
                .DefaultIfEmpty(0u)
                .Max();
            uint step = Math.Max(Nearest(unsure), reach / 3);
            uint? target = free.Where(lba => SameSide(lba)
                    && Math.Abs((long)lba - reference) > reach
                    && Math.Abs((long)lba - reference) <= reach + step)
                .OrderByDescending(lba => Math.Abs((long)lba - reference))
                .Select(lba => (uint?)lba).FirstOrDefault();
            return target ?? unsure;
        }

        return free.OrderByDescending(Nearest).First();
    }

    private static (double U, double V) NominalUv(CalibrationMedia media)
    {
        CalibrationLadderShape shape = CalibrationMediaProfile.Ladder(media);
        double bitLength = CalibrationMediaProfile.BitLengthMm(media);
        return (shape.NominalInnerRadiusMm / bitLength, shape.NominalPitchMm / bitLength);
    }

    private static (CalibrationPrior U, CalibrationPrior V) GetPriors(CalibrationMedia media)
    {
        CalibrationLadderShape shape = CalibrationMediaProfile.Ladder(media);
        double bitLength = CalibrationMediaProfile.BitLengthMm(media);
        return (
            new(shape.NominalInnerRadiusMm / bitLength, shape.InnerRadiusSigmaMm / bitLength),
            new(shape.NominalPitchMm / bitLength, shape.PitchSigmaMm / bitLength));
    }

    private static double SectorsPerTurnNear(CalibrationMedia media, uint lba)
    {
        (double u, double v) = NominalUv(media);
        double bit = CalibrationMediaProfile.CompletionBit(media, lba);
        double radiusBits = Math.Sqrt(u * u + v * bit / Math.PI);
        return 2 * Math.PI * radiusBits / CalibrationMediaProfile.SectorBits(media);
    }

    private static string? GetSpecProblem(
        CalibrationMedia media,
        CalibrationSpiralParameters geometry,
        double basePitchMicrometres)
    {
        (double PitchLow, double PitchHigh, double InnerLow, double InnerHigh) bounds = media == CalibrationMedia.Dvd
            ? (0.71, 0.77, 23.6, 24.6)
            : (1.3, 1.8, 22.5, 27);
        string family = media == CalibrationMedia.Dvd ? "DVD" : "CD";
        if (basePitchMicrometres < bounds.PitchLow || basePitchMicrometres > bounds.PitchHigh)
        {
            return $"测出的轨距是 {basePitchMicrometres:F4} µm，超出 {family} 的范围（{bounds.PitchLow}–{bounds.PitchHigh} µm），测量可能出错了。请重新测量这张盘。";
        }

        if (geometry.InnerRadiusMm < bounds.InnerLow || geometry.InnerRadiusMm > bounds.InnerHigh)
        {
            return $"测出的内圈半径是 {geometry.InnerRadiusMm:F4} mm，超出 {family} 的范围（{bounds.InnerLow}–{bounds.InnerHigh} mm），测量可能出错了。请重新测量这张盘。";
        }

        if (media == CalibrationMedia.Dvd
            && (geometry.OuterRadiusMm < 57.3 || geometry.OuterRadiusMm > 58.5))
        {
            return $"测出的外圈半径是 {geometry.OuterRadiusMm:F4} mm，超出 DVD 的范围（57.3–58.5 mm），测量可能出错了。请重新测量这张盘。";
        }

        return null;
    }

    private sealed record Measurement(CircularPhaseStatistics Statistics, double PeriodNanoseconds);

    private sealed record CalibrationFinalFit(
        List<CalibrationPhasePoint> Points,
        ConstantPitchFit Constant,
        CubicPitchFit Cubic,
        double NoiseDegrees);

    private sealed class MeasurementSession(
        ITimedOpticalDrive drive,
        CancellationToken cancellationToken,
        CalibrationRandom random,
        double initialPeriodNanoseconds,
        uint total,
        uint farLba,
        CalibrationLadderShape shape)
    {
        public double PeriodNanoseconds { get; private set; } = initialPeriodNanoseconds;

        public double Read(uint lba)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double nanoseconds = random.NextUnit() * PeriodNanoseconds;
            drive.Wait(TimeSpan.FromTicks(checked((long)(nanoseconds / 100))), cancellationToken);
            bool fua = drive.SupportsForceUnitAccess;
            if (!fua)
            {
                uint far = lba > total / 2 ? farLba / 4 : farLba;
                _ = drive.ReadTimed(far, forceUnitAccess: false, cancellationToken);
            }

            return drive.ReadTimed(lba, fua, cancellationToken).CompletionNanoseconds;
        }

        public Measurement Measure(uint reference, uint target, int repetitions)
        {
            for (int attempt = 0; attempt < Retries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var references = new double[repetitions];
                var targets = new double[repetitions];
                for (int i = 0; i < repetitions; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    references[i] = Read(reference);
                    targets[i] = Read(target);
                }

                SpindlePeriodFit fit = SpindlePhaseMath.RefinePeriod(references, PeriodNanoseconds, 0.05);
                if (fit.Coherence < MinimumCoherence)
                {
                    Settle(reference);
                    continue;
                }

                PeriodNanoseconds = fit.PeriodNanoseconds;
                double[] phases = targets.Select(time => SpindlePhaseMath.PhaseDegrees(time, fit)).ToArray();
                return new(SpindlePhaseMath.CircularStatistics(phases), fit.PeriodNanoseconds);
            }

            throw new IOException("这个位置读取不稳定，已跳过");
        }

        public void Settle(uint lba)
        {
            PeriodNanoseconds = shape.SlowestTurnNanoseconds;
            double? previous = null;
            for (int round = 0; round < SettleRounds; round++)
            {
                var timestamps = new double[SettleReads];
                for (int i = 0; i < timestamps.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    timestamps[i] = Read(lba);
                }

                SpindlePeriodFit fit = SpindlePhaseMath.EstimatePeriod(timestamps, shape.RpmLow, shape.RpmHigh);
                if (fit.Coherence <= SettledCoherence)
                {
                    previous = null;
                    continue;
                }

                if (previous is { } period
                    && Math.Abs(fit.PeriodNanoseconds / period - 1) < PeriodTolerance)
                {
                    PeriodNanoseconds = fit.PeriodNanoseconds;
                    return;
                }

                previous = fit.PeriodNanoseconds;
            }

            throw new IOException("光驱转速一直不稳定，无法标定。请重试，或换一台光驱。");
        }
    }
}
