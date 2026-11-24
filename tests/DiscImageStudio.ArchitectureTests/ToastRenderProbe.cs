using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Controls;
using DiscImageStudio.ViewModels;

/// <summary>Renders a representative toast stack to a PNG so the severity icons, the
/// action button and the overflow footer can be inspected visually without launching the
/// full application. Invoked through the --render-toast switch.</summary>
internal static class ToastRenderProbe
{
    internal static void Run(string output)
    {
        WpfTestHost.Run(() => Render(output));
    }

    private static void Render(string output)
    {
        ToastHost host = new();
        StackState state = new();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        state.Toasts.Add(new ToastItem(
            dispatcher, "任务完成", "banner-cd.iso", ToastKind.Success, TimeSpan.FromMinutes(5),
            "打开输出位置", () => { }));
        state.Toasts.Add(new ToastItem(
            dispatcher, "流式刻录完成", "流式刻录已经完成，可以取出光盘。", ToastKind.Success,
            TimeSpan.FromMinutes(5)));
        state.Toasts.Add(new ToastItem(
            dispatcher, "缓存位置不可用", "已改用默认缓存目录。", ToastKind.Warning, TimeSpan.FromMinutes(5)));
        state.Toasts.Add(new ToastItem(
            dispatcher, "无法保存工程", "磁盘已满或被其他程序占用，请关闭占用程序后重试。", ToastKind.Error,
            TimeSpan.FromMinutes(5)));
        // Plain CLR properties bind once at DataContext assignment, so the overflow state
        // must be populated before the host sees its DataContext.
        state.ToastOverflowLabel = "还有 3 条通知";
        state.ToastOverflowVisibility = Visibility.Visible;
        host.DataContext = state;

        host.Measure(new Size(360, 560));
        host.Arrange(new Rect(0, 0, 360, 560));
        host.UpdateLayout();
        // The entrance slide holds every card 24px off its slot until Loaded fires; settle
        // them so the render shows the steady state a user actually sees.
        foreach (Border card in CollectBorders(host))
        {
            if (card.RenderTransform is TranslateTransform slide)
            {
                slide.X = 0;
            }
        }

        host.UpdateLayout();

        RenderTargetBitmap bitmap = new(
            (int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96,
            PixelFormats.Pbgra32);
        bitmap.Render(host);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(directory);
        using FileStream stream = File.Create(output);
        encoder.Save(stream);
        Console.WriteLine("toast render: " + Path.GetFullPath(output));
    }

    private static System.Collections.Generic.List<T> CollectDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var matches = new System.Collections.Generic.List<T>();
        Collect(root, matches);
        return matches;

        static void Collect(DependencyObject node, System.Collections.Generic.List<T> matches)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(node, index);
                if (child is T match)
                {
                    matches.Add(match);
                }

                Collect(child, matches);
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static System.Collections.Generic.List<Border> CollectBorders(DependencyObject root)
    {
        var borders = new System.Collections.Generic.List<Border>();
        Collect(root, borders);
        return borders;

        static void Collect(DependencyObject node, System.Collections.Generic.List<Border> borders)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(node, index);
                if (child is Border border)
                {
                    borders.Add(border);
                }

                Collect(child, borders);
            }
        }
    }

    /// <summary>Mirrors the shell's toast surface: the same property names the host binds
    /// against, so the probe exercises the real binding paths.</summary>
    private sealed class StackState
    {
        public ObservableCollection<ToastItem> Toasts { get; } = [];

        public string? ToastOverflowLabel { get; set; }

        public Visibility ToastOverflowVisibility { get; set; }
    }
}
