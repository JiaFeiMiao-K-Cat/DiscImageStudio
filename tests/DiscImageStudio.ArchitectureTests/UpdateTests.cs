using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using DiscImageStudio.Services;

/// <summary>Covers the update manifest contract: SemVer ordering, architecture selection,
/// version gating, rejection of malformed or untrusted download metadata, PE/version
/// verification of the downloaded executable, and the PowerShell replacement helper's
/// readiness, swap, rollback and cleanup behavior. Network access remains a manual release
/// check.</summary>
internal static class UpdateTests
{
    private const string Sha256 = "0123456789AbCdEf0123456789abcdef0123456789ABCDEF0123456789abcdef";

    public static void Run()
    {
        TestVersionParsing();
        TestVersionOrdering();
        TestManifestSelection();
        TestManifestRejection();
        TestInstallationDetection();
        TestDownloadedExecutableVerification();
        TestHelperScriptParsing();
        TestHelperHandshake();
    }

    private static void TestVersionParsing()
    {
        RequireVersion("1.5.0", "1.5.0", "1.5.0");
        RequireVersion("1.5.1-rc.2", "1.5.1-rc.2", "1.5.1-rc.2");
        RequireVersion("1.5.1-rc.2+build.7", "1.5.1-rc.2", "1.5.1-rc.2");
        RequireVersion("1.5.1+sha.abcdef", "1.5.1", "1.5.1");
        RequireVersion("1.2.3-alpha", "1.2.3-alpha", "1.2.3-alpha");

        foreach (string invalid in new[]
                 {
                     "", " ", "1", "1.2", "1.2.3.4", "01.2.3", "1.02.3", "1.2.03",
                     "v1.2.3", " 1.2.3", "1.2.3 ", "1.2.3-", "1.2.3-rc.01", "1.2.3-01",
                     "1.2.3-rc..1", "1.2.3-a_b", "1.2.3+", "1.2.3-rc.1+", "-1.2.3",
                 })
        {
            Require(!AppVersion.TryParse(invalid, out _), "reject invalid version '" + invalid + "'");
        }

        Require(AppVersion.TryParse("1.2.3-99999999999999999999", out _),
            "large numeric prerelease identifiers remain comparable");
    }

    private static void TestVersionOrdering()
    {
        Require(Version("1.5.1") > Version("1.5.0"), "newer patch wins");
        Require(Version("1.10.0") > Version("1.9.9"), "numeric minor parts do not compare as text");
        Require(Version("1.5.0") > Version("1.5.0-rc.1"), "stable release outranks its prerelease");
        Require(Version("1.5.0-rc.2") > Version("1.5.0-rc.1"), "later release candidates win");
        Require(Version("1.5.0-rc.1") > Version("1.5.0-beta.9"), "release candidate outranks beta");
        Require(Version("1.5.0-beta.1") > Version("1.5.0-alpha.9"), "beta outranks alpha");
        Require(Version("1.5.0-alpha.10") > Version("1.5.0-alpha.9"), "numeric prerelease parts compare numerically");
        Require(Version("1.5.0-alpha") < Version("1.5.0-alpha.1"), "a shorter prerelease list sorts first");
        Require(Version("1.5.0-alpha.1") < Version("1.5.0-alpha.beta"), "numeric identifiers sort before text");
        Require(Version("1.5.0+abc") == Version("1.5.0+def"), "build metadata is ignored");
    }

