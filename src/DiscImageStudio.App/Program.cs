using System.IO;
using System.Runtime.InteropServices;

namespace DiscImageStudio;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            try
            {
                using Services.SingleInstanceGuard? instance = Services.SingleInstanceGuard.TryAcquire();
                if (instance is null) return 0;
                return AppHost.Run();
            }
            catch (Exception error)
            {
                return Services.StartupFailure.Report(error);
            }
        }

        AttachToParentConsole();
        return UnifiedCommandRunner.Run(arguments);
    }

    private static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows() || !AttachConsole(AttachParentProcess))
        {
            return;
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
