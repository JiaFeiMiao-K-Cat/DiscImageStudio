using System.IO;

namespace DiscImageStudio.Services;

public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>Application-level preferences. These describe the workstation, not a disc
/// project, so they live beside the recent-project history instead of inside .discproj.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>Default folder for the new-project dialog; empty means the system Documents
    /// folder (see <see cref="Core.ProjectJsonStore.GetDefaultProjectsDirectory"/>).</summary>
    public string DefaultProjectDirectory { get; init; } = string.Empty;

    /// <summary>Working folder for rendered composites, previews and thumbnails; empty means
    /// the system temp folder.</summary>
    public string CacheDirectory { get; init; } = string.Empty;

    /// <summary>Drops values that cannot be represented on this machine: an unknown enum,
    /// relative paths, and paths with invalid characters. Hand-edited files therefore load
    /// without a crash.</summary>
    public AppSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.Dark,
        DefaultProjectDirectory = NormalizeDirectory(DefaultProjectDirectory),
        CacheDirectory = NormalizeDirectory(CacheDirectory),
    };

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim();
        if (!Path.IsPathRooted(trimmed))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }
}
