using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiscImageStudio.Services;

/// <summary>A user-facing update failure whose message is a localization key.</summary>
internal sealed class AppUpdateException : Exception
{
    public AppUpdateException(string message) : base(message)
    {
    }

    public AppUpdateException(string message, string? details) : base(message)
    {
        Details = details;
    }

    /// <summary>Diagnostic text from the replacement helper, shown under the localized
    /// message. It is never used as a localization key.</summary>
    public string? Details { get; }
}

/// <summary>Newer release selected from the manifest, with the verified download coordinates.</summary>
internal sealed record AppUpdateCheck(
    AppVersion Version,
    Uri DownloadUri,
    string Sha256,
    long Size,
    string RuntimeIdentifier);

/// <summary>Checks GitHub Releases for a newer portable build, downloads it beside the cached
/// update files, verifies the manifest length, SHA-256, product version and PE architecture,
/// then hands the running EXE to a short-lived PowerShell helper that waits for exit, swaps
/// the file atomically and restarts it. The executable is unsigned, so the manifest is only as
/// trustworthy as the release account. Only the official GitHub repository is supported; forks
/// and GitHub Enterprise builds must be updated manually.</summary>
internal sealed class AppUpdateService
{
    internal const string OfficialRepository = "JiaFeiMiao-K-Cat/DiscImageStudio";
    internal const string RepositoryUrl = "https://github.com/" + OfficialRepository;
    internal const string ManifestUrl = RepositoryUrl + "/releases/latest/download/latest.json";
    internal const string HealthVariable = "DIS_UPDATE_HEALTH_FILE";
    internal const string FailureVariable = "DIS_UPDATE_FAILURE_FILE";

