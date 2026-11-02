using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>Guards instant theme switching: the light overlay must override every Color and
/// Brush key from the dark tokens with the same element type, and no view may hold a palette
/// resource through StaticResource, which would freeze the dark value at load time.</summary>
internal static class ThemePaletteTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex StaticResourceReference = new(
        @"\{StaticResource\s+([A-Za-z0-9_]+)\s*\}",
        RegexOptions.CultureInvariant);

    public static void Run()
    {
        string repositoryRoot = FindRepositoryRoot();
        string appRoot = Path.Combine(repositoryRoot, "src", "DiscImageStudio.App");
        string tokensPath = Path.Combine(appRoot, "Themes", "Tokens.xaml");
        string lightPath = Path.Combine(appRoot, "Themes", "Palette.Light.xaml");

        Dictionary<string, string> tokens = ReadEntries(tokensPath);
        Dictionary<string, string> light = ReadEntries(lightPath);

        HashSet<string> themeKeys = tokens.Keys
            .Where(key => key.EndsWith("Color", StringComparison.Ordinal)
                          || key.EndsWith("Brush", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Require(themeKeys.Count > 0, "dark tokens expose palette keys");

        foreach (string key in themeKeys)
        {
            Require(light.TryGetValue(key, out string? lightElement),
                "light palette overrides " + key);
            Require(lightElement == tokens[key],
                "light palette keeps the element type of " + key);
        }

        foreach (string key in light.Keys)
        {
            Require(tokens.ContainsKey(key),
                "light palette does not invent the key " + key);
        }

        foreach (string file in EnumerateViewXaml(appRoot))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in StaticResourceReference.Matches(text))
            {
                string key = match.Groups[1].Value;
                Require(!themeKeys.Contains(key),
                    Path.GetFileName(file) + " binds " + key
                    + " with StaticResource; palette references must use DynamicResource");
            }
        }
    }

    private static IEnumerable<string> EnumerateViewXaml(string appRoot)
    {
        foreach (string file in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(appRoot, file);
            string[] segments = relative.Split(Path.DirectorySeparatorChar);
            if (segments.Any(segment =>
                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string name = Path.GetFileName(file);
            if (name.Equals("Tokens.xaml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static Dictionary<string, string> ReadEntries(string path)
    {
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        XDocument document = XDocument.Load(path);
        foreach (XElement element in document.Root!.Elements())
        {
            XAttribute? key = element.Attribute(Xaml + "Key");
            if (key is not null)
            {
                entries.Add(key.Value, element.Name.LocalName);
            }
        }

        return entries;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiscImageStudio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Theme palette check failed: cannot locate DiscImageStudio.slnx above the test output folder.");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Theme palette check failed: " + name);
        }
    }
}
