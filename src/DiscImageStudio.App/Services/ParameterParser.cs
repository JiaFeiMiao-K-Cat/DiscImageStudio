using DiscImageStudio.Localization;
using System.Globalization;

namespace DiscImageStudio.Services;

/// Input parsing with dual-culture tolerance; messages are identical to the original UI.
internal static class ParameterParser
{
    internal static double ParseDouble(string raw, string fieldName)
    {
        string value = raw.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
        {
            return result;
        }

        throw new FormatException(L.F($"{fieldName}不是有效数字。"));
    }

    internal static double ParsePositiveDouble(string raw, string fieldName)
    {
        double result = ParseDouble(raw, fieldName);
        return double.IsFinite(result) && result > 0
            ? result
            : throw new ArgumentOutOfRangeException(fieldName, L.F($"{fieldName}必须大于 0。"));
    }

    internal static int ParsePositiveInt(string raw, string fieldName)
    {
        string value = raw.Trim();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result > 0
            ? result
            : throw new FormatException(L.F($"{fieldName}必须是正整数。"));
    }

    internal static long ParsePositiveLong(string raw, string fieldName)
    {
        string value = raw.Trim();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) && result > 0
            ? result
            : throw new FormatException(L.F($"{fieldName}必须是正整数。"));
    }

    internal static long ParseNonNegativeLong(string raw, string fieldName)
    {
        string value = raw.Trim();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) && result >= 0
            ? result
            : throw new FormatException(L.F($"{fieldName}必须是非负整数。"));
    }

    internal static int ParsePreviewSize(string raw)
    {
        int size = ParsePositiveInt(raw, L.T("预览尺寸"));
        return size is >= 64 and <= 8192
            ? size
            : throw new ArgumentOutOfRangeException(nameof(raw), L.T("预览尺寸必须在 64–8192 px 之间。"));
    }

    internal static int ParsePreviewSamples(string raw, int maximum)
    {
        int samples = ParsePositiveInt(raw, L.T("每扇区采样"));
        return samples <= maximum
            ? samples
            : throw new ArgumentOutOfRangeException(nameof(raw), L.F($"每扇区采样必须在 1–{maximum} 之间。"));
    }
}
