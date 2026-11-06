using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using DiscImageStudio;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Desktop;
using DiscImageStudio.Dvd;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Services;
using SkiaSharp;

string work = Path.Combine(Path.GetTempPath(), "disc-desktop-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
void Reject(Action action, string message)
{
    try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(message);
}
try
{
    string input = Path.Combine(work, "asymmetric input 图.png");
    byte[] pixels = new byte[128 * 128 * 4];
    for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
    {
        int i = (y * 128 + x) * 4; pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = x < 64 ? (byte)255 : (byte)0; pixels[i + 3] = 255;
    }
    RasterCodec.SavePng(input, 128, 128, pixels, false);
    ProjectSession session = new(); session.Create("cd", "Round trip", new(24.5, 56.8, 2));
    session.Update(session.Document! with { Layers = [new("a", "非对称", input, true, 0, 0, 0, 1)], Tone = new(0.75, 4, 3, true) });
    string project = Path.Combine(work, "example.discproj"); session.Save(project);
    var loaded = ProjectJsonStore.Load(project);
    Check(loaded.Layers[0].ImagePath == input && loaded.Tone == session.Document!.Tone && !session.IsDirty, "Project lost layer/tone data");
    Check(loaded.ThumbnailPngBase64 is { Length: > 0 }, "Missing portable project thumbnail");
    session.Open(project);
    string composed = session.Compose(256); var composite = RasterCodec.LoadBgra32(composed);
    Check(composite.Pixels[(128 * 256 + 64) * 4 + 2] > 240 && composite.Pixels[(128 * 256 + 190) * 4 + 2] < 10, "Composition mirrored the input");
    var rotated = loaded with { Layers = [loaded.Layers[0] with { RotationDeg = 180 }] };
    string rotation = Path.Combine(work, "rotation.png"); DesignComposer.Render(rotated, 57.5, 256, rotation);
    var rot = RasterCodec.LoadBgra32(rotation);
    Check(rot.Pixels[(128 * 256 + 64) * 4 + 2] < 10 && rot.Pixels[(128 * 256 + 190) * 4 + 2] > 240, "Rotation not applied");
    foreach (string family in new[] { "cd", "dvd", "bd" })
    {
        long sectors = family == "cd" ? 2 : family == "dvd" ? 160 : 1024;
        session.Create(family, "Optical smoke", new(24.5, 56.8, sectors));
        var doc = session.Document! with { Outputs = session.Document!.Outputs with { DvdReadCompatibility = family == "dvd" } };
        if (family == "bd") doc = doc with { Geometry = doc.Geometry with { PitchLinear = 0.02, PitchQuadratic = -0.01, PitchCubic = 0.003 } };
        string output = Path.Combine(work, family == "cd" ? "track.wav" : family + ".iso");
        string[] commandArgs = JobArguments.Generate(doc, input, output);
        var result = await ConsoleModuleRunner.Catalog.ExecuteAsync(new(commandArgs[0], commandArgs.Skip(1)));
        Check(result.Succeeded, family + " generation failed");
        Check(new FileInfo(output).Length == sectors * (family == "cd" ? 2352 : 2048) + (family == "cd" ? 44 : 0), family + " wrong output length");
        if (family == "dvd")
        {
            Check(DvdImageMetadata.TryLoad(output) is { ReadCompatibility: true }, "DVD export lost the project's read compatibility setting");
            session.Update(doc); session.Save(project); session.Open(project);
            Check(session.Document!.Outputs.DvdReadCompatibility && session.CalibrationParameters().DvdReadCompatibility,
                "DVD compatibility must survive project reopening and calibration capture");
        }
        if (family == "bd")
        {
            var metadata = BluRayIsoMetadata.TryLoad(output);
            Check(metadata is { PitchLinear: 0.02, PitchQuadratic: -0.01, PitchCubic: 0.003 }, "BD export lost calibrated pitch coefficients");
            string actualPreview = Path.Combine(work, "bd-pitched-actual.png");
            string expectedPreview = Path.Combine(work, "bd-pitched-expected.png");
            await PreviewService.Render(doc with { Geometry = new(24.5, 56.8, 1024) }, output, actualPreview, true, 24.5, 56.8, 256, 16);
            var measured = new BluRayDiscParameters(1024, 1, 24.5, 56.8, PitchLinear: 0.02, PitchQuadratic: -0.01, PitchCubic: 0.003);
            BluRayPreviewRenderer.RenderReadback(output, expectedPreview, measured, 256, default, metadata!.DrawingStartSector, metadata.DrawingEndSectorExclusive);
            FileRegionPreviewOverlay.ApplyBluRay(expectedPreview, measured, metadata.DrawingStartSector, metadata.DrawingEndSectorExclusive, default);
            Check(RasterCodec.LoadBgra32(actualPreview).Pixels.AsSpan().SequenceEqual(RasterCodec.LoadBgra32(expectedPreview).Pixels),
                "BD readback ignored the artifact's calibrated pitch coefficients");
        }
        if (family == "cd")
        {
            Check(File.ReadAllText(Path.ChangeExtension(output, ".cue")).Contains("FILE \"track.wav\" WAVE"), "Wrong WAV CUE");
            string aiff = Path.Combine(work, "track.aiff");
            Check(MacDiscRecorder.GetAudioSectorCount(output) == 2, "WAV preflight read the wrong sector count");
            Check(MacDiscRecorder.WriteAudioAiff(output, aiff) == 2, "Wrong burn sector count");
            byte[] wave = File.ReadAllBytes(output), ai = File.ReadAllBytes(aiff);
            Check(ai.Length == 54 + 2 * 2352 && BinaryPrimitives.ReadUInt32BigEndian(ai.AsSpan(4, 4)) == ai.Length - 8, "Invalid AIFF header");
            Check(Enumerable.Range(0, 2 * 2352).All(i => ai[54 + i] == wave[44 + (i ^ 1)]), "Audio PCM changed in conversion");
            foreach (bool readback in new[] { false, true })
            {
                string preview = Path.Combine(work, "cd-" + readback + ".png");
                var command = JobArguments.Preview(doc, readback ? output : input, preview, readback, 24.5, 56.8, 256, 16);
                var p = await ConsoleModuleRunner.Catalog.ExecuteAsync(new(command[0], command.Skip(1)));
                Check(p.Succeeded && RasterCodec.LoadBgra32(preview).Width == 256, "CD preview failed");
            }
        }
        if (family == "dvd") foreach (bool readback in new[] { false, true })
        {
            string preview = Path.Combine(work, "dvd-" + readback + ".png");
            var command = JobArguments.Preview(doc, readback ? output : input, preview, readback, 24.5, 56.8, 256, 16);
            var p = await ConsoleModuleRunner.Catalog.ExecuteAsync(new(command[0], command.Skip(1)));
            Check(p.Succeeded && RasterCodec.LoadBgra32(preview).Width == 256, "DVD preview failed");
        }
    }
    // Imported variable-pitch CD geometry must survive export and both preview routes.
    session.Create("cd", "Variable pitch", new(24.9, 57.9, 160, 0.02, -0.01, 0.003));
    var pitched = session.Document!;
    string pitchedTrack = Path.Combine(work, "pitched.wav");
    var pitchedArgs = JobArguments.Generate(pitched, input, pitchedTrack);
    var pitchedResult = await ConsoleModuleRunner.Catalog.ExecuteAsync(new(pitchedArgs[0], pitchedArgs.Skip(1)));
    var pitchedMetadata = CdTrackMetadata.TryLoad(pitchedTrack);
    Check(pitchedResult.Succeeded && pitchedMetadata is { PitchLinear: 0.02, PitchQuadratic: -0.01, PitchCubic: 0.003 },
        "CD export lost calibrated pitch coefficients");
    foreach (bool readback in new[] { false, true })
    {
        string actualPreview = Path.Combine(work, "pitch-actual-" + readback + ".png");
        string expectedPreview = Path.Combine(work, "pitch-expected-" + readback + ".png");
        var previewDocument = readback ? pitched with { Geometry = new(24.5, 56.8, 2) } : pitched;
        await PreviewService.Render(previewDocument, readback ? pitchedTrack : input, actualPreview, readback, 24.9, 57.9, 256, 16);
        var parameters = pitchedMetadata!.CreateParameters();
        if (readback) CdTrackGenerator.PreviewTrack(pitchedTrack, expectedPreview, parameters, 256, 147, pitchedMetadata.Interleaved);
        else CdTrackGenerator.PreviewWarp(input, expectedPreview, parameters, parameters, 256, 16, pitched.Tone ?? ToneProfile.LegacyBinary);
        Check(RasterCodec.LoadBgra32(actualPreview).Pixels.AsSpan().SequenceEqual(RasterCodec.LoadBgra32(expectedPreview).Pixels),
            "CD preview ignored calibrated pitch or artifact metadata: " + readback);
    }
    // Mixed discs must retain files and mark their extents identically in projection/readback.
    string data = Path.Combine(work, "随盘文件"); Directory.CreateDirectory(data);
    byte[] markerBytes = System.Text.Encoding.UTF8.GetBytes("portable-desktop-file-integrity-marker-12345");
    File.WriteAllBytes(Path.Combine(data, "readme.txt"), markerBytes);
    foreach (string family in new[] { "cd", "dvd", "bd" }) foreach (bool outer in new[] { false, true })
    {
        session.Create(family, "Mixed disc", new(24.5, 56.8, 1024));
        var doc = session.Document! with { Outputs = session.Document!.Outputs with { DataDirectory = data, FilesAtOuterRing = outer } };
        string iso = Path.Combine(work, family + outer + ".iso");
        var command = JobArguments.Generate(doc, input, iso);
        var generated = await ConsoleModuleRunner.Catalog.ExecuteAsync(new(command[0], command.Skip(1)));
        Check(generated.Succeeded && File.ReadAllBytes(iso).AsSpan().IndexOf(markerBytes) >= 0, "Mixed disc lost file payload: " + family);
        foreach (bool readback in new[] { false, true })
        {
            string preview = Path.Combine(work, family + outer + readback + ".png");
            await PreviewService.Render(doc, readback ? iso : input, preview, readback, 24.5, 56.8, 256, 16);
            var rendered = RasterCodec.LoadBgra32(preview);
            Check(Enumerable.Range(0, rendered.Width * rendered.Height).Any(i => rendered.Pixels[i * 4] == 21 && rendered.Pixels[i * 4 + 1] == 204 && rendered.Pixels[i * 4 + 2] == 250), "Missing file-region overlay: " + family + readback);
        }
    }
    session.Create("cd", "Guard", new(24.5, 56.8, 2));
    Reject(() => JobArguments.Generate(session.Document!, input, Path.Combine(work, "bad.iso")), "Accepted wrong CD audio extension");
    Reject(() => JobArguments.Preview(session.Document!, input, "p.png", false, 56.8, 24.5, 256, 16), "Accepted invalid measured radii");
    Reject(() => MacDiscRecorder.WriteAudioAiff(input, Path.Combine(work, "bad.aiff")), "Accepted PNG as audio");
    const string xml = "<?xml version=\"1.0\"?><!DOCTYPE [<!ELEMENT deviceList (device)*>]><deviceList><device index=\"1\"/></deviceList>";
    Check(MacDiscRecorder.ReadSystemXml(xml).Root!.Name == "deviceList", "Failed to parse real drutil's anonymous DTD");
    var media = MacDiscRecorder.ParseMedia("<statusdoc><statusfordevice><device index='2'/><deviceStatus><mediaInfo><blank/><freeSpace msf='79:59:74'/><mediaClass value='CD'/><mediaType value='CD-R'/></mediaInfo></deviceStatus></statusfordevice></statusdoc>", "2");
    Check(media.Blank && media.FreeSectors == 359999, "Incorrect CD capacity conversion");
    const string scopedStatus = "<statusdoc><statusfordevice><device name='lenovo Slim_USB_Burner'/><deviceStatus><mediaInfo><blank/><freeSpace blockCount='359847' msf='79:57:72'/><mediaClass value='CD'/><mediaType value='CD-R'/></mediaInfo></deviceStatus></statusfordevice></statusdoc>";
    Check(MacDiscRecorder.ParseMedia(scopedStatus, "1") is { Blank: true, FreeSectors: 359847, Description: "CD-R" },
        "Drive-scoped status must recognize a blank disc when the device index is omitted");
    var blankCd = MacDiscRecorder.ParseMedia(scopedStatus, "1");
    MacDiscRecorder.ValidateMedia(blankCd, true, 359697);
    MacDiscRecorder.ValidateMedia(blankCd, false, 359847);
    Reject(() => MacDiscRecorder.ValidateMedia(blankCd, true, 359698), "Audio preflight forgot the first-track gap");
    MacDiscRecorder.ValidateMedia(media, true, 359849);
    Reject(() => MacDiscRecorder.ValidateMedia(media with { Blank = false }, true, 1), "Accepted recorded media");
    Reject(() => MacDiscRecorder.ValidateMedia(media, true, 360000), "Accepted over-capacity image");
    Reject(() => MacDiscRecorder.ValidateMedia(media with { MediaClass = "DVD" }, true, 1), "Accepted audio on DVD");
    Reject(() => MacDiscRecorder.ParseMedia("<statusdoc/>", "1"), "Accepted missing media");
    var burnUpdates = new List<MacBurnProgress>();
    var burnDiagnostics = new List<string>();
    using (var terminal = new StringReader("Burning Audio Disc: fixture\nPreparing...\nWriting track 1 ... 38% "
        + "\b \b\b \b\b \b9% \r                     \rClosing... (this might take a while)"
        + "\rVerifying... [=====] 96% \nBurn completed.\n"))
        await MacDiscRecorder.ReadBurnOutputAsync(terminal, burnDiagnostics.Add, burnUpdates.Add);
    Check(burnUpdates.SequenceEqual([
        new(MacBurnStage.Preparing, null), new(MacBurnStage.Writing, 38), new(MacBurnStage.Writing, 39),
        new(MacBurnStage.Closing, null), new(MacBurnStage.Verifying, 96)]),
        "Burn progress must follow backspace updates and preserve the current stage");
    Check(burnDiagnostics.SequenceEqual(["Burning Audio Disc: fixture", "Burn completed."]),
        "Burn output must preserve diagnostic and completion lines");
    if (OperatingSystem.IsMacOS())
    {
        string finish = Path.Combine(work, "finish terminal fixture");
        var liveUpdate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string fixture = """
            test -t 0 && test -t 1 && test "$(/bin/stty size)" = "24 80" || exit 31
            printf 'Writing track 1 ... [=======] 38%% '
            for second in 1 2 3 4 5 6; do
                if test -f "$1"; then
                    printf '\rClosing... (this might take a while)\nBurn completed.\n'
                    exit 0
                fi
                sleep 1
            done
            exit 32
            """;
        Task burn = MacDiscRecorder.RunBurnToolAsync("/bin/sh", ["-c", fixture, "fixture", finish], _ => { },
            update => { if (update is { Stage: MacBurnStage.Writing, Percent: 38 }) liveUpdate.TrySetResult(true); });
        try
        {
            Task first = await Task.WhenAny(liveUpdate.Task, burn, Task.Delay(TimeSpan.FromSeconds(5)));
            Check(first == liveUpdate.Task && !burn.IsCompleted, "The real terminal transport must report progress before exit");
        }
        finally
        {
            File.WriteAllText(finish, "");
            await burn;
        }

        bool failed = false;
        try
        {
            await MacDiscRecorder.RunBurnToolAsync("/bin/sh", ["-c", "printf 'Burn completed.\\n'; exit 29"],
                _ => { }, _ => { });
        }
        catch (IOException e) { failed = e.Message.Contains("(29)", StringComparison.Ordinal); }
        Check(failed, "The terminal transport must propagate a nonzero child exit even if it printed completion");
    }
    var target = CalibrationTarget.Create(new(CalibrationDiscKind.Cd, 24.5, 56.8, 359849, 1200));
    var map = CalibrationCrossPrediction.CreateMapping(target, target.Parameters);
    var trace = Enumerable.Range(0, 24).Select(i => map(25 + i * 1.3)).ToArray();
    var fit = CalibrationCrossFitter.Fit(target, [new(0, trace)], new(AutoRange: true));
    Check(fit.Succeeded && fit.RmsErrorMm < 0.01, "Core calibration failed from portable shell");
    var record = new CalibrationSession(target, [], Photo: new(Convert.ToBase64String(File.ReadAllBytes(input)),
        Enumerable.Range(0, 8).Select(i => new CalibrationPoint(64 + 60 * Math.Cos(i * Math.PI / 4), 64 + 60 * Math.Sin(i * Math.PI / 4))).ToArray(),
        Enumerable.Range(0, 8).Select(i => new CalibrationPoint(64 + 7.5 * Math.Cos(i * Math.PI / 4), 64 + 7.5 * Math.Sin(i * Math.PI / 4))).ToArray(),
        60, true, [new(0, trace)]));
    Check(CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(record)).Photo!.Traces[0].Points.Count == 24, "Calibration session not interoperable");
    if (OperatingSystem.IsMacOS()) Check((await MacDiscRecorder.ListAsync()).Count >= 0, "Native recorder discovery failed");
    session.CleanCache();

    // Headless rendering and navigation use the real app, templates and Skia renderer.
    AppBuilder.Configure<DesktopApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
    var window = new MainWindow(); window.Show(); window.Session.Create("cd", "UI test", new(24.5, 56.8, 359849));
    window.Session.Update(window.Session.Document! with { Layers = [new("ui", "Fixture", input, true, 0, 0, 0, 1)] });
    foreach (string page in new[] { "主页", "设计", "预览", "导出", "刻录", "标定" })
    {
        window.Navigate(page); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        using var bitmap = new RenderTargetBitmap(new PixelSize(1280, 850), new Vector(96, 96)); bitmap.Render(window);
        string path = Path.Combine(work, "ui-" + page + ".png"); bitmap.Save(path);
        Check(new FileInfo(path).Length > 5000, "Empty UI page: " + page);
    }
    window.Navigate("设计");
    window.UpdateLayout();
    window.GetVisualDescendants().OfType<ListBox>().Single().SelectedIndex = 0;
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();
    var canvas = window.GetVisualDescendants().OfType<DiscCanvas>().Single();
    var center = new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2);
    var sample = center + new Vector(30, 100);
    SKColor CanvasPixel()
    {
        using var frame = new RenderTargetBitmap(PixelSize.FromSize(canvas.Bounds.Size, 1), new Vector(96, 96));
        frame.Render(canvas);
        using var png = new MemoryStream();
        frame.Save(png);
        png.Position = 0;
        using var image = SKBitmap.Decode(png);
        return image.GetPixel((int)sample.X, (int)sample.Y);
    }
    var start = canvas.TranslatePoint(center, window)!.Value;
    var end = start + new Vector(40, 0);
    var originalPixel = CanvasPixel();
    window.MouseDown(start, MouseButton.Left);
    window.MouseMove(end, RawInputModifiers.LeftMouseButton);
    Dispatcher.UIThread.RunJobs();
    var draggedPixel = CanvasPixel();
    Check(originalPixel.Red < 20 && draggedPixel.Red > 230 && draggedPixel.Green < 20,
        $"Canvas must move the image before pointer release: {originalPixel} → {draggedPixel}");
    window.MouseUp(end, MouseButton.Left);
    window.MouseWheel(end, new Vector(0, -10));
    Dispatcher.UIThread.RunJobs();
    var scaledPixel = CanvasPixel();
    Check(scaledPixel.Green > 180 && window.Session.Document!.Layers[0].Scale < 0.5,
        "Wheel zoom must update the rendered layer and project on the same input");

    Button FindAction(string title) => window.GetVisualDescendants().OfType<Button>()
        .Single(button => AutomationProperties.GetName(button) == title);
    var taskLog = FindAction("任务日志");
    new ButtonAutomationPeer(taskLog).Invoke();
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();
    var brightness = window.GetVisualDescendants().OfType<NumericUpDown>().Single(field => field.Maximum == 64);
    var scroll = brightness.GetVisualAncestors().OfType<ScrollViewer>().Single();
    brightness.BringIntoView();
    brightness.GetVisualDescendants().OfType<TextBox>().Single().Focus();
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();
    double scrollBeforeWheel = scroll.Offset.Y;
    var brightnessValue = brightness.Value;
    var wheelPoint = brightness.TranslatePoint(new Rect(brightness.Bounds.Size).Center, window)!.Value;
    window.MouseWheel(wheelPoint, new Vector(0, -1));
    Dispatcher.UIThread.RunJobs();
    Check(scroll.Offset.Y > scrollBeforeWheel && brightness.Value == brightnessValue,
        "Scrolling over a focused tone control must move the page without changing the tone");
    scroll.ScrollToEnd();
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();
    var lastAction = FindAction("下一步：预览");
    double lastBottom = lastAction.TranslatePoint(new Point(0, lastAction.Bounds.Height), window)!.Value.Y;
    double scrollBottom = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window)!.Value.Y;
    Check(lastBottom <= scrollBottom - 20, "Last design action must scroll fully above the open job dock");

    var progressBar = window.GetVisualDescendants().OfType<ProgressBar>().Single();
    var log = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.IsReadOnly);
    bool ShowsBurnStatus(string status) => window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == status);
    await window.RunBurnWork(report =>
    {
        report(new(MacBurnStage.Writing, 38));
        Check(!progressBar.IsIndeterminate && progressBar.Value == 38 && ShowsBurnStatus("正在写入盘片 · 38%")
            && log.Text!.Contains("正在写入盘片 · 38%"), "Burn percent must reach both the status bar and the log");
        report(new(MacBurnStage.Closing, 100));
        Check(progressBar.Value == 99 && ShowsBurnStatus("正在封盘 · 99%"), "Burn must not show 100% before completion");
        return Task.CompletedTask;
    });
    Check(progressBar.Value == 100 && ShowsBurnStatus("刻录完成 · 100%") && log.Text!.Contains("刻录完成 · 100%"),
        "A successful burn must report 100% in both places");

    window.Navigate("标定");
    window.UpdateLayout();
    var photoCanvas = window.GetVisualDescendants().OfType<CalibrationCanvas>().Single();
    photoCanvas.SetImage(input);
    Dispatcher.UIThread.RunJobs();
    var photoCenter = photoCanvas.TranslatePoint(new Rect(photoCanvas.Bounds.Size).Center, window)!.Value;
    window.MouseDown(photoCenter, MouseButton.Left);
    window.MouseUp(photoCenter, MouseButton.Left);
    Check(photoCanvas.Points.Count == 1 && Math.Abs(photoCanvas.Points[0].X - 64) < .5, "A photo click must add a point");
    var movedPoint = photoCenter + new Vector(30, 0);
    window.MouseDown(photoCenter, MouseButton.Left);
    window.MouseMove(movedPoint, RawInputModifiers.LeftMouseButton);
    window.MouseUp(movedPoint, MouseButton.Left);
    var pointBeforePan = photoCanvas.Points[0];
    Check(pointBeforePan.X > 64, "Dragging an existing calibration point must update it");
    var panStart = photoCenter - new Vector(30, 0);
    var panEnd = panStart + new Vector(45, 0);
    window.MouseDown(panStart, MouseButton.Left);
    window.MouseMove(panEnd, RawInputModifiers.LeftMouseButton);
    window.MouseUp(panEnd, MouseButton.Left);
    Check(photoCanvas.Points.Count == 1 && photoCanvas.Points[0] == pointBeforePan,
        "Dragging unmarked photo space must pan without changing the calibration points");
    window.MouseDown(photoCenter, MouseButton.Left);
    window.MouseUp(photoCenter, MouseButton.Left);
    Check(photoCanvas.Points.Count == 2 && photoCanvas.Points[1].X < 60,
        "A primary drag must move the photo and change the coordinate under the cursor");
    var calibrationScroll = photoCanvas.GetVisualAncestors().OfType<ScrollViewer>().Single();
    double calibrationOffset = calibrationScroll.Offset.Y;
    window.MouseWheel(photoCenter, new Vector(0, -1), RawInputModifiers.Shift);
    Dispatcher.UIThread.RunJobs();
    Check(calibrationScroll.Offset.Y > calibrationOffset, "Shift-wheel over the photo must scroll the calibration page");

    var more = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "更多操作");
    new ButtonAutomationPeer(more).Invoke(); Dispatcher.UIThread.RunJobs();
    Check(more.Flyout is MenuFlyout { IsOpen: true }, "More button did not expose Save As/project settings");
    more.Flyout!.Hide();
    window.Session.Create("cd", "Calibration regression", new(24.9, 57.9, 160));
    string calibrationTrack = Path.Combine(work, "calibration.wav");
    Task calibrationTask = window.GenerateCalibration(calibrationTrack);
    var calibrationDeadline = DateTime.UtcNow.AddSeconds(30);
    while (!calibrationTask.IsCompleted && DateTime.UtcNow < calibrationDeadline)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(10);
    }
    Check(calibrationTask.IsCompleted, "Calibration generation did not finish");
    calibrationTask.GetAwaiter().GetResult();
    var generatedTarget = CalibrationSessionJson.Load(calibrationTrack + ".calibration.json").Target;
    Check(generatedTarget.Artwork == CalibrationArtwork.ReferenceCross,
        "Calibration records must identify the reference artwork actually encoded by the desktop");
    Check(CdTrackMetadata.TryLoad(calibrationTrack)?.ImageOuterRadiusMm == generatedTarget.Parameters.OuterRadiusMm,
        "Calibration source scaling differs between the encoded track and the saved target");
    string referenceTrack = Path.Combine(work, "reference.wav");
    CdTrackGenerator.GeneratePattern(generatedTarget, referenceTrack, window.Session.Document!.Outputs.Interleave);
    Check(File.ReadAllBytes(calibrationTrack).AsSpan().SequenceEqual(File.ReadAllBytes(referenceTrack)),
        "Desktop calibration track differs from the saved target's core encoder output");
    Console.WriteLine($"Portable desktop: {checks} checks passed.");
    window.CloseForTest();
}
finally { Directory.Delete(work, recursive: true); }
