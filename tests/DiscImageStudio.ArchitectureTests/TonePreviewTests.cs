using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Localization;
using DiscImageStudio.ViewModels;

internal static class TonePreviewTests
{
    private const int Sectors = 4096;
    private const int Size = 512;

    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("tone-preview-ui-");
        try { WpfTestHost.Run(() => Wait(CheckAsync(directory.FullName))); }
        finally { directory.Delete(recursive: true); }
        Console.WriteLine("tone-preview: passed (CD/DVD/BD 512px masks, exact inner/outer file regions, automatic refresh, source isolation, stale-result clearing and offscreen panel)");
    }

    private static async Task CheckAsync(string directory)
    {
        string source = Path.Combine(directory, "source.png");
        WriteSource(source);
        byte[] originalSource = SHA256.HashData(File.ReadAllBytes(source));
        string firstFolder = Directory.CreateDirectory(Path.Combine(directory, "first")).FullName;
        string secondFolder = Directory.CreateDirectory(Path.Combine(directory, "second")).FullName;
        File.WriteAllBytes(Path.Combine(firstFolder, "payload.bin"), new byte[256 * 1024]);
        File.WriteAllBytes(Path.Combine(secondFolder, "larger.bin"), new byte[512 * 1024]);
        string originalLanguage = L.CurrentLanguage;
        var shell = new ShellViewModel(Dispatcher.CurrentDispatcher, new NoDeviceBurner());
        try
        {
            L.SetLanguage(L.English);
            foreach (OpticalDiscFamily family in new[] { OpticalDiscFamily.CompactDisc, OpticalDiscFamily.Dvd, OpticalDiscFamily.BluRay })
            {
                string familyName = family == OpticalDiscFamily.CompactDisc ? "cd" : family == OpticalDiscFamily.Dvd ? "dvd" : "bd";
                var document = new ProjectDocument(ProjectJsonStore.CurrentSchemaVersion, "Tone preview " + familyName,
                    family == OpticalDiscFamily.CompactDisc ? ProjectDocument.CdFamily : family == OpticalDiscFamily.Dvd ? ProjectDocument.DvdFamily : ProjectDocument.BluRayFamily,
                    new(24, 58, Sectors, UsableSectors: Sectors), [new("layer", "Asymmetric grayscale", source, true, 0, 0, 0, 1)],
                    new(Path.Combine(directory, familyName + ".iso"), false, "", "TONE_TEST", false),
                    null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ToneProfile.LegacyBinary);
                typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, document);
                shell.IsProjectOpen = true;
                shell.Design.SetDiscFamily(family);
                ProjectSession.ApplyToState(document, shell.State);
                shell.SelectWorkspace(Workspace.Design);
                shell.Design.LoadLayers(document.Layers);
                ToneViewModel tone = shell.Design.Tone;
                await Until(() => tone.IsTonePreviewReady && tone.TonePreviewImage is not null, tone, "initial " + familyName);
                CheckImage(tone.TonePreviewImage!, family, 24, 58, null, familyName + " no files");
                Snapshot(tone, familyName + "-no-files");
                string composite = SourcePath(shell.State, family);
                byte[] originalComposite = SHA256.HashData(File.ReadAllBytes(composite));

                await Refresh(tone, () => SetFolder(shell.State, family, firstFolder), familyName + " folder change");
                CheckImage(tone.TonePreviewImage!, family, 24, 58,
                    DrawingRange(family, firstFolder, false, 24, 58), familyName + " inner files");
                Snapshot(tone, familyName + "-inner-files");

                await Refresh(tone, () => SetOuter(shell.State, family, true), familyName + " outer placement");
                CheckImage(tone.TonePreviewImage!, family, 24, 58,
                    DrawingRange(family, firstFolder, true, 24, 58), familyName + " outer files");
                Snapshot(tone, familyName + "-outer-files");

                await Refresh(tone, () => SetFolder(shell.State, family, secondFolder), familyName + " replacement folder");
                CheckImage(tone.TonePreviewImage!, family, 24, 58,
                    DrawingRange(family, secondFolder, true, 24, 58), familyName + " larger files");
                Require(SHA256.HashData(File.ReadAllBytes(composite)).AsSpan().SequenceEqual(originalComposite),
                    familyName + ": file overlays never change the composite");
                await Refresh(tone, () => SetGeometry(shell.State, family, "30", "54"), familyName + " geometry change");
                CheckImage(tone.TonePreviewImage!, family, 30, 54,
                    DrawingRange(family, secondFolder, true, 30, 54), familyName + " changed geometry");
                if (family == OpticalDiscFamily.CompactDisc) Snapshot(tone, "cd-custom-radius-wide", wide: true);

                await Refresh(tone, () =>
                {
                    SetFolder(shell.State, family, firstFolder);
                    SetOuter(shell.State, family, false);
                    SetFolder(shell.State, family, secondFolder);
                }, familyName + " superseded requests");
                CheckImage(tone.TonePreviewImage!, family, 30, 54,
                    DrawingRange(family, secondFolder, false, 30, 54), familyName + " latest request");

                Require(SHA256.HashData(File.ReadAllBytes(source)).AsSpan().SequenceEqual(originalSource),
                    familyName + ": geometry and overlays never change the source layer");
                SetFolder(shell.State, family, Path.Combine(directory, "missing-folder"));
                await Until(() => !tone.HasTonePreview && tone.TonePreviewImage is null && !tone.IsTonePreviewReady,
                    tone, familyName + " layout error removes stale pixels");
                await Refresh(tone, () => SetFolder(shell.State, family, firstFolder), familyName + " error recovery");
                SetFolder(shell.State, family, secondFolder);
                shell.IsProjectOpen = false;
                shell.Design.ClearLayers();
                Require(tone.TonePreviewImage is null && !tone.HasTonePreview, familyName + ": project close clears the image immediately");
                await Task.Delay(250);
                Require(tone.TonePreviewImage is null && !tone.HasTonePreview, familyName + ": a cancelled request cannot republish after close");
            }
        }
        finally
        {
            shell.IsProjectOpen = false;
            shell.Design.ClearLayers();
            L.SetLanguage(originalLanguage);
        }
    }

    private static (double Inner, double Outer) DrawingRange(OpticalDiscFamily family, string directory,
        bool outer, double innerRadius, double outerRadius)
    {
        if (family == OpticalDiscFamily.CompactDisc)
        {
            var parameters = new CdDiscParameters(innerRadius, outerRadius, Sectors);
            var plan = CdDataImageGenerator.PrepareLayout(directory, parameters, "TONE_TEST", outer);
            return (parameters.RadiusFromByte((long)plan.DrawingStartLba * CdDiscParameters.BytesPerSector),
                parameters.RadiusFromByte((long)plan.DrawingEndLbaExclusive * CdDiscParameters.BytesPerSector));
        }
        if (family == OpticalDiscFamily.Dvd)
        {
            var options = new DvdStreamingOptions(Sectors, innerRadius, outerRadius);
            var plan = DvdStreamingGenerator.PrepareHybrid(directory, options, "TONE_TEST", outer);
            double length = Sectors * 38688.0 * options.ChannelBitLengthNm * 1e-6;
            var geometry = DvdTrackGeometry.Create(innerRadius, outerRadius, length);
            return (geometry.RadiusAtArcLength(length * plan.DrawingStartLba / Sectors),
                geometry.RadiusAtArcLength(length * plan.DrawingEndLbaExclusive / Sectors));
        }
        var bd = BluRayIsoGenerator.PrepareDrawing(new BluRayDiscParameters(Sectors, 1, innerRadius, outerRadius),
            directory, "TONE_TEST", filesAtOuterRing: outer);
        return (bd.DrawingStartRadiusMm, bd.DrawingEndRadiusMm);
    }

    private static void CheckImage(BitmapSource bitmap, OpticalDiscFamily family, double inner, double outer,
        (double Inner, double Outer)? drawing, string name)
    {
        Require(bitmap.PixelWidth == Size && bitmap.PixelHeight == Size, name + ": all media use 512 by 512 pixels");
        BitmapSource converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[Size * Size * 4];
        converted.CopyPixels(pixels, Size * 4, 0);
        bool cd = family == OpticalDiscFamily.CompactDisc;
        double centre = cd ? Size / 2.0 : (Size - 1) / 2.0;
        double mmPerPixel = outer * 2 / (family == OpticalDiscFamily.Dvd ? Size - 1 : Size);
        int wrongMask = 0, wrongFiles = 0, yellow = 0, opaqueDrawing = 0;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int offset = (y * Size + x) * 4;
            double dx = (x - centre) * mmPerPixel, dy = (y - centre) * mmPerPixel;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            bool isYellow = pixels[offset] == 21 && pixels[offset + 1] == 204 && pixels[offset + 2] == 250 && pixels[offset + 3] == 255;
            if (isYellow) yellow++;
            // A one-pixel margin excludes sampling/rounding of the physical boundary.
            if (radius < inner - mmPerPixel || radius > outer + mmPerPixel)
            {
                if (pixels[offset + 3] != 0) wrongMask++;
            }
            if (radius >= inner && radius < outer && drawing is { } range)
            {
                bool expectedFile = radius < range.Inner || radius >= range.Outer;
                if (expectedFile != isYellow) wrongFiles++;
            }
            if (radius > inner + mmPerPixel && radius < outer - mmPerPixel && !isYellow && pixels[offset + 3] == 255)
                opaqueDrawing++;
        }
        Require(wrongMask == 0 && pixels[(256 * Size + 256) * 4 + 3] == 0,
            name + ": the centre and outside the recording annulus are transparent");
        Require(wrongFiles == 0 && (drawing is null ? yellow == 0 : yellow > 0),
            name + $": yellow pixels match the filesystem plan, including both ends (mismatches {wrongFiles}, yellow {yellow})");
        Require(opaqueDrawing > 1000, name + ": the tone picture remains visible inside the drawing range");
    }

    private static async Task Refresh(ToneViewModel tone, Action change, string name)
    {
        BitmapSource? previous = tone.TonePreviewImage;
        change();
        await Until(() => tone.IsTonePreviewReady && tone.TonePreviewImage is not null
            && !ReferenceEquals(previous, tone.TonePreviewImage), tone, name);
    }

    private static async Task Until(Func<bool> condition, ToneViewModel tone, string name)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(20))
                throw new InvalidOperationException("Tone preview timed out: " + name + "; " + tone.TonePreviewStatus);
            await Task.Delay(20);
        }
    }

    private static string SourcePath(DiscParametersState state, OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.Dvd => state.DvdImagePath,
        OpticalDiscFamily.BluRay => state.BdImagePath,
        _ => state.CdImagePath,
    };

    private static void SetFolder(DiscParametersState state, OpticalDiscFamily family, string path)
    {
        if (family == OpticalDiscFamily.Dvd) state.DvdDataDirectory = path;
        else if (family == OpticalDiscFamily.BluRay) state.BdDataDirectory = path;
        else state.CdDataDirectory = path;
    }

    private static void SetOuter(DiscParametersState state, OpticalDiscFamily family, bool value)
    {
        if (family == OpticalDiscFamily.Dvd) state.DvdFilesAtOuterRing = value;
        else if (family == OpticalDiscFamily.BluRay) state.BdFilesAtOuterRing = value;
        else state.CdFilesAtOuterRing = value;
    }

    private static void SetGeometry(DiscParametersState state, OpticalDiscFamily family, string inner, string outer)
    {
        if (family == OpticalDiscFamily.Dvd) { state.DvdInnerRadius = inner; state.DvdOuterRadius = outer; }
        else if (family == OpticalDiscFamily.BluRay) { state.BdInnerRadius = inner; state.BdOuterRadius = outer; }
        else { state.CdInnerRadius = inner; state.CdOuterRadius = outer; }
    }

    private static void Snapshot(ToneViewModel tone, string name, bool wide = false)
    {
        var panel = new TonePanel { DataContext = tone };
        var host = new Border { Padding = new Thickness(12), Child = panel };
        Size dimensions = wide ? new Size(784, 444) : new Size(344, 760);
        if (!wide) panel.RailHeightBudget = dimensions.Height - 24;
        // Bind the family-specific controls before the responsive panel measures
        // its remaining preview height, as its real design-page host does.
        for (int pass = 0; pass < 2; pass++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            host.Measure(dimensions);
            host.Arrange(new Rect(new Point(), dimensions));
            host.UpdateLayout();
        }
        Require(panel.FindName("PreviewDisc") is Image { Source: not null, Stretch: Stretch.Uniform, Clip: null },
            "the tone panel displays the whole alpha bitmap without a fixed circular crop");
        UiLayoutSnapshot.Save(host, "tone-" + name);
    }

    private static void WriteSource(string path)
    {
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int offset = (y * Size + x) * 4;
            byte value = (byte)((x * 3 + y) % 256);
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Tone preview: " + message);
    }

    private sealed class NoDeviceBurner : IOpticalDiscBurner
    {
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([]);
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
