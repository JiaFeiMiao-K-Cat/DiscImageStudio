using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class AppCubicMediaTests
{
    private static readonly (string Name, OpticalDiscFamily Family, CalibrationDiscKind Kind, long Sectors, double Density)[] Media =
    [
        ("CD", OpticalDiscFamily.CompactDisc, CalibrationDiscKind.Cd, 359849, 1200),
        ("DVD", OpticalDiscFamily.Dvd, CalibrationDiscKind.Dvd, 2295104, 133.3),
        ("BD", OpticalDiscFamily.BluRay, CalibrationDiscKind.BluRay, 12219392, 74.5),
    ];

    internal static void Run()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                DirectoryInfo directory = Directory.CreateTempSubdirectory("disc-cubic-media-");
                try
                {
                    TestProjectAndPresetPersistence(directory.FullName);
                    await TestFrozenPhotoSources(directory.FullName, dispatcher);
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    directory.Delete(recursive: true);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("app-cubic-media: CD/DVD/BD project/preset roundtrip, controls and frozen photo sources passed");
    }

    private static void TestProjectAndPresetPersistence(string directory)
    {
        DiscParametersState state = new(Path.Combine(directory, "presets.json"));
        foreach (var media in Media)
        {
            ProjectGeometry geometry = new(24, 58, media.Sectors, .00012, -.00008, .00004);
            ProjectGeometry resolved = geometry with
            {
                UsableSectors = media.Family switch
                {
                    OpticalDiscFamily.CompactDisc => 359_847,
                    OpticalDiscFamily.Dvd => 2_294_496,
                    _ => 12_219_392,
                },
            };
            ProjectDocument project = new(ProjectJsonStore.CurrentSchemaVersion, media.Name,
                media.Family == OpticalDiscFamily.BluRay ? ProjectDocument.BluRayFamily : media.Name.ToLowerInvariant(),
                geometry, [], new(Path.Combine(directory, media.Name + ".iso"), false, "", "TEST", false),
                null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            string path = Path.Combine(directory, media.Name + ".discproj");
            ProjectJsonStore.Save(path, project);
            ProjectSession.ApplyToState(ProjectJsonStore.Load(path), state);
            Require(ProjectSession.CaptureGeometry(project, state) == resolved,
                media.Name + " project preserves all geometry and resolves the absent usable count");
            Require(state.GetPitchSummary(media.Family, actual: true).Contains("x³"), media.Name + " measured preview starts with valid cubic geometry");

            state.AddUserPreset(media.Family, media.Name + " cubic", 24, 58, media.Sectors,
                geometry.PitchLinear, geometry.PitchQuadratic, geometry.PitchCubic);
            DiscPresetJsonDocument saved = DiscPresetJsonStore.Load(Path.Combine(directory, "presets.json"));
            switch (media.Family)
            {
                case OpticalDiscFamily.CompactDisc:
                    var cd = saved.CdPresets.Single(p => p.DisplayName == media.Name + " cubic");
                    Require((cd.PitchLinear, cd.PitchQuadratic, cd.PitchCubic) == (.00012, -.00008, .00004), "CD preset JSON");
                    state.ApplyCdDiscPreset(state.CdPresets.Single(p => p.Id == cd.Id));
                    break;
                case OpticalDiscFamily.Dvd:
                    var dvd = saved.DvdPresets.Single(p => p.DisplayName == media.Name + " cubic");
                    Require((dvd.PitchLinear, dvd.PitchQuadratic, dvd.PitchCubic) == (.00012, -.00008, .00004), "DVD preset JSON");
                    state.ApplyDvdDiscPreset(state.DvdPresets.Single(p => p.Id == dvd.Id));
                    break;
                default:
                    var bd = saved.BluRayPresets!.Single(p => p.DisplayName == media.Name + " cubic");
                    Require((bd.PitchLinear, bd.PitchQuadratic, bd.PitchCubic) == (.00012, -.00008, .00004), "BD preset JSON");
                    state.ApplyBluRayDiscPreset(state.BluRayPresets.Single(p => p.Id == bd.Id));
                    break;
            }
            Require(ProjectSession.CaptureGeometry(project, state) == resolved,
                media.Name + " preset restores all geometry and the same usable count");

            ProjectDialogViewModel dialog = new(state, new(EditMode: true, PreselectFamily: media.Family),
                new RecentTemplateStore(Path.Combine(directory, "recent.json")))
            {
                Name = media.Name, SaveDirectory = directory, InnerRadiusText = "24", OuterRadiusText = "58",
                SectorsText = media.Sectors.ToString(CultureInfo.InvariantCulture),
                PitchLinearText = "0.00012", PitchQuadraticText = "-0.00008", PitchCubicText = "0.00004",
            };
            dialog.ConfirmCommand.Execute(null);
            Require(dialog.Result is { PitchLinear: .00012, PitchQuadratic: -.00008, PitchCubic: .00004 },
                media.Name + " project dialog retains the edited cubic terms: " + dialog.ErrorMessage);
        }
    }

    private static async Task TestFrozenPhotoSources(string directory, Dispatcher dispatcher)
    {
        ShellViewModel shell = new(dispatcher);
        CalibrationViewModel model = shell.Calibration;
        try
        {
            foreach (var media in Media)
            {
                CalibrationParameters burned = new(media.Kind, 24, 58, media.Sectors, media.Density,
                    .00012, -.00008, .00004);
                CalibrationTarget target = CalibrationTarget.Create(burned, artwork: CalibrationArtwork.ReferenceCross);
                CalibrationPhotoSession photo = Photo();
                string path = Path.Combine(directory, media.Name + ".calibration.json");
                CalibrationSessionJson.Save(path, new(target, [], new(FitDvdPitch: true), Photo: photo));
                await model.LoadSessionFromAsync(path);
                Require(model.Target?.Id == target.Id && model.IsFineStep && model.CanFitDvdPitch,
                    media.Name + " restores a cubic photo target with fitting controls");
                Require(model.SetupPitchLinear == burned.PitchLinear.ToString("G17", CultureInfo.InvariantCulture)
                    && model.SetupPitchQuadratic == burned.PitchQuadratic.ToString("G17", CultureInfo.InvariantCulture)
                    && model.SetupPitchCubic == burned.PitchCubic.ToString("G17", CultureInfo.InvariantCulture),
                    media.Name + " session restores all generation coefficients");

                // Isolate application and persistence from the numerical solver (covered by
                // the independent multi-round calibration regression). Install a confirmed
                // result exactly at the command boundary after its preview has completed.
                CalibrationParameters fitted = burned with
                {
                    InnerRadiusMm = 24.001, OuterRadiusMm = 57.998,
                    PitchLinear = .00015, PitchQuadratic = -.00009, PitchCubic = .00005,
                };
                var candidate = new CalibrationViewModel.CoarseCandidate("fit", fitted, 0, .001);
                Set(model, "_installing", true);
                model.CoarseCandidates = [candidate];
                model.SelectedCoarseCandidate = candidate;
                Set(model, "_installing", false);
                Set(model, "_fit", new CalibrationFitResult(true, fitted, 0, .001,
                    [new(fitted, 0, .001)], []));
                Set(model, "_previewReady", true);
                model.ContourConfirmed = true;
                Require(model.ApplyParametersCommand.CanExecute(null), media.Name + " apply is ready: " + model.ApplyParametersHint);
                model.ApplyParametersCommand.Execute(null);
                Require(model.SetupPitchLinear == fitted.PitchLinear.ToString("G17", CultureInfo.InvariantCulture)
                    && model.SetupPitchQuadratic == fitted.PitchQuadratic.ToString("G17", CultureInfo.InvariantCulture)
                    && model.SetupPitchCubic == fitted.PitchCubic.ToString("G17", CultureInfo.InvariantCulture),
                    media.Name + " applying a result updates all next-target coefficients");

                model.SaveSessionTo(path);
                CalibrationSession restored = CalibrationSessionJson.Load(path);
                Require(restored.Target.Id == target.Id && restored.Target.Parameters == burned,
                    media.Name + " applying a fit never replaces the burned photo-source geometry");
                Require(restored.Photo?.ImagePngBase64 == photo.ImagePngBase64,
                    media.Name + " the same photo stays attached to the original source");

                model.CreateTargetCommand.Execute(null);
                Require(model.Target is { } next && next.Id != target.Id
                    && next.Parameters.InnerRadiusMm == fitted.InnerRadiusMm && next.Parameters.OuterRadiusMm == fitted.OuterRadiusMm
                    && (next.Parameters.PitchLinear, next.Parameters.PitchQuadratic, next.Parameters.PitchCubic)
                        == (fitted.PitchLinear, fitted.PitchQuadratic, fitted.PitchCubic) && model.PhotoImage is null,
                    media.Name + " only explicit target creation adopts fitted geometry and clears the old photo");
                if (media.Kind == CalibrationDiscKind.BluRay)
                {
                    var output = CalibrationViewModel.BluRayParameters(model.Target!.Parameters);
                    Require((output.PitchLinear, output.PitchQuadratic, output.PitchCubic)
                        == (fitted.PitchLinear, fitted.PitchQuadratic, fitted.PitchCubic), "BD calibration output preserves coefficients");
                }

                model.SelectCoarseModeCommand.Execute(null);
                model.CreateTargetCommand.Execute(null);
                Require(model.Target?.HasWideAddressBands == true, media.Name + " coarse target available");
                model.IsPhotoStep = false;
                Set(model, "_photoTransform", new CalibrationPhotoTransform([1, 0, 0, 0, 1, 0, 0, 0, 1]));
                CalibrationParameters coarse = fitted with { InnerRadiusMm = 24.002, PitchLinear = 0, PitchQuadratic = 0, PitchCubic = 0 };
                Set(model, "_ringFit", new CalibrationFitResult(true, coarse, 0, .001,
                    [new(coarse, 0, .001)], []));
                Require(model.ApplyCoarseParametersCommand.CanExecute(null), media.Name + " coarse estimate is applicable");
                model.ApplyCoarseParametersCommand.Execute(null);
                Require(model.SetupPitchLinear == fitted.PitchLinear.ToString("G17", CultureInfo.InvariantCulture)
                    && model.SetupPitchQuadratic == fitted.PitchQuadratic.ToString("G17", CultureInfo.InvariantCulture)
                    && model.SetupPitchCubic == fitted.PitchCubic.ToString("G17", CultureInfo.InvariantCulture),
                    media.Name + " applying coarse radii retains the setup's independently fitted pitch");
            }
        }
        finally { model.OnWindowClosed(); }
    }

    private static CalibrationPhotoSession Photo()
    {
        const int size = 128;
        BitmapSource image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, new byte[size * size], size);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using MemoryStream stream = new();
        encoder.Save(stream);
        CalibrationPoint[] Boundary(double radius) => Enumerable.Range(0, 8).Select(i =>
            new CalibrationPoint(63.5 + radius * Math.Cos(i * Math.PI / 4), 63.5 + radius * Math.Sin(i * Math.PI / 4))).ToArray();
        return new(Convert.ToBase64String(stream.ToArray()), Boundary(60), Boundary(7.5), 60, true, []);
    }

    private static void Set(object instance, string name, object value) => instance.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("app-cubic-media: " + message);
    }
}
