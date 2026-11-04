using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Controls;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private const double BandRayToleranceDegrees = 3;
    private string? _bandRayHint;
    // An old unfinished three-point edit is retained if a record is simply opened and saved.
    private CalibrationRingArcDraft? _oldWideBandDraft;
    public bool IsBandRayMode => HasWideBandTarget;
    public string RestartBandRayLabel => L.T(_target?.HasAngleSweep == true ? "AngleBands.Restart" : "BandEdges.RestartRay");

    private CalibrationRingArc? CurrentBandEdge => (uint)SelectedRingIndex < (uint)RingArcs.Count
        ? RingArcs[SelectedRingIndex] : null;

    public double? BandRayAngleDegrees => RingArcs.FirstOrDefault(r => r.Points?.Count == 1)?.Points?[0]
        is { } p ? BearingDegrees(p) : null;

    public string BandRaySummary
    {
        get
        {
            if (_bandRayHint is not null) return _bandRayHint;
            if (!HasWideBandTarget || CurrentBandEdge is not { } edge) return string.Empty;
            if (_target?.HasAngleSweep == true && RingArcs.Any(r => r.IsConfirmed && r.Points is not { Count: 1 }))
                return L.T("AngleBands.OldMeasurements");
            if (RingArcs.All(r => r.IsConfirmed)) return L.T("BandRay.Complete");
            string label = CalibrationRingArc.WideEdgeLabel(edge.RingIndex);
            if (edge.IsConfirmed) return L.Format("BandRay.AlreadyMarked", label);
            return L.Format(BandRayAngleDegrees.HasValue ? "BandRay.Next" : "BandRay.First", label);
        }
    }

    public RelayCommand<CalibrationPoint> BandRayPointCommand { get; private set; } = null!;
    public RelayCommand RestartBandRayCommand { get; private set; } = null!;
    public RelayCommand RemoveSelectedBandEdgeCommand { get; private set; } = null!;

    private void InitializeBandRayCommands()
    {
        BandRayPointCommand = new RelayCommand<CalibrationPoint>(AddBandRayPoint, _ => CanEditBandRay());
        RestartBandRayCommand = new RelayCommand(RestartBandRay,
            () => CanEditBandRay() && (_oldWideBandDraft is not null || RingArcs.Any(CanRestartBandEdge)));
        RemoveSelectedBandEdgeCommand = new RelayCommand(() =>
        {
            if (CanEditBandRay() && CurrentBandEdge is { IsConfirmed: true } edge)
            {
                _bandRayHint = null;
                ReplaceRing(edge.RingIndex, null);
            }
        }, () => CanEditBandRay() && CurrentBandEdge?.IsConfirmed == true);
    }

    private bool CanEditBandRay() => IsBandRayMode && CanMeasureRings() && !ShowTargetPreview;
    private static double BearingDegrees(CalibrationPoint p) => Math.Atan2(p.Y, p.X) * 180 / Math.PI;
    private static bool WithinRay(double first, double second) =>
        Math.Abs(Math.IEEERemainder(first - second, 360)) <= BandRayToleranceDegrees;

    private static bool AllSixOnOneRay(IReadOnlyList<CalibrationRingObservation> observations)
    {
        if (observations.Count != 6 || observations.Any(p => p.Points is not { Count: 1 })) return false;
        var angles = observations.Select(p => BearingDegrees(p.Points![0])).ToArray();
        return angles.All(a => angles.All(b => WithinRay(a, b)));
    }

    private void AddBandRayPoint(CalibrationPoint? point)
    {
        if (point is null || !CanEditBandRay() || CurrentBandEdge is not { } selected) return;
        if (selected.IsConfirmed)
        {
            SetBandRayHint(L.Format("BandRay.AlreadyMarked", CalibrationRingArc.WideEdgeLabel(selected.RingIndex)));
            return;
        }
        double radius = double.Hypot(point.X, point.Y);
        if (!double.IsFinite(radius) || radius < 7.5 || radius > SelectedPhysicalDiscSize.OuterRadiusMm)
        {
            SetBandRayHint(L.T("圆弧采样点必须位于中心孔与盘片外缘之间。"));
            return;
        }
        // A common direction makes neighbouring width measurements comparable. Keep the
        // actual clicks for reopening the photograph and for inspection at higher zoom.
        double angle = BearingDegrees(point);
        if (RingArcs.Any(edge => edge.Points is { Count: 1 } points
            && !WithinRay(angle, BearingDegrees(points[0]))))
        {
            SetBandRayHint(L.Format("BandRay.Direction", BandRayToleranceDegrees));
            return;
        }
        bool beforeSelection = true;
        foreach (int edgeId in CalibrationRingFitter.WideBandEdgeOrder)
        {
            if (edgeId == selected.RingIndex) { beforeSelection = false; continue; }
            var other = RingArcs[edgeId];
            if (other.IsConfirmed && (beforeSelection ? other.RadiusMm >= radius : other.RadiusMm <= radius))
            {
                SetBandRayHint(L.T("BandRay.Order"));
                return;
            }
        }
        _bandRayHint = null;
        ReplaceRing(selected.RingIndex, radius, [point]);
        SelectNextBandEdge(selected.RingIndex);
    }

    private void SelectNextBandEdge(int? afterEdge = null)
    {
        var order = CalibrationRingFitter.WideBandEdgeOrder;
        int start = afterEdge.HasValue ? order.TakeWhile(id => id != afterEdge.Value).Count() + 1 : 0;
        for (int step = 0; step < order.Count; step++)
        {
            int id = order[(start + step) % order.Count];
            if ((uint)id < (uint)RingArcs.Count && !RingArcs[id].IsConfirmed)
            {
                SelectedRingIndex = id;
                return;
            }
        }
    }

    private void RestartBandRay()
    {
        if (!CanEditBandRay()) return;
        _bandRayHint = null;
        _oldWideBandDraft = null;
        foreach (var edge in RingArcs.Where(CanRestartBandEdge).ToArray())
            ReplaceRing(edge.RingIndex, null);
        SelectNextBandEdge();
        RefreshBandRayCommands();
    }

    private bool CanRestartBandEdge(CalibrationRingArc edge) =>
        _target?.HasAngleSweep == true ? edge.IsConfirmed : edge.Points?.Count == 1;

    private void ResetBandRayMode()
    {
        _bandRayHint = null;
        _oldWideBandDraft = null;
        OnPropertyChanged(nameof(IsBandRayMode));
        OnPropertyChanged(nameof(IsDragRingMode));
        OnPropertyChanged(nameof(RestartBandRayLabel));
        if (IsBandRayMode) SelectNextBandEdge();
        RefreshBandRayCommands();
    }

    private void SetBandRayHint(string hint)
    {
        _bandRayHint = hint;
        OnPropertyChanged(nameof(BandRaySummary));
    }

    private void OnBandRaySelectionChanged()
    {
        _bandRayHint = null;
        RefreshBandRayCommands();
    }

    private void RefreshBandRayCommands()
    {
        OnPropertyChanged(nameof(BandRayAngleDegrees));
        OnPropertyChanged(nameof(BandRaySummary));
        BandRayPointCommand?.NotifyCanExecuteChanged();
        RestartBandRayCommand?.NotifyCanExecuteChanged();
        RemoveSelectedBandEdgeCommand?.NotifyCanExecuteChanged();
    }
}
