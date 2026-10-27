using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
            DiscParametersState state = shell.State;
            state.CdInnerRadius = state.DvdInnerRadius = state.BdInnerRadius = "24";
            state.CdOuterRadius = state.DvdOuterRadius = state.BdOuterRadius = "58";
            state.CdSectors = "8";
            state.DvdTotalSectors = "16";
            state.DvdPitchLinear = state.DvdPitchQuadratic = state.DvdPitchCubic = "0";
            state.BdTotalSectors = "64";
            state.BdInvertMapping = true;
            state.CdInterleave = true;
            // A data directory must not change a calibration target into a CD data burn.
            state.CdDataDirectory = "not-a-real-directory";

            // Avoid opening a real project or touching the user's recent-project history.
            SetProjectFamily(shell, ProjectDocument.BluRayFamily);
            shell.Calibration.EnsureTarget();
            Require(shell.Calibration.Target?.Parameters.Kind == CalibrationDiscKind.BluRay
                && shell.Calibration.SelectedDiscType == "BD", "first target follows the BD project");
            BurnViewModel burn = shell.Burn;
            burn.UseCalibrationPattern = true;
            burn.RefreshDevicesAsync().GetAwaiter().GetResult();
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
            state.BdTotalSectors = "96";
            state.BdInvertMapping = false;
            burn.UpdateSourceSummary();
            Require(burn.SourceDetails == savedSummary, "summary is isolated from later project parameter edits");
            VerifySource(shell, burner, "BD", OpticalBurnMediaKind.BluRayData);
            shell.Calibration.SaveSessionTo(Path.Combine(directory, "bd.calibration.json"));
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

            foreach ((string label, OpticalBurnMediaKind mediaKind) in new[]
            {
                ("DVD", OpticalBurnMediaKind.DvdData),
                ("CD", OpticalBurnMediaKind.CdAudio),
                ("BD", OpticalBurnMediaKind.BluRayData),
            })
            {
                int previousQueries = burner.SpeedQueries.Count;
                shell.Calibration.SelectedDiscType = label;
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
        BurnViewModel burn = shell.Burn;
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
