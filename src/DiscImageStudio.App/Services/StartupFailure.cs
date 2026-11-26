using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DiscImageStudio.Services;

/// <summary>Reports desktop startup failures without depending on application XAML resources.</summary>
internal static class StartupFailure
{
    internal static int Report(Exception error)
    {
        string? log = null;
        foreach (string directory in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiscImageStudio", "Logs"),
            Path.Combine(Path.GetTempPath(), "DiscImageStudio", "Logs"),
        })
        {
            try { log = WriteLog(error, directory); break; }
            catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                // A read-only profile must not suppress the native failure message.
            }
        }

        string message = "Disc Image Studio 启动失败。\n\n" + error.GetBaseException().Message
            + (log is null ? "\n\n无法写入错误日志。" : "\n\n错误日志：\n" + log);
        if (OperatingSystem.IsWindows())
            _ = MessageBoxW(IntPtr.Zero, message, "Disc Image Studio · 启动失败", 0x10);
        else
            Console.Error.WriteLine(message);
        return 1;
    }

    internal static string WriteLog(Exception error, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "startup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")
            + "-" + Guid.NewGuid().ToString("N") + ".log");
        Assembly assembly = typeof(StartupFailure).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        string details = "Disc Image Studio startup failure\nVersion: " + version
            + "\nUTC: " + DateTime.UtcNow.ToString("O")
            + "\nOS: " + RuntimeInformation.OSDescription
            + "\nArchitecture: " + RuntimeInformation.ProcessArchitecture
            + "\nRuntime: " + RuntimeInformation.FrameworkDescription
            + "\n\n" + error + Environment.NewLine;
        File.WriteAllText(path, details, new UTF8Encoding(false));
        return path;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
