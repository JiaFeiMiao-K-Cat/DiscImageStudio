namespace DvdImageSolver;

/// <summary>
/// Compatibility entry point for command-line callers. Graphical hosts use the typed
/// generation and rendering APIs so engine exceptions are not reduced to exit codes.
/// </summary>
public static class DvdCommandRunner
{
    public static int Run(params string[] arguments) => Program.RunCommand(arguments);
}
