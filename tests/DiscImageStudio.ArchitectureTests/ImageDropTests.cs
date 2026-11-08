using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class ImageDropTests
{
    internal static void Run()
    {
        WpfTestHost.Run(CheckDrop);
        Console.WriteLine("image-drop: multiple files, filtering, fitting, failure feedback, vanished files and busy guard passed");
    }

    private static void CheckDrop()
    {
        string root = Path.Combine(Path.GetTempPath(), $"image-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
        DesignView view = new(shell.Design);
        try
        {
            string first = Path.Combine(root, "first.PNG");
            string second = Path.Combine(root, "second.png");
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
                4, 2, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)255, 32).ToArray(), 16)));
            using (FileStream output = File.Create(first)) encoder.Save(output);
            File.Copy(first, second);
            string text = Path.Combine(root, "notes.txt");
            File.WriteAllText(text, "not artwork");
            string corrupt = Path.Combine(root, "broken.png");
            File.WriteAllText(corrupt, "not an image");

            DataObject files = new(DataFormats.FileDrop, new[] { first, text, root, second, corrupt });
            DragEventArgs hover = Raise(view, files, DragDrop.PreviewDragEnterEvent);
            Require(hover.Handled && hover.Effects == DragDropEffects.Copy, "image hover offers copy");
            Require(shell.Design.Layers.Count == 0, "hover does not import");
            // Route from a child text field to ensure the page handles it before text editing.
            TextBox child = new();
            view.Content = child;
            DragEventArgs drop = Raise(child, files, DragDrop.PreviewDropEvent);
            Require(drop.Handled && drop.Effects == DragDropEffects.Copy, "child drop reaches page");
            Require(shell.Design.Layers.Select(layer => layer.ImagePath).SequenceEqual([second, first]),
                "only decoded images become layers, in the picker's stacking order");
            Require(shell.Design.SelectedLayer?.ImagePath == first, "first imported image is selected");
            Require(shell.Design.Layers.All(layer => layer.Image is not null
                && layer.BaseWidthMm > 0 && layer.BaseWidthMm == 2 * layer.BaseHeightMm),
                "images keep their aspect ratio and physical fit");
            Require(shell.Toasts.Count == 1 && shell.Toasts[0].Message.Contains(corrupt),
                "corrupt image is reported without losing valid images");

            DesignView snapshot = new(shell.Design);
            Size size = new(1440, 1000);
            snapshot.Measure(size);
            snapshot.Arrange(new Rect(size));
            snapshot.UpdateLayout();
            UiLayoutSnapshot.Save(snapshot, "design-image-drop");

            Require(Raise(view, new DataObject(DataFormats.FileDrop, new[] { text, root }),
                DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None, "unrelated files rejected");
            DragEventArgs textDrop = Raise(view, new DataObject(DataFormats.Text, first),
                DragDrop.PreviewDropEvent);
            Require(textDrop.Handled && textDrop.Effects == DragDropEffects.None,
                "text is not a file drop");
            Require(Raise(view, files, DragDrop.PreviewDropEvent, DragDropEffects.Move).Effects
                == DragDropEffects.None, "move-only source is rejected");

            string vanished = Path.Combine(root, "vanished.png");
            File.Copy(first, vanished);
            DataObject vanishing = new(DataFormats.FileDrop, new[] { vanished });
            Require(Raise(view, vanishing, DragDrop.PreviewDragOverEvent).Effects
                == DragDropEffects.Copy, "hover accepts an image before it disappears");
            File.Delete(vanished);
            Require(Raise(view, vanishing, DragDrop.PreviewDropEvent).Effects
                == DragDropEffects.None, "drop re-checks that the image still exists");
            Require(shell.Design.Layers.Count == 2, "a vanished image adds no layer");

            bool importStateChanged = false;
            shell.Design.ImportImagesCommand.CanExecuteChanged += (_, _) => importStateChanged = true;
            shell.SetBusy(true);
            Require(importStateChanged && !shell.Design.ImportImagesCommand.CanExecute(null),
                "busy disables the import command");
            Require(Raise(view, files, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None,
                "busy hover rejects import");
            Raise(view, files, DragDrop.PreviewDropEvent);
            shell.Design.ImportImages([first]);
            Require(shell.Design.Layers.Count == 2, "busy drop and shared import leave layers unchanged");
        }
        finally
        {
            shell.Design.ClearLayers();
            Directory.Delete(root, recursive: true);
        }
    }

    // WPF creates these arguments internally during an OLE drag session.
    private static DragEventArgs Raise(UIElement target, IDataObject data, RoutedEvent routedEvent,
        DragDropEffects allowed = DragDropEffects.Copy)
    {
        DragEventArgs args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [data, DragDropKeyStates.LeftMouseButton, allowed, target, new Point()], null)!;
        args.RoutedEvent = routedEvent;
        target.RaiseEvent(args);
        return args;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
