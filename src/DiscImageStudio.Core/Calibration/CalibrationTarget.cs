using System.Text.Json.Serialization;

namespace DiscImageStudio.Core.Calibration;

public enum CalibrationDiscKind { Cd, Dvd, BluRay }
public enum CalibrationSector { Coarse, Large, Medium, Small }
public enum CalibrationGroupRole { Coarse, Fine, Validation }

/// <summary>Derived group metadata, reconstructed from the saved generation rules.</summary>
public sealed record CalibrationGroup(string Id, int Index, CalibrationGroupRole Role, double AngleDegrees,
    IReadOnlyList<double> ArcRadiiMm, double ArcThicknessMm, double ArcStartAngleDegrees, double ArcEndAngleDegrees,
    double SpineWidthMm, double SpineInnerRadiusMm, double SpineOuterRadiusMm);

/// <summary>Coordinates are millimetres from the disc centre: x right, y down.</summary>
public sealed record CalibrationPoint(double X, double Y);

/// <summary>Canonical identity of one physical control point, shared by every selecting region.</summary>
public sealed record CalibrationControlPoint(string Id, string CellId, int CornerIndex,
    CalibrationPoint Position, string GroupId, CalibrationGroupRole Role);

public sealed record CalibrationParameters(
    CalibrationDiscKind Kind, double InnerRadiusMm, double OuterRadiusMm,
    long Sectors, double LinearDensity,
    double PitchLinear = 0, double PitchQuadratic = 0, double PitchCubic = 0,
    bool BluRayInvertMapping = false, long DrawingStartSector = 0, DateTimeOffset? BluRayIsoTimestamp = null,
    long DrawingEndSectorExclusive = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DvdReadCompatibility = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? UsableSectors = null)
{
    public const int DvdChannelBitsPerSector = 38_688;
    public const int BluRaySectorsPerCluster = 32;
    public const int BluRayChannelBitsPerRecordingUnit = 962_136;
    public const double BluRayChannelBitLengthNm = 74.5;

    [JsonIgnore]
    public long OutputSectors => UsableSectors ?? Sectors;

    [JsonIgnore]
    public long EffectiveDrawingEndSectorExclusive => DrawingEndSectorExclusive == 0
        ? OutputSectors : DrawingEndSectorExclusive;

    [JsonIgnore]
    public double TrackLengthMm => Kind switch
    {
        CalibrationDiscKind.Cd => Sectors / 75.0 * LinearDensity,
        CalibrationDiscKind.Dvd => Sectors * (double)DvdChannelBitsPerSector * LinearDensity * 1e-6,
        CalibrationDiscKind.BluRay => Sectors / (double)BluRaySectorsPerCluster
            * BluRayChannelBitsPerRecordingUnit * LinearDensity * 1e-6,
        _ => double.NaN,
    };

    [JsonIgnore]
    public double DrawingInnerRadiusMm => DrawingStartSector == 0 ? InnerRadiusMm
        : new CalibrationGeometry(this).AtProgress(DrawingStartSector / (double)Sectors).Radius;

    [JsonIgnore]
    public double DrawingOuterRadiusMm => EffectiveDrawingEndSectorExclusive == Sectors ? OuterRadiusMm
        : new CalibrationGeometry(this).AtProgress(EffectiveDrawingEndSectorExclusive / (double)Sectors).Radius;

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Sectors <= 0 || !double.IsFinite(InnerRadiusMm)
            || !double.IsFinite(OuterRadiusMm) || InnerRadiusMm <= 0
            || OuterRadiusMm <= InnerRadiusMm || !double.IsFinite(LinearDensity)
            || LinearDensity <= 0 || !double.IsFinite(TrackLengthMm)
            || TrackLengthMm <= OuterRadiusMm - InnerRadiusMm
            || !double.IsFinite(OuterRadiusMm * OuterRadiusMm)
            || !double.IsFinite(2 * TrackLengthMm / (OuterRadiusMm * OuterRadiusMm - InnerRadiusMm * InnerRadiusMm)))
            throw new ArgumentException("标定参数无效：请检查盘片类型、内外半径、扇区数和线速度／位长。");
        DvdTrackGeometry.ValidatePitchCoefficients(PitchLinear, PitchQuadratic, PitchCubic);
        int alignment = Kind == CalibrationDiscKind.Dvd ? 16 : Kind == CalibrationDiscKind.BluRay ? 32 : 1;
        if (OutputSectors <= 0 || OutputSectors > Sectors
            || (UsableSectors is not null && OutputSectors % alignment != 0))
            throw new ArgumentException("标定的可用扇区数必须为正数、不超过总扇区数，并符合介质写入对齐要求。");
        if (DrawingStartSector < 0 || DrawingStartSector >= OutputSectors
            || DrawingEndSectorExclusive < 0 || DrawingEndSectorExclusive > OutputSectors
            || (DrawingEndSectorExclusive != 0 && DrawingEndSectorExclusive <= DrawingStartSector)
            || (Kind != CalibrationDiscKind.BluRay && (DrawingStartSector != 0 || DrawingEndSectorExclusive != 0
                || BluRayInvertMapping || BluRayIsoTimestamp is not null)))
            throw new ArgumentException("标定图案的可绘制扇区范围无效。");
        if (Kind == CalibrationDiscKind.BluRay && (Sectors > uint.MaxValue
            || Sectors % BluRaySectorsPerCluster != 0 || DrawingStartSector % BluRaySectorsPerCluster != 0
            || DrawingEndSectorExclusive % BluRaySectorsPerCluster != 0
            || LinearDensity != BluRayChannelBitLengthNm || OuterRadiusMm > 60))
            throw new ArgumentException("蓝光标定需要完整的 32 扇区簇、74.5 nm 位长和不超过 60 mm 的外半径。");
    }
}

