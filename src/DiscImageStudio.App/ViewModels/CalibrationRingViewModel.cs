using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Controls;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private CalibrationFitResult? _ringFit;
    private CalibrationSectorScaleResult? _sectorCheck;
    private long _ringRevision;
    // An accepted coarse estimate seeds the fine search on this photograph. The
    // target and its independent, possibly user-supplied search centre stay intact.
    private CalibrationParameters? _acceptedRingSeed;

    private CalibrationAngleSweepResult? _sweepReadout;
    public string RingSolveLabel => L.T(_target?.HasAngleSweep == true ? "AngleBands.Solve" : "Xaml.CoarseRingSolve");
    public string RingBoundaryInstructions => L.T(_target?.HasAngleSweep == true ? "AngleBands.Edges" : "BandEdges.Instructions");
    public bool IsRingStep => IsTraceStep && IsCoarseMode
        && (HasWideBandTarget || _target is { SchemaVersion: <= 12, HasCrossArms: true });
    [ObservableProperty] private IReadOnlyList<CalibrationRingArc> _ringArcs = [];
    public sealed class RingChoice(int index, bool wideBand) : ObservableObject
    {
        public int Index { get; } = index;
        public string Label => wideBand ? CalibrationRingArc.WideEdgeLabel(Index) : (Index + 1).ToString();
        internal void RefreshLabel() => OnPropertyChanged(nameof(Label));
    }
    [ObservableProperty] private IReadOnlyList<RingChoice> _ringChoices = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDragRingMode))] private bool _threePointMode;
    public bool IsDragRingMode => !ThreePointMode && !IsBandRayMode;
    public string RingPickerLabel => L.T(HasWideBandTarget ? "BandEdges.SelectEdge" : "Xaml.CoarseRingNumber");
    public string RingOperationHint => L.T(HasWideBandTarget
        ? "BandEdges.RayHint"
        : ThreePointMode ? "Xaml.CoarseRingPointHint" : "Xaml.CoarseRingDragHint");
    [ObservableProperty] private int _selectedRingIndex = -1;
    [ObservableProperty] private IReadOnlyList<CalibrationPoint> _ringPointMarkers = [];
    private string? _ringPointError;
    public string RingPointSummary => _ringPointError ?? (RingPointMarkers.Count == 3 && (uint)SelectedRingIndex < (uint)RingArcs.Count
        ? L.Format("Rings.PointsReady", RingArcs[SelectedRingIndex].RadiusMm)
        : L.Format("Rings.PointsCount", RingPointMarkers.Count));
    public RelayCommand<CalibrationPoint> RingPointCommand { get; private set; } = null!;
    public RelayCommand UndoRingPointCommand { get; private set; } = null!;
    public RelayCommand RestartRingPointsCommand { get; private set; } = null!;

    public RelayCommand SelectCoarseModeCommand { get; private set; } = null!;
    public RelayCommand SelectFineModeCommand { get; private set; } = null!;
    public RelayCommand<CalibrationRingEdit> RingEditedCommand { get; private set; } = null!;
    public IAsyncRelayCommand SolveRingsCommand { get; private set; } = null!;
    public RelayCommand UseRingSeedCommand { get; private set; } = null!;
    public RelayCommand ClearRingSeedCommand { get; private set; } = null!;
    public RelayCommand ApplyCoarseParametersCommand { get; private set; } = null!;
    public IAsyncRelayCommand VerifySectorScaleCommand { get; private set; } = null!;
    public RelayCommand ApplyVerifiedScaleCommand { get; private set; } = null!;
    public bool CanVerifySectorScale => _target?.HasAngularReference == true;
    public string SectorCheckSummary => _sectorCheck switch
    {
        { Corrected: { } p } result => L.Format("Phases.VerifiedScale", result.ApparentOverPhysical,
            result.InnerCoherence, result.OuterCoherence, p.InnerRadiusMm, p.OuterRadiusMm),
        { Status: CalibrationSectorScaleStatus.Disagree } bad =>
            L.Format("Phases.Disagree", bad.InnerScale, bad.OuterScale),
        { Status: CalibrationSectorScaleStatus.OutsideDisc } => L.T("Phases.OutOfBounds"),
        { } weak => L.Format("Phases.Unverified", weak.InnerCoherence, weak.OuterCoherence),
        _ => L.T("Phases.SectorHint"),
    };
    public bool HasAvailableRingSeed => RingEstimateAccepted(_ringFit, SelectedPhysicalDiscSize.OuterRadiusMm);
    public bool HasAcceptedRingSeed => _acceptedRingSeed is not null;
    public string FineInitialSummary => _acceptedRingSeed is { } seed
        ? L.Format("Phases.RingSeed", seed.InnerRadiusMm, seed.OuterRadiusMm)
        : _searchCenter is { } centre
            ? L.Format(IsIntermediateMode ? "HalfDisc.RecordSeed" : "Phases.RecordSeed", centre.InnerRadiusMm, centre.OuterRadiusMm)
            : L.T("Phases.PendingSeed");

    public string CoarseSummary
    {
        get
        {
            int count = RingArcs.Count(ring => ring.IsConfirmed);
            if (!CanMeasureAtPhotoSize) return L.T("WideBands.Size");
            if (RingArcs.Count < 3) return L.T("Rings.InsufficientTarget");
            if (_ringFit is null)
                return L.Format(HasWideBandTarget ? (_target!.SchemaVersion == 11 ? "BandEdges.CountLegacy" : "BandEdges.Count") : "Rings.Count",
                    count, RingArcs.Count);
            if (_ringFit.Candidates.FirstOrDefault() is not { } best)
                return string.Join("\n", _ringFit.Warnings.Select(L.T));
            if (HasWideBandTarget)
            {
                string radii = L.Format("WideBands.Result", best.Parameters.InnerRadiusMm, best.Parameters.OuterRadiusMm);
                if (!HasAvailableRingSeed) return radii + "\n" + L.T("Rings.CheckAlignment");
                string fit = count == 2 ? L.T("WideBands.Pair") : L.Format("BandEdges.Residual", best.MaxErrorMm);
                if (_target?.HasAngleSweep == true)
                {
                    if (_sweepReadout?.Corrected is { } corrected && ValidSweepRadius(corrected))
                    {
                        string offsets = string.Join(" / ", _sweepReadout.DegreesPerTurnOffset
                            .Select(value => value.ToString("+0.00;-0.00;0.00") + "°"));
                        return L.Format("AngleBands.Result", corrected.InnerRadiusMm, corrected.OuterRadiusMm,
                            _sweepReadout.StationCount, best.Parameters.InnerRadiusMm, best.Parameters.OuterRadiusMm)
                            + Environment.NewLine + L.Format("AngleBands.Angles", offsets);
                    }
                    string hint = count < 6 ? "AngleBands.NeedSix"
                        : !AllSixOnOneRay(RingObservations()) ? "AngleBands.OldMeasurements" : "AngleBands.Weak";
                    return radii + "\n" + L.T(hint);
                }
                return radii + "\n" + fit;
            }
            string estimate = L.Format("Rings.Estimate", best.Parameters.InnerRadiusMm,
                best.Parameters.OuterRadiusMm, best.RmsErrorMm, best.MaxErrorMm);
            if (!RingEstimateAccepted(_ringFit, SelectedPhysicalDiscSize.OuterRadiusMm))
                return estimate + "\n" + L.T("Rings.CheckAlignment");
            return _target?.HasAngularReference == false
                ? estimate + "\n" + L.T("Phases.LegacyRingEstimate") : estimate;
        }
    }

    public string BandIntervalSummary
    {
        get
        {
            if (_target is not { HasWideAddressBands: true } || !RingArcs.Any(r => r.IsConfirmed)) return string.Empty;
            var spans = CalibrationRingFitter.GetWideBandIntervals(_target, RingObservations());
            static string Join(IEnumerable<double?> values) =>
                string.Join(" / ", values.Select(v => v.HasValue ? v.Value.ToString("0.00") : "—"));
            string summary = L.Format("BandEdges.Widths", Join(spans.SegmentWidthsMm))
                + Environment.NewLine + L.Format("BandEdges.Spacings", Join(spans.InnerEdgeSpacingsMm));
            if (!spans.PredictedInnerEdgeSpacingsMm.Any(p => p.HasValue)) return summary;
            return summary + Environment.NewLine + L.Format("BandEdges.PredictedSpacings", Join(spans.PredictedInnerEdgeSpacingsMm));
        }
    }

    private void InitializeRingCommands()
    {
        InitializeClockCommands();
        InitializeArcLengthCommands();
        InitializeBandRayCommands();
        SelectCoarseModeCommand = new RelayCommand(() => SelectStage(CalibrationStage.Coarse), () => !IsBusy);
        SelectIntermediateModeCommand = new RelayCommand(() => SelectStage(CalibrationStage.SingleLine), () => !IsBusy);
        SelectFineModeCommand = new RelayCommand(() => SelectStage(CalibrationStage.Fine), () => !IsBusy);
        RingEditedCommand = new RelayCommand<CalibrationRingEdit>(EditRing, _ => CanMeasureRings() && IsDragRingMode);
        RingPointCommand = new RelayCommand<CalibrationPoint>(AddRingPoint,
            _ => CanMarkPoints() && RingPointMarkers.Count < 3);
        UndoRingPointCommand = new RelayCommand(UndoRingPoint,
            () => CanMarkPoints() && RingPointMarkers.Count > 0);
        RestartRingPointsCommand = new RelayCommand(RestartRingPoints,
            () => CanMarkPoints() && (RingPointMarkers.Count > 0 || RingArcs[SelectedRingIndex].IsConfirmed));
        SolveRingsCommand = new AsyncRelayCommand(SolveRingsAsync,
            () => CanMeasureRings() && (HasWideBandTarget
                ? CalibrationRingFitter.HasRequiredWideBandEdges(_target!, RingArcs.Where(r => r.IsConfirmed).Select(r => r.RingIndex))
                : RingArcs.Count(r => r.IsConfirmed) >= 3));
        UseRingSeedCommand = new RelayCommand(() => SetFineSeed(_ringFit?.BestParameters),
            () => CanUseTarget() && IsTraceStep && _target!.HasCrossArms && HasAvailableRingSeed && !HasAcceptedRingSeed);
        ClearRingSeedCommand = new RelayCommand(() => SetFineSeed(null),
            () => CanUseTarget() && IsTraceStep && HasAcceptedRingSeed);
        ApplyCoarseParametersCommand = new RelayCommand(ApplyCoarseParameters, CanUseRingEstimate);
        VerifySectorScaleCommand = new AsyncRelayCommand(VerifySectorScaleAsync,
            () => CanVerifySectorScale && CanUseRingEstimate() && RectifiedPhoto is not null);
        ApplyVerifiedScaleCommand = new RelayCommand(ApplyVerifiedScale,
            () => CanVerifySectorScale && CanUseRingEstimate() && _sectorCheck?.Succeeded == true);
    }

    private void RefreshRingCommands()
    {
        RefreshClockCommands();
        RefreshArcLengthCommands();
        RefreshBandRayCommands();
        OnPropertyChanged(nameof(RingOperationHint));
        OnPropertyChanged(nameof(RingPickerLabel));
        SelectCoarseModeCommand?.NotifyCanExecuteChanged();
        SelectIntermediateModeCommand?.NotifyCanExecuteChanged();
        SelectFineModeCommand?.NotifyCanExecuteChanged();
        RingEditedCommand?.NotifyCanExecuteChanged();
        RingPointCommand?.NotifyCanExecuteChanged();
        UndoRingPointCommand?.NotifyCanExecuteChanged();
        RestartRingPointsCommand?.NotifyCanExecuteChanged();
        SolveRingsCommand?.NotifyCanExecuteChanged();
        UseRingSeedCommand?.NotifyCanExecuteChanged();
        ClearRingSeedCommand?.NotifyCanExecuteChanged();
        ApplyCoarseParametersCommand?.NotifyCanExecuteChanged();
        VerifySectorScaleCommand?.NotifyCanExecuteChanged();
        ApplyVerifiedScaleCommand?.NotifyCanExecuteChanged();
    }

    private bool CanMeasureAtPhotoSize => _target is not { HasWideAddressBands: true, Parameters.Kind: CalibrationDiscKind.Cd }
        || SelectedPhysicalDiscSize.OuterRadiusMm == 60;
    private bool CanMeasureRings() => CanUseTarget() && IsRingStep && _photoTransform is not null && CanMeasureAtPhotoSize;
    private bool CanMarkPoints() => !IsBandRayMode && CanMeasureRings() && ThreePointMode && !ShowTargetPreview
        && (uint)SelectedRingIndex < (uint)RingArcs.Count;

    private void SetRingObservations(IReadOnlyList<CalibrationRingObservation>? saved = null)
    {
        ResetClockReadout();
        SetArcLengthMarks(null);
        _ringRevision++;
        _acceptedRingSeed = null;
        _ringFit = null;
        _sweepReadout = null;
        _sectorCheck = null;
        int ringCount = _target?.VisibleRings.Count ?? 0;
        RingArcs = Enumerable.Range(0, HasWideBandTarget ? 2 * ringCount : ringCount).Select(index =>
        {
            var observed = saved?.FirstOrDefault(item => item.RingIndex == index);
            return new CalibrationRingArc(index, observed?.RadiusMm ?? NominalEdgeRadius(index),
                observed is not null, observed?.Points);
        }).ToArray();
        IEnumerable<int> ids = HasWideBandTarget ? CalibrationRingFitter.WideBandEdgeOrder : Enumerable.Range(0, ringCount);
        RingChoices = ids.Select(id => new RingChoice(id, HasWideBandTarget)).ToArray();
        ThreePointMode = false;
        SelectedRingIndex = RingArcs.Count == 0 ? -1 : 0;
        ResetBandRayMode();
        ShowSelectedRingPoints();
        NotifyRingResult();
    }

    private CalibrationRingObservation[] RingObservations() => RingArcs.Where(r => r.IsConfirmed)
        .Select(r => new CalibrationRingObservation(r.RingIndex, r.RadiusMm, r.Points)).ToArray();

    private double NominalEdgeRadius(int index)
    {
        var bands = _target!.VisibleRings;
        if (index < bands.Count) return bands[index].InnerRadiusMm;
        var band = bands[index - bands.Count];
        return band.InnerRadiusMm + band.WidthMm;
    }

    private void EditRing(CalibrationRingEdit? edit)
    {
        if (edit is null || !CanMeasureRings() || !IsDragRingMode || ShowTargetPreview
            || (uint)edit.RingIndex >= (uint)RingArcs.Count) return;
        if (edit.RadiusMm is double radius
            && (!double.IsFinite(radius) || radius < 7.5 || radius > SelectedPhysicalDiscSize.OuterRadiusMm)) return;
        var previous = RingArcs[edit.RingIndex];
        if (previous.IsConfirmed && previous.RadiusMm == edit.RadiusMm) return;
        ReplaceRing(edit.RingIndex, edit.RadiusMm);
    }

    private void ReplaceRing(int index, double? measured, IReadOnlyList<CalibrationPoint>? points = null)
    {
        var arcs = RingArcs.ToArray();
        arcs[index] = new(index, measured ?? NominalEdgeRadius(index), measured.HasValue, points);
        RingArcs = arcs;
        if (_oldWideBandDraft?.RingIndex == index) _oldWideBandDraft = null;
        if (SelectedRingIndex == index) ShowSelectedRingPoints();
        InvalidateRingResult();
    }

    private void InvalidateRingResult()
    {
        _ringRevision++;
        _ringFit = null;
        _sweepReadout = null;
        _sectorCheck = null;
        // A fine fit that used the record's initial values did not depend on the edited rings.
        if (HasAcceptedRingSeed)
        {
            _acceptedRingSeed = null;
            InvalidateFit();
        }
        NotifyRingResult();
    }

    private void NotifyRingResult()
    {
        OnPropertyChanged(nameof(RingSolveLabel));
        OnPropertyChanged(nameof(RingBoundaryInstructions));
        OnPropertyChanged(nameof(CoarseSummary));
        OnPropertyChanged(nameof(BandIntervalSummary));
        OnPropertyChanged(nameof(HasAvailableRingSeed));
        OnPropertyChanged(nameof(HasAcceptedRingSeed));
        OnPropertyChanged(nameof(FineInitialSummary));
        OnPropertyChanged(nameof(CanVerifySectorScale));
        OnPropertyChanged(nameof(SectorCheckSummary));
        RefreshRingCommands();
    }

    partial void OnSelectedRingIndexChanged(int value)
    {
        ShowSelectedRingPoints();
        OnBandRaySelectionChanged();
    }
    partial void OnThreePointModeChanged(bool value)
    {
        ShowSelectedRingPoints();
    }
    partial void OnShowTargetPreviewChanged(bool value) => RefreshRingCommands();

    private void ShowSelectedRingPoints() => SetRingMarkers((uint)SelectedRingIndex < (uint)RingArcs.Count
        ? RingArcs[SelectedRingIndex].Points ?? [] : []);

    private void SetRingMarkers(IReadOnlyList<CalibrationPoint> points, string? error = null)
    {
        _ringPointError = error;
        RingPointMarkers = points;
        OnPropertyChanged(nameof(RingPointSummary));
        RefreshRingCommands();
    }

    private void AddRingPoint(CalibrationPoint? point)
    {
        if (!CanMarkPoints() || point is null || RingPointMarkers.Count >= 3) return;
        double radius = Math.Sqrt(point.X * point.X + point.Y * point.Y);
        if (!double.IsFinite(radius) || radius < 7.5 || radius > SelectedPhysicalDiscSize.OuterRadiusMm)
        {
            SetRingMarkers(RingPointMarkers, L.T("圆弧采样点必须位于中心孔与盘片外缘之间。"));
            return;
        }
        CalibrationPoint[] points = [.. RingPointMarkers, point];
        if (points.Length == 3)
        {
            try
            {
                double measured = CalibrationRingFitter.MeasureArc(points, SelectedPhysicalDiscSize.OuterRadiusMm);
                ReplaceRing(SelectedRingIndex, measured, points);
            }
            catch (ArgumentException ex)
            {
                // Keep the first two clicks; the user can retry the third or undo either one.
                SetRingMarkers(RingPointMarkers, L.Error(ex));
            }
            return;
        }
        if (RingArcs[SelectedRingIndex].IsConfirmed) ReplaceRing(SelectedRingIndex, null);
        SetRingMarkers(points);
    }

    private void UndoRingPoint()
    {
        if (!CanMarkPoints() || RingPointMarkers.Count == 0) return;
        var remaining = RingPointMarkers.Take(RingPointMarkers.Count - 1).ToArray();
        if (RingArcs[SelectedRingIndex].IsConfirmed) ReplaceRing(SelectedRingIndex, null);
        SetRingMarkers(remaining);
    }

    private void RestartRingPoints()
    {
        if (!CanMarkPoints()) return;
        if (RingArcs[SelectedRingIndex].IsConfirmed) ReplaceRing(SelectedRingIndex, null);
        else SetRingMarkers([]);
    }

    private async Task SolveRingsAsync()
    {
        if (!CanMeasureRings() || _target is not { } target) return;
        var observations = RingObservations();
        long revision = _ringRevision;
        var photo = RectifiedPhoto;
        double canvas = PhotoCanvasRadiusMm, physicalRadius = SelectedPhysicalDiscSize.OuterRadiusMm;
        double? angle = BandRayAngleDegrees;
        await RunAsync(L.T("Rings.Fitting"), async token =>
        {
            var (fit, sweep) = await Task.Run(() =>
            {
                var radial = CalibrationRingFitter.Fit(target, observations, token);
                CalibrationAngleSweepResult? angular = null;
                if (target.HasAngleSweep && photo is not null && AllSixOnOneRay(observations) && RingEstimateAccepted(radial, physicalRadius))
                    angular = CalibrationAngleSweepReader.Read(target, observations, canvas,
                        CalibrationClockPhotoReader.CreateSampler(photo, canvas), angle, token);
                return (radial, angular);
            }, token);
            token.ThrowIfCancellationRequested();
            if (revision != _ringRevision || !ReferenceEquals(target, _target)) throw new OperationCanceledException();
            _ringFit = fit;
            _sweepReadout = sweep;
            NotifyRingResult();
            _statusMessage = CoarseSummary;
        });
    }

    private static bool RingEstimateAccepted(CalibrationFitResult? fit, double physicalRadius) =>
        fit?.Succeeded == true && fit.Candidates.FirstOrDefault() is { } candidate
        && candidate.Parameters.InnerRadiusMm >= 7.5 && candidate.Parameters.OuterRadiusMm <= physicalRadius
        && ResidualAccepted(candidate.RmsErrorMm, candidate.MaxErrorMm);

    private bool CanUseRingEstimate() => CanMeasureRings() && HasAvailableRingSeed;
    private bool ValidSweepRadius(CalibrationParameters p) =>
        p.InnerRadiusMm >= 7.5 && p.OuterRadiusMm <= SelectedPhysicalDiscSize.OuterRadiusMm && p.OuterRadiusMm > p.InnerRadiusMm;

    private void SetFineSeed(CalibrationParameters? seed)
    {
        if (_acceptedRingSeed == seed) return;
        _acceptedRingSeed = seed;
        InvalidateFit();
        NotifyRingResult();
    }

    private void ApplyCoarseParameters()
    {
        if (!CanUseRingEstimate() || _ringFit!.BestParameters is not { } p) return;
        if (_sweepReadout?.Corrected is { } corrected && ValidSweepRadius(corrected)) p = corrected;
        if (HasWideBandTarget)
        {
            // Broad bands estimate radii at constant pitch. Their zero shape terms
            // are not a new measurement of the setup's independently fitted pitch.
            WriteSetupRadii(p);
            string sectors = p.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
            switch (p.Kind)
            {
                case CalibrationDiscKind.Cd:
                    _state.CdSectors = sectors;
                    _state.CdUsableSectors = p.OutputSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case CalibrationDiscKind.BluRay:
                    _state.BdTotalSectors = sectors;
                    _state.BdUsableSectors = p.OutputSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _state.BdInvertMapping = p.BluRayInvertMapping;
                    break;
                default:
                    _state.DvdTotalSectors = sectors;
                    _state.DvdUsableSectors = p.OutputSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
            if (p.Kind == CalibrationDiscKind.Cd) _state.CdInterleave = _cdInterleave;
        }
        else WriteSetupRadii(p);
        ReportInitialGeometryApplied(p);
    }

    private async Task VerifySectorScaleAsync()
    {
        if (!CanVerifySectorScale || !CanUseRingEstimate() || _ringFit?.BestParameters is not { } apparent
            || _target is not { } target || RectifiedPhoto is not { } photo) return;
        long revision = _ringRevision;
        double canvas = PhotoCanvasRadiusMm, edge = SelectedPhysicalDiscSize.OuterRadiusMm;
        await RunAsync(L.T("Phases.Verifying"), async token =>
        {
            _sectorCheck = null;
            NotifyRingResult();
            var check = await Task.Run(() => CalibrationSectorPhotoReader.Verify(photo, canvas, target, apparent, token), token);
            token.ThrowIfCancellationRequested();
            if (revision != _ringRevision || !ReferenceEquals(target, _target) || !ReferenceEquals(photo, RectifiedPhoto))
                throw new OperationCanceledException();
            if (check.Corrected is { } p && (p.InnerRadiusMm < 7.5 || p.OuterRadiusMm > edge))
                check = check with { Corrected = null, Status = CalibrationSectorScaleStatus.OutsideDisc };
            _sectorCheck = check;
            _statusMessage = SectorCheckSummary;
            NotifyRingResult();
        });
    }

    private void ApplyVerifiedScale()
    {
        if (!CanUseRingEstimate() || _sectorCheck?.Corrected is not { } p) return;
        // A physical estimate for a subsequent burn must travel with the track length
        // used to infer it. Keep the currently measured photo in its apparent coordinates.
        WriteSetupParameters(p);
        if (p.Kind == CalibrationDiscKind.Cd) _state.CdInterleave = _cdInterleave;
        ReportInitialGeometryApplied(p);
    }

    private void ReportInitialGeometryApplied(CalibrationParameters p)
    {
        _statusMessage = L.Format("Rings.Applied", p.InnerRadiusMm, p.OuterRadiusMm);
        _shell.AppendLog(_statusMessage + "\n");
        _shell.ShowToast(L.T("Phases.InitialApplied"), _statusMessage, ToastKind.Success);
    }
}
