using DiscImageStudio.Core;

namespace DvdImageSolver.Solver;

/// <summary>Black and white DVD tones, expressed as scrambled bytes Q presented to EFMPlus.</summary>
public static class DvdGrayscalePalette
{
    public const byte BlackScrambledByte = 0xA5;
    public const byte WhiteScrambledByte = 0x92;

    public static ReadOnlySpan<byte> ScrambledBytes =>
        [BlackScrambledByte, WhiteScrambledByte];

    public static byte GetScrambledByte(byte luminance)
        => GetScrambledByteForLevel(BinaryGrayscale.GetLevelIndex(luminance));

    public static byte GetScrambledByteForLevel(int levelIndex)
        => levelIndex switch
        {
            0 => BlackScrambledByte,
            1 => WhiteScrambledByte,
            _ => throw new ArgumentOutOfRangeException(nameof(levelIndex)),
        };

    public static bool TryGetLuminance(byte scrambledByte, out byte luminance)
    {
        int value = scrambledByte switch
        {
            BlackScrambledByte => 0,
            WhiteScrambledByte => 255,
            // Legacy four-tone ISOs used Q=66 as dark gray. On the tested disc it
            // merges with A5 at ordinary viewing angles; never generate it anew.
            0x66 => 0,
            // Historical three-/four-tone output is still readable. Its B9 tone
            // retains the nominal dark appearance rather than being called white.
            0xB9 => 85,
            _ => -1,
        };
        luminance = value < 0 ? (byte)0 : (byte)value;
        return value >= 0;
    }
}
