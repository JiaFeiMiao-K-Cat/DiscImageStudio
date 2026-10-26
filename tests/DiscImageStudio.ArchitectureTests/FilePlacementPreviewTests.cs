using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class FilePlacementPreviewTests
{
    private static readonly Color Yellow = Color.FromRgb(250, 204, 21);
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    internal static void Run()
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try { RunOnSta(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        Console.WriteLine("file-placement-preview: passed (inner/outer projection and readback, exact cubic bounds, yellow file regions, missing metadata, invalid folders, stale state)");
    }

    private static void RunOnSta()
    {
        string directory = Path.Combine(Path.GetTempPath(), "file-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        ShellViewModel? shell = null;
        try
        {
            string source = Path.Combine(directory, "source.png");
            WritePng(source, 128, Colors.Black);
            string files = Path.Combine(directory, "files");
            Directory.CreateDirectory(files);
            File.WriteAllBytes(Path.Combine(files, "payload.bin"), new byte[48 * 2048]);
            VerifyCubicGeometry(directory);
            VerifyReadbackRange(directory);

            shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
            VerifyCdPreview(shell, directory, source, files);
            VerifyDvdPreview(shell, directory, source, files);
            VerifyBluRayPreview(shell, directory, source, files);

            LivePreviewViewModel preview = shell.LivePreview;
            preview.HasFileRegionOverlay = true;
            preview.SelectedFamily = OpticalDiscFamily.CompactDisc;
            Require(!preview.HasFileRegionOverlay && !preview.HasResultImage,
                "switching to CD clears the file legend and previous image");
            preview.ShowResultPreview(source);
            preview.HasFileRegionOverlay = true;
            preview.UpdateMode(clearResult: true);
            Require(!preview.HasFileRegionOverlay && !preview.HasResultImage && preview.ResultImage is null,
                "project/mode reset clears all preview state");
            preview.HasFileRegionOverlay = true;
            preview.PrepareResultPreview(source);
            Require(!preview.HasFileRegionOverlay, "new output preparation clears the file legend");
            preview.ShowResultPreview(source);
            string invalid = Path.Combine(directory, "invalid.png");
            File.WriteAllText(invalid, "invalid image");
            Invoke(preview, "PublishPreview", [invalid, source, "invalid preview", true]);
            Require(!preview.HasFileRegionOverlay, "failed image decoding cannot attach a file legend to the previous image");
        }
        finally
        {
            shell?.LivePreview.OnWindowClosed();
            SynchronizationContext.SetSynchronizationContext(previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyCdPreview(ShellViewModel shell, string directory, string source, string files)
    {
        DiscParametersState state = shell.State;
        LivePreviewViewModel preview = shell.LivePreview;
        preview.SelectedFamily = OpticalDiscFamily.CompactDisc;
        preview.IsCalibrationPreview = true;
        state.CdImagePath = source;
        state.CdSectors = "256";
        state.CdInnerRadius = state.CdActualInnerRadius = "24.5";
        state.CdOuterRadius = state.CdActualOuterRadius = "56.8";
        state.CdPreviewSize = "256";
        state.CdSamplesPerSector = "2";
        state.CdDataDirectory = files;
        CdDiscParameters disc = new(24.5, 56.8, 256);
        foreach (bool outer in new[] { false, true })
        {
            state.CdFilesAtOuterRing = outer;
            RunPreview(preview);
            RequirePreview(preview, true, $"CD {(outer ? "outer" : "inner")} design projection");
            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(files, disc, filesAtOuterRing: outer);
            BitmapSource projection = preview.ResultImage!;
            double drawingMiddle = disc.RadiusFromByte(
                ((long)plan.DrawingStartLba + plan.DrawingEndLbaExclusive) * CdDiscParameters.BytesPerSector / 2);
            double fileRadius = disc.RadiusFromByte((outer
                ? (long)plan.DrawingEndLbaExclusive + disc.Sectors
                : plan.DrawingStartLba) * CdDiscParameters.BytesPerSector / 2);
            Color SampleCd(double radius)
                => Sample(projection, (int)Math.Round(projection.PixelWidth / 2.0
                    + radius * projection.PixelWidth / (2 * disc.ImageOuterRadiusMm)), projection.PixelHeight / 2);
            Require(SampleCd(fileRadius) == Yellow, "CD overlay follows CD image radius and pixel centre");
            Require(SampleCd(drawingMiddle) != Yellow, "CD drawing retains its physical range");

            string iso = Path.Combine(directory, $"cd-{outer}.iso");
            CdDataImageGenerator.Generate(source, iso, disc, plan);
            state.CdFilesAtOuterRing = !outer;
            state.CdTrackPath = iso;
            Require(state.CdFilesAtOuterRing == outer, "CD sidecar restores file placement");
            Require(!preview.Inspector.IsCdAudioArtifact, "CD ISO readback hides audio interleave");
            preview.IsCalibrationPreview = false;
            RunPreview(preview);
            RequirePreview(preview, true, $"CD {(outer ? "outer" : "inner")} ISO readback");
            preview.IsCalibrationPreview = true;
        }

        CdDiscParameters customDisc = disc with
        {
            LinearVelocityMmPerSecond = 1300,
            StartAngleRadians = .7,
            ImageOuterRadiusMm = 61,
        };
        CdDataLayoutPlan customPlan = CdDataImageGenerator.PrepareLayout(files, customDisc);
        string customIso = Path.Combine(directory, "cd-custom-geometry.iso");
        CdDataImageGenerator.Generate(source, customIso, customDisc, customPlan);
        state.CdTrackPath = customIso;
        preview.IsCalibrationPreview = false;
        RunPreview(preview);
        RequirePreview(preview, true, "CD ISO custom CLI geometry readback");
        string customPreview = Path.Combine(directory, "cd-custom-expected.png");
        CdDataImageGenerator.PreviewTrack(customIso, customPreview, customDisc, 256, 1024);
        FileRegionPreviewOverlay.ApplyCd(customPreview, customDisc,
            customPlan.DrawingStartLba, customPlan.DrawingEndLbaExclusive);
        Require(Pixels(preview.ResultImage!).SequenceEqual(Pixels(ReadPng(customPreview))),
            "CD readback preserves sidecar velocity, start angle and image outer radius");
        preview.IsCalibrationPreview = true;

        state.CdDataDirectory = string.Empty;
        RunPreview(preview);
        RequirePreview(preview, false, "CD without files returns to audio projection");
        state.CdDataDirectory = Path.Combine(directory, "missing-cd-files");
        RunPreview(preview);
        Require(!preview.HasFileRegionOverlay && !preview.StatusText.StartsWith("已实时更新"),
            "missing CD folder cannot silently show audio projection");
        state.CdDataDirectory = files;
        state.CdSectors = "32";
        RunPreview(preview);
        Require(!preview.HasFileRegionOverlay && !preview.StatusText.StartsWith("已实时更新"),
            "CD capacity failure does not fall back to full-disc drawing");
        state.CdSectors = "256";
        preview.IsCalibrationPreview = false;
        File.WriteAllText(state.CdTrackPath + ".json", "invalid sidecar");
        RunPreview(preview);
        RequirePreview(preview, false, "CD ISO readback without valid layout metadata");
        state.CdTrackPath = Path.Combine(directory, "audio.wav");
        Require(preview.Inspector.IsCdAudioArtifact, "CD audio readback restores interleave controls");
    }

    private static void VerifyDvdPreview(ShellViewModel shell, string directory, string source, string files)
    {
        DiscParametersState state = shell.State;
        LivePreviewViewModel preview = shell.LivePreview;
        preview.SelectedFamily = OpticalDiscFamily.Dvd;
        preview.IsCalibrationPreview = true;
        state.DvdImagePath = source;
        state.DvdTotalSectors = "256";
        state.DvdInnerRadius = state.DvdActualInnerRadius = "24";
        state.DvdOuterRadius = state.DvdActualOuterRadius = "58";
        state.DvdPitchLinear = state.DvdActualPitchLinear = "0";
        state.DvdPitchQuadratic = state.DvdActualPitchQuadratic = "0";
        state.DvdPitchCubic = state.DvdActualPitchCubic = "2";
        state.DvdPreviewSize = "256";
        state.DvdSamplesPerSector = "1";
        state.DvdDataDirectory = files;
        DvdStreamingOptions disc = new(256, 24, 58, PitchCubic: 2);

        foreach (bool outer in new[] { false, true })
        {
            state.DvdFilesAtOuterRing = outer;
            RunPreview(preview);
            RequirePreview(preview, true, $"DVD {(outer ? "outer" : "inner")} design projection");
            DvdHybridStreamingPlan plan = DvdStreamingGenerator.PrepareHybrid(files, disc, filesAtOuterRing: outer);
            object?[] arguments = [Path.Combine(directory, "command.png"), null, null, 0, 0, null, null];
            Require((bool)Invoke(preview, "TryBuildDvdCalibrateCommand", arguments)!, "DVD projection command builds");
            string[] command = (string[])arguments[2]!;
            Require(Argument(command, "--lba") == plan.DrawingStartLba.ToString()
                && Argument(command, "--fill-sectors") == plan.DrawingSectors.ToString()
                && Argument(command, "--total-sectors") == "256",
                "DVD projection limits the drawing range while preserving full-disc geometry");

            string iso = Path.Combine(directory, $"dvd-{outer}.iso");
            using (FileStream stream = File.Create(iso)) stream.SetLength(disc.ContentLength);
            WriteDvdMetadata(iso, disc, plan);
            state.DvdFilesAtOuterRing = !outer;
            state.DvdIsoPath = iso;
            Require(state.DvdFilesAtOuterRing == outer, "DVD sidecar restores placement even when geometry already matches");
            preview.IsCalibrationPreview = false;
            RunPreview(preview);
            RequirePreview(preview, true, $"DVD {(outer ? "outer" : "inner")} finished-image readback");
            preview.IsCalibrationPreview = true;
        }

        state.DvdDataDirectory = string.Empty;
        RunPreview(preview);
        RequirePreview(preview, false, "pure DVD drawing projection");
        state.DvdDataDirectory = Path.Combine(directory, "missing");
        RunPreview(preview);
        Require(!preview.HasFileRegionOverlay && !preview.StatusText.StartsWith("已实时更新"),
            "missing DVD folder reports failure instead of falling back to a full-disc drawing");
        state.DvdDataDirectory = files;
        state.DvdTotalSectors = "32";
        RunPreview(preview);
        Require(!preview.HasFileRegionOverlay && !preview.StatusText.StartsWith("已实时更新"),
            "DVD capacity failure does not fall back to full-disc drawing");
        state.DvdTotalSectors = "256";
        preview.IsCalibrationPreview = false;
        File.WriteAllText(state.DvdIsoPath + ".json", "invalid sidecar");
        RunPreview(preview);
        RequirePreview(preview, false, "DVD readback without valid layout metadata");
    }

    private static void VerifyBluRayPreview(ShellViewModel shell, string directory, string source, string files)
    {
        DiscParametersState state = shell.State;
        LivePreviewViewModel preview = shell.LivePreview;
        preview.SelectedFamily = OpticalDiscFamily.BluRay;
        preview.IsCalibrationPreview = true;
        state.BdImagePath = source;
        state.BdTotalSectors = "1024";
        state.BdInnerRadius = state.BdActualInnerRadius = "24";
        state.BdOuterRadius = state.BdActualOuterRadius = "58";
        state.BdPreviewSize = "128";
        state.BdDataDirectory = files;
        BluRayDiscParameters disc = new(TotalSectors: 1024);
        foreach (bool outer in new[] { false, true })
        {
            state.BdFilesAtOuterRing = outer;
            // These old readback bounds must never replace the current project layout.
            state.BdDrawingStartSector = "32";
            state.BdDrawingEndSectorExclusive = "64";
            BluRayIsoPlan plan = preview.PrepareBluRayProjectionPlan(disc);
            Require(plan.HasFileSystem && plan.FilesAtOuterRing == outer && plan.DrawingStartSector != 32,
                "BD design layout follows folder/placement instead of stale sidecar bounds");
            RunPreview(preview);
            RequirePreview(preview, true, $"BD {(outer ? "outer" : "inner")} design projection");
            string iso = Path.Combine(directory, $"bd-{outer}.iso");
            using (FileStream stream = File.Create(iso)) stream.SetLength(disc.ContentLength);
            BluRayIsoMetadata.FromPlan(plan, source, iso, false, ToneProfile.LegacyBinary, Timestamp).Save(iso);
            state.BdFilesAtOuterRing = !outer;
            state.BdIsoPath = iso;
            Require(state.BdFilesAtOuterRing == outer, "BD sidecar restores file placement");
            preview.IsCalibrationPreview = false;
            RunPreview(preview);
            RequirePreview(preview, true, $"BD {(outer ? "outer" : "inner")} finished-image readback");
            preview.IsCalibrationPreview = true;
        }

        state.BdDataDirectory = string.Empty;
        RunPreview(preview);
        RequirePreview(preview, false, "pure BD drawing projection despite stale sidecar bounds and outer flag");
        state.BdDataDirectory = Path.Combine(directory, "missing");
        RunPreview(preview);
        Require(!preview.HasFileRegionOverlay && !preview.StatusText.StartsWith("已实时更新"),
            "missing BD folder reports failure instead of falling back to full-disc drawing");
        state.BdDataDirectory = files;
        state.BdTotalSectors = "512";
        RunPreview(preview);
        Require(!preview.HasFileRegionOverlay && !preview.StatusText.StartsWith("已实时更新"),
            "BD capacity failure does not fall back to full-disc drawing");
        state.BdTotalSectors = "1024";
        preview.IsCalibrationPreview = false;
        File.WriteAllText(state.BdIsoPath + ".json", "invalid sidecar");
        RunPreview(preview);
        RequirePreview(preview, false, "BD readback without valid layout metadata");
    }

    private static void VerifyCubicGeometry(string directory)
    {
        string path = Path.Combine(directory, "cubic-overlay.png");
        WritePng(path, 1025, Colors.Red);
        DvdStreamingOptions actual = new(4096, 24, 58, PitchCubic: 4);
        double length = 4096 * 38688.0 * actual.ChannelBitLengthNm * 1e-6;
        double exact = DvdTrackGeometry.Create(24, 58, length, pitchCubic: 4).RadiusAtArcLength(length / 2);
        double areaEstimate = Math.Sqrt((24 * 24 + 58 * 58) / 2.0);
        Require(Math.Abs(exact - areaEstimate) > 1, "cubic test separates exact geometry from area approximation");
        FileRegionPreviewOverlay.ApplyDvd(path, actual, 2048, 4096);
        BitmapSource bitmap = ReadPng(path);
        Require(SampleRadius(bitmap, 24 + .5, dvd: true) == Yellow, "DVD inner file area is yellow");
        Require(SampleRadius(bitmap, exact + .5, dvd: true) == Colors.Red, "DVD drawing keeps its original pixels");
        double distinguishingRadius = (exact + areaEstimate) / 2;
        Require(SampleRadius(bitmap, distinguishingRadius, dvd: true) == (distinguishingRadius < exact ? Yellow : Colors.Red),
            "DVD cubic file boundary uses track arc length rather than disc area");
        Require(Sample(bitmap, 512, 512) == Colors.Red && Sample(bitmap, 0, 0) == Colors.Red,
            "file overlay preserves the centre hole and space outside the disc");
        WritePng(path, 1025, Colors.Red);
        FileRegionPreviewOverlay.ApplyDvd(path, actual, 0, 2048);
        bitmap = ReadPng(path);
        Require(SampleRadius(bitmap, exact + .5, dvd: true) == Yellow
            && SampleRadius(bitmap, 24 + .5, dvd: true) == Colors.Red,
            "DVD outer file region moves outward without stretching the drawing");
    }

    private static void VerifyReadbackRange(string directory)
    {
        BluRayDiscParameters disc = new(TotalSectors: 1024);
        string iso = Path.Combine(directory, "constant.iso"), png = Path.Combine(directory, "readback.png");
        BluRayStreamingGenerator.GenerateFile(iso, new(disc, SectorCount: disc.TotalSectors),
            new BluRayConstantByteMapping(0xAA));
        byte[] original = File.ReadAllBytes(iso);
        BluRayPreviewRenderer.RenderReadback(iso, png, disc, 256,
            drawingStartSector: 256, drawingEndSectorExclusive: 768);
        BitmapSource before = ReadPng(png);
        double fileRadius = new BluRayTrackGeometry(disc).AtSector(900).RadiusMm;
        double drawingRadius = new BluRayTrackGeometry(disc).AtSector(500).RadiusMm;
        Require(SampleRadius(before, fileRadius, dvd: false) == Color.FromRgb(128, 128, 128),
            "BD readback excludes file bytes even when they resemble a valid drawing texture");
        Require(SampleRadius(before, drawingRadius, dvd: false) == Colors.Black,
            "BD readback still classifies drawing payload normally");
        FileRegionPreviewOverlay.ApplyBluRay(png, disc, 256, 768);
        BitmapSource after = ReadPng(png);
        Require(SampleRadius(after, fileRadius, dvd: false) == Yellow
            && SampleRadius(after, drawingRadius, dvd: false) == Colors.Black,
            "BD readback overlays only occupied regions");
        Require(original.SequenceEqual(File.ReadAllBytes(iso)), "preview coloration never changes burn data");
    }

    private static void WriteDvdMetadata(string iso, DvdStreamingOptions disc, DvdHybridStreamingPlan plan)
        => File.WriteAllText(iso + ".json", JsonSerializer.Serialize(new
        {
            totalSectors = disc.TotalSectors,
            startLba = plan.DrawingStartLba,
            fillSectors = plan.DrawingSectors,
            filesAtOuterRing = plan.FilesAtOuterRing,
            iso = new { mountableFilesystem = true },
            imageMapping = new { totalSectors = disc.TotalSectors, innerRadiusMm = disc.InnerRadiusMm,
                outerRadiusMm = disc.OuterRadiusMm, pitchCubic = disc.PitchCubic },
        }));

    private static void RunPreview(LivePreviewViewModel preview)
    {
        FieldInfo revision = typeof(LivePreviewViewModel).GetField("_livePreviewRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        revision.SetValue(preview, (int)revision.GetValue(preview)! + 1);
        Task task = (Task)Invoke(preview, "RefreshAsync", [])!;
        DispatcherFrame frame = new();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static object? Invoke(object target, string name, object?[] arguments)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static string Argument(string[] arguments, string name) => arguments[Array.IndexOf(arguments, name) + 1];

    private static void RequirePreview(LivePreviewViewModel preview, bool yellow, string context)
    {
        Require(preview.HasResultImage && preview.StatusText.StartsWith("已实时更新"), context + ": " + preview.StatusText);
        Require(preview.HasFileRegionOverlay == yellow, context + " legend visibility");
        BitmapSource bitmap = preview.ResultImage!;
        byte[] pixels = Pixels(bitmap);
        int yellowPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] == 21 && pixels[i + 1] == 204 && pixels[i + 2] == 250 && pixels[i + 3] == 255) yellowPixels++;
        Require(yellow ? yellowPixels > 10 : yellowPixels == 0, context + " rendered yellow file pixels");
    }

    private static Color SampleRadius(BitmapSource bitmap, double radius, bool dvd)
    {
        double centre = (bitmap.PixelWidth - 1) / 2.0;
        int x = (int)Math.Round(centre + radius * (dvd ? bitmap.PixelWidth - 1 : bitmap.PixelWidth) / 116);
        return Sample(bitmap, x, (int)Math.Round(centre));
    }

    private static Color Sample(BitmapSource bitmap, int x, int y)
    {
        byte[] bytes = Pixels(bitmap);
        int offset = (y * bitmap.PixelWidth + x) * 4;
        return Color.FromArgb(bytes[offset + 3], bytes[offset + 2], bytes[offset + 1], bytes[offset]);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        if (bitmap.Format != PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static BitmapSource ReadPng(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static void WritePng(string path, int size, Color color)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = color.A;
        }
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("File placement preview: " + message);
    }
}
