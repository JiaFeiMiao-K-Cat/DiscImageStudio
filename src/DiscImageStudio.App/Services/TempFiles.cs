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

    // Static initializers run in textual order: the Lazy must exist before the field below reads it.
    private static readonly Lazy<string> DefaultDirectoryValue = new(ResolveDefaultDirectory);
    private static readonly Lock Gate = new();
    private static string _livePreviewDirectory = DefaultDirectory;

    /// <summary>Cache folder next to the executable, so a portable copy keeps its artifacts with
    /// itself. When that folder cannot be written (for example under Program Files) the cache
    /// moves to %LOCALAPPDATA%\DiscImageStudio\cache. The system temp folder is never used.</summary>
    internal static string DefaultDirectory => DefaultDirectoryValue.Value;

    private static string ResolveDefaultDirectory()
    {
        // In a single-file bundle AppContext.BaseDirectory may be the extraction folder, so the
        // executable's own path is the reliable anchor.
        // When launched as "dotnet DiscImageStudio.dll" the process is the shared dotnet host,
        // so the app's own folder is the anchor instead of the SDK directory.
        string? processPath = Environment.ProcessPath;
        bool hostedByDotnet = string.Equals(
            Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        string? executableDirectory = hostedByDotnet
            ? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetDirectoryName(processPath);
        if (!string.IsNullOrEmpty(executableDirectory))
        {
            string besideExecutable = Path.Combine(executableDirectory, DefaultFolderName + "-cache");
            if (CanWrite(besideExecutable))
            {
                return besideExecutable;
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultFolderName,
            "cache");
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return false;
        }
    }

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
    /// unusable folder (relative path, unwritable) falls back to <see cref="DefaultDirectory"/>; the
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

    /// <summary>Resolves a managed artifact inside this process's private cache session.
    /// The returned path remains valid when the configured cache root changes.</summary>
    internal static string GetCacheFilePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !IsManagedCacheFile(fileName))
            throw new ArgumentException("Expected an app-managed cache file name.", nameof(fileName));

        lock (Gate)
        {
            string directory = Path.Combine(_livePreviewDirectory, CacheSession.DirectoryName);
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The cache session directory must not be a link.");
            string leasePath = Path.Combine(directory, CacheSession.LeaseName);
            if (CacheSession.Leases.TryGetValue(directory, out FileStream? existing) && !File.Exists(leasePath))
            {
                existing.Dispose();
                CacheSession.Leases.Remove(directory);
            }
            if (!CacheSession.Leases.ContainsKey(directory))
            {
                // Keep the handle until process exit, including across cancellation
                // and cache-root changes: an already-started job may still use it.
                FileStream lease = new(leasePath,
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
                CacheSession.Leases.Add(directory, lease);
            }
            return Path.Combine(directory, fileName);
        }
    }

    /// <summary>Bytes held by app-managed files in the current root and cleanable sessions. Unrelated
    /// files in a shared custom folder are not counted, matching what <see cref="ClearCache"/>
    /// would remove.</summary>
    internal static long GetCacheSize() => EnumerateManagedFiles()
        .Sum(file => GetLength(file));

    /// <summary>Deletes only whitelisted artifacts in the current root, this session and
    /// inactive sessions. Other running processes and all directories are left alone.</summary>
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
            List<string> files = Directory.EnumerateFiles(directory).Where(IsManagedCacheFile).ToList();
            foreach (string session in Directory.EnumerateDirectories(directory))
            {
                if (!CanCleanSession(session)) continue;
                try
                {
                    files.AddRange(Directory.EnumerateFiles(session).Where(IsManagedCacheFile));
                }
                catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException
                                                      or System.Security.SecurityException)
                {
                }
            }
            return files;
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

    private static bool CanCleanSession(string directory)
    {
        string name = Path.GetFileName(directory);
        if (!name.StartsWith(CacheSession.Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[CacheSession.Prefix.Length..], "N", out _)) return false;
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            string leasePath = Path.Combine(directory, CacheSession.LeaseName);
            if (!File.Exists(leasePath) || (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0) return false;
            lock (Gate)
            {
                if (CacheSession.Leases.ContainsKey(directory)) return true;
            }
            // Opening exclusively fails while another process holds its lease.
            // Session names are never reused by subsequent processes.
            using FileStream lease = new(leasePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static class CacheSession
    {
        internal const string Prefix = "session-";
        internal const string LeaseName = ".cache-session.lock";
        internal static readonly string DirectoryName = Prefix + Guid.NewGuid().ToString("N");
        internal static readonly Dictionary<string, FileStream> Leases = new(StringComparer.OrdinalIgnoreCase);
    }
}
