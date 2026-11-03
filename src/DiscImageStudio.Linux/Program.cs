using Avalonia;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux;

/// <summary>How the process was asked to start.</summary>
public sealed record LaunchOptions(string? ProjectPath, string? ScreenshotDirectory, ThemePreference? Theme);

internal static class Program
{
    public static LaunchOptions Launch { get; private set; } = new(null, null, null);

    /// <summary>Without arguments (or with a .discproj path) this opens the GUI. Any engine
    /// command runs headless exactly like the disc-image-studio CLI, so one install serves
    /// both: <c>disc-image-studio-gui cd-generate --input …</c>.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            string first = args[0];
            if (first is "--help" or "-h" or "help")
            {
                PrintHelp();
                return 0;
            }

            if (first is "--version" or "-V")
            {
                Console.WriteLine($"Disc Image Studio {ViewModels.MainViewModel.Version} (Linux GUI)");
                return 0;
            }

            bool guiOption = first is "--screenshot" or "--theme" or "--project";
            bool projectFile = first.EndsWith(ProjectJsonStore.FileExtension, StringComparison.OrdinalIgnoreCase) || first.StartsWith("file://", StringComparison.Ordinal);
            if (!guiOption && !projectFile)
            {
                return RunCli(args);
            }

            Launch = ParseGuiOptions(args);
        }

        AppPaths.CleanWorkDirectory();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                // Matches StartupWMClass in the .desktop file so docks group the window.
                WmClass = "DiscImageStudio",
                UseDBusFilePicker = true,
            })
            .WithInterFont()
            .LogToTrace();

    private static LaunchOptions ParseGuiOptions(string[] args)
    {
        string? project = null, screenshots = null;
        ThemePreference? theme = null;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            string? Next() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"Missing value for {argument}.");
            switch (argument)
            {
                case "--screenshot":
                    screenshots = Next();
                    break;
                case "--theme":
                    theme = Next()?.ToLowerInvariant() switch
                    {
                        "dark" => ThemePreference.Dark,
                        "light" => ThemePreference.Light,
                        _ => ThemePreference.System,
                    };
                    break;
                case "--project":
                    project = Next();
                    break;
                default:
                    project = argument.StartsWith("file://", StringComparison.Ordinal) ? new Uri(argument).LocalPath : argument;
                    break;
            }
        }

        return new LaunchOptions(project, screenshots, theme);
    }

    private static int RunCli(string[] args)
    {
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

    private static void PrintHelp()
    {
        Console.WriteLine($"Disc Image Studio {ViewModels.MainViewModel.Version} for Linux");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  disc-image-studio-gui [PROJECT.discproj]     open the graphical interface");
        Console.WriteLine("  disc-image-studio-gui COMMAND [OPTIONS]      run an engine command without the GUI");
        Console.WriteLine("  disc-image-studio-gui --screenshot DIR [--project FILE] [--theme dark|light]");
        Console.WriteLine("                                               render every page to DIR/*.png and exit");
        Console.WriteLine("  disc-image-studio-gui --version");
        foreach (IOpticalDiscModule module in ConsoleModuleRunner.Catalog.Modules)
        {
            Console.WriteLine($"\n{module.Descriptor.DisplayName}");
            foreach (DiscCommandDescriptor command in module.Descriptor.Commands)
            {
                Console.WriteLine($"  {command.Name,-20} {command.Description}");
            }
        }
    }
}
