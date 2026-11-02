using System.IO;
using System.Windows;
using System.Windows.Input;
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

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PickDirectory = PickFolder;
        viewModel.ConfirmClearCache = ConfirmClearCacheAsync;
        SourceInitialized += (_, _) => WindowCorners.Apply(this);
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
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
        "清空缓存",
        "将删除本应用写入缓存目录的缩略图、合成图与预览图片。工程文件与导出文件不受影响。",
        "清空缓存");

    internal void ShowCategoryForSnapshot(int index) => SelectCategory(index);

    private void GeneralNav_Activated(object sender, RoutedEventArgs e) => SelectCategory(0);

    private void StorageNav_Activated(object sender, RoutedEventArgs e) => SelectCategory(1);

    private void SelectCategory(int index)
    {
        GeneralNav.IsActive = index == 0;
        StorageNav.IsActive = index == 1;
        GeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (ClearCacheConfirm.HandleKeyDown(e.Key))
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
