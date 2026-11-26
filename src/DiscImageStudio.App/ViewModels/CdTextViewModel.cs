using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

/// <summary>One editable CD-TEXT row: a CD-Extra music track.</summary>
public sealed partial class CdTextTrackRowViewModel : ObservableObject
{
    private readonly Action<CdTextTrackRowViewModel> _changed;
    private bool _loading;

    internal CdTextTrackRowViewModel(int number, string path, CdTextTrack text, Action<CdTextTrackRowViewModel> changed)
    {
        Number = number;
        Path = path;
        _changed = changed;
        _loading = true;
        Title = text.Title ?? string.Empty;
        Performer = text.Performer ?? string.Empty;
        _loading = false;
    }

    public int Number { get; }

    public string NumberText => Number.ToString("00", CultureInfo.InvariantCulture);

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _performer = string.Empty;

    partial void OnTitleChanged(string value) { if (!_loading) _changed(this); }

    partial void OnPerformerChanged(string value) { if (!_loading) _changed(this); }
}

/// <summary>
/// The design page's CD-TEXT module. Off by default; switched on, the album title and
/// performer and the text of every track (CD-Extra music, or the single picture track of a
/// picture-only audio CD) go into the lead-in of the audio session. Text is prefilled from
/// the source files' tags and can be edited. The status line shows the encoding the disc will
/// carry (ISO-8859-1, or MS-JIS for Japanese) and every character that neither can hold.
/// </summary>
public sealed partial class CdTextViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly DesignViewModel _design;
    private readonly Dictionary<string, AudioTags> _tagCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncing;

    internal CdTextViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design)
    {
        _shell = shell;
        _state = state;
        _design = design;
        FillFromTagsCommand = new RelayCommand(() => FillFromTags(overwrite: true), CanFillFromTags);
        _state.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(DiscParametersState.CdMusicTracks):
                    if (_state.CdText.Enabled) FillFromTags(overwrite: false);
                    RebuildRows();
                    break;
                case nameof(DiscParametersState.CdTrackTexts):
                    if (!_syncing) RebuildRows();
                    break;
                case nameof(DiscParametersState.CdText):
                    if (!_syncing) LoadFields();
                    break;
                case nameof(DiscParametersState.CdExtraEnabled):
                case nameof(DiscParametersState.HasCdFiles):
                    OnPropertyChanged(nameof(IsMusicMode));
                    OnPropertyChanged(nameof(IsPictureTrackMode));
                    OnPropertyChanged(nameof(IsAvailable));
                    RefreshStatus();
                    break;
            }
        };
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.IsBusy) or nameof(ShellViewModel.IsProjectOpen))
                FillFromTagsCommand.NotifyCanExecuteChanged();
        };
        LoadFields();
        RebuildRows();
    }

    public DiscParametersState State => _state;

    public ObservableCollection<CdTextTrackRowViewModel> Tracks { get; } = [];

    public RelayCommand FillFromTagsCommand { get; }

    /// <summary>CD-TEXT needs an audio session: a CD-Extra disc or a picture-only audio CD.</summary>
    public bool IsAvailable => _state.CdExtraEnabled || !_state.HasCdFiles;

    public bool IsMusicMode => _state.CdExtraEnabled;

    public bool IsPictureTrackMode => !_state.CdExtraEnabled && !_state.HasCdFiles;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _albumTitle = string.Empty;

    [ObservableProperty]
    private string _albumPerformer = string.Empty;

    [ObservableProperty]
    private string _pictureTrackTitle = string.Empty;

    [ObservableProperty]
    private string _pictureTrackPerformer = string.Empty;

    /// <summary>"ISO-8859-1 · 14 个数据包" or "日文 MS-JIS · …".</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _hasWarning;

    [ObservableProperty]
    private string _warningText = string.Empty;

    /// <summary>The disc cannot hold this text at all (more than one block).</summary>
    [ObservableProperty]
    private bool _hasError;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_syncing) return;
        Store();
        if (value) FillFromTags(overwrite: false);
    }

    partial void OnAlbumTitleChanged(string value) => Store();

    partial void OnAlbumPerformerChanged(string value) => Store();

    partial void OnPictureTrackTitleChanged(string value) => Store();

    partial void OnPictureTrackPerformerChanged(string value) => Store();

    internal void Relocalize() => RefreshStatus();

    private bool CanFillFromTags() => !_shell.IsBusy && _shell.IsProjectOpen && _design.IsCd && _state.CdMusicTracks.Count > 0;

    private void LoadFields()
    {
        _syncing = true;
        try
        {
            ProjectCdText text = _state.CdText;
            IsEnabled = text.Enabled;
            AlbumTitle = text.AlbumTitle ?? string.Empty;
            AlbumPerformer = text.AlbumPerformer ?? string.Empty;
            PictureTrackTitle = text.PictureTrackTitle ?? string.Empty;
            PictureTrackPerformer = text.PictureTrackPerformer ?? string.Empty;
        }
        finally { _syncing = false; }
        RefreshStatus();
    }

    private void Store()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            _state.CdText = new ProjectCdText(IsEnabled, Limit(AlbumTitle), Limit(AlbumPerformer),
                Limit(PictureTrackTitle), Limit(PictureTrackPerformer));
        }
        finally { _syncing = false; }
        RefreshStatus();
    }

    private void RebuildRows()
    {
        Tracks.Clear();
        int number = 1;
        foreach (string path in _state.CdMusicTracks)
        {
            CdTextTrack text = _state.CdTrackTexts.TryGetValue(path, out CdTextTrack? stored) ? stored : new CdTextTrack();
            Tracks.Add(new CdTextTrackRowViewModel(number++, path, text, OnRowChanged));
        }
        FillFromTagsCommand.NotifyCanExecuteChanged();
        RefreshStatus();
    }

    private void OnRowChanged(CdTextTrackRowViewModel row)
    {
        Dictionary<string, CdTextTrack> texts = new(_state.CdTrackTexts, StringComparer.OrdinalIgnoreCase)
        {
            [row.Path] = new CdTextTrack(Limit(row.Title), Limit(row.Performer)),
        };
        _syncing = true;
        try { _state.CdTrackTexts = texts; }
        finally { _syncing = false; }
        RefreshStatus();
    }

    /// <summary>Prefills track (and, when empty, album) text from the music files' tags.
    /// Without <paramref name="overwrite"/>, only fields that are still empty are filled.</summary>
    internal void FillFromTags(bool overwrite)
    {
        Dictionary<string, CdTextTrack> texts = new(_state.CdTrackTexts, StringComparer.OrdinalIgnoreCase);
        string? album = null, albumArtist = null;
        bool changed = false;
        foreach (string path in _state.CdMusicTracks)
        {
            AudioTags tags = ReadTags(path);
            album ??= tags.Album;
            albumArtist ??= tags.AlbumArtist ?? tags.Artist;
            texts.TryGetValue(path, out CdTextTrack? current);
            string? title = overwrite || string.IsNullOrWhiteSpace(current?.Title)
                ? tags.Title ?? current?.Title
                : current!.Title;
            string? performer = overwrite || string.IsNullOrWhiteSpace(current?.Performer)
                ? tags.Artist ?? current?.Performer
                : current!.Performer;
            if (string.IsNullOrWhiteSpace(title) && current is null)
                title = Path.GetFileNameWithoutExtension(path);
            CdTextTrack next = new(Limit(title ?? string.Empty), Limit(performer ?? string.Empty));
            if (current != next)
            {
                texts[path] = next;
                changed = true;
            }
        }

        _syncing = true;
        try
        {
            if (changed) _state.CdTrackTexts = texts;
        }
        finally { _syncing = false; }
        if (changed) RebuildRows();
        if (album is not null && (overwrite || AlbumTitle.Length == 0)) AlbumTitle = album;
        if (albumArtist is not null && (overwrite || AlbumPerformer.Length == 0)) AlbumPerformer = albumArtist;
    }

    private AudioTags ReadTags(string path)
    {
        if (_tagCache.TryGetValue(path, out AudioTags? cached)) return cached;
        AudioTags tags;
        try { tags = File.Exists(path) ? AudioTagReader.Read(path) : AudioTags.None; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { tags = AudioTags.None; }
        _tagCache[path] = tags;
        return tags;
    }

    private void RefreshStatus()
    {
        HasError = false;
        HasWarning = false;
        WarningText = string.Empty;
        if (!IsEnabled || !IsAvailable)
        {
            StatusText = IsAvailable ? L.T("CD-TEXT 已关闭：光盘不带文字信息，刻录方式与以前相同。")
                : L.T("数据 CD 没有音频导入区，不能写入 CD-TEXT。");
            return;
        }

        CdTextAlbum? album = _state.EffectiveCdText();
        if (album is null)
        {
            StatusText = L.T("尚未填写任何文字；留空时不写入 CD-TEXT。");
            return;
        }

        try
        {
            CdTextEncoding encoding = CdTextPacks.Encode(album);
            string code = encoding.CharacterCode == CdTextCharacterCode.MsJis ? L.T("日文 MS-JIS") : "ISO-8859-1";
            StatusText = L.F($"{code} · {encoding.PackCount} 个数据包（上限 {CdTextPacks.MaximumTextPacks + 3}）· 刻录时需要光驱支持 SAO");
            if (encoding.Unrepresentable.Count > 0)
            {
                HasWarning = true;
                string chars = string.Join(" ", encoding.Unrepresentable.Distinct().Take(12));
                WarningText = L.F($"以下字符无法用 CD-TEXT 表示，将写成“？”：{chars}");
            }
        }
        catch (CdTextTooLongException exception)
        {
            HasError = true;
            StatusText = L.F($"文字太长：需要 {exception.Packs} 个数据包，一张盘最多 {CdTextPacks.MaximumTextPacks} 个。请缩短标题。");
        }
    }

    private static string? Limit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        return trimmed.Length <= ProjectCdText.MaximumFieldLength ? trimmed : trimmed[..ProjectCdText.MaximumFieldLength];
    }
}
