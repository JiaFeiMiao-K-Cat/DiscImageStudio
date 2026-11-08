using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
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

    // One row-height slide, eased out; short enough to feel attached to the pointer.
    private static readonly TimeSpan RowShiftDuration = TimeSpan.FromMilliseconds(160);

    private readonly DesignViewModel _viewModel;

    private DesignLayerViewModel? _pressLayer;
    private Point _pressPoint;
    private LayerDragSession? _layerDrag;

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
        // A drag in flight owns Escape: it cancels the reorder and puts the row back, which
        // must not also drop the selection the row was carrying.
        if (_layerDrag is not null && e.Key == Key.Escape)
        {
            FinishLayerDrag(commit: false);
            e.Handled = true;
            return;
        }

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

    // Claim file drops at the page level. Every other drag is refused explicitly: the page
    // is the drop target for its whole area, so leaving one unhandled would keep the
    // source's copy cursor while the drop itself does nothing. Layer reordering uses mouse
    // capture rather than WPF file drag-and-drop.
    private void OnImageDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;

        // Hover checks extensions only: the drop repeats the filter with File.Exists, so a
        // network share is not stat'ed on every DragOver tick.
        if (CanAcceptDrop(e) && GetDroppedPaths(e.Data) is { } paths && paths.Any(IsImagePath))
        {
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void OnImageDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (!CanAcceptDrop(e) || GetDroppedPaths(e.Data) is not { } paths)
        {
            return;
        }

        string[] images = paths.Where(IsImagePath).Where(File.Exists).ToArray();
        if (images.Length == 0)
        {
            return;
        }

        _viewModel.ImportImages(images);
        e.Effects = DragDropEffects.Copy;
    }

    private bool CanAcceptDrop(DragEventArgs e) =>
        _viewModel.ImportImagesCommand.CanExecute(null)
        && (e.AllowedEffects & DragDropEffects.Copy) != 0;

    private static string[]? GetDroppedPaths(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop)
        && data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths
            : null;

    // Match the image picker's formats; directories and unrelated files are dropped by
    // the File.Exists check at drop time.
    private static bool IsImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif";

    // ---- Layer list drag-reorder ----

    private void OnLayerListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressLayer = null;
        DependencyObject? hit = ToVisual(e.OriginalSource as DependencyObject);
        if (_layerDrag is not null || IsInsideButton(hit))
        {
            return;
        }

        if (hit is not null
            && ItemsControl.ContainerFromElement(LayerList, hit) is ListBoxItem item
            && item.DataContext is DesignLayerViewModel layer)
        {
            // The pointer is captured here, so the row never sees the press: select it by hand
            // and keep the capture for the drag that may follow.
            _pressLayer = layer;
            _pressPoint = e.GetPosition(LayerList);
            LayerList.SelectedItem = layer;
            Mouse.Capture(LayerList, CaptureMode.Element);
        }
    }

    /// <summary>A right-click picks the row it lands on before the menu opens. The menu has a
    /// single action and it acts on the selection, so pointing at a row and choosing delete
    /// must never land on whichever row happened to be selected before.</summary>
    private void OnLayerListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_layerDrag is not null)
        {
            return;
        }

        DependencyObject? hit = ToVisual(e.OriginalSource as DependencyObject);
        if (hit is not null
            && ItemsControl.ContainerFromElement(LayerList, hit) is ListBoxItem item
            && item.DataContext is DesignLayerViewModel layer)
        {
            LayerList.SelectedItem = layer;
        }
    }

    private void OnLayerListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_layerDrag is not null)
        {
            UpdateLayerDrag(e.GetPosition(LayerList));
            e.Handled = true;
            return;
        }

        if (_pressLayer is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = e.GetPosition(LayerList);
        if (Math.Abs(current.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        BeginLayerDrag(_pressLayer, current);
        e.Handled = true;
    }

    private void OnLayerListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_layerDrag is not null)
        {
            FinishLayerDrag(commit: true);
            e.Handled = true;
        }

        _pressLayer = null;
        if (Mouse.Captured == LayerList)
        {
            Mouse.Capture(null);
        }
    }

    private void OnLayerListLostMouseCapture(object sender, MouseEventArgs e)
    {
        _pressLayer = null;
        FinishLayerDrag(commit: false);
    }

    /// <summary>Lifts the pressed row out of the list: a snapshot of the row rides under the
    /// pointer on the card's own overlay canvas while the row itself turns transparent, leaving
    /// the gap that the neighbouring rows animate into as the pointer crosses each midpoint.</summary>
    private void BeginLayerDrag(DesignLayerViewModel layer, Point pointer)
    {
        if (LayerList.ItemContainerGenerator.ContainerFromItem(layer) is not ListBoxItem container
            || container.ActualWidth <= 0
            || container.ActualHeight <= 0)
        {
            return;
        }

        int from = LayerList.Items.IndexOf(layer);
        if (from < 0)
        {
            return;
        }

        // The list does not virtualise, so every row has a container for the whole drag: the
        // shift animation and the slot calculation both need rows that stay with their item.
        ListBoxItem?[] rows = new ListBoxItem?[LayerList.Items.Count];
        for (int index = 0; index < rows.Length; index++)
        {
            rows[index] = LayerList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
        }

        Point origin = container.TransformToAncestor(LayerList).Transform(new Point(0, 0));
        // Grab is taken from the live pointer rather than the press: the drag only starts after
        // the threshold, and hanging the snapshot off the press would snap it back by that gap.
        Vector grab = pointer - origin;
        ScrollViewer? scroller = FindScrollViewer(LayerList);
        FrameworkElement ghost = CreateGhost(container, container.ActualWidth, container.ActualHeight);

        // The overlay and the list share a cell, but the translation between their coordinate
        // spaces is measured instead of assumed. It is the list's origin that is wanted here:
        // translating the row's own origin would count the row's offset twice, which parks the
        // ghost a full row-distance away from the pointer.
        Vector overlayOffset = (Vector)LayerList.TranslatePoint(new Point(0, 0), DragOverlay);

        DragOverlay.Children.Clear();
        DragOverlay.Children.Add(ghost);

        container.Opacity = 0;
        _layerDrag = new LayerDragSession
        {
            Layer = layer,
            Container = container,
            Ghost = ghost,
            OverlayOffset = overlayOffset,
            Rows = rows,
            From = from,
            RowHeight = container.ActualHeight,
            Grab = grab,
            Scroller = scroller,
            Slot = -1,
        };

        UpdateLayerDrag(pointer);
    }

    private void UpdateLayerDrag(Point pointer)
    {
        if (_layerDrag is not { } drag)
        {
            return;
        }

        Canvas.SetLeft(drag.Ghost, pointer.X + drag.OverlayOffset.X - drag.Grab.X);
        Canvas.SetTop(drag.Ghost, pointer.Y + drag.OverlayOffset.Y - drag.Grab.Y);

        AutoScroll(drag, pointer);

        int slot = SlotAt(drag, pointer.Y);
        if (slot == drag.Slot)
        {
            return;
        }

        drag.Slot = slot;
        ApplyRowShifts(drag);
    }

    /// <summary>The insertion slot under the pointer, read from the rows' own positions every
    /// time. Each row's live position already carries the shift this drag applied to it, so
    /// subtracting that shift gives the row's resting place: the answer cannot drift when the
    /// animation is mid-flight, when the list scrolls under a stationary pointer, or when rows
    /// are recycled. A pointer below every midpoint belongs after the last row.</summary>
    private int SlotAt(LayerDragSession drag, double pointerY)
    {
        int slot = 0;
        for (int index = 0; index < drag.Rows.Length; index++)
        {
            if (drag.Rows[index] is not { } row)
            {
                continue;
            }

            double shift = row.RenderTransform is TranslateTransform transform ? transform.Y : 0;
            double top = row.TransformToAncestor(LayerList).Transform(new Point(0, 0)).Y - shift;
            if (pointerY <= top + row.ActualHeight / 2)
            {
                return slot;
            }

            slot = index + 1;
        }

        return slot;
    }

    /// <summary>Rows between the source and the pointer slide by exactly one row height, so the
    /// gap follows the pointer instead of being marked by a line.</summary>
    private static void ApplyRowShifts(LayerDragSession drag)
    {
        double gap = drag.RowHeight;
        for (int index = 0; index < drag.Rows.Length; index++)
        {
            if (index == drag.From || drag.Rows[index] is not { } row)
            {
                continue;
            }

            double target = 0;
            if (drag.Slot > drag.From && index > drag.From && index < drag.Slot)
            {
                target = -gap;
            }
            else if (drag.Slot < drag.From && index >= drag.Slot && index < drag.From)
            {
                target = gap;
            }

            AnimateRowShift(row, target);
        }
    }

    private static void AnimateRowShift(ListBoxItem row, double target)
    {
        if (row.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            row.RenderTransform = transform;
        }

        double current = transform.Y;
        if (Math.Abs(current - target) < 0.5)
        {
            return;
        }

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(current, target, RowShiftDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>Drops the ghost and commits the move. On commit the shift transforms are
    /// cleared outright: the model move lands the rows exactly where the animation was heading,
    /// so an eased return would fight the real layout.</summary>
    private void FinishLayerDrag(bool commit)
    {
        if (_layerDrag is not { } drag)
        {
            return;
        }

        _layerDrag = null;
        if (Mouse.Captured == LayerList)
        {
            Mouse.Capture(null);
        }

        RetireGhost(drag.Ghost, fade: !commit);
        drag.Container.Opacity = 1;
        ClearRowShifts(drag, animate: !commit);

        if (commit && drag.Slot >= 0)
        {
            _viewModel.MoveLayer(drag.Layer, drag.Slot);
        }
    }

    private static void ClearRowShifts(LayerDragSession drag, bool animate)
    {
        foreach (ListBoxItem? row in drag.Rows)
        {
            if (row?.RenderTransform is not TranslateTransform transform)
            {
                continue;
            }

            if (animate && Math.Abs(transform.Y) > 0.5)
            {
                transform.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(transform.Y, 0, RowShiftDuration)
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.Stop,
                    });
                continue;
            }

            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0;
        }
    }

    /// <summary>Keeps long lists reachable while dragging: the pointer near an edge scrolls the
    /// list a step at a time, and the slot calculation reads the rows' live positions, so the
    /// insertion point follows the scroll for free.</summary>
    private static void AutoScroll(LayerDragSession drag, Point pointer)
    {
        if (drag.Scroller is not { } scroller)
        {
            return;
        }

        const double edge = 24;
        const double step = 18;
        if (pointer.Y < edge)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - step);
        }
        else if (pointer.Y > scroller.ViewportHeight - edge)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + step);
        }
    }

    /// <summary>Snapshot of a realised row, taken at device resolution and stretched back to the
    /// row's layout size. A frozen bitmap is deliberate: the row itself is about to be made
    /// transparent, and a live brush would carry that opacity into the ghost.</summary>
    private static FrameworkElement CreateGhost(ListBoxItem container, double width, double height)
    {
        return new Image
        {
            Source = SnapshotRow(container, width, height),
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black,
            },
        };
    }

    /// <summary>Paints the row into a bitmap through a brush-relative
    /// <see cref="VisualBrush"/>. Rendering the container directly does not work: the visual
    /// carries its own layout offset, so every row below the first lands outside the bitmap and
    /// the ghost comes out empty. A VisualBrush samples the row in its own coordinate space,
    /// which keeps the offset out of the picture.</summary>
    private static RenderTargetBitmap SnapshotRow(ListBoxItem container, double width, double height)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(container);
        DrawingVisual painted = new();
        using (DrawingContext context = painted.RenderOpen())
        {
            VisualBrush brush = new(container)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        RenderTargetBitmap snapshot = new(
            Math.Max(1, (int)Math.Round(width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(height * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        snapshot.Render(painted);
        snapshot.Freeze();
        return snapshot;
    }

    /// <summary>Takes the ghost off the overlay. A cancelled drag fades it out instead of
    /// blinking it away, so the rows sliding back and the card leaving read as one motion.</summary>
    private void RetireGhost(FrameworkElement ghost, bool fade)
    {
        if (!fade)
        {
            DragOverlay.Children.Remove(ghost);
            return;
        }

        DoubleAnimation fadeOut = new(1, 0, RowShiftDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fadeOut.Completed += (_, _) => DragOverlay.Children.Remove(ghost);
        ghost.BeginAnimation(OpacityProperty, fadeOut);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The visibility toggle lives inside the row; pressing it must not arm a
    /// reorder drag, otherwise the row would move as soon as the pointer slides.</summary>
    private static bool IsInsideButton(DependencyObject? source)
    {
        source = ToVisual(source);
        while (source is not null && source is not ListBoxItem)
        {
            if (source is ButtonBase)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>Hit tests report whatever element sits under the pointer, and the layer rows
    /// carry <see cref="System.Windows.Documents.Run"/> instances for the coordinate text. A
    /// content element is not a Visual, so <see cref="VisualTreeHelper"/> throws on it; climb
    /// out through the logical tree until a visual ancestor is reached.</summary>
    private static DependencyObject? ToVisual(DependencyObject? source)
    {
        while (source is not null and not Visual and not Visual3D)
        {
            source = source is FrameworkContentElement content
                ? LogicalTreeHelper.GetParent(content)
                : null;
        }

        return source;
    }

    private sealed class LayerDragSession
    {
        public required DesignLayerViewModel Layer { get; init; }

        public required ListBoxItem Container { get; init; }

        public required FrameworkElement Ghost { get; init; }

        /// <summary>Translation from the list's coordinate space (where the pointer and the row
        /// tops are measured) to the overlay's, which is where the ghost is placed.</summary>
        public required Vector OverlayOffset { get; init; }

        public required ListBoxItem?[] Rows { get; init; }

        public required double RowHeight { get; init; }

        public required int From { get; init; }

        public required Vector Grab { get; init; }

        public required ScrollViewer? Scroller { get; init; }

        public int Slot { get; set; }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void ZoomInClick(object sender, RoutedEventArgs e) => DesignCanvas.ZoomIn();

    private void ZoomOutClick(object sender, RoutedEventArgs e) => DesignCanvas.ZoomOut();

    private void ResetCanvasZoomClick(object sender, RoutedEventArgs e) => DesignCanvas.ResetView();

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
