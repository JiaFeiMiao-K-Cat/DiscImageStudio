using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>Where the CD-Extra window's CD-TEXT starts from.</summary>
public enum CdExtraTextSource
{
    /// <summary>The tags of the music added in this window (the default).</summary>
    Window,

    /// <summary>The design page's CD-TEXT, matched to this window's music by file path.</summary>
    Design,
}

/// <summary>One track's CD-TEXT in the CD-Extra window. <see cref="Origin"/> names where the
/// current text came from, so the table doubles as the per-track preview.</summary>
public sealed partial class CdExtraTextRow : ObservableObject
{
    private readonly Action _edited;
    private bool _loading;

    internal CdExtraTextRow(string path, Action edited)
    {
        Path = path;
        _edited = edited;
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText))]
    private int _number;

    public string NumberText => Number.ToString("00", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _performer = string.Empty;

    [ObservableProperty]
    private string _origin = string.Empty;

    /// <summary>Why the title will not show correctly on most players, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTitleIssue), nameof(TitleToolTip))]
    private string? _titleIssue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPerformerIssue))]
    private string? _performerIssue;

    public bool HasTitleIssue => TitleIssue is not null;

    public bool HasPerformerIssue => PerformerIssue is not null;

    public string TitleToolTip => TitleIssue is null ? FileName : FileName + "\n" + TitleIssue;

    /// <summary>Text set by the window, not typed: the origin label stays.</summary>
    internal void Set(string? title, string? performer, string origin)
    {
        _loading = true;
        Title = title ?? string.Empty;
        Performer = performer ?? string.Empty;
        Origin = origin;
        _loading = false;
    }

    partial void OnTitleChanged(string value) => Typed();

    partial void OnPerformerChanged(string value) => Typed();

    private void Typed()
    {
        if (_loading) return;
        Origin = CdExtraCdTextModel.OriginManual;
        _edited();
    }
}

/// <summary>
/// The CD-Extra window's own CD-TEXT: album fields and an editable row per track. It starts
/// from this window's music tags (or, when chosen, from the design page) and is changed only
/// here; online lookup writes into it after the user confirms, never into the design project.
/// Every field is checked the way it will be written: one non-Latin character switches the
/// whole disc to Japanese MS-JIS, which many players show garbled, and characters MS-JIS
/// cannot hold become "？".
/// </summary>
public sealed partial class CdExtraCdTextModel : ObservableObject
{
    internal const string OriginTags = "标签";
    internal const string OriginFileName = "文件名";
    internal const string OriginDesign = "设计页";
    internal const string OriginPackage = "刻录包";
    internal const string OriginManual = "手动";

    private readonly ProjectCdText? _design;
    private readonly IReadOnlyDictionary<string, CdTextTrack> _designTracks;
    private readonly Func<string, AudioTags> _readTags;
    private readonly Dictionary<string, AudioTags> _tags = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    public CdExtraCdTextModel(ProjectCdText? design, IReadOnlyDictionary<string, CdTextTrack>? designTracks,
        Func<string, AudioTags>? readTags = null)
    {
        _design = design;
        _designTracks = designTracks ?? new Dictionary<string, CdTextTrack>();
        _readTags = readTags ?? ReadTags;
    }

    public ObservableCollection<CdExtraTextRow> Rows { get; } = [];

    [ObservableProperty]
    private string _albumTitle = string.Empty;

    [ObservableProperty]
    private string _albumPerformer = string.Empty;

    [ObservableProperty]
    private CdExtraTextSource _source = CdExtraTextSource.Window;

