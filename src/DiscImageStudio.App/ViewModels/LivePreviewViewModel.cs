using DiscImageStudio.Localization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;
using DvdImageSolver.Solver;

namespace DiscImageStudio.ViewModels;

/// Live read-back preview pipeline: 450ms debounce, revision-based stale-result discard,
/// running/pending coalescing, cancel-on-change. Drawn on STA worker threads.
///
/// Two input modes share that pipeline:
/// - Calibration preview (default): the input is the source picture and two parameter sets
///   describe it — the generated geometry lays it out, the measured geometry reads it back.
///   Rendering never touches the multi-hundred-megabyte artifact, which is what makes parameter
///   iteration cheap; the price is that the picture is a projection of the source, not a
///   read-back of the bytes that were actually written.
/// - Read-back simulation: the input is the generated burn artifact (CD track or DVD ISO,
///   matching the cdimage calibration model), so the picture shows what the disc really
///   carries. One parameter set describes the measured disc geometry.
public partial class LivePreviewViewModel : ObservableObject
{
    private const int MaximumPreviewSize = 8192;

    private readonly DispatcherTimer _livePreviewTimer;
    private readonly DiscParametersState _state;
    private readonly ShellViewModel _shell;

    /// <summary>Exposed for the view's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;

    /// <summary>The parameter panel. It is a lens over both disc families, so the family switch
    /// re-points it instead of swapping in a second copy of the panel.</summary>
    public PreviewInspectorViewModel Inspector { get; }

    /// <summary>Workspace navigation for the page header's "下一步" action.</summary>
    public IRelayCommand<Workspace> NavigateCommand => _shell.NavigateCommand;

