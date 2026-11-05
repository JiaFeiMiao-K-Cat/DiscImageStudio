using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class CalibrationThreeStageTests
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
                var directory = Directory.CreateTempSubdirectory("disc-three-stages-");
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
        Console.WriteLine("three-stage calibration: stage selection, portable records, frozen output and one-line-to-cross handoff passed");
    }

    private static async Task CheckWorkflow(string directory, Dispatcher dispatcher)
    {
        var shell = new ShellViewModel(dispatcher);
        var model = shell.Calibration;
        try
        {
            model.SelectedDiscType = "DVD";
            model.Parameters.DvdTotalSectors = "2297888";
            model.Parameters.DvdInnerRadius = "24.1";
            model.Parameters.DvdOuterRadius = "58.1";
            model.Parameters.DvdPitchLinear = "-0.000039";
            model.Parameters.DvdPitchQuadratic = "0.000070";
            model.Parameters.DvdPitchCubic = "-0.000047";
            model.SelectCoarseModeCommand.Execute(null);
            model.CreateTargetCommand.Execute(null);
            var coarse = model.Target ?? throw new InvalidOperationException(model.LastStatusMessage);
            Require(coarse.HasAngleSweep && model.IsCoarseMode && model.GenerateImageCommand.CanExecute(null),
                "coarse stage creates the writable v13 target");
            string coarsePath = Path.Combine(directory, "bands.calibration.json");
            model.SaveSessionTo(coarsePath);

            model.SelectIntermediateModeCommand.Execute(null);
            Require(model.IsIntermediateMode && !model.GenerateImageCommand.CanExecute(null) && !model.StreamBurnCommand.CanExecute(null),
                "selecting the next stage cannot export or stream the previously created bands");
            model.CreateTargetCommand.Execute(null);
            var half = model.Target!;
            Require(half.HasWavyBoundary && half.Parameters == coarse.Parameters && model.GenerateImageCommand.CanExecute(null),
                "middle-stage creation uses broad wavy boundaries independently of any coarse photo");
            // Previously engraved v14 discs retain their continuous-centreline workflow.
            var ray = CalibrationTarget.Create(coarse.Parameters, artwork: CalibrationArtwork.SingleRadialLine);
            string rayPath = Path.Combine(directory, "ray.calibration.json");
            CalibrationSessionJson.Save(rayPath, new(ray, []));
            model.SelectFineModeCommand.Execute(null);
            await model.LoadSessionFromAsync(rayPath);
            Require(model.IsIntermediateMode && model.IsPhotoStep && model.Target?.Id == ray.Id,
                "a burned one-line record with no photo restores its stage");

            var actual = ray.Parameters with { InnerRadiusMm = ray.Parameters.InnerRadiusMm + .003,
                OuterRadiusMm = ray.Parameters.OuterRadiusMm - .007 };
            const double angle = .41;
            var photo = RenderRayPhoto(ray, actual, angle);
            CalibrationSessionJson.Save(rayPath, new(ray, [], Photo: photo));
            model.SelectCoarseModeCommand.Execute(null);
            await model.LoadSessionFromAsync(rayPath);
            Require(model.IsIntermediateMode && model.IsLineFitStep,
                "a rectified one-line photo opens directly for centreline tracing");
            var map = ray.CreateMapping(actual, angle);
            for (int i = 0; i < 65; i++)
            {
                double radius = ray.DrawingInnerRadiusMm + (.05 + .90 * i / 64) * (ray.DrawingOuterRadiusMm - ray.DrawingInnerRadiusMm);
                var point = map(new(0, -radius));
                model.TraceEditedCommand.Execute(new(0, -1, point.X, point.Y));
            }
            Require(model.TracePoints.Count == 65 && model.SolveCommand.CanExecute(null), "a traced middle-stage centreline enables fitting");
            await model.SolveCommand.ExecuteAsync(null);
            Require(model.CrossPredictionContours.Count == 1 && !model.ApplyParametersCommand.CanExecute(null),
                "one predicted curve awaits confirmation");
            model.ContourConfirmed = true;
            Require(model.ApplyParametersCommand.CanExecute(null), model.LastStatusMessage);
            model.ApplyParametersCommand.Execute(null);
            Require(model.Target!.Id == ray.Id && model.Target.Parameters == ray.Parameters, "applying the fit preserves the burned ray snapshot");
            model.SelectFineModeCommand.Execute(null);
            Require(!model.GenerateImageCommand.CanExecute(null) && !model.StreamBurnCommand.CanExecute(null),
                "the fine stage cannot accidentally burn the previous ray");
            model.CreateTargetCommand.Execute(null);
            var fine = model.Target!;
            Require(fine.UsesReferencePattern && fine.TraceArmCount == 4 && fine.Id != ray.Id
                && Math.Abs(fine.Parameters.InnerRadiusMm - actual.InnerRadiusMm) < .00001
                && Math.Abs(fine.Parameters.OuterRadiusMm - actual.OuterRadiusMm) < .00001
                && fine.Parameters.PitchCubic == actual.PitchCubic && fine.Parameters.Sectors == actual.Sectors,
                "the original reference cross is generated with both refined radii and the retained DVD pitch");
            string finePath = Path.Combine(directory, "cross.calibration.json");
            model.SaveSessionTo(finePath);
            await model.LoadSessionFromAsync(coarsePath);
            Require(model.IsCoarseMode && model.Target?.Id == coarse.Id, "a no-photo band record restores coarse mode");
            await model.LoadSessionFromAsync(finePath);
            Require(model.IsFineMode && model.Target?.Id == fine.Id && model.GenerateImageCommand.CanExecute(null),
                "a no-photo reference record restores fine mode");
        }
        finally { model.OnWindowClosed(); }
    }

    private static CalibrationPhotoSession RenderRayPhoto(CalibrationTarget ray, CalibrationParameters actual, double rotation)
    {
        const double canvas = 63;
        const int size = 320;
        var image = CalibrationPatternRenderer.Render(ray, canvas, size, actual, rotation);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        CalibrationPoint[] Boundary(double radius) => Enumerable.Range(0, 8).Select(i => new CalibrationPoint(
            (size - 1) / 2.0 + radius * size / (2 * canvas) * Math.Cos(i * Math.PI / 4),
            (size - 1) / 2.0 + radius * size / (2 * canvas) * Math.Sin(i * Math.PI / 4))).ToArray();
        return new(Convert.ToBase64String(stream.ToArray()), Boundary(60), Boundary(7.5), 60, true, []);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Three-stage calibration: " + message);
    }
}
