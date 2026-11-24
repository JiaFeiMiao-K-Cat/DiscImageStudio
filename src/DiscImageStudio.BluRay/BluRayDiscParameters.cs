using DiscImageStudio.Core;

namespace DiscImageStudio.BluRay;

/// <summary>Single-layer BD data-zone geometry with a contiguous logical user area.</summary>
public sealed record BluRayDiscParameters(
    uint TotalSectors = BluRayDiscParameters.SectorsPerLayer,
    int LayerCount = 1,
    double InnerRadiusMm = 24.0,
    double OuterRadiusMm = 58.0,
    double StartAngleDegrees = 0,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    uint? UsableSectors = null)
{
    public const int BytesPerSector = 2048;
    public const int SectorsPerCluster = 32;
    public const int PayloadBytesPerCluster = BytesPerSector * SectorsPerCluster;
    public const uint SectorsPerLayer = 12_219_392;
    public const uint FirstLayer0PhysicalSector = 0x00100000;
    public const int ChannelBitsPerPhysicalCluster = 496 * 1932;
    public const int ChannelBitsPerRecordingUnit = 2760 + ChannelBitsPerPhysicalCluster + 1104;
    public const double ChannelBitLengthNm = 74.5;
    public const double NominalTrackPitchMicrometers = 0.320;
    public uint FirstUserDataPhysicalSector { get; private init; } = FirstLayer0PhysicalSector;
    private uint? UserAreaSectors { get; init; }
    internal uint UserDataSectorOffset => FirstUserDataPhysicalSector - FirstLayer0PhysicalSector;
    internal uint LogicalGeometrySectors => Math.Min(UserAreaSectors ?? uint.MaxValue, TotalSectors - UserDataSectorOffset);
    public uint OutputSectors => UsableSectors ?? TotalSectors;
    public long ContentLength => checked((long)OutputSectors * BytesPerSector);
    internal double OutputInnerRadiusMm => UserDataSectorOffset == 0 ? InnerRadiusMm
        : new BluRayTrackGeometry(this).AtSector(0).RadiusMm;
    public double OutputOuterRadiusMm => OutputSectors + UserDataSectorOffset == TotalSectors ? OuterRadiusMm
        : new BluRayTrackGeometry(this).AtSector(OutputSectors).RadiusMm;
    public bool HasVariablePitch => PitchLinear != 0 || PitchQuadratic != 0 || PitchCubic != 0;

    public void Validate()
    {
        if (LayerCount != 1)
            throw new ArgumentOutOfRangeException(nameof(LayerCount), "Only single-layer 25 GB BD geometry is supported.");
        if (TotalSectors == 0 || TotalSectors % SectorsPerCluster != 0 || TotalSectors > SectorsPerLayer)
            throw new ArgumentOutOfRangeException(nameof(TotalSectors),
                $"BD total sectors must be a positive multiple of 32, no greater than {SectorsPerLayer}.");
        if (UserDataSectorOffset >= TotalSectors)
            throw new ArgumentOutOfRangeException(nameof(FirstUserDataPhysicalSector), "BD user area must start within the geometry.");
        if (OutputSectors == 0 || OutputSectors > LogicalGeometrySectors || OutputSectors % SectorsPerCluster != 0)
            throw new ArgumentOutOfRangeException(nameof(UsableSectors), "BD output sectors must be a positive multiple of 32 within the geometry.");
        if (!double.IsFinite(InnerRadiusMm) || !double.IsFinite(OuterRadiusMm)
            || InnerRadiusMm <= 0 || OuterRadiusMm <= InnerRadiusMm || OuterRadiusMm > 60)
            throw new ArgumentOutOfRangeException(nameof(OuterRadiusMm), "BD radii must satisfy 0 < inner < outer <= 60 mm.");
        if (!double.IsFinite(StartAngleDegrees))
            throw new ArgumentOutOfRangeException(nameof(StartAngleDegrees));
        if (HasVariablePitch)
            DvdTrackGeometry.ValidatePitchCoefficients(PitchLinear, PitchQuadratic, PitchCubic);
    }

    /// <summary>Fits an existing drawing to a drive-verified contiguous user area without
    /// rescaling the physical track or increasing the requested output length.</summary>
    public BluRayDiscParameters WithPhysicalLayout(uint firstUserDataPhysicalSector, uint userDataSectors)
    {
        Validate();
        if (firstUserDataPhysicalSector < FirstLayer0PhysicalSector)
            throw new ArgumentOutOfRangeException(nameof(firstUserDataPhysicalSector),
                "The BD user area cannot start before the layer-0 data zone.");
        uint offset = firstUserDataPhysicalSector - FirstLayer0PhysicalSector;
        if (offset >= TotalSectors || offset % SectorsPerCluster != 0)
            throw new ArgumentOutOfRangeException(nameof(firstUserDataPhysicalSector),
                "The first BD user sector must be an aligned layer-0 address within the geometry.");
        if (userDataSectors == 0 || userDataSectors % SectorsPerCluster != 0
            || userDataSectors > SectorsPerLayer - offset)
            throw new ArgumentOutOfRangeException(nameof(userDataSectors),
                "The BD user area must be cluster-aligned and fit within layer 0.");
        uint outputSectors = Math.Min(OutputSectors, Math.Min(userDataSectors, TotalSectors - offset));
        return this with
        {
            FirstUserDataPhysicalSector = firstUserDataPhysicalSector,
            UserAreaSectors = userDataSectors < TotalSectors - offset ? userDataSectors : null,
            UsableSectors = outputSectors == OutputSectors ? UsableSectors : outputSectors,
        };
    }

    /// <summary>Maps a logical sector to its physical address within the contiguous user area.</summary>
    public uint GetPhysicalSectorNumber(uint sector)
    {
        Validate();
        if (sector >= LogicalGeometrySectors) throw new ArgumentOutOfRangeException(nameof(sector));
        return FirstUserDataPhysicalSector + sector;
    }
}