    private const string UpdateFileName = "update.exe";
    private const string ReadyFileName = "update-ready.txt";
    private const string ErrorFileName = "update-error.txt";
    private const string HealthFileName = "update-health.txt";
    private const string ScriptFileName = "apply-update.ps1";
    private const long MaximumManifestBytes = 128 * 1024;
    private const long MaximumErrorBytes = 16 * 1024;
    private const long FreeSpaceMargin = 16 * 1024 * 1024;
    private static readonly string UpdateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiscImageStudio",
        "updates");
    private static readonly string UpdateRootPrefix = Path.GetFullPath(UpdateRoot)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    private static readonly HttpClient Client = CreateClient();
    private static readonly AppVersion CurrentVersion = ReadCurrentVersion();
    // A named semaphore coordinates separate copies of the app without being thread-affine,
    // so it can stay held across the asynchronous download and helper handshake.
    private static readonly Semaphore UpdateGate = new(1, 1, @"Local\DiscImageStudio.Update");

    internal async Task<AppUpdateCheck?> CheckAsync(CancellationToken cancellationToken)
    {
        EnsureSupportedInstallation();
        string runtimeIdentifier = GetSupportedRuntimeIdentifier()!;
        string json = await FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        return ParseCheck(json, CurrentVersion, runtimeIdentifier);
    }

    private static async Task<string> FetchManifestAsync(CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, ManifestUrl);
                request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                using HttpResponseMessage response = await Client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new AppUpdateException("未找到更新信息。");
                }

                if (response.Content.Headers.ContentLength is { } contentLength
                    && contentLength > MaximumManifestBytes)
                {
                    throw new AppUpdateException("更新信息无效。");
                }

                await using Stream stream = await response.Content
                    .ReadAsStreamAsync(timeout.Token)
                    .ConfigureAwait(false);
                return await ReadLimitedAsync(stream, MaximumManifestBytes, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = new AppUpdateException("检查更新超时。");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                lastFailure = exception;
            }

            if (attempt == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastFailure is AppUpdateException updateFailure)
        {
            throw updateFailure;
        }

        throw lastFailure ?? new AppUpdateException("检查更新超时。");
    }

    internal static AppUpdateCheck? ParseCheck(string json, AppVersion currentVersion, string runtimeIdentifier)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw new AppUpdateException("更新信息无效。");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "version", out string? versionText)
                || !AppVersion.TryParse(versionText, out AppVersion version)
                || !TryGetString(root, "tag", out string? tag)
                || !string.Equals(tag, "v" + version, StringComparison.Ordinal)
                || !root.TryGetProperty("assets", out JsonElement assets)
                || assets.ValueKind != JsonValueKind.Object)
            {
                throw new AppUpdateException("更新信息无效。");
            }

            if (!assets.TryGetProperty(runtimeIdentifier, out JsonElement asset)
                || asset.ValueKind != JsonValueKind.Object)
            {
                throw new AppUpdateException("未找到更新信息。");
            }

            if (!TryGetString(asset, "url", out string? urlText)
                || !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? downloadUri)
                || !IsTrustedDownloadUri(downloadUri)
                || !TryGetString(asset, "sha256", out string? sha256)
                || !IsSha256(sha256)
                || !asset.TryGetProperty("size", out JsonElement sizeElement)
                || !sizeElement.TryGetInt64(out long size)
                || size <= 0)
            {
                throw new AppUpdateException("更新信息无效。");
            }

            return version > currentVersion
                ? new AppUpdateCheck(version, downloadUri, sha256!.ToLowerInvariant(), size, runtimeIdentifier)
                : null;
        }
    }

    internal async Task<string> DownloadAsync(
        AppUpdateCheck check,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        bool gateHeld = false;
        string directory = GetUpdateDirectory(check.Version);
        string targetPath = Path.Combine(directory, UpdateFileName);
        string temporaryPath = targetPath + ".part";
        try
        {
            AcquireUpdateGate();
            gateHeld = true;
            EnsureInstallable(directory, check.Size);
            Directory.CreateDirectory(directory);
            TryDelete(temporaryPath);
            TryDelete(targetPath);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    progress?.Report(0);
                    await DownloadOnceAsync(check, temporaryPath, progress, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TryDelete(temporaryPath);
                    if (attempt == 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    throw new AppUpdateException("下载更新超时。");
                }
                catch (Exception exception) when (attempt == 0
                                                      && (exception is HttpRequestException or IOException))
                {
                    TryDelete(temporaryPath);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            VerifyDownloadedExecutable(temporaryPath, check.Version, check.RuntimeIdentifier);
            File.Move(temporaryPath, targetPath, overwrite: true);
            CleanupOtherVersions(directory);
            return targetPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            if (!File.Exists(targetPath))
            {
                TryDeleteEmptyDirectory(directory);
                TryDeleteEmptyDirectory(UpdateRoot);
            }

            throw;
        }
        finally
        {
            if (gateHeld)
            {
                UpdateGate.Release();
            }
        }
    }

    private static async Task DownloadOnceAsync(
        AppUpdateCheck check,
        string temporaryPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using HttpResponseMessage response = await Client
            .GetAsync(check.DownloadUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream input = await response.Content
            .ReadAsStreamAsync(timeout.Token)
            .ConfigureAwait(false);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        int lastPercent = -1;
        try
        {
            await using FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > check.Size)
                {
                    throw new AppUpdateException("更新文件校验失败。");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                if (progress is not null)
                {
                    int percent = (int)Math.Min(100, total * 100 / check.Size);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            }
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }

        if (total != check.Size
            || !string.Equals(
                Convert.ToHexString(hash.GetHashAndReset()),
                check.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new AppUpdateException("更新文件校验失败。");
        }
    }

    /// <summary>Confirms the downloaded file really is the release the manifest described:
    /// its product version matches and its PE header targets this process architecture.</summary>
    internal static void VerifyDownloadedExecutable(string path, AppVersion expectedVersion, string runtimeIdentifier)
    {
        string? productVersion;
        try
        {
            productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch (FileNotFoundException)
        {
            throw new AppUpdateException("更新文件校验失败。");
        }

        if (!AppVersion.TryParse(productVersion, out AppVersion version) || version != expectedVersion)
        {
            throw new AppUpdateException("更新文件与应用版本不匹配。");
        }

        ushort expectedMachine = runtimeIdentifier switch
        {
            "win-x64" => 0x8664,
            "win-arm64" => 0xAA64,
            _ => 0,
        };
        if (ReadPortableExecutableMachine(path) != expectedMachine)
        {
            throw new AppUpdateException("更新文件与当前系统架构不匹配。");
        }
    }

    private static ushort ReadPortableExecutableMachine(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            {
                return 0;
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 0x40 || peOffset > stream.Length - 6)
            {
                return 0;
            }

            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550 ? reader.ReadUInt16() : (ushort)0;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            return 0;
        }
    }

    internal async Task ApplyAndRestartAsync(string downloadedPath)
    {
        bool gateHeld = false;
        bool restarting = false;
        string? stagingPath = null;
        string? workDirectory = null;
        try
        {
            AcquireUpdateGate();
            gateHeld = true;
            EnsureSupportedInstallation();
            EnsureWritableInstallLocation();
            EnsureNoOtherInstance();

            string targetPath = Environment.ProcessPath
                ?? throw new AppUpdateException("无法启动更新程序。");
            if (!File.Exists(downloadedPath)
                || !TryGetUpdateArtifactPath(downloadedPath, out string resolvedDownload))
            {
                throw new AppUpdateException("无法启动更新程序。");
            }

            workDirectory = Path.GetDirectoryName(resolvedDownload)!;
            if (!AppVersion.TryParse(Path.GetFileName(workDirectory), out AppVersion updateVersion))
            {
                workDirectory = null;
                throw new AppUpdateException("更新文件校验失败。");
            }

            long downloadSize = new FileInfo(resolvedDownload).Length;
            EnsureFreeSpace(Path.GetDirectoryName(targetPath)!, downloadSize + FreeSpaceMargin);

            stagingPath = targetPath + ".update";
            TryDelete(stagingPath);
            try
            {
                File.Copy(resolvedDownload, stagingPath, overwrite: false);
                VerifyDownloadedExecutable(
                    stagingPath,
                    updateVersion,
                    GetSupportedRuntimeIdentifier()!);
            }
            catch
            {
                TryDelete(stagingPath);
                throw;
            }

            // The helper works only with the same-volume staging copy; the downloaded file
            // is no longer needed once the copy has been verified.
            TryDelete(resolvedDownload);

            string scriptPath = Path.Combine(workDirectory, ScriptFileName);
            string errorLogPath = Path.Combine(workDirectory, ErrorFileName);
            string readyPath = Path.Combine(workDirectory, ReadyFileName);
            string healthPath = Path.Combine(workDirectory, HealthFileName);
            TryDelete(errorLogPath);
            TryDelete(readyPath);
            TryDelete(healthPath);
            File.WriteAllText(scriptPath, HelperScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powershell))
            {
                powershell = "powershell.exe";
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = powershell,
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
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-Target");
            startInfo.ArgumentList.Add(targetPath);
            startInfo.ArgumentList.Add("-Source");
            startInfo.ArgumentList.Add(stagingPath);
            startInfo.ArgumentList.Add("-Backup");
            startInfo.ArgumentList.Add(targetPath + ".old");
            startInfo.ArgumentList.Add("-HealthFile");
            startInfo.ArgumentList.Add(healthPath);
            startInfo.ArgumentList.Add("-ErrorLog");
            startInfo.ArgumentList.Add(errorLogPath);
            startInfo.ArgumentList.Add("-ReadyFile");
            startInfo.ArgumentList.Add(readyPath);
            startInfo.ArgumentList.Add("-WorkDirectory");
            startInfo.ArgumentList.Add(workDirectory);

            Process helper;
            try
            {
                helper = Process.Start(startInfo)
                    ?? throw new AppUpdateException("无法启动更新程序。");
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                                  or InvalidOperationException)
            {
                throw new AppUpdateException("无法启动更新程序。", exception.Message);
            }

            // The helper writes a ready file only after validating the swap inputs. If script
            // execution is blocked it exits first; the app then stays open with the details.
            using (helper)
            {
                Stopwatch wait = Stopwatch.StartNew();
                while (!File.Exists(readyPath))
                {
                    if (helper.HasExited || wait.Elapsed >= TimeSpan.FromSeconds(15))
                    {
                        try
                        {
                            if (!helper.HasExited)
                            {
                                helper.Kill(entireProcessTree: true);
                            }
                        }
                        catch (Exception exception) when (exception is InvalidOperationException
                                                              or System.ComponentModel.Win32Exception
                                                              or NotSupportedException)
                        {
                        }

                        string? details = ReadErrorLog(errorLogPath);
                        TryDelete(readyPath);
                        throw new AppUpdateException("无法启动更新程序。", details);
                    }

                    await Task.Delay(100).ConfigureAwait(false);
                }
            }

            restarting = true;
        }
        catch
        {
            if (stagingPath is not null)
            {
                TryDelete(stagingPath);
            }

            if (workDirectory is not null)
            {
                TryDeleteDirectory(workDirectory);
            }

            TryDeleteEmptyDirectory(UpdateRoot);
            throw;
        }
        finally
        {
            if (gateHeld && !restarting)
            {
                UpdateGate.Release();
            }
        }
    }

    /// <summary>Resolves the health handshake for the instance the helper just started and
    /// removes stale update work directories and app-directory swap leftovers. Returns the
    /// previous update's error text so the first window can show why it was rolled back.</summary>
    internal static string? CompleteStartupUpdate()
    {
        string? healthText = Environment.GetEnvironmentVariable(HealthVariable);
        Environment.SetEnvironmentVariable(HealthVariable, null);
        string? protectedDirectory = null;
        if (TryGetUpdateArtifactPath(healthText, out string healthPath)
            && string.Equals(Path.GetFileName(healthPath), HealthFileName, StringComparison.Ordinal))
        {
            string? directory = Path.GetDirectoryName(healthPath);
            if (directory is not null && Directory.Exists(directory))
            {
                try
                {
                    File.WriteAllText(healthPath, "started", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    protectedDirectory = directory;
                }
                catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException
                                                      or NotSupportedException)
                {
                }
            }
        }

        string? failureText = Environment.GetEnvironmentVariable(FailureVariable);
        Environment.SetEnvironmentVariable(FailureVariable, null);
        string? details = null;
        if (TryGetUpdateArtifactPath(failureText, out string failurePath)
            && string.Equals(Path.GetFileName(failurePath), ErrorFileName, StringComparison.Ordinal)
            && File.Exists(failurePath))
        {
            details = ReadErrorLog(failurePath);
        }

        // The helper is still waiting for this instance's health marker; deleting the backup
        // or work directory now would remove its rollback path.
        bool handshakePending = !string.IsNullOrWhiteSpace(healthText) && protectedDirectory is null;
        return CleanupStartupArtifacts(protectedDirectory, details, handshakePending);
    }

    private static string? CleanupStartupArtifacts(
        string? protectedDirectory,
        string? details,
        bool handshakePending)
    {
        if (handshakePending || HasOtherInstance())
        {
            return details;
        }

        // Another instance may be downloading into the shared cache; leave it alone.
        if (!UpdateGate.WaitOne(0))
        {
            return details;
        }

        try
        {
            return CleanupStartupArtifactsCore(protectedDirectory, details);
        }
        finally
        {
            UpdateGate.Release();
        }
    }

    private static string? CleanupStartupArtifactsCore(string? protectedDirectory, string? details)
    {
        string? failure = details;
        if (Directory.Exists(UpdateRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(UpdateRoot).ToArray())
            {
                if (protectedDirectory is not null
                    && string.Equals(directory, protectedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsReparsePoint(directory))
                {
                    continue;
                }

                if (failure is null)
                {
                    string logPath = Path.Combine(directory, ErrorFileName);
                    if (File.Exists(logPath))
                    {
                        failure = ReadErrorLog(logPath);
                    }
                }

                TryDeleteDirectory(directory);
            }

            foreach (string file in Directory.EnumerateFiles(UpdateRoot).ToArray())
            {
                TryDelete(file);
            }

            TryDeleteEmptyDirectory(UpdateRoot);
        }

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            TryDelete(processPath + ".update");
            TryDelete(processPath + ".old");
        }

        return failure;
    }

    /// <summary>Deletes a downloaded update that will not be installed, including its work
    /// directory. Only paths under the updater's private cache are touched.</summary>
    internal static void DiscardDownloadedUpdate(string? downloadedPath)
    {
        if (!TryGetUpdateArtifactPath(downloadedPath, out string resolved))
        {
            return;
        }

        TryDelete(resolved);
        string? directory = Path.GetDirectoryName(resolved);
        if (directory is not null)
        {
            TryDelete(Path.Combine(directory, ScriptFileName));
            TryDelete(Path.Combine(directory, ReadyFileName));
            TryDelete(Path.Combine(directory, ErrorFileName));
            TryDelete(Path.Combine(directory, HealthFileName));
            TryDeleteEmptyDirectory(directory);
        }

        TryDeleteEmptyDirectory(UpdateRoot);
    }

    internal static string? GetSupportedRuntimeIdentifier() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => null,
    };

    internal static bool IsPortableSingleFileApp()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)
            || !processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Framework-dependent builds keep the entry DLL on disk; single-file builds do not.
        // Assembly.Location would say the same but the single-file analyzer rejects it (IL3000).
        string assemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? "DiscImageStudio";
        return !File.Exists(Path.Combine(
            Path.GetDirectoryName(processPath) ?? string.Empty,
            assemblyName + ".dll"));
    }

    private static void EnsureSupportedInstallation()
    {
        if (!OperatingSystem.IsWindows() || GetSupportedRuntimeIdentifier() is null)
        {
            throw new AppUpdateException("当前系统架构不支持应用内更新。");
        }

        if (!IsPortableSingleFileApp())
        {
            throw new AppUpdateException("应用内更新仅支持便携版 EXE。");
        }
    }

    private static string GetUpdateDirectory(AppVersion version) =>
        Path.Combine(UpdateRoot, version.ToString());

    private static void EnsureInstallable(string updateDirectory, long downloadSize)
    {
        EnsureSupportedInstallation();
        EnsureWritableInstallLocation();
        EnsureFreeSpace(updateDirectory, downloadSize + FreeSpaceMargin);
        EnsureFreeSpace(
            Path.GetDirectoryName(Environment.ProcessPath!)!,
            downloadSize + FreeSpaceMargin);
    }

    private static void EnsureWritableInstallLocation()
    {
        string? applicationDirectory = Path.GetDirectoryName(Environment.ProcessPath!);
        if (string.IsNullOrEmpty(applicationDirectory))
        {
            throw new AppUpdateException("无法写入应用所在目录，请手动下载新版本。");
        }

        EnsureDirectoryWritable(applicationDirectory);
    }

    private static void EnsureDirectoryWritable(string directory)
    {
        string probePath = Path.Combine(directory, ".disc-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using FileStream probe = new(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException
                                              or System.Security.SecurityException)
        {
            throw new AppUpdateException("无法写入应用所在目录，请手动下载新版本。");
        }
    }

    private static void EnsureFreeSpace(string directory, long requiredBytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            DriveInfo drive = new(root);
            if (drive.AvailableFreeSpace < requiredBytes)
            {
                throw new AppUpdateException("磁盘空间不足，无法下载更新。");
            }
        }
        catch (AppUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            throw new AppUpdateException("更新目录不可用。");
        }
    }

    private static void EnsureNoOtherInstance()
    {
        if (HasOtherInstance())
        {
            throw new AppUpdateException("请先关闭其他应用窗口再安装更新。");
        }
    }

    private static bool HasOtherInstance()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        string processName = Path.GetFileNameWithoutExtension(processPath);
        if (processName.Length == 0)
        {
            return false;
        }

        try
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    try
                    {
                        if (string.Equals(
                                process.MainModule?.FileName,
                                processPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                                          or InvalidOperationException
                                                          or NotSupportedException)
                    {
                    }
                }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
        }

        return false;
    }

    private static void AcquireUpdateGate()
    {
        if (!UpdateGate.WaitOne(0))
        {
            throw new AppUpdateException("另一个实例正在下载或安装更新。");
        }
    }

    private static void CleanupOtherVersions(string currentDirectory)
    {
        if (!Directory.Exists(UpdateRoot))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(UpdateRoot).ToArray())
        {
            if (string.Equals(directory, currentDirectory, StringComparison.OrdinalIgnoreCase)
                || !AppVersion.TryParse(Path.GetFileName(directory), out _)
                || IsReparsePoint(directory))
            {
                continue;
            }

            TryDeleteDirectory(directory);
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { Length: > 0 } text)
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Only the official release asset layout is accepted, so a tampered manifest
    /// cannot redirect the download to another repository or host.</summary>
    private static bool IsTrustedDownloadUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(
            "/" + OfficialRepository + "/releases/download/",
            StringComparison.Ordinal);

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9')
                && character is not (>= 'a' and <= 'f')
                && character is not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string> ReadLimitedAsync(
        Stream stream,
        long limit,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new AppUpdateException("更新信息无效。");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? ReadErrorLog(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int length = (int)Math.Min(stream.Length, MaximumErrorBytes);
            byte[] buffer = new byte[length];
            int read = stream.Read(buffer, 0, length);
            string text = Encoding.UTF8.GetString(buffer, 0, read).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException)
        {
            return null;
        }
    }

    private static AppVersion ReadCurrentVersion()
    {
        Assembly assembly = typeof(AppUpdateService).Assembly;
        string? text = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (AppVersion.TryParse(text, out AppVersion version))
        {
            return version;
        }

        Version? fallback = assembly.GetName().Version;
        return fallback is null
            ? new AppVersion(0, 0, 0, null)
            : new AppVersion(fallback.Major, fallback.Minor, Math.Max(0, fallback.Build), null);
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DiscImageStudio-Updater/1.0");
        return client;
    }

    private static bool TryGetUpdateArtifactPath(string? path, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        try
        {
            resolved = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException)
        {
            return false;
        }

        return resolved.StartsWith(UpdateRootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException)
        {
            return true;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !IsReparsePoint(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && !IsReparsePoint(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal const string HelperScript = """
        param(
            [Parameter(Mandatory)][int]$ProcessId,
            [Parameter(Mandatory)][string]$Target,
            [Parameter(Mandatory)][string]$Source,
            [Parameter(Mandatory)][string]$Backup,
            [Parameter(Mandatory)][string]$HealthFile,
            [Parameter(Mandatory)][string]$ErrorLog,
            [Parameter(Mandatory)][string]$ReadyFile,
            [Parameter(Mandatory)][string]$WorkDirectory,
            [int]$ExitTimeoutSeconds = 120,
            [int]$HealthTimeoutSeconds = 20
        )
        $ErrorActionPreference = 'Stop'
        $replaced = $false
        $completed = $false
        try {
            # Validate every input before signalling readiness; any failure leaves the app
            # running and lets it surface the message in this file.
            if (-not (Test-Path -LiteralPath $Target -PathType Leaf)) {
                throw 'The current application file is missing.'
            }
            if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
                throw 'The downloaded update is missing.'
            }
            if (-not (Test-Path -LiteralPath (Split-Path -Parent $Target) -PathType Container)) {
                throw 'The application directory is missing.'
            }
            Remove-Item -LiteralPath $HealthFile -Force -ErrorAction SilentlyContinue
            [IO.File]::WriteAllText($ReadyFile, 'ready')

            $deadline = [DateTime]::UtcNow.AddSeconds($ExitTimeoutSeconds)
            while ($true) {
                if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
                    break
                }
                if ([DateTime]::UtcNow -gt $deadline) {
                    throw 'The application did not exit in time.'
                }
                Start-Sleep -Milliseconds 250
            }

            if (Test-Path -LiteralPath $Backup) {
                Remove-Item -LiteralPath $Backup -Force
            }
            # The staging copy sits beside the target, so Replace is a single-volume atomic
            # swap: a crash leaves either the old or the new executable, never neither.
            [IO.File]::Replace($Source, $Target, $Backup, $true)
            $replaced = $true

            $env:DIS_UPDATE_HEALTH_FILE = $HealthFile
            $newProcess = Start-Process -FilePath $Target -WorkingDirectory (Split-Path -Parent $Target) -PassThru
            Remove-Item Env:DIS_UPDATE_HEALTH_FILE -ErrorAction SilentlyContinue
            $healthDeadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
            while ($true) {
                if (Test-Path -LiteralPath $HealthFile) {
                    $completed = $true
                    break
                }
                if ($newProcess.HasExited) {
                    throw "The updated application exited during startup with code $($newProcess.ExitCode)."
                }
                if ([DateTime]::UtcNow -gt $healthDeadline) {
                    throw 'The updated application did not finish starting in time.'
                }
                Start-Sleep -Milliseconds 200
            }
            Remove-Item -LiteralPath $Backup -Force -ErrorAction SilentlyContinue
        }
        catch {
            try { [IO.File]::WriteAllText($ErrorLog, [string]$_) } catch { }
            if ($replaced) {
                try {
                    if (Get-Process -Id $newProcess.Id -ErrorAction SilentlyContinue) {
                        Stop-Process -Id $newProcess.Id -Force -ErrorAction SilentlyContinue
                    }
                } catch { }
                try {
                    [IO.File]::Replace($Backup, $Target, $null, $true)
                } catch {
                    try { Move-Item -LiteralPath $Backup -Destination $Target -Force } catch { }
                }
                try {
                    $env:DIS_UPDATE_FAILURE_FILE = $ErrorLog
                    Start-Process -FilePath $Target -WorkingDirectory (Split-Path -Parent $Target)
                    Remove-Item Env:DIS_UPDATE_FAILURE_FILE -ErrorAction SilentlyContinue
                } catch { }
            }
            else {
                Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
            }
        }
        finally {
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $ReadyFile -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $HealthFile -Force -ErrorAction SilentlyContinue
            if ($completed) {
                Remove-Item -LiteralPath $WorkDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        """;
}
