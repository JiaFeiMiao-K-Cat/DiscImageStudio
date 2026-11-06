using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private enum CalibrationStage { Coarse, SingleLine, Fine }
    private CalibrationStage _stage = CalibrationStage.Coarse;
    public bool IsCoarseMode => _stage == CalibrationStage.Coarse;
    public bool IsIntermediateMode => _stage == CalibrationStage.SingleLine;
    public bool IsFineMode => _stage == CalibrationStage.Fine;
    public RelayCommand SelectIntermediateModeCommand { get; private set; } = null!;
    public bool IsFineStep => IsTraceStep && IsFineMode && _target is { HasCrossArms: true, HasSingleRadialLine: false, HasBoundaryPattern: false };
    public bool IsLineFitStep => IsFineStep || (IsTraceStep && IsIntermediateMode && (_target?.HasSingleRadialLine == true || HasBoundarySegments));
    public bool HasBoundarySegments => _target?.HasBoundaryPattern == true;
    public bool IsBoundarySegmentStep => IsLineFitStep && HasBoundarySegments;
    public bool CanFitDvdPitch => IsFineMode && _target is not null;

    private bool TargetMatchesStage => _stage switch
    {
        CalibrationStage.SingleLine => _target?.HasSingleRadialLine == true || HasBoundarySegments,
        CalibrationStage.Fine => _target is { HasCrossArms: true, HasSingleRadialLine: false, HasBoundaryPattern: false },
        // Old combination targets could be used both for ring measurements and cross fitting.
        _ => _target is { HasAngleSweep: true } or { SchemaVersion: <= 12 },
    };
    public bool NeedsStageTarget => _target is not null && !TargetMatchesStage;
    private bool CanUseStageTarget() => CanUseTarget() && TargetMatchesStage && (!IsCoarseMode || _target!.HasAngleSweep);

    public string TraceMarkingHint => (_target?.HasWavyBoundary == true ? L.T("WavyBoundary.Trace") + Environment.NewLine : string.Empty)
        + L.T(HasBoundarySegments ? "HalfDisc.Trace" : IsIntermediateMode ? "SingleLine.Trace" : "Xaml.PlacePointsEvenlyAlongAClearCurveFromInside");
    public string TraceConfirmationLabel => L.T(HasBoundarySegments ? "HalfDisc.Confirm" : "Xaml.ICheckedTheFullCurveItsShapeAndTurns");
    public string ApplyTraceLabel => L.T(IsIntermediateMode ? "SingleLine.Apply" : "Xaml.ApplyCalibration");

    private void SelectStage(CalibrationStage stage)
    {
        if (_stage == stage) return;
        _stage = stage;
        OnPropertyChanged(nameof(IsCoarseMode));
        OnPropertyChanged(nameof(IsIntermediateMode));
        OnPropertyChanged(nameof(IsFineMode));
        OnPropertyChanged(nameof(IsRingStep));
        OnPropertyChanged(nameof(IsRadialArcStep));
        OnPropertyChanged(nameof(IsClockStep));
        NotifyTraceStage();
        ShowTargetPreview = false;
        if (NeedsStageTarget) StepOneExpanded = true;
        else if (IsTraceStep) StepThreeExpanded = true;
        else if (_target is not null) StepTwoExpanded = true;
        RefreshCommands();
    }

    private void NotifyTraceStage()
    {
        OnPropertyChanged(nameof(IsFineStep));
        OnPropertyChanged(nameof(IsLineFitStep));
        OnPropertyChanged(nameof(HasBoundarySegments));
        OnPropertyChanged(nameof(IsBoundarySegmentStep));
        OnPropertyChanged(nameof(ActiveSearchRangeChoices));
        OnPropertyChanged(nameof(CanFitDvdPitch));
        OnPropertyChanged(nameof(NeedsStageTarget));
        OnPropertyChanged(nameof(TraceMarkingHint));
        OnPropertyChanged(nameof(TraceConfirmationLabel));
        OnPropertyChanged(nameof(FineInitialSummary));
        OnPropertyChanged(nameof(ApplyTraceLabel));
    }

    private void RestoreStage(CalibrationSession session)
    {
        var target = session.Target;
        var stage = target.HasSingleRadialLine || target.HasBoundaryPattern ? CalibrationStage.SingleLine
            : !target.HasCrossArms ? CalibrationStage.Coarse
            : target.Artwork == CalibrationArtwork.ReferenceCross ? CalibrationStage.Fine
            : session.Photo?.IsCoarseMode == true || (session.Photo is null && target.HasAngularReference)
                ? CalibrationStage.Coarse : CalibrationStage.Fine;
        SelectStage(stage);
    }
}
