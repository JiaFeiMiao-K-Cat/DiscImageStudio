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
    string? Contributor = null)
{
    public static DvdDiscPreset Manual { get; } = new(
        "__manual__",
        "自定义参数（未保存）",
        "生成参数已在界面中手动调整；如需长期保存，请写入用户预设 JSON。",
        2_295_104,
        24.0,
        58.0,
        IsCustom: true);
}
