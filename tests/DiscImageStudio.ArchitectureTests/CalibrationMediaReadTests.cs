using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.ViewModels;

internal static class CalibrationMediaReadTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckEditor(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("calibration-media-read: recorder selection, editable capacity, target isolation and read failures passed");
    }

    private static void CheckEditor()
    {
        var a = new OpticalBurnDevice("a", "Recorder A", []);
        var b = new OpticalBurnDevice("b", "Recorder B", []);
        var backend = new Reader { Devices = [a] };
        var shell = new ShellViewModel(Dispatcher.CurrentDispatcher, backend);
        shell.NavigateTo(Workspace.Calibration);
        var model = shell.Calibration;
        model.SelectedDiscType = "DVD";
        model.Parameters.DvdTotalSectors = "2295104";
        model.CreateTargetCommand.Execute(null);
        var existing = model.Target ?? throw new InvalidOperationException(model.LastStatusMessage);
        string projectSectors = shell.State.DvdTotalSectors;
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(backend.Queries.SequenceEqual([("a", OpticalBurnMediaKind.DvdData)]) && model.Parameters.DvdTotalSectors == "714544",
            "one recorder fills the selected family's editable sector count in one operation");
        Require(ReferenceEquals(model.Target, existing) && existing.Parameters.Sectors == 2295104
            && shell.State.DvdTotalSectors == projectSectors && !shell.IsDirty, "reading a disc does not change a burn snapshot or drawing project");
        model.CreateTargetCommand.Execute(null);
        Require(model.Target!.Parameters.Sectors == 714544, "new target consumes the reported sectors");

        model.SelectedDiscType = "CD";
        backend.Devices = [a, b];
        model.SelectedCapacityRecorder = null;
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.HasRecorderChoice && model.SelectedCapacityRecorder is null && backend.Queries.Count == 1,
            "multiple recorders require an explicit choice");
        model.SelectedCapacityRecorder = b;
        backend.Read = () => Task.FromResult(359845L);
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(backend.Queries[^1] == ("b", OpticalBurnMediaKind.CdAudio) && model.Parameters.CdSectors == "359845",
            "the selected CD recorder is read, preserving the independent DVD draft");
        backend.Read = () => Task.FromException<long>(new IOException("no readable medium"));
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.CapacityStatus.Contains("no readable medium") && model.Parameters.CdSectors == "359845"
            && model.Parameters.DvdTotalSectors == "714544", "failure leaves valid manual values available");
        backend.Devices = [];
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(!model.HasRecorderChoice && model.SelectedCapacityRecorder is null && backend.Queries.Count == 3
            && !string.IsNullOrWhiteSpace(model.CapacityStatus), "empty drive list exits with a status and performs no media query");
        model.OnWindowClosed();
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Calibration media read: " + description);
    }

    private sealed class Reader : IOpticalDiscBurner
    {
        internal IReadOnlyList<OpticalBurnDevice> Devices = [];
        internal Func<Task<long>> Read = () => Task.FromResult(714544L);
        internal List<(string, OpticalBurnMediaKind)> Queries { get; } = [];
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Devices);
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
        {
            Queries.Add((deviceId, mediaKind));
            return Read();
        }
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request, IProgress<OpticalBurnProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
