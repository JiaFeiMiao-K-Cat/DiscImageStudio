using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Services;

/// <summary>Persists <see cref="AppSettings"/> as
/// %LOCALAPPDATA%\DiscImageStudio\settings.json. Loading is forgiving (missing, corrupt or
/// newer-schema files fall back to defaults) because a broken preference file must never stop
/// the app from starting; saving propagates IO errors so the settings dialog can report them.
/// The write goes through a temp file and an atomic replace, so a crash mid-save cannot leave
/// a truncated settings file behind.</summary>
public sealed class AppSettingsStore
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public AppSettingsStore()
        : this(DefaultPath)
    {
    }

    public AppSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiscImageStudio",
        FileName);

    public string FilePath { get; }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            using FileStream stream = new(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
            // A newer schema may carry meanings this build cannot honor; start from defaults
            // instead of silently dropping or misreading the unknown fields.
            if (settings is null || settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                return new AppSettings();
            }

            return settings.Normalize();
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException
                                              or NotSupportedException
                                              or ArgumentException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings.Normalize(), JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
