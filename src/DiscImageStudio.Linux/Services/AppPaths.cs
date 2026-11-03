namespace DiscImageStudio.Linux.Services;

/// <summary>Per-user locations following the XDG base-directory specification. .NET maps
/// ApplicationData to $XDG_CONFIG_HOME and LocalApplicationData to $XDG_DATA_HOME; the
/// cache directory is resolved here because .NET has no special folder for it.</summary>
public static class AppPaths
{
    public const string AppDirectoryName = "DiscImageStudio";

    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        AppDirectoryName);

    public static string CacheDirectory { get; } = Path.Combine(ResolveCacheHome(), AppDirectoryName);

    public static string SettingsFile => Path.Combine(ConfigDirectory, "linux-settings.json");

    /// <summary>Composites and previews: regenerated on demand, safe to delete at any time.</summary>
    public static string WorkDirectory => Path.Combine(CacheDirectory, "work");

    public static string HomeDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>~/Documents (or the XDG documents folder) when it exists, otherwise home.</summary>
    public static string DocumentsDirectory
    {
        get
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrEmpty(documents) || !Directory.Exists(documents) ? HomeDirectory : documents;
        }
    }

    /// <summary>~/Documents/DiscImageStudio Projects, falling back to the home folder when
    /// there is no documents folder (Core's default would be a relative path then).</summary>
    public static string DefaultProjectsDirectory => Path.Combine(DocumentsDirectory, "DiscImageStudio Projects");

    public static string NewWorkFile(string prefix, string extension)
    {
        Directory.CreateDirectory(WorkDirectory);
        return Path.Combine(WorkDirectory, $"{prefix}-{Guid.NewGuid():N}{extension}");
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
            File.Delete(path + ".json");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Clears composites left by an earlier session; each is up to 8192² pixels.</summary>
    public static void CleanWorkDirectory()
    {
        try
        {
            if (Directory.Exists(WorkDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(WorkDirectory))
                {
                    TryDelete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Expands a leading ~ and resolves a relative path against
    /// <paramref name="baseDirectory"/>, not the process working directory, which is / when
    /// the app starts from a menu.</summary>
    public static string ResolveUserPath(string path, string? baseDirectory)
    {
        string trimmed = path.Trim();
        if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            trimmed = HomeDirectory + trimmed[1..];
        }

        return Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(baseDirectory ?? HomeDirectory, trimmed));
    }

    public static string ShortenHome(string path)
        => !string.IsNullOrEmpty(HomeDirectory) && path.StartsWith(HomeDirectory + "/", StringComparison.Ordinal)
            ? "~" + path[HomeDirectory.Length..]
            : path;

    private static string ResolveCacheHome()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg))
        {
            return xdg;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Path.GetTempPath() : Path.Combine(home, ".cache");
    }
}