    private static void TestManifestSelection()
    {
        AppVersion current = Version("1.5.0");
        AppUpdateCheck? check = AppUpdateService.ParseCheck(BuildManifest(), current, "win-x64");
        Require(check is not null, "a newer release produces an update check");
        Require(check!.Version == Version("1.6.0") && check.Version.ToString() == "1.6.0",
            "the manifest version is selected");
        Require(check.DownloadUri == new Uri(AssetUrl()),
            "the manifest download URL is preserved");
        Require(check.Sha256 == Sha256.ToLowerInvariant(), "the checksum is normalized to lowercase");
        Require(check.Size == 123456, "the manifest size is preserved");
        Require(check.RuntimeIdentifier == "win-x64", "the selected architecture is carried with the check");

        Require(AppUpdateService.ParseCheck(BuildManifest(version: "1.5.0", tag: "v1.5.0"), current, "win-x64") is null,
            "the running version is not offered again");
        Require(AppUpdateService.ParseCheck(BuildManifest(version: "1.4.9", tag: "v1.4.9"), current, "win-x64") is null,
            "older releases are not offered as updates");

        AppUpdateCheck? prerelease = AppUpdateService.ParseCheck(
            BuildManifest(version: "1.6.0-rc.1", tag: "v1.6.0-rc.1"),
            current,
            "win-x64");
        Require(prerelease?.Version == Version("1.6.0-rc.1"), "prerelease updates are selected when newer");
        Require(AppUpdateService.ParseCheck(
                BuildManifest(version: "1.6.0-rc.1", tag: "v1.6.0-rc.1"),
                Version("1.6.0-rc.2"),
                "win-x64") is null,
            "an older release candidate is not offered");
        Require(AppUpdateService.ParseCheck(
                BuildManifest(version: "1.6.0", tag: "v1.6.0"),
                Version("1.6.0-rc.2"),
                "win-x64") is not null,
            "the stable release replaces a release candidate");

        AppUpdateCheck? arm64 = AppUpdateService.ParseCheck(
            BuildManifest(runtimeIdentifier: "win-arm64"),
            current,
            "win-arm64");
        Require(arm64 is not null, "the selected architecture's asset is used");
    }

    private static void TestManifestRejection()
    {
        AppVersion current = Version("1.5.0");
        Reject(BuildManifest(runtimeIdentifier: "win-arm64"), current, "win-x64", "未找到更新信息。");
        Reject("not json", current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(tag: "v1.6.1"), current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(url: "http://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/releases/download/v1.6.0/file.exe"),
            current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(url: "https://example.com/DiscImageStudio.exe"),
            current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(url: "https://github.com/Other/DiscImageStudio/releases/download/v1.6.0/file.exe"),
            current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(url: AssetUrl() + "?download=1"), current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(url: AssetUrl() + "#fragment"), current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(url: "https://github.com.evil.example/JiaFeiMiao-K-Cat/DiscImageStudio/releases/download/v1.6.0/file.exe"),
            current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(url: "https://github.com@example.com/JiaFeiMiao-K-Cat/DiscImageStudio/releases/download/v1.6.0/file.exe"),
            current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(url: "https://github.com:444/JiaFeiMiao-K-Cat/DiscImageStudio/releases/download/v1.6.0/file.exe"),
            current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(sha256: "abc"), current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(sha256: new string('g', 64)), current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(size: 0), current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(version: "1.6"), current, "win-x64", "更新信息无效。");
        Reject(BuildManifest(version: "1.6.0", tag: "1.6.0"), current, "win-x64", "更新信息无效。");
        Reject("""{"version":"1.6.0","tag":"v1.6.0"}""", current, "win-x64", "更新信息无效。");
        Reject("""{"version":"1.6.0","tag":"v1.6.0","assets":[]}""", current, "win-x64", "更新信息无效。");
    }

    private static void TestInstallationDetection()
    {
        Require(!AppUpdateService.IsPortableSingleFileApp(),
            "the framework-dependent test host is not treated as a portable EXE");
        Require(AppUpdateService.GetSupportedRuntimeIdentifier() is not null,
            "the test host runs on a supported Windows architecture");
    }

