using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Controls;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class CalibrationBoundaryWorkflowTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var directory = Directory.CreateTempSubdirectory("disc-boundary-workflow-");
                try { await CheckWorkflow(directory.FullName, dispatcher); }
                catch (Exception ex) { failure = ex; }
                finally { directory.Delete(recursive: true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("half-disc workflow: independent segments, stable edits, portable drafts, bounded fit and two-boundary preview passed");
    }

    private static async Task CheckWorkflow(string directory, Dispatcher dispatcher)
    {
        var shell = new ShellViewModel(dispatcher);
        var model = shell.Calibration;
        try
        {
            model.SelectedDiscType = "DVD";
            model.Parameters.DvdTotalSectors = "2297888";
            model.Parameters.DvdInnerRadius = "24";
            model.Parameters.DvdOuterRadius = "58";
            model.Parameters.DvdPitchLinear = "0";
            model.Parameters.DvdPitchQuadratic = "0";
            model.Parameters.DvdPitchCubic = "0";
            model.SelectIntermediateModeCommand.Execute(null);
            model.CreateTargetCommand.Execute(null);
            var wavyTarget = model.Target!;
            Require(wavyTarget.HasWavyBoundary && model.HasBoundarySegments && model.GenerateImageCommand.CanExecute(null)
                && model.SelectedCoarseSearchRange.RadiusRangeMm == .01,
                "new middle targets are writable slowly swaying boundaries");
            Require(!model.SolveCommand.CanExecute(null) && !model.AddBoundarySegmentCommand.CanExecute(null),
                "no boundary marking is possible before rectification");
            string path = Path.Combine(directory, "half.calibration.json");
            var target = CalibrationTarget.Create(wavyTarget.Parameters, artwork: CalibrationArtwork.HalfDisc);
            var actual = target.Parameters with { InnerRadiusMm = 24.005, OuterRadiusMm = 58.005 };
            var photo = MakePhoto(target, actual);
            CalibrationSessionJson.Save(path, new(target, [], new(RadiusRangeMm: .02, AutoRange: false), Photo: photo));
            model.SelectFineModeCommand.Execute(null);
            await model.LoadSessionFromAsync(path);
            Require(model.IsBoundarySegmentStep && !model.CanFitDvdPitch && !model.SelectedCoarseSearchRange.Auto
                && model.SelectedCoarseSearchRange.RadiusRangeMm == .02,
                "portable half-disc restores its middle stage with bounded radius-only fit");
            model.AddBoundarySegmentCommand.Execute(null);
            model.AddBoundarySegmentCommand.Execute(null);
            Require(model.BoundarySegmentChoices.Count == 1, "repeated add reuses an empty draft");
            await CheckOrderedEditing(model, path);

            var segments = VisibleSegments(target, actual);
            Require(segments.Count >= 4, "synthetic one-side photograph exposes enough disconnected boundaries");
            foreach (var segment in segments)
            {
                model.AddBoundarySegmentCommand.Execute(null);
                int id = model.SelectedTraceArm;
                // The user may extend both ends; numerical ordering must not rewrite
                // the editing path even when the same points arrive out of radial order.
                foreach (var point in new[] { segment[0], segment[^1], segment[1], segment[3], segment[2] })
                    model.TraceEditedCommand.Execute(new(id, -1, point.X, point.Y));
            }
            Require(model.TracePoints.Select(p => p.Arm).Distinct().Count() == segments.Count
                && model.TracePoints.GroupBy(p => p.Arm).All(g => g.Count() == 5
                    && g.Zip(g.Skip(1)).Any(pair => Radius(pair.First) > Radius(pair.Second)))
                && model.SolveCommand.CanExecute(null), "disconnected sections retain their editing order instead of radial sorting");
            var pointToMove = model.TracePoints.First();
            model.TraceEditedCommand.Execute(new(pointToMove.Arm, pointToMove.Index, pointToMove.XMm + .001, pointToMove.YMm));
            Require(model.TracePoints.Single(p => p.Index == pointToMove.Index).Arm == pointToMove.Arm,
                "dragging preserves its segment and stable point identity");
            model.TraceEditedCommand.Execute(new(pointToMove.Arm, pointToMove.Index, pointToMove.XMm, pointToMove.YMm));
            model.SaveSessionTo(path);
            var saved = CalibrationSessionJson.Load(path);
            Require(saved.Photo!.Traces.Count == 0 && saved.Photo.BoundarySegments!.Count == segments.Count,
                "half-disc points use their own persistent segments, never a fabricated continuous trace");
            var before = model.TracePoints.Select(p => (p.Arm, p.XMm, p.YMm)).ToArray();
            await model.LoadSessionFromAsync(path);
            Require(model.TracePoints.Select(p => (p.Arm, p.XMm, p.YMm)).SequenceEqual(before),
                "reopening preserves disconnected identities and complete coordinates");
            Require(!model.ApplyParametersCommand.CanExecute(null)
                && model.ApplyParametersHint == L.T("CalibrationApply.Solve")
                && model.FitAssessment == model.ApplyParametersHint,
                "reopened points explain that a fresh calculation is required before applying");

            await model.SolveCommand.ExecuteAsync(null);
            Require(model.CoarseCandidates.Count > 0 && model.CrossPredictionContours.Count == 2,
                "a candidate overlays both opposite half-disc boundaries: " + model.LastStatusMessage);
            Require(!model.ApplyParametersCommand.CanExecute(null)
                && model.ApplyParametersHint == L.Format("CalibrationApply.Confirm", model.TraceConfirmationLabel)
                && model.FitAssessment.Contains(model.ApplyParametersHint, StringComparison.Ordinal),
                "a finished valid preview asks for contour confirmation instead of silently disabling apply");
            var changed = new HashSet<string>();
            model.PropertyChanged += (_, change) => changed.Add(change.PropertyName ?? string.Empty);
            model.ContourConfirmed = true;
            Require(model.ApplyParametersCommand.CanExecute(null)
                && model.ApplyParametersHint == L.T("CalibrationApply.Ready")
                && model.FitAssessment.Contains(model.ApplyParametersHint, StringComparison.Ordinal)
                && changed.Contains(nameof(model.ApplyParametersHint)) && changed.Contains(nameof(model.FitAssessment)),
                "confirmation refreshes the visible verdict, tooltip and command together: " + model.LastStatusMessage);
            model.ContourConfirmed = false;
            Require(!model.ApplyParametersCommand.CanExecute(null)
                && model.ApplyParametersHint == L.Format("CalibrationApply.Confirm", model.TraceConfirmationLabel),
                "removing contour confirmation restores the actionable disabled reason");
            model.ContourConfirmed = true;
            var verified = model.SelectedCoarseCandidate!;
            var comparison = model.CoarseCandidates.Skip(1).FirstOrDefault(candidate => candidate.RmsErrorMm <= .35 && candidate.MaxErrorMm <= .8);
            Require(comparison is not null, "fixture includes a low-residual alternative candidate for manual selection");
            model.SelectedCoarseCandidate = comparison;
            Require(!model.ContourConfirmed && !model.ApplyParametersCommand.CanExecute(null),
                "switching candidates clears photographic confirmation");
            for (int wait = 0; wait < 200 && model.CrossPredictionContours.Count != 2; wait++) await Task.Delay(5);
            model.ContourConfirmed = true;
            Require(model.ApplyParametersCommand.CanExecute(null)
                && model.FitAssessment.Contains(L.T("HalfDisc.ComparisonOnly"), StringComparison.Ordinal),
                "manual confirmation permits the selected alternative while retaining automatic ranking advice");
            model.ApplyParametersCommand.Execute(null);
            AssertAppliedCandidate(model, comparison!.Parameters);
            model.SelectedCoarseCandidate = verified;
            Require(!model.ContourConfirmed && !model.ApplyParametersCommand.CanExecute(null),
                "returning to the best candidate also requires fresh confirmation");
            // Candidate preview runs off-thread and must finish before confirmation can apply.
            for (int wait = 0; wait < 200 && model.CrossPredictionContours.Count != 2; wait++) await Task.Delay(5);
            model.ContourConfirmed = true;
            Require(model.ApplyParametersCommand.CanExecute(null), "the verified candidate remains usable after comparison");
            model.ApplyParametersCommand.Execute(null);
            Require(model.Target!.Id == target.Id && model.Target.Parameters == target.Parameters,
                "applying a middle fit never changes the written target snapshot");
            await CheckFailedFitFeedback(model);
            int previousCount = model.TracePoints.Count;
            int previousSegment = model.SelectedTraceArm;
            model.DeleteBoundarySegmentCommand.Execute(null);
            Require(model.TracePoints.Count == previousCount - 5 && !model.TracePoints.Any(p => p.Arm == previousSegment)
                && model.CoarseCandidates.Count == 0 && !model.ApplyParametersCommand.CanExecute(null),
                "deleting one section preserves others and invalidates every fit and confirmation");
            for (int iteration = 0; iteration < 70; iteration++)
            {
                model.AddBoundarySegmentCommand.Execute(null);
                int id = model.SelectedTraceArm;
                Require(id is >= 0 and < 64, "segment ID reuse stays within persisted bounds");
                model.TraceEditedCommand.Execute(new(id, -1, 30, 0));
                Require(!model.SolveCommand.CanExecute(null), "an unfinished nonempty segment is not silently ignored");
                model.DeleteBoundarySegmentCommand.Execute(null);
            }
            CheckCanvasSegments(model.RectifiedPhoto!);
            await CheckContinuousBoundary(model, path, target, target.Parameters, "a straight boundary");
            await CheckContinuousBoundary(model, path, target,
                target.Parameters with { InnerRadiusMm = 24.0003, OuterRadiusMm = 57.9997 }, "a gently bending boundary");
            await CheckContinuousBoundary(model, path, wavyTarget, wavyTarget.Parameters,
                "the intentional shape of a new swaying boundary");
            await CheckContinuousBoundary(model, path, wavyTarget,
                wavyTarget.Parameters with { InnerRadiusMm = 24.0003, OuterRadiusMm = 57.9997 },
                "a new swaying boundary with additional gentle distortion");
            await CheckTwoVisibleSections(model, path, wavyTarget);
            model.BackToPhotoCommand.Execute(null);
            Require(!model.TraceEditedCommand.CanExecute(new(0, -1, 30, 0)) && !model.AddBoundarySegmentCommand.CanExecute(null),
                "returning to photo correction disables segment editing");
        }
        finally { model.OnWindowClosed(); }
    }

    private static async Task CheckOrderedEditing(CalibrationViewModel model, string path)
    {
        var selector = new ComboBox { DataContext = model, DisplayMemberPath = "Label", SelectedValuePath = "Id" };
        selector.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.BoundarySegmentChoices)));
        selector.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(model.SelectedTraceArm)) { Mode = BindingMode.TwoWay });
        var selection = selector.SelectedItem;
        Require(selection is not null && Equals(selector.SelectedValue, 0), "bound segment selector starts on the current segment");
        Point[] curve = [new(35, 0), new(34, 2), new(33, 0), new(34, -2), new(35, -3)];
        foreach (Point p in curve) model.TraceEditedCommand.Execute(new(0, -1, p.X, p.Y));
        Require(ReferenceEquals(selector.SelectedItem, selection) && Equals(selector.SelectedValue, 0)
            && model.SelectedTraceArm == 0 && !Validation.GetHasError(selector),
            "point-count updates preserve the bound segment selection without validation errors");
        Require(model.TracePoints.Select(p => new Point(p.XMm, p.YMm)).SequenceEqual(curve),
            "ordinary clicks preserve a curved path even when its radius reverses");
        var original = model.TracePoints.ToArray();
        int[] identities = original.Select(p => p.Index).ToArray();
        model.TraceEditedCommand.Execute(new(0, original[1].Index, 38, 2));
        Require(model.TracePoints.Select(p => p.Index).SequenceEqual(identities),
            "moving a point across its neighbours' radii never swaps its neighbours");
        model.TraceEditedCommand.Execute(new(0, -1, 35, 1, InsertBeforeIndex: original[1].Index));
        var inserted = model.TracePoints[1];
        Require(model.TracePoints[0].Index == original[0].Index && model.TracePoints[2].Index == original[1].Index
            && inserted.Index != original[1].Index && inserted.XMm == 35 && inserted.YMm == 1,
            "inserting a point splits only the requested edge");
        model.TraceEditedCommand.Execute(new(0, -1, 36, 0, InsertBeforeIndex: original[0].Index));
        Require(model.TracePoints[0].XMm == 36 && model.TracePoints[1].Index == original[0].Index,
            "the first endpoint can extend without reversing the existing path");
        int count = model.TracePoints.Count;
        model.TraceEditedCommand.Execute(new(0, -1, 40, 1, InsertBeforeIndex: 999));
        Require(model.TracePoints.Count == count, "a stale insertion hint never appends a stray connector");
        model.TraceEditedCommand.Execute(new(0, inserted.Index, inserted.XMm, inserted.YMm, Remove: true));
        Require(model.TracePoints[1].Index == original[0].Index && model.TracePoints[2].Index == original[1].Index,
            "deleting an inserted point rejoins its original neighbours");
        var before = model.TracePoints.Select(p => (p.Arm, p.XMm, p.YMm)).ToArray();
        model.SaveSessionTo(path);
        await model.LoadSessionFromAsync(path);
        Require(model.TracePoints.Select(p => (p.Arm, p.XMm, p.YMm)).SequenceEqual(before),
            "saving and reopening preserves the drawn path, including radial reversals");
        model.ClearTracesCommand.Execute(null);
        Require(model.TracePoints.Count == 0, "ordered editing fixture is cleared before numerical workflow checks");
        BindingOperations.ClearAllBindings(selector);
    }

    private static async Task CheckFailedFitFeedback(CalibrationViewModel model)
    {
        // Reproduce an automatic residual failure with a usable candidate. Manual
        // confirmation authorizes that exact candidate, without hiding solver warnings.
        var field = typeof(CalibrationViewModel).GetField("_fit",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var original = (CalibrationFitResult)field.GetValue(model)!;
        var originalCandidates = model.CoarseCandidates;
        var originalSelected = model.SelectedCoarseCandidate!;
        var parameters = originalSelected.Parameters with
        {
            InnerRadiusMm = originalSelected.Parameters.InnerRadiusMm + .0004,
            OuterRadiusMm = originalSelected.Parameters.OuterRadiusMm - .0003,
        };
        var selected = originalSelected with { Parameters = parameters, RmsErrorMm = .222243, MaxErrorMm = .786889 };
        string[] warnings = ["边界片段与同一个盘面模型不一致，请检查误点、照片矫正和片段是否跨过了另一条边界。", "仍有其他候选，请补充不同半径的边界。"];
        try
        {
            var failed = original with
            {
                Succeeded = false, BestParameters = null, RmsErrorMm = selected.RmsErrorMm, Warnings = warnings,
                Candidates = [new(parameters, selected.RotationRadians, selected.RmsErrorMm, MaxErrorMm: selected.MaxErrorMm)],
            };
            field.SetValue(model, failed);
            model.CoarseCandidates = [selected];
            model.SelectedCoarseCandidate = selected;
            for (int wait = 0; wait < 200 && model.CrossPredictionContours.Count != 2; wait++) await Task.Delay(5);
            Require(model.CrossPredictionContours.Count == 2 && !model.ContourConfirmed
                && !model.ApplyParametersCommand.CanExecute(null),
                "a failed automatic result still needs a completed preview and manual confirmation");
            model.ContourConfirmed = true;
            Require(model.ApplyParametersCommand.CanExecute(null)
                && model.FitAssessment.Contains(L.T("CalibrationApply.AutomaticNotPassed"), StringComparison.Ordinal)
                && warnings.All(warning => model.FitAssessment.Contains(warning, StringComparison.Ordinal)),
                "manual confirmation permits a failed numerical candidate and preserves every automatic warning");
            model.ApplyParametersCommand.Execute(null);
            AssertAppliedCandidate(model, parameters);

            field.SetValue(model, failed with { Candidates = [] });
            model.ContourConfirmed = false;
            model.ContourConfirmed = true;
            Require(!model.ApplyParametersCommand.CanExecute(null)
                && model.ApplyParametersHint == L.T("CalibrationApply.NoCandidate"),
                "manual confirmation cannot apply a result with no current candidate");
        }
        finally
        {
            field.SetValue(model, original);
            model.CoarseCandidates = originalCandidates;
            model.SelectedCoarseCandidate = originalSelected;
            for (int wait = 0; wait < 200 && model.CrossPredictionContours.Count != 2; wait++) await Task.Delay(5);
            model.ContourConfirmed = true;
        }
    }

    private static void AssertAppliedCandidate(CalibrationViewModel model, CalibrationParameters parameters)
    {
        Require(double.Parse(model.Parameters.DvdInnerRadius, System.Globalization.CultureInfo.InvariantCulture) == parameters.InnerRadiusMm
            && double.Parse(model.Parameters.DvdOuterRadius, System.Globalization.CultureInfo.InvariantCulture) == parameters.OuterRadiusMm,
            "applying writes the manually selected candidate's exact radii");
    }

    private static async Task CheckTwoVisibleSections(CalibrationViewModel model, string path, CalibrationTarget target)
    {
        CalibrationSessionJson.Save(path, new(target, [], new(RadiusRangeMm: .01, AutoRange: false), Photo: MakePhoto(target, target.Parameters)));
        await model.LoadSessionFromAsync(path);
        var mapping = CalibrationCrossPrediction.CreateMapping(target, target.Parameters, .37);
        double width = target.DrawingOuterRadiusMm - target.DrawingInnerRadiusMm;
        foreach (double start in new[] { 1d / 12 - .055, .75 - .055 })
        {
            model.AddBoundarySegmentCommand.Execute(null);
            for (int i = 0; i < 6; i++)
            {
                var p = mapping(target.DrawingInnerRadiusMm + (start + .11 * i / 5) * width);
                model.TraceEditedCommand.Execute(new(model.SelectedTraceArm, -1, p.X, p.Y));
            }
        }
        var angles = model.TracePoints.Select(p => Math.Atan2(p.YMm, p.XMm)).ToArray();
        Require(model.BoundarySegmentChoices.Count == 2 && model.SolveCommand.CanExecute(null)
            && angles.Max() - angles.Min() < Math.PI / 4,
            "two separated long sections in one 45-degree area enable boundary refinement");
        await model.SolveCommand.ExecuteAsync(null);
        model.ContourConfirmed = true;
        Require(model.ApplyParametersCommand.CanExecute(null)
            && Math.Abs(model.SelectedCoarseCandidate!.Parameters.InnerRadiusMm - target.Parameters.InnerRadiusMm) < .00001
            && Math.Abs(model.SelectedCoarseCandidate.Parameters.OuterRadiusMm - target.Parameters.OuterRadiusMm) < .00001,
            "two visible sections retain the actual straightened geometry after intended sway is removed: " + model.LastStatusMessage);
    }

    private static async Task CheckContinuousBoundary(CalibrationViewModel model, string path, CalibrationTarget target,
        CalibrationParameters actual, string description)
    {
        CalibrationSessionJson.Save(path, new(target, [], new(RadiusRangeMm: .01, AutoRange: false), Photo: MakePhoto(target, actual)));
        await model.LoadSessionFromAsync(path);
        var mapping = CalibrationCrossPrediction.CreateMapping(target, actual, .37);
        int count = target.HasWavyBoundary ? 25 : 6;
        for (int i = 0; i < count; i++)
        {
            var p = mapping(24.7 + i * (57.3 - 24.7) / (count - 1));
            model.TraceEditedCommand.Execute(new(0, -1, p.X, p.Y));
        }
        Require(model.BoundarySegmentChoices.Count == 1 && model.SolveCommand.CanExecute(null),
            description + " enables fitting with one complete section");
        await model.SolveCommand.ExecuteAsync(null);
        model.ContourConfirmed = true;
        Require(model.ApplyParametersCommand.CanExecute(null) && model.CrossPredictionContours.Count == 2
            && Math.Abs(model.SelectedCoarseCandidate!.Parameters.InnerRadiusMm - actual.InnerRadiusMm) < .00001
            && Math.Abs(model.SelectedCoarseCandidate.Parameters.OuterRadiusMm - actual.OuterRadiusMm) < .00001,
            description + " fits without adding artificial disconnected sections: " + model.LastStatusMessage);
    }

    private static List<CalibrationPoint[]> VisibleSegments(CalibrationTarget target, CalibrationParameters actual)
    {
        var all = new List<CalibrationPoint[]>();
        const double rotation = .37;
        for (int arm = 0; arm < 2; arm++)
        {
            var mapping = CalibrationCrossPrediction.CreateMapping(target, actual, rotation, arm);
            var current = new List<CalibrationPoint>();
            for (int i = 0; i <= 50000; i++)
            {
                double radius = 24.4 + i / 50000.0 * 33.2;
                var p = mapping(radius);
                double angle = Math.Atan2(p.Y, p.X);
                bool visible = Math.Abs(angle) <= 20 * Math.PI / 180;
                if (visible) current.Add(p);
                if ((!visible || i == 50000) && current.Count > 0)
                {
                    if (current.Count >= 20)
                        all.Add(Enumerable.Range(0, 5).Select(slot => current[(int)Math.Round(slot * (current.Count - 1) / 4.0)]).ToArray());
                    current.Clear();
                }
            }
        }
        var ordered = all.OrderBy(points => Math.Sqrt(points[0].X * points[0].X + points[0].Y * points[0].Y)).ToArray();
        int count = Math.Min(8, ordered.Length);
        return Enumerable.Range(0, count).Select(i => ordered[(int)Math.Round(i * (ordered.Length - 1.0) / (count - 1))]).ToList();
    }

    private static void CheckCanvasSegments(BitmapSource image)
    {
        var canvas = new CalibrationPhotoCanvas { Image = image, IsPhotoStep = false, IsBoundarySegmentMode = true,
            SelectedTraceArm = 12, CanvasRadiusMm = 63, Width = 630, Height = 630 };
        canvas.Measure(new Size(630, 630)); canvas.Arrange(new Rect(0, 0, 630, 630));
        CalibrationTraceEdit? edit = null;
        canvas.TraceEditedCommand = new RelayCommand<CalibrationTraceEdit>(value =>
        {
            edit = value;
            if (value is not null) canvas.TracePoints = [new(value.Arm, 0, value.XMm, value.YMm)];
        });
        Require(canvas.AddTraceAt(canvas.WorldToView(new Point(30, 0))) && edit?.Arm == 12,
            "canvas accepts independent segment numbers beyond four cross arms");
        canvas.TracePoints = [new(0, 0, -35, -20), new(0, 1, -25, -20), new(12, 2, 25, 20), new(12, 3, 35, 20)];
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(630, 630, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        bool CyanNear(Point point)
        {
            for (int y = (int)point.Y - 3; y <= (int)point.Y + 3; y++)
            for (int x = (int)point.X - 3; x <= (int)point.X + 3; x++)
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                if (pixel[0] > 180 && pixel[1] > 180 && pixel[2] < 100) return true;
            }
            return false;
        }
        Require(CyanNear(canvas.WorldToView(new(-35, -20))) && CyanNear(canvas.WorldToView(new(35, 20)))
            && !CyanNear(canvas.WorldToView(new(0, 0))), "canvas renders both high-ID segments without a connecting line across the unseen gap");
        canvas.IsBoundarySegmentMode = false;
        Require(!canvas.AddTraceAt(canvas.WorldToView(new Point(30, 0))), "legacy canvas keeps its arm validation");
    }

    private static CalibrationPhotoSession MakePhoto(CalibrationTarget target, CalibrationParameters actual)
    {
        const int size = 320;
        const double canvas = 63;
        var image = CalibrationPatternRenderer.Render(target, canvas, size, actual, .37);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream);
        CalibrationPoint[] Boundary(double radius) => Enumerable.Range(0, 8).Select(i => new CalibrationPoint(
            (size - 1) / 2.0 + radius * size / (2 * canvas) * Math.Cos(i * Math.PI / 4),
            (size - 1) / 2.0 + radius * size / (2 * canvas) * Math.Sin(i * Math.PI / 4))).ToArray();
        return new(Convert.ToBase64String(stream.ToArray()), Boundary(60), Boundary(7.5), 60, true, []);
    }

    private static double Radius(CalibrationTracePoint p) => p.XMm * p.XMm + p.YMm * p.YMm;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Boundary workflow: " + message);
    }
}
