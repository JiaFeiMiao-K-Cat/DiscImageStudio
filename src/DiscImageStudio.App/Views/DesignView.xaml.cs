using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class DesignView : UserControl
{
    // Below this page width the three-column arrangement leaves the canvas too narrow to work
    // on (at 720px it collapsed to nothing), so the panels move under it instead.
    private const double NarrowBreakpoint = 900;

    // Fixed heights for the stacked arrangement: the page scrolls as one surface there, so the
    // canvas and the layer list need explicit viewports rather than star rows.
    private const double StackedCanvasHeight = 380;
    private const double StackedLayersHeight = 260;
    private const double MinimumViewportForStretch = 100;

    private readonly DesignViewModel _viewModel;

    public DesignView(DesignViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        // Page-level keys: the nudge must work wherever the focus sits (the layer list, a
        // button, the canvas), so it cannot hang off the canvas's own KeyDown.
        PreviewKeyDown += OnPagePreviewKeyDown;
    }

    /// <summary>Arrow keys nudge the selected layer by 1 mm; Delete removes it and Escape
    /// clears the selection. Text fields, sliders and the segmented control keep their own
    /// editing keys, so a coordinate being typed never moves the layer underneath the caret
    /// and a focused slider still responds to its arrows.</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or Slider or RadioButton)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left or Key.Right or Key.Up or Key.Down
                when _viewModel.SelectedLayer is { } layer:
            {
                // 1 mm per press; rounding keeps repeated presses from accumulating binary
                // noise in the mm coordinates.
                double dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                double dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                layer.TranslateXMm = Math.Round(layer.TranslateXMm + dx, 2);
                layer.TranslateYMm = Math.Round(layer.TranslateYMm + dy, 2);
                e.Handled = true;
                break;
            }
            case Key.Delete when _viewModel.SelectedLayer is not null:
                _viewModel.RemoveSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when _viewModel.SelectedLayer is not null:
                _viewModel.SelectedLayer = null;
                e.Handled = true;
                break;
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void OnFilesCardVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateResponsiveLayout();

    // Viewport or extent size changes re-run the pin: the first arrange can report a zero
    // viewport, and fractional-DPI rounding can leave the pinned content a pixel taller than
    // the viewport — scrolling back and forth must never be the permanent state of the page.
    private void OnWorkScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0
            || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        {
            UpdateResponsiveLayout();
        }
    }

    // Wide: the workspace is exactly the viewport tall — the canvas fills it and both rails
    // scroll inside their own columns. Narrow: canvas, layers/files and the property/tone rail stack
    // and the page scrolls; the canvas keeps a real size instead of being squeezed between the
    // panels.
    private void UpdateResponsiveLayout()
    {
        if (Workspace is null || WorkScroll is null)
        {
            return;
        }

        if (ActualWidth < NarrowBreakpoint)
        {
            Workspace.Height = double.NaN;
            LeftRail.MinHeight = 0;
            RightRail.MinHeight = 0;
            ToneRail.RailHeightBudget = null;
            StageRow.Height = GridLength.Auto;
            CanvasCard.Height = StackedCanvasHeight;
            LayersCard.MaxHeight = double.PositiveInfinity;
            LayersCard.Height = StackedLayersHeight;
            LeftRailScroll.Margin = new Thickness(0, 16, 0, 0);
            LeftRailScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            LeftRailScroll.ScrollToVerticalOffset(0);
            LayersColumn.Width = new GridLength(1, GridUnitType.Star);
            CanvasLeadColumn.Width = new GridLength(0);
            CanvasColumn.Width = new GridLength(0);
            PropsLeadColumn.Width = new GridLength(0);
            PropsColumn.Width = new GridLength(0);
            Grid.SetColumn(CanvasCard, 0);
            Grid.SetColumnSpan(CanvasCard, 5);
            Grid.SetColumn(LeftRailScroll, 0);
            Grid.SetColumnSpan(LeftRailScroll, 5);
            Grid.SetRow(LeftRailScroll, 1);
            // The rail becomes part of the page's single scroll surface: its own scrollbar
            // would nest a second viewport inside a page that is already scrolling.
            RightRailScroll.Margin = new Thickness(0, 16, 0, 0);
            RightRailScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            RightRailScroll.ScrollToVerticalOffset(0);
            Grid.SetColumn(RightRailScroll, 0);
            Grid.SetColumnSpan(RightRailScroll, 5);
            Grid.SetRow(RightRailScroll, 2);
            return;
        }

        StageRow.Height = new GridLength(1, GridUnitType.Star);
        CanvasCard.Height = double.NaN;
        LayersCard.Height = double.NaN;
        LeftRailScroll.Margin = new Thickness(0);
        LeftRailScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        RightRailScroll.Margin = new Thickness(0);
        RightRailScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        LayersColumn.Width = (GridLength)FindResource("SidePanelWidth");
        CanvasLeadColumn.Width = new GridLength(16);
        CanvasColumn.Width = new GridLength(1, GridUnitType.Star);
        PropsLeadColumn.Width = new GridLength(16);
        PropsColumn.Width = (GridLength)FindResource("SidePanelWidth");
        Grid.SetColumn(CanvasCard, 2);
        Grid.SetColumnSpan(CanvasCard, 1);
        Grid.SetColumn(LeftRailScroll, 0);
        Grid.SetColumnSpan(LeftRailScroll, 1);
        Grid.SetRow(LeftRailScroll, 0);
        Grid.SetColumn(RightRailScroll, 4);
        Grid.SetColumnSpan(RightRailScroll, 1);
        Grid.SetRow(RightRailScroll, 0);

        // Star rows cannot size inside a scroll viewer, so the work area is pinned to the
        // viewport height while the wide arrangement is active.
        double viewport = WorkScroll.ViewportHeight;
        Workspace.Height = viewport > MinimumViewportForStretch ? viewport - 2 : double.NaN;
        LeftRail.MinHeight = LeftRailScroll.ViewportHeight > MinimumViewportForStretch
            ? LeftRailScroll.ViewportHeight
            : 0;
        // A ScrollViewer measures with infinite height, so the star row alone cannot keep
        // the ListBox in a viewport. Bound the layer card to the room left by the file
        // controls; the list then scrolls internally instead of pushing the picker below
        // every layer. Very short windows can still scroll the whole rail.
        double fileControlsHeight = FilesCardGate.Visibility == Visibility.Visible
            ? FilesCardGate.ActualHeight
            : 0;
        LayersCard.MaxHeight = Math.Max(StackedLayersHeight,
            LeftRailScroll.ViewportHeight - fileControlsHeight);
        // The rail matches the viewport when its content is shorter: the tone card's star
        // row absorbs the slack so the column ends flush with the canvas instead of at a
        // ragged natural height. A taller rail keeps its natural height and scrolls. The
        // budget tells the tone module how much of that height it may consume, so the disc
        // preview shrinks before the column would spill into a scrollbar.
        RightRail.MinHeight = RightRailScroll.ViewportHeight > MinimumViewportForStretch
            ? RightRailScroll.ViewportHeight
            : 0;
        if (RightRailScroll.ViewportHeight > MinimumViewportForStretch && ToneCard.ActualHeight > 0)
        {
            double toneChrome = ToneCard.Padding.Top + ToneCard.Padding.Bottom
                + ToneCard.BorderThickness.Top + ToneCard.BorderThickness.Bottom;
            ToneRail.RailHeightBudget = RightRailScroll.ViewportHeight
                - PropsCard.ActualHeight - ToneCard.Margin.Top - toneChrome;
        }
        else
        {
            ToneRail.RailHeightBudget = null;
        }
    }
}
