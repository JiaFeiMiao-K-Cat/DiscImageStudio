using System.IO;
using DiscImageStudio.Services;

/// <summary>Covers the configurable cache directory: an unwritable or missing location falls
/// back to the temp folder, and clearing the cache only removes the app's own artifacts from a
/// folder that may be shared with the user's files.</summary>
internal static class TempFilesCacheTests
{
    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "disc-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestCustomDirectory(directory);
            TestFallback(directory);
            TestWhitelistCleanup(directory);
            TestSessionIsolation(directory);
            TestRootSwitchKeepsPaths(directory);
            TestUnrelatedDirectoriesAndLinks(directory);
        }
        finally
        {
            TempFiles.TryConfigureCacheDirectory(null);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void TestCustomDirectory(string directory)
    {
        string custom = Path.Combine(directory, "custom-cache");
        string resolved = TempFiles.TryConfigureCacheDirectory(custom);
        Require(resolved == Path.GetFullPath(custom), "a writable folder becomes the cache directory");
        Require(TempFiles.LivePreviewDirectory == resolved, "the configured folder is live immediately");
        Require(Directory.Exists(custom), "the cache folder is created on demand");
        Require(
            Directory.GetFiles(custom, ".write-probe-*").Length == 0,
            "the write probe is removed after configuration");
    }

    private static void TestFallback(string directory)
    {
        Require(
            TempFiles.TryConfigureCacheDirectory(null) == TempFiles.DefaultDirectory,
            "an empty value falls back to the default temp folder");

        Require(
            TempFiles.TryConfigureCacheDirectory("relative-cache") == TempFiles.DefaultDirectory,
            "a relative path falls back to the default temp folder");

        string blocker = Path.Combine(directory, "blocker.txt");
        File.WriteAllText(blocker, "not a folder");
        Require(
            TempFiles.TryConfigureCacheDirectory(blocker) == TempFiles.DefaultDirectory,
            "a path that cannot host a folder falls back to the default temp folder");
    }

    private static void TestWhitelistCleanup(string directory)
    {
        string cache = Path.Combine(directory, "shared-cache");
        TempFiles.TryConfigureCacheDirectory(cache);

        string[] managed =
        [
            "design-composite-cd.png",
            "design-tone-3.png",
            "design-thumbnail.png",
            "cd-live-preview-7.png",
            "dvd-live-warp-7.png.json",
            "bd-live-readback-7.png",
            "BD-LIVE-PROJECTION-7.PNG",
        ];
        string[] unmanaged =
        [
            "notes.txt",
            "photo.png",
            "design-composite-cd.jpg",
            "design-thumbnail.png.json",
            "archive.zip",
        ];

        long expectedSize = 0;
        foreach (string name in managed)
        {
            byte[] content = new byte[100 + name.Length];
            File.WriteAllBytes(Path.Combine(cache, name), content);
            expectedSize += content.Length;
        }

        foreach (string name in unmanaged)
        {
            File.WriteAllBytes(Path.Combine(cache, name), new byte[7]);
        }

        Directory.CreateDirectory(Path.Combine(cache, "nested"));

        Require(
            TempFiles.IsManagedCacheFile(Path.Combine(cache, "cd-live-preview-7.png.json")),
            "sidecars count as managed cache files");
        Require(
            !TempFiles.IsManagedCacheFile(Path.Combine(cache, "design-thumbnail.png.json")),
            "the thumbnail's sidecar is not written by the app");
        Require(
            !TempFiles.IsManagedCacheFile(Path.Combine(cache, "notes.txt")),
            "unrelated files are not managed");

        Require(TempFiles.GetCacheSize() == expectedSize, "cache size counts only managed files");
        Require(TempFiles.ClearCache() == managed.Length, "clear removes every managed file");

        foreach (string name in managed)
        {
            Require(!File.Exists(Path.Combine(cache, name)), "managed file removed: " + name);
        }

        foreach (string name in unmanaged)
        {
            Require(File.Exists(Path.Combine(cache, name)), "unrelated file kept: " + name);
        }

        Require(Directory.Exists(Path.Combine(cache, "nested")), "clear never deletes directories");
        Require(TempFiles.GetCacheSize() == 0, "cleared cache reports zero bytes");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Cache check failed: " + name);
        }
    }

    private static void TestSessionIsolation(string directory)
    {
        string root = Path.Combine(directory, "sessions");
        TempFiles.TryConfigureCacheDirectory(root);
        string ownExport = TempFiles.GetCacheFilePath("design-composite-cd-export.png");
        string ownDirectory = Path.GetDirectoryName(ownExport)!;
        Require(TempFiles.LivePreviewDirectory == root && Path.GetDirectoryName(ownDirectory) == root,
            "settings retain the configured root while generated files use a private session");
        Require(TempFiles.GetCacheFilePath("design-composite-cd-export.png") == ownExport,
            "unchanged designs retain a stable source path within their session");

        string otherDirectory = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherDirectory);
        string otherExport = Path.Combine(otherDirectory, "design-composite-cd-export.png");
        string leasePath = Path.Combine(otherDirectory, ".cache-session.lock");
        using (FileStream otherLease = new(leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
        {
            File.WriteAllText(ownExport, "design A");
            File.WriteAllText(otherExport, "design B");
            Require(ownExport != otherExport && File.ReadAllText(ownExport) == "design A",
                "another session cannot overwrite a cached export with the same logical filename");
            string legacy = Path.Combine(root, "design-composite-dvd.png");
            File.WriteAllBytes(legacy, new byte[11]);
            Require(TempFiles.GetCacheSize() == new FileInfo(ownExport).Length + 11,
                "cache usage matches files eligible for cleanup and excludes another active session");
            Require(TempFiles.ClearCache() == 2 && !File.Exists(ownExport) && !File.Exists(legacy),
                "cleanup removes this session and legacy root artifacts");
            Require(File.ReadAllText(otherExport) == "design B", "cleanup leaves another running session's source intact");
        }
        Require(TempFiles.GetCacheSize() == new FileInfo(otherExport).Length && TempFiles.ClearCache() == 1,
            "a closed session becomes cleanable when its lease is released");
        Require(!File.Exists(otherExport) && Directory.Exists(otherDirectory), "cleanup does not delete session directories");

        foreach (string invalid in new[] { "../design-composite-cd.png", "notes.txt", "design-composite-cd:other.png" })
        {
            bool rejected = false;
            try { TempFiles.GetCacheFilePath(invalid); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "cache path API accepts only managed file names");
        }
    }

    private static void TestRootSwitchKeepsPaths(string directory)
    {
        string firstRoot = Path.Combine(directory, "first-root"), secondRoot = Path.Combine(directory, "second-root");
        TempFiles.TryConfigureCacheDirectory(firstRoot);
        string runningPath = TempFiles.GetCacheFilePath("design-composite-dvd-export.png");
        File.WriteAllText(runningPath, "running source");
        TempFiles.TryConfigureCacheDirectory(secondRoot);
        string nextPath = TempFiles.GetCacheFilePath("design-composite-dvd-export.png");
        File.WriteAllText(nextPath, "next source");
        Require(runningPath != nextPath && File.ReadAllText(runningPath) == "running source",
            "a cache-root change does not retarget or overwrite a running job's resolved source");
        Require(TempFiles.ClearCache() == 1 && File.Exists(runningPath), "cleanup only visits the newly selected root");
        bool protectedByLease = false;
        try
        {
            using FileStream exclusive = new(Path.Combine(Path.GetDirectoryName(runningPath)!, ".cache-session.lock"),
                FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (IOException) { protectedByLease = true; }
        Require(protectedByLease, "the old root keeps its process lease until outstanding jobs can finish");
        TempFiles.TryConfigureCacheDirectory(firstRoot);
        Require(TempFiles.GetCacheFilePath("design-composite-dvd-export.png") == runningPath,
            "switching back reuses this session without acquiring a conflicting lease");
    }

    private static void TestUnrelatedDirectoriesAndLinks(string directory)
    {
        string root = Path.Combine(directory, "directory-safety");
        TempFiles.TryConfigureCacheDirectory(root);
        string unrelated = Path.Combine(root, "user-files");
        string unmarked = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        foreach (string child in new[] { unrelated, unmarked })
        {
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "design-composite-cd.png"), "keep");
        }
        Require(TempFiles.ClearCache() == 0 && TempFiles.GetCacheSize() == 0,
            "cleanup never descends into arbitrary or unmarked user directories");

        string linkTarget = Path.Combine(directory, "link-target");
        Directory.CreateDirectory(linkTarget);
        File.WriteAllText(Path.Combine(linkTarget, ".cache-session.lock"), string.Empty);
        string outside = Path.Combine(linkTarget, "design-composite-cd.png");
        File.WriteAllText(outside, "outside cache");
        string link = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateSymbolicLink(link, linkTarget); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Creating symlinks requires a privilege or Developer Mode on Windows.
            return;
        }
        Require(TempFiles.ClearCache() == 0 && TempFiles.GetCacheSize() == 0 && File.Exists(outside),
            "cleanup never follows a session-shaped directory link");
    }
}
