using Avalonia.Platform.Storage;

namespace DiscImageStudio.Linux.Services;

public enum DialogChoice
{
    Primary,
    Secondary,
    Cancel,
}

/// <summary>What view models need from the window: native pickers (the XDG desktop portal
/// when available, so GNOME and KDE show their own dialogs), modal questions and the
/// clipboard.</summary>
public interface IUiServices
{
    Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FilePickerFileType> filters, string? startDirectory, bool allowMultiple);

    Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string defaultExtension, IReadOnlyList<FilePickerFileType> filters, string? startDirectory);

    Task<string?> PickFolderAsync(string title, string? startDirectory);

    Task<DialogChoice> AskAsync(string title, string message, string primary, string? secondary = null, string? cancel = "Cancel", bool danger = false);

    Task<ViewModels.ProjectSetupViewModel?> ShowProjectSetupAsync(ViewModels.ProjectSetupViewModel setup);

    Task ShowSettingsAsync();

    Task ShowAboutAsync();

    Task CopyTextAsync(string text);
}

public static class FileTypes
{
    public static readonly FilePickerFileType Images = new("Images")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.PNG", "*.JPG", "*.JPEG"],
        MimeTypes = ["image/png", "image/jpeg", "image/bmp", "image/gif", "image/webp"],
    };

    public static readonly FilePickerFileType Project = new("Disc Image Studio project")
    {
        Patterns = ["*.discproj"],
        MimeTypes = ["application/x-discimagestudio-project"],
    };

    public static readonly FilePickerFileType Iso = new("ISO image") { Patterns = ["*.iso"], MimeTypes = ["application/x-iso9660-image"] };

    public static readonly FilePickerFileType Wave = new("WAV audio track (recommended)") { Patterns = ["*.wav"], MimeTypes = ["audio/x-wav", "audio/wav"] };

    public static readonly FilePickerFileType Raw = new("RAW audio track (big-endian, legacy)") { Patterns = ["*.raw"] };

    public static readonly FilePickerFileType Burnable = new("Disc images and tracks") { Patterns = ["*.iso", "*.wav", "*.raw"] };

    public static readonly FilePickerFileType All = new("All files") { Patterns = ["*"] };
}
