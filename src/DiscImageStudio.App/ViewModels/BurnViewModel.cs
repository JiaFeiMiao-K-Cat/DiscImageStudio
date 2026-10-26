using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class BurnViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly DesignViewModel _design;
    private readonly IOpticalDiscBurner _opticalDiscBurner = new WindowsImapiBurner();
    private CancellationTokenSource? _burnCancellation;
    private int _burnWriteSpeedRefreshRevision;
    private int _burnPreparationRevision;
    private string? _sourceIdentity;
    private bool _isPreparingBurn;

    [ObservableProperty]
    private IReadOnlyList<OpticalBurnDevice>? _devices;

    [ObservableProperty]
    private OpticalBurnDevice? _selectedDevice;

    [ObservableProperty]
    private string _deviceStatusText = "进入此页面后会检测本机刻录机。";

    [ObservableProperty]
    private IReadOnlyList<BurnWriteSpeedOption> _speeds = [BurnWriteSpeedOption.Automatic];

    [ObservableProperty]
    private BurnWriteSpeedOption? _selectedSpeed = BurnWriteSpeedOption.Automatic;

    [ObservableProperty]
    private bool _isSpeedComboEnabled;

    [ObservableProperty]
    private bool _isSpeedRefreshEnabled;

    [ObservableProperty]
    private string _speedStatusText = "放入可写盘片后读取其支持的速度；默认自动使用最低可用速度，以降低写入错误率。";

    [ObservableProperty]
    private bool _useCalibrationPattern;

    [ObservableProperty]
    private string _sourceTitle = "使用工程当前合成图与参数";

    [ObservableProperty]
    private string _sourceDetails = "确保磁盘预留适量空间";

    [ObservableProperty]
    private bool _confirmChecked;

    [ObservableProperty]
    private string _burnStatusText = "等待开始";

    [ObservableProperty]
    private double _burnProgressValue;

    [ObservableProperty]
    private bool _isBurnRunning;

    public BurnViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design)
    {
        _shell = shell;
        _state = state;
        _design = design;
        RefreshDevicesCommand = new RelayCommand(() => _ = RefreshDevicesAsync(), () => !_shell.IsBusy);
        RefreshSpeedsCommand = new RelayCommand(() => _ = RefreshWriteSpeedsAsync(), () => !_shell.IsBusy);
        StartBurnCommand = new AsyncRelayCommand(() => StartStreamBurnAsync(), CanStartBurn);
        CancelBurnCommand = new RelayCommand(CancelStreamBurn, () => IsBurnRunning);
        _shell.BusyChanged += () =>
        {
            InvalidateConfirmation();
            OnPropertyChanged(nameof(IsSetupEnabled));
            RefreshDevicesCommand.NotifyCanExecuteChanged();
            RefreshSpeedsCommand.NotifyCanExecuteChanged();
            StartBurnCommand.NotifyCanExecuteChanged();
            CancelBurnCommand.NotifyCanExecuteChanged();
        };
        // The disc family is derived from the burn source (project family, or the calibration
        // target's family), so a project switch or a new target has to re-derive and re-arm.
        _shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.IsProjectOpen)
                or nameof(ShellViewModel.CurrentProject))
            {
                OnFamilyInputsChanged();
            }
        };
        _state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiscParametersState.CdDataDirectory)
                && !UseCalibrationPattern
                && _shell.CurrentProject?.Family == OpticalDiscFamily.CompactDisc)
            {
                OnFamilyInputsChanged();
            }
        };
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsBurnRunning))
            {
                CancelBurnCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public RelayCommand RefreshDevicesCommand { get; }

    public RelayCommand RefreshSpeedsCommand { get; }

    public IAsyncRelayCommand StartBurnCommand { get; }

    public RelayCommand CancelBurnCommand { get; }

    public bool IsSetupEnabled => !_shell.IsBusy && !_isPreparingBurn;

    /// <summary>Exposed for the view's two-way bindings to the shared parameters, so the page
    /// can bind this view model directly like every other workspace page.</summary>
    public DiscParametersState State => _state;

    /// <summary>Workspace navigation for the page's cross-workspace links.</summary>
    public IRelayCommand<Workspace> NavigateCommand => _shell.NavigateCommand;

    /// <summary>Disc family of the material about to be written: the calibration target's
    /// family while burning a target, otherwise the open project's family. Deriving it removes
    /// the old manual switch, which could be left on the wrong family and then failed at start
    /// with only a hint line to explain why.</summary>
    public bool IsDvdBurn => UseCalibrationPattern
        ? _shell.Calibration.Target?.Parameters.Kind == CalibrationDiscKind.Dvd
        : _shell.CurrentProject?.IsDvd ?? false;

    public bool IsBluRayBurn => UseCalibrationPattern
        ? _shell.Calibration.Target?.Parameters.Kind == CalibrationDiscKind.BluRay
        : _shell.CurrentProject?.IsBluRay ?? false;

    /// <summary>Family chip in the page header; follows the material being written.</summary>
    public string FamilyLabel => UseCalibrationPattern
        ? _shell.Calibration.Target?.Parameters.Kind switch
        {
            CalibrationDiscKind.Dvd => "DVD",
            CalibrationDiscKind.BluRay => "BD",
            _ => "CD",
        }
        : _shell.CurrentProject?.FamilyLabel ?? "CD";

    private OpticalBurnMediaKind CurrentMediaKind => IsBluRayBurn
        ? OpticalBurnMediaKind.BluRayData
        : IsDvdBurn
            ? OpticalBurnMediaKind.DvdData
            : !UseCalibrationPattern && !string.IsNullOrWhiteSpace(_state.CdDataDirectory)
                ? OpticalBurnMediaKind.CdData : OpticalBurnMediaKind.CdAudio;

    /// <summary>Device→speeds→confirm gating: not busy, device chosen, checkbox ticked.</summary>
    private bool CanStartBurn()
        => !_shell.IsBusy
            && !_isPreparingBurn
            && SelectedDevice is not null
            && ConfirmChecked;

    private void InvalidateConfirmation()
    {
        _burnPreparationRevision++;
        ConfirmChecked = false;
        UpdateActionState();
    }

    partial void OnSelectedDeviceChanged(OpticalBurnDevice? value)
    {
        InvalidateConfirmation();
        _ = RefreshWriteSpeedsAsync();
    }

    partial void OnSelectedSpeedChanged(BurnWriteSpeedOption? value)
    {
        InvalidateConfirmation();
        UpdateActionState();
    }

    partial void OnConfirmCheckedChanged(bool value) => UpdateActionState();

    partial void OnUseCalibrationPatternChanged(bool value) => OnFamilyInputsChanged();

    /// <summary>A source change (project switch, calibration target, calibration switch) moves
    /// the derived family, so confirmation, summary and speeds all have to re-derive.</summary>
    public void OnCalibrationTargetChanged() => OnFamilyInputsChanged();

    private void OnFamilyInputsChanged()
    {
        OnPropertyChanged(nameof(IsDvdBurn));
        OnPropertyChanged(nameof(IsBluRayBurn));
        OnPropertyChanged(nameof(FamilyLabel));
        InvalidateConfirmation();
        UpdateSourceSummary();
        UpdateActionState();
        _ = RefreshWriteSpeedsAsync();
    }

    public void UpdateSourceSummary()
    {
        string identity;
        if (UseCalibrationPattern)
        {
            CalibrationTarget? target = _shell.Calibration.Target;
            if (target is null)
            {
                SourceTitle = "尚未创建标定图案";
                SourceDetails = "请先在辅助标定工作区创建图案，再回到这里刻录。";
                identity = "calibration:none";
            }
            else
            {
                CalibrationParameters p = target.Parameters;
                string kind = p.Kind == CalibrationDiscKind.Dvd ? "DVD" : "CD";
                SourceTitle = $"标定图案 · {kind} · {target.Id[..8]}";
                string encoding = p.Kind == CalibrationDiscKind.Dvd
                    ? $"位长 {p.LinearDensity:0.######} nm"
                    : $"线速度 {p.LinearDensity:0.######} mm/s · "
                        + (_shell.Calibration.CdInterleave ? "延迟交织" : "未交织");
                SourceDetails = $"绘图区 {p.InnerRadiusMm:0.######}–{p.OuterRadiusMm:0.######} mm"
                    + $" · {p.Sectors} 扇区 · {encoding}\n"
                    + "将使用标定模块提供的参数。";
                identity = $"calibration:{target.Id}:{p}:{_shell.Calibration.CdInterleave}";
            }
        }
        else if (IsDvdBurn)
        {
            string dataSuffix = string.IsNullOrWhiteSpace(_state.DvdDataDirectory)
                ? string.Empty
                : _state.DvdFilesAtOuterRing ? "；文件位于外圈，图案位于其内侧" : "；文件位于内圈，图案位于其外侧";
            SourceTitle = _design.HasLayers
                ? "使用工程当前合成图与参数"
                : "请先在设计页导入图片";
            SourceDetails = _design.HasLayers
                ? "确保磁盘预留适量空间" + dataSuffix
                : "合成图会自动生成本页输入。" + dataSuffix;
            identity = string.Join("\u001f", "dvd", _design.CompositeRevision, _state.DvdTotalSectors,
                _state.DvdInnerRadius, _state.DvdOuterRadius,
                _state.DvdPitchLinear, _state.DvdPitchQuadratic, _state.DvdPitchCubic,
                _state.DvdDataDirectory, _state.DvdVolumeLabel, _state.DvdFilesAtOuterRing,
                _state.ToneDitherStrength, _state.ToneBrightness, _state.ToneSrgbLinearization);
        }
        else if (IsBluRayBurn)
        {
            string dataSuffix = string.IsNullOrWhiteSpace(_state.BdDataDirectory)
                ? string.Empty
                : _state.BdFilesAtOuterRing ? "；文件位于外圈，图案位于其内侧" : "；文件位于内圈，图案位于其外侧";
            SourceTitle = _design.HasLayers
                ? "使用工程当前合成图与参数"
                : "请先在设计页导入图片";
            string polarity = _state.BdInvertMapping ? "反转 3T/6T" : "标准 3T/6T";
            SourceDetails = _design.HasLayers
                ? $"确保磁盘预留适量空间 · {polarity}" + dataSuffix
                : $"合成图会自动生成本页输入。 · {polarity}" + dataSuffix;
            identity = string.Join("\u001f", "bd", _design.CompositeRevision, _state.BdTotalSectors,
                _state.BdInnerRadius, _state.BdOuterRadius, _state.BdDataDirectory,
                _state.BdVolumeLabel, _state.BdInvertMapping, _state.BdFilesAtOuterRing,
                _state.ToneDitherStrength, _state.ToneBrightness, _state.ToneSrgbLinearization);
        }
        else
        {
            SourceTitle = _design.HasLayers
                ? "使用工程当前合成图与参数"
                : "请先在设计页导入图片";
            // The encoding is part of the material actually written, and the burn page has
            // no switch of its own: surfacing it here keeps the streamed bytes explainable.
            string encoding = string.IsNullOrWhiteSpace(_state.CdDataDirectory)
                ? _state.CdInterleave ? "CD 音轨 · 启用延迟交织" : "CD 音轨 · 未交织"
                : $"CD 数据盘 · 文件位于{(_state.CdFilesAtOuterRing ? "外圈" : "内圈")}";
            SourceDetails = _design.HasLayers
                ? $"确保磁盘预留适量空间 · {encoding}"
                : $"合成图会自动生成本页输入。 · {encoding}";
            identity = string.Join("\u001f", "cd", _design.CompositeRevision, _state.CdSectors,
                _state.CdInnerRadius, _state.CdOuterRadius, _state.CdInterleave,
                _state.CdDataDirectory, _state.CdVolumeLabel, _state.CdFilesAtOuterRing,
                _state.ToneDitherStrength, _state.ToneLevels, _state.ToneBrightness,
                _state.ToneSrgbLinearization);
        }

        if (!string.Equals(identity, _sourceIdentity, StringComparison.Ordinal))
        {
            _sourceIdentity = identity;
            InvalidateConfirmation();
        }
    }

    private void UpdateActionState() => StartBurnCommand.NotifyCanExecuteChanged();

    internal async Task RefreshDevicesAsync()
    {
        if (_shell.IsBusy)
        {
            return;
        }

        Devices = null;
        DeviceStatusText = "正在检测刻录机…";
        try
        {
            IReadOnlyList<OpticalBurnDevice> devices =
                await _opticalDiscBurner.GetDevicesAsync();
            Devices = devices;
            SelectedDevice = devices.Count == 0 ? null : devices[0];
            DeviceStatusText = devices.Count == 0
                ? "未检测到支持 IMAPI2 的 CD/DVD/BD 刻录机。"
                : $"检测到 {devices.Count} 台刻录机";
        }
        catch (Exception exception)
        {
            Devices = null;
            SelectedDevice = null;
            DeviceStatusText = "检测刻录机失败：" + exception.Message;
        }
        finally
        {
            UpdateActionState();
        }
    }

    internal async Task RefreshWriteSpeedsAsync()
    {
        if (_shell.IsBusy)
        {
            return;
        }

        int revision = ++_burnWriteSpeedRefreshRevision;
        OpticalWriteSpeed? previousSpeed = SelectedSpeed?.Speed;
        Speeds = [BurnWriteSpeedOption.Automatic];
        SelectedSpeed = BurnWriteSpeedOption.Automatic;

        if (SelectedDevice is not OpticalBurnDevice device)
        {
            IsSpeedComboEnabled = false;
            IsSpeedRefreshEnabled = false;
            SpeedStatusText = "请先选择刻录机。";
            return;
        }

        OpticalBurnMediaKind mediaKind = CurrentMediaKind;
        IsSpeedComboEnabled = false;
        IsSpeedRefreshEnabled = false;
        SpeedStatusText = "正在读取当前盘片支持的刻录速度…";
        try
        {
            IReadOnlyList<OpticalWriteSpeed> speeds =
                await _opticalDiscBurner.GetSupportedWriteSpeedsAsync(
                    device.Id,
                    mediaKind);
            if (revision != _burnWriteSpeedRefreshRevision)
            {
                return;
            }

            List<BurnWriteSpeedOption> options = [BurnWriteSpeedOption.Automatic];
            options.AddRange(speeds.Select(speed => new BurnWriteSpeedOption(
                speed,
                FormatBurnWriteSpeed(speed, mediaKind))));
            Speeds = options;
            int selectedIndex = previousSpeed is null
                ? 0
                : options.FindIndex(option => option.Speed == previousSpeed);
            SelectedSpeed = selectedIndex < 0 ? options[0] : options[selectedIndex];
            SpeedStatusText = speeds.Count == 0
                ? "当前盘片未报告可选速度，将使用刻录接口的保守回退设置。"
                : $"当前盘片报告 {speeds.Count} 种写入配置；自动模式将使用最低值 "
                    + $"{FormatBurnWriteSpeed(speeds[0], mediaKind)}，最终以刻录机采用值为准。";
        }
        catch (Exception exception)
        {
            if (revision == _burnWriteSpeedRefreshRevision)
            {
                SpeedStatusText =
                    "未能读取当前盘片速度，将使用刻录接口的保守回退设置：" + exception.Message;
            }
        }
        finally
        {
            if (revision == _burnWriteSpeedRefreshRevision)
            {
                IsSpeedComboEnabled = true;
                IsSpeedRefreshEnabled = true;
            }
        }
    }

    private static string FormatBurnWriteSpeed(
        OpticalWriteSpeed speed,
        OpticalBurnMediaKind mediaKind)
    {
        string rotation = speed.RotationTypeIsPureCav ? " · CAV" : string.Empty;
        return $"{speed.GetMultiplier(mediaKind):0.#}×"
            + $" · {speed.GetMegabytesPerSecond(mediaKind):0.0} MB/s{rotation}";
    }

    private async Task StartStreamBurnAsync()
    {
        if (_shell.IsBusy)
        {
            _shell.ShowToast("操作未执行", "已有任务正在运行。", ToastKind.Info);
            return;
        }

        bool ownsBusyState = false;
        _isPreparingBurn = true;
        OnPropertyChanged(nameof(IsSetupEnabled));
        UpdateActionState();
        try
        {
            if (SelectedDevice is not OpticalBurnDevice device)
            {
                throw new ArgumentException("请选择刻录机。");
            }

            if (!ConfirmChecked)
            {
                throw new ArgumentException("请先勾选刻录确认。");
            }

            bool dvd = IsDvdBurn;
            bool bluRay = IsBluRayBurn;
            bool calibration = UseCalibrationPattern;
            int preparationRevision = _burnPreparationRevision;
            OpticalBurnMediaKind mediaKind = CurrentMediaKind;
            BurnWriteSpeedOption selectedWriteSpeed = SelectedSpeed ?? BurnWriteSpeedOption.Automatic;
            OpticalBurnRequest request;
            string contentDescription;
            if (calibration)
            {
                CalibrationTarget target = _shell.Calibration.Target
                    ?? throw new ArgumentException("请先在辅助标定工作区创建图案。");
                bool interleave = _shell.Calibration.CdInterleave;
                if (!_shell.Calibration.EnsureSessionSavedForBurn())
                {
                    return;
                }

                if (!ReferenceEquals(target, _shell.Calibration.Target)
                    || interleave != _shell.Calibration.CdInterleave)
                {
                    throw new InvalidOperationException("标定图案已变化，请重新保存记录并确认刻录。");
                }

                request = CreateCalibrationBurnRequest(target, interleave, device.Id, selectedWriteSpeed.Speed);
                string family = target.Parameters.Kind switch
                {
                    CalibrationDiscKind.Dvd => "DVD",
                    CalibrationDiscKind.BluRay => "BD",
                    _ => "CD",
                };
                contentDescription = $"{family} 标定图案 · {target.Id[..8]}"
                    + $" · {target.Parameters.Sectors} 扇区 · {request.ContentLength / (1024.0 * 1024.0):F1} MiB";
            }
            else
            {
                // Streaming burns the same export-quality composite the file path writes, so
                // what the read-back preview verified is the material that lands on the disc.
                string compositePath = await _design.EnsureExportCompositeAsync();
                if (bluRay)
                {
                    BluRayDiscParameters disc = ReadBluRayParameters();
                    string dataDirectory = _state.BdDataDirectory.Trim();
                    BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(
                        disc,
                        dataDirectory.Length == 0 ? null : dataDirectory,
                        string.IsNullOrWhiteSpace(_state.BdVolumeLabel)
                            ? "BD_IMAGE"
                            : _state.BdVolumeLabel.Trim(),
                        filesAtOuterRing: dataDirectory.Length != 0 && _state.BdFilesAtOuterRing);
                    ToneProfile tone = _state.BluRayTone;
                    bool invert = _state.BdInvertMapping;
                    request = new OpticalBurnRequest(
                        device.Id,
                        OpticalBurnMediaKind.BluRayData,
                        plan.ContentLength,
                        (output, cancellationToken) =>
                        {
                            BluRayGray8Raster raster = BluRayImageLoader.Load(
                                compositePath, tone, cancellationToken);
                            BluRayImagePayloadMapping mapping = new(
                                disc, raster, new BluRayRunLengthMapping(invert));
                            BluRayIsoGenerator.GenerateToStream(
                                output, plan, mapping, progress: null, cancellationToken);
                        },
                        selectedWriteSpeed.Speed);
                    contentDescription = $"BD-R · {disc.TotalSectors} 扇区"
                        + $" · {plan.ContentLength / (1024.0 * 1024.0):F1} MiB"
                        + $" · 绘图 LBA {plan.DrawingStartSector}–{plan.DrawingEndSectorExclusive - 1}"
                        + (plan.HasFileSystem ? $" · 文件：{(_state.BdFilesAtOuterRing ? "外圈" : "内圈")}" : string.Empty)
                        + (invert ? " · 反转 3T/6T" : " · 标准 3T/6T");
                }
                else if (dvd)
                {
                    DvdStreamingOptions options = ReadDvdStreamingOptions();
                    string dataDirectory = _state.DvdDataDirectory.Trim();
                    DvdHybridStreamingPlan? hybridPlan = dataDirectory.Length == 0
                        ? null
                        : DvdStreamingGenerator.PrepareHybrid(
                            dataDirectory,
                            options,
                            string.IsNullOrWhiteSpace(_state.DvdVolumeLabel)
                                ? "DISC_IMAGE"
                                : _state.DvdVolumeLabel.Trim(),
                            filesAtOuterRing: _state.DvdFilesAtOuterRing);
                    request = new OpticalBurnRequest(
                        device.Id,
                        OpticalBurnMediaKind.DvdData,
                        options.ContentLength,
                        (output, cancellationToken) =>
                        {
                            if (hybridPlan is null)
                            {
                                DvdStreamingGenerator.Generate(
                                    compositePath,
                                    output,
                                    options,
                                    cancellationToken: cancellationToken);
                            }
                            else
                            {
                                DvdStreamingGenerator.GenerateHybrid(
                                    compositePath,
                                    output,
                                    options,
                                    hybridPlan,
                                    cancellationToken: cancellationToken);
                            }
                        },
                        selectedWriteSpeed.Speed);
                    contentDescription = hybridPlan is null
                        ? $"DVD · {options.TotalSectors} 扇区 · {options.ContentLength / (1024.0 * 1024.0):F1} MiB"
                        : $"DVD 混合盘 · {hybridPlan.FileCount} 个文件（{(_state.DvdFilesAtOuterRing ? "外圈" : "内圈")}）"
                            + $" · 绘图 LBA {hybridPlan.DrawingStartLba}–{hybridPlan.DrawingEndLbaExclusive - 1}"
                            + $" · {options.ContentLength / (1024.0 * 1024.0):F1} MiB";
                }
                else
                {
                    CdDiscParameters parameters = ReadCdGeneratedParameters();
                    request = CreateCdBurnRequest(
                        compositePath,
                        parameters,
                        _state.CdDataDirectory,
                        _state.CdVolumeLabel,
                        _state.CdFilesAtOuterRing,
                        _state.CdInterleave,
                        _state.Tone,
                        device.Id,
                        selectedWriteSpeed.Speed);
                    contentDescription = request.MediaKind == OpticalBurnMediaKind.CdData
                        ? $"CD 数据盘（DAO） · 文件位于{(_state.CdFilesAtOuterRing ? "外圈" : "内圈")}"
                        : "CD 音轨";
                    contentDescription += $" · {parameters.Sectors} 扇区 · {request.ContentLength / (1024.0 * 1024.0):F1} MiB";
                }
            }

            // In-app danger modal; default focus sits on the refusing button.
            bool confirmed = await _shell.ConfirmAsync(
                "确认开始流式刻录",
                $"即将写入：{device.DisplayName}\n{contentDescription}\n"
                    + $"请求速度：{selectedWriteSpeed.DisplayName}\n\n"
                    + "仅允许空白盘。开始后取消或断电可能使盘片报废。是否继续？",
                "确认刻录");
            if (!confirmed)
            {
                return;
            }

            if (_shell.IsBusy || preparationRevision != _burnPreparationRevision || !ConfirmChecked)
            {
                BurnStatusText = "刻录来源或选项已变化，请重新确认后开始。";
                return;
            }

            ownsBusyState = true;
            _shell.SetBusy(true);
            IsBurnRunning = true;
            _burnCancellation = new CancellationTokenSource();
            // The burn reports real percentages, so it drives the job dock's bar instead of
            // showing a second progress surface inside this page.
            _shell.BeginDockJob("流式刻录", determinate: true);
            BurnProgressValue = 0;
            BurnStatusText = "正在准备流式刻录…";
            _shell.AppendLog(
                $"\n[{DateTime.Now:HH:mm:ss}] 开始流式刻录：{contentDescription}；"
                + $"设备={device.DisplayName}；请求速度={selectedWriteSpeed.DisplayName}。\n");
            Progress<OpticalBurnProgress> progress = new(value =>
            {
                BurnStatusText = value.Message;
                BurnProgressValue = Math.Clamp(value.Fraction * 100.0, 0.0, 100.0);
                _shell.ReportDockJob(value.Message, BurnProgressValue);
            });
            OpticalBurnResult result = await _opticalDiscBurner.BurnAsync(
                request,
                progress,
                _burnCancellation.Token);
            BurnProgressValue = 100;
            BurnStatusText = "刻录完成，可以取出光盘。";
            _shell.CompleteDockJob("完成：流式刻录 " + contentDescription);
            _shell.AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] 流式刻录完成：{result.BytesWritten} 字节，"
                + $"耗时 {result.Elapsed.TotalSeconds:F1} 秒"
                + (result.ActualWriteSpeed is null
                    ? "。\n"
                    : $"，实际速度={FormatBurnWriteSpeed(result.ActualWriteSpeed, mediaKind)}。\n"));
            ConfirmChecked = false;
            _shell.ShowToast("流式刻录完成", "流式刻录已经完成，可以取出光盘。", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            BurnStatusText = "已请求取消；光驱可能仍需一段时间才能停止。";
            _shell.AbortDockJob("已取消：流式刻录", expandLog: false);
            _shell.AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] 已取消流式刻录；当前盘片可能无法继续使用。\n");
        }
        catch (Exception exception)
        {
            BurnStatusText = "刻录未完成，请查看错误信息。";
            _shell.AbortDockJob("失败：流式刻录", expandLog: true);
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 流式刻录失败：{exception.Message}\n");
            _shell.ShowToast(
                "流式刻录未完成",
                exception.Message + "\n\n如果写入已经开始，当前盘片可能无法继续使用。",
                ToastKind.Error);
        }
        finally
        {
            _burnCancellation?.Dispose();
            _burnCancellation = null;
            IsBurnRunning = false;
            InvalidateConfirmation();
            _isPreparingBurn = false;
            if (ownsBusyState)
            {
                _shell.SetBusy(false);
            }

            OnPropertyChanged(nameof(IsSetupEnabled));
            UpdateActionState();
        }
    }

    // Build the layout before physical writing and capture all generation options.
    internal static OpticalBurnRequest CreateCdBurnRequest(
        string imagePath,
        CdDiscParameters parameters,
        string? dataDirectory,
        string volumeLabel,
        bool filesAtOuterRing,
        bool interleave,
        ToneProfile tone,
        string deviceId,
        OpticalWriteSpeed? writeSpeed)
    {
        parameters.Validate();
        ToneProfile capturedTone = tone.Normalized();
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.CdAudio, parameters.TotalBytes,
                (output, token) => CdTrackGenerator.GenerateToStream(imagePath, output, parameters,
                    interleave, cancellationToken: token, audioByteOrder: CdAudioByteOrder.LittleEndian,
                    tone: capturedTone), writeSpeed);
        }

        CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(dataDirectory.Trim(), parameters,
            string.IsNullOrWhiteSpace(volumeLabel) ? "CD_IMAGE" : volumeLabel.Trim(), filesAtOuterRing);
        return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.CdData, checked(parameters.Sectors * 2048),
            (output, token) => CdDataImageGenerator.GenerateToStream(imagePath, output, parameters, plan,
                cancellationToken: token, tone: capturedTone), writeSpeed);
    }

    // Request construction is separate from physical writing so the captured
    // calibration content can also be validated with an ordinary memory stream.
    internal static OpticalBurnRequest CreateCalibrationBurnRequest(
        CalibrationTarget target,
        bool cdInterleave,
        string deviceId,
        OpticalWriteSpeed? writeSpeed)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Parameters.Kind == CalibrationDiscKind.BluRay)
        {
            BluRayDiscParameters disc = CalibrationViewModel.BluRayParameters(target.Parameters);
            BluRayPatternRangeMapping mapping = CalibrationViewModel.BluRayPatternMapping(target);
            return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.BluRayData, disc.ContentLength,
                (output, token) => BluRayStreamingGenerator.GenerateToStream(
                    output,
                    new BluRayStreamingOptions(disc, 0, disc.TotalSectors),
                    mapping,
                    progress: null,
                    cancellationToken: token),
                writeSpeed);
        }

        if (target.Parameters.Kind == CalibrationDiscKind.Dvd)
        {
            DvdStreamingOptions options = CalibrationViewModel.DvdOptions(target);
            options.Validate();
            return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.DvdData, options.ContentLength,
                (output, token) => DvdStreamingGenerator.GeneratePattern(target.Sample, output, options,
                    cancellationToken: token), writeSpeed);
        }

        CdDiscParameters parameters = CalibrationViewModel.CdParameters(target);
        parameters.Validate();
        return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.CdAudio, parameters.TotalBytes,
            (output, token) => CdTrackGenerator.GeneratePatternToStream(target.Sample, output, parameters,
                cdInterleave, cancellationToken: token, audioByteOrder: CdAudioByteOrder.LittleEndian), writeSpeed);
    }

    private void CancelStreamBurn()
    {
        CancelBurnCommand.NotifyCanExecuteChanged();
        BurnStatusText = "正在请求停止刻录，请不要取出盘片…";
        _burnCancellation?.Cancel();
    }

    private CdDiscParameters ReadCdGeneratedParameters()
    {
        CdDiscParameters parameters = new(
            ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "CD 内半径"),
            ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "CD 外半径"),
            ParameterParser.ParsePositiveLong(_state.CdSectors, "CD 扇区数"));
        parameters.Validate();
        return parameters;
    }

    private BluRayDiscParameters ReadBluRayParameters()
    {
        long totalSectors = ParameterParser.ParsePositiveLong(_state.BdTotalSectors, "蓝光总扇区数");
        if (totalSectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException("蓝光总扇区数", "总扇区数超出蓝光引擎支持范围。");
        }

        BluRayDiscParameters disc = new(
            checked((uint)totalSectors),
            1,
            ParameterParser.ParsePositiveDouble(_state.BdInnerRadius, "蓝光内半径"),
            ParameterParser.ParsePositiveDouble(_state.BdOuterRadius, "蓝光外半径"));
        disc.Validate();
        return disc;
    }

    private DvdStreamingOptions ReadDvdStreamingOptions()
    {
        int totalSectors = ParameterParser.ParsePositiveInt(_state.DvdTotalSectors, "DVD 总扇区数");
        DvdStreamingOptions options = new(
            checked((uint)totalSectors),
            ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, "DVD 内半径"),
            ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, "DVD 外半径"),
            PitchLinear: ParameterParser.ParseDouble(_state.DvdPitchLinear, "DVD 轨距一次项"),
            PitchQuadratic: ParameterParser.ParseDouble(_state.DvdPitchQuadratic, "DVD 轨距二次项"),
            PitchCubic: ParameterParser.ParseDouble(_state.DvdPitchCubic, "DVD 轨距三次项"),
            Tone: _state.DvdTone);
        options.Validate();
        return options;
    }

    public sealed record BurnWriteSpeedOption(
        OpticalWriteSpeed? Speed,
        string DisplayName)
    {
        public static BurnWriteSpeedOption Automatic { get; } =
            new(null, "自动（最低速率，推荐）");
    }
}
