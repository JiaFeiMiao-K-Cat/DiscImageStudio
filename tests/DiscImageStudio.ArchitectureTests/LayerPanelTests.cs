using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

/// <summary>The layer panel's drag-reorder hands the view model a slot measured in the list's
/// pre-drag order, and project opening hands it decoded images from the background preload.
/// These checks pin both conversions: the drop-below-source slot that lands one row earlier
/// once the source leaves the sequence, and the preloaded decode that must not be re-read.</summary>
internal static class LayerPanelTests
{
    internal static void Run()
    {
        WpfTestHost.Run(CheckSlotsAndPreload);
        Console.WriteLine("layer-panel: drag slots, preloaded decode reuse and selection passed");
    }

    private static void CheckSlotsAndPreload()
    {
        var shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
        DesignViewModel design = shell.Design;
        DesignLayerViewModel a = new() { Name = "A" };
        DesignLayerViewModel b = new() { Name = "B" };
        DesignLayerViewModel c = new() { Name = "C" };
        DesignLayerViewModel d = new() { Name = "D" };
        design.Layers.Add(a);
        design.Layers.Add(b);
        design.Layers.Add(c);
        design.Layers.Add(d);

        // Dropping below the source counts the source row out of the sequence first.
        design.MoveLayer(b, 3);
        Require(design.Layers.Select(layer => layer.Name).SequenceEqual(["A", "C", "B", "D"]),
            "dropping below the source lands after the neighbour");
        Require(ReferenceEquals(design.SelectedLayer, b), "the moved layer stays selected");

        // Slot Count means after the last row regardless of where the source started.
        design.MoveLayer(a, 4);
        Require(design.Layers.Select(layer => layer.Name).SequenceEqual(["C", "B", "D", "A"]),
            "dropping past the last row moves the layer to the end");

        design.MoveLayer(d, 0);
        Require(design.Layers.Select(layer => layer.Name).SequenceEqual(["D", "C", "B", "A"]),
            "dropping before the first row moves the layer to the front");

        // The gap immediately above the source itself is a no-op.
        design.MoveLayer(c, 1);
        Require(design.Layers.Select(layer => layer.Name).SequenceEqual(["D", "C", "B", "A"]),
            "dropping in the source's own gap leaves the order unchanged");

        design.ClearLayers();

        BitmapSource? preloaded = BitmapSource.Create(
            4, 2, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 2 * 4], 4 * 4);
        preloaded.Freeze();
        Dictionary<string, (BitmapSource? Image, BitmapSource? Thumbnail)> map =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["missing-on-disk.png"] = (preloaded, preloaded),
            };
        ProjectLayer layer = new("id", "Preloaded", "missing-on-disk.png", true, 1, 2, 3, 0.5);
        design.LoadLayers([layer], map);
        Require(design.Layers.Count == 1, "a preloaded layer is applied without touching the disk");
        Require(ReferenceEquals(design.Layers[0].Image, preloaded), "the preloaded image is reused as-is");
        Require(ReferenceEquals(design.Layers[0].Thumbnail, preloaded), "the preloaded thumbnail is reused as-is");
        Require(design.Layers[0].BaseWidthMm > 0 && design.Layers[0].BaseHeightMm > 0,
            "the preloaded image still derives its physical size");
        design.ClearLayers();
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{name}: condition was false.");
        }
    }
}
