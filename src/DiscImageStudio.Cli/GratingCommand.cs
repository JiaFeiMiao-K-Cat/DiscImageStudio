using DiscImageStudio.Dvd;

namespace DiscImageStudio.Cli;

/// <summary>Writes the seven-colour grating DVD ISO, to a file or to stdout.</summary>
internal static class GratingCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        CliOptionSet options = CliOptionSet.Parse(arguments, 1,
        [
            "image", "output", "volume-label", "total-sectors", "cell-mm",
            "inner-radius-mm", "outer-radius-mm",
            "pitch-linear", "pitch-quadratic", "pitch-cubic",
        ]);
        DvdGratingGenerator.Write(
            options.Require("image"),
            options.Require("output"),
            options.GetString("volume-label", "SUZUME-GRATING"),
            options.GetUInt("total-sectors", 2_297_888),
            options.GetDouble("inner-radius-mm", 24.063112765327595),
            options.GetDouble("outer-radius-mm", 58.22734793558852),
            options.GetDouble("cell-mm", 0.034),
            options.GetDouble("pitch-linear", -3.625995787598983e-11),
            options.GetDouble("pitch-quadratic", 5.59609470367389e-10),
            options.GetDouble("pitch-cubic", 4.337809583594884e-10));
        return 0;
    }
}
