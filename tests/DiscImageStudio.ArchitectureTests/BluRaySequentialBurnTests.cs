using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;

internal static class BluRaySequentialBurnTests
{
    private const uint FullSectors = 12_219_392;
    private const uint SmallSectors = 1024;
    private static readonly OpticalBurnBufferOptions SmallBuffer = new(512 * 1024, 256 * 1024);
    private static readonly BluRaySequentialMediaInfo BlankMedia = new(
        Profile: 0x41, DiscIsBlank: true, FormatStatus: 1, TrackIsBlank: true,
        NextWritableAddressValid: true, TrackStartAddress: 0, NextWritableAddress: 0,
        FreeBlocks: FullSectors, TrackSize: FullSectors, MaximumBlocks: FullSectors, TrackNumber: 1);

    internal static void Run()
    {
        TestFullCapacityMetadata();
        TestRejectIncompatibleMedia();
        TestSmallUdfStream();
        TestFailuresReleaseRecorder();
        TestCancellation();
        TestMmcReadFixtures();
        TestRecordedLbaDiagnostics();
        TestMmcFinalizationAndErrors();
        TestUnavailableSenseDiagnostics();
        Console.WriteLine("blu-ray-sequential-burn: passed (12,219,392-sector metadata; 2 MiB UDF stream; no device access or full-disc ISO)");
    }

    private static void TestFullCapacityMetadata()
    {
        BluRayDiscParameters disc = new();
        BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(disc);
        bool produced = false;
        OpticalBurnRequest request = new("fake-recorder", OpticalBurnMediaKind.BluRayData,
            plan.ContentLength, (_, _) => produced = true);
        request.Validate();
        WindowsImapiBurner.ValidateBluRaySequentialMedia(request, BlankMedia);
        Require(disc.TotalSectors == FullSectors && plan.TotalSectors == FullSectors,
            "geometry and the UDF plan retain the complete unformatted single-layer capacity");
        Require(request.ContentLength == FullSectors * 2048L && plan.ContentLength == disc.ContentLength,
            "the stream declaration uses the same complete sector count with 64-bit byte arithmetic");
        BluRayTrackGeometry geometry = new(disc);
        Require(geometry.AtSector(FullSectors).RadiusMm == disc.OuterRadiusMm,
            "the full-capacity geometry reaches the outer edge at the declared stream endpoint");
        Require(geometry.AtSector(plan.DrawingStartSector).RadiusMm == plan.DrawingStartRadiusMm
            && geometry.AtSector(plan.DrawingEndSectorExclusive).RadiusMm == plan.DrawingEndRadiusMm,
            "UDF inner and tail reservations use the same complete-disc geometry");
        Require(!produced, "full-capacity checks only inspect metadata and never invoke the producer");
    }

    private static void TestRejectIncompatibleMedia()
    {
        OpticalBurnRequest request = CreateRequest(FullSectors, (_, _) =>
            throw new InvalidOperationException("A metadata check must never generate a full-disc stream."));
        (string Name, BluRaySequentialMediaInfo Media)[] rejected =
        [
            ("BD-ROM", BlankMedia with { Profile = 0x40 }),
            ("BD-R random recording", BlankMedia with { Profile = 0x42 }),
            ("BD-RE", BlankMedia with { Profile = 0x43 }),
            ("DVD", BlankMedia with { Profile = 0x11 }),
            ("written disc", BlankMedia with { DiscIsBlank = false }),
            ("formatted disc with spare areas", BlankMedia with { FormatStatus = 2 }),
            ("unknown format status", BlankMedia with { FormatStatus = 0 }),
            ("missing medium", BlankMedia with { FormatStatus = 3 }),
            ("written track", BlankMedia with { TrackIsBlank = false }),
            ("invalid next writable address", BlankMedia with { NextWritableAddressValid = false }),
            ("nonzero track origin", BlankMedia with { TrackStartAddress = 32 }),
            ("nonzero write origin", BlankMedia with { NextWritableAddress = 32 }),
            ("additional track", BlankMedia with { TrackNumber = 2 }),
            ("capacity reduced by spare areas", BlankMedia with { FreeBlocks = 11_826_176 }),
            ("one sector too short", BlankMedia with { FreeBlocks = FullSectors - 1 }),
            ("shorter track layout", BlankMedia with { TrackSize = FullSectors - 32 }),
            ("smaller physical data zone", BlankMedia with { MaximumBlocks = 11_826_176 }),
            ("dual-layer physical capacity", BlankMedia with { MaximumBlocks = FullSectors * 2 }),
        ];
        foreach ((string name, BluRaySequentialMediaInfo media) in rejected)
            Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateBluRaySequentialMedia(request, media), name);

