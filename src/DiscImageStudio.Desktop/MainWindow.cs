using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.Desktop;

public sealed partial class MainWindow : Window
{
    internal readonly ProjectSession Session = new();
    private readonly ContentControl _page = new();
    private readonly TextBlock _projectTitle = Text("Disc Image Studio", 14, true);
    private readonly TextBlock _status = Text("就绪", 12);
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, Height = 105, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 3, IsVisible = false };
    private readonly Button _cancel = new() { Content = L.T("取消"), IsVisible = false };
    private readonly StackPanel _navigation = new() { Spacing = 5, Margin = new Thickness(14, 20) };
    private readonly List<Button> _navButtons = [];
    private readonly List<DiscCanvas> _canvases = [];
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _closingApproved;
    private string _workspace = "主页";
    private string? _previewPath;
    private ProjectDocument? _previousDocument;
    private string? _lastArtifact;
    private int _selectedLayer = -1;
    private ListBox? _layerList;
    private ContentControl? _layerEditor;
    private Action<ProjectLayer>? _syncLayerFields;
    private DiscCanvas? _designCanvas;
    private TextBox? _artifactInput;
    private List<string> _recent = [];
    private static string SettingsPath => Path.Combine(ProjectSession.ApplicationDirectory, "desktop-settings.json");
    private DesktopSettings _settings = new();

    public MainWindow()
    {
        LoadSettings();
        string[] args = Environment.GetCommandLineArgs();
        int snapshot = Array.IndexOf(args, "--ui-snapshot");
        if (snapshot >= 0)
        {
            _recent = [];
            _settings.Dark = args.Contains("--snapshot-dark");
        }
        L.Initialize(_settings.Language);
        _status.Text = L.T("就绪"); _cancel.Content = L.T("取消");
        if (Application.Current is { } app) app.RequestedThemeVariant = _settings.Dark
            ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        Title = "Disc Image Studio · macOS";
        Width = snapshot >= 0 && args.Contains("--snapshot-narrow") ? 960 : 1280; Height = 850; MinWidth = 900; MinHeight = 650;
        FontFamily = new FontFamily("avares://DiscImageStudio.Desktop/Assets/Fonts#HarmonyOS Sans SC");
        Background = Surface("#F5F5F7", "#101318");
        Foreground = Surface("#18181B", "#ECEEF4");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*,Auto"), Margin = new Thickness(22, 12), ColumnSpacing = 18 };
        var brand = Row(new DiscMark { Width = 34, Height = 34 }, Column(Text("Disc Image", 14, true), Muted("STUDIO", 9)));
        brand.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(brand);
        _projectTitle.VerticalAlignment = VerticalAlignment.Center;
        _projectTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _projectTitle.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(_projectTitle, 1); toolbar.Children.Add(_projectTitle);
        var more = IconButton("more", "更多操作", () => { });
        var saveAs = new MenuItem { Header = L.T("另存为") };
        saveAs.Click += async (_, _) => await Guard(() => SaveProject(true));
        var projectSettings = new MenuItem { Header = L.T("工程设置") };
        projectSettings.Click += async (_, _) => await Guard(EditGeometry);
        more.Flyout = new MenuFlyout { ItemsSource = new[] { saveAs, projectSettings } };
        var actions = Row(Quiet(Button("新建工程", () => NewProject())), Quiet(Button("打开工程", OpenProject)),
            Button("保存", () => SaveProject()), more, IconButton("settings", "设置", ShowSettings));
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actions, 2); toolbar.Children.Add(actions);
        root.Children.Add(new Border { Child = toolbar, BorderBrush = Surface("#E6E9F0", "#2B3444"), BorderThickness = new Thickness(0, 0, 0, 1) });
        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("204,*") };
        Grid.SetRow(middle, 1);
        _navigation.Children.Add(new Border { Padding = new Thickness(14, 0, 0, 12), Child = Muted("工作空间", 11) });
        foreach (var (label, symbol) in new[] { ("主页", "home"), ("设计", "design"), ("预览", "preview"), ("导出", "export"), ("刻录", "burn"), ("标定", "calibrate") })
        {
            var item = Quiet(Button(label, () => Navigate(label)));
            item.Content = Row(Glyph(symbol, 18), Text(label, 13));
            item.Classes.Add("nav");
            _navigation.Children.Add(item); _navButtons.Add(item);
        }
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        sidebar.Children.Add(_navigation);
        var sidebarFooter = new Border { Margin = new Thickness(28, 20), Child = Muted("macOS 桌面版", 11) };
        Grid.SetRow(sidebarFooter, 1); sidebar.Children.Add(sidebarFooter);
        middle.Children.Add(new Border { Background = Surface("#F0F2F7", "#171D29"), Child = sidebar,
            BorderBrush = Surface("#E6E9F0", "#2B3444"), BorderThickness = new Thickness(0, 0, 1, 0) });
        Grid.SetColumn(_page, 1); middle.Children.Add(_page); root.Children.Add(middle);
        var dock = new StackPanel { Spacing = 8, Margin = new Thickness(22, 5) };
        var statusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var status = Row(Glyph("check", 14), _status); status.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(status);
        var logToggle = Quiet(Button("任务日志", () =>
        {
            _log.IsVisible = !_log.IsVisible;
            if (_log.IsVisible) _log.CaretIndex = _log.Text?.Length ?? 0;
        }));
        var dockActions = Row(_cancel, logToggle);
        Grid.SetColumn(dockActions, 1); statusRow.Children.Add(dockActions);
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _log.IsVisible = false;
        dock.Children.Add(statusRow); dock.Children.Add(_progress); dock.Children.Add(_log);
        var dockBorder = new Border { Child = dock, Background = Surface("#FFFFFF", "#1C212B"),
            BorderBrush = Surface("#E6E9F0", "#2B3444"), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(dockBorder, 2); root.Children.Add(dockBorder);
        Content = root;
        Session.Changed += () =>
        {
            var d = Session.Document;
            if (_previousDocument is { } previous && d is not null
                && (previous.Geometry != d.Geometry || previous.Tone != d.Tone || previous.BluRay != d.BluRay
                    || !previous.Layers.SequenceEqual(d.Layers) || previous.Outputs.DataDirectory != d.Outputs.DataDirectory
                    || previous.Outputs.FilesAtOuterRing != d.Outputs.FilesAtOuterRing || previous.Outputs.VolumeLabel != d.Outputs.VolumeLabel
                    || previous.Outputs.Interleave != d.Outputs.Interleave)) _previewPath = null;
            _previousDocument = d;
            _projectTitle.Text = d is not null ? $"{d.Name}{(Session.IsDirty ? " •" : "")}  /  {d.FamilyLabel}" : "Disc Image Studio";
        };
        KeyDown += async (_, e) =>
        {
            if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0) return;
            if (e.Key == Key.S) { await Guard(() => SaveProject()); e.Handled = true; }
            if (e.Key == Key.O) { await Guard(OpenProject); e.Handled = true; }
            if (e.Key == Key.N) { await Guard(() => NewProject()); e.Handled = true; }
        };
        Closing += OnClosing;
        Closed += (_, _) => { Session.CleanCache(); DisposeCanvases(); _photoCanvas?.Dispose(); };
        Navigate("主页");
        // Deterministic snapshots are an explicit developer entry point, never a normal startup side effect.
        if (snapshot >= 0 && snapshot + 1 < args.Length)
            Opened += async (_, _) => await CaptureSnapshots(args[snapshot + 1]);
    }

    internal static TextBlock Text(string value, double size = 14, bool bold = false) => new()
    { Text = L.T(value), FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    internal Button Button(string title, Action action) => Button(title, () => { action(); return Task.CompletedTask; });
    internal Button Button(string title, Func<Task> action)
    {
        var button = new Button();
        button.Classes.Add("action");
        bool primary = title.StartsWith("下一步", StringComparison.Ordinal) || title is "生成文件" or "渲染预览" or "创建工程";
        if (primary) button.Classes.Add("primary");
        string? symbol = title switch
        {
            "新建工程" or "创建工程" => "plus", "打开工程" => "open", "保存" or "另存为" => "save",
            "导入图片" or "导入照片" => "image", "删除" => "trash", "↑" => "up", "↓" => "down",
            "工程设置" => "geometry", "刷新设备" => "refresh", "任务日志" => "log",
            "生成文件" or "导出预览" => "export", "渲染预览" => "preview",
            _ when title.StartsWith("下一步", StringComparison.Ordinal) => "arrow", _ => null
        };
        bool iconOnly = title is "↑" or "↓";
        if (symbol is null) button.Content = L.T(title);
        else
        {
            var icon = Glyph(symbol, 16); if (primary) icon.Ink = Brushes.White;
            button.Content = iconOnly ? icon : Row(icon, new TextBlock { Text = L.T(title), VerticalAlignment = VerticalAlignment.Center });
            if (iconOnly) ToolTip.SetTip(button, title == "↑" ? "上移图层" : "下移图层");
        }
        AutomationProperties.SetName(button, L.T(title));
        button.Click += async (_, _) => await Guard(action);
        return button;
    }
    internal static StackPanel Row(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    internal static StackPanel Column(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    private static IBrush Surface(string light, string dark) => Brush.Parse(Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? dark : light);
    internal static Border Card(string title, Control content) => new()
    {
        Background = Surface("#FFFFFF", "#1C212B"), BorderBrush = Surface("#E6EAF2", "#343B49"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = Column(Text(title, 14, true), content)
    };
    internal NumericUpDown Number(string title, double value, double minimum, double maximum, Action<double> changed,
        StackPanel parent, double increment = 0.1)
    {
        var field = new NumericUpDown { Value = (decimal)value, Minimum = (decimal)minimum, Maximum = (decimal)maximum,
            Increment = (decimal)increment, FormatString = "0.##########", HorizontalAlignment = HorizontalAlignment.Stretch };
        field.ValueChanged += (_, _) => { if (field.Value is { } number) changed((double)number); };
        parent.Children.Add(Column(Text(title, 12), field));
        return field;
    }
    internal CheckBox Check(string title, bool value, Action<bool> changed)
    {
        var field = new CheckBox { Content = L.T(title), IsChecked = value };
        field.IsCheckedChanged += (_, _) => changed(field.IsChecked == true);
        return field;
    }
    private void DisposeCanvases()
    {
        foreach (var canvas in _canvases) canvas.Dispose();
        _canvases.Clear(); _designCanvas = null;
        _syncLayerFields = null;
    }
    internal async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { Log(exception.ToString()); _status.Text = exception.Message; await Message("操作未完成", exception.Message); }
    }
    internal async Task Message(string title, string message)
    {
        var dialog = Dialog(title, 500);
        dialog.Content = Column(Text(message), Button("关闭", dialog.Close));
        await dialog.ShowDialog(this);
    }
    internal Window Dialog(string title, double width = 560) => new()
    {
        Title = L.T(title), Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 760,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Padding = new Thickness(24), CanResize = false,
        FontFamily = FontFamily
    };
    internal async Task<bool> Confirm(string title, string message)
    {
        var dialog = Dialog(title);
        dialog.Content = Column(Text(message), Row(Button("取消", () => dialog.Close(false)), Button("继续", () => dialog.Close(true))));
        return await dialog.ShowDialog<bool>(this);
    }
    internal void Log(string message)
    {
        void Append()
        {
            string oldText = _log.Text ?? "";
            bool follow = _log.CaretIndex == oldText.Length;
            string newText = oldText + message + "\n";
            _log.Text = newText.Length > 60000 ? newText[^40000..] : newText;
            if (follow) _log.CaretIndex = _log.Text.Length;
        }
        if (Dispatcher.UIThread.CheckAccess()) Append(); else Dispatcher.UIThread.Post(Append);
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingApproved) return;
        if (_busy) { e.Cancel = true; _status.Text = L.T("请等待当前任务完成。"); return; }
        if (!Session.IsDirty) return;
        e.Cancel = true;
        try { if (await CanReplaceProject()) { _closingApproved = true; Close(); } }
        catch (Exception exception) { _status.Text = exception.Message; Log(exception.ToString()); }
    }
    private async Task<bool> CanReplaceProject()
    {
        if (!Session.IsDirty) return true;
        var dialog = Dialog("保存工程");
        dialog.Content = Column(Text("当前工程尚未保存。"), Row(Button("取消", () => dialog.Close(0)),
            Button("不保存", () => dialog.Close(1)), Button("保存", () => dialog.Close(2))));
        int result = await dialog.ShowDialog<int>(this);
        return result == 1 || result == 2 && await SaveProject();
    }
    internal void Navigate(string page)
    {
        if (_busy) return;
        if (page != "主页" && Session.Document is null) { _status.Text = L.T("请先新建或打开工程。"); return; }
        DisposeCanvases(); _photoCanvas?.Dispose(); _photoCanvas = null; _workspace = page;
        for (int i = 0; i < _navButtons.Count; i++)
        {
            bool selected = new[] { "主页", "设计", "预览", "导出", "刻录", "标定" }[i] == page;
            _navButtons[i].Classes.Set("selected", selected);
            if (_navButtons[i].Content is StackPanel row && row.Children[0] is StudioIcon icon)
            {
                icon.Ink = selected ? Surface("#4269CC", "#A8BEFF") : Surface("#7B859A", "#AAB5CD");
                icon.InvalidateVisual();
                if (row.Children[1] is TextBlock label) label.Foreground = selected ? Surface("#365BC5", "#C3D2FF") : Surface("#59647A", "#D8DFEF");
            }
        }
        var body = page switch { "主页" => Home(), "设计" => Design(), "预览" => Preview(), "导出" => Export(), "刻录" => Burn(), _ => Calibration() };
        var content = Column(Muted(page == "主页" ? "DISC IMAGE STUDIO" : "工作空间  /  " + L.T(page), 10),
            Text(page == "主页" ? "把图像刻进光盘" : page, 27, true),
            Muted(page == "主页" ? "从一张图片，开始一张独一无二的光盘。" : $"{Session.Document!.FamilyLabel}  ·  {Session.Document.Geometry.InnerRadiusMm:G8}–{Session.Document.Geometry.OuterRadiusMm:G8} mm  ·  {Session.Document.Geometry.Sectors:N0} sectors", 12));
        content.Spacing = 10;
        body.Margin = new Thickness(0, 16, 0, 0);
        content.Children.Add(body);
        // Include the padding in the scrollable extent so the last controls clear the job dock.
        var scroll = new ScrollViewer { Content = new Border { Child = content, Padding = new Thickness(28, 26) } };
        scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (scroll.Extent.Height <= scroll.Viewport.Height || e.Source is not Visual source) return;
            // Keep Shift-scroll available over the calibration photo, where the wheel normally zooms.
            bool photoScroll = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                && source.FindAncestorOfType<CalibrationCanvas>(includeSelf: true) is not null;
            bool focusedSpinner = e.KeyModifiers != KeyModifiers.Shift
                && source.FindAncestorOfType<NumericUpDown>(includeSelf: true) is { IsKeyboardFocusWithin: true };
            if (!photoScroll && !focusedSpinner) return;
            double delta = photoScroll && e.Delta.Y == 0 ? e.Delta.X : e.Delta.Y;
            if (delta == 0) return;
            scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y - delta * 50);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _page.Content = scroll;
    }
    private Control Home()
    {
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        foreach (var (family, title, subtitle, index) in new[] { ("cd", "CD", "WAV + CUE / RAW / ISO", 0), ("dvd", "DVD", "ISO · 4.7 GB", 1), ("bd", "Blu-ray", "BD-R · 25 GB", 2) })
        {
            var create = Button("新建工程", () => NewProject(family));
            create.HorizontalAlignment = HorizontalAlignment.Stretch;
            create.HorizontalContentAlignment = HorizontalAlignment.Center;
            var iconPlate = new Border { Background = Surface("#EEF2FF", "#273650"), CornerRadius = new CornerRadius(12),
                Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new StudioIcon { Symbol = "disc", Width = 26, Height = 26, Ink = Surface("#5B78D7", "#A8BEFF"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var cardContent = Column(iconPlate, Text(title, 21, true), Muted(subtitle, 12), create);
            cardContent.Spacing = 18;
            var card = new Border { Child = cardContent, Background = Surface("#FFFFFF", "#1C212B"), Padding = new Thickness(24),
                BorderBrush = Surface("#E6EAF2", "#343B49"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16) };
            card.Margin = new Thickness(0, 0, index < 2 ? 16 : 0, 0); Grid.SetColumn(card, index); cards.Children.Add(card);
        }
        var recent = Column();
        foreach (string path in _recent.Where(File.Exists).Take(8))
            recent.Children.Add(Button(Path.GetFileNameWithoutExtension(path), () => OpenProjectPath(path)));
        if (recent.Children.Count == 0)
        {
            var empty = Row(Glyph("open", 22), Column(Text("还没有最近工程。", 13), Muted("创建一个新工程，或打开已有的 .discproj 文件。", 12)));
            empty.Margin = new Thickness(4, 16); recent.Children.Add(empty);
        }
        var home = Column(cards, Card("最近工程", recent)); home.Spacing = 22; return home;
    }
    internal async Task NewProject(string family = "cd")
    {
        if (_busy || !await CanReplaceProject()) return;
        var presets = DiscPresetJsonStore.Merge(DiscPresetJsonStore.LoadBuiltIn(), DiscPresetJsonStore.LoadOrCreate(DiscPresetJsonStore.GetDefaultPath(), DiscPresetJsonStore.CreateInitialUserDocument()));
        var choices = new List<(string Name, ProjectGeometry Geometry, bool Invert)>();
        if (family == "cd") choices.AddRange(presets.CdPresets.Select(p => (p.DisplayName, new ProjectGeometry(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors), false)));
        else if (family == "dvd") choices.AddRange(presets.DvdPresets.Select(p => (p.DisplayName, new ProjectGeometry(p.InnerRadiusMm, p.OuterRadiusMm, p.TotalSectors, p.PitchLinear, p.PitchQuadratic, p.PitchCubic), false)));
        else choices.AddRange((presets.BluRayPresets ?? []).Select(p => (p.DisplayName, new ProjectGeometry(p.InnerRadiusMm, p.OuterRadiusMm, p.TotalSectors), p.InvertMapping)));
        var dialog = Dialog("新建工程");
        var name = new TextBox { Text = "Untitled", Watermark = L.T("工程名称") };
        var preset = new ComboBox { ItemsSource = choices.Select(p => p.Name).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        dialog.Content = Column(Text(family.ToUpperInvariant(), 24, true), Text("工程名称"), name, Text("盘片预设"), preset,
            Row(Button("取消", () => dialog.Close(false)), Button("创建工程", () => dialog.Close(true))));
        if (!await dialog.ShowDialog<bool>(this)) return;
        if (preset.SelectedIndex < 0) throw new InvalidOperationException("没有可用的盘片预设。");
        var chosen = choices[preset.SelectedIndex];
        Session.Create(family, name.Text ?? "Untitled", chosen.Geometry, chosen.Invert);
        _selectedLayer = -1; _lastArtifact = null; _previewPath = null; ResetCalibration(); Navigate("设计");
    }
    private async Task OpenProject()
    {
        string? path = await PickOpen("打开工程", "*.discproj");
        if (path is not null) await OpenProjectPath(path);
    }
    private async Task OpenProjectPath(string path)
    {
        if (_busy || !await CanReplaceProject()) return;
        Session.Open(path); Remember(path); ResetCalibration(); _lastArtifact = null; _previewPath = null;
        _selectedLayer = Session.Document!.Layers.Count > 0 ? 0 : -1; Navigate("设计");
    }
    private async Task<bool> SaveProject(bool saveAs = false)
    {
        if (_busy || Session.Document is null) return false;
        string? path = (saveAs ? null : Session.ProjectPath) ?? await PickSave("保存工程", Session.Document.Name + ".discproj", "*.discproj");
        if (path is null) return false;
        Session.Save(path); Remember(path); _status.Text = L.T("工程已保存"); return true;
    }
    internal async Task<string?> PickOpen(string title, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T(title), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.T(title)) { Patterns = patterns }, FilePickerFileTypes.All] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    internal async Task<string?> PickSave(string title, string name, params string[] patterns)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = L.T(title), SuggestedFileName = name,
            ShowOverwritePrompt = true, FileTypeChoices = [new FilePickerFileType(L.T(title)) { Patterns = patterns }] });
        return file?.TryGetLocalPath();
    }
    private async Task<string?> PickFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("选择文件夹"), AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
    private void Remember(string path) { _recent.Remove(path); _recent.Insert(0, path); _recent = _recent.Take(12).ToList(); SaveSettings(); }
    private void LoadSettings()
    {
        try { if (File.Exists(SettingsPath)) _settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        _recent = _settings.Recent ?? [];
    }
    private void SaveSettings()
    {
        Directory.CreateDirectory(ProjectSession.ApplicationDirectory);
        _settings.Recent = _recent;
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(_settings)); File.Move(temporary, SettingsPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task ShowSettings()
    {
        var dialog = Dialog("设置");
        string[] codes = ["system", "zh-CN", "zh-TW", "en", "ja"];
        var language = new ComboBox { ItemsSource = new[] { "System", "简体中文", "繁體中文", "English", "日本語" }, SelectedIndex = Math.Max(0, Array.IndexOf(codes, _settings.Language)) };
        var dark = Check("深色主题", _settings.Dark, _ => { });
        dialog.Content = Column(Text("语言"), language, Text("语言更改在重启后生效。", 12), dark,
            Text("Disc Image Studio · GPL-3.0-or-later", 12), Row(Button("取消", dialog.Close), Button("保存", () =>
            {
                _settings.Language = codes[language.SelectedIndex]; _settings.Dark = dark.IsChecked == true;
                SaveSettings();
                if (Application.Current is { } app) app.RequestedThemeVariant = _settings.Dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                Background = Surface("#F5F5F7", "#101318"); Foreground = Surface("#18181B", "#ECEEF4");
                if (_navigation.Parent?.Parent is Border nav) nav.Background = Surface("#F0F2F7", "#171D29");
                Navigate(_workspace); dialog.Close();
            })));
        await dialog.ShowDialog(this);
    }
    internal void CloseForTest() { _closingApproved = true; Close(); }
    private sealed class DesktopSettings
    {
        public string Language { get; set; } = "system";
        public bool Dark { get; set; }
        public List<string>? Recent { get; set; }
    }
}
