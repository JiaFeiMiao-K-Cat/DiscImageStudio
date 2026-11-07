using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Core.Calibration;

public sealed record CalibrationSession(
    CalibrationTarget Target, List<CalibrationObservation> Observations,
    CalibrationFitOptions? FitOptions = null, bool CdInterleave = true,
    CalibrationPhotoSession? Photo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalibrationConfirmedFit? ConfirmedFit = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(Observations);
        Target.Validate();
        CalibrationFitter.ValidateObservations(Target, Observations);
        FitOptions?.Validate(Target);
        Photo?.Validate();
        ConfirmedFit?.Validate(Target, Photo);
        if (Target.HasWideAddressBands && Photo?.UseRingSeed == true)
            throw new ArgumentException("宽带测量用于估算下一张盘的初始半径。");
        bool wrongWideDiscSize = Target.Parameters.Kind == CalibrationDiscKind.Cd && Photo?.PhysicalOuterRadiusMm != 60;
        if (Target.HasWideAddressBands && wrongWideDiscSize && Photo is { } widePhoto
            && (widePhoto.RingObservations is { Count: > 0 } || widePhoto.ArcDraft is not null))
            throw new ArgumentException("宽带测量需要 12 cm 盘片的矫正照片，用于估算下一张盘的初始半径。");
        if (Photo?.RadialArcLengths is { } lengths) lengths.Validate(Target, Photo);
        if (Photo?.PreferredClockAngleDegrees is double angle &&
            (!Target.HasClockRuler || !double.IsFinite(angle) || angle < 0 || angle >= 360))
            throw new ArgumentException("时标读数的优先方向无效。");
        if ((!Target.HasCrossArms || Target.HasSingleRadialLine || Target.HasBoundaryPattern) && Photo is not null)
            CalibrationCrossFitter.ValidateTraces(Target, Photo.Traces);
        if (Photo?.BoundarySegments is { } segments)
        {
            if (!Target.HasBoundaryPattern || Photo.Traces.Count != 0)
                throw new ArgumentException("边界描点只能用于对应的边界中标记录，不能与十字线或单线的中线描点混用。");
            CalibrationBoundaryFitter.ValidateSegments(Target, segments);
        }
        if (Photo?.RingObservations is { } rings)
            CalibrationRingFitter.ValidateObservations(Target, rings, Photo.PhysicalOuterRadiusMm);
        if (Photo?.ArcDraft is { } draft && (uint)draft.RingIndex >= (uint)CalibrationRingFitter.MeasurementCount(Target))
            throw new ArgumentException("圆环编号或实测半径无效。");
        if (Photo?.BandRaySelection is int selection &&
            (!Target.HasWideAddressBands || wrongWideDiscSize || !Photo.RectificationConfirmed
             || Photo.ArcDraft is not null || (uint)selection >= (uint)CalibrationRingFitter.MeasurementCount(Target)))
            throw new ArgumentException("单线取点记录中的交界或照片状态无效。");
    }
}

/// <summary>A manually confirmed fit belongs to the photographed target, whose original
/// generation geometry stays frozen. Its selected candidate supplies the next disc's setup.</summary>
public sealed record CalibrationConfirmedFit(string TargetId, CalibrationFitResult Result, int SelectedCandidateIndex)
{
    internal void Validate(CalibrationTarget target, CalibrationPhotoSession? photo)
    {
        if (TargetId != target.Id || photo?.RectificationConfirmed != true
            || !(target.HasCrossArms || target.HasSingleRadialLine || target.HasBoundaryPattern)
            || Result is null || Result.Candidates is null || Result.Candidates.Count is < 1 or > 20
            || (uint)SelectedCandidateIndex >= (uint)Result.Candidates.Count
            || Result.Warnings is null || Result.Warnings.Any(warning => warning is null)
            || !double.IsFinite(Result.RotationRadians) || !ValidError(Result.RmsErrorMm))
            throw new ArgumentException("Invalid confirmed calibration fit.");
        if (Result.BestParameters is { } best) target.ValidateCompatible(best);
        if (Result.Succeeded && Result.BestParameters is null)
            throw new ArgumentException("Invalid confirmed calibration fit.");
        foreach (CalibrationFitCandidate candidate in Result.Candidates)
        {
            if (candidate is null || candidate.Parameters is null
                || !double.IsFinite(candidate.RotationRadians) || !ValidError(candidate.RmsErrorMm)
                || !ValidError(candidate.RobustCost) || !ValidError(candidate.MaxErrorMm) || candidate.OutlierCount < 0)
                throw new ArgumentException("Invalid confirmed calibration fit candidate.");
            target.ValidateCompatible(candidate.Parameters);
        }
    }

    private static bool ValidError(double value) => double.IsFinite(value) && value >= 0;
}

/// <summary>Selection in three-point mode and up to two clicks on an unfinished inner edge.</summary>
public sealed record CalibrationRingArcDraft(int RingIndex, IReadOnlyList<CalibrationPoint> Points);