/// <summary>Legacy serialized options; group geometry is derived solely from the disc radii.</summary>
public sealed record CalibrationPatternOptions(
    double LargeCellMm = 8, double MediumCellMm = 4,
    double SmallCellMm = 2, double SectorGapMm = 1)
{
    public void Validate()
    {
        if (!double.IsFinite(LargeCellMm) || !double.IsFinite(MediumCellMm)
            || !double.IsFinite(SmallCellMm) || !double.IsFinite(SectorGapMm)
            || SmallCellMm < 0.25 || MediumCellMm <= SmallCellMm
            || LargeCellMm <= MediumCellMm || LargeCellMm > 100
            || SectorGapMm <= 0 || SectorGapMm > 10)
            throw new ArgumentException("标定记录中的兼容图案选项无效，请重新创建标定图案。");
    }
}

/// <summary>Three radial tiers with asymmetric angular steps on both ends.</summary>
public sealed record CalibrationArcShape(double CenterRadiusMm, double ThicknessMm)
{
    // These scalar values, rather than an array of vertices, preserve value equality
    // when a saved target is reconstructed and compared with its generation rules.
    internal (double Radius, double Degrees)[] PolarCorners =>
    [
        (CenterRadiusMm - ThicknessMm / 2, -157),
        (CenterRadiusMm - ThicknessMm / 2, -128),
        (CenterRadiusMm - ThicknessMm * 0.19, -128),
        (CenterRadiusMm - ThicknessMm * 0.19, -115),
        (CenterRadiusMm + ThicknessMm * 0.16, -115),
        (CenterRadiusMm + ThicknessMm * 0.16, -99),
        (CenterRadiusMm + ThicknessMm / 2, -99),
        (CenterRadiusMm + ThicknessMm / 2, -169),
        (CenterRadiusMm + ThicknessMm * 0.16, -169),
        (CenterRadiusMm + ThicknessMm * 0.16, -162),
        (CenterRadiusMm - ThicknessMm * 0.19, -162),
        (CenterRadiusMm - ThicknessMm * 0.19, -157),
    ];

    internal bool Contains(double radius, double degrees)
    {
        if (radius < CenterRadiusMm - ThicknessMm / 2 || radius > CenterRadiusMm + ThicknessMm / 2)
            return false;
        (double start, double end) = radius < CenterRadiusMm - ThicknessMm * 0.19
            ? (-157, -128) : radius < CenterRadiusMm + ThicknessMm * 0.16 ? (-162, -115) : (-169, -99);
        return degrees >= start && degrees <= end;
    }

    internal static CalibrationPoint ToPoint(double radius, double degrees)
    {
        double angle = degrees * Math.PI / 180;
        return new(radius * Math.Cos(angle), radius * Math.Sin(angle));
    }
}

/// <summary>A selectable region between two arcs and a radial spine; one side has no ink.</summary>
public sealed record CalibrationPolarQuad(double InnerRadiusMm, double OuterRadiusMm,
    double StartAngleDegrees, double EndAngleDegrees, double SpineAngleDegrees)
{
    internal (double Radius, double Degrees)[] PolarCorners =>
    [
        (InnerRadiusMm, StartAngleDegrees), (InnerRadiusMm, EndAngleDegrees),
        (OuterRadiusMm, EndAngleDegrees), (OuterRadiusMm, StartAngleDegrees),
    ];

    internal (double Radius, double Degrees)[] OpenBoundary => SpineAngleDegrees == EndAngleDegrees
        ? PolarCorners : [PolarCorners[1], PolarCorners[0], PolarCorners[3], PolarCorners[2]];
}

/// <summary>Derived editable centreline; it adds controls without changing the engraved pattern.</summary>
public sealed record CalibrationRadialLine(double InnerRadiusMm, double OuterRadiusMm, double AngleDegrees)
{
    internal (double Radius, double Degrees)[] PolarCorners =>
        [(InnerRadiusMm, AngleDegrees), (OuterRadiusMm, AngleDegrees)];
}

public sealed record CalibrationCell(
    string Id, CalibrationSector Sector, double X, double Y, double Size, double? HeightMm = null,
    CalibrationArcShape? ArcShape = null, CalibrationPolarQuad? PolarQuad = null,
    [property: JsonIgnore] CalibrationRadialLine? RadialLine = null)
{
    [JsonIgnore]
    public double Height => HeightMm ?? Size;

    [JsonIgnore]
    public string? GroupId => PolarQuad is null && RadialLine is null ? null : string.Join('-', Id.Split('-').Take(2));

    [JsonIgnore]
    public bool IsOutlineClosed => PolarQuad is null && RadialLine is null;

    // Rectangles: clockwise from top left. Arcs: ordered around their 12-vertex boundary.
    [JsonIgnore]
    public IReadOnlyList<CalibrationPoint> Corners => RadialLine is not null
        ? RadialLine.PolarCorners.Select(point => CalibrationArcShape.ToPoint(point.Radius, point.Degrees)).ToArray()
        : PolarQuad is not null
        ? PolarQuad.PolarCorners.Select(point => CalibrationArcShape.ToPoint(point.Radius, point.Degrees)).ToArray()
        : ArcShape is null
        ? [new(X, Y), new(X + Size, Y), new(X + Size, Y + Height), new(X, Y + Height)]
        : ArcShape.PolarCorners.Select(point => CalibrationArcShape.ToPoint(point.Radius, point.Degrees)).ToArray();
}

public sealed class CalibrationTarget
{
    public const int CurrentSchemaVersion = 16;
    public const double SingleRadialLineWidthMm = 6;
    private readonly CalibrationAngularPattern? _angularPattern;
    private readonly Dictionary<string, CalibrationCell> _byId;
    private readonly Dictionary<(string CellId, int CornerIndex), CalibrationControlPoint> _controlPointByReference;
    private readonly CalibrationGeometry _generatedGeometry;
    private readonly double _drawingInnerRadiusMm;
    private readonly double _drawingOuterRadiusMm;

