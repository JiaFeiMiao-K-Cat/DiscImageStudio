using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Controls;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class CalibrationPhotoViewModelTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-photo-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            TestWorkflow(directory); TestObservedRadiusDomain(directory); TestRingWorkflow(directory);
            TestHighResolutionSession(directory);
            TestClockWorkflow(directory); TestArcWorkflow(directory); TestWideBands(directory);
            TestDataDiscWideBands(directory, "DVD", 40);
            TestDataDiscWideBands(directory, "BD", 60);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("calibration-photo-view-model: passed (perspective photo, complete observed-radius curve, 10-point preservation, fit/apply, portable sessions, invalidation)");
    }

    private static void TestWorkflow(string directory)
    {
        var state = new DiscParametersState
        {
            CdInnerRadius = "24.5", CdOuterRadius = "56.8", CdSectors = "300000", CdInterleave = true,
        };
        var model = CreateModel(state);
        Require(ReferenceEquals(model.SelectedPhysicalDiscSize, model.PhysicalDiscSizeChoices[0])
            && ReferenceEquals(model.SelectedCoarseSearchRange, model.CoarseSearchRangeChoices[0]),
            "default disc size and search range reference the listed items so both selectors start selected");
        Require(model.SelectFineModeCommand.CanExecute(null), "fine mode can be chosen before creating any target or photo");
        model.SelectFineModeCommand.Execute(null);
        model.CreateTargetCommand.Execute(null);
        var target = model.Target ?? throw new InvalidOperationException(model.LastStatusMessage);
        Require(target.SchemaVersion == 14 && target.Artwork == CalibrationArtwork.ReferenceCross && target.UsesReferencePattern && model.IsPhotoStep
            && !model.SolveCommand.CanExecute(null), "new fine target starts in photo step");
        Require(model.CanvasOuterRadiusMm == CalibrationReferencePattern.CanvasRadiusMm && model.PhotoCanvasRadiusMm == 63,
            "disc-preview background remains 120 mm while target scaling and corrected-photo margin are independent");
        Require(model.CurrentParametersSummary.Contains("内半径", StringComparison.Ordinal)
            && model.CurrentParametersSummary.Contains("外半径", StringComparison.Ordinal),
            "the target button names the project parameters a new target is built from");
        var actual = target.Parameters with { InnerRadiusMm = 24.507, OuterRadiusMm = 56.791 };
        var camera = new CalibrationPhotoTransform([6.5, .8, 460, .35, 5.4, 380, .0018, -.0025, 1]);
        string photoPath = Path.Combine(directory, "disc.png");
        WritePhoto(photoPath, target, actual, camera);
        Await(model, "ImportPhotoFromAsync", photoPath);
        Require(model.PhotoImage is not null && model.PhotoBoundaryPoints.Count == 16 && model.IsPhotoStep,
            "photo import installs editable physical boundary guides");
        Require(!model.ConfirmRectificationCommand.CanExecute(null), "import alone cannot confirm perspective");
        foreach (var boundary in model.PhotoBoundaryPoints.ToArray())
        {
            double radius = boundary.Boundary == CalibrationPhotoBoundaryKind.OuterEdge ? 60 : 7.5;
            double angle = -Math.PI / 2 + boundary.Index * Math.PI / 4;
            var pixel = camera.DiscToSource(new(radius * Math.Cos(angle), radius * Math.Sin(angle)));
            model.BoundaryEditedCommand.Execute(new(boundary.Boundary, boundary.Index, pixel.X, pixel.Y));
        }
        model.RectifyPhotoCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.IsRectificationReady && model.PreviewRectification && model.RectifiedPhoto is not null,
            "joint contour calculation produces a read-only corrected preview: " + model.LastStatusMessage);
        model.ConfirmRectificationCommand.Execute(null);
        Require(model.IsFineStep && !model.PreviewRectification && !model.SolveCommand.CanExecute(null),
            "target generation and photo confirmation preserve the chosen fine stage");
        var corrected = Field<CalibrationPhotoTransform>(model, "_photoTransform");
        var samples = Enumerable.Range(0, 70).Select(i =>
        {
            double radius = target.Parameters.InnerRadiusMm + (target.Parameters.OuterRadiusMm - target.Parameters.InnerRadiusMm) * (.08 + .84 * i / 69);
            var disc = target.MapPoint(new(0, -radius), actual, .61);
            return corrected.SourceToDisc(camera.DiscToSource(disc));
        }).ToArray();
        // Enter alternate samples and then fill the gaps: IDs remain stable while the line is radially ordered.
        foreach (int parity in new[] { 0, 1 })
        foreach (var p in samples.Where((_, i) => i % 2 == parity)) model.TraceEditedCommand.Execute(new(0, -1, p.X, p.Y));
        Require(model.TracePoints.Count == 70 && model.SolveCommand.CanExecute(null), "one curve alone enables fitting");
        Require(model.TracePoints.Zip(model.TracePoints.Skip(1)).All(pair => Radius(pair.First) <= Radius(pair.Second)),
            "inserting points into gaps preserves radial curve order");
        var first = model.TracePoints[0];
        model.TraceEditedCommand.Execute(new(0, first.Index, first.XMm + .001, first.YMm));
        Require(model.TracePoints.Count == 70 && model.TracePoints.Single(p => p.Index == first.Index).XMm == first.XMm + .001,
            "dragging edits the stable selected point without creating a duplicate");
        model.TraceEditedCommand.Execute(new(0, first.Index, first.XMm, first.YMm));
        model.SolveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.CoarseCandidates.Count > 0 && model.CrossPredictionContours.Count == 1,
            "fit produces a continuous one-arm overlay: " + model.LastStatusMessage);
        var best = model.SelectedCoarseCandidate ?? throw new InvalidOperationException("No candidate.");
        Require(Math.Abs(best.Parameters.InnerRadiusMm - actual.InnerRadiusMm) < .0002
            && Math.Abs(best.Parameters.OuterRadiusMm - actual.OuterRadiusMm) < .0002, "single photographed curve recovers radii");
        Require(!model.ApplyParametersCommand.CanExecute(null), "fitting alone does not approve the contour");
        model.ContourConfirmed = true;
        Require(model.ApplyParametersCommand.CanExecute(null), "completed overlay and contour confirmation enable application");
        var prediction = model.CrossPredictionContours;
        model.SelectCoarseModeCommand.Execute(null);
        Require(!model.IsRingStep && !model.RingEditedCommand.CanExecute(new CalibrationRingEdit(0, null)),
            "switching stage on a new fine target cannot expose archived coarse ring editing");
        model.RingEditedCommand.Execute(new(0, target.VisibleRings[0].InnerRadiusMm));
        model.SelectFineModeCommand.Execute(null);
        Require(ReferenceEquals(best, model.SelectedCoarseCandidate) && ReferenceEquals(prediction, model.CrossPredictionContours)
            && model.ContourConfirmed && model.ApplyParametersCommand.CanExecute(null),
            "visiting and editing independent ring measurements keeps the unseeded fine result and contour confirmation");
        model.SelectCoarseModeCommand.Execute(null);
        model.RingEditedCommand.Execute(new(0, null));
        model.SelectFineModeCommand.Execute(null);
        model.SelectedDiscType = "BD"; // The editable setup can differ from the photographed target.
        model.ApplyParametersCommand.Execute(null);
        Require(model.IsCdSetup && Math.Abs(double.Parse(state.CdInnerRadius, CultureInfo.InvariantCulture) - actual.InnerRadiusMm) < .0002,
            "applying the chosen candidate selects the photographed family for the next preset or target");
        Require(model.Target!.Parameters == target.Parameters, "application preserves the burned target snapshot");

        string sessionPath = Path.Combine(directory, "portable.calibration.json");
        Invoke(model, "SaveSessionTo", sessionPath);
        var session = CalibrationSessionJson.Load(sessionPath);
        Require(session.Photo is { RectificationConfirmed: true } && session.Photo.Traces.Single().Points.Count == 70
            && session.Observations.Count == 0, "session contains the photo and arbitrary curve samples rather than source corners");
        if (Environment.GetEnvironmentVariable("DISC_CALIBRATION_FIXTURES") is { Length: > 0 } fixtureDirectory)
        {
            Directory.CreateDirectory(fixtureDirectory);
            CalibrationSessionJson.Save(Path.Combine(fixtureDirectory, "calibration-photo-trace.calibration.json"), session);
            CalibrationSessionJson.Save(Path.Combine(fixtureDirectory, "calibration-photo-boundary.calibration.json"),
                session with { Photo = session.Photo! with { RectificationConfirmed = false, Traces = [] }, ConfirmedFit = null });
        }
        File.Delete(photoPath);
        var reopened = CreateModel(new DiscParametersState());
        Await(reopened, "LoadSessionFromAsync", sessionPath);
        Require(reopened.IsTraceStep && reopened.PhotoImage is not null && reopened.RectifiedPhoto is not null
            && reopened.TracePoints.Count == 70 && reopened.Target!.Id == target.Id, "session reopens without the original photo file");
        Require(reopened.ContourConfirmed && reopened.ApplyParametersCommand.CanExecute(null)
            && reopened.SelectedCoarseCandidate?.Parameters == best.Parameters
            && reopened.CrossPredictionContours.Count == 1,
            "reopening a confirmed fit restores the chosen parameters, approval and predicted curve");
        Require(reopened.Target!.Parameters == target.Parameters
            && double.Parse(reopened.Parameters.CdInnerRadius, CultureInfo.InvariantCulture) == best.Parameters.InnerRadiusMm
            && double.Parse(reopened.Parameters.CdPitchLinear, CultureInfo.InvariantCulture) == best.Parameters.PitchLinear
            && double.Parse(reopened.Parameters.CdPitchQuadratic, CultureInfo.InvariantCulture) == best.Parameters.PitchQuadratic
            && double.Parse(reopened.Parameters.CdPitchCubic, CultureInfo.InvariantCulture) == best.Parameters.PitchCubic,
            "reopening restores the confirmed next-disc setup while preserving the photographed target geometry");

        bool cancelledAtPublication = false;
        System.ComponentModel.PropertyChangedEventHandler cancelAtPublication = (_, change) =>
        {
            if (!cancelledAtPublication && reopened.IsBusy && change.PropertyName == nameof(reopened.CoarseCandidates)
                && reopened.CoarseCandidates.Count > 0)
            {
                cancelledAtPublication = true;
                reopened.CancelCommand.Execute(null);
            }
        };
        reopened.PropertyChanged += cancelAtPublication;
        try { reopened.SolveCommand.ExecuteAsync(null).GetAwaiter().GetResult(); }
        finally { reopened.PropertyChanged -= cancelAtPublication; }
        reopened.ContourConfirmed = true;
        Require(cancelledAtPublication && !reopened.IsBusy && reopened.CoarseCandidates.Count == 0
            && !reopened.ApplyParametersCommand.CanExecute(null), "cancelling during result publication cannot expose an applicable candidate");

        model.TraceEditedCommand.Execute(new(0, first.Index, first.XMm, first.YMm, Remove: true));
        Require(model.TracePoints.Count == 69 && model.CoarseCandidates.Count == 0 && !model.ContourConfirmed
            && !model.ApplyParametersCommand.CanExecute(null), "deleting a point invalidates the full fit and approval");
        model.BackToPhotoCommand.Execute(null);
        Require(model.IsPhotoStep && model.TracePoints.Count == 69, "returning to inspect the photo retains the curve until a boundary changes");
        var edge = model.PhotoBoundaryPoints[0];
        model.BoundaryEditedCommand.Execute(new(edge.Boundary, edge.Index, edge.X + .2, edge.Y));
        Require(model.TracePoints.Count == 0 && !model.IsRectificationReady && model.RectifiedPhoto is null
            && !model.ConfirmRectificationCommand.CanExecute(null), "changing a physical edge invalidates rectification and all dependent curve data");
        reopened.SelectedPhysicalDiscSize = reopened.PhysicalDiscSizeChoices[1];
        Require(reopened.IsPhotoStep && reopened.TracePoints.Count == 0 && !reopened.IsRectificationReady
            && reopened.PhotoCanvasRadiusMm == 43, "changing physical disc size resets all downstream coordinates");
        Invoke(model, "OnWindowClosed"); Invoke(reopened, "OnWindowClosed");
    }

    private static double Radius(CalibrationTracePoint p) => p.XMm * p.XMm + p.YMm * p.YMm;

    private static void TestHighResolutionSession(string directory)
    {
        const int width = 4096, height = 3072;
        byte[] pixels = new byte[width * height];
        // Sensor-like noise keeps the lossless image large enough to exercise the
        // former 14M Base64 / 16M JSON limits, rather than only the dimension limits.
        new Random(7391).NextBytes(pixels);
        for (int y = height / 2 - 4; y <= height / 2 + 4; y++)
        for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)(((x / 2) & 1) * 255);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        string photoPath = Path.Combine(directory, "high-resolution.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(photoPath)) encoder.Save(stream);

        var model = CreateModel(new DiscParametersState());
        var reopened = CreateModel(new DiscParametersState());
        try
        {
            model.SelectFineModeCommand.Execute(null);
            model.CreateTargetCommand.Execute(null);
            Await(model, "ImportPhotoFromAsync", photoPath);
            Require(model.PhotoImage?.PixelWidth == width && model.PhotoImage.PixelHeight == height,
                "a 12 megapixel photograph reaches the GUI without the former 1600 px resize: " + model.LastStatusMessage);
            var camera = new CalibrationPhotoTransform([23, 0, width / 2.0, 0, 23, height / 2.0, 0, 0, 1]);
            foreach (var boundary in model.PhotoBoundaryPoints.ToArray())
            {
                double radius = boundary.Boundary == CalibrationPhotoBoundaryKind.OuterEdge ? 60 : 7.5;
                double angle = -Math.PI / 2 + boundary.Index * Math.PI / 4;
                var point = camera.DiscToSource(new(radius * Math.Cos(angle), radius * Math.Sin(angle)));
                model.BoundaryEditedCommand.Execute(new(boundary.Boundary, boundary.Index, point.X, point.Y));
            }
            Require(model.PhotoBoundaryPoints.Any(point => point.X > 3000 || point.Y > 2000),
                "full-resolution source coordinates survive editing beyond the old session limit");
            model.RectifyPhotoCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Require(model.IsRectificationReady && model.RectifiedPhoto is { PixelWidth: > 2700 },
                "rectification uses the photographed disc's pixel density instead of a fixed 1400 px output: " + model.LastStatusMessage);
            model.ConfirmRectificationCommand.Execute(null);
            model.TraceEditedCommand.Execute(new(0, -1, 12.25, -30.5));
            model.TraceEditedCommand.Execute(new(0, -1, 15.75, -45.125));
            var boundaryPoints = model.PhotoBoundaryPoints.ToArray();
            var traces = model.TracePoints.ToArray();
            Require(traces.Length == 2, "fine measurement coordinates can be entered on the high-resolution corrected photo");
            int correctedSize = model.RectifiedPhoto!.PixelWidth;
            byte[] correctedRow = ReadMiddleRow(model.RectifiedPhoto);
            Require(correctedRow.Max() - correctedRow.Where((_, i) => i % 4 != 3).Min() > 200,
                "fine black and white photograph detail remains visible after rectification");

            string sessionPath = Path.Combine(directory, "high-resolution.calibration.json");
            model.SaveSessionTo(sessionPath);
            Require(File.Exists(sessionPath), "the full-resolution photo and coordinates can be saved: " + model.LastStatusMessage);
            Require(Field<string>(model, "_photoPng").Length > 14_000_000 && new FileInfo(sessionPath).Length > 16_000_000,
                "the portable high-resolution fixture exceeds both former encoded-photo and session-file limits");
            if (Environment.GetEnvironmentVariable("DISC_CALIBRATION_FIXTURES") is { Length: > 0 } fixtureDirectory)
            {
                Directory.CreateDirectory(fixtureDirectory);
                File.Copy(sessionPath, Path.Combine(fixtureDirectory, "calibration-photo-high-resolution.calibration.json"), overwrite: true);
            }
            File.Delete(photoPath);
            Await(reopened, "LoadSessionFromAsync", sessionPath);
            Require(reopened.PhotoImage?.PixelWidth == width && reopened.PhotoImage.PixelHeight == height
                && reopened.RectifiedPhoto?.PixelWidth == correctedSize && reopened.IsTraceStep,
                "portable sessions reopen both image resolutions without the original photo: " + reopened.LastStatusMessage);
            Require(reopened.PhotoBoundaryPoints.SequenceEqual(boundaryPoints)
                && reopened.TracePoints.SequenceEqual(traces),
                "saving and reopening preserve pixel-space boundaries and millimetre-space measurements");
            Require(ReadMiddleRow(reopened.RectifiedPhoto!).SequenceEqual(correctedRow),
                "reopening repeats the same full-resolution resampling without an extra lossy resize");
            byte[] sourceRow = ReadMiddleRow(reopened.PhotoImage!);
            for (int x = 128; x < 256; x++)
                Require(sourceRow[x * 4] == pixels[(height / 2) * width + x],
                    "the embedded photograph retains the original narrow stripe pixels");
        }
        finally
        {
            Invoke(model, "OnWindowClosed");
            Invoke(reopened, "OnWindowClosed");
        }
    }

    private static byte[] ReadMiddleRow(BitmapSource bitmap)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[source.PixelWidth * 4];
        source.CopyPixels(new System.Windows.Int32Rect(0, source.PixelHeight / 2, source.PixelWidth, 1), pixels, pixels.Length, 0);
        return pixels;
    }

    private static void TestClockWorkflow(string directory)
    {
        var state = new DiscParametersState { CdInnerRadius = "24.5", CdOuterRadius = "56.8", CdSectors = "359848" };
        var model = CreateModel(state);
        var target = CalibrationTarget.Create(new(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200),
            artwork: CalibrationArtwork.ClockRuler);
        var photo = BlankRectifiedPhoto(60);
        string path = Path.Combine(directory, "clock.calibration.json");
        CalibrationSessionJson.Save(path, new(target, [], Photo: photo));
        Await(model, "LoadSessionFromAsync", path);
        Require(model.IsClockStep && model.ReadClockCommand.CanExecute(null) && !model.IsRingStep,
            "a restored clock photo can be read without ring clicks or a preferred region");
        model.PreferClockAngleCommand.Execute(359.0);
        model.ReadClockCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(!model.ApplyClockCommand.CanExecute(null) && model.ClockReadAngles.Count == 0
            && model.PreferredClockAngle == 359, "a textureless photograph cannot supply initial radii");
        model.SaveSessionTo(path);
        Await(model, "LoadSessionFromAsync", path);
        Require(model.PreferredClockAngle == 359 && model.IsClockStep, "clock search direction survives opening the saved photo");
        model.SelectFineModeCommand.Execute(null);
        Require(!model.IsFineStep && !model.ReadClockCommand.CanExecute(null) && !model.TraceEditedCommand.CanExecute(null),
            "a clock target cannot masquerade as a photograph with traceable cross arms");
        model.CreateTargetCommand.Execute(null);
        Require(model.Target?.HasCrossArms == true && model.PreferredClockAngle is null,
            "creating the fine disc resets the coarse photo and restores cross artwork");
        model.SelectCoarseModeCommand.Execute(null);
        model.CreateTargetCommand.Execute(null);
        Require(model.Target?.HasAngleSweep == true && !model.HasClockTarget,
            "creating a coarse target after a legacy clock session always generates v13");
        Invoke(model, "OnWindowClosed");
    }

    private static void TestArcWorkflow(string directory)
    {
        var state = new DiscParametersState { CdInnerRadius = "24.78", CdOuterRadius = "57.76", CdSectors = "359848" };
        var model = CreateModel(state);
        var target = CalibrationTarget.Create(new(CalibrationDiscKind.Cd, 24.78, 57.76, 359848, 1200),
            artwork: CalibrationArtwork.RadialArcDashes);
        var photo = CalibrationSessionJson.Load(Path.Combine(directory, "clock.calibration.json")).Photo!;
        string path = Path.Combine(directory, "arc.calibration.json");
        CalibrationSessionJson.Save(path, new(target, [], Photo: photo with { PreferredClockAngleDegrees = null }));
        Await(model, "LoadSessionFromAsync", path);
        Require(model.IsRadialArcStep && !model.IsRingStep && !model.IsClockStep && !model.IsFineStep,
            "reopening a rectified arc photograph offers the arc length workflow");

        CalibrationPoint At(CalibrationRadialArc arc, double angle) =>
            new(arc.RadiusMm * Math.Cos(angle), arc.RadiusMm * Math.Sin(angle));
        var inner = target.RadialArcDashes[0];
        model.ArcLengthPointCommand.Execute(At(inner, -inner.HalfAngleRadians));
        model.ArcLengthPointCommand.Execute(At(inner, 0));
        model.ArcComplete = true;
        Require(!model.ArcComplete && !model.SolveArcLengthsCommand.CanExecute(null), "two tentative clicks cannot confirm a mark");
        model.SaveSessionTo(path);
        Await(model, "LoadSessionFromAsync", path);
        Require(model.ArcLengthPoints.Count == 2 && !model.ArcComplete, "partial arc survives a session round trip");
        model.ArcLengthPointCommand.Execute(At(inner, inner.HalfAngleRadians));
        model.ArcComplete = true;
        model.SelectedArcBand = 2;
        var outer = target.RadialArcDashes[2];
        foreach (double angle in new[] { -outer.HalfAngleRadians, 0, outer.HalfAngleRadians })
            model.ArcLengthPointCommand.Execute(At(outer, angle));
        model.ArcComplete = true;
        model.SaveSessionTo(path);
        Await(model, "LoadSessionFromAsync", path);
        Require(model.SelectedArcBand == 2 && model.ArcComplete && model.SolveArcLengthsCommand.CanExecute(null),
            "two confirmed, separated arcs and the selected band survive reopening");
        model.SolveArcLengthsCommand.Execute(null);
        Require(model.ApplyArcLengthsCommand.CanExecute(null), "nominal arc lengths give a usable coarse fit: " + model.ArcFitSummary);
        model.ApplyArcLengthsCommand.Execute(null);
        Require(model.Target?.Parameters == target.Parameters
            && Math.Abs(double.Parse(state.CdInnerRadius, CultureInfo.InvariantCulture) - target.Parameters.InnerRadiusMm) < .00001,
            "applying an arc fit updates setup while preserving the burned target");
        model.UndoArcLengthPointCommand.Execute(null);
        Require(!model.ArcComplete && !model.ApplyArcLengthsCommand.CanExecute(null) && !model.SolveArcLengthsCommand.CanExecute(null),
            "editing an arc invalidates its confirmation and the previous fit");
        model.SelectFineModeCommand.Execute(null);
        model.CreateTargetCommand.Execute(null);
        Require(model.Target?.HasCrossArms == true && model.ArcLengthPoints.Count == 0,
            "the independent fine target uses the cross and clears the previous photograph's marks");
        Invoke(model, "OnWindowClosed");
    }

    private static void TestWideBands(string directory)
    {
        var state = new DiscParametersState { CdInnerRadius = "24.78", CdOuterRadius = "57.76", CdSectors = "359848", CdInterleave = true };
        var model = CreateModel(state);
        model.CreateTargetCommand.Execute(null);
        var target = model.Target!;
        Require(target.SchemaVersion == 13 && model.HasWideBandTarget && model.IsBandRayMode && !model.IsDragRingMode
            && model.IsPhotoStep && model.RingChoices.Select(c => c.Index).SequenceEqual(new[] { 0, 3, 1, 4, 2, 5 }),
            "a new standard CD offers all six physical boundaries in order and defaults to radial clicks");
        var photo = CalibrationSessionJson.Load(Path.Combine(directory, "clock.calibration.json")).Photo!
            with { PreferredClockAngleDegrees = null };
        string path = Path.Combine(directory, "local-bands.calibration.json");
        CalibrationSessionJson.Save(path, new(target, [], Photo: photo));
        Await(model, "LoadSessionFromAsync", path);
        Require(model.IsRingStep && !model.IsFineStep && !model.CanVerifySectorScale && !model.ReadClockCommand.CanExecute(null),
            "the broad band photograph exposes the manual ring workflow");
        var actual = target.Parameters with { InnerRadiusMm = 24.87, OuterRadiusMm = 58.02 };
        double Radius(int index)
        {
            var band = target.VisibleRings[index % 3];
            double r = band.InnerRadiusMm + (index >= 3 ? band.WidthMm : 0);
            var edge = target.MapPoint(new(r, 0), actual);
            return double.Hypot(edge.X, edge.Y);
        }
        void ClickRay(int expected)
        {
            Require(model.SelectedRingIndex == expected, "the next unmeasured boundary follows spatial order");
            model.BandRayPointCommand.Execute(new(Radius(expected), 0));
        }
        ClickRay(0);
        model.BandRayPointCommand.Execute(new(0, Radius(3)));
        Require(!model.RingArcs[3].IsConfirmed && model.SelectedRingIndex == 3, "a click away from the first radial line is rejected");
        model.SelectedRingIndex = 0;
        model.BandRayPointCommand.Execute(new(Radius(0) + 1, 0));
        Require(Math.Abs(model.RingArcs[0].RadiusMm - Radius(0)) < 1e-9, "ordinary clicks do not overwrite an existing edge");
        model.SelectedRingIndex = 3;
        model.SaveSessionTo(path);
        Await(model, "LoadSessionFromAsync", path);
        Require(model.IsBandRayMode && model.SelectedRingIndex == 3 && model.RingArcs[0].Points?.Count == 1,
            "the partial one-ray measurement and selection survive reopening");
        ClickRay(3);
        model.SelectedRingIndex = 2; // The middle band is obscured; mark the outer band first.
        ClickRay(2);
        Require(!model.SolveRingsCommand.CanExecute(null), "v12 needs both edges of the outermost band");
        ClickRay(5);
        Require(model.SolveRingsCommand.CanExecute(null) && model.SelectedRingIndex == 1,
            "finishing the outer band enables a partial fit and returns to the skipped middle band");
        ClickRay(1); ClickRay(4);
        Require(model.SolveRingsCommand.CanExecute(null) && model.RingArcs.Count(r => r.IsConfirmed) == 6,
            "six clicks mark six distinct shared boundaries");
        model.SolveRingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.ApplyCoarseParametersCommand.CanExecute(null) && !model.UseRingSeedCommand.CanExecute(null),
            "wide band boundaries supply initial geometry independently from tracing a cross: " + model.CoarseSummary);
        Require(model.BandIntervalSummary.Contains((Radius(1) - Radius(3)).ToString("0.00"))
            && model.BandIntervalSummary.Contains((Radius(2) - Radius(1)).ToString("0.00")),
            "light-gap and inner-edge spacings are visible in the diagnostics");
        model.ApplyCoarseParametersCommand.Execute(null);
        Require(Math.Abs(double.Parse(state.CdOuterRadius, CultureInfo.InvariantCulture) - actual.OuterRadiusMm) < .0001
            && model.Target?.Parameters == target.Parameters, "local measurements update setup while preserving the burned record");
        model.SelectedRingIndex = 1;
        model.RemoveSelectedBandEdgeCommand.Execute(null);
        Require(!model.ApplyCoarseParametersCommand.CanExecute(null) && model.SolveRingsCommand.CanExecute(null),
            "removing a middle edge invalidates the fit but leaves both anchor bands");
        Require(!model.ThreePointMode && !model.RingPointCommand.CanExecute(new CalibrationPoint(Radius(0), 0))
            && !model.RingEditedCommand.CanExecute(new CalibrationRingEdit(4, null)), "only single-ray input is enabled for a wide band");
        model.RingEditedCommand.Execute(new(4, null));
        Require(model.RingArcs[4].IsConfirmed, "a drag cannot clear a measured boundary");
        model.SelectFineModeCommand.Execute(null);
        Require(!model.TraceEditedCommand.CanExecute(null) && !model.IsFineStep && !model.IsRingStep,
            "a coarse-only band record cannot be traced as a fine cross");
        model.SelectCoarseModeCommand.Execute(null);
        model.SelectedPhysicalDiscSize = model.PhysicalDiscSizeChoices.Single(size => size.OuterRadiusMm == 40);
        Require(!model.RingPointCommand.CanExecute(new CalibrationPoint(28, 0)) && !model.ApplyCoarseParametersCommand.CanExecute(null),
            "an 8 cm photo cannot provide wide-band measurements");

        // A v13 session from the retired three-click method can still be read and saved losslessly.
        var arc = new CalibrationRingArcDraft(1, [new(Radius(1), 0)]);
        var manual = Enumerable.Range(0, 6).Where(i => i != 1)
            .Select(i => new CalibrationRingObservation(i, Radius(i))).ToArray();
        CalibrationSessionJson.Save(path, new(target, [], Photo: photo with { RingObservations = manual, ArcDraft = arc }));
        Await(model, "LoadSessionFromAsync", path);
        model.SaveSessionTo(path);
        var kept = CalibrationSessionJson.Load(path).Photo!;
        Require(model.IsBandRayMode && !model.ThreePointMode && kept.ArcDraft?.Points.Count == 1
            && kept.RingObservations!.All(p => p.Points is null), "reopening a former arc draft preserves its measurements without inventing ray samples");
        model.BandRayPointCommand.Execute(new(Radius(1), 0));
        model.SolveRingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.CoarseSummary.Contains(DiscImageStudio.Localization.L.T("AngleBands.OldMeasurements"))
            && model.RingArcs.Count(p => p.Points?.Count == 1) == 1, "mixed legacy/new observations cannot be treated as six ray clicks");
        model.RestartBandRayCommand.Execute(null);
        Require(model.RingArcs.All(p => !p.IsConfirmed) && model.SelectedRingIndex == 0,
            "v13 can restart all boundaries along a single new ray");

        var old = new CalibrationTarget(11, target.Id, target.Parameters, target.Options, target.Cells, CalibrationArtwork.WideAddressBands);
        var legacyEdges = new[] { 0, 2 }.Select(i => new CalibrationRingObservation(i, old.VisibleRings[i].InnerRadiusMm)).ToArray();
        CalibrationSessionJson.Save(path, new(old, [], Photo: photo with { RingObservations = legacyEdges }));
        Await(model, "LoadSessionFromAsync", path);
        Require(model.Target?.SchemaVersion == 11 && model.SolveRingsCommand.CanExecute(null)
            && model.RingArcs.Count(a => a.IsConfirmed) == 2 && model.RingArcs[2].IsConfirmed && !model.RingArcs[3].IsConfirmed,
            "v11 inner-edge IDs and two-edge legacy fitting are preserved without reinterpretation");
        model.BandRayPointCommand.Execute(new(old.VisibleRings[0].InnerRadiusMm + old.VisibleRings[0].WidthMm, 0));
        Require(model.RingArcs[0].IsConfirmed && model.RingArcs[3].IsConfirmed && !model.SolveRingsCommand.CanExecute(null),
            "v11 can supplement a clear outer edge without overwriting its inner edge");
        model.RestartBandRayCommand.Execute(null);
        Require(model.RingArcs.Count(a => a.IsConfirmed) == 2 && model.SolveRingsCommand.CanExecute(null),
            "restarting a v11 ray preserves the two original measurements");
        Invoke(model, "OnWindowClosed");
    }

    private static CalibrationPhotoSession BlankRectifiedPhoto(double physicalRadius)
    {
        const int size = 240;
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, new byte[size * size], size);
        using var encoded = new MemoryStream();
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        png.Save(encoded);
        CalibrationPoint[] Boundary(double radius) => Enumerable.Range(0, 8).Select(i =>
            new CalibrationPoint(120 + radius * 1.8 * Math.Cos(i * Math.PI / 4),
                                 120 + radius * 1.8 * Math.Sin(i * Math.PI / 4))).ToArray();
        return new(Convert.ToBase64String(encoded.ToArray()), Boundary(physicalRadius), Boundary(7.5),
            physicalRadius, true, [], true);
    }

    private static void TestDataDiscWideBands(string directory, string family, double physicalRadius)
    {
        var state = new DiscParametersState(Path.Combine(directory, "disc-presets.json"));
        state.LoadPresets();
        var model = CreateModel(state);
        model.SelectedDiscType = family;
        if (family == "DVD")
        {
            state.SelectedDvdPreset = state.DvdPresets.Single(p => p.Id == "dvd-5-80mm");
            state.DvdPitchLinear = "0.0001";
        }
        else
        {
            state.SelectedBluRayPreset = state.BluRayPresets.Single(p => p.Id == "bd-r-sl-25gb");
            state.BdInvertMapping = true;
        }
        CalibrationTarget target;
        if (physicalRadius == 40)
        {
            Require(!model.CreateTargetCommand.CanExecute(null) && model.CoarseSetupHint.Length > 0,
                "narrow DVD cannot silently generate a retired coarse pattern");
            // Open the v12 target already burned on a mini DVD.
            target = CalibrationTarget.Create(new(CalibrationDiscKind.Dvd, 24, 38, 714544, 133.3, PitchLinear: .0001),
                artwork: CalibrationArtwork.WideAddressBands);
        }
        else
        {
            Require(model.CreateTargetCommand.CanExecute(null), family + " supports v13");
            model.CreateTargetCommand.Execute(null);
            target = model.Target ?? throw new InvalidOperationException(model.LastStatusMessage);
            Require(target.HasAngleSweep, family + " creates a v13 target");
        }
        string path = Path.Combine(directory, family + "-v12.calibration.json");
        CalibrationSessionJson.Save(path, new(target, [], Photo: BlankRectifiedPhoto(physicalRadius)));
        Await(model, "LoadSessionFromAsync", path);
        Require(model.IsRingStep && model.IsBandRayMode && model.SelectedPhysicalDiscSize.OuterRadiusMm == physicalRadius,
            family + " opens the rectified photo in the matching physical disc size and one-ray mode");

        const double inner = 24.4, angle = .22;
        double outer = physicalRadius - 2.3;
        foreach (int id in new[] { 0, 3, 1, 4, 2, 5 })
        {
            var range = target.WideBandSectorRanges[id % 3];
            double q = (id < 3 ? range.Start : range.EndExclusive) / (double)target.Parameters.Sectors;
            double radius = Math.Sqrt((1-q) * inner * inner + q * outer * outer);
            var point = new CalibrationPoint(radius * Math.Cos(angle), radius * Math.Sin(angle));
            Require(model.SelectedRingIndex == id && model.BandRayPointCommand.CanExecute(point),
                family + " accepts the next boundary on the same ray");
            model.BandRayPointCommand.Execute(point);
        }
        model.SaveSessionTo(path);
        Await(model, "LoadSessionFromAsync", path);
        Require(model.RingArcs.Count(a => a.IsConfirmed && a.Points?.Count == 1) == 6
            && model.IsBandRayMode && model.SolveRingsCommand.CanExecute(null),
            family + " restores six separate one-ray measurements");
        model.SolveRingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.ApplyCoarseParametersCommand.CanExecute(null), family + " rough fit: " + model.CoarseSummary);
        if (target.HasAngleSweep)
            Require(model.CoarseSummary.Contains(DiscImageStudio.Localization.L.T("AngleBands.Weak")),
                family + " reports band-only fallback on a photograph without any angular marks");
        // Edited fine pitch coefficients belong to the next target. BD polarity belongs to the photographed disc.
        if (family == "DVD")
        {
            state.DvdPitchLinear = "-0.0002"; state.DvdPitchQuadratic = "0.0003"; state.DvdPitchCubic = "-0.0001";
        }
        else state.BdInvertMapping = false;
        model.ApplyCoarseParametersCommand.Execute(null);
        Require(model.Target!.Id == target.Id && model.Target.Parameters == target.Parameters,
            family + " keeps the measured generation snapshot intact");
        model.SelectFineModeCommand.Execute(null);
        model.CreateTargetCommand.Execute(null);
        var fine = model.Target!;
        Require(fine.Artwork == CalibrationArtwork.ReferenceCross && fine.Id != target.Id && fine.HasCrossArms
            && fine.Parameters.Sectors == target.Parameters.Sectors
            && Math.Abs(fine.Parameters.InnerRadiusMm - inner) < .0001
            && Math.Abs(fine.Parameters.OuterRadiusMm - outer) < .0001,
            family + " creates an independent fine cross target with the fitted endpoint radii");
        if (family == "DVD")
            Require(fine.Parameters.PitchLinear == -.0002 && fine.Parameters.PitchQuadratic == .0003
                && fine.Parameters.PitchCubic == -.0001, "DVD coarse fitting preserves edited fine pitch");
        else
            Require(fine.Parameters.BluRayInvertMapping, "BD coarse fitting restores the recorded texture polarity");
        Invoke(model, "OnWindowClosed");
    }

    private static void TestRingWorkflow(string directory)
    {
        string path = Path.Combine(directory, "manual-rings.calibration.json");
        var original = CalibrationSessionJson.Load(Path.Combine(directory, "portable.calibration.json"));
        var target = CalibrationTarget.Create(original.Target.Parameters, artwork: CalibrationArtwork.FineCross);
        var actual = target.Parameters with { InnerRadiusMm = 24.507, OuterRadiusMm = 56.791 };
        // This box excludes the physical radii until the ring fit supplies the centre.
        CalibrationSessionJson.Save(path, original with
        {
            Target = target, FitOptions = new(target.Parameters, .001, AutoRange: false), ConfirmedFit = null,
        });
        var state = new DiscParametersState();
        var model = CreateModel(state);
        Await(model, "LoadSessionFromAsync", path);
        var trace = model.TracePoints.ToArray();
        model.SelectCoarseModeCommand.Execute(null);
        Require(model.IsRingStep && !model.SolveCommand.CanExecute(null) && !model.SolveRingsCommand.CanExecute(null),
            "old fine-only photo opens in ring mode with no preconfirmed nominal guides");
        model.ThreePointMode = true;
        model.SelectedRingIndex = 0;
        var firstEdge = target.MapPoint(new(target.VisibleRings[0].InnerRadiusMm, 0), actual);
        double radius = Math.Sqrt(firstEdge.X * firstEdge.X + firstEdge.Y * firstEdge.Y);
        var clicks = new[] { .1, .2, .3 }.Select(a => new CalibrationPoint(radius * Math.Cos(a), radius * Math.Sin(a))).ToArray();
        model.RingPointCommand.Execute(clicks[0]);
        model.RingPointCommand.Execute(clicks[1]);
        Invoke(model, "SaveSessionTo", path);
        Require(CalibrationSessionJson.Load(path).Photo is { ArcDraft.Points.Count: 2 }
            && !model.RingArcs[0].IsConfirmed, "two clicks are saved as a draft, not a measured edge");
        Await(model, "LoadSessionFromAsync", path);
        Require(model.ThreePointMode && model.RingPointMarkers.SequenceEqual(clicks[..2]),
            "reopening restores the selected ring and the unfinished arc");
        model.RingPointCommand.Execute(clicks[2]);
        Require(model.RingArcs[0].IsConfirmed && model.RingPointMarkers.SequenceEqual(clicks),
            "three points on the same side confirm one inner-edge radius");
        model.UndoRingPointCommand.Execute(null);
        Require(!model.RingArcs[0].IsConfirmed && model.RingPointMarkers.Count == 2, "undo reopens the completed measurement");
        model.RingPointCommand.Execute(clicks[2]);
        model.ThreePointMode = false;
        var photo = model.RectifiedPhoto;
        foreach (var ring in new[] { target.VisibleRings[target.VisibleRings.Count / 2], target.VisibleRings[^1] })
        {
            var p = target.MapPoint(new(ring.InnerRadiusMm, 0), actual);
            model.RingEditedCommand.Execute(new(ring.RingIndex, Math.Sqrt(p.X * p.X + p.Y * p.Y)));
        }
        Require(model.SolveRingsCommand.CanExecute(null) && !model.UseRingSeedCommand.CanExecute(null),
            "manually aligning three separated rings enables estimation");
        Invoke(model, "SaveSessionTo", path);
        var draft = CalibrationSessionJson.Load(path);
        Require(draft.Photo is { IsCoarseMode: true, RingObservations.Count: 3, UseRingSeed: false }
            && draft.Photo.RingObservations.Single(r => r.RingIndex == 0).Points!.SequenceEqual(clicks),
            "a coarse draft retains its three confirmed rings alongside the existing trace");
        model.SolveRingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.ApplyCoarseParametersCommand.CanExecute(null) && model.UseRingSeedCommand.CanExecute(null)
            && model.HasAvailableRingSeed && !model.HasAcceptedRingSeed && model.IsRingStep,
            "a ring estimate is available without selecting a fine search seed or changing stages");
        var sectors = state.CdSectors;
        model.ApplyCoarseParametersCommand.Execute(null);
        Require(Math.Abs(double.Parse(state.CdInnerRadius, CultureInfo.InvariantCulture) - actual.InnerRadiusMm) < 1e-6
            && Math.Abs(double.Parse(state.CdOuterRadius, CultureInfo.InvariantCulture) - actual.OuterRadiusMm) < 1e-6
            && state.CdSectors == sectors && model.Target!.Parameters == target.Parameters,
            "ring-only application updates the project's radii and preserves its sector count and the burn snapshot");
        model.SelectFineModeCommand.Execute(null);
        Require(model.IsFineStep && !model.HasAcceptedRingSeed && model.UseRingSeedCommand.CanExecute(null),
            "fine calibration can choose the record's initial values even after coarse fitting");
        var first = model.TracePoints[0];
        model.TraceEditedCommand.Execute(new(0, first.Index, first.XMm + .01, first.YMm));
        model.TraceEditedCommand.Execute(new(0, first.Index, first.XMm, first.YMm));
        Require(model.HasAvailableRingSeed && !model.HasAcceptedRingSeed,
            "editing the fine trace cannot invalidate a previously solved set of rings");
        model.UseRingSeedCommand.Execute(null);
        model.ClearRingSeedCommand.Execute(null);
        Require(!model.HasAcceptedRingSeed && model.HasAvailableRingSeed && model.IsFineStep,
            "returning to the record's seed in fine mode preserves the computed coarse estimate");
        model.SelectCoarseModeCommand.Execute(null);
        model.UseRingSeedCommand.Execute(null);
        Require(model.IsRingStep && model.HasAcceptedRingSeed, "adopting initial radii does not leave the coarse stage");
        model.SelectFineModeCommand.Execute(null);
        Require(model.IsFineStep && model.TracePoints.SequenceEqual(trace) && ReferenceEquals(model.RectifiedPhoto, photo)
            && model.Target!.Id == target.Id, "continuing preserves the rectified photo, existing trace and target geometry");
        Invoke(model, "SaveSessionTo", path);
        Require(CalibrationSessionJson.Load(path).Photo is { UseRingSeed: true, IsCoarseMode: false },
            "the session distinguishes accepted ring seeds from unconfirmed guides");
        var reopened = CreateModel(state);
        Await(reopened, "LoadSessionFromAsync", path);
        reopened.SolveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(reopened.SelectedCoarseCandidate is { } result
            && Math.Abs(result.Parameters.InnerRadiusMm - actual.InnerRadiusMm) < .0002
            && Math.Abs(result.Parameters.OuterRadiusMm - actual.OuterRadiusMm) < .0002,
            "the restored manual fine search box is centred on the accepted ring estimate");
        reopened.SelectCoarseModeCommand.Execute(null);
        reopened.RingEditedCommand.Execute(new(0, null));
        Invoke(reopened, "SaveSessionTo", path);
        Require(CalibrationSessionJson.Load(path).Photo is { UseRingSeed: false, RingObservations.Count: 2 }
            && !reopened.ApplyCoarseParametersCommand.CanExecute(null) && reopened.CoarseCandidates.Count == 0,
            "removing a measured radius invalidates the coarse estimate and the fine fit seeded by it");
        reopened.BackToPhotoCommand.Execute(null);
        var mark = reopened.PhotoBoundaryPoints[0];
        reopened.BoundaryEditedCommand.Execute(new(mark.Boundary, mark.Index, mark.X + .2, mark.Y));
        Require(reopened.RingArcs.All(r => !r.IsConfirmed) && reopened.TracePoints.Count == 0,
            "adjusting the physical edges invalidates both coordinate-dependent inputs");
        Invoke(model, "OnWindowClosed"); Invoke(reopened, "OnWindowClosed");
    }

    private static void TestObservedRadiusDomain(string directory)
    {
        // Regression coordinates from the complete ten-point photograph record. The first
        // real point lies inside the generation baseline's inner radius and must remain usable.
        CalibrationPoint[] measured =
        [
            new(5.351503459785284, -23.20680086238514), new(6.56368510376428, -26.5854106189566),
            new(7.454068070802904, -30.312922955106217), new(8.298604485217327, -34.38626843977292),
            new(8.948837997727669, -38.2033721484917), new(9.411218632270403, -42.07827674273897),
            new(10.18426580168618, -45.75087823763653), new(11.164423994350658, -49.59688238911955),
            new(12.659955783187392, -53.17557066987591), new(14.271766707805234, -56.010394939672025),
        ];
        var target = CalibrationTarget.Create(new(CalibrationDiscKind.Dvd, 23.9968875, 57.9779875, 2_297_888, 133.33));
        var baseline = CalibrationSessionJson.Load(Path.Combine(directory, "portable.calibration.json"));
        // The reusable photo fixture supplies valid physical-edge rectification. Curve points
        // are entered independently, exactly as they are after rectifying a user's photograph.
        var startingSession = baseline with
        {
            Target = target, FitOptions = new(target.Parameters, .005),
            Photo = baseline.Photo! with { Traces = [] },
            ConfirmedFit = null,
        };
        string path = Path.Combine(directory, "complete-observed-curve.calibration.json");
        CalibrationSessionJson.Save(path, startingSession);
        var model = CreateModel(new DiscParametersState());
        Await(model, "LoadSessionFromAsync", path);
        foreach (var point in measured) model.TraceEditedCommand.Execute(new(0, -1, point.X, point.Y));
        Require(model.TracePoints.Count == 10 && model.TracePoints.Select(p => new CalibrationPoint(p.XMm, p.YMm)).SequenceEqual(measured),
            "all ten measured points enter unchanged, including the point outside the generation annulus");
        Require(Math.Sqrt(Radius(model.TracePoints[0])) < target.Parameters.InnerRadiusMm && model.SolveCommand.CanExecute(null),
            "generation inner radius is a fitting baseline rather than a photo-point boundary");
        model.TraceEditedCommand.Execute(new(0, -1, 61, 0));
        model.TraceEditedCommand.Execute(new(0, -1, 0, 7));
        model.TraceEditedCommand.Execute(new(0, model.TracePoints[0].Index, 61, 0));
        Require(model.TracePoints.Count == 10 && model.TracePoints.Select(p => new CalibrationPoint(p.XMm, p.YMm)).SequenceEqual(measured),
            "physical outer edge and central hole reject invalid additions and moves without deleting any of the ten valid points");
        model.SolveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.CoarseCandidates.Count > 0 && model.CrossPredictionContours.Count == 1,
            "complete ten-point record produces a diagnostic continuous prediction: " + model.LastStatusMessage);
        Require(model.TracePoints.Count == 10 && model.TracePoints.Select(p => new CalibrationPoint(p.XMm, p.YMm)).SequenceEqual(measured),
            "solving never trims the original ten measured samples");
        AssertObservedEndpoints(model);
        Require(model.SelectedCoarseCandidate is not null && !model.ContourConfirmed
            && !model.ApplyParametersCommand.CanExecute(null)
            && !string.IsNullOrWhiteSpace(model.ApplyParametersHint)
            && model.FitAssessment.Contains(model.ApplyParametersHint, StringComparison.Ordinal),
            "a low-residual numerical result remains a candidate requiring photographic comparison");
        Invoke(model, "SaveSessionTo", path);
        var saved = CalibrationSessionJson.Load(path);
        Require(saved.Photo!.Traces.Single().Points.SequenceEqual(measured), "portable save retains every original measured point");
        var reopened = CreateModel(new DiscParametersState());
        Await(reopened, "LoadSessionFromAsync", path);
        Require(reopened.TracePoints.Select(p => new CalibrationPoint(p.XMm, p.YMm)).SequenceEqual(measured),
            "reopening does not discard the leading point outside the generation annulus");

        // Residual thresholds remain visible advice. The user's explicit photographic
        // confirmation authorizes a current candidate even when its residual is high.
        var candidate = model.SelectedCoarseCandidate ?? throw new InvalidOperationException("Missing observed-curve candidate.");
        var highResidual = candidate with { RmsErrorMm = 3.2 };
        Set(model, "_installing", true);
        model.CoarseCandidates = [highResidual];
        model.SelectedCoarseCandidate = highResidual;
        Set(model, "_installing", false);
        Await(model, "ShowCandidateAsync", highResidual);
        model.ContourConfirmed = true;
        Require(model.CrossPredictionContours.Count == 1 && model.ApplyParametersCommand.CanExecute(null)
            && model.FitAssessment.Contains(DiscImageStudio.Localization.L.T("CalibrationApply.ResidualWarning"), StringComparison.Ordinal)
            && model.TracePoints.Count == 10,
            "manual confirmation permits a high-residual candidate while keeping the automatic warning visible");
        model.ApplyParametersCommand.Execute(null);
        Require(double.Parse(model.Parameters.DvdInnerRadius, CultureInfo.InvariantCulture) == highResidual.Parameters.InnerRadiusMm
            && double.Parse(model.Parameters.DvdOuterRadius, CultureInfo.InvariantCulture) == highResidual.Parameters.OuterRadiusMm,
            "manual application writes the high-residual candidate's selected parameters");

        model.ClearTracesCommand.Execute(null);
        var mapping = CalibrationCrossPrediction.CreateMapping(target, target.Parameters, .41);
        double minimum = target.Parameters.InnerRadiusMm - .75, maximum = target.Parameters.OuterRadiusMm + .75;
        foreach (int i in Enumerable.Range(0, 10))
        {
            var point = mapping(minimum + (maximum - minimum) * i / 9);
            model.TraceEditedCommand.Execute(new(0, -1, point.X, point.Y));
        }
        model.SolveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.TracePoints.Count == 10 && model.CrossPredictionContours.Count == 1,
            "a continuous curve crossing both nominal band boundaries is retained and predicted");
        AssertObservedEndpoints(model);
        Require(Math.Sqrt(Radius(model.TracePoints[0])) < target.Parameters.InnerRadiusMm
            && Math.Sqrt(Radius(model.TracePoints[^1])) > target.Parameters.OuterRadiusMm,
            "both ends of the extended regression curve genuinely exceed the baseline annulus");

        // A disc whose geometry is 0.05 mm away from the generated baseline must not be
        // published as the clamped edge of the default +-0.02 mm box. Auto mode widens the
        // box until the optimum is interior and then recovers the real radii.
        model.ClearTracesCommand.Execute(null);
        model.FitDvdPitch = false;
        CalibrationParameters farActual = target.Parameters with
        {
            InnerRadiusMm = target.Parameters.InnerRadiusMm + .05,
            OuterRadiusMm = target.Parameters.OuterRadiusMm - .04,
        };
        double band = target.Parameters.OuterRadiusMm - target.Parameters.InnerRadiusMm;
        for (int i = 0; i < 70; i++)
        {
            double radius = target.Parameters.InnerRadiusMm + band * (.08 + .84 * i / 69.0);
            var point = target.MapPoint(new(0, -radius), farActual, .41);
            model.TraceEditedCommand.Execute(new(0, -1, point.X, point.Y));
        }
        Require(model.TracePoints.Count == 70 && model.SolveCommand.CanExecute(null),
            "a dense far-offset curve is ready for the auto-range fit");
        model.SolveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        var autoCandidate = model.SelectedCoarseCandidate;
        Require(autoCandidate is not null
            && Math.Abs(autoCandidate.Parameters.InnerRadiusMm - farActual.InnerRadiusMm) < .001
            && Math.Abs(autoCandidate.Parameters.OuterRadiusMm - farActual.OuterRadiusMm) < .001,
            "auto search widens past the default box and recovers the far-offset radii: " + model.LastStatusMessage);
        Require(model.FitAssessment.Contains("已自动扩大", StringComparison.Ordinal),
            "fit assessment names the widened search box");

        Invoke(model, "OnWindowClosed"); Invoke(reopened, "OnWindowClosed");
    }

    private static void AssertObservedEndpoints(CalibrationViewModel model)
    {
        var curve = model.CrossPredictionContours.Single();
        double firstRadius = Math.Sqrt(curve[0].X * curve[0].X + curve[0].Y * curve[0].Y);
        double lastRadius = Math.Sqrt(curve[^1].X * curve[^1].X + curve[^1].Y * curve[^1].Y);
        Require(curve.Length > 2 && Math.Abs(firstRadius - Math.Sqrt(Radius(model.TracePoints[0]))) < 1e-7
            && Math.Abs(lastRadius - Math.Sqrt(Radius(model.TracePoints[^1]))) < 1e-7,
            "predicted curve includes the exact first and last observed radii instead of clipping to the target spine");
    }

    private static void WritePhoto(string path, CalibrationTarget target, CalibrationParameters actual, CalibrationPhotoTransform camera)
    {
        const int width = 1000, height = 880;
        byte[] pixels = new byte[width * height];
        var inverse = target.CreateInverseMapping(actual, .61);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var point = camera.SourceToDisc(new(x, y));
            double radius = Math.Sqrt(point.X * point.X + point.Y * point.Y);
            byte value = radius > 60 || radius < 7.5 ? (byte)48 : (byte)216;
            if (Math.Abs(radius - 60) < .2 || Math.Abs(radius - 7.5) < .2) value = 26;
            if (inverse(point) is { } source && target.Sample(source.X, source.Y) == 0) value = 95;
            pixels[y * width + x] = value;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static CalibrationViewModel CreateModel(DiscParametersState state)
    {
        var shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        Set(shell, "_log", new StringBuilder()); Set(shell, "_dispatcher", Dispatcher.CurrentDispatcher);
        Set(shell, "<Toasts>k__BackingField", new ObservableCollection<ToastItem>());
        foreach (string name in new[]
        {
            "OpenOutputCommand", "BrowseCdTrackCommand", "BrowseDvdIsoCommand",
        })
            Set(shell, $"<{name}>k__BackingField", new RelayCommand(() => { }));
        return new(shell, state);
    }
    private static void Await(object instance, string name, params object?[] arguments) => ((Task)Invoke(instance, name, arguments)!).GetAwaiter().GetResult();
    private static object? Invoke(object instance, string name, params object?[] arguments)
        => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, arguments);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Photo VM: " + message); }
}
