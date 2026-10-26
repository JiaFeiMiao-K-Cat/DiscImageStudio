using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Core;

/// <summary>Geometry of one project: the generation parameters a disc family needs.
/// CD uses only the radii pair and the sector count; the remaining pitch fields exist so a
/// DVD project round-trips without reshaping the record.</summary>
public sealed record ProjectGeometry(
    double InnerRadiusMm,
    double OuterRadiusMm,
    long Sectors,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0);

/// <summary>One design layer: an image placed relative to the disc centre. Translation is in
/// millimetres (+x right, +y down on the canvas), rotation in degrees clockwise, and scale is
/// a factor applied to the image's initially fitted size.</summary>
public sealed record ProjectLayer(
    string Id,
    string Name,
    string ImagePath,
    bool Visible,
    double TranslateXMm,
    double TranslateYMm,
    double RotationDeg,
    double Scale);

/// <summary>Where the next generation job writes, and the per-family output switches.</summary>
public sealed record ProjectOutputs(
    string OutputPath,
    bool WriteCue,
    string DataDirectory,
    string VolumeLabel,
    bool Interleave,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool FilesAtOuterRing = false);

/// <summary>Generation settings that exist only for BD-R: the 3T/6T mapping polarity.
/// This is deliberately separate from <see cref="ToneProfile"/>: polarity selects which
/// run-length class a payload bit drives, while tone decides how a source grey becomes
/// that bit.</summary>
public sealed record ProjectBluRaySettings(bool InvertMapping = false);

