using System.IO;
using System.Text.Json;
using DiscImageStudio.Services;

/// <summary>Covers the workstation preference file: forgiving load (missing, corrupt, newer
/// schema), camelCase JSON with string enums, sanitizing of hand-edited paths, and the
/// temp-file-plus-replace save that must never leave a partial file behind.</summary>
internal static class AppSettingsTests
{
    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "disc-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestDefaultsWhenMissing();
            TestRoundTrip(directory);
            TestCorruptFile(directory);
            TestUnknownTheme(directory);
            TestNumericEnum(directory);
            TestNewerSchema(directory);
            TestNormalizePaths();
            TestSaveReplacesExisting(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestDefaultsWhenMissing()
    {
        string path = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".json");
        AppSettings settings = new AppSettingsStore(path).Load();
        Require(settings.Theme == AppTheme.Dark, "missing file falls back to the dark theme");
        Require(settings.DefaultProjectDirectory.Length == 0, "missing file has no project directory");
        Require(settings.CacheDirectory.Length == 0, "missing file has no cache directory");
    }

    private static void TestRoundTrip(string directory)
    {
        string path = Path.Combine(directory, "round-trip.json");
        AppSettingsStore store = new(path);
        store.Save(new AppSettings
        {
            Theme = AppTheme.Light,
            DefaultProjectDirectory = directory,
            CacheDirectory = Path.Combine(directory, "cache"),
        });

        string json = File.ReadAllText(path);
        Require(json.Contains("\"schemaVersion\": 1", StringComparison.Ordinal), "schema version is camelCase");
        Require(json.Contains("\"theme\": \"light\"", StringComparison.Ordinal), "theme serializes as a camelCase string");
        Require(json.Contains("\"defaultProjectDirectory\"", StringComparison.Ordinal), "project directory is camelCase");

        AppSettings loaded = store.Load();
        Require(loaded.Theme == AppTheme.Light, "theme survives a round trip");
        Require(loaded.DefaultProjectDirectory == directory, "project directory survives a round trip");
        Require(loaded.CacheDirectory == Path.Combine(directory, "cache"), "cache directory survives a round trip");
    }

    private static void TestCorruptFile(string directory)
    {
        string path = Path.Combine(directory, "corrupt.json");
        File.WriteAllText(path, "{ not json");
        AppSettings settings = new AppSettingsStore(path).Load();
        Require(settings.Theme == AppTheme.Dark, "corrupt JSON falls back to defaults");
        Require(settings.SchemaVersion == AppSettings.CurrentSchemaVersion, "defaults carry the current schema");
    }

    private static void TestUnknownTheme(string directory)
    {
        string path = Path.Combine(directory, "unknown-theme.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "theme": "neon" }""");
        AppSettings settings = new AppSettingsStore(path).Load();
        Require(settings.Theme == AppTheme.Dark, "an unknown theme string falls back to defaults");
    }

    private static void TestNumericEnum(string directory)
    {
        string path = Path.Combine(directory, "numeric-theme.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "theme": 42 }""");
        AppSettings settings = new AppSettingsStore(path).Load();
        Require(settings.Theme == AppTheme.Dark, "an out-of-range theme number is normalized to dark");
    }

    private static void TestNewerSchema(string directory)
    {
        string path = Path.Combine(directory, "newer.json");
        File.WriteAllText(path, """{ "schemaVersion": 99, "theme": "light" }""");
        AppSettings settings = new AppSettingsStore(path).Load();
        Require(settings.Theme == AppTheme.Dark, "a newer schema is not read");

        // Unknown members must not stop an otherwise valid file from loading.
        string tolerant = Path.Combine(directory, "unknown-member.json");
        File.WriteAllText(tolerant, """{ "schemaVersion": 1, "theme": "light", "future": true }""");
        Require(new AppSettingsStore(tolerant).Load().Theme == AppTheme.Light,
            "unknown members are ignored");
    }

    private static void TestNormalizePaths()
    {
        AppSettings normalized = new AppSettings
        {
            Theme = (AppTheme)99,
            DefaultProjectDirectory = "relative\\folder",
            CacheDirectory = "   ",
        }.Normalize();
        Require(normalized.Theme == AppTheme.Dark, "an undefined enum normalizes to dark");
        Require(normalized.DefaultProjectDirectory.Length == 0, "a relative project directory is dropped");
        Require(normalized.CacheDirectory.Length == 0, "a blank cache directory is dropped");

        string rooted = Path.GetPathRoot(Path.GetTempPath())!;
        Require(new AppSettings { DefaultProjectDirectory = rooted }.Normalize()
            .DefaultProjectDirectory == rooted, "a rooted directory is kept");
    }

    private static void TestSaveReplacesExisting(string directory)
    {
        string path = Path.Combine(directory, "replace.json");
        AppSettingsStore store = new(path);
        store.Save(new AppSettings { Theme = AppTheme.Light });
        store.Save(new AppSettings { Theme = AppTheme.System });
        Require(store.Load().Theme == AppTheme.System, "the second save wins");

        string[] leftovers = Directory.GetFiles(directory, "*.tmp");
        Require(leftovers.Length == 0, "atomic save leaves no temp file behind");

        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        Require(document.RootElement.GetProperty("theme").GetString() == "system",
            "System serializes in camelCase");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("App settings check failed: " + name);
        }
    }
}
