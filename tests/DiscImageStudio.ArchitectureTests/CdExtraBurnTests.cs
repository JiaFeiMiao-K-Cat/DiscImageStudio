using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

/// <summary>CD-Extra recording without hardware: Track-at-Once write parameters, WRITE(10)
/// streams with the not-ready retry, session closing, the two-session orchestration with a
/// scripted recorder, and the burn request that regenerates the picture for the measured
/// session address.</summary>
internal static class CdExtraBurnTests
{
    internal static void Run()
    {
        TestSharedConstants();
        TestWriteParametersPage();
        TestTaoWriter();
        TestCommands();
        TestProgress();
        TestOrchestration();
        TestRecorderSequence();
        string directory = Path.Combine(Path.GetTempPath(), "cd-extra-burn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { TestBurnRequest(directory); }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("cd-extra-burn: passed (page 05h fields, TAO WRITE(10) addresses/data/retry, busy retries and CLOSE fallback, progress, two-session order, measured session 2 on a command-level CD-R model, music pinning, failure/cancel/test-write policy; no hardware)");
    }

    /// <summary>Burning has no project references, so it repeats the CD constants the Cd
    /// module plans with (and Core the track limit it validates); they must not drift.</summary>
    private static void TestSharedConstants()
    {
        Require(CdExtraBurnRequest.MaximumAudioTracks == CdExtraLayout.MaximumAudioTracks
                && ProjectCdExtraSettings.MaximumTracks == CdExtraLayout.MaximumAudioTracks
                && CdExtraAudioTrack.MinimumSectors == CdExtraLayout.MinimumTrackSectors
                && CdExtraBurnRequest.RunOutSectors == CdExtraLayout.RunOutSectors
                && CdExtraBurnRequest.DataPostgapSectors == CdExtraLayout.DataPostgapSectors
                && CdExtraLayout.MinimumDataTrackSectors
                    == CdExtraAudioTrack.MinimumSectors + CdExtraBurnRequest.DataPostgapSectors
                && CdExtraAudioTrack.SectorBytes == CdDiscParameters.BytesPerSector
                && CdTaoWriter.AudioBlockBytes == CdExtraAudioTrack.SectorBytes
                && CdTaoWriter.DataBlockBytes == CdExtraDataContent.SectorBytes
                && new CdWriteParameters(1, 3, 0, 0, 0, false, false).AudioPauseLength == CdExtraLayout.TrackPauseSectors,
            "Core, Cd and Burning agree on the CD-Extra track limit, sector sizes, gaps and run-out");
        Require(typeof(ICdExtraBurner).IsAssignableFrom(typeof(WindowsImapiBurner))
                && typeof(IOpticalDiscBurner).GetMethod(nameof(ICdExtraBurner.BurnCdExtraAsync)) is null,
            "CD-Extra recording is a capability of its own, not a member every burner must carry");
    }

    private static void TestWriteParametersPage()
    {
        byte[] sensed = new byte[52];
        sensed[0] = 0x85; // PS bit + page 05h
        sensed[1] = 0x32;
        sensed[2] = 0x22; // LS_V set, packet write type
        sensed[3] = 0x2F;
        sensed[5] = 7;
        sensed[16] = 0x80; // valid MCN must not be rewritten
        sensed[33] = 0x80;
        byte[] audio = CdWriteParameters.AudioSession(bufferUnderrunFree: true, testWrite: false).BuildModeSelectData(sensed);
        Require(audio.Length == 60 && audio.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0, "zeroed MODE SELECT(10) header");
        ReadOnlySpan<byte> page = audio.AsSpan(8);
        Require(page[0] == 0x05 && page[1] == 0x32, "page code without PS and the drive's page length");
        Require(page[2] == 0x41 && page[3] == 0xC0 && page[4] == 0x00 && page[5] == 0 && page[8] == 0x00,
            "audio: BUFE, Track-at-Once, multi-session 11b, track mode 0, raw 2352-byte blocks, CD-DA session");
        Require(page[14] == 0x00 && page[15] == 150 && page.Slice(16, 36).IndexOfAnyExcept((byte)0) < 0,
            "150-block audio pause; MCN, ISRC and sub-header cleared");
        byte[] data = CdWriteParameters.FinalDataSession(bufferUnderrunFree: false, testWrite: true).BuildModeSelectData(sensed);
        Require(data[8 + 2] == 0x11 && data[8 + 3] == 0x04 && data[8 + 4] == 0x08,
            "data: Test Write, Track-at-Once, no next session, data track mode, Mode 1");

        byte[] vendor = new byte[56];
        vendor[0] = 0x05;
        vendor[1] = 0x36;
        vendor[53] = 0x5A;
        byte[] longer = CdWriteParameters.AudioSession(false, false).BuildModeSelectData(vendor);
        Require(longer.Length == 64 && longer[8 + 53] == 0x5A, "a 56-byte page keeps its vendor bytes");
        Throws<IOException>(() => CdWriteParameters.AudioSession(false, false).BuildModeSelectData(new byte[52]),
            "a page other than 05h is refused");
    }

    private static void TestTaoWriter()
    {
        byte[] audio = Enumerable.Range(0, 100 * 2352).Select(index => (byte)(index * 13 + 7)).ToArray();
        WriteTransport transport = new();
        List<long> accepted = [];
        CdTaoWriter.Write(transport, new MemoryContent(audio), 11_702, 100, 2352, accepted.Add, sleep: _ => { });
        Require(transport.Writes.Select(write => Lba(write.Cdb)).SequenceEqual(new long[] { 11_702, 11_729, 11_756, 11_783 })
                && transport.Writes.Select(write => Blocks(write.Cdb)).SequenceEqual(new[] { 27, 27, 27, 19 }),
            "27 audio blocks per WRITE(10) from the next writable address, short final command");
        Require(transport.Writes.SelectMany(write => write.Data).SequenceEqual(audio) && accepted[^1] == audio.Length
                && transport.Writes.All(write => write.Cdb[0] == 0x2a && write.Timeout == CdTaoWriter.WriteTimeoutSeconds),
            "the stream reaches the recorder unchanged");

        byte[] data = Enumerable.Range(0, 70 * 2048).Select(index => (byte)(index * 5)).ToArray();
        WriteTransport dataTransport = new();
        CdTaoWriter.Write(dataTransport, new MemoryContent(data), 34_506, 70, 2048, sleep: _ => { });
        Require(dataTransport.Writes.Select(write => Blocks(write.Cdb)).SequenceEqual(new[] { 32, 32, 6 })
                && dataTransport.Writes.SelectMany(write => write.Data).SequenceEqual(data),
            "Mode 1 user data uses 32-block commands");

        WriteTransport busy = new() { Script = { [1] = [NotReady(), Good()] } };
        CdTaoWriter.Write(busy, new MemoryContent(audio), 0, 100, 2352, sleep: _ => { });
        Require(busy.Writes.Count == 5 && Lba(busy.Writes[1].Cdb) == 27 && Lba(busy.Writes[2].Cdb) == 27
                && busy.Writes.Skip(2).SelectMany(write => write.Data).SequenceEqual(audio.Skip(27 * 2352)),
            "a WRITE answered with 02/04/08 was not executed and is repeated once ready");

        WriteTransport failing = new() { Script = { [1] = [Sense(3, 0x0C, 0)] } };
        CdTaoWriteException? failure = null;
        try { CdTaoWriter.Write(failing, new MemoryContent(audio), 0, 100, 2352, sleep: _ => { }); }
        catch (CdTaoWriteException exception) { failure = exception; }
        Require(failure is { Lba: 27, Blocks: 27 } && failing.Writes.Count == 2, "a medium error stops the track without a retry");

        Throws<ArgumentException>(() => CdTaoWriter.Write(new WriteTransport(), new MemoryContent(audio), 0, 99, 2352),
            "stream length must match the planned blocks");
    }

    private static void TestCommands()
    {
        ScriptedTransport transport = new();
        transport.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 0, lastTrack: 3));
        transport.TrackInfo = TrackInfo(track: 3, nwa: 34_506, free: 325_341);
        CdMmcCommands commands = new(transport, _ => { });
        CdTrackInformation track = commands.ReadNextWritableTrack();
        Require(track is { TrackNumber: 3, NextWritableAddressValid: true, Blank: true, NextWritableAddress: 34_506, FreeBlocks: 325_341 },
            "next writable track from READ DISC INFORMATION and READ TRACK INFORMATION");
        Require(transport.Commands[^1][0] == 0x52 && transport.Commands[^1][1] == 1
                && BinaryPrimitives.ReadUInt32BigEndian(transport.Commands[^1].AsSpan(2)) == 3, "track-number address type");

