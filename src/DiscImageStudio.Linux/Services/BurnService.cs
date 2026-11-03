using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DiscImageStudio.Core;

namespace DiscImageStudio.Linux.Services;

public enum BurnMaterial
{
    CdAudio,
    CdData,
    Dvd,
    BluRay,
}

public sealed record OpticalDrive(string Device, string Description, bool Accessible)
{
    public override string ToString() => Description;
}

/// <summary>A burning program found on PATH.</summary>
public sealed record BurnTool(string Id, string DisplayName, string Executable)
{
    public override string ToString() => DisplayName;
}

public sealed record BurnCommand(string Executable, IReadOnlyList<string> Arguments)
{
    public string ShellText => EngineJobRunner.ToShellCommand(Executable, Arguments);
}

/// <summary>Writes generated images with the standard Linux recording tools. The Windows
/// app streams through IMAPI2; on Linux the generated files are burned as-is, which yields
/// the same physical pattern because every family's image starts at LBA 0 and the drive
/// applies the same scrambling and channel coding.
/// <para>Rules that matter for a visible picture: CD audio goes through cdrecord's
/// <c>-audio</c> without <c>-swab</c> (docs/CD_AUDIO_COMPATIBILITY.md); data images are
/// written disc-at-once without padding because they fill the whole disc; BD-R must stay
/// unformatted with no spare areas (docs/ADDING_BLURAY.md) so sectors keep their nominal
/// physical addresses.</para></summary>
public static partial class BurnService
{
    private static readonly (string Id, string Name)[] KnownTools =
    [
        ("cdrecord", "cdrecord (cdrtools)"),
        ("wodim", "wodim (cdrkit)"),
        ("cdrskin", "cdrskin (libburn)"),
        ("growisofs", "growisofs (dvd+rw-tools)"),
        ("xorriso", "xorriso (libburnia)"),
    ];

    public static BurnMaterial? MaterialFor(OpticalDiscFamily family, string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return family switch
        {
            OpticalDiscFamily.CompactDisc when extension is ".wav" or ".raw" => BurnMaterial.CdAudio,
            OpticalDiscFamily.CompactDisc when extension == ".iso" => BurnMaterial.CdData,
            OpticalDiscFamily.Dvd when extension == ".iso" => BurnMaterial.Dvd,
            OpticalDiscFamily.BluRay when extension == ".iso" => BurnMaterial.BluRay,
            _ => null,
        };
    }

    public static string Describe(BurnMaterial material) => material switch
    {
        BurnMaterial.CdAudio => "CD-DA audio track",
        BurnMaterial.CdData => "CD Mode 1 data image",
        BurnMaterial.Dvd => "DVD data image",
        _ => "BD-R data image",
    };

    /// <summary>Tools able to write this material, best choice first.</summary>
    public static IReadOnlyList<BurnTool> ToolsFor(BurnMaterial material, string path)
    {
        bool wave = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);
        string[] order = material switch
        {
            // RAW tracks are big-endian, the cdrecord -audio default; cdrskin only gets WAV,
            // whose header states the byte order.
            BurnMaterial.CdAudio => wave ? ["cdrecord", "wodim", "cdrskin"] : ["cdrecord", "wodim"],
            BurnMaterial.CdData => ["cdrecord", "xorriso", "cdrskin", "wodim"],
            BurnMaterial.Dvd => ["growisofs", "xorriso", "cdrecord"],
            _ => ["growisofs", "xorriso"],
        };
        List<BurnTool> tools = [];
        foreach (string id in order)
        {
            string? executable = FindOnPath(id);
            if (executable is null)
            {
                continue;
            }

            // Debian and Ubuntu ship "cdrecord" as a link to wodim; list it once, as wodim.
            string implementation = ResolveImplementation(executable);
            if (id == "cdrecord" && implementation is "wodim" or "cdrskin")
            {
                continue;
            }

            tools.Add(new BurnTool(id, KnownTools.First(t => t.Id == id).Name, executable));
        }

