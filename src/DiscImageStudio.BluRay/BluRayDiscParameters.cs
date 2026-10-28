namespace DiscImageStudio.BluRay;

/// <summary>ISO/IEC 30190:2021 BD-R data-zone model. Drive spare areas and remapping
/// can change the usable logical capacity and its relation to physical addresses.</summary>
public sealed record BluRayDiscParameters(
    uint TotalSectors = BluRayDiscParameters.SectorsPerLayer,
    int LayerCount = 1,
    double InnerRadiusMm = 24.0,
    double OuterRadiusMm = 58.0,
    double StartAngleDegrees = 0)
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
    public long ContentLength => checked((long)TotalSectors * BytesPerSector);

    public void Validate()
    {
        if (LayerCount != 1)
            throw new ArgumentOutOfRangeException(nameof(LayerCount), "Only single-layer 25 GB BD-R is currently supported.");
        if (TotalSectors == 0 || TotalSectors % SectorsPerCluster != 0 || TotalSectors > SectorsPerLayer)
            throw new ArgumentOutOfRangeException(nameof(TotalSectors),
                $"BD-R total sectors must be a positive multiple of 32, no greater than {SectorsPerLayer}.");
        if (!double.IsFinite(InnerRadiusMm) || !double.IsFinite(OuterRadiusMm)
            || InnerRadiusMm <= 0 || OuterRadiusMm <= InnerRadiusMm || OuterRadiusMm > 60)
            throw new ArgumentOutOfRangeException(nameof(OuterRadiusMm), "BD-R radii must satisfy 0 < inner < outer <= 60 mm.");
        if (!double.IsFinite(StartAngleDegrees))
            throw new ArgumentOutOfRangeException(nameof(StartAngleDegrees));
    }

    /// <summary>Nominal data-zone address; does not include drive-specific spare-area
    /// allocation or defect relocation.</summary>
    public uint GetPhysicalSectorNumber(uint sector)
    {
        Validate();
        if (sector >= TotalSectors) throw new ArgumentOutOfRangeException(nameof(sector));
        return FirstLayer0PhysicalSector + sector;
    }
}

public sealed record BluRayDiscPreset(string Id, string DisplayName, int LayerCount,
    BluRayDiscParameters? Geometry = null, bool InvertMapping = false, bool IsCustom = false)
{
    public uint TotalSectors => Geometry?.TotalSectors ?? checked(BluRayDiscParameters.SectorsPerLayer * (uint)LayerCount);
    public long ContentLength => (long)TotalSectors * BluRayDiscParameters.BytesPerSector;
    public bool IsUserPreset => Geometry is not null && !IsCustom
        && !BuiltIn.Any(preset => preset.Id.Equals(Id, StringComparison.OrdinalIgnoreCase));
    public BluRayDiscParameters CreateParameters() => Geometry ?? new(TotalSectors, LayerCount);

    public static BluRayDiscPreset Manual { get; } = new("__manual__", "自定义参数（未保存）", 1, IsCustom: true);

    public static IReadOnlyList<BluRayDiscPreset> BuiltIn { get; } = Array.AsReadOnly(
        new BluRayDiscPreset[]
        {
            new("bd-r-sl-25gb", "BD-R 单层 25 GB", 1),
            new("bd-r-ritek-10x", "铼德 BD-R 10x", 1,
                new(12_219_392, 1, 23.98822105616562, 58.023234731100764, 0), InvertMapping: false),
            new("bd-r-verbatim-64099", "Verbatim BD-R (64099)", 1,
                new(12_219_392, 1, 23.987042157339335, 57.987960341783236, 0), InvertMapping: false),
        });

    public static BluRayDiscPreset Find(string id)
        => BuiltIn.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown BD-R preset '{id}'.", nameof(id));
}
