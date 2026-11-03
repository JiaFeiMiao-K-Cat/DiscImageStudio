using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Linux.Services;

public enum ThemePreference
{
    System,
    Dark,
    Light,
}

/// <summary>Linux GUI preferences. Separate from the Windows settings file so neither app
/// rejects the other's fields; project files are the shared format.</summary>
public sealed class LinuxSettings
{
    public const int MaxRecentProjects = 12;

    [JsonConverter(typeof(JsonStringEnumConverter<ThemePreference>))]
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public List<string> RecentProjects { get; set; } = [];

    public string? LastImageDirectory { get; set; }

    public string? ProjectsDirectory { get; set; }

    public string? LastBurnDevice { get; set; }

    public void TouchRecent(string projectPath)
    {
        string full = Path.GetFullPath(projectPath);
        RecentProjects.RemoveAll(path => string.Equals(path, full, StringComparison.Ordinal));
        RecentProjects.Insert(0, full);
        if (RecentProjects.Count > MaxRecentProjects)
        {
            RecentProjects.RemoveRange(MaxRecentProjects, RecentProjects.Count - MaxRecentProjects);
        }
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static LinuxSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                using FileStream stream = File.OpenRead(AppPaths.SettingsFile);
                return JsonSerializer.Deserialize<LinuxSettings>(stream, Options) ?? new LinuxSettings();
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"warning: ignoring unreadable settings file: {exception.Message}");
        }

        return new LinuxSettings();
    }

    public static void Save(LinuxSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            string temporary = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"warning: cannot save settings: {exception.Message}");
        }
    }
}
