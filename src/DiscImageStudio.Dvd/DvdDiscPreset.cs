namespace DiscImageStudio.Dvd;

public sealed record DvdDiscPreset(
    string Id,
    string DisplayName,
    string Description,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    string? DisplayNameResourceKey = null,
    string? DescriptionResourceKey = null,
    bool IsCustom = false,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    string? Contributor = null,
    uint? UsableSectors = null)
{
    public uint ResolvedUsableSectors => ResolveUsableSectors(TotalSectors, UsableSectors);

    // A conservative fallback for legacy presets, not a deduction from device-reported free space.
    public static uint ResolveUsableSectors(uint totalSectors, uint? usableSectors = null)
        => usableSectors ?? (totalSectors > 608 ? (totalSectors - 608) / 16 * 16 : 0);

    public static DvdDiscPreset Manual { get; } = new(
        "__manual__",
        "自定义参数（未保存）",
        "生成参数已在界面中手动调整；如需长期保存，请写入用户预设 JSON。",
        2_295_104,
        24.0,
        58.0,
        IsCustom: true);
}
