using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using Microsoft.Win32;

namespace DiscImageStudio;

/// <summary>A separate audio-first workflow. Opening or using it does not change
/// the project's ordinary data-disc placement or its calibrated preset.</summary>
public sealed partial class CdExtraWindow : TransitionWindow
{
    private readonly string? _artwork;
    private readonly CdDiscParameters _geometry;
    private readonly ToneProfile _tone;
    private readonly ObservableCollection<string> _music = [];
    private CdExtraPackage? _package;
    private string? _directory;
    private readonly ViewModels.BurnMilestonesViewModel _milestones = new();
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _speedQuery;
    private bool _refreshingDevices;
    private bool _refreshMediaAfterTask;
    private bool _closed;
    private StreamWriter? _historyLog;
    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscImageStudio", "CDExtra", "Logs");

    public CdExtraWindow(string? artwork, CdDiscParameters geometry, ToneProfile tone)
    {
        _artwork = artwork;
        _geometry = geometry;
        _tone = tone;
        InitializeComponent();
        MilestoneStrip.DataContext = _milestones;
        MilestoneGate.SetBinding(VisibilityGate.IsOpenProperty,
            new System.Windows.Data.Binding(nameof(ViewModels.BurnMilestonesViewModel.IsVisible)) { Source = _milestones });

        _list.ItemsSource = _music;
        _summary.Text = artwork is null
            ? "可打开已有刻录包。准备新包前，请在设计页导入图案。"
            : "使用当前工程的图案与影调。";
        AddMusicButton.Click += (_, _) => AddMusic();
        SortMusicButton.Click += (_, _) => SortMusicByFileName();
        _music.CollectionChanged += (_, _) => SortMusicButton.IsEnabled = _music.Count > 1;
        MoveUpButton.Click += (_, _) => Move(-1);
        MoveDownButton.Click += (_, _) => Move(1);
        RemoveMusicButton.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            _music.RemoveAt(_list.SelectedIndex);
            InvalidatePackage();
        };
        PreparePackageButton.Click += async (_, _) => await PrepareAsync();
        OpenPackageButton.Click += async (_, _) => await LoadAsync();
        RefreshDevicesButton.Click += async (_, _) => await RefreshDevicesAsync();
        _devices.SelectionChanged += async (_, _) => await RefreshWriteSpeedsAsync();
        _speed.SelectionChanged += (_, _) => UpdateMediaActions();
        ResumePictureButton.Click += async (_, _) => await ResumePictureAsync();
        CheckMediaButton.Click += async (_, _) => await CheckMediaAsync();
        _burn.Click += async (_, _) => await BurnAsync();
        _cancel.Click += (_, _) =>
        {
            _operation?.Cancel();
            _cancel.IsEnabled = false;
            Append("已请求取消，等待当前设备操作返回。");
        };
        HistoryButton.Click += (_, _) => OpenHistory();
        Loaded += async (_, _) => await RefreshDevicesAsync();
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCorners.Apply(this);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        WindowCorners.EnableTransitions(handle);
        HwndSource.FromHwnd(handle)?.AddHook(OnWindowMessage);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _speedQuery?.Cancel();
        base.OnClosed(e);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmDeviceChange = 0x0219;
        const int DeviceArrival = 0x8000;
        const int DeviceRemoval = 0x8004;
        const int VolumeDevice = 2;
        const int MediaFlag = 1;
        // DEV_BROADCAST_VOLUME: three DWORD header fields, the drive mask, then the flags.
        if (message == WmDeviceChange && wParam.ToInt64() is DeviceArrival or DeviceRemoval
            && lParam != IntPtr.Zero && Marshal.ReadInt32(lParam) >= 18
            && Marshal.ReadInt32(lParam, 4) == VolumeDevice && (Marshal.ReadInt16(lParam, 16) & MediaFlag) != 0)
        {
            int driveMask = Marshal.ReadInt32(lParam, 12);
            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (_devices.SelectedItem is OpticalBurnDevice device
                    && (device.VolumePaths.Count == 0 || device.VolumePaths.Any(path =>
                        path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])
                        && (driveMask & (1 << (char.ToUpperInvariant(path[0]) - 'A'))) != 0)))
                {
                    await RefreshWriteSpeedsAsync();
                }
            });
        }
        return IntPtr.Zero;
    }

    private void AddMusic()
    {
        OpenFileDialog dialog = new() { Multiselect = true, CheckFileExists = true,
            Filter = "音乐文件|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.wma|所有文件|*.*", Title = "添加音乐（按列表顺序刻录）" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (string path in dialog.FileNames) _music.Add(path);
        InvalidatePackage();
    }
    private void SortMusicByFileName()
    {
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture,
            CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        var sorted = _music.Select((path, index) => (Path: path, Index: index))
            .OrderBy(track => Path.GetFileName(track.Path), comparer)
            .ToArray();
        if (sorted.Select(track => track.Index).SequenceEqual(Enumerable.Range(0, sorted.Length))) return;

        int originalSelection = _list.SelectedIndex;
        _music.Clear();
        foreach (var track in sorted) _music.Add(track.Path);
        _list.SelectedIndex = Array.FindIndex(sorted, track => track.Index == originalSelection);
        InvalidatePackage();
    }

    private void Move(int delta)
    {
        int from = _list.SelectedIndex, to = from + delta;
        if (from < 0 || to < 0 || to >= _music.Count) return;
        _music.Move(from, to); _list.SelectedIndex = to; InvalidatePackage();
    }
    private void InvalidatePackage()
    {
        _package = null; _directory = null; _burn.IsEnabled = false;
        _summary.Text = "音乐列表已变化，请重新准备刻录包。";
    }

    private async Task RefreshDevicesAsync()
    {
        if (_refreshingDevices || _operation is not null) return;
        _refreshingDevices = true;
        RefreshDevicesButton.IsEnabled = false;
        _devices.IsEnabled = false;
        _speedQuery?.Cancel();
        _speed.ItemsSource = null;
        _speedHint.Text = "正在读取刻录机…";
        UpdateMediaActions();
        try
        {
            string? selectedId = (_devices.SelectedItem as OpticalBurnDevice)?.Id;
            var devices = await new WindowsImapiBurner().GetDevicesAsync();
            if (_closed) return;
            _devices.ItemsSource = devices;
            _devices.SelectedItem = devices.FirstOrDefault(device => device.Id == selectedId) ?? devices.FirstOrDefault();
            if (devices.Count == 0) Append("没有检测到刻录机。仍可准备刻录包。");
        }
        catch (Exception e) { Append("检测刻录机失败：" + e.Message); }
        finally
        {
            _refreshingDevices = false;
            RefreshDevicesButton.IsEnabled = true;
            _devices.IsEnabled = true;
        }
        await RefreshWriteSpeedsAsync();
    }

    private async Task RefreshWriteSpeedsAsync()
    {
        if (_closed || _refreshingDevices) return;
        if (_operation is not null)
        {
            _refreshMediaAfterTask = true;
            return;
        }

        _speedQuery?.Cancel();
        _speed.ItemsSource = null;
        _speedHint.ToolTip = null;
        UpdateMediaActions();
        if (_devices.SelectedItem is not OpticalBurnDevice device)
        {
            _speedHint.Text = "请选择刻录机。";
            return;
        }

        using CancellationTokenSource query = new();
        _speedQuery = query;
        _speedHint.Text = "正在读取当前盘片的可用倍速…";
        try
        {
            var speeds = await new WindowsImapiBurner().GetSupportedWriteSpeedsAsync(device.Id, OpticalBurnMediaKind.CdAudio, query.Token);
            if (query.IsCancellationRequested) return;
            _speed.ItemsSource = speeds.Select(speed => new WriteSpeedChoice(speed)).ToArray();
            _speed.SelectedIndex = speeds.Count == 0 ? -1 : 0;
            _speedHint.Text = speeds.Count == 0
                ? "刻录机未报告可用倍速。请确认空白 CD-R 已就绪，然后刷新。"
                : string.Empty;
            UpdateMediaActions();
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (query.IsCancellationRequested) return;
            _speedHint.Text = e is InvalidOperationException ? e.Message : "无法读取倍速。请确认空白 CD-R 已就绪，然后刷新。";
            _speedHint.ToolTip = e.Message;
        }
        finally
        {
            if (_speedQuery == query) _speedQuery = null;
        }
    }

    private void UpdateMediaActions()
    {
        bool speedSelected = _speed.SelectedItem is WriteSpeedChoice;
        bool idle = _operation is null && !_refreshingDevices;
        _speed.IsEnabled = idle && _speed.HasItems;
        ResumePictureButton.IsEnabled = idle && _devices.SelectedItem is OpticalBurnDevice;
        CheckMediaButton.IsEnabled = idle && speedSelected;
        _burn.IsEnabled = idle && speedSelected && _package is not null;
    }

    private sealed record WriteSpeedChoice(OpticalWriteSpeed Speed)
    {
        public string DisplayName => $"{Speed.GetMultiplier(OpticalBurnMediaKind.CdAudio):0.##}×"
            + (Speed.RotationTypeIsPureCav ? " (CAV)" : string.Empty);
    }
    private async Task PrepareAsync()
    {
        if (_artwork is null) { Append("请先在设计页导入图案。"); return; }
        OpenFolderDialog dialog = new() { Title = "选择刻录包的保存目录（将在其中新建子目录）" };
        if (dialog.ShowDialog(this) != true) return;
        string[] music = _music.ToArray();
        string destination = Path.Combine(dialog.FolderName, "CDExtra-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        await RunAsync("准备刻录包", async (token, status) =>
        {
            IProgress<string> progress = new Progress<string>(Append);
            var package = await StaWorker.RunAsync(() => CdExtraPackage.Create(destination, _artwork, music,
                _geometry, _tone, progress.Report, token, status));
            SetPackage(destination, package);
            Append("刻录包准备完成：" + destination);
        });
    }
    private async Task LoadAsync()
    {
        OpenFileDialog dialog = new() { Filter = "CD Extra 刻录包|package.cdextra.json", Title = "打开刻录包" };
        if (dialog.ShowDialog(this) != true) return;
        string directory = Path.GetDirectoryName(dialog.FileName)!;
        await RunAsync("打开刻录包", async (token, _) =>
        {
            var package = await Task.Run(() => { token.ThrowIfCancellationRequested(); return CdExtraPackage.Load(directory); }, token);
            _music.Clear(); foreach (var t in package.Tracks) _music.Add(Path.Combine(directory, t.File));
            SetPackage(directory, package);
            Append("已打开刻录包：" + directory);
        });
    }
    private void SetPackage(string directory, CdExtraPackage package)
    {
        _package = package; _directory = directory;
        double minutes = package.Tracks.Sum(t => t.Sectors) / 75.0 / 60;
        double radius = package.Geometry.RadiusFromByte(package.EstimatedStart * 2352);
        _summary.Text = $"已准备 {package.Tracks.Count} 首 · 音乐 {minutes:F2} 分钟\n"
            + $"估计绘图起始半径约 {radius:F2} mm（刻录时按实际会话起点重算）。\n刻录包：{directory}";
    }
    private async Task BurnAsync()
    {
        if (_package is null || _directory is null) return;
        if (_devices.SelectedItem is not OpticalBurnDevice device) { Append("请选择刻录机。"); return; }
        if (_speed.SelectedItem is not WriteSpeedChoice choice) return;
        if (MessageBox.Show(this, $"即将写入 {device.DisplayName}：\n{_package.Tracks.Count} 首音乐＋外圈图案数据，最后封盘。\n\n"
            + "需要空白 CD-R。开始后取消或断电可能使盘片报废。是否开始？", "确认 CD Extra 刻录",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        string directory = _directory;
        await RunAsync("刻录 CD Extra", async (token, status) =>
        {
            _refreshMediaAfterTask = true;
            Append($"刻录机：{device.DisplayName}，请求 {choice.DisplayName}。刻录包：{directory}");
            CdExtraPackage package = await Task.Run(() => CdExtraPackage.Load(directory), token);
            IProgress<string> progress = new Progress<string>(Append);
            var request = await StaWorker.RunAsync(() => package.CreateBurnRequest(
                directory, device.Id, choice.Speed, token, progress.Report));
            string? lastPhase = null;
            _milestones.Begin(testWrite: request.TestWrite);
            var deviceProgress = new Progress<OpticalBurnProgress>(value =>
            {
                if (_operation?.Token != token) return;
                _milestones.Observe(value);
                if (value.Stage is "diagnostic" or "reconnect" || lastPhase != value.Stage) Append(value.Message);
                lastPhase = value.Stage;
                status.Report(new(value.Message, value.TotalBytes > 0 ? value.Fraction : null));
            });
            CdExtraBurnResult result;
            try
            {
                result = await new WindowsImapiBurner().BurnCdExtraAsync(request, deviceProgress, token);
            }
            catch (Exception e)
            {
                _milestones.Fail(e.Message, cancelled: e is OperationCanceledException);
                throw;
            }

            _milestones.Succeed(L.F($"图案轨 {result.VerifiedPictureSectors:N0} 个扇区读回一致"));
            Append($"已封盘，图案轨 {result.VerifiedPictureSectors:N0} 个扇区全部读回校验通过。");
        });
    }

    private async Task ResumePictureAsync()
    {
        if (_operation is not null || _devices.SelectedItem is not OpticalBurnDevice device) return;
        string? path = new FileDialogService(() => this).PickCdExtraCheckpoint(_directory);
        if (path is null || MessageBox.Show(this, $"{device.DisplayName}\n{path}\n\n确认原盘与已写数据后续写缺失部分。已封盘时只读校验。",
            "图案断点续写与校验", MessageBoxButton.OKCancel, MessageBoxImage.Information,
            MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await RunAsync("图案断点续写与校验", async (token, status) =>
        {
            _refreshMediaAfterTask = true;
            _milestones.Begin(resume: true);
            var progress = new Progress<OpticalBurnProgress>(p =>
            {
                if (_operation?.Token != token) return;
                _milestones.Observe(p);
                if (p.Stage is "diagnostic" or "reconnect" or "finalize" or "complete") Append(p.Message);
                status.Report(new(p.Message, p.TotalBytes > 0 ? p.Fraction : null));
            });
            long sectors;
            try
            {
                sectors = await new WindowsImapiBurner().ResumeCdExtraPictureAsync(device.Id, path, progress, token);
            }
            catch (Exception e)
            {
                _milestones.Fail(e.Message, cancelled: e is OperationCanceledException);
                throw;
            }

            _milestones.Succeed(L.F($"图案轨全部 {sectors:N0} 个扇区读回一致"));
            Append($"图案轨 {sectors:N0} 个扇区全部读回校验通过。");
        });
    }

    private async Task CheckMediaAsync()
    {
        if (_devices.SelectedItem is not OpticalBurnDevice device) { Append("请选择刻录机。"); return; }
        if (_speed.SelectedItem is not WriteSpeedChoice choice) return;
        await RunAsync("检查空白盘", async (token, _) =>
        {
            Append($"刻录机：{device.DisplayName}，请求 {choice.DisplayName}。");
            OpticalMediaCapacity medium = await new WindowsImapiBurner().GetMediaCapacityAsync(
                device.Id, OpticalBurnMediaKind.CdAudio, token);
            if (medium.WritableSectors <= 0)
                throw new IOException("当前盘片没有可用的空白 CD 容量。");
            Append($"设备报告可写 {medium.WritableSectors:N0} 扇区。");
            Append("空白盘容量检查通过。开始刻录时将核对音频和图案的写入格式。");
        });
    }

    private async Task RunAsync(string task, Func<CancellationToken, IProgress<CdExtraProgress>, Task> action)
    {
        if (_operation is not null) return;
        using CancellationTokenSource cancellation = new();
        _operation = cancellation; _editing.IsEnabled = false; _burn.IsEnabled = false; _cancel.IsEnabled = true;
        _progress.Value = 0;
        SetProgress(new($"正在{task}…"));
        IProgress<CdExtraProgress> status = new Progress<CdExtraProgress>(update =>
        {
            if (_operation == cancellation) SetProgress(update);
        });
        string result = "任务完成";
        try
        {
            StartHistory(task);
            string? version = typeof(CdExtraWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Append($"开始任务：{task}（{version}）");
            await action(cancellation.Token, status);
        }
        catch (OperationCanceledException) { result = "任务已取消"; }
        catch (Exception e) { result = "任务未完成"; Append(result + "：" + e); }
        finally
        {
            // Let already queued worker messages reach the log before closing its file.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Append(result);
            EndHistory();
            _operation = null;
            _progress.IsIndeterminate = false;
            if (result == "任务完成") _progress.Value = 100;
            _status.Text = result;
            _editing.IsEnabled = true;
            UpdateMediaActions();
            _cancel.IsEnabled = false;
            if (_refreshMediaAfterTask)
            {
                _refreshMediaAfterTask = false;
                _ = RefreshWriteSpeedsAsync();
            }
        }
    }

    private void SetProgress(CdExtraProgress update)
    {
        _progress.IsIndeterminate = update.Fraction is null;
        if (update.Fraction is double fraction)
        {
            _progress.Value = Math.Clamp(fraction, 0, 1) * 100;
            _status.Text = $"{update.Message} · {_progress.Value:F0}%";
        }
        else _status.Text = update.Message;
    }

    private void StartHistory(string task)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string path = Path.Combine(LogDirectory, $"{stamp}_{task}_{Guid.NewGuid():N}.log");
            _historyLog = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Append("无法保存历史日志：" + e.Message);
        }
    }

    private void EndHistory()
    {
        StreamWriter? log = _historyLog;
        _historyLog = null;
        try { log?.Dispose(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Append("无法保存历史日志：" + e.Message);
        }
    }

    private void OpenHistory()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            OpenFileDialog dialog = new() { InitialDirectory = LogDirectory, Filter = "CD Extra 日志|*.log", Title = "选择历史日志" };
            if (dialog.ShowDialog(this) != true) return;
            using FileStream file = new(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(file);
            var box = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), FontSize = 12 };
            box.SetResourceReference(StyleProperty, "BareTextBox");
            box.SetResourceReference(FontFamilyProperty, "MonoFontFamily");
            string fileName = Path.GetFileName(dialog.FileName);
            Grid content = new();
            content.RowDefinitions.Add(new() { Height = new GridLength(32) });
            content.RowDefinitions.Add(new());
            content.Children.Add(new TitleBar { ProjectTitle = fileName, UseSystemWindowCommands = true });
            Grid.SetRow(box, 1);
            content.Children.Add(box);
            var view = new TransitionWindow
            {
                Owner = this, Title = fileName, Width = 760, Height = 520, MinWidth = 420, MinHeight = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                UseLayoutRounding = true, SnapsToDevicePixels = true, Content = content,
            };
            WindowChrome.SetWindowChrome(view, new WindowChrome
            {
                CaptionHeight = 32, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(7),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
            });
            view.SourceInitialized += (_, _) =>
            {
                WindowCorners.Apply(view);
                WindowCorners.EnableTransitions(new WindowInteropHelper(view).Handle);
            };
            view.SetResourceReference(FontFamilyProperty, "AppFontFamily");
            view.SetResourceReference(BackgroundProperty, "BackgroundBrush");
            view.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            view.ShowDialog();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Append("无法打开历史日志：" + e.Message);
        }
    }

    private void Append(string text)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}";
        _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
        try { _historyLog?.WriteLine(line); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            EndHistory();
            Append("无法继续保存历史日志：" + e.Message);
        }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return;
        e.Cancel = true;
        Append("当前任务尚未结束。请先取消并等待设备操作返回，再关闭窗口。");
    }
}
