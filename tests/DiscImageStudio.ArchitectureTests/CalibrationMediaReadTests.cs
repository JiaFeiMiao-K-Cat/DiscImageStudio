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
        model.SelectedDiscType = "CD";
        backend.DetectedKind = OpticalBurnMediaKind.DvdData;
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.SelectedDiscType == "DVD" && !model.CalibrateDualLayerDvd
            && backend.Queries.Last() == ("a", OpticalBurnMediaKind.DvdData),
            "physical DVD switches the selected family before querying capacity");
        backend.Read = () => Task.FromException<OpticalMediaCapacity>(new DvdLayerBoundaryRequiredException());
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.CalibrateDualLayerDvd && model.CalibrationLayerBreak == "2000000"
            && backend.BoundaryDevice == "a", "DVD+R DL switches mode and reads the same recorder");
        backend.DetectedKind = OpticalBurnMediaKind.CdAudio;
        backend.Read = () => Task.FromResult(new OpticalMediaCapacity(360000, 359998));
        model.ReadDiscSectorsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(model.SelectedDiscType == "CD" && model.Parameters.CdSectors == "360000",
            "the next disc switches the family back to CD");
        model.SelectedDiscType = "DVD";
        model.CalibrateDualLayerDvd = true;
        model.CalibrationDvdLayerFormat = "DVD-R DL";
        model.CreateTargetCommand.Execute(null);
        Require(model.Target?.Parameters is { DvdLayerBreakLba: 2_092_896, DvdMinusRTotalSectors: 4_171_712 },
            "DVD-R DL defaults create a sequential target without reading a drive");
        model.CalibrationDvdLayerFormat = "DVD+R DL";
        model.CreateTargetCommand.Execute(null);
        Require(model.Target?.Parameters is { DvdLayerBreakLba: 2_086_912, DvdMinusRTotalSectors: null },
            "switching to DVD+R DL discards the DVD-R DL layout");
        model.OnWindowClosed();
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Calibration media read: " + description);
    }

    private sealed class Reader : IOpticalDiscBurner
    {
        internal IReadOnlyList<OpticalBurnDevice> Devices = [];
        internal OpticalBurnMediaKind? DetectedKind;
        internal string? BoundaryDevice;
        public Task<OpticalBurnMediaKind?> DetectCalibrationMediaKindAsync(string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(DetectedKind);
        public Task<DvdLayerBoundary> ReadDvdLayerBoundaryAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            BoundaryDevice = deviceId;
            return Task.FromResult(new DvdLayerBoundary("Recorder A", 2_000_000, false));
        }
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
