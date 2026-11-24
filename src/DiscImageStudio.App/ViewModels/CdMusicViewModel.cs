using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Cd;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>One music track row of the CD-Extra list.</summary>
public sealed partial class CdMusicTrackRowViewModel : ObservableObject
{
    internal CdMusicTrackRowViewModel(int number, string path)
    {
        Number = number;
        Path = path;
    }

    public int Number { get; }

    public string NumberText => Number.ToString("00", CultureInfo.InvariantCulture);

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Duration, a padding note, or the reason the file cannot be used.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private bool _hasError;
}

/// <summary>
/// The design page's CD-Extra module: the switch between a picture-only CD and an Enhanced
/// CD, the music list of session 1, and the share of the disc that music, the session gap
/// and the picture take. The numbers come from the design guide's plan, so the canvas rings,
/// the previews and this summary always describe the same layout.
/// </summary>
public sealed partial class CdMusicViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly DesignViewModel _design;

    internal CdMusicViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design)
    {
        _shell = shell;
        _state = state;
        _design = design;
        AddMusicCommand = new RelayCommand(AddMusic, CanEdit);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => CanEdit() && SelectedTrack is not null);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1),
            () => CanEdit() && SelectedTrack is { Number: > 1 });
        MoveDownCommand = new RelayCommand(() => MoveSelected(1),
            () => CanEdit() && SelectedTrack is { } track && track.Number < Tracks.Count);
        ClearCommand = new RelayCommand(Clear, () => CanEdit() && Tracks.Count > 0);
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DiscParametersState.CdMusicTracks))
                RebuildRows();
            else if (e.PropertyName is nameof(DiscParametersState.CdExtraEnabled)
                     or nameof(DiscParametersState.CdMusicAtOuterRing))
                RefreshSummary();
        };
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.IsBusy) or nameof(ShellViewModel.IsProjectOpen))
                NotifyCommands();
        };
        _design.CdExtraPlanChanged += RefreshSummary;
        // CanEdit depends on the disc family; opening a CD project after a DVD/BD one must
        // enable the buttons even when the (empty) music list itself does not change.
        _design.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DesignViewModel.IsCd))
                NotifyCommands();
        };
        RebuildRows();
    }

    public DiscParametersState State => _state;

    public ObservableCollection<CdMusicTrackRowViewModel> Tracks { get; } = [];

    [ObservableProperty]
    private CdMusicTrackRowViewModel? _selectedTrack;

    public RelayCommand AddMusicCommand { get; }

    public RelayCommand RemoveSelectedCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand ClearCommand { get; }

    public bool HasTracks => Tracks.Count > 0;

    /// <summary>"3 首 · 共 12:34".</summary>
    [ObservableProperty]
    private string _trackSummary = string.Empty;

    [ObservableProperty]
    private bool _hasLayout;

    [ObservableProperty]
    private string _musicShareText = string.Empty;

    [ObservableProperty]
    private string _gapShareText = string.Empty;

    [ObservableProperty]
    private string _pictureShareText = string.Empty;

    [ObservableProperty]
    private string _session2Text = string.Empty;

    /// <summary>Star widths of the three segments of the share bar.</summary>
    [ObservableProperty]
    private GridLength _musicShareWidth = new(0);

    [ObservableProperty]
    private GridLength _gapShareWidth = new(0);

    [ObservableProperty]
    private GridLength _pictureShareWidth = new(1, GridUnitType.Star);

    /// <summary>The music segment behind the picture when the music is on the outer rings;
    /// the bar reads from the disc centre outwards.</summary>
    [ObservableProperty]
    private GridLength _outerMusicShareWidth = new(0);

    /// <summary>Only a CD-Extra disc has a session gap.</summary>
    [ObservableProperty]
    private bool _hasSessionGap;

    [ObservableProperty]
    private bool _hasProblem;

    [ObservableProperty]
    private string _problemText = string.Empty;

    /// <summary>Short legend text for the canvas corner: "音乐 41% · 图片 57%".</summary>
    [ObservableProperty]
    private string _canvasLegendText = string.Empty;

    partial void OnSelectedTrackChanged(CdMusicTrackRowViewModel? value) => NotifyCommands();

    /// <summary>Appends WAV files after the current tracks; the picker and page drops call
    /// this. A picker or Explorer drag lists the focused file first, so each batch is added
    /// in natural file-name order, as the CLI orders a folder; the list can be reordered
    /// afterwards. Paths already in the list are skipped. Files are validated by the guide.</summary>
    public void AddTracks(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        List<string> tracks = [.. _state.CdMusicTracks];
        IEnumerable<string> batch = paths
            .Where(raw => !string.IsNullOrWhiteSpace(raw))
            .Select(raw => System.IO.Path.GetFullPath(raw.Trim()))
            .Order(Comparer<string>.Create(CdMusicFile.CompareTrackFileNames));
        foreach (string path in batch)
        {
            if (tracks.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            if (tracks.Count >= CdExtraLayout.MaximumAudioTracks) break;
            tracks.Add(path);
        }

        if (tracks.Count == _state.CdMusicTracks.Count) return;
        _state.CdMusicTracks = tracks.AsReadOnly();
        // Adding music is the explicit request for the CD-Extra layout.
        _state.CdExtraEnabled = true;
    }

    public void RemoveTrack(CdMusicTrackRowViewModel? row)
    {
        if (row is null) return;
        List<string> tracks = [.. _state.CdMusicTracks];
        int index = row.Number - 1;
        if (index < 0 || index >= tracks.Count) return;
        tracks.RemoveAt(index);
        _state.CdMusicTracks = tracks.AsReadOnly();
        SelectedTrack = Tracks.Count == 0 ? null : Tracks[Math.Min(index, Tracks.Count - 1)];
    }

    public void MoveTrack(CdMusicTrackRowViewModel? row, int offset)
    {
        if (row is null) return;
        List<string> tracks = [.. _state.CdMusicTracks];
        int index = row.Number - 1;
        int target = index + offset;
        if (index < 0 || index >= tracks.Count || target < 0 || target >= tracks.Count) return;
        (tracks[index], tracks[target]) = (tracks[target], tracks[index]);
        _state.CdMusicTracks = tracks.AsReadOnly();
        SelectedTrack = Tracks[target];
    }

    internal void Relocalize()
    {
        foreach (CdMusicTrackRowViewModel row in Tracks)
            row.Detail = string.Empty;
        RefreshSummary();
    }

    private bool CanEdit() => !_shell.IsBusy && _shell.IsProjectOpen && _design.IsCd;

    private void AddMusic()
    {
        string[]? paths = _shell.Dialogs?.PickAudioFiles();
        if (paths is { Length: > 0 }) AddTracks(paths);
    }

    private void RemoveSelected() => RemoveTrack(SelectedTrack);

    private void MoveSelected(int offset) => MoveTrack(SelectedTrack, offset);

    private void Clear()
    {
        _state.CdMusicTracks = [];
        SelectedTrack = null;
    }

    private void RebuildRows()
    {
        string? selectedPath = SelectedTrack?.Path;
        Tracks.Clear();
        int number = 1;
        foreach (string path in _state.CdMusicTracks)
            Tracks.Add(new CdMusicTrackRowViewModel(number++, path));
        SelectedTrack = selectedPath is null ? null
            : Tracks.FirstOrDefault(row => string.Equals(row.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasTracks));
        NotifyCommands();
        RefreshSummary();
    }

    private void NotifyCommands()
    {
        AddMusicCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Rebuilds the cached localized summary from the latest guide plan. Rows whose
    /// file has not been read yet show a pending note until the next pass.</summary>
    private void RefreshSummary()
    {
        CdExtraPlan? plan = _design.CdExtraPlan;
        Dictionary<string, CdExtraTrackProbe> probes = new(StringComparer.OrdinalIgnoreCase);
        if (plan is not null)
        {
            foreach (CdExtraTrackProbe probe in plan.Tracks)
                probes[probe.Path] = probe;
        }

        foreach (CdMusicTrackRowViewModel row in Tracks)
        {
            if (!probes.TryGetValue(row.Path, out CdExtraTrackProbe? probe))
            {
                row.Detail = L.T("正在读取…");
                row.HasError = false;
                continue;
            }

            if (probe.Info is { } info)
            {
                string duration = FormatDuration(info.Duration);
                row.Detail = info.PaddedToMinimumLength
                    ? L.F($"{duration}（不足 4 秒，补静音）")
                    : duration;
                row.HasError = false;
            }
            else
            {
                row.Detail = probe.Error ?? L.T("无法读取");
                row.HasError = true;
            }
        }

        TimeSpan total = TimeSpan.FromTicks(Tracks.Sum(row =>
            probes.TryGetValue(row.Path, out CdExtraTrackProbe? probe) && probe.Info is { } info
                ? info.Duration.Ticks : 0));
        TrackSummary = Tracks.Count == 0
            ? L.T("尚未添加音乐")
            : L.F($"{Tracks.Count} 首 · 共 {FormatDuration(total)}");

        // The music layout is shown whenever it is known, also when only the disc files do
        // not fit or cannot be read, so the shares stay visible next to the problem.
        CdExtraLayout? layout = plan?.Layout;
        HasLayout = layout is not null;
        HasSessionGap = layout is { MusicAtOuterRing: false };
        if (layout is { MusicAtOuterRing: true })
        {
            MusicShareText = L.F($"音乐区 {layout.MusicInnerRadiusMm:F1}–{layout.MusicOuterRadiusMm:F1} mm · 约占 {layout.MusicAreaFraction:P0}");
            GapShareText = string.Empty;
            PictureShareText = L.F($"图片区 {layout.PictureInnerRadiusMm:F1}–{layout.PictureOuterRadiusMm:F1} mm · 约占 {layout.PictureAreaFraction:P0}");
            Session2Text = L.T("图片写在第 1 轨的预间隙里，CD 机从音乐开始播放；整盘一次写入，图片位置不依赖光驱。");
            MusicShareWidth = new GridLength(0);
            GapShareWidth = new GridLength(0);
            PictureShareWidth = new GridLength(layout.PictureAreaFraction, GridUnitType.Star);
            OuterMusicShareWidth = new GridLength(layout.MusicAreaFraction, GridUnitType.Star);
            CanvasLegendText = L.F($"图片 {layout.PictureAreaFraction:P0} · 音乐 {layout.MusicAreaFraction:P0}");
        }
        else if (layout is not null)
        {
            OuterMusicShareWidth = new GridLength(0);
            MusicShareText = L.F($"音乐区 {layout.MusicInnerRadiusMm:F1}–{layout.MusicOuterRadiusMm:F1} mm · 约占 {layout.MusicAreaFraction:P0}");
            GapShareText = L.F($"区段间隔 {layout.MusicOuterRadiusMm:F1}–{layout.PictureInnerRadiusMm:F1} mm · 约占 {layout.SessionGapAreaFraction:P1}（导出区与导入区，不能绘图）");
            PictureShareText = L.F($"图片区 {layout.PictureInnerRadiusMm:F1}–{layout.PictureOuterRadiusMm:F1} mm · 约占 {layout.PictureAreaFraction:P0}");
            Session2Text = L.F($"第 2 区段预计从 LBA {layout.Session2StartLba:N0} 开始；刻录时按光驱报告的实际地址生成图片。");
            MusicShareWidth = new GridLength(layout.MusicAreaFraction, GridUnitType.Star);
            GapShareWidth = new GridLength(layout.SessionGapAreaFraction, GridUnitType.Star);
            PictureShareWidth = new GridLength(layout.PictureAreaFraction, GridUnitType.Star);
            CanvasLegendText = L.F($"音乐 {layout.MusicAreaFraction:P0} · 区段间隔 {layout.SessionGapAreaFraction:P1} · 图片 {layout.PictureAreaFraction:P0}");
        }
        else
        {
            MusicShareText = GapShareText = PictureShareText = Session2Text = CanvasLegendText = string.Empty;
            MusicShareWidth = new GridLength(0);
            GapShareWidth = new GridLength(0);
            OuterMusicShareWidth = new GridLength(0);
            PictureShareWidth = new GridLength(1, GridUnitType.Star);
        }

        string? problem = !_state.CdExtraEnabled ? null : Tracks.Count == 0
            ? L.T("添加 WAV 音乐后，这里会显示音乐区与图片区各占多少盘面。")
            : plan is null or { Problem: CdExtraPlanProblem.None or CdExtraPlanProblem.NoMusic }
                ? null
                : CdExtraPlanner.DescribeProblem(plan);
        HasProblem = problem is not null;
        ProblemText = problem ?? string.Empty;
    }

    internal static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
