using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.Linux;
using DiscImageStudio.Linux.Services;
using DiscImageStudio.Linux.ViewModels;
using SkiaSharp;

namespace DiscImageStudio.LinuxTests;

internal static partial class Program
{
    private static int _failures;
    private static int _passes;

    private static int Main()
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        // The checks run on the thread pool while this, the UI thread, pumps Avalonia's
        // dispatcher; view-model checks hop onto it with Dispatcher.UIThread.InvokeAsync.
        using CancellationTokenSource finished = new();
        Task<int> run = Task.Run(RunAllAsync);
        run.ContinueWith(_ => finished.Cancel(), TaskScheduler.Default);
        Dispatcher.UIThread.MainLoop(finished.Token);
        return run.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAllAsync()
    {
        string work = Path.Combine(Path.GetTempPath(), "dis-linux-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string image = CreateTestImage(Path.Combine(work, "input image.png"));
            string files = Path.Combine(work, "files");
            Directory.CreateDirectory(files);
            File.WriteAllText(Path.Combine(files, "hello.txt"), "Hello from Linux\n");

            Run("presets are translated and cover every family", PresetsTranslated);
            Run("compositor places and rotates layers like the Windows composer", () => CompositorGeometry(work));
            Run("generate commands match the Windows export page", CommandShapes);
            await RunAsync("CD audio track generation", () => GenerateCdAudio(work, image));
            await RunAsync("CD data image generation with files", () => GenerateCdData(work, image, files));
            await RunAsync("DVD image generation", () => GenerateDvd(work, image, null));
            await RunAsync("DVD hybrid image generation", () => GenerateDvd(work, image, files));
            await RunAsync("BD-R image generation", () => GenerateBluRay(work, image, null));
            await RunAsync("BD-R hybrid image generation", () => GenerateBluRay(work, image, files));
            Run("projection previews for every family", () => Projections(work, image, files));
            Run("project round trip keeps every field", () => ProjectRoundTrip(work, image));
            Run("legacy project without tone opens with the hard threshold", () => LegacyToneProject(work));
            Run("project setup validates geometry", SetupValidation);
            Run("burn commands follow the media rules", BurnCommands);
            Run("burn progress parsing", BurnProgress);
            Run("shell quoting", ShellQuoting);
            Run("calibration messages are in English", CalibrationMessages);
            Run("calibration targets follow the project geometry", CalibrationTargets);
            await RunAsync("calibration target files for every family", () => CalibrationFiles(work));
            await RunAsync("calibration from a photo recovers the disc geometry", () => OnUi(() => CalibrationEndToEnd(work)));
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        Console.WriteLine($"\n{_passes} passed, {_failures} failed");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passes++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failures++;
            Console.WriteLine($"FAIL {name}: {exception}");
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passes++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failures++;
            Console.WriteLine($"FAIL {name}: {exception}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string what)
        => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected {expected}, got {actual}");

    private static string CreateTestImage(string path)
    {
        using SKBitmap bitmap = new(320, 200);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.White);
            using SKPaint black = new() { Color = SKColors.Black };
            canvas.DrawRect(0, 0, 160, 200, black);
            canvas.DrawCircle(240, 100, 60, black);
        }

        using FileStream stream = File.Create(path);
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        return path;
    }

    private static DiscSettings Settings(OpticalDiscFamily family, long sectors, double inner, double outer, string? files = null)
        => new(family, inner, outer, sectors, 0, 0, 0, ToneProfile.Photo, false, files ?? string.Empty, string.Empty, false, true, true);

    private static async Task<DiscJobResult> Generate(DiscSettings settings, string image, string output)
    {
        string[] arguments = settings.BuildGenerateCommand(image, output);
        List<string> log = [];
        DiscJobResult result = await EngineJobRunner.RunAsync(arguments, log.Add, _ => { }, CancellationToken.None);
        Assert(result.Succeeded, $"engine failed: {result.Summary}\n{string.Join('\n', log)}");
        return result;
    }

    private static void PresetsTranslated()
    {
        IReadOnlyList<DiscPreset> presets = PresetCatalog.All;
        foreach (OpticalDiscFamily family in Enum.GetValues<OpticalDiscFamily>())
        {
            Assert(presets.Any(p => p.Family == family), $"no {family} preset");
        }

        foreach (DiscPreset preset in presets.Where(p => p.IsBuiltIn))
        {
            Assert(!preset.Name.Any(c => c >= 0x3000), $"preset name not translated: {preset.Name}");
        }
    }

    private static void CompositorGeometry(string work)
    {
        string red = Path.Combine(work, "red.png");
        using (SKBitmap bitmap = new(200, 100))
        {
            bitmap.Erase(SKColors.Red);
            using FileStream stream = File.Create(red);
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        }

        // 60 mm canvas radius on 600 px: 5 px/mm. A 40×20 mm layer, 10 mm right of centre.
        string output = Path.Combine(work, "composite.png");
        Compositor.Render([new LayerRenderItem(red, 40, 20, 10, 0, 0, 1)], 60, 600, output);
        using SKBitmap plain = SKBitmap.Decode(output);
        Equal(600, plain.Width, "width");
        Equal(SKColors.Red, plain.GetPixel(350, 300), "layer centre");
        Equal((byte)0, plain.GetPixel(300 + 50 + 105, 300).Alpha, "right of the layer");
        Equal((byte)0, plain.GetPixel(350, 300 - 55).Alpha, "above the layer");

        // Rotated 90° clockwise, the layer stands upright: 20 mm wide, 40 mm tall.
        Compositor.Render([new LayerRenderItem(red, 40, 20, 0, 0, 90, 1)], 60, 600, output);
        using SKBitmap rotated = SKBitmap.Decode(output);
        Equal(SKColors.Red, rotated.GetPixel(300, 300 - 90), "rotated top");
        Equal((byte)0, rotated.GetPixel(300 + 60, 300).Alpha, "rotated side");

        // Topmost-first order: the first layer covers the second.
        string blue = Path.Combine(work, "blue.png");
        using (SKBitmap bitmap = new(10, 10))
        {
            bitmap.Erase(SKColors.Blue);
            using FileStream stream = File.Create(blue);
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        }

        Compositor.Render([new LayerRenderItem(blue, 10, 10, 0, 0, 0, 1), new LayerRenderItem(red, 40, 20, 0, 0, 0, 1)], 60, 600, output);
        using SKBitmap stacked = SKBitmap.Decode(output);
        Equal(SKColors.Blue, stacked.GetPixel(300, 300), "top layer wins");
    }

    private static void CommandShapes()
    {
        string[] cd = Settings(OpticalDiscFamily.CompactDisc, 359849, 24.5, 56.8).BuildGenerateCommand("in.png", "out.wav");
        Equal("cd-generate", cd[0], "CD command");
        Assert(cd.Contains("--interleave") && cd.Contains("--cue"), "CD audio switches");
        Assert(!cd.Contains("--data-dir"), "CD audio has no data dir");

        string[] cdData = Settings(OpticalDiscFamily.CompactDisc, 359849, 24.5, 56.8, "/tmp/files").BuildGenerateCommand("in.png", "out.iso");
        Assert(cdData.Contains("--data-dir") && cdData.Contains("--volume-label") && !cdData.Contains("--interleave"), "CD data switches");

        string[] dvd = Settings(OpticalDiscFamily.Dvd, 2295104, 24, 58).BuildGenerateCommand("in.png", "out.iso");
        Equal("solve", dvd[0], "DVD command");
        string Value(string[] args, string key) => args[Array.IndexOf(args, key) + 1];
        Equal("2", Value(dvd, "--tone-levels"), "DVD is binary");
        Equal("2295104", Value(dvd, "--fill-sectors"), "DVD fills the disc without files");
        Equal("dispersion", Value(dvd, "--algorithm"), "DVD algorithm");

        DiscSettings fourLevelBd = Settings(OpticalDiscFamily.BluRay, 12219392, 24, 58) with { Tone = new ToneProfile(0.5, 4, 3) };
        string[] bd = fourLevelBd.BuildGenerateCommand("in.png", "out.iso");
        Equal("bd-generate", bd[0], "BD command");
        Equal("2", Value(bd, "--tone-levels"), "BD is binary");
        Equal("BD_IMAGE", Value(bd, "--volume-label"), "BD default label");
    }

    private static async Task GenerateCdAudio(string work, string image)
    {
        string output = Path.Combine(work, "track.wav");
        DiscSettings settings = Settings(OpticalDiscFamily.CompactDisc, 2, 24.5, 56.8);
        await Generate(settings, image, output);
        Equal(44L + (2 * 2352), new FileInfo(output).Length, "WAV size");
        Assert(File.ReadAllText(Path.Combine(work, "track.cue")).Contains("FILE \"track.wav\" WAVE"), "CUE references the WAV");

        string png = Path.Combine(work, "track-readback.png");
        PreviewInfo info = await PreviewRenderer.RenderReadbackAsync(settings, output, png, 256, _ => { }, CancellationToken.None);
        AssertPng(info.Path, 256);
    }

    private static async Task GenerateCdData(string work, string image, string files)
    {
        string output = Path.Combine(work, "cd-data.iso");
        DiscSettings settings = Settings(OpticalDiscFamily.CompactDisc, 1200, 24.5, 56.8, files);
        await Generate(settings, image, output);
        Equal(1200L * 2048, new FileInfo(output).Length, "CD ISO size");
        Assert(PreviewRenderer.FileBands(settings).Count > 0, "CD file band");
        PreviewInfo info = await PreviewRenderer.RenderReadbackAsync(settings, output, Path.Combine(work, "cd-data.png"), 256, _ => { }, CancellationToken.None);
        AssertPng(info.Path, 256);
    }

    private static async Task GenerateDvd(string work, string image, string? files)
    {
        long sectors = files is null ? 160 : 3200;
        string output = Path.Combine(work, files is null ? "dvd.iso" : "dvd-hybrid.iso");
        DiscSettings settings = Settings(OpticalDiscFamily.Dvd, sectors, 24, 58, files);
        await Generate(settings, image, output);
        Equal(sectors * 2048, new FileInfo(output).Length, "DVD ISO size");
        if (files is not null)
        {
            Assert(PreviewRenderer.FileBands(settings).Count > 0, "DVD file band");
        }

        List<string> log = [];
        PreviewInfo info = await PreviewRenderer.RenderReadbackAsync(settings, output, output + ".png", 256, log.Add, CancellationToken.None);
        AssertPng(info.Path, 256);
    }

    private static async Task GenerateBluRay(string work, string image, string? files)
    {
        long sectors = files is null ? 1024 : 4096;
        string output = Path.Combine(work, files is null ? "bd.iso" : "bd-hybrid.iso");
        DiscSettings settings = Settings(OpticalDiscFamily.BluRay, sectors, 24, 58, files);
        await Generate(settings, image, output);
        Equal(sectors * 2048, new FileInfo(output).Length, "BD ISO size");
        Assert(File.Exists(output + ".json"), "BD sidecar");
        if (files is not null)
        {
            Assert(PreviewRenderer.FileBands(settings).Count > 0, "BD file band");
        }

        PreviewInfo info = await PreviewRenderer.RenderReadbackAsync(settings, output, output + ".png", 256, _ => { }, CancellationToken.None);
        AssertPng(info.Path, 256);
    }

    private static void Projections(string work, string image, string files)
    {
        DiscSettings[] cases =
        [
            Settings(OpticalDiscFamily.CompactDisc, 359849, 24.5, 56.8),
            Settings(OpticalDiscFamily.CompactDisc, 359849, 24.5, 56.8, files),
            Settings(OpticalDiscFamily.Dvd, 2295104, 24, 58),
            Settings(OpticalDiscFamily.BluRay, 12219392, 24, 58),
            Settings(OpticalDiscFamily.BluRay, 12219392, 24, 58, files),
        ];
        int index = 0;
        foreach (DiscSettings settings in cases)
        {
            string output = Path.Combine(work, $"projection-{index++}.png");
            PreviewInfo info = PreviewRenderer.RenderProjection(settings, image, output, 200, 4, null, null, CancellationToken.None);
            AssertPng(info.Path, 200);
            Assert(info.InnerRadiusMm == settings.InnerRadiusMm && info.OuterRadiusMm == settings.OuterRadiusMm, "ring radii");
            Assert(settings.HasFiles == info.FileBands.Count > 0, "file bands only with files");
            PreviewInfo measured = PreviewRenderer.RenderProjection(settings, image, output, 200, 4,
                settings.InnerRadiusMm - 0.2, settings.OuterRadiusMm + 0.3, CancellationToken.None);
            Equal(settings.OuterRadiusMm + 0.3, measured.OuterRadiusMm, "measured outer radius");
        }
    }

    private static void AssertPng(string path, int size)
    {
        using SKCodec codec = SKCodec.Create(path) ?? throw new InvalidOperationException($"not an image: {path}");
        Equal(size, codec.Info.Width, $"{Path.GetFileName(path)} width");
    }

    private static void ProjectRoundTrip(string work, string image)
    {
        ProjectDocument created = ProjectViewModel.CreateDocument("Round trip", OpticalDiscFamily.Dvd, 23.99, 57.96, 2297888,
            -3.9e-05, 6.9e-05, -4.6e-05, false, Path.Combine(work, "round.iso"));
        ProjectViewModel project = new(created, null);
        LayerViewModel layer = project.AddImage(image);
        layer.TranslateXMm = 1.5;
        layer.TranslateYMm = -2.25;
        layer.RotationDeg = 33;
        layer.Scale = 0.8;
        layer.Name = "Picture";
        project.DitherPercent = 40;
        project.Brightness = -12;
        project.SrgbLinearization = true;
        project.DataDirectory = work;
        project.FilesAtOuterRing = true;
        project.VolumeLabel = "MY_DISC";
        Assert(project.IsDirty, "edits mark the project dirty");

        string path = Path.Combine(work, "round.discproj");
        project.Save(path);
        Assert(!project.IsDirty, "saving clears dirty");
        ProjectDocument loaded = ProjectJsonStore.Load(path);
        Equal("dvd", loaded.DiscFamily, "family");
        Equal(2297888L, loaded.Geometry.Sectors, "sectors");
        Equal(-3.9e-05, loaded.Geometry.PitchLinear, "pitch");
        Equal(1, loaded.Layers.Count, "layers");
        Equal(1.5, loaded.Layers[0].TranslateXMm, "x");
        Equal(-2.25, loaded.Layers[0].TranslateYMm, "y");
        Equal(33.0, loaded.Layers[0].RotationDeg, "rotation");
        Equal(0.8, loaded.Layers[0].Scale, "scale");
        Equal("Picture", loaded.Layers[0].Name, "layer name");
        Equal(0.4, loaded.Tone!.Value.DitherStrength, "dither");
        Equal(-12.0, loaded.Tone!.Value.Brightness, "brightness");
        Assert(loaded.Tone!.Value.SrgbLinearization, "gamma");
        Equal(2, loaded.Tone!.Value.Levels, "DVD levels");
        Assert(loaded.Outputs.FilesAtOuterRing, "outer files");
        Equal("MY_DISC", loaded.Outputs.VolumeLabel, "label");
        Assert(loaded.ThumbnailPngBase64 is { Length: > 100 }, "thumbnail written for the Windows home page");
        Assert(loaded.BluRay is null, "no BD section on DVD");

        // The JSON is camelCase and strict, as the Windows app expects.
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        Assert(json.RootElement.TryGetProperty("discFamily", out _), "camelCase fields");

        ProjectViewModel reopened = new(loaded, path);
        reopened.LoadImages();
        Assert(reopened.Layers[0].HasImage, "image reloads");
        Equal(2 * 57.96, Math.Max(reopened.Layers[0].BaseWidthMm, reopened.Layers[0].BaseHeightMm), "fit rule");
        Assert(!reopened.IsDirty, "opening does not dirty");
    }

    private static void LegacyToneProject(string work)
    {
        ProjectDocument legacy = ProjectViewModel.CreateDocument("Legacy", OpticalDiscFamily.CompactDisc, 24.5, 56.8, 359849,
            0, 0, 0, false, Path.Combine(work, "legacy.wav")) with { Tone = null };
        ProjectViewModel project = new(legacy, null);
        Assert(project.Tone.IsLegacyBinary, "missing tone keeps the historical threshold");
    }

    private static void SetupValidation()
    {
        ProjectSetupViewModel setup = new(isEditMode: false, Path.GetTempPath()) { Family = OpticalDiscFamily.Dvd };
        Assert(setup.SelectedPreset is not null && setup.InnerText.Length > 0, "DVD preset fills geometry");
        setup.SectorsText = "2295105";
        Equal("custom", setup.SelectedPreset!.Id, "manual edit switches to custom");
        setup.ConfirmCommand.Execute(null);
        Assert(!setup.Accepted && setup.ErrorMessage is not null, "DVD sectors must be a multiple of 16");

        setup.Family = OpticalDiscFamily.BluRay;
        setup.SectorsText = "12219424";
        setup.ConfirmCommand.Execute(null);
        Assert(!setup.Accepted && setup.ErrorMessage!.Contains("exceed"), "BD single-layer limit");

        setup.SectorsText = "12219392";
        setup.ConfirmCommand.Execute(null);
        Assert(setup.Accepted, $"valid BD geometry accepted: {setup.ErrorMessage}");
        Equal(12219392L, setup.Sectors, "parsed sectors");
    }

    private static void BurnCommands()
    {
        BurnTool growisofs = new("growisofs", "growisofs", "/usr/bin/growisofs");
        BurnTool cdrecord = new("cdrecord", "cdrecord", "/usr/bin/cdrecord");
        BurnTool xorriso = new("xorriso", "xorriso", "/usr/bin/xorriso");

        BurnCommand bd = BurnService.Build(BurnMaterial.BluRay, growisofs, "/dev/sr0", "/x/bd.iso", 2, simulate: false, eject: true);
        Assert(bd.Arguments.Contains("-use-the-force-luke=spare:none"), "BD-R stays unformatted");
        Assert(bd.Arguments.Contains("-Z") && bd.Arguments.Contains("/dev/sr0=/x/bd.iso"), "growisofs target");
        Assert(bd.Arguments.Contains("-speed=2"), "speed");

        BurnCommand dvd = BurnService.Build(BurnMaterial.Dvd, growisofs, "/dev/sr0", "/x/dvd.iso", null, simulate: true, eject: false);
        Assert(dvd.Arguments.Contains("-dvd-compat") && dvd.Arguments.Contains("-use-the-force-luke=dummy"), "DVD flags");
        Assert(!dvd.Arguments.Any(a => a.StartsWith("-speed", StringComparison.Ordinal)), "auto speed");

        BurnCommand audio = BurnService.Build(BurnMaterial.CdAudio, cdrecord, "/dev/sr1", "/x/t.wav", 8, simulate: false, eject: true);
        Assert(audio.Arguments.Contains("-audio") && !audio.Arguments.Contains("-swab"), "CD audio without -swab");
        Assert(audio.Arguments.Contains("dev=/dev/sr1") && audio.Arguments.Contains("speed=8") && audio.Arguments.Contains("-eject"), "cdrecord options");
        Equal("/x/t.wav", audio.Arguments[^1], "track last");

        BurnCommand data = BurnService.Build(BurnMaterial.CdData, cdrecord, "/dev/sr0", "/x/d.iso", null, simulate: true, eject: false);
        Assert(data.Arguments.Contains("-dao") && data.Arguments.Contains("-data") && data.Arguments.Contains("-nopad") && data.Arguments.Contains("-dummy"), "CD data flags");

        BurnCommand xorrisoBd = BurnService.Build(BurnMaterial.BluRay, xorriso, "/dev/sr0", "/x/bd.iso", 4, simulate: false, eject: false);
        Assert(xorrisoBd.Arguments.Contains("-nopad") && !xorrisoBd.Arguments.Contains("-dao") && xorrisoBd.Arguments.Contains("speed=4b"), "xorriso BD");

        Assert(BurnService.MaterialFor(OpticalDiscFamily.CompactDisc, "a.raw") == BurnMaterial.CdAudio, "raw is audio");
        Assert(BurnService.MaterialFor(OpticalDiscFamily.Dvd, "a.wav") is null, "DVD needs an ISO");
    }

    private static void BurnProgress()
    {
        static double Parse(string line) => BurnService.TryParseProgress(line, out double value) ? value : -1;
        Equal(0.5, Parse("Track 01:  350 of  700 MB written (fifo 100%) [buf  99%]  16.0x."), "cdrecord");
        Equal(0.25, Parse(" 1175093248/4700372992 (25.0%) @3.9x, remaining 4:51 RBU 100.0% UBU  99.6%"), "growisofs");
        Equal(0.027, Math.Round(Parse("xorriso : UPDATE : Writing:     12384s    2.7%   fifo 100%  buf  50%   10.2xD"), 3), "xorriso");
        Equal(-1.0, Parse("Starting to write CD/DVD at speed 16.0 in real TAO mode for single session."), "plain line");
    }

    private static void ShellQuoting()
    {
        Equal("cdrecord dev=/dev/sr0 '/home/me/My Disc.wav'",
            EngineJobRunner.ToShellCommand("cdrecord", ["dev=/dev/sr0", "/home/me/My Disc.wav"]), "spaces quoted");
        Equal("x 'it'\\''s'", EngineJobRunner.ToShellCommand("x", ["it's"]), "single quote");
    }
}
