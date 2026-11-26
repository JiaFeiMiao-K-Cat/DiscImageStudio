using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MahApps.Metro.IconPacks;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio;

/// <summary>
/// Online album lookup for the CD-Extra window. Every search shows what will be sent and
/// where before anything leaves the computer. Picking a result reads its tracks and lists
/// them beside the window's current text; only the ticked lines are filled in, and only into
/// the CD-Extra window.
/// </summary>
public sealed partial class AlbumLookupWindow : TransitionWindow
{
    private readonly CdExtraCdTextModel _model;
    private readonly MusicBrainzClient _musicBrainz = new();
    private readonly AppleMusicClient _apple = new();
    private CancellationTokenSource? _request;
    private AlbumDetails? _details;
    private AppleStore _searchedStore;

    /// <summary>Test seam: answers the "send this search?" question.</summary>
    internal Func<string, bool>? Confirm { get; set; }

    public AlbumLookupWindow(CdExtraCdTextModel model)
    {
        _model = model;
        InitializeComponent();
        AlbumBox.Text = model.AlbumTitle;
        ArtistBox.Text = model.AlbumPerformer;
        StoreBox.ItemsSource = Enum.GetValues<AppleStore>().Select(store => new StoreChoice(store)).ToArray();
        StoreBox.DisplayMemberPath = nameof(StoreChoice.Name);
        StoreBox.SelectedIndex = 0;
        MusicBrainzChoice.Checked += (_, _) => UpdateSourceHint();
        AppleChoice.Checked += (_, _) => UpdateSourceHint();
        UpdateSourceHint();
        SearchButton.Click += async (_, _) => await SearchAsync();
        ResultList.SelectionChanged += async (_, _) => await ReadSelectedAsync();
        PerformerCheck.Checked += (_, _) => UpdateApply();
        PerformerCheck.Unchecked += (_, _) => UpdateApply();
        ApplyButton.Click += (_, _) => ApplySelection();
        PreviewKeyDown += (_, e) => { if (NetworkConfirm.HandleKeyDown(e.Key, Keyboard.Modifiers)) e.Handled = true; };
        Closed += (_, _) =>
        {
            _request?.Cancel();
            NetworkConfirm.CancelPending();
        };
        StatusText.Text = $"窗口中有 {model.Rows.Count} 首。查到的文字只填入 CD Extra 窗口，不改动设计页。";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCorners.Apply(this);
        WindowCorners.EnableTransitions(new WindowInteropHelper(this).Handle);
    }

    private AlbumSource SelectedSource => AppleChoice.IsChecked == true ? AlbumSource.Apple : AlbumSource.MusicBrainz;

    private AppleStore SelectedStore => (StoreBox.SelectedItem as StoreChoice)?.Store ?? AppleStore.HongKong;

    private void UpdateSourceHint()
        => SourceHint.Text = SelectedSource == AlbumSource.MusicBrainz
            ? "MusicBrainz 是开放的音乐数据库：日文专辑常有罗马字或英文曲目表，华语艺术家常有英文名；中文曲名通常只有原文。"
            : "Apple Music 公开目录：返回所选区域商店的原文，不翻译曲名。中国大陆区不提供专辑与曲目数据，因此未列出。";

