using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DiscImageStudio.Linux.ViewModels;

namespace DiscImageStudio.Linux.Views;

public partial class DesignView : UserControl
{
    public DesignView()
    {
        InitializeComponent();
        Canvas.PropertyChanged += (_, e) =>
        {
            if (e.Property == Controls.DiscCanvas.ZoomProperty)
            {
                ZoomLabel.Text = $"{Canvas.Zoom * 100:0}%";
            }
        };
        Canvas.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Canvas.AddHandler(DragDrop.DropEvent, OnDrop);
        KeyDown += OnKeyDown;
    }

    private DesignViewModel? Model => DataContext as DesignViewModel;

    private void ZoomIn(object? sender, RoutedEventArgs e) => Canvas.Zoom *= 1.25;

    private void ZoomOut(object? sender, RoutedEventArgs e) => Canvas.Zoom /= 1.25;

    private void ResetZoom(object? sender, RoutedEventArgs e) => Canvas.ResetView();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && e.Source is not TextBox && Model?.RemoveSelectedCommand.CanExecute(null) == true)
        {
            Model.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        IStorageItem[]? items = e.DataTransfer.TryGetFiles();
        if (items is null || Model is null)
        {
            return;
        }

        Model.ImportFiles(items.Select(item => item.TryGetLocalPath()).OfType<string>());
        e.Handled = true;
    }
}
