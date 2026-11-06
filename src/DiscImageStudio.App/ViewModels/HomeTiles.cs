using DiscImageStudio.Localization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.ViewModels;

/// <summary>One template card on the home page: the blank project (PresetId null) or a disc
/// preset from the editable catalog. Clicking opens the project dialog with the template
/// preselected; the dialog remains where the parameters are confirmed.</summary>
public sealed record HomeTemplateTile(
    string? PresetId,
    string Title,
    string Detail,
    PackIconLucideKind Icon,
    OpticalDiscFamily Family,
    bool IsBlank,
    string? Contributor = null)
{
    // Built on demand: the card carries localized text, so a cached instance would keep the
    // language it was created in.
    public static HomeTemplateTile CreateBlank() => new(
        null,
        L.T("空白工程"),
        L.T("从默认参数开始"),
        PackIconLucideKind.SquarePen,
        OpticalDiscFamily.CompactDisc,
        IsBlank: true);

    public string FamilyLabel => Family switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };

    public string ToolTipText => string.IsNullOrWhiteSpace(Detail)
        ? Title
        : $"{Title}\n{Detail}";

    public bool HasContributor => !string.IsNullOrWhiteSpace(Contributor);

    public System.Windows.Visibility ContributorVisibility => HasContributor
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public string ContributorToolTip => L.F($"贡献者：{Contributor}");
}

/// <summary>One card in the home page's recent-project list. The thumbnail is the picture
/// rendered into the project file at last save; a missing file degrades the card but keeps
/// its row so the path stays discoverable.</summary>
public sealed partial class RecentProjectTile : ObservableObject
{
    private readonly bool _projectExists;

    /// <summary>This tile's project is being opened, so the sleeve shows the looping wait
    /// stroke. Set by the home page around the open call and cleared when it returns.</summary>
    [ObservableProperty] private bool _isLoading;

    private RecentProjectTile(RecentProjectEntry entry, BitmapSource? thumbnail, bool projectExists)
    {
        Entry = entry;
        Thumbnail = thumbnail;
        _projectExists = projectExists;
    }

    internal RecentProjectEntry Entry { get; }

    public BitmapSource? Thumbnail { get; }

    public bool HasThumbnail => Thumbnail is not null;

    public string Title => Entry.Name;

    public string FamilyLabel => Entry.FamilyLabel;

    public string LocationText => _projectExists
        ? Path.GetDirectoryName(Entry.ProjectPath) ?? Entry.ProjectPath
        : L.T("工程文件已移动或删除");

    public string ToolTipText
        => L.F($"{Entry.ProjectPath}\n最近打开：{Entry.LastOpenedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");

    internal static RecentProjectTile Create(RecentProjectEntry entry)
    {
        bool projectExists = File.Exists(entry.ProjectPath);
        return new RecentProjectTile(entry, DecodeThumbnail(entry.ThumbnailPngBase64), projectExists);
    }

    private static BitmapSource? DecodeThumbnail(string? pngBase64)
    {
        if (string.IsNullOrWhiteSpace(pngBase64))
        {
            return null;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(pngBase64);
            BitmapImage bitmap = new();
            using MemoryStream stream = new(bytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
