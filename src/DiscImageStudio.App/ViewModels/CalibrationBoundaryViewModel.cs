using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private readonly List<int> _boundarySegmentIds = [0];
    public sealed class BoundarySegmentChoice(int id, string label) : ObservableObject
    {
        public int Id { get; } = id;
        private string _label = label;
        public string Label { get => _label; internal set => SetProperty(ref _label, value); }
    }
    public ObservableCollection<BoundarySegmentChoice> BoundarySegmentChoices { get; } = [];
    public RelayCommand AddBoundarySegmentCommand { get; private set; } = null!;
    public RelayCommand DeleteBoundarySegmentCommand { get; private set; } = null!;
    public IReadOnlyList<CoarseSearchRange> BoundarySearchRangeChoices { get; } =
        [new("±0.005 mm", 0.005), new("±0.01 mm", 0.01), new("±0.02 mm", 0.02), new("±0.05 mm", 0.05)];
    public IReadOnlyList<CoarseSearchRange> ActiveSearchRangeChoices => HasBoundarySegments
        ? BoundarySearchRangeChoices : CoarseSearchRangeChoices;

    private void InitializeBoundaryCommands()
    {
        AddBoundarySegmentCommand = new RelayCommand(AddBoundarySegment,
            () => !IsBusy && IsBoundarySegmentStep && _photoTransform is not null && _boundarySegmentIds.Count < 64);
        DeleteBoundarySegmentCommand = new RelayCommand(DeleteBoundarySegment,
            () => !IsBusy && IsBoundarySegmentStep && _photoTransform is not null
                && (_boundarySegmentIds.Count > 1 || TracePoints.Any(p => p.Arm == SelectedTraceArm)));
        RefreshBoundarySegments();
    }

    private void RefreshBoundarySegments()
    {
        // Keep both the collection and existing items stable while point counts
        // change; replacing them can clear a bound ComboBox's SelectedValue.
        for (int i = BoundarySegmentChoices.Count - 1; i >= 0; i--)
            if (!_boundarySegmentIds.Contains(BoundarySegmentChoices[i].Id)) BoundarySegmentChoices.RemoveAt(i);
        for (int i = 0; i < _boundarySegmentIds.Count; i++)
        {
            int id = _boundarySegmentIds[i];
            string label = L.Format("HalfDisc.Segment", id + 1, TracePoints.Count(p => p.Arm == id));
            var existing = BoundarySegmentChoices.FirstOrDefault(choice => choice.Id == id);
            if (existing is null) BoundarySegmentChoices.Insert(i, new(id, label));
            else
            {
                int oldIndex = BoundarySegmentChoices.IndexOf(existing);
                if (oldIndex != i) BoundarySegmentChoices.Move(oldIndex, i);
                existing.Label = label;
            }
        }
        OnPropertyChanged(nameof(TraceSummary));
        AddBoundarySegmentCommand?.NotifyCanExecuteChanged();
        DeleteBoundarySegmentCommand?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTraceArmChanged(int value)
    {
        DeleteBoundarySegmentCommand?.NotifyCanExecuteChanged();
    }

    private void ResetBoundarySegments()
    {
        _boundarySegmentIds.Clear();
        _boundarySegmentIds.Add(0);
        SelectedTraceArm = 0;
        RefreshBoundarySegments();
    }

    private void AddBoundarySegment()
    {
        if (!AddBoundarySegmentCommand.CanExecute(null)) return;
        // Reuse the current empty draft instead of accumulating unmarked segments.
        int? empty = _boundarySegmentIds.Where(id => !TracePoints.Any(p => p.Arm == id)).Cast<int?>().FirstOrDefault();
        int id = empty ?? Enumerable.Range(0, 64).First(candidate => !_boundarySegmentIds.Contains(candidate));
        if (empty is null) _boundarySegmentIds.Add(id);
        RefreshBoundarySegments();
        SelectedTraceArm = id;
    }

    private void DeleteBoundarySegment()
    {
        if (!DeleteBoundarySegmentCommand.CanExecute(null)) return;
        int selected = SelectedTraceArm;
        _boundarySegmentIds.Remove(selected);
        if (_boundarySegmentIds.Count == 0) _boundarySegmentIds.Add(0);
        TracePoints = TracePoints.Where(point => point.Arm != selected).ToArray();
        SelectedTraceArm = _boundarySegmentIds[0];
        RefreshBoundarySegments();
        InvalidateFit();
        RefreshCommands();
    }

    private IReadOnlyList<CalibrationBoundarySegment> BoundarySegments() => _boundarySegmentIds
        .Select(id => new CalibrationBoundarySegment(id, TracePoints.Where(p => p.Arm == id)
            .Select(p => new CalibrationPoint(p.XMm, p.YMm)).ToArray())).ToArray();

    private bool HasEnoughBoundaryPoints() => CalibrationBoundaryFitter.HasEnoughPoints(BoundarySegments());

    private void RestoreBoundarySegments(IReadOnlyList<CalibrationBoundarySegment>? segments)
    {
        _boundarySegmentIds.Clear();
        if (segments is not null) _boundarySegmentIds.AddRange(segments.Select(segment => segment.Id));
        if (_boundarySegmentIds.Count == 0) _boundarySegmentIds.Add(0);
        int index = 0;
        TracePoints = (segments ?? []).SelectMany(segment => segment.Points.Select(p =>
                new Controls.CalibrationTracePoint(segment.Id, index++, p.X, p.Y))).ToArray();
        SelectedTraceArm = _boundarySegmentIds[0];
        RefreshBoundarySegments();
    }
}
