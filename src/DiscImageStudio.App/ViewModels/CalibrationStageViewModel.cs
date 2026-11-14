using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private enum CalibrationStage { Coarse, SingleLine, Fine }
    private CalibrationStage _stage = CalibrationStage.Coarse;
    private readonly Dictionary<CalibrationStage, CalibrationSession> _autoWizardSessions = [];
    private bool _installingAutoWizardSession;
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
        if (!_installingAutoWizardSession && _autoWizardSessions.TryGetValue(stage, out CalibrationSession? savedSession))
        {
            if (_target is not null)
            {
                _autoWizardSessions[_stage] = Session();
            }

            _ = LoadAutoWizardStageAsync(savedSession);
            return;
        }

        _stage = stage;
        NotifyStageProperties();
        ShowTargetPreview = false;
        if (NeedsStageTarget) StepOneExpanded = true;
        else if (IsTraceStep) StepThreeExpanded = true;
        else if (_target is not null) StepTwoExpanded = true;
        RefreshCommands();
    }

    internal void ApplyAutoCalibrationSessions(IReadOnlyList<CalibrationSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        Dictionary<CalibrationStage, CalibrationSession> stageSessions = [];
        foreach (CalibrationSession session in sessions)
        {
            ArgumentNullException.ThrowIfNull(session);
            CalibrationStage stage = GetStage(session);
            if (!stageSessions.TryAdd(stage, session))
            {
                throw new ArgumentException($"自动标定记录中包含重复的{StageName(stage)}阶段。", nameof(sessions));
            }
        }

        if (stageSessions.Count != 3)
        {
            throw new ArgumentException("自动标定必须为粗标定、边界标定和精细标定各提供一份记录。", nameof(sessions));
        }

        _autoWizardSessions.Clear();
        foreach ((CalibrationStage stage, CalibrationSession session) in stageSessions)
        {
            _autoWizardSessions.Add(stage, session);
        }

        InstallAutoWizardStage(CalibrationStage.Coarse, stageSessions[CalibrationStage.Coarse]);
    }

    private static CalibrationStage GetStage(CalibrationSession session)
    {
        if (session.Target.HasBoundaryPattern || session.Target.HasSingleRadialLine)
        {
            return CalibrationStage.SingleLine;
        }

        return session.Target.HasCrossArms ? CalibrationStage.Fine : CalibrationStage.Coarse;
    }

    private static string StageName(CalibrationStage stage) => stage switch
    {
        CalibrationStage.Coarse => "粗标定",
        CalibrationStage.SingleLine => "边界标定",
        CalibrationStage.Fine => "精细标定",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };

    private void InstallAutoWizardStage(CalibrationStage stage, CalibrationSession session)
    {
        _installingAutoWizardSession = true;
        try
        {
            _stage = stage;
            InstallTarget(session.Target, session.FitOptions, session.CdInterleave);
            WriteSetupParameters(session.Target.Parameters);
            if (session.Target.Parameters.Kind == CalibrationDiscKind.Cd)
            {
                _state.CdInterleave = session.CdInterleave;
            }

            _sessionPath = null;
            OutputPath = string.Empty;
            _statusMessage = L.T("自动标定数据已载入，可继续编辑当前阶段。");
            NotifyStageProperties();
            RefreshCommands();
        }
        finally
        {
            _installingAutoWizardSession = false;
        }
    }

    private async Task LoadAutoWizardStageAsync(CalibrationSession session)
    {
        _installingAutoWizardSession = true;
        try
        {
            await LoadSessionAsync(session);
        }
        finally
        {
            _installingAutoWizardSession = false;
        }
    }

    private void NotifyStageProperties()
    {
        OnPropertyChanged(nameof(IsCoarseMode));
        OnPropertyChanged(nameof(IsIntermediateMode));
        OnPropertyChanged(nameof(IsFineMode));
        OnPropertyChanged(nameof(IsRingStep));
        OnPropertyChanged(nameof(IsRadialArcStep));
        OnPropertyChanged(nameof(IsClockStep));
        NotifyTraceStage();
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
