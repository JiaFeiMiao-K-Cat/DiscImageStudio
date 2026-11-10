using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

/// <summary>CD-Extra in the application: project persistence and validation, the CD mode
/// switches, the music list, the design guide's music/gap/picture plan and rings, the
/// preview bands, and the design page with the music card.</summary>
internal static class CdExtraUiTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cd-extra-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestProjectStore(directory);
            TestStateModes();
            TestPlannerAndOverlay(directory);
            Exception? error = null;
            Thread thread = new(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                try { TestDesignIntegration(directory); }
                catch (Exception exception) { error = exception; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
            WpfTestHost.Run(() => TestDesignView(directory));
            WpfTestHost.Run(() => TestMusicDrop(directory));
            WpfTestHost.Run(() => TestOuterMusic(directory));
            WpfTestHost.Run(() => Wait(TestTonePreviewAsync(directory)));
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("cd-extra-ui: passed (project round trip and legacy shape, validation, CD mode switches incl. RAW round trip, music list edits and order, guide plan, rings and problems, preview bands, export measured start, read-back sidecar, design page, WAV drops, outer-music mode switches, plan, project field, export and burn requests)");
    }

    private static ProjectDocument CdDocument(ProjectCdExtraSettings? cdExtra, string family = ProjectDocument.CdFamily)
        => new(ProjectJsonStore.CurrentSchemaVersion, "CD-Extra", family,
            new ProjectGeometry(24.5, 56.8, 359_849),
            [],
            new ProjectOutputs("D:\\out\\cd-track.wav", true, string.Empty, "CD_IMAGE", true),
            null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            ToneProfile.Photo, null, cdExtra);

    private static void TestProjectStore(string directory)
    {
        string path = Path.Combine(directory, "extra.discproj");
        ProjectCdExtraSettings settings = new(true, [new ProjectMusicTrack("D:\\music\\01.wav"), new ProjectMusicTrack("D:\\music\\02.wav")]);
        ProjectJsonStore.Save(path, CdDocument(settings));
        string json = File.ReadAllText(path);
        Require(json.Contains("\"cdExtra\"", StringComparison.Ordinal), "CD-Extra settings are saved");
        // A build without CD-Extra rejects unknown members; version 3 makes it report an
        // unsupported version instead of a malformed file.
        Require(json.Contains("\"schemaVersion\": 3", StringComparison.Ordinal),
            "a project with a cdExtra block is written as schema 3");
        ProjectDocument loaded = ProjectJsonStore.Load(path);
        Require(loaded.SchemaVersion == ProjectJsonStore.CurrentSchemaVersion
                && loaded.CdExtra is { Enabled: true } extra
                && extra.Tracks.Select(track => track.Path).SequenceEqual(new[] { "D:\\music\\01.wav", "D:\\music\\02.wav" }),
            "tracks round-trip in order");
        string downgraded = Path.Combine(directory, "downgraded.discproj");
        File.WriteAllText(downgraded, json.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2", StringComparison.Ordinal));
        Throws<InvalidDataException>(() => ProjectJsonStore.Load(downgraded), "a cdExtra block in a schema 2 file");
        ProjectJsonStore.Save(path, loaded with { CdExtra = null });
        Require(File.ReadAllText(path).Contains("\"schemaVersion\": 2", StringComparison.Ordinal),
            "removing the music returns the project to schema 2");

        string legacy = Path.Combine(directory, "plain.discproj");
        ProjectJsonStore.Save(legacy, CdDocument(null));
        Require(!File.ReadAllText(legacy).Contains("cdExtra", StringComparison.OrdinalIgnoreCase)
                && File.ReadAllText(legacy).Contains("\"schemaVersion\": 2", StringComparison.Ordinal),
            "ordinary CD projects keep their JSON shape and schema 2");
        Require(ProjectJsonStore.Load(legacy).CdExtra is null, "legacy projects load without CD-Extra");

        Throws<InvalidDataException>(() => ProjectJsonStore.Validate(CdDocument(settings, ProjectDocument.DvdFamily)),
            "CD-Extra on a DVD project");
        Throws<InvalidDataException>(() => ProjectJsonStore.Validate(CdDocument(new ProjectCdExtraSettings(true,
            [new ProjectMusicTrack(" ")]))), "blank music path");
        Throws<InvalidDataException>(() => ProjectJsonStore.Validate(CdDocument(new ProjectCdExtraSettings(true,
            Enumerable.Range(0, 99).Select(index => new ProjectMusicTrack($"D:\\m\\{index}.wav")).ToArray()))),
            "more than 98 music tracks");
    }

    private static void TestStateModes()
    {
        string presets = Path.Combine(Path.GetTempPath(), "cd-extra-presets-" + Guid.NewGuid().ToString("N") + ".json");
        DiscParametersState state = new(presets);
        ProjectDocument document = CdDocument(new ProjectCdExtraSettings(true, [new ProjectMusicTrack("D:\\music\\01.wav")]));
        ProjectSession.ApplyToState(document, state);
        Require(state.CdExtraEnabled && state.IsCdExtraMode && state.CdMusicTracks.SequenceEqual(new[] { "D:\\music\\01.wav" }),
            "opening a CD-Extra project restores the music");
        Require(!state.IsCdAudioMode && !state.IsCdWaveOutput && state.UsesCdVolumeLabel
                && state.CdOutputPath.EndsWith(".iso", StringComparison.Ordinal),
            "CD-Extra exports an ISO and offers the volume label");
        Require(ProjectSession.CaptureCdExtra(document, state) is { Enabled: true } captured && captured.Tracks.Count == 1,
            "save captures the mode and tracks");
        state.CdExtraEnabled = false;
        Require(state.IsCdAudioMode && state.CdOutputPath.EndsWith(".wav", StringComparison.Ordinal)
                && ProjectSession.CaptureCdExtra(document, state) is { Enabled: false },
            "switching off keeps the list but returns to the audio track output");
        state.CdMusicTracks = [];
        Require(ProjectSession.CaptureCdExtra(document, state) is null, "nothing is saved without mode or music");
        state.CdExtraEnabled = true;
        state.CdMusicTracks = ["D:\\x.wav"];
        ProjectSession.ApplyToState(CdDocument(null), state);
        Require(!state.CdExtraEnabled && state.CdMusicTracks.Count == 0, "a project without CD-Extra does not inherit music");
        Require(ProjectSession.CaptureCdExtra(CdDocument(null, ProjectDocument.DvdFamily) with { DiscFamily = ProjectDocument.DvdFamily },
            state) is null, "other families never save CD-Extra");

        state.CdExtraEnabled = false;
        state.CdDataDirectory = string.Empty;
        state.CdOutputPath = "D:\\out\\disc.raw";
        // A project without music takes the default position (outer); this suite covers CD-Extra.
        state.CdMusicAtOuterRing = false;
        state.CdExtraEnabled = true;
        Require(state.CdOutputPath == "D:\\out\\disc.iso", "CD-Extra turns a RAW output into the session ISO");
        state.CdExtraEnabled = false;
        Require(state.CdOutputPath == "D:\\out\\disc.raw", "switching back restores the RAW format the user chose");

        // The remembered format belongs to one project: B never turns into a RAW track.
        ProjectSession.ApplyToState(CdDocument(null) with
        {
            Outputs = new ProjectOutputs("D:\\a\\disc.raw", true, string.Empty, "CD_IMAGE", true),
        }, state);
        ProjectSession.ApplyToState(CdDocument(new ProjectCdExtraSettings(true, [new ProjectMusicTrack("D:\\m\\1.wav")])) with
        {
            Outputs = new ProjectOutputs("D:\\b\\cd-extra-session2.iso", true, string.Empty, "CD_EXTRA", true),
        }, state);
        state.CdExtraEnabled = false;
        Require(state.CdOutputPath == "D:\\b\\cd-extra-session2.wav", "another project's RAW choice does not resurface");

        // Reopening a RAW project assigns the same path string again; its format still holds.
        ProjectDocument rawProject = CdDocument(null) with
        {
            Outputs = new ProjectOutputs("D:\\a\\disc.raw", true, string.Empty, "CD_IMAGE", true),
        };
        ProjectSession.ApplyToState(rawProject, state);
        ProjectSession.ApplyToState(rawProject, state);
        state.CdMusicAtOuterRing = false;
        state.CdExtraEnabled = true;
        state.CdExtraEnabled = false;
        Require(state.CdOutputPath == "D:\\a\\disc.raw", "reopening a RAW project keeps its format");
    }

    private static void TestPlannerAndOverlay(string directory)
    {
        string first = Path.Combine(directory, "01.wav");
        string second = Path.Combine(directory, "02.wav");
        CdExtraLayoutTests.WriteWave(first, frames: 588 * 2_000, seed: 1);
        CdExtraLayoutTests.WriteWave(second, frames: 588 * 1_500 + 4, seed: 2);
        string files = Path.Combine(directory, "files");
        Directory.CreateDirectory(files);
        File.WriteAllBytes(Path.Combine(files, "notes.bin"), new byte[60 * 2048]);
        DataRegionInputs inputs = new(OpticalDiscFamily.CompactDisc, files, "CD_EXTRA", false,
            40_000, 39_998, 24.5, 32.0, 0, 0, 0, [first, second]);
        (IReadOnlyList<DesignViewModel.DataRegionMm>? regions, CdExtraPlan plan) = DataRegionPlanner.ComputeCdExtra(inputs);
        CdExtraLayout layout = plan.Layout!;
        Require(plan.IsValid && layout.Tracks.Select(track => track.Sectors).SequenceEqual(new long[] { 2_000, 1_501 })
                && layout.Session2StartLba == 2_000 + 152 + 1_501 + 2 + 11_400,
            "the guide plans the music from the WAV files");
        Require(regions is { Count: > 0 } && regions.All(region => region.InnerRadiusMm >= layout.PictureInnerRadiusMm - 1e-9),
            "file regions sit inside the picture session, not over the music");
        string big = Path.Combine(directory, "big-files");
        Directory.CreateDirectory(big);
        using (FileStream huge = File.Create(Path.Combine(big, "huge.bin"))) huge.SetLength(60L * 1024 * 1024);
        (_, CdExtraPlan tooLarge) = DataRegionPlanner.ComputeCdExtra(inputs with { Directory = big });
        Require(tooLarge.Problem == CdExtraPlanProblem.DataCapacity && tooLarge.Layout is not null && !tooLarge.IsValid
                && CdExtraPlanner.DescribeProblem(tooLarge).Contains("随盘文件太大", StringComparison.Ordinal),
            "disc files that do not fit are not reported as music that is too long");
        (_, CdExtraPlan missingFolder) = DataRegionPlanner.ComputeCdExtra(inputs with { Directory = Path.Combine(directory, "no-such-folder") });
        Require(missingFolder.Problem == CdExtraPlanProblem.DataError && missingFolder.Layout is not null
                && missingFolder.Tracks.All(track => track.Info is not null)
                && CdExtraPlanner.DescribeProblem(missingFolder).StartsWith("图片区段无法规划：", StringComparison.Ordinal),
            "a missing folder keeps the music plan and names the problem");
        (IReadOnlyList<DesignViewModel.DataRegionMm>? noFiles, CdExtraPlan noFilesPlan) =
            DataRegionPlanner.ComputeCdExtra(inputs with { Directory = string.Empty });
        Require(noFiles is null && noFilesPlan.IsValid, "without files only the music/gap/picture plan is drawn");
        (_, CdExtraPlan full) = DataRegionPlanner.ComputeCdExtra(inputs with { UsableSectors = 15_000 });
        Require(full.Problem == CdExtraPlanProblem.Capacity && full.Layout is null, "music without room reports capacity");

        // Preview bands: amber music, grey gap, untouched picture.
        string source = Path.Combine(directory, "source.png");
        CdExtraLayoutTests.WriteImage(source);
        CdDiscParameters parameters = inputs.CreateCdParameters();
        CdDiscParameters display = parameters with { ImageOuterRadiusMm = parameters.OutputOuterRadiusMm };
        string preview = Path.Combine(directory, "bands.png");
        CdDataImageGenerator.PreviewWarp(source, preview, parameters, display, 512, 8, ToneProfile.Photo, plan.DataPlan);
        FileRegionPreviewOverlay.ApplyCdExtra(preview, display, layout, plan.DataPlan!);
        BitmapSource bitmap = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(preview), BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        Color Pixel(double radiusMm)
        {
            double mmPerPixel = display.PreviewImageOuterRadiusMm * 2 / bitmap.PixelWidth;
            int x = (int)Math.Round(bitmap.PixelWidth / 2.0 + radiusMm / mmPerPixel);
            int y = bitmap.PixelHeight / 2;
            byte[] pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }
        double musicMiddle = (layout.MusicInnerRadiusMm + layout.MusicOuterRadiusMm) / 2;
        double gapMiddle = (layout.MusicOuterRadiusMm + layout.PictureInnerRadiusMm) / 2;
        double pictureMiddle = (layout.PictureInnerRadiusMm + layout.PictureOuterRadiusMm) / 2;
        Require(Pixel(musicMiddle) == FileRegionPreviewOverlay.MusicColor, "music ring is amber");
        Require(Pixel(gapMiddle) == FileRegionPreviewOverlay.SessionGapColor, "session gap is grey");
        Color picture = Pixel(pictureMiddle);
        Require(picture != FileRegionPreviewOverlay.MusicColor && picture != FileRegionPreviewOverlay.SessionGapColor
                && picture.R == picture.G && picture.G == picture.B, "the picture ring keeps the rendered grey");
    }

    private static void TestDesignIntegration(string directory)
    {
        string first = Path.Combine(directory, "01.wav");
        string second = Path.Combine(directory, "02.wav");
        string broken = Path.Combine(directory, "broken.wav");
        File.WriteAllText(broken, "not a wave file");
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
        DesignViewModel design = shell.Design;
        DiscParametersState state = shell.State;
        design.SetDiscFamily(OpticalDiscFamily.CompactDisc);
        state.CdInnerRadius = "24.5";
        state.CdOuterRadius = "32";
        state.CdSectors = "40000";
        state.CdUsableSectors = "39998";
        state.CdDataDirectory = string.Empty;
        state.CdMusicAtOuterRing = false;

        CdMusicViewModel music = design.Music;
        music.AddTracks([first, second, first.ToUpperInvariant()]);
        Require(state.CdExtraEnabled && state.CdMusicTracks.SequenceEqual(new[] { first, second }),
            "adding music switches to CD-Extra and skips duplicates");
        Require(music.Tracks.Select(row => row.FileName).SequenceEqual(new[] { "01.wav", "02.wav" })
                && music.Tracks.All(row => row.Detail.Length > 0), "rows follow the state");
        music.MoveTrack(music.Tracks[1], -1);
        Require(state.CdMusicTracks.SequenceEqual(new[] { second, first }) && music.SelectedTrack?.Path == second,
            "moving a row reorders the disc");
        music.MoveTrack(music.Tracks[0], 1);

        RunGuide(design);
        CdExtraPlan? plan = design.CdExtraPlan;
        Require(plan is { IsValid: true } && design.CdExtraRings is { } rings
                && Math.Abs(rings.MusicOuterMm - plan.Layout!.MusicOuterRadiusMm) < 1e-12
                && Math.Abs(rings.PictureInnerMm - plan.Layout.PictureInnerRadiusMm) < 1e-12,
            "the guide pass publishes the plan and the canvas rings");
        Require(music.HasLayout && !music.HasProblem && music.TrackSummary.Contains('2')
                && music.PictureShareText.Length > 0 && music.CanvasLegendText.Length > 0
                && music.MusicShareWidth.IsStar && music.PictureShareWidth.Value > music.MusicShareWidth.Value
                && music.Tracks.All(row => !row.HasError),
            "the music card shows durations and the disc shares");

        // Export: the predicted start is shown; a measured start is cleared by other music.
        ExportViewModel export = shell.Export;
        Require(export.CdExtraSession2Hint.Contains(design.CdExtraPlan!.Layout!.Session2StartLba.ToString("N0"), StringComparison.Ordinal),
            "the export hint shows the predicted session 2 start");
        export.CdExtraSession2StartLba = "0,15060";
        music.MoveTrack(music.Tracks[1], -1);
        Require(export.CdExtraSession2StartLba.Length == 0, "a measured start is cleared when the music changes");
        music.MoveTrack(music.Tracks[0], 1);

        // The typed measured start reaches the export request and survives the project-settings
        // notification (same disc); only a real project switch clears it, and Test Write too.
        state.CdOutputPath = Path.Combine(directory, "export", "session2.iso");
        string composite = Path.Combine(directory, "source.png");
        export.CdExtraSession2StartLba = "0,15060";
        Require(export.CreateCdImageRequest(composite).Session2StartLba == 15_060, "a measured start reaches the generator");
        export.CdExtraSession2StartLba = "15,060";
        Require(export.CreateCdImageRequest(composite).Session2StartLba == 15_060, "the grouped form the app prints is one LBA");
        export.CdExtraSession2StartLba = "next";
        Throws<ArgumentException>(() => export.CreateCdImageRequest(composite), "a measured start that is not an LBA");
        export.CdExtraSession2StartLba = string.Empty;
        Require(export.CreateCdImageRequest(composite).Session2StartLba is null, "an empty field uses the prediction");
        export.CdExtraSession2StartLba = "0,15060";
        shell.Burn.CdExtraTestWrite = true;
        RaisePropertyChanged(shell, nameof(ShellViewModel.CurrentProject));
        Require(export.CdExtraSession2StartLba == "0,15060" && shell.Burn.CdExtraTestWrite,
            "confirming the project settings keeps the measured start and Test Write");
        shell.IsProjectOpen = true;
        shell.IsProjectOpen = false;
        Require(export.CdExtraSession2StartLba.Length == 0 && !shell.Burn.CdExtraTestWrite,
            "a project switch clears the measured start and Test Write");

        // Disc files too large for the picture session: the files card warns, the rings stay.
        state.CdDataDirectory = Path.Combine(directory, "big-files");
        RunGuide(design);
        Require(design.DataRegionOverflow && design.CdExtraRings is not null && music.HasProblem && music.HasLayout
                && music.ProblemText.Contains("随盘文件太大", StringComparison.Ordinal),
            "files that do not fit warn in the files card and keep the music rings");
        state.CdDataDirectory = string.Empty;
        RunGuide(design);

        music.AddTracks([broken]);
        RunGuide(design);
        Require(design.CdExtraRings is null && music.HasProblem && !music.HasLayout
                && music.Tracks[^1].HasError && music.Tracks[^1].Detail.Length > 0,
            "an unusable file is marked and blocks the layout");
        music.RemoveTrack(music.Tracks[^1]);
        state.CdExtraEnabled = false;
        RunGuide(design);
        Require(design.CdExtraRings is null && design.CdExtraPlan is null && !music.HasLayout && !music.HasProblem,
            "switching to picture only removes the rings");
        Require(state.CdMusicTracks.Count == 2, "the music list survives the switch");

        // Live calibration preview: bands for a valid plan, the problem instead of a picture-only
        // disc for an invalid one, and no volume-label check for a plain audio track.
        LivePreviewViewModel preview = shell.LivePreview;
        try
        {
            preview.SelectedFamily = OpticalDiscFamily.CompactDisc;
            preview.IsCalibrationPreview = true;
            state.CdImagePath = Path.Combine(directory, "source.png");
            state.CdSectors = "40000";
            state.CdUsableSectors = "39998";
            state.CdInnerRadius = state.CdActualInnerRadius = "24.5";
            state.CdOuterRadius = state.CdActualOuterRadius = "32";
            state.CdPreviewSize = "256";
            state.CdSamplesPerSector = "2";
            state.CdDataDirectory = string.Empty;
            state.CdVolumeLabel = "CD_EXTRA";
            state.CdMusicAtOuterRing = false;
            state.CdExtraEnabled = true;
            state.CdMusicTracks = [first, second];
            RunPreview(preview);
            Require(preview.HasResultImage && preview.StatusText.StartsWith("已实时更新", StringComparison.Ordinal)
                    && preview.HasMusicRegionOverlay,
                "the live preview draws the picture session with music bands: " + preview.StatusText);
            state.CdMusicTracks = [broken];
            RunPreview(preview);
            Require(!preview.HasResultImage && !preview.StatusText.StartsWith("已实时更新", StringComparison.Ordinal)
                    && preview.StatusText.Contains("有音乐文件无法使用", StringComparison.Ordinal),
                "an unusable plan is reported instead of a picture-only preview: " + preview.StatusText);
            state.CdExtraEnabled = false;
            state.CdVolumeLabel = "MY_HOLIDAY_DISC_2025";
            RunPreview(preview);
            Require(preview.HasResultImage && preview.StatusText.StartsWith("已实时更新", StringComparison.Ordinal)
                    && !preview.HasMusicRegionOverlay,
                "a plain audio track ignores a legacy long label: " + preview.StatusText);
            state.CdVolumeLabel = "CD_EXTRA";
            state.CdMusicTracks = [first, second];
        }
        finally { preview.OnWindowClosed(); }

        // Read-back of a CD-Extra picture session adopts the geometry it was generated for.
        string iso = Path.Combine(directory, "readback", "session2.iso");
        CdImageGenerator.Generate(new CdImageRequest(Path.Combine(directory, "source.png"), iso,
            new CdDiscParameters(24.5, 27.5, 15_000, UsableSectors: 14_998), ToneProfile.LegacyBinary,
            MusicTracks: [first]));
        MethodInfo adopt = typeof(LivePreviewViewModel).GetMethod("AdoptTrackMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!;
        adopt.Invoke(shell.LivePreview, [iso]);
        Require(state.CdSectors == "15000" && state.CdUsableSectors == "14998" && state.CdOuterRadius == "27.5",
            "a CD-Extra ISO's own geometry is adopted for read-back");
    }

    private static void TestDesignView(string directory)
    {
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
        shell.Design.SetDiscFamily(OpticalDiscFamily.CompactDisc);
        shell.State.CdMusicAtOuterRing = false;
        shell.State.CdExtraEnabled = true;
        shell.State.CdMusicTracks = [Path.Combine(directory, "01.wav"), Path.Combine(directory, "02.wav")];
        RunGuide(shell.Design);
        DesignView view = new(shell.Design);
        // Tall enough that the whole music card, share bar included, is laid out.
        Size size = new(1440, 1500);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        // The page is measured off-screen, so check the laid-out visibility, not IsVisible
        // (which needs a presentation source).
        Require(view.FindName("MusicCardGate") is FrameworkElement card && card.Visibility == Visibility.Visible
                && card.ActualHeight > 0
                && view.FindName("MusicList") is System.Windows.Controls.ListBox list
                && list.Visibility == Visibility.Visible && list.Items.Count == 2,
            "the design page shows the music card and its tracks");
        Require(shell.Design.Music.HasLayout
                && view.FindName("DiscShareBar") is System.Windows.Controls.Grid bar && bar.ActualWidth > 0
                && bar.ColumnDefinitions[0].ActualWidth > 0 && bar.ColumnDefinitions[1].ActualWidth > 0
                && bar.ColumnDefinitions[2].ActualWidth > bar.ColumnDefinitions[0].ActualWidth,
            "the share bar splits music, gap and picture by area");
        UiLayoutSnapshot.Save(view, "design-cd-extra");
        string summary = shell.Design.Music.PictureShareText;
        CdExtraTrackProbe brokenProbe = new("x.wav", null, new InvalidDataException("bad"));
        string? brokenProbeChinese = brokenProbe.Error;
        try
        {
            // English strings are longer; the legend rows must still wrap inside the card.
            L.SetLanguage(L.English);
            shell.Relocalize();
            view.UpdateLayout();
            Require(shell.Design.Music.PictureShareText != summary
                    && shell.Design.Music.PictureShareText.StartsWith("Picture", StringComparison.Ordinal),
                "the share summary follows the UI language");
            UiLayoutSnapshot.Save(view, "design-cd-extra.en");
            Require(brokenProbeChinese == "需要 44.1 kHz、16 位、立体声 PCM 的 WAV 文件"
                    && brokenProbe.Error == "Needs a 44.1 kHz, 16-bit stereo PCM WAV file",
                "a probe read in Chinese is worded in English after the switch");
        }
        finally
        {
            L.SetLanguage(L.Chinese);
            shell.Relocalize();
        }
    }

    /// <summary>Dropping WAV files on a CD design page appends music (images in the same drop
    /// still become layers); other families and a closed project ignore WAV drops.</summary>
    private static void TestMusicDrop(string directory)
    {
        string third = Path.Combine(directory, "03.wav");
        CdExtraLayoutTests.WriteWave(third, frames: 588 * 310, seed: 5);
        string text = Path.Combine(directory, "notes.txt");
        File.WriteAllText(text, "not music");

        // The music commands follow the disc family: a CD project opened after a DVD one.
        ShellViewModel family = new(Dispatcher.CurrentDispatcher);
        family.IsProjectOpen = true;
        family.Design.SetDiscFamily(OpticalDiscFamily.Dvd);
        bool dvdCanAdd = family.Design.Music.AddMusicCommand.CanExecute(null);
        bool notified = false;
        family.Design.Music.AddMusicCommand.CanExecuteChanged += (_, _) => notified = true;
        family.Design.SetDiscFamily(OpticalDiscFamily.CompactDisc);
        Require(!dvdCanAdd && notified && family.Design.Music.AddMusicCommand.CanExecute(null),
            "switching to a CD project re-enables adding music");
        family.Design.Music.AddTracks([Path.Combine(directory, "10 b.wav"), Path.Combine(directory, "2 a.wav")]);
        Require(family.State.CdMusicTracks.Select(Path.GetFileName).SequenceEqual(new[] { "2 a.wav", "10 b.wav" }),
            "a picked or dropped batch is added in natural file-name order");

        ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
        shell.Design.SetDiscFamily(OpticalDiscFamily.CompactDisc);
        DesignView view = new(shell.Design);
        DataObject files = new(DataFormats.FileDrop, new[] { text, third, Path.Combine(directory, "01.wav") });
        Require(Raise(view, files, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None
                && Raise(view, files, DragDrop.PreviewDropEvent).Effects == DragDropEffects.None
                && shell.State.CdMusicTracks.Count == 0,
            "without an open project WAV drops are refused");
        shell.IsProjectOpen = true;
        Require(Raise(view, files, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.Copy,
            "hovering WAV files offers copy");
        DragEventArgs drop = Raise(view, files, DragDrop.PreviewDropEvent);
        Require(drop.Handled && drop.Effects == DragDropEffects.Copy && shell.State.CdExtraEnabled
                && shell.State.CdMusicTracks.SequenceEqual(new[] { Path.Combine(directory, "01.wav"), third })
                && shell.Design.Layers.Count == 0,
            "dropped WAV files become music tracks in file-name order; other files are ignored");
        Raise(view, new DataObject(DataFormats.FileDrop, new[] { third.ToUpperInvariant() }), DragDrop.PreviewDropEvent);
        Require(shell.State.CdMusicTracks.Count == 2, "a track already in the list is not added twice");

        // A mixed drop on a picture-only CD project imports the images and leaves the layout alone.
        string cover = Path.Combine(directory, "cover.png");
        DataObject mixed = new(DataFormats.FileDrop, new[] { cover, third });
        shell.State.CdMusicTracks = [];
        shell.State.CdExtraEnabled = false;
        Raise(view, mixed, DragDrop.PreviewDropEvent);
        Require(!shell.State.CdExtraEnabled && shell.State.CdMusicTracks.Count == 0,
            "a mixed drop does not turn a picture-only project into a CD-Extra one");
        shell.State.CdExtraEnabled = true;
        Raise(view, mixed, DragDrop.PreviewDropEvent);
        Require(shell.State.CdMusicTracks.SequenceEqual(new[] { third }),
            "a mixed drop on a CD-Extra project also adds its music");

        shell.State.CdMusicTracks = [];
        shell.State.CdExtraEnabled = false;
        shell.Design.SetDiscFamily(OpticalDiscFamily.Dvd);
        Require(Raise(view, files, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None
                && Raise(view, files, DragDrop.PreviewDropEvent).Effects == DragDropEffects.None
                && shell.State.CdMusicTracks.Count == 0 && !shell.State.CdExtraEnabled,
            "DVD projects ignore WAV drops");
        // Settle the page inside the test: deferred bindings must not run after the
        // test (or the process) has moved on.
        Size size = new(1440, 1100);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
    }

    // WPF creates these arguments internally during an OLE drag session.
    private static DragEventArgs Raise(UIElement target, IDataObject data, RoutedEvent routedEvent,
        DragDropEffects allowed = DragDropEffects.Copy)
    {
        DragEventArgs args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [data, DragDropKeyStates.LeftMouseButton, allowed, target, new Point()], null)!;
        args.RoutedEvent = routedEvent;
        target.RaiseEvent(args);
        return args;
    }

    /// <summary>The design page's tone preview in CD-Extra mode: bands for a valid plan, the
    /// problem for an invalid one, and a plain audio track that ignores a legacy long label.</summary>
    private static async Task TestTonePreviewAsync(string directory)
    {
        string source = Path.Combine(directory, "source.png");
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher, new NoDeviceBurner());
        try
        {
            ProjectDocument document = new(ProjectJsonStore.CurrentSchemaVersion, "CD-Extra tone", ProjectDocument.CdFamily,
                new ProjectGeometry(24.5, 32, 40_000, UsableSectors: 39_998),
                [new ProjectLayer("layer", "picture", source, true, 0, 0, 0, 1)],
                new ProjectOutputs(Path.Combine(directory, "tone.iso"), false, string.Empty, "CD_EXTRA", false),
                null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ToneProfile.LegacyBinary, null,
                new ProjectCdExtraSettings(true, [new ProjectMusicTrack(Path.Combine(directory, "01.wav")),
                    new ProjectMusicTrack(Path.Combine(directory, "02.wav"))]));
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, document);
            shell.IsProjectOpen = true;
            shell.Design.SetDiscFamily(OpticalDiscFamily.CompactDisc);
            ProjectSession.ApplyToState(document, shell.State);
            shell.SelectWorkspace(Workspace.Design);
            shell.Design.LoadLayers(document.Layers);
            ToneViewModel tone = shell.Design.Tone;
            await Until(() => tone.IsTonePreviewReady && tone.TonePreviewImage is not null && tone.HasMusicRegionOverlay,
                tone, "CD-Extra bands");
            shell.State.CdMusicTracks = [Path.Combine(directory, "broken.wav")];
            await Until(() => !tone.HasTonePreview && !tone.IsTonePreviewReady
                && tone.TonePreviewStatus.Contains("有音乐文件无法使用", StringComparison.Ordinal), tone, "invalid plan");
            shell.State.CdExtraEnabled = false;
            shell.State.CdVolumeLabel = "MY_HOLIDAY_DISC_2025";
            await Until(() => tone.IsTonePreviewReady && tone.TonePreviewImage is not null && !tone.HasMusicRegionOverlay,
                tone, "plain audio track with a legacy label");
        }
        finally
        {
            shell.IsProjectOpen = false;
            shell.Design.ClearLayers();
        }
    }

    private static async Task Until(Func<bool> condition, ToneViewModel tone, string name)
    {
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("CD-Extra UI: tone preview timed out: " + name + "; " + tone.TonePreviewStatus);
            await Task.Delay(20);
        }
    }

    private static void Wait(Task task)
    {
        DispatcherFrame frame = new();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Music on the outer rings through the app: the default position, the output
    /// format, the guide plan and summary, the project field, and the export and burn
    /// requests, whose bytes are the same recording.</summary>
    private static void TestOuterMusic(string directory)
    {
        string first = Path.Combine(directory, "01.wav");
        string second = Path.Combine(directory, "02.wav");
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
        DiscParametersState state = shell.State;
        shell.Design.SetDiscFamily(OpticalDiscFamily.CompactDisc);
        shell.IsProjectOpen = true;
        state.CdInnerRadius = "24.5";
        state.CdOuterRadius = "32";
        state.CdSectors = "40000";
        state.CdUsableSectors = "39998";
        state.CdDataDirectory = string.Empty;
        state.CdOutputPath = "D:\\out\\disc.raw";
        Require(state.CdMusicAtOuterRing && !state.IsCdMusicOuter, "the outer position is the default; without music it changes nothing");

        CdMusicViewModel music = shell.Design.Music;
        music.AddTracks([first, second]);
        Require(state.IsCdMusicOuter && !state.IsCdMusicInner && state.IsCdAudioMode && !state.UsesCdVolumeLabel
                && state.CdOutputPath == "D:\\out\\disc.wav",
            "music on the outer rings exports one WAV track");
        state.CdMusicAtOuterRing = false;
        Require(state.IsCdMusicInner && !state.IsCdAudioMode && state.UsesCdVolumeLabel
                && state.CdOutputPath == "D:\\out\\disc.iso",
            "moving the music to the inner rings makes a CD-Extra disc with an ISO output");
        state.CdMusicAtOuterRing = true;
        Require(state.CdOutputPath == "D:\\out\\disc.wav", "and back to the WAV track");
        state.CdExtraEnabled = false;
        Require(state.CdOutputPath == "D:\\out\\disc.raw", "without music the RAW choice returns");
        state.CdExtraEnabled = true;

        RunGuide(shell.Design);
        Require(shell.Design.CdExtraPlan is { IsValid: true, DataPlan: null, Layout: { MusicAtOuterRing: true } layout }
                && layout.MusicEndLbaExclusive == 39_998 && layout.PictureSectors == 39_998 - 3_501
                && shell.Design.CdExtraRings is { } rings
                && Math.Abs(rings.PictureOuterMm - rings.MusicInnerMm) < 1e-9 && rings.MusicOuterMm > rings.MusicInnerMm
                && Math.Abs(rings.PictureInnerMm - 24.5) < 1e-6,
            "the guide puts the picture inside and the music ring at the edge");
        Require(music.HasLayout && !music.HasSessionGap && music.GapShareText.Length == 0 && !music.HasProblem
                && music.MusicShareWidth.Value == 0 && music.GapShareWidth.Value == 0
                && music.OuterMusicShareWidth.Value > 0 && music.PictureShareWidth.Value > music.OuterMusicShareWidth.Value
                && music.MusicShareText.Length > 0 && music.CanvasLegendText.Length > 0,
            "the card shows picture and music shares without a session gap");
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, CdDocument(null));
        Require(shell.Burn.IsCdOuterMusicBurn && !shell.Burn.IsCdExtraBurn, "the burn page writes one audio session");
        state.CdMusicAtOuterRing = false;
        Require(shell.Burn.IsCdExtraBurn && !shell.Burn.IsCdOuterMusicBurn, "the inner position selects the two-session burn");
        state.CdMusicAtOuterRing = true;

        // An audio disc cannot carry disc files; the rings stay visible next to the problem.
        state.CdDataDirectory = Path.Combine(directory, "files");
        RunGuide(shell.Design);
        Require(shell.Design.CdExtraPlan is { IsValid: false, Problem: CdExtraPlanProblem.FilesWithOuterMusic, Layout: not null }
                && music.HasProblem && music.HasLayout && shell.Design.DataRegions is null,
            "disc files with outer music are reported, not planned");
        string composite = Path.Combine(directory, "outer-source.png");
        CdExtraLayoutTests.WriteImage(composite);
        Throws<ArgumentException>(() => shell.Export.CreateCdImageRequest(composite), "exporting outer music with disc files");
        state.CdDataDirectory = string.Empty;
        RunGuide(shell.Design);
        Require(!music.HasProblem, "clearing the folder clears the problem");
        CdImageRequest export = shell.Export.CreateCdImageRequest(composite);
        Require(export.MusicAtOuterRing && export.MusicTracks!.SequenceEqual(new[] { first, second })
                && export.Session2StartLba is null && export.OutputPath.EndsWith(".wav", StringComparison.Ordinal),
            "the export request carries the position");

        // Project field: written with the position, absent means inner (earlier files).
        ProjectDocument document = CdDocument(null) with
        {
            Outputs = new ProjectOutputs("D:\\out\\disc.wav", true, string.Empty, "CD_IMAGE", true),
        };
        ProjectCdExtraSettings? captured = ProjectSession.CaptureCdExtra(document, state);
        Require(captured is { Enabled: true, MusicAtOuterRing: true, Tracks.Count: 2 }, "save captures the position");
        string project = Path.Combine(directory, "outer.discproj");
        ProjectJsonStore.Save(project, document with { CdExtra = captured });
        Require(File.ReadAllText(project).Contains("\"musicAtOuterRing\": true", StringComparison.Ordinal),
            "the project file records the outer position");
        DiscParametersState reopened = new(Path.Combine(directory, "outer-presets.json"));
        ProjectSession.ApplyToState(ProjectJsonStore.Load(project), reopened);
        Require(reopened.IsCdMusicOuter && reopened.CdOutputPath == "D:\\out\\disc.wav", "reopening restores the outer position");
        ProjectSession.ApplyToState(document with
        {
            CdExtra = new ProjectCdExtraSettings(true, [new ProjectMusicTrack(first)]),
        }, reopened);
        Require(reopened.IsCdMusicInner && reopened.CdOutputPath == "D:\\out\\disc.iso",
            "a project without the field keeps its music on the inner rings");
        ProjectSession.ApplyToState(CdDocument(null), reopened);
        Require(reopened.CdMusicAtOuterRing && !reopened.CdExtraEnabled, "a project without music returns to the default position");

        // The burn request records exactly what the export writes.
        CdDiscParameters parameters = new(24.5, 27.5, 15_000, UsableSectors: 14_998, SourceImageOuterRadiusMm: 27.5);
        CdExtraPlan plan = CdExtraPlanner.Plan(parameters, [first, second], null, string.Empty, false, musicAtOuterRing: true);
        OpticalBurnRequest request = BurnViewModel.CreateCdOuterMusicBurnRequest(composite, parameters, plan,
            interleave: true, ToneProfile.Photo, "drive", null);
        request.Validate();
        Require(request.MediaKind == OpticalBurnMediaKind.CdAudio && request.ContentLength == 14_998L * 2352
                && request.CdAudioLayout is { PregapSectors: 14_998 - 3_501 } tracks
                && tracks.TrackSectors.SequenceEqual(new long[] { 2_000, 1_501 }),
            "one audio burn: the picture as the pre-gap of track 1, then both tracks");
        using MemoryStream burned = new();
        request.ProduceContent(burned, CancellationToken.None);
        string wave = Path.Combine(directory, "outer.wav");
        CdImageGenerator.Generate(new CdImageRequest(composite, wave, parameters, ToneProfile.Photo,
            Interleave: true, MusicTracks: [first, second], MusicAtOuterRing: true));
        Require(burned.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(wave).AsSpan(44)),
            "the burned content equals the exported WAV");
        CdExtraPlan withFiles = CdExtraPlanner.Plan(parameters, [first, second], Path.Combine(directory, "files"),
            string.Empty, false, musicAtOuterRing: true);
        Throws<ArgumentException>(() => BurnViewModel.CreateCdOuterMusicBurnRequest(composite, parameters, withFiles,
            true, ToneProfile.Photo, "drive", null), "a burn request for outer music with disc files");
        Throws<ArgumentException>(() => BurnViewModel.CreateCdOuterMusicBurnRequest(composite, parameters,
            CdExtraPlanner.Plan(parameters, [first, second], null, "CD_EXTRA", false), true, ToneProfile.Photo, "drive", null),
            "an inner-music plan is not an outer-music burn");
        Require(!CdExtraPlanner.Plan(parameters, [first, second, first, second, first, second, first, second, first],
                null, string.Empty, false, musicAtOuterRing: true).IsValid,
            "music longer than the disc leaves no picture");
    }

    private static void RunPreview(LivePreviewViewModel preview)
    {
        FieldInfo revision = typeof(LivePreviewViewModel).GetField("_livePreviewRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        revision.SetValue(preview, (int)revision.GetValue(preview)! + 1);
        Task task = (Task)typeof(LivePreviewViewModel).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(preview, [])!;
        Wait(task);
    }

    private static void RaisePropertyChanged(object target, string property)
        => typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject)
            .GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)])!
            .Invoke(target, [property]);

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

    private static void RunGuide(DesignViewModel design)
    {
        MethodInfo method = typeof(DesignViewModel).GetMethod("RunDataRegionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task task = (Task)method.Invoke(design, [])!;
        DispatcherFrame frame = new();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("CD-Extra UI: expected " + typeof(T).Name + " for " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD-Extra UI: " + message);
    }
}
