namespace DiscImageStudio.Core.Calibration;

/// <summary>The observed bend from the mark's inner edge toward its outer edge, in photo coordinates (Y down).</summary>
public enum CalibrationArcBendDirection { Unknown, Clockwise, Counterclockwise }

/// <summary>A complete observed short mark. SpanDegrees includes the mark's original angular width.
/// An apparent radius is optional and only checks that the selected mark occupies the expected band.</summary>
public sealed record CalibrationRadialArcObservation(int BandIndex, double SpanDegrees,
    CalibrationArcBendDirection Direction, bool IsComplete, bool SingleTurnConfirmed,
    bool MonotonicConfirmed, double? ApparentRadiusMm = null);

public enum CalibrationRadialArcFitStatus { Success, InsufficientData, Ambiguous, Inconsistent }

public sealed record CalibrationRadialArcFitResult(CalibrationRadialArcFitStatus Status,
    CalibrationParameters? Corrected, double RmsDegrees, string Message)
{
    public bool Succeeded => Status == CalibrationRadialArcFitStatus.Success && Corrected is not null;
}

/// <summary>Fits CD endpoint radii from signed, complete short-arc spans. The unknown rotation
/// of the photograph cancels within each mark. Marks that wrap a whole turn or reverse direction
/// within the mark do not have an unambiguous span and must not be submitted as complete.</summary>
public static class CalibrationRadialArcFitter
{
    private readonly record struct Datum(double Q0, double Q1, double GeneratedAngle,
        double IntrinsicAngle, double MeasuredAngle, int Sign, double MeanRadius, double? ApparentRadius);

