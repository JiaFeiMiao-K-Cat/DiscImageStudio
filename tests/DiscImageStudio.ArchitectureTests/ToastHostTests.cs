using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DiscImageStudio.Controls;
using DiscImageStudio.ViewModels;
using MahApps.Metro.IconPacks;

/// <summary>Covers the toast close path: the dismiss button must be reachable by the same
/// hit testing that real mouse input uses, and clicking it must close the toast. WPF coerces
/// IsHitTestVisible to false for every descendant of a hit-test-invisible element, so a
/// hit-test-invisible host silently disables any interactive content inside it. Also covers
/// the severity icon and the optional action button.</summary>
internal static class ToastHostTests
{
    internal static void Run()
    {
        WpfTestHost.Run(TestDismissButtonReceivesClicks);
        WpfTestHost.Run(TestKindIconReflectsSeverity);
        WpfTestHost.Run(TestActionButtonRunsAndCloses);
        WpfTestHost.Run(TestPlainToastHidesActionButton);
        Console.WriteLine("toast-host: passed (dismiss button hit-testable, click closes toast)");
    }

    private sealed class ToastsHolder
    {
        public ObservableCollection<ToastItem> Toasts { get; } = [];
    }

    private static void TestDismissButtonReceivesClicks()
    {
        ToastHost host = new();
        ToastsHolder holder = new();
        host.DataContext = holder;
        ToastItem toast = new(
            Dispatcher.CurrentDispatcher, "标题", "内容", ToastKind.Info, TimeSpan.FromMinutes(1));
        holder.Toasts.Add(toast);

        host.Measure(new Size(600, 300));
        host.Arrange(new Rect(0, 0, 600, 300));
        host.UpdateLayout();

        Button dismiss = FindDescendant<Button>(host, candidate => candidate.Name == "DismissButton")
            ?? throw new InvalidOperationException("the toast dismiss button was not found");
        // The entrance slide holds the card 24px off its slot until Loaded fires; settle it
        // so the probe runs against the steady state a user actually clicks. The host
        // template also contributes a plain Border, so match the card by its slide transform.
        Border card = FindDescendant<Border>(host, candidate => candidate.RenderTransform is TranslateTransform)
            ?? throw new InvalidOperationException("the toast card was not found");
        ((TranslateTransform)card.RenderTransform).X = 0;
        host.UpdateLayout();

        Point probe = dismiss.TransformToAncestor(host).Transform(
            new Point(dismiss.ActualWidth / 2, dismiss.ActualHeight / 2));
        DependencyObject? hit = InputHitTest(host, probe);
        Require(hit is not null && (ReferenceEquals(hit, dismiss) || IsDescendant(hit, dismiss)),
            $"input hit testing must reach the toast dismiss button (hit={hit?.GetType().Name ?? "null"}, probe={probe})\n"
            + DumpTree(host));

        bool closed = false;
        toast.Closed += (_, _) => closed = true;
        dismiss.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(closed, "clicking the toast dismiss button must close the toast");
    }

    private static void TestKindIconReflectsSeverity()
    {
        ToastHost host = new();
        ToastsHolder holder = new();
        host.DataContext = holder;
        holder.Toasts.Add(new ToastItem(
            Dispatcher.CurrentDispatcher, "完成", "内容", ToastKind.Success, TimeSpan.FromMinutes(1)));
        holder.Toasts.Add(new ToastItem(
            Dispatcher.CurrentDispatcher, "警告", "内容", ToastKind.Warning, TimeSpan.FromMinutes(1)));
        holder.Toasts.Add(new ToastItem(
            Dispatcher.CurrentDispatcher, "错误", "内容", ToastKind.Error, TimeSpan.FromMinutes(1)));
        holder.Toasts.Add(new ToastItem(
            Dispatcher.CurrentDispatcher, "提示", "内容", ToastKind.Info, TimeSpan.FromMinutes(1)));

        host.Measure(new Size(600, 600));
        host.Arrange(new Rect(0, 0, 600, 600));
        host.UpdateLayout();

        var icons = CollectDescendants<PackIconLucide>(host)
            .Where(icon => icon.Name == "KindIcon")
            .Select(icon => icon.Kind)
            .ToArray();
        Require(
            icons.Contains(PackIconLucideKind.CircleCheck)
            && icons.Contains(PackIconLucideKind.TriangleAlert)
            && icons.Contains(PackIconLucideKind.CircleAlert)
            && icons.Contains(PackIconLucideKind.Info),
            "each toast severity renders its own Lucide kind icon, found: " + string.Join(",", icons));
    }

