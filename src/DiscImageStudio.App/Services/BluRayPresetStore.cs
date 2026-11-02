using DiscImageStudio.Localization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using DiscImageStudio.BluRay;

namespace DiscImageStudio.Services;

/// <summary>Independent user BD catalog. Constructors do no I/O; saves never touch CD/DVD presets.</summary>
public sealed class BluRayPresetStore(string path)
{
    public const string FileName = "blu-ray-presets.json";
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscImageStudio", FileName);

    public IReadOnlyList<BluRayDiscPreset> Load()
        => File.Exists(Path) ? ReadCatalog(Path).Presets.Select(ToPreset).ToArray() : [];

    public BluRayDiscPreset Save(string name, BluRayDiscParameters parameters, bool invert)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Entry entry = new(NewId(), name.Trim(), parameters, invert);
        Validate(entry);
        Document existing = File.Exists(Path) ? ReadCatalog(Path) : new(1, []);
        Write(new(1, [.. existing.Presets, entry]));
        return ToPreset(entry);
    }

    /// <summary>Imports this catalog shape or the original bd-info export, preserving its geometry and polarity.</summary>
    public IReadOnlyList<BluRayDiscPreset> Import(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Entry[] imported;
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(sourcePath), new JsonDocumentOptions
        {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
        }))
        {
            JsonElement root = json.RootElement;
            if (Find(root, "presets") is not null)
                imported = ReadCatalog(sourcePath).Presets.Select(entry => entry with { Id = NewId() }).ToArray();
            else
            {
                JsonElement parametersJson = Find(root, "parameters")
                    ?? throw new InvalidDataException(L.T("请选择蓝光预设库或之前导出的蓝光参数 JSON（缺少 parameters）。"));
                ValidateGeometryFields(parametersJson);
                BluRayDiscParameters parameters = parametersJson.Deserialize<BluRayDiscParameters>(JsonOptions)
                    ?? throw new InvalidDataException(L.T("蓝光 parameters 不能为空。"));
                bool invert = false;
                if (Find(root, "luminanceMapping") is JsonElement mapping && Find(mapping, "invert") is JsonElement polarity)
                {
                    if (polarity.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException(L.T("蓝光 luminanceMapping.invert 必须是布尔值。"));
                    invert = polarity.GetBoolean();
                }
                imported = [new(NewId(), System.IO.Path.GetFileNameWithoutExtension(sourcePath), parameters, invert)];
            }
        }
        if (imported.Length == 0) throw new InvalidDataException(L.T("该文件没有可导入的蓝光预设。"));
        foreach (Entry entry in imported) Validate(entry);
        // Load only after the entire import has passed validation. A malformed
        // existing file also blocks the save; it is never silently replaced.
        Document existing = File.Exists(Path) ? ReadCatalog(Path) : new(1, []);
        Write(new(1, [.. existing.Presets, .. imported]));
        return imported.Select(ToPreset).ToArray();
    }

    private static Document ReadCatalog(string path)
    {
        Document document;
        try
        {
            string source = File.ReadAllText(path);
            using JsonDocument json = JsonDocument.Parse(source, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (Find(json.RootElement, "presets") is JsonElement presets && presets.ValueKind == JsonValueKind.Array)
                foreach (JsonElement preset in presets.EnumerateArray())
                    ValidateGeometryFields(Find(preset, "parameters")
                        ?? throw new InvalidDataException(L.T("蓝光预设缺少 parameters。")));
            document = JsonSerializer.Deserialize<Document>(source, JsonOptions)
                ?? throw new InvalidDataException(L.T("蓝光预设 JSON 不能为空。"));
        }
        catch (JsonException exception) { throw new InvalidDataException(L.T("蓝光预设 JSON 格式无效。"), exception); }
        if (document.SchemaVersion != 1 || document.Presets is null)
            throw new InvalidDataException(L.T("蓝光预设 JSON 必须包含 schemaVersion: 1 和 presets 列表。"));
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (Entry entry in document.Presets)
        {
            Validate(entry);
            if (!ids.Add(entry.Id)) throw new InvalidDataException(L.F($"蓝光预设 ID 重复：{entry.Id}"));
        }
        return document;
    }

    private void Write(Document document)
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(Entry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.DisplayName)
            || entry.Parameters is null || entry.Id == BluRayDiscPreset.Manual.Id
            || BluRayDiscPreset.BuiltIn.Any(preset => preset.Id == entry.Id))
            throw new InvalidDataException(L.T("蓝光预设必须包含独立 ID、名称和参数，不能使用内置或未保存预设的 ID。"));
        entry.Parameters.Validate();
        if (entry.Parameters.StartAngleDegrees != 0)
            throw new InvalidDataException(L.T("当前蓝光制作与标定仅支持起始角 0°；此 JSON 的非零起始角不能导入，也不会自动改为 0°。"));
    }
    private static JsonElement? Find(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(property => (JsonElement?)property.Value).FirstOrDefault()
            : null;
    private static void ValidateGeometryFields(JsonElement parameters)
    {
        foreach (string field in new[] { "totalSectors", "layerCount", "innerRadiusMm", "outerRadiusMm" })
            if (Find(parameters, field) is null)
                throw new InvalidDataException(L.F($"蓝光 parameters 缺少 {field}，不能使用默认值替代导入的参数。"));
    }
    private static string NewId() => "user-bd-" + Guid.NewGuid().ToString("N");
    private static BluRayDiscPreset ToPreset(Entry entry)
        => new(entry.Id, entry.DisplayName, entry.Parameters.LayerCount, entry.Parameters, entry.InvertMapping);
    private sealed record Entry(string Id, string DisplayName, BluRayDiscParameters Parameters, bool InvertMapping);
    private sealed record Document(int SchemaVersion, IReadOnlyList<Entry> Presets);
}