    /// <summary>True once anything was typed, looked up or translated since the text was last
    /// filled from a source; switching source then asks before overwriting.</summary>
    [ObservableProperty]
    private bool _isEdited;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbumTitleIssue))]
    private string? _albumTitleIssue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbumPerformerIssue))]
    private string? _albumPerformerIssue;

    public bool HasAlbumTitleIssue => AlbumTitleIssue is not null;

    public bool HasAlbumPerformerIssue => AlbumPerformerIssue is not null;

    /// <summary>Fields that will not show correctly on most players.</summary>
    [ObservableProperty]
    private int _issueCount;

    /// <summary>Raised after any change; the window drops a prepared package.</summary>
    public event Action? Changed;

    public bool HasDesignText => _design is { } design && (!design.IsEmpty || _designTracks.Count > 0)
        || _designTracks.Values.Any(track => !string.IsNullOrWhiteSpace(track.Title) || !string.IsNullOrWhiteSpace(track.Performer));

    partial void OnAlbumTitleChanged(string value) => Edited();

    partial void OnAlbumPerformerChanged(string value) => Edited();

    /// <summary>Matches the rows to the window's music list: a kept file keeps its text (even
    /// when moved), a new file is filled from the current source, a removed file's row goes.</summary>
    public void SyncTracks(IReadOnlyList<string> music)
    {
        List<CdExtraTextRow> old = [.. Rows];
        List<CdExtraTextRow> rows = [];
        foreach (string path in music)
        {
            CdExtraTextRow? kept = old.FirstOrDefault(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase));
            if (kept is not null)
            {
                old.Remove(kept);
                rows.Add(kept);
                continue;
            }
            CdExtraTextRow row = new(path, Edited);
            FillRow(row, Source);
            rows.Add(row);
        }
        bool wasEmpty = Rows.Count == 0;
        Rows.Clear();
        for (int index = 0; index < rows.Count; index++)
        {
            rows[index].Number = index + 1;
            Rows.Add(rows[index]);
        }
        // The first music fills empty album fields from its tags (or the design page).
        if (wasEmpty && rows.Count > 0 && !IsEdited) FillAlbum(Source);
        Refresh();
    }

    /// <summary>Refills every field from <paramref name="source"/>; the caller has asked the
    /// user first when <see cref="IsEdited"/> was set.</summary>
    public void FillFrom(CdExtraTextSource source)
    {
        _loading = true;
        Source = source;
        FillAlbum(source);
        foreach (CdExtraTextRow row in Rows) FillRow(row, source);
        IsEdited = false;
        _loading = false;
        Refresh();
    }

    /// <summary>The text an opened burn package carries.</summary>
    internal void LoadPackage(CdExtraPackageCdText? text)
    {
        _loading = true;
        AlbumTitle = text?.AlbumTitle ?? string.Empty;
        AlbumPerformer = text?.AlbumPerformer ?? string.Empty;
        for (int index = 0; index < Rows.Count; index++)
        {
            CdTextTrack? track = text is not null && index < text.Tracks.Count ? text.Tracks[index] : null;
            if (track is null) FillRow(Rows[index], Source);
            else Rows[index].Set(track.Title, track.Performer, OriginPackage);
        }
        IsEdited = text is not null;
        _loading = false;
        Refresh();
    }

    /// <summary>Puts confirmed online text in place. <paramref name="tracks"/> pairs a row
    /// index with the text to use; a null field keeps what the row has.</summary>
    public void ApplyLookup(string? albumTitle, string? albumPerformer,
        IEnumerable<(int Row, string? Title, string? Performer)> tracks, string origin)
    {
        _loading = true;
        if (albumTitle is not null) AlbumTitle = albumTitle;
        if (albumPerformer is not null) AlbumPerformer = albumPerformer;
        foreach (var (index, title, performer) in tracks)
        {
            if (index < 0 || index >= Rows.Count) continue;
            CdExtraTextRow row = Rows[index];
            row.Set(title ?? row.Title, performer ?? row.Performer, origin);
        }
        _loading = false;
        Edited();
    }

    /// <summary>One line per field that will not show correctly, for the check before the
    /// package is prepared.</summary>
    public IReadOnlyList<string> IssueLines()
    {
        List<string> lines = [];
        void Add(string label, string text, string? issue)
        {
            if (issue is not null) lines.Add($"{label}“{text.Trim()}”：{issue}");
        }
        Add("专辑名", AlbumTitle, AlbumTitleIssue);
        Add("专辑艺术家", AlbumPerformer, AlbumPerformerIssue);
        foreach (CdExtraTextRow row in Rows)
        {
            Add($"第 {row.NumberText} 首曲名", row.Title, row.TitleIssue);
            Add($"第 {row.NumberText} 首艺术家", row.Performer, row.PerformerIssue);
        }
        return lines;
    }

    /// <summary>What a field will look like on the disc, or null when it is plain Latin.</summary>
    internal static string? Issue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || CdTextPacks.IsLatin1Text(text.Trim())) return null;
        IReadOnlyList<string> missing = CdTextPacks.MsJisUnrepresentable(text.Trim());
        return missing.Count > 0
            ? $"“{string.Join(" ", missing.Take(8))}”无法写入，将显示为“？”"
            : "含非拉丁字母：整张盘将改用日文 MS-JIS，许多播放器会显示乱码";
    }

    /// <summary>The package's CD-TEXT: a blank title becomes the file name, as before.</summary>
    internal CdExtraPackageCdText Collect()
        => new(Clean(AlbumTitle), Clean(AlbumPerformer), Rows.Select(row => new CdTextTrack(
            Clean(row.Title) ?? System.IO.Path.GetFileNameWithoutExtension(row.Path), Clean(row.Performer))).ToList());

    internal static string Clip(string text)
        => text.Length <= ProjectCdText.MaximumFieldLength ? text : text[..ProjectCdText.MaximumFieldLength];

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void FillAlbum(CdExtraTextSource source)
    {
        bool loading = _loading;
        _loading = true;
        string? title = null, performer = null;
        if (source == CdExtraTextSource.Design)
        {
            title = Clean(_design?.AlbumTitle);
            performer = Clean(_design?.AlbumPerformer);
        }
        foreach (CdExtraTextRow row in Rows)
        {
            AudioTags tags = Tags(row.Path);
            title ??= tags.Album;
            performer ??= tags.AlbumArtist ?? tags.Artist;
        }
        AlbumTitle = title ?? string.Empty;
        AlbumPerformer = performer ?? string.Empty;
        _loading = loading;
    }

    private void FillRow(CdExtraTextRow row, CdExtraTextSource source)
    {
        AudioTags tags = Tags(row.Path);
        if (source == CdExtraTextSource.Design && _designTracks.TryGetValue(row.Path, out CdTextTrack? design)
            && (Clean(design.Title) is not null || Clean(design.Performer) is not null))
        {
            row.Set(Clean(design.Title) ?? tags.Title ?? System.IO.Path.GetFileNameWithoutExtension(row.Path),
                Clean(design.Performer) ?? tags.Artist, OriginDesign);
            return;
        }
        row.Set(tags.Title ?? System.IO.Path.GetFileNameWithoutExtension(row.Path), tags.Artist,
            tags.Title is null ? OriginFileName : OriginTags);
    }

    private AudioTags Tags(string path)
    {
        if (_tags.TryGetValue(path, out AudioTags? tags)) return tags;
        tags = _readTags(path);
        _tags[path] = tags;
        return tags;
    }

    private static AudioTags ReadTags(string path)
    {
        try { return AudioTagReader.Read(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return AudioTags.None; }
    }

    private void Edited()
    {
        if (_loading) return;
        IsEdited = true;
        Refresh();
    }

    /// <summary>The encoding the disc will carry and any character it cannot hold, as on the
    /// design page.</summary>
    private void Refresh()
    {
        HasError = false;
        WarningText = string.Empty;
        AlbumTitleIssue = Issue(AlbumTitle);
        AlbumPerformerIssue = Issue(AlbumPerformer);
        int issues = (AlbumTitleIssue is null ? 0 : 1) + (AlbumPerformerIssue is null ? 0 : 1);
        foreach (CdExtraTextRow row in Rows)
        {
            string title = string.IsNullOrWhiteSpace(row.Title) ? System.IO.Path.GetFileNameWithoutExtension(row.Path) : row.Title;
            row.TitleIssue = Issue(title);
            row.PerformerIssue = Issue(row.Performer);
            issues += (row.TitleIssue is null ? 0 : 1) + (row.PerformerIssue is null ? 0 : 1);
        }
        IssueCount = issues;
        if (Rows.Count == 0)
        {
            StatusText = "添加音乐后，这里逐曲列出将写入的文字，可直接修改。";
        }
        else
        {
            try
            {
                CdTextEncoding encoding = CdTextPacks.Encode(Collect().ToAlbum());
                string code = encoding.CharacterCode == CdTextCharacterCode.MsJis ? "日文 MS-JIS" : "ISO-8859-1";
                StatusText = $"{code} · {encoding.PackCount} 个数据包（上限 {CdTextPacks.MaximumTextPacks + 3}）· 刻录时需要光驱支持 SAO";
                if (issues > 0)
                {
                    WarningText = $"{issues} 个字段（已标出）含非拉丁字母：整张盘将用日文 MS-JIS 编码，Windows 媒体播放器、VLC 等可能显示乱码"
                        + (encoding.Unrepresentable.Count > 0
                            ? "；其中 " + string.Join(" ", encoding.Unrepresentable.Distinct().Take(12)) + " 无法写入，将显示为“？”"
                            : string.Empty)
                        + "。可用“在线查找专辑信息”换成官方英文或罗马字名称，或直接修改。";
                }
            }
            catch (CdTextTooLongException exception)
            {
                HasError = true;
                StatusText = $"文字太长：需要 {exception.Packs} 个数据包，一张盘最多 {CdTextPacks.MaximumTextPacks} 个。请缩短标题。";
            }
        }
        Changed?.Invoke();
    }
}
