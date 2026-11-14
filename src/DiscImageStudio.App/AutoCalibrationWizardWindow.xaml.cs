using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DiscImageStudio.AutoCalibration;
using DiscImageStudio.Burning;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio;

public partial class AutoCalibrationWizardWindow : Window
{
    private const uint MaximumCdDiscSectors = 500_000;

    private static readonly Regex PercentPattern = new(@"(?<percent>\d+(?:\.\d+)?)%", RegexOptions.Compiled);
    private static readonly Regex StepProgressPattern =
        new(@"\[标定\]\s*第\s*(?<current>\d+)\s*/\s*(?<total>\d+)\s*阶", RegexOptions.Compiled);

    private readonly ProjectDialogResult _project;
    private readonly AutoCalibrationService _runner;
    private readonly IOpticalDiscBurner _burner;
    private readonly FileDialogService _dialogs;
    private readonly bool _debug;
    private readonly string _mediaType;
    private readonly OpticalBurnMediaKind _mediaKind;
    private readonly List<DiscDriveInfo> _readDrives = [];
    private readonly List<OpticalBurnDevice> _burnDrives = [];
    private readonly DispatcherTimer _elapsedTimer;
    private readonly StringBuilder _burnLog = new();
    private readonly StringBuilder _readLog = new();
    private readonly StringBuilder _blankMediaLog = new();
    private IReadOnlyList<ImportedDiscratcherPreset> _importedPresets = [];
    private string? _importedPresetPath;
    private CancellationTokenSource? _operationCancellation;
    private Stopwatch? _elapsed;
    private bool _isRunning;
    private bool _updatingDriveLists;
    private double _progressFraction;
    private int _currentRung;
    private int _totalRungs;

    public AutoCalibrationWizardResult? Result { get; private set; }

    public IReadOnlyList<string> ExportedFiles { get; private set; } = [];

    internal AutoCalibrationWizardWindow(
        ProjectDialogResult project,
        AppSettings settings,
        IOpticalDiscBurner burner,
        FileDialogService dialogs)
    {
        _project = project;
        _debug = settings.AutoWizardDebugMode;
        _runner = new AutoCalibrationService();
        _burner = burner;
        _dialogs = dialogs;
        _mediaType = project.Family == OpticalDiscFamily.CompactDisc ? "cd" : "dvd";
        _mediaKind = project.Family == OpticalDiscFamily.CompactDisc
            ? OpticalBurnMediaKind.CdData
            : OpticalBurnMediaKind.DvdData;
        InitializeComponent();

        if (_mediaType == "cd")
        {
            MaxTargetsText.Text = "36";
            RepsText.Text = "32";
        }

        MediaTypeText.Text = _mediaType == "cd"
            ? "当前工程类型：CD。镜像大小取决于空白盘总扇区数；也可在下方手动输入。"
            : "当前工程类型：DVD。使用标准单层 DVD 标记镜像；刻录前仍会检查实际空白盘。";

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => RefreshElapsedLabels();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        GoTo(0);
        if (!_runner.IsAvailable)
        {
            ShowPrepareError("内置自动标定引擎不可用。请检查应用安装是否完整。");
            return;
        }

        if (_debug)
        {
            BurnDriveCombo.ItemsSource = new[] { "调试模拟刻录机" };
            BurnDriveCombo.SelectedIndex = 0;
            BurnDriveStatusText.Text = "调试模式：流式刻录步骤将模拟运行，不会访问真实光驱或生成大型镜像。";
            await RefreshReadDrivesAsync();
        }
        else
        {
            await RefreshBurnDrivesAsync();
            await RefreshReadDrivesAsync();
        }
    }

    private async Task RefreshBurnDrivesAsync()
    {
        RefreshBurnDrivesButton.IsEnabled = false;
        BurnDriveStatusText.Text = "正在检测 IMAPI2 刻录机…";
        try
        {
            IReadOnlyList<OpticalBurnDevice> drives = await _burner.GetDevicesAsync();
            _burnDrives.Clear();
            _burnDrives.AddRange(drives);
            BurnDriveCombo.ItemsSource = drives.Select(d => d.DisplayName).ToList();
            BurnDriveCombo.SelectedIndex = drives.Count > 0 ? 0 : -1;
            BurnDriveStatusText.Text = drives.Count == 0
                ? "未检测到可用的 IMAPI2 刻录机。仍可导出镜像并使用外部软件刻录。"
                : $"检测到 {drives.Count} 台刻录机。请先插入空白盘，再开始流式刻录。";
        }
        catch (Exception exception)
        {
            BurnDriveCombo.ItemsSource = null;
            _burnDrives.Clear();
            BurnDriveStatusText.Text = $"无法读取刻录机列表：{exception.Message}";
        }
        finally
        {
            RefreshBurnDrivesButton.IsEnabled = true;
        }
    }

