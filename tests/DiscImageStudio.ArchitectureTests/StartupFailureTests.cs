using System.IO;
using DiscImageStudio.Services;

internal static class StartupFailureTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "DiscImageStudio-StartupTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Exception error = new InvalidOperationException("Startup fixture",
                new FormatException("Invalid font fixture"));
            string path = StartupFailure.WriteLog(error, root);
            string text = File.ReadAllText(path);
            if (!text.Contains("Startup fixture") || !text.Contains("Invalid font fixture")
                || !text.Contains("Version:") || !text.Contains("Runtime:"))
                throw new InvalidOperationException("Startup log did not preserve the cause and runtime details.");
            string second = StartupFailure.WriteLog(error, root);
            if (second == path || Directory.GetFiles(root).Length != 2)
                throw new InvalidOperationException("A later startup failure overwrote the earlier diagnostic.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine("startup-diagnostics: error chain, runtime details and separate log files passed");
    }
}
