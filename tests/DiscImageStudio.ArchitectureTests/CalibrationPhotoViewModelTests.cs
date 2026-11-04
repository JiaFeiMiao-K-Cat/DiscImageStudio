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
        try { TestWorkflow(directory); TestObservedRadiusDomain(directory); }
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
        model.CreateTargetCommand.Execute(null);
        var target = model.Target ?? throw new InvalidOperationException(model.LastStatusMessage);
        Require(target.SchemaVersion == CalibrationTarget.CurrentSchemaVersion && model.IsPhotoStep && !model.SolveCommand.CanExecute(null), "new target starts in photo step");
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
        Require(model.IsTraceStep && !model.PreviewRectification && !model.SolveCommand.CanExecute(null),
            "confirmation opens an empty editable single-curve step");
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
        model.ApplyParametersCommand.Execute(null);
        Require(Math.Abs(double.Parse(state.CdInnerRadius, CultureInfo.InvariantCulture) - actual.InnerRadiusMm) < .0002,
            "chosen candidate is applied to disc parameters");
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
                session with { Photo = session.Photo! with { RectificationConfirmed = false, Traces = [] } });
        }
        File.Delete(photoPath);
        var reopened = CreateModel(new DiscParametersState());
        Await(reopened, "LoadSessionFromAsync", sessionPath);
        Require(reopened.IsTraceStep && reopened.PhotoImage is not null && reopened.RectifiedPhoto is not null
            && reopened.TracePoints.Count == 70 && reopened.Target!.Id == target.Id, "session reopens without the original photo file");
        Require(!reopened.ApplyParametersCommand.CanExecute(null), "reopened points require a fresh fit");

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
        Require(model.FitAssessment.Contains("候选", StringComparison.Ordinal) && model.FitAssessment.Contains("待核对", StringComparison.Ordinal),
            "a low-residual numerical result remains a candidate requiring photographic comparison");
        Invoke(model, "SaveSessionTo", path);
        var saved = CalibrationSessionJson.Load(path);
        Require(saved.Photo!.Traces.Single().Points.SequenceEqual(measured), "portable save retains every original measured point");
        var reopened = CreateModel(new DiscParametersState());
        Await(reopened, "LoadSessionFromAsync", path);
        Require(reopened.TracePoints.Select(p => new CalibrationPoint(p.XMm, p.YMm)).SequenceEqual(measured),
            "reopening does not discard the leading point outside the generation annulus");

        // Candidate selection must not enable applying an obviously high-residual numerical
        // result merely because preview generation and manual contour confirmation succeeded.
        var candidate = model.SelectedCoarseCandidate ?? throw new InvalidOperationException("Missing observed-curve candidate.");
        var highResidual = candidate with { RmsErrorMm = 3.2 };
        Set(model, "_installing", true);
        model.CoarseCandidates = [highResidual];
        model.SelectedCoarseCandidate = highResidual;
        Set(model, "_installing", false);
        Await(model, "ShowCandidateAsync", highResidual);
        model.ContourConfirmed = true;
        Require(model.CrossPredictionContours.Count == 1 && !model.ApplyParametersCommand.CanExecute(null)
            && model.FitAssessment.Contains("不能应用", StringComparison.Ordinal) && model.TracePoints.Count == 10,
            "high-residual candidates remain visible for diagnosis but cannot be presented as an applicable calibration");

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