    private async Task RefreshReadDrivesAsync()
    {
        RefreshReadDrivesButton.IsEnabled = false;
        RefreshBlankMediaButton.IsEnabled = false;
        ReadDriveStatusText.Text = "正在检测可读取的光驱…";
        BlankMediaInfoText.Text = "正在重读光驱与介质参数…";
        AppendBlankMediaLog("正在重新查询光驱、介质类型、空白状态和容量。");
        string? selectedReadId = SelectedDriveId(ReadDriveCombo);
        string? selectedParameterId = SelectedDriveId(ParameterDriveCombo);
        try
        {
            IReadOnlyList<DiscDriveInfo> drives = await _runner.ListDrivesAsync(
                _debug, null, cancellationToken: default, mediaType: _mediaType);
            _readDrives.Clear();
            _readDrives.AddRange(drives);
            _updatingDriveLists = true;
            ReadDriveCombo.ItemsSource = drives.Select(DescribeDrive).ToList();
            ReadDriveCombo.SelectedIndex = FindDriveIndex(selectedReadId);
            if (ReadDriveCombo.SelectedIndex < 0 && drives.Count > 0)
            {
                ReadDriveCombo.SelectedIndex = 0;
            }

            ParameterDriveCombo.ItemsSource = drives.Select(DescribeDrive).ToList();
            ParameterDriveCombo.SelectedIndex = FindDriveIndex(selectedParameterId);
            if (ParameterDriveCombo.SelectedIndex < 0)
            {
                ParameterDriveCombo.SelectedIndex = FindPreferredParameterDriveIndex();
            }
            _updatingDriveLists = false;

            ReadDriveStatusText.Text = drives.Count == 0
                ? "未检测到可读取的 CD/DVD 光驱。"
                : _debug
                    ? "调试模式：模拟光驱已就绪。"
                    : $"检测到 {drives.Count} 台光驱；插入标记盘后继续。";
            UpdateBlankMediaInfo();
            foreach (DiscDriveInfo drive in drives)
            {
                AppendBlankMediaLog(
                    $"{drive.Id}: {(drive.MediaPresent ? drive.MediaType : "无盘")}，" +
                    $"容量 {drive.TotalSectors:N0} 扇区，" +
                    $"{(drive.IsBlank ? "空白" : "非空白/状态未知")}，" +
                    $"{(drive.Supported ? "支持" : "不支持")}。");
            }
        }
        catch (Exception exception)
        {
            _updatingDriveLists = true;
            ReadDriveCombo.ItemsSource = null;
            ParameterDriveCombo.ItemsSource = null;
            _readDrives.Clear();
            _updatingDriveLists = false;
            ReadDriveStatusText.Text = $"无法读取光驱列表：{exception.Message}";
            BlankMediaInfoText.Text = $"读取光盘参数失败：{exception.Message}";
            AppendBlankMediaLog($"读取光盘参数失败：{exception.Message}");
        }
        finally
        {
            RefreshReadDrivesButton.IsEnabled = true;
            RefreshBlankMediaButton.IsEnabled = true;
        }
    }

    private string? SelectedDriveId(System.Windows.Controls.ComboBox comboBox) =>
        comboBox.SelectedIndex >= 0 && comboBox.SelectedIndex < _readDrives.Count
            ? _readDrives[comboBox.SelectedIndex].Id
            : null;

    private int FindDriveIndex(string? id) =>
        id is null ? -1 : _readDrives.FindIndex(drive => string.Equals(drive.Id, id, StringComparison.Ordinal));

    private int FindPreferredParameterDriveIndex()
    {
        int blank = _readDrives.FindIndex(drive =>
            drive.MediaPresent && drive.Supported && drive.IsBlank
            && MatchesProjectMedia(drive.MediaType));
        if (blank >= 0)
        {
            return blank;
        }

        int matching = _readDrives.FindIndex(drive =>
            drive.MediaPresent && MatchesProjectMedia(drive.MediaType));
        return matching >= 0 ? matching : _readDrives.Count > 0 ? 0 : -1;
    }

