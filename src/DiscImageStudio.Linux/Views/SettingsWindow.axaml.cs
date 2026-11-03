using Avalonia.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;
using DiscImageStudio.Linux.ViewModels;

namespace DiscImageStudio.Linux.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel? _model;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(MainViewModel model) : this()
    {
        _model = model;
        ThemeSystem.IsChecked = model.Settings.Theme == ThemePreference.System;
        ThemeDark.IsChecked = model.Settings.Theme == ThemePreference.Dark;
        ThemeLight.IsChecked = model.Settings.Theme == ThemePreference.Light;
        ThemeSystem.IsCheckedChanged += (_, _) => SetTheme(ThemeSystem, ThemePreference.System);
        ThemeDark.IsCheckedChanged += (_, _) => SetTheme(ThemeDark, ThemePreference.Dark);
        ThemeLight.IsCheckedChanged += (_, _) => SetTheme(ThemeLight, ThemePreference.Light);
        PresetsText.Text = AppPaths.ShortenHome(PresetCatalog.UserPresetPath) + (File.Exists(PresetCatalog.UserPresetPath) ? string.Empty : "  (not created)");
        SettingsText.Text = AppPaths.ShortenHome(AppPaths.SettingsFile);
        ChooseProjectsButton.Click += async (_, _) => await ChooseProjectsFolder();
        ClearCacheButton.Click += (_, _) => ClearCache();
        DoneButton.Click += (_, _) => Close();
        Refresh();
    }

    private void SetTheme(RadioButton button, ThemePreference preference)
    {
        if (_model is null || button.IsChecked != true)
        {
            return;
        }

        _model.Settings.Theme = preference;
        App.ApplyTheme(preference);
        SettingsStore.Save(_model.Settings);
    }

    private async Task ChooseProjectsFolder()
    {
        if (_model?.Ui is null)
        {
            return;
        }

        string? folder = await _model.Ui.PickFolderAsync("Folder for new projects", ProjectsFolder());
        if (folder is not null)
        {
            _model.Settings.ProjectsDirectory = folder;
            SettingsStore.Save(_model.Settings);
            Refresh();
        }
    }

    private void ClearCache()
    {
        _model?.Project?.ReleaseCaches();
        AppPaths.CleanWorkDirectory();
        Refresh();
    }

    private string ProjectsFolder()
        => _model?.Settings.ProjectsDirectory is { Length: > 0 } folder ? folder : AppPaths.DefaultProjectsDirectory;

    private void Refresh()
    {
        ProjectsText.Text = AppPaths.ShortenHome(ProjectsFolder());
        long bytes = 0;
        try
        {
            if (Directory.Exists(AppPaths.WorkDirectory))
            {
                bytes = Directory.EnumerateFiles(AppPaths.WorkDirectory).Sum(file => new FileInfo(file).Length);
            }
        }
        catch (IOException)
        {
        }

        CacheText.Text = $"{AppPaths.ShortenHome(AppPaths.WorkDirectory)}  ({Num.Bytes(bytes)})";
    }
}
