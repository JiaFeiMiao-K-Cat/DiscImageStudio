namespace DiscImageStudio.AutoCalibration;

public readonly record struct CalibrationPhasePoint(uint Lba, double BitPosition, double AngleDegrees, double SigmaDegrees);
public readonly record struct CalibrationPrior(double Value, double Sigma);

public sealed record ConstantPitchFit(
    double U,
    double V,
    double OffsetDegrees,
    double ReferenceBit,
    double RmsDegrees,
    double ChiSquaredPerDegreeOfFreedom,
    double[,] Covariance)
{
    public double PredictDegrees(double bitPosition) =>
        CalibrationFitter.ConstantPitchAngleDegrees(bitPosition, U, V)
        - CalibrationFitter.ConstantPitchAngleDegrees(ReferenceBit, U, V)
        + OffsetDegrees;

    public double PredictSigmaDegrees(double bitPosition)
    {
        (double a, double b) = CalibrationFitter.ConstantPitchGradient(bitPosition, U, V);
        (double e, double f) = CalibrationFitter.ConstantPitchGradient(ReferenceBit, U, V);
        double[] gradient = [a - e, b - f, 1];
        double variance = 0;
        for (int i = 0; i < gradient.Length; i++)
        {
            for (int j = 0; j < gradient.Length; j++)
            {
                variance += gradient[i] * Covariance[i, j] * gradient[j];
            }
        }

        return Math.Sqrt(Math.Max(variance, 0));
    }
}

public sealed record CubicPitchFit(
    CalibrationSpiralParameters Parameters,
    double BasePitchMicrometres,
    double OffsetDegrees,
    double RmsDegrees,
    IReadOnlyList<double> ResidualsDegrees);

public static class CalibrationFitter
{
    public const double CubicLambda = 1e-3;

    public static double ConstantPitchAngleDegrees(double bitPosition, double u, double v) =>
        2 * Math.PI / v
        * (Math.Sqrt(u * u + v * bitPosition / Math.PI) - u)
        * 180.0 / Math.PI;

    internal static (double U, double V) ConstantPitchGradient(double bitPosition, double u, double v)
    {
        double root = Math.Sqrt(u * u + v * bitPosition / Math.PI);
        double du = 2 * Math.PI / v * (u / root - 1);
        double dv = -2 * Math.PI / (v * v) * (root - u) + 2 * Math.PI / v * bitPosition / (2 * Math.PI * root);
        return (du * 180.0 / Math.PI, dv * 180.0 / Math.PI);
    }

