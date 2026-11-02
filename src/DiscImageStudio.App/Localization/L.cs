using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace DiscImageStudio.Localization;

/// <summary>Application text. Language is selected before any windows are constructed;
/// changing the saved preference takes effect on the next launch. Numeric culture is
/// deliberately left alone so engineering parameters and file formats retain their rules.</summary>
public static class L
{
    public const string Chinese = "zh-CN";
    public const string TraditionalChinese = "zh-TW";
    public const string English = "en";
    public const string Japanese = "ja";
    public const string Spanish = "es";
    public const string SystemLanguage = "system";

    private static readonly CultureInfo StartupCulture = CultureInfo.CurrentUICulture;
    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> Catalog = LoadCatalog();

    public static string CurrentLanguage { get; private set; } = Chinese;

    public static string NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "system" => SystemLanguage,
        "en" => English,
        string name when name.StartsWith("en-", StringComparison.Ordinal) => English,
        "es" => Spanish,
        string name when name.StartsWith("es-", StringComparison.Ordinal) => Spanish,
        "ja" => Japanese,
        string name when name.StartsWith("ja-", StringComparison.Ordinal) => Japanese,
        string name when IsTraditionalChinese(name) => TraditionalChinese,
        "zh-cn" or "zh-sg" or "zh" or "zh-hans" or "zh-chs" => Chinese,
        string name when name.StartsWith("zh-hans-", StringComparison.Ordinal) => Chinese,
        _ => SystemLanguage,
    };

    public static string ResolveLanguage(string? language, CultureInfo? systemCulture = null)
    {
        string normalized = NormalizeLanguage(language);
        if (normalized != SystemLanguage) return normalized;

        CultureInfo culture = systemCulture ?? StartupCulture;
        if (culture.TwoLetterISOLanguageName == "es") return Spanish;
        if (culture.TwoLetterISOLanguageName == "ja") return Japanese;
        if (culture.TwoLetterISOLanguageName != "zh") return English;
        return IsTraditionalChinese(culture.Name) ? TraditionalChinese : Chinese;
    }

    private static bool IsTraditionalChinese(string name) =>
        name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
        || name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
        || name.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)
        || name.Equals("zh-CHT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("zh-Hant-", StringComparison.OrdinalIgnoreCase);

    public static void Initialize(string? language)
    {
        CurrentLanguage = ResolveLanguage(language);
        CultureInfo culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static string T(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Catalog.TryGetValue(key, out Dictionary<string, string>? translations)) return key;
        if (translations.TryGetValue(CurrentLanguage, out string? translated)) return translated;
        return translations.TryGetValue(Chinese, out string? fallback) ? fallback : key;
    }

    public static string F(FormattableString text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Format(text.Format, text.GetArguments());
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(key), arguments);

    internal static IReadOnlyDictionary<string, Dictionary<string, string>> Entries => Catalog;

    private static IReadOnlyDictionary<string, Dictionary<string, string>> LoadCatalog()
    {
        Dictionary<string, Dictionary<string, string>> result = new(StringComparer.Ordinal);
        Assembly assembly = typeof(L).Assembly;
        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith("DiscImageStudio.Localization.Catalogs.", StringComparison.Ordinal)
                                    && name.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            var entries = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
                          ?? throw new InvalidDataException($"Empty localization catalog: {name}");
            foreach (var entry in entries)
            {
                if (!result.TryAdd(entry.Key, entry.Value))
                    throw new InvalidDataException($"Duplicate localization key: {entry.Key}");
            }
        }

        return result;
    }
}
