using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.ViewModels;

internal static class CalibrationBurnWorkflowTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-calibration-burn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (bool interleave in new[] { false, true })
            {
                TestCapturedRequest(directory, CalibrationDiscKind.Cd, interleave);
            }

            TestCapturedRequest(directory, CalibrationDiscKind.Dvd, false);
            TestCapturedRequest(directory, CalibrationDiscKind.BluRay, false);
            TestClockRuler(directory);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                File.Delete(file);
            }

            Directory.Delete(directory);
        }

        Console.WriteLine("calibration-burn-workflow: passed (memory-only requests, saved snapshots, reload isolation)");
    }

    private static void TestClockRuler(string directory)
    {
        const int prefixBytes = 300 * CdDiscParameters.BytesPerSector;
        var parameters = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200);
        var clock = CalibrationTarget.Create(parameters, artwork: CalibrationArtwork.ClockRuler);
        string sessionPath = Path.Combine(directory, "clock.calibration.json");
        CalibrationSessionJson.Save(sessionPath, new(clock, [], CdInterleave: true));
        var restored = CalibrationSessionJson.Load(sessionPath);
        // The whole logical track includes each guide/gutter boundary and every dither-table wrap.
        // This reference is independent of the encoder; the validation does not retain a full CD in memory or on disk.
        using (var hash = SHA256.Create())
        using (var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
        {
            CdTrackGenerator.GeneratePatternToStream(restored.Target, sink, false);
            sink.FlushFinalBlock();
            Require(Convert.ToHexStringLower(hash.Hash!) ==
                "9edebaa241661eeb689e5cda5d75f1e01cf702e1db9145acd86342ba3078b4a9",
                "complete logical clock track matches the independent schema 8 reference");
        }
        foreach (bool interleave in new[] { false, true })
        {
            string path = Path.Combine(directory, $"clock-{interleave}");
            byte[] ExportPrefix(string extension)
            {
                string file = path + extension;
                using CancellationTokenSource stop = new();
                try
                {
                    CdTrackGenerator.GeneratePattern(restored.Target, file, interleave, _ => stop.Cancel(), stop.Token);
                    throw new InvalidOperationException("Expected cancellation after the first progress report.");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                using var input = File.OpenRead(file);
                if (extension == ".wav") input.Position = 44;
                var bytes = new byte[prefixBytes];
                input.ReadExactly(bytes);
                return bytes;
            }
            byte[] raw = ExportPrefix(".raw");

            // The burn request uses different initial radii. The clock and address bands depend only on byte count.
            var other = CalibrationTarget.Create(parameters with { InnerRadiusMm = 25, OuterRadiusMm = 57 },
                artwork: CalibrationArtwork.ClockRuler);
            var request = CreateRequest(other, interleave, new OpticalWriteSpeed(300, false));
            byte[] streamed = new byte[prefixBytes];
            using var limited = new MemoryStream(streamed, 0, streamed.Length, writable: true, publiclyVisible: false);
            using var forward = new ForwardOnlyWriteStream(limited);
            try
            {
                request.ProduceContent(forward, CancellationToken.None);
                throw new InvalidOperationException("Expected the test sink to fill.");
            }
            catch (NotSupportedException) when (limited.Position == prefixBytes) { }
            Require(request.ContentLength == parameters.Sectors * CdDiscParameters.BytesPerSector
                && streamed.AsSpan().SequenceEqual(ExportPrefix(".wav")), "clock WAV and non-seekable little-endian burn agree");
            for (int i = 0; i < raw.Length; i++) Require(raw[i] == streamed[i ^ 1],
                "clock RAW and WAV differ only by 16-bit byte order after interleave");
        }
    }

    private static void TestCapturedRequest(string directory, CalibrationDiscKind kind, bool interleave)
    {
        // Constructing the shared parameter store does not enumerate or access a
        // recorder. This test never creates a burner or calls BurnAsync.
        DiscParametersState state = new()
        {
            CdInnerRadius = "24.5", CdOuterRadius = "56.8", CdSectors = "8", CdUsableSectors = "8",
            CdInterleave = interleave, CdImagePath = "unused-image-must-not-be-read.png",
            DvdInnerRadius = "24.5", DvdOuterRadius = "56.8", DvdTotalSectors = "16", DvdUsableSectors = "16",
            DvdImagePath = "unused-dvd-image-must-not-be-read.png",
            DvdDataDirectory = "unused-folder-must-not-be-read",
            BdInnerRadius = "24.0", BdOuterRadius = "58.0", BdTotalSectors = "64",
            BdImagePath = "unused-bd-image-must-not-be-read.png",
        };
        CalibrationParameters generated = ReadParameters(state, kind);
        CalibrationTarget target = CalibrationTarget.Create(generated);
        Require(target.SchemaVersion == 7 && target.VisibleRings.Count == 8,
            "the burn request captures the current combined target and all eight coarse rings");
        CalibrationSession original = new(target, [], CdInterleave: state.CdInterleave);
        string path = Path.Combine(directory, $"{kind}-{interleave}.calibration.json");
        CalibrationSessionJson.Save(path, original);
        string originalJson = File.ReadAllText(path);
        CalibrationSession activeSession = CalibrationSessionJson.Load(path);
        Require(activeSession.Target.Parameters == generated && activeSession.Target.Id == target.Id,
            "durable session restores original generation parameters and target identity");
        Require(activeSession.Target.SchemaVersion == target.SchemaVersion && activeSession.Target.VisibleRings.SequenceEqual(target.VisibleRings),
            "durable records retain the target revision and engraved ring geometry");

        OpticalWriteSpeed speed = new(300, false);
        OpticalBurnRequest request = CreateRequest(activeSession.Target, activeSession.CdInterleave, speed);
        request.Validate();
        Require(request.DeviceId == "memory-only-test-device" && request.WriteSpeed == speed,
            "request preserves selected device and speed without accessing hardware");
        Require(request.MediaKind == kind switch
            {
                CalibrationDiscKind.Cd => OpticalBurnMediaKind.CdAudio,
                CalibrationDiscKind.BluRay => OpticalBurnMediaKind.BluRayData,
                _ => OpticalBurnMediaKind.DvdData,
            },
            "request media type comes from the saved target");

        byte[] expected = GenerateDirect(target, interleave);
        Require(request.ContentLength == expected.Length, "request declares the complete generated sector length");
        Require(expected.Distinct().Count() > 1, "real whole-disc polar target produces nonconstant encoded content");

        // Main-page edits and opening a different saved calibration must not change
        // a request whose producer has already captured the original target.
        state.CdInnerRadius = "25.2";
        state.CdOuterRadius = "57.3";
        state.CdSectors = "32";
        state.CdUsableSectors = "32";
        state.CdInterleave = !interleave;
        state.DvdInnerRadius = "25.2";
        state.DvdOuterRadius = "57.3";
        state.DvdTotalSectors = "32";
        state.DvdUsableSectors = "32";
        state.BdInnerRadius = "25.2";
        state.BdOuterRadius = "57.3";
        state.BdInvertMapping = true;
        CalibrationTarget replacement = CalibrationTarget.Create(ReadParameters(state, kind));
        CalibrationSessionJson.Save(path, new(replacement, [], CdInterleave: state.CdInterleave));
        activeSession = CalibrationSessionJson.Load(path);
        Require(activeSession.Target.Parameters != generated && activeSession.Target.Id != target.Id,
            "test switches to a materially different saved target");

        byte[] captured = Produce(request);
        Require(captured.AsSpan().SequenceEqual(expected), "captured producer matches direct analytic encoding after state edits and session replacement");
        Require(Produce(request).AsSpan().SequenceEqual(captured), "captured producer is repeatable");
        Require(target.Parameters == generated, "original target parameters remain immutable");

        CalibrationSession reloadedOriginal = CalibrationSessionJson.Deserialize(originalJson);
        Require(reloadedOriginal.Target.SchemaVersion == target.SchemaVersion && reloadedOriginal.Target.VisibleRings.SequenceEqual(target.VisibleRings),
            "opening the saved snapshot does not reinterpret its engraving geometry");
        OpticalBurnRequest restoredRequest = CreateRequest(reloadedOriginal.Target, reloadedOriginal.CdInterleave, speed);
        Require(Produce(restoredRequest).AsSpan().SequenceEqual(expected),
            "reloading the original JSON reproduces the same CD/DVD payload despite current main-page values");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using MemoryStream cancelledOutput = new();
        try
        {
            request.ProduceContent(cancelledOutput, cancellation.Token);
            throw new InvalidOperationException("Calibration burn producer ignored cancellation.");
        }
        catch (OperationCanceledException)
        {
            Require(cancelledOutput.Length == 0, "pre-cancelled request produces no payload");
        }
    }

    private static OpticalBurnRequest CreateRequest(CalibrationTarget target, bool interleave, OpticalWriteSpeed speed)
    {
        MethodInfo factory = typeof(BurnViewModel).GetMethod("CreateCalibrationBurnRequest",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Calibration burn request factory was not found.");
        return (OpticalBurnRequest)(factory.Invoke(null, [target, interleave, "memory-only-test-device", speed, null])
            ?? throw new InvalidOperationException("Calibration burn request factory returned no request."));
    }

    private static CalibrationParameters ReadParameters(DiscParametersState state, CalibrationDiscKind kind)
        => kind switch
        {
            CalibrationDiscKind.Cd => new(kind, Number(state.CdInnerRadius), Number(state.CdOuterRadius),
                long.Parse(state.CdSectors, CultureInfo.InvariantCulture),
                CdDiscParameters.StandardLinearVelocityMmPerSecond),
            CalibrationDiscKind.BluRay => new(kind, Number(state.BdInnerRadius), Number(state.BdOuterRadius),
                long.Parse(state.BdTotalSectors, CultureInfo.InvariantCulture),
                BluRayDiscParameters.ChannelBitLengthNm,
                BluRayInvertMapping: state.BdInvertMapping,
                DrawingStartSector: 0,
                DrawingEndSectorExclusive: long.Parse(state.BdTotalSectors, CultureInfo.InvariantCulture)),
            _ => new(kind, Number(state.DvdInnerRadius), Number(state.DvdOuterRadius),
                long.Parse(state.DvdTotalSectors, CultureInfo.InvariantCulture),
                DvdStreamingOptions.StandardChannelBitLengthNm),
        };

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static byte[] GenerateDirect(CalibrationTarget target, bool interleave)
    {
        CalibrationParameters p = target.Parameters;
        using MemoryStream output = new();
        if (p.Kind == CalibrationDiscKind.Cd)
        {
            CdDiscParameters cd = new(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.LinearDensity,
                StartAngleRadians: 0, ImageOuterRadiusMm: p.OuterRadiusMm);
            CdTrackGenerator.GeneratePatternToStream(target.Sample, output, cd, interleave,
                audioByteOrder: CdAudioByteOrder.LittleEndian);
        }
        else if (p.Kind == CalibrationDiscKind.BluRay)
        {
            BluRayDiscParameters disc = CalibrationViewModel.BluRayParameters(p);
            BluRayPatternRangeMapping mapping = CalibrationViewModel.BluRayPatternMapping(target);
            BluRayStreamingGenerator.GenerateToStream(
                output,
                new BluRayStreamingOptions(disc, 0, disc.TotalSectors),
                mapping);
        }
        else
        {
            DvdStreamingOptions dvd = new(checked((uint)p.Sectors), p.InnerRadiusMm, p.OuterRadiusMm,
                p.LinearDensity, StartAngleDegrees: 0);
            DvdStreamingGenerator.GeneratePattern(target.Sample, output, dvd);
        }

        return output.ToArray();
    }

    private static byte[] Produce(OpticalBurnRequest request)
    {
        using MemoryStream backing = new();
        using (ForwardOnlyWriteStream output = new(backing))
        {
            request.ProduceContent(output, CancellationToken.None);
        }

        return backing.ToArray();
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Calibration burn workflow: {name}.");
        }
    }
}
