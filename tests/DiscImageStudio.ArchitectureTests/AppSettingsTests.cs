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
            TestSparsePreferences(directory);
            TestSystemDefaultsRoundTrip(directory);
            TestRoundTrip(directory);
            TestLanguagePreferences(directory);
            TestInvalidLanguagesPreservePreferences(directory);
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
        Require(settings.Theme == AppTheme.System, "missing file follows the system theme");
        Require(settings.Language == "system", "missing file follows the system language");
        Require(settings.DefaultProjectDirectory.Length == 0, "missing file has no project directory");
        Require(settings.CacheDirectory.Length == 0, "missing file has no cache directory");
    }

    private static void TestSparsePreferences(string directory)
    {
        string path = Path.Combine(directory, "sparse.json");
        AppSettingsStore store = new(path);
        foreach (string json in new[] { "", "null", "{}", """{ "schemaVersion": 1 }""" })
        {
            File.WriteAllText(path, json);
            AppSettings settings = store.Load();
            Require(settings.Theme == AppTheme.System && settings.Language == "system",
                "empty and sparse preferences follow both system defaults");
        }

        File.WriteAllText(path, """{ "schemaVersion": 1, "theme": "dark" }""");
        AppSettings themeOnly = store.Load();
        Require(themeOnly.Theme == AppTheme.Dark && themeOnly.Language == "system",
            "an explicit legacy dark theme is preserved while the missing language follows the system");
        File.WriteAllText(path, """{ "schemaVersion": 1, "language": "en" }""");
        AppSettings languageOnly = store.Load();
        Require(languageOnly.Theme == AppTheme.System && languageOnly.Language == "en",
            "an explicit language is preserved while the missing theme follows the system");
        File.WriteAllText(path, """{ "schemaVersion": 1, "theme": "dark", "language": "zh-CN" }""");
        AppSettings explicitLegacy = store.Load();
        Require(explicitLegacy.Theme == AppTheme.Dark && explicitLegacy.Language == "zh-CN",
            "explicit legacy dark and Simplified Chinese preferences are not migrated to system");
    }

    private static void TestSystemDefaultsRoundTrip(string directory)
    {
        AppSettingsStore store = new(Path.Combine(directory, "system-defaults.json"));
        store.Save(new AppSettings());
        AppSettings loaded = store.Load();
        Require(loaded.Theme == AppTheme.System && loaded.Language == "system",
            "new system defaults survive saving and reloading");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Require(document.RootElement.GetProperty("theme").GetString() == "system"
                && document.RootElement.GetProperty("language").GetString() == "system",
            "system defaults persist as preferences rather than their current resolved values");
    }

    private static void TestRoundTrip(string directory)
    {
        string path = Path.Combine(directory, "round-trip.json");
        AppSettingsStore store = new(path);
        store.Save(new AppSettings
        {
            Theme = AppTheme.Light,
            Language = "en",
            DefaultProjectDirectory = directory,
            CacheDirectory = Path.Combine(directory, "cache"),
        });

        string json = File.ReadAllText(path);
        Require(json.Contains("\"schemaVersion\": 1", StringComparison.Ordinal), "schema version is camelCase");
        Require(json.Contains("\"theme\": \"light\"", StringComparison.Ordinal), "theme serializes as a camelCase string");
        Require(json.Contains("\"defaultProjectDirectory\"", StringComparison.Ordinal), "project directory is camelCase");

        AppSettings loaded = store.Load();
        Require(loaded.Theme == AppTheme.Light, "theme survives a round trip");
        Require(loaded.Language == "en", "language survives a round trip");
        Require(loaded.DefaultProjectDirectory == directory, "project directory survives a round trip");
        Require(loaded.CacheDirectory == Path.Combine(directory, "cache"), "cache directory survives a round trip");
    }

    private static void TestLanguagePreferences(string directory)
    {
        string path = Path.Combine(directory, "language.json");
        AppSettingsStore store = new(path);
        File.WriteAllText(path, """{ "schemaVersion": 1, "theme": "light" }""");
        AppSettings legacy = store.Load();
        Require(legacy.Language == "system" && legacy.Theme == AppTheme.Light,
            "preferences from before i18n follow the system language and preserve their theme");

        (string Stored, string Canonical)[] cases =
        [
            ("en", "en"),
            ("en-AU", "en"),
            ("zh-CN", "zh-CN"),
            ("zh-Hans-TW", "zh-CN"),
            ("system", "system"),
            ("zh-TW", "zh-TW"),
            ("zh-HK", "zh-TW"),
            ("zh-MO", "zh-TW"),
            ("zh-Hant", "zh-TW"),
            ("zh-Hant-TW", "zh-TW"),
            ("ja", "ja"),
            ("ja-JP", "ja"),
            ("JA-jp", "ja"),
            ("es", "es"),
            ("es-ES", "es"),
            ("ES-mx", "es"),
            ("es-419", "es"),
        ];
        foreach ((string stored, string canonical) in cases)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                theme = "light",
                language = stored,
            }));
            AppSettings loaded = store.Load();
            Require(loaded.Language == canonical, stored + " loads as " + canonical);
            Require(loaded.Theme == AppTheme.Light, "normalizing a language preserves other preferences");
            store.Save(loaded);
            Require(store.Load().Language == canonical, canonical + " survives a settings round trip");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Require(document.RootElement.GetProperty("language").GetString() == canonical,
                "the language preference is saved as a stable canonical tag");
        }
    }

    private static void TestInvalidLanguagesPreservePreferences(string directory)
    {
        AppSettingsStore store = new(Path.Combine(directory, "invalid-language.json"));
        string cache = Path.Combine(directory, "cache");
        foreach (string? language in new string?[] { null, "", "   ", "unsupported" })
        {
            File.WriteAllText(store.FilePath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                theme = "light",
                language,
                defaultProjectDirectory = directory,
                cacheDirectory = cache,
            }));
            AppSettings settings = store.Load();
            Require(settings.Language == "system", "an invalid language follows the system");
            Require(settings.Theme == AppTheme.Light
                    && settings.DefaultProjectDirectory == directory && settings.CacheDirectory == cache,
                "normalizing an invalid language retains unrelated explicit preferences");
            store.Save(settings);
            Require(store.Load() == settings, "normalized language settings survive a round trip");
        }
    }

    private static void TestCorruptFile(string directory)
    {
        string path = Path.Combine(directory, "corrupt.json");
        File.WriteAllText(path, "{ not json");
        AppSettings settings = new AppSettingsStore(path).Load();
        Require(settings.Theme == AppTheme.System && settings.Language == "system",
            "corrupt JSON falls back to system defaults");
        Require(settings.SchemaVersion == AppSettings.CurrentSchemaVersion, "defaults carry the current schema");
    }

    private static void TestUnknownTheme(string directory)
    {
        string path = Path.Combine(directory, "unknown-theme.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "theme": "neon" }""");
        AppSettings settings = new AppSettingsStore(path).Load();
        Require(settings.Theme == AppTheme.System, "an unknown theme string falls back to the system theme");
    }

    private static void TestNumericEnum(string directory)
    {
        string path = Path.Combine(directory, "numeric-theme.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "theme": 42 }""");
        AppSettings settings = new AppSettingsStore(path).Load();
        Require(settings.Theme == AppTheme.System, "an out-of-range theme number is normalized to system");
    }

    private static void TestNewerSchema(string directory)
    {
        string path = Path.Combine(directory, "newer.json");
        File.WriteAllText(path, """{ "schemaVersion": 99, "theme": "light" }""");
        AppSettings settings = new AppSettingsStore(path).Load();
        Require(settings.Theme == AppTheme.System && settings.Language == "system",
            "a newer schema falls back to system defaults");

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
        Require(normalized.Theme == AppTheme.System, "an undefined enum normalizes to system");
        Require(new AppSettings { Language = "unsupported" }.Normalize().Language == "system",
            "an unsupported language follows the system");
        Require(new AppSettings { Language = null! }.Normalize().Language == "system",
            "a null language follows the system");
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
