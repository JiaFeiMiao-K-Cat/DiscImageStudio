using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiscImageStudio.Controls;

/// <summary>Places the drive details just right of the sidebar, bottom-aligned with the card.</summary>
internal sealed class DriveDetailsMargin : IValueConverter
{
    public static readonly DriveDetailsMargin Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new Thickness((value is double width ? width : 240) + 8, 0, 0, 16);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