        return tools;
    }

    /// <summary>Packages that provide a suitable tool, for the "nothing installed" hint.</summary>
    public static string InstallHint(BurnMaterial material) => material switch
    {
        BurnMaterial.CdAudio => "Install cdrtools (cdrecord) or cdrkit (wodim). Arch: sudo pacman -S cdrtools · Debian/Ubuntu: sudo apt install wodim · Fedora: sudo dnf install cdrskin",
        BurnMaterial.CdData => "Install cdrtools, wodim or xorriso. Arch: sudo pacman -S cdrtools · Debian/Ubuntu: sudo apt install xorriso · Fedora: sudo dnf install xorriso",
        _ => "Install dvd+rw-tools (growisofs) or xorriso. Arch: sudo pacman -S dvd+rw-tools · Debian/Ubuntu: sudo apt install growisofs · Fedora: sudo dnf install dvd+rw-tools",
    };

    public static BurnCommand Build(BurnMaterial material, BurnTool tool, string device, string file, int? speed, bool simulate, bool eject)
    {
        List<string> arguments = [];
        switch (tool.Id)
        {
            case "growisofs":
                if (material == BurnMaterial.BluRay)
                {
                    // Keep the BD-R unformatted: spare areas would shift every sector's
                    // physical position and break the picture geometry.
                    arguments.Add("-use-the-force-luke=spare:none");
                }
                else
                {
                    arguments.Add("-dvd-compat");
                }

                if (simulate)
                {
                    arguments.Add("-use-the-force-luke=dummy");
                }

                if (speed is > 0)
                {
                    arguments.Add($"-speed={speed.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                arguments.Add("-Z");
                arguments.Add($"{device}={file}");
                break;

            case "xorriso":
                arguments.AddRange(["-as", "cdrecord", "-v", $"dev={device}"]);
                if (speed is > 0)
                {
                    string suffix = material switch { BurnMaterial.Dvd => "d", BurnMaterial.BluRay => "b", _ => "c" };
                    arguments.Add($"speed={speed.Value.ToString(CultureInfo.InvariantCulture)}{suffix}");
                }

                if (material != BurnMaterial.BluRay)
                {
                    arguments.Add("-dao");
                }

                if (simulate)
                {
                    arguments.Add("-dummy");
                }

                if (eject)
                {
                    arguments.Add("-eject");
                }

                arguments.Add("-nopad");
                arguments.Add(file);
                break;

            default: // cdrecord, wodim, cdrskin share the cdrecord command line.
                arguments.AddRange(["-v", $"dev={device}"]);
                if (speed is > 0)
                {
                    arguments.Add($"speed={speed.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                if (simulate)
                {
                    arguments.Add("-dummy");
                }

                if (eject)
                {
                    arguments.Add("-eject");
                }

                if (material == BurnMaterial.CdAudio)
                {
                    arguments.Add("-audio");
                }
                else
                {
                    arguments.AddRange(["-dao", "-data", "-nopad"]);
                }

                arguments.Add(file);
                break;
        }

        return new BurnCommand(tool.Executable, arguments);
    }

    /// <summary>Optical drives from sysfs (/sys/block/sr*), with vendor and model.</summary>
    public static IReadOnlyList<OpticalDrive> FindDrives()
    {
        List<OpticalDrive> drives = [];
        try
        {
            if (!Directory.Exists("/sys/block"))
            {
                return drives;
            }

            foreach (string entry in Directory.EnumerateDirectories("/sys/block", "sr*").Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(entry);
                string device = "/dev/" + name;
                string vendor = ReadTrimmed(Path.Combine(entry, "device", "vendor"));
                string model = ReadTrimmed(Path.Combine(entry, "device", "model"));
                string label = string.Join(' ', new[] { vendor, model }.Where(s => s.Length > 0));
                bool accessible = File.Exists(device) && Access(device, ReadWrite) == 0;
                drives.Add(new OpticalDrive(device, label.Length == 0 ? device : $"{label}  ({device})", accessible));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return drives;
    }

    /// <summary>The group that owns the device node (optical on Arch, cdrom on Debian and
    /// Ubuntu), for the permission hint.</summary>
    public static string DeviceGroup(string device)
    {
        try
        {
            ProcessStartInfo info = new("stat", ["-c", "%G", device])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? process = Process.Start(info);
            string group = process?.StandardOutput.ReadToEnd().Trim() ?? string.Empty;
            process?.WaitForExit(2000);
            return group.Length > 0 ? group : "optical";
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "optical";
        }
    }

    /// <summary>Command that prints what is in the drive, using whichever tool exists.</summary>
    public static BurnCommand? MediaInfoCommand(string device)
    {
        if (FindOnPath("xorriso") is { } xorriso)
        {
            return new BurnCommand(xorriso, ["-outdev", device, "-toc"]);
        }

        if (FindOnPath("dvd+rw-mediainfo") is { } mediainfo)
        {
            return new BurnCommand(mediainfo, [device]);
        }

        foreach (string id in new[] { "cdrecord", "wodim", "cdrskin" })
        {
            if (FindOnPath(id) is { } tool)
            {
                return new BurnCommand(tool, [$"dev={device}", "-minfo"]);
            }
        }

        return null;
    }

    public static BurnCommand? EjectCommand(string device)
        => FindOnPath("eject") is { } eject ? new BurnCommand(eject, [device]) : null;

    /// <summary>Runs a tool, streaming its output lines (split on CR as well as LF, since
    /// progress lines overwrite themselves) and a parsed completion fraction. Cancelling
    /// sends SIGINT so the recorder can stop cleanly, and kills it if it has not exited
    /// half a minute later.</summary>
    public static async Task<int> RunAsync(BurnCommand command, Action<string> log, Action<double> progress, CancellationToken cancellationToken)
    {
        ProcessStartInfo info = new(command.Executable, command.Arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        info.Environment["LC_ALL"] = "C";
        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {command.Executable}.");
        process.StandardInput.Close();
        Task stdout = Pump(process.StandardOutput, log, progress);
        Task stderr = Pump(process.StandardError, log, progress);
        await using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    _ = Kill(process.Id, SignalInterrupt);
                    _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                log("The program did not stop after SIGINT; terminating it.");
                                process.Kill(entireProcessTree: true);
                            }
                        }
                        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
                        {
                        }
                    }, TaskScheduler.Default);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private static async Task Pump(StreamReader reader, Action<string> log, Action<double> progress)
    {
        StringBuilder line = new();
        int loggedDecile = -1;
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (int index = 0; index < read; index++)
            {
                char c = buffer[index];
                if (c is '\n' or '\r')
                {
                    Emit();
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        Emit();

        void Emit()
        {
            if (line.Length == 0)
            {
                return;
            }

            string text = line.ToString();
            line.Clear();
            if (TryParseProgress(text, out double fraction))
            {
                progress(fraction);
                // Progress lines repeat many times a second; log one per 10 %.
                int decile = (int)(fraction * 10);
                if (decile == loggedDecile)
                {
                    return;
                }

                loggedDecile = decile;
            }

            log(text);
        }
    }

    internal static bool TryParseProgress(string line, out double fraction)
    {
        fraction = 0;
        Match written = CdrecordProgress().Match(line);
        if (written.Success
            && double.TryParse(written.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double done)
            && double.TryParse(written.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double total)
            && total > 0)
        {
            fraction = Math.Clamp(done / total, 0, 1);
            return true;
        }

        // xorriso: "xorriso : UPDATE : Writing:  12384s  2.7%  fifo 100%  buf  50%  10.2xD"
        Match percent = line.Contains("UPDATE", StringComparison.Ordinal)
            ? FirstPercent().Match(line)
            : PercentProgress().Match(line);
        if (percent.Success
            && double.TryParse(percent.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            fraction = Math.Clamp(value / 100, 0, 1);
            return true;
        }

        return false;
    }

    public static string? FindOnPath(string name)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        foreach (string directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(["/usr/sbin", "/sbin"]))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) && Access(candidate, Execute) == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ResolveImplementation(string executable)
    {
        try
        {
            FileSystemInfo? target = File.ResolveLinkTarget(executable, returnFinalTarget: true);
            return Path.GetFileName(target?.FullName ?? executable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Path.GetFileName(executable);
        }
    }

    private static string ReadTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private const int Execute = 1;
    private const int ReadWrite = 2 | 4;
    private const int SignalInterrupt = 2;

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Access(string path, int mode);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    // cdrecord / wodim / cdrskin: "Track 01:  123 of  700 MB written".
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s+of\s+(\d+(?:\.\d+)?)\s+MB written")]
    private static partial Regex CdrecordProgress();

    // growisofs "( 12.3%)" and "12.3% done".
    [GeneratedRegex(@"(\d{1,3}(?:\.\d+)?)%\s*(?:\)|done)")]
    private static partial Regex PercentProgress();

    [GeneratedRegex(@"(\d{1,3}(?:\.\d+)?)%")]
    private static partial Regex FirstPercent();
}