    public static ConstantPitchFit FitConstant(
        IReadOnlyList<CalibrationPhasePoint> points,
        double referenceBit,
        (double U, double V) initial,
        (CalibrationPrior U, CalibrationPrior V)? priors = null)
    {
        if (points.Count == 0 || (points.Count < 3 && priors is null))
        {
            throw new InvalidOperationException("点数不足，无法拟合。");
        }

        double[] Residual(double[] x)
        {
            double baseline = ConstantPitchAngleDegrees(referenceBit, x[0], x[1]);
            double[] values = new double[points.Count + (priors is null ? 0 : 2)];
            for (int i = 0; i < points.Count; i++)
            {
                CalibrationPhasePoint point = points[i];
                values[i] = (ConstantPitchAngleDegrees(point.BitPosition, x[0], x[1])
                    - baseline + x[2] - point.AngleDegrees) / point.SigmaDegrees;
            }

            if (priors is { } prior)
            {
                values[^2] = (x[0] - prior.U.Value) / prior.U.Sigma;
                values[^1] = (x[1] - prior.V.Value) / prior.V.Sigma;
            }

            return values;
        }

        double[][] Jacobian(double[] x)
        {
            (double baseU, double baseV) = ConstantPitchGradient(referenceBit, x[0], x[1]);
            var rows = new List<double[]>(points.Count + (priors is null ? 0 : 2));
            foreach (CalibrationPhasePoint point in points)
            {
                (double u, double v) = ConstantPitchGradient(point.BitPosition, x[0], x[1]);
                rows.Add([(u - baseU) / point.SigmaDegrees, (v - baseV) / point.SigmaDegrees, 1 / point.SigmaDegrees]);
            }

            if (priors is { } prior)
            {
                rows.Add([1 / prior.U.Sigma, 0, 0]);
                rows.Add([0, 1 / prior.V.Sigma, 0]);
            }

            return rows.ToArray();
        }

        LmOutcome outcome = LevenbergMarquardt(
            [initial.U, initial.V, 0],
            Residual,
            Jacobian);
        double[] residual = Residual(outcome.Parameters);
        double chiSquared = residual.Take(points.Count).Sum(value => value * value);
        double baseline = ConstantPitchAngleDegrees(referenceBit, outcome.Parameters[0], outcome.Parameters[1]);
        double rms = Math.Sqrt(points.Sum(point =>
        {
            double error = ConstantPitchAngleDegrees(point.BitPosition, outcome.Parameters[0], outcome.Parameters[1])
                - baseline + outcome.Parameters[2] - point.AngleDegrees;
            return error * error;
        }) / points.Count);
        double degrees = Math.Max(points.Count - 3, 1);
        return new(
            outcome.Parameters[0],
            outcome.Parameters[1],
            outcome.Parameters[2],
            referenceBit,
            rms,
            chiSquared / degrees,
            outcome.Covariance);
    }

    public static CubicPitchFit FitCubic(
        IReadOnlyList<CalibrationPhasePoint> points,
        double referenceBit,
        double innerRadiusMm,
        double initialOuterRadiusMm,
        uint totalSectors,
        uint sectorBits,
        double bitLengthMm,
        double lambda = CubicLambda)
    {
        CalibrationSpiralParameters Create(double[] x) =>
            new(totalSectors, sectorBits, bitLengthMm, innerRadiusMm, x[0], x[1], x[2], x[3]);

        double regularization = Math.Sqrt(lambda) / 1e-4;
        double[] Residual(double[] x)
        {
            CalibrationSpiral spiral;
            try
            {
                spiral = CalibrationSpiral.Create(Create(x), gridSize: 8_001);
            }
            catch (ArgumentException)
            {
                return Enumerable.Repeat(double.PositiveInfinity, points.Count + 3).ToArray();
            }

            double baseline = spiral.AngleAtBit(referenceBit);
            double[] values = new double[points.Count + 3];
            for (int i = 0; i < points.Count; i++)
            {
                values[i] = (spiral.AngleAtBit(points[i].BitPosition) - baseline) * 180 / Math.PI
                    + x[4] - points[i].AngleDegrees;
            }

            values[^3] = x[1] * regularization;
            values[^2] = x[2] * regularization;
            values[^1] = x[3] * regularization;
            return values;
        }

        double[][] Jacobian(double[] x)
        {
            double[] steps = [1e-7, 1e-7, 1e-7, 1e-7, 1e-2];
            double[][] positive = new double[5][];
            double[][] negative = new double[5][];
            for (int column = 0; column < 5; column++)
            {
                double[] a = (double[])x.Clone();
                double[] b = (double[])x.Clone();
                a[column] += steps[column];
                b[column] -= steps[column];
                positive[column] = Residual(a);
                negative[column] = Residual(b);
            }

            var rows = new double[points.Count + 3][];
            for (int row = 0; row < rows.Length; row++)
            {
                rows[row] = new double[5];
                for (int column = 0; column < 5; column++)
                {
                    rows[row][column] = (positive[column][row] - negative[column][row]) / (2 * steps[column]);
                }
            }

            return rows;
        }

        LmOutcome outcome = LevenbergMarquardt(
            [initialOuterRadiusMm, 0, 0, 0, 0],
            Residual,
            Jacobian);
        CalibrationSpiralParameters parameters = Create(outcome.Parameters);
        CalibrationSpiral fullSpiral = CalibrationSpiral.Create(parameters);
        double baseAngle = fullSpiral.AngleAtBit(referenceBit);
        double[] raw = points
            .Select(point => (fullSpiral.AngleAtBit(point.BitPosition) - baseAngle) * 180 / Math.PI - point.AngleDegrees)
            .ToArray();
        double offset = raw.Average();
        double[] residuals = raw.Select(value => value - offset).ToArray();
        double rms = Math.Sqrt(residuals.Sum(value => value * value) / residuals.Length);
        return new(parameters, fullSpiral.P0Mm * 1000, -offset, rms, residuals);
    }