    public static CalibrationRadialArcFitResult Fit(CalibrationTarget target,
        IReadOnlyList<CalibrationRadialArcObservation> observations, double angularToleranceDegrees = 3,
        double radiusToleranceMm = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();
        if (!target.HasRadialArcDashes || target.Parameters.Kind != CalibrationDiscKind.Cd)
            throw DiagnosticMessage.Argument($"当前标靶没有用于测量的短圆弧。", nameof(target));
        if (!double.IsFinite(angularToleranceDegrees) || angularToleranceDegrees <= 0 || angularToleranceDegrees > 30)
            throw new ArgumentOutOfRangeException(nameof(angularToleranceDegrees));
        if (!double.IsFinite(radiusToleranceMm) || radiusToleranceMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(radiusToleranceMm));
        if (observations.Count > target.RadialArcDashes.Count || observations.Any(o => o is null)
            || observations.Select(o => o.BandIndex).Distinct().Count() != observations.Count)
            throw DiagnosticMessage.Argument($"短圆弧编号无效或重复。", nameof(observations));

        var geometry = new CalibrationGeometry(target.Parameters);
        var data = new List<Datum>();
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)observation.BandIndex >= (uint)target.RadialArcDashes.Count
                || !Enum.IsDefined(observation.Direction) || !double.IsFinite(observation.SpanDegrees)
                || observation.SpanDegrees <= 0 || observation.SpanDegrees >= 360
                || observation.ApparentRadiusMm is double p && (!double.IsFinite(p) || p < 7.5 || p > 60))
                throw DiagnosticMessage.Argument($"短圆弧测量值无效。", nameof(observations));
            if (!observation.IsComplete || !observation.SingleTurnConfirmed || !observation.MonotonicConfirmed)
                continue;
            var arc = target.RadialArcDashes[observation.BandIndex];
            double measured = observation.SpanDegrees * Math.PI / 180;
            double intrinsic = 2 * arc.HalfAngleRadians;
            double tolerance = angularToleranceDegrees * Math.PI / 180;
            if (measured + tolerance < intrinsic)
                return Failure(CalibrationRadialArcFitStatus.Inconsistent, "测得的短弧短于图案本身，请检查是否标到了完整两端。");
            if (observation.Direction == CalibrationArcBendDirection.Unknown && measured - intrinsic > tolerance)
                return Failure(CalibrationRadialArcFitStatus.Ambiguous, "请确认每一段从内向外的弯曲方向。仅凭弧长可能有多个解。");
            double q0 = geometry.ProgressAtRadius(arc.InnerRadiusMm);
            double q1 = geometry.ProgressAtRadius(arc.OuterRadiusMm);
            double generated = geometry.AngleAtRadius(arc.OuterRadiusMm) - geometry.AngleAtRadius(arc.InnerRadiusMm);
            int sign = observation.Direction == CalibrationArcBendDirection.Unknown ? 0
                : observation.Direction == CalibrationArcBendDirection.Clockwise ? 1 : -1;
            double observed = generated + sign * Math.Max(0, measured - intrinsic);
            if (q1 <= q0 || observed <= 0)
                return Failure(CalibrationRadialArcFitStatus.Inconsistent, "短弧转角无法对应有效的盘片半径。");
            // For a CD with constant linear speed, Δθ = L·Δq / mean(r0,r1).
            double mean = target.Parameters.TrackLengthMm * (q1 - q0) / observed;
            data.Add(new(q0, q1, generated, intrinsic, measured, sign, mean, observation.ApparentRadiusMm));
        }

        if (data.Count < 2 || data.Max(d => d.Q0) - data.Min(d => d.Q1) < .20)
            return Failure(CalibrationRadialArcFitStatus.InsufficientData, "请确认至少两段相距较远的完整短弧，保留中间的弯曲方向。");

        // Fit both endpoint radii from the mean physical radius of each progress interval.
        // Start with the squared-radius linear solution, then use the exact interval average.
        double sx = 0, sy = 0, xx = 0, xy = 0;
        foreach (var d in data)
        {
            double q = (d.Q0 + d.Q1) / 2, r2 = d.MeanRadius * d.MeanRadius;
            sx += q; sy += r2; xx += q * q; xy += q * r2;
        }
        double determinant = data.Count * xx - sx * sx;
        if (determinant <= 1e-12) return Failure(CalibrationRadialArcFitStatus.InsufficientData, "短弧位置过于集中。请测量靠内和靠外的短弧。");
        double slope = (data.Count * xy - sx * sy) / determinant;
        double intercept = (sy - slope * sx) / data.Count;
        if (intercept <= 0 || intercept + slope <= 0) return Failure(CalibrationRadialArcFitStatus.Inconsistent, "短弧无法对应有效的盘片半径。");
        double inner = Math.Sqrt(intercept), outer = Math.Sqrt(intercept + slope);
        for (int iteration = 0; iteration < 12; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double aa = 0, ab = 0, bb = 0, ar = 0, br = 0;
            foreach (var d in data)
            {
                double r0 = Math.Sqrt((1 - d.Q0) * inner * inner + d.Q0 * outer * outer);
                double r1 = Math.Sqrt((1 - d.Q1) * inner * inner + d.Q1 * outer * outer);
                double residual = (r0 + r1) / 2 - d.MeanRadius;
                double a = inner * ((1 - d.Q0) / r0 + (1 - d.Q1) / r1) / 2;
                double b = outer * (d.Q0 / r0 + d.Q1 / r1) / 2;
                aa += a * a; ab += a * b; bb += b * b; ar += a * residual; br += b * residual;
            }
            double det = aa * bb - ab * ab;
            if (!double.IsFinite(det) || det <= 1e-14) return Failure(CalibrationRadialArcFitStatus.Inconsistent, "短弧无法稳定确定两个半径。");
            double di = (bb * ar - ab * br) / det, dout = (aa * br - ab * ar) / det;
            inner -= di; outer -= dout;
            if (!double.IsFinite(inner) || !double.IsFinite(outer) || inner < 7.5 || outer > 60 || outer <= inner)
                return Failure(CalibrationRadialArcFitStatus.Inconsistent, "短弧结果超出实体盘片范围，请检查方向和两端。");
            if (Math.Max(Math.Abs(di), Math.Abs(dout)) < 1e-10) break;
        }

        double squaredError = 0, maxError = 0;
        foreach (var d in data)
        {
            double r0 = Math.Sqrt((1 - d.Q0) * inner * inner + d.Q0 * outer * outer);
            double r1 = Math.Sqrt((1 - d.Q1) * inner * inner + d.Q1 * outer * outer);
            if (d.ApparentRadius is double apparent && Math.Abs((r0 + r1) / 2 - apparent) > radiusToleranceMm)
                return Failure(CalibrationRadialArcFitStatus.Inconsistent, "短弧位置与照片不一致，请检查圈号或照片矫正。");
            double actual = target.Parameters.TrackLengthMm * (d.Q1 - d.Q0) / ((r0 + r1) / 2);
            double bend = actual - d.GeneratedAngle;
            double g0 = geometry.AtProgress(d.Q0).Radius, g1 = geometry.AtProgress(d.Q1).Radius;
            double derivative0 = target.Parameters.TrackLengthMm * (1 / r0 - 1 / g0) * (d.Q1 - d.Q0);
            double derivative1 = target.Parameters.TrackLengthMm * (1 / r1 - 1 / g1) * (d.Q1 - d.Q0);
            double bendTolerance = angularToleranceDegrees * Math.PI / 180;
            if (derivative0 * derivative1 < 0 && Math.Min(Math.Abs(derivative0), Math.Abs(derivative1)) > bendTolerance)
                return Failure(CalibrationRadialArcFitStatus.Ambiguous, "拟合结果显示短弧内部可能折返，无法从总角度可靠确定半径。");
            double error = (d.IntrinsicAngle + (d.Sign == 0 ? Math.Abs(bend) : d.Sign * bend) - d.MeasuredAngle) * 180 / Math.PI;
            squaredError += error * error;
            maxError = Math.Max(maxError, Math.Abs(error));
        }
        double rms = Math.Sqrt(squaredError / data.Count);
        if (!double.IsFinite(rms) || maxError > angularToleranceDegrees)
            return Failure(CalibrationRadialArcFitStatus.Inconsistent, "各段短弧不一致，请重新核对完整轮廓和弯曲方向。");
        var result = target.Parameters with { InnerRadiusMm = inner, OuterRadiusMm = outer };
        return new(CalibrationRadialArcFitStatus.Success, result, rms, "");
    }

    private static CalibrationRadialArcFitResult Failure(CalibrationRadialArcFitStatus status, string message) =>
        new(status, null, double.PositiveInfinity, message);
}
