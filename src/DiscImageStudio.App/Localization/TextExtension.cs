using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Markup;

namespace DiscImageStudio.Localization;

/// <summary>Localized text for XAML, including styles and control templates. The extension
/// writes the literal string for the current language and remembers the target, so a runtime
/// language switch can rewrite it in place. A binding cannot be returned here: the XAML loader
/// stores extension results as local values, which rejects <see cref="System.Windows.Data.Binding"/>
/// objects.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TextExtension : MarkupExtension
{
    private static readonly ConditionalWeakTable<DependencyObject, List<Target>> Targets = new();
    private static bool _subscribed;

    private sealed record Target(DependencyProperty Property, string Key);

    public TextExtension() { }

    public TextExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        string text = L.T(Key);
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget
            { TargetObject: DependencyObject target, TargetProperty: DependencyProperty property }
            && target is not Freezable { IsFrozen: true })
        {
            Track(target, property, Key);
        }

        return text;
    }

    private static void Track(DependencyObject target, DependencyProperty property, string key)
    {
        List<Target> targets = Targets.GetOrCreateValue(target);
        for (int i = targets.Count - 1; i >= 0; i--)
        {
            if (targets[i].Property == property)
            {
                targets.RemoveAt(i);
            }
        }

        targets.Add(new Target(property, key));

        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        L.LanguageChanged += (_, _) => Refresh();
    }

    private static void Refresh()
    {
        foreach ((DependencyObject target, List<Target> targets) in Targets)
        {
            foreach (Target entry in targets)
            {
                target.SetValue(entry.Property, L.T(entry.Key));
            }
        }
    }
}