        // A deliberately small test image is permitted on a genuine full-capacity
        // single-layer medium, without changing the disc's measured physical identity.
        WindowsImapiBurner.ValidateBluRaySequentialMedia(request with { ContentLength = SmallSectors * 2048L }, BlankMedia);
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateBluRaySequentialMedia(
            request with { ContentLength = (FullSectors + 32L) * 2048 }, BlankMedia), "stream exceeds the physical data zone");
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateBluRaySequentialMedia(
            request with { ContentLength = 33 * 2048L }, BlankMedia), "stream must fill complete 32-sector recording units");
    }

    private static void TestSmallUdfStream()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-sequential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] innerFile = [0, 255, 23, 65, 10, 13];
            File.WriteAllBytes(Path.Combine(directory, "readme.bin"), innerFile);
            BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(new(TotalSectors: SmallSectors), directory,
                "BD_NOSPARES", new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
            using MemoryStream expected = new();
            BluRayIsoGenerator.GenerateToStream(expected, plan, new SectorMapping());
            OpticalWriteSpeed requestedSpeed = new(4500, false);
            OpticalBurnRequest request = CreateRequest(SmallSectors, (output, token) =>
                BluRayIsoGenerator.GenerateToStream(output, plan, new SectorMapping(), cancellationToken: token))
                with { WriteSpeed = requestedSpeed };
            using GeneratedContentComStream stream = NewStream(request);
            stream.WaitUntilPrebuffered(CancellationToken.None);
            using FakeSession session = new();
            List<OpticalWriteSpeed> reportedSpeeds = [];
            List<string> reportedOperations = [];
            OpticalWriteSpeed actual = WindowsImapiBurner.BurnBluRay(session, stream, request,
                CancellationToken.None, reportOperation: reportedOperations.Add, reportWriteSpeed: reportedSpeeds.Add);
            Require(reportedOperations.TakeLast(5).Select(WindowsImapiBurner.GetBurnOperationStage)
                .SequenceEqual(new[] { "write-complete", "flush", "close-track", "close-disc", "release" }),
                "BD transfer completion, cache flush, track close, disc close and release have distinct stages");
            Require(session.Operations.SequenceEqual(new[]
                { "acquire", "read-media", "configure", "write", "flush", "close-track:1", "close-disc", "release" }),
                "writing is followed by flush, track close, disc close and release, without a format step");
            Require(session.RequestedSpeed == requestedSpeed && actual == session.ActualSpeed
                && reportedSpeeds.SequenceEqual(new[] { actual }), "selected and actual write speeds are propagated");
            Require(session.SectorCount == plan.TotalSectors && session.DeclaredLength == plan.ContentLength,
                "the sequential writer receives exactly the plan's declared sectors and stream length");
            byte[] written = session.Bytes.ToArray();
            Require(written.Length == SmallSectors * 2048L && written.AsSpan().SequenceEqual(expected.ToArray()),
                "the fake recorder receives every UDF and drawing byte with no inserted gap, trim or padding");
            Dictionary<string, byte[]> files = UdfFilesystemTests.Inspect(written, SmallSectors,
                "BD_NOSPARES", plan.DrawingStartSector, plan.DrawingEndSectorExclusive);
            Require(files.Count == 1 && files["readme.bin"].SequenceEqual(innerFile),
                "the sequential burn stream preserves the UDF inner file");
            foreach (uint anchor in new[] { 256U, SmallSectors - 257, SmallSectors - 1 })
                Require(BitConverter.ToUInt16(written, checked((int)anchor * 2048)) == 2
                    && BitConverter.ToUInt32(written, checked((int)anchor * 2048 + 12)) == anchor,
                    "all UDF anchors, including the last sector, reach the recorder unchanged");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestFailuresReleaseRecorder()
    {
        foreach (string failingOperation in new[] { "read-media", "configure", "write", "flush", "close-track:1", "close-disc" })
        {
            OpticalBurnRequest request = CreateTinyRequest();
            using GeneratedContentComStream stream = NewStream(request);
            stream.WaitUntilPrebuffered(CancellationToken.None);
            IOException expected = new("Injected " + failingOperation + " failure.");
            using FakeSession session = new() { FailOperation = failingOperation, Failure = expected };
            string[]? operationsAtFailure = null;
            IOException actual = Throws<IOException>(() => WindowsImapiBurner.BurnBluRay(
                session, stream, request, CancellationToken.None, reportFailure: error =>
                {
                    Require(ReferenceEquals(error, expected), "failure observer receives the original exception");
                    operationsAtFailure = session.Operations.ToArray();
                }), failingOperation + " failure");
            Require(operationsAtFailure is not null && operationsAtFailure[^1] == failingOperation,
                "failure snapshot occurs before recovery flush and release");
            string expectedOperation = failingOperation switch
            {
                "read-media" => "检查未格式化 BD-R 的原始容量",
                "configure" => "设置 BD-R 写入速度",
                "write" => $"无备用区顺序写入 BD-R：{request.ContentLength / 2048:N0} 扇区",
                "flush" => "刷新 BD-R 缓存（请等待完成）",
                "close-track:1" => "关闭 BD-R 轨道（请等待完成）",
                "close-disc" => "封闭 BD-R 盘片（请等待完成）",
                _ => throw new InvalidOperationException(),
            };
            Require(Equals(actual.Data["Burn.Operation"], expectedOperation),
                "cleanup cannot overwrite the exact failed BD operation");
            Require(ReferenceEquals(actual, expected), "the original device failure is preserved");
            Require(session.Operations[0] == "acquire" && session.Operations[^1] == "release"
                && session.Operations.Count(x => x == "release") == 1,
                "every failure after acquisition releases the recorder exactly once");
            int failureIndex = session.Operations.IndexOf(failingOperation);
            Require(session.Operations.Skip(failureIndex + 1).All(x => x is "release" or "cancel"
                || (x == "flush" && failingOperation is "write" or "flush")),
                "a failed stage only drains already accepted blocks and releases; it never closes or reformats");
        }

        OpticalBurnRequest rejectedRequest = CreateTinyRequest();
        using GeneratedContentComStream rejectedStream = NewStream(rejectedRequest);
        rejectedStream.WaitUntilPrebuffered(CancellationToken.None);
        using FakeSession formatted = new() { Media = BlankMedia with { FormatStatus = 2 } };
        Throws<InvalidOperationException>(() => WindowsImapiBurner.BurnBluRay(
            formatted, rejectedStream, rejectedRequest, CancellationToken.None), "formatted medium during burn");
        Require(formatted.Operations.SequenceEqual(new[] { "acquire", "read-media", "release" }),
            "formatted media are rejected before configuring or writing and never silently reformatted");

        using FakeSession acquisitionFailure = new() { FailOperation = "acquire" };
        Throws<IOException>(() => WindowsImapiBurner.BurnBluRay(acquisitionFailure,
            rejectedStream, rejectedRequest, CancellationToken.None), "acquisition failure");
        Require(acquisitionFailure.Operations.SequenceEqual(new[] { "acquire" }),
            "the orchestrator does not release access that was never acquired");

        using FakeSession twoFailures = new() { FailOperation = "write", ReleaseFails = true };
        IOException first = Throws<IOException>(() => WindowsImapiBurner.BurnBluRay(twoFailures,
            rejectedStream, rejectedRequest, CancellationToken.None), "write failure followed by release failure");
        Require(ReferenceEquals(first, twoFailures.Failure), "cleanup failure cannot hide the original write failure");

        using FakeSession releaseFailure = new() { ReleaseFails = true };
        IOException cleanup = Throws<IOException>(() => WindowsImapiBurner.BurnBluRay(releaseFailure,
            rejectedStream, rejectedRequest, CancellationToken.None), "release failure after a completed write");
        Require(ReferenceEquals(cleanup, releaseFailure.ReleaseFailure), "a release error after success is still reported");
        Require(Equals(cleanup.Data["Burn.Operation"], "释放 BD-R 刻录机访问权"),
            "a release failure is not mislabeled as sealing the disc");
        using FakeSession brokenObserver = new();
        using GeneratedContentComStream observerStream = NewStream(rejectedRequest);
        observerStream.WaitUntilPrebuffered(CancellationToken.None);
        WindowsImapiBurner.BurnBluRay(brokenObserver, observerStream, rejectedRequest, CancellationToken.None,
            reportOperation: _ => throw new InvalidOperationException("broken progress observer"));
        Require(brokenObserver.Operations[^1] == "release", "a diagnostic observer cannot prevent device release");
    }

    private static void TestRecordedLbaDiagnostics()
    {
        foreach (bool valid in new[] { false, true })
        {
            byte[][] fixtures = MediaFixtures();
            fixtures[3][7] = valid ? (byte)3 : (byte)1;
            BinaryPrimitives.WriteUInt32BigEndian(fixtures[3].AsSpan(28, 4), 123);
            FakeMmcTransport transport = MediaTransport(fixtures);
            List<(int Start, int Last)> snapshots = [];
            new BluRayMmcCommands(transport, (start, last) => snapshots.Add((start, last))).ReadMedia();
            Require(snapshots.Count == (valid ? 1 : 0) && (!valid || snapshots[0] == (0, 123)),
                "last recorded LBA is consumed only when the device marks LRA valid");
            Require(transport.Remaining == 0 && transport.Commands.Count == 4,
                "recorded-LBA telemetry reuses existing responses without additional device I/O");
        }
        byte[][] throwingFixture = MediaFixtures();
        throwingFixture[3][7] |= 2;
        new BluRayMmcCommands(MediaTransport(throwingFixture), (_, _) => throw new IOException("observer"))
            .ReadMedia();
    }

    private static void TestCancellation()
    {
        OpticalBurnRequest request = CreateTinyRequest();
        using GeneratedContentComStream stream = NewStream(request);
        stream.WaitUntilPrebuffered(CancellationToken.None);
        using CancellationTokenSource preCancelled = new();
        preCancelled.Cancel();
        using FakeSession untouched = new();
        Throws<OperationCanceledException>(() => WindowsImapiBurner.BurnBluRay(
            untouched, stream, request, preCancelled.Token), "pre-cancelled burn");
        Require(untouched.Operations.Count == 0, "pre-cancellation performs no device operations");

        using CancellationTokenSource cancel = new();
        using FakeSession interrupted = new()
        {
            DuringWrite = () => { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); },
        };
        Throws<OperationCanceledException>(() => WindowsImapiBurner.BurnBluRay(
            interrupted, stream, request, cancel.Token), "cancellation during write");
        Require(interrupted.Operations.Contains("cancel") && interrupted.Operations[^1] == "release",
            "mid-write cancellation reaches the write engine and releases the recorder");
        Require(interrupted.Operations.Count(x => x == "flush") == 1,
            "cancelled writes attempt to drain already accepted blocks before releasing the recorder");
        Require(!interrupted.Operations.Any(x => x == "close-disc" || x.StartsWith("close-track:", StringComparison.Ordinal)),
            "cancelled incomplete streams are not closed as successful burns");
    }

    private static OpticalBurnRequest CreateTinyRequest()
        => CreateRequest(32, (output, token) => { token.ThrowIfCancellationRequested(); output.Write(new byte[32 * 2048]); });

    private static OpticalBurnRequest CreateRequest(uint sectors, Action<Stream, CancellationToken> producer)
        => new("fake-recorder", OpticalBurnMediaKind.BluRayData, sectors * 2048L, producer, BufferOptions: SmallBuffer);

    private static GeneratedContentComStream NewStream(OpticalBurnRequest request)
        => new(request.ContentLength, request.ProduceContent, null, CancellationToken.None, SmallBuffer);

    private static void TestMmcReadFixtures()
    {
        FakeMmcTransport transport = MediaTransport();
        BluRaySequentialMediaInfo actual = new BluRayMmcCommands(transport).ReadMedia();
        Require(actual == BlankMedia, "MMC parses the unformatted raw track capacity instead of a smaller default formatting proposal");
        Require(transport.Remaining == 0 && transport.Commands.All(cdb => cdb[0] != 0x04),
            "capacity inspection consumes only read commands and never FORMAT UNIT");

        foreach (int response in Enumerable.Range(0, 4))
        {
            byte[][] fixtures = MediaFixtures();
            fixtures[response] = fixtures[response][..(response == 0 ? 7 : response == 1 ? 11 : response == 2 ? 11 : 35)];
            Throws<IOException>(() => new BluRayMmcCommands(MediaTransport(fixtures)).ReadMedia(),
                "truncated MMC response " + response);
        }
        byte[][] invalid = MediaFixtures();
        invalid[0][3] = 3;
        Throws<IOException>(() => new BluRayMmcCommands(MediaTransport(invalid)).ReadMedia(),
            "configuration header cannot omit the current profile");
        invalid = MediaFixtures();
        invalid[2][3] = 15;
        Throws<IOException>(() => new BluRayMmcCommands(MediaTransport(invalid)).ReadMedia(),
            "incomplete format descriptor list");
        invalid = MediaFixtures();
        invalid[2][3] = 24;
        Throws<IOException>(() => new BluRayMmcCommands(MediaTransport(invalid)).ReadMedia(),
            "format descriptor length beyond returned data");
        invalid = MediaFixtures();
        invalid[3][2] = 2;
        Throws<IOException>(() => new BluRayMmcCommands(MediaTransport(invalid)).ReadMedia(),
            "read track response identifies a different track");
        invalid = MediaFixtures();
        invalid[3][5] |= 0x20;
        Throws<IOException>(() => new BluRayMmcCommands(MediaTransport(invalid)).ReadMedia(),
            "damaged track flag");
        invalid = MediaFixtures();
        invalid[3][0] = 0;
        invalid[3][1] = 2;
        Throws<IOException>(() => new BluRayMmcCommands(MediaTransport(invalid)).ReadMedia(),
            "track header declares fewer bytes than required");

        byte[][] formatted = MediaFixtures();
        formatted[2][8] = 2;
        BluRaySequentialMediaInfo formattedMedia = new BluRayMmcCommands(MediaTransport(formatted)).ReadMedia();
        Require(formattedMedia.FormatStatus == 2, "the raw format descriptor status is preserved for validation");
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateBluRaySequentialMedia(
            CreateTinyRequest(), formattedMedia), "formatted response cannot pass the no-spares media validator");
    }

    private static void TestMmcFinalizationAndErrors()
    {
        FakeMmcTransport transport = new();
        transport.Expect([0x1e, 0, 0, 0, 1, 0], 0, 30);
        transport.Expect([0x35, 2, 0, 0, 0, 0, 0, 0, 0, 0], 0, 120);
        transport.Expect([0, 0, 0, 0, 0, 0], 0, 30);
        ExpectDiscStatus(transport, 1);
        ExpectTrackStatus(transport, 0x1234, closed: false);
        transport.Expect([0x5b, 1, 1, 0, 0x12, 0x34, 0, 0, 0, 0], 0, 120);
        transport.Expect([0, 0, 0, 0, 0, 0], 0, 30);
        ExpectDiscStatus(transport, 1);
        ExpectTrackStatus(transport, 0x1234, closed: true);
        ExpectDiscStatus(transport, 1);
        transport.Expect([0x5b, 1, 6, 0, 0, 0, 0, 0, 0, 0], 0, 120);
        transport.Expect([0, 0, 0, 0, 0, 0], 0, 30);
        ExpectDiscStatus(transport, 2);
        transport.Expect([0x1e, 0, 0, 0, 0, 0], 0, 30);
        BluRayMmcCommands commands = new(transport);
        commands.PreventRemoval(true);
        commands.SynchronizeCache();
        commands.CloseTrack(0x1234);
        commands.CloseDisc();
        commands.PreventRemoval(false);
        Require(transport.Remaining == 0, "flush and BD-R disc finalization issue the expected immediate commands followed by readiness checks");
        Require(transport.Commands.All(cdb => cdb[0] != 0x04), "finalization never introduces a format command");
        Throws<ArgumentOutOfRangeException>(() => commands.CloseTrack(0), "zero track number");
        Throws<ArgumentOutOfRangeException>(() => commands.CloseTrack(ushort.MaxValue + 1), "overflowing track number");

        FakeMmcTransport alreadyFinalized = new();
        ExpectDiscStatus(alreadyFinalized, 2);
        ExpectDiscStatus(alreadyFinalized, 2);
        BluRayMmcCommands fullDisc = new(alreadyFinalized);
        fullDisc.CloseTrack(1);
        fullDisc.CloseDisc();
        Require(alreadyFinalized.Remaining == 0 && alreadyFinalized.Commands.All(cdb => cdb[0] == 0x51),
            "a full disc automatically finalized by the writer is recognized without redundant CLOSE commands");

        FakeMmcTransport closedTrack = new();
        ExpectDiscStatus(closedTrack, 1);
        ExpectTrackStatus(closedTrack, 1, closed: true);
        new BluRayMmcCommands(closedTrack).CloseTrack(1);
        Require(closedTrack.Remaining == 0 && closedTrack.Commands.All(cdb => cdb[0] is 0x51 or 0x52),
            "a full track that closed automatically does not receive a redundant track CLOSE");

        TestMmcCloseFailureConfirmation();
        TestMmcBusyFinalizationConfirmation();

        foreach (bool descriptorFormat in new[] { false, true })
        {
            byte[] sense = descriptorFormat ? [0x72, 5, 0x24, 0] : FixedSense(5, 0x24, 0);
            FakeMmcTransport failed = new();
            failed.Expect([0x1e, 0, 0, 0, 1, 0], 0, 30,
                new(BluRayMmcCommands.CommandHasSenseData, sense, []));
            MmcCommandException error = Throws<MmcCommandException>(
                () => new BluRayMmcCommands(failed).PreventRemoval(true), "positive HRESULT carrying sense data");
            Require(error.HResult == 0x00AA0200 && error.SenseKey == 5
                && error.AdditionalSenseCode == 0x24 && error.AdditionalSenseQualifier == 0,
                "fixed and descriptor sense preserve the command failure despite its nonnegative HRESULT");
        }

        foreach (byte qualifier in new byte[] { 1, 7, 8 })
        {
            FakeMmcTransport busy = new();
            busy.Expect([0, 0, 0, 0, 0, 0], 0, 30,
                new(BluRayMmcCommands.CommandHasSenseData, FixedSense(2, 4, qualifier), []));
            busy.Expect([0, 0, 0, 0, 0, 0], 0, 30);
            new BluRayMmcCommands(busy).WaitUntilReady(TimeSpan.FromSeconds(1));
            Require(busy.Remaining == 0 && busy.Commands.Count == 2,
                "readiness retries becoming ready, operation in progress and long write in progress");
        }

        foreach ((byte asc, byte qualifier) in new (byte, byte)[] { (4, 2), (4, 3), (0x3a, 0) })
        {
            FakeMmcTransport failed = new();
            failed.Expect([0, 0, 0, 0, 0, 0], 0, 30,
                new(BluRayMmcCommands.CommandHasSenseData, FixedSense(2, asc, qualifier), []));
            Throws<MmcCommandException>(() => new BluRayMmcCommands(failed).WaitUntilReady(TimeSpan.FromSeconds(1)),
                "initialization required, manual intervention and missing media are terminal failures");
            Require(failed.Commands.Count == 1, "non-progress sense is reported without retry");
        }
    }

    private static void TestUnavailableSenseDiagnostics()
    {
        foreach (byte[] sense in new[] { Array.Empty<byte>(), new byte[18], new byte[] { 0x70, 0, 2 } })
        {
            MmcCommandException error = new(0x2a, new(unchecked((int)0xc0aa020d), sense, []));
            Require(!error.HasValidSense && !error.IsOperationInProgress
                && error.Message.Contains("未返回有效", StringComparison.Ordinal)
                && !error.Message.Contains("FFFFFFFF", StringComparison.Ordinal),
                "missing, zero-filled and truncated Sense is unknown, not a fabricated device error code");
        }
        MmcCommandException disconnected = new(0x1e,
            new(unchecked((int)0x8007048f), new byte[18], []));
        Require(disconnected.Message.Contains("Windows 报告设备未连接", StringComparison.Ordinal)
            && disconnected.HResult == unchecked((int)0x8007048f),
            "device-not-connected reports the Windows connection failure while retaining HRESULT");
        MmcCommandException valid = new(0x2a, new(BluRayMmcCommands.CommandHasSenseData,
            FixedSense(2, 4, 8), []));
        Require(valid.HasValidSense && valid.IsOperationInProgress
            && valid.Message.Contains("Sense 02/04/08", StringComparison.Ordinal),
            "valid returned Sense retains its device-specific diagnostic meaning");
    }

    private static byte[] FixedSense(byte key, byte asc, byte ascq)
    {
        byte[] sense = new byte[18];
        sense[0] = 0x70;
        sense[2] = key;
        sense[7] = 10;
        sense[12] = asc;
        sense[13] = ascq;
        return sense;
    }

    private static void ExpectDiscStatus(FakeMmcTransport transport, byte status)
    {
        byte[] info = new byte[64];
        info[1] = 62;
        info[2] = status;
        info[6] = 1;
        transport.Expect([0x51, 0, 0, 0, 0, 0, 0, 0, 64, 0], 64, 30, new(0, [], info));
    }

    private static void ExpectTrackStatus(FakeMmcTransport transport, int trackNumber, bool closed)
    {
        byte[] cdb = [0x52, 1, 0, 0, 0, 0, 0, 0, 48, 0];
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), (uint)trackNumber);
        byte[] info = new byte[48];
        info[1] = 46;
        info[2] = (byte)trackNumber;
        info[32] = (byte)(trackNumber >> 8);
        info[7] = closed ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(16, 4), closed ? 0U : 32U);
        transport.Expect(cdb, 48, 30, new(0, [], info));
    }

    private static void TestMmcCloseFailureConfirmation()
    {
        MmcCommandResult failedClose = new(BluRayMmcCommands.CommandHasSenseData, FixedSense(5, 0x24, 0), []);
        foreach (bool isClosed in new[] { false, true })
        {
            FakeMmcTransport track = new();
            ExpectDiscStatus(track, 1);
            ExpectTrackStatus(track, 1, closed: false);
            track.Expect([0x5b, 1, 1, 0, 0, 1, 0, 0, 0, 0], 0, 120, failedClose);
            ExpectDiscStatus(track, 1);
            ExpectTrackStatus(track, 1, closed: isClosed);
            if (isClosed) new BluRayMmcCommands(track).CloseTrack(1);
            else Throws<MmcCommandException>(() => new BluRayMmcCommands(track).CloseTrack(1),
                "a failed track CLOSE remains an error unless read-back confirms a closed track");
            Require(track.Remaining == 0, "track CLOSE failure verifies the actual resulting state");

            FakeMmcTransport disc = new();
            ExpectDiscStatus(disc, 1);
            disc.Expect([0x5b, 1, 6, 0, 0, 0, 0, 0, 0, 0], 0, 120, failedClose);
            ExpectDiscStatus(disc, isClosed ? (byte)2 : (byte)1);
            if (isClosed) new BluRayMmcCommands(disc).CloseDisc();
            else Throws<MmcCommandException>(() => new BluRayMmcCommands(disc).CloseDisc(),
                "a failed disc CLOSE remains an error unless read-back confirms finalization");
            Require(disc.Remaining == 0, "disc CLOSE failure verifies the actual resulting state");
        }

        FakeMmcTransport delayed = new();
        ExpectDiscStatus(delayed, 1);
        delayed.Expect([0x5b, 1, 6, 0, 0, 0, 0, 0, 0, 0], 0, 120);
        delayed.Expect([0, 0, 0, 0, 0, 0], 0, 30);
        ExpectDiscStatus(delayed, 1);
        delayed.Expect([0, 0, 0, 0, 0, 0], 0, 30);
        ExpectDiscStatus(delayed, 2);
        new BluRayMmcCommands(delayed).CloseDisc();
        Require(delayed.Remaining == 0, "GOOD readiness alone is insufficient until disc status confirms finalization");

        FakeMmcTransport invalidFinalState = new();
        ExpectDiscStatus(invalidFinalState, 1);
        invalidFinalState.Expect([0x5b, 1, 6, 0, 0, 0, 0, 0, 0, 0], 0, 120);
        invalidFinalState.Expect([0, 0, 0, 0, 0, 0], 0, 30);
        ExpectDiscStatus(invalidFinalState, 0);
        Throws<IOException>(() => new BluRayMmcCommands(invalidFinalState).CloseDisc(),
            "blank state after successful CLOSE is not reported as a completed burn");
    }

    private static void TestMmcBusyFinalizationConfirmation()
    {
        foreach (byte qualifier in new byte[] { 7, 8 })
        {
            MmcCommandResult busy = new(BluRayMmcCommands.CommandHasSenseData, FixedSense(2, 4, qualifier), []);
            FakeMmcTransport disc = new();
            ExpectDiscStatus(disc, 1);
            disc.Expect([0x5b, 1, 6, 0, 0, 0, 0, 0, 0, 0], 0, 120);
            disc.Expect([0, 0, 0, 0, 0, 0], 0, 30);
            disc.Expect([0x51, 0, 0, 0, 0, 0, 0, 0, 64, 0], 64, 30, busy);
            disc.Expect([0, 0, 0, 0, 0, 0], 0, 30);
            ExpectDiscStatus(disc, 2);
            new BluRayMmcCommands(disc).CloseDisc();
            Require(disc.Remaining == 0 && disc.Commands.Count(cdb => cdb[0] == 0x5b) == 1,
                "disc confirmation retries busy READ DISC INFORMATION after GOOD readiness without issuing CLOSE twice");

            foreach (bool busyTrackRead in new[] { false, true })
            {
                FakeMmcTransport track = new();
                ExpectDiscStatus(track, 1);
                ExpectTrackStatus(track, 1, closed: false);
                track.Expect([0x5b, 1, 1, 0, 0, 1, 0, 0, 0, 0], 0, 120);
                track.Expect([0, 0, 0, 0, 0, 0], 0, 30);
                if (busyTrackRead)
                {
                    ExpectDiscStatus(track, 1);
                    track.Expect([0x52, 1, 0, 0, 0, 1, 0, 0, 48, 0], 48, 30, busy);
                }
                else
                {
                    track.Expect([0x51, 0, 0, 0, 0, 0, 0, 0, 64, 0], 64, 30, busy);
                }
                track.Expect([0, 0, 0, 0, 0, 0], 0, 30);
                ExpectDiscStatus(track, 1);
                ExpectTrackStatus(track, 1, closed: true);
                new BluRayMmcCommands(track).CloseTrack(1);
                Require(track.Remaining == 0 && track.Commands.Count(cdb => cdb[0] == 0x5b) == 1,
                    "track confirmation retries busy disc or track information after GOOD readiness until closure is verified");
            }
        }

        FakeMmcTransport delayedTrack = new();
        ExpectDiscStatus(delayedTrack, 1);
        ExpectTrackStatus(delayedTrack, 1, closed: false);
        delayedTrack.Expect([0x5b, 1, 1, 0, 0, 1, 0, 0, 0, 0], 0, 120);
        delayedTrack.Expect([0, 0, 0, 0, 0, 0], 0, 30);
        ExpectDiscStatus(delayedTrack, 1);
        ExpectTrackStatus(delayedTrack, 1, closed: false);
        delayedTrack.Expect([0, 0, 0, 0, 0, 0], 0, 30);
        ExpectDiscStatus(delayedTrack, 1);
        ExpectTrackStatus(delayedTrack, 1, closed: true);
        new BluRayMmcCommands(delayedTrack).CloseTrack(1);
        Require(delayedTrack.Remaining == 0 && delayedTrack.Commands.Count(cdb => cdb[0] == 0x5b) == 1,
            "track CLOSE waits beyond GOOD readiness when the track still reports an open state");

        FakeMmcTransport unrecoverable = new();
        ExpectDiscStatus(unrecoverable, 1);
        unrecoverable.Expect([0x5b, 1, 6, 0, 0, 0, 0, 0, 0, 0], 0, 120);
        unrecoverable.Expect([0, 0, 0, 0, 0, 0], 0, 30);
        unrecoverable.Expect([0x51, 0, 0, 0, 0, 0, 0, 0, 64, 0], 64, 30,
            new(BluRayMmcCommands.CommandHasSenseData, FixedSense(5, 0x24, 0), []));
        Throws<MmcCommandException>(() => new BluRayMmcCommands(unrecoverable).CloseDisc(),
            "an unrecoverable confirmation-read failure is not retried or reported as success");
        Require(unrecoverable.Remaining == 0, "unrecoverable confirmation failure stops immediately");
    }

    private static byte[][] MediaFixtures()
    {
        byte[] configuration = [0, 0, 0, 4, 0, 0, 0, 0x41];
        byte[] disc = new byte[64];
        disc[1] = 62;
        disc[6] = 1;
        byte[] capacities = new byte[20];
        capacities[3] = 16;
        BinaryPrimitives.WriteUInt32BigEndian(capacities.AsSpan(4, 4), FullSectors);
        capacities[8] = 1; // Current maximum: not formatted.
        BinaryPrimitives.WriteUInt32BigEndian(capacities.AsSpan(12, 4), 11_826_176);
        capacities[16] = 0x32 << 2; // A smaller formatting proposal is deliberately present.
        byte[] track = new byte[48];
        track[1] = 46;
        track[2] = 1;
        track[6] = 0x40;
        track[7] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(16, 4), FullSectors);
        BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(24, 4), FullSectors);
        return [configuration, disc, capacities, track];
    }

    private static FakeMmcTransport MediaTransport(byte[][]? fixtures = null)
    {
        fixtures ??= MediaFixtures();
        FakeMmcTransport transport = new();
        transport.Expect([0x46, 0, 0, 0, 0, 0, 0, 0, 8, 0], 8, 30, new(0, [], fixtures[0]));
        transport.Expect([0x51, 0, 0, 0, 0, 0, 0, 0, 64, 0], 64, 30, new(0, [], fixtures[1]));
        transport.Expect([0x23, 0, 0, 0, 0, 0, 0, 1, 0, 0], 256, 30, new(0, [], fixtures[2]));
        transport.Expect([0x52, 1, 0, 0, 0, 1, 0, 0, 48, 0], 48, 30, new(0, [], fixtures[3]));
        return transport;
    }

    private sealed class FakeMmcTransport : IMmcCommandTransport
    {
        private readonly Queue<(byte[] Cdb, int Allocation, uint Timeout, MmcCommandResult Result)> _expectations = new();
        internal List<byte[]> Commands { get; } = [];
        internal int Remaining => _expectations.Count;
        internal void Expect(byte[] cdb, int allocation, uint timeout, MmcCommandResult? result = null)
            => _expectations.Enqueue((cdb, allocation, timeout, result ?? new(0, [], [])));
        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            Require(_expectations.Count > 0, "no unexpected command or retry may reach the transport");
            var expected = _expectations.Dequeue();
            Require(cdb.AsSpan().SequenceEqual(expected.Cdb) && allocationLength == expected.Allocation
                && timeoutSeconds == expected.Timeout, "MMC CDB, allocation and timeout match the independent fixture");
            Commands.Add(cdb.ToArray());
            return expected.Result;
        }
    }

    private sealed class SectorMapping : IBluRayPayloadMapping
    {
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
            => destination.Fill((byte)(context.Sector * 17 + 3));
    }

    private sealed class FakeSession : IBluRaySequentialSession
    {
        internal BluRaySequentialMediaInfo Media { get; init; } = BlankMedia;
        internal List<string> Operations { get; } = [];
        internal MemoryStream Bytes { get; } = new();
        internal OpticalWriteSpeed ActualSpeed { get; } = new(4394, false);
        internal OpticalWriteSpeed? RequestedSpeed { get; private set; }
        internal uint SectorCount { get; private set; }
        internal long DeclaredLength { get; private set; }
        internal string? FailOperation { get; init; }
        internal IOException Failure { get; init; } = new("Injected device failure.");
        internal bool ReleaseFails { get; init; }
        internal IOException ReleaseFailure { get; } = new("Injected release failure.");
        internal Action? DuringWrite { get; init; }

        public void Acquire() => Step("acquire");
        public BluRaySequentialMediaInfo ReadMedia() { Step("read-media"); return Media; }
        public OpticalWriteSpeed ConfigureWrite(OpticalWriteSpeed? speed)
        {
            Step("configure");
            RequestedSpeed = speed;
            return ActualSpeed;
        }

        public void Write(IStream content, uint sectorCount)
        {
            Step("write");
            DuringWrite?.Invoke();
            // This hard limit makes accidental full-disc test generation fail before
            // a read or allocation. Full capacity is tested only as metadata above.
            Require(sectorCount <= SmallSectors, "test writer refuses more than 2 MiB");
            SectorCount = sectorCount;
            content.Stat(out STATSTG info, 1);
            DeclaredLength = info.cbSize;
            Require(info.cbSize == sectorCount * 2048L, "declared stream length matches sectors");
            byte[] buffer = new byte[64 * 1024];
            IntPtr count = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                long remaining = info.cbSize;
                while (remaining > 0)
                {
                    int wanted = (int)Math.Min(buffer.Length, remaining);
                    Marshal.WriteInt32(count, -1);
                    content.Read(buffer, wanted, count);
                    int read = Marshal.ReadInt32(count);
                    Require(read > 0 && read <= wanted, "stream supplies bounded, nonempty chunks until its declared endpoint");
                    Bytes.Write(buffer, 0, read);
                    remaining -= read;
                }
                content.Read(buffer, 1, count);
                Require(Marshal.ReadInt32(count) == 0, "stream ends exactly after the requested sectors");
            }
            finally { Marshal.FreeHGlobal(count); }
        }

        public void CancelWrite() => Step("cancel");
        public void Flush() => Step("flush");
        public void CloseTrack(int trackNumber) => Step("close-track:" + trackNumber);
        public void CloseDisc() => Step("close-disc");
        public void Release()
        {
            Step("release");
            if (ReleaseFails) throw ReleaseFailure;
        }
        public void Dispose() => Bytes.Dispose();

        private void Step(string operation)
        {
            Operations.Add(operation);
            if (operation == FailOperation) throw Failure;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blu-ray sequential burn: " + message);
    }

    private static T Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Blu-ray sequential burn: {name} did not throw {typeof(T).Name}.");
    }
}
