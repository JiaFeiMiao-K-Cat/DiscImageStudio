using DiscImageStudio.Core;

namespace DvdImageSolver.Solver;

/// <summary>Black and white DVD tones, expressed as scrambled bytes Q presented to EFMPlus.</summary>
public static class DvdGrayscalePalette
{
    public const byte BlackScrambledByte = 0xA5;
    public const byte WhiteScrambledByte = 0x92;

    public static ReadOnlySpan<byte> ScrambledBytes =>
        [BlackScrambledByte, WhiteScrambledByte];

    // Experimental write-safe mode. 0xA5 and 0x92 are the only bytes with exactly
    // 5 / 2 transitions in every EFMPlus state, so a pure tone area records as an
    // almost single-frequency signal (~80% 3T for black, 100% 8T for white). Some
    // drive/media pairs (slim drives, low-grade dye) lose runtime OPC on such long
    // single-tone stretches and abort with an unrecoverable write error. Replacing
    // every Nth anchor byte with a near-tone byte broadens the run-length spectrum.
    // The inserts were chosen so that an encoder choosing main/substitution words
    // by |DSV| stays DC-balanced (simulated max |DSV| <= 2). Contrast cost is ~17%.
    private static readonly byte[] WhiteWriteSafeInserts = [0x90, 0x94, 0x98, 0x9D];
    private static readonly byte[] BlackWriteSafeInserts = [0x3B, 0x3C, 0x63, 0x64];
    private const int WhiteWriteSafePeriod = 4;
    private const int BlackWriteSafePeriod = 4;

    // Level 2 ("strong"): long dark areas stay ~70% 3T at level 1, and a slim drive
    // was observed to stall where a dark band thickens. Every 2nd black byte becomes
    // a 3–5 transition byte: 3T share 80% -> 38%, runs spread over 3T..6T, simulated
    // max |DSV| 2 with run-length-valid encoder choices. Dark areas get visibly lighter.
    private static readonly byte[] BlackStrongWriteSafeInserts = [0x58, 0x5D, 0xB0, 0xB7];
    private const int BlackStrongWriteSafePeriod = 2;

    /// <summary>DISCIMAGESTUDIO_DVD_WRITE_SAFE: 1 = light mixing, 2 = strong mixing for dark areas,
    /// 3 = zonal: strong only inside an LBA zone, light elsewhere.</summary>
    public static int WriteSafeLevel { get; } =
        Environment.GetEnvironmentVariable("DISCIMAGESTUDIO_DVD_WRITE_SAFE") switch
        {
            "3" or "zone" => 3,
            "2" or "strong" => 2,
            "1" or "true" => 1,
            _ => 0,
        };

    public static bool WriteSafeEnabled => WriteSafeLevel > 0;

    // Default zone ≈ 26.4–30.6 mm on a 24–58 mm single-layer disc (track fraction
    // 0.044–0.131): the ring where a slim drive stalled on long dark stretches.
    // Override with DISCIMAGESTUDIO_DVD_WRITE_SAFE_ZONE=startLba-endLba.
    private const long DefaultZoneStartLba = 100_000;
    private const long DefaultZoneEndLba = 300_000;
    private const long PayloadBytesPerSector = 2048;

    public static (long Start, long End) WriteSafeZone { get; } = ParseZone(
        Environment.GetEnvironmentVariable("DISCIMAGESTUDIO_DVD_WRITE_SAFE_ZONE"));

    private static (long, long) ParseZone(string? text)
    {
        string[] parts = (text ?? string.Empty).Split('-', 2);
        return parts.Length == 2 && long.TryParse(parts[0], out long start)
            && long.TryParse(parts[1], out long end) && start >= 0 && end > start
            ? (start, end)
            : (DefaultZoneStartLba, DefaultZoneEndLba);
    }

    private static bool UseStrongBlack(long toneIndex)
    {
        if (WriteSafeLevel == 2) return true;
        if (WriteSafeLevel != 3) return false;
        long lba = toneIndex / PayloadBytesPerSector;
        return lba >= WriteSafeZone.Start && lba < WriteSafeZone.End;
    }

    /// <summary>Returns the anchor tone byte, or a write-safe insert at its rotating slot.</summary>
    public static byte ApplyWriteSafe(byte anchor, long toneIndex)
    {
        if (!WriteSafeEnabled || toneIndex < 0) return anchor;
        (byte[] inserts, int period) = anchor switch
        {
            WhiteScrambledByte => (WhiteWriteSafeInserts, WhiteWriteSafePeriod),
            BlackScrambledByte when UseStrongBlack(toneIndex) => (BlackStrongWriteSafeInserts, BlackStrongWriteSafePeriod),
            BlackScrambledByte => (BlackWriteSafeInserts, BlackWriteSafePeriod),
            _ => (Array.Empty<byte>(), 0),
        };
        if (period == 0 || toneIndex % period != 0) return anchor;
        return inserts[(int)(toneIndex / period % inserts.Length)];
    }

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
            // Experimental write-safe inserts read back as their anchor tone.
            0x90 or 0x94 or 0x98 or 0x9D => 255,
            0x3B or 0x3C or 0x63 or 0x64 => 0,
            0x58 or 0x5D or 0xB0 or 0xB7 => 0,
            _ => -1,
        };
        luminance = value < 0 ? (byte)0 : (byte)value;
        return value >= 0;
    }
}
