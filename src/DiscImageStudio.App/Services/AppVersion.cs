using System.Globalization;
using System.Numerics;

namespace DiscImageStudio.Services;

/// <summary>Semantic version used by the update manifest. Build metadata is accepted and
/// ignored for ordering; prerelease identifiers follow SemVer 2.0 precedence.</summary>
internal readonly record struct AppVersion(int Major, int Minor, int Patch, string? Prerelease)
    : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int metadataIndex = text.IndexOf('+');
        if (metadataIndex >= 0)
        {
            if (metadataIndex == text.Length - 1)
            {
                return false;
            }

            text = text[..metadataIndex];
        }

        string? prerelease = null;
        int prereleaseIndex = text.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            prerelease = text[(prereleaseIndex + 1)..];
            if (!IsValidPrerelease(prerelease))
            {
                return false;
            }

            text = text[..prereleaseIndex];
        }

        string[] components = text.Split('.');
        if (components.Length != 3
            || !TryParseComponent(components[0], out int major)
            || !TryParseComponent(components[1], out int minor)
            || !TryParseComponent(components[2], out int patch))
        {
            return false;
        }

        version = new AppVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        if (Prerelease is null)
        {
            return other.Prerelease is null ? 0 : 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        string[] left = Prerelease.Split('.');
        string[] right = other.Prerelease.Split('.');
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            result = CompareIdentifier(left[index], right[index]);
            if (result != 0) return result;
        }

        return left.Length.CompareTo(right.Length);
    }

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (Prerelease is null ? string.Empty : "-" + Prerelease);

    private static bool TryParseComponent(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && text.Length > 0
        && (text == "0" || text[0] != '0');

    private static bool IsValidPrerelease(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (string identifier in text.Split('.'))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            bool numeric = true;
            foreach (char character in identifier)
            {
                if (character is >= '0' and <= '9')
                {
                    continue;
                }

                numeric = false;
                if (character is not (>= 'a' and <= 'z') && character is not (>= 'A' and <= 'Z') && character != '-')
                {
                    return false;
                }
            }

            if (numeric && identifier.Length > 1 && identifier[0] == '0')
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareIdentifier(string left, string right)
    {
        bool leftNumeric = IsNumeric(left);
        bool rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            return BigInteger.Parse(left, CultureInfo.InvariantCulture)
                .CompareTo(BigInteger.Parse(right, CultureInfo.InvariantCulture));
        }

        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string text) => text.All(character => character is >= '0' and <= '9');
}