    public static CubicPitchFit FitCubicScanningInnerRadius(
        IReadOnlyList<CalibrationPhasePoint> points,
        double referenceBit,
        double innerRadiusMm,
        double outerRadiusMm,
        uint totalSectors,
        uint sectorBits,
        double bitLengthMm,
        double lambda = CubicLambda)
    {
        CubicPitchFit At(double innerRadius)
        {
            double outer = Math.Sqrt(outerRadiusMm * outerRadiusMm
                + innerRadius * innerRadius - innerRadiusMm * innerRadiusMm);
            return FitCubic(points, referenceBit, innerRadius, outer, totalSectors, sectorBits, bitLengthMm, lambda);
        }

        const int steps = 9;
        double step = 0.36 / steps;
        double bestInner = innerRadiusMm;
        CubicPitchFit best = At(innerRadiusMm);
        for (int k = -steps; k <= steps; k++)
        {
            if (k == 0)
            {
                continue;
            }

            double candidateInner = innerRadiusMm + k * step;
            CubicPitchFit candidate = At(candidateInner);
            if (candidate.RmsDegrees < best.RmsDegrees)
            {
                (bestInner, best) = (candidateInner, candidate);
            }
        }

        double low = bestInner - step;
        double high = bestInner + step;
        for (int i = 0; i < 24; i++)
        {
            double a = low + (high - low) * 0.382;
            double b = low + (high - low) * 0.618;
            CubicPitchFit fa = At(a);
            CubicPitchFit fb = At(b);
            if (fa.RmsDegrees < fb.RmsDegrees)
            {
                high = b;
                if (fa.RmsDegrees < best.RmsDegrees)
                {
                    (bestInner, best) = (a, fa);
                }
            }
            else
            {
                low = a;
                if (fb.RmsDegrees < best.RmsDegrees)
                {
                    (bestInner, best) = (b, fb);
                }
            }
        }

        return best;
    }

    private static LmOutcome LevenbergMarquardt(
        double[] initial,
        Func<double[], double[]> residual,
        Func<double[], double[][]> jacobian)
    {
        static double Cost(IReadOnlyList<double> values) => values.Sum(value => value * value);

        double[] x = initial;
        double[] r = residual(x);
        double cost = Cost(r);
        double mu = 1e-8;
        double[][]? lastMatrix = null;
        double[]? lastScale = null;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            double[][] j = jacobian(x);
            int columns = x.Length;
            double[] scale = Enumerable.Range(0, columns)
                .Select(column => Math.Max(Math.Sqrt(j.Sum(row => row[column] * row[column])), 1e-300))
                .ToArray();
            bool improved = false;
            while (mu < 1e12)
            {
                double[][] a = j.Select(row => row.Zip(scale, (value, s) => value / s).ToArray()).ToArray();
                double[] b = r.Select(value => -value).ToArray();
                for (int column = 0; column < columns; column++)
                {
                    double[] row = new double[columns];
                    row[column] = Math.Sqrt(mu);
                    a = [.. a, row];
                    b = [.. b, 0];
                }

                double[] y = QrSolve(a, b).Solution;
                double[] trial = x.Zip(y.Zip(scale, (delta, s) => delta / s), (value, delta) => value + delta).ToArray();
                double[] trialResidual = residual(trial);
                double trialCost = Cost(trialResidual);
                if (double.IsFinite(trialCost) && trialCost < cost)
                {
                    bool small = cost - trialCost <= 1e-12 * cost;
                    (x, r, cost) = (trial, trialResidual, trialCost);
                    mu = Math.Max(mu * 0.2, 1e-14);
                    improved = !small;
                    break;
                }

                mu *= 8;
            }

            lastMatrix = j.Select(row => row.Zip(scale, (value, s) => value / s).ToArray()).ToArray();
            lastScale = scale;
            if (!improved)
            {
                break;
            }
        }

