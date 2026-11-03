using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.Desktop;

public sealed partial class MainWindow
{
    private DiscCanvas Canvas(bool design = false)
    {
        var canvas = new DiscCanvas { Session = Session, Height = 450, MinWidth = 260, SelectedLayer = _selectedLayer };
        _canvases.Add(canvas);
        if (design)
        {
            _designCanvas = canvas;
            canvas.LayerDragged += layer => { ReplaceLayer(layer); _composeTimer.Stop(); _composeTimer.Start(); };
        }
        return canvas;
    }
    private Control Design()
    {
        _layerList = new ListBox { MinHeight = 120, MaxHeight = 260 };
        _layerEditor = new ContentControl();
        _layerList.SelectionChanged += (_, _) =>
        {
            _selectedLayer = _layerList.SelectedIndex;
            if (_designCanvas is { } canvas) canvas.SelectedLayer = _selectedLayer;
            ShowLayerEditor();
        };
        var layers = Card("图层", Column(Button("导入图片", ImportImage), _layerList,
            Row(Button("↑", () => MoveLayer(-1)), Button("↓", () => MoveLayer(1)), Button("删除", DeleteLayer))));
        var canvas = Canvas(true);
        var stage = Card("盘面设计", Column(canvas, Text("拖动移动 · 滚轮缩放 · Shift + 滚轮旋转", 12)));
        var props = Column(Card("属性", _layerEditor), TonePanel());
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,260"), ColumnSpacing = 14 };
        layout.Children.Add(layers); Grid.SetColumn(stage, 1); layout.Children.Add(stage); Grid.SetColumn(props, 2); layout.Children.Add(props);
        layout.SizeChanged += (_, _) =>
        {
            bool narrow = layout.Bounds.Width < 980;
            layout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "220,*,260");
            layout.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto,Auto" : "Auto");
            Grid.SetColumn(stage, narrow ? 0 : 1); Grid.SetRow(stage, narrow ? 1 : 0);
            Grid.SetColumn(props, narrow ? 0 : 2); Grid.SetRow(props, narrow ? 2 : 0);
        };
        RefreshLayers(); RefreshComposite();
        return Column(layout, Row(Button("工程设置", EditGeometry), Button("下一步：预览", () => Navigate("预览"))));
    }
    private async Task ImportImage()
    {
        string? path = await PickOpen("导入图片", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif");
        if (path is null) return;
        _ = DiscImageStudio.ImageIO.RasterCodec.LoadBgra32(path, 256 * 1024 * 1024, 100_000_000);
        var d = Session.Document!;
        Session.Update(d with { Layers = new[] { new ProjectLayer(Guid.NewGuid().ToString("N"), Path.GetFileName(path), path, true, 0, 0, 0, 1) }.Concat(d.Layers).ToArray() });
        _selectedLayer = 0; RefreshLayers(); RefreshComposite();
    }
    private void RefreshLayers()
    {
        if (_layerList is null) return;
        _layerList.ItemsSource = Session.Document!.Layers.Select(l => (l.Visible ? "◉ " : "○ ") + l.Name).ToArray();
        _layerList.SelectedIndex = _selectedLayer;
        ShowLayerEditor();
    }
    private void ShowLayerEditor()
    {
        if (_layerEditor is null) return;
        var d = Session.Document!;
        if (_selectedLayer < 0 || _selectedLayer >= d.Layers.Count) { _layerEditor.Content = Text("选择或导入一张图片。"); return; }
        string id = d.Layers[_selectedLayer].Id;
        ProjectLayer Current() => Session.Document!.Layers.First(l => l.Id == id);
        var layer = Current();
        var fields = Column();
        var name = new TextBox { Text = layer.Name };
        name.LostFocus += (_, _) => { ReplaceLayer(Current() with { Name = name.Text ?? "Image" }); RefreshLayers(); };
        fields.Children.Add(name);
        fields.Children.Add(Check("可见", layer.Visible, value => { ReplaceLayer(Current() with { Visible = value }); RefreshComposite(); }));
        Number("X (mm)", layer.TranslateXMm, -500, 500, v => Edit(Current() with { TranslateXMm = v }), fields);
        Number("Y (mm)", layer.TranslateYMm, -500, 500, v => Edit(Current() with { TranslateYMm = v }), fields);
        Number("旋转 (°)", layer.RotationDeg, -36000, 36000, v => Edit(Current() with { RotationDeg = v }), fields, 1);
        Number("缩放", layer.Scale, 0.01, 100, v => Edit(Current() with { Scale = v }), fields, 0.05);
        _layerEditor.Content = fields;
        void Edit(ProjectLayer value) { ReplaceLayer(value); _composeTimer.Stop(); _composeTimer.Start(); }
    }
    private void ReplaceLayer(ProjectLayer layer)
    {
        if (_busy) return;
        var d = Session.Document!;
        Session.Update(d with { Layers = d.Layers.Select(l => l.Id == layer.Id ? layer : l).ToArray() });
    }
    private void MoveLayer(int delta)
    {
        var layers = Session.Document!.Layers.ToList();
        int target = _selectedLayer + delta;
        if (_selectedLayer < 0 || target < 0 || target >= layers.Count) return;
        (layers[target], layers[_selectedLayer]) = (layers[_selectedLayer], layers[target]);
        Session.Update(Session.Document with { Layers = layers }); _selectedLayer = target; RefreshLayers(); RefreshComposite();
    }
    private void DeleteLayer()
    {
        var layers = Session.Document!.Layers.ToList();
        if (_selectedLayer < 0 || _selectedLayer >= layers.Count) return;
        layers.RemoveAt(_selectedLayer); _selectedLayer = Math.Min(_selectedLayer, layers.Count - 1);
        Session.Update(Session.Document with { Layers = layers }); RefreshLayers(); RefreshComposite();
    }
    private void RefreshComposite()
    {
        try
        {
            _projectPreview = Session.Document?.Layers.Any(l => l.Visible) == true ? Session.Compose(1024) : null;
            _designCanvas?.SetImage(_projectPreview);
        }
        catch (Exception e) { _status.Text = e.Message; Log(e.Message); }
    }
    private Control TonePanel()
    {
        var fields = Column(); var d = Session.Document!; var tone = d.Tone ?? ToneProfile.LegacyBinary;
        ToneProfile Current() => Session.Document!.Tone ?? ToneProfile.LegacyBinary;
        void Update(ToneProfile value) { Session.Update(Session.Document! with { Tone = value }); }
        Number("抖动强度 (%)", tone.DitherStrength * 100, 0, 100, v => Update(Current() with { DitherStrength = v / 100 }), fields, 5);
        Number("亮度", tone.Brightness, -64, 64, v => Update(Current() with { Brightness = v }), fields, 1);
        fields.Children.Add(Check("sRGB 线性化", tone.SrgbLinearization, v => Update(Current() with { SrgbLinearization = v })));
        if (d.IsCd) fields.Children.Add(Check("四级灰阶", tone.Levels == 4, v => Update(Current() with { Levels = v ? 4 : 2 })));
        if (d.IsBluRay) fields.Children.Add(Check("反转 3T/6T 极性", d.BluRay?.InvertMapping ?? false,
            v => Session.Update(Session.Document! with { BluRay = new(v) })));
        fields.Children.Add(Text("影调效果在预览页查看。", 12));
        return Card("影调", fields);
    }
    private async Task EditGeometry()
    {
        if (_busy || Session.Document is null) return;
        var d = Session.Document; var geometry = d.Geometry;
        var dialog = Dialog("工程设置"); var fields = Column();
        var name = new TextBox { Text = d.Name }; fields.Children.Add(Column(Text("工程名称"), name));
        Number("内半径 (mm)", geometry.InnerRadiusMm, 0.1, 59.9, v => geometry = geometry with { InnerRadiusMm = v }, fields, 0.001);
        Number("外半径 (mm)", geometry.OuterRadiusMm, 0.2, 60, v => geometry = geometry with { OuterRadiusMm = v }, fields, 0.001);
        Number("扇区数", geometry.Sectors, 1, 12219392, v => geometry = geometry with { Sectors = (long)v }, fields, d.IsCd ? 1 : d.IsDvd ? 16 : 32);
        if (d.IsDvd)
        {
            Number("轨距线性系数", geometry.PitchLinear, -10, 10, v => geometry = geometry with { PitchLinear = v }, fields, 0.00001);
            Number("轨距二次系数", geometry.PitchQuadratic, -10, 10, v => geometry = geometry with { PitchQuadratic = v }, fields, 0.00001);
            Number("轨距三次系数", geometry.PitchCubic, -10, 10, v => geometry = geometry with { PitchCubic = v }, fields, 0.00001);
        }
        fields.Children.Add(Row(Button("取消", dialog.Close), Button("保存", () =>
        {
            var changed = d with { Name = name.Text ?? d.Name, Geometry = geometry };
            Session.Update(changed); dialog.Close(); Navigate(_workspace);
        })));
        dialog.Content = new ScrollViewer { Content = fields };
        await dialog.ShowDialog(this);
    }
    private Control Preview()
    {
        var d = Session.Document!;
        var mode = new ComboBox { ItemsSource = new[] { L.T("设计投影"), L.T("成品读回") }, SelectedIndex = 0 };
        _artifactInput = new TextBox { Text = _lastArtifact ?? d.Outputs.OutputPath, Watermark = "WAV / RAW / ISO" };
        var fields = Column(Text("预览模式"), mode, Text("成品路径"), _artifactInput,
            Button("选择成品", async () => { string? path = await PickOpen("选择成品", "*.wav", "*.raw", "*.iso"); if (path is not null) _artifactInput.Text = path; }));
        double inner = d.Geometry.InnerRadiusMm, outer = d.Geometry.OuterRadiusMm; int size = 1600, samples = 16;
        Number("实测内半径 (mm)", inner, 0.1, 59, v => inner = v, fields, 0.001);
        Number("实测外半径 (mm)", outer, 0.2, 60, v => outer = v, fields, 0.001);
        Number("导出尺寸 (px)", size, 256, 8192, v => size = (int)v, fields, 256);
        Number("每扇区采样", samples, 1, 256, v => samples = (int)v, fields, 1);
        var canvas = Canvas(); canvas.SetImage(_previewPath);
        fields.Children.Add(Button("渲染预览", async () =>
        {
            string? artifact = _artifactInput.Text;
            string output = Session.CachePath("preview.png");
            bool readback = mode.SelectedIndex == 1;
            await RunWork("正在渲染预览…", !d.IsDvd, async token =>
            {
                string input = readback ? artifact ?? "" : await Task.Run(() => Session.Compose(2048), token);
                if (readback && !File.Exists(input)) throw new FileNotFoundException("请选择存在的成品文件。", input);
                await Task.Run(() => PreviewService.Render(d, input, output, readback, inner, outer, size, samples,
                    new Progress<DiscJobProgress>(p => Log(p.Message)), token), token);
                _previewPath = output; canvas.SetImage(output);
            });
        }));
        fields.Children.Add(Button("导出预览", async () =>
        {
            if (_previewPath is null) throw new InvalidOperationException("请先渲染预览。");
            string? path = await PickSave("导出预览", "disc-preview.png", "*.png");
            if (path is not null) File.Copy(_previewPath, path, true);
        }));
        fields.Children.Add(Text("读回是编码数据的投影，实盘可见度仍需刻录确认。", 12));
        var panel = Card("预览参数", fields); var stage = Card("盘面预览", Column(canvas, Button("下一步：导出", () => Navigate("导出"))));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,290"), ColumnSpacing = 14 };
        grid.Children.Add(stage); Grid.SetColumn(panel, 1); grid.Children.Add(panel);
        grid.SizeChanged += (_, _) =>
        {
            bool narrow = grid.Bounds.Width < 800;
            grid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,290");
            grid.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
            Grid.SetColumn(panel, narrow ? 0 : 1); Grid.SetRow(panel, narrow ? 1 : 0);
        };
        return grid;
    }
    private Control Export()
    {
        var d = Session.Document!;
        var path = new TextBox { Text = d.Outputs.OutputPath, Watermark = "WAV / RAW / ISO" };
        var folder = new TextBox { Text = d.Outputs.DataDirectory };
        var label = new TextBox { Text = d.Outputs.VolumeLabel };
        void Update() => Session.Update(Session.Document! with { Outputs = Session.Document!.Outputs with {
            OutputPath = string.IsNullOrWhiteSpace(path.Text) ? Session.Document!.Outputs.OutputPath : path.Text, DataDirectory = folder.Text ?? "", VolumeLabel = label.Text ?? "DISC_IMAGE" } });
        foreach (var field in new[] { path, folder, label }) field.LostFocus += (_, _) => Update();
        var fields = Column(Text("输出位置"), path, Button("选择输出位置", async () =>
        {
            Update(); bool audio = d.IsCd && string.IsNullOrWhiteSpace(folder.Text);
            string? picked = await PickSave("输出文件", audio ? "cd-track.wav" : "disc-image.iso", audio ? "*.wav" : "*.iso");
            if (picked is not null) { path.Text = picked; Update(); }
        }), Text("随盘文件夹（留空为纯绘图）"), folder,
            Row(Button("选择文件夹", async () => { string? picked = await PickFolder(); if (picked is not null) { folder.Text = picked; Update(); } }),
                Button("清空", () => { folder.Text = ""; Update(); })), Text("卷标"), label,
            Check("文件放在外圈", d.Outputs.FilesAtOuterRing, v => Session.Update(Session.Document! with { Outputs = Session.Document!.Outputs with { FilesAtOuterRing = v } })));
        if (d.IsCd)
        {
            fields.Children.Add(Check("同时导出 CUE 文件", d.Outputs.WriteCue, v => Session.Update(Session.Document! with { Outputs = Session.Document!.Outputs with { WriteCue = v } })));
            fields.Children.Add(Check("延迟交织", d.Outputs.Interleave, v => Session.Update(Session.Document! with { Outputs = Session.Document!.Outputs with { Interleave = v } })));
        }
        fields.Children.Add(Text("输出使用 8192 px 合成图；影调在设计页调整。", 12));
        fields.Children.Add(Button("生成文件", async () =>
        {
            Update(); string output = path.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("请选择输出位置。");
            output = Path.GetFullPath(output);
            if (File.Exists(output) && !await Confirm("覆盖文件", output)) return;
            var document = Session.Document!;
            await RunWork("正在生成镜像…", !document.IsDvd, async token =>
            {
                string source = await Task.Run(() => Session.Compose(8192), token);
                await Execute(JobArguments.Generate(document, source, output), token);
                _lastArtifact = output;
            });
        }));
        fields.Children.Add(Button("下一步：刻录", () => Navigate("刻录")));
        return Card("导出镜像 / 音轨", fields);
    }
    private async Task Execute(string[] args, CancellationToken token)
    {
        var result = await Task.Run(async () => await ConsoleModuleRunner.Catalog.ExecuteAsync(
            new DiscJobRequest(args[0], args.Skip(1)), new Progress<DiscJobProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                _status.Text = p.Message;
                if (p.Fraction is { } fraction) { _progress.IsIndeterminate = false; _progress.Value = fraction * 100; }
            })), token), token);
        Log(result.Summary ?? "");
        if (!result.Succeeded) throw new InvalidOperationException(result.Summary ?? $"任务失败 ({result.ExitCode})");
    }
    internal async Task RunWork(string title, bool canCancel, Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true; _status.Text = L.T(title); _progress.IsIndeterminate = true; _progress.Value = 0;
        _cancel.IsVisible = canCancel;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        _page.IsEnabled = false; _navigation.IsEnabled = false;
        Log(title);
        try { await work(cancellation.Token); _status.Text = L.T("完成"); _progress.Value = 100; }
        catch (OperationCanceledException) { _status.Text = L.T("已取消"); Log("已取消；未完成的输出文件可能留在磁盘上。"); }
        finally { _busy = false; _cancellation = null; _cancel.IsVisible = false; _page.IsEnabled = true; _navigation.IsEnabled = true; _progress.IsIndeterminate = false; }
    }
}
