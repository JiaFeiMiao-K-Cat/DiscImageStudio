using System.Windows.Markup;

namespace DiscImageStudio.Localization;

/// <summary>Localized text for XAML, including styles and control templates.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension() { }

    public TextExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
