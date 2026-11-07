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
        Require(backend.Queries.SequenceEqual([("a", OpticalBurnMediaKind.DvdData)])
            && model.Parameters.DvdTotalSectors == "714546" && model.Parameters.DvdUsableSectors == "713936",
            "one recorder preserves raw geometry and records the separate writable count without subtracting 608 again");
        Require(ReferenceEquals(model.Target, existing) && existing.Parameters.Sectors == 2295104
            && shell.State.DvdTotalSectors == projectSectors && !shell.IsDirty, "reading a disc does not change a burn snapshot or drawing project");
        model.CreateTargetCommand.Execute(null);
        Require(model.Target!.Parameters.Sectors == 714546, "new target keeps the reported geometry extent");

        model.SelectedDiscType = "CD";
        backend.Devices = [a, b];
        model.SelectedCapacityRecorder = null;
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.HasRecorderChoice && model.SelectedCapacityRecorder is null && backend.Queries.Count == 1,
            "multiple recorders require an explicit choice");
        model.SelectedCapacityRecorder = b;
        backend.Read = () => Task.FromResult(new OpticalMediaCapacity(359845, 359843));
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(backend.Queries[^1] == ("b", OpticalBurnMediaKind.CdAudio)
            && model.Parameters.CdSectors == "359845" && model.Parameters.CdUsableSectors == "359843",
            "the selected CD recorder supplies independent geometry and conservative payload limits");
        backend.Read = () => Task.FromException<OpticalMediaCapacity>(new IOException("no readable medium"));
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.CapacityStatus.Contains("no readable medium") && model.Parameters.CdSectors == "359845"
            && model.Parameters.CdUsableSectors == "359843" && model.Parameters.DvdTotalSectors == "714546"
            && model.Parameters.DvdUsableSectors == "713936", "failure leaves both valid capacity values available");
        backend.Devices = [];
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(!model.HasRecorderChoice && model.SelectedCapacityRecorder is null && backend.Queries.Count == 3
            && !string.IsNullOrWhiteSpace(model.CapacityStatus), "empty drive list exits with a status and performs no media query");

        model.SelectedDiscType = "DVD";
        backend.Devices = [a];
        backend.Read = () => Task.FromResult(new OpticalMediaCapacity(714546, 713951));
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.Parameters.DvdTotalSectors == "714546" && model.Parameters.DvdUsableSectors == "713936",
            "only the writable output limit is rounded down to a complete DVD block");
        backend.Read = () => Task.FromResult(new OpticalMediaCapacity(714560, 0));
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.Parameters.DvdTotalSectors == "714560" && model.Parameters.DvdUsableSectors == string.Empty
            && model.Parameters.GetUsableSectors(DiscImageStudio.Core.OpticalDiscFamily.Dvd) == 713952
            && !string.IsNullOrWhiteSpace(model.CapacityStatus),
            "a recorded disc still supplies geometry, clears a stale writable limit, and explicitly uses the default estimate");
        model.OnWindowClosed();
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Calibration media read: " + description);
    }

    private sealed class Reader : IOpticalDiscBurner
    {
        internal IReadOnlyList<OpticalBurnDevice> Devices = [];
        internal Func<Task<OpticalMediaCapacity>> Read = () => Task.FromResult(new OpticalMediaCapacity(714546, 713936));
        internal List<(string, OpticalBurnMediaKind)> Queries { get; } = [];
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Devices);
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Calibration must read both capacity values together.");
        public Task<OpticalMediaCapacity> GetMediaCapacityAsync(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
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