public sealed record BluRayDiscPreset(string Id, string DisplayName, int LayerCount,
    BluRayDiscParameters? Geometry = null, bool InvertMapping = false, bool IsCustom = false,
    string? Contributor = null)
{
    public uint TotalSectors => Geometry?.TotalSectors ?? checked(BluRayDiscParameters.SectorsPerLayer * (uint)LayerCount);
    public uint? UsableSectors => Geometry?.UsableSectors;
    public uint ResolvedUsableSectors => ResolveUsableSectors(TotalSectors, UsableSectors);
    public long ContentLength => (long)ResolvedUsableSectors * BluRayDiscParameters.BytesPerSector;
    public static uint ResolveUsableSectors(uint totalSectors, uint? usableSectors = null)
        => usableSectors ?? totalSectors / 32 * 32;
    public bool IsUserPreset => Geometry is not null && !IsCustom
        && !BuiltIn.Any(preset => preset.Id.Equals(Id, StringComparison.OrdinalIgnoreCase));
    public BluRayDiscParameters CreateParameters()
        => (Geometry ?? new(TotalSectors, LayerCount)) with { UsableSectors = ResolvedUsableSectors };

    public static BluRayDiscPreset Manual { get; } = new("__manual__", "自定义参数（未保存）", 1, IsCustom: true);

    public static IReadOnlyList<BluRayDiscPreset> BuiltIn { get; } = Array.AsReadOnly(
        new BluRayDiscPreset[]
        {
            new("bd-r-sl-25gb", "BD-R 单层 25 GB", 1),
            new("bd-r-ritek-10x", "铼德 BD-R 10x", 1,
                new(12_219_392, 1, 23.98822105616562, 58.023234731100764, 0), InvertMapping: false),
            new("bd-r-ritek-12x", "铼德 BD-R 12x", 1,
                new(12_219_392, 1, 23.98795, 58.02236, 0), InvertMapping: false,
                Contributor: "DeepAQ"),
            new("bd-r-verbatim-64099", "Verbatim BD-R (64099)", 1,
                new(12_219_392, 1, 23.987042157339335, 57.987960341783236, 0), InvertMapping: false),
            new("bd-r-verbatim-64293", "Verbatim BD-R (64293)", 1,
                new(12_219_392, 1, 23.987042157339335, 57.987960341783236, 0), InvertMapping: false,
                Contributor: "bczhc"),
            new("bd-re-verbatim-43614", "Verbatim BD-RE (43614)", 1,
                new(12_219_392, 1, 23.987215945206195, 57.9887794672236,
                    PitchLinear: 9.828857433309327E-06, PitchQuadratic: -2.23099595737756E-05,
                    PitchCubic: 1.4515048946969683E-05, UsableSectors: 12_219_392), InvertMapping: false),
        });

    public static BluRayDiscPreset Find(string id)
        => BuiltIn.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown Blu-ray preset '{id}'.", nameof(id));
}
