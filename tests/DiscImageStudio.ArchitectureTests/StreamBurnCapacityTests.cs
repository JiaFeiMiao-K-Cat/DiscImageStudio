using System.IO;
using DiscImageStudio.Burning;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class StreamBurnCapacityTests
{
    internal static void Run()
    {
        Require(StreamBurnCapacity.Resolve(2_297_888, 2_297_888, OpticalBurnMediaKind.DvdData) == 2_297_888,
            "an explicit DVD limit and reported free count are not reduced twice");
        Require(StreamBurnCapacity.Resolve(359_843, 359_843, OpticalBurnMediaKind.CdAudio) == 359_843,
            "an explicit CD limit is not reduced twice");
        Require(StreamBurnCapacity.Resolve(1024, 713, OpticalBurnMediaKind.DvdData) == 704,
            "device limits round down to a complete DVD block");
        Require(StreamBurnCapacity.Resolve(1024, 713, OpticalBurnMediaKind.BluRayData) == 704,
            "device limits round down to a complete BD cluster");
        try
        {
            StreamBurnCapacity.Resolve(1024, 0, OpticalBurnMediaKind.DvdData);
            throw new Exception("A full medium was accepted.");
        }
        catch (InvalidOperationException) { }

        CheckRequest(new(CalibrationDiscKind.Cd, 24, 58, 128, 1200, UsableSectors: 96), 62, 62, 2352);
        CheckRequest(new(CalibrationDiscKind.Dvd, 24, 58, 1024, 133.3, UsableSectors: 512), 510, 496, 2048);
        CheckRequest(new(CalibrationDiscKind.BluRay, 24, 58, 128, 74.5, UsableSectors: 96), 95, 64, 2048);
        Console.WriteLine("stream-burn-capacity: passed (explicit limits, device constraints, alignment, captured calibration streams)");
    }

    private static void CheckRequest(CalibrationParameters parameters, long deviceFree, long outputSectors, int sectorBytes)
    {
        CalibrationTarget target = CalibrationTarget.Create(parameters);
        OpticalBurnRequest full = BurnViewModel.CreateCalibrationBurnRequest(target, false, "memory-only", null);
        OpticalBurnRequest limited = BurnViewModel.CreateCalibrationBurnRequest(target, false, "memory-only", null, deviceFree);
        Require(limited.ContentLength == outputSectors * sectorBytes, "the request declares the aligned writable payload");
        using MemoryStream expected = new(), actual = new();
        full.ProduceContent(expected, CancellationToken.None);
        limited.ProduceContent(actual, CancellationToken.None);
        Require(actual.Length == limited.ContentLength, "the producer stops exactly at the requested limit");
        Require(expected.ToArray().AsSpan(0, (int)actual.Length).SequenceEqual(actual.ToArray()),
            "truncating a frozen calibration preserves existing physical addresses and samples");
        Require(target.Parameters == parameters, "device capacity does not rewrite the saved geometry or preset limit");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
