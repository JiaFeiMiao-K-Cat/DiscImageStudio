namespace DiscImageStudio.Core.Calibration;

public sealed record CalibrationArcLengthMark(int BandIndex, IReadOnlyList<CalibrationPoint> Points,
    CalibrationArcBendDirection Direction = CalibrationArcBendDirection.Unknown, bool Complete = false)
{
    // The middle click identifies the visible path, including an arc longer than half a turn.
    // This is the order of the clicks on the photograph, independent of the radial bend direction.
    public static bool TrySpan(IReadOnlyList<CalibrationPoint> points, out double signedDegrees)
    {
        signedDegrees = 0;
        if (points.Count != 3) return false;
        double Angle(int i) => Math.Atan2(points[i].Y, points[i].X) * 180 / Math.PI;
        double start = Angle(0);
        double middle = (Angle(1) - start + 360) % 360;
        double end = (Angle(2) - start + 360) % 360;
        if (!double.IsFinite(middle + end) || Math.Min(end, 360 - end) < .01
            || Math.Min(middle, 360 - middle) < .01 || Math.Abs(middle - end) < .01) return false;
        signedDegrees = middle < end ? end : end - 360;
        return true;
    }
}

public sealed record CalibrationArcLengthSession(int SelectedBandIndex, IReadOnlyList<CalibrationArcLengthMark> Marks)
{
    internal void Validate(CalibrationTarget target, CalibrationPhotoSession photo)
    {
        int count = target.RadialArcDashes.Count;
        if (!target.HasRadialArcDashes || !photo.RectificationConfirmed || (uint)SelectedBandIndex >= count ||
            Marks is null || Marks.Count > count || Marks.Any(m => m is null || (uint)m.BandIndex >= count
                || m.Points is null || m.Points.Count > 3 || !Enum.IsDefined(m.Direction)
                || m.Points.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                    || double.Hypot(p.X, p.Y) < 7.5 || double.Hypot(p.X, p.Y) > photo.PhysicalOuterRadiusMm)
                || (m.Complete && !CalibrationArcLengthMark.TrySpan(m.Points, out _))) ||
            Marks.Select(m => m.BandIndex).Distinct().Count() != Marks.Count)
            throw new ArgumentException("短圆弧量取记录无效。");
    }
}
