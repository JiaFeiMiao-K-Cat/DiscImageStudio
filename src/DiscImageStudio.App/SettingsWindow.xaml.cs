using DiscImageStudio.Localization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using Microsoft.Win32;

namespace DiscImageStudio;

/// <summary>Modal settings surface: categories on the left, the active category on the right,
/// Save/Cancel pinned to the bottom. Nothing is applied until Save; Cancel or Escape drops
/// every edit. The window owns the folder picker and the clear-cache confirmation so the view
/// model stays free of dialogs.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private string? _downloadedUpdatePath;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PickDirectory = PickFolder;
        viewModel.ConfirmClearCache = ConfirmClearCacheAsync;
        viewModel.ConfirmUpdate = ConfirmUpdateAsync;
        viewModel.RequestUpdateRestart = RequestUpdateRestart;
        SourceInitialized += (_, _) => WindowCorners.Apply(this);
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
        Closing += SettingsWindow_Closing;
    }

    /// <summary>Set when the downloaded update should be installed by the main window after
    /// this dialog closes. The update itself never shuts the application down from here.</summary>
    internal bool UpdateRequested { get; private set; }

    internal string? PendingUpdatePath { get; private set; }

    /// <summary>Persists the edits that are still pending in this dialog, then closes it so the
    /// main window can run its own unsaved-project check and restart. Invalid settings keep the
    /// dialog open; the downloaded file stays cached until the user fixes or cancels.</summary>
    private bool RequestUpdateRestart(string downloadedPath)
    {
        _downloadedUpdatePath = downloadedPath;
        if (!_viewModel.TrySave())
        {
            return false;
        }

        UpdateRequested = true;
        PendingUpdatePath = downloadedPath;
        DialogResult = false;
        return true;
    }

    private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Escape, X, Save and Cancel all end this dialog's lifetime. Stop any in-flight
        // network work and release confirmations that would otherwise await a hidden modal.
        _viewModel.CancelPendingOperations();
        ClearCacheConfirm.CancelPending();
        UpdateConfirm.CancelPending();
        if (!UpdateRequested)
        {
            // A downloaded update that never reaches the main window would otherwise sit in
            // the cache until the next launch; drop it with the dialog that owned it.
            AppUpdateService.DiscardDownloadedUpdate(_downloadedUpdatePath);
        }
    }

    private string? PickFolder(string title, string? initialDirectory)
    {
        OpenFolderDialog dialog = new()
        {
            Title = title,
            Multiselect = false,
        };
        string start = string.IsNullOrWhiteSpace(initialDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : initialDirectory;
        if (Directory.Exists(start))
        {
            dialog.InitialDirectory = start;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private Task<bool> ConfirmClearCacheAsync() => ClearCacheConfirm.ShowAsync(
        L.T("清空缓存"),
        L.T("将删除本应用写入缓存目录的缩略图、合成图与预览图片。工程文件与导出文件不受影响。"),
        L.T("清空缓存"));

    private Task<bool> ConfirmUpdateAsync(string version) => UpdateConfirm.ShowPrimaryAsync(
        L.T("应用更新"),
        L.F($"发现新版本 {version}。是否下载并安装？应用将重新启动。"),
        L.T("下载并安装"));

    internal void ShowCategoryForSnapshot(int index) => SelectCategory(index);

    private void GeneralNav_Activated(object sender, RoutedEventArgs e) => SelectCategory(0);

    private void StorageNav_Activated(object sender, RoutedEventArgs e) => SelectCategory(1);

    private void AutoCalNav_Activated(object sender, RoutedEventArgs e) => SelectCategory(2);

    private void AboutNav_Activated(object sender, RoutedEventArgs e) => SelectCategory(3);

    private void SelectCategory(int index)
    {
        GeneralNav.IsActive = index == 0;
        StorageNav.IsActive = index == 1;
        AutoCalNav.IsActive = index == 2;
        AboutNav.IsActive = index == 3;
        GeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AutoCalPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (ClearCacheConfirm.HandleKeyDown(e.Key) || UpdateConfirm.HandleKeyDown(e.Key))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        DialogResult = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TrySave())
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}