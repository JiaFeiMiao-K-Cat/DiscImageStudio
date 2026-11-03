using System.Text.Json;

namespace DiscImageStudio.Linux.Services;

/// <summary>English lookup over the Windows app's localization catalogs. Built-in preset
/// names and several Core validation messages are authored in Simplified Chinese; the
/// catalogs already carry their English translations, so this GUI reuses them instead of
/// keeping a second copy.</summary>
public static class Translator
{
    private static readonly Lazy<Dictionary<string, string>> English = new(Load);

    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return English.Value.TryGetValue(text, out string? english) ? english : text;
    }

    private static Dictionary<string, string> Load()
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        var assembly = typeof(Translator).Assembly;
        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith("DiscImageStudio.Linux.Catalogs.", StringComparison.Ordinal)))
        {
            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(stream);
            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Object
                    && entry.Value.TryGetProperty("en", out JsonElement english)
                    && english.ValueKind == JsonValueKind.String)
                {
                    result.TryAdd(entry.Name, english.GetString() ?? entry.Name);
                }
            }
        }

        return result;
    }
}
