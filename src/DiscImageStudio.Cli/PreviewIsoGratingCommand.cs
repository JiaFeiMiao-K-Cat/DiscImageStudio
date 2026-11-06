using DiscImageStudio.Dvd;
using DvdImageSolver.Solver;

namespace DiscImageStudio.Cli;

/// <summary>Colour read-back of a DVD ISO by grating motif, for GUI previews.</summary>
internal static class PreviewIsoGratingCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        CliOptionSet options = CliOptionSet.Parse(arguments, 1,
        [
            "iso", "output", "total-sectors", "lba", "fill-sectors", "psn-offset",
            "inner-radius-mm", "outer-radius-mm", "channel-bit-nm", "clockwise",
            "preview-size", "samples-per-sector",
            "pitch-linear", "pitch-quadratic", "pitch-cubic",
        ]);
        string isoPath = Path.GetFullPath(options.Require("iso"));
        string outputPath = Path.GetFullPath(options.Require("output"));
        GratingReadbackOptions readback = new(
            options.GetUInt("total-sectors", 0),
            options.GetUInt("lba", 32),
            options.GetUInt("fill-sectors", 0),
            options.GetUInt("psn-offset", 0x30000),
            options.GetDouble("inner-radius-mm", 0),
            options.GetDouble("outer-radius-mm", 0),
            options.GetDouble("channel-bit-nm", 133.3),
            options.GetBool("clockwise", true),
            options.GetInt("preview-size", 1200),
            options.GetInt("samples-per-sector", 32),
            options.GetDouble("pitch-linear", 0),
            options.GetDouble("pitch-quadratic", 0),
            options.GetDouble("pitch-cubic", 0));
        GratingReadbackSummary summary = GratingIsoPreview.Render(isoPath, outputPath, readback);
        Console.WriteLine(
            $"DVD grating preview: {summary.PreviewSize}x{summary.PreviewSize}, "
            + $"coloured={summary.ColouredSamples}, off={summary.OffSamples}, output={outputPath}");
        return 0;
    }
}
