using System.Globalization;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Controls;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class CalibrationConfirmedFitTests
{
    internal static void Run()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                DirectoryInfo directory = Directory.CreateTempSubdirectory("disc-confirmed-fit-");
                ShellViewModel shell = new(dispatcher);
                try
                {
                    foreach (var media in new[]
                    {
                        (CalibrationDiscKind.Cd, 359849L, 1200d),
                        (CalibrationDiscKind.Dvd, 2295104L, 133.3),
                        (CalibrationDiscKind.BluRay, 12219392L, 74.5),
                    })
                        await CheckRoundtrip(shell.Calibration, directory.FullName, media.Item1, media.Item2, media.Item3);
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    shell.Calibration.OnWindowClosed();
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
        Console.WriteLine("confirmed-calibration-fit: CD/DVD/BD confirmed results, frozen targets, invalidation and legacy records passed");
    }

    private static async Task CheckRoundtrip(CalibrationViewModel model, string directory,
        CalibrationDiscKind kind, long sectors, double density)
    {
        CalibrationParameters burned = new(kind, 24, 58, sectors, density,
            -2.1293990601237857E-06, -1.0728145734002103E-06, -5.796740088071808E-08);
        CalibrationParameters fitted = burned with
        {
            InnerRadiusMm = 23.991460702437305, OuterRadiusMm = 57.93579168028281,
            PitchLinear = -2.6724564452606465E-05, PitchQuadratic = 4.0503386747536064E-05,
            PitchCubic = -2.771840423519746E-05,
        };
        CalibrationTarget target = CalibrationTarget.Create(burned, artwork: CalibrationArtwork.ReferenceCross);
        CalibrationPhotoSession photo = Photo(target, fitted);
        CalibrationSession legacy = new(target, [], new(FitDvdPitch: true), Photo: photo);
        string path = Path.Combine(directory, kind + ".calibration.json");
        CalibrationSessionJson.Save(path, legacy);
        Require(!File.ReadAllText(path).Contains("confirmedFit", StringComparison.Ordinal), "legacy optional field omitted");
        await model.LoadSessionFromAsync(path);
        Require(!model.ContourConfirmed && model.CoarseCandidates.Count == 0, "legacy records remain unconfirmed");
        Require(Number(model.SetupPitchLinear) == burned.PitchLinear, "legacy records restore burned setup");

        CalibrationFitResult result = new(true, burned, .2, .002,
            [new(burned, .2, .002, MaxErrorMm: .004), new(fitted, .3, .001, MaxErrorMm: .003)], ["saved diagnostic"]);
        await InstallFit(model, result, selectedIndex: 1);
        model.SaveSessionTo(path);
        Require(CalibrationSessionJson.Load(path).ConfirmedFit is null, "unconfirmed fitted candidates are not saved");

        // Confirmation alone is sufficient; applying is a separate action and is not required.
        model.ContourConfirmed = true;
        Require(model.ApplyParametersCommand.CanExecute(null), "candidate is ready for confirmation persistence");
        model.SaveSessionTo(path);
        CalibrationSession saved = CalibrationSessionJson.Load(path);
        Require(saved.Target.Id == target.Id && saved.Target.Parameters == burned, "confirmed fit does not replace frozen geometry");
        Require(saved.ConfirmedFit is { SelectedCandidateIndex: 1 } confirmed
            && confirmed.TargetId == target.Id && confirmed.Result.Candidates[1].Parameters == fitted
            && confirmed.Result.Warnings.SequenceEqual(result.Warnings), "selected result and diagnostics are persisted");
        CheckValidation(saved);

        await model.LoadSessionFromAsync(path);
        Require(model.Target?.Id == target.Id && model.Target.Parameters == burned, "reopen preserves original photo target");
        Require(model.CoarseCandidates.Count == 2 && model.SelectedCoarseCandidate?.Parameters == fitted
            && model.ContourConfirmed && model.ApplyParametersCommand.CanExecute(null),
            "reopen restores selected candidate, prediction and manual confirmation");
        Require(Number(model.SetupPitchLinear) == fitted.PitchLinear
            && Number(model.SetupPitchQuadratic) == fitted.PitchQuadratic
            && Number(model.SetupPitchCubic) == fitted.PitchCubic,
            "reopen uses the confirmed geometry for the next target");
        Require(model.CrossPredictionContours.Count > 0, "restored fit redraws its prediction");

        model.ApplyParametersCommand.Execute(null);
        model.SaveSessionTo(path);
        Require(CalibrationSessionJson.Load(path).ConfirmedFit?.Result.Candidates[1].Parameters == fitted,
            "applying and resaving keeps the confirmed result");
        model.ContourConfirmed = false;
        model.SaveSessionTo(path);
        Require(CalibrationSessionJson.Load(path).ConfirmedFit is null, "revoked confirmation is not persisted");

        await InstallFit(model, result, selectedIndex: 1);
        model.ContourConfirmed = true;
        Set(model, "_installing", true);
        model.SelectedCoarseCandidate = model.CoarseCandidates[0];
        Set(model, "_installing", false);
        model.SaveSessionTo(path);
        Require(CalibrationSessionJson.Load(path).ConfirmedFit is null, "switching candidates clears the saved confirmation");

        await InstallFit(model, result, selectedIndex: 1);
        model.ContourConfirmed = true;
        CalibrationTracePoint point = model.TracePoints[0];
        model.TraceEditedCommand.Execute(new(0, point.Index, point.XMm + .01, point.YMm));
        model.SaveSessionTo(path);
        Require(CalibrationSessionJson.Load(path).ConfirmedFit is null, "editing measurements invalidates confirmed results");

        CalibrationSessionJson.Save(path, saved with { Photo = photo with { Traces = [] } });
        await model.LoadSessionFromAsync(path);
        Require(!model.ContourConfirmed && !model.ApplyParametersCommand.CanExecute(null)
            && Number(model.SetupPitchLinear) == burned.PitchLinear,
            "a result whose preview cannot be reconstructed does not restore approval or fitted setup");

        // A valid old record still loads after a confirmed result has existed in this view model.
        CalibrationSessionJson.Save(path, legacy);
        await model.LoadSessionFromAsync(path);
        Require(!model.ContourConfirmed && model.CoarseCandidates.Count == 0
            && Number(model.SetupPitchLinear) == burned.PitchLinear, "loading legacy clears previous confirmed state");
        await CheckRestoreRaces(model, saved.ConfirmedFit!);
    }

    private static async Task CheckRestoreRaces(CalibrationViewModel model, CalibrationConfirmedFit saved)
    {
        // Hold each real renderer's UI continuations separately so the newly selected
        // candidate finishes first, then let the cancelled saved-candidate task resume.
        // This reproduces the ordering without sleeps or a mock of the approval logic.
        model.SetupPitchLinear = "0.000123";
        QueuedContext restoring = new();
        Task restore = InContext(restoring, () => Restore(model, saved, CancellationToken.None));
        Require(!restore.IsCompleted, "saved preview is in flight before switching candidate");
        Set(model, "_installing", true);
        model.SelectedCoarseCandidate = model.CoarseCandidates[0];
        Set(model, "_installing", false);
        QueuedContext switched = new();
        Task preview = InContext(switched, () => ShowCandidate(model));
        await switched.CompleteAsync(preview);
        Require(model.CrossPredictionContours.Count > 0 && !model.ContourConfirmed,
            "the replacement candidate preview completed without manual approval");
        await restoring.CompleteAsync(restore);
        Require(!model.ContourConfirmed && model.SetupPitchLinear == "0.000123",
            "saved approval never transfers to a candidate selected during restoration");

        using CancellationTokenSource cancellation = new();
        QueuedContext cancelled = new();
        Task cancelledRestore = InContext(cancelled, () => Restore(model, saved, cancellation.Token));
        cancellation.Cancel();
        try
        {
            await cancelled.CompleteAsync(cancelledRestore);
            throw new InvalidOperationException("confirmed-calibration-fit: cancelled restoration completed");
        }
        catch (OperationCanceledException) { }
        Require(!model.ContourConfirmed && model.SetupPitchLinear == "0.000123",
            "cancelling record loading does not confirm or apply its result");
    }

    private static Task Restore(CalibrationViewModel model, CalibrationConfirmedFit saved, CancellationToken token) =>
        (Task)typeof(CalibrationViewModel).GetMethod("RestoreConfirmedFitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [saved, token])!;

    private static Task ShowCandidate(CalibrationViewModel model) =>
        (Task)typeof(CalibrationViewModel).GetMethod("ShowCandidateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [model.SelectedCoarseCandidate])!;

    private static Task InContext(SynchronizationContext context, Func<Task> start)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { return start(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public override SynchronizationContext CreateCopy() => this;

        internal async Task CompleteAsync(Task task)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            while (!task.IsCompleted)
            {
                if (_callbacks.TryDequeue(out var next))
                {
                    SynchronizationContext? previous = Current;
                    SetSynchronizationContext(this);
                    try { next.Callback(next.State); }
                    finally { SetSynchronizationContext(previous); }
                }
                else await Task.Delay(1, timeout.Token);
            }
            await task;
        }
    }

    private static void CheckValidation(CalibrationSession saved)
    {
        CalibrationConfirmedFit fit = saved.ConfirmedFit!;
        Reject(saved with { ConfirmedFit = fit with { TargetId = "unrelated-target" } });
        Reject(saved with { ConfirmedFit = fit with { SelectedCandidateIndex = 2 } });
        Reject(saved with { Photo = saved.Photo! with { RectificationConfirmed = false, Traces = [] } });
        CalibrationFitCandidate selected = fit.Result.Candidates[1];
        Reject(saved with { ConfirmedFit = fit with { Result = fit.Result with
        {
            Candidates = [selected with { Parameters = selected.Parameters with { Sectors = selected.Parameters.Sectors + 32 } }],
        }, SelectedCandidateIndex = 0 } });
        Reject(saved with { ConfirmedFit = fit with { Result = fit.Result with
        {
            Candidates = [selected with { RmsErrorMm = double.NaN }],
        }, SelectedCandidateIndex = 0 } });
    }

    private static async Task InstallFit(CalibrationViewModel model, CalibrationFitResult result, int selectedIndex)
    {
        Set(model, "_fit", result);
        Set(model, "_installing", true);
        model.CoarseCandidates = result.Candidates.Select((candidate, index) => new CalibrationViewModel.CoarseCandidate(
            "candidate " + index, candidate.Parameters, candidate.RotationRadians, candidate.RmsErrorMm,
            candidate.MaxErrorMm, candidate.OutlierCount)).ToArray();
        model.SelectedCoarseCandidate = model.CoarseCandidates[selectedIndex];
        Set(model, "_installing", false);
        await (Task)typeof(CalibrationViewModel).GetMethod("ShowCandidateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [model.SelectedCoarseCandidate])!;
    }

    private static CalibrationPhotoSession Photo(CalibrationTarget target, CalibrationParameters fitted)
    {
        const int size = 128;
        BitmapSource image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, new byte[size * size], size);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using MemoryStream stream = new();
        encoder.Save(stream);
        CalibrationPoint[] Boundary(double radius) => Enumerable.Range(0, 8).Select(index => new CalibrationPoint(
            63.5 + radius * Math.Cos(index * Math.PI / 4), 63.5 + radius * Math.Sin(index * Math.PI / 4))).ToArray();
        var mapping = CalibrationCrossPrediction.CreateMapping(target, fitted, .3);
        CalibrationPoint[] points = Enumerable.Range(0, 14).Select(index => mapping(24.3 + 33 * index / 13d)).ToArray();
        return new(Convert.ToBase64String(stream.ToArray()), Boundary(60), Boundary(7.5), 60, true,
            [new CalibrationCrossTrace(0, points)]);
    }

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    private static void Set(object instance, string name, object value) => instance.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void Reject(CalibrationSession session)
    {
        try { session.Validate(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("confirmed-calibration-fit: invalid result accepted");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("confirmed-calibration-fit: " + message);
    }
}
