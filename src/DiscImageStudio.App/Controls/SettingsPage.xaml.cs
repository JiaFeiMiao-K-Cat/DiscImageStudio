using DiscImageStudio.Localization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using Microsoft.Win32;

namespace DiscImageStudio.Controls;

/// <summary>How a settings session ended.</summary>
internal enum SettingsOutcome
{
    Cancelled,
    Saved,
    UpdateRequested,
}

/// <summary>
/// The settings surface hosted inside the main window. It keeps the exact rules of the former
/// modal dialog: nothing is applied until Save; Cancel, Back and Escape drop the edits, and
/// unsaved edits go through the save/discard/cancel confirmation first. The page owns the
/// folder picker and the clear-cache/update confirmations so the view model stays free of
/// dialogs. A session is one <see cref="Begin"/> → <see cref="Completion"/>.
/// </summary>
public partial class SettingsPage : UserControl
{
    private SettingsViewModel? _viewModel;
    private TaskCompletionSource<SettingsOutcome>? _completion;
    private string? _downloadedUpdatePath;
    private bool _isCloseFlowRunning;

    public SettingsPage()
    {
        InitializeComponent();
        PreviewKeyDown += SettingsPage_PreviewKeyDown;
    }

    internal bool UpdateRequested { get; private set; }

    internal string? PendingUpdatePath { get; private set; }

    internal SettingsViewModel? ViewModel => _viewModel;

    internal bool IsActiveSession => _completion is { Task.IsCompleted: false };

    /// <summary>Starts a session on a fresh view model and returns when it ends.</summary>
    internal Task<SettingsOutcome> Begin(SettingsViewModel viewModel)
    {
        _completion?.TrySetResult(SettingsOutcome.Cancelled);
        _viewModel = viewModel;
        _downloadedUpdatePath = null;
        UpdateRequested = false;
        PendingUpdatePath = null;
        _isCloseFlowRunning = false;
        DataContext = viewModel;
        viewModel.PickDirectory = PickFolder;
        viewModel.ConfirmClearCache = ConfirmClearCacheAsync;
        viewModel.ConfirmUpdate = ConfirmUpdateAsync;
        viewModel.RequestUpdateRestart = RequestUpdateRestart;
        SelectCategory(0);
        ContentScroller.ScrollToVerticalOffset(0);
        _completion = new TaskCompletionSource<SettingsOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _completion.Task;
    }

    /// <summary>Back arrow, title-bar gear (second click) and Escape all ask to leave; unsaved
    /// edits are confirmed first, exactly like closing the old dialog.</summary>
    internal void RequestClose()
    {
        if (!IsActiveSession || _viewModel is null) return;
        if (!UpdateRequested && _viewModel.IsDirty)
        {
            if (_isCloseFlowRunning) return;
            _isCloseFlowRunning = true;
            _ = RunCloseConfirmationAsync();
            return;
        }

        Finish(SettingsOutcome.Cancelled);
    }

    /// <summary>Window shutdown: end immediately, releasing pending confirmations.</summary>
    internal void Abort()
    {
        if (!IsActiveSession) return;
        Finish(SettingsOutcome.Cancelled);
    }

    private bool RequestUpdateRestart(string downloadedPath)
    {
        _downloadedUpdatePath = downloadedPath;
        if (_viewModel is null || !_viewModel.TrySave())
        {
            return false;
        }

        UpdateRequested = true;
        PendingUpdatePath = downloadedPath;
        Finish(SettingsOutcome.UpdateRequested);
        return true;
    }

    private async Task RunCloseConfirmationAsync()
    {
        try
        {
            bool? decision = await UnsavedConfirm.ShowThreeWayAsync(
                L.T("有未保存的更改"),
                L.T("设置更改尚未保存。要保存吗？"),
                L.T("Settings.Save"),
                L.T("不保存"));
            if (decision is true)
            {
                // A failed save keeps the page open with its error message visible.
                if (_viewModel?.TrySave() == true)
                {
                    Finish(SettingsOutcome.Saved);
                }

                return;
            }

            if (decision is false)
            {
                Finish(SettingsOutcome.Cancelled);
            }
            // null: cancelled — keep editing.
        }
        finally
        {
            _isCloseFlowRunning = false;
        }
    }

    private void Finish(SettingsOutcome outcome)
    {
        TaskCompletionSource<SettingsOutcome>? completion = _completion;
        if (completion is null || completion.Task.IsCompleted) return;
        // Stop in-flight network work and release confirmations that would otherwise await a
        // page that is folding away.
        _viewModel?.CancelPendingOperations();
        ClearCacheConfirm.CancelPending();
        UpdateConfirm.CancelPending();
        UnsavedConfirm.CancelPending();
        if (outcome != SettingsOutcome.UpdateRequested)
        {
            // A downloaded update that never reaches the main window would otherwise sit in
            // the cache until the next launch; drop it with the session that owned it.
            AppUpdateService.DiscardDownloadedUpdate(_downloadedUpdatePath);
        }

        completion.TrySetResult(outcome);
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

        Window? owner = Window.GetWindow(this);
        bool? picked = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return picked == true ? dialog.FolderName : null;
    }

    private Task<bool> ConfirmClearCacheAsync() => ClearCacheConfirm.ShowAsync(
        L.T("清空缓存"),
        L.T("将删除本应用写入缓存目录的缩略图、合成图与预览图片。工程文件与导出文件不受影响。"),
        L.T("清空缓存"));

    private Task<bool> ConfirmUpdateAsync(string version) => UpdateConfirm.ShowPrimaryAsync(
        L.T("应用更新"),
        L.F($"发现新版本 {version}。是否下载并安装？应用将重新启动。"),
        L.T("下载并安装"));

    /// <summary>Keyboard focus lands on the active category, like a freshly opened dialog.</summary>
    internal void FocusFirst()
    {
        if (!IsActiveSession) return;
        SideNavItem active = GeneralNav.IsActive ? GeneralNav : StorageNav.IsActive ? StorageNav : AboutNav;
        active.Focus();
    }

    internal void ShowCategoryForSnapshot(int index) => SelectCategory(index);

    private void GeneralNav_Activated(object sender, RoutedEventArgs e) => SelectCategory(0);

    private void StorageNav_Activated(object sender, RoutedEventArgs e) => SelectCategory(1);

    private void AboutNav_Activated(object sender, RoutedEventArgs e) => SelectCategory(2);

    private int _category = -1;

    private void SelectCategory(int index)
    {
        GeneralNav.IsActive = index == 0;
        StorageNav.IsActive = index == 1;
        AboutNav.IsActive = index == 2;
        GeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (_category != index && _category >= 0 && IsLoaded)
        {
            ContentScroller.ScrollToVerticalOffset(0);
            Motion.Enter(ContentColumn, 8);
        }

        _category = index;
    }

    /// <summary>Escape first closes an open confirmation; otherwise it leaves settings.</summary>
    internal bool HandleEscape()
    {
        if (ClearCacheConfirm.HandleKeyDown(Key.Escape) || UpdateConfirm.HandleKeyDown(Key.Escape)
            || UnsavedConfirm.HandleKeyDown(Key.Escape))
        {
            return true;
        }

        RequestClose();
        return true;
    }

    private void SettingsPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsActiveSession)
        {
            e.Handled = HandleEscape();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.TrySave() == true)
        {
            Finish(SettingsOutcome.Saved);
        }
    }

    // Same as the old dialog: Cancel closes through the unsaved-edits gate.
    private void Cancel_Click(object sender, RoutedEventArgs e) => RequestClose();
}