    private static void TestDownloadedExecutableVerification()
    {
        // The managed DLL is AnyCPU, so the native apphost beside it is the PE image whose
        // machine field can actually match the running architecture.
        string hostName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? "DiscImageStudio.ArchitectureTests";
        string hostPath = Path.Combine(AppContext.BaseDirectory, hostName + ".exe");
        Require(File.Exists(hostPath), "the test host ships a native executable");
        string? productVersion = FileVersionInfo.GetVersionInfo(hostPath).ProductVersion;
        Require(AppVersion.TryParse(productVersion, out AppVersion current),
            "the application assembly carries a parseable product version");
        string runtimeIdentifier = AppUpdateService.GetSupportedRuntimeIdentifier()!;
        string directory = CreateTestDirectory("verify");
        try
        {
            string executable = Path.Combine(directory, "candidate.exe");
            File.Copy(hostPath, executable);
            AppUpdateService.VerifyDownloadedExecutable(executable, current, runtimeIdentifier);

            RejectExecutable(executable, Version("999.0.0"), runtimeIdentifier,
                "更新文件与应用版本不匹配。");
            string otherRuntime = runtimeIdentifier == "win-x64" ? "win-arm64" : "win-x64";
            RejectExecutable(executable, current, otherRuntime,
                "更新文件与当前系统架构不匹配。");

            string invalid = Path.Combine(directory, "invalid.exe");
            File.WriteAllText(invalid, "not a portable executable");
            RejectExecutable(invalid, current, runtimeIdentifier,
                "更新文件与应用版本不匹配。");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RejectExecutable(
        string path,
        AppVersion version,
        string runtimeIdentifier,
        string expectedMessage)
    {
        try
        {
            AppUpdateService.VerifyDownloadedExecutable(path, version, runtimeIdentifier);
        }
        catch (AppUpdateException exception)
        {
            Require(exception.Message == expectedMessage,
                "executable rejection reports " + expectedMessage + ", received " + exception.Message);
            return;
        }

        throw new InvalidOperationException("Executable should have been rejected: " + expectedMessage);
    }

    private static void TestHelperScriptParsing()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "disc-update-helper-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(path, AppUpdateService.HelperScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "try { [void][scriptblock]::Create([IO.File]::ReadAllText($env:HELPER_PATH)); exit 0 } "
                + "catch { Write-Error $_; exit 1 }");
            startInfo.Environment["HELPER_PATH"] = path;

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Cannot start Windows PowerShell.");
            try
            {
                process.WaitForExit(30000);
                Require(process.HasExited && process.ExitCode == 0,
                    "the replacement helper is valid Windows PowerShell");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void TestHelperHandshake()
    {
        TestHelperRejectsMissingReadiness();
        TestHelperWaitsForApplicationExit();
        TestHelperSwapsAndCleansUp();
        TestHelperRollsBackFailedStartup();
    }

    private static void TestHelperRejectsMissingReadiness()
    {
        string directory = CreateTestDirectory("blocked");
        string target = Path.Combine(directory, "application.exe");
        string source = Path.Combine(directory, "update.exe");
        string work = Path.Combine(directory, "work");
        Directory.CreateDirectory(work);
        string errorLog = Path.Combine(work, "update-error.txt");
        string health = Path.Combine(work, "update-health.txt");
        string ready = Path.Combine(work, "missing", "update-ready.txt");
        File.WriteAllText(target, "old");
        File.WriteAllText(source, "new");

        Process? standIn = null;
        Process? helper = null;
        try
        {
            standIn = StartStandInApp(keepRunning: true);
            helper = StartHelper(directory, standIn.Id, target, source, target + ".old",
                health, errorLog, ready, work);
            Require(helper.WaitForExit(15000) && helper.HasExited,
                "a helper without a readiness marker exits");
            Require(!File.Exists(ready), "a helper that cannot signal readiness leaves no marker");
            Require(File.ReadAllText(target) == "old",
                "a helper that never signalled readiness leaves the executable untouched");
            Require(!File.Exists(source),
                "a helper that failed before the swap removes only the staged source");
            Require(File.Exists(errorLog) && new FileInfo(errorLog).Length > 0,
                "a failed helper records the reason in its work directory");
        }
        finally
        {
            StopTestProcesses(helper, standIn, directory);
        }
    }

    private static void TestHelperWaitsForApplicationExit()
    {
        string directory = CreateTestDirectory("wait");
        string target = Path.Combine(directory, "application.exe");
        string source = Path.Combine(directory, "update.exe");
        string work = Path.Combine(directory, "work");
        Directory.CreateDirectory(work);
        string errorLog = Path.Combine(work, "update-error.txt");
        string health = Path.Combine(work, "update-health.txt");
        string ready = Path.Combine(work, "update-ready.txt");
        File.WriteAllText(target, "old");
        File.WriteAllText(source, "new");

        Process? standIn = null;
        Process? helper = null;
        try
        {
            standIn = StartStandInApp(keepRunning: true);
            helper = StartHelper(directory, standIn.Id, target, source, target + ".old",
                health, errorLog, ready, work);
            Stopwatch readyWait = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                Require(!helper.HasExited && readyWait.Elapsed < TimeSpan.FromSeconds(15),
                    "the helper signals readiness while the app is still running");
                Thread.Sleep(50);
            }

            Require(File.ReadAllText(target) == "old" && File.ReadAllText(source) == "new",
                "the helper does not replace the executable before the app exits");
            helper.Kill(entireProcessTree: true);
            Require(helper.WaitForExit(10000), "a killed helper stops waiting for the app");
            Require(File.ReadAllText(target) == "old" && File.ReadAllText(source) == "new",
                "a helper killed before the app exits leaves the original executable in place");
        }
        finally
        {
            StopTestProcesses(helper, standIn, directory);
        }
    }

    private static void TestHelperSwapsAndCleansUp()
    {
        string directory = CreateTestDirectory("swap");
        string target = Path.Combine(directory, "application.cmd");
        string source = Path.Combine(directory, "update.cmd");
        string backup = target + ".old";
        string work = Path.Combine(directory, "work");
        Directory.CreateDirectory(work);
        string errorLog = Path.Combine(work, "update-error.txt");
        string health = Path.Combine(work, "update-health.txt");
        string ready = Path.Combine(work, "update-ready.txt");
        string oldContent = "@echo off\r\nexit /b 0\r\n";
        string newContent = "@echo off\r\n> \"%DIS_UPDATE_HEALTH_FILE%\" echo started\r\n";
        File.WriteAllText(target, oldContent);
        File.WriteAllText(source, newContent);

        Process? standIn = null;
        Process? helper = null;
        try
        {
            standIn = StartStandInApp(keepRunning: false);
            helper = StartHelper(directory, standIn.Id, target, source, backup,
                health, errorLog, ready, work);
            Require(helper.WaitForExit(30000) && helper.HasExited && helper.ExitCode == 0,
                "the helper completes after the replacement reports health");
            Require(File.ReadAllText(target) == newContent,
                "the new executable remains in place after a healthy start");
            Require(!File.Exists(source) && !File.Exists(backup),
                "a successful swap leaves no staged source or backup file");
            Require(!Directory.Exists(work),
                "a successful swap removes its work directory and helper script");
        }
        finally
        {
            StopTestProcesses(helper, standIn, directory);
        }
    }

    private static void TestHelperRollsBackFailedStartup()
    {
        string directory = CreateTestDirectory("rollback");
        string target = Path.Combine(directory, "application.cmd");
        string source = Path.Combine(directory, "update.cmd");
        string backup = target + ".old";
        string work = Path.Combine(directory, "work");
        Directory.CreateDirectory(work);
        string errorLog = Path.Combine(work, "update-error.txt");
        string health = Path.Combine(work, "update-health.txt");
        string ready = Path.Combine(work, "update-ready.txt");
        string oldContent = "@echo off\r\nexit /b 0\r\n";
        string newContent = "@echo off\r\nexit /b 1\r\n";
        File.WriteAllText(target, oldContent);
        File.WriteAllText(source, newContent);

        Process? standIn = null;
        Process? helper = null;
        try
        {
            standIn = StartStandInApp(keepRunning: false);
            helper = StartHelper(directory, standIn.Id, target, source, backup,
                health, errorLog, ready, work);
            Require(helper.WaitForExit(30000) && helper.HasExited,
                "the helper finishes after a failed startup");
            Require(File.ReadAllText(target) == oldContent,
                "a failed startup restores the previous executable");
            Require(!File.Exists(source) && !File.Exists(backup),
                "rollback leaves no staged source or backup file");
            Require(File.Exists(errorLog), "a failed startup records the reason");
            Require(File.ReadAllText(errorLog).Contains("startup", StringComparison.OrdinalIgnoreCase),
                "the error log explains the startup failure");
            Require(Directory.Exists(work),
                "a failed startup keeps its work directory for diagnostics");
            Require(!Directory.EnumerateFiles(work, "*.ps1").Any(),
                "the helper removes its script after running");
        }
        finally
        {
            StopTestProcesses(helper, standIn, directory);
        }
    }

    private static Process StartHelper(
        string directory,
        int processId,
        string target,
        string source,
        string backup,
        string healthFile,
        string errorLog,
        string readyFile,
        string workDirectory)
    {
        string scriptPath = Path.Combine(
            directory,
            "apply-update-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(
            scriptPath,
            AppUpdateService.HelperScript,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-Target");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add("-Source");
        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add("-Backup");
        startInfo.ArgumentList.Add(backup);
        startInfo.ArgumentList.Add("-HealthFile");
        startInfo.ArgumentList.Add(healthFile);
        startInfo.ArgumentList.Add("-ErrorLog");
        startInfo.ArgumentList.Add(errorLog);
        startInfo.ArgumentList.Add("-ReadyFile");
        startInfo.ArgumentList.Add(readyFile);
        startInfo.ArgumentList.Add("-WorkDirectory");
        startInfo.ArgumentList.Add(workDirectory);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cannot start the replacement helper.");
    }

    private static Process StartStandInApp(bool keepRunning)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(keepRunning ? "ping -n 30 127.0.0.1 > nul" : "exit 0");
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cannot start the stand-in app process.");
        if (!keepRunning)
        {
            Require(process.WaitForExit(10000), "the stand-in app exits before the helper starts");
        }

        return process;
    }

    private static string CreateTestDirectory(string name)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "disc-update-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void StopTestProcesses(Process? helper, Process? standIn, string directory)
    {
        foreach (Process? process in new[] { helper, standIn })
        {
            if (process is { HasExited: false })
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception
                                                      or NotSupportedException)
                {
                }
            }

            process?.Dispose();
        }

