using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DiscImageStudio;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Desktop;
using DiscImageStudio.ImageIO;
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
        var doc = session.Document!;
        string output = Path.Combine(work, family == "cd" ? "track.wav" : family + ".iso");
        string[] commandArgs = JobArguments.Generate(doc, input, output);
        var result = await ConsoleModuleRunner.Catalog.ExecuteAsync(new(commandArgs[0], commandArgs.Skip(1)));
        Check(result.Succeeded, family + " generation failed");
        Check(new FileInfo(output).Length == sectors * (family == "cd" ? 2352 : 2048) + (family == "cd" ? 44 : 0), family + " wrong output length");
        if (family == "cd")
        {
            Check(File.ReadAllText(Path.ChangeExtension(output, ".cue")).Contains("FILE \"track.wav\" WAVE"), "Wrong WAV CUE");
            string aiff = Path.Combine(work, "track.aiff");
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
    MacDiscRecorder.ValidateMedia(media, true, 359849);
    Reject(() => MacDiscRecorder.ValidateMedia(media with { Blank = false }, true, 1), "Accepted recorded media");
    Reject(() => MacDiscRecorder.ValidateMedia(media, true, 360000), "Accepted over-capacity image");
    Reject(() => MacDiscRecorder.ValidateMedia(media with { MediaClass = "DVD" }, true, 1), "Accepted audio on DVD");
    Reject(() => MacDiscRecorder.ParseMedia("<statusdoc/>", "1"), "Accepted missing media");
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
    Console.WriteLine($"Portable desktop: {checks} checks passed.");
    window.CloseForTest();
}
finally { Directory.Delete(work, recursive: true); }
