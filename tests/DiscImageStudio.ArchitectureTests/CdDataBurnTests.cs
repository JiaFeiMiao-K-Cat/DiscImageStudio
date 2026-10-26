using System.Dynamic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class CdDataBurnTests
{
    internal static void Run()
    {
        TestMediaAndSpeed();
        TestCapacityDiagnostics();
        TestProgress();
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-data-burn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string imagePath = Path.Combine(directory, "source.png");
            WriteSourceImage(imagePath);
            string files = Path.Combine(directory, "files");
            Directory.CreateDirectory(files);
            File.WriteAllText(Path.Combine(files, "hello.txt"), "CD data burn keeps user files readable.");
            CdDiscParameters parameters = new(24.5, 25.5, 128);
            ToneProfile tone = new(0.75, ToneProfile.TwoLevels, 7);
            OpticalWriteSpeed speed = new(300, false);
            foreach (bool outer in new[] { false, true })
            {
                OpticalBurnRequest request = BurnViewModel.CreateCdBurnRequest(imagePath, parameters,
                    files, "CD_FILES", outer, interleave: true, tone, "memory-only-device", speed);
                request.Validate();
                Require(request.MediaKind == OpticalBurnMediaKind.CdData
                    && request.ContentLength == parameters.Sectors * 2048
                    && request.DeviceId == "memory-only-device" && request.WriteSpeed == speed,
                    "file mode requests 2048-byte data sectors and preserves device/speed");
                byte[] actual = Produce(request);
                CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(files, parameters, "CD_FILES", outer);
                using MemoryStream direct = new();
                CdDataImageGenerator.GenerateToStream(imagePath, direct, parameters, plan, tone: tone);
                Require(SameDataApartFromBuildTime(actual, direct.ToArray()),
                    "forward-only burn producer matches data generation for each ring");
                OpticalBurnRequest noInterleave = BurnViewModel.CreateCdBurnRequest(imagePath, parameters,
                    files, "CD_FILES", outer, interleave: false, tone, "memory-only-device", speed);
                Require(SameDataApartFromBuildTime(actual, Produce(noInterleave)),
                    "CD-DA interleave option never changes data sectors");
                TestCancelledProducer(request);
            }

            foreach (bool interleave in new[] { false, true })
            {
                OpticalBurnRequest audio = BurnViewModel.CreateCdBurnRequest(imagePath, parameters,
                    " ", "unused", filesAtOuterRing: true, interleave, tone, "memory-only-device", speed);
                audio.Validate();
                Require(audio.MediaKind == OpticalBurnMediaKind.CdAudio
                    && audio.ContentLength == parameters.TotalBytes,
                    "clearing files restores the complete 2352-byte audio track");
                using MemoryStream direct = new();
                CdTrackGenerator.GenerateToStream(imagePath, direct, parameters, interleave,
                    audioByteOrder: CdAudioByteOrder.LittleEndian, tone: tone);
                Require(Produce(audio).AsSpan().SequenceEqual(direct.ToArray()),
                    "no-file burn preserves PCM byte order and optional audio interleave");
            }

            CalibrationTarget target = CalibrationTarget.Create(new(CalibrationDiscKind.Cd,
                parameters.InnerRadiusMm, parameters.OuterRadiusMm, 8,
                CdDiscParameters.StandardLinearVelocityMmPerSecond));
            OpticalBurnRequest calibration = BurnViewModel.CreateCalibrationBurnRequest(target,
                true, "memory-only-device", speed);
            Require(calibration.MediaKind == OpticalBurnMediaKind.CdAudio,
                "CD calibration remains audio independently of project file mode");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("cd-data-burn: passed (data/audio selection, ring streaming, media, capacity, speed, progress; no device access)");
    }

    private static void TestMediaAndSpeed()
    {
        Require(OpticalBurnMedia.GetSectorBytes(OpticalBurnMediaKind.CdData) == 2048,
            "CD data sector length");
        OpticalWriteSpeed oneX = new(75, false);
        Require(oneX.GetMultiplier(OpticalBurnMediaKind.CdData) == 1
            && Math.Abs(oneX.GetMegabytesPerSecond(OpticalBurnMediaKind.CdData) - 0.1536) < 1e-12,
            "CD data 1x is 75 logical sectors per second");
        Require(WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.CdData, 0x2, [0x9])
            && WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.CdData, 0x3, [0xA]),
            "CD data accepts matched writable CD-R and CD-RW media");
        Require(!WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.CdData, 0x1, [0x8])
            && !WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.CdData, 0x2, [0xA])
            && !WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.CdData, 0x9, [0x11])
            && !WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.CdData, 0x12, [0x41]),
            "CD data rejects read-only, mismatched, DVD and BD media");
        OpticalBurnRequest request = new("memory-only-device", OpticalBurnMediaKind.CdData,
            128L * 2048, (_, _) => { });
        request.Validate();
        WindowsImapiBurner.ValidateDataMedia(request, 0x2, 128);
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateDataMedia(request, 0, 128),
            "nonblank CD data disc is rejected");
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateDataMedia(request, 0x2, 127),
            "undersized CD data disc is rejected without audio overburn allowance");
        Throws<ArgumentOutOfRangeException>(() => (request with { ContentLength = 2352 }).Validate(),
            "data stream rejects audio sector length");
    }

    private static void TestCapacityDiagnostics()
    {
        // The generic cooked-data capacity validator remains strict. Production
        // CD data now uses the separate DAO lead-out check, just like CD audio.
        OpticalBurnRequest request = new("memory-only-device", OpticalBurnMediaKind.CdData,
            359_849L * 2048, (_, _) => throw new InvalidOperationException("Capacity validation must not generate data."));
        try
        {
            WindowsImapiBurner.ValidateDataMedia(request, 0x6, 359_843);
            throw new InvalidOperationException("Expected the six-sector deficit to be rejected.");
        }
        catch (InvalidOperationException exception)
        {
            Require(exception.Message.Contains($"需要 {359_849:N0} 扇区")
                && exception.Message.Contains($"实际可写 {359_843:N0} 扇区")
                && exception.Message.Contains("超出 6 扇区")
                && exception.Message.Contains("MiB"),
                "capacity failure reports required, available and missing sectors with binary size units");
        }
        WindowsImapiBurner.ValidateDataMedia(request with { ContentLength = 359_843L * 2048 }, 0x6, 359_843);
        foreach (OpticalBurnMediaKind kind in new[] { OpticalBurnMediaKind.CdData, OpticalBurnMediaKind.DvdData })
        {
            Require(WindowsImapiBurner.ValidateWritableDataCapacity(kind, 0x6, 359_843).Length > 0,
                "blank-plus-appendable media has valid usable capacity");
            foreach (long invalid in new[] { 0L, -1L })
                Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateWritableDataCapacity(kind, 0x6, invalid),
                    "zero or negative reported capacity is rejected");
            Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateWritableDataCapacity(kind, 0x4, 359_843),
                "appendable but nonblank media is rejected by the cooked-data validator");
        }
    }

    private static void TestProgress()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        ImapiWriteProgressReporter progress = new(OpticalBurnMediaKind.CdData, 1000L * 2048,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        progress.ReportDataUpdate(Event(-1));
        seconds = 1;
        progress.ReportDataUpdate(Event(74));
        Require(updates[^1].CompletedBytes == 75L * 2048
            && updates[^1].BytesPerSecond == 153600,
            "CD data progress measures 2048-byte sectors from the data format event");

        updates.Clear();
        seconds = 0;
        ImapiWriteProgressReporter dao = new(OpticalBurnMediaKind.CdData, 1000L * 2048,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        dao.ReportRawCdUpdate(Event(-100, 2));
        Require(updates[^1].CompletedBytes == 0, "data DAO lead-in does not count as ISO content");
        dao.ReportRawCdUpdate(Event(-1, 2));
        dao.ReportRawCdUpdate(Event(0, 2));
        seconds = 1;
        dao.ReportRawCdUpdate(Event(75, 2));
        Require(updates[^1].CompletedBytes == 76L * 2048
            && updates[^1].BytesPerSecond == 153600,
            "data DAO progress counts cooked 2048-byte payloads, not audio or RAW framing");

        static object Event(int last, int action = 4)
        {
            dynamic value = new ExpandoObject();
            value.StartLba = 0;
            value.LastWrittenLba = last;
            value.CurrentAction = action;
            return value;
        }
    }

    private static byte[] Produce(OpticalBurnRequest request)
    {
        using MemoryStream backing = new();
        using (ForwardOnlyWriteStream output = new(backing))
            request.ProduceContent(output, CancellationToken.None);
        Require(backing.Length == request.ContentLength, "producer length matches the request");
        return backing.ToArray();
    }

    private static bool SameDataApartFromBuildTime(byte[] first, byte[] second)
    {
        // Each independently planned ISO has its own volume creation time; all
        // directory/file extents, payload bytes and drawing sectors must agree.
        byte[] left = (byte[])first.Clone();
        byte[] right = (byte[])second.Clone();
        foreach (int sector in new[] { 16, 17 })
        {
            foreach ((int offset, int count) in new[] { (174, 7), (813, 17), (830, 17) })
            {
                left.AsSpan(sector * 2048 + offset, count).Clear();
                right.AsSpan(sector * 2048 + offset, count).Clear();
            }
        }
        return left.AsSpan().SequenceEqual(right);
    }

    private static void TestCancelledProducer(OpticalBurnRequest request)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using MemoryStream output = new();
        Throws<OperationCanceledException>(() => request.ProduceContent(output, cancellation.Token),
            "cancelled data producer stops before writing");
        Require(output.Length == 0, "cancelled producer writes no data");
    }

    private static void WriteSourceImage(string path)
    {
        byte[] pixels = Enumerable.Range(0, 32 * 32).Select(i => (byte)(i % 256)).ToArray();
        BitmapSource bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Gray8, null, pixels, 32);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"CD data burn: {message}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"CD data burn: {message}.");
    }
}
