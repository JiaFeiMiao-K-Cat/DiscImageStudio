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
            Console.WriteLine("  bd-preview            Render a Blu-ray design projection PNG");
            Console.WriteLine("  bd-preview-readback   Render a Blu-ray read-back PNG from an ISO sidecar");
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

        if (args[0].Equals("bd-preview", StringComparison.OrdinalIgnoreCase))
        {
            return RunAuxiliary(() => BdPreviewCommand.Run(args));
        }

        if (args[0].Equals("bd-preview-readback", StringComparison.OrdinalIgnoreCase))
        {
            return RunAuxiliary(() => BdPreviewReadbackCommand.Run(args));
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
}
