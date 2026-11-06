namespace DiscImageStudio.Cd;

public sealed record CdDiscPreset(
    string Id,
    string DisplayName,
    string Description,
    long Sectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    string? DisplayNameResourceKey = null,
    string? DescriptionResourceKey = null,
    bool IsCustom = false,
    string? Contributor = null,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0)
{
    public static CdDiscPreset Manual { get; } = new(
        "__manual__",
        "自定义参数（未保存）",
        "生成参数已在界面中手动调整；如需长期保存，请写入用户预设 JSON。",
        359_849,
        24.5,
        56.8,
        IsCustom: true);
}
