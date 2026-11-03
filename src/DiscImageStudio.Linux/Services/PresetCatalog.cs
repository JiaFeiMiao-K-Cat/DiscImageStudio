using DiscImageStudio.Core;

namespace DiscImageStudio.Linux.Services;

/// <summary>One disc preset as the GUI uses it: geometry for any family, with display text
/// already translated to English.</summary>
public sealed record DiscPreset(
    string Id,
    OpticalDiscFamily Family,
    string Name,
    string Description,
    long Sectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    bool InvertMapping = false,
    bool IsBuiltIn = true)
{
    public string FamilyLabel => DiscFamilies.Label(Family);

    public override string ToString() => Name;
}

public static class DiscFamilies
{
    public static string Label(OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };

    public static string LongLabel(OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.Dvd => "DVD±R",
        OpticalDiscFamily.BluRay => "BD-R",
        _ => "CD-R",
    };

    public static string ProjectKey(OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.Dvd => ProjectDocument.DvdFamily,
        OpticalDiscFamily.BluRay => ProjectDocument.BluRayFamily,
        _ => ProjectDocument.CdFamily,
    };

    public static string Icon(OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.Dvd => Controls.Icons.Disc,
        OpticalDiscFamily.BluRay => Controls.Icons.DiscAlbum,
        _ => Controls.Icons.Disc3,
    };
}

/// <summary>Built-in presets from Core merged with the user's preset file, the same file
/// the Windows app edits (<see cref="DiscPresetJsonStore.GetDefaultPath"/>, which resolves
/// to ~/.local/share/DiscImageStudio/disc-presets.json on Linux).</summary>
public static class PresetCatalog
{
    private static IReadOnlyList<DiscPreset>? _all;

    public static string UserPresetPath => DiscPresetJsonStore.GetDefaultPath();

    public static string? LoadWarning { get; private set; }

    public static IReadOnlyList<DiscPreset> All => _all ??= Load();

    public static IReadOnlyList<DiscPreset> For(OpticalDiscFamily family)
        => All.Where(preset => preset.Family == family).ToList();

    public static DiscPreset Default(OpticalDiscFamily family) => For(family)[0];

    public static void Reload() => _all = null;

    private static IReadOnlyList<DiscPreset> Load()
    {
        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
        DiscPresetJsonDocument merged = builtIn;
        try
        {
            if (File.Exists(UserPresetPath))
            {
                merged = DiscPresetJsonStore.Merge(builtIn, DiscPresetJsonStore.Load(UserPresetPath));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            LoadWarning = $"User presets were ignored ({UserPresetPath}): {Translator.T(exception.Message)}";
        }

        HashSet<string> builtInIds = new(
            builtIn.CdPresets.Select(p => p.Id)
                .Concat(builtIn.DvdPresets.Select(p => p.Id))
                .Concat((builtIn.BluRayPresets ?? []).Select(p => p.Id)),
            StringComparer.Ordinal);

        List<DiscPreset> presets = [];
        presets.AddRange(merged.CdPresets.Select(p => new DiscPreset(
            p.Id, OpticalDiscFamily.CompactDisc, Translator.T(p.DisplayName), Translator.T(p.Description),
            p.Sectors, p.InnerRadiusMm, p.OuterRadiusMm, IsBuiltIn: builtInIds.Contains(p.Id))));
        presets.AddRange(merged.DvdPresets.Select(p => new DiscPreset(
            p.Id, OpticalDiscFamily.Dvd, Translator.T(p.DisplayName), Translator.T(p.Description),
            p.TotalSectors, p.InnerRadiusMm, p.OuterRadiusMm, p.PitchLinear, p.PitchQuadratic, p.PitchCubic,
            IsBuiltIn: builtInIds.Contains(p.Id))));
        presets.AddRange((merged.BluRayPresets ?? []).Select(p => new DiscPreset(
            p.Id, OpticalDiscFamily.BluRay, Translator.T(p.DisplayName), Translator.T(p.Description),
            p.TotalSectors, p.InnerRadiusMm, p.OuterRadiusMm, InvertMapping: p.InvertMapping,
            IsBuiltIn: builtInIds.Contains(p.Id))));
        return presets;
    }
}