    /// <summary>Sends nothing unless the user agrees to this exact search.</summary>
    private async Task SearchAsync()
    {
        string album = AlbumBox.Text.Trim(), artist = ArtistBox.Text.Trim();
        if (album.Length == 0 && artist.Length == 0)
        {
            StatusText.Text = "请填写专辑名或艺术家。";
            return;
        }
        AlbumSource source = SelectedSource;
        AppleStore store = SelectedStore;
        string host = source == AlbumSource.MusicBrainz ? "MusicBrainz（musicbrainz.org）" : $"Apple（itunes.apple.com，{AppleMusicClient.StoreName(store)}）";
        string question = $"将把以下文字发送到 {host}：\n\n专辑名：{(album.Length > 0 ? album : "（空）")}\n艺术家：{(artist.Length > 0 ? artist : "（空）")}\n\n不会发送文件、路径或其他信息。";
        SearchButton.IsEnabled = false;
        bool agreed;
        try
        {
            agreed = Confirm is not null
                ? Confirm(question)
                : await NetworkConfirm.ShowPrimaryAsync("联网搜索专辑", question, "搜索", PackIconLucideKind.Globe);
        }
        finally
        {
            SearchButton.IsEnabled = _request is null;
        }
        if (!agreed) return;

        CancellationToken token = Begin("正在搜索…");
        ResultList.ItemsSource = null;
        ClearComparison();
        try
        {
            IReadOnlyList<AlbumCandidate> results = source == AlbumSource.MusicBrainz
                ? await _musicBrainz.SearchAsync(album, artist, token)
                : await _apple.SearchAsync(album, artist, store, token);
            if (token.IsCancellationRequested) return;
            _searchedStore = store;
            ResultList.ItemsSource = results.Select(result => new ResultRow(result)).ToArray();
            ResultHint.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultHint.Text = source == AlbumSource.MusicBrainz
                ? "没有找到。可改写关键词，或换用 Apple Music。"
                : "没有找到。可改写关键词，或换个区域。";
            StatusText.Text = results.Count == 0 ? "没有找到匹配的专辑。" : $"找到 {results.Count} 个结果。点选一个查看曲目。";
        }
        catch (AlbumLookupException exception)
        {
            StatusText.Text = exception.Message + (source == AlbumSource.MusicBrainz && exception.Retryable ? " 也可换用 Apple Music。" : string.Empty);
        }
        catch (OperationCanceledException) { }
        finally { End(token); }
    }

    private async Task ReadSelectedAsync()
    {
        if (ResultList.SelectedItem is not ResultRow row) return;
        CancellationToken token = Begin("正在读取曲目…");
        ClearComparison();
        try
        {
            AlbumDetails details = row.Candidate.Source == AlbumSource.MusicBrainz
                ? await _musicBrainz.ReadAsync(row.Candidate, token)
                : await _apple.ReadAsync(row.Candidate, _searchedStore, token);
            if (token.IsCancellationRequested) return;
            ShowComparison(details);
        }
        catch (AlbumLookupException exception) { StatusText.Text = exception.Message; }
        catch (OperationCanceledException) { }
        finally { End(token); }
    }