/// <summary>The on-disk project (.discproj): everything needed to reopen a disc design and
/// regenerate its outputs. Images stay referenced by absolute path.
/// <see cref="Tone"/> is null for files written before tone mapping existed; those projects
/// keep the original hard-threshold output until their tone slider is touched.
/// Schema 1 held CD/DVD only; schema 2 adds <see cref="BluRay"/> and reads schema 1 by
/// treating it as a CD/DVD project.</summary>
public sealed record ProjectDocument(
    int SchemaVersion,
    string Name,
    string DiscFamily,
    ProjectGeometry Geometry,
    IReadOnlyList<ProjectLayer> Layers,
    ProjectOutputs Outputs,
    string? CalibrationSessionPath,
    string? ThumbnailPngBase64,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    ToneProfile? Tone = null,
    ProjectBluRaySettings? BluRay = null)
{
    public const string CdFamily = "cd";
    public const string DvdFamily = "dvd";
    public const string BluRayFamily = "bd";
    public const int BluRaySectorsPerCluster = 32;
    public const long BluRaySectorsPerLayer = 12_219_392;
    // Sized for the home page's recent-project disc (176px display): the face is shown near
    // this resolution, so a 96px thumbnail would visibly blur.
    public const int ThumbnailDecodeSize = 256;

    /// <summary>Members the current schema no longer has but older files legitimately carry.
    /// The removed imageProcessing section is the one legacy key: files written before the
    /// ring-copy mode was abolished load with it captured here (instead of failing strict
    /// unmapped-member validation) and drop it on the next save. Any other unknown member is
    /// still rejected by <see cref="ProjectJsonStore.Validate"/>.</summary>
    public const string LegacyImageProcessingKey = "imageProcessing";

    [JsonIgnore]
    public OpticalDiscFamily Family => DiscFamily switch
    {
        DvdFamily => OpticalDiscFamily.Dvd,
        BluRayFamily => OpticalDiscFamily.BluRay,
        _ => OpticalDiscFamily.CompactDisc,
    };

    [JsonIgnore]
    public bool IsCd => Family == OpticalDiscFamily.CompactDisc;

    [JsonIgnore]
    public bool IsDvd => Family == OpticalDiscFamily.Dvd;

    [JsonIgnore]
    public bool IsBluRay => Family == OpticalDiscFamily.BluRay;

    [JsonIgnore]
    public string FamilyLabel => Family switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Persists <see cref="ProjectDocument"/> files. Mirrors DiscPresetJsonStore: strict
/// JSON, validated on both load and save, written through a temp file so a crash mid-write
/// can never leave a half-written project behind.</summary>
public static class ProjectJsonStore
{
    public const int CurrentSchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    public const string FileExtension = ".discproj";
    public const string FileFilter = "Disc Image Studio 工程|*.discproj";

    // 256px PNG of a photographic design stays under ~200K base64 chars; the cap keeps a
    // pathological file from bloating the project JSON.
    private const int MaxThumbnailBase64Chars = 512 * 1024;

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

    public static string GetDefaultProjectsDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DiscImageStudio Projects");

    public static ProjectDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return Deserialize(stream, Path.GetFileName(path));
    }

    public static void Save(string path, ProjectDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("工程路径必须包含目录。", nameof(path));
        }

        Directory.CreateDirectory(directory);
        // Legacy extension members (the removed imageProcessing section) are accepted on load
        // but never written back: saving is what migrates an old file to the current schema.
        ProjectDocument clean = document with
        {
            SchemaVersion = CurrentSchemaVersion,
            ExtensionData = document.ExtensionData is { Count: > 0 } ? null : document.ExtensionData,
        };
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, clean, JsonOptions);
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

    public static void Validate(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion))
        {
            throw new InvalidDataException(
                $"不支持 schemaVersion={document.SchemaVersion}；当前支持 {LegacySchemaVersion} 与 {CurrentSchemaVersion}。");
        }

        if (document.SchemaVersion == LegacySchemaVersion
            && (document.IsBluRay || document.BluRay is not null))
        {
            throw new InvalidDataException("schemaVersion 1 只包含 CD/DVD 工程，不能声明蓝光字段。");
        }

        if (!document.IsBluRay && document.BluRay is not null)
        {
            throw new InvalidDataException("bluRay 设置只用于蓝光工程。");
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            throw new InvalidDataException("工程名不能为空。");
        }

        if (!IsKnownFamily(document.DiscFamily))
        {
            throw new InvalidDataException(
                $"discFamily '{document.DiscFamily}' 无效；仅支持 '{ProjectDocument.CdFamily}'、"
                + $"'{ProjectDocument.DvdFamily}' 与 '{ProjectDocument.BluRayFamily}'。");
        }

        if (document.Geometry is null)
        {
            throw new InvalidDataException("geometry 不能为空。");
        }

        if (document.Outputs is null)
        {
            throw new InvalidDataException("outputs 不能为空。");
        }

        ValidateExtensionData(document);
        ValidateGeometry(document);
        ValidateLayers(document);
        ValidateOutputs(document);
        ValidateTone(document);
        ValidateThumbnail(document.ThumbnailPngBase64);
    }

    private static void ValidateTone(ProjectDocument document)
    {
        if (document.Tone is not { } tone)
        {
            return;
        }

        try
        {
            tone.Validate("tone");
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
    }

    /// <summary>Strict about unknown members even though <see cref="ProjectDocument"/> has an
    /// extension-data bag: the bag exists only so files carrying the removed imageProcessing
    /// section still open, never as a silent sink for misspelled fields.</summary>
    private static void ValidateExtensionData(ProjectDocument document)
    {
        if (document.ExtensionData is null)
        {
            return;
        }

        string? unknown = document.ExtensionData.Keys.FirstOrDefault(
            key => !string.Equals(key, ProjectDocument.LegacyImageProcessingKey, StringComparison.Ordinal));
        if (unknown is not null)
        {
            throw new InvalidDataException($"无法识别的工程字段 '{unknown}'。");
        }
    }

    public static bool IsKnownFamily(string? family)
        => string.Equals(family, ProjectDocument.CdFamily, StringComparison.OrdinalIgnoreCase)
           || string.Equals(family, ProjectDocument.DvdFamily, StringComparison.OrdinalIgnoreCase)
           || string.Equals(family, ProjectDocument.BluRayFamily, StringComparison.OrdinalIgnoreCase);

    private static void ValidateGeometry(ProjectDocument document)
    {
        ProjectGeometry geometry = document.Geometry;
        ValidatePositiveFinite(geometry.InnerRadiusMm, "innerRadiusMm");
        ValidatePositiveFinite(geometry.OuterRadiusMm, "outerRadiusMm");
        if (geometry.OuterRadiusMm <= geometry.InnerRadiusMm)
        {
            throw new InvalidDataException("outerRadiusMm 必须大于 innerRadiusMm。");
        }

        if (geometry.Sectors <= 0)
        {
            throw new InvalidDataException("sectors 必须大于 0。");
        }

        if (document.IsDvd && geometry.Sectors % 16 != 0)
        {
            throw new InvalidDataException("DVD 的 sectors 必须是 16 的倍数。");
        }

        if (document.IsBluRay)
        {
            if (geometry.Sectors % ProjectDocument.BluRaySectorsPerCluster != 0)
            {
                throw new InvalidDataException("蓝光的 sectors 必须是 32 的倍数。");
            }

            if (geometry.Sectors > ProjectDocument.BluRaySectorsPerLayer)
            {
                throw new InvalidDataException(
                    $"蓝光 sectors 不能超过单层容量 {ProjectDocument.BluRaySectorsPerLayer:N0}。");
            }

            if (geometry.OuterRadiusMm > 60)
            {
                throw new InvalidDataException("蓝光外半径不能超过 60 mm。");
            }

            if (geometry.PitchLinear != 0 || geometry.PitchQuadratic != 0 || geometry.PitchCubic != 0)
            {
                throw new InvalidDataException("蓝光使用恒定轨距，pitch 系数必须为 0。");
            }
        }

        try
        {
            DvdTrackGeometry.ValidatePitchCoefficients(
                geometry.PitchLinear,
                geometry.PitchQuadratic,
                geometry.PitchCubic);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"轨距系数无效：{exception.Message}", exception);
        }
    }

    private static void ValidateLayers(ProjectDocument document)
    {
        if (document.Layers is null)
        {
            throw new InvalidDataException("layers 不能为空。");
        }

        HashSet<string> uniqueIds = new(StringComparer.Ordinal);
        foreach (ProjectLayer? layer in document.Layers)
        {
            if (layer is null)
            {
                throw new InvalidDataException("图层不能是 null。");
            }

            if (string.IsNullOrWhiteSpace(layer.Id) || !uniqueIds.Add(layer.Id))
            {
                throw new InvalidDataException($"图层 id '{layer.Id}' 为空或重复。");
            }

            if (string.IsNullOrWhiteSpace(layer.Name))
            {
                throw new InvalidDataException($"图层 '{layer.Id}' 的 name 不能为空。");
            }

            if (string.IsNullOrWhiteSpace(layer.ImagePath))
            {
                throw new InvalidDataException($"图层 '{layer.Id}' 的 imagePath 不能为空。");
            }

            ValidateFinite(layer.TranslateXMm, $"图层 '{layer.Id}' 的 translateXMm");
            ValidateFinite(layer.TranslateYMm, $"图层 '{layer.Id}' 的 translateYMm");
            ValidateFinite(layer.RotationDeg, $"图层 '{layer.Id}' 的 rotationDeg");
            if (!double.IsFinite(layer.Scale) || layer.Scale <= 0)
            {
                throw new InvalidDataException($"图层 '{layer.Id}' 的 scale 必须是大于 0 的有限数值。");
            }
        }
    }

    private static void ValidateOutputs(ProjectDocument document)
    {
        ProjectOutputs outputs = document.Outputs;
        if (string.IsNullOrWhiteSpace(outputs.OutputPath))
        {
            throw new InvalidDataException("outputs.outputPath 不能为空。");
        }
    }

    private static void ValidateThumbnail(string? thumbnailBase64)
    {
        if (thumbnailBase64 is null)
        {
            return;
        }

        if (thumbnailBase64.Length == 0 || thumbnailBase64.Length > MaxThumbnailBase64Chars)
        {
            throw new InvalidDataException("thumbnailPngBase64 长度超出允许范围。");
        }

        try
        {
            _ = Convert.FromBase64String(thumbnailBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("thumbnailPngBase64 不是有效的 Base64。", exception);
        }
    }

    private static ProjectDocument Deserialize(Stream stream, string sourceName)
    {
        try
        {
            ProjectDocument? document = JsonSerializer.Deserialize<ProjectDocument>(stream, JsonOptions);
            if (document is null)
            {
                throw new InvalidDataException($"{sourceName} 不能为空。");
            }

            Validate(document);
            // Schema 1 is read as its current equivalent; the next save writes version 2.
            return document.SchemaVersion == LegacySchemaVersion
                ? document with { SchemaVersion = CurrentSchemaVersion }
                : document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{sourceName} 格式有误（第 {exception.LineNumber + 1} 行，第 {exception.BytePositionInLine + 1} 列）：{exception.Message}",
                exception);
        }
    }

    private static void ValidatePositiveFinite(double value, string fieldName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new InvalidDataException($"{fieldName} 必须是大于 0 的有限数值。");
        }
    }

    private static void ValidateFinite(double value, string fieldName)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException($"{fieldName} 必须是有限数值。");
        }
    }
}
