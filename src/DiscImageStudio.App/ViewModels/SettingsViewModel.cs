using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

/// <summary>Backing state for SettingsWindow. The dialog works on a copy: nothing is written
/// or applied until <see cref="TrySave"/> succeeds, so Cancel/Escape discards every edit.
/// Cache usage and clearing always target the directory currently in effect, not the
/// unsaved path in the field. The About category is read-only statics, so saving skips it.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _store;
    private readonly AppUpdateService _updateService = new();
    private readonly CancellationTokenSource _lifetime = new();

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private int _languageIndex;

    [ObservableProperty]
    private string _defaultProjectDirectory = string.Empty;

    [ObservableProperty]
    private string _cacheDirectory = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _cacheUsageText = string.Empty;

    [ObservableProperty]
    private string _activeCacheDirectory = string.Empty;

    [ObservableProperty]
    private string _cacheMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _updateMessage = string.Empty;

    [ObservableProperty]
    private bool _updateFailed;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    public SettingsViewModel(AppSettings settings, AppSettingsStore store)
    {
        _store = store;
        AppSettings normalized = settings.Normalize();
        LanguageIndex = normalized.Language switch
        {
            L.SystemLanguage => 0,
            L.Chinese => 1,
            L.TraditionalChinese => 2,
            L.English => 3,
            L.Japanese => 4,
            L.Turkish => 5,
            L.Spanish => 6,
            L.Czech => 7,
            _ => 0,
        };
        ThemeIndex = normalized.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 0,
            _ => 2,
        };
        DefaultProjectDirectory = normalized.DefaultProjectDirectory;
        CacheDirectory = normalized.CacheDirectory;
        RefreshCacheUsage();
    }

    /// <summary>Folder the empty default-project field falls back to, shown as that field's
    /// watermark: an empty setting must not read as "no location".</summary>
    public string DefaultProjectDirectoryPlaceholder { get; } = ProjectJsonStore.GetDefaultProjectsDirectory();

    /// <summary>Cache folder used when the field is empty; same watermark purpose.</summary>
    public string DefaultCacheDirectoryPlaceholder { get; } = TempFiles.DefaultDirectory;

    /// <summary>Read-only About-category content: version line and the embedded license
    /// texts the standalone About dialog used to show.</summary>
    public string VersionText { get; } = BuildVersionText();

    public string RepositoryUrl { get; } = "https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio";

    public string LicenseText { get; } = ReadLicense("GPL");

    public string ThirdPartyNoticesText { get; } = ReadLicense("ThirdParty");

    public string FontLicenseText { get; } = ReadLicense("HarmonyOS");

    private static string ReadLicense(string name)
    {
        using Stream stream = typeof(SettingsViewModel).Assembly.GetManifestResourceStream($"DiscImageStudio.Licenses.{name}")
            ?? throw new InvalidOperationException($"Missing embedded license: {name}");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string BuildVersionText()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return L.F($"版本 {version?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");
    }

    /// <summary>Folder picker, supplied by the window: dialog title plus a starting folder.</summary>
    public Func<string, string?, string?>? PickDirectory { get; set; }

    /// <summary>Double confirmation for 清空缓存, supplied by the window.</summary>
    public Func<Task<bool>>? ConfirmClearCache { get; set; }

    /// <summary>Confirmation for installing a discovered version, supplied by the window.</summary>
    public Func<string, Task<bool>>? ConfirmUpdate { get; set; }

    /// <summary>Closes the dialog and hands the verified download to the main window, which
    /// runs the unsaved-project check before starting the replacement helper. Returns false
    /// when pending settings are invalid, so the dialog stays open for correction.</summary>
    public Func<string, bool>? RequestUpdateRestart { get; set; }

    /// <summary>Stops in-flight update work when the dialog closes for any reason.</summary>
    public void CancelPendingOperations() => _lifetime.Cancel();

    /// <summary>Reads the shell's busy flag so clearing stays disabled during a job.</summary>
    public Func<bool>? IsBusyProvider { get; set; }

    public AppSettings? SavedSettings { get; private set; }

    public void RefreshBusyState() => IsBusy = IsBusyProvider?.Invoke() ?? false;

    public void RefreshCacheUsage()
    {
        ActiveCacheDirectory = TempFiles.LivePreviewDirectory;
        CacheUsageText = L.T("已用 ") + FormatSize(TempFiles.GetCacheSize());
    }

    /// <summary>Validates the fields, persists the settings and reports whether the dialog may
    /// close. Save errors stay in the dialog so the user can fix the path and retry.</summary>
    public bool TrySave()
    {
        ErrorMessage = string.Empty;
        if (!TryNormalizeDirectory(DefaultProjectDirectory, requireExisting: true, out string projectDirectory,
                out string? error))
        {
            ErrorMessage = error!;
            return false;
        }

        if (!TryNormalizeDirectory(CacheDirectory, requireExisting: false, out string cacheDirectory,
                out error))
        {
            ErrorMessage = error!;
            return false;
        }

        AppSettings settings = new()
        {
            Language = LanguageIndex switch
            {
                0 => L.SystemLanguage,
                1 => L.Chinese,
                2 => L.TraditionalChinese,
                3 => L.English,
                4 => L.Japanese,
                5 => L.Turkish,
                6 => L.Spanish,
                7 => L.Czech,
                _ => L.SystemLanguage,
            },
            Theme = ThemeIndex switch
            {
                1 => AppTheme.Light,
                2 => AppTheme.System,
                0 => AppTheme.Dark,
                _ => AppTheme.System,
            },
            DefaultProjectDirectory = projectDirectory,
            CacheDirectory = cacheDirectory,
        };

        try
        {
            _store.Save(settings);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException
                                              or ArgumentException
                                              or SecurityException)
        {
            ErrorMessage = L.T("无法保存设置：") + exception.Message;
            return false;
        }

        SavedSettings = settings.Normalize();
        return true;
    }

    [RelayCommand]
    private void BrowseDefaultProjectDirectory()
    {
        string start = string.IsNullOrWhiteSpace(DefaultProjectDirectory)
            ? DefaultProjectDirectoryPlaceholder
            : DefaultProjectDirectory;
        string? picked = PickDirectory?.Invoke(L.T("选择默认工程位置"), start);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            DefaultProjectDirectory = picked;
        }
    }

    [RelayCommand]
    private void ResetDefaultProjectDirectory() => DefaultProjectDirectory = string.Empty;

    [RelayCommand]
    private void BrowseCacheDirectory()
    {
        string start = string.IsNullOrWhiteSpace(CacheDirectory)
            ? DefaultCacheDirectoryPlaceholder
            : CacheDirectory;
        string? picked = PickDirectory?.Invoke(L.T("选择缓存位置"), start);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            CacheDirectory = picked;
        }
    }

    [RelayCommand]
    private void ResetCacheDirectory() => CacheDirectory = string.Empty;

    [RelayCommand(CanExecute = nameof(CanClearCache))]
    private async Task ClearCacheAsync()
    {
        if (ConfirmClearCache is not null && !await ConfirmClearCache())
        {
            return;
        }

        int removed = TempFiles.ClearCache();
        RefreshCacheUsage();
        CacheMessage = removed > 0
            ? L.F($"已删除 {removed} 个缓存文件。")
            : L.T("缓存目录中没有可清理的文件。");
    }

    private bool CanClearCache() => !IsBusy;

    [RelayCommand]
    private void OpenAppDataDirectory()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscImageStudio");
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.ComponentModel.Win32Exception)
        {
            ErrorMessage = L.T("无法打开应用数据目录：") + exception.Message;
        }
    }

    [RelayCommand]
    private void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RepositoryUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException)
        {
            ErrorMessage = L.T("无法打开 GitHub：") + exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        CancellationToken cancellationToken = _lifetime.Token;
        IsCheckingForUpdates = true;
        UpdateFailed = false;
        UpdateMessage = L.T("正在检查更新");
        try
        {
            AppUpdateCheck? check;
            try
            {
                check = await _updateService.CheckAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                UpdateMessage = L.T("已取消更新。");
                return;
            }
            catch (AppUpdateException exception)
            {
                ShowUpdateFailure(L.T(exception.Message), exception.Details);
                return;
            }
            catch (Exception exception) when (IsUpdateNetworkFailure(exception))
            {
                ShowUpdateFailure(L.F($"检查更新失败：{exception.Message}"));
                return;
            }

            if (check is null)
            {
                UpdateMessage = L.T("已是最新版本。");
                return;
            }

            UpdateMessage = L.F($"发现新版本 {check.Version}。");
            if (ConfirmUpdate is null || !await ConfirmUpdate(check.Version.ToString()))
            {
                UpdateMessage = L.T("已取消更新。");
                return;
            }

            string downloadedPath;
            try
            {
                downloadedPath = await _updateService.DownloadAsync(
                    check,
                    new Progress<double>(percent => UpdateMessage = L.F($"正在下载更新：{percent}%")),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                UpdateMessage = L.T("已取消更新。");
                return;
            }
            catch (AppUpdateException exception)
            {
                ShowUpdateFailure(L.T(exception.Message), exception.Details);
                return;
            }
            catch (Exception exception) when (IsUpdateNetworkFailure(exception))
            {
                ShowUpdateFailure(L.F($"下载更新失败：{exception.Message}"));
                return;
            }

            if (RequestUpdateRestart is null)
            {
                ShowUpdateFailure(L.T("无法启动更新程序。"));
                return;
            }

            // Success closes the dialog and lets the main window persist the project before
            // any executable is touched. Invalid pending settings leave the dialog open.
            if (!RequestUpdateRestart(downloadedPath))
            {
                UpdateMessage = L.T("更新已下载。请修正设置后再次安装。");
                return;
            }

            UpdateMessage = L.T("更新已下载，正在重启应用。");
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private static bool IsUpdateNetworkFailure(Exception exception) =>
        exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or TaskCanceledException;

    private void ShowUpdateFailure(string message, string? details = null)
    {
        UpdateFailed = true;
        UpdateMessage = string.IsNullOrWhiteSpace(details) ? message : message + "\n" + details;
    }

    private bool CanCheckForUpdates() => !IsBusy && !IsCheckingForUpdates;

    partial void OnIsBusyChanged(bool value)
    {
        ClearCacheCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCheckingForUpdatesChanged(bool value) => CheckForUpdatesCommand.NotifyCanExecuteChanged();

    private static bool TryNormalizeDirectory(
        string value,
        bool requireExisting,
        out string fullPath,
        out string? error)
    {
        fullPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string trimmed = value.Trim();
        if (!Path.IsPathRooted(trimmed))
        {
            error = L.T("请填写绝对路径。");
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or SecurityException)
        {
            error = L.T("路径无效：") + trimmed;
            return false;
        }

        if (requireExisting && !Directory.Exists(fullPath))
        {
            error = L.T("目录不存在：") + fullPath;
            return false;
        }

        return true;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
