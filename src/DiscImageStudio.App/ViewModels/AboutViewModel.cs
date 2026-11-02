using DiscImageStudio.Localization;
using System.IO;
using System.Reflection;

namespace DiscImageStudio.ViewModels;

public class AboutViewModel
{
    public string VersionText { get; } = BuildVersionText();

    public string LicenseText { get; } = ReadLicense("GPL");

    public string ThirdPartyNoticesText { get; } = ReadLicense("ThirdParty");

    public string FontLicenseText { get; } = ReadLicense("HarmonyOS");

    private static string ReadLicense(string name)
    {
        using Stream stream = typeof(AboutViewModel).Assembly.GetManifestResourceStream($"DiscImageStudio.Licenses.{name}")
            ?? throw new InvalidOperationException($"Missing embedded license: {name}");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string BuildVersionText()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return L.F($"版本 {version?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");
    }
}
