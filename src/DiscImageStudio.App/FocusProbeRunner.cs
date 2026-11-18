using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiscImageStudio.Controls;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio;

/// <summary>Keyboard audit (`ui-focus-probe`): boots the real shell with a fixture drive,
/// then walks the actual Tab traversal of every workspace with MoveFocus — the same
/// algorithm the Tab key uses — and reports dead ends, focusable-but-never-visited
/// elements, and the Escape routing of the confirm modal. A non-zero exit means the
/// audit found a keyboard dead end, so CI and local runs can gate on it.</summary>
internal static class FocusProbeRunner
{
    private static readonly (Workspace Workspace, string Name)[] Tabs =
    [
        (Workspace.Home, "home"),
        (Workspace.Design, "design"),
        (Workspace.Preview, "preview"),
        (Workspace.Export, "export"),
        (Workspace.Burn, "burn"),
        (Workspace.Calibration, "calibration"),
        (Workspace.CalibrationBurn, "calibration-burn"),
    ];

    private const int MaxWalkSteps = 400;

    internal static int Run(string[] arguments)
    {
        string? output = FindOption(arguments, "output");
        Application application = AppHost.CreateApplication(
            ShutdownMode.OnExplicitShutdown, AppTheme.Dark, L.Chinese);
        MainWindow window = new(new AppSettings(), new AppSettingsStore(),
            new SnapshotOpticalDriveProbe("cd"))
        {
            Width = 1366,
            Height = 768,
            // The app ships maximized; the probe needs the exact client size, so the
            // XAML default is overridden here.
            WindowState = WindowState.Normal,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        WaitForDriveProbe(window, TimeSpan.FromSeconds(10));

        var shell = (ShellViewModel)window.DataContext!;
        var report = new StringBuilder();
        var failures = new List<string>();

        // A long-lived toast with an action keeps the toast surface in every workspace's
        // tree, so the walks prove the dismiss and action buttons are tab-reachable.
        shell.ShowToast(
            L.T("任务完成"), "focus-probe", ToastKind.Success, TimeSpan.FromMinutes(5),
            L.T("Xaml.OpenOutputFolder"), () => { });

        foreach ((Workspace workspace, string name) in Tabs)
        {
            if (workspace == Workspace.CalibrationBurn)
            {
                shell.Calibration.EnsureTarget();
                shell.CalibrationBurn.UpdateSourceSummary();
            }

            shell.SelectWorkspace(workspace);
            PumpDispatcher(TimeSpan.FromMilliseconds(350));
            window.UpdateLayout();

            (List<FrameworkElement> order, List<string> deadEnds) = WalkTabOrder(window);
            var visited = new HashSet<FrameworkElement>(order);
            List<FrameworkElement> unvisited = CollectTabCandidates(window)
                .Where(element => !visited.Contains(element))
                .ToList();

            report.AppendLine($"## {name}: {order.Count} stops, {deadEnds.Count} dead ends, {unvisited.Count} unreachable");
            report.AppendLine("  order: " + string.Join(" -> ", order.Select(Describe)));
            foreach (string deadEnd in deadEnds)
            {
                report.AppendLine("  DEAD END: " + deadEnd);
                failures.Add($"{name}: {deadEnd}");
            }

            foreach (FrameworkElement element in unvisited)
            {
                report.AppendLine("  UNREACHABLE: " + Describe(element));
                failures.Add($"{name}: unreachable focusable {Describe(element)}");
            }

            foreach (string requirement in MissingRequirements(workspace, order))
            {
                report.AppendLine("  MISSING: " + requirement);
                failures.Add($"{name}: {requirement}");
            }
        }

        foreach (string failure in CheckModalKeyboardRouting(window, shell, report))
        {
            failures.Add(failure);
            report.AppendLine("  MODAL: " + failure);
        }

        string headline = failures.Count == 0
            ? "focus-probe: passed (no dead ends, no unreachable focusables, modal keyboard routing OK)"
            : $"focus-probe: FAILED with {failures.Count} issue(s)";
        report.Insert(0, headline + "\n");
        Console.WriteLine(report.ToString());
        if (output is not null)
        {
            string fullPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, report.ToString());
            Console.WriteLine("report: " + fullPath);
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Key surfaces the round's fixes promise, asserted against the walked order
    /// instead of the template text: the toast dismiss and action buttons, the sidebar's
    /// copyable drive values (design page — the home page has no sidebar), and the
    /// calibration expander header whose text lives on the templated Expander, not the
    /// toggle's own Content.</summary>
    private static IEnumerable<string> MissingRequirements(Workspace workspace, List<FrameworkElement> order)
    {
        if (workspace == Workspace.Home)
        {
            if (!order.Any(element => element.Name == "DismissButton"))
            {
                yield return "the toast dismiss button is not in the Tab order";
            }

            if (!order.Any(element => element.Name == "ToastAction"))
            {
                yield return "the toast action button is not in the Tab order";
            }
        }

        if (workspace == Workspace.Design
            && order.Count(element => element.GetType().Name == "CopyableText") == 0)
        {
            yield return "no copyable drive value is in the Tab order";
        }

        if (workspace == Workspace.Calibration
            && !order.Any(element => element is ToggleButton { Name: "PART_Header", TemplatedParent: Expander expander }
                && (expander.Header as string ?? string.Empty).Contains("其他标定参数")))
        {
            yield return "the calibration parameter expander header is not in the Tab order";
        }
    }

    /// <summary>Opens the burn-confirm modal and drives its keyboard surface the way the
    /// window routes keys: focus must start on Cancel, Tab must cycle the modal's own
    /// buttons (wrapping), Enter on Confirm must resolve true, and a fresh modal must
    /// still cancel on Escape.</summary>
    private static IEnumerable<string> CheckModalKeyboardRouting(
        MainWindow window, ShellViewModel shell, StringBuilder report)
    {
        Task<bool> confirmTask = shell.ConfirmAsync(
            L.T("确认测试"), L.T("焦点探针"), L.T("确定"));
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        if (!window.BurnConfirmModal.IsOpen)
        {
            yield return "the confirm modal did not open for the keyboard check";
            yield break;
        }

        ConfirmModal modal = window.BurnConfirmModal;
        if (!ReferenceEquals(Keyboard.FocusedElement, modal.CancelButton))
        {
            yield return "the confirm modal did not take focus on its cancel button";
        }

        SendModalKey(window, Key.Tab);
        PumpDispatcher(TimeSpan.FromMilliseconds(120));
        if (!ReferenceEquals(Keyboard.FocusedElement, modal.ConfirmButton))
        {
            yield return "Tab did not move from the cancel button to the confirm button";
        }

        SendModalKey(window, Key.Tab);
        PumpDispatcher(TimeSpan.FromMilliseconds(120));
        if (!ReferenceEquals(Keyboard.FocusedElement, modal.CancelButton))
        {
            yield return "Tab did not wrap from the confirm button back to the cancel button";
        }

        SendModalKey(window, Key.Tab);
        PumpDispatcher(TimeSpan.FromMilliseconds(120));
        SendModalKey(window, Key.Enter);
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        if (!confirmTask.IsCompleted)
        {
            yield return "Enter on the confirm button did not settle the modal";
            yield break;
        }

        if (!confirmTask.Result)
        {
            yield return "Enter on the confirm button resolved as a refusal";
            yield break;
        }

        // Escape is checked on a fresh modal: the first one is already settled by Enter.
        Task<bool> escapeTask = shell.ConfirmAsync(
            L.T("确认测试"), L.T("焦点探针"), L.T("确定"));
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        SendModalKey(window, Key.Escape);
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        if (!escapeTask.IsCompleted)
        {
            yield return "Escape did not settle the confirm modal";
            yield break;
        }

        if (escapeTask.Result)
        {
            yield return "Escape confirmed the modal instead of cancelling it";
            yield break;
        }

        // The three-way save dialog carries a Discard button between Cancel and Confirm;
        // Tab must walk all three and Escape must resolve null (cancel), not a choice.
        Task<bool?> saveTask = shell.ConfirmUnsavedChangesAsync(L.T("焦点探针"));
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        if (!modal.IsOpen)
        {
            yield return "the three-way save modal did not open for the keyboard check";
            yield break;
        }

        SendModalKey(window, Key.Tab);
        PumpDispatcher(TimeSpan.FromMilliseconds(120));
        if (!ReferenceEquals(Keyboard.FocusedElement, modal.NeutralButton))
        {
            yield return "Tab did not move from the cancel button to the discard button";
        }

        SendModalKey(window, Key.Tab);
        PumpDispatcher(TimeSpan.FromMilliseconds(120));
        if (!ReferenceEquals(Keyboard.FocusedElement, modal.ConfirmButton))
        {
            yield return "Tab did not move from the discard button to the confirm button";
        }

        SendModalKey(window, Key.Escape);
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        if (!saveTask.IsCompleted)
        {
            yield return "Escape did not settle the three-way save modal";
            yield break;
        }

        if (saveTask.Result is not null)
        {
            yield return "Escape did not cancel the three-way save modal";
            yield break;
        }

        report.AppendLine("## modal keyboard: focus starts on Cancel, Tab cycles the modal's"
            + " buttons with wrap-around, Enter confirms, Escape cancels"
            + " (two-button and the three-way save dialog with its discard button)");
    }

    /// Raises one key press through the same route a physical key takes: PreviewKeyDown
    /// tunnels from the window (where the modal routing lives), then an unhandled KeyDown
    /// bubbles from the focused element (which is how buttons receive Enter and Space).
    private static void SendModalKey(Window window, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(window)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
        };
        window.RaiseEvent(preview);
        if (preview.Handled || Keyboard.FocusedElement is not UIElement focused)
        {
            return;
        }

        focused.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = UIElement.KeyDownEvent,
        });
    }

    private static (List<FrameworkElement> Order, List<string> DeadEnds) WalkTabOrder(Window window)
    {
        var order = new List<FrameworkElement>();
        var seen = new HashSet<FrameworkElement>();
        var deadEnds = new List<string>();
        if (!window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
            deadEnds.Add("MoveFocus(First) found no tab stop in the window");
            return (order, deadEnds);
        }

        for (int step = 0; step < MaxWalkSteps; step++)
        {
            if (Keyboard.FocusedElement is not FrameworkElement element)
            {
                deadEnds.Add("keyboard focus left the window on "
                    + Keyboard.FocusedElement?.GetType().Name);
                break;
            }

            if (!seen.Add(element))
            {
                // The traversal wrapped to an already-visited element: the cycle is closed.
                break;
            }

            order.Add(element);
            if (!element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
            {
                deadEnds.Add("MoveFocus(Next) refused at " + Describe(element));
                break;
            }
        }

        return (order, deadEnds);
    }

    /// <summary>Every visible, enabled, tabbable element in the window tree. Popup content
    /// (drop-downs, tooltips) is absent from the tree until opened and therefore never
    /// counted; collapsed expander bodies and the folded dock are excluded by IsVisible.</summary>
    private static List<FrameworkElement> CollectTabCandidates(DependencyObject root)
    {
        var matches = new List<FrameworkElement>();
        Collect(root, matches);
        return matches;

        static void Collect(DependencyObject node, List<FrameworkElement> matches)
        {
            if (node is FrameworkElement element && IsTabCandidate(element))
            {
                matches.Add(element);
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
            {
                Collect(VisualTreeHelper.GetChild(node, index), matches);
            }
        }
    }

    private static bool IsTabCandidate(FrameworkElement element)
    {
        if (element is Window)
        {
            return false;
        }

        if (!element.IsVisible || !element.IsEnabled || !element.Focusable)
        {
            return false;
        }

        // IsTabStop only exists on Control; a focusable plain element (rare) counts too.
        return element is not Control control || control.IsTabStop;
    }

    private static string Describe(FrameworkElement element)
    {
        string label = AutomationProperties.GetName(element);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = element switch
            {
                TextBox textBox => textBox.Text,
                TextBlock block => block.Text,
                ContentControl { Content: string text } => text,
                ContentControl { Content: not null } content => content.Content!.GetType().Name,
                _ => string.Empty,
            };
        }

        string id = element.Name is { Length: > 0 } name ? "+" + name : string.Empty;
        label = label.Length > 36 ? label[..36] + "…" : label;
        return string.IsNullOrWhiteSpace(label) ? element.GetType().Name + id : $"{element.GetType().Name}:{label}{id}";
    }

    private static string? FindOption(string[] arguments, string name)
    {
        for (int index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index] == "--" + name)
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static void WaitForDriveProbe(MainWindow window, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!window.IsDriveProbeReady && stopwatch.Elapsed < timeout)
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
        }

        if (!window.IsDriveProbeReady)
        {
            throw new TimeoutException("Drive probe did not finish within the probe timeout.");
        }
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            DispatcherFrame frame = new();
            DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
