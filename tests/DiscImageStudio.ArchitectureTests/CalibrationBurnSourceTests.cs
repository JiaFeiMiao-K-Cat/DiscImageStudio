using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class CalibrationBurnSourceTests
{
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
        Console.WriteLine("calibration-burn-source: passed (CD/DVD/BD labels, saved BD parameters, speed routing, confirmation and memory-only burn request)");
    }

    private static void RunOnSta()
    {
        string directory = Path.Combine(Path.GetTempPath(), "calibration-burn-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        ShellViewModel? shell = null;
        try
        {
            RecordingBurner burner = new();
            shell = new ShellViewModel(Dispatcher.CurrentDispatcher, burner);
            shell.NavigateTo(Workspace.Calibration);
            Require(!shell.IsProjectOpen && !shell.IsProjectMode && shell.IsJobDockVisible && shell.Calibration.Target is null,
                "calibration opens directly from Home with an empty setup and the shared job dock");
            shell.ReturnFromCalibrationCommand.Execute(null);
            Require(shell.CurrentWorkspace == Workspace.Home && !shell.IsJobDockVisible,
                "home entry returns to home without an idle job dock");
            DiscParametersState state = shell.Calibration.Parameters;
            Require(!ReferenceEquals(state, shell.State) && !shell.Burn.UseCalibrationPattern
                && shell.CalibrationBurn.UseCalibrationPattern, "project and calibration have independent geometry and burn sources");
            state.CdInnerRadius = state.DvdInnerRadius = state.BdInnerRadius = "24";
            state.CdOuterRadius = state.DvdOuterRadius = state.BdOuterRadius = "58";
            state.CdSectors = "8";
            state.CdUsableSectors = "8";
            state.DvdTotalSectors = "16";
            state.DvdUsableSectors = "16";
            state.DvdPitchLinear = state.DvdPitchQuadratic = state.DvdPitchCubic = "0";
            state.BdTotalSectors = "64";
            state.BdUsableSectors = "64";
            state.BdInvertMapping = true;
            state.CdInterleave = true;
            // A data directory must not change a calibration target into a CD data burn.
            state.CdDataDirectory = "not-a-real-directory";

            shell.NavigateTo(Workspace.Calibration);
            shell.Calibration.SelectedDiscType = "BD";
            shell.Calibration.SelectFineModeCommand.Execute(null); // The small fixture is below the v13 sector threshold.
            shell.Calibration.EnsureTarget();
            Require(shell.Calibration.Target?.Parameters.Kind == CalibrationDiscKind.BluRay
                && shell.Calibration.SelectedDiscType == "BD", "BD setup builds a target without an open project");
            BurnViewModel burn = shell.CalibrationBurn;
            shell.NavigateTo(Workspace.CalibrationBurn);
            Require(!shell.IsProjectMode && shell.IsJobDockVisible && shell.CurrentWorkspace == Workspace.CalibrationBurn,
                "calibration burn setup has a job dock without opening a project");
            VerifySource(shell, burner, "BD", OpticalBurnMediaKind.BluRayData);
            Require(burn.SourceDetails.Contains("74.5 nm", StringComparison.Ordinal)
                && burn.SourceDetails.Contains("反转 3T/6T", StringComparison.Ordinal)
                && burn.SourceDetails.Contains("绘图 LBA 0–63", StringComparison.Ordinal),
                "BD summary displays the saved channel length, polarity and drawing range");
            Require(burn.Speeds[1].DisplayName == "2× · 9.2 MB/s", "BD speed uses the BD 1× rate");

            string savedSummary = burn.SourceDetails;
            // Once created, the calibration source remains its own snapshot even if the
            // project family or main-page geometry changes before the stream is started.
            SetProjectFamily(shell, ProjectDocument.CdFamily);
            shell.State.BdTotalSectors = "96";
            shell.State.BdUsableSectors = "96";
            shell.State.BdInvertMapping = false;
            burn.UpdateSourceSummary();
            Require(burn.SourceDetails == savedSummary, "summary is isolated from later project parameter edits");
            shell.IsProjectOpen = true;
            shell.IsDirty = true;
            shell.SelectWorkspace(Workspace.Export);
            shell.NavigateTo(Workspace.Calibration);
            var id = shell.Calibration.Target!.Id;
            shell.NavigateTo(Workspace.CalibrationBurn);
            shell.NavigateTo(Workspace.Calibration);
            shell.ReturnFromCalibrationCommand.Execute(null);
            Require(shell.CurrentWorkspace == Workspace.Export && shell.IsDirty && shell.Calibration.Target!.Id == id,
                "return from calibration preserves the open project and its originating page");
            shell.NavigateTo(Workspace.Calibration);
            shell.NavigateTo(Workspace.CalibrationBurn);
            VerifySource(shell, burner, "BD", OpticalBurnMediaKind.BluRayData);
            shell.Calibration.SaveSessionTo(Path.Combine(directory, "bd.calibration.json"));
            shell.RegisterOutput(Path.Combine(directory, "drawing.iso"));
            string? confirmation = null;
            shell.ConfirmHandler = (_, message, _) =>
            {
                confirmation = message;
                return Task.FromResult(true);
            };
            burn.ConfirmChecked = true;
            burn.StartBurnCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Require(burner.Request is { MediaKind: OpticalBurnMediaKind.BluRayData, ContentLength: 64 * 2048 },
                "the displayed saved BD target is the request passed to the recording service");
            Require(confirmation?.Contains("BD 标定图案", StringComparison.Ordinal) == true
                && !confirmation.Contains("CD 标定图案", StringComparison.Ordinal),
                "confirmation describes BD calibration material");
            Require(shell.LogText.Contains("开始流式刻录：BD 标定图案", StringComparison.Ordinal)
                && !shell.LogText.Contains("CD 标定图案", StringComparison.Ordinal),
                "burn log describes BD calibration material");
            Require(shell.IsBurnTelemetryVisible && shell.JobProgressValue == 100 && shell.JobHasResult
                && shell.JobStatusText.Contains("BD 标定图案", StringComparison.Ordinal)
                && shell.ResultActionsVisibility == Visibility.Collapsed
                && shell.CanPreviewLastOutput && shell.OpenOutputEnabled,
                "calibration burning reports completion in the shared dock without presenting the previous project file as its output");

            foreach ((string label, OpticalBurnMediaKind mediaKind) in new[]
            {
                ("DVD", OpticalBurnMediaKind.DvdData),
                ("CD", OpticalBurnMediaKind.CdAudio),
                ("BD", OpticalBurnMediaKind.BluRayData),
            })
            {
                int previousQueries = burner.SpeedQueries.Count;
                if (label == "BD")
                {
                    state.BdTotalSectors = "96";
            state.BdUsableSectors = "96";
                    state.BdInvertMapping = false;
                }
                shell.Calibration.SelectedDiscType = label;
                shell.Calibration.SelectFineModeCommand.Execute(null); // Speed routing also uses small test capacities.
                shell.Calibration.CreateTargetCommand.Execute(null);
                Require(burner.SpeedQueries.Count > previousQueries, "changing the target refreshes its media-specific speeds");
                VerifySource(shell, burner, label, mediaKind);
            }
            Require(burn.SourceDetails.Contains("标准 3T/6T", StringComparison.Ordinal)
                && burn.SourceDetails.Contains("绘图 LBA 0–95", StringComparison.Ordinal),
                "a new BD target updates its polarity and range");
        }
        finally
        {
            shell?.Calibration.OnWindowClosed();
            shell?.LivePreview.OnWindowClosed();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            CultureInfo.CurrentCulture = previousCulture;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifySource(ShellViewModel shell, RecordingBurner burner,
        string label, OpticalBurnMediaKind mediaKind)
    {
        BurnViewModel burn = shell.CalibrationBurn;
        Require(burn.FamilyLabel == label && burn.SourceTitle.StartsWith($"标定图案 · {label} · ", StringComparison.Ordinal),
            $"{label} header and source title agree");
        Require(burner.SpeedQueries[^1] == mediaKind, $"{label} speed query agrees with the target");
        Require(burn.IsBluRayBurn == (label == "BD") && burn.IsDvdBurn == (label == "DVD"),
            $"{label} burn flags follow the selected target");
        Require(label == "CD"
                ? burn.SourceDetails.Contains("mm/s", StringComparison.Ordinal)
                    && burn.SourceDetails.Contains("延迟交织", StringComparison.Ordinal)
                : burn.SourceDetails.Contains(" nm", StringComparison.Ordinal)
                    && !burn.SourceDetails.Contains("mm/s", StringComparison.Ordinal)
                    && !burn.SourceDetails.Contains("交织", StringComparison.Ordinal),
            $"{label} summary uses the target's encoding units");
    }

    private static void SetProjectFamily(ShellViewModel shell, string family)
    {
        ProjectDocument document = new(ProjectJsonStore.CurrentSchemaVersion, "memory-only", family,
            new(24, 58, 64), [], new("", false, "", "TEST", false), null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(shell, document);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Calibration burn source: " + message);
    }

    private sealed class RecordingBurner : IOpticalDiscBurner
    {
        internal List<OpticalBurnMediaKind> SpeedQueries { get; } = [];
        internal OpticalBurnRequest? Request { get; private set; }

        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([new("memory-only", "Memory recorder", [])]);

        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OpticalMediaCapacity> GetMediaCapacityAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => Task.FromResult(new OpticalMediaCapacity(96, 96));

        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
        {
            SpeedQueries.Add(mediaKind);
            return Task.FromResult<IReadOnlyList<OpticalWriteSpeed>>([new(4500, false)]);
        }

        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            // Validate and capture the request without opening a device or generating content.
            request.Validate();
            Request = request;
            return Task.FromResult(new OpticalBurnResult(request.DeviceId, request.MediaKind,
                request.ContentLength, TimeSpan.FromSeconds(1)));
        }
    }
}