    private static void TestActionButtonRunsAndCloses()
    {
        ToastHost host = new();
        ToastsHolder holder = new();
        host.DataContext = holder;
        bool actionRan = false;
        ToastItem toast = new(
            Dispatcher.CurrentDispatcher, "任务完成", "picture.iso", ToastKind.Success,
            TimeSpan.FromMinutes(1), "打开输出位置", () => actionRan = true);
        holder.Toasts.Add(toast);

        host.Measure(new Size(600, 300));
        host.Arrange(new Rect(0, 0, 600, 300));
        host.UpdateLayout();

        Button action = FindDescendant<Button>(host, candidate => candidate.Name == "ToastAction")
            ?? throw new InvalidOperationException("the toast action button was not found");
        Require(action.Visibility == Visibility.Visible,
            "a toast with an action label shows the action button");
        Require(action.Content as string == "打开输出位置", "the action button carries the follow-up label");

        bool closed = false;
        toast.Closed += (_, _) => closed = true;
        action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(actionRan, "clicking the toast action button runs the follow-up action");
        Require(closed, "running the follow-up action closes the toast");
    }

    private static void TestPlainToastHidesActionButton()
    {
        ToastHost host = new();
        ToastsHolder holder = new();
        host.DataContext = holder;
        holder.Toasts.Add(new ToastItem(
            Dispatcher.CurrentDispatcher, "提示", "内容", ToastKind.Info, TimeSpan.FromMinutes(1)));

        host.Measure(new Size(600, 300));
        host.Arrange(new Rect(0, 0, 600, 300));
        host.UpdateLayout();

        Button action = FindDescendant<Button>(host, candidate => candidate.Name == "ToastAction")
            ?? throw new InvalidOperationException("the toast action button was not found");
        Require(action.Visibility == Visibility.Collapsed,
            "a toast without an action label hides the action button");
    }

    private static System.Collections.Generic.List<T> CollectDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var matches = new System.Collections.Generic.List<T>();
        Collect(root, matches);
        return matches;

        static void Collect(DependencyObject node, System.Collections.Generic.List<T> matches)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, index);
                if (child is T match)
                {
                    matches.Add(match);
                }

                Collect(child, matches);
            }
        }
    }

    /// <summary>Mirrors UIElement.InputHitTest's filter — skip invisible and
    /// hit-test-invisible subtrees — while working on a tree that is not connected to a
    /// presentation source (IsVisible is false for every detached element, so the real
    /// input path cannot be exercised headlessly).</summary>
    private static DependencyObject? InputHitTest(Visual root, Point point)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(root,
            node => node is not UIElement element
                ? HitTestFilterBehavior.Continue
                : element.Visibility == Visibility.Visible && element.IsHitTestVisible
                    ? HitTestFilterBehavior.Continue
                    : HitTestFilterBehavior.ContinueSkipSelfAndChildren,
            result =>
            {
                hit = result.VisualHit;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(point));
        return hit;
    }

    private static bool IsDescendant(object? node, DependencyObject ancestor)
        => node is Visual visual && visual.IsDescendantOf(ancestor);

    private static string DumpTree(DependencyObject node) 
    {
        var sb = new System.Text.StringBuilder();
        Dump(node, 0, sb);
        return sb.ToString();
    }

    private static void Dump(DependencyObject node, int depth, System.Text.StringBuilder sb)
    {
        sb.Append(new string(' ', depth * 2)).Append(node.GetType().Name);
        if (node is FrameworkElement fe)
        {
            sb.Append($" [{fe.ActualWidth:F0}x{fe.ActualHeight:F0}] vis={fe.IsVisible} htv={fe.IsHitTestVisible}");
            if (node is Border border)
            {
                sb.Append($" bg={(border.Background?.GetType().Name ?? "null")}");
            }
        }
        sb.AppendLine();
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < count; index++)
        {
            Dump(VisualTreeHelper.GetChild(node, index), depth + 1, sb);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        => FindDescendant<T>(root, _ => true);

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            T? nested = FindDescendant(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{name}: condition was false.");
        }
    }
}
