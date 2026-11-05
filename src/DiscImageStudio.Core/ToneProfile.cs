using System.Text.Json.Serialization;

namespace DiscImageStudio.Core;

/// <summary>
/// How a source grey ramp is translated into the disc's recordable levels.
/// <para><see cref="DitherStrength"/> 0 keeps the hard threshold the project shipped with;
/// 1 mixes neighbouring levels per byte so midtones survive at viewing distance.</para>
/// <para><see cref="Levels"/> is 2 (black/white) or 4 (two intermediate tones, CD only).
/// <see cref="Brightness"/> shifts the whole ramp in 0..255 luminance units.</para>
/// </summary>
public readonly record struct ToneProfile(
    double DitherStrength,
    int Levels,
    double Brightness,
    bool SrgbLinearization = false)
{
    public const int TwoLevels = 2;
    public const int FourLevels = 4;
    public const double MinimumBrightness = -64;
    public const double MaximumBrightness = 64;

    // Gray inputs are bytes. Retain the exact double result of the original
    // transfer function so CD/DVD need no per-payload-byte Math.Pow. Brightness
    // and final rounding still run in the original order for every profile.
    private static readonly double[] SrgbLinearValues = CreateSrgbLinearValues();

    private static double[] CreateSrgbLinearValues()
    {
        var values = new double[256];
        for (int gray = 0; gray < values.Length; gray++)
        {
            double value = gray / 255.0;
            values[gray] = value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return values;
    }

    /// <summary>The pre-tone behaviour: one hard threshold at mid-grey, two levels.</summary>
    public static ToneProfile LegacyBinary { get; } = new(0, TwoLevels, 0);

    /// <summary>Photographic starting point: 75% byte-level dither, still two levels. The
    /// missing quarter keeps the grain from dominating the picture at viewing distance while
    /// the dither still reconstructs midtones.</summary>
    public static ToneProfile Photo { get; } = new(0.75, TwoLevels, 0);

    /// <summary>True when mapping is exactly the old <c>grey &lt; 128 ? black : white</c> rule.</summary>
    [JsonIgnore]
    public bool IsLegacyBinary
        => DitherStrength <= 0 && Levels == TwoLevels && Brightness == 0 && !SrgbLinearization;

    [JsonIgnore]
    public bool IsDvdCompatible => Levels == TwoLevels;

    /// <summary>Source grey → sRGB-linear light (when enabled) → brightness offset. This is
    /// the one tone curve shared by the byte-level CD/DVD paths and the image-level BD path;
    /// gamma changes black/white dot density, not the output voltage or reflectance.</summary>
    public byte AdjustedGray(byte gray)
    {
        double value = SrgbLinearization ? SrgbLinearValues[gray] : gray / 255.0;
        value = Math.Clamp(value + (Brightness / 255.0), 0.0, 1.0);
        return (byte)Math.Round(value * 255.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Binary decision shared with the engines' 128 threshold. Absent dithering
    /// and gamma this is exactly the historical threshold comparison.</summary>
    public bool UseWhite(byte gray, long sampleIndex, double threshold = 128)
    {
        if (IsLegacyBinary)
        {
            return gray >= threshold;
        }

        double strength = DitherStrength > 1 ? 1 : DitherStrength;
        double shiftedThreshold = threshold + (strength * 255.0 * (ToneDither.Phase(sampleIndex) - 0.5));
        return AdjustedGray(gray) >= shiftedThreshold;
    }

    public void Validate(string fieldName = "tone")
    {
        if (!double.IsFinite(DitherStrength) || DitherStrength is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                fieldName,
                $"{fieldName}.ditherStrength must be a finite number between 0 and 1.");
        }

        if (Levels is not (TwoLevels or FourLevels))
        {
            throw new ArgumentOutOfRangeException(
                fieldName,
                $"{fieldName}.levels must be {TwoLevels} or {FourLevels}.");
        }

        if (!double.IsFinite(Brightness)
            || Brightness is < MinimumBrightness or > MaximumBrightness)
        {
            throw new ArgumentOutOfRangeException(
                fieldName,
                $"{fieldName}.brightness must be between {MinimumBrightness} and {MaximumBrightness}.");
        }
    }

    /// <summary>Clamps a user-facing value into the supported range instead of failing on a
    /// slider rounding artefact; persisted values are still validated strictly.</summary>
    public ToneProfile Normalized()
        => new(
            Math.Clamp(double.IsFinite(DitherStrength) ? DitherStrength : 0, 0, 1),
            Levels == FourLevels ? FourLevels : TwoLevels,
            Math.Clamp(
                double.IsFinite(Brightness) ? Brightness : 0,
                MinimumBrightness,
                MaximumBrightness),
            SrgbLinearization);
}

/// <summary>
/// Deterministic per-byte dither phase in [0, 1). The phase is keyed to the logical sample
/// index, not to a screen coordinate, so it survives the CD delay interleave and can be
/// reproduced by the read-back preview byte for byte.
/// <para>A hashed sequence is used instead of an ordered matrix or a golden-ratio sequence:
/// previews sample every Nth byte, and any coherent sequence (including a low-discrepancy one)
/// beats against the fixed stride and paints rings or moiré. Hashing decorrelates the sampled
/// subset, so a sparse preview sees grain instead of structure.</para>
/// </summary>
public static class ToneDither
{
    public static double Phase(long sampleIndex)
    {
        uint value = unchecked((uint)sampleIndex * 2654435761u);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value / 4294967296.0;
    }
}
