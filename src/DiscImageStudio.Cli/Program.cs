using DiscImageStudio.Core;

namespace DiscImageStudio.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine("Disc Image Studio | experimental Linux and macOS CLI for offline CD, DVD and Blu-ray image generation");
            Console.WriteLine("Usage: disc-image-studio COMMAND [OPTIONS]");
            Console.WriteLine("  catalog [--json]      Print the command catalog as JSON for GUI clients");
            Console.WriteLine("  presets [--json]      Print the disc preset catalog for GUI clients");
            Console.WriteLine("  presets-import --file FILE   Merge a preset JSON into the user preset file");
            Console.WriteLine("  bd-preview            Render a Blu-ray design projection PNG");
            Console.WriteLine("  bd-preview-readback   Render a Blu-ray read-back PNG from an ISO sidecar");
            Console.WriteLine("  calibration-target    Render the built-in calibration target and session skeleton");
            Console.WriteLine("  calibration-photo     Resize and rectify a calibration photo");
            Console.WriteLine("  calibration-fit       Fit disc radii from calibration traces");
            Console.WriteLine("  calibration-coarse-target   Render the half-black coarse calibration target");
            Console.WriteLine("  calibration-coarse-fit      Fit radii/rotation from coarse boundary points");
            Console.WriteLine("  calibration-session-write   Write a Windows-compatible calibration session");
            Console.WriteLine("  calibration-session-read    Print a calibration session summary JSON");
            foreach (IOpticalDiscModule module in ConsoleModuleRunner.Catalog.Modules)
            {
                Console.WriteLine($"\n{module.Descriptor.DisplayName}");
                foreach (DiscCommandDescriptor command in module.Descriptor.Commands)
                    Console.WriteLine($"  {command.Name,-20} {command.Description}");
            }
            return 0;
        }

        if (args[0].Equals("catalog", StringComparison.OrdinalIgnoreCase))
        {
            return RunAuxiliary(() => CatalogJson.Run(args));
        }

        if (args[0].Equals("presets", StringComparison.OrdinalIgnoreCase))
        {
            return RunAuxiliary(() => PresetsCommand.Run(args));
        }

        if (args[0].Equals("presets-import", StringComparison.OrdinalIgnoreCase))
        {
            return RunAuxiliary(() => PresetsImportCommand.Run(args));
        }

        if (args[0].Equals("bd-preview", StringComparison.OrdinalIgnoreCase))
        {
            return RunAuxiliary(() => BdPreviewCommand.Run(args));
        }

        if (args[0].Equals("bd-preview-readback", StringComparison.OrdinalIgnoreCase))
        {
            return RunAuxiliary(() => BdPreviewReadbackCommand.Run(args));
        }

        if (args[0].Equals("calibration-target", StringComparison.OrdinalIgnoreCase))
        {
            return RunCalibration(() => CalibrationTargetCommand.Run(args));
        }

        if (args[0].Equals("calibration-photo", StringComparison.OrdinalIgnoreCase))
        {
            return RunCalibration(() => CalibrationPhotoCommand.Run(args));
        }

        if (args[0].Equals("calibration-fit", StringComparison.OrdinalIgnoreCase))
        {
            return RunCalibration(() => CalibrationFitCommand.Run(args));
        }

        if (args[0].Equals("calibration-coarse-target", StringComparison.OrdinalIgnoreCase))
        {
            return RunCalibration(() => CalibrationCoarseTargetCommand.Run(args));
        }

        if (args[0].Equals("calibration-coarse-fit", StringComparison.OrdinalIgnoreCase))
        {
            return RunCalibration(() => CalibrationCoarseFitCommand.Run(args));
        }

        if (args[0].Equals("calibration-session-write", StringComparison.OrdinalIgnoreCase))
        {
            return RunCalibration(() => CalibrationSessionWriteCommand.Run(args));
        }

        if (args[0].Equals("calibration-session-read", StringComparison.OrdinalIgnoreCase))
        {
            return RunCalibration(() => CalibrationSessionReadCommand.Run(args));
        }

        try
        {
            return ConsoleModuleRunner.Run(args);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"error: {error.Message}");
            return 1;
        }
    }

    /// <summary>Runs a read-only auxiliary command, turning failures into <c>error: ...</c>.</summary>
    private static int RunAuxiliary(Func<int> command)
    {
        try
        {
            return command();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"error: {error.Message}");
            return 1;
        }
    }

    /// <summary>Calibration commands use exit code 2 for malformed parameters and 1 for
    /// predictable I/O or session errors.</summary>
    private static int RunCalibration(Func<int> command)
    {
        try
        {
            return command();
        }
        catch (CalibrationUsageException error)
        {
            Console.Error.WriteLine($"error: {error.Message}");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"error: {error.Message}");
            return 1;
        }
    }
}