/// <summary>The bounded source photo is embedded at its working resolution so a saved calibration can be reopened on its own.</summary>
public sealed record CalibrationPhotoSession(string ImagePngBase64,
    IReadOnlyList<CalibrationPoint> OuterBoundary, IReadOnlyList<CalibrationPoint> HoleBoundary,
    double PhysicalOuterRadiusMm, bool RectificationConfirmed,
    IReadOnlyList<CalibrationCrossTrace> Traces,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsCoarseMode = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalibrationRingObservation>? RingObservations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool UseRingSeed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalibrationRingArcDraft? ArcDraft = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PreferredClockAngleDegrees = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalibrationArcLengthSession? RadialArcLengths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BandRaySelection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalibrationBoundarySegment>? BoundarySegments = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ImagePngBase64) || ImagePngBase64.Length > CalibrationPhotoLimits.MaxEncodedPhotoLength)
            throw new ArgumentException("标定照片为空或过大，请重新导入照片。");
        if (PhysicalOuterRadiusMm is not (40 or 60))
            throw new ArgumentException("请选择 12 cm 或 8 cm 光盘。");
        ValidateBoundary(OuterBoundary);
        ValidateBoundary(HoleBoundary);
        if (Traces is null || Traces.Count > 1 || Traces.Any(t => t is null || t.ArmIndex != 0
            || t.Points is null || t.Points.Count > 4000 || t.Points.Any(p => p is null
                || !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                || Math.Sqrt(p.X * p.X + p.Y * p.Y) > PhysicalOuterRadiusMm))
            || Traces.Select(t => t.ArmIndex).Distinct().Count() != Traces.Count)
            throw new ArgumentException("十字线记录无效。");
        if (!RectificationConfirmed && Traces.Any(t => t.Points.Count > 0))
            throw new ArgumentException("尚未确认照片矫正，不能保存描线点。");
        if (BoundarySegments is { } segments && (segments.Count > 64
            || segments.Any(segment => segment is null || segment.Points is null
                || segment.Points.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                    || p.X * p.X + p.Y * p.Y > PhysicalOuterRadiusMm * PhysicalOuterRadiusMm))
            || (!RectificationConfirmed && segments.Any(segment => segment.Points.Count > 0))))
            throw new ArgumentException("分段描边记录需要已确认的矫正照片，所有点须位于盘面上。");
        if (RingObservations is { Count: > 0 } rings
            && (!RectificationConfirmed || rings.Any(r => r is null || !double.IsFinite(r.RadiusMm)
                || r.RadiusMm < 7.5 || r.RadiusMm > PhysicalOuterRadiusMm)))
            throw new ArgumentException("量环记录需要已矫正的照片，圆弧半径须位于盘面上。");
        if (UseRingSeed && RingObservations is not { Count: >= 3 })
            throw new ArgumentException("作为精标初值的量环记录需要至少三条已对准的圆环。");
        if (ArcDraft is { } draft && (!RectificationConfirmed || draft.Points is null || draft.Points.Count > 2
            || draft.Points.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                || p.X * p.X + p.Y * p.Y < 7.5 * 7.5 || p.X * p.X + p.Y * p.Y > PhysicalOuterRadiusMm * PhysicalOuterRadiusMm)
            || (draft.Points.Count > 0 && RingObservations?.Any(r => r.RingIndex == draft.RingIndex) == true)))
            throw new ArgumentException("未完成的三点量环记录无效。");
    }

    private static void ValidateBoundary(IReadOnlyList<CalibrationPoint> points)
    {
        if (points is null || points.Count is < 5 or > 128 || points.Any(p => p is null
            || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0
            || p.X >= CalibrationPhotoLimits.MaxDimension || p.Y >= CalibrationPhotoLimits.MaxDimension))
            throw new ArgumentException("照片轮廓控制点无效。");
    }
}

public static class CalibrationSessionJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(CalibrationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();
        string json = JsonSerializer.Serialize(session, JsonOptions);
        if (json.Length > CalibrationPhotoLimits.MaxSessionLength) throw new ArgumentException("标定会话文件过大。");
        return json;
    }

    public static CalibrationSession Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > CalibrationPhotoLimits.MaxSessionLength) throw new ArgumentException("标定会话文件过大。");
        CalibrationSession session = JsonSerializer.Deserialize<CalibrationSession>(json, JsonOptions)
            ?? throw new ArgumentException("标定会话为空。");
        session.Validate();
        return session;
    }

    public static void Save(string path, CalibrationSession session)
    {
        string json = Serialize(session);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null) Directory.CreateDirectory(directory);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static CalibrationSession Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > CalibrationPhotoLimits.MaxSessionLength) throw new ArgumentException("标定会话文件过大。");
        return Deserialize(File.ReadAllText(path));
    }
}
