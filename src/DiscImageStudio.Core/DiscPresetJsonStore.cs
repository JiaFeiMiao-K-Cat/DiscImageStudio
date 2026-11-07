using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Core;

public sealed record CdDiscPresetDefinition(
    string Id,
    string DisplayName,
    string Description,
    long Sectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    // Retained so a user file keeps every field it was written with. The CD engine scans at
    // the ECMA-130 standard speed regardless of this value and the pages no longer expose a
    // form for it, so the field is carried, validated and written back unchanged.
    double LinearVelocityMmPerSecond = 1_200,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayNameResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DescriptionResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Contributor = null,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? UsableSectors = null);

public sealed record DvdDiscPresetDefinition(
    string Id,
    string DisplayName,
    string Description,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    // Same role as the CD linear velocity above: retained for file compatibility, fixed at
    // the DVD standard channel-bit length while generating.
    double ChannelBitLengthNm = 133.3,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayNameResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DescriptionResourceKey = null,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Contributor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    uint? UsableSectors = null);

public sealed record BluRayDiscPresetDefinition(
    string Id,
    string DisplayName,
    string Description,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    bool InvertMapping = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayNameResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DescriptionResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Contributor = null,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    uint? UsableSectors = null);

public sealed record DiscPresetJsonDocument(
    int SchemaVersion,
    IReadOnlyList<CdDiscPresetDefinition> CdPresets,
    IReadOnlyList<DvdDiscPresetDefinition> DvdPresets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DisabledCdPresetIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DisabledDvdPresetIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FallbackLanguage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<BluRayDiscPresetDefinition>? BluRayPresets = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DisabledBluRayPresetIds = null);

public static class DiscPresetJsonStore
{
    public const int CurrentSchemaVersion = 4;

    /// <summary>Schema 3 was written by a build that dropped <c>linearVelocityMmPerSecond</c>
    /// and <c>channelBitLengthNm</c> from the preset shape. Schema 2 predates the BD catalog.
    /// Both stay readable so a version bump cannot strand presets a user already saved.
    /// Schema 4 adds <c>bluRayPresets</c>.</summary>
    private const int InterimSchemaVersion = 3;
    private const int LegacySchemaVersion = 2;

    public const string FileName = "disc-presets.json";

