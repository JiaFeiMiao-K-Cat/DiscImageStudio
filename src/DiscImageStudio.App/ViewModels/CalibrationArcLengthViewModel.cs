using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private CalibrationArcLengthMark[] _arcMarks = [];
    private CalibrationRadialArcFitResult? _arcFit;
    private string? _arcEditHint;

    public bool HasRadialArcTarget => _target?.HasRadialArcDashes == true;
    public bool IsRadialArcStep => HasRadialArcTarget && IsCoarseMode && IsTraceStep;
    public sealed class ArcBandChoice(string key) : ObservableObject
    {
        public string Label => L.T(key);
        internal void RefreshLabel() => OnPropertyChanged(nameof(Label));
    }
    public IReadOnlyList<ArcBandChoice> ArcBandNames { get; } = [new("Arc.Inner"), new("Arc.Middle"), new("Arc.Outer")];
    public sealed class ArcDirectionChoice(CalibrationArcBendDirection value, string key) : ObservableObject
    {
        public CalibrationArcBendDirection Value { get; } = value;
        public string Label => L.T(key);
        internal void RefreshLabel() => OnPropertyChanged(nameof(Label));
    }
    public IReadOnlyList<ArcDirectionChoice> ArcDirections { get; } =
        [new(CalibrationArcBendDirection.Unknown,"Arc.Unknown"), new(CalibrationArcBendDirection.Clockwise,"Arc.CW"),
         new(CalibrationArcBendDirection.Counterclockwise,"Arc.CCW")];
    [ObservableProperty] private int _selectedArcBand;
    private CalibrationArcLengthMark? CurrentArc => (uint)SelectedArcBand < (uint)_arcMarks.Length ? _arcMarks[SelectedArcBand] : null;
    private bool HasArcLengthMarks => _arcMarks.Any(m => m.Points.Count > 0);
    public IReadOnlyList<CalibrationPoint> ArcLengthPoints => CurrentArc?.Points ?? [];
    public CalibrationArcBendDirection SelectedArcDirection
    {
        get => CurrentArc?.Direction ?? CalibrationArcBendDirection.Unknown;
        set
        {
            if (CurrentArc is { } mark && value != mark.Direction && Enum.IsDefined(value))
                ReplaceArc(mark with { Direction = value });
        }
    }
    public bool CanConfirmArc => CanMeasureArcLengths() && !ShowTargetPreview
        && CalibrationArcLengthMark.TrySpan(ArcLengthPoints, out _);
    public bool ArcComplete
    {
        get => CurrentArc?.Complete == true;
        set
        {
            if (CurrentArc is { } mark && value != mark.Complete && (!value || CanConfirmArc))
                ReplaceArc(mark with { Complete = value });
        }
    }
    public string ArcMeasurementSummary
    {
        get
        {
            if (_arcEditHint is not null) return _arcEditHint;
            var points = ArcLengthPoints;
            if (!CalibrationArcLengthMark.TrySpan(points, out double degrees))
                return L.Format("Arc.PointCount", points.Count);
            double radius = points.Average(p => double.Hypot(p.X, p.Y));
            return L.Format("Arc.Span", Math.Abs(degrees), radius * Math.Abs(degrees) * Math.PI / 180);
        }
    }
    public string ArcFitSummary => _arcFit?.Corrected is { } p
        ? L.Format("Arc.Result", p.InnerRadiusMm, p.OuterRadiusMm, _arcFit.RmsDegrees)
        : _arcFit is not null ? L.T(_arcFit.Message) : L.Format("Arc.Count", _arcMarks.Count(m => m.Complete));

    public RelayCommand<CalibrationPoint> ArcLengthPointCommand { get; private set; } = null!;
    public RelayCommand UndoArcLengthPointCommand { get; private set; } = null!;
    public RelayCommand SolveArcLengthsCommand { get; private set; } = null!;
    public RelayCommand ApplyArcLengthsCommand { get; private set; } = null!;

    private bool CanMeasureArcLengths() => CanUseTarget() && IsRadialArcStep && RectifiedPhoto is not null
        && SelectedPhysicalDiscSize.OuterRadiusMm == 60;

    private void InitializeArcLengthCommands()
    {
        ArcLengthPointCommand = new RelayCommand<CalibrationPoint>(AddArcLengthPoint,
            _ => CanMeasureArcLengths() && !ShowTargetPreview && CurrentArc is { Points.Count: < 3 });
        UndoArcLengthPointCommand = new RelayCommand(() =>
        {
            if (CurrentArc is { Points.Count: > 0 } mark)
                ReplaceArc(mark with { Points = mark.Points.SkipLast(1).ToArray(), Complete = false });
        }, () => CanMeasureArcLengths() && !ShowTargetPreview && ArcLengthPoints.Count > 0);
        SolveArcLengthsCommand = new RelayCommand(SolveArcLengths,
            () => CanMeasureArcLengths() && _arcMarks.Count(m => m.Complete) >= 2);
        ApplyArcLengthsCommand = new RelayCommand(() =>
        {
            if (!CanMeasureArcLengths() || _arcFit?.Corrected is not { } p) return;
            WriteSetupParameters(p);
            _state.CdInterleave = _cdInterleave;
            ReportInitialGeometryApplied(p);
        }, () => CanMeasureArcLengths() && _arcFit?.Succeeded == true);
    }

    private void AddArcLengthPoint(CalibrationPoint? point)
    {
        if (point is null || CurrentArc is not { Points.Count: < 3 } mark || !CanMeasureArcLengths() || ShowTargetPreview) return;
        double r = double.Hypot(point.X, point.Y);
        if (!double.IsFinite(r) || r < 7.5 || r > SelectedPhysicalDiscSize.OuterRadiusMm) return;
        CalibrationPoint[] points = [.. mark.Points, point];
        if (points.Length == 3 && !CalibrationArcLengthMark.TrySpan(points, out _))
        {
            _arcEditHint = L.T("Arc.Distinct");
            OnPropertyChanged(nameof(ArcMeasurementSummary));
            return;
        }
        ReplaceArc(mark with { Points = points, Complete = false });
    }

    private void SolveArcLengths()
    {
        if (!CanMeasureArcLengths() || _target is not { } target) return;
        var measurements = new List<CalibrationRadialArcObservation>();
        foreach (var mark in _arcMarks.Where(m => m.Complete))
            if (CalibrationArcLengthMark.TrySpan(mark.Points, out double angle))
                measurements.Add(new(mark.BandIndex, Math.Abs(angle), mark.Direction, true, true, true,
                    mark.Points.Average(p => double.Hypot(p.X,p.Y))));
        _arcFit = CalibrationRadialArcFitter.Fit(target, measurements);
        _statusMessage = ArcFitSummary;
        NotifyArcLengthProperties();
    }

    private void SetArcLengthMarks(CalibrationArcLengthSession? session)
    {
        _arcMarks = _target?.RadialArcDashes.Select(arc =>
            session?.Marks.FirstOrDefault(m => m.BandIndex == arc.Index) ?? new CalibrationArcLengthMark(arc.Index, [])).ToArray() ?? [];
        _arcFit = null;
        _arcEditHint = null;
        SelectedArcBand = session?.SelectedBandIndex ?? 0;
        NotifyArcLengthProperties();
    }

    private void ReplaceArc(CalibrationArcLengthMark mark)
    {
        _arcMarks[mark.BandIndex] = mark;
        _arcFit = null;
        _arcEditHint = null;
        NotifyArcLengthProperties();
    }

    partial void OnSelectedArcBandChanged(int value)
    {
        _arcEditHint = null;
        NotifyArcLengthProperties();
    }

    private void NotifyArcLengthProperties()
    {
        OnPropertyChanged(nameof(ArcLengthPoints));
        OnPropertyChanged(nameof(SelectedArcDirection));
        OnPropertyChanged(nameof(ArcComplete));
        OnPropertyChanged(nameof(ArcMeasurementSummary));
        OnPropertyChanged(nameof(ArcFitSummary));
        RefreshArcLengthCommands();
    }

    private void RefreshArcLengthCommands()
    {
        OnPropertyChanged(nameof(CanConfirmArc));
        ArcLengthPointCommand?.NotifyCanExecuteChanged();
        UndoArcLengthPointCommand?.NotifyCanExecuteChanged();
        SolveArcLengthsCommand?.NotifyCanExecuteChanged();
        ApplyArcLengthsCommand?.NotifyCanExecuteChanged();
    }
}
