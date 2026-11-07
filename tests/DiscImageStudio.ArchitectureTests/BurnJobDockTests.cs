using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.ViewModels;

internal static class BurnJobDockTests
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
        Console.WriteLine("burn-job-dock: passed (standalone telemetry, navigation, cancellation, failure, output isolation and late callbacks; no device access)");
    }

    private static void RunOnSta()
    {
        string directory = Path.Combine(Path.GetTempPath(), "burn-job-dock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        ShellViewModel? shell = null;
        using ControlledBurner burner = new();
        try
        {
            shell = new ShellViewModel(Dispatcher.CurrentDispatcher, burner);
            shell.NavigateTo(Workspace.Calibration);
            Require(!shell.IsProjectOpen && shell.IsJobDockVisible, "standalone calibration has the shared dock");
            shell.Calibration.Parameters.BdTotalSectors = "64";
            shell.Calibration.Parameters.BdUsableSectors = "64";
            shell.Calibration.SelectedDiscType = "BD";
            shell.Calibration.SelectFineModeCommand.Execute(null);
            shell.Calibration.EnsureTarget();
            shell.Calibration.SaveSessionTo(Path.Combine(directory, "target.calibration.json"));
            shell.NavigateTo(Workspace.CalibrationBurn);
            shell.ConfirmHandler = (_, _, _) => Task.FromResult(true);

            string previousOutput = Path.Combine(directory, "previous.iso");
            shell.RegisterOutput(previousOutput);
            Require(shell.ResultActionsVisibility == Visibility.Visible, "a generated file has output actions");
            BurnViewModel burn = shell.CalibrationBurn;
            Task successful = Start(burn);
            Require(shell.IsBusy && shell.IsJobRunning && burn.IsBurnRunning && !shell.Burn.IsBurnRunning,
                "the calibration burn occupies the shell independently of project burning");
            Require(shell.IsBurnTelemetryVisible && shell.JobProgressValue == 0
                && shell.ResultActionsVisibility == Visibility.Collapsed
                && shell.CancelJobCommand.CanExecute(null) && shell.CancelJobVisibility == Visibility.Visible,
                "the shared dock owns progress and cancellation without claiming the previous file");

            burner.Report("transfer", "producer transferred all bytes", burner.TotalBytes);
            DrainDispatcher();
            Require(shell.JobProgressValue == 0, "producer transfer cannot advance device-written progress");
            burner.Report("write", "device acknowledged one quarter", burner.TotalBytes / 4, 9_000_000);
            DrainDispatcher();
            Require(shell.JobProgressValue == 25 && shell.BurnMultiplierText == "2.0×"
                && shell.LogText.Contains("device acknowledged one quarter", StringComparison.Ordinal),
                "device measurements and diagnostic logs reach the shared dock");
            foreach (string stage in new[] { "finalize", "write-complete", "verify", "flush", "close-track", "close-disc", "release" })
            {
                string message = "recorder phase: " + stage;
                burner.Report(stage, message, burner.TotalBytes);
                DrainDispatcher();
                Require(shell.IsBusy && shell.IsJobRunning && burn.IsBurnRunning && !shell.JobHasResult
                    && shell.JobProgressValue == 25 && shell.BurnMultiplierText == "—×"
                    && shell.JobStatusText == message && shell.LogText.Contains(message, StringComparison.Ordinal),
                    stage + " updates the dock and log without prematurely completing the burn");
            }
            burner.Report("diagnostic", "release result and last written LBA", burner.TotalBytes);
            DrainDispatcher();
            Require(shell.LogText.Contains("release result and last written LBA", StringComparison.Ordinal),
                "final diagnostics remain visible after the write-complete event");
            shell.NavigateTo(Workspace.Home);
            Require(shell.IsJobDockVisible && shell.CancelJobCommand.CanExecute(null),
                "returning Home cannot hide an active burn or its cancel action");
            IProgress<OpticalBurnProgress> oldProgress = burner.Progress!;
            burner.Succeed();
            Wait(successful);
            Require(!shell.IsBusy && !shell.IsJobDockVisible && shell.JobHasResult
                && shell.JobProgressValue == 100 && !shell.CancelJobCommand.CanExecute(null),
                "successful completion releases the shell and hides the idle Home dock");
            Require(shell.ResultActionsVisibility == Visibility.Collapsed
                && shell.CanPreviewLastOutput && shell.OpenOutputEnabled
                && (string?)typeof(ShellViewModel).GetField("_lastOutputPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(shell) == previousOutput,
                "burning preserves the prior file without advertising it as a new output");
            string completedLog = shell.LogText;
            string completedStatus = shell.JobStatusText;
            oldProgress.Report(new("diagnostic", "late completed callback", 1, burner.TotalBytes));
            DrainDispatcher();
            Require(shell.LogText == completedLog && shell.JobStatusText == completedStatus && shell.JobProgressValue == 100,
                "callbacks arriving after completion cannot alter the recorded outcome");

            shell.RegisterOutput(Path.Combine(directory, "newly-exported.iso"));
            Require(!shell.IsBurnTelemetryVisible && shell.ResultActionsVisibility == Visibility.Visible
                && shell.JobStatusText.Contains("newly-exported.iso", StringComparison.Ordinal),
                "registering a new file replaces the completed burn telemetry and restores file result actions");

            shell.NavigateTo(Workspace.CalibrationBurn);
            Require(shell.IsJobDockVisible, "returning to calibration exposes the completed burn log");
            burner.DelayCancellation = true;
            Task cancelled = Start(burn);
            oldProgress.Report(new("write", "callback from previous burn", burner.TotalBytes, burner.TotalBytes));
            DrainDispatcher();
            Require(shell.JobProgressValue == 0 && !shell.LogText.Contains("callback from previous burn", StringComparison.Ordinal),
                "callbacks from an earlier burn cannot update a later burn");
            burner.Report("write", "device acknowledged half", burner.TotalBytes / 2, 9_000_000);
            DrainDispatcher();
            string writingStatus = shell.JobStatusText;
            shell.CancelJobCommand.Execute(null);
            string cancellationStatus = shell.JobStatusText;
            Require(burner.CancellationRequested && shell.IsBusy && !cancelled.IsCompleted
                && cancellationStatus != writingStatus,
                "a cancellation request remains active until the recorder actually stops");
            burner.Report("write", "device continues writing while stopping", burner.TotalBytes * 3 / 4, 9_000_000);
            DrainDispatcher();
            Require(shell.JobProgressValue == 75 && shell.JobStatusText == cancellationStatus,
                "device progress continues after cancellation without replacing the stopping message");
            burner.Report("finalize", "device finishes closing after cancellation", burner.TotalBytes * 3 / 4);
            DrainDispatcher();
            Require(shell.JobProgressValue == 75 && shell.JobStatusText == cancellationStatus,
                "late finalization keeps the cancellation message until the recorder exits");
            burner.Report("release", "device releases after cancellation", burner.TotalBytes * 3 / 4);
            DrainDispatcher();
            Require(shell.JobStatusText == cancellationStatus
                && shell.LogText.Contains("device releases after cancellation", StringComparison.Ordinal),
                "release diagnostics stay visible without replacing the stopping message");
            shell.SetLastOutputPath(Path.Combine(directory, "preview-during-burn.png"));
            Require(shell.IsBurnTelemetryVisible && shell.JobProgressValue == 75
                && shell.JobStatusText == cancellationStatus && !shell.HasJobOutput
                && shell.ResultActionsVisibility == Visibility.Collapsed,
                "exporting a preview cannot replace an active burn's telemetry or cancellation status");
            burner.FinishCancellation();
            Wait(cancelled);
            Require(burner.CancellationRequested && !shell.IsBusy && !burn.IsBurnRunning
                && !shell.JobHasResult && shell.JobProgressValue == 75
                && shell.CancelJobVisibility == Visibility.Collapsed,
                "the dock cancels the active calibration recorder and preserves the last device-written count");
            Require(shell.LogText.Contains("OperationCanceledException", StringComparison.Ordinal)
                && shell.LogText.Contains("HRESULT=", StringComparison.Ordinal)
                && shell.LogText.Contains("Burn.CancellationContext", StringComparison.Ordinal)
                && shell.LogText.Contains("release context preserved", StringComparison.Ordinal),
                "cancellation retains the exception type, HRESULT, inner details and attached recorder context");

            shell.IsJobDockExpanded = false;
            burner.DelayCancellation = false;
            Task failed = Start(burn);
            COMException recorderError = new("feature page is not current", unchecked((int)0xC0AA020B));
            recorderError.Data["Burn.LastWrittenLba"] = 12345;
            IOException writeError = new("memory recorder failed", recorderError);
            writeError.Data["Burn.Operation"] = "IDiscFormat2Data.Write";
            burner.Fail(writeError);
            Wait(failed);
            Require(!shell.IsBusy && !shell.JobHasResult && shell.IsJobDockExpanded
                && shell.LogText.Contains("memory recorder failed", StringComparison.Ordinal),
                "a failed recording releases the shell and expands its shared error log");
            Require(shell.LogText.Contains("0xC0AA020B", StringComparison.Ordinal)
                && shell.LogText.Contains("feature page is not current", StringComparison.Ordinal)
                && shell.LogText.Contains("Burn.LastWrittenLba: 12345", StringComparison.Ordinal)
                && shell.LogText.Contains("Burn.Operation: IDiscFormat2Data.Write", StringComparison.Ordinal)
                && shell.LogText.Contains("ControlledBurner.Fail", StringComparison.Ordinal),
                "the shared failure log retains inner HRESULTs, captured context and the throwing stack frame");
            Require(!shell.Toasts[^1].Message.Contains("0xC0AA020B", StringComparison.Ordinal)
                && !shell.Toasts[^1].Message.Contains("ControlledBurner", StringComparison.Ordinal),
                "full diagnostics remain in the task log rather than the brief failure toast");

            shell.BeginDockJob("new calibration calculation", determinate: false);
            Require(!shell.IsBurnTelemetryVisible && shell.JobProgressValue == 0
                && shell.ResultActionsVisibility == Visibility.Collapsed,
                "a subsequent calibration task replaces stale burn telemetry without exposing a stale file");
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

    private static Task Start(BurnViewModel burn)
    {
        burn.ConfirmChecked = true;
        Require(burn.StartBurnCommand.CanExecute(null), "the prepared recorder is ready to start");
        return burn.StartBurnCommand.ExecuteAsync(null);
    }

    private static void DrainDispatcher()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Wait(Task task)
    {
        DispatcherFrame frame = new();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => frame.Continue = false;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        timeout.Start();
        try { if (!task.IsCompleted) Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Require(task.IsCompleted, "the recorder continuation completed within the test timeout");
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Burn job dock: " + message);
    }

    private sealed class ControlledBurner : IOpticalDiscBurner, IDisposable
    {
        private TaskCompletionSource<OpticalBurnResult>? _completion;
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationToken _cancellationToken;
        private OpticalBurnRequest? _request;
        internal IProgress<OpticalBurnProgress>? Progress { get; private set; }
        internal bool CancellationRequested { get; private set; }
        internal bool DelayCancellation { get; set; }
        internal long TotalBytes => _request!.ContentLength;

        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([new("memory-only", "Memory recorder", [])]);

        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OpticalMediaCapacity> GetMediaCapacityAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => Task.FromResult(new OpticalMediaCapacity(64, 64));

        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalWriteSpeed>>([new(4500, false)]);

        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            request.Validate();
            _request = request;
            Progress = progress;
            CancellationRequested = false;
            _cancellationToken = cancellationToken;
            _cancellationRegistration.Dispose();
            TaskCompletionSource<OpticalBurnResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion;
            _cancellationRegistration = cancellationToken.Register(() =>
            {
                CancellationRequested = true;
                if (!DelayCancellation) completion.TrySetCanceled(cancellationToken);
            });
            return completion.Task;
        }

        internal void Report(string stage, string message, long completedBytes, double? rate = null)
            => Progress!.Report(new(stage, message, completedBytes, TotalBytes, rate));

        internal void Succeed() => _completion!.SetResult(new OpticalBurnResult(
            _request!.DeviceId, _request.MediaKind, TotalBytes, TimeSpan.FromSeconds(1)));

        internal void Fail(Exception exception)
        {
            try { throw exception; }
            catch (Exception captured) { _completion!.SetException(captured); }
        }

        internal void FinishCancellation()
        {
            OperationCanceledException exception = new("recorder stopped", new IOException("release context preserved"), _cancellationToken);
            exception.Data["Burn.CancellationContext"] = "release";
            Fail(exception);
        }

        public void Dispose() => _cancellationRegistration.Dispose();
    }
}
