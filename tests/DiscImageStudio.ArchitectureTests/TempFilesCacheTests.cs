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
}