    private const string BuiltInResourceName =
        "DiscImageStudio.Core.BuiltInDiscPresets.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static string GetDefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscImageStudio",
            FileName);

    public static DiscPresetJsonDocument LoadBuiltIn()
    {
        using Stream stream = typeof(DiscPresetJsonStore).Assembly
            .GetManifestResourceStream(BuiltInResourceName)
            ?? throw new InvalidOperationException("程序缺少内置盘片预设资源。");
        return Deserialize(stream, (FormattableString)$"内置盘片预设");
    }

    public static DiscPresetJsonDocument CreateInitialUserDocument()
        => new(
            CurrentSchemaVersion,
            [
                new CdDiscPresetDefinition(
                    "user-custom-cd",
                    "自定义 CD 参数",
                    "用户预设，可直接修改或复制后添加更多 CD 参数。",
                    359_849,
                    24.5,
                    56.8,
                    1_200),
            ],
            [
                new DvdDiscPresetDefinition(
                    "user-custom-dvd",
                    "自定义 DVD 参数",
                    "用户预设，可直接修改或复制后添加更多 DVD 参数。",
                    2_295_104,
                    24.0,
                    58.0,
                    133.3),
            ],
            DisabledCdPresetIds: [],
            DisabledDvdPresetIds: [],
            BluRayPresets:
            [
                new BluRayDiscPresetDefinition(
                    "user-custom-bd",
                    "自定义蓝光参数",
                    "用户预设，可直接修改或复制后添加更多蓝光参数。",
                    12_219_392,
                    24.0,
                    58.0),
            ],
            DisabledBluRayPresetIds: []);

    public static DiscPresetJsonDocument LoadOrCreate(
        string path,
        DiscPresetJsonDocument initialUserDocument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(initialUserDocument);

        if (!File.Exists(path))
        {
            Save(path, initialUserDocument);
        }

        return Load(path);
    }

    public static DiscPresetJsonDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return Deserialize(stream, Path.GetFileName(path));
    }

    public static DiscPresetJsonDocument Merge(
        DiscPresetJsonDocument builtIn,
        DiscPresetJsonDocument user)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(user);
        Validate(builtIn);
        Validate(user);

        HashSet<string> disabledCdIds = new(
            user.DisabledCdPresetIds ?? [],
            StringComparer.Ordinal);
        HashSet<string> disabledDvdIds = new(
            user.DisabledDvdPresetIds ?? [],
            StringComparer.Ordinal);
        HashSet<string> disabledBluRayIds = new(
            user.DisabledBluRayPresetIds ?? [],
            StringComparer.Ordinal);
        IReadOnlyList<BluRayDiscPresetDefinition> builtInBluRay = builtIn.BluRayPresets ?? [];
        IReadOnlyList<BluRayDiscPresetDefinition> userBluRay = user.BluRayPresets ?? [];
        DiscPresetJsonDocument merged = new(
            CurrentSchemaVersion,
            MergePresets(
                builtIn.CdPresets,
                user.CdPresets,
                disabledCdIds,
                value => value.Id),
            MergePresets(
                builtIn.DvdPresets,
                user.DvdPresets,
                disabledDvdIds,
                value => value.Id),
            FallbackLanguage: builtIn.FallbackLanguage,
            BluRayPresets: MergePresets(
                builtInBluRay,
                userBluRay,
                disabledBluRayIds,
                value => value.Id),
            DisabledBluRayPresetIds: null);
        Validate(merged);
        return merged;
    }

    public static void Save(string path, DiscPresetJsonDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        // Older schemas are migrated on write; the BD catalog starts empty until merged
        // with the built-in presets on the next load.
        DiscPresetJsonDocument migrated = document with
        {
            SchemaVersion = CurrentSchemaVersion,
            BluRayPresets = document.BluRayPresets ?? [],
        };
        Validate(migrated);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw DiagnosticMessage.Argument($"预设 JSON 路径必须包含目录。", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, migrated, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void Validate(DiscPresetJsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion is not (LegacySchemaVersion or InterimSchemaVersion or CurrentSchemaVersion))
        {
            throw DiagnosticMessage.InvalidData(
                $"不支持 schemaVersion={document.SchemaVersion}；当前支持 {LegacySchemaVersion}、{InterimSchemaVersion} 与 {CurrentSchemaVersion}。请把顶层 schemaVersion 改为受支持的值，或删除该文件让程序重新生成分层配置模板。");
        }

        if (document.SchemaVersion == CurrentSchemaVersion && document.BluRayPresets is null)
        {
            throw DiagnosticMessage.InvalidData($"schemaVersion 4 必须包含 bluRayPresets 列表。");
        }

        if (document.CdPresets is null)
        {
            throw DiagnosticMessage.InvalidData($"cdPresets 不能为空。");
        }

        if (document.DvdPresets is null)
        {
            throw DiagnosticMessage.InvalidData($"dvdPresets 不能为空。");
        }

        ValidateUniqueIds(document.CdPresets.Select(value => value?.Id), "CD");
        ValidateUniqueIds(document.DvdPresets.Select(value => value?.Id), "DVD");
        ValidateUniqueIds((document.BluRayPresets ?? []).Select(value => value?.Id), "Blu-ray");
        ValidateUniqueIds(document.DisabledCdPresetIds ?? [], "disabled CD");
        ValidateUniqueIds(document.DisabledDvdPresetIds ?? [], "disabled DVD");
        ValidateUniqueIds(document.DisabledBluRayPresetIds ?? [], "disabled Blu-ray");
        if (document.FallbackLanguage is not null)
        {
            ValidateText(document.FallbackLanguage, "document", "fallbackLanguage");
        }

        foreach (string id in document.DisabledCdPresetIds ?? [])
        {
            ValidateText(id, "disabled CD", "id");
        }

        foreach (string id in document.DisabledDvdPresetIds ?? [])
        {
            ValidateText(id, "disabled DVD", "id");
        }

        foreach (string id in document.DisabledBluRayPresetIds ?? [])
        {
            ValidateText(id, "disabled Blu-ray", "id");
        }

        foreach (CdDiscPresetDefinition? preset in document.CdPresets)
        {
            if (preset is null)
            {
                throw DiagnosticMessage.InvalidData($"CD 预设不能是 null。");
            }

            ValidateText(preset.Id, "CD", "id");
            ValidateText(preset.DisplayName, preset.Id, "displayName");
            ValidateText(preset.Description, preset.Id, "description");
            ValidateOptionalText(
                preset.DisplayNameResourceKey,
                preset.Id,
                "displayNameResourceKey");
            ValidateOptionalText(
                preset.DescriptionResourceKey,
                preset.Id,
                "descriptionResourceKey");
            ValidateOptionalText(preset.Contributor, preset.Id, "contributor");
            ValidateReservedId(preset.Id, "CD");
            if (preset.Sectors <= 0)
            {
                throw InvalidValue(preset.Id, "sectors", $"必须大于 0");
            }

            ValidateUsableSectors(preset.Id, preset.Sectors, preset.UsableSectors, 1);
            ValidateRadiusRange(preset.Id, preset.InnerRadiusMm, preset.OuterRadiusMm);
            ValidatePitch(preset.Id, preset.PitchLinear, preset.PitchQuadratic, preset.PitchCubic);
            ValidatePositiveFinite(
                preset.Id,
                "linearVelocityMmPerSecond",
                preset.LinearVelocityMmPerSecond);
        }

        foreach (DvdDiscPresetDefinition? preset in document.DvdPresets)
        {
            if (preset is null)
            {
                throw DiagnosticMessage.InvalidData($"DVD 预设不能是 null。");
            }

            ValidateText(preset.Id, "DVD", "id");
            ValidateText(preset.DisplayName, preset.Id, "displayName");
            ValidateText(preset.Description, preset.Id, "description");
            ValidateOptionalText(
                preset.DisplayNameResourceKey,
                preset.Id,
                "displayNameResourceKey");
            ValidateOptionalText(
                preset.DescriptionResourceKey,
                preset.Id,
                "descriptionResourceKey");
            ValidateOptionalText(preset.Contributor, preset.Id, "contributor");
            ValidateReservedId(preset.Id, "DVD");
            if (preset.TotalSectors == 0)
            {
                throw InvalidValue(preset.Id, "totalSectors", $"必须大于 0");
            }

            ValidateUsableSectors(preset.Id, preset.TotalSectors, preset.UsableSectors, 16);
            ValidateRadiusRange(preset.Id, preset.InnerRadiusMm, preset.OuterRadiusMm);
            ValidatePitch(preset.Id, preset.PitchLinear, preset.PitchQuadratic, preset.PitchCubic);
            ValidatePositiveFinite(preset.Id, "channelBitLengthNm", preset.ChannelBitLengthNm);
        }

        foreach (BluRayDiscPresetDefinition? preset in document.BluRayPresets ?? [])
        {
            if (preset is null)
            {
                throw DiagnosticMessage.InvalidData($"蓝光预设不能是 null。");
            }

            ValidateText(preset.Id, "Blu-ray", "id");
            ValidateText(preset.DisplayName, preset.Id, "displayName");
            ValidateText(preset.Description, preset.Id, "description");
            ValidateOptionalText(preset.DisplayNameResourceKey, preset.Id, "displayNameResourceKey");
            ValidateOptionalText(preset.DescriptionResourceKey, preset.Id, "descriptionResourceKey");
            ValidateOptionalText(preset.Contributor, preset.Id, "contributor");
            ValidateReservedId(preset.Id, "Blu-ray");
            if (preset.TotalSectors == 0
                || preset.TotalSectors > ProjectDocument.BluRaySectorsPerLayer
                || preset.TotalSectors % ProjectDocument.BluRaySectorsPerCluster != 0)
            {
                throw InvalidValue(
                    preset.Id,
                    "totalSectors",
                    $"必须是 32 的倍数且不超过 {ProjectDocument.BluRaySectorsPerLayer:N0}");
            }

            ValidateUsableSectors(preset.Id, preset.TotalSectors, preset.UsableSectors, 32);
            ValidateRadiusRange(preset.Id, preset.InnerRadiusMm, preset.OuterRadiusMm);
            ValidatePitch(preset.Id, preset.PitchLinear, preset.PitchQuadratic, preset.PitchCubic);
            if (preset.OuterRadiusMm > 60)
            {
                throw InvalidValue(preset.Id, "outerRadiusMm", $"不能超过 60");
            }
        }
    }

    private static void ValidateUsableSectors(string presetId, long total, long? usable, int alignment)
    {
        if (usable is { } value && (value <= 0 || value > total || value % alignment != 0))
        {
            throw InvalidValue(presetId, "usableSectors", $"必须大于 0、不超过总扇区数，且是 {alignment} 的倍数");
        }
    }

    private static DiscPresetJsonDocument Deserialize(Stream stream, object sourceName)
    {
        try
        {
            DiscPresetJsonDocument? document = JsonSerializer.Deserialize<DiscPresetJsonDocument>(
                stream,
                JsonOptions);
            if (document is null)
            {
                throw DiagnosticMessage.InvalidData($"{sourceName} 不能为空。");
            }

            Validate(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw DiagnosticMessage.InvalidData(
                $"{sourceName} 格式有误（第 {exception.LineNumber + 1} 行，第 {exception.BytePositionInLine + 1} 列）：{DiagnosticMessage.Detail(exception)}",
                exception);
        }
    }

    private static IReadOnlyList<TPreset> MergePresets<TPreset>(
        IReadOnlyList<TPreset> builtIn,
        IReadOnlyList<TPreset> user,
        IReadOnlySet<string> disabledIds,
        Func<TPreset, string> getId)
    {
        Dictionary<string, TPreset> userById = user.ToDictionary(getId, StringComparer.Ordinal);
        HashSet<string> builtInIds = new(builtIn.Select(getId), StringComparer.Ordinal);
        List<TPreset> merged = [];

        foreach (TPreset preset in builtIn)
        {
            string id = getId(preset);
            if (!disabledIds.Contains(id))
            {
                merged.Add(userById.TryGetValue(id, out TPreset? replacement) ? replacement : preset);
            }
        }

        foreach (TPreset preset in user)
        {
            string id = getId(preset);
            if (!builtInIds.Contains(id) && !disabledIds.Contains(id))
            {
                merged.Add(preset);
            }
        }

        return merged;
    }

    private static void ValidateUniqueIds(IEnumerable<string?> ids, string family)
    {
        HashSet<string> uniqueIds = new(StringComparer.Ordinal);
        foreach (string? id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id) && !uniqueIds.Add(id))
            {
                throw DiagnosticMessage.InvalidData($"{family} 预设 id '{id}' 重复。");
            }
        }
    }

    private static void ValidateReservedId(string id, string family)
    {
        if (string.Equals(id, "__manual__", StringComparison.Ordinal))
        {
            throw DiagnosticMessage.InvalidData(
                $"{family} 预设 id '__manual__' 由界面的未保存状态保留，请换一个 id。");
        }
    }

    private static void ValidatePitch(string id, double linear, double quadratic, double cubic)
    {
        try { DvdTrackGeometry.ValidatePitchCoefficients(linear, quadratic, cubic); }
        catch (ArgumentException exception)
        {
            throw DiagnosticMessage.InvalidData($"预设 '{id}' 的轨距系数无效：{DiagnosticMessage.Detail(exception)}", exception);
        }
    }

    private static void ValidateText(string? value, string presetId, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidValue(presetId, fieldName, $"不能为空");
        }
    }

    private static void ValidateOptionalText(string? value, string presetId, string fieldName)
    {
        if (value is not null)
        {
            ValidateText(value, presetId, fieldName);
        }
    }

    private static void ValidateRadiusRange(
        string presetId,
        double innerRadius,
        double outerRadius)
    {
        ValidatePositiveFinite(presetId, "innerRadiusMm", innerRadius);
        ValidatePositiveFinite(presetId, "outerRadiusMm", outerRadius);
        if (outerRadius <= innerRadius)
        {
            throw InvalidValue(presetId, "outerRadiusMm", $"必须大于 innerRadiusMm");
        }
    }

    private static void ValidatePositiveFinite(string presetId, string fieldName, double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw InvalidValue(presetId, fieldName, $"必须是大于 0 的有限数值");
        }
    }

    private static InvalidDataException InvalidValue(
        string presetId,
        string fieldName,
        FormattableString requirement)
        => DiagnosticMessage.InvalidData($"预设 '{presetId}' 的 {fieldName} {requirement}。");
}
