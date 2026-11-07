using DiscImageStudio.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel : ObservableObject
{
    private static readonly Lazy<BluRayGray8Raster> BluRayReferenceRaster = new(() => new(
        CalibrationReferencePattern.PixelSize, CalibrationReferencePattern.PixelSize,
        CalibrationReferencePattern.ReadGrayPixels()));
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private CalibrationTarget? _target;
    private CalibrationParameters? _searchCenter;
    private CalibrationFitResult? _fit;
    private int _fitStatusPointCount;
    private string? _fitStatusText;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _previewCancellation;
    private bool _cdInterleave = true;
    private string? _sessionPath;
    private bool _installing;
    private bool _previewReady;
    private long _inputRevision;
    private bool _isSolving;
    private CalibrationFitOptions _loadedFitOptions = new();

    public CalibrationViewModel(ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        CreateTargetCommand = new RelayCommand(CreateTarget, CanCreateTarget);
        SaveAsPresetCommand = new RelayCommand(SaveAsPreset, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ExportPngCommand = new AsyncRelayCommand(ExportPngAsync, CanUseStageTarget);
        GenerateImageCommand = new AsyncRelayCommand(GenerateImageAsync, CanUseStageTarget);
        StreamBurnCommand = new RelayCommand(PrepareStreamBurn, CanUseStageTarget);
        SaveSessionCommand = new RelayCommand(SaveSession, CanUseTarget);
        OpenSessionCommand = new AsyncRelayCommand(OpenSessionAsync, () => !IsBusy);
        SolveCommand = new AsyncRelayCommand(SolveAsync, CanSolveTrace);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => _cancellation is not null);
        ApplyParametersCommand = new RelayCommand(ApplyParameters, CanApplyParameters);
        InitializePhotoCommands();
        InitializeRingCommands();
        InitializeCapacityCommands();
        RefreshCoarseSetup();
        shell.BusyChanged += () => { OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); };
        // The target is a snapshot. Edits here change the setup for the next target.
        state.PropertyChanged += (_, change) =>
        {
            OnPropertyChanged(nameof(CurrentParametersSummary));
            OnPropertyChanged(nameof(SetupPitchLinear));
            OnPropertyChanged(nameof(SetupPitchQuadratic));
            OnPropertyChanged(nameof(SetupPitchCubic));
            RefreshCoarseSetup();
            if (change.PropertyName is nameof(state.CdSectors) or nameof(state.DvdTotalSectors) or nameof(state.BdTotalSectors)
                or nameof(state.CdUsableSectors) or nameof(state.DvdUsableSectors) or nameof(state.BdUsableSectors))
                CapacityStatus = string.Empty;
        };
    }

    [ObservableProperty] private string _selectedDiscType = "CD";
    public DiscParametersState Parameters => _state;
    public string SetupPitchLinear
    {
        get => SelectedDiscType switch { "DVD" => _state.DvdPitchLinear, "BD" => _state.BdPitchLinear, _ => _state.CdPitchLinear };
        set
        {
            if (SelectedDiscType == "DVD") _state.DvdPitchLinear = value;
            else if (SelectedDiscType == "BD") _state.BdPitchLinear = value;
            else _state.CdPitchLinear = value;
        }
    }

    public string SetupPitchQuadratic
    {
        get => SelectedDiscType switch { "DVD" => _state.DvdPitchQuadratic, "BD" => _state.BdPitchQuadratic, _ => _state.CdPitchQuadratic };
        set
        {
            if (SelectedDiscType == "DVD") _state.DvdPitchQuadratic = value;
            else if (SelectedDiscType == "BD") _state.BdPitchQuadratic = value;
            else _state.CdPitchQuadratic = value;
        }
    }

    public string SetupPitchCubic
    {
        get => SelectedDiscType switch { "DVD" => _state.DvdPitchCubic, "BD" => _state.BdPitchCubic, _ => _state.CdPitchCubic };
        set
        {
            if (SelectedDiscType == "DVD") _state.DvdPitchCubic = value;
            else if (SelectedDiscType == "BD") _state.BdPitchCubic = value;
            else _state.CdPitchCubic = value;
        }
    }

    public IReadOnlyList<string> DiscTypeChoices { get; } = ["CD", "DVD", "BD"];
    public bool IsCdSetup => SelectedDiscType == "CD";
    public bool HasWideBandTarget => _target?.HasWideAddressBands == true;
    private bool _supportsCoarseTarget;
    public string CoarseSetupHint { get; private set; } = string.Empty;
    private bool CanCreateTarget() => !IsBusy && (!IsCoarseMode || _supportsCoarseTarget);

    private void RefreshCoarseSetup()
    {
        string hint;
        try
        {
            _supportsCoarseTarget = CalibrationWideBandPattern.SupportsAngleSweep(ReadSetup());
            hint = L.T(_supportsCoarseTarget ? "AngleBands.Setup" : "AngleBands.Unsupported");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            _supportsCoarseTarget = false;
            hint = L.T("AngleBands.CheckSetup");
        }
        if (hint == CoarseSetupHint) return;
        CoarseSetupHint = hint;
        OnPropertyChanged(nameof(CoarseSetupHint));
        CreateTargetCommand.NotifyCanExecuteChanged();
    }
    public bool IsDvdSetup => SelectedDiscType == "DVD";
    public bool IsBluRaySetup => SelectedDiscType == "BD";
    [ObservableProperty] private BitmapSource? _targetImage;
    [ObservableProperty] private double _canvasOuterRadiusMm = 60;
    [ObservableProperty] private IReadOnlyList<CoarseCandidate> _coarseCandidates = [];
    [ObservableProperty] private CoarseCandidate? _selectedCoarseCandidate;
    // The range is a search box for the two radius parameters, not a curve-shape choice.
    // Auto starts at the tight box and widens only when the optimum touches its edge, so
    // healthy discs are not silently clamped to a user-picked boundary.
    // The collection is per-view-model so a legacy session can contribute its own range as a
    // listed item; a detached object would leave the ComboBox unselected, just like the
    // pre-fix default did.
    public ObservableCollection<CoarseSearchRange> CoarseSearchRangeChoices { get; } = new(CoarseSearchRanges);
    // Default references the listed instance: the class uses reference equality, so a
    // separate object would leave the ComboBox unselected until the user re-picks a range.
    [ObservableProperty] private CoarseSearchRange _selectedCoarseSearchRange = CoarseSearchRanges[0];
    private static readonly IReadOnlyList<CoarseSearchRange> CoarseSearchRanges =
        [new(L.T("自动（推荐）"), 0.02, Auto: true), new("±0.02 mm", 0.02),
         new("±0.1 mm", 0.1), new("±0.5 mm", 0.5)];
    public string SearchRangeHeader => SelectedCoarseSearchRange.Auto
        ? L.T("搜索范围（自动）")
        : L.F($"搜索范围（{SelectedCoarseSearchRange.Label}）");
    private double _fitRangeMm = 0.02;
    private bool _fitRangeExpanded;
    [ObservableProperty] private bool _contourConfirmed;
    [ObservableProperty] private bool _fitDvdPitch = true;
    public bool IsDvdTarget => _target?.Parameters.Kind == CalibrationDiscKind.Dvd;

    public bool IsBluRayTarget => _target?.Parameters.Kind == CalibrationDiscKind.BluRay;
    [ObservableProperty] private string _outputPath = string.Empty;
    // The shared job dock displays running operations and their outcomes; errors also toast.
    private string _statusMessage = string.Empty;

    /// <summary>Last operation message, also reported in the shared task log.</summary>
    public string LastStatusMessage => _statusMessage;
    [ObservableProperty] private string _fitAssessment = L.T("全部有效点都会参与计算，结果需与实拍曲线核对。");
    public bool IsBusy => _shell.IsBusy;
    public CalibrationTarget? Target => _target;
    public bool CdInterleave => _cdInterleave;
    public string TargetSummary => _target?.Parameters is { } p
        ? L.Format("CalibrationSetup.Target", DiscTypeName(p.Kind), p.Sectors, p.InnerRadiusMm, p.OuterRadiusMm)
            + Environment.NewLine + DescribeArtwork(_target)
            + (p.Kind == CalibrationDiscKind.Dvd ? Environment.NewLine
                + L.T(p.DvdReadCompatibility ? "DvdReadCompatibility.Enabled" : "DvdReadCompatibility.Disabled") : string.Empty)
        : L.T("CalibrationSetup.NoTarget");

    private static string DescribeArtwork(CalibrationTarget target)
    {
        if (target.HasWavyBoundary) return L.T("WavyBoundary.Target");
        if (target.HasHalfDisc) return L.T("HalfDisc.Target");
        if (target.HasSingleRadialLine) return L.T("SingleLine.Target");
        if (target.Artwork == CalibrationArtwork.ReferenceCross) return L.T("ReferenceCross.Target");
        if (target.HasRadialArcDashes && target.SchemaVersion == 10)
        {
            var arcs = target.RadialArcDashes;
            static double Thickness(CalibrationRadialArc arc) => arc.OuterRadiusMm - arc.InnerRadiusMm;
            return L.Format("Arc.ThickTarget", Thickness(arcs[0]), Thickness(arcs[1]),
                Thickness(arcs[2]), arcs[0].HalfAngleRadians * 360 / Math.PI);
        }
        return L.T(target.SchemaVersion == 6 ? "CalibrationArt.Legacy"
            : target.HasAngleSweep ? "AngleBands.Target"
            : target.HasWideAddressBands ? (target.SchemaVersion == 11 ? "WideBands.Target" : "BandEdges.Target")
            : target.HasRadialArcDashes ? "Arc.Target" : target.HasClockRuler ? "Clock.Target"
            : target.HasAngularReference ? "CalibrationArt.Swept" : "CalibrationArt.Plain");
    }

    private static string DiscTypeName(CalibrationDiscKind kind) => kind switch
    {
        CalibrationDiscKind.Dvd => "DVD",
        CalibrationDiscKind.BluRay => "BD",
        _ => "CD",
    };

    /// <summary>Geometry that will be recorded when a new calibration target is created.</summary>
    public string CurrentParametersSummary => SelectedDiscType switch
    {
        "DVD" => L.F($"内半径 {Trimmed(_state.DvdInnerRadius)} mm · 外半径 {Trimmed(_state.DvdOuterRadius)} mm · 三次项 {Trimmed(_state.DvdPitchCubic)}"),
        "BD" => L.F($"内半径 {Trimmed(_state.BdInnerRadius)} mm · 外半径 {Trimmed(_state.BdOuterRadius)} mm · ")
            + L.F($"{Trimmed(_state.BdTotalSectors)} 扇区"),
        _ => L.F($"内半径 {Trimmed(_state.CdInnerRadius)} mm · 外半径 {Trimmed(_state.CdOuterRadius)} mm"),
    };

    private static string Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    /// <summary>Re-reads the fit-side strings and the photo step after a language switch. The
    /// search-range choices only need their labels refreshed, which keeps the ComboBox
    /// selection and the fitted result intact.</summary>
    internal void Relocalize()
    {
        foreach (CoarseSearchRange range in CoarseSearchRangeChoices)
        {
            range.RefreshLabel();
        }

        OnPropertyChanged(nameof(SearchRangeHeader));
        OnPropertyChanged(nameof(CurrentParametersSummary));
        OnPropertyChanged(nameof(TargetSummary));
        RefreshCoarseSetup();
        NotifyTraceStage();
        RefreshApplyParametersState();
        RelocalizeFitStatus();
        RelocalizePhoto();
        foreach (RingChoice choice in RingChoices) choice.RefreshLabel();
        foreach (ArcBandChoice choice in ArcBandNames) choice.RefreshLabel();
        foreach (ArcDirectionChoice choice in ArcDirections) choice.RefreshLabel();
        NotifyRingResult();
        OnPropertyChanged(nameof(RingPointSummary));
        OnPropertyChanged(nameof(RestartBandRayLabel));
        OnPropertyChanged(nameof(ClockReadSummary));
        OnPropertyChanged(nameof(ArcMeasurementSummary));
        OnPropertyChanged(nameof(ArcFitSummary));
    }

    // ---- Step column: state chips and progressive disclosure ----
    // One step is active at a time and its card is the expanded one; finished steps collapse
    // into a check-marked header. The photo/trace transitions raise these from the photo
    // partial, target installation raises them in InstallTarget.

    public StepCardState StepOneState => _target is null ? StepCardState.Active : StepCardState.Done;

    public StepCardState StepTwoState => _target is null
        ? StepCardState.Pending
        : IsPhotoStep ? StepCardState.Active : StepCardState.Done;

    public StepCardState StepThreeState => IsTraceStep ? StepCardState.Active : StepCardState.Pending;

    [ObservableProperty] private bool _stepOneExpanded = true;
    [ObservableProperty] private bool _stepTwoExpanded;
    [ObservableProperty] private bool _stepThreeExpanded;

    // Accordion: opening one step closes the others, so the expanded card can claim the full
    // remaining height of the step column and all three headers stay on screen. Collapse is
    // still allowed, which leaves all three closed.
    partial void OnStepOneExpandedChanged(bool value) { if (value) CollapseOtherSteps(1); }
    partial void OnStepTwoExpandedChanged(bool value) { if (value) CollapseOtherSteps(2); }
    partial void OnStepThreeExpandedChanged(bool value) { if (value) CollapseOtherSteps(3); }

    private void CollapseOtherSteps(int keep)
    {
        if (keep != 1 && StepOneExpanded) StepOneExpanded = false;
        if (keep != 2 && StepTwoExpanded) StepTwoExpanded = false;
        if (keep != 3 && StepThreeExpanded) StepThreeExpanded = false;
    }

    // Canvas view toggles: they change what the stage draws and nothing else, so they live
    // here instead of view code-behind (which used to reset them by reach-in).
    [ObservableProperty] private bool _showTargetPreview;
    [ObservableProperty] private bool _showPrediction = true;

    public RelayCommand CreateTargetCommand { get; }
    public IRelayCommand ReturnFromCalibrationCommand => _shell.ReturnFromCalibrationCommand;
    public RelayCommand SaveAsPresetCommand { get; }
    internal Func<PresetSaveResult?>? ShowPresetSaveDialog { get; set; }
    public RelayCommand BrowseOutputCommand { get; }
    public IAsyncRelayCommand ExportPngCommand { get; }
    public IAsyncRelayCommand GenerateImageCommand { get; }
    public RelayCommand StreamBurnCommand { get; }
    public RelayCommand SaveSessionCommand { get; }
    public IAsyncRelayCommand OpenSessionCommand { get; }
    public IAsyncRelayCommand SolveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ApplyParametersCommand { get; }

    internal void EnsureTarget()
    {
        if (_target is not null)
        {
            return;
        }

        CreateTarget();
    }

    internal void OnWindowClosed() { _cancellation?.Cancel(); _previewCancellation?.Cancel(); SetTargetPreviewVisible(false); }
    private bool CanUseTarget() => !IsBusy && _target is not null;
    // Automatic fit checks explain uncertainty; the user's photographic confirmation
    // is authoritative for applying a current candidate whose preview is ready.
    private bool CanApplyParameters() => ApplyParametersBlockedReason() is null;
    public string ApplyParametersHint => ApplyParametersBlockedReason() ?? L.T("CalibrationApply.Ready");

    private string? ApplyParametersBlockedReason()
    {
        if (IsBusy) return L.T("CalibrationApply.Busy");
        if (!IsLineFitStep) return L.T("CalibrationApply.Stage");
        if (_photoTransform is null) return L.T("CalibrationApply.Rectify");
        if (_fit is null) return L.T("CalibrationApply.Solve");
        if (_fit.Candidates.Count == 0) return L.T("CalibrationApply.NoCandidate");
        if (SelectedCoarseCandidate is not { } selected) return L.T("CalibrationApply.SelectCandidate");
        if (!CoarseCandidates.Contains(selected)
            || !_fit.Candidates.Any(c => c.Parameters == selected.Parameters && c.RotationRadians == selected.RotationRadians))
            return L.T("CalibrationApply.StaleCandidate");
        if (!_previewReady) return L.T("CalibrationApply.PreviewPending");
        if (!ContourConfirmed) return L.Format("CalibrationApply.Confirm", TraceConfirmationLabel);
        return null;
    }

    private void RefreshApplyParametersState()
    {
        OnPropertyChanged(nameof(ApplyParametersHint));
        UpdateFitAssessment();
        ApplyParametersCommand?.NotifyCanExecuteChanged();
    }
    private bool IsVerifiedBoundaryCandidate(CoarseCandidate candidate) => !HasBoundarySegments
        || (_fit is { Succeeded: true } fit && candidate.Parameters == fit.BestParameters
            && candidate.RotationRadians == fit.RotationRadians);
    partial void OnContourConfirmedChanged(bool value) => RefreshApplyParametersState();

    // Residual bounds supply an automatic warning, not a veto of manual confirmation.
    private static bool CandidateResidualAccepted(CoarseCandidate candidate) =>
        ResidualAccepted(candidate.RmsErrorMm, candidate.MaxErrorMm);

    private static bool ResidualAccepted(double rms, double maximum) =>
        double.IsFinite(rms) && double.IsFinite(maximum) && rms <= 0.35 && maximum <= 0.8;

    partial void OnSelectedDiscTypeChanged(string value)
    {
        CapacityStatus = string.Empty;
        OnPropertyChanged(nameof(CurrentParametersSummary));
        OnPropertyChanged(nameof(IsCdSetup));
        RefreshCoarseSetup();
        OnPropertyChanged(nameof(IsDvdSetup));
        OnPropertyChanged(nameof(IsBluRaySetup));
        OnPropertyChanged(nameof(SetupPitchLinear));
        OnPropertyChanged(nameof(SetupPitchQuadratic));
        OnPropertyChanged(nameof(SetupPitchCubic));
    }

    private void CreateTarget()
    {
        try
        {
            var parameters = ReadSetup();
            var artwork = _stage switch
            {
                CalibrationStage.Coarse => CalibrationArtwork.AngleSweepBands,
                CalibrationStage.SingleLine => CalibrationArtwork.WavyBoundary,
                CalibrationStage.Fine => CalibrationArtwork.ReferenceCross,
                _ => throw new InvalidOperationException(),
            };
            InstallTarget(CalibrationTarget.Create(parameters, artwork: artwork),
                new(RadiusRangeMm: artwork == CalibrationArtwork.WavyBoundary ? .01 : .02,
                    AutoRange: artwork == CalibrationArtwork.WavyBoundary ? false : null), _state.CdInterleave);
            _sessionPath = null;
            OutputPath = string.Empty;
        }
        catch (Exception ex) { Report(ex); }
    }

    private CalibrationParameters ReadSetup()
    {
        CalibrationParameters p = SelectedDiscType switch
        {
            "DVD" => new(CalibrationDiscKind.Dvd, Number(_state.DvdInnerRadius), Number(_state.DvdOuterRadius),
                ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, L.T("DVD 扇区数")),
                DvdStreamingOptions.StandardChannelBitLengthNm,
                ParameterParser.ParseDouble(_state.DvdPitchLinear, L.T("DVD 相对一次项")),
                ParameterParser.ParseDouble(_state.DvdPitchQuadratic, L.T("DVD 相对二次项")),
                ParameterParser.ParseDouble(_state.DvdPitchCubic, L.T("DVD 相对三次项")),
                DvdReadCompatibility: _state.DvdReadCompatibility,
                UsableSectors: _state.GetUsableSectors(OpticalDiscFamily.Dvd)),
            "BD" => CreateBluRayParameters(),
            "CD" => new(CalibrationDiscKind.Cd, Number(_state.CdInnerRadius), Number(_state.CdOuterRadius),
                ParameterParser.ParsePositiveLong(_state.CdSectors, L.T("CD 扇区数")),
                CdDiscParameters.StandardLinearVelocityMmPerSecond,
                ParameterParser.ParseDouble(_state.CdPitchLinear, L.T("相对一次项")),
                ParameterParser.ParseDouble(_state.CdPitchQuadratic, L.T("相对二次项")),
                ParameterParser.ParseDouble(_state.CdPitchCubic, L.T("相对三次项")),
                UsableSectors: _state.GetUsableSectors(OpticalDiscFamily.CompactDisc)),
            _ => throw new ArgumentException(L.T("CalibrationSetup.ChooseFamily")),
        };
        ValidateOutputParameters(p);
        return p;
    }

    private void SaveAsPreset()
    {
        try
        {
            var p = ReadSetup();
            if (ShowPresetSaveDialog?.Invoke() is not { } result) return;
            var family = p.Kind switch
            {
                CalibrationDiscKind.Cd => OpticalDiscFamily.CompactDisc,
                CalibrationDiscKind.Dvd => OpticalDiscFamily.Dvd,
                _ => OpticalDiscFamily.BluRay,
            };
            string name = _state.AddUserPreset(family, result.Name, p.InnerRadiusMm, p.OuterRadiusMm,
                p.Sectors, p.PitchLinear, p.PitchQuadratic, p.PitchCubic, p.BluRayInvertMapping, p.UsableSectors);
            // The catalog is shared. Only the list in project dialogs is refreshed.
            _shell.State.UsePresetCatalog(_state);
            _statusMessage = L.F($"已保存为预设：{name}");
            _shell.AppendLog(_statusMessage + "\n");
            _shell.ShowToast(L.T("Xaml.SaveAsPreset"), name, ToastKind.Success);
        }
        catch (Exception ex) { Report(ex); }
    }

    private static double Number(string raw) => ParameterParser.ParsePositiveDouble(raw, L.T("标定参数"));

    private CalibrationParameters CreateBluRayParameters()
    {
        long sectors = ParameterParser.ParsePositiveLong(_state.BdTotalSectors, L.T("蓝光总扇区数"));
        if (sectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(L.T("蓝光总扇区数"), L.T("总扇区数超出蓝光引擎支持范围。"));
        }

        uint totalSectors = checked((uint)sectors);
        BluRayDiscParameters disc = new(
            totalSectors, 1, Number(_state.BdInnerRadius), Number(_state.BdOuterRadius),
            PitchLinear: ParameterParser.ParseDouble(_state.BdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.BdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.BdPitchCubic, L.T("相对三次项")),
            UsableSectors: checked((uint)_state.GetUsableSectors(OpticalDiscFamily.BluRay)));
        disc.Validate();
        return new CalibrationParameters(
            CalibrationDiscKind.BluRay,
            disc.InnerRadiusMm,
            disc.OuterRadiusMm,
            disc.TotalSectors,
            BluRayDiscParameters.ChannelBitLengthNm,
            disc.PitchLinear, disc.PitchQuadratic, disc.PitchCubic,
            BluRayInvertMapping: _state.BdInvertMapping,
            DrawingStartSector: 0,
            DrawingEndSectorExclusive: disc.OutputSectors, UsableSectors: disc.OutputSectors);
    }

    private void InstallTarget(CalibrationTarget target, CalibrationFitOptions? options, bool interleave)
    {
        _installing = true;
        try
        {
            _previewCancellation?.Cancel();
            _target = target;
            ResetPhoto();
            _cdInterleave = interleave;
            _searchCenter = options?.SearchCenter ?? target.Parameters;
            OnPropertyChanged(nameof(FineInitialSummary));
            _loadedFitOptions = options ?? new(RadiusRangeMm: target.HasBoundaryPattern ? .01 : .02);
            FitDvdPitch = _loadedFitOptions.FitDvdPitch;
            double savedRange = options?.RadiusRangeMm ?? (target.HasBoundaryPattern ? .01 : .02);
            // Sessions saved before the auto mode carry no flag (null) and default to auto;
            // a session that explicitly saved a manual box keeps that box.
            bool autoRange = options?.AutoRange ?? true;
            SelectedCoarseSearchRange = target.HasBoundaryPattern
                ? BoundarySearchRangeChoices.FirstOrDefault(range => range.RadiusRangeMm == savedRange)
                    ?? BoundarySearchRangeChoices[1]
                : ResolveSearchRange(savedRange, autoRange);
            SelectedDiscType = DiscTypeName(target.Parameters.Kind);
            // The 60 mm canvas is a disc-preview background; the target itself maps its
            // reference pixels using the saved generation outer radius.
            CanvasOuterRadiusMm = Math.Max(CalibrationReferencePattern.CanvasRadiusMm, target.Parameters.OuterRadiusMm);
            InstallTargetPreview(target);
            InvalidateFit();
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(TargetSummary));
            OnPropertyChanged(nameof(HasClockTarget));
            OnPropertyChanged(nameof(HasWideBandTarget));
            OnPropertyChanged(nameof(HasRadialArcTarget));
            OnPropertyChanged(nameof(IsRadialArcStep));
            OnPropertyChanged(nameof(IsClockStep));
            OnPropertyChanged(nameof(IsRingStep));
            NotifyTraceStage();
            OnPropertyChanged(nameof(IsDvdTarget));
            OnPropertyChanged(nameof(IsBluRayTarget));
            OnPropertyChanged(nameof(StepOneState));
            OnPropertyChanged(nameof(StepTwoState));
            // The target exists, so stage 1 is done and photographing it takes over.
            StepOneExpanded = false;
            StepTwoExpanded = true;
            RefreshCommands();
            _shell.CalibrationBurn?.OnCalibrationTargetChanged();
        }
        finally { _installing = false; }
    }

    /// <summary>A session can carry a manual box that the current choices do not list. The
    /// entry is added to this view model's collection so the selector shows the saved value;
    /// the previous session's custom entry is replaced instead of accumulating.</summary>
    private CoarseSearchRange ResolveSearchRange(double savedRange, bool autoRange)
    {
        // The custom entry belongs to the session that contributed it; any other install
        // (including an auto one) replaces it instead of adding a second entry.
        while (CoarseSearchRangeChoices.Count > CoarseSearchRanges.Count)
        {
            CoarseSearchRangeChoices.RemoveAt(CoarseSearchRangeChoices.Count - 1);
        }

        if (autoRange)
        {
            return CoarseSearchRangeChoices[0];
        }

        CoarseSearchRange? listed = CoarseSearchRangeChoices
            .FirstOrDefault(range => !range.Auto && range.RadiusRangeMm == savedRange);
        if (listed is not null)
        {
            return listed;
        }

        CoarseSearchRange custom = new($"{savedRange:0.###} mm", savedRange);
        CoarseSearchRangeChoices.Add(custom);
        return custom;
    }

    private void InvalidateFit()
    {
        _inputRevision++;
        if (_isSolving) _cancellation?.Cancel();
        _fit = null;
        _fitStatusText = null;
        ContourConfirmed = false;
        _previewCancellation?.Cancel();
        _previewReady = false;
        CrossPredictionContours = [];
        OutlierTracePointIndices = [];
        CoarseCandidates = [];
        SelectedCoarseCandidate = null;
        _fitRangeExpanded = false;
        RefreshApplyParametersState();
    }

    private CalibrationFitOptions FitOptions(bool useRingSeed = true) => _loadedFitOptions with
    {
        SearchCenter = useRingSeed ? _acceptedRingSeed ?? _searchCenter : _searchCenter,
        RadiusRangeMm = SelectedCoarseSearchRange.RadiusRangeMm,
        FitDvdPitch = FitDvdPitch && _target?.HasSingleRadialLine != true && !HasBoundarySegments,
        AdaptiveRobustScale = true,
        AutoRange = !HasBoundarySegments && SelectedCoarseSearchRange.Auto,
    };

    partial void OnFitDvdPitchChanged(bool value)
    {
        if (!_installing) InvalidateFit();
    }
    private CalibrationSession Session() => new(_target ?? throw new InvalidOperationException(L.T("请先创建图案。")),
        [], FitOptions(useRingSeed: false), _cdInterleave, PhotoSession(), CaptureConfirmedFit());

    private CalibrationConfirmedFit? CaptureConfirmedFit()
    {
        // Session() also runs while an export owns the busy state. Confirmation belongs
        // to the measured inputs and current candidate, not to the operation status.
        if (!ContourConfirmed || !_previewReady || _photoTransform is null || _photoPng is null
            || _target is null || _fit is null || SelectedCoarseCandidate is not { } selected
            || !CoarseCandidates.Contains(selected)) return null;
        int index = _fit.Candidates.ToList().FindIndex(candidate => candidate.Parameters == selected.Parameters
            && candidate.RotationRadians == selected.RotationRadians);
        return index < 0 ? null : new(_target.Id, _fit, index);
    }

    private CoarseCandidate[] FitCandidates(CalibrationFitResult result) => result.Candidates.Select((candidate, index) =>
        new CoarseCandidate(_target?.HasBoundaryPattern == true && (!result.Succeeded
            || candidate.Parameters != result.BestParameters || candidate.RotationRadians != result.RotationRadians)
            ? L.Format("HalfDisc.ComparisonCandidate", index + 1, candidate.RmsErrorMm)
            : L.F($"候选 {index + 1} · 偏差 {candidate.RmsErrorMm:0.###} mm"), candidate.Parameters,
            candidate.RotationRadians, candidate.RmsErrorMm, candidate.MaxErrorMm, candidate.OutlierCount)).ToArray();

    private async Task RestoreConfirmedFitAsync(CalibrationConfirmedFit saved, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CalibrationTarget? target = _target;
        long revision = _inputRevision;
        CalibrationFitResult result = saved.Result;
        _fit = result;
        CoarseCandidates = FitCandidates(result);
        CoarseCandidate candidate = CoarseCandidates[saved.SelectedCandidateIndex];
        _installing = true;
        try { SelectedCoarseCandidate = candidate; }
        finally { _installing = false; }
        _fitStatusPointCount = TracePoints.Count;
        await ShowCandidateAsync(candidate);
        token.ThrowIfCancellationRequested();
        // Another candidate can finish rendering before this cancelled preview resumes.
        // Approval belongs only to the saved result and its unchanged measurement inputs.
        if (_previewReady && ReferenceEquals(_target, target) && _inputRevision == revision
            && ReferenceEquals(_fit, result) && ReferenceEquals(SelectedCoarseCandidate, candidate))
        {
            ContourConfirmed = true;
            WriteSetupParameters(candidate.Parameters);
        }
        UpdateFitAssessment();
    }

    private async Task SolveAsync()
    {
        if (!IsLineFitStep || _target is not { } target) return;
        InvalidateFit();
        long revision = _inputRevision;
        var traces = CrossTraces();
        var segments = HasBoundarySegments ? BoundarySegments() : [];
        var options = FitOptions();
        int pointCount = HasBoundarySegments ? segments.Sum(segment => segment.Points.Count) : traces.Sum(trace => trace.Points.Count);
        await RunAsync(L.F($"正在用全部 {pointCount} 个曲线点计算参数…"), async token =>
        {
            _isSolving = true;
            FitAttempt attempt = await Task.Run(() => target.HasBoundaryPattern
                ? new FitAttempt(CalibrationBoundaryFitter.Fit(target, segments, options, token), options.RadiusRangeMm, false)
                : FitWithAutoRange(target, traces, options, SelectedCoarseSearchRange.Auto, token), token);
            CalibrationFitResult result = attempt.Result;
            _fitRangeMm = attempt.RangeMm;
            _fitRangeExpanded = attempt.Expanded;
            var candidates = FitCandidates(result);
            token.ThrowIfCancellationRequested();
            if (revision != _inputRevision || !ReferenceEquals(target, _target)) throw new OperationCanceledException();
            // Publish the result and its candidate set together, only for the current inputs.
            _fit = result;
            ContourConfirmed = false;
            CoarseCandidates = candidates;
            _installing = true;
            try { SelectedCoarseCandidate = CoarseCandidates.FirstOrDefault(); }
            finally { _installing = false; }
            await ShowCandidateAsync(SelectedCoarseCandidate);
            token.ThrowIfCancellationRequested();
            UpdateFitAssessment();
            _fitStatusPointCount = pointCount;
            if (FormatFitStatus() is { } status)
                _statusMessage = _fitStatusText = status;
        });
    }

    private string? FormatFitStatus()
    {
        if (_fit is not { } result) return null;
        if (!result.Succeeded)
        {
            string reasons = string.Join("\n", result.Warnings.Select(L.T));
            return result.Candidates.Count > 0
                ? L.Format("CalibrationApply.FailedWithCandidates", _fitStatusPointCount, reasons)
                : L.F($"计算未通过，已保留全部 {_fitStatusPointCount} 点。\n{reasons}");
        }
        if (SelectedCoarseCandidate is { } selected && !CandidateResidualAccepted(selected))
            return L.T("CalibrationApply.ResidualWarning");
        return _previewReady
            ? L.F($"已用全部 {_fitStatusPointCount} 点生成参数候选{(_fitRangeExpanded ? L.F($"，搜索范围自动扩大至 ±{_fitRangeMm:0.###} mm") : string.Empty)}。请核对橙色预测是否沿青色实拍点连续贴合，再决定是否应用。")
            : null;
    }

    private void RelocalizeFitStatus()
    {
        // Recompose from the raw fit reasons without touching the fit or rewriting
        // historical logs. A newer operation owns the dock once its text changes.
        if (_fitStatusText is not { } previous || _statusMessage != previous
            || FormatFitStatus() is not { } status) return;
        _statusMessage = _fitStatusText = status;
        OnPropertyChanged(nameof(LastStatusMessage));
        if (!IsBusy && _shell.JobStatusText == previous) _shell.JobStatusText = status;
    }

    /// <summary>
    /// Auto mode widens the radius box one step at a time until the optimum is interior.
    /// A boundary-clamped answer is not a measurement, so it is never published as the
    /// final result when a wider box can contain the real optimum. Explicit ranges keep
    /// their exact meaning for users who want to constrain the search.
    /// </summary>
    private static FitAttempt FitWithAutoRange(CalibrationTarget target, IReadOnlyList<CalibrationCrossTrace> traces,
        CalibrationFitOptions options, bool auto, CancellationToken token)
    {
        if (!auto)
            return new(CalibrationCrossFitter.Fit(target, traces, options, token), options.RadiusRangeMm, false);

        double[] ranges = [options.RadiusRangeMm, 0.1, 0.5, 2, 5];
        CalibrationFitResult? best = null;
        CalibrationFitResult? last = null;
        double bestRange = options.RadiusRangeMm;
        foreach (double range in ranges.Where(range => range >= options.RadiusRangeMm)
                     .Distinct().OrderBy(range => range))
        {
            token.ThrowIfCancellationRequested();
            CalibrationFitResult result = CalibrationCrossFitter.Fit(target, traces,
                options with { RadiusRangeMm = range }, token);
            last = result;
            // BD's observed curve continues beyond the drawing band, so a narrow box can
            // return an interior solution with a visibly unusable residual instead of a
            // boundary-clamped one. Keep widening while the fit is that poor.
            bool unusableBluRay = target.Parameters.Kind == CalibrationDiscKind.BluRay
                && result.RmsErrorMm > 0.8;
            if (result.Succeeded && !result.BoundaryLimited && !unusableBluRay)
                return new(result, range, range > options.RadiusRangeMm);
            if (result.Succeeded && (best is null || result.RmsErrorMm < best.RmsErrorMm))
            {
                best = result;
                bestRange = range;
            }
            if (!result.Succeeded && IsInputFailure(result)) break;
        }

        CalibrationFitResult fallback = best ?? last
            ?? CalibrationCrossFitter.Fit(target, traces, options, token);
        return new(fallback, bestRange, bestRange > options.RadiusRangeMm);
    }

    private static bool IsInputFailure(CalibrationFitResult result) => !result.Succeeded
        && result.Warnings.Any(warning =>
            warning.Contains("至少标出", StringComparison.Ordinal)
            || warning.Contains("覆盖范围太窄", StringComparison.Ordinal)
            || warning.Contains("向内回退", StringComparison.Ordinal)
            || warning.Contains("接近半圈", StringComparison.Ordinal)
            || warning.Contains("转角过大", StringComparison.Ordinal));

    private void UpdateFitAssessment()
    {
        if (_fit is null)
        {
            FitAssessment = ApplyParametersHint;
            return;
        }
        var lines = new List<string>();
        if (SelectedCoarseCandidate is { } candidate)
        {
            lines.Add(L.Format("CalibrationApply.ResidualSummary", candidate.RmsErrorMm, candidate.MaxErrorMm));
            if (_fit.Succeeded && !IsVerifiedBoundaryCandidate(candidate)) lines.Add(L.T("HalfDisc.ComparisonOnly"));
            if (!CandidateResidualAccepted(candidate)) lines.Add(L.T("CalibrationApply.ResidualWarning"));
        }
        if (!_fit.Succeeded) lines.Add(L.T("CalibrationApply.AutomaticNotPassed"));
        lines.AddRange(_fit.Warnings.Select(warning => L.T(warning)));
        if (_fitRangeExpanded) lines.Add(L.F($"已自动扩大搜索至 ±{_fitRangeMm:0.###} mm。"));
        if (OutlierTracePointIndices.Count > 0)
            lines.Add(L.F($"已标记 {OutlierTracePointIndices.Count} 个偏离较大的点。"));
        lines.Add(ApplyParametersHint);
        FitAssessment = string.Join(Environment.NewLine, lines.Distinct());
    }

    private void ApplyParameters()
    {
        if (!CanApplyParameters() || SelectedCoarseCandidate?.Parameters is not { } p) return;
        WriteSetupParameters(p);
        string ri = p.InnerRadiusMm.ToString("G17", CultureInfo.InvariantCulture);
        string ro = p.OuterRadiusMm.ToString("G17", CultureInfo.InvariantCulture);
        _statusMessage = L.Format("CalibrationSetup.FitApplied", ri, ro);
        _shell.AppendLog(_statusMessage + "\n");
    }

    private void WriteSetupParameters(CalibrationParameters p)
    {
        WriteSetupRadii(p);
        switch (p.Kind)
        {
            case CalibrationDiscKind.Cd:
                _state.CdSectors = p.Sectors.ToString(CultureInfo.InvariantCulture);
                _state.CdUsableSectors = p.OutputSectors.ToString(CultureInfo.InvariantCulture);
                _state.CdPitchLinear = _state.CdActualPitchLinear = p.PitchLinear.ToString("G17", CultureInfo.InvariantCulture);
                _state.CdPitchQuadratic = _state.CdActualPitchQuadratic = p.PitchQuadratic.ToString("G17", CultureInfo.InvariantCulture);
                _state.CdPitchCubic = _state.CdActualPitchCubic = p.PitchCubic.ToString("G17", CultureInfo.InvariantCulture);
                break;
            case CalibrationDiscKind.BluRay:
                _state.BdTotalSectors = p.Sectors.ToString(CultureInfo.InvariantCulture);
                _state.BdUsableSectors = p.OutputSectors.ToString(CultureInfo.InvariantCulture);
                _state.BdPitchLinear = _state.BdActualPitchLinear = p.PitchLinear.ToString("G17", CultureInfo.InvariantCulture);
                _state.BdPitchQuadratic = _state.BdActualPitchQuadratic = p.PitchQuadratic.ToString("G17", CultureInfo.InvariantCulture);
                _state.BdPitchCubic = _state.BdActualPitchCubic = p.PitchCubic.ToString("G17", CultureInfo.InvariantCulture);
                _state.BdInvertMapping = p.BluRayInvertMapping;
                break;
            default:
                _state.DvdPitchLinear = _state.DvdActualPitchLinear = p.PitchLinear.ToString("G17", CultureInfo.InvariantCulture);
                _state.DvdPitchQuadratic = _state.DvdActualPitchQuadratic = p.PitchQuadratic.ToString("G17", CultureInfo.InvariantCulture);
                _state.DvdPitchCubic = _state.DvdActualPitchCubic = p.PitchCubic.ToString("G17", CultureInfo.InvariantCulture);
                _state.DvdTotalSectors = p.Sectors.ToString(CultureInfo.InvariantCulture);
                _state.DvdUsableSectors = p.OutputSectors.ToString(CultureInfo.InvariantCulture);
                _state.DvdReadCompatibility = p.DvdReadCompatibility;
                break;
        }
    }

    private void WriteSetupRadii(CalibrationParameters p)
    {
        SelectedDiscType = DiscTypeName(p.Kind);
        string ri = p.InnerRadiusMm.ToString("G17", CultureInfo.InvariantCulture);
        string ro = p.OuterRadiusMm.ToString("G17", CultureInfo.InvariantCulture);
        switch (p.Kind)
        {
            case CalibrationDiscKind.Cd:
                _state.CdInnerRadius = ri; _state.CdOuterRadius = ro;
                _state.CdActualInnerRadius = ri; _state.CdActualOuterRadius = ro;
                break;
            case CalibrationDiscKind.BluRay:
                _state.BdInnerRadius = ri; _state.BdOuterRadius = ro;
                _state.BdActualInnerRadius = ri; _state.BdActualOuterRadius = ro;
                break;
            default:
                _state.DvdInnerRadius = ri; _state.DvdOuterRadius = ro;
                _state.DvdActualInnerRadius = ri; _state.DvdActualOuterRadius = ro;
                break;
        }
    }

    partial void OnSelectedCoarseCandidateChanged(CoarseCandidate? value)
    {
        ContourConfirmed = false;
        _previewReady = false;
        if (!_installing) _ = ShowCandidateAsync(value);
        RefreshApplyParametersState();
    }

    partial void OnSelectedCoarseSearchRangeChanged(CoarseSearchRange value)
    {
        OnPropertyChanged(nameof(SearchRangeHeader));
        if (!_installing) InvalidateFit();
    }

    private async Task ShowCandidateAsync(CoarseCandidate? candidate)
    {
        _previewCancellation?.Cancel();
        _previewReady = false;
        CrossPredictionContours = [];
        OutlierTracePointIndices = [];
        RefreshApplyParametersState();
        if (candidate is null || _target is not { } target) return;
        var traceSnapshot = TracePoints;
        double[] observedRadii = traceSnapshot.Select(point => Math.Sqrt(point.XMm * point.XMm + point.YMm * point.YMm)).ToArray();
        if (observedRadii.Length < 2) return;
        double minimumObservedRadius = observedRadii.Min(), maximumObservedRadius = observedRadii.Max();
        if (!(maximumObservedRadius > minimumObservedRadius)) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation?.Token ?? CancellationToken.None);
        _previewCancellation = cancellation;
        long revision = _inputRevision;
        try
        {
            var preview = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var predictions = Enumerable.Range(0, target.HasBoundaryPattern ? 2 : 1)
                    .Select(arm => CalibrationCrossPrediction.GetOutline(target, candidate.Parameters,
                        minimumObservedRadius, maximumObservedRadius, candidate.RotationRadians, armIndex: arm,
                        cancellationToken: cancellation.Token)).ToArray();
                // The predicted curve is the reference; a point beyond a small multiple of
                // the mean residual is visibly off it, not measurement noise.
                double threshold = Math.Max(0.2, candidate.RmsErrorMm * 2.5);
                int[] outliers = traceSnapshot
                    .Where(point => predictions.Min(prediction => DistanceToPolyline(point, prediction)) > threshold)
                    .Select(point => point.Index).OrderBy(index => index).ToArray();
                return (Contours: predictions.Select(prediction => prediction.Select(point => new Point(point.X, point.Y)).ToArray()).ToArray(), Outliers: outliers);
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (revision != _inputRevision || !ReferenceEquals(_target, target)
                || !ReferenceEquals(SelectedCoarseCandidate, candidate)) return;
            CrossPredictionContours = preview.Contours;
            OutlierTracePointIndices = preview.Outliers;
            _previewReady = true;
            UpdateFitAssessment();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            {
                CrossPredictionContours = [];
                ContourConfirmed = false;
                Report(ex, L.T("标定轮廓预览"));
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            RefreshApplyParametersState();
        }
    }

    private static double DistanceToPolyline(CalibrationTracePoint point, IReadOnlyList<CalibrationPoint> line)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < line.Count; i++)
        {
            double ax = line[i - 1].X, ay = line[i - 1].Y;
            double dx = line[i].X - ax, dy = line[i].Y - ay;
            double t = Math.Clamp(((point.XMm - ax) * dx + (point.YMm - ay) * dy)
                / Math.Max(1e-30, dx * dx + dy * dy), 0, 1);
            double ex = point.XMm - ax - t * dx, ey = point.YMm - ay - t * dy;
            best = Math.Min(best, ex * ex + ey * ey);
        }
        return Math.Sqrt(best);
    }

    private void BrowseOutput()
    {
        if (_target is null) return;
        bool discImage = _target.Parameters.Kind is CalibrationDiscKind.Dvd or CalibrationDiscKind.BluRay;
        string? path = _shell.Dialogs?.PickSave(
            discImage ? L.T("光盘镜像|*.iso") : L.T("CD 音轨|*.wav|CD 原始音轨|*.raw"),
            discImage ? ".iso" : ".wav",
            $"calibration-{_target.Id[..8]}{(discImage ? ".iso" : ".wav")}");
        if (path is not null) OutputPath = path;
    }

    private async Task ExportPngAsync()
    {
        if (!CanUseStageTarget() || _target is not { } target) return;
        bool schematic = target.HasClockRuler || target.HasWideAddressBands;
        string suffix = schematic ? "-preview.png" : ".png";
        string? path = _shell.Dialogs?.PickSave(L.T("PNG 图案|*.png"), ".png", $"calibration-{target.Id[..8]}{suffix}");
        if (path is null || !ReferenceEquals(target, _target)) return;
        var session = Session();
        await RunAsync(L.T("正在导出图案…"), async token =>
        {
            double canvas = CanvasOuterRadiusMm;
            int size = target.HasAngleSweep ? CalibrationPatternRenderer.HighResolutionSize : CalibrationReferencePattern.PixelSize;
            var cached = schematic && TargetImage?.PixelWidth == size ? TargetImage : null;
            await Task.Run(() =>
            {
                BitmapSource image = cached ?? (schematic
                    ? CalibrationPatternRenderer.Render(target, canvas, size, cancellationToken: token)
                    : CalibrationPatternRenderer.RenderEncodingImage(target, token));
                token.ThrowIfCancellationRequested();
                CalibrationPatternRenderer.SavePng(image, path);
                CalibrationSessionJson.Save(Path.ChangeExtension(path, ".calibration.json"), session);
            }, token);
            _statusMessage = L.T(target.HasWideAddressBands ? "WideBands.PreviewSaved" : target.HasClockRuler ? "Clock.PreviewSaved"
                : "PNG 与标定记录已导出。刻录请使用本页的生成或流式刻录按钮，以保持坐标比例。");
        });
    }

    private async Task GenerateImageAsync()
    {
        if (!CanUseStageTarget() || _target is not { } target) return;
        if (string.IsNullOrWhiteSpace(OutputPath)) BrowseOutput();
        if (string.IsNullOrWhiteSpace(OutputPath)) return;
        string path;
        try
        {
            path = Path.GetFullPath(OutputPath.Trim());
            string extension = Path.GetExtension(path).ToLowerInvariant();
            bool discImage = target.Parameters.Kind is CalibrationDiscKind.Dvd or CalibrationDiscKind.BluRay;
            if (discImage ? extension != ".iso" : extension is not (".wav" or ".raw"))
                throw new ArgumentException(L.T("请选择 DVD/BD 的 .iso 或 CD 的 .wav / .raw 输出文件。"));
        }
        catch (Exception ex) { Report(ex); return; }
        var session = Session();
        await RunAsync(L.T("正在生成标定文件…"), async token =>
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            string staging = Path.Combine(directory, $".calibration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                string stagedPath = Path.Combine(staging, Path.GetFileName(path));
                await Task.Run(() =>
                {
                    if (target.Parameters.Kind == CalibrationDiscKind.Cd)
                    {
                        CdTrackGenerator.GeneratePattern(target, stagedPath, session.CdInterleave,
                            cancellationToken: token);
                    }
                    else if (target.Parameters.Kind == CalibrationDiscKind.BluRay)
                    {
                        BluRayDiscParameters disc = BluRayParameters(target.Parameters);
                        using FileStream output = new(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        BluRayStreamingGenerator.GenerateToStream(
                            output,
                            new BluRayStreamingOptions(disc, 0, disc.OutputSectors),
                            BluRayPatternMapping(target),
                            progress: null,
                            cancellationToken: token);
                    }
                    else
                    {
                        DvdImageGenerator.GenerateCalibration(target, stagedPath, DvdOptions(target), token);
                    }
                    token.ThrowIfCancellationRequested();
                    CalibrationSessionJson.Save(Path.ChangeExtension(stagedPath, ".calibration.json"), session);
                }, token);
                token.ThrowIfCancellationRequested();
                CalibrationOutputPublisher.Publish(staging, directory);
                _sessionPath = Path.ChangeExtension(path, ".calibration.json");
                _statusMessage = L.T("标定文件与同名标定记录已生成。刻录后打开记录、导入照片，即可矫正和描线。");
            }
            finally
            {
                JobDiagnostics.TryCleanup(() => Directory.Delete(staging, recursive: true),
                    _shell.AppendLog, $"清理标定临时目录：{staging}");
            }
        });
    }

    internal static BluRayDiscParameters BluRayParameters(CalibrationParameters parameters)
    {
        if (parameters.Kind != CalibrationDiscKind.BluRay)
        {
            throw new ArgumentException(L.T("标定参数不是蓝光目标。"), nameof(parameters));
        }

        BluRayDiscParameters disc = new(
            checked((uint)parameters.Sectors),
            1,
            parameters.InnerRadiusMm,
            parameters.OuterRadiusMm,
            PitchLinear: parameters.PitchLinear, PitchQuadratic: parameters.PitchQuadratic, PitchCubic: parameters.PitchCubic,
            UsableSectors: parameters.UsableSectors is { } usable ? checked((uint)usable) : null);
        disc.Validate();
        return disc;
    }

    /// <summary>Full-disc calibration payload: pattern inside the saved drawing range, zeros
    /// outside, so the burn stream still covers every address.</summary>
    internal static BluRayPatternRangeMapping BluRayPatternMapping(CalibrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        BluRayDiscParameters disc = BluRayParameters(target.Parameters);
        uint start = checked((uint)target.Parameters.DrawingStartSector);
        uint end = target.Parameters.DrawingEndSectorExclusive == 0
            ? disc.OutputSectors
            : checked((uint)target.Parameters.DrawingEndSectorExclusive);
        if (target.HasWideAddressBands)
            return new BluRayPatternRangeMapping(disc, start, end,
                target.WideBandSectorRanges, target.Parameters.BluRayInvertMapping,
                target.HasAngleSweep ? new CalibrationAngleSweepSampler(target).Switches : null);
        // Version 14 uses the original PNG orientation (image Y down, track Y up).
        // Keep older targets and analytic artwork on their saved sampler contract.
        if (target.SchemaVersion == 14 && target.Artwork == CalibrationArtwork.ReferenceCross)
            return new BluRayPatternRangeMapping(disc, BluRayReferenceRaster.Value, start, end,
                target.Parameters.BluRayInvertMapping,
                Math.Max(0, target.DrawingInnerRadiusMm * target.DrawingInnerRadiusMm - 1e-10),
                target.DrawingOuterRadiusMm * target.DrawingOuterRadiusMm + 1e-10);
        return new BluRayPatternRangeMapping(
            disc,
            // BD track coordinates use Y up; the calibration canvas and original PNG
            // use Y down. Existing snapshots retain the sampler they were burned with.
            target.SchemaVersion >= 14 ? (x, y) => target.Sample(x, -y) : target.Sample,
            start,
            end,
            target.Parameters.BluRayInvertMapping,
            // Saved targets and the fixed reference pixels are immutable.
            allowParallelSampling: true);
    }

    private void SaveSession()
    {
        if (_target is null) return;
        string? path = _shell.Dialogs?.PickSave(L.T("标定记录|*.calibration.json"), ".calibration.json",
            _sessionPath is null ? $"calibration-{_target.Id[..8]}.calibration.json" : Path.GetFileName(_sessionPath));
        if (path is null) return;
        try { SaveSessionTo(path); _statusMessage = L.T("已保存图案、生成参数、照片、边界和曲线点。"); }
        catch (Exception ex) { Report(ex); }
    }

    internal void SaveSessionTo(string path)
    {
        CalibrationSessionJson.Save(path, Session());
        _sessionPath = path;
    }

    private async Task OpenSessionAsync()
    {
        string? path = _shell.Dialogs?.PickOpen(title: L.T("打开标定记录"), filter: L.T("标定记录|*.calibration.json;*.json"));
        if (path is null) return;
        await LoadSessionFromAsync(path);
    }

    private void PrepareStreamBurn()
    {
        if (!CanUseStageTarget() || _target is null) return;
        if (!EnsureSessionSavedForBurn()) return;
        // The standalone recorder takes its material from this frozen target.
        _shell.NavigateTo(Workspace.CalibrationBurn);
    }

    internal bool EnsureSessionSavedForBurn()
    {
        if (_target is null) return false;
        try
        {
            string? path = _sessionPath ?? _shell.Dialogs?.PickSave(L.T("标定记录|*.calibration.json"), ".calibration.json",
                $"calibration-{_target.Id[..8]}.calibration.json");
            if (path is null) return false;
            SaveSessionTo(path);
            return true;
        }
        catch (Exception ex) { Report(ex); return false; }
    }

    internal static DvdStreamingOptions DvdOptions(CalibrationTarget target) => new(checked((uint)target.Parameters.Sectors),
        target.Parameters.InnerRadiusMm, target.Parameters.OuterRadiusMm, target.Parameters.LinearDensity, 0,
        PitchLinear: target.Parameters.PitchLinear, PitchQuadratic: target.Parameters.PitchQuadratic,
        PitchCubic: target.Parameters.PitchCubic,
        ReadCompatibility: target.Parameters.DvdReadCompatibility,
        UsableSectors: target.Parameters.UsableSectors is { } usable ? checked((uint)usable) : null,
        ReadCompatibilityMode: target.EffectiveDvdReadCompatibilityMode);
    private static void ValidateOutputParameters(CalibrationParameters p)
    {
        p.Validate();
        if (p.Kind == CalibrationDiscKind.BluRay)
            BluRayParameters(p);
        else if (p.Kind == CalibrationDiscKind.Dvd)
            new DvdStreamingOptions(checked((uint)p.Sectors), p.InnerRadiusMm, p.OuterRadiusMm, p.LinearDensity, 0,
                PitchLinear: p.PitchLinear, PitchQuadratic: p.PitchQuadratic, PitchCubic: p.PitchCubic,
                UsableSectors: p.UsableSectors is { } usable ? checked((uint)usable) : null).Validate();
        else new CdDiscParameters(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.LinearDensity,
            PitchLinear: p.PitchLinear, PitchQuadratic: p.PitchQuadratic, PitchCubic: p.PitchCubic,
            UsableSectors: p.UsableSectors).Validate();
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        _cancellation = new();
        _fitStatusText = null;
        _statusMessage = message;
        _shell.BeginDockJob(L.T("Xaml.Calibration"), determinate: false);
        _shell.ReportDockJob(message, 0);
        _shell.SetBusy(true);
        _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] 标定：{message}\n"));
        try
        {
            await action(_cancellation.Token);
            // Calibration owns its files and records; completing a task must not register
            // one as the drawing project's output or relabel an earlier project artifact.
            _shell.CompleteDockJob(_statusMessage);
        }
        catch (OperationCanceledException)
        {
            if (_isSolving) InvalidateFit();
            _statusMessage = L.T("操作已取消。");
            _shell.AbortDockJob(_statusMessage, expandLog: false);
        }
        catch (Exception ex)
        {
            Report(ex, message);
            _shell.AbortDockJob(_statusMessage, expandLog: true);
        }
        finally
        {
            _isSolving = false;
            _cancellation.Dispose(); _cancellation = null;
            _shell.SetBusy(false);
            _shell.AppendLog(L.F($"[{DateTime.Now:HH:mm:ss}] 标定：{_statusMessage}\n"));
        }
    }

    private void Report(Exception ex, string? context = null)
    {
        _statusMessage = L.Error(ex);
        _shell.AppendLog(JobDiagnostics.FormatLog(context ?? L.T("标定操作"), ex));
        _shell.ShowToast(L.T("标定操作未完成"), _statusMessage, ToastKind.Error);
    }

    private void RefreshCommands()
    {
        CreateTargetCommand.NotifyCanExecuteChanged(); BrowseOutputCommand.NotifyCanExecuteChanged();
        SaveAsPresetCommand.NotifyCanExecuteChanged();
        RefreshCapacityCommands();
        ExportPngCommand.NotifyCanExecuteChanged(); GenerateImageCommand.NotifyCanExecuteChanged();
        StreamBurnCommand.NotifyCanExecuteChanged(); SaveSessionCommand.NotifyCanExecuteChanged();
        OpenSessionCommand.NotifyCanExecuteChanged(); SolveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged(); RefreshApplyParametersState();
        RefreshPhotoCommands();
        RefreshRingCommands();
    }

    public sealed record CoarseCandidate(string Label, CalibrationParameters Parameters,
        double RotationRadians, double RmsErrorMm, double MaxErrorMm = 0, int OutlierCount = 0);
    /// <summary>One search box for the two radius parameters. The automatic entry is the only
    /// localized label; it is computed so a language switch can refresh it in place.</summary>
    public sealed class CoarseSearchRange : INotifyPropertyChanged
    {
        private readonly string _label;

        public CoarseSearchRange(string label, double radiusRangeMm, bool Auto = false)
        {
            _label = label;
            RadiusRangeMm = radiusRangeMm;
            this.Auto = Auto;
        }

        public string Label => Auto ? L.T("自动（推荐）") : _label;

        public double RadiusRangeMm { get; }

        public bool Auto { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void RefreshLabel()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }
    private sealed record FitAttempt(CalibrationFitResult Result, double RangeMm, bool Expanded);
}