    [JsonConstructor]
    public CalibrationTarget(int schemaVersion, string id, CalibrationParameters parameters,
        CalibrationPatternOptions options, IReadOnlyList<CalibrationCell> cells, CalibrationArtwork artwork = default,
        string? dvdReadCompatibilityMode = null)
    {
        SchemaVersion = schemaVersion;
        Artwork = artwork;
        Id = id;
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        DvdReadCompatibilityMode = dvdReadCompatibilityMode;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Cells = Array.AsReadOnly(cells?.ToArray() ?? throw new ArgumentNullException(nameof(cells)));
        Validate();
        _generatedGeometry = new(ParametersForGeometry(Parameters));
        WideAddressBands = HasWideAddressBands ? CalibrationWideBandPattern.ForVersion(SchemaVersion) : [];
        WideBandSectorRanges = HasWideAddressBands && Parameters.Kind != CalibrationDiscKind.Cd
            ? CalibrationWideBandPattern.SectorRanges(Parameters, WideAddressBands) : [];
        AngleSweepStations = HasAngleSweep ? CalibrationAngleSweepStation.Create(this, _generatedGeometry) : [];
        _drawingInnerRadiusMm = _generatedGeometry.AtProgress(Parameters.DrawingStartSector / (double)Parameters.Sectors).Radius;
        _drawingOuterRadiusMm = Parameters.DrawingOuterRadiusMm;
        double pixelSizeMm = CalibrationReferencePattern.MillimetresPerPixel(Parameters.OuterRadiusMm, Parameters.Kind);
        double crossWidth = CalibrationReferencePattern.NominalLineWidthPixels * pixelSizeMm;
        if (UsesReferencePattern)
        {
            RingRadiiMm = Array.AsReadOnly(CalibrationReferencePattern.RingRadiiPixels.Select(radius => radius * pixelSizeMm).ToArray());
            VisibleRings = Array.AsReadOnly(RingRadiiMm
                .Where(radius => radius - crossWidth / 2 >= DrawingInnerRadiusMm && radius + crossWidth / 2 <= DrawingOuterRadiusMm)
                .Select((radius, index) => new CalibrationRing(index, radius, crossWidth)).ToArray());
        }
        else if (HasBoundaryPattern || HasSingleRadialLine || HasRadialArcDashes)
        {
            if (HasSingleRadialLine) crossWidth = SingleRadialLineWidthMm;
            VisibleRings = [];
            RingRadiiMm = [];
        }
        else if (HasClockRuler || HasWideAddressBands)
        {
            VisibleRings = HasWideAddressBands ? CalibrationWideBandPattern.AddressRings(this, _generatedGeometry)
                : CalibrationClockRulerPattern.AddressRings(_generatedGeometry);
            if (HasClockRuler && Parameters.OutputSectors < Parameters.Sectors)
                VisibleRings = Array.AsReadOnly(VisibleRings.Where(ring =>
                    ring.RadiusMm - ring.WidthMm / 2 >= DrawingInnerRadiusMm
                    && ring.RadiusMm + ring.WidthMm / 2 <= DrawingOuterRadiusMm).ToArray());
            RingRadiiMm = Array.AsReadOnly(VisibleRings.Select(r => r.RadiusMm).ToArray());
        }
        else
        {
            crossWidth = Math.Min(1.5 * crossWidth, .025 * (DrawingOuterRadiusMm - DrawingInnerRadiusMm));
            double radiansPerMm = _generatedGeometry.AngleAtRadius(Parameters.OuterRadiusMm)
                / (Parameters.OuterRadiusMm - Parameters.InnerRadiusMm);
            _angularPattern = new(DrawingInnerRadiusMm, DrawingOuterRadiusMm, radiansPerMm, crossWidth, artwork);
            VisibleRings = _angularPattern.Rings;
            RingRadiiMm = Array.AsReadOnly(VisibleRings.Select(r => r.RadiusMm).ToArray());
        }
        RadialArcDashes = HasRadialArcDashes
            ? CalibrationRadialArcPattern.Create(schemaVersion, DrawingInnerRadiusMm, DrawingOuterRadiusMm) : [];
        // Wavy boundaries have no straight legacy control spines. Their exact source
        // profile is used by the boundary fitter and prediction directly.
        Groups = !HasCrossArms || HasWavyBoundary ? [] : Array.AsReadOnly(BuildGroups(Parameters, crossWidth, TraceArmCount, HasBoundaryPattern ? 180 : 90));
        ControlCells = Array.AsReadOnly(Cells.Concat(BuildSpineCells(Groups)).ToArray());
        _byId = ControlCells.ToDictionary(cell => cell.Id, StringComparer.Ordinal);
        _controlPointByReference = new();
        var uniquePoints = new Dictionary<CalibrationPoint, CalibrationControlPoint>();
        var canonicalPoints = new List<CalibrationControlPoint>();
        foreach (CalibrationCell cell in ControlCells)
        {
            IReadOnlyList<CalibrationPoint> corners = cell.Corners;
            CalibrationGroup group = Groups.First(item => item.Id == cell.GroupId);
            for (int index = 0; index < corners.Count; index++)
            {
                CalibrationPoint position = corners[index];
                if (!uniquePoints.TryGetValue(position, out CalibrationControlPoint? canonical))
                {
                    canonical = new($"{cell.Id}:{index}", cell.Id, index, position, group.Id, group.Role);
                    uniquePoints.Add(position, canonical);
                    canonicalPoints.Add(canonical);
                }
                _controlPointByReference.Add((cell.Id, index), canonical);
            }
        }
        ControlPoints = canonicalPoints.AsReadOnly();
    }

