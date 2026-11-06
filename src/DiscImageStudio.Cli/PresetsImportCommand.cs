using System.Text.Json;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cli;

/// <summary>
/// Imports a schema 4 preset JSON into the user preset file, merging by id.
/// Used by the macOS GUI (Home "Import presets…" and "Save calibration as preset").
/// </summary>
internal static class PresetsImportCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        string? sourcePath = null;
        string? userPathOption = null;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument.Equals("--file", StringComparison.Ordinal))
            {
                if (sourcePath is not null) throw new ArgumentException("--file was specified more than once.");
                if (++index >= arguments.Count) throw new ArgumentException("Missing value for --file.");
                sourcePath = arguments[index];
                continue;
            }
            if (argument.StartsWith("--file=", StringComparison.Ordinal))
            {
                if (sourcePath is not null) throw new ArgumentException("--file was specified more than once.");
                sourcePath = argument["--file=".Length..];
                continue;
            }
            if (argument.Equals("--path", StringComparison.Ordinal))
            {
                if (userPathOption is not null) throw new ArgumentException("--path was specified more than once.");
                if (++index >= arguments.Count) throw new ArgumentException("Missing value for --path.");
                userPathOption = arguments[index];
                continue;
            }
            if (argument.StartsWith("--path=", StringComparison.Ordinal))
            {
                if (userPathOption is not null) throw new ArgumentException("--path was specified more than once.");
                userPathOption = argument["--path=".Length..];
                continue;
            }
            throw new ArgumentException(
                $"presets-import does not accept '{argument}'; supported options are --file FILE and --path FILE.");
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("--file is required.");
        string sourceFullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourceFullPath))
            throw new ArgumentException($"Preset JSON does not exist: {sourceFullPath}");

        string userPath = Path.GetFullPath(string.IsNullOrWhiteSpace(userPathOption)
            ? DiscPresetJsonStore.GetDefaultPath()
            : userPathOption);
        DiscPresetJsonDocument incoming = LoadIncoming(sourceFullPath);
        DiscPresetJsonDocument user = File.Exists(userPath)
            ? DiscPresetJsonStore.Load(userPath)
            : DiscPresetJsonStore.CreateInitialUserDocument();

        DiscPresetJsonDocument merged = user with
        {
            CdPresets = MergeById(user.CdPresets, incoming.CdPresets, value => value.Id),
            DvdPresets = MergeById(user.DvdPresets, incoming.DvdPresets, value => value.Id),
            BluRayPresets = MergeById(
                user.BluRayPresets ?? [],
                incoming.BluRayPresets ?? [],
                value => value.Id),
            DisabledCdPresetIds = UnionIds(user.DisabledCdPresetIds, incoming.DisabledCdPresetIds),
            DisabledDvdPresetIds = UnionIds(user.DisabledDvdPresetIds, incoming.DisabledDvdPresetIds),
            DisabledBluRayPresetIds = UnionIds(user.DisabledBluRayPresetIds, incoming.DisabledBluRayPresetIds),
        };
        DiscPresetJsonStore.Save(userPath, merged);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            path = userPath,
            cdPresets = merged.CdPresets.Count,
            dvdPresets = merged.DvdPresets.Count,
            bluRayPresets = merged.BluRayPresets?.Count ?? 0,
        }));
        return 0;
    }

    /// <summary>
    /// Accepts a schema 4 document, or a single preset (bare object or under "preset")
    /// as produced by disc-probe. The family is inferred from the field shape.
    /// </summary>
    private static DiscPresetJsonDocument LoadIncoming(string path)
    {
        string text = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        bool isDocument = root.ValueKind == JsonValueKind.Object
            && (root.TryGetProperty("cdPresets", out _)
                || root.TryGetProperty("dvdPresets", out _)
                || root.TryGetProperty("bluRayPresets", out _)
                || root.TryGetProperty("schemaVersion", out _));
        if (isDocument) return DiscPresetJsonStore.Load(path);

        JsonElement preset = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("preset", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;
        if (preset.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Preset JSON must be a schema 4 document or a single preset object.");

        string family = preset.TryGetProperty("invertMapping", out _)
            ? "bluRay"
            : preset.TryGetProperty("pitchLinear", out _)
                || preset.TryGetProperty("channelBitLengthNm", out _)
                    ? "dvd"
                    : "cd";
        var wrapper = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 4,
            ["cdPresets"] = family == "cd" ? new object[] { preset } : Array.Empty<object>(),
            ["dvdPresets"] = family == "dvd" ? new object[] { preset } : Array.Empty<object>(),
            ["bluRayPresets"] = family == "bluRay" ? new object[] { preset } : Array.Empty<object>(),
        };
        string wrapperJson = JsonSerializer.Serialize(wrapper);
        DiscPresetJsonDocument parsed = JsonSerializer.Deserialize<DiscPresetJsonDocument>(
            wrapperJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Preset JSON could not be parsed.");
        return parsed;
    }

    private static List<T> MergeById<T>(
        IReadOnlyList<T> existing,
        IReadOnlyList<T> incoming,
        Func<T, string> id)
    {
        List<T> result = [.. existing];
        Dictionary<string, int> indexById = result
            .Select((value, i) => (id(value), i))
            .GroupBy(pair => pair.Item1, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().i, StringComparer.Ordinal);
        foreach (T value in incoming)
        {
            string key = id(value);
            if (indexById.TryGetValue(key, out int index))
            {
                result[index] = value;
            }
            else
            {
                indexById[key] = result.Count;
                result.Add(value);
            }
        }
        return result;
    }

    private static IReadOnlyList<string>? UnionIds(
        IReadOnlyList<string>? existing,
        IReadOnlyList<string>? incoming)
    {
        if (existing is null && incoming is null) return null;
        List<string> result = [.. existing ?? []];
        foreach (string id in incoming ?? [])
            if (!result.Contains(id, StringComparer.Ordinal)) result.Add(id);
        return result;
    }
}
