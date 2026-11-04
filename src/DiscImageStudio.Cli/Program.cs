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
            foreach (IOpticalDiscModule module in ConsoleModuleRunner.Catalog.Modules)
            {
                Console.WriteLine($"\n{module.Descriptor.DisplayName}");
                foreach (DiscCommandDescriptor command in module.Descriptor.Commands)
                    Console.WriteLine($"  {command.Name,-20} {command.Description}");
            }
            return 0;
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
}
