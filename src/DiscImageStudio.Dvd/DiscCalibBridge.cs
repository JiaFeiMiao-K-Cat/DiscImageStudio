using System.ComponentModel;
using System.Diagnostics;

namespace DiscImageStudio.Dvd;

/// <summary>
/// Forwards the dvd-calib-* commands to the Python disc-calib tool in tools/disc-calib,
/// which depends on OpenCV and SciPy and so cannot ship inside the single-file CLI.
/// </summary>
internal static class DiscCalibBridge
{
    internal const string PatternCommand = "dvd-calib-pattern";
    internal const string SolveCommand = "dvd-calib-solve";

    /// <summary>The shell's "command not found" code; disc-calib itself uses 2 for poor fits.</summary>
    private const int ToolMissingExitCode = 127;

    private const string ToolOverrideVariable = "DISC_CALIB";
    private const string ToolName = "disc-calib";

    internal static bool Handles(string command)
        => command.Equals(PatternCommand, StringComparison.OrdinalIgnoreCase)
            || command.Equals(SolveCommand, StringComparison.OrdinalIgnoreCase);

    internal static int Run(string command, IReadOnlyList<string> arguments)
    {
        string subcommand = command.Equals(PatternCommand, StringComparison.OrdinalIgnoreCase)
            ? "pattern"
            : "code";
        foreach ((string fileName, string[] prefix) in FindCandidates())
        {
            ProcessStartInfo startInfo = new(fileName) { UseShellExecute = false };
            foreach (string argument in prefix)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add(subcommand);
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            try
            {
                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Could not start {fileName}.");
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Win32Exception)
            {
                // Not installed or not runnable; try the next candidate.
            }
        }

        Console.Error.WriteLine(
            $"error: {command} needs the Python {ToolName} tool, which was not found. Install it with\n"
            + "  uv tool install \"git+https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio#subdirectory=tools/disc-calib\"\n"
            + $"or set {ToolOverrideVariable} to its executable.");
        return ToolMissingExitCode;
    }

    private static IEnumerable<(string FileName, string[] Prefix)> FindCandidates()
    {
        string? toolOverride = Environment.GetEnvironmentVariable(ToolOverrideVariable);
        if (!string.IsNullOrWhiteSpace(toolOverride))
        {
            yield return (toolOverride, []);
            yield break;
        }

        yield return (ToolName, []);
        string? project = FindCheckoutProject();
        if (project is not null)
        {
            yield return ("uv", ["run", "--project", project, ToolName]);
        }
    }

    /// <summary>Lets `dotnet run` from a source checkout work without installing the tool.</summary>
    private static string? FindCheckoutProject()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            string project = Path.Combine(directory.FullName, "tools", ToolName);
            if (File.Exists(Path.Combine(project, "pyproject.toml")))
            {
                return project;
            }
        }

        return null;
    }
}