        // A relaunched stub may still be releasing its working directory; retry briefly.
        for (int attempt = 0; Directory.Exists(directory) && attempt < 50; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)
                                                  && attempt < 49)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void Reject(string json, AppVersion current, string runtimeIdentifier, string expectedMessage)
    {
        try
        {
            _ = AppUpdateService.ParseCheck(json, current, runtimeIdentifier);
        }
        catch (AppUpdateException exception)
        {
            Require(exception.Message == expectedMessage,
                "manifest rejection reports " + expectedMessage + ", received " + exception.Message);
            return;
        }

        throw new InvalidOperationException("Manifest should have been rejected: " + expectedMessage);
    }

    private static string BuildManifest(
        string version = "1.6.0",
        string tag = "v1.6.0",
        string runtimeIdentifier = "win-x64",
        string? url = null,
        string sha256 = Sha256,
        long size = 123456) =>
        $$"""
          {
            "version": "{{version}}",
            "tag": "{{tag}}",
            "notesUrl": "https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/releases/tag/{{tag}}",
            "assets": {
              "{{runtimeIdentifier}}": {
                "url": "{{url ?? AssetUrl(version)}}",
                "sha256": "{{sha256}}",
                "size": {{size}}
              }
            }
          }
          """;

    private static string AssetUrl(string version = "1.6.0") =>
        "https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/releases/download/v" + version
        + "/DiscImageStudio-v" + version + "-win-x64.exe";

    private static AppVersion Version(string text)
    {
        if (!AppVersion.TryParse(text, out AppVersion version))
        {
            throw new InvalidOperationException("Test version does not parse: " + text);
        }

        return version;
    }

    private static void RequireVersion(string text, string expectedText, string expectedToString)
    {
        if (!AppVersion.TryParse(text, out AppVersion version))
        {
            throw new InvalidOperationException("Version does not parse: " + text);
        }

        Require(version == Version(expectedText), "parse " + text);
        Require(version.ToString() == expectedToString, "round-trip " + text);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Update check failed: " + name);
        }
    }
}