    private void UpdateBlankMediaInfo()
    {
        if (ParameterDriveCombo.SelectedIndex < 0
            || ParameterDriveCombo.SelectedIndex >= _readDrives.Count)
        {
            BlankMediaInfoText.Text = _debug
                ? "调试模拟器不读取实体介质参数；可启用手动参数模式查看流程。"
                : "未检测到可读取光驱。插入空白盘后点击“重读光盘参数”，或启用手动参数模式。";
            return;
        }

        DiscDriveInfo drive = _readDrives[ParameterDriveCombo.SelectedIndex];
        if (drive.Simulated)
        {
            BlankMediaInfoText.Text = "调试模拟器：不访问实体光驱。";
        }
        else if (!drive.MediaPresent)
        {
            BlankMediaInfoText.Text = "所选光驱当前无盘；若要导出镜像，请插入空白盘并重读参数，或改用手动参数模式。";
        }
        else if (!drive.Supported)
        {
            BlankMediaInfoText.Text = $"不支持所选介质「{drive.MediaType}」。请使用 CD 或单层 DVD。";
        }
        else if (!MatchesProjectMedia(drive.MediaType))
        {
            BlankMediaInfoText.Text = $"所选介质「{drive.MediaType}」与当前 {_mediaType.ToUpperInvariant()} 工程不匹配。";
        }
        else if (!drive.IsBlank)
        {
            BlankMediaInfoText.Text =
                $"光驱报告介质容量 {drive.TotalSectors:N0} 扇区，但未确认为空白盘（也可能是设备无法报告空白状态）。" +
                "此参数不能作为空白盘读取结果；请换空白盘重读或手动输入。";
        }
        else if (drive.TotalSectors <= 0)
        {
            BlankMediaInfoText.Text = "光驱已报告空白盘，但没有可用容量数据；请手动输入参数。";
        }
        else
        {
            uint imageSectors;
            try
            {
                imageSectors = CalibrationImageSizing.ResolveForExport(
                    _mediaType, useManualParameters: false, null, drive.TotalSectors);
                BlankMediaInfoText.Text =
                    $"已读取空白 {drive.MediaType}：总容量 {drive.TotalSectors:N0} 扇区；" +
                    $"将生成 {imageSectors:N0} 扇区标记镜像。";
                if (_mediaType == "cd" && ManualMediaParametersCheckBox.IsChecked != true)
                {
                    ManualSectorsText.Text = drive.TotalSectors.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (InvalidOperationException exception)
            {
                BlankMediaInfoText.Text = exception.Message;
            }
        }
    }

    private static string DescribeDrive(DiscDriveInfo drive)
    {
        string model = string.Join(" ", new[] { drive.Vendor, drive.Product, drive.Revision }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (model.Length == 0)
        {
            model = drive.Id;
        }

        return model + (drive.MediaPresent ? $" · {drive.MediaType}" : " · 无盘");
    }

    private void RefreshBurnDrives_Click(object sender, RoutedEventArgs e) => _ = RefreshBurnDrivesAsync();

    private void RefreshReadDrives_Click(object sender, RoutedEventArgs e) => _ = RefreshReadDrivesAsync();

    private void RefreshBlankMedia_Click(object sender, RoutedEventArgs e) => _ = RefreshReadDrivesAsync();

    private void ParameterDriveCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_updatingDriveLists)
        {
            UpdateBlankMediaInfo();
        }
    }

    private void ManualMediaParametersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool manual = ManualMediaParametersCheckBox.IsChecked == true;
        ManualSectorsPanel.Visibility = manual && _mediaType == "cd"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ManualMediaHintText.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ImportDiscratcherPreset_Click(object sender, RoutedEventArgs e)
    {
        string? path = _dialogs.PickOpen(
            "导入 Discratcher 预设或标定记录",
            "Discratcher JSON|*.json|所有文件|*.*");
        if (path is null)
        {
            return;
        }

        _importedPresets = [];
        _importedPresetPath = null;
        ImportedPresetCombo.ItemsSource = null;
        ImportedPresetCombo.Visibility = Visibility.Collapsed;
        UseImportedPresetButton.IsEnabled = false;
        ImportedPresetInfoText.Text = string.Empty;
        try
        {
            IReadOnlyList<ImportedDiscratcherPreset> imported = DiscratcherPresetImporter.Read(path);
            _importedPresets = imported
                .Where(preset => string.Equals(preset.MediaType, _mediaType, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (_importedPresets.Count == 0)
            {
                throw new InvalidOperationException(
                    $"文件中有 {imported.Count} 个预设，但没有与当前 {_mediaType.ToUpperInvariant()} 工程匹配的预设。");
            }

            _importedPresetPath = path;
            ImportErrorText.Visibility = Visibility.Collapsed;
            ImportedPresetCombo.ItemsSource = _importedPresets;
            ImportedPresetCombo.DisplayMemberPath = nameof(ImportedDiscratcherPreset.DisplayName);
            ImportedPresetCombo.SelectedIndex = 0;
            ImportedPresetCombo.Visibility = Visibility.Visible;
            UseImportedPresetButton.IsEnabled = true;
            UpdateImportedPresetInfo();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            UseImportedPresetButton.IsEnabled = false;
            ImportErrorText.Text = $"导入失败：{exception.Message}";
            ImportErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ImportedPresetCombo_SelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateImportedPresetInfo();

    private void UpdateImportedPresetInfo()
    {
        if (ImportedPresetCombo.SelectedItem is not ImportedDiscratcherPreset preset)
        {
            return;
        }

        string info =
            $"已从 {Path.GetFileName(_importedPresetPath ?? string.Empty)} 读取 {_importedPresets.Count} 个匹配预设。\n" +
            $"{preset.MediaType.ToUpperInvariant()} · {preset.Geometry.Sectors:N0} 扇区 · " +
            $"内/外半径 {preset.Geometry.InnerRadiusMm:0.###}/{preset.Geometry.OuterRadiusMm:0.###} mm · " +
            $"Discratcher 通道位长 {preset.ChannelBitLengthNm:0.###} nm";
        if (preset.AlongTrackFrames is { } frames)
        {
            info += $"\n沿轨帧偏移 {frames:0.###}：DiscImageStudio 当前没有对应字段，不会应用该项。";
        }
        if (_mediaType == "cd")
        {
            info += "\nCD 的通道位长与 DiscImageStudio 使用的线速度不是同一参数；导入只映射半径、扇区和螺距。";
        }
        else if (Math.Abs(preset.ChannelBitLengthNm - 133.3) > 0.000001)
        {
            info += "\n此预设的 DVD 通道位长不是 133.3 nm；DiscImageStudio 当前使用标准值，导入不会覆盖该值。";
        }

        ImportedPresetInfoText.Text = info;
    }

    private void UseImportedPreset_Click(object sender, RoutedEventArgs e)
    {
        if (ImportedPresetCombo.SelectedItem is not ImportedDiscratcherPreset preset)
        {
            return;
        }

        try
        {
            IReadOnlyList<DiscImageStudio.Core.Calibration.CalibrationSession> sessions =
                CalibrationSessionExporter.CreateSessions(preset.Geometry);
            Result = new AutoCalibrationWizardResult(preset.Geometry, sessions);
            _progressFraction = 1;
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = 100;
            RunStatusText.Text = "已导入 Discratcher 几何预设；无需插入标记盘即可应用或导出辅助标定记录。";
            ReadCurrentText.Text = "导入完成";
            DoneSummaryText.Text =
                $"来源：{Path.GetFileName(_importedPresetPath ?? string.Empty)} · {preset.DisplayName}\n" +
                $"内半径 {preset.Geometry.InnerRadiusMm:0.###} mm · " +
                $"外半径 {preset.Geometry.OuterRadiusMm:0.###} mm · 扇区 {preset.Geometry.Sectors:N0}\n" +
                $"螺距 c1 {preset.Geometry.PitchLinear:E3} · c2 {preset.Geometry.PitchQuadratic:E3} · " +
                $"c3 {preset.Geometry.PitchCubic:E3} · RMS {preset.Geometry.RmsResidualDeg:0.###}°";
            DoneSummaryText.Visibility = Visibility.Visible;
            RunLogText.Text =
                $"已从 Discratcher 导入：{preset.DisplayName}\n" +
                $"JSON 文件：{_importedPresetPath}\n" +
                $"通道位长：{preset.ChannelBitLengthNm:0.###} nm";
            if (preset.AlongTrackFrames is { } frames)
            {
                RunLogText.Text += $"\n未应用的沿轨帧偏移：{frames:0.###}（DiscImageStudio 没有对应字段）";
            }
            ExportMarkersButton.IsEnabled = true;
            FinishButton.IsEnabled = true;
            GoTo(4);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ImportErrorText.Text = $"无法采用此预设：{exception.Message}";
            ImportErrorText.Visibility = Visibility.Visible;
        }
    }

    private void WelcomeNext_Click(object sender, RoutedEventArgs e) => GoTo(1);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        if (RunPanel.Visibility == Visibility.Visible)
        {
            GoTo(3);
        }
        else if (ReadPreparePanel.Visibility == Visibility.Visible)
        {
            GoTo(1);
        }
        else
        {
            GoTo(0);
        }
    }

    private async void ExportIso_Click(object sender, RoutedEventArgs e)
    {
        if (_debug)
        {
            ShowPrepareError("调试模式不会生成大型镜像。关闭设置页中的自动向导调试选项后即可导出。");
            return;
        }

        if (!TryParseOptions(out _, out uint? manualDiscSectors))
        {
            ShowPrepareError("高级选项或手动介质参数无效：请检查目标点数、重复数、范围和 CD 总扇区数。");
            return;
        }

        if (!TryGetPreparedImageSectors(manualDiscSectors, out uint imageSectors, out string error))
        {
            ShowPrepareError(error);
            return;
        }

        string? path = _dialogs.PickSave(
            "ISO 镜像|*.iso",
            ".iso",
            $"{SafeFileName(_project.Name)}-auto.iso");
        if (path is null)
        {
            return;
        }

        await RunExportIsoAsync(path, imageSectors);
    }

    private async Task RunExportIsoAsync(string path, uint imageSectors)
    {
        if (_isRunning)
        {
            return;
        }

        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.tmp.iso");
        BeginOperation();
        try
        {
            ShowPrepareError(string.Empty);
            _burnLog.Clear();
            BurnLogText.Text = string.Empty;
            BurnDriveStatusText.Text = "正在生成标记盘镜像…";
            await _runner.GenerateIsoAsync(
                temporaryPath,
                _mediaType,
                line => AppendBurnLog(line),
                imageSectors,
                _operationCancellation!.Token);
            File.Move(temporaryPath, path, overwrite: true);
            BurnDriveStatusText.Text = $"镜像已导出：{path}";
        }
        catch (OperationCanceledException) when (_operationCancellation?.IsCancellationRequested == true)
        {
            BurnDriveStatusText.Text = "镜像生成已取消。";
        }
        catch (Exception exception)
        {
            ShowPrepareError($"镜像导出失败：{exception.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ShowPrepareError($"临时镜像清理失败：{exception.Message}");
            }

            EndOperation();
        }
    }

    private void ExternalRead_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        ShowPrepareError(string.Empty);
        GoTo(3);
        _ = RefreshReadDrivesAsync();
    }

    private async void BurnAndCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseOptions(out _, out uint? manualDiscSectors))
        {
            ShowPrepareError("高级选项或手动介质参数无效：请检查目标点数、重复数、范围和 CD 总扇区数。");
            return;
        }

        if (_debug)
        {
            await SimulateBurnAsync();
            return;
        }

        if (BurnDriveCombo.SelectedIndex < 0 || BurnDriveCombo.SelectedIndex >= _burnDrives.Count)
        {
            ShowPrepareError("请选择可用的 IMAPI2 刻录机，或改为导出镜像后使用外部软件刻录。");
            return;
        }

        await BurnIsoAsync(_burnDrives[BurnDriveCombo.SelectedIndex], manualDiscSectors);
    }

    private async Task SimulateBurnAsync()
    {
        if (_isRunning)
        {
            return;
        }

        BeginOperation();
        GoTo(2);
        BurnProgress.IsIndeterminate = false;
        BurnStatusText.Text = "调试模式：模拟生成标记盘并完成流式刻录…";
        try
        {
            for (int step = 1; step <= 20; step++)
            {
                _operationCancellation!.Token.ThrowIfCancellationRequested();
                await Task.Delay(150, _operationCancellation.Token);
                BurnProgress.Value = step * 5;
                _progressFraction = step / 20.0;
                BurnSpeedText.Text = "模拟";
                BurnEstimateText.Text = FormatDuration(TimeSpan.FromSeconds((20 - step) * 0.15));
                AppendBurnLog($"[调试] 刻录进度 {step * 5}%");
            }

            BurnStatusText.Text = "模拟刻录完成。接下来将使用模拟光驱读取测量。";
            await Task.Delay(250, _operationCancellation!.Token);
            GoTo(3);
            await RefreshReadDrivesAsync();
        }
        catch (OperationCanceledException) when (_operationCancellation?.IsCancellationRequested == true)
        {
            BurnStatusText.Text = "模拟流程已取消。";
            GoTo(1);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task BurnIsoAsync(OpticalBurnDevice device, uint? manualDiscSectors)
    {
        if (_isRunning)
        {
            return;
        }

        BeginOperation();
        GoTo(2);
        BurnProgress.IsIndeterminate = true;
        BurnProgress.Value = 0;
        BurnStatusText.Text = "正在检查空白盘并准备标记镜像…";
        BurnLogText.Text = string.Empty;
        _burnLog.Clear();
        string isoPath = Path.Combine(Path.GetTempPath(), $"discimage-auto-{Guid.NewGuid():N}.iso");
        try
        {
            OpticalMediaCapacity capacity = await _burner.GetMediaCapacityAsync(
                device.Id, _mediaKind, _operationCancellation!.Token);
            if (capacity.IsBlank != true)
            {
                throw new InvalidOperationException("当前盘片不是空白盘，请换入新的可写空白盘后重试。");
            }

            uint imageSectors = CalibrationImageSizing.FromDiscCapacity(
                _mediaType, manualDiscSectors ?? capacity.TotalSectors);
            if (capacity.WritableSectors < imageSectors)
            {
                throw new InvalidOperationException(
                    $"当前盘片可写 {capacity.WritableSectors:N0} 扇区，标记镜像需要 {imageSectors:N0} 扇区。");
            }

            BurnStatusText.Text = "正在生成标记盘镜像…";
            await _runner.GenerateIsoAsync(
                isoPath,
                _mediaType,
                AppendBurnLog,
                imageSectors,
                _operationCancellation.Token);

            FileInfo image = new(isoPath);
            long sectorBytes = 2048;
            var request = new OpticalBurnRequest(
                device.Id,
                _mediaKind,
                image.Length,
                (output, token) => CopyIsoToStream(isoPath, output, token));
            if (image.Length != checked((long)imageSectors * sectorBytes))
            {
                throw new IOException("生成的标记镜像大小与请求扇区数不一致。");
            }

            BurnProgress.IsIndeterminate = true;
            BurnStatusText.Text = "正在向刻录机传输标记镜像…";
            Progress<OpticalBurnProgress> burnProgress = new(ReportBurnProgress);
            await _burner.BurnAsync(request, burnProgress, _operationCancellation.Token);
            BurnProgress.IsIndeterminate = false;
            BurnProgress.Value = 100;
            BurnStatusText.Text = "流式刻录完成。请将标记盘放入读取光驱。";
            AppendBurnLog("刻录完成；正在刷新读取光驱列表。");
            await RefreshReadDrivesAsync();
            GoTo(3);
        }
        catch (OperationCanceledException) when (_operationCancellation?.IsCancellationRequested == true)
        {
            BurnStatusText.Text = "刻录已取消。请检查盘片状态后再继续。";
            GoTo(1);
        }
        catch (Exception exception)
        {
            ShowPrepareError($"流式刻录失败：{exception.Message}");
            GoTo(1);
        }
        finally
        {
            try
            {
                if (File.Exists(isoPath))
                {
                    File.Delete(isoPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppendBurnLog($"临时镜像清理失败：{exception.Message}");
            }

            EndOperation();
        }
    }

    private static void CopyIsoToStream(string path, Stream output, CancellationToken cancellationToken)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }

    private void ReportBurnProgress(OpticalBurnProgress value)
    {
        BurnStatusText.Text = value.Message;
        if (value.TotalBytes > 0)
        {
            BurnProgress.IsIndeterminate = false;
            _progressFraction = Math.Clamp(value.Fraction, 0, 1);
            BurnProgress.Value = _progressFraction * 100;
        }

        BurnSpeedText.Text = value.BytesPerSecond is > 0
            ? $"{value.BytesPerSecond.Value / 1_000_000.0:0.00} MB/s"
            : "—";
        RefreshElapsedLabels();
        if (!string.IsNullOrWhiteSpace(value.Message))
        {
            AppendBurnLog(value.Message);
        }
    }

    private void ReadStart_Click(object sender, RoutedEventArgs e) => _ = ReadAndCalibrateAsync();

    private async Task ReadAndCalibrateAsync()
    {
        if (_isRunning || !TryParseOptions(out var options, out _, validateManualMediaParameters: false))
        {
            ShowReadPrepareError("高级选项无效：请检查目标点数、重复数和范围。");
            return;
        }

        string driveId;
        if (_debug)
        {
            driveId = "sim";
        }
        else
        {
            if (ReadDriveCombo.SelectedIndex < 0 || ReadDriveCombo.SelectedIndex >= _readDrives.Count)
            {
                ShowReadPrepareError("请选择可读取的光驱。");
                return;
            }

            DiscDriveInfo drive = _readDrives[ReadDriveCombo.SelectedIndex];
            if (!drive.MediaPresent)
            {
                ShowReadPrepareError("所选光驱当前无盘。请插入已刻录完成的 CD/DVD 标记盘并刷新列表。");
                return;
            }
            if (!MatchesProjectMedia(drive.MediaType))
            {
                ShowReadPrepareError(
                    $"光驱报告当前介质为「{drive.MediaType}」，与当前 {_mediaType.ToUpperInvariant()} 工程不匹配。");
                return;
            }

            driveId = drive.Id;
        }

        BeginOperation();
        GoTo(4);
        RunProgress.IsIndeterminate = true;
        RunProgress.Value = 0;
        RunStatusText.Text = "正在校验标记盘…";
        RunLogText.Text = string.Empty;
        _readLog.Clear();
        DoneSummaryText.Visibility = Visibility.Collapsed;
        ExportMarkersButton.IsEnabled = false;
        FinishButton.IsEnabled = false;
        _progressFraction = 0;
        _currentRung = 0;
        _totalRungs = options.MaxTargets;
        ReadCurrentText.Text = "等待读取";
        try
        {
            if (!_debug)
            {
                DiscDriveInfo? selectedDrive = _readDrives.FirstOrDefault(drive =>
                    string.Equals(drive.Id, driveId, StringComparison.Ordinal));
                AppendReadLog(selectedDrive?.IsBlank == true
                    ? "光驱列表报告介质为空白；仍将直接读取盘上卷标和采样扇区，以实际内容为准。"
                    : "正在直接校验盘上卷标、镜像长度和采样扇区。");
                DiscImageStudio.Services.CalibrationDiscCheck check = await _runner.CheckAsync(
                    driveId, _mediaType, ReportReadLine, _operationCancellation!.Token);
                AppendReadLog($"标记盘校验通过：{check.Sectors:N0} 扇区"
                    + (check.Foreign
                        ? "（标记数据与 Discratcher 镜像不同，将按可读取扇区继续测量）"
                        : "（Discratcher 镜像匹配）"));
            }
            else
            {
                AppendReadLog("调试模式：跳过实体标记盘校验。");
            }

            RunStatusText.Text = "正在读取盘片时序并拟合几何参数…";
            _progressFraction = 0;
            RunProgress.IsIndeterminate = true;
            AutoCalibrationResult geometry = await _runner.MeasureAsync(
                driveId, _mediaType, _debug, options, ReportReadLine, _operationCancellation!.Token);
            Result = new AutoCalibrationWizardResult(
                geometry,
                CalibrationSessionExporter.CreateSessions(geometry));
            _progressFraction = 1;
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = 100;
            RunStatusText.Text = "测量完成。核对结果后可导出辅助标定记录，或直接应用到工程。";
            ReadCurrentText.Text = $"{_currentRung:N0} / {_totalRungs:N0}";
            DoneSummaryText.Text =
                $"内半径 {geometry.InnerRadiusMm:0.###} mm · 外半径 {geometry.OuterRadiusMm:0.###} mm · " +
                $"扇区 {geometry.Sectors:N0}\n" +
                $"螺距 c1 {geometry.PitchLinear:E3} · c2 {geometry.PitchQuadratic:E3} · " +
                $"c3 {geometry.PitchCubic:E3} · RMS {geometry.RmsResidualDeg:0.###}°";
            DoneSummaryText.Visibility = Visibility.Visible;
            ExportMarkersButton.IsEnabled = true;
            FinishButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (_operationCancellation?.IsCancellationRequested == true)
        {
            RunStatusText.Text = "读取已取消。";
            AppendReadLog("操作已取消。");
        }
        catch (Exception exception)
        {
            RunStatusText.Text = "读取或标定失败。";
            AppendReadLog($"错误：{exception.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    private void ReportReadLine(string line)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AppendReadLog(line);
            Match stepProgress = StepProgressPattern.Match(line);
            if (stepProgress.Success
                && int.TryParse(stepProgress.Groups["current"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int current)
                && int.TryParse(stepProgress.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int total)
                && total > 0)
            {
                _currentRung = current;
                _totalRungs = total;
                _progressFraction = Math.Clamp((double)current / total, 0, 1);
                RunProgress.IsIndeterminate = false;
                RunProgress.Value = _progressFraction * 100;
                ReadCurrentText.Text = $"{current:N0} / {total:N0}";
                double samplesPerSecond = _elapsed is { Elapsed.TotalSeconds: > 0 } watch
                    ? current / watch.Elapsed.TotalSeconds
                    : 0;
                ReadSpeedText.Text = samplesPerSecond > 0 ? $"{samplesPerSecond:0.0} 点/秒" : "—";
            }
            else
            {
                Match percent = PercentPattern.Match(line);
                if (percent.Success
                    && double.TryParse(percent.Groups["percent"].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double value))
                {
                    _progressFraction = Math.Clamp(value / 100, 0, 1);
                    RunProgress.IsIndeterminate = false;
                    RunProgress.Value = _progressFraction * 100;
                }
            }

            RunStatusText.Text = line;
            RefreshElapsedLabels();
        }));
    }

    private void ExportMarkers_Click(object sender, RoutedEventArgs e)
    {
        if (Result is null || _isRunning)
        {
            return;
        }

        string? directory = _dialogs.PickFolder("选择辅助标定记录导出文件夹");
        if (directory is null)
        {
            return;
        }

        try
        {
            ExportedFiles = CalibrationSessionExporter.Export(Result.Geometry, _project.Name, directory);
            foreach (string file in ExportedFiles)
            {
                AppendReadLog($"已导出：{Path.Combine(directory, file)}");
            }

            RunStatusText.Text = "三份辅助标定记录已导出。";
        }
        catch (Exception exception)
        {
            RunStatusText.Text = "导出失败。";
            AppendReadLog($"导出失败：{exception.Message}");
        }
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (Result is not null && !_isRunning)
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            CancelButton.IsEnabled = false;
            CancelButton.Content = "正在停止…";
            _operationCancellation?.Cancel();
            return;
        }

        DialogResult = false;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isRunning)
        {
            e.Cancel = true;
            CancelButton.IsEnabled = false;
            CancelButton.Content = "正在停止…";
            _operationCancellation?.Cancel();
        }
    }

    private void GoTo(int step)
    {
        WelcomePanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreparePanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        BurnPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        ReadPreparePanel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        RunPanel.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        StepWelcomeLabel.FontWeight = step == 0 ? FontWeights.Bold : FontWeights.Normal;
        StepPrepareLabel.FontWeight = step == 1 ? FontWeights.Bold : FontWeights.Normal;
        StepBurnLabel.FontWeight = step == 2 ? FontWeights.Bold : FontWeights.Normal;
        StepReadPrepareLabel.FontWeight = step == 3 ? FontWeights.Bold : FontWeights.Normal;
        StepReadLabel.FontWeight = step == 4 ? FontWeights.Bold : FontWeights.Normal;
        BackButton.Visibility = step is 1 or 3 or 4 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = !_isRunning;
        WelcomeNextButton.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        CancelButton.Content = "取消";
    }

    private void BeginOperation()
    {
        _isRunning = true;
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _elapsed = Stopwatch.StartNew();
        _progressFraction = 0;
        _elapsedTimer.Start();
        CancelButton.IsEnabled = true;
        BackButton.IsEnabled = false;
    }

    private void EndOperation()
    {
        _isRunning = false;
        _elapsedTimer.Stop();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        CancelButton.IsEnabled = true;
        CancelButton.Content = "取消";
        BackButton.IsEnabled = true;
        RefreshElapsedLabels();
    }

    private void RefreshElapsedLabels()
    {
        TimeSpan elapsed = _elapsed?.Elapsed ?? TimeSpan.Zero;
        BurnElapsedText.Text = FormatDuration(elapsed);
        ReadElapsedText.Text = FormatDuration(elapsed);
        if (_progressFraction > 0 && _progressFraction < 1)
        {
            TimeSpan remaining = TimeSpan.FromSeconds(
                elapsed.TotalSeconds * (1 - _progressFraction) / _progressFraction);
            BurnEstimateText.Text = FormatDuration(remaining);
            ReadEstimateText.Text = FormatDuration(remaining);
        }
        else
        {
            BurnEstimateText.Text = _progressFraction >= 1 ? "完成" : "计算中";
            ReadEstimateText.Text = _progressFraction >= 1 ? "完成" : "计算中";
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void AppendBurnLog(string line)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _burnLog.AppendLine(line);
            BurnLogText.Text = _burnLog.ToString();
        }));
    }

    private void AppendReadLog(string line)
    {
        _readLog.AppendLine(line);
        RunLogText.Text = _readLog.ToString();
    }

    private void ShowPrepareError(string message)
    {
        RunErrorText.Text = message;
        RunErrorText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowReadPrepareError(string message)
    {
        ReadPrepareErrorText.Text = message;
        ReadPrepareErrorText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool TryParseOptions(
        out (int MaxTargets, int Reps, double RangeLo, double RangeHi) options,
        out uint? manualDiscSectors,
        bool validateManualMediaParameters = true)
    {
        options = default;
        manualDiscSectors = null;
        if (!int.TryParse(MaxTargetsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int targets)
            || !int.TryParse(RepsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int reps)
            || !double.TryParse(RangeLoText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)
            || !double.TryParse(RangeHiText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double hi)
            || !DiscratcherMeasurementOptions.IsValid(targets, reps, lo, hi))
        {
            return false;
        }

        if (validateManualMediaParameters
            && ManualMediaParametersCheckBox.IsChecked == true
            && _mediaType == "cd")
        {
            if (!uint.TryParse(ManualSectorsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint sectors)
                || sectors < CalibrationImageSizing.MinimumCdImageSectors
                    + CalibrationImageSizing.CdLeadOutMarginSectors
                || sectors > MaximumCdDiscSectors)
            {
                return false;
            }

            manualDiscSectors = sectors;
        }

        options = (targets, reps, lo, hi);
        return true;
    }

    private bool TryGetPreparedImageSectors(
        uint? manualDiscSectors, out uint imageSectors, out string error)
    {
        imageSectors = 0;
        error = string.Empty;
        if (ManualMediaParametersCheckBox.IsChecked == true)
        {
            try
            {
                imageSectors = CalibrationImageSizing.ResolveForExport(
                    _mediaType, useManualParameters: true, manualDiscSectors, null);
                return true;
            }
            catch (InvalidOperationException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        if (ParameterDriveCombo.SelectedIndex < 0
            || ParameterDriveCombo.SelectedIndex >= _readDrives.Count)
        {
            error = "尚未读取空白盘参数。请插入空白盘并点击“重读光盘参数”，或启用手动参数模式。";
            return false;
        }

        DiscDriveInfo drive = _readDrives[ParameterDriveCombo.SelectedIndex];
        if (!drive.MediaPresent || !drive.Supported || !drive.IsBlank
            || !MatchesProjectMedia(drive.MediaType) || drive.TotalSectors <= 0)
        {
            error = "所选光驱未报告匹配的空白盘及有效容量。请插入空白盘后重读参数，或启用手动参数模式。";
            return false;
        }

        try
        {
            imageSectors = CalibrationImageSizing.ResolveForExport(
                _mediaType, useManualParameters: false, null, drive.TotalSectors);
            return true;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool MatchesProjectMedia(string mediaType) =>
        _mediaType == "cd"
            ? mediaType.Contains("CD", StringComparison.OrdinalIgnoreCase)
            : mediaType.Contains("DVD", StringComparison.OrdinalIgnoreCase);

    private void AppendBlankMediaLog(string message)
    {
        _blankMediaLog.Append('[')
            .Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            .Append("] ")
            .AppendLine(message);
        BlankMediaLogText.Text = _blankMediaLog.ToString();
    }

    private static string SafeFileName(string name)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        invalid.UnionWith(['<', '>', ':', '"', '/', '\\', '|', '?', '*']);
        string safe = new(name.Trim().Select(character =>
            char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray());
        safe = safe.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "DiscImageStudio" : safe;
    }
}