        transport.Commands.Clear();
        transport.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 1, lastTrack: 3));
        transport.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 0, lastTrack: 4));
        commands.CloseSession(expectFinalized: false);
        Require(transport.Commands[0].SequenceEqual(new byte[] { 0x5b, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0 }),
            "CLOSE TRACK SESSION function 010b with Immed");
        Require(transport.Commands.Count(cdb => cdb[0] == 0x5b) == 1, "the session is closed once");

        transport.DiscInfo.Enqueue(DiscInfo(status: 2, lastSession: 3, lastTrack: 4));
        commands.CloseSession(expectFinalized: true);
        transport.DiscInfo.Enqueue(DiscInfo(status: 2, lastSession: 3, lastTrack: 4));
        Throws<IOException>(() => commands.CloseSession(expectFinalized: false),
            "closing the music session must not finalize the disc");

        // 02/04/08 after a flush: the command was not executed and is sent again once ready.
        ScriptedTransport busy = new() { Busy = { [0x51] = 1 } };
        busy.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 1, lastTrack: 3));
        busy.TrackInfo = TrackInfo(track: 3, nwa: 34_506, free: 325_341);
        CdTrackInformation afterBusy = new CdMmcCommands(busy, _ => { }).ReadNextWritableTrack();
        Require(afterBusy.NextWritableAddress == 34_506 && busy.Commands.Count(cdb => cdb[0] == 0x51) == 2
                && busy.Commands.Any(cdb => cdb[0] == 0x00),
            "a busy READ DISC INFORMATION is repeated after TEST UNIT READY");
        ScriptedTransport select = new() { Busy = { [0x55] = 2 } };
        new CdMmcCommands(select, _ => { }).ModeSelectWriteParameters(new byte[60]);
        Require(select.Commands.Count(cdb => cdb[0] == 0x55) == 3, "a busy MODE SELECT is sent again");
        ScriptedTransport syncBusy = new() { Busy = { [0x35] = 1 } };
        new CdMmcCommands(syncBusy, _ => { }).SynchronizeCache();
        Require(syncBusy.Commands.Count(cdb => cdb[0] == 0x35) == 2, "a busy SYNCHRONIZE CACHE is sent again");

        // 06/28/00 once after a close (the table of contents changed): not a failure.
        ScriptedTransport attention = new() { Fail = { [0x00] = Sense(6, 0x28, 0) } };
        attention.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 0, lastTrack: 4));
        new CdMmcCommands(attention, _ => { }).CloseSession(expectFinalized: false);
        Require(attention.Commands.Count(cdb => cdb[0] == 0x5b) == 1 && attention.Commands.Count(cdb => cdb[0] == 0x00) >= 2,
            "a unit attention after CLOSE SESSION is polled past without closing again");
        ScriptedTransport attentionOnQuery = new() { Fail = { [0x51] = Sense(6, 0x28, 0) } };
        attentionOnQuery.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 0, lastTrack: 4));
        attentionOnQuery.TrackInfo = TrackInfo(track: 4, nwa: 34_506, free: 325_341);
        Require(new CdMmcCommands(attentionOnQuery, _ => { }).ReadNextWritableTrack().NextWritableAddress == 34_506,
            "a query answered with the media-change notice is repeated");
        ScriptedTransport reset = new() { Fail = { [0x51] = Sense(6, 0x29, 0) } };
        Throws<MmcCommandException>(() => new CdMmcCommands(reset, _ => { }).ReadDiscInformation(),
            "a bus reset is not retried: it clears the write parameters");
        // A recorder that names a written track as the last one is asked for the invisible
        // track (FFh), which answers with its real number.
        byte[] writtenTrack = TrackInfo(track: 1, nwa: 0, free: 0);
        writtenTrack[6] = 1;
        writtenTrack[7] = 0;
        ScriptedTransport lagging = new() { TrackInfo = writtenTrack, InvisibleTrackInfo = TrackInfo(track: 2, nwa: 22_636, free: 300_000) };
        lagging.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 1, lastTrack: 1));
        CdTrackInformation invisible = new CdMmcCommands(lagging, _ => { }).ReadNextWritableTrack();
        Require(invisible is { TrackNumber: 2, NextWritableAddress: 22_636, Blank: true, NextWritableAddressValid: true }
                && lagging.Commands[^1][0] == 0x52 && lagging.Commands[^1][5] == 0xff,
            "the invisible track is used when the numbered track is not writable");
        ScriptedTransport unwritable = new() { TrackInfo = writtenTrack, InvisibleTrackInfo = writtenTrack };
        unwritable.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 1, lastTrack: 1));
        Require(new CdMmcCommands(unwritable, _ => { }).ReadNextWritableTrack() is { Blank: false, TrackNumber: 1 },
            "without a writable track the first answer is returned for the caller to report");
        byte[] shortTrack = TrackInfo(track: 3, nwa: 34_506, free: 325_341)[..40];
        ScriptedTransport truncated = new() { TrackInfo = shortTrack };
        Throws<IOException>(() => new CdMmcCommands(truncated, _ => { }).ReadTrackInformation(3),
            "a reply shorter than its declared length");

        // A CLOSE that reports an error is accepted only when the session is in fact closed.
        ScriptedTransport closedAnyway = new() { Fail = { [0x5b] = Sense(5, 0x2c, 0) } };
        closedAnyway.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 0, lastTrack: 4));
        new CdMmcCommands(closedAnyway, _ => { }).CloseSession(expectFinalized: false);
        ScriptedTransport stillOpen = new() { Fail = { [0x5b] = Sense(5, 0x2c, 0) } };
        stillOpen.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 1, lastTrack: 3));
        Throws<MmcCommandException>(() => new CdMmcCommands(stillOpen, _ => { }).CloseSession(expectFinalized: false),
            "a failed CLOSE on a session that is still open");
        ScriptedTransport finalizedAnyway = new() { Fail = { [0x5b] = Sense(3, 0x73, 3) } };
        finalizedAnyway.DiscInfo.Enqueue(DiscInfo(status: 2, lastSession: 3, lastTrack: 4));
        new CdMmcCommands(finalizedAnyway, _ => { }).CloseSession(expectFinalized: true);
    }

    private static void TestProgress()
    {
        // Every acknowledged WRITE reaches the dock (throttled to 4 per second), with a rate
        // and the phase's sector size, so the data track's speed is not shown 13% low.
        List<OpticalBurnProgress> reports = [];
        TimeSpan now = TimeSpan.FromSeconds(5);
        CdExtraProgressTracker tracker = new(reports.Add, 300 * 2352, 1_000 * 2048, () => now);
        tracker.BeginPhase("music", 0);
        tracker.ReportAccepted(27 * 2352);
        now += TimeSpan.FromMilliseconds(100);
        tracker.ReportAccepted(54 * 2352);
        now += TimeSpan.FromSeconds(2);
        tracker.ReportAccepted(300 * 2352);
        OpticalBurnProgress[] music = reports.Where(report => report.Stage == "write").ToArray();
        Require(music.Length == 3 && music[1].CompletedBytes == 27 * 2352 && music[2].CompletedBytes == 300 * 2352
                && music[2].BytesPerSecond is { } rate && Math.Abs(rate - (300 - 27) * 2352 / 2.1) < 1
                && music.All(report => report.SectorBytes == 2352),
            "accepted audio bytes are published with a rate (no TimeSpan overflow)");
        reports.Clear();
        tracker.BeginPhase("picture", 300 * 2352, CdExtraDataContent.SectorBytes);
        tracker.ReportAccepted(32 * 2048);
        now += TimeSpan.FromSeconds(1);
        tracker.ReportAccepted(32 * 2048 + 2048 * 75 * 4);
        OpticalBurnProgress last = reports[^1];
        Require(reports.Count == 3 && last.SectorBytes == 2048 && last.CompletedBytes == 300 * 2352 + 32 * 2048 + 2048 * 75 * 4,
            "the picture phase reports 2048-byte sectors");
        BurnProgressDisplay display = new(OpticalBurnMediaKind.CdAudio, 300 * 2352 + 1_000 * 2048, () => TimeSpan.Zero);
        display.Apply(last);
        Require(display.MultiplierText == "4.0×", "2048-byte Mode 1 sectors convert to the right speed: " + display.MultiplierText);
        display.Apply(new OpticalBurnProgress("write", "music", 1, 2, 2352 * 75 * 8.0, 2352));
        Require(display.MultiplierText == "8.0×", "audio sectors keep the 2352-byte conversion");
    }

    private static void TestOrchestration()
    {
        // Success: the data producer receives the measured address, not the prediction.
        FakeSession session = new() { NextSession = (12_258, 300_000) };
        CdExtraSessionStart? seen = null;
        CdExtraBurnRequest request = Request(start => { seen = start; return DataContent(1_000); });
        CdExtraBurnResult result = WindowsImapiBurner.BurnCdExtra(session, request,
            new CdExtraProgressTracker(null, request.AudioBytes, 1_000 * 2048), CancellationToken.None);
        Require(session.Steps.SequenceEqual(new[]
            {
                "acquire", "media", "speed", "audio:300:False", "audio:400:False", "close:False", "nwa",
                "data:1000:12258:False", "close:True", "release",
            }), "two sessions in order: music, close with B0, measured start, picture, finalize");
        Require(seen is { NextWritableAddress: 12_258, FreeBlocks: 300_000, Simulated: false }
                && seen.AudioTrackStarts.SequenceEqual(new long[] { 0, 452 }),
            "the picture is generated for the recorder's address");
        Require(result.Session2StartLba == 12_258 && result.AudioBytesWritten == 700 * 2352
                && result.DataBytesWritten == 1_000 * 2048 && !result.TestWrite
                && session.Audio[0].SequenceEqual(Pcm(300, 1)) && session.Audio[1].SequenceEqual(Pcm(400, 2))
                && session.Data!.SequenceEqual(DataBytes(1_000)),
            "all produced bytes reach the recorder");

        // A picture-session failure leaves the closed music session playable and never finalizes.
        FakeSession failing = new() { FailData = new IOException("medium error") };
        InvalidOperationException? failure = null;
        try
        {
            WindowsImapiBurner.BurnCdExtra(failing, Request(_ => DataContent(1_000)),
                new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        }
        catch (InvalidOperationException exception) { failure = exception; }
        Require(failure is not null && failure.Message.Contains("第 1 区段（音乐）已经关闭", StringComparison.Ordinal)
                && failure.InnerException is IOException,
            "the failure names the playable music session");
        Require(!failing.Steps.Contains("close:True") && failing.Steps[^2] == "flush" && failing.Steps[^1] == "release",
            "after a failure only drain and release");

        // Test write: nothing is closed and the predicted address is used.
        FakeSession simulated = new();
        CdExtraSessionStart? simulatedStart = null;
        WindowsImapiBurner.BurnCdExtra(simulated, Request(start => { simulatedStart = start; return DataContent(1_000); }, testWrite: true),
            new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        Require(!simulated.Steps.Any(step => step.StartsWith("close", StringComparison.Ordinal))
                && !simulated.Steps.Contains("nwa") && simulated.Steps.Contains("data:1000:12254:True")
                && simulatedStart is { Simulated: true, NextWritableAddress: 12_254 },
            "a test write closes nothing and uses the predicted session address");

        // Every music source is verified and pinned before the first WRITE and released after
        // the music session; a changed file stops the burn before anything is written.
        FakeSession pinned = new();
        CdExtraBurnRequest pinnedRequest = Request(_ => DataContent(1_000)) with
        {
            PrepareAudio = () =>
            {
                pinned.Steps.Add("pin");
                return new Callback(() => pinned.Steps.Add("unpin"));
            },
        };
        WindowsImapiBurner.BurnCdExtra(pinned, pinnedRequest, new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        int pin = pinned.Steps.IndexOf("pin");
        int unpin = pinned.Steps.IndexOf("unpin");
        Require(pin > pinned.Steps.IndexOf("speed") && pin < pinned.Steps.IndexOf("audio:300:False")
                && unpin > pinned.Steps.IndexOf("audio:400:False") && unpin < pinned.Steps.IndexOf("close:False"),
            "music is pinned before the first WRITE and released after the music session");
        FakeSession edited = new();
        InvalidOperationException? refused = null;
        try
        {
            WindowsImapiBurner.BurnCdExtra(edited, Request(_ => DataContent(1_000)) with
                {
                    PrepareAudio = () => throw new IOException("Music file changed after it was added: 05.wav"),
                },
                new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        }
        catch (InvalidOperationException exception) { refused = exception; }
        Require(refused is not null && refused.Message.Contains("尚未向盘片写入数据", StringComparison.Ordinal)
                && !edited.Steps.Any(step => step.StartsWith("audio", StringComparison.Ordinal))
                && edited.Steps[^1] == "release",
            "a music file edited after planning stops the burn before any WRITE");

        // A written disc is refused before any WRITE.
        FakeSession written = new() { Media = Media() with { DiscStatus = 1 } };
        Throws<InvalidOperationException>(() => WindowsImapiBurner.BurnCdExtra(written, Request(_ => DataContent(1_000)),
            new CdExtraProgressTracker(null, 1, 1), CancellationToken.None), "non-blank media");
        Require(written.Steps.SequenceEqual(new[] { "acquire", "media", "release" }), "no write on a used disc");

        // Missing Mode 1 support is refused before any WRITE.
        FakeSession noMode1 = new() { Media = Media() with { TaoFeature = new CdTaoFeature(9, true, true, true, 1) } };
        Throws<InvalidOperationException>(() => WindowsImapiBurner.BurnCdExtra(noMode1, Request(_ => DataContent(1_000)),
            new CdExtraProgressTracker(null, 1, 1), CancellationToken.None), "missing data block type 8");

        // Cancellation during the music: drain, release, never close.
        using CancellationTokenSource cancellation = new();
        FakeSession cancelled = new() { AfterAudio = cancellation.Cancel };
        Throws<OperationCanceledException>(() => WindowsImapiBurner.BurnCdExtra(cancelled, Request(_ => DataContent(1_000)),
            new CdExtraProgressTracker(null, 1, 1), cancellation.Token), "cancellation");
        Require(!cancelled.Steps.Any(step => step.StartsWith("close", StringComparison.Ordinal))
                && cancelled.Steps[^2] == "flush" && cancelled.Steps[^1] == "release",
            "a cancelled music session is drained but not closed");
    }

    private static void TestBurnRequest(string directory)
    {
        string image = Path.Combine(directory, "source.png");
        CdExtraLayoutTests.WriteImage(image);
        string first = Path.Combine(directory, "01.wav");
        string second = Path.Combine(directory, "02.wav");
        byte[] firstPcm = CdExtraLayoutTests.WriteWave(first, frames: 588 * 50 + 3, seed: 2);
        CdExtraLayoutTests.WriteWave(second, frames: 588 * 400, seed: 4);
        CdDiscParameters parameters = new(24.5, 27.5, 15_000, UsableSectors: 14_998);
        CdExtraPlan plan = CdExtraPlanner.Plan(parameters, [first, second], null, "CD_EXTRA", false);
        CdExtraLayout layout = plan.Layout!;
        Require(plan.IsValid && layout.Session2StartLba == 300 + 152 + 400 + 2 + 11_400, "plan from WAV files");
        CdExtraBurnRequest request = BurnViewModel.CreateCdExtraBurnRequest(image, parameters, plan, null, "CD_EXTRA",
            false, ToneProfile.Photo, "memory-device", new OpticalWriteSpeed(1200, false));
        request.Validate();
        Require(request.AudioTracks.Select(track => track.Sectors).SequenceEqual(new long[] { 300, 400 })
                && request.PredictedSession2StartLba == layout.Session2StartLba
                && request.PredictedDataSectors == plan.DataPlan!.TrackSectors && !request.TestWrite,
            "request carries the padded track lengths and the prediction");
        using (MemoryStream pcm = new())
        {
            request.AudioTracks[0].ProducePcm(pcm, CancellationToken.None);
            byte[] bytes = pcm.ToArray();
            Require(bytes.Length == 300 * 2352 && bytes.AsSpan(0, firstPcm.Length).SequenceEqual(firstPcm)
                    && bytes.AsSpan(firstPcm.Length).IndexOfAnyExcept((byte)0) < 0,
                "music streams verbatim, padded with silence");
        }

        // The request pins the music; the producers then read through the verified handles,
        // so a metadata-only change (share modes do not block it) cannot stop the session.
        Require(request.PrepareAudio is not null, "the burn request pins its music before the first WRITE");
        using (IDisposable pin = request.PrepareAudio!())
        {
            Throws<IOException>(() => File.OpenWrite(first).Dispose(), "editing a pinned music file");
            File.SetLastWriteTimeUtc(first, File.GetLastWriteTimeUtc(first).AddMinutes(1));
            using MemoryStream pinnedPcm = new();
            request.AudioTracks[0].ProducePcm(pinnedPcm, CancellationToken.None);
            Require(pinnedPcm.Length == 300 * 2352 && pinnedPcm.ToArray().AsSpan(0, firstPcm.Length).SequenceEqual(firstPcm),
                "pinned music is read through the verified handles despite a new timestamp");
        }

        long measured = layout.Session2StartLba + 5;
        CdExtraDataContent data = request.CreateData(new CdExtraSessionStart(measured, 10_000, 15_000, [0, 452]));
        Require(data.Sectors == 14_998 - measured, "the data track fills the disc from the measured address");
        CdExtraDataContent limited = request.CreateData(new CdExtraSessionStart(measured, 1_000, 15_000, [0, 452]));
        Require(limited.Sectors == 1_000, "the drive's free blocks limit the picture track");
        using (MemoryStream iso = new())
        {
            data.Produce(iso, CancellationToken.None);
            ReadOnlySpan<byte> pvd = iso.ToArray().AsSpan(16 * 2048, 2048);
            Require(iso.Length == data.ContentLength && pvd.Slice(1, 5).SequenceEqual("CD001"u8)
                    && BinaryPrimitives.ReadUInt32LittleEndian(pvd[80..]) == measured + data.Sectors - 150,
                "the filesystem is authored for the measured session start");
        }

        CdExtraDataContent simulated = request.CreateData(new CdExtraSessionStart(layout.Session2StartLba, 0, 15_000, [0], Simulated: true));
        Require(simulated.Sectors == plan.DataPlan!.TrackSectors, "a simulated burn keeps the predicted layout");

        CdExtraPlan broken = CdExtraPlanner.Plan(parameters, [first, Path.Combine(directory, "missing.wav")], null, "CD_EXTRA", false);
        Require(broken.Problem == CdExtraPlanProblem.UnreadableMusic && broken.Tracks[1].Error is { Length: > 0 },
            "unreadable music is reported per track");
        Throws<ArgumentException>(() => BurnViewModel.CreateCdExtraBurnRequest(image, parameters, broken, null, "CD_EXTRA",
            false, ToneProfile.Photo, "memory-device", null), "no burn request for unreadable music");
    }

    /// <summary>The production <see cref="DynamicCdExtraSession"/> against a command-level
    /// model of a blank CD-R: every page 05h, NWA, WRITE(10), flush and CLOSE goes through the
    /// same MMC code as on a recorder.</summary>
    private static void TestRecorderSequence()
    {
        // The drive reports session 2 three blocks after the Track-at-Once prediction and
        // answers busy to the first command after every flush.
        FakeTaoRecorder recorder = new() { Session2Offset = 3, BusyAfterFlush = 1 };
        List<OpticalBurnProgress> progress = [];
        CdExtraBurnRequest request = Request(start => DataContent(1_000));
        CdExtraProgressTracker tracker = new(progress.Add, request.AudioBytes, 1_000 * 2048);
        CdExtraSessionStart? seen = null;
        CdExtraBurnResult result;
        using (DynamicCdExtraSession session = Session(recorder, tracker))
        {
            result = WindowsImapiBurner.BurnCdExtra(session, request with
            {
                CreateData = start => { seen = start; return DataContent(1_000); },
            }, tracker, CancellationToken.None);
        }
        Require(recorder.Tracks.SequenceEqual(new[] { (0L, 300L, 1), (452L, 400L, 1), (12_257L, 1_000L, 2) })
                && recorder.DiscStatus == 2,
            "two audio tracks and the picture track at the drive's addresses; the disc is finalized");
        Require(seen is { NextWritableAddress: 12_257, Simulated: false } && result.Session2StartLba == 12_257
                && seen.AudioTrackStarts.SequenceEqual(new long[] { 0, 452 }),
            "the picture is generated for the measured session 2 start, not the prediction (12,254)");
        Require(recorder.TrackData[0].SequenceEqual(Pcm(300, 1)) && recorder.TrackData[1].SequenceEqual(Pcm(400, 2))
                && recorder.TrackData[2].SequenceEqual(DataBytes(1_000)),
            "the recorder receives every produced byte");
        Require(recorder.Writes.Where(write => write.Lba < 12_257).All(write => write.BlockBytes == 2352)
                && recorder.Writes.Where(write => write.Lba >= 12_257).All(write => write.BlockBytes == 2048),
            "audio as 2352-byte blocks, the picture as 2048-byte Mode 1 blocks");
        Require(recorder.PagesAtSession2Read.Count > 0
                && recorder.PagesAtSession2Read.All(page => page == "wt1:ms0:tm4:bt8:tw0"),
            "session 2 is measured with the data-track write parameters selected");
        Require(recorder.Log.Count(entry => entry == "close:ms3") == 1 && recorder.Log.Count(entry => entry == "close:ms0") == 1
                && recorder.Log.IndexOf("close:ms3") < recorder.Log.IndexOf("select:wt1:ms0:tm4:bt8:tw0"),
            "the music session is closed with a next-session pointer before the picture parameters");
        Require(recorder.Log.Count(entry => entry.StartsWith("busy", StringComparison.Ordinal)) >= 3,
            "busy answers after each flush were retried");
        Require(progress.Any(report => report.Stage == "write" && report.CompletedBytes == 27 * 2352)
                && progress.Any(report => report.Stage == "write" && report.SectorBytes == 2048
                    && report.CompletedBytes == request.AudioBytes + 32 * 2048),
            "WRITE acknowledgements of both sessions reach the progress display");

        // The address changes between measuring and writing: refuse, keep the music playable.
        FakeTaoRecorder moving = new() { ShiftNwaAfterFirstSession2Read = true };
        InvalidOperationException? moved = null;
        try
        {
            using DynamicCdExtraSession session = Session(moving, null);
            WindowsImapiBurner.BurnCdExtra(session, Request(_ => DataContent(1_000)),
                new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        }
        catch (InvalidOperationException exception) { moved = exception; }
        Require(moved is not null && moved.InnerException is IOException
                && moved.Message.Contains("第 1 区段（音乐）已经关闭", StringComparison.Ordinal)
                && !moving.Writes.Any(write => write.BlockBytes == 2048) && !moving.Log.Contains("close:ms0"),
            "a moved session 2 address stops before the picture is written");

        // Fewer free blocks than the picture needs: stop before writing it.
        FakeTaoRecorder small = new() { Session2FreeBlocks = 500 };
        InvalidOperationException? full = null;
        try
        {
            using DynamicCdExtraSession session = Session(small, null);
            WindowsImapiBurner.BurnCdExtra(session, Request(_ => DataContent(1_000)),
                new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        }
        catch (InvalidOperationException exception) { full = exception; }
        Require(full is not null && full.Message.Contains("500", StringComparison.Ordinal)
                && !small.Writes.Any(write => write.BlockBytes == 2048) && !small.Log.Contains("close:ms0"),
            "a picture larger than the drive's free blocks is not written");

        // Free blocks shrink between measuring and writing: the data track's own check stops it.
        FakeTaoRecorder shrinking = new() { Session2FreeBlocksOnSecondRead = 500 };
        InvalidOperationException? shrunk = null;
        try
        {
            using DynamicCdExtraSession session = Session(shrinking, null);
            WindowsImapiBurner.BurnCdExtra(session, Request(_ => DataContent(1_000)),
                new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        }
        catch (InvalidOperationException exception) { shrunk = exception; }
        Require(shrunk?.InnerException is IOException inner && inner.Message.Contains("可写 500", StringComparison.Ordinal)
                && !shrinking.Writes.Any(write => write.BlockBytes == 2048) && !shrinking.Log.Contains("close:ms0"),
            "the data track re-checks the drive's free blocks before writing");

        // Test write: every command with the Test Write bit, nothing closed, the disc stays blank.
        FakeTaoRecorder simulated = new();
        using (DynamicCdExtraSession session = Session(simulated, null))
        {
            WindowsImapiBurner.BurnCdExtra(session, Request(_ => DataContent(1_000), testWrite: true),
                new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        }
        Require(simulated.DiscStatus == 0 && simulated.Tracks.Count == 0
                && !simulated.Log.Any(entry => entry.StartsWith("close", StringComparison.Ordinal))
                && simulated.Log.Where(entry => entry.StartsWith("select", StringComparison.Ordinal)).All(entry => entry.EndsWith(":tw1", StringComparison.Ordinal)),
            "a test write sets Test Write on every page and records nothing");
    }

    private static DynamicCdExtraSession Session(FakeTaoRecorder recorder, CdExtraProgressTracker? tracker)
        => new(recorder, requested => requested ?? new OpticalWriteSpeed(1200, false),
            commands => new BluRaySessionCleanup(
                acquireExclusive: () => { },
                disableMcn: () => { },
                preventRemoval: () => commands.PreventRemoval(true),
                allowRemoval: () => commands.PreventRemoval(false),
                enableMcn: () => { },
                releaseExclusive: () => { },
                releaseWriteEngine: () => { }),
            tracker is null ? null : tracker.ReportAccepted,
            sleep: _ => { });

    private static CdExtraBurnRequest Request(Func<CdExtraSessionStart, CdExtraDataContent> createData, bool testWrite = false)
        => new("fake", [new CdExtraAudioTrack(300, Producer(Pcm(300, 1))), new CdExtraAudioTrack(400, Producer(Pcm(400, 2)))],
            createData, 300 + 152 + 400 + 2 + 11_400, 1_000, new OpticalWriteSpeed(1200, false), TestWrite: testWrite);

    private static CdExtraDataContent DataContent(long sectors) => new(sectors, Producer(DataBytes(sectors)));

    private static Action<Stream, CancellationToken> Producer(byte[] bytes) => (output, _) => output.Write(bytes);

    private static byte[] Pcm(long sectors, int seed)
        => Enumerable.Range(0, checked((int)(sectors * 2352))).Select(index => (byte)(index * seed + seed)).ToArray();

    private static byte[] DataBytes(long sectors)
        => Enumerable.Range(0, checked((int)(sectors * 2048))).Select(index => (byte)(index * 3 + 1)).ToArray();

    private static CdExtraMediaInfo Media()
        => new(0x0009, 0, 0, 359_845, new CdTaoFeature(0x0009, true, true, true, (1 << 0) | (1 << 8)), 52);

    private static long Lba(byte[] cdb) => BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2, 4));

    private static int Blocks(byte[] cdb) => BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7, 2));

    private static MmcCommandResult Good() => new(0, new byte[18], []);

    private static MmcCommandResult NotReady() => Sense(2, 4, 8);

    private static MmcCommandResult Sense(int key, int asc, int ascq)
    {
        byte[] sense = new byte[18];
        sense[0] = 0x70;
        sense[2] = (byte)key;
        sense[12] = (byte)asc;
        sense[13] = (byte)ascq;
        return new(BluRayMmcCommands.CommandHasSenseData, sense, []);
    }

    private static byte[] DiscInfo(int status, int lastSession, int lastTrack)
    {
        byte[] data = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(data, 32);
        data[2] = (byte)((lastSession << 2) | status);
        data[4] = 1;
        data[6] = (byte)lastTrack;
        return data;
    }

    private static byte[] TrackInfo(int track, uint nwa, uint free)
    {
        byte[] data = new byte[48];
        BinaryPrimitives.WriteUInt16BigEndian(data, 46);
        data[2] = (byte)track;
        data[3] = 2;
        data[5] = 4;
        data[6] = 0x40 | 1;
        data[7] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), nwa);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), nwa);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), free);
        return data;
    }

    private static byte[] ReadAll(IStream stream)
    {
        using MemoryStream bytes = new();
        byte[] buffer = new byte[65536];
        IntPtr count = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, count);
                int read = Marshal.ReadInt32(count);
                if (read <= 0) break;
                bytes.Write(buffer, 0, read);
            }
        }
        finally { Marshal.FreeHGlobal(count); }
        return bytes.ToArray();
    }

    private sealed record WriteCommand(byte[] Cdb, byte[] Data, uint Timeout);

    private sealed class WriteTransport : IMmcCommandTransport
    {
        private readonly Dictionary<int, int> _attempts = [];
        internal List<WriteCommand> Writes { get; } = [];
        internal Dictionary<int, MmcCommandResult[]> Script { get; } = [];
        public MmcWriteTransferLimits GetWriteTransferLimits() => new(4095, 65536);
        public MmcCommandResult SendData(byte[] cdb, IntPtr buffer, int bufferLength, uint timeoutSeconds)
        {
            ulong address = unchecked((ulong)buffer.ToInt64());
            Require(address % (uint)Environment.SystemPageSize == 0, "aligned transfer buffer");
            byte[] data = new byte[bufferLength];
            Marshal.Copy(buffer, data, 0, bufferLength);
            int command = Writes.Select(write => Lba(write.Cdb)).Distinct().Count();
            if (Writes.Count > 0 && Lba(Writes[^1].Cdb) == Lba(cdb)) command--;
            Writes.Add(new(cdb.ToArray(), data, timeoutSeconds));
            if (!Script.TryGetValue(command, out MmcCommandResult[]? results)) return Good();
            int attempt = _attempts.GetValueOrDefault(command);
            _attempts[command] = attempt + 1;
            return attempt < results.Length ? results[attempt] : Good();
        }
        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
            => throw new InvalidOperationException("The write loop issues only WRITE(10).");
    }

    private sealed class ScriptedTransport : IMmcCommandTransport
    {
        internal List<byte[]> Commands { get; } = [];
        internal Queue<byte[]> DiscInfo { get; } = new();
        internal byte[] TrackInfo { get; set; } = [];
        /// <summary>The answer for track FFh; null answers like any other track.</summary>
        internal byte[]? InvisibleTrackInfo { get; set; }
        /// <summary>NOT READY answers (02/04/08) to send before executing an opcode.</summary>
        internal Dictionary<byte, int> Busy { get; } = [];
        /// <summary>One failure to answer for an opcode.</summary>
        internal Dictionary<byte, MmcCommandResult> Fail { get; } = [];
        public MmcWriteTransferLimits GetWriteTransferLimits() => new(4095, 65536);
        public MmcCommandResult SendData(byte[] cdb, IntPtr buffer, int bufferLength, uint timeoutSeconds)
            => Answer(cdb) ?? Good();
        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            if (Answer(cdb) is { } scripted) return scripted;
            return cdb[0] switch
            {
                0x51 => new(0, new byte[18], DiscInfo.Dequeue()),
                0x52 => new(0, new byte[18], cdb[5] == 0xff && InvisibleTrackInfo is { } invisible ? invisible : TrackInfo),
                0x5b or 0x00 or 0x35 => Good(),
                _ => throw new InvalidOperationException($"Unexpected command 0x{cdb[0]:X2}."),
            };
        }

        private MmcCommandResult? Answer(byte[] cdb)
        {
            Commands.Add(cdb.ToArray());
            if (Busy.TryGetValue(cdb[0], out int busy) && busy > 0)
            {
                Busy[cdb[0]] = busy - 1;
                return NotReady();
            }
            return Fail.Remove(cdb[0], out MmcCommandResult? failure) ? failure : null;
        }
    }

    /// <summary>
    /// A blank 40,000-block CD-R as a Track-at-Once recorder reports it. Only the invisible
    /// track's next writable address accepts WRITE(10), with the block size of the selected
    /// page 05h. SYNCHRONIZE CACHE completes the track (2 run-out + 150 pre-gap blocks before
    /// the next); CLOSE SESSION with multi-session 11b writes lead-out and lead-in (11,400
    /// blocks to the next program area), with 00b it finalizes. With Test Write nothing is
    /// recorded.
    /// </summary>
    private sealed class FakeTaoRecorder : IMmcCommandTransport
    {
        private readonly byte[] _page = new byte[52];
        private readonly List<byte[]> _openData = [];
        private long _nwa;
        private long? _openStart;
        private long _writePointer;
        private int _session = 1;
        private int _firstTrackInSession = 1;
        private int _lastSessionState;
        private int _busy;
        private bool _shifted;
        private int _session2Reads;

        internal FakeTaoRecorder()
        {
            _page[0] = 0x05;
            _page[1] = 0x32;
            _page[2] = 0x02; // drive default: SAO
        }

        internal long LeadoutStart { get; init; } = 40_000;
        internal long Session2Offset { get; init; }
        internal long? Session2FreeBlocks { get; init; }
        internal long? Session2FreeBlocksOnSecondRead { get; init; }
        internal bool ShiftNwaAfterFirstSession2Read { get; init; }
        internal int BusyAfterFlush { get; init; }
        internal int DiscStatus { get; private set; }
        internal List<(long Start, long Length, int Session)> Tracks { get; } = [];
        internal List<byte[]> TrackData { get; } = [];
        internal List<(long Lba, int Blocks, int BlockBytes)> Writes { get; } = [];
        internal List<string> Log { get; } = [];
        internal List<string> PagesAtSession2Read { get; } = [];

        private bool TestWrite => (_page[2] & 0x10) != 0;
        private int MultiSession => _page[3] >> 6;
        private int BlockBytes => (_page[4] & 0x0f) switch { 0 => 2352, 8 => 2048, _ => 0 };
        private string Page => $"wt{_page[2] & 0x0f}:ms{_page[3] >> 6}:tm{_page[3] & 0x0f}:bt{_page[4] & 0x0f}:tw{(_page[2] >> 4) & 1}";

        public MmcWriteTransferLimits GetWriteTransferLimits() => new(4095, 1 << 20);

        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            if (cdb[0] == 0x00) return Good();
            if (Busy(cdb[0]) is { } busy) return busy;
            switch (cdb[0])
            {
                case 0x1e:
                    return Good();
                case 0x46:
                {
                    byte[] data = new byte[32];
                    BinaryPrimitives.WriteUInt32BigEndian(data, 28);
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), 0x0009);
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), 0x002d);
                    data[10] = 0x01; // current
                    data[11] = 4;
                    data[12] = 0x40 | 0x04; // BUF, Test Write
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14), 0x0101); // raw audio, Mode 1
                    return new(0, new byte[18], data);
                }
                case 0x51:
                {
                    byte[] data = new byte[34];
                    BinaryPrimitives.WriteUInt16BigEndian(data, 32);
                    data[2] = (byte)((_lastSessionState << 2) | DiscStatus);
                    data[3] = 1;
                    data[4] = (byte)_session;
                    data[5] = (byte)_firstTrackInSession;
                    data[6] = (byte)(Tracks.Count + (DiscStatus == 2 ? 0 : 1));
                    return new(0, new byte[18], data);
                }
                case 0x52:
                    return new(0, new byte[18], TrackInformation(BinaryPrimitives.ReadInt32BigEndian(cdb.AsSpan(2))));
                case 0x43:
                {
                    byte[] data = new byte[28];
                    BinaryPrimitives.WriteUInt16BigEndian(data, 26);
                    long frames = LeadoutStart + 150;
                    data[12] = (byte)(frames / 4500);
                    data[13] = (byte)(frames / 75 % 60);
                    data[14] = (byte)(frames % 75);
                    return new(0, new byte[18], data);
                }
                case 0x5a:
                {
                    byte[] data = new byte[8 + _page.Length];
                    BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)(data.Length - 2));
                    _page.CopyTo(data, 8);
                    return new(0, new byte[18], data);
                }
                case 0x35:
                    CompleteTrack();
                    _busy = BusyAfterFlush;
                    return Good();
                case 0x5b:
                    return Close(cdb);
                default:
                    throw new InvalidOperationException($"Unexpected command 0x{cdb[0]:X2}.");
            }
        }

        public MmcCommandResult SendData(byte[] cdb, IntPtr buffer, int bufferLength, uint timeoutSeconds)
        {
            if (Busy(cdb[0]) is { } busy) return busy;
            byte[] data = new byte[bufferLength];
            Marshal.Copy(buffer, data, 0, bufferLength);
            if (cdb[0] == 0x55)
            {
                if (data.AsSpan(0, 8).IndexOfAnyExcept((byte)0) >= 0 || data[8] != 0x05)
                    return Sense(5, 0x26, 0);
                data.AsSpan(8, _page.Length).CopyTo(_page);
                Log.Add("select:" + Page);
                return Good();
            }
            if (cdb[0] != 0x2a) throw new InvalidOperationException($"Unexpected data command 0x{cdb[0]:X2}.");
            long lba = BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2));
            int blocks = BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7));
            if (BlockBytes == 0 || blocks * BlockBytes != bufferLength || DiscStatus == 2)
                return Sense(5, 0x24, 0);
            if (lba != (_openStart is null ? _nwa : _writePointer))
                return Sense(5, 0x21, 2); // invalid address for write
            _openStart ??= lba;
            _writePointer = lba + blocks;
            _openData.Add(data);
            Writes.Add((lba, blocks, BlockBytes));
            return Good();
        }

        private MmcCommandResult? Busy(byte opcode)
        {
            if (_busy <= 0) return null;
            _busy--;
            Log.Add($"busy:0x{opcode:X2}");
            return NotReady();
        }

        private byte[] TrackInformation(int track)
        {
            byte[] data = new byte[48];
            BinaryPrimitives.WriteUInt16BigEndian(data, 46);
            data[2] = (byte)track;
            bool invisible = track == Tracks.Count + 1 && DiscStatus != 2;
            if (invisible)
            {
                if (_session == 2)
                {
                    _session2Reads++;
                    PagesAtSession2Read.Add(Page);
                    if (ShiftNwaAfterFirstSession2Read && _shifted) _nwa++;
                    _shifted = true;
                }
                long free = _session == 2 && _session2Reads > 1 && Session2FreeBlocksOnSecondRead is { } shrunk ? shrunk
                    : _session == 2 && Session2FreeBlocks is { } limit ? limit
                    : LeadoutStart - 2 - _nwa;
                data[3] = (byte)_session;
                data[6] = 0x40; // blank
                data[7] = 1;    // NWA_V
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)_nwa);
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), (uint)_nwa);
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)free);
            }
            else
            {
                (long start, long length, int session) = Tracks[track - 1];
                data[3] = (byte)session;
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)start);
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), (uint)length);
            }
            return data;
        }

        private void CompleteTrack()
        {
            if (_openStart is not { } start) return;
            if (!TestWrite)
            {
                Tracks.Add((start, _writePointer - start, _session));
                TrackData.Add(_openData.SelectMany(chunk => chunk).ToArray());
                _nwa = _writePointer + 2 + 150;
                DiscStatus = 1;
                _lastSessionState = 1;
            }
            _openData.Clear();
            _openStart = null;
        }

        private MmcCommandResult Close(byte[] cdb)
        {
            if ((cdb[2] & 7) != 2) return Sense(5, 0x24, 0);
            Log.Add($"close:ms{MultiSession}");
            if (TestWrite || Tracks.Count == 0) return Sense(5, 0x2c, 0);
            if (MultiSession == 3)
            {
                long leadout = Tracks[^1].Start + Tracks[^1].Length + 2;
                _nwa = leadout + 6_750 + 4_500 + 150 + Session2Offset;
                _session++;
                _firstTrackInSession = Tracks.Count + 1;
                _lastSessionState = 0;
            }
            else
            {
                DiscStatus = 2;
                _lastSessionState = 3;
            }
            return Good();
        }
    }

    private sealed class Callback(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed class FakeSession : ICdExtraSession
    {
        private long _nextAudio;
        internal List<string> Steps { get; } = [];
        internal List<byte[]> Audio { get; } = [];
        internal byte[]? Data { get; private set; }
        internal CdExtraMediaInfo Media { get; init; } = CdExtraBurnTests.Media();
        internal (long Nwa, long Free) NextSession { get; init; } = (12_254, 300_000);
        internal Exception? FailData { get; init; }
        internal Action? AfterAudio { get; init; }
        public void Acquire() => Steps.Add("acquire");
        public CdExtraMediaInfo ReadMedia() { Steps.Add("media"); return Media; }
        public OpticalWriteSpeed ConfigureWrite(OpticalWriteSpeed? requested)
        {
            Steps.Add("speed");
            return requested ?? new OpticalWriteSpeed(1200, false);
        }
        public long WriteAudioTrack(IStream pcm, long sectors, bool testWrite)
        {
            Steps.Add($"audio:{sectors}:{testWrite}");
            Audio.Add(ReadAll(pcm));
            AfterAudio?.Invoke();
            long start = _nextAudio;
            _nextAudio += sectors + 152;
            return start;
        }
        public void CloseSession(bool finalize) => Steps.Add($"close:{finalize}");
        public (long NextWritableAddress, long FreeBlocks) ReadNextSessionStart()
        {
            Steps.Add("nwa");
            return NextSession;
        }
        public long WriteDataTrack(IStream data, long sectors, long expectedStartLba, bool testWrite)
        {
            Steps.Add($"data:{sectors}:{expectedStartLba}:{testWrite}");
            if (FailData is not null) throw FailData;
            Data = ReadAll(data);
            return expectedStartLba;
        }
        public void Flush() => Steps.Add("flush");
        public void CancelWrite() { }
        public void Release() => Steps.Add("release");
        public void Dispose() { }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class MemoryContent(byte[] bytes) : IStream
    {
        private int _position;
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int count = Math.Min(cb, bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(pv);
            _position += count;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, count);
        }
        public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = bytes.Length };
        public void Seek(long offset, int origin, IntPtr result) => throw new NotSupportedException();
        public void Write(byte[] a, int b, IntPtr c) => throw new NotSupportedException();
        public void SetSize(long a) => throw new NotSupportedException();
        public void CopyTo(IStream a, long b, IntPtr c, IntPtr d) => throw new NotSupportedException();
        public void Commit(int a) { }
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long a, long b, int c) => throw new NotSupportedException();
        public void UnlockRegion(long a, long b, int c) => throw new NotSupportedException();
        public void Clone(out IStream stream) => throw new NotSupportedException();
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("CD-Extra burn: expected " + typeof(T).Name + " for " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD-Extra burn: " + message);
    }
}
