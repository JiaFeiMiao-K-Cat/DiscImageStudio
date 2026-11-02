using DiscImageStudio.Localization;
using System.IO;
using Microsoft.Win32;

namespace DiscImageStudio.Services;

/// Wraps the Win32 file dialogs behind testable methods; the owner window is
/// supplied by the host so the dialogs stay modal to the main window.
internal sealed class FileDialogService
{
    private readonly Func<System.Windows.Window> _ownerProvider;

    internal FileDialogService(Func<System.Windows.Window> ownerProvider)
    {
        _ownerProvider = ownerProvider;
    }

    internal string? PickImage()
    {
        OpenFileDialog dialog = new()
        {
            Title = L.T("选择源图片"),
            Filter = L.T("图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|所有文件|*.*"),
            CheckFileExists = true,
        };
        return dialog.ShowDialog(_ownerProvider()) == true ? dialog.FileName : null;
    }

    /// <summary>Multi-select image picker for the design page's import action.</summary>
    internal string[]? PickImages()
    {
        OpenFileDialog dialog = new()
        {
            Title = L.T("导入图片"),
            Filter = L.T("图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|所有文件|*.*"),
            CheckFileExists = true,
            Multiselect = true,
        };
        return dialog.ShowDialog(_ownerProvider()) == true ? dialog.FileNames : null;
    }

    internal string? PickOpen(string title, string filter)
    {
        OpenFileDialog dialog = new()
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(_ownerProvider()) == true ? dialog.FileName : null;
    }

    internal string? PickSave(string filter, string extension, string fileName)
    {
        SaveFileDialog dialog = new()
        {
            Title = L.T("选择输出位置"),
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            FileName = fileName,
        };
        return dialog.ShowDialog(_ownerProvider()) == true ? dialog.FileName : null;
    }

    internal string? PickFolder(string title)
    {
        OpenFolderDialog dialog = new()
        {
            Title = title,
            Multiselect = false,
        };
        return dialog.ShowDialog(_ownerProvider()) == true ? dialog.FolderName : null;
    }
}
