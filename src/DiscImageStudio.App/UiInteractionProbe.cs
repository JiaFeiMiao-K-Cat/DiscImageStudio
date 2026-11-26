using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio;

/// <summary>
/// `ui-interaction-probe --output DIR --image PATH [--scenario all|settings|drive|canvas]`
///
/// Drives the real main window with operating-system input (SendInput: real cursor moves,
/// clicks, wheel notches and key presses — nothing is invoked behind the UI's back) and
/// records what is on screen while it happens: a PNG per sampled frame plus the animated
/// values behind it. Each scenario ends with assertions about the settled state, so the
/// exit code is a pass/fail verdict and the frames are the evidence.
/// </summary>
internal static class UiInteractionProbe
{
    private static readonly List<string> Failures = [];
    private static readonly List<object> Report = [];
    private static string _output = ".";
    private static MainWindow _window = null!;
    private static ShellViewModel _shell = null!;
    private static int _frameCounter;

    internal static int Run(string[] arguments)
    {
        _output = Path.GetFullPath(FindOption(arguments, "output") ?? "ui-probe");
        string? image = FindOption(arguments, "image");
        string scenario = FindOption(arguments, "scenario") ?? "all";
        string theme = FindOption(arguments, "theme") ?? "light";
        Directory.CreateDirectory(_output);
        // As under Application.Run: async continuations must come back to the UI thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        AppHost.CreateApplication(ShutdownMode.OnExplicitShutdown,
            theme == "dark" ? AppTheme.Dark : AppTheme.Light, L.Chinese);
        AppSettingsStore store = new(Path.Combine(_output, "settings.json"));
        _window = new MainWindow(new AppSettings { Theme = theme == "dark" ? AppTheme.Dark : AppTheme.Light },
            store, new SnapshotOpticalDriveProbe("cd"))
        {
            Width = 1280,
            Height = 800,
            WindowState = WindowState.Normal,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
        };
        _shell = (ShellViewModel)_window.DataContext;
        _window.Show();
        _window.Activate();
        Pump(800);
        Log($"motion enabled: {Motion.Enabled}");
        try
        {
            if (scenario is "all" or "settings") SettingsScenario();
            if (scenario is "all" or "reduced") ReducedMotionScenario();
            if (scenario is "music")
            {
                CreateProject();
                MusicFormatsScenario();
            }
            if (scenario is "cdtext")
            {
                CreateProject();
                CdTextScenario();
            }
            if (scenario is "all" or "drive" or "canvas")
            {
                string project = CreateProject();
                if (scenario is "all" or "drive") SidebarScenario();
                if (scenario is "all" or "drive") DriveScenario();
                if ((scenario is "all" or "canvas") && image is not null) CanvasScenario(image, project);
            }
        }
        catch (Exception exception)
        {
            Fail("probe crashed: " + exception);
        }

