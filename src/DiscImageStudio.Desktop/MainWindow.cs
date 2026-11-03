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
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.Desktop;

public sealed partial class MainWindow : Window
{
    internal readonly ProjectSession Session = new();
    private readonly ContentControl _page = new();
    private readonly TextBlock _projectTitle = Text("Disc Image Studio", 16, true);
    private readonly TextBlock _status = Text("就绪", 12);
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, Height = 105, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 4 };
    private readonly Button _cancel = new() { Content = L.T("取消"), IsVisible = false };
    private readonly StackPanel _navigation = new() { Spacing = 8, Margin = new Thickness(14, 22) };
    private readonly List<Button> _navButtons = [];
    private readonly List<DiscCanvas> _canvases = [];
    private readonly DispatcherTimer _composeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _closingApproved;
    private string _workspace = "主页";
    private string? _previewPath;
    private string? _lastArtifact;
    private string? _projectPreview;
    private int _selectedLayer = -1;
    private ListBox? _layerList;
    private ContentControl? _layerEditor;
    private DiscCanvas? _designCanvas;
    private TextBox? _artifactInput;
    private List<string> _recent = [];
    private static string SettingsPath => Path.Combine(ProjectSession.ApplicationDirectory, "desktop-settings.json");
    private DesktopSettings _settings = new();

    public MainWindow()
    {
        LoadSettings();
        L.Initialize(_settings.Language);
        _status.Text = L.T("就绪"); _cancel.Content = L.T("取消");
        if (Application.Current is { } app) app.RequestedThemeVariant = _settings.Dark
            ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        Title = "Disc Image Studio · macOS";
        Width = 1280; Height = 850; MinWidth = 900; MinHeight = 650;
        FontFamily = new FontFamily("avares://DiscImageStudio.Desktop/Assets/Fonts#HarmonyOS Sans SC");
        Background = Surface("#F5F5F7", "#101318");
        Foreground = Surface("#18181B", "#ECEEF4");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(22, 12) };
        toolbar.Children.Add(_projectTitle);
        var actions = Row(Button("新建工程", () => NewProject()), Button("打开工程", OpenProject), Button("保存", () => SaveProject()), Button("另存为", () => SaveProject(true)), Button("工程设置", EditGeometry), Button("设置", ShowSettings));
        Grid.SetColumn(actions, 1); toolbar.Children.Add(actions);
        root.Children.Add(toolbar);
        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("176,*") };
        Grid.SetRow(middle, 1);
        foreach (var (label, glyph) in new[] { ("主页", "⌂"), ("设计", "◈"), ("预览", "◎"), ("导出", "↗"), ("刻录", "◉"), ("标定", "⌖") })
        {
            var item = Button($"{glyph}   {L.T(label)}", () => Navigate(label));
            item.HorizontalAlignment = HorizontalAlignment.Stretch;
            item.HorizontalContentAlignment = HorizontalAlignment.Left;
            item.MinHeight = 44;
            _navigation.Children.Add(item); _navButtons.Add(item);
        }
        middle.Children.Add(new Border { Background = Surface("#ECECEF", "#171B23"), Child = _navigation });
        Grid.SetColumn(_page, 1); middle.Children.Add(_page); root.Children.Add(middle);
        var dock = new StackPanel { Spacing = 5, Margin = new Thickness(20, 8) };
        var statusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        statusRow.Children.Add(_status); Grid.SetColumn(_cancel, 1); statusRow.Children.Add(_cancel);
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        dock.Children.Add(statusRow); dock.Children.Add(_progress);
        dock.Children.Add(new Expander { Header = L.T("任务日志"), Content = _log });
        Grid.SetRow(dock, 2); root.Children.Add(dock);
        Content = root;
        Session.Changed += () => { _projectTitle.Text = Session.Document is { } d ? $"{d.Name}{(Session.IsDirty ? " •" : "")}  /  {d.FamilyLabel}" : "Disc Image Studio"; };
        _composeTimer.Tick += (_, _) => { _composeTimer.Stop(); RefreshComposite(); };
        KeyDown += async (_, e) =>
        {
            if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0) return;
            if (e.Key == Key.S) { await Guard(() => SaveProject()); e.Handled = true; }
            if (e.Key == Key.O) { await Guard(OpenProject); e.Handled = true; }
            if (e.Key == Key.N) { await Guard(() => NewProject()); e.Handled = true; }
        };
        Closing += OnClosing;
        Closed += (_, _) => { _composeTimer.Stop(); Session.CleanCache(); DisposeCanvases(); _photoCanvas?.Dispose(); };
        Navigate("主页");
        // Deterministic snapshots are an explicit developer entry point, never a normal startup side effect.
        string[] args = Environment.GetCommandLineArgs();
        int snapshot = Array.IndexOf(args, "--ui-snapshot");
        if (snapshot >= 0 && snapshot + 1 < args.Length)
            Opened += async (_, _) => await CaptureSnapshots(args[snapshot + 1]);
    }

    internal static TextBlock Text(string value, double size = 14, bool bold = false) => new()
    { Text = L.T(value), FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    internal Button Button(string title, Action action) => Button(title, () => { action(); return Task.CompletedTask; });
    internal Button Button(string title, Func<Task> action)
    {
        var button = new Button { Content = L.T(title), Padding = new Thickness(14, 8), MinHeight = 36,
            CornerRadius = new CornerRadius(7), Background = Surface("#FFFFFF", "#282F3C"),
            BorderBrush = Surface("#D6D9E0", "#454F62"), BorderThickness = new Thickness(1) };
        if (title.StartsWith("下一步", StringComparison.Ordinal) || title is "生成文件" or "渲染预览" or "创建工程")
        {
            button.Background = Brush.Parse("#2569CC"); button.Foreground = Brushes.White; button.BorderThickness = new Thickness(0);
        }
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
        Background = Surface("#FFFFFF", "#1C212B"), BorderBrush = Surface("#E2E2E6", "#343B49"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Child = Column(Text(title, 16, true), content)
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
        void Append() { _log.Text = ((_log.Text ?? "") + message + "\n"); if (_log.Text.Length > 60000) _log.Text = _log.Text[^40000..]; }
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
        _composeTimer.Stop(); DisposeCanvases(); _photoCanvas?.Dispose(); _photoCanvas = null; _workspace = page;
        for (int i = 0; i < _navButtons.Count; i++)
            _navButtons[i].Background = new[] { "主页", "设计", "预览", "导出", "刻录", "标定" }[i] == page ? Surface("#D9E8FF", "#204A78") : Brushes.Transparent;
        var body = page switch { "主页" => Home(), "设计" => Design(), "预览" => Preview(), "导出" => Export(), "刻录" => Burn(), _ => Calibration() };
        var content = Column(Text(page == "主页" ? "把图像刻进光盘" : page, 28, true),
            Text(page == "主页" ? "选择盘片，开始你的下一个设计。" : $"{Session.Document!.FamilyLabel}  ·  {Session.Document.Geometry.InnerRadiusMm:G8}–{Session.Document.Geometry.OuterRadiusMm:G8} mm  ·  {Session.Document.Geometry.Sectors:N0} sectors", 12));
        if (page != "主页") content.Children.Add(Row(Button("设计", () => Navigate("设计")), Button("预览", () => Navigate("预览")),
            Button("导出", () => Navigate("导出")), Button("刻录", () => Navigate("刻录")), Button("标定", () => Navigate("标定"))));
        content.Children.Add(body);
        _page.Content = new ScrollViewer { Content = content, Padding = new Thickness(28, 24) };
    }
    private Control Home()
    {
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        foreach (var (family, title, subtitle, index) in new[] { ("cd", "CD", "WAV + CUE / RAW / ISO", 0), ("dvd", "DVD", "ISO · 4.7 GB", 1), ("bd", "Blu-ray", "BD-R · 25 GB", 2) })
        {
            var card = Card(title, Column(Text(subtitle, 13), Button("新建工程", () => NewProject(family))));
            card.Margin = new Thickness(0, 0, 12, 0); Grid.SetColumn(card, index); cards.Children.Add(card);
        }
        var recent = Column();
        foreach (string path in _recent.Where(File.Exists).Take(8))
            recent.Children.Add(Button(Path.GetFileNameWithoutExtension(path), () => OpenProjectPath(path)));
        if (recent.Children.Count == 0) recent.Children.Add(Text("还没有最近工程。"));
        return Column(cards, Card("最近工程", recent), Text("macOS 桌面版 · 复用 Windows 版工程与编码算法", 12));
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
                if (_navigation.Parent is Border nav) nav.Background = Surface("#ECECEF", "#171B23");
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
