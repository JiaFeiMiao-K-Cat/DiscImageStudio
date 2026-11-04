using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private CalibrationClockResult? _clockReadout;
    public bool HasClockTarget => _target?.HasClockRuler == true;
    public bool IsClockStep => IsTraceStep && IsCoarseMode && HasClockTarget;
    public double? PreferredClockAngle { get; private set; }
    public IReadOnlyList<double> ClockReadAngles { get; private set; } = [];
    public IAsyncRelayCommand ReadClockCommand { get; private set; } = null!;
    public RelayCommand ApplyClockCommand { get; private set; } = null!;
    public RelayCommand<double?> PreferClockAngleCommand { get; private set; } = null!;
    public string ClockReadSummary => _clockReadout switch
    {
        { Corrected: { } p } result => L.Format("Clock.Result", result.Sectors.Count, result.CoverageDegrees,
            p.InnerRadiusMm, p.OuterRadiusMm),
        { Status: CalibrationClockStatus.ConflictingSectors } => L.T("Clock.Conflict"),
        { } => L.T("Clock.Weak"),
        _ when SelectedPhysicalDiscSize.OuterRadiusMm != 60 => L.T("Clock.DiscSize"),
        _ => L.T("Clock.ReadHint")
    };

    private void InitializeClockCommands()
    {
        ReadClockCommand = new AsyncRelayCommand(ReadClockAsync, CanReadClock);
        ApplyClockCommand = new RelayCommand(ApplyClock, () => CanReadClock() && _clockReadout?.Succeeded == true);
        PreferClockAngleCommand = new RelayCommand<double?>(SetPreferredClockAngle,
            angle => CanReadClock() && !ShowTargetPreview && angle is >= 0 and < 360);
    }

    private void SetPreferredClockAngle(double? angle)
    {
        if (angle == PreferredClockAngle) return;
        PreferredClockAngle = angle;
        OnPropertyChanged(nameof(PreferredClockAngle));
        SetClockReadout(null);
    }

    private bool CanReadClock() => CanUseTarget() && IsClockStep && RectifiedPhoto is not null
        && SelectedPhysicalDiscSize.OuterRadiusMm == 60;

    private async Task ReadClockAsync()
    {
        if (!CanReadClock() || _target is not { } target || RectifiedPhoto is not { } photo) return;
        double canvas = PhotoCanvasRadiusMm;
        double? preferredAngle = PreferredClockAngle;
        await RunAsync(L.T("Clock.Reading"), async token =>
        {
            SetClockReadout(null);
            var result = await Task.Run(() =>
                CalibrationClockPhotoReader.Read(photo, canvas, target, preferredAngle, token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(target, _target) || !ReferenceEquals(photo, RectifiedPhoto))
                throw new OperationCanceledException();
            SetClockReadout(result);
            ShowTargetPreview = false;
            _statusMessage = ClockReadSummary;
        });
    }

    private void SetClockReadout(CalibrationClockResult? result)
    {
        _clockReadout = result;
        ClockReadAngles = result?.Succeeded == true ? result.Sectors.Select(s => s.CenterDegrees).ToArray() : [];
        OnPropertyChanged(nameof(ClockReadAngles));
        OnPropertyChanged(nameof(ClockReadSummary));
        ApplyClockCommand?.NotifyCanExecuteChanged();
    }

    private void ResetClockReadout()
    {
        PreferredClockAngle = null;
        OnPropertyChanged(nameof(PreferredClockAngle));
        SetClockReadout(null);
    }

    private void ApplyClock()
    {
        if (!CanReadClock() || _clockReadout?.Corrected is not { } p) return;
        WriteSetupParameters(p);
        _state.CdInterleave = _cdInterleave;
        ReportInitialGeometryApplied(p);
    }

    private void RefreshClockCommands()
    {
        ReadClockCommand?.NotifyCanExecuteChanged();
        ApplyClockCommand?.NotifyCanExecuteChanged();
        PreferClockAngleCommand?.NotifyCanExecuteChanged();
    }
}
