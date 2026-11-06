using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

/// <summary>Optional sector-local bridge for dispersion payloads. This changes only
/// explicitly owned drawing bytes; callers remain responsible for excluding file extents.</summary>
internal static class DvdReadCompatibilityGuard
{
    internal const string ModeName = "bridge-9b-v1";
    private const int WindowLength = 64;
    private const int MinimumPaletteBytes = 58;

    // Algorithm based on Ashley-QwQ/disc-probe, tools/bridge_iso.py (GPL-3.0),
    // commit 7574eca39e009f70c9b80a9a0ddb5bbe3d392173.
    // The source's sector/window rules are retained; explicit byte ownership
    // replaces its content-based assumption that a sector contains drawing data.
    internal static int ApplySector(
        Span<byte> payload, ReadOnlySpan<byte> scramblerMask, ReadOnlySpan<byte> ownership)
    {
        const int sectorLength = DvdEccBlockEncoder.PayloadBytesPerSector;
        if (payload.Length != sectorLength || scramblerMask.Length != sectorLength
            || ownership.Length != sectorLength)
            throw new ArgumentException("Payload, scrambler mask, and ownership must each contain one 2048-byte sector.");

        // All decisions use the original Q. Bridges written earlier in this
        // sector must not alter either later run detection or window counts.
        Span<byte> original = stackalloc byte[sectorLength];
        Span<int> palettePrefix = stackalloc int[sectorLength + 1];
        palettePrefix[0] = 0;
        for (int index = 0; index < sectorLength; index++)
        {
            byte value = (byte)(payload[index] ^ scramblerMask[index]);
            original[index] = value;
            palettePrefix[index + 1] = palettePrefix[index] + (value is 0xA5 or 0x92 ? 1 : 0);
        }

        int changes = 0;
        for (int index = 0; index < sectorLength;)
        {
            if (original[index] != 0x92)
            {
                index++;
                continue;
            }

            int start = index++;
            while (index < sectorLength && original[index] == 0x92)
                index++;
            if (index - start < 2 || ownership[start] == 0
                || (start > 0 && original[start - 1] is 0x75 or 0x9B))
                continue;

            int backwardStart = start + 1 - WindowLength;
            bool forward = start + WindowLength <= sectorLength
                && palettePrefix[start + WindowLength] - palettePrefix[start] >= MinimumPaletteBytes;
            bool backward = backwardStart >= 0
                && palettePrefix[start + 1] - palettePrefix[backwardStart] >= MinimumPaletteBytes;
            if (!forward && !backward)
                continue;

            payload[start] = (byte)(0x9B ^ scramblerMask[start]);
            changes++;
        }

        return changes;
    }
}
