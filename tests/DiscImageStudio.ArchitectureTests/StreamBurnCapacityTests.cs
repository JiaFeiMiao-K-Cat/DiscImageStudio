using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;
using DiscImageStudio.Core;
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
        CheckBdReDesignRequest();
        CheckBdReCalibrationRequest();
        Console.WriteLine("stream-burn-capacity: passed (limits, alignment, captured calibration, BD-RE mapped streams)");
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

    private static void CheckBdReDesignRequest()
    {
        string source = Path.Combine(Path.GetTempPath(), $"bd-re-burn-source-{Guid.NewGuid():N}.png");
        try
        {
            using (FileStream image = File.Create(source))
            {
                byte[] pixels = [0, 192, 64, 255];
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray8, null, pixels, 2);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(image);
            }

            BluRayDiscParameters disc = new(TotalSectors: 128, UsableSectors: 64);
            BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc);
            double imageOuterRadius = disc.OutputOuterRadiusMm;
            OpticalBurnRequest request = BurnViewModel.CreateBluRayBurnRequest(source, plan, imageOuterRadius,
                ToneProfile.LegacyBinary, false, "memory-only", null, BluRayRewritablePreparation.Format);
            Require(request.BluRayRewritablePreparation == BluRayRewritablePreparation.Format,
                "BD-RE captures the preflight preparation and planned length");
            var write = request.ProduceMappedBluRayContent
                ?? throw new InvalidOperationException("BD-RE design request did not bind a mapped producer.");
            const uint physicalStart = BluRayDiscParameters.FirstLayer0PhysicalSector + 32;
            using MemoryStream actual = new(), nominal = new(), expected = new();
            write(actual, physicalStart, 96, CancellationToken.None);
            request.ProduceContent(nominal, CancellationToken.None);

            BluRayDiscParameters actualDisc = disc.WithPhysicalLayout(physicalStart, 96);
            BluRayGray8Raster raster = BluRayImageLoader.Load(source, ToneProfile.LegacyBinary, CancellationToken.None);
            BluRayIsoGenerator.GenerateToStream(expected, BluRayIsoGenerator.PrepareDrawing(actualDisc),
                new BluRayImagePayloadMapping(actualDisc, raster, new BluRayRunLengthMapping(), imageOuterRadius));
            Require(actual.Length == request.ContentLength && actual.ToArray().AsSpan().SequenceEqual(expected.ToArray()),
                "BD-RE producer applies the verified physical layout while retaining the captured drawing scale");
            Require(!actual.ToArray().AsSpan().SequenceEqual(nominal.ToArray()),
                "BD-RE producer cannot silently generate the nominal geometry's payload");
            CheckRejectedLayout(write, physicalStart, 32);
        }
        finally
        {
            File.Delete(source);
        }
    }

    private static void CheckBdReCalibrationRequest()
    {
        const uint nominalStart = BluRayDiscParameters.FirstLayer0PhysicalSector;
        CalibrationTarget target = CalibrationTarget.Create(
            new(CalibrationDiscKind.BluRay, 24, 58, 128, 74.5, UsableSectors: 96));
        OpticalBurnRequest request = BurnViewModel.CreateCalibrationBurnRequest(target, false, "memory-only",
            null, writableSectors: 64, bluRayPreparation: BluRayRewritablePreparation.Overwrite);
        var write = request.ProduceMappedBluRayContent
            ?? throw new InvalidOperationException("BD-RE calibration request did not bind a mapped producer.");
        using MemoryStream expected = new(), actual = new();
        request.ProduceContent(expected, CancellationToken.None);
        write(actual, nominalStart, BluRayDiscParameters.SectorsPerLayer, CancellationToken.None);
        Require(actual.Length == request.ContentLength && actual.ToArray().AsSpan().SequenceEqual(expected.ToArray()),
            "the verified nominal BD-RE layout preserves frozen calibration content");
        CheckRejectedLayout(write, nominalStart + 32, 96);
        CheckRejectedLayout(write, nominalStart, 32);
    }

    private static void CheckRejectedLayout(Action<Stream, uint, uint, CancellationToken> write,
        uint firstSector, uint availableSectors)
    {
        using MemoryStream output = new();
        try
        {
            write(output, firstSector, availableSectors, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            Require(output.Length == 0, "a layout conflict must be rejected before producing any sectors");
            return;
        }
        throw new InvalidOperationException("A layout incompatible with the confirmed content was accepted.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
