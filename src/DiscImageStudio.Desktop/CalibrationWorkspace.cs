using Avalonia;
using Avalonia.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ImageIO;
using SkiaSharp;

namespace DiscImageStudio.Desktop;

public sealed partial class MainWindow
{
    private CalibrationTarget? _target;
    private string? _photoPath;
    private string? _rectifiedPath;
    private CalibrationPhotoTransform? _photoTransform;
    private readonly List<CalibrationPoint> _outerPoints = [];
    private readonly List<CalibrationPoint> _holePoints = [];
    private readonly List<CalibrationPoint> _tracePixels = [];
    private CalibrationFitResult? _fit;
    private CalibrationCanvas? _photoCanvas;
    private bool _calibrationInterleave = true;
    private bool _shapeConfirmed;
    private double _physicalRadius = 60;
    private const int RectifiedSize = 1600;
    private void ResetCalibration()
    {
        _target = null; _photoPath = null; _rectifiedPath = null; _photoTransform = null; _fit = null;
        _outerPoints.Clear(); _holePoints.Clear(); _tracePixels.Clear();
    }
    private Control Calibration()
    {
        _photoCanvas?.Dispose();
        _photoCanvas = new CalibrationCanvas { Height = 510, MinWidth = 280 };
        var mode = new ComboBox { ItemsSource = new[] { "1 · 实体外缘", "2 · 中心孔边缘", "3 · 径向曲线" }, SelectedIndex = _photoTransform is null ? 0 : 2 };
        var count = Text("", 12);
        void SetMode()
        {
            bool trace = mode.SelectedIndex == 2;
            _photoCanvas.Points = trace ? _tracePixels : mode.SelectedIndex == 0 ? _outerPoints : _holePoints;
            _photoCanvas.SetImage(trace ? _rectifiedPath : _photoPath);
            count.Text = $"外缘 {_outerPoints.Count} · 中心孔 {_holePoints.Count} · 曲线 {_tracePixels.Count}";
            _photoCanvas.InvalidateVisual();
        }
        mode.SelectionChanged += (_, _) => SetMode();
        var result = Text(_fit is null ? "尚未拟合" : $"RMS {_fit.RmsErrorMm:F4} mm");
        var candidate = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var shape = Check("已核对整条预测曲线与照片吻合", false, v => _shapeConfirmed = v);
        _photoCanvas.Edited += () =>
        {
            _fit = null; _shapeConfirmed = false; shape.IsChecked = false; candidate.ItemsSource = null; _photoCanvas.Prediction = [];
            if (mode.SelectedIndex != 2) { _photoTransform = null; _rectifiedPath = null; _tracePixels.Clear(); }
            count.Text = $"外缘 {_outerPoints.Count} · 中心孔 {_holePoints.Count} · 曲线 {_tracePixels.Count}";
        };
        var fields = Column(Button("生成标定盘", GenerateCalibration),
            Row(Button("打开记录", OpenCalibration), Button("保存记录", SaveCalibration)),
            Text("先生成标定盘，刻录后导入该盘照片。", 12), Button("导入照片", async () =>
            {
                if (_target is null) throw new InvalidOperationException("请先创建或打开对应标定记录。");
                string? path = await PickOpen("导入照片", "*.png", "*.jpg", "*.jpeg", "*.bmp");
                if (path is null) return;
                using var original = SKBitmap.Decode(path) ?? throw new InvalidDataException("无法读取照片。");
                double scale = Math.Min(1, 2000.0 / Math.Max(original.Width, original.Height));
                using var resized = original.Resize(new SKImageInfo(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale))), SKFilterQuality.High);
                _photoPath = Session.CachePath("calibration-photo.png");
                using var encoded = resized.Encode(SKEncodedImageFormat.Png, 100); using (var stream = File.Create(_photoPath)) encoded.SaveTo(stream);
                _outerPoints.Clear(); _holePoints.Clear(); _tracePixels.Clear(); _photoTransform = null; _rectifiedPath = null; _fit = null;
                mode.SelectedIndex = 0; SetMode();
            }), mode, count, Text("点击布点，拖动已有点修正。拖动空白处或按住右键拖动可平移照片。滚轮缩放，Shift + 滚轮滚动页面，Option + 点击删除。", 12));
        fields.Children.Add(Check("8 cm 小盘（默认 12 cm）", _physicalRadius == 40, v =>
        {
            _physicalRadius = v ? 40 : 60; _photoTransform = null; _rectifiedPath = null; _tracePixels.Clear(); _fit = null; mode.SelectedIndex = 0; SetMode();
        }));
        fields.Children.Add(Button("计算照片矫正", async () =>
        {
            if (_photoPath is null) throw new InvalidOperationException("请先导入照片，并沿实体外缘和中心孔各布至少 5 个点。");
            await RunWork("正在矫正照片…", true, async token =>
            {
                var fit = await Task.Run(() => CalibrationPhotoRectifier.Fit(_outerPoints, _holePoints, _physicalRadius, 7.5, token), token);
                if (!fit.Succeeded) throw new InvalidOperationException(fit.Message);
                _photoTransform = fit.Transform!;
                _rectifiedPath = Session.CachePath("calibration-rectified.png");
                await Task.Run(() => RectifyPhoto(_photoPath, _rectifiedPath, _photoTransform, _physicalRadius, token), token);
                _tracePixels.Clear(); _fit = null; _shapeConfirmed = false; shape.IsChecked = false;
                mode.SelectedIndex = 2; SetMode(); result.Text = fit.Message;
            });
        }));
        fields.Children.Add(Button("反推参数", async () =>
        {
            if (_target is null || _photoTransform is null || _tracePixels.Count < 6) throw new InvalidOperationException("请在矫正后的照片上沿同一条径向曲线由内向外布至少 6 个点，建议 20 个以上。");
            var trace = _tracePixels.Select(PixelToDisc).ToArray();
            if (trace.Any(p => p.X * p.X + p.Y * p.Y > _physicalRadius * _physicalRadius))
                throw new InvalidOperationException("曲线点必须位于实体光盘内，请删除盘外的点。");
            await RunWork("正在反推参数…", true, async token =>
            {
                _fit = await Task.Run(() => CalibrationCrossFitter.Fit(_target, [new(0, trace)], new(AutoRange: true, AdaptiveRobustScale: true), token), token);
                candidate.ItemsSource = _fit.Candidates.Select((c, i) => $"{i + 1}: RMS {c.RmsErrorMm:F4} / max {c.MaxErrorMm:F4} mm").ToArray();
                candidate.SelectedIndex = _fit.Candidates.Count > 0 ? 0 : -1;
                result.Text = string.Join("\n", _fit.Warnings.Prepend(_fit.Succeeded ? $"RMS {_fit.RmsErrorMm:F4} mm" : "拟合未通过"));
            });
        }));
        candidate.SelectionChanged += async (_, _) => await Guard(() =>
        {
            _shapeConfirmed = false; shape.IsChecked = false;
            if (_fit is null || _target is null || candidate.SelectedIndex < 0) return Task.CompletedTask;
            var c = _fit.Candidates[candidate.SelectedIndex];
            var radii = _tracePixels.Select(PixelToDisc).Select(p => Math.Sqrt(p.X * p.X + p.Y * p.Y)).ToArray();
            _photoCanvas.Prediction = CalibrationCrossPrediction.GetOutline(_target, c.Parameters, radii.Min(), radii.Max(), c.RotationRadians).Select(DiscToPixel).ToArray();
            _photoCanvas.InvalidateVisual();
            return Task.CompletedTask;
        });
        fields.Children.Add(candidate); fields.Children.Add(result); fields.Children.Add(shape);
        fields.Children.Add(Button("应用参数", () =>
        {
            if (_fit is not { Succeeded: true } || candidate.SelectedIndex < 0 || !_shapeConfirmed) throw new InvalidOperationException("请先拟合并核对整条预测曲线。");
            var c = _fit.Candidates[candidate.SelectedIndex];
            if (c.RmsErrorMm > 0.35 || c.MaxErrorMm > 0.8) throw new InvalidOperationException("偏差过大，请修正照片轮廓或曲线点后重新拟合。");
            var p = c.Parameters; var d = Session.Document!;
            Session.Update(d with { Geometry = new(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.PitchLinear, p.PitchQuadratic, p.PitchCubic),
                BluRay = d.IsBluRay ? new(p.BluRayInvertMapping) : null });
            _status.Text = "参数已更新；已有标定记录保持原生成参数。";
        }));
        SetMode();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,330"), ColumnSpacing = 14, RowSpacing = 14 };
        grid.Children.Add(Card("标定照片", _photoCanvas)); var panel = Card("辅助标定", fields); Grid.SetColumn(panel, 1); grid.Children.Add(panel);
        grid.SizeChanged += (_, _) =>
        {
            bool narrow = grid.Bounds.Width < 800;
            grid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,330");
            grid.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
            Grid.SetColumn(panel, narrow ? 0 : 1); Grid.SetRow(panel, narrow ? 1 : 0);
        };
        return grid;
    }
    private CalibrationPoint PixelToDisc(CalibrationPoint p) => new((p.X / RectifiedSize * 2 - 1) * _physicalRadius, (p.Y / RectifiedSize * 2 - 1) * _physicalRadius);
    private CalibrationPoint DiscToPixel(CalibrationPoint p) => new((p.X / _physicalRadius + 1) * RectifiedSize / 2, (p.Y / _physicalRadius + 1) * RectifiedSize / 2);
    public static void RectifyPhoto(string source, string output, CalibrationPhotoTransform transform, double radius, CancellationToken token)
    {
        var image = RasterCodec.LoadBgra32(source);
        var pixels = new byte[RectifiedSize * RectifiedSize * 4];
        for (int y = 0; y < RectifiedSize; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < RectifiedSize; x++)
            {
                var p = transform.DiscToSource(new((x / (double)RectifiedSize * 2 - 1) * radius, (y / (double)RectifiedSize * 2 - 1) * radius));
                int sx = (int)Math.Round(p.X), sy = (int)Math.Round(p.Y);
                if (sx < 0 || sx >= image.Width || sy < 0 || sy >= image.Height) continue;
                Buffer.BlockCopy(image.Pixels, (sy * image.Width + sx) * 4, pixels, (y * RectifiedSize + x) * 4, 4);
            }
        }
        RasterCodec.SavePng(output, RectifiedSize, RectifiedSize, pixels, false);
    }
    private async Task GenerateCalibration()
    {
        var d = Session.Document!;
        string? output = await PickSave("生成标定盘", d.IsCd ? "calibration.wav" : "calibration.iso", d.IsCd ? "*.wav" : "*.iso");
        if (output is null) return;
        var parameters = Session.CalibrationParameters();
        var target = CalibrationTarget.Create(parameters);
        var source = Session.CachePath("calibration-target.png");
        byte[] gray = CalibrationReferencePattern.ReadGrayPixels();
        double inner2 = target.DrawingInnerRadiusMm * target.DrawingInnerRadiusMm;
        double outer2 = target.DrawingOuterRadiusMm * target.DrawingOuterRadiusMm;
        for (int y = 0; y < 2400; y++) for (int x = 0; x < 2400; x++)
        {
            var p = CalibrationReferencePattern.PixelToDisc(x, y, parameters.OuterRadiusMm, parameters.Kind);
            double r2 = p.X * p.X + p.Y * p.Y;
            if (r2 < inner2 - 1e-10 || r2 > outer2 + 1e-10) gray[y * 2400 + x] = 255;
        }
        RasterCodec.SavePng(source, 2400, 2400, gray, true);
        var document = d with { Tone = ToneProfile.LegacyBinary, Outputs = d.Outputs with { DataDirectory = "", WriteCue = true } };
        await RunWork("正在生成标定盘…", !d.IsDvd, async token =>
        {
            await Execute(JobArguments.Generate(document, source, output), token);
            ResetCalibration(); _target = target; _calibrationInterleave = d.Outputs.Interleave;
            string record = output + ".calibration.json";
            CalibrationSessionJson.Save(record, new(target, [], CdInterleave: _calibrationInterleave));
            Session.Update(Session.Document! with { CalibrationSessionPath = record }); _lastArtifact = output;
        });
        Navigate("标定");
    }
    private async Task SaveCalibration()
    {
        if (_target is null) throw new InvalidOperationException("请先生成或打开标定记录。");
        string? path = await PickSave("保存标定记录", "disc.calibration.json", "*.json");
        if (path is null) return;
        CalibrationPhotoSession? photo = null;
        if (_photoPath is not null)
            photo = new(Convert.ToBase64String(File.ReadAllBytes(_photoPath)), _outerPoints, _holePoints, _physicalRadius,
                _photoTransform is not null, _photoTransform is null ? [] : [new(0, _tracePixels.Select(PixelToDisc).ToArray())]);
        CalibrationSessionJson.Save(path, new(_target, [], CdInterleave: _calibrationInterleave, Photo: photo));
        Session.Update(Session.Document! with { CalibrationSessionPath = path });
    }
    private async Task OpenCalibration()
    {
        string? path = await PickOpen("打开标定记录", "*.json");
        if (path is null) return;
        var session = CalibrationSessionJson.Load(path);
        var expected = Session.Document!.IsCd ? CalibrationDiscKind.Cd : Session.Document.IsDvd ? CalibrationDiscKind.Dvd : CalibrationDiscKind.BluRay;
        if (session.Target.Parameters.Kind != expected) throw new InvalidOperationException("标定记录盘型与当前工程不一致。");
        ResetCalibration(); _target = session.Target; _calibrationInterleave = session.CdInterleave;
        if (session.Photo is { } photo)
        {
            _physicalRadius = photo.PhysicalOuterRadiusMm; _outerPoints.AddRange(photo.OuterBoundary); _holePoints.AddRange(photo.HoleBoundary);
            _photoPath = Session.CachePath("calibration-photo.png"); File.WriteAllBytes(_photoPath, Convert.FromBase64String(photo.ImagePngBase64));
            if (photo.RectificationConfirmed)
            {
                var fit = CalibrationPhotoRectifier.Fit(_outerPoints, _holePoints, _physicalRadius);
                if (!fit.Succeeded) throw new InvalidDataException(fit.Message);
                _photoTransform = fit.Transform!; _rectifiedPath = Session.CachePath("calibration-rectified.png");
                await Task.Run(() => RectifyPhoto(_photoPath, _rectifiedPath, _photoTransform, _physicalRadius, default));
                _tracePixels.AddRange(photo.Traces.SelectMany(t => t.Points).Select(DiscToPixel));
            }
        }
        Navigate("标定");
    }
}
