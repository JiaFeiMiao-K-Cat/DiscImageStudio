using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Controls;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

public sealed record TemplateItem(DiscPreset? Preset, string Name, string Subtitle, string Icon);

public sealed partial class RecentProjectItem : ObservableObject
{
    [ObservableProperty] private Bitmap? _thumbnail;

    public RecentProjectItem(string path, string name, string familyLabel, bool exists)
    {
        Path = path;
        Name = name;
        FamilyLabel = familyLabel;
        Exists = exists;
    }

    public string Path { get; }

    public string Name { get; }

    public string FamilyLabel { get; }

    public bool Exists { get; }

    public string Folder => AppPaths.ShortenHome(System.IO.Path.GetDirectoryName(Path) ?? Path);

    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
}

public partial class HomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _refreshGeneration;

    [ObservableProperty] private bool _hasRecent;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        Templates.Add(new TemplateItem(null, "Blank project", "Start from defaults", Icons.FilePlus));
        foreach (DiscPreset preset in PresetCatalog.All.Where(p => p.IsBuiltIn))
        {
            Templates.Add(new TemplateItem(preset, preset.Name, DiscFamilies.LongLabel(preset.Family), DiscFamilies.Icon(preset.Family)));
        }

        RefreshRecent();
    }

    public ObservableCollection<TemplateItem> Templates { get; } = [];

    public ObservableCollection<RecentProjectItem> Recent { get; } = [];

    public string Version => MainViewModel.Version;

    [RelayCommand]
    private async Task NewFromTemplate(TemplateItem? template) => await _main.CreateProjectAsync(template?.Preset);

    [RelayCommand]
    private async Task OpenRecent(RecentProjectItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!File.Exists(item.Path))
        {
            _main.ShowToast(ToastKind.Warning, "Project not found", AppPaths.ShortenHome(item.Path));
            RemoveRecent(item);
            return;
        }

        await _main.OpenProjectFileAsync(item.Path);
    }

    [RelayCommand]
    private void RemoveRecent(RecentProjectItem? item)
    {
        if (item is null)
        {
            return;
        }

        _main.Settings.RecentProjects.Remove(item.Path);
        SettingsStore.Save(_main.Settings);
        Recent.Remove(item);
        HasRecent = Recent.Count > 0;
    }

    [RelayCommand]
    private void ClearRecent()
    {
        _main.Settings.RecentProjects.Clear();
        SettingsStore.Save(_main.Settings);
        Recent.Clear();
        HasRecent = false;
    }

    /// <summary>Reads each recent project's header and thumbnail in the background.</summary>
    public void RefreshRecent()
    {
        int generation = ++_refreshGeneration;
        List<string> paths = [.. _main.Settings.RecentProjects];
        _ = Task.Run(() =>
        {
            List<(RecentProjectItem Item, byte[]? Png)> items = [];
            foreach (string path in paths)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        items.Add((new RecentProjectItem(path, System.IO.Path.GetFileNameWithoutExtension(path), "?", false), null));
                        continue;
                    }

                    ProjectDocument document = ProjectJsonStore.Load(path);
                    byte[]? png = document.ThumbnailPngBase64 is { Length: > 0 } base64 ? Convert.FromBase64String(base64) : null;
                    items.Add((new RecentProjectItem(path, document.Name, document.FamilyLabel, true), png));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
                {
                    items.Add((new RecentProjectItem(path, System.IO.Path.GetFileNameWithoutExtension(path), "!", true), null));
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _refreshGeneration)
                {
                    return;
                }

                Recent.Clear();
                foreach ((RecentProjectItem item, byte[]? png) in items)
                {
                    if (png is not null)
                    {
                        try
                        {
                            using MemoryStream stream = new(png);
                            item.Thumbnail = new Bitmap(stream);
                        }
                        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
                        {
                        }
                    }

                    Recent.Add(item);
                }

                HasRecent = Recent.Count > 0;
            });
        });
    }
}
