using DiscImageStudio.AutoCalibration;
using DiscImageStudio.Services;

internal static class AutoCalibrationDebugServiceTests
{
    public static void Run()
    {
        var service = new AutoCalibrationService();
        IReadOnlyList<DiscDriveInfo> drives = service.ListDrivesAsync(
            debug: true,
            progress: null,
            mediaType: "cd").GetAwaiter().GetResult();
        if (drives.Count != 1
            || !drives[0].Simulated
            || drives[0].MediaType != "CD-R"
            || drives[0].TotalSectors <= 0)
        {
            throw new InvalidOperationException("Debug mode did not expose a simulated CD drive.");
        }

        AutoCalibrationResult result = service.MeasureAsync(
            drives[0].Id,
            "cd",
            debug: true,
            (MaxTargets: 36, Reps: 32, RangeLo: 0.02, RangeHi: 0.96),
            progress: null).GetAwaiter().GetResult();
        if (result.MediaType != "cd"
            || result.Sectors <= 0
            || !double.IsFinite(result.InnerRadiusMm)
            || !double.IsFinite(result.OuterRadiusMm))
        {
            throw new InvalidOperationException("Debug mode did not produce a valid CD calibration.");
        }

        Console.WriteLine("auto-calibration debug service: simulated drive and measurement passed");
    }
}
