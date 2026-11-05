using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class CalibrationView : UserControl
{
    private const double NarrowBreakpoint = 990;
    private const double StackedStageHeight = 420;
    private const double MinimumViewportForStretch = 100;

    private CalibrationViewModel? _calibration;
    private bool _narrowLayout = true;

    public CalibrationView()
    {
        InitializeComponent();
    }

    public CalibrationView(object viewModel) : this()
    {
        if (viewModel is CalibrationViewModel calibration)
        {
            _calibration = calibration;
            calibration.PropertyChanged += OnCalibrationPropertyChanged;
            IsVisibleChanged += (_, _) => calibration.SetTargetPreviewVisible(IsVisible);
        }
        DataContext = viewModel;
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    // Viewport or extent size changes re-run the pin: the first arrange can report a zero
    // viewport, and fractional-DPI rounding can leave the pinned content a pixel taller than
    // the viewport — the page must never settle into a state where it scrolls for no reason.
    private void OnWorkScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0
            || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        {
            UpdateResponsiveLayout();
        }
    }

    private void OnCalibrationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The accordion state decides which step row stretches, so track it live.
        if (e.PropertyName is nameof(CalibrationViewModel.StepOneExpanded)
            or nameof(CalibrationViewModel.StepTwoExpanded)
            or nameof(CalibrationViewModel.StepThreeExpanded))
        {
            UpdateStepRows();
        }
    }

    // Wide: the work area is exactly the viewport tall — the stage fills it and the step
    // column is an accordion where the expanded card takes the height the two collapsed
    // headers leave over, so all three steps stay on screen without scrolling. Narrow: the
    // step column stacks below the stage and the page scrolls as one surface.
    private void UpdateResponsiveLayout()
    {
        if (WorkArea is null || WorkScroll is null || StepsPanel is null || StageCard is null)
        {
            return;
        }

        _narrowLayout = ActualWidth < NarrowBreakpoint;
        if (_narrowLayout)
        {
            WorkArea.Height = double.NaN;
            StageRow.Height = GridLength.Auto;
            GapColumn.Width = new GridLength(0);
            StepsColumn.Width = new GridLength(0);
            Grid.SetColumn(StepsPanel, 0);
            Grid.SetRow(StepsPanel, 1);
            StepsPanel.Margin = new Thickness(0, 16, 0, 0);
            StageCard.Height = StackedStageHeight;
            UpdateStepRows();
            return;
        }

        StageRow.Height = new GridLength(1, GridUnitType.Star);
        GapColumn.Width = new GridLength(16);
        StepsColumn.Width = (GridLength)FindResource("SidePanelWidth");
        Grid.SetColumn(StepsPanel, 2);
        Grid.SetRow(StepsPanel, 0);
        StepsPanel.Margin = new Thickness(0);
        StageCard.Height = double.NaN;
        double viewport = WorkScroll.ViewportHeight;
        WorkArea.Height = viewport > MinimumViewportForStretch ? viewport - 2 : double.NaN;
        UpdateStepRows();
    }

    // The expanded step's row is the star: its card fills whatever the two collapsed headers
    // leave over (its body scrolls if the content needs more). All collapsed, every row is
    // auto and the cards pile up at the top as before.
    private void UpdateStepRows()
    {
        if (StepOneRow is null)
        {
            return;
        }

        bool stretch = !_narrowLayout && _calibration is not null;
        StepOneRow.Height = ToRowHeight(stretch && _calibration!.StepOneExpanded);
        StepTwoRow.Height = ToRowHeight(stretch && _calibration!.StepTwoExpanded);
        StepThreeRow.Height = ToRowHeight(stretch && _calibration!.StepThreeExpanded);
    }

    private static GridLength ToRowHeight(bool expanded) => expanded
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(1, GridUnitType.Auto);

    private void OnZoomOut(object sender, RoutedEventArgs e) => PhotoCanvas.ZoomBy(1 / 1.3);
    private void OnZoomIn(object sender, RoutedEventArgs e) => PhotoCanvas.ZoomBy(1.3);
    private void OnResetView(object sender, RoutedEventArgs e) => PhotoCanvas.ResetView();
    private void OnRemoveTracePoint(object sender, RoutedEventArgs e) => PhotoCanvas.RemoveSelectedTracePoint();
    private void OnRemoveRing(object sender, RoutedEventArgs e) => PhotoCanvas.RemoveSelectedRing();
}
