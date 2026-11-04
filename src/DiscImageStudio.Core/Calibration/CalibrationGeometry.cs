namespace DiscImageStudio.Core.Calibration;

/// <summary>Continuous track envelope matching the CD, DVD and Blu-ray encoders.</summary>
internal sealed class CalibrationGeometry
{
    private readonly CalibrationParameters _parameters;
    private readonly double _b;
    private readonly double _squaredRange;
    private readonly DvdTrackGeometry? _track;
    private readonly ConstantPitchCalibrationGeometry? _constant;

    internal double RadialGrowthPerRadianMm => _b;
    internal bool IsConstantPitch => _track?.IsConstantPitch ?? true;
    internal bool UsesAreaApproximation => _parameters.Kind == CalibrationDiscKind.Cd && _track is null;

    internal double ArcLengthFactor(double radius)
    {
        double growth = RadialGrowthAtRadius(radius);
        return UsesAreaApproximation ? radius : Math.Sqrt(radius * radius + growth * growth);
    }

    internal CalibrationGeometry(CalibrationParameters parameters)
    {
        _parameters = parameters;
        _squaredRange = parameters.OuterRadiusMm * parameters.OuterRadiusMm
            - parameters.InnerRadiusMm * parameters.InnerRadiusMm;
        _b = _squaredRange / (2 * parameters.TrackLengthMm);
        // CD's historical zero-coefficient model uses the area formula, and BD
        // retains its own constant-pitch inverse. All nonconstant media share
        // the exact arc-length model used by their encoders.
        if (parameters.Kind == CalibrationDiscKind.Dvd
            || parameters.PitchLinear != 0 || parameters.PitchQuadratic != 0 || parameters.PitchCubic != 0)
        {
            _track = DvdTrackGeometry.Create(parameters.InnerRadiusMm, parameters.OuterRadiusMm,
                parameters.TrackLengthMm, parameters.PitchLinear, parameters.PitchQuadratic, parameters.PitchCubic);
            _b = _track.RadialGrowthPerRadianMm;
        }
        else if (parameters.Kind == CalibrationDiscKind.BluRay)
        {
            _constant = new(parameters);
            _b = _constant.Growth;
        }
    }

    internal double RadialGrowthAtRadius(double radius) => _track?.RadialGrowthAtRadius(radius) ?? _b;

    internal (double Minimum, double Maximum) RadialGrowthBounds(double first, double last)
    {
        if (IsConstantPitch) return (_b, _b);
        double width = _parameters.OuterRadiusMm - _parameters.InnerRadiusMm;
        double low = (first - _parameters.InnerRadiusMm) / width;
        double high = (last - _parameters.InnerRadiusMm) / width;
        double a = _parameters.PitchLinear, b = _parameters.PitchQuadratic, c = _parameters.PitchCubic;
        double Q(double x) => 1 + x * (a + x * (b + x * c));
        double minimum = Math.Min(Q(low), Q(high)), maximum = Math.Max(Q(low), Q(high));
        void Include(double x)
        {
            if (x <= low || x >= high || !double.IsFinite(x)) return;
            minimum = Math.Min(minimum, Q(x)); maximum = Math.Max(maximum, Q(x));
        }
        if (c == 0) { if (b != 0) Include(-a / (2 * b)); }
        else
        {
            double discriminant = b * b - 3 * c * a;
            if (discriminant >= 0)
            {
                double root = Math.Sqrt(discriminant);
                Include((-b - root) / (3 * c)); Include((-b + root) / (3 * c));
            }
        }
        return (minimum * _b, maximum * _b);
    }

    internal double ProgressAtRadius(double radius) => Math.Clamp(_constant is not null
        ? _constant.ProgressAtRadius(radius) : _track is null
        ? (radius * radius - _parameters.InnerRadiusMm * _parameters.InnerRadiusMm) / _squaredRange
        : _track.ArcLengthAtRadius(radius) / _parameters.TrackLengthMm, 0, 1);

    internal double AngleAtRadius(double radius) => _track?.AngleAtRadius(radius)
        ?? (radius - _parameters.InnerRadiusMm) / _b;

    internal (double Radius, double Angle) AtProgress(double progress)
    {
        if (_constant is not null)
        {
            if (!_constant.TryAtProgress(progress, out double constantRadius))
                throw new ArgumentException("该进度没有有效的正半径轨迹。");
            return (constantRadius, AngleAtRadius(constantRadius));
        }
        double radius = _track is null
            ? Math.Sqrt(_parameters.InnerRadiusMm * _parameters.InnerRadiusMm + progress * _squaredRange)
            : _track.RadiusAtArcLength(progress * _parameters.TrackLengthMm);
        return (radius, AngleAtRadius(radius));
    }

    /// <summary>Continue the model only while radius and local pitch stay positive.</summary>
    internal bool TryProgressAtRadiusExtended(double radius, out double progress)
    {
        if (_track is not null) return _track.TryProgressAtRadiusExtended(radius, out progress);
        progress = double.NaN;
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(radius * radius)) return false;
        if (_constant is not null)
        {
            progress = _constant.ProgressAtRadius(radius);
            return double.IsFinite(progress);
        }
        progress = (radius - _parameters.InnerRadiusMm) * (radius + _parameters.InnerRadiusMm) / _squaredRange;
        return double.IsFinite(progress);
    }

    internal bool TryAtProgressExtended(double progress, out (double Radius, double Angle) point)
    {
        if (_track is not null) return _track.TryAtProgressExtended(progress, out point);
        point = (double.NaN, double.NaN);
        if (!double.IsFinite(progress)) return false;
        if (_constant is not null)
        {
            if (!_constant.TryAtProgress(progress, out double constantRadius)) return false;
            point = (constantRadius, AngleAtRadius(constantRadius));
            return double.IsFinite(point.Angle);
        }
        double squared = _parameters.InnerRadiusMm * _parameters.InnerRadiusMm + progress * _squaredRange;
        if (!double.IsFinite(squared) || squared <= 0) return false;
        double radius = Math.Sqrt(squared), angle = AngleAtRadius(radius);
        if (!double.IsFinite(angle)) return false;
        point = (radius, angle);
        return true;
    }
}
