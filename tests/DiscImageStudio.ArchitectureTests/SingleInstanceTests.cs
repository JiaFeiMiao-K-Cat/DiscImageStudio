using System.Diagnostics;
using System.IO;
using System.Reflection;
using DiscImageStudio.Services;

internal static class SingleInstanceTests
{
    internal static int Probe(string name)
    {
        using SingleInstanceGuard? instance = SingleInstanceGuard.TryAcquire(name);
        Console.WriteLine(instance is null ? "duplicate" : "primary");
        Console.Out.Flush();
        if (instance is not null) Console.ReadLine();
        return 0;
    }

    public static void Run()
    {
        string name = "DiscImageStudio.Tests." + Guid.NewGuid().ToString("N");
        using (Process primary = Start(name))
        {
            try
            {
                Require(ReadStatus(primary) == "primary", "the first process acquires the instance");
                using Process duplicate = Start(name);
                Require(ReadStatus(duplicate) == "duplicate", "a second process exits while the first is alive");
                ExpectExit(duplicate);
                primary.StandardInput.WriteLine("exit");
                primary.StandardInput.Flush();
                ExpectExit(primary);
            }
            finally { Stop(primary); }
        }
        using (Process reopened = Start(name))
        {
            try
            {
                Require(ReadStatus(reopened) == "primary", "normal exit allows restarting");
                // Simulate a crash without Dispose/ReleaseMutex.
                reopened.Kill(entireProcessTree: true);
                Require(reopened.WaitForExit(10000), "the crashed instance terminates");
            }
            finally { Stop(reopened); }
        }
        using (Process recovered = Start(name))
        {
            try { Require(ReadStatus(recovered) == "primary", "a crash does not leave a stale lock"); }
            finally { Stop(recovered); }
        }

        List<Process> contenders = [];
        try
        {
            for (int i = 0; i < 8; i++) contenders.Add(Start(name));
            int primaries = 0;
            foreach (Process contender in contenders)
            {
                string status = ReadStatus(contender);
                if (status == "primary") primaries++;
                else
                {
                    Require(status == "duplicate", "contending launches return a known result");
                    ExpectExit(contender);
                }
            }
            Require(primaries == 1, "eight concurrent launches leave exactly one primary");
        }
        finally
        {
            foreach (Process contender in contenders) { Stop(contender); contender.Dispose(); }
        }
        Console.WriteLine("single-instance: normal exit, duplicate exit, crash recovery and 8 concurrent launches passed");
    }

    private static Process Start(string name)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test process path.");
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--single-instance-probe");
        start.ArgumentList.Add(name);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the instance probe.");
    }

    private static string ReadStatus(Process process)
    {
        Task<string?> read = process.StandardOutput.ReadLineAsync();
        Require(read.Wait(TimeSpan.FromSeconds(10)), "instance detection does not block startup");
        return read.GetAwaiter().GetResult() ?? throw new InvalidOperationException(
            "Instance probe exited before reporting: " + process.StandardError.ReadToEnd());
    }

    private static void ExpectExit(Process process)
    {
        Require(process.WaitForExit(10000), "the process exits promptly");
        Require(process.ExitCode == 0, "an ordinary duplicate/exit succeeds");
    }

    private static void Stop(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        process.WaitForExit(10000);
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Single-instance check failed: " + description);
    }
}