    public int SchemaVersion { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CalibrationArtwork Artwork { get; }
    public string Id { get; }
    public CalibrationParameters Parameters { get; }
    /// <summary>Frozen output recipe. Missing on old targets, whose enabled option used the legacy bridge.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DvdReadCompatibilityMode { get; }

    [JsonIgnore]
    public string EffectiveDvdReadCompatibilityMode => Parameters.Kind == CalibrationDiscKind.Dvd
        && Parameters.DvdReadCompatibility
            ? DvdReadCompatibilityMode ?? DvdReadCompatibilityModes.LegacyBridge
            : DvdReadCompatibilityModes.Off;
    public CalibrationPatternOptions Options { get; }
    public IReadOnlyList<CalibrationCell> Cells { get; }

    [JsonIgnore]
    public IReadOnlyList<CalibrationGroup> Groups { get; }

    /// <summary>Source circle centres, including any circles cropped from legacy artwork.</summary>
    [JsonIgnore]
    public IReadOnlyList<double> RingRadiiMm { get; }

    /// <summary>Measurable rings, numbered from the innermost complete circle on this particular disc.</summary>
    [JsonIgnore]
    public IReadOnlyList<CalibrationRing> VisibleRings { get; }

    [JsonIgnore]
    public bool HasAngularReference => _angularPattern is not null && Artwork == CalibrationArtwork.SweptReference;

    [JsonIgnore]
    public bool HasClockRuler => Artwork == CalibrationArtwork.ClockRuler;

    [JsonIgnore]
    public bool HasRadialArcDashes => Artwork == CalibrationArtwork.RadialArcDashes;

    [JsonIgnore]
    public bool HasWideAddressBands => Artwork is CalibrationArtwork.WideAddressBands or CalibrationArtwork.AngleSweepBands;

    [JsonIgnore]
    public bool HasAngleSweep => Artwork == CalibrationArtwork.AngleSweepBands;

    [JsonIgnore]
    public IReadOnlyList<CalibrationAngleSweepStation> AngleSweepStations { get; }

    [JsonIgnore]
    public IReadOnlyList<CalibrationAddressBand> WideAddressBands { get; }

    /// <summary>Absolute, half-open DVD or BD sector ranges used by the writer and the fit.
    /// CD retains its original byte-addressed transitions.</summary>
    [JsonIgnore]
    public IReadOnlyList<(long Start, long EndExclusive)> WideBandSectorRanges { get; }

    internal (double Start, double EndExclusive) WideBandProgress(int bandIndex)
    {
        if (Parameters.Kind == CalibrationDiscKind.Cd)
        {
            var band = WideAddressBands[bandIndex];
            return (band.StartProgress, band.EndProgress);
        }
        var range = WideBandSectorRanges[bandIndex];
        return (range.Start / (double)Parameters.Sectors, range.EndExclusive / (double)Parameters.Sectors);
    }

    // The saved setup can include fine-calibrated pitch coefficients. Coarse
    // bands use fixed pitch for both their schematic and their endpoint estimate.
    internal CalibrationParameters ParametersForGeometry(CalibrationParameters p) =>
        HasWideAddressBands
            ? p with { PitchLinear = 0, PitchQuadratic = 0, PitchCubic = 0 } : p;

    [JsonIgnore]
    public bool UsesReferencePattern => SchemaVersion == 6 || Artwork == CalibrationArtwork.ReferenceCross;

    [JsonIgnore]
    public bool HasSingleRadialLine => Artwork == CalibrationArtwork.SingleRadialLine;

    /// <summary>Two opposite black/white boundaries, measured as separate visible fragments.</summary>
    [JsonIgnore]
    public bool HasHalfDisc => Artwork == CalibrationArtwork.HalfDisc;

    [JsonIgnore]
    public bool HasWavyBoundary => Artwork == CalibrationArtwork.WavyBoundary;

    [JsonIgnore]
    public bool HasBoundaryPattern => HasHalfDisc || HasWavyBoundary;

    /// <summary>Known source bearing offset of both opposed boundaries; no whole turns are prescribed.</summary>
    public double BoundaryPhaseAtSourceRadius(double radius)
        => HasWavyBoundary ? Math.PI / 2 * Math.Sin(6 * Math.PI
            * (radius - DrawingInnerRadiusMm) / (DrawingOuterRadiusMm - DrawingInnerRadiusMm)) : 0;

    internal double BoundaryPhaseDerivative(double radius)
        => HasWavyBoundary ? BoundaryPhaseDerivativeBound * Math.Cos(6 * Math.PI
            * (radius - DrawingInnerRadiusMm) / (DrawingOuterRadiusMm - DrawingInnerRadiusMm)) : 0;

    internal double BoundaryPhaseDerivativeBound => HasWavyBoundary
        ? 3 * Math.PI * Math.PI / (DrawingOuterRadiusMm - DrawingInnerRadiusMm) : 0;

    [JsonIgnore]
    public int TraceArmCount => Artwork switch
    {
        CalibrationArtwork.SingleRadialLine => 1,
        CalibrationArtwork.HalfDisc or CalibrationArtwork.WavyBoundary => 2,
        CalibrationArtwork.SweptReference or CalibrationArtwork.FineCross or CalibrationArtwork.ReferenceCross => 4,
        _ => 0,
    };

    [JsonIgnore]
    public bool HasCrossArms => TraceArmCount > 0;

    [JsonIgnore]
    public IReadOnlyList<CalibrationRadialArc> RadialArcDashes { get; }

    /// <summary>Unwrapped source sector bearing in the calibration photo convention (Y down).</summary>
    public double SectorAngleAtRadius(double sourceRadiusMm) => HasAngularReference
        ? _angularPattern!.SectorAngle(sourceRadiusMm) : throw new InvalidOperationException("此标靶未刻录宽角度参考标记。");

    [JsonIgnore]
    public double RingThicknessMm => UsesReferencePattern
        ? CalibrationReferencePattern.NominalLineWidthPixels * CalibrationReferencePattern.MillimetresPerPixel(Parameters.OuterRadiusMm, Parameters.Kind)
        : VisibleRings.Count == 0 ? 0 : VisibleRings[0].WidthMm;

    [JsonIgnore]
    public IReadOnlyList<CalibrationCell> ControlCells { get; }

    [JsonIgnore]
    public IReadOnlyList<CalibrationControlPoint> ControlPoints { get; }

    public CalibrationControlPoint GetControlPoint(string cellId, int cornerIndex)
    {
        if (string.IsNullOrWhiteSpace(cellId)
            || !_controlPointByReference.TryGetValue((cellId, cornerIndex), out CalibrationControlPoint? point))
            throw new ArgumentException("控制块或控制点编号无效。");
        return point;
    }

    public CalibrationGroupRole GetGroupRole(string cellId)
    {
        if (string.IsNullOrWhiteSpace(cellId) || !_byId.TryGetValue(cellId, out CalibrationCell? cell))
            throw new ArgumentException("控制块编号无效。");
        return Groups.First(group => group.Id == cell.GroupId).Role;
    }

    public static CalibrationTarget Create(CalibrationParameters parameters, CalibrationPatternOptions? options = null,
        CalibrationArtwork artwork = CalibrationArtwork.SweptReference)
    {
        parameters.Validate();
        double scale = Math.Clamp((parameters.OuterRadiusMm - parameters.InnerRadiusMm) / 32.3, 0.5, 2);
        options ??= new(8 * scale, 4 * scale, 2 * scale);
        options.Validate();
        int version = artwork switch
        {
            CalibrationArtwork.ClockRuler => 8,
            CalibrationArtwork.RadialArcDashes => 10,
            CalibrationArtwork.WideAddressBands => 12,
            CalibrationArtwork.AngleSweepBands => 13,
            CalibrationArtwork.ReferenceCross or CalibrationArtwork.SingleRadialLine => 14,
            CalibrationArtwork.HalfDisc => 15,
            CalibrationArtwork.WavyBoundary => 16,
            _ => 7,
        };
        // CD coarse bands keep their frozen full-track byte addresses. A new
        // target needs the last edge and at least one complete light sector after
        // it; loading an existing target must retain its original address map.
        if (parameters.Kind == CalibrationDiscKind.Cd && version is 12 or 13
            && parameters.OutputSectors <= CalibrationWideBandPattern.ForVersion(version)[^1]
                .UnitRange(parameters.Sectors).EndExclusive)
            throw new ArgumentException("CD 粗标的可用扇区数不足以包含最外侧固定地址边界及其后的留白。请增加可用扇区数，或使用边界标定。");
        return new(version, Guid.NewGuid().ToString("N"), parameters, options,
            BuildCells(parameters), artwork,
            parameters.Kind == CalibrationDiscKind.Dvd && parameters.DvdReadCompatibility
                ? DvdReadCompatibilityModes.Current : null);
    }

    public void Validate()
    {
        if (SchemaVersion is < 6 or > CurrentSchemaVersion)
            throw DiagnosticMessage.Argument($"不支持的标定会话版本 {SchemaVersion}。");
        if (!Enum.IsDefined(Artwork) || (SchemaVersion == 6 && Artwork != default)
            || ((SchemaVersion == 8) != HasClockRuler)
            || ((SchemaVersion is 9 or 10) != HasRadialArcDashes)
            || ((SchemaVersion is 11 or 12) != (Artwork == CalibrationArtwork.WideAddressBands))
            || ((SchemaVersion == 13) != HasAngleSweep)
            || ((SchemaVersion == 14) != (Artwork is CalibrationArtwork.ReferenceCross or CalibrationArtwork.SingleRadialLine))
            || ((SchemaVersion == 15) != HasHalfDisc)
            || ((SchemaVersion == 16) != HasWavyBoundary))
            throw new ArgumentException("标定图案类型与记录版本不匹配。");
        if (!Guid.TryParseExact(Id, "N", out _)) throw new ArgumentException("标定图案 ID 无效。");
        Parameters.Validate();
        if (DvdReadCompatibilityMode is not null && !DvdReadCompatibilityModes.IsSupported(DvdReadCompatibilityMode))
            throw new ArgumentException("Unsupported DVD read compatibility recipe in the calibration target.");
        if (HasClockRuler && !CalibrationClockRulerPattern.Supports(Parameters))
            throw new ArgumentException("时钟尺粗标靶需要 70–85 分钟、1200 mm/s 的标准 12 cm CD。");
        if (HasRadialArcDashes && !CalibrationRadialArcPattern.Supports(Parameters))
            throw new ArgumentException("圆弧粗标靶需要 70–85 分钟、1200 mm/s 的标准 12 cm CD。");
        if (HasWideAddressBands && (SchemaVersion == 11 || Parameters.Kind == CalibrationDiscKind.Cd)
            && !CalibrationClockRulerPattern.Supports(Parameters))
            throw new ArgumentException("宽带粗标靶需要 70–85 分钟、1200 mm/s 的标准 12 cm CD。");
        if (HasWideAddressBands && SchemaVersion == 12 && !CalibrationWideBandPattern.Supports(Parameters))
            throw new ArgumentException("宽带粗标靶需要完整的 DVD 扇区块或蓝光簇，且绘图区须足以分辨六个交界并估算全盘半径。");
        if (HasAngleSweep && !CalibrationWideBandPattern.SupportsAngleSweep(Parameters))
            throw new ArgumentException("当前盘型、容量或绘图区不适用 v13 角度粗标。");
        Options.Validate();
        if (Cells.Count > 20_000 || Cells.Any(cell => cell is null))
            throw new ArgumentException("标定图案控制块数量无效。");
        if (Cells.Select(cell => cell.Id).Distinct(StringComparer.Ordinal).Count() != Cells.Count)
            throw new ArgumentException("标定图案含重复控制块 ID。");
        // Persist the actual control geometry; reject altered or incompatible definitions.
        CalibrationCell[] expected = BuildCells(Parameters);
        if (!Cells.SequenceEqual(expected))
            throw new ArgumentException("标定图案与生成参数或图案规则不一致。");
    }

    public CalibrationPoint GetCorner(string cellId, int cornerIndex)
    {
        if (string.IsNullOrWhiteSpace(cellId) || !_byId.TryGetValue(cellId, out CalibrationCell? cell)
            || cornerIndex < 0 || cornerIndex >= cell.Corners.Count)
            throw new ArgumentException("控制块或角点编号无效。");
        return cell.Corners[cornerIndex];
    }

    /// <summary>True source ink path. Each cross arm is an open radial line.</summary>
    public IReadOnlyList<CalibrationPoint> GetOutline(string cellId, int samplesPerEdge = 32)
    {
        if (samplesPerEdge is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(samplesPerEdge));
        if (string.IsNullOrWhiteSpace(cellId) || !_byId.TryGetValue(cellId, out CalibrationCell? cell))
            throw new ArgumentException("控制块编号无效。");
        return SampleBoundary(CellBoundary(cell), samplesPerEdge);
    }

    public IReadOnlyList<IReadOnlyList<CalibrationPoint>> GetGroupContours(string groupId, int samplesPerEdge = 32)
    {
        if (samplesPerEdge is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(samplesPerEdge));
        CalibrationGroup group = Groups.FirstOrDefault(item => item.Id == groupId)
            ?? throw new ArgumentException("标定组编号无效。");
        return GroupBoundaries(group).Select(boundary => SampleBoundary(boundary, samplesPerEdge)).ToArray();
    }

    public IReadOnlyList<IReadOnlyList<CalibrationPoint>> GetMappedGroupContours(string groupId,
        CalibrationParameters actual, double rotationRadians = 0, int maxPoints = 50000)
    {
        if (maxPoints is < 16 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        CalibrationGroup group = Groups.FirstOrDefault(item => item.Id == groupId)
            ?? throw new ArgumentException("标定组编号无效。");
        var paths = new List<IReadOnlyList<CalibrationPoint>>();
        int remaining = maxPoints;
        foreach (SourceBoundary boundary in GroupBoundaries(group))
        {
            if (remaining < 2) throw OutlineLimit();
            IReadOnlyList<CalibrationPoint> path = MapBoundary(boundary, actual, rotationRadians, remaining);
            paths.Add(path);
            remaining -= path.Count;
        }
        return paths;
    }

    private static IReadOnlyList<CalibrationPoint> SampleBoundary(SourceBoundary boundary, int samplesPerEdge)
    {
        IReadOnlyList<CalibrationPoint> corners = boundary.Corners;
        (double Radius, double Degrees)[]? polar = boundary.Polar;
        int edgeCount = boundary.Closed ? corners.Count : corners.Count - 1;
        var outline = new List<CalibrationPoint>(edgeCount * samplesPerEdge + 1);
        for (int edge = 0; edge < edgeCount; edge++)
        {
            int next = (edge + 1) % corners.Count;
            for (int sample = 0; sample < samplesPerEdge; sample++)
            {
                double t = sample / (double)samplesPerEdge;
                if (polar is not null)
                    outline.Add(CalibrationArcShape.ToPoint(
                        polar[edge].Radius + (polar[next].Radius - polar[edge].Radius) * t,
                        polar[edge].Degrees + (polar[next].Degrees - polar[edge].Degrees) * t));
                else
                    outline.Add(new(corners[edge].X + (corners[next].X - corners[edge].X) * t,
                        corners[edge].Y + (corners[next].Y - corners[edge].Y) * t));
            }
        }
        outline.Add(boundary.Closed ? outline[0] : corners[^1]);
        return outline;
    }

    /// <summary>Sample the frozen artwork for this target version inside its recording annulus.</summary>
    public byte Sample(double xMm, double yMm)
    {
        if (HasClockRuler)
            throw new InvalidOperationException("时钟尺按 CD 逻辑字节生成，不能用平面图像采样编码。");
        if (!double.IsFinite(xMm) || !double.IsFinite(yMm)) return 255;
        double radiusSquared = xMm * xMm + yMm * yMm;
        if (radiusSquared < DrawingInnerRadiusMm * DrawingInnerRadiusMm - 1e-10
            || radiusSquared > DrawingOuterRadiusMm * DrawingOuterRadiusMm + 1e-10)
            return 255;
        // Half-disc black/white boundaries lie on the two vertical radii. Their 50%
        // duty cycle retains contrast when the recorded image winds into tight spirals.
        if (HasHalfDisc) return xMm < 0 ? (byte)0 : (byte)255;
        if (HasWavyBoundary)
        {
            double phase = BoundaryPhaseAtSourceRadius(Math.Sqrt(radiusSquared));
            return xMm * Math.Cos(phase) + yMm * Math.Sin(phase) < 0 ? (byte)0 : (byte)255;
        }
        // Arm zero points upwards in photo coordinates (+Y down). Only its centreline
        // enters the fit; the two broad edges are visual guides to that same centreline.
        if (HasSingleRadialLine)
            return yMm < 0 && Math.Abs(xMm) <= SingleRadialLineWidthMm / 2 ? (byte)0 : (byte)255;
        if (HasRadialArcDashes)
            return CalibrationRadialArcPattern.Sample(RadialArcDashes, xMm, radiusSquared);
        if (HasWideAddressBands)
        {
            // The encoder uses the intervals' exact byte addresses. The planar preview shows
            // the same intervals at the radii predicted by this generation snapshot.
            double radius = Math.Sqrt(radiusSquared);
            for (int i = 0; i < VisibleRings.Count; i++)
            {
                var band = VisibleRings[i];
                if (radius >= band.InnerRadiusMm && radius < band.InnerRadiusMm + band.WidthMm)
                {
                    if (HasAngleSweep)
                    {
                        var station = AngleSweepStations[i];
                        double q = _generatedGeometry.ProgressAtRadius(radius);
                        if (q >= station.StartProgress && q < station.EndProgress)
                        {
                            double phase = Math.Atan2(yMm, xMm) - _generatedGeometry.AngleAtRadius(radius)
                                + station.PhaseAt(q);
                            return CalibrationAngleSweepStation.Tone(phase);
                        }
                    }
                    return 0;
                }
            }
            return 255;
        }
        return _angularPattern is null
            ? CalibrationReferencePattern.Sample(xMm, yMm, Parameters.OuterRadiusMm, Parameters.Kind)
            : _angularPattern.Sample(xMm, yMm, radiusSquared);
    }

    [JsonIgnore]
    public double DrawingInnerRadiusMm => _drawingInnerRadiusMm;

    [JsonIgnore]
    public double DrawingOuterRadiusMm => _drawingOuterRadiusMm;

    public CalibrationPoint MapPoint(CalibrationPoint sourcePoint, CalibrationParameters actualParameters,
        double rotationRadians = 0)
    {
        return CreateMapping(actualParameters, rotationRadians)(sourcePoint);
    }

    public Func<CalibrationPoint, CalibrationPoint> CreateMapping(CalibrationParameters actualParameters,
        double rotationRadians = 0)
    {
        ValidateCompatible(actualParameters);
        if (!double.IsFinite(rotationRadians)) throw new ArgumentException("旋转必须是有限数值。");
        var actual = new CalibrationGeometry(ParametersForGeometry(actualParameters));
        return sourcePoint =>
        {
            ArgumentNullException.ThrowIfNull(sourcePoint);
            if (!double.IsFinite(sourcePoint.X) || !double.IsFinite(sourcePoint.Y))
                throw new ArgumentException("角点坐标必须是有限数值。");
            double radius = Math.Sqrt(sourcePoint.X * sourcePoint.X + sourcePoint.Y * sourcePoint.Y);
            if (radius < Parameters.InnerRadiusMm - 1e-8 || radius > Parameters.OuterRadiusMm + 1e-8)
                throw new ArgumentException("源控制点不在生成时的绘图环带内。");
            double progress = _generatedGeometry.ProgressAtRadius(radius);
            (double actualRadius, double actualAngle) = actual.AtProgress(progress);
            double angle = Math.Atan2(sourcePoint.Y, sourcePoint.X)
                + actualAngle - _generatedGeometry.AngleAtRadius(radius) + rotationRadians;
            return new(actualRadius * Math.Cos(angle), actualRadius * Math.Sin(angle));
        };
    }

    /// <summary>
    /// Trace the complete mapped outline using a bound on unwrapped angular variation.
    /// Coincident endpoints cannot hide intervening turns. Throws rather than silently
    /// simplifying an outline whose required point count exceeds the caller's budget.
    /// </summary>
    public IReadOnlyList<CalibrationPoint> GetMappedOutline(string cellId, CalibrationParameters actual,
        double rotationRadians = 0, int maxPoints = 20000)
    {
        if (maxPoints is < 16 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        if (string.IsNullOrWhiteSpace(cellId) || !_byId.TryGetValue(cellId, out CalibrationCell? cell))
            throw new ArgumentException("控制块编号无效。");
        return MapBoundary(CellBoundary(cell), actual, rotationRadians, maxPoints);
    }

    private IReadOnlyList<CalibrationPoint> MapBoundary(SourceBoundary boundary, CalibrationParameters actual,
        double rotationRadians, int maxPoints)
    {
        ValidateCompatible(actual);
        Func<CalibrationPoint, CalibrationPoint> map = CreateMapping(actual, rotationRadians);
        var actualGeometry = new CalibrationGeometry(ParametersForGeometry(actual));
        var reverseModel = new CalibrationCrossModel(actualGeometry, _generatedGeometry,
            Parameters.Kind, actual == Parameters);
        var points = new List<CalibrationPoint>();
        IReadOnlyList<CalibrationPoint> corners = boundary.Corners;
        (double Radius, double Degrees)[]? polar = boundary.Polar;
        const double maxAngleStep = Math.PI / 36;
        int edgeCount = boundary.Closed ? corners.Count : corners.Count - 1;
        for (int edge = 0; edge < edgeCount; edge++)
        {
            int next = (edge + 1) % corners.Count;
            CalibrationPoint SourceAt(double t) => polar is not null
                ? CalibrationArcShape.ToPoint(polar[edge].Radius + (polar[next].Radius - polar[edge].Radius) * t,
                    polar[edge].Degrees + (polar[next].Degrees - polar[edge].Degrees) * t)
                : new(corners[edge].X + (corners[next].X - corners[edge].X) * t,
                    corners[edge].Y + (corners[next].Y - corners[edge].Y) * t);

            void AddSegment(double firstT, double lastT, int depth)
            {
                CalibrationPoint first = SourceAt(firstT), last = SourceAt(lastT);
                double firstRadius = Math.Sqrt(first.X * first.X + first.Y * first.Y);
                double lastRadius = Math.Sqrt(last.X * last.X + last.Y * last.Y);
                double lowRadius = Math.Min(firstRadius, lastRadius), highRadius = Math.Max(firstRadius, lastRadius);
                if (!reverseModel.TryAtRadius(lowRadius, out CalibrationCrossSample lowSample)
                    || !reverseModel.TryAtRadius(highRadius, out CalibrationCrossSample highSample))
                    throw new ArgumentException("轮廓在当前轨距参数下没有有效映射。");
                double derivativeBound = reverseModel.DerivativeBound(lowSample, highSample);
                double sourceAngle = Math.Atan2(last.Y, last.X) - Math.Atan2(first.Y, first.X);
                sourceAngle = Math.Abs(Math.Atan2(Math.Sin(sourceAngle), Math.Cos(sourceAngle)));
                double variationBound = sourceAngle + derivativeBound * (highRadius - lowRadius);
                if (variationBound > maxAngleStep)
                {
                    if (depth >= 40) throw OutlineLimit();
                    double middle = (firstT + lastT) / 2;
                    AddSegment(firstT, middle, depth + 1);
                    AddSegment(middle, lastT, depth + 1);
                }
                else
                {
                    if (points.Count >= maxPoints - 1) throw OutlineLimit();
                    points.Add(map(first));
                }
            }
            AddSegment(0, 1, 0);
        }
        points.Add(boundary.Closed ? points[0] : map(corners[^1]));
        return points;
    }

    private static InvalidOperationException OutlineLimit() => new(
        "候选绕行过多，完整轮廓采样超出上限；请缩小搜索范围或提高采样上限后再确认形状。");

    /// <summary>Map a point in the predicted actual disc back into the saved generated target.</summary>
    public Func<CalibrationPoint, CalibrationPoint?> CreateInverseMapping(CalibrationParameters actualParameters,
        double rotationRadians = 0)
    {
        ValidateCompatible(actualParameters);
        if (!double.IsFinite(rotationRadians)) throw new ArgumentException("旋转必须是有限数值。");
        var actual = new CalibrationGeometry(ParametersForGeometry(actualParameters));
        return point =>
        {
            if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
            double radius = Math.Sqrt(point.X * point.X + point.Y * point.Y);
            if (radius < actualParameters.InnerRadiusMm || radius > actualParameters.OuterRadiusMm) return null;
            double progress = actual.ProgressAtRadius(radius);
            (double sourceRadius, double sourceAngle) = _generatedGeometry.AtProgress(progress);
            double angle = Math.Atan2(point.Y, point.X) - rotationRadians
                + sourceAngle - actual.AngleAtRadius(radius);
            return new(sourceRadius * Math.Cos(angle), sourceRadius * Math.Sin(angle));
        };
    }

    internal void ValidateCompatible(CalibrationParameters actual)
    {
        actual.Validate();
        if (actual.Kind != Parameters.Kind || actual.Sectors != Parameters.Sectors
            || actual.OutputSectors != Parameters.OutputSectors
            || actual.LinearDensity != Parameters.LinearDensity
            || actual.BluRayInvertMapping != Parameters.BluRayInvertMapping
            || actual.DrawingStartSector != Parameters.DrawingStartSector
            || actual.DrawingEndSectorExclusive != Parameters.DrawingEndSectorExclusive
            || actual.BluRayIsoTimestamp != Parameters.BluRayIsoTimestamp)
            throw new ArgumentException("标定固定盘片类型、扇区数和线速度／位长，可拟合内外半径、三次轨距与整体旋转。");
    }

    private static CalibrationCell[] BuildCells(CalibrationParameters parameters)
        => [];

    private static SourceBoundary CellBoundary(CalibrationCell cell)
    {
        (double Radius, double Degrees)[]? polar = cell.RadialLine?.PolarCorners
            ?? cell.PolarQuad?.OpenBoundary ?? cell.ArcShape?.PolarCorners;
        return polar is null ? new(cell.Corners, null, true) : PolarBoundary(polar, cell.IsOutlineClosed);
    }

    private static SourceBoundary PolarBoundary((double Radius, double Degrees)[] polar, bool closed = false)
        => new(polar.Select(point => CalibrationArcShape.ToPoint(point.Radius, point.Degrees)).ToArray(), polar, closed);

    private static IEnumerable<SourceBoundary> GroupBoundaries(CalibrationGroup group)
    {
        yield return PolarBoundary([(group.SpineInnerRadiusMm, group.AngleDegrees), (group.SpineOuterRadiusMm, group.AngleDegrees)]);
    }

    private static CalibrationGroup[] BuildGroups(CalibrationParameters parameters, double crossWidth, int count, double angleStep = 90)
    {
        return Enumerable.Range(0, count).Select(index =>
        {
            double angle = -90 + index * angleStep;
            return new CalibrationGroup($"C-{index}", index, CalibrationGroupRole.Coarse, angle, [],
                0, angle, angle, crossWidth,
                parameters.DrawingInnerRadiusMm, parameters.DrawingOuterRadiusMm);
        }).ToArray();
    }

    private static IEnumerable<CalibrationCell> BuildSpineCells(IReadOnlyList<CalibrationGroup> groups)
    {
        foreach (CalibrationGroup group in groups)
        {
            var line = new CalibrationRadialLine(group.SpineInnerRadiusMm, group.SpineOuterRadiusMm, group.AngleDegrees);
            CalibrationPoint first = CalibrationArcShape.ToPoint(line.InnerRadiusMm, line.AngleDegrees);
            CalibrationPoint last = CalibrationArcShape.ToPoint(line.OuterRadiusMm, line.AngleDegrees);
            yield return new($"{group.Id}-spine", CalibrationSector.Coarse,
                Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Abs(last.X - first.X),
                Math.Abs(last.Y - first.Y), RadialLine: line);
        }
    }

    private sealed record SourceBoundary(IReadOnlyList<CalibrationPoint> Corners,
        (double Radius, double Degrees)[]? Polar, bool Closed);
}
