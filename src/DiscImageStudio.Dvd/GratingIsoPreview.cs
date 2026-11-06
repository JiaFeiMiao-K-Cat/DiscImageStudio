using DvdImageSolver.Solver;

namespace DiscImageStudio.Dvd;

/// <summary>Portable entry point used by the main CLI and the macOS GUI.</summary>
public static class GratingIsoPreview
{
    public static GratingReadbackSummary Render(
        string isoPath, string outputPath, GratingReadbackOptions options)
        => GratingReadbackRenderer.Render(isoPath, outputPath, options);
}
