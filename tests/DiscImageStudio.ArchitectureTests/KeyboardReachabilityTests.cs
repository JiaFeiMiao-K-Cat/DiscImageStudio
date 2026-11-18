using System;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>Keyboard-reachability audit: interactive chrome — icon buttons, expander
/// headers, copyable values, toast actions — must be tab-reachable and wear the shared
/// focus ring. Container elements (page hosts, cards, popup parts) may stay non-focusable;
/// this guard only forbids the Setter form that styles used to disable focus wholesale,
/// so a regression on IconButton or HeaderActionButton fails here.</summary>
internal static class KeyboardReachabilityTests
{
    public static void Run()
    {
        string appRoot = Path.Combine(FindRepositoryRoot(), "src", "DiscImageStudio.App");
        string controls = File.ReadAllText(Path.Combine(appRoot, "Themes", "Controls.xaml"));
        string copyable = File.ReadAllText(Path.Combine(appRoot, "Controls", "CopyableText.xaml"));
        string copyableCode = File.ReadAllText(Path.Combine(appRoot, "Controls", "CopyableText.xaml.cs"));
        string toastHost = File.ReadAllText(Path.Combine(appRoot, "Controls", "ToastHost.xaml"));
        string shell = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "ShellViewModel.cs"));

        // Container styles (RevealPanel, MonitorCard) may disable focus on themselves;
        // interactive button styles must not.
        foreach (string key in new[] { "IconButton", "HeaderActionButton" })
        {
            Match style = Regex.Match(
                controls,
                "<Style x:Key=\"" + key + "\".*?</Style>",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
            Require(style.Success, "the " + key + " style is still declared");
            Require(!style.Value.Contains("Focusable", StringComparison.Ordinal),
                "the " + key + " style keeps its buttons keyboard-focusable");
        }

        Match header = Regex.Match(
            controls,
            "<ToggleButton x:Name=\"PART_Header\".*?(?=<ContentPresenter)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        Require(header.Success, "the expander template still declares its header toggle");
        Require(!header.Value.Contains("Focusable=\"False\"", StringComparison.Ordinal),
            "the expander header is keyboard-focusable");
        Require(header.Value.Contains("FocusVisualStyle=\"{StaticResource FocusRing}\"", StringComparison.Ordinal),
            "the expander header wears the shared focus ring");

        Require(copyable.Contains("Focusable=\"True\" IsTabStop=\"True\"", StringComparison.Ordinal),
            "copyable values are tab stops");
        Require(copyable.Contains("FocusVisualStyle=\"{StaticResource FocusRing}\"", StringComparison.Ordinal),
            "copyable values wear the shared focus ring");
        Require(copyableCode.Contains("Key.Enter or Key.Space", StringComparison.Ordinal)
                && copyableCode.Contains("KeyDown +=", StringComparison.Ordinal),
            "copyable values copy on Enter and Space");
        Require(copyableCode.Contains("IsKeyboardFocusWithin", StringComparison.Ordinal),
            "copyable values underline on keyboard focus");

        Require(toastHost.Contains("x:Name=\"KindIcon\"", StringComparison.Ordinal),
            "toasts render a severity icon");
        Require(toastHost.Contains("x:Name=\"ToastAction\"", StringComparison.Ordinal)
                && toastHost.Contains("Click=\"ToastAction_Click\"", StringComparison.Ordinal),
            "toasts render the optional action button");
        Require(toastHost.Contains("AutomationProperties.LiveSetting=\"Assertive\"", StringComparison.Ordinal),
            "arriving toasts are announced as an assertive live region");
        Require(toastHost.Contains("ToastOverflowVisibility", StringComparison.Ordinal)
                && toastHost.Contains("ToastOverflowLabel", StringComparison.Ordinal),
            "the toast host shows the backlog footer");

        // The modal used to resolve on every key, which made its buttons unreachable by
        // keyboard: the routing must be limited to Escape (cancel) and Tab (cycle focus).
        string modal = File.ReadAllText(Path.Combine(appRoot, "Controls", "ConfirmModal.xaml.cs"));
        Require(modal.Contains("key == Key.Escape", StringComparison.Ordinal)
                && modal.Contains("key == Key.Tab", StringComparison.Ordinal)
                && modal.Contains("MoveModalFocus", StringComparison.Ordinal),
            "the confirm modal resolves on Escape and cycles its buttons on Tab, not on every key");

        string helpHint = File.ReadAllText(Path.Combine(appRoot, "Controls", "HelpHint.xaml.cs"));
        Require(helpHint.Contains("SetResourceReference(FocusVisualStyleProperty, \"FocusRing\")", StringComparison.Ordinal),
            "help hints wear the shared focus ring");

        string toastItem = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "ToastItem.cs"));
        Require(toastItem.Contains("PauseLifetime", StringComparison.Ordinal)
                && toastItem.Contains("ResumeLifetime", StringComparison.Ordinal)
                && toastItem.Contains("_deadline", StringComparison.Ordinal),
            "toast countdowns pause and resume with the remaining time");
        Require(toastHost.Contains("ToastCard_MouseEnter", StringComparison.Ordinal)
                && toastHost.Contains("ToastCard_GotKeyboardFocus", StringComparison.Ordinal),
            "hovering or focusing a toast pauses its countdown");

        Require(shell.Contains("Xaml.OpenOutputFolder", StringComparison.Ordinal)
                && shell.Contains("ToastKind.Success", StringComparison.Ordinal)
                && shell.Contains("TimeSpan.FromSeconds(8)", StringComparison.Ordinal),
            "completed jobs raise a success toast with the open-output action");
        Require(shell.Contains("ShowLogToast", StringComparison.Ordinal)
                && shell.Contains("Xaml.ViewLog", StringComparison.Ordinal),
            "failure toasts carry the view-log action and unfold the job dock");
        string export = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "ExportViewModel.cs"));
        string burn = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "BurnViewModel.cs"));
        Require(export.Contains("ShowLogToast", StringComparison.Ordinal)
                && burn.Contains("ShowLogToast", StringComparison.Ordinal),
            "export and burn failures raise the log toast");
        string livePreview = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "LivePreviewViewModel.cs"));
        Require(livePreview.Contains("ToastPreviewFailureOnce", StringComparison.Ordinal)
                && livePreview.Contains("PublishPreview", StringComparison.Ordinal),
            "preview failures toast once per distinct message and a success re-arms it");

        Console.WriteLine("keyboard-reachability: passed (icon buttons, expander header, copyable text, toast action)");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiscImageStudio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Keyboard reachability check failed: cannot locate DiscImageStudio.slnx above the test output folder.");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Keyboard reachability check failed: " + name);
        }
    }
}
