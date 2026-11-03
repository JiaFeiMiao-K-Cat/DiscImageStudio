using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DiscImageStudio.Linux.ViewModels;

namespace DiscImageStudio.Linux.Views;

public partial class CalibrateView : UserControl
{
    private static readonly string[] PhotoExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"];

    public CalibrateView()
    {
        InitializeComponent();
        Canvas.BoundaryEdited += (kind, index, x, y) => Model?.EditBoundary(kind, index, x, y);
        Canvas.TraceEdited += (index, x, y, remove) => Model?.EditTrace(index, x, y, remove);
        Canvas.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Canvas.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private CalibrationViewModel? Model => DataContext as CalibrationViewModel;

    private void ZoomIn(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1.25);

    private void ZoomOut(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1 / 1.25);

    private void FitView(object? sender, RoutedEventArgs e) => Canvas.ResetView();

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>A dropped .calibration.json reopens a record; a dropped photo is imported for
    /// the current target.</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Model is not { } model || e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().FirstOrDefault() is not { } path)
        {
            return;
        }

        e.Handled = true;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            await model.LoadSessionAsync(path);
        }
        else if (model.HasTarget && PhotoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            await model.ImportPhotoFromAsync(path);
        }
    }
}
