using DiscImageStudio.Localization;
using System.IO;
using Microsoft.Win32;

namespace DiscImageStudio.Services;

/// Wraps the Win32 file dialogs behind testable methods; the owner window is
/// supplied by the host so the dialogs stay modal to the main window.
internal sealed class FileDialogService
{
    private const string ArtworkImageFilterKey = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.svg|所有文件|*.*";

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
            Filter = L.T(ArtworkImageFilterKey),
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
            Filter = L.T(ArtworkImageFilterKey),
            CheckFileExists = true,
            Multiselect = true,
        };
        return dialog.ShowDialog(_ownerProvider()) == true ? dialog.FileNames : null;
    }

    /// <summary>Multi-select picker for CD-Extra music; the formats the CD-Extra window accepts.</summary>
    internal string[]? PickAudioFiles()
    {
        OpenFileDialog dialog = new()
        {
            Title = L.T("添加音乐"),
            Filter = L.T("音乐文件（WAV、MP3、FLAC、M4A、AAC、WMA）|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.wma|所有文件|*.*"),
            CheckFileExists = true,
            Multiselect = true,
        };
        return dialog.ShowDialog(_ownerProvider()) == true ? dialog.FileNames : null;
    }

    internal string? PickCdExtraCheckpoint(string? folder = null)
    {
        OpenFileDialog dialog = new()
        {
            Title = L.T("选择图案断点"),
            Filter = L.T("CD Extra 图案断点") + "|" + Burning.CdExtraCheckpoint.FileName,
            CheckFileExists = true,
        };
        string initial = folder ?? Burning.CdExtraCheckpoint.DefaultRoot;
        if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(_ownerProvider()) == true ? dialog.FileName : null;
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