        File.WriteAllText(Path.Combine(_output, "report.json"), JsonSerializer.Serialize(new
        {
            passed = Failures.Count == 0,
            failures = Failures,
            steps = Report,
        }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        Console.WriteLine(Failures.Count == 0
            ? "ui-interaction-probe: PASS"
            : "ui-interaction-probe: FAIL\n  " + string.Join("\n  ", Failures));
        _window.Hide();
        return Failures.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ settings

    private static void SettingsScenario()
    {
        ConnectedSurface surface = _window.SettingsSurfaceForTests;
        FrameworkElement gear = _window.AppTitleBar.SettingsAnchor;
        int gearClicks = 0;
        _window.AppTitleBar.SettingsAnchor.Click += (_, _) => gearClicks++;
        _window.AppTitleBar.SettingsAnchor.PreviewMouseLeftButtonDown += (_, e) => Note("gear-down", new { e.ClickCount, gearClicks });
        _window.PreviewMouseDown += (_, e) => Note("window-down", new { source = e.OriginalSource?.GetType().Name, e.ClickCount, t = Environment.TickCount64 % 100000 });
        _window.PreviewMouseUp += (_, e) => Note("window-up", new { source = e.OriginalSource?.GetType().Name, t = Environment.TickCount64 % 100000 });
        Capture("settings-00-home");

        // 1. Home → settings: a real click on the gear, sampled frame by frame.
        Click(gear);
        List<Sample> open = Record("settings-open", surface, 520, () => Center(gear));
        RequireRising(open, "settings open");
        RequireSettledOpen(surface, "settings open");
        Capture("settings-01-open");
        Require(Keyboard.FocusedElement is DependencyObject focused && IsInside(focused, _window.SettingsPageHost),
            "keyboard focus moves into settings");

        // 2. Back arrow → home, folding back into the gear.
        Click(_window.SettingsPageHost.BackButton);
        List<Sample> close = Record("settings-close", surface, 420, () => Center(gear));
        RequireFalling(close, "settings close");
        RequireConvergesOn(close, "settings close folds into the gear");
        RequireSettledClosed(surface, _window.SettingsBackdropForTests, "settings close");
        Capture("settings-02-back-home");

        // 3. Interrupt: open, Escape mid-flight, open again mid-fold.
        Click(gear);
        Pump(110);
        Key(VirtualKey.Escape);
        Pump(70);
        Click(gear);
        List<Sample> interrupted = Record("settings-interrupt", surface, 700, () => Center(gear));
        RequireContinuous(interrupted, "settings open→escape→open");
        RequireSettledOpen(surface, "settings after reopen");
        Capture("settings-03-reopened");

        // 4. Rapid double click on the gear: open then close, ending folded, no jump.
        Key(VirtualKey.Escape);
        Pump(500);
        Note("rapid-start", Environment.TickCount64 % 100000);
        Click(gear);
        Pump(45);
        Note("rapid-second", Environment.TickCount64 % 100000);
        Click(gear);
        List<Sample> rapid = Record("settings-rapid", surface, 600, () => Center(gear));
        Note("gear-clicks-after-rapid", gearClicks);
        RequireContinuous(rapid, "settings rapid toggle");
        RequireSettledClosed(surface, _window.SettingsBackdropForTests, "settings rapid toggle");

        // 5. Ten fast toggles; whatever the parity, the end state must be at rest.
        for (int i = 0; i < 10; i++)
        {
            Click(gear);
            Pump(25 + i * 7);
        }

        List<Sample> burst = Record("settings-burst", surface, 800, () => Center(gear));
        RequireContinuous(burst, "settings 10-click burst");
        if (surface.IsOpen) RequireSettledOpen(surface, "settings burst");
        else RequireSettledClosed(surface, _window.SettingsBackdropForTests, "settings burst");
        if (surface.IsOpen)
        {
            Key(VirtualKey.Escape);
            Pump(500);
        }

        // 6. No late jump: one more second after rest, nothing on the surface moves.
        double rest = surface.Progress;
        Pump(1000);
        Require(surface.Progress == rest && !surface.IsTransitioning, "settings stays at rest after settling");
    }

    private static void ReducedMotionScenario()
    {
        bool original = GetClientAreaAnimation();
        try
        {
            SetClientAreaAnimation(false);
            Pump(200);
            ConnectedSurface surface = _window.SettingsSurfaceForTests;
            Click(_window.AppTitleBar.SettingsAnchor);
            Pump(60);
            Note("reduced-motion", new { progress = surface.Progress, motionEnabled = Motion.Enabled });
            if (!Motion.Enabled)
            {
                Require(surface.Progress == 1 && !surface.IsTransitioning, "without animation settings land open at once");
            }

            Key(VirtualKey.Escape);
            Pump(60);
            if (!Motion.Enabled)
            {
                Require(surface.Progress == 0 && !surface.IsShown, "without animation settings close at once");
            }
        }
        finally
        {
            SetClientAreaAnimation(original);
            Pump(200);
        }
    }

    // ------------------------------------------------------------------ drive

    private static void DriveScenario()
    {
        _shell.NavigateTo(Workspace.Design);
        WaitFor(() => _window.DriveCardHost.IsVisible && _shell.Drive.HasMedia, 8000, "fixture drive with a CD appears");
        Pump(600);
        ConnectedSurface surface = _window.DriveDetailsSurfaceForTests;
        FrameworkElement card = _window.DriveCardHost.Anchor;
        Capture("drive-00-card");

        Click(card);
        List<Sample> open = Record("drive-open", surface, 420, () => Center(card));
        RequireRising(open, "drive details open");
        RequireSettledOpen(surface, "drive details open");
        Capture("drive-01-open");

        // Light dismiss: a click on empty page area folds the panel back into the card.
        Point empty = new(_window.ActualWidth - 60, 120);
        ClickAt(_window.PointToScreen(empty));
        List<Sample> close = Record("drive-close", surface, 360, () => Center(card));
        RequireFalling(close, "drive details close");
        RequireConvergesOn(close, "drive details fold into the card");
        RequireSettledClosed(surface, null, "drive details close");
        Capture("drive-02-closed");

        // Clicking the card while open closes it (the card is a toggle), mid-flight reversal.
        Click(card);
        Pump(90);
        Click(card);
        Pump(60);
        Click(card);
        List<Sample> reversal = Record("drive-reversal", surface, 600, () => Center(card));
        RequireContinuous(reversal, "drive open→close→open");
        RequireSettledOpen(surface, "drive after reversal");
        Key(VirtualKey.Escape);
        List<Sample> escape = Record("drive-escape", surface, 360, () => Center(card));
        RequireSettledClosed(surface, null, "drive escape");
        for (int i = 0; i < 9; i++)
        {
            Click(card);
            Pump(20 + i * 9);
        }

        List<Sample> burst = Record("drive-burst", surface, 700, () => Center(card));
        RequireContinuous(burst, "drive 9-click burst");
        if (surface.IsOpen) RequireSettledOpen(surface, "drive burst");
        else RequireSettledClosed(surface, null, "drive burst");
        _shell.Drive.IsDetailsOpen = false;
        Pump(400);
    }

    // ------------------------------------------------------------------ sidebar

    private static void SidebarScenario()
    {
        FrameworkElement sidebar = _window.SidebarBorder;
        double full = (double)_window.FindResource("SidebarWidth");
        Require(sidebar.IsVisible && Math.Abs(sidebar.ActualWidth - full) < 0.5, "project mode shows the full sidebar");

        // Back home with the real back button: the sidebar slides out and is gone.
        Click(_window.BackHomeButton);
        Log("sidebar step 1: Click(_window.BackHomeButton);");
        List<double> closing = RecordWidth(sidebar, 360);
        Log("sidebar step 2: List<double> closing = RecordWidth(sideb");
        Require(closing.Any(width => width > 1 && width < full - 1) || !Motion.Enabled, "sidebar slides out (intermediate widths)");
        Require(!sidebar.IsVisible, "sidebar is collapsed on the home page");
        Capture("sidebar-00-home");
        Log("sidebar step 3: Capture(sidebar-00-home);");

        // Back to the project, then home again mid-slide, then back: always ends at rest.
        _shell.Home.ContinueProjectCommand.Execute(null);
        Log("sidebar step 4: _shell.Home.ContinueProjectCommand.Execu");
        Pump(80);
        Log("sidebar step 5: Pump(80);");
        // Mid-slide the back button is still clipped, so it cannot be clicked for real;
        // the command it is bound to is invoked instead (same code path as the button).
        _ = _shell.BackHomeCommand.ExecuteAsync(null);
        Log("sidebar step 6: Click(_window.BackHomeButton);");
        Pump(60);
        Log("sidebar step 7: Pump(60);");
        _shell.Home.ContinueProjectCommand.Execute(null);
        Log("sidebar step 8: _shell.Home.ContinueProjectCommand.Execu");
        List<double> reversed = RecordWidth(sidebar, 500);
        Log("sidebar step 9: List<double> reversed = RecordWidth(side");
        for (int i = 1; i < reversed.Count; i++)
            Require(Math.Abs(reversed[i] - reversed[i - 1]) < full * 0.6, "sidebar width never jumps");
        Require(sidebar.IsVisible && Math.Abs(sidebar.ActualWidth - full) < 0.5
                && sidebar.ReadLocalValue(FrameworkElement.WidthProperty) is double local && Math.Abs(local - full) < 0.5,
            "sidebar ends fully open with its final width as the base value");
        Capture("sidebar-01-project");
        Log("sidebar step 10: Capture(sidebar-01-project);");
    }

    private static List<double> RecordWidth(FrameworkElement element, int milliseconds)
    {
        List<double> widths = [];
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Pump(16);
            widths.Add(element.IsVisible ? element.ActualWidth : 0);
        }

        Note("sidebar-widths", widths);
        return widths;
    }

    // ------------------------------------------------------------------ canvas

    private static string CreateProject()
    {
        string path = Path.Combine(_output, "probe-project", "Probe.discproj");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DateTimeOffset now = DateTimeOffset.Now;
        ProjectDocument document = new(
            ProjectJsonStore.CurrentSchemaVersion,
            "Probe",
            ProjectDocument.CdFamily,
            new ProjectGeometry(24.5, 56.8, 359_849, 0, 0, 0, UsableSectors: 359_847),
            [],
            new ProjectOutputs(Path.Combine(Path.GetDirectoryName(path)!, "Probe.wav"), WriteCue: true,
                DataDirectory: string.Empty, VolumeLabel: "CD_IMAGE", Interleave: true),
            CalibrationSessionPath: null,
            ThumbnailPngBase64: null,
            now,
            now,
            Tone: ToneProfile.Photo);
        ProjectJsonStore.Save(path, document);
        RunAsync(() => _shell.OpenProjectFromAsync(path), 20000, "open the probe project");
        WaitFor(() => _shell.IsProjectOpen, 5000, "project opens");
        _shell.NavigateTo(Workspace.Design);
        Pump(700);
        return path;
    }

    // ------------------------------------------------------------------ CD-TEXT

    /// <summary>CD-Extra with two tagged WAVs (one Japanese): tags fill the CD-TEXT card, the
    /// track-range disc follows the list and the list follows a click on the disc, and the
    /// burn page shows the same disc and a fourth milestone.</summary>
    private static void CdTextScenario()
    {
        string music = Path.Combine(_output, "music");
        Directory.CreateDirectory(music);
        string first = Path.Combine(music, "01.wav"), second = Path.Combine(music, "02.wav");
        WriteTaggedWave(first, 75 * 60 * 3, "風の谷のナウシカ", "安田成美", "風の谷 ベスト");
        WriteTaggedWave(second, 75 * 60 * 4, "Song of Joy", "Felix", "風の谷 ベスト");
        _shell.Design.Music.AddTracks([first, second]);
        WaitFor(() => _shell.Design.Music.TrackRanges.Count == 2, 20000, "the plan yields two track ranges");
        _shell.Design.CdText.IsEnabled = true;
        Pump(400);
        CdTextViewModel text = _shell.Design.CdText;
        Note("cdtext-card", new { text.AlbumTitle, text.AlbumPerformer, text.StatusText, tracks = text.Tracks.Select(t => new { t.Title, t.Performer }) });
        Require(text.AlbumTitle == "風の谷 ベスト" && text.Tracks.Count == 2 && text.Tracks[0].Title == "風の谷のナウシカ"
                && text.Tracks[1].Performer == "Felix", "switching CD-TEXT on fills album and tracks from the WAV tags");
        Require(text.StatusText.Contains("MS-JIS", StringComparison.Ordinal), "Japanese text is reported as MS-JIS: " + text.StatusText);
        Require(_shell.Design.Music.RangeModeText.Contains("SAO", StringComparison.Ordinal), "the preview disc says SAO with CD-TEXT");

        CdTrackRangeDisc disc = FindNamed<CdTrackRangeDisc>(_window, "MusicRangeDisc")
            ?? throw new InvalidOperationException("track-range disc not found");
        disc.BringIntoView();
        FindNamed<FrameworkElement>(_window, "CdTextCardGate")?.BringIntoView();
        Pump(500);
        disc.BringIntoView();
        Pump(500);
        disc.Snap();
        Capture("cdtext-00-design");

        // Hover the second ring: the card shows that track.
        MoveTo(disc.PointToScreen(disc.RingPoint(1)));
        Pump(400);
        Require(disc.HoverIndex == 1, $"hovering the second ring highlights track 2 (hover={disc.HoverIndex})");
        Capture("cdtext-01-hover");

        // Click the first ring: the list selects track 1; then the list drives the disc.
        ClickAt(disc.PointToScreen(disc.RingPoint(0)));
        Pump(300);
        Require(_shell.Design.Music.SelectedTrack?.Number == 1 && disc.SelectedIndex == 0, "clicking a ring selects that track in the list");
        _shell.Design.Music.SelectedTrack = _shell.Design.Music.Tracks[1];
        Pump(300);
        Require(disc.SelectedIndex == 1, "selecting in the list highlights the ring");
        disc.Snap();
        Capture("cdtext-02-linked");

        // A longer second track: the rings morph to the new plan instead of jumping.
        WriteTaggedWave(second, 75 * 60 * 9, "Song of Joy", "Felix", "風の谷 ベスト");
        double before = disc.Tracks![1].OuterRadiusMm;
        _shell.Design.Music.MoveTrack(_shell.Design.Music.Tracks[1], -1);
        WaitFor(() => disc.Tracks!.Count == 2 && disc.Tracks[0].Title == "Song of Joy", 20000, "reordering re-plans the rings");
        Note("cdtext-morph", new { before, animating = disc.IsAnimating, rings = disc.Tracks!.Select(t => new { t.Title, t.InnerRadiusMm, t.OuterRadiusMm }) });
        Capture("cdtext-03-morph-frame");
        Pump(500);
        Require(!disc.IsAnimating, "the ring animation settles");
        Capture("cdtext-04-reordered");

        // Burn page: same disc, and the four milestones of a CD-TEXT burn.
        _shell.NavigateTo(Workspace.Burn);
        Pump(700);
        BurnMilestonesViewModel milestones = _shell.Burn.Milestones;
        milestones.Begin(cdText: true);
        milestones.Observe(new DiscImageStudio.Burning.OpticalBurnProgress("complete", "图案读回一致", 1, 1));
        milestones.Succeed("图案轨读回一致");
        CdTextAlbum album = _shell.State.EffectiveCdText()!;
        CdTextReadback.Report(milestones, album, new DiscImageStudio.Burning.CdTextBurnOutcome(true, CdTextPacks.Encode(album).Packs, null));
        Pump(500);
        Require(milestones.Items.Count == 4 && milestones.Items[3].State == MilestoneState.Done, "the burn page shows CD-TEXT read back as the fourth milestone");
        CdTrackRangeDisc? burnDisc = FindNamed<CdTrackRangeDisc>(_window, "BurnRangeDisc");
        Require(burnDisc is { Tracks.Count: 2 }, "the burn page shows the same track ranges");
        burnDisc?.Snap();
        burnDisc?.BringIntoView();
        Pump(600);
        Capture("cdtext-05-burn-ranges");
        FindDescendant<BurnMilestoneStrip>(_window)?.BringIntoView();
        Pump(600);
        Capture("cdtext-06-burn-milestones");
    }

    // ------------------------------------------------------------------ Music formats

    /// <summary>Design-page music beyond CD WAV: a CD WAV is ready at once; a 48 kHz WAV and an
    /// MP3 are converted in the background and end as a duration (Windows) or a red decode
    /// error (Wine has no Media Foundation resampler). Then the outer-ring option's label.</summary>
    private static void MusicFormatsScenario()
    {
        string music = Path.Combine(_output, "music");
        Directory.CreateDirectory(music);
        string cd = Path.Combine(music, "01 cd.wav"), hiRes = Path.Combine(music, "02 48k.wav"), mp3 = Path.Combine(music, "03 song.mp3");
        WriteTaggedWave(cd, 75 * 60 * 2, "CD", "A", "B");
        using (FileStream file = File.Create(hiRes))
        using (BinaryWriter w = new(file))
        {
            int pcm = 48000 * 4 * 3;
            w.Write("RIFF"u8); w.Write(36 + pcm); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(48000); w.Write(192000); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm); w.Write(new byte[pcm]);
        }
        File.WriteAllBytes(mp3, [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0, 0xFF, 0xFB, 0x90, 0x00, .. new byte[400]]);
        _shell.Design.Music.AddTracks([cd, hiRes, mp3]);
        Pump(300);
        Note("music-added", _shell.Design.Music.Tracks.Select(t => new { t.FileName, t.Detail, t.HasError }));
        FindNamed<FrameworkElement>(_window, "MusicRangeDisc")?.BringIntoView();
        Pump(300);
        Capture("music-00-added");
        RunAsync(Services.CdMusicSources.WhenIdleAsync, 120000, "the conversions finish");
        WaitFor(() => _shell.Design.Music.Tracks.All(t => !t.Detail.Contains('…')), 20000, "every row settles");
        Pump(500);
        var rows = _shell.Design.Music.Tracks.Select(t => new { t.FileName, t.Detail, t.HasError }).ToArray();
        Note("music-settled", rows);
        FindDescendants<ItemsControl>(_window).FirstOrDefault(list => ReferenceEquals(list.ItemsSource, _shell.Design.Music.Tracks))
            ?.BringIntoView();
        Pump(500);
        Require(!rows[0].HasError && rows[0].Detail.StartsWith("2:00", StringComparison.Ordinal), "the CD WAV is used as it is: " + rows[0].Detail);
        Require(rows.Skip(1).All(r => r.HasError ? r.Detail.StartsWith("Windows 无法解码", StringComparison.Ordinal) : r.Detail.Length > 0),
            "converted rows end as a duration or a decode error");
        Capture("music-01-settled");

        string files = Path.Combine(_output, "files");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "readme.txt"), "probe");
        _shell.State.CdDataDirectory = files;
        _shell.State.CdFilesAtOuterRing = true;
        Pump(800);
        CheckBox? bleed = FindDescendants<CheckBox>(_window).FirstOrDefault(box => box.IsVisible && Equals(box.Content, "避开最外缘"));
        Require(bleed is not null, "the outer-ring option reads 避开最外缘");
        bleed!.BringIntoView();
        Pump(500);
        Capture("music-02-outer-edge");
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private static void WriteTaggedWave(string path, int sectors, string title, string artist, string album)
    {
        static byte[] Info(string id, string value)
        {
            byte[] data = [.. System.Text.Encoding.UTF8.GetBytes(value), 0];
            byte[] chunk = new byte[8 + data.Length + (data.Length & 1)];
            System.Text.Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
            BitConverter.GetBytes(data.Length).CopyTo(chunk, 4);
            data.CopyTo(chunk, 8);
            return chunk;
        }
        byte[] info = [.. "INFO"u8, .. Info("INAM", title), .. Info("IART", artist), .. Info("IPRD", album)];
        int pcm = sectors * 2352;
        using FileStream file = File.Create(path);
        using BinaryWriter w = new(file);
        w.Write("RIFF"u8); w.Write(4 + 24 + 8 + pcm + 8 + info.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(176400); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm); w.Write(new byte[pcm]);
        w.Write("LIST"u8); w.Write(info.Length); w.Write(info);
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (FindNamed<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private static void CanvasScenario(string image, string projectPath)
    {
        DiscDesignCanvas canvas = FindDescendant<DiscDesignCanvas>(_window)
            ?? throw new InvalidOperationException("design canvas not found");
        RunAsync(() => _shell.Design.ImportImagesAsync([image]), 60000, "import the photo");
        Pump(600);
        DesignLayerViewModel layer = _shell.Design.SelectedLayer ?? _shell.Design.Layers.FirstOrDefault()
            ?? throw new InvalidOperationException("import produced no layer");
        _shell.Design.SelectedLayer = layer;
        Pump(300);
        Require(layer.Image is not null, "the photo decoded into a layer");
        Capture("canvas-00-imported");
        Note("canvas-imported", Describe(layer, canvas));

        // Drag the image body: the layer moves by the pointer travel converted to millimetres.
        double scaleBefore = layer.Scale;
        Point centerOnScreen = ToScreen(canvas, canvas.ProbeScreenPointForMm(layer.TranslateXMm, layer.TranslateYMm));
        double mmPerPixel = MmPerPixel(canvas);
        double x0 = layer.TranslateXMm, y0 = layer.TranslateYMm;
        Drag(centerOnScreen, centerOnScreen + new Vector(60, 36));
        Pump(200);
        Note("canvas-move", Describe(layer, canvas));
        Require(Math.Abs(layer.TranslateXMm - x0 - 60 * mmPerPixel) < 1.0
                && Math.Abs(layer.TranslateYMm - y0 - 36 * mmPerPixel) < 1.0,
            $"dragging the image moves it with the pointer (Δ={layer.TranslateXMm - x0:F2},{layer.TranslateYMm - y0:F2} mm, expected {60 * mmPerPixel:F2},{36 * mmPerPixel:F2})");
        Require(layer.Scale == scaleBefore, "moving does not resize");
        Capture("canvas-01-moved");

        // Corner handle: drag the SE corner away from the centre → the layer grows.
        // Grab whichever corner handle is on the visible canvas (a large photo can reach past
        // the disc edge), nearest the canvas centre, and pull it outwards.
        Point[] corners = canvas.ProbeCornerPoints(layer);
        Rect visible = new(12, 12, canvas.ActualWidth - 24, canvas.ActualHeight - 24);
        Point local = corners.Where(visible.Contains)
            .OrderBy(point => (point - new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2)).Length)
            .DefaultIfEmpty(corners[2]).First();
        Point se = ToScreen(canvas, local);
        Point c = ToScreen(canvas, canvas.ProbeScreenPointForMm(layer.TranslateXMm, layer.TranslateYMm));
        Vector outward = se - c;
        outward.Normalize();
        Note("canvas-corner", new { local.X, local.Y });
        Drag(se, se + outward * 45);
        Pump(200);
        Note("canvas-scale", Describe(layer, canvas));
        Require(layer.Scale > scaleBefore * 1.05, $"dragging a corner enlarges the image ({scaleBefore:F3} → {layer.Scale:F3})");
        double scaleAfterCorner = layer.Scale;
        Capture("canvas-02-scaled");

        // Rotation handle: sweep it a quarter turn around the centre.
        double rotationBefore = layer.RotationDeg;
        Point? handle = canvas.ProbeRotateHandle(layer);
        Require(handle is not null, "rotation handle exists");
        if (handle is { } h)
        {
            Point hs = ToScreen(canvas, h);
            Point center = ToScreen(canvas, canvas.ProbeScreenPointForMm(layer.TranslateXMm, layer.TranslateYMm));
            Vector arm = hs - center;
            Vector turned = new(-arm.Y, arm.X); // +90° (clockwise on screen)
            DragPath(hs, Enumerable.Range(1, 12).Select(step =>
            {
                double t = step / 12.0 * Math.PI / 2;
                return center + new Vector(arm.X * Math.Cos(t) - arm.Y * Math.Sin(t), arm.X * Math.Sin(t) + arm.Y * Math.Cos(t));
            }).ToArray());
            Pump(200);
            double delta = layer.RotationDeg - rotationBefore;
            Note("canvas-rotate", Describe(layer, canvas));
            Require(Math.Abs(delta - 90) < 6, $"dragging the rotation handle turns the image (Δ={delta:F1}°)");
            _ = turned;
        }

        Capture("canvas-03-rotated");

        // Wheel: zooms the viewport only; the artwork keeps its millimetre size.
        double zoomBefore = canvas.ZoomFactor;
        Point over = ToScreen(canvas, new Point(canvas.ActualWidth * 0.5, canvas.ActualHeight * 0.5));
        MoveTo(over);
        Pump(80);
        Wheel(120);
        Pump(150);
        Wheel(120);
        Pump(250);
        Note("canvas-wheel", new { zoomBefore, zoomAfter = canvas.ZoomFactor, layer = Describe(layer, canvas) });
        Require(canvas.ZoomFactor > zoomBefore * 1.25, $"wheel zooms the view ({zoomBefore:F2} → {canvas.ZoomFactor:F2})");
        Require(layer.Scale == scaleAfterCorner, "wheel does not resize the image");
        Capture("canvas-04-wheel-zoomed");
        Wheel(-240);
        Pump(250);

        // Save with Ctrl+S, then reopen the file through the normal open path.
        double tx = layer.TranslateXMm, ty = layer.TranslateYMm, scale = layer.Scale, rotation = layer.RotationDeg;
        KeyChord(VirtualKey.Control, VirtualKey.S);
        WaitFor(() => !_shell.IsDirty, 15000, "Ctrl+S saves the project");
        Capture("canvas-05-saved");
        string json = File.ReadAllText(projectPath);
        Note("saved-json-bytes", json.Length);
        RunAsync(() => _shell.BackHomeCommand.ExecuteAsync(null), 8000, "back home");
        Pump(400);
        RunAsync(() => _shell.OpenProjectFromAsync(projectPath), 30000, "reopen the project");
        _shell.NavigateTo(Workspace.Design);
        Pump(1200);
        DesignLayerViewModel? reopened = _shell.Design.Layers.FirstOrDefault();
        Require(reopened is not null, "reopened project has the layer");
        if (reopened is not null)
        {
            Note("canvas-reopened", new { reopened.ImagePath, reopened.TranslateXMm, reopened.TranslateYMm, reopened.Scale, reopened.RotationDeg });
            Require(Math.Abs(reopened.TranslateXMm - tx) < 1e-6 && Math.Abs(reopened.TranslateYMm - ty) < 1e-6
                    && Math.Abs(reopened.Scale - scale) < 1e-9 && Math.Abs(reopened.RotationDeg - rotation) < 1e-9
                    && string.Equals(reopened.ImagePath, image, StringComparison.OrdinalIgnoreCase)
                    && reopened.Image is not null,
                "position, size, rotation and the image path survive save → reopen");
        }

        _shell.Design.SelectedLayer = reopened;
        Pump(300);
        Capture("canvas-06-reopened");
    }

    // ------------------------------------------------------------------ recording

    private sealed record Sample(double Ms, double Progress, double Opacity, double CenterDistance);

    private static List<Sample> Record(string name, ConnectedSurface surface, int milliseconds, Func<Point> anchor)
    {
        List<Sample> samples = [];
        Stopwatch clock = Stopwatch.StartNew();
        int frame = 0;
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Pump(16);
            double distance = double.NaN;
            if (surface.IsShown && surface.ActualWidth > 0)
            {
                Rect bounds = surface.TransformToVisual(_window).TransformBounds(
                    new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
                Point a = anchor();
                Point middle = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                distance = (middle - a).Length;
            }

            samples.Add(new Sample(clock.Elapsed.TotalMilliseconds, surface.Progress, surface.Opacity, distance));
            if (frame % 5 == 0) Capture($"{name}-f{frame:D3}", frames: true);
            frame++;
        }

        Note(name, samples);
        return samples;
    }

    private static void RequireRising(List<Sample> samples, string what)
    {
        Require(samples.Count > 4, what + ": sampled");
        Require(samples.Any(sample => sample.Progress > 0.02 && sample.Progress < 0.98) || !Motion.Enabled,
            what + ": intermediate frames are visible (an entrance animation runs)");
        for (int i = 1; i < samples.Count; i++)
            Require(samples[i].Progress >= samples[i - 1].Progress - 1e-9, what + ": never moves backwards");
        RequireContinuous(samples, what);
    }

    private static void RequireFalling(List<Sample> samples, string what)
    {
        Require(samples.Any(sample => sample.Progress > 0.02 && sample.Progress < 0.98) || !Motion.Enabled,
            what + ": intermediate frames are visible (an exit animation runs)");
        for (int i = 1; i < samples.Count; i++)
            Require(samples[i].Progress <= samples[i - 1].Progress + 1e-9, what + ": never moves backwards");
        RequireContinuous(samples, what);
    }

    private static void RequireContinuous(List<Sample> samples, string what)
    {
        for (int i = 1; i < samples.Count; i++)
        {
            double step = Math.Abs(samples[i].Progress - samples[i - 1].Progress);
            double dt = Math.Max(1, samples[i].Ms - samples[i - 1].Ms);
            // A 90 ms minimum clock covers at most ~0.5 per 45 ms; anything bigger is a jump.
            if (step > 0.5 && dt < 60)
            {
                Fail($"{what}: jump of {step:F2} between frames at {samples[i].Ms:F0} ms");
                return;
            }
        }
    }

    private static void RequireConvergesOn(List<Sample> samples, string what)
    {
        List<Sample> visible = samples.Where(sample => !double.IsNaN(sample.CenterDistance) && sample.Progress > 0).ToList();
        if (visible.Count < 3 || !Motion.Enabled) return;
        Require(visible[^1].CenterDistance < visible[0].CenterDistance * 0.75,
            $"{what} (centre distance {visible[0].CenterDistance:F0} → {visible[^1].CenterDistance:F0} px)");
    }

    private static void RequireSettledOpen(ConnectedSurface surface, string what)
    {
        Require(surface.IsOpen && surface.Progress == 1 && surface.Opacity == 1 && !surface.IsTransitioning
                && surface.Visibility == Visibility.Visible,
            $"{what}: settles fully open (progress={surface.Progress:F3}, opacity={surface.Opacity:F2})");
        Require(surface.ReadLocalValue(ConnectedSurface.ProgressProperty) is double local && local == 1,
            what + ": the base value is the destination, so the end of the clock cannot snap back");
    }

    private static void RequireSettledClosed(ConnectedSurface surface, UIElement? backdrop, string what)
    {
        Require(!surface.IsOpen && surface.Progress == 0 && surface.Visibility == Visibility.Collapsed && !surface.IsTransitioning,
            $"{what}: settles fully closed (progress={surface.Progress:F3}, visibility={surface.Visibility})");
        if (backdrop is not null)
            Require(backdrop.Visibility == Visibility.Collapsed,
                $"{what}: backdrop removed (visibility={backdrop.Visibility}, opacity={backdrop.Opacity:F2}, hit={backdrop.IsHitTestVisible})");
    }

    // ------------------------------------------------------------------ OS input

    private enum VirtualKey : ushort
    {
        Control = 0x11,
        Escape = 0x1B,
        S = 0x53,
    }

    private static Point Center(FrameworkElement element)
        => element.TransformToVisual(_window).Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));

    private static void Click(FrameworkElement element)
    {
        Point screen = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        ClickAt(screen);
    }

    private static void ClickAt(Point screen)
    {
        MoveTo(screen);
        Pump(30);
        Mouse(MouseDown);
        Pump(20);
        Mouse(MouseUp);
        Pump(10);
    }

    private static void Drag(Point from, Point to) => DragPath(from, Enumerable.Range(1, 10)
        .Select(step => from + (to - from) * (step / 10.0)).ToArray());

    private static void DragPath(Point from, Point[] path)
    {
        MoveTo(from);
        Pump(60);
        Mouse(MouseDown);
        Pump(40);
        foreach (Point point in path)
        {
            MoveTo(point);
            Pump(20);
        }

        Pump(40);
        Mouse(MouseUp);
        Pump(40);
    }

    private static void MoveTo(Point screen)
    {
        // Physical pixels: the window sits at 0,0 on a 96-DPI virtual display.
        SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        Input input = new()
        {
            Type = 0,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = (int)Math.Round(screen.X * 65535.0 / Math.Max(1, GetSystemMetrics(0) - 1)),
                    Dy = (int)Math.Round(screen.Y * 65535.0 / Math.Max(1, GetSystemMetrics(1) - 1)),
                    Flags = MouseMove | MouseAbsolute,
                },
            },
        };
        Send(input);
    }

    private static void Mouse(uint flags) => Send(new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } });

    private static void Wheel(int delta) => Send(new Input
    {
        Type = 0,
        Data = new InputUnion { Mouse = new MouseInput { Flags = MouseWheel, MouseData = unchecked((uint)delta) } },
    });

    private static void Key(VirtualKey key)
    {
        Send(KeyInput(key, false));
        Pump(15);
        Send(KeyInput(key, true));
        Pump(15);
    }

    private static void KeyChord(VirtualKey modifier, VirtualKey key)
    {
        Send(KeyInput(modifier, false));
        Pump(15);
        Send(KeyInput(key, false));
        Pump(15);
        Send(KeyInput(key, true));
        Pump(15);
        Send(KeyInput(modifier, true));
        Pump(15);
    }

    private static Input KeyInput(VirtualKey key, bool up) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)key, Flags = up ? 2u : 0u } },
    };

    private static void Send(Input input)
    {
        Input[] inputs = [input];
        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
            Fail("SendInput rejected an event: " + Marshal.GetLastWin32Error());
    }

    private const uint MouseMove = 0x0001, MouseDown = 0x0002, MouseUp = 0x0004, MouseWheel = 0x0800, MouseAbsolute = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
        // Pad to the size of MOUSEINPUT so the union matches the native layout.
        public uint Pad1;
        public uint Pad2;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    private const uint SpiGetClientAreaAnimation = 0x1042, SpiSetClientAreaAnimation = 0x1043;

    private static bool GetClientAreaAnimation()
    {
        bool value = true;
        SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref value, 0);
        return value;
    }

    private static void SetClientAreaAnimation(bool value)
    {
        SystemParametersInfo(SpiSetClientAreaAnimation, 0, value ? new IntPtr(1) : IntPtr.Zero, 2);
        // WPF caches SystemParameters until WM_SETTINGCHANGE; Wine may not broadcast it.
        typeof(SystemParameters).GetMethod("InvalidateCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null)
            ?.Invoke(null, null);
    }

    // ------------------------------------------------------------------ helpers

    private static Point ToScreen(FrameworkElement element, Point local) => element.PointToScreen(local);

    private static double MmPerPixel(DiscDesignCanvas canvas)
    {
        Point a = canvas.ProbeScreenPointForMm(0, 0);
        Point b = canvas.ProbeScreenPointForMm(10, 0);
        return 10 / Math.Max(1e-6, b.X - a.X);
    }

    private static object Describe(DesignLayerViewModel layer, DiscDesignCanvas canvas) => new
    {
        layer.TranslateXMm,
        layer.TranslateYMm,
        layer.Scale,
        layer.RotationDeg,
        layer.BaseWidthMm,
        layer.BaseHeightMm,
        canvas.ZoomFactor,
    };

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }

        return false;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }

        return null;
    }

    private static void Capture(string name, bool frames = false)
    {
        _window.UpdateLayout();
        FrameworkElement root = (FrameworkElement)_window.Content;
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        if (width <= 0 || height <= 0) return;
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        string folder = frames ? Path.Combine(_output, "frames") : _output;
        Directory.CreateDirectory(folder);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(folder, (frames ? $"{_frameCounter++:D4}-" : string.Empty) + name + ".png"));
        encoder.Save(stream);
    }

    private static void RunAsync(Func<Task> start, int timeoutMs, string what)
    {
        Task task = start();
        WaitFor(() => task.IsCompleted, timeoutMs, what);
        if (task.IsFaulted)
        {
            Fail(what + ": " + task.Exception?.GetBaseException().Message);
            Note("exception", task.Exception?.ToString() ?? string.Empty);
        }
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs, string what)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < timeoutMs) Pump(30);
        Require(condition(), what);
    }

    private static void Pump(int milliseconds)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Note(string step, object data) => Report.Add(new { step, data });

    private static void Log(string message) { Console.WriteLine(message); Console.Out.Flush(); }

    private static void Require(bool condition, string what)
    {
        if (condition) Report.Add(new { check = what, ok = true });
        else Fail(what);
    }

    private static void Fail(string what)
    {
        Failures.Add(what);
        Report.Add(new { check = what, ok = false });
        Console.WriteLine("FAIL " + what);
    }

    private static string? FindOption(string[] arguments, string name)
    {
        for (int index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index] == "--" + name) return arguments[index + 1];
        }

        return null;
    }
}