        if (lastMatrix is null || lastScale is null)
        {
            throw new InvalidOperationException("非线性拟合没有执行。");
        }

        double[][] upper = QrSolve(lastMatrix, new double[lastMatrix.Length]).UpperTriangular;
        double[,] covariance = InverseRtr(upper);
        for (int row = 0; row < x.Length; row++)
        {
            for (int column = 0; column < x.Length; column++)
            {
                covariance[row, column] /= lastScale[row] * lastScale[column];
            }
        }

        return new(x, covariance);
    }

    private static (double[] Solution, double[][] UpperTriangular) QrSolve(double[][] matrix, double[] vector)
    {
        double[][] a = matrix.Select(row => (double[])row.Clone()).ToArray();
        double[] b = (double[])vector.Clone();
        int rows = a.Length;
        int columns = a[0].Length;
        for (int k = 0; k < columns; k++)
        {
            double norm = Math.Sqrt(Enumerable.Range(k, rows - k).Sum(row => a[row][k] * a[row][k]));
            if (norm == 0)
            {
                continue;
            }

            double alpha = a[k][k] > 0 ? -norm : norm;
            double[] v = Enumerable.Range(k, rows - k).Select(row => a[row][k]).ToArray();
            v[0] -= alpha;
            double vv = v.Sum(value => value * value);
            if (vv == 0)
            {
                continue;
            }

            for (int column = k; column < columns; column++)
            {
                double dot = 2 * v.Select((value, i) => value * a[k + i][column]).Sum() / vv;
                for (int i = 0; i < v.Length; i++)
                {
                    a[k + i][column] -= dot * v[i];
                }
            }

            double vectorDot = 2 * v.Select((value, i) => value * b[k + i]).Sum() / vv;
            for (int i = 0; i < v.Length; i++)
            {
                b[k + i] -= vectorDot * v[i];
            }
        }

        double[] solution = new double[columns];
        for (int row = columns - 1; row >= 0; row--)
        {
            double sum = 0;
            for (int column = row + 1; column < columns; column++)
            {
                sum += a[row][column] * solution[column];
            }

            solution[row] = Math.Abs(a[row][row]) < 1e-300 ? 0 : (b[row] - sum) / a[row][row];
        }

        double[][] upper = Enumerable.Range(0, columns)
            .Select(row => a[row].Take(columns).ToArray())
            .ToArray();
        return (solution, upper);
    }

    private static double[,] InverseRtr(double[][] r)
    {
        int n = r.Length;
        var inverse = new double[n, n];
        for (int column = 0; column < n; column++)
        {
            for (int row = column; row >= 0; row--)
            {
                double sum = 0;
                for (int j = row + 1; j <= column; j++)
                {
                    sum += r[row][j] * inverse[j, column];
                }

                inverse[row, column] = ((row == column ? 1 : 0) - sum) / r[row][row];
            }
        }

        var result = new double[n, n];
        for (int row = 0; row < n; row++)
        {
            for (int column = 0; column < n; column++)
            {
                for (int k = 0; k < n; k++)
                {
                    result[row, column] += inverse[row, k] * inverse[column, k];
                }
            }
        }

        return result;
    }

    private sealed record LmOutcome(double[] Parameters, double[,] Covariance);
}
