using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiscImageStudio.Localization;

/// <summary>Single binding source for every localized string. The revision only exists so a
/// language switch has a property change to tell the bindings about.</summary>
public sealed class LocalizedTextSource : INotifyPropertyChanged
{
    public static LocalizedTextSource Instance { get; } = new();

    private LocalizedTextSource()
    {
        L.LanguageChanged += (_, _) =>
        {
            Revision++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        };
    }

    public int Revision { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Resolves a catalog key from a binding. Also used as a multi-value converter for
/// text whose key is selected by a template trigger: first value is the key, the second is the
/// language revision that makes the switch observable.</summary>
public sealed class LocalizedTextConverter : IValueConverter, IMultiValueConverter
{
    public static LocalizedTextConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => parameter is string key ? L.T(key) : string.Empty;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length > 0 && values[0] is string key ? L.T(key) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Catalog key held on a visual so a template trigger can swap the text. The visual's
/// <c>Text</c> binds to this property, which keeps trigger precedence intact (a trigger that
/// wrote <c>Text</c> itself would lose to the local value a runtime refresh needs).</summary>
public static class LocalizedText
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(LocalizedText), new PropertyMetadata(string.Empty));

    public static string GetKey(DependencyObject target) => (string)target.GetValue(KeyProperty);

    public static void SetKey(DependencyObject target, string value) => target.SetValue(KeyProperty, value);
}
