using DvdImageSolver;

namespace DiscImageStudio.Dvd;

/// <summary>Portable entry point for the seven-colour grating DVD image generator.</summary>
public static class DvdGratingGenerator
{
    public static void Write(
        string imagePath,
        string outputPath,
        string volumeLabel,
        uint totalSectors,
        double inner,
        double outer,
        double cellMm,
        double pitchLinear,
        double pitchQuadratic,
        double pitchCubic)
        => GratingPaletteCommand.Write(imagePath, outputPath, volumeLabel, totalSectors,
            inner, outer, cellMm, pitchLinear, pitchQuadratic, pitchCubic);
}
