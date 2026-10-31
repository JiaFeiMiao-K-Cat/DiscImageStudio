using System.Diagnostics;
using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>Backing state for SettingsWindow. The dialog works on a copy: nothing is written
/// or applied until <see cref="TrySave"/> succeeds, so Cancel/Escape discards every edit.
/// Cache usage and clearing always target the directory currently in effect, not the
/// unsaved path in the field.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _store;

    [ObservableProperty]
    private int _themeIndex;

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

    public SettingsViewModel(AppSettings settings, AppSettingsStore store)
    {
        _store = store;
        AppSettings normalized = settings.Normalize();
        ThemeIndex = normalized.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.System => 2,
            _ => 0,
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

    /// <summary>Folder picker, supplied by the window: dialog title plus a starting folder.</summary>
    public Func<string, string?, string?>? PickDirectory { get; set; }

    /// <summary>Double confirmation for 清空缓存, supplied by the window.</summary>
    public Func<Task<bool>>? ConfirmClearCache { get; set; }

    /// <summary>Reads the shell's busy flag so clearing stays disabled during a job.</summary>
    public Func<bool>? IsBusyProvider { get; set; }

    public AppSettings? SavedSettings { get; private set; }

    public void RefreshBusyState() => IsBusy = IsBusyProvider?.Invoke() ?? false;

    public void RefreshCacheUsage()
    {
        ActiveCacheDirectory = TempFiles.LivePreviewDirectory;
        CacheUsageText = "已用 " + FormatSize(TempFiles.GetCacheSize());
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
            Theme = ThemeIndex switch
            {
                1 => AppTheme.Light,
                2 => AppTheme.System,
                _ => AppTheme.Dark,
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
            ErrorMessage = "无法保存设置：" + exception.Message;
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
        string? picked = PickDirectory?.Invoke("选择默认工程位置", start);
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
        string? picked = PickDirectory?.Invoke("选择缓存位置", start);
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
            ? $"已删除 {removed} 个缓存文件。"
            : "缓存目录中没有可清理的文件。";
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
            ErrorMessage = "无法打开应用数据目录：" + exception.Message;
        }
    }

    partial void OnIsBusyChanged(bool value) => ClearCacheCommand.NotifyCanExecuteChanged();

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
            error = "请填写绝对路径。";
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
            error = "路径无效：" + trimmed;
            return false;
        }

        if (requireExisting && !Directory.Exists(fullPath))
        {
            error = "目录不存在：" + fullPath;
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
