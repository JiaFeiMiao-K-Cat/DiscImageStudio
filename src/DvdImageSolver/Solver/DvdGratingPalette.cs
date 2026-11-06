namespace DvdImageSolver.Solver;

/// <summary>Single source of truth for the grating motif sequences and their
/// preview colours. The writer and the read-back preview both use this table.</summary>
public static class DvdGratingPalette
{
    public static readonly string[] Names =
        ["A", "B", "G", "C", "D", "E", "F"];

    public static readonly byte[][] Motifs =
    [
        [0xA5],                                      // A 0.853 µm
        [0x66],                                      // B 1.066 µm
        [0xB9],                                      // G 1.421 µm
        [0x09, 0x88, 0xA4, 0x71],                    // C 1.312 µm
        [0x43, 0x80, 0x69],                          // D 1.600 µm
        [0xB2, 0xD1, 0xCB, 0x3E, 0xC0, 0xCD, 0xE6],  // E 1.866 µm
        [0x92],                                      // F 2.133 µm
    ];

    public static readonly (byte R, byte G, byte B)[] Colours =
    [
        (128, 80, 220),
        (60, 190, 90),
        (230, 190, 50),
        (220, 60, 60),
        (60, 170, 140),
        (60, 130, 230),
        (230, 100, 50),
    ];

    public static bool TryColour(byte q, out (string Name, byte R, byte G, byte B) colour)
    {
        // A5/92 are the monochrome black/white motifs. They are deliberately
        // reserved and never reused as coloured periods.
        if (q == 0xA5) { colour = ("black", 0, 0, 0); return true; }
        if (q == 0x92) { colour = ("white", 255, 255, 255); return true; }
        for (int index = 0; index < Motifs.Length; index++)
        {
            if (index is 0 or 6) continue;
            if (Array.IndexOf(Motifs[index], q) >= 0)
            {
                var rgb = Colours[index];
                colour = (Names[index], rgb.R, rgb.G, rgb.B);
                return true;
            }
        }
        colour = default;
        return false;
    }

}
