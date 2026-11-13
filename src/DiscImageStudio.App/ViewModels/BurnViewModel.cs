using DiscImageStudio.Localization;
using System.ComponentModel;
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
    private readonly IOpticalDiscBurner _opticalDiscBurner;
    private CancellationTokenSource? _burnCancellation;
    private int _burnWriteSpeedRefreshRevision;
    private int _burnPreparationRevision;
    private string? _sourceIdentity;
    private bool _isPreparingBurn;
    // The speeds the current disc reported, kept so the status line can be re-worded after a
    // language switch without touching the drive again.
    private IReadOnlyList<OpticalWriteSpeed>? _supportedSpeeds;
    // Probe failures are cached for the same reason: their text carries an exception message
    // that must not be replaced by the idle hint after a language switch. The in-flight flags
    // keep a switch from showing the idle hint while a probe is still running.
    private string? _deviceStatusError;
    private string? _speedStatusError;
    private bool _isRefreshingDevices;
    private bool _isRefreshingSpeeds;

    [ObservableProperty]
    private IReadOnlyList<OpticalBurnDevice>? _devices;

    [ObservableProperty]
    private OpticalBurnDevice? _selectedDevice;

    [ObservableProperty]
    private string _deviceStatusText = L.T("进入此页面后会检测本机刻录机。");

    [ObservableProperty]
    private IReadOnlyList<BurnWriteSpeedOption> _speeds = [BurnWriteSpeedOption.Automatic];

    [ObservableProperty]
    private BurnWriteSpeedOption? _selectedSpeed = BurnWriteSpeedOption.Automatic;

    [ObservableProperty]
    private bool _isSpeedComboEnabled;

    [ObservableProperty]
    private bool _isSpeedRefreshEnabled;

    [ObservableProperty]
    private string _speedStatusText = L.T("放入可写盘片后读取其支持的速度；默认自动使用最低可用速度，以降低写入错误率。");

    public bool UseCalibrationPattern { get; }
    public Workspace PageWorkspace => UseCalibrationPattern ? Workspace.CalibrationBurn : Workspace.Burn;
    public string PageTitle => UseCalibrationPattern ? L.T("Standalone.BurnTitle") : L.T("刻录");

    [ObservableProperty]
    private string _sourceTitle = L.T("使用工程当前合成图与参数");

    [ObservableProperty]
    private string _sourceDetails = L.T("确保磁盘预留适量空间");

    [ObservableProperty]
    private bool _confirmChecked;

    [ObservableProperty]
    private bool _isBurnRunning;

    public BurnViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design)
        : this(shell, state, design, new WindowsImapiBurner())
    {
    }

    internal BurnViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design,
        IOpticalDiscBurner opticalDiscBurner, bool useCalibrationPattern = false)
    {
        _shell = shell;
        _state = state;
        _design = design;
        _opticalDiscBurner = opticalDiscBurner;
        UseCalibrationPattern = useCalibrationPattern;
        RefreshDevicesCommand = new RelayCommand(() => _ = RefreshDevicesAsync(), () => !_shell.IsBusy);
        RefreshSpeedsCommand = new RelayCommand(() => _ = RefreshWriteSpeedsAsync(), () => !_shell.IsBusy);
        StartBurnCommand = new AsyncRelayCommand(() => StartStreamBurnAsync(), CanStartBurn);
        CancelBurnCommand = new RelayCommand(CancelStreamBurn, () => IsBurnRunning);
        _shell.BusyChanged += () =>
        {
            InvalidateConfirmation();
            OnPropertyChanged(nameof(IsSetupEnabled));
            OnPropertyChanged(nameof(CanEditDvdReadCompatibility));
            RefreshDevicesCommand.NotifyCanExecuteChanged();
            RefreshSpeedsCommand.NotifyCanExecuteChanged();
            StartBurnCommand.NotifyCanExecuteChanged();
            CancelBurnCommand.NotifyCanExecuteChanged();
        };
        // The disc family is derived from the burn source (project family, or the calibration
        // target's family), so a project switch or a new target has to re-derive and re-arm.
        _shell.PropertyChanged += (_, args) =>
        {
            if (!UseCalibrationPattern && args.PropertyName is (nameof(ShellViewModel.IsProjectOpen)
                or nameof(ShellViewModel.CurrentProject)))
            {
                // A test write is a per-disc check; it never carries over to another project.
                // Confirming the project settings re-raises CurrentProject and keeps it.
                if (args.PropertyName is nameof(ShellViewModel.IsProjectOpen))
                    CdExtraTestWrite = false;
                OnFamilyInputsChanged();
            }
        };
        _state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiscParametersState.DvdReadCompatibility) && !UseCalibrationPattern)
            {
                OnPropertyChanged(nameof(DvdReadCompatibility));
                UpdateSourceSummary();
            }
            if (args.PropertyName is nameof(DiscParametersState.CdMusicTracks) && !UseCalibrationPattern)
            {
                // Music edits change the material, not the medium: re-arm, no speed query.
                UpdateSourceSummary(refreshFiles: true);
            }
            else if (args.PropertyName is nameof(DiscParametersState.CdDataDirectory)
                    or nameof(DiscParametersState.CdExtraEnabled)
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
                _shell.RefreshCancelJobState();
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

    // Calibration burns replay the saved target, regardless of changes to the next-target setup.
    public bool DvdReadCompatibility
    {
        get => UseCalibrationPattern
            ? _shell.Calibration.Target?.Parameters.DvdReadCompatibility ?? false
            : _state.DvdReadCompatibility;
        set
        {
            if (!UseCalibrationPattern) _state.DvdReadCompatibility = value;
        }
    }

    public bool CanEditDvdReadCompatibility => !UseCalibrationPattern && IsSetupEnabled;

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

    /// <summary>A CD project in CD-Extra mode: music session plus picture session.</summary>
    public bool IsCdExtraBurn => !UseCalibrationPattern
        && _shell.CurrentProject?.Family == OpticalDiscFamily.CompactDisc
        && _state.CdExtraEnabled;

    /// <summary>CD-Extra only: the recorder simulates every WRITE (Test Write) and no session
    /// is closed, so a drive can be checked without using a disc.</summary>
    [ObservableProperty]
    private bool _cdExtraTestWrite;

    private IReadOnlyList<string>? _musicIdentityTracks;
    private string _musicIdentity = string.Empty;

    partial void OnCdExtraTestWriteChanged(bool value) => UpdateSourceSummary();

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

    /// <summary>A source change (project switch, calibration target, calibration switch) moves
    /// the derived family, so confirmation, summary and speeds all have to re-derive.</summary>
    public void OnCalibrationTargetChanged() => OnFamilyInputsChanged();

    private void OnFamilyInputsChanged()
    {
        OnPropertyChanged(nameof(IsDvdBurn));
        OnPropertyChanged(nameof(DvdReadCompatibility));
        OnPropertyChanged(nameof(IsBluRayBurn));
        OnPropertyChanged(nameof(IsCdExtraBurn));
        OnPropertyChanged(nameof(FamilyLabel));
        InvalidateConfirmation();
        UpdateSourceSummary();
        UpdateActionState();
        _ = RefreshWriteSpeedsAsync();
    }

    public void UpdateSourceSummary() => UpdateSourceSummary(refreshFiles: false);

    /// <param name="refreshFiles">Re-read the music files' sizes and dates. Parameter edits
    /// reuse the last reading; a music-list change or opening the page refreshes it.</param>
    public void UpdateSourceSummary(bool refreshFiles)
    {
        string identity;
        if (UseCalibrationPattern)
        {
            CalibrationTarget? target = _shell.Calibration.Target;
            if (target is null)
            {
                SourceTitle = L.T("尚未创建标定图案");
                SourceDetails = L.T("请先在辅助标定工作区创建图案，再回到这里刻录。");
                identity = "calibration:none";
            }
            else
            {
                CalibrationParameters p = target.Parameters;
                SourceTitle = L.F($"标定图案 · {FamilyLabel} · {target.Id[..8]}");
                string encoding = p.Kind switch
                {
                    CalibrationDiscKind.BluRay => L.F($"位长 {p.LinearDensity:0.######} nm · ")
                        + (p.BluRayInvertMapping ? L.T("反转 3T/6T") : L.T("标准 3T/6T"))
                        + L.F($" · 绘图 LBA {p.DrawingStartSector}–{(p.DrawingEndSectorExclusive == 0 ? p.Sectors : p.DrawingEndSectorExclusive) - 1}"),
                    CalibrationDiscKind.Dvd => L.F($"位长 {p.LinearDensity:0.######} nm"),
                    _ => L.F($"线速度 {p.LinearDensity:0.######} mm/s · ")
                        + (_shell.Calibration.CdInterleave ? L.T("延迟交织") : L.T("未交织")),
                };
                SourceDetails = L.F($"绘图区 {p.InnerRadiusMm:0.######}–{p.OuterRadiusMm:0.######} mm")
                    + L.F($" · {p.Sectors} 扇区 · {encoding}\n")
                    + L.T("按标定图案记录的盘型与参数写入。");
                identity = $"calibration:{target.Id}:{p}:{_shell.Calibration.CdInterleave}";
            }
        }
        else if (IsDvdBurn)
        {
            string dataSuffix = string.IsNullOrWhiteSpace(_state.DvdDataDirectory)
                ? string.Empty
                : _state.DvdFilesAtOuterRing ? L.T("；文件位于外圈，图案位于其内侧") : L.T("；文件位于内圈，图案位于其外侧");
            SourceTitle = _design.HasLayers
                ? L.T("使用工程当前合成图与参数")
                : L.T("请先在设计页导入图片");
            SourceDetails = _design.HasLayers
                ? L.T("确保磁盘预留适量空间") + dataSuffix
                : L.T("合成图会自动生成本页输入。") + dataSuffix;
            identity = string.Join("\u001f", "dvd", _design.CompositeRevision, _state.DvdTotalSectors, _state.DvdUsableSectors,
                _state.DvdInnerRadius, _state.DvdOuterRadius,
                _state.DvdPitchLinear, _state.DvdPitchQuadratic, _state.DvdPitchCubic,
                _state.DvdDataDirectory, _state.DvdVolumeLabel, _state.DvdFilesAtOuterRing, _state.DvdOuterRingBleed, _state.DvdReadCompatibility,
                _state.ToneDitherStrength, _state.ToneBrightness, _state.ToneSrgbLinearization);
        }
        else if (IsBluRayBurn)
        {
            string dataSuffix = string.IsNullOrWhiteSpace(_state.BdDataDirectory)
                ? string.Empty
                : _state.BdFilesAtOuterRing ? L.T("；文件位于外圈，图案位于其内侧") : L.T("；文件位于内圈，图案位于其外侧");
            SourceTitle = _design.HasLayers
                ? L.T("使用工程当前合成图与参数")
                : L.T("请先在设计页导入图片");
            string polarity = _state.BdInvertMapping ? L.T("反转 3T/6T") : L.T("标准 3T/6T");
            SourceDetails = _design.HasLayers
                ? L.F($"确保磁盘预留适量空间 · {polarity}") + dataSuffix
                : L.F($"合成图会自动生成本页输入。 · {polarity}") + dataSuffix;
            identity = string.Join("\u001f", "bd", _design.CompositeRevision, _state.BdTotalSectors, _state.BdUsableSectors,
                _state.BdInnerRadius, _state.BdOuterRadius, _state.BdDataDirectory,
                _state.BdPitchLinear, _state.BdPitchQuadratic, _state.BdPitchCubic,
                _state.BdVolumeLabel, _state.BdInvertMapping, _state.BdFilesAtOuterRing, _state.BdOuterRingBleed,
                _state.ToneDitherStrength, _state.ToneBrightness, _state.ToneSrgbLinearization);
        }
        else
        {
            SourceTitle = _design.HasLayers
                ? L.T("使用工程当前合成图与参数")
                : L.T("请先在设计页导入图片");
            // The encoding is part of the material actually written, and the burn page has
            // no switch of its own: surfacing it here keeps the streamed bytes explainable.
            string encoding = IsCdExtraBurn
                ? L.F($"CD-Extra · 第 1 区段 {_state.CdMusicTracks.Count} 首音乐 · 第 2 区段图片数据轨")
                    + (CdExtraTestWrite ? L.T(" · 模拟写入") : string.Empty)
                : string.IsNullOrWhiteSpace(_state.CdDataDirectory)
                    ? _state.CdInterleave ? L.T("CD 音轨 · 启用延迟交织") : L.T("CD 音轨 · 未交织")
                    : L.F($"CD 数据盘 · 文件位于{(_state.CdFilesAtOuterRing ? L.T("外圈") : L.T("内圈"))}");
            SourceDetails = _design.HasLayers
                ? L.F($"确保磁盘预留适量空间 · {encoding}")
                : L.F($"合成图会自动生成本页输入。 · {encoding}");
            identity = string.Join("\u001f", "cd", _design.CompositeRevision, _state.CdSectors, _state.CdUsableSectors,
                _state.CdInnerRadius, _state.CdOuterRadius, _state.CdInterleave,
                _state.CdPitchLinear, _state.CdPitchQuadratic, _state.CdPitchCubic,
                _state.CdDataDirectory, _state.CdVolumeLabel, _state.CdFilesAtOuterRing, _state.CdOuterRingBleed,
                _state.ToneDitherStrength, _state.ToneLevels, _state.ToneBrightness,
                _state.ToneSrgbLinearization, IsCdExtraBurn, CdExtraTestWrite, DescribeMusicIdentity(refreshFiles));
        }

        if (!string.Equals(identity, _sourceIdentity, StringComparison.Ordinal))
        {
            _sourceIdentity = identity;
            InvalidateConfirmation();
        }
    }

    /// <summary>Re-reads the cached source, device and speed text. The source identity is
    /// unchanged, so confirmation and detected devices stay exactly where they were.</summary>
    internal void Relocalize()
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(FamilyLabel));
        UpdateSourceSummary();
        if (_isRefreshingDevices)
        {
            DeviceStatusText = L.T("正在检测刻录机…");
        }
        else if (Devices is { } devices)
        {
            DeviceStatusText = devices.Count == 0
                ? L.T("未检测到支持 IMAPI2 的 CD/DVD/BD 刻录机。")
                : L.F($"检测到 {devices.Count} 台刻录机");
        }
        else
        {
            DeviceStatusText = _deviceStatusError is { } deviceError
                ? L.T("检测刻录机失败：") + deviceError
                : L.T("进入此页面后会检测本机刻录机。");
        }

        foreach (BurnWriteSpeedOption option in Speeds)
        {
            option.RefreshLabel();
        }

        OnPropertyChanged(nameof(SelectedSpeed));
        if (SelectedDevice is null)
        {
            SpeedStatusText = L.T("请先选择刻录机。");
        }
        else if (_isRefreshingSpeeds)
        {
            SpeedStatusText = L.T("正在读取当前盘片支持的刻录速度…");
        }
        else if (_supportedSpeeds is { } supported)
        {
            OpticalBurnMediaKind mediaKind = CurrentMediaKind;
            SpeedStatusText = supported.Count == 0
                ? L.T("当前盘片未报告可选速度，将使用刻录接口的保守回退设置。")
                : L.F($"当前盘片报告 {supported.Count} 种写入配置；自动模式将使用最低值 ")
                    + L.F($"{FormatBurnWriteSpeed(supported[0], mediaKind)}，最终以刻录机采用值为准。");
        }
        else if (_speedStatusError is { } speedError)
        {
            SpeedStatusText =
                L.T("未能读取当前盘片速度，将使用刻录接口的保守回退设置：") + speedError;
        }

        UpdateActionState();
    }

    private void UpdateActionState() => StartBurnCommand.NotifyCanExecuteChanged();

    internal async Task RefreshDevicesAsync()
    {
        if (_shell.IsBusy)
        {
            return;
        }

        Devices = null;
        _deviceStatusError = null;
        _isRefreshingDevices = true;
        DeviceStatusText = L.T("正在检测刻录机…");
        try
        {
            IReadOnlyList<OpticalBurnDevice> devices =
                await _opticalDiscBurner.GetDevicesAsync();
            Devices = devices;
            SelectedDevice = devices.Count == 0 ? null : devices[0];
            DeviceStatusText = devices.Count == 0
                ? L.T("未检测到支持 IMAPI2 的 CD/DVD/BD 刻录机。")
                : L.F($"检测到 {devices.Count} 台刻录机");
        }
        catch (Exception exception)
        {
            Devices = null;
            SelectedDevice = null;
            _deviceStatusError = exception.Message;
            DeviceStatusText = L.T("检测刻录机失败：") + exception.Message;
        }
        finally
        {
            _isRefreshingDevices = false;
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
        _supportedSpeeds = null;
        _speedStatusError = null;

        if (SelectedDevice is not OpticalBurnDevice device)
        {
            // This call owns the latest revision, so it also clears the in-flight marker the
            // superseded probe will no longer clear.
            _isRefreshingSpeeds = false;
            IsSpeedComboEnabled = false;
            IsSpeedRefreshEnabled = false;
            SpeedStatusText = L.T("请先选择刻录机。");
            return;
        }

        OpticalBurnMediaKind mediaKind = CurrentMediaKind;
        _isRefreshingSpeeds = true;
        IsSpeedComboEnabled = false;
        IsSpeedRefreshEnabled = false;
        SpeedStatusText = L.T("正在读取当前盘片支持的刻录速度…");
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
            _supportedSpeeds = speeds;
            int selectedIndex = previousSpeed is null
                ? 0
                : options.FindIndex(option => option.Speed == previousSpeed);
            SelectedSpeed = selectedIndex < 0 ? options[0] : options[selectedIndex];
            SpeedStatusText = speeds.Count == 0
                ? L.T("当前盘片未报告可选速度，将使用刻录接口的保守回退设置。")
                : L.F($"当前盘片报告 {speeds.Count} 种写入配置；自动模式将使用最低值 ")
                    + L.F($"{FormatBurnWriteSpeed(speeds[0], mediaKind)}，最终以刻录机采用值为准。");
        }
        catch (Exception exception)
        {
            if (revision == _burnWriteSpeedRefreshRevision)
            {
                _speedStatusError = exception.Message;
                SpeedStatusText =
                    L.T("未能读取当前盘片速度，将使用刻录接口的保守回退设置：") + exception.Message;
            }
        }
        finally
        {
            if (revision == _burnWriteSpeedRefreshRevision)
            {
                _isRefreshingSpeeds = false;
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
            _shell.ShowToast(L.T("操作未执行"), L.T("已有任务正在运行。"), ToastKind.Info);
            return;
        }

        bool ownsBusyState = false;
        _isPreparingBurn = true;
        OnPropertyChanged(nameof(IsSetupEnabled));
        OnPropertyChanged(nameof(CanEditDvdReadCompatibility));
        UpdateActionState();
        try
        {
            if (SelectedDevice is not OpticalBurnDevice device)
            {
                throw new ArgumentException(L.T("请选择刻录机。"));
            }

            if (!ConfirmChecked)
            {
                throw new ArgumentException(L.T("请先勾选刻录确认。"));
            }

            bool dvd = IsDvdBurn;
            bool bluRay = IsBluRayBurn;
            bool calibration = UseCalibrationPattern;
            int preparationRevision = _burnPreparationRevision;
            OpticalBurnMediaKind mediaKind = CurrentMediaKind;
            BurnWriteSpeedOption selectedWriteSpeed = SelectedSpeed ?? BurnWriteSpeedOption.Automatic;
            OpticalBurnRequest? request = null;
            CdExtraBurnRequest? cdExtraRequest = null;
            string contentDescription;
            OpticalMediaCapacity capacity = await _opticalDiscBurner.GetMediaCapacityAsync(device.Id, mediaKind);
            if (preparationRevision != _burnPreparationRevision || !ConfirmChecked)
                return;
            if (calibration)
            {
                CalibrationTarget target = _shell.Calibration.Target
                    ?? throw new ArgumentException(L.T("请先在辅助标定工作区创建图案。"));
                bool interleave = _shell.Calibration.CdInterleave;
                if (!_shell.Calibration.EnsureSessionSavedForBurn())
                {
                    return;
                }

                if (!ReferenceEquals(target, _shell.Calibration.Target)
                    || interleave != _shell.Calibration.CdInterleave)
                {
                    throw new InvalidOperationException(L.T("标定图案已变化，请重新保存记录并确认刻录。"));
                }

                request = CreateCalibrationBurnRequest(target, interleave, device.Id, selectedWriteSpeed.Speed, capacity.WritableSectors);
                string family = target.Parameters.Kind switch
                {
                    CalibrationDiscKind.Dvd => "DVD",
                    CalibrationDiscKind.BluRay => "BD",
                    _ => "CD",
                };
                contentDescription = L.F($"{family} 标定图案 · {target.Id[..8]}")
                    + L.F($" · {request.ContentLength / (request.MediaKind == OpticalBurnMediaKind.CdAudio ? CdDiscParameters.BytesPerSector : 2048)} 扇区 · {request.ContentLength / (1024.0 * 1024.0):F1} MiB");
            }
            else
            {
                // Streaming burns the same export-quality composite the file path writes, so
                // what the read-back preview verified is the material that lands on the disc.
                string compositePath = await _design.EnsureExportCompositeAsync();
                if (bluRay)
                {
                    BluRayDiscParameters disc = ReadBluRayParameters();
                    double imageOuterRadiusMm = disc.OutputOuterRadiusMm;
                    disc = disc with { UsableSectors = checked((uint)StreamBurnCapacity.Resolve(disc.OutputSectors, capacity.WritableSectors, mediaKind)) };
                    string dataDirectory = _state.BdDataDirectory.Trim();
                    BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(
                        disc,
                        dataDirectory.Length == 0 ? null : dataDirectory,
                        _state.ResolveVolumeLabel(OpticalDiscFamily.BluRay),
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
                                disc, raster, new BluRayRunLengthMapping(invert), imageOuterRadiusMm);
                            BluRayIsoGenerator.GenerateToStream(
                                output, plan, mapping, progress: null, cancellationToken);
                        },
                        selectedWriteSpeed.Speed);
                    contentDescription = L.F($"BD-R · {disc.OutputSectors} 扇区")
                        + $" · {plan.ContentLength / (1024.0 * 1024.0):F1} MiB"
                        + L.F($" · 绘图 LBA {plan.DrawingStartSector}–{plan.DrawingEndSectorExclusive - 1}")
                        + (plan.HasFileSystem ? L.F($" · 文件：{(_state.BdFilesAtOuterRing ? L.T("外圈") : L.T("内圈"))}") : string.Empty)
                        + (invert ? L.T(" · 反转 3T/6T") : L.T(" · 标准 3T/6T"));
                }
                else if (dvd)
                {
                    DvdStreamingOptions options = ReadDvdStreamingOptions();
                    options = options with
                    {
                        SourceImageOuterRadiusMm = options.OutputOuterRadiusMm,
                        UsableSectors = checked((uint)StreamBurnCapacity.Resolve(options.OutputSectors, capacity.WritableSectors, mediaKind)),
                    };
                    string dataDirectory = _state.DvdDataDirectory.Trim();
                    DvdHybridStreamingPlan? hybridPlan = dataDirectory.Length == 0
                        ? null
                        : DvdStreamingGenerator.PrepareHybrid(
                            dataDirectory,
                            options,
                            _state.ResolveVolumeLabel(OpticalDiscFamily.Dvd),
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
                        ? L.F($"DVD · {options.OutputSectors} 扇区 · {options.ContentLength / (1024.0 * 1024.0):F1} MiB")
                        : L.F($"DVD 混合盘 · {hybridPlan.FileCount} 个文件（{(_state.DvdFilesAtOuterRing ? L.T("外圈") : L.T("内圈"))}）")
                            + L.F($" · 绘图 LBA {hybridPlan.DrawingStartLba}–{hybridPlan.DrawingEndLbaExclusive - 1}")
                            + $" · {options.ContentLength / (1024.0 * 1024.0):F1} MiB";
                }
                else if (IsCdExtraBurn)
                {
                    if (_opticalDiscBurner is not ICdExtraBurner)
                        throw new NotSupportedException(L.T("当前的刻录后端不支持 CD-Extra 刻录；可以在导出页生成图片区段 ISO 后用其他软件刻录。"));
                    CdDiscParameters parameters = ReadCdGeneratedParameters();
                    parameters = parameters with
                    {
                        SourceImageOuterRadiusMm = parameters.SamplingImageOuterRadiusMm,
                        UsableSectors = StreamBurnCapacity.Resolve(parameters.OutputSectors, capacity.WritableSectors, mediaKind),
                    };
                    IReadOnlyList<string> music = _state.CdMusicTracks;
                    if (music.Count == 0)
                        throw new ArgumentException(L.T("CD-Extra 需要至少一首音乐：请在设计页添加 WAV 音乐，或切换回“整盘图片”。"));
                    string dataDirectory = _state.CdDataDirectory.Trim();
                    string volumeLabel = _state.ResolveVolumeLabel(OpticalDiscFamily.CompactDisc);
                    bool filesAtOuterRing = _state.CdFilesAtOuterRing;
                    ToneProfile tone = _state.Tone;
                    bool testWrite = CdExtraTestWrite;
                    OpticalWriteSpeed? speed = selectedWriteSpeed.Speed;
                    (CdExtraBurnRequest Request, CdExtraPlan Plan) prepared = await StaWorker.RunAsync(() =>
                    {
                        CdExtraPlan extra = CdExtraPlanner.Plan(parameters, music,
                            dataDirectory.Length == 0 ? null : dataDirectory, volumeLabel, filesAtOuterRing);
                        return (CreateCdExtraBurnRequest(compositePath, parameters, extra, dataDirectory, volumeLabel,
                            filesAtOuterRing, tone, device.Id, speed, testWrite), extra);
                    });
                    cdExtraRequest = prepared.Request;
                    CdExtraLayout layout = prepared.Plan.Layout!;
                    contentDescription = L.F($"CD-Extra · 第 1 区段 {music.Count} 首音乐（{CdMusicViewModel.FormatDuration(prepared.Plan.MusicDuration)}）")
                        + L.F($" · 第 2 区段图片预计从 LBA {layout.Session2StartLba:N0} 开始，约占盘面 {layout.PictureAreaFraction:P0}")
                        + (testWrite ? L.T(" · 模拟写入（不改变盘片）") : string.Empty);
                }
                else
                {
                    CdDiscParameters parameters = ReadCdGeneratedParameters();
                    parameters = parameters with
                    {
                        SourceImageOuterRadiusMm = parameters.SamplingImageOuterRadiusMm,
                        UsableSectors = StreamBurnCapacity.Resolve(parameters.OutputSectors, capacity.WritableSectors, mediaKind),
                    };
                    request = CreateCdBurnRequest(
                        compositePath,
                        parameters,
                        _state.CdDataDirectory,
                        _state.ResolveVolumeLabel(OpticalDiscFamily.CompactDisc),
                        _state.CdFilesAtOuterRing,
                        _state.CdInterleave,
                        _state.Tone,
                        device.Id,
                        selectedWriteSpeed.Speed);
                    contentDescription = request.MediaKind == OpticalBurnMediaKind.CdData
                        ? L.F($"CD 数据盘（DAO） · 文件位于{(_state.CdFilesAtOuterRing ? L.T("外圈") : L.T("内圈"))}")
                        : L.T("CD 音轨");
                    contentDescription += L.F($" · {parameters.OutputSectors} 扇区 · {request.ContentLength / (1024.0 * 1024.0):F1} MiB");
                }
            }

            // In-app danger modal; default focus sits on the refusing button.
            string warning = cdExtraRequest is null
                ? L.T("仅允许空白盘。开始后取消或断电可能使盘片报废。是否继续？")
                : cdExtraRequest.TestWrite
                    ? L.T("模拟写入：光驱按 Track-at-Once 走完全部写入命令，但不会改变盘片，也不会关闭区段。需要放入空白 CD-R。是否继续？")
                    : L.T("仅允许空白 CD-R 或已擦除的 CD-RW。先写入并关闭音乐区段，再按光驱报告的地址生成并写入图片区段，最后封盘。中途取消或断电可能使盘片报废；若音乐区段已关闭，音乐仍可在 CD 机播放。是否继续？");
            bool confirmed = await _shell.ConfirmAsync(
                L.T("确认开始流式刻录"),
                L.F($"即将写入：{device.DisplayName}\n{contentDescription}\n")
                    + L.F($"请求速度：{selectedWriteSpeed.DisplayName}\n\n")
                    + warning,
                L.T("确认刻录"));
            if (!confirmed)
            {
                return;
            }

            if (_shell.IsBusy || preparationRevision != _burnPreparationRevision || !ConfirmChecked)
            {
                _shell.ShowToast(L.T("操作未执行"), L.T("刻录来源或选项已变化，请重新确认后开始。"), ToastKind.Info);
                return;
            }

            ownsBusyState = true;
            _shell.SetBusy(true);
            IsBurnRunning = true;
            CancellationTokenSource burnCancellation = new();
            _burnCancellation = burnCancellation;
            _shell.BeginBurnDockJob(cdExtraRequest is null ? request!.MediaKind : OpticalBurnMediaKind.CdAudio,
                cdExtraRequest?.PredictedTotalBytes ?? request!.ContentLength);
            _shell.AppendLog(
                L.F($"\n[{DateTime.Now:HH:mm:ss}] 开始流式刻录：{contentDescription}；")
                + L.F($"设备={device.DisplayName}；请求速度={selectedWriteSpeed.DisplayName}。\n"));
            Progress<OpticalBurnProgress> progress = new(value =>
            {
                if (!IsBurnRunning || !ReferenceEquals(_burnCancellation, burnCancellation))
                {
                    return;
                }

                _shell.ReportBurnProgress(value, cancellationRequested: burnCancellation.IsCancellationRequested);
            });
            OpticalBurnResult result;
            if (cdExtraRequest is not null)
            {
                CdExtraBurnResult extra = await ((ICdExtraBurner)_opticalDiscBurner).BurnCdExtraAsync(
                    cdExtraRequest, progress, burnCancellation.Token);
                result = new OpticalBurnResult(extra.DeviceId, OpticalBurnMediaKind.CdAudio,
                    extra.AudioBytesWritten + extra.DataBytesWritten, extra.Elapsed, extra.ActualWriteSpeed);
                _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] CD-Extra：音轨起点 LBA {string.Join(", ", extra.AudioTrackStarts)}；")
                    + L.F($"第 2 区段起点 LBA {extra.Session2StartLba}{(extra.TestWrite ? L.T("（模拟写入，未改变盘片）") : string.Empty)}。\n"));
            }
            else
            {
                result = await _opticalDiscBurner.BurnAsync(
                    request!,
                    progress,
                    burnCancellation.Token);
            }
            _shell.CompleteDockJob(L.T("完成：流式刻录 ") + contentDescription);
            _shell.AppendLog(
                L.F($"[{DateTime.Now:HH:mm:ss}] 流式刻录完成：{result.BytesWritten} 字节，")
                + L.F($"耗时 {result.Elapsed.TotalSeconds:F1} 秒")
                + (result.ActualWriteSpeed is null
                    ? "。\n"
                    : L.F($"，实际速度={FormatBurnWriteSpeed(result.ActualWriteSpeed, mediaKind)}。\n")));
            ConfirmChecked = false;
            if (cdExtraRequest?.TestWrite == true)
                _shell.ShowToast(L.T("模拟写入完成"), L.T("光驱接受了全部写入命令，盘片没有被改变。"), ToastKind.Success);
            else
                _shell.ShowToast(L.T("流式刻录完成"), L.T("流式刻录已经完成，可以取出光盘。"), ToastKind.Success);
        }
        catch (OperationCanceledException exception)
        {
            if (ownsBusyState) _shell.AbortDockJob(L.T("已取消：流式刻录"), expandLog: false);
            _shell.AppendLog(
                L.F($"[{DateTime.Now:HH:mm:ss}] 已取消流式刻录；当前盘片可能无法继续使用。\n"));
            _shell.AppendLog(OpticalBurnDiagnostics.FormatException(exception) + "\n");
        }
        catch (Exception exception)
        {
            if (ownsBusyState) _shell.AbortDockJob(L.T("失败：流式刻录"), expandLog: true);
            _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] 流式刻录失败：{exception.Message}\n"));
            _shell.AppendLog(OpticalBurnDiagnostics.FormatException(exception) + "\n");
            _shell.ShowToast(
                L.T("流式刻录未完成"),
                L.T("详情见窗口底部的任务日志。") + L.T("\n\n如果写入已经开始，当前盘片可能无法继续使用。"),
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
            OnPropertyChanged(nameof(CanEditDvdReadCompatibility));
            UpdateActionState();
        }
    }

    /// <summary>Path, size and modification time of every music file: replacing a WAV after
    /// ticking the confirmation must re-arm it, because the burned samples would differ.
    /// The files are read again only for a new list or an explicit refresh; the burn itself
    /// re-checks every file before the first write.</summary>
    private string DescribeMusicIdentity(bool refreshFiles)
    {
        if (!_state.CdExtraEnabled) return string.Empty;
        IReadOnlyList<string> tracks = _state.CdMusicTracks;
        if (!refreshFiles && ReferenceEquals(tracks, _musicIdentityTracks)) return _musicIdentity;
        _musicIdentityTracks = tracks;
        return _musicIdentity = string.Join("|", tracks.Select(path =>
        {
            try
            {
                FileInfo file = new(path);
                return file.Exists ? $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : $"{path}:missing";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                return $"{path}:unreadable";
            }
        }));
    }

    /// <summary>
    /// CD-Extra request: the music tracks stream their padded PCM; the picture session is
    /// generated only after the recorder reports where session 2 starts, so the picture and
    /// its filesystem use the measured address rather than the prediction. In a simulated
    /// burn the predicted address is used.
    /// </summary>
    internal static CdExtraBurnRequest CreateCdExtraBurnRequest(
        string imagePath,
        CdDiscParameters parameters,
        CdExtraPlan plan,
        string? dataDirectory,
        string volumeLabel,
        bool filesAtOuterRing,
        ToneProfile tone,
        string deviceId,
        OpticalWriteSpeed? writeSpeed,
        bool testWrite = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        parameters.Validate();
        if (!plan.IsValid)
        {
            throw new ArgumentException(CdExtraPlanner.DescribeProblem(plan));
        }

        ToneProfile capturedTone = tone.Normalized();
        string? folder = string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory.Trim();
        string label = string.IsNullOrWhiteSpace(volumeLabel) ? "CD_IMAGE" : volumeLabel.Trim();
        CdMusicTrackInfo[] tracks = plan.Tracks.Select(track => track.Info!).ToArray();
        long[] sectors = tracks.Select(track => track.RecordedSectors).ToArray();
        // PrepareAudio pins and verifies every file before the first WRITE; the producers
        // then read the samples through those handles, so neither an edit nor a changed
        // timestamp can stop the music session half-way.
        PinnedMusic pinned = new();
        CdExtraAudioTrack[] audio = tracks
            .Select(track => new CdExtraAudioTrack(track.RecordedSectors,
                (output, token) => pinned.WritePaddedPcm(track, output, token)))
            .ToArray();
        CdExtraLayout predicted = plan.Layout!;
        return new CdExtraBurnRequest(deviceId, audio, start =>
            {
                CdExtraLayout layout = start.Simulated
                    ? predicted
                    : CdExtraLayout.Plan(parameters, sectors, start.NextWritableAddress, start.FreeBlocks);
                CdDataLayoutPlan data = CdDataImageGenerator.PrepareSessionLayout(parameters,
                    layout.Session2StartLba, layout.DataTrackSectors, folder, label, filesAtOuterRing);
                return new CdExtraDataContent(data.TrackSectors, (output, token) =>
                    CdDataImageGenerator.GenerateToStream(imagePath, output, parameters, data,
                        cancellationToken: token, tone: capturedTone));
            },
            predicted.Session2StartLba,
            plan.DataPlan!.TrackSectors,
            writeSpeed,
            TestWrite: testWrite,
            PrepareAudio: () => pinned.Pin(tracks));
    }

    /// <summary>Hands the music files pinned by PrepareAudio to the track producers. Without a
    /// pin (a recorder that never calls PrepareAudio) each producer verifies its file itself.</summary>
    private sealed class PinnedMusic
    {
        private CdMusicPin? _pin;

        internal IDisposable Pin(IReadOnlyList<CdMusicTrackInfo> tracks)
        {
            CdMusicPin pin = CdMusicFile.PinUnchanged(tracks);
            Volatile.Write(ref _pin, pin);
            return pin;
        }

        internal void WritePaddedPcm(CdMusicTrackInfo track, Stream output, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _pin) is { } pin) pin.WritePaddedPcm(track, output, cancellationToken);
            else CdMusicFile.WritePaddedPcm(track, output, cancellationToken);
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
        return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.CdData, checked(parameters.OutputSectors * 2048),
            (output, token) => CdDataImageGenerator.GenerateToStream(imagePath, output, parameters, plan,
                cancellationToken: token, tone: capturedTone), writeSpeed);
    }

    // Request construction is separate from physical writing so the captured
    // calibration content can also be validated with an ordinary memory stream.
    internal static OpticalBurnRequest CreateCalibrationBurnRequest(
        CalibrationTarget target,
        bool cdInterleave,
        string deviceId,
        OpticalWriteSpeed? writeSpeed,
        long? writableSectors = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Parameters.Kind == CalibrationDiscKind.BluRay)
        {
            BluRayDiscParameters disc = CalibrationViewModel.BluRayParameters(target.Parameters);
            uint outputSectors = writableSectors.HasValue
                ? checked((uint)StreamBurnCapacity.Resolve(disc.OutputSectors, writableSectors.Value, OpticalBurnMediaKind.BluRayData))
                : disc.OutputSectors;
            BluRayPatternRangeMapping mapping = CalibrationViewModel.BluRayPatternMapping(target);
            return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.BluRayData, checked((long)outputSectors * BluRayDiscParameters.BytesPerSector),
                (output, token) => BluRayStreamingGenerator.GenerateToStream(
                    output,
                    new BluRayStreamingOptions(disc, 0, outputSectors),
                    mapping,
                    progress: null,
                    cancellationToken: token),
                writeSpeed);
        }

        if (target.Parameters.Kind == CalibrationDiscKind.Dvd)
        {
            DvdStreamingOptions options = CalibrationViewModel.DvdOptions(target);
            if (writableSectors.HasValue)
                options = options with { UsableSectors = checked((uint)StreamBurnCapacity.Resolve(options.OutputSectors, writableSectors.Value, OpticalBurnMediaKind.DvdData)) };
            options.Validate();
            return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.DvdData, options.ContentLength,
                (output, token) => DvdStreamingGenerator.GeneratePattern(target, output, options,
                    cancellationToken: token), writeSpeed);
        }

        CdDiscParameters parameters = CdDiscParameters.FromCalibration(target);
        if (writableSectors.HasValue)
            parameters = parameters with { UsableSectors = StreamBurnCapacity.Resolve(parameters.OutputSectors, writableSectors.Value, OpticalBurnMediaKind.CdAudio) };
        parameters.Validate();
        return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.CdAudio, parameters.TotalBytes,
            (output, token) => CdTrackGenerator.GeneratePatternToStream(target, output, cdInterleave,
                cancellationToken: token, audioByteOrder: CdAudioByteOrder.LittleEndian,
                usableSectors: parameters.UsableSectors), writeSpeed);
    }

    private void CancelStreamBurn()
    {
        CancelBurnCommand.NotifyCanExecuteChanged();
        _shell.JobStatusText = L.T("正在请求停止刻录，请不要取出盘片…");
        _burnCancellation?.Cancel();
    }

    private CdDiscParameters ReadCdGeneratedParameters()
    {
        CdDiscParameters parameters = new(
            ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, L.T("CD 内半径")),
            ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, L.T("CD 外半径")),
            ParameterParser.ParsePositiveLong(_state.CdSectors, L.T("CD 扇区数")),
            PitchLinear: ParameterParser.ParseDouble(_state.CdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.CdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.CdPitchCubic, L.T("相对三次项")),
            UsableSectors: _state.GetUsableSectors(OpticalDiscFamily.CompactDisc));
        parameters.Validate();
        return parameters;
    }

    private BluRayDiscParameters ReadBluRayParameters()
    {
        long totalSectors = ParameterParser.ParsePositiveLong(_state.BdTotalSectors, L.T("蓝光总扇区数"));
        if (totalSectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(L.T("蓝光总扇区数"), L.T("总扇区数超出蓝光引擎支持范围。"));
        }

        BluRayDiscParameters disc = new(
            checked((uint)totalSectors),
            1,
            ParameterParser.ParsePositiveDouble(_state.BdInnerRadius, L.T("蓝光内半径")),
            ParameterParser.ParsePositiveDouble(_state.BdOuterRadius, L.T("蓝光外半径")),
            PitchLinear: ParameterParser.ParseDouble(_state.BdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.BdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.BdPitchCubic, L.T("相对三次项")),
            UsableSectors: checked((uint)_state.GetUsableSectors(OpticalDiscFamily.BluRay)));
        disc.Validate();
        return disc;
    }

    private DvdStreamingOptions ReadDvdStreamingOptions()
    {
        int totalSectors = ParameterParser.ParsePositiveInt(_state.DvdTotalSectors, L.T("DVD 总扇区数"));
        DvdStreamingOptions options = new(
            checked((uint)totalSectors),
            ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, L.T("DVD 内半径")),
            ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, L.T("DVD 外半径")),
            PitchLinear: ParameterParser.ParseDouble(_state.DvdPitchLinear, L.T("DVD 轨距一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.DvdPitchQuadratic, L.T("DVD 轨距二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.DvdPitchCubic, L.T("DVD 轨距三次项")),
            UsableSectors: checked((uint)_state.GetUsableSectors(OpticalDiscFamily.Dvd)),
            Tone: _state.DvdTone,
            ReadCompatibility: _state.DvdReadCompatibility);
        options.Validate();
        return options;
    }

    /// <summary>One selectable write speed. The automatic entry names itself in the current
    /// language, so the label is computed and can notify without replacing the list (which
    /// would disturb the selection and the burn confirmation).</summary>
    // Selector indexes items by their hash. Notification subscriptions must not change an
    // option's identity when DisplayMemberPath creates or removes its label binding.
    public sealed class BurnWriteSpeedOption(
        OpticalWriteSpeed? Speed,
        string? SpeedLabel) : INotifyPropertyChanged
    {
        public OpticalWriteSpeed? Speed { get; } = Speed;
        public string? SpeedLabel { get; } = SpeedLabel;

        public static BurnWriteSpeedOption Automatic { get; } = new(null, null);

        public string DisplayName => Speed is null
            ? L.T("自动（最低速率，推荐）")
            : SpeedLabel ?? string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void RefreshLabel()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
    }
}
