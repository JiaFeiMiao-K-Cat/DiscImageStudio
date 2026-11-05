using DiscImageStudio.Burning;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio;

internal static class UnifiedCommandRunner
{
    private static DiscModuleCatalog Modules => ConsoleModuleRunner.Catalog;

    internal static int Run(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0)
            {
                throw new ArgumentException("Missing command.");
            }

            string command = arguments[0].ToLowerInvariant();
            if (command is "help" or "--help" or "-h")
            {
                return PrintHelp();
            }

            if (command == "ui-snapshot")
            {
                ReadOnlySpan<string> snapshotArguments = arguments.AsSpan(1);
                string? tabValue = FindOption(snapshotArguments, "tab");
                int workspaceIndex = tabValue is null ? 0 : int.Parse(tabValue);
                if (workspaceIndex < 0 || workspaceIndex > (int)Workspace.CalibrationBurn)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(arguments),
                        "Tabs: 0 home, 1 design, 2 preview, 3 export, 4 burn, 5 calibration, 6 calibration burn.");
                }

                bool settingsDialog = HasOption(snapshotArguments, "settings-dialog");
                return UiPreviewRenderer.Render(
                    RequireOption(snapshotArguments, "output"),
                    (Workspace)workspaceIndex,
                    FindOption(snapshotArguments, "preview"),
                    FindOption(snapshotArguments, "live-input"),
                    FindOption(snapshotArguments, "live-disc"),
                    FindOption(snapshotArguments, "live-mode"),
                    int.Parse(FindOption(snapshotArguments, "width") ?? (settingsDialog ? "760" : "1366")),
                    int.Parse(FindOption(snapshotArguments, "height") ?? (settingsDialog ? "600" : "768")),
                    FindOption(snapshotArguments, "calibration-session"),
                    FindOption(snapshotArguments, "calibration-view"),
                    FindOption(snapshotArguments, "project"),
                    FindOption(snapshotArguments, "project-dialog"),
                    HasOption(snapshotArguments, "preset-dialog"),
                    HasOption(snapshotArguments, "dock"),
                    FindOption(snapshotArguments, "burn-source"),
                    HasOption(snapshotArguments, "expand"),
                    int.Parse(FindOption(snapshotArguments, "scroll") ?? "0"),
                    HasOption(snapshotArguments, "home-hover"),
                    HasOption(snapshotArguments, "project-update"),
                    HasOption(snapshotArguments, "project-save"),
                    HasOption(snapshotArguments, "tone-preview"),
                    FindOption(snapshotArguments, "burn-progress"),
                    FindOption(snapshotArguments, "theme"),
                    settingsDialog,
                    FindOption(snapshotArguments, "settings-view"),
                    FindOption(snapshotArguments, "language"));
            }

            if (command == "burn-devices")
            {
                return PrintBurnDevices();
            }

            if (command == "burn-build-info")
            {
                return PrintBurnBuildInfo();
            }

            return ConsoleModuleRunner.Run(arguments);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception}");
            return 1;
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine(
            """
            Disc Image Studio - modular CD/DVD/Blu-ray optical-disc image generator

            Start without arguments to open the graphical interface.
            Disc commands are provided by modules through DiscImageStudio.Core.
            """);
        foreach (IOpticalDiscModule module in Modules.Modules)
        {
            Console.WriteLine($"\n{module.Descriptor.DisplayName} [{module.Descriptor.Id}]");
            foreach (DiscCommandDescriptor command in module.Descriptor.Commands)
            {
                Console.WriteLine($"  {command.Name,-20} {command.Description}");
            }
        }

        Console.WriteLine(
            """

            Developer command:
              ui-snapshot --output SCREENSHOT.png [--tab N] [--width 1366] [--height 768]
                          [--project FILE.discproj] [--live-input IMAGE --live-disc cd|dvd]
                          [--dock] [--burn-source project|calibration]
                          [--preset-dialog] [--settings-dialog]
                          [--language zh-CN|zh-TW|en|ja|tr|es|cs|system]
                          [--settings-view general|storage|about] [--theme dark|light|system]
                          [--expand] [--scroll N] [--home-hover]
                          [--burn-progress write|buffer|finalize|write-complete|verify|flush|close-track|close-disc|release]
                          [--project-update] [--project-save] [--tone-preview]
              burn-devices
              burn-build-info

            Tabs: 0 home, 1 design, 2 preview, 3 export, 4 burn, 5 calibration, 6 calibration burn.
            --dock expands the job dock (progress + run log) for the snapshot; --burn-source
            picks what the burn workspace shows as the material to write (calibration also
            creates the calibration target); --scroll N scrolls the page down N pixels so the
            part below the viewport can be captured; --project-update plays the applied-
            calibration header acknowledgement; --project-save paints the saved-project
            header trail at a fixed point in its lap; --tone-preview waits for the design
            page's tone module and switches the snapshot to that page; --theme picks the
            palette before the window is created; --settings-dialog captures the settings
            window at 760x600 by default and never touches the real settings file;
            --settings-view picks its category.

            Blu-ray: bd-info exports parameters; bd-probe exports raw payload tests;
            bd-encode exports one ECC cluster; bd-generate maps dark to 3T and light to 6T run-count bias.
              bd-generate --input IMAGE.png --output PICTURE.iso [--invert true]
                          [--total-sectors 12219392] [--data-dir FOLDER] [--volume-label BD_IMAGE]
                          [--tone-dither 0..1] [--tone-brightness -64..64] [--tone-gamma true]
            Total sectors must align to 32. Output is a complete ISO9660/Joliet image:
            inner files first, followed by picture data. Press Ctrl+C to cancel.
            See docs/ADDING_BLURAY.md for options and physical-model limitations.
            """);
        return 0;
    }

    private static int PrintBurnDevices()
    {
        IReadOnlyList<OpticalBurnDevice> devices = new WindowsImapiBurner()
            .GetDevicesAsync()
            .GetAwaiter()
            .GetResult();
        if (devices.Count == 0)
        {
            Console.WriteLine("No IMAPI2 optical recorder was found.");
            return 0;
        }

        foreach (OpticalBurnDevice device in devices)
        {
            Console.WriteLine($"{device.DisplayName}\t{device.Id}");
        }

        return 0;
    }

    private static int PrintBurnBuildInfo()
    {
        Type burningAssemblyMarker = typeof(WindowsImapiBurner);
        Type? sessionType = burningAssemblyMarker.Assembly.GetType(
            "DiscImageStudio.Burning.ICdTrackAtOnceSession",
            throwOnError: false);
        bool containsLegacyFinalizeSetter = sessionType?
            .GetProperty("DoNotFinalizeMedia") is not null;
        Type? rawSessionType = burningAssemblyMarker.Assembly.GetType(
            "DiscImageStudio.Burning.ICdRawSession",
            throwOnError: false);
        bool containsRawDaoWriter = rawSessionType?
            .GetMethod("WriteMedia") is not null;
        Console.WriteLine(
            $"Burning assembly MVID: {burningAssemblyMarker.Module.ModuleVersionId}");
        Console.WriteLine(
            $"Legacy DoNotFinalizeMedia setter present: {containsLegacyFinalizeSetter}");
        Console.WriteLine($"Raw CD DAO writer present: {containsRawDaoWriter}");
        return containsLegacyFinalizeSetter || !containsRawDaoWriter ? 1 : 0;
    }

    private static string RequireOption(ReadOnlySpan<string> arguments, string key)
        => FindOption(arguments, key)
            ?? throw new ArgumentException($"Missing required option --{key}.");

    private static string? FindOption(ReadOnlySpan<string> arguments, string key)
    {
        string name = "--" + key;
        string prefix = name + "=";
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }

            if (argument.Equals(name, StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Length)
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    /// Presence check for valueless flags (<c>--dock</c>): FindOption cannot express them —
    /// trailing, it finds no next token; mid-order, it would swallow the next flag as a value.
    private static bool HasOption(ReadOnlySpan<string> arguments, string key)
    {
        string name = "--" + key;
        for (int index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

}
