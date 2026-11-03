using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using DiscImageStudio.Linux.Services;
using DiscImageStudio.Linux.ViewModels;
using DiscImageStudio.Linux.Views;

namespace DiscImageStudio.Linux;

public partial class MainWindow : Window, IUiServices
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is { } model)
        {
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.LogText))
                {
                    LogScroller.ScrollToEnd();
                }
            };
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || Model is not { } model)
        {
            return;
        }

        if (model.IsBusy || model.Project is { IsDirty: true })
        {
            e.Cancel = true;
            if (model.IsBusy
                && await AskAsync("A job is still running", "Quit anyway? The output being written will be incomplete, and a disc being burned will most likely be ruined.",
                    "Quit", null, "Keep running", danger: true) != DialogChoice.Primary)
            {
                return;
            }

            if (!await model.ConfirmDiscardChangesAsync())
            {
                return;
            }

            _closeConfirmed = true;
            model.CancelJobCommand.Execute(null);
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (Model is { } model)
        {
            model.Project?.ReleaseCaches();
            SettingsStore.Save(model.Settings);
        }
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FilePickerFileType> filters, string? startDirectory, bool allowMultiple)
    {
        FilePickerOpenOptions options = new()
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = filters,
            SuggestedStartLocation = await FolderAsync(startDirectory),
        };
        IReadOnlyList<IStorageFile> files = await Guard(() => StorageProvider.OpenFilePickerAsync(options), []);
        return files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string defaultExtension, IReadOnlyList<FilePickerFileType> filters, string? startDirectory)
    {
        FilePickerSaveOptions options = new()
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension,
            FileTypeChoices = filters,
            ShowOverwritePrompt = false,
            SuggestedStartLocation = await FolderAsync(startDirectory),
        };
        IStorageFile? file = await Guard(() => StorageProvider.SaveFilePickerAsync(options), null);
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title, string? startDirectory)
    {
        FolderPickerOpenOptions options = new()
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await FolderAsync(startDirectory),
        };
        IReadOnlyList<IStorageFolder> folders = await Guard(() => StorageProvider.OpenFolderPickerAsync(options), []);
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<DialogChoice> AskAsync(string title, string message, string primary, string? secondary = null, string? cancel = "Cancel", bool danger = false)
    {
        MessageWindow dialog = new(title, message, primary, secondary, cancel, danger);
        return await dialog.ShowDialog<DialogChoice?>(this) ?? DialogChoice.Cancel;
    }

    public async Task<ProjectSetupViewModel?> ShowProjectSetupAsync(ProjectSetupViewModel setup)
    {
        ProjectSetupWindow dialog = new() { DataContext = setup };
        setup.PickFolder = start => PickFolderAsync("Choose the project folder", start);
        await dialog.ShowDialog(this);
        return setup.Accepted ? setup : null;
    }

    public async Task ShowSettingsAsync()
    {
        if (Model is { } model)
        {
            await new SettingsWindow(model).ShowDialog(this);
        }
    }

    public async Task ShowAboutAsync() => await new AboutWindow().ShowDialog(this);

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>File dialogs come from the XDG desktop portal, or GTK 3 as a fallback. A
    /// minimal session may have neither; say what to install instead of failing silently.</summary>
    private async Task<T> Guard<T>(Func<Task<T>> picker, T fallback)
    {
        try
        {
            return await picker();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Model?.ShowToast(ToastKind.Error, "No file dialog available",
                "Install xdg-desktop-portal with a backend (for example xdg-desktop-portal-gtk) or GTK 3. " + exception.Message);
            return fallback;
        }
    }

    private async Task<IStorageFolder?> FolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string? existing = path;
        while (existing is not null && !Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
        }

        return existing is null ? null : await StorageProvider.TryGetFolderFromPathAsync(new Uri(existing));
    }
}
