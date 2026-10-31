using System.IO;

namespace DiscImageStudio.Services;

/// <summary>Shared cache location and best-effort cleanup for artifacts the app stages
/// between jobs (design composites, live previews, thumbnails). The directory is
/// configurable through settings; running jobs keep their already-resolved paths, so a
/// switch only affects newly started work. Cleanup is whitelisted by file name: a custom
/// directory may be shared, and this class must never delete anything it did not write.</summary>
internal static class TempFiles
{
    private const string DefaultFolderName = "DiscImageStudio";

    // Every file name the app writes into the cache directory. Sidecars are the matching
    // ".png.json" metadata files.
    private static readonly string[] ManagedPrefixes =
    [
        "design-composite-",
        "design-tone-",
        "cd-live-",
        "dvd-live-",
        "bd-live-",
    ];

    private static readonly string[] ManagedNames = ["design-thumbnail.png"];

    private static readonly Lock Gate = new();
    private static string _livePreviewDirectory = DefaultDirectory;

    internal static string DefaultDirectory => Path.Combine(
        Path.GetTempPath(),
        DefaultFolderName);

    /// <summary>Directory for newly written preview/composite artifacts. Never null; falls
    /// back to <see cref="DefaultDirectory"/>.</summary>
    internal static string LivePreviewDirectory
    {
        get
        {
            lock (Gate)
            {
                return _livePreviewDirectory;
            }
        }
    }

    /// <summary>Points new cache writes at <paramref name="directory"/>. An empty value or an
    /// unusable folder (relative path, unwritable) falls back to the default temp folder; the
    /// return value is the directory actually in use.</summary>
    internal static string TryConfigureCacheDirectory(string? directory)
    {
        string resolved = DefaultDirectory;
        if (!string.IsNullOrWhiteSpace(directory) && Path.IsPathRooted(directory.Trim()))
        {
            try
            {
                string candidate = Path.GetFullPath(directory.Trim());
                Directory.CreateDirectory(candidate);
                // Creating the folder is not enough: an existing folder may still refuse
                // files. Probe with a zero-byte write before committing to it.
                string probe = Path.Combine(candidate, ".write-probe-" + Guid.NewGuid().ToString("N"));
                using (FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Flush(flushToDisk: false);
                }

                File.Delete(probe);
                resolved = candidate;
            }
            catch (Exception exception) when (exception is ArgumentException
                                                  or IOException
                                                  or NotSupportedException
                                                  or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
            {
                resolved = DefaultDirectory;
            }
        }

        lock (Gate)
        {
            _livePreviewDirectory = resolved;
        }

        return resolved;
    }

    internal static void TryDeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

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

    /// <summary>Bytes held by app-managed cache files in the current directory. Unrelated
    /// files in a shared custom folder are not counted, matching what <see cref="ClearCache"/>
    /// would remove.</summary>
    internal static long GetCacheSize() => EnumerateManagedFiles()
        .Sum(file => GetLength(file));

    /// <summary>Deletes only whitelisted cache artifacts and returns how many files were
    /// removed. Directories are never touched.</summary>
    internal static int ClearCache()
    {
        int removed = 0;
        foreach (string file in EnumerateManagedFiles())
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    internal static bool IsManagedCacheFile(string path)
    {
        string name = Path.GetFileName(path);
        if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".png.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ManagedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || ManagedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateManagedFiles()
    {
        string directory = LivePreviewDirectory;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(IsManagedCacheFile)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static long GetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return 0;
        }
    }
}