    internal void ShowComparison(AlbumDetails details)
    {
        _details = details;
        List<CompareRow> rows =
        [
            new(CompareKind.AlbumTitle, -1, "专辑名", _model.AlbumTitle, details.Title, null),
            new(CompareKind.AlbumPerformer, -1, "专辑艺术家", _model.AlbumPerformer, details.Artist, null),
        ];
        int count = Math.Min(details.Tracks.Count, _model.Rows.Count);
        for (int index = 0; index < count; index++)
        {
            CdExtraTextRow current = _model.Rows[index];
            AlbumTrackInfo found = details.Tracks[index];
            rows.Add(new(CompareKind.Track, index, "第 " + current.NumberText + " 首",
                Join(current.Title, current.Performer), found.Title, found.Artist));
        }
        foreach (CompareRow row in rows)
        {
            row.Apply = !string.Equals(row.Current, row.Display, StringComparison.Ordinal);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CompareRow.Apply)) UpdateApply(); };
        }
        CompareList.ItemsSource = rows;
        CompareTitle.Text = $"{details.Title} — {details.Artist}";
        string counts = details.Tracks.Count == _model.Rows.Count
            ? $"{details.Tracks.Count} 首，与窗口一致。"
            : $"专辑 {details.Tracks.Count} 首，窗口 {_model.Rows.Count} 首：按顺序对应前 {count} 首，请核对。";
        CompareNote.Text = (details.Note is null ? string.Empty : details.Note + "。") + counts;
        PerformerCheck.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateApply();
    }

    private void UpdateApply()
    {
        bool performers = PerformerCheck.IsChecked == true;
        if (CompareList.ItemsSource is IEnumerable<CompareRow> rows)
        {
            foreach (CompareRow row in rows) row.ShowPerformer = performers;
            ApplyButton.IsEnabled = _request is null && rows.Any(row => row.Apply);
        }
        else ApplyButton.IsEnabled = false;
    }

    private void ApplySelection()
    {
        if (ApplyChosen()) DialogResult = true;
    }

    internal IReadOnlyList<CompareRow> ComparisonRows => CompareList.ItemsSource as IReadOnlyList<CompareRow> ?? [];

    /// <summary>Fills the ticked lines into the window's table; false when nothing was ticked.</summary>
    internal bool ApplyChosen()
    {
        if (_details is null || CompareList.ItemsSource is not IEnumerable<CompareRow> rows) return false;
        CompareRow[] chosen = rows.Where(row => row.Apply).ToArray();
        if (chosen.Length == 0) return false;
        bool performers = PerformerCheck.IsChecked == true;
        string origin = _details.Source == AlbumSource.MusicBrainz ? "MusicBrainz" : "Apple";
        _model.ApplyLookup(
            chosen.FirstOrDefault(row => row.Kind == CompareKind.AlbumTitle)?.Proposed is { } title ? CdExtraCdTextModel.Clip(title) : null,
            chosen.FirstOrDefault(row => row.Kind == CompareKind.AlbumPerformer)?.Proposed is { } performer ? CdExtraCdTextModel.Clip(performer) : null,
            chosen.Where(row => row.Kind == CompareKind.Track).Select(row => (row.Row, (string?)CdExtraCdTextModel.Clip(row.Proposed),
                performers && row.ProposedPerformer is { } artist ? CdExtraCdTextModel.Clip(artist) : null)),
            origin);
        return true;
    }

    private void ClearComparison()
    {
        _details = null;
        CompareList.ItemsSource = null;
        CompareTitle.Text = "对照";
        CompareNote.Text = "左侧点选一个结果后，这里逐项列出当前文字与查到的文字。";
        PerformerCheck.Visibility = Visibility.Collapsed;
        ApplyButton.IsEnabled = false;
    }

    private CancellationToken Begin(string status)
    {
        _request?.Cancel();
        _request = new CancellationTokenSource();
        SearchButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        StatusText.Text = status;
        return _request.Token;
    }

    private void End(CancellationToken token)
    {
        if (_request is null || _request.Token != token) return;
        _request.Dispose();
        _request = null;
        SearchButton.IsEnabled = true;
        UpdateApply();
    }

    private static string Join(string title, string? performer)
        => string.IsNullOrWhiteSpace(performer) ? title : $"{title} · {performer}";

    private sealed record StoreChoice(AppleStore Store)
    {
        public string Name => AppleMusicClient.StoreName(Store);
    }

    private sealed record ResultRow(AlbumCandidate Candidate)
    {
        public string Title => Candidate.Title;

        public string Detail => string.Join(" · ", new[]
        {
            Candidate.Artist, Candidate.Year, Candidate.TrackCount is int count ? $"{count} 首" : null, Candidate.Note,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    internal enum CompareKind { AlbumTitle, AlbumPerformer, Track }

    /// <summary>One line of the comparison: what the window has and what was found.</summary>
    internal sealed partial class CompareRow(CompareKind kind, int row, string label, string current, string proposed, string? proposedPerformer)
        : ObservableObject
    {
        public CompareKind Kind { get; } = kind;
        public int Row { get; } = row;
        public string Label { get; } = label;
        public string Current { get; } = current;
        public string Proposed { get; } = proposed;
        public string? ProposedPerformer { get; } = proposedPerformer;

        [ObservableProperty]
        private bool _apply;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Display))]
        private bool _showPerformer = true;

        /// <summary>The found text as it would be filled in.</summary>
        public string Display => Kind == CompareKind.Track && ShowPerformer ? Join(Proposed, ProposedPerformer) : Proposed;
    }
}
