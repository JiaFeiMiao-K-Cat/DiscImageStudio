using System.IO;
using System.Text.RegularExpressions;

/// <summary>Guards the unified keyboard-focus ring: one FocusRingBrush token in both
/// palettes, one shared FocusRing focus visual, every interactive control family either
/// wearing it or — for fields whose template paints its own glow — explicitly opting out
/// of the system dotted rectangle so keyboard focus never stacks two different rings.</summary>
internal static class FocusRingTests
{
    private static readonly Regex FocusRingSetter = new(
        Regex.Escape("<Setter Property=\"FocusVisualStyle\" Value=\"{StaticResource FocusRing}\" />"),
        RegexOptions.CultureInvariant);

    private static readonly Regex FocusVisualNullSetter = new(
        Regex.Escape("<Setter Property=\"FocusVisualStyle\" Value=\"{x:Null}\" />"),
        RegexOptions.CultureInvariant);

    private static readonly Regex FocusRingAttribute = new(
        Regex.Escape("FocusVisualStyle=\"{StaticResource FocusRing}\""),
        RegexOptions.CultureInvariant);

    private static readonly Regex FocusRingDynamicAttribute = new(
        Regex.Escape("FocusVisualStyle=\"{DynamicResource FocusRing}\""),
        RegexOptions.CultureInvariant);

    private static readonly Regex KeyboardFocusTrigger = new(
        Regex.Escape("<Trigger Property=\"IsKeyboardFocusWithin\" Value=\"True\">") + ".*?" +
        Regex.Escape("</Trigger>"),
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static void Run()
    {
        string appRoot = Path.Combine(FindRepositoryRoot(), "src", "DiscImageStudio.App");
        string controls = File.ReadAllText(Path.Combine(appRoot, "Themes", "Controls.xaml"));
        string tokens = File.ReadAllText(Path.Combine(appRoot, "Themes", "Tokens.xaml"));
        string light = File.ReadAllText(Path.Combine(appRoot, "Themes", "Palette.Light.xaml"));
        string sideNav = File.ReadAllText(Path.Combine(appRoot, "Controls", "SideNavItem.xaml"));
        string stageBar = File.ReadAllText(Path.Combine(appRoot, "Controls", "StageBar.xaml"));
        string titleBar = File.ReadAllText(Path.Combine(appRoot, "Controls", "TitleBar.xaml"));
        string segmented = File.ReadAllText(Path.Combine(appRoot, "Controls", "SegmentedControl.xaml"));
        string projectDialog = File.ReadAllText(Path.Combine(appRoot, "ProjectDialogWindow.xaml"));

        Require(Regex.IsMatch(tokens, "<SolidColorBrush x:Key=\"FocusRingBrush\""), "dark tokens define FocusRingBrush as a brush");
        Require(Regex.IsMatch(light, "<SolidColorBrush x:Key=\"FocusRingBrush\""), "light palette defines FocusRingBrush as a brush");
        Require(controls.Contains("x:Key=\"FocusRing\""), "Controls.xaml defines the shared FocusRing visual");
        Require(controls.Contains("Stroke=\"{DynamicResource FocusRingBrush}\""), "the ring paints with FocusRingBrush");

        Require(FocusRingSetter.Matches(controls).Count >= 8,
            "filled buttons, tiles, bare text box, combo items, check/radio boxes and sliders wear the ring");
        Require(FocusRingAttribute.Matches(controls).Count >= 1,
            "the step-card accordion header wears the ring");
        Require(FocusVisualNullSetter.Matches(controls).Count == 2,
            "exactly the implicit TextBox and ComboBox opt out of the system focus rectangle");

        foreach (Match trigger in KeyboardFocusTrigger.Matches(controls))
        {
            Require(!trigger.Value.Contains("PrimaryGlowBrush"),
                "keyboard-focus triggers use the FocusRingBrush token, not the glow brush");
        }

        Require(FocusRingDynamicAttribute.Matches(sideNav).Count == 1, "sidebar navigation item wears the ring");
        Require(FocusRingDynamicAttribute.Matches(stageBar).Count == 1, "stage rail buttons wear the ring");
        Require(FocusRingDynamicAttribute.Matches(titleBar).Count == 4, "all four title-bar window buttons wear the ring");

        foreach (string key in new[] { "RowListBoxItem", "CardListBoxItem" })
        {
            Match style = Regex.Match(
                controls,
                "<Style x:Key=\"" + key + "\".*?</Style>",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
            Require(style.Success && FocusRingSetter.IsMatch(style.Value),
                "the " + key + " style wears the shared focus ring");
        }

        Match recentCard = Regex.Match(
            controls,
            "<Style TargetType=\"controls:RecentProjectCard\">.*?</Style>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        Require(recentCard.Success && FocusRingSetter.IsMatch(recentCard.Value),
            "recent-project tiles wear the shared focus ring");

        Require(FocusRingSetter.Matches(projectDialog).Count == 2,
            "new-project dialog tabs and template cards wear the shared focus ring");

        Require(Regex.Matches(segmented, Regex.Escape("Value=\"{DynamicResource FocusRingBrush}\"")).Count == 3,
            "the segmented control paints keyboard focus with the shared ring brush");

        Console.WriteLine("focus-ring: passed (token in both palettes, shared visual, ring coverage, field opt-outs)");
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
            "Focus ring check failed: cannot locate DiscImageStudio.slnx above the test output folder.");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Focus ring check failed: " + name);
        }
    }
}
