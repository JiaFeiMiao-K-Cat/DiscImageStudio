using System.Globalization;

namespace DiscImageStudio.Linux.Services;

/// <summary>Invariant number formatting for the engine command lines and the editors.</summary>
public static class Num
{
    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Bool(bool value) => value ? "true" : "false";

    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        string trimmed = (text ?? string.Empty).Trim().Replace(',', '.');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    public static bool TryParse(string? text, out long value)
        => long.TryParse((text ?? string.Empty).Trim().Replace("_", string.Empty).Replace(",", string.Empty),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string Duration(TimeSpan elapsed)
        => elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