    /// <summary>Family chip in the page header.</summary>
    public string DiscFamilyLabel => SelectedFamily switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };

    /// <summary>Opens the project settings dialog, which owns the generation geometry. The
    /// preview shows that geometry read-only and links here instead of editing it a second
    /// time.</summary>
    public IAsyncRelayCommand EditGeometryCommand => _shell.EditProjectCommand;

    private CancellationTokenSource? _livePreviewCancellation;
    private bool _livePreviewRefreshRunning;
    private bool _livePreviewRefreshPending;
    private bool _isLivePreviewReady;
    private bool _suppressDiscTypeAutoUpdate;
    private bool _suppressPreviewModeAutoUpdate;
    private int _livePreviewRevision;
    private string? _lastLivePreviewPath;
    // True only while the canvas shows a picture the export action can write out: the
    // read-back simulation or the calibration projection. A job's own PNG output takes the
    // canvas over without a file of its own to export, so the action falls back to disabled.
    private bool _downloadablePreview;
    // Catalog key of the canvas title while a result is showing, so a language switch can
    // re-resolve it without rebuilding the picture.
    private string? _resultTitleKey;
    // Completion line of the result currently on screen. It is composed again after a
    // language switch; recomputing the picture itself would cancel a read-back for text only.
    private Func<string>? _readyStatus;
    // Failure line of the most recent refresh, kept for the same reason: a switch must
    // re-word the error rather than replace it with the idle hint.
    private string? _livePreviewFailure;
    private string? _calibratedTrackPath;
    private string? _calibratedIsoPath;
    private string? _calibratedBluRayPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscFamilyLabel), nameof(IsDvdSelected), nameof(IsCdSelected),
        nameof(IsBluRaySelected))]
    private OpticalDiscFamily _selectedFamily = OpticalDiscFamily.CompactDisc;

    public bool IsCdSelected => SelectedFamily == OpticalDiscFamily.CompactDisc;

    public bool IsDvdSelected => SelectedFamily == OpticalDiscFamily.Dvd;

    public bool IsBluRaySelected => SelectedFamily == OpticalDiscFamily.BluRay;

    /// <summary>False renders the generated artifact (read-back simulation), true renders the
    /// source picture through the generated/measured parameter pair.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadbackPreview))]
    private bool _isCalibrationPreview = true;

    /// <summary>Inverse of <see cref="IsCalibrationPreview"/>, so the view can trigger its
    /// read-back panels without an inverse-binding converter. Settable because the input switch
    /// puts this mode on its right-hand option, and the switch's <c>IsSecondSelected</c> is
    /// two-way: a write lands here and flips the single source of truth.</summary>
    public bool IsReadbackPreview
    {
        get => !IsCalibrationPreview;
        set => IsCalibrationPreview = !value;
    }

    [ObservableProperty]
    private string _resultPreviewTitle = L.T("预览 - CD");

    [ObservableProperty]
    private string _resultPreviewPath = string.Empty;

    [ObservableProperty]
    private string _resultPreviewPlaceholderText = L.T("请先选择生成的 CD 音轨或 ISO。");

    [ObservableProperty]
    private string _statusText = L.T("等待生成的 CD 音轨或 ISO");

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _resultImage;

    [ObservableProperty]
    private bool _hasResultImage;

    [ObservableProperty]
    private bool _hasFileRegionOverlay;

    public LivePreviewViewModel(Dispatcher dispatcher, ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        Inspector = new PreviewInspectorViewModel(shell, state);
        _state.PropertyChanged += State_PropertyChanged;
        _livePreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450),
        };
        _livePreviewTimer.Tick += (_, _) =>
        {
            _livePreviewTimer.Stop();
            _ = RefreshAsync();
        };
        RefreshCommand = new RelayCommand(RefreshNow);
        DownloadPreviewCommand = new RelayCommand(DownloadPreview, CanDownloadPreview);
    }

    internal bool IsLivePreviewReady => _isLivePreviewReady;

    public RelayCommand RefreshCommand { get; }

    /// <summary>Writes the current preview next to the picture it was derived from.</summary>
    public RelayCommand DownloadPreviewCommand { get; }

    /// <summary>Picks the generated artifact to read back, matching the export page's pickers.</summary>
    public RelayCommand BrowseCdTrackCommand => _shell.BrowseCdTrackCommand;

    public RelayCommand BrowseDvdIsoCommand => _shell.BrowseDvdIsoCommand;

    /// <summary>
    /// Switching to another artifact adopts the geometry it was generated with, so an imported
    /// track or ISO starts from its own parameters instead of whatever was fitted before.
    /// </summary>
    private void State_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscParametersState.CdTrackPath))
        {
            AdoptTrackMetadata(_state.CdTrackPath.Trim());
            DownloadPreviewCommand.NotifyCanExecuteChanged();
            RaiseEmptyStateAction();
        }
        else if (e.PropertyName == nameof(DiscParametersState.DvdIsoPath))
        {
            AdoptIsoMetadata(_state.DvdIsoPath.Trim());
            DownloadPreviewCommand.NotifyCanExecuteChanged();
            RaiseEmptyStateAction();
        }
        else if (e.PropertyName == nameof(DiscParametersState.BdIsoPath))
        {
            AdoptBluRayMetadata(_state.BdIsoPath.Trim());
            DownloadPreviewCommand.NotifyCanExecuteChanged();
            RaiseEmptyStateAction();
        }
        else if (e.PropertyName is nameof(DiscParametersState.CdImagePath)
            or nameof(DiscParametersState.DvdImagePath)
            or nameof(DiscParametersState.BdImagePath))
        {
            // The calibration preview exports next to its source picture, so editing that path
            // can change whether the export has a destination at all.
            DownloadPreviewCommand.NotifyCanExecuteChanged();
            RaiseEmptyStateAction();
        }
    }

    // ---- Monitor empty state: the placeholder plus the one jump that fills the gap ----

    /// <summary>The workspace that can produce what the empty monitor is waiting for, or null
    /// once there is nothing to wait for (a result is showing, or the input exists but the
    /// picture is held back by something a jump cannot fix, like an invalid parameter).</summary>
    public Workspace? EmptyStateActionWorkspace
    {
        get
        {
            if (HasResultImage)
            {
                return null;
            }

            if (IsCalibrationPreview)
            {
                string imagePath = SelectedFamily switch
                {
                    OpticalDiscFamily.Dvd => _state.DvdImagePath,
                    OpticalDiscFamily.BluRay => _state.BdImagePath,
                    _ => _state.CdImagePath,
                };
                imagePath = imagePath.Trim();
                return imagePath.Length == 0 || !File.Exists(imagePath) ? Workspace.Design : null;
            }

            string artifactPath = SelectedFamily switch
            {
                OpticalDiscFamily.Dvd => _state.DvdIsoPath,
                OpticalDiscFamily.BluRay => _state.BdIsoPath,
                _ => _state.CdTrackPath,
            };
            artifactPath = artifactPath.Trim();
            return artifactPath.Length == 0 || !File.Exists(artifactPath) ? Workspace.Export : null;
        }
    }

    /// <summary>Empty label when no jump applies, which keeps the empty-state card's action
    /// hidden instead of navigating to a null workspace.</summary>
    public string EmptyStateActionLabel => EmptyStateActionWorkspace switch
    {
        Workspace.Design => L.T("前往设计"),
        Workspace.Export => L.T("前往导出"),
        _ => string.Empty,
    };

    private void RaiseEmptyStateAction()
    {
        OnPropertyChanged(nameof(EmptyStateActionWorkspace));
        OnPropertyChanged(nameof(EmptyStateActionLabel));
    }

    private void AdoptTrackMetadata(string trackPath)
    {
        if (trackPath.Length == 0
            || string.Equals(trackPath, _calibratedTrackPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _calibratedTrackPath = trackPath;
        CdDataImageMetadata? dataMetadata = CdDataImageMetadata.TryLoad(trackPath);
        if (dataMetadata is not null)
        {
            CdDiscParameters parameters = dataMetadata.Parameters;
            ToneProfile dataTone = dataMetadata.Tone.Normalized();
            _state.CdSectors = parameters.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _state.CdUsableSectors = parameters.OutputSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _state.CdInnerRadius = FormatNumber(parameters.InnerRadiusMm);
            _state.CdOuterRadius = FormatNumber(parameters.OuterRadiusMm);
            _state.CdPitchLinear = FormatNumber(parameters.PitchLinear);
            _state.CdPitchQuadratic = FormatNumber(parameters.PitchQuadratic);
            _state.CdPitchCubic = FormatNumber(parameters.PitchCubic);
            _state.CdFilesAtOuterRing = dataMetadata.FilesAtOuterRing;
            _state.ToneDitherStrength = dataTone.DitherStrength;
            _state.ToneLevels = dataTone.Levels;
            _state.ToneBrightness = dataTone.Brightness;
            _state.ToneSrgbLinearization = dataTone.SrgbLinearization;
            _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] 已载入 {Path.GetFileName(trackPath)} 的 CD 数据盘几何与文件位置。\n"));
            return;
        }
        CdTrackMetadata? metadata = CdTrackMetadata.TryLoad(trackPath);
        if (metadata is null)
        {
            return;
        }

        DiscParametersState state = _state;
        string sectors = metadata.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string innerRadius = FormatNumber(metadata.InnerRadiusMm);
        string outerRadius = FormatNumber(metadata.OuterRadiusMm);
        ToneProfile tone = new ToneProfile(
            metadata.ToneDitherStrength,
            metadata.ToneLevels,
            metadata.ToneBrightness,
            metadata.ToneSrgbLinearization).Normalized();
        if (state.CdUsableSectors.Trim() == (metadata.UsableSectors ?? metadata.Sectors).ToString(System.Globalization.CultureInfo.InvariantCulture)
            && state.CdSectors.Trim() == sectors
            && state.CdInnerRadius.Trim() == innerRadius
            && state.CdOuterRadius.Trim() == outerRadius
            && state.CdPitchLinear.Trim() == FormatNumber(metadata.PitchLinear)
            && state.CdPitchQuadratic.Trim() == FormatNumber(metadata.PitchQuadratic)
            && state.CdPitchCubic.Trim() == FormatNumber(metadata.PitchCubic)
            && state.CdInterleave == metadata.Interleaved
            && state.ToneDitherStrength == tone.DitherStrength
            && state.ToneLevels == tone.Levels
            && state.ToneBrightness == tone.Brightness
            && state.ToneSrgbLinearization == tone.SrgbLinearization)
        {
            return;
        }

        state.CdSectors = sectors;
        state.CdUsableSectors = (metadata.UsableSectors ?? metadata.Sectors).ToString(System.Globalization.CultureInfo.InvariantCulture);
        state.CdInnerRadius = innerRadius;
        state.CdOuterRadius = outerRadius;
        state.CdPitchLinear = FormatNumber(metadata.PitchLinear);
        state.CdPitchQuadratic = FormatNumber(metadata.PitchQuadratic);
        state.CdPitchCubic = FormatNumber(metadata.PitchCubic);
        state.CdInterleave = metadata.Interleaved;
        state.ToneDitherStrength = tone.DitherStrength;
        state.ToneLevels = tone.Levels;
        state.ToneBrightness = tone.Brightness;
        state.ToneSrgbLinearization = tone.SrgbLinearization;
        _shell.AppendLog(
            L.F($"[{DateTime.Now:HH:mm:ss}] 已按 {System.IO.Path.GetFileName(trackPath)} 的生成参数载入标定几何：")
            + L.F($"r0={innerRadius} mm, r1={outerRadius} mm, {sectors} 扇区，交织={metadata.Interleaved}，")
            + L.F($"影调={tone.Levels} 级/{tone.DitherStrength * 100:0}% 抖动。\n"));
    }

    private void AdoptIsoMetadata(string isoPath)
    {
        if (isoPath.Length == 0
            || string.Equals(isoPath, _calibratedIsoPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _calibratedIsoPath = isoPath;
        DvdImageMetadata? metadata = DvdImageMetadata.TryLoad(isoPath);
        if (metadata is null)
        {
            return;
        }

        DiscParametersState state = _state;
        string totalSectors = metadata.TotalSectors.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string innerRadius = FormatNumber(metadata.InnerRadiusMm);
        string outerRadius = FormatNumber(metadata.OuterRadiusMm);
        if (state.DvdUsableSectors.Trim() == (metadata.UsableSectors ?? metadata.TotalSectors).ToString(System.Globalization.CultureInfo.InvariantCulture)
            && state.DvdTotalSectors.Trim() == totalSectors
            && state.DvdInnerRadius.Trim() == innerRadius
            && state.DvdOuterRadius.Trim() == outerRadius
            && state.DvdPitchLinear.Trim() == FormatNumber(metadata.PitchLinear)
            && state.DvdPitchQuadratic.Trim() == FormatNumber(metadata.PitchQuadratic)
            && state.DvdPitchCubic.Trim() == FormatNumber(metadata.PitchCubic)
            && state.DvdFilesAtOuterRing == metadata.FilesAtOuterRing)
        {
            return;
        }

        state.DvdTotalSectors = totalSectors;
        state.DvdUsableSectors = (metadata.UsableSectors ?? metadata.TotalSectors).ToString(System.Globalization.CultureInfo.InvariantCulture);
        state.DvdInnerRadius = innerRadius;
        state.DvdOuterRadius = outerRadius;
        state.DvdPitchLinear = FormatNumber(metadata.PitchLinear);
        state.DvdPitchQuadratic = FormatNumber(metadata.PitchQuadratic);
        state.DvdPitchCubic = FormatNumber(metadata.PitchCubic);
        state.DvdFilesAtOuterRing = metadata.FilesAtOuterRing;
        _shell.AppendLog(
            L.F($"[{DateTime.Now:HH:mm:ss}] 已按 {System.IO.Path.GetFileName(isoPath)} 的生成参数载入标定几何：")
            + L.F($"r0={innerRadius} mm, r1={outerRadius} mm, {totalSectors} 总扇区；{state.DvdPitchSummary}\n"));
    }

    /// <summary>The BD sidecar restores the geometry, drawing range and mapping polarity used
    /// to produce the ISO. Without it the current project geometry stays in place and the
    /// projection still works, but the file may have been made with different parameters.</summary>
    private void AdoptBluRayMetadata(string isoPath)
    {
        if (isoPath.Length == 0
            || string.Equals(isoPath, _calibratedBluRayPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _calibratedBluRayPath = isoPath;
        BluRayIsoMetadata? metadata = BluRayIsoMetadata.TryLoad(isoPath);
        if (metadata is null)
        {
            _shell.AppendLog(
                L.F($"[{DateTime.Now:HH:mm:ss}] {System.IO.Path.GetFileName(isoPath)} 没有可用的蓝光 sidecar，")
                + L.T("预览继续使用当前工程几何。\n"));
            return;
        }

        DiscParametersState state = _state;
        string sectors = metadata.TotalSectors.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string innerRadius = FormatNumber(metadata.InnerRadiusMm);
        string outerRadius = FormatNumber(metadata.OuterRadiusMm);
        state.BdTotalSectors = sectors;
        state.BdUsableSectors = (metadata.UsableSectors ?? metadata.TotalSectors).ToString(System.Globalization.CultureInfo.InvariantCulture);
        state.BdInnerRadius = innerRadius;
        state.BdOuterRadius = outerRadius;
        state.BdPitchLinear = FormatNumber(metadata.PitchLinear);
        state.BdPitchQuadratic = FormatNumber(metadata.PitchQuadratic);
        state.BdPitchCubic = FormatNumber(metadata.PitchCubic);
        state.BdActualPitchLinear = FormatNumber(metadata.PitchLinear);
        state.BdActualPitchQuadratic = FormatNumber(metadata.PitchQuadratic);
        state.BdActualPitchCubic = FormatNumber(metadata.PitchCubic);
        state.BdActualInnerRadius = innerRadius;
        state.BdActualOuterRadius = outerRadius;
        state.BdInvertMapping = metadata.InvertMapping;
        state.BdFilesAtOuterRing = metadata.FilesAtOuterRing;
        state.BdDrawingStartSector = metadata.DrawingStartSector.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        state.BdDrawingEndSectorExclusive = metadata.DrawingEndSectorExclusive.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        state.ToneDitherStrength = metadata.Tone.DitherStrength;
        state.ToneLevels = ToneProfile.TwoLevels;
        state.ToneBrightness = metadata.Tone.Brightness;
        state.ToneSrgbLinearization = metadata.Tone.SrgbLinearization;
        _shell.AppendLog(
            L.F($"[{DateTime.Now:HH:mm:ss}] 已按 {System.IO.Path.GetFileName(isoPath)} 的 sidecar 载入几何：")
            + L.F($"r0={innerRadius} mm, r1={outerRadius} mm, {sectors} 扇区，")
            + L.F($"绘图 {metadata.DrawingStartSector}..{metadata.DrawingEndSectorExclusive - 1}，")
            + L.F($"极性={(metadata.InvertMapping ? L.T("反转 3T/6T") : L.T("标准 3T/6T"))}。\n"));
    }

    private static string FormatNumber(double value)
        => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Disc-type selection: clear the stale canvas and reschedule.</summary>
    partial void OnSelectedFamilyChanged(OpticalDiscFamily value)
    {
        if (_suppressDiscTypeAutoUpdate)
        {
            return;
        }

        UpdateMode(clearResult: true);
        Schedule();
    }

    /// <summary>Configures the disc type without clearing the canvas (snapshot entry point).</summary>
    internal void SetDiscType(OpticalDiscFamily family, bool clearResult)
    {
        _suppressDiscTypeAutoUpdate = true;
        try
        {
            SelectedFamily = family;
        }
        finally
        {
            _suppressDiscTypeAutoUpdate = false;
        }

        UpdateMode(clearResult);
    }

    internal void SetDiscType(bool dvd, bool clearResult)
        => SetDiscType(dvd ? OpticalDiscFamily.Dvd : OpticalDiscFamily.CompactDisc, clearResult);

    /// <summary>Preview-mode semantics: the input changes, so the stale canvas goes and the
    /// new pipeline runs.</summary>
    partial void OnIsCalibrationPreviewChanged(bool value)
    {
        if (_suppressPreviewModeAutoUpdate)
        {
            return;
        }

        UpdateMode(clearResult: true);
        Schedule();
    }

    /// <summary>Configures the preview mode without clearing the canvas (snapshot entry point).</summary>
    internal void SetPreviewMode(bool calibration, bool clearResult)
    {
        _suppressPreviewModeAutoUpdate = true;
        try
        {
            IsCalibrationPreview = calibration;
        }
        finally
        {
            _suppressPreviewModeAutoUpdate = false;
        }

        UpdateMode(clearResult);
    }

    internal void OnWindowLoaded()
    {
        UpdateMode(clearResult: false);
        Schedule();
    }

    /// <summary>Re-reads the titles, placeholder and inspector hint after a language switch.
    /// A rendered picture is kept; only its caption changes.</summary>
    internal void Relocalize()
    {
        Inspector.Relocalize();
        RaiseEmptyStateAction();
        if (!HasResultImage)
        {
            // A scheduled or running refresh owns the status line; resetting the empty state
            // here would claim the preview is idle while the read-back is still working.
            if (!_livePreviewRefreshRunning && !_livePreviewTimer.IsEnabled)
            {
                // The error line is state, not chrome: UpdateMode drops it with the rest of
                // the empty-state text, so it is put back in the new language.
                string? failure = _livePreviewFailure;
                UpdateMode(clearResult: false);
                if (failure is not null)
                {
                    _livePreviewFailure = failure;
                    StatusText = L.F($"实时预览失败：{failure}");
                    ResultPreviewPlaceholderText = StatusText;
                }
            }

            return;
        }

        if (_readyStatus is not null)
        {
            StatusText = _readyStatus();
        }

        if (_resultTitleKey is { } key)
        {
            ResultPreviewTitle = L.T(key);
        }
    }

    internal void UpdateMode(bool clearResult)
    {
        HasFileRegionOverlay = false;
        _readyStatus = null;
        _livePreviewFailure = null;
        // The inspector is a lens over both families; every mode change routes through here, so
        // this is where it is re-pointed.
        Inspector.SetFamily(SelectedFamily);
        string discName = DiscFamilyLabel;
        string inputName = IsCalibrationPreview
            ? L.F($"{discName} 源图片")
            : SelectedFamily switch
            {
                OpticalDiscFamily.Dvd => L.T("生成的 DVD ISO"),
                OpticalDiscFamily.BluRay => L.T("生成的蓝光 ISO"),
                _ => L.T("生成的 CD 音轨或 ISO"),
            };
        ResultPreviewTitle = L.F($"预览 - {discName}");
        ResultPreviewPlaceholderText = L.F($"请先选择 {inputName}。");
        StatusText = L.F($"等待 {inputName}");
        _resultTitleKey = null;
        RaiseEmptyStateAction();

        if (!clearResult)
        {
            return;
        }

        ClearResult();
    }

    /// <summary>Drops the rendered picture, its legend, its export source and its temporary
    /// file. Every failed refresh routes through here first: a stale picture under an error
    /// line claims the disc still carries what the previous run produced.</summary>
    private void ClearResult()
    {
        HasFileRegionOverlay = false;
        ResultImage = null;
        HasResultImage = false;
        ResultPreviewPath = string.Empty;
        _downloadablePreview = false;
        _readyStatus = null;
        _livePreviewFailure = null;
        DownloadPreviewCommand.NotifyCanExecuteChanged();
        string? previousPath = _lastLivePreviewPath;
        _lastLivePreviewPath = null;
        TempFiles.TryDeleteTemporaryFile(previousPath);
    }

    private void SetReadyStatus(Func<string> compose)
    {
        _readyStatus = compose;
        StatusText = compose();
    }

    private void SetLivePreviewFailure(Exception exception)
    {
        ClearResult();
        _livePreviewFailure = exception.Message;
        StatusText = L.F($"实时预览失败：{exception.Message}");
        ResultPreviewPlaceholderText = StatusText;
    }

    /// <summary>An input problem detected before rendering starts (missing artifact, invalid
    /// geometry). It clears the picture exactly like a thrown failure, but the message is not
    /// kept for a language switch: it names the missing input rather than a broken pipeline.</summary>
    private void SetLivePreviewUnavailable(string message)
    {
        ClearResult();
        StatusText = message;
        ResultPreviewPlaceholderText = message;
    }

    internal void Schedule()
    {
        HasFileRegionOverlay = false;
        _readyStatus = null;
        _livePreviewFailure = null;
        if (!_shell.IsWindowLoaded)
        {
            return;
        }

        _livePreviewCancellation?.Cancel();
        _livePreviewTimer.Stop();
        _livePreviewRevision++;
        _isLivePreviewReady = false;
        StatusText = L.T("参数已变化，准备刷新…");
        _livePreviewTimer.Start();
    }

    internal void RefreshNow()
    {
        HasFileRegionOverlay = false;
        _readyStatus = null;
        _livePreviewFailure = null;
        _livePreviewTimer.Stop();
        _livePreviewCancellation?.Cancel();
        _livePreviewRevision++;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_livePreviewRefreshRunning)
        {
            _livePreviewRefreshPending = true;
            return;
        }

        _livePreviewRefreshRunning = true;
        try
        {
            do
            {
                HasFileRegionOverlay = false;
                _readyStatus = null;
                _livePreviewFailure = null;
                _livePreviewRefreshPending = false;
                int revision = _livePreviewRevision;
                switch (SelectedFamily)
                {
                    case OpticalDiscFamily.Dvd:
                        await RefreshDvdAsync(revision);
                        break;
                    case OpticalDiscFamily.BluRay:
                        await RefreshBluRayAsync(revision);
                        break;
                    default:
                        await RefreshCdAsync(revision);
                        break;
                }

                if (revision != _livePreviewRevision)
                {
                    _livePreviewRefreshPending = true;
                }
            }
            while (_livePreviewRefreshPending && _shell.IsWindowLoaded);
        }
        finally
        {
            _livePreviewRefreshRunning = false;
        }
    }

    private async Task RefreshCdAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        if (IsCalibrationPreview)
        {
            await RefreshCdCalibrationAsync(revision, cancellation.Token);
        }
        else
        {
            await RefreshCdReadbackAsync(revision, cancellation.Token);
        }
    }

    /// <summary>Read-back simulation: undo the generation's delay interleave, then map the
    /// logical bytes onto the spiral described by the calibration geometry.</summary>
    private async Task RefreshCdReadbackAsync(int revision, CancellationToken cancellationToken)
    {
        if (!TryReadCdSettings(
                out string trackPath,
                out CdDiscParameters parameters,
                out int outputSize,
                out int samplesPerSector,
                out string message))
        {
            SetLivePreviewUnavailable(message);
            return;
        }

        bool deinterleave = _state.CdInterleave;
        bool dataImage = Path.GetExtension(trackPath).Equals(".iso", StringComparison.OrdinalIgnoreCase);
        string? outputPath = null;
        StatusText = L.F($"正在刷新 {outputSize} px 实时预览…");
        bool keepOutput = false;
        try
        {
            outputPath = TempFiles.GetCacheFilePath($"cd-live-preview-{revision}.png");
            CdDataImageMetadata? metadata = dataImage ? CdDataImageMetadata.TryLoad(trackPath) : null;
            if (metadata is not null)
            {
                parameters = metadata.Parameters with
                {
                    InnerRadiusMm = parameters.InnerRadiusMm,
                    OuterRadiusMm = parameters.OuterRadiusMm,
                    Sectors = parameters.Sectors,
                };
            }
            bool overlay = metadata is not null && metadata.Parameters.Sectors == parameters.Sectors;
            await StaWorker.RunAsync(() =>
            {
                if (dataImage)
                {
                    CdDataImageGenerator.PreviewTrack(trackPath, outputPath, parameters,
                        outputSize, 2048 / samplesPerSector, cancellationToken);
                    if (overlay)
                        FileRegionPreviewOverlay.ApplyCd(outputPath, parameters,
                            metadata!.DrawingStartLba, metadata.DrawingEndLbaExclusive, cancellationToken);
                }
                else
                {
                    CdTrackGenerator.PreviewTrack(
                        trackPath, outputPath, parameters, outputSize,
                        CdDiscParameters.BytesPerSector / samplesPerSector, deinterleave, cancellationToken);
                }
                return 0;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, trackPath, "预览 - CD", overlay);
            keepOutput = true;
            SetReadyStatus(() => L.F($"已实时更新 · 读回模拟 · {(dataImage ? L.T("CD 数据盘") : deinterleave ? L.T("逆交织") : L.T("纯音轨"))}")
                + L.F($" · {outputSize} px · 每扇区 {samplesPerSector} 个采样"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetLivePreviewFailure(exception);
            _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] 实时预览失败：{exception}\n"));
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Calibration projection: the source picture is sampled where the generated
    /// geometry puts it and drawn where the measured geometry reads it. Only the source image
    /// is read, so a refresh costs the picture's size rather than the track's.</summary>
    private async Task RefreshCdCalibrationAsync(int revision, CancellationToken cancellationToken)
    {
        if (!TryReadCdWarpSettings(
                out string imagePath,
                out CdDiscParameters generated,
                out CdDiscParameters actual,
                out int outputSize,
                out int samplesPerSector,
                out ToneProfile tone,
                out string message))
        {
            SetLivePreviewUnavailable(message);
            return;
        }

        string? outputPath = null;
        StatusText = L.F($"正在刷新 {outputSize} px 标定预览…");
        bool keepOutput = false;
        try
        {
            outputPath = TempFiles.GetCacheFilePath($"cd-live-warp-{revision}.png");
            string dataDirectory = _state.CdDataDirectory.Trim();
            string volumeLabel = _state.CdVolumeLabel.Trim();
            bool filesAtOuterRing = _state.CdFilesAtOuterRing;
            CdDataLayoutPlan? plan = await StaWorker.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CdDataLayoutPlan? layout = dataDirectory.Length > 0
                    ? CdDataImageGenerator.PrepareLayout(dataDirectory, generated, volumeLabel, filesAtOuterRing)
                    : null;
                cancellationToken.ThrowIfCancellationRequested();
                if (layout is not null)
                {
                    CdDataImageGenerator.PreviewWarp(imagePath, outputPath, generated, actual,
                        outputSize, samplesPerSector, tone, layout, cancellationToken);
                    FileRegionPreviewOverlay.ApplyCd(outputPath, actual,
                        layout.DrawingStartLba, layout.DrawingEndLbaExclusive, cancellationToken);
                }
                else
                {
                    CdTrackGenerator.PreviewWarp(imagePath, outputPath, generated, actual,
                        outputSize, samplesPerSector, tone, cancellationToken);
                }
                return layout;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, imagePath, "预览 - CD", plan is not null);
            keepOutput = true;
            SetReadyStatus(() => L.F($"已实时更新 · 标定预览 · 灰度 {tone.Levels} 级")
                + L.F($" · 抖动 {tone.DitherStrength * 100:0}% · {outputSize} px")
                + L.F($" · 每扇区 {samplesPerSector} 个采样"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetLivePreviewFailure(exception);
            _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] 实时预览失败：{exception}\n"));
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    private async Task RefreshDvdAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        if (IsCalibrationPreview)
        {
            await RefreshDvdCalibrationAsync(revision, cancellation.Token);
        }
        else
        {
            await RefreshDvdReadbackAsync(revision, cancellation.Token);
        }
    }

    /// <summary>Read-back simulation: the solver classifies the generated ISO's payload
    /// bytes and splats them under the measured disc geometry.</summary>
    private async Task RefreshDvdReadbackAsync(int revision, CancellationToken cancellationToken)
    {
        string? outputPath = null;
        if (!TryBuildDvdReadbackOptions(
                out string isoPath,
                out IsoReadbackOptions options,
                out string message))
        {
            SetLivePreviewUnavailable(message);
            return;
        }

        int outputSize = options.PreviewSize;
        int samplesPerSector = options.SamplesPerSector;
        StatusText = L.F($"正在刷新 {outputSize} px DVD 实时预览…");
        bool keepOutput = false;
        try
        {
            outputPath = TempFiles.GetCacheFilePath($"dvd-live-preview-{revision}.png");
            DvdImageMetadata? metadata = DvdImageMetadata.TryLoad(isoPath);
            DvdStreamingOptions actual = ReadDvdPreviewGeometry(calibration: false);
            bool overlay = metadata is { HasFileSystem: true };
            await StaWorker.RunAsync(() =>
            {
                var range = IsoReadbackRenderer.ReadDrawingRange(isoPath, options.TotalSectors);
                IsoReadbackRenderer.Render(isoPath, outputPath,
                    options with { StartLba = range.StartLba, FillSectors = range.FillSectors }, cancellationToken);
                if (overlay)
                    FileRegionPreviewOverlay.ApplyDvd(outputPath, actual,
                        metadata!.DrawingStartLba, metadata.DrawingEndLbaExclusive, cancellationToken);
                return 0;
            });

            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, isoPath, "预览 - DVD", overlay);
            keepOutput = true;
            SetReadyStatus(() => L.F($"已实时更新 · 读回模拟 · CW · {outputSize} px · 每扇区 {samplesPerSector} 个采样"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetLivePreviewFailure(exception);
            _shell.AppendLog(JobDiagnostics.FormatLog($"DVD preview · {options}", exception));
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Calibration projection: the solver renders the source picture at the
    /// generated radii and projects it onto the measured ones.</summary>
    private async Task RefreshDvdCalibrationAsync(int revision, CancellationToken cancellationToken)
    {
        string? outputPath = null;
        if (!TryBuildDvdCalibrationOptions(
                out string imagePath,
                out CalibrationPreviewOptions options,
                out DvdHybridStreamingPlan? filePlan,
                out string message))
        {
            SetLivePreviewUnavailable(message);
            return;
        }

        int outputSize = options.PreviewSize;
        int samplesPerSector = options.SamplesPerSector;
        StatusText = L.F($"正在刷新 {outputSize} px DVD 标定预览…");
        bool keepOutput = false;
        try
        {
            outputPath = TempFiles.GetCacheFilePath($"dvd-live-warp-{revision}.png");
            DvdStreamingOptions actual = ReadDvdPreviewGeometry(calibration: true);
            await StaWorker.RunAsync(() =>
            {
                CalibrationPreviewRenderer.Render(imagePath, outputPath, options, cancellationToken);
                if (filePlan is not null)
                    FileRegionPreviewOverlay.ApplyDvd(outputPath, actual,
                        filePlan.DrawingStartLba, filePlan.DrawingEndLbaExclusive, cancellationToken);
                return 0;
            });

            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, imagePath, L.T("预览 - DVD"), filePlan is not null);
            keepOutput = true;
            SetReadyStatus(() => L.F($"已实时更新 · 标定预览 · CW · 抖动 {_state.DvdTone.DitherStrength * 100:0}%")
                + L.F($" · {outputSize} px · 每扇区 {samplesPerSector} 个采样"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetLivePreviewFailure(exception);
            _shell.AppendLog(JobDiagnostics.FormatLog($"DVD preview · {options}", exception));
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    private async Task RefreshBluRayAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        if (IsCalibrationPreview)
        {
            await RefreshBluRayProjectionAsync(revision, cancellation.Token);
        }
        else
        {
            await RefreshBluRayReadbackAsync(revision, cancellation.Token);
        }
    }

    /// <summary>Design projection: the source picture is quantized once by the tone profile,
    /// laid out at the generated geometry and projected onto the measured one. Polarity is
    /// part of the generation model, so this side of the preview applies it.</summary>
    private async Task RefreshBluRayProjectionAsync(int revision, CancellationToken cancellationToken)
    {
        string imagePath = _state.BdImagePath.Trim();
        if (imagePath.Length == 0 || !File.Exists(imagePath))
        {
            string message = imagePath.Length == 0
                ? L.T("请先在设计页导入图片，合成图会自动填入。")
                : L.T("蓝光源图片不存在。");
            SetLivePreviewUnavailable(message);
            return;
        }

        BluRayDiscParameters generated, actual;
        int outputSize;
        ToneProfile tone;
        bool invert;
        BluRayIsoPlan plan;
        try
        {
            generated = ReadBluRayDisc(_state.BdTotalSectors, _state.BdInnerRadius, _state.BdOuterRadius,
                _state.BdPitchLinear, _state.BdPitchQuadratic, _state.BdPitchCubic);
            actual = ReadBluRayDisc(_state.BdTotalSectors,
                _state.BdActualInnerRadius, _state.BdActualOuterRadius,
                _state.BdActualPitchLinear, _state.BdActualPitchQuadratic, _state.BdActualPitchCubic);
            outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.BdPreviewSize, L.T("预览尺寸")), 64, MaximumPreviewSize);
            tone = _state.BluRayTone;
            invert = _state.BdInvertMapping;
            plan = PrepareBluRayProjectionPlan(generated);
        }
        catch (Exception exception) when (exception is FormatException
                                              or OverflowException
                                              or ArgumentException
                                              or InvalidOperationException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            SetLivePreviewUnavailable(exception.Message);
            return;
        }

        string? outputPath = null;
        StatusText = L.F($"正在刷新 {outputSize} px 蓝光设计投影…");
        bool keepOutput = false;
        try
        {
            outputPath = TempFiles.GetCacheFilePath($"bd-live-projection-{revision}.png");
            await StaWorker.RunAsync(() =>
            {
                BluRayPreviewRenderer.RenderCalibration(
                    imagePath,
                    outputPath,
                    generated,
                    actual,
                    plan.DrawingStartSector,
                    plan.DrawingEndSectorExclusive,
                    outputSize,
                    invert,
                    cancellationToken,
                    tone);
                if (plan.HasFileSystem)
                    FileRegionPreviewOverlay.ApplyBluRay(outputPath, actual,
                        plan.DrawingStartSector, plan.DrawingEndSectorExclusive, cancellationToken);
                return 0;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, imagePath, "预览 - BD", plan.HasFileSystem);
            keepOutput = true;
            SetReadyStatus(() => L.F($"已实时更新 · 设计投影 · 3T/6T 类别图")
                + $" · {(invert ? L.T("反转 3T/6T") : L.T("标准 3T/6T"))}"
                + $" · {(tone.SrgbLinearization ? L.T("sRGB 线性化 · ") : string.Empty)}{outputSize} px");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetLivePreviewFailure(exception);
            _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] 蓝光实时预览失败：{exception}\n"));
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Finished-artifact read-back: classify the ISO's payload bytes as AA/44 and
    /// splat them under the measured geometry. Polarity is not applied again: the preview
    /// shows the bytes on the disc, which is a 3T/6T class map rather than reflectance.</summary>
    private async Task RefreshBluRayReadbackAsync(int revision, CancellationToken cancellationToken)
    {
        string isoPath = _state.BdIsoPath.Trim();
        if (isoPath.Length == 0 || !File.Exists(isoPath))
        {
            string message = isoPath.Length == 0
                ? L.T("请先选择生成的蓝光 ISO（导出页生成后自动填入）。")
                : L.T("蓝光 ISO 文件不存在。");
            SetLivePreviewUnavailable(message);
            return;
        }

        BluRayDiscParameters actual;
        int outputSize;
        try
        {
            actual = ReadBluRayDisc(_state.BdTotalSectors,
                _state.BdActualInnerRadius, _state.BdActualOuterRadius,
                _state.BdActualPitchLinear, _state.BdActualPitchQuadratic, _state.BdActualPitchCubic);
            outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.BdPreviewSize, L.T("预览尺寸")), 64, MaximumPreviewSize);
        }
        catch (Exception exception) when (exception is FormatException
                                              or OverflowException
                                              or ArgumentException)
        {
            SetLivePreviewUnavailable(exception.Message);
            return;
        }

        string? outputPath = null;
        StatusText = L.F($"正在刷新 {outputSize} px 蓝光成品读回…");
        bool keepOutput = false;
        try
        {
            outputPath = TempFiles.GetCacheFilePath($"bd-live-readback-{revision}.png");
            BluRayIsoMetadata? metadata = BluRayIsoMetadata.TryLoad(isoPath);
            bool overlay = metadata?.UdfTimestamp is not null;
            await StaWorker.RunAsync(() =>
            {
                BluRayPreviewRenderer.RenderReadback(
                    isoPath, outputPath, actual, outputSize, cancellationToken,
                    metadata?.DrawingStartSector ?? 0, metadata?.DrawingEndSectorExclusive);
                if (overlay)
                    FileRegionPreviewOverlay.ApplyBluRay(outputPath, actual,
                        metadata!.DrawingStartSector, metadata.DrawingEndSectorExclusive, cancellationToken);
                return 0;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, isoPath, "预览 - BD", overlay);
            keepOutput = true;
            SetReadyStatus(() => L.F($"已实时更新 · 成品读回 · 3T/6T 类别图（非反射亮度） · {outputSize} px"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetLivePreviewFailure(exception);
            _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] 蓝光成品读回失败：{exception}\n"));
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Project projection always follows the current folder and placement. A
    /// sidecar range belongs to its finished ISO and must not override later edits.</summary>
    internal BluRayIsoPlan PrepareBluRayProjectionPlan(BluRayDiscParameters generated)
    {
        string dataDirectory = _state.BdDataDirectory.Trim();
        return BluRayIsoGenerator.PrepareDrawing(generated,
            dataDirectory.Length == 0 ? null : dataDirectory,
            string.IsNullOrWhiteSpace(_state.BdVolumeLabel) ? "BD_IMAGE" : _state.BdVolumeLabel.Trim(),
            filesAtOuterRing: dataDirectory.Length > 0 && _state.BdFilesAtOuterRing);
    }

    private DvdStreamingOptions ReadDvdPreviewGeometry(bool calibration)
    {
        uint sectors = checked((uint)ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, L.T("总扇区数")));
        DvdStreamingOptions options = new(sectors,
            ParameterParser.ParsePositiveDouble(calibration ? _state.DvdActualInnerRadius : _state.DvdInnerRadius, L.T("内半径")),
            ParameterParser.ParsePositiveDouble(calibration ? _state.DvdActualOuterRadius : _state.DvdOuterRadius, L.T("外半径")),
            PitchLinear: ParameterParser.ParseDouble(calibration ? _state.DvdActualPitchLinear : _state.DvdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(calibration ? _state.DvdActualPitchQuadratic : _state.DvdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(calibration ? _state.DvdActualPitchCubic : _state.DvdPitchCubic, L.T("相对三次项")),
            UsableSectors: checked((uint)_state.GetUsableSectors(OpticalDiscFamily.Dvd)));
        options.Validate();
        return options;
    }

    private BluRayDiscParameters ReadBluRayDisc(
        string sectorsText, string innerText, string outerText,
        string pitchLinear, string pitchQuadratic, string pitchCubic)
    {
        long sectors = ParameterParser.ParsePositiveLong(sectorsText, L.T("蓝光总扇区数"));
        if (sectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(L.T("蓝光总扇区数"), L.T("总扇区数超出蓝光引擎支持范围。"));
        }

        BluRayDiscParameters disc = new(
            checked((uint)sectors),
            1,
            ParameterParser.ParsePositiveDouble(innerText, L.T("蓝光内半径")),
            ParameterParser.ParsePositiveDouble(outerText, L.T("蓝光外半径")),
            PitchLinear: ParameterParser.ParseDouble(pitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(pitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(pitchCubic, L.T("相对三次项")),
            UsableSectors: checked((uint)_state.GetUsableSectors(OpticalDiscFamily.BluRay)));
        disc.Validate();
        return disc;
    }

    /// <summary>Adopts a rendered preview as the canvas content and the export source, and
    /// retires the previous temporary file.</summary>
    private void PublishPreview(string outputPath, string inputPath, string titleKey,
        bool hasFileRegionOverlay = false)
    {
        string? previousPath = _lastLivePreviewPath;
        _lastLivePreviewPath = outputPath;
        _downloadablePreview = true;
        bool loaded = ShowResultPreview(outputPath);
        HasFileRegionOverlay = hasFileRegionOverlay && loaded;
        _isLivePreviewReady = true;
        _resultTitleKey = titleKey;
        ResultPreviewTitle = L.T(titleKey);
        ResultPreviewPath = Path.GetFileName(inputPath);
        TempFiles.TryDeleteTemporaryFile(previousPath);
    }

    private bool TryReadCdSettings(
        out string trackPath,
        out CdDiscParameters parameters,
        out int outputSize,
        out int samplesPerSector,
        out string message)
    {
        trackPath = _state.CdTrackPath.Trim();
        parameters = null!;
        outputSize = 0;
        samplesPerSector = 0;
        if (trackPath.Length == 0)
        {
            message = L.T("请先选择生成的 CD 音轨或 ISO（导出页生成后自动填入）。");
            return false;
        }

        if (!File.Exists(trackPath))
        {
            message = L.T("CD 音轨或 ISO 文件不存在。");
            return false;
        }

        try
        {
            long sectors = ParameterParser.ParsePositiveLong(_state.CdSectors, L.T("扇区数"));
            parameters = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, L.T("内半径")),
                ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, L.T("外半径")),
                sectors,
                PitchLinear: ParameterParser.ParseDouble(_state.CdPitchLinear, L.T("相对一次项")),
                PitchQuadratic: ParameterParser.ParseDouble(_state.CdPitchQuadratic, L.T("相对二次项")),
                PitchCubic: ParameterParser.ParseDouble(_state.CdPitchCubic, L.T("相对三次项")),
                UsableSectors: _state.GetUsableSectors(OpticalDiscFamily.CompactDisc));
            if (!Path.GetExtension(trackPath).Equals(".iso", StringComparison.OrdinalIgnoreCase)
                && CdTrackMetadata.TryLoad(trackPath) is { } metadata)
            {
                // A legacy sidecar's absent limit also records its original preview
                // canvas convention. Keep that distinction after State adopts its count.
                parameters = parameters with
                {
                    LinearVelocityMmPerSecond = metadata.LinearVelocityMmPerSecond,
                    StartAngleRadians = metadata.StartAngleDegrees * Math.PI / 180.0,
                    ImageOuterRadiusMm = metadata.ImageOuterRadiusMm,
                    UsableSectors = metadata.UsableSectors,
                    SourceImageOuterRadiusMm = metadata.SourceImageOuterRadiusMm,
                };
            }
            parameters.Validate();

            int configuredSize = ParameterParser.ParsePositiveInt(_state.CdPreviewSize, L.T("预览尺寸"));
            int configuredSamples = ParameterParser.ParsePositiveInt(_state.CdSamplesPerSector, L.T("每扇区采样"));
            outputSize = Math.Clamp(configuredSize, 256, MaximumPreviewSize);
            samplesPerSector = Math.Clamp(configuredSamples, 1, 32);

            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            message = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Calibration variables of the CD projection. Both parameter sets share the program's
    /// sector count; velocity, start angle and the image outer radius stay the physical
    /// constants of the scheme, as in the read-back path.
    /// </summary>
    private bool TryReadCdWarpSettings(
        out string imagePath,
        out CdDiscParameters generated,
        out CdDiscParameters actual,
        out int outputSize,
        out int samplesPerSector,
        out ToneProfile tone,
        out string message)
    {
        imagePath = _state.CdImagePath.Trim();
        generated = null!;
        actual = null!;
        outputSize = 0;
        samplesPerSector = 0;
        tone = _state.Tone;
        if (imagePath.Length == 0)
        {
            message = L.T("请先在设计页导入图片，合成图会自动填入。");
            return false;
        }

        if (!File.Exists(imagePath))
        {
            message = L.T("CD 源图片不存在。");
            return false;
        }

        try
        {
            long sectors = ParameterParser.ParsePositiveLong(_state.CdSectors, L.T("扇区数"));
            generated = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, L.T("生成内半径")),
                ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, L.T("生成外半径")),
                sectors,
                PitchLinear: ParameterParser.ParseDouble(_state.CdPitchLinear, L.T("相对一次项")),
                PitchQuadratic: ParameterParser.ParseDouble(_state.CdPitchQuadratic, L.T("相对二次项")),
                PitchCubic: ParameterParser.ParseDouble(_state.CdPitchCubic, L.T("相对三次项")),
                UsableSectors: _state.GetUsableSectors(OpticalDiscFamily.CompactDisc));
            actual = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdActualInnerRadius, L.T("实测内半径")),
                ParameterParser.ParsePositiveDouble(_state.CdActualOuterRadius, L.T("实测外半径")),
                sectors,
                PitchLinear: ParameterParser.ParseDouble(_state.CdActualPitchLinear, L.T("相对一次项")),
                PitchQuadratic: ParameterParser.ParseDouble(_state.CdActualPitchQuadratic, L.T("相对二次项")),
                PitchCubic: ParameterParser.ParseDouble(_state.CdActualPitchCubic, L.T("相对三次项")),
                UsableSectors: _state.GetUsableSectors(OpticalDiscFamily.CompactDisc));
            generated.Validate();
            actual.Validate();

            int configuredSize = ParameterParser.ParsePositiveInt(_state.CdPreviewSize, L.T("预览尺寸"));
            int configuredSamples = ParameterParser.ParsePositiveInt(_state.CdSamplesPerSector, L.T("每扇区采样"));
            outputSize = Math.Clamp(configuredSize, 256, MaximumPreviewSize);
            samplesPerSector = Math.Clamp(configuredSamples, 1, 32);

            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            message = exception.Message;
            return false;
        }
    }

    private bool TryBuildDvdReadbackOptions(
        out string isoPath,
        out IsoReadbackOptions options,
        out string message)
    {
        isoPath = _state.DvdIsoPath.Trim();
        options = null!;
        if (isoPath.Length == 0)
        {
            message = L.T("请先选择生成的 DVD ISO（导出页生成后自动填入）。");
            return false;
        }

        if (!File.Exists(isoPath))
        {
            message = L.T("DVD ISO 文件不存在。");
            return false;
        }

        try
        {
            long totalSectorsValue = ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, L.T("总扇区数"));
            if (totalSectorsValue > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(L.T("总扇区数"), L.T("总扇区数超出 DVD 引擎支持范围。"));
            }

            double innerRadius = ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, L.T("内半径"));
            double outerRadius = ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, L.T("外半径"));
            if (innerRadius >= outerRadius)
            {
                throw new ArgumentOutOfRangeException(L.T("半径"), L.T("内半径必须小于外半径。"));
            }

            int outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdPreviewSize, L.T("预览尺寸")),
                256,
                MaximumPreviewSize);
            int samplesPerSector = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdSamplesPerSector, L.T("每扇区采样")),
                1,
                16);

            options = new IsoReadbackOptions(
                checked((uint)totalSectorsValue), 0, checked((uint)_state.GetUsableSectors(OpticalDiscFamily.Dvd)), 0x30000,
                innerRadius, outerRadius, DvdStreamingOptions.StandardChannelBitLengthNm, true,
                outputSize, samplesPerSector,
                ParameterParser.ParseDouble(_state.DvdPitchLinear, L.T("相对一次项")),
                ParameterParser.ParseDouble(_state.DvdPitchQuadratic, L.T("相对二次项")),
                ParameterParser.ParseDouble(_state.DvdPitchCubic, L.T("相对三次项")),
                PreviewOuterRadiusMm: ReadDvdPreviewGeometry(false).OutputOuterRadiusMm);
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            message = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Calibration variables of the DVD projection. Unlike the read-back path, the request
    /// carries both radii pairs: the solver lays the picture out at the generated radii and
    /// projects it onto the measured ones. Channel bit (133.3 nm) and start angle (0°) stay
    /// the physical constants of the scheme.
    /// </summary>
    private bool TryBuildDvdCalibrationOptions(
        out string imagePath,
        out CalibrationPreviewOptions options,
        out DvdHybridStreamingPlan? filePlan,
        out string message)
    {
        imagePath = _state.DvdImagePath.Trim();
        options = null!;
        filePlan = null;
        if (imagePath.Length == 0)
        {
            message = L.T("请先在设计页导入图片，合成图会自动填入。");
            return false;
        }

        if (!File.Exists(imagePath))
        {
            message = L.T("DVD 源图片不存在。");
            return false;
        }

        try
        {
            long totalSectorsValue = ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, L.T("总扇区数"));
            if (totalSectorsValue > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(L.T("总扇区数"), L.T("总扇区数超出 DVD 引擎支持范围。"));
            }

            double generatedInner = ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, L.T("生成内半径"));
            double generatedOuter = ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, L.T("生成外半径"));
            double actualInner = ParameterParser.ParsePositiveDouble(_state.DvdActualInnerRadius, L.T("实测内半径"));
            double actualOuter = ParameterParser.ParsePositiveDouble(_state.DvdActualOuterRadius, L.T("实测外半径"));
            if (generatedInner >= generatedOuter || actualInner >= actualOuter)
            {
                throw new ArgumentOutOfRangeException(L.T("半径"), L.T("内半径必须小于外半径。"));
            }

            int outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdPreviewSize, L.T("预览尺寸")),
                256,
                MaximumPreviewSize);
            int samplesPerSector = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdSamplesPerSector, L.T("每扇区采样")),
                1,
                16);

            ToneProfile tone = _state.DvdTone;
            uint drawingStart = 0;
            uint drawingSectors = checked((uint)_state.GetUsableSectors(OpticalDiscFamily.Dvd));
            string dataDirectory = _state.DvdDataDirectory.Trim();
            if (dataDirectory.Length > 0)
            {
                DvdStreamingOptions generated = ReadDvdPreviewGeometry(calibration: false);
                filePlan = DvdStreamingGenerator.PrepareHybrid(dataDirectory, generated,
                    string.IsNullOrWhiteSpace(_state.DvdVolumeLabel) ? "DISC_IMAGE" : _state.DvdVolumeLabel.Trim(),
                    filesAtOuterRing: _state.DvdFilesAtOuterRing);
                drawingStart = filePlan.DrawingStartLba;
                drawingSectors = filePlan.DrawingSectors;
            }
            options = new CalibrationPreviewOptions(
                checked((uint)totalSectorsValue), drawingStart, drawingSectors,
                generatedInner, generatedOuter, actualInner, actualOuter,
                DvdStreamingOptions.StandardChannelBitLengthNm, true, 128, 1, outputSize, samplesPerSector,
                ParameterParser.ParseDouble(_state.DvdPitchLinear, L.T("相对一次项")),
                ParameterParser.ParseDouble(_state.DvdPitchQuadratic, L.T("相对二次项")),
                ParameterParser.ParseDouble(_state.DvdPitchCubic, L.T("相对三次项")),
                ParameterParser.ParseDouble(_state.DvdActualPitchLinear, L.T("相对一次项")),
                ParameterParser.ParseDouble(_state.DvdActualPitchQuadratic, L.T("相对二次项")),
                ParameterParser.ParseDouble(_state.DvdActualPitchCubic, L.T("相对三次项")), tone,
                SourceImageOuterRadiusMm: ReadDvdPreviewGeometry(false).OutputOuterRadiusMm,
                PreviewOuterRadiusMm: ReadDvdPreviewGeometry(true).OutputOuterRadiusMm);
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException
                                              or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _shell.AppendLog(JobDiagnostics.FormatLog("DVD projection parameters · " + imagePath, exception));
            message = exception.Message;
            return false;
        }
    }

    internal void PrepareResultPreview(string outputPath)
    {
        HasFileRegionOverlay = false;
        ResultImage = null;
        HasResultImage = false;
        // The job output replaces the live result, so its completion line must not be
        // restored by a later language switch.
        _readyStatus = null;
        _downloadablePreview = false;
        _resultTitleKey = "输出预览";
        ResultPreviewTitle = L.T("输出预览");
        ResultPreviewPath = string.Empty;
        ResultPreviewPlaceholderText = ShellViewModel.IsPngPath(outputPath)
            ? L.T("正在生成图片预览…")
            : L.T("当前任务生成文件，不包含可视图片预览。");
    }

    internal bool ShowResultPreview(string outputPath)
    {
        HasFileRegionOverlay = false;
        if (!ShellViewModel.IsPngPath(outputPath))
        {
            return false;
        }

        try
        {
            using FileStream stream = new(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            System.Windows.Media.Imaging.BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            ResultImage = bitmap;
            HasResultImage = true;
            ResultPreviewPath = Path.GetFileName(outputPath);
            return true;
        }
        catch (Exception exception)
        {
            ResultPreviewPlaceholderText = L.T("图片已生成，但无法在界面中加载。");
            _shell.AppendLog(JobDiagnostics.FormatLog(L.T("图片已生成，但无法在界面中加载。") + " · " + outputPath, exception));
            return false;
        }
    }

    internal void OnWindowClosed()
    {
        _livePreviewTimer.Stop();
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        TempFiles.TryDeleteTemporaryFile(_lastLivePreviewPath);
    }

    partial void OnHasResultImageChanged(bool value)
    {
        DownloadPreviewCommand.NotifyCanExecuteChanged();
        RaiseEmptyStateAction();
    }

    private bool CanDownloadPreview()
        => HasResultImage && _downloadablePreview && PreviewArtifactDirectory() is not null;

    /// <summary>
    /// Exports the live preview next to the picture it was derived from, named after that
    /// picture (<c>cd-track-preview.png</c>): the read-back picture belongs with the track or
    /// ISO the user keeps, and the projection belongs with its source picture, not in the
    /// preview's temporary directory.
    /// </summary>
    private void DownloadPreview()
    {
        string? previewPath = _lastLivePreviewPath;
        string? directory = PreviewArtifactDirectory();
        if (!_downloadablePreview || previewPath is null || directory is null || !File.Exists(previewPath))
        {
            return;
        }

        string artifactPath = PreviewInputPath();
        string baseName = Path.GetFileNameWithoutExtension(artifactPath);
        string destination = Path.Combine(
            directory,
            (baseName.Length == 0 ? "live-preview" : baseName) + "-preview.png");
        try
        {
            File.Copy(previewPath, destination, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast(L.T("导出失败"), exception.Message, ToastKind.Error);
            _shell.AppendLog(JobDiagnostics.FormatLog(L.T("导出失败") + " · " + destination, exception));
            return;
        }

        _shell.SetLastOutputPath(destination);
        _shell.ShowToast(L.T("预览已导出"), destination, ToastKind.Success);
        _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] 预览已导出：{destination}\n"));
    }

    /// <summary>The picture the preview is derived from: the artifact being read back, or the
    /// source picture being projected.</summary>
    private string PreviewInputPath()
    {
        return SelectedFamily switch
        {
            OpticalDiscFamily.Dvd => (IsCalibrationPreview ? _state.DvdImagePath : _state.DvdIsoPath).Trim(),
            OpticalDiscFamily.BluRay => (IsCalibrationPreview ? _state.BdImagePath : _state.BdIsoPath).Trim(),
            _ => (IsCalibrationPreview ? _state.CdImagePath : _state.CdTrackPath).Trim(),
        };
    }

    /// <summary>Folder holding that picture, or null when unknown.</summary>
    private string? PreviewArtifactDirectory()
    {
        string path = PreviewInputPath();
        if (path.Length == 0)
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) || !Directory.Exists(directory) ? null : directory;
    }
}
