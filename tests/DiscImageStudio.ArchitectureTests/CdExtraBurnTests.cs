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
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "cd-extra-burn-" + Guid.NewGuid().ToString("N"));

    internal static void Run()
    {
        Directory.CreateDirectory(Scratch);
        try
        {
            TestSharedConstants();
            TestWriteParametersPage();
            TestTaoWriter();
            TestCommands();
            TestProgress();
            TestOrchestration();
            TestRecorderSequence();
            TestUnreportedTaoFormats();
            TestPictureRecovery();
            TestBurnRequest(Scratch);
            TestPersistentPicture(Path.Combine(Scratch, "durable"));
            TestAutomaticReconnect();
            TestUnexpectedWriteResponse();
            TestUnexpectedFinalizationResponse();
        }
        finally { Directory.Delete(Scratch, recursive: true); }
        Console.WriteLine("cd-extra-burn: passed (TAO writes, two sessions, picture recovery and readback on a command-level CD-R model; no hardware)");
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

        // cdrecord and libburn name 3 "complete": a recorder that reports the session it has
        // just closed instead of the new empty one is accepted as well (the tested BU20N
        // reports 0; this path is covered by the model only).
        ScriptedTransport reportsComplete = new();
        reportsComplete.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 1, lastTrack: 3));
        reportsComplete.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 3, lastTrack: 3));
        new CdMmcCommands(reportsComplete, _ => { }).CloseSession(expectFinalized: false);
        Require(reportsComplete.Commands.Count(cdb => cdb[0] == 0x51) == 2,
            "an incomplete session is waited on, then state 3 counts as closed");

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
        ScriptedTransport closedAnywayComplete = new() { Fail = { [0x5b] = Sense(5, 0x2c, 0) } };
        closedAnywayComplete.DiscInfo.Enqueue(DiscInfo(status: 1, lastSession: 3, lastTrack: 4));
        new CdMmcCommands(closedAnywayComplete, _ => { }).CloseSession(expectFinalized: false);
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
        tracker.Verify(250, 1000);
        display.Apply(reports[^1]);
        Require(display.Percent == 25 && display.BytesPerSecond is null, "readback reports its own sector progress");
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
        Require(!failing.Steps.Contains("close:True") && !failing.Steps.Contains("flush") && failing.Steps[^1] == "release",
            "release without closing the interrupted picture track");

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
        CdExtraBurnRequest request = CdExtraPlanner.CreateBurnRequest(image, parameters, plan, null, "CD_EXTRA",
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
        Throws<ArgumentException>(() => CdExtraPlanner.CreateBurnRequest(image, parameters, broken, null, "CD_EXTRA",
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

        // A recorder that reports the closed session as complete (3) instead of the new empty
        // one: the burn continues into session 2 the same way.
        FakeTaoRecorder reportsComplete = new() { LastSessionStateAfterClose = 3 };
        using (DynamicCdExtraSession session = Session(reportsComplete, null))
        {
            CdExtraBurnResult completeResult = WindowsImapiBurner.BurnCdExtra(session, Request(_ => DataContent(1_000)),
                new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
            Require(completeResult.Session2StartLba == 12_254 && reportsComplete.DiscStatus == 2
                    && reportsComplete.Tracks.Count == 3 && reportsComplete.Tracks[^1] == (12_254L, 1_000L, 2),
                "last-session state 3 after closing session 1 continues into session 2");
        }

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

    private static void TestUnreportedTaoFormats()
    {
        foreach (string mode in new[] { "accept", "test-write", "reject-data", "ignore-audio", "ignore-test-bit", "inactive", "short-descriptor" })
        {
            bool simulated = mode is "test-write" or "ignore-test-bit";
            var disc = new FakeTaoRecorder { TaoDataTypes = 0, RejectMode1 = mode == "reject-data",
                IgnoreAudioSelection = mode == "ignore-audio", IgnoreTestBit = mode == "ignore-test-bit" };
            using var session = Session(new CommandView(disc, (cdb, execute) =>
            {
                var response = execute();
                if (cdb[0] == 0x46)
                {
                    if (mode == "inactive") response.Data[10] &= 0xfe;
                    if (mode == "short-descriptor") response.Data[11] = 0;
                }
                return response;
            }), null);
            var request = Request(_ => DataContent(1000), testWrite: simulated);
            CdExtraBurnResult? result = null;
            Exception? error = null;
            try { result = WindowsImapiBurner.BurnCdExtra(session, request, new CdExtraProgressTracker(null, 1, 1), default); }
            catch (InvalidOperationException failure) { error = failure; }
            if (mode is "accept" or "test-write")
                Require(error is null && result?.VerifiedPictureSectors == (simulated ? 0 : 1000) && disc.DiscStatus == (simulated ? 0 : 2),
                    mode + " completes with an empty capability bitmap: " + error);
            else
                Require(error is not null && disc.Writes.Count == 0 && disc.Tracks.Count == 0 && disc.DiscStatus == 0
                    && !disc.Log.Any(s => s.StartsWith("close:")), mode + " stops before recording");
        }
        Console.WriteLine("cd-extra-tao-preflight: zero bitmap, confirmed audio and Mode 1, test bit, rejected/ignored settings, inactive/truncated descriptor passed");
    }

    private static void TestPictureRecovery()
    {
        foreach (int writtenBeforeTimeout in new[] { 0, 7, 32 })
        {
            FakeTaoRecorder drive = new() { DataTimeoutAfterBlocks = writtenBeforeTimeout };
            using var session = Session(drive, null);
            WindowsImapiBurner.BurnCdExtra(session, Request(_ => DataContent(1000)),
                new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
            Require(drive.DiscStatus == 2 && drive.TrackData[2].SequenceEqual(DataBytes(1000))
                    && drive.PictureReadbacks == (writtenBeforeTimeout == 0 ? 0 : 1),
                $"a lost acknowledgement after {writtenBeforeTimeout} blocks writes the exact picture once");
        }
        foreach (FakeTaoRecorder drive in new[]
        {
            new FakeTaoRecorder { DataTimeoutAfterBlocks = 5, CorruptReadback = true },
            new FakeTaoRecorder { DataTimeoutAfterBlocks = 0, RepeatedTimeout = true },
            new FakeTaoRecorder { DataTimeoutAfterBlocks = 3, InvalidRecoveryNwa = true },
        })
        {
            using var session = Session(drive, null);
            Throws<InvalidOperationException>(() => WindowsImapiBurner.BurnCdExtra(session, Request(_ => DataContent(1000)),
                new CdExtraProgressTracker(null, 1, 1), CancellationToken.None), "an unconfirmed picture stops");
            Require(!drive.Log.Contains("close:ms0") && drive.InjectedTimeouts is >= 1 and <= 3,
                "bounded retries and no finalization of incomplete data");
        }
        Console.WriteLine("cd-extra-picture-recovery: zero, partial and full acknowledgement loss; bad readback, invalid NWA and retry exhaustion passed");
    }

    private static void TestPersistentPicture(string directory)
    {
        const long start = 12_254;
        var drive = new FakeTaoRecorder { DataTimeoutAfterBlocks = 7, RepeatedTimeout = true, TimeoutOffset = 64 };
        var request = Request(_ => DataContent(1000)) with { RecoveryRoot = directory };
        var tracker = new CdExtraProgressTracker(null, request.AudioBytes, 1000 * 2048);
        using (var initial = Session(drive, tracker))
            Throws<InvalidOperationException>(() => WindowsImapiBurner.BurnCdExtra(initial, request, tracker,
                CancellationToken.None), "persistent timeout leaves the picture open");
        string record = Directory.GetFiles(directory, CdExtraCheckpoint.FileName, SearchOption.AllDirectories).Single();
        int before = drive.Writes.Count;
        Require(drive.Tracks.Count == 2 && drive.DiscStatus == 1 && drive.InjectedTimeouts == 3, "partial track retained");
        drive.DataTimeoutAfterBlocks = null;
        drive.DiscId = 99;
        using (var wrong = Session(drive, tracker))
            Throws<IOException>(() => wrong.ResumePicture("fake", record, tracker, default), "different disc ID");
        drive.DiscId = 0x11223344;
        using (var wrong = Session(drive, tracker))
            Throws<IOException>(() => wrong.ResumePicture("another recorder", record, tracker, default), "different drive");
        using (var cancelled = Session(drive, tracker))
            Throws<OperationCanceledException>(() => cancelled.ResumePicture("fake", record, tracker,
                new CancellationToken(true)), "cancel before writing");
        string iso = Path.Combine(Path.GetDirectoryName(record)!, CdExtraCheckpoint.ImageName);
        using (var changed = File.OpenWrite(iso)) changed.WriteByte(88);
        using (var badSource = Session(drive, tracker))
            Throws<InvalidDataException>(() => badSource.ResumePicture("fake", record, tracker, default), "changed ISO");
        using (var original = File.OpenWrite(iso)) original.WriteByte(DataBytes(1)[0]);
        drive.CorruptReadback = true;
        using (var badPrefix = Session(drive, tracker))
            Throws<InvalidDataException>(() => badPrefix.ResumePicture("fake", record, tracker, default), "corrupt recorded prefix");
        drive.CorruptReadback = false;
        using var stop = new CancellationTokenSource();
        var cancelDuringRead = new CdExtraProgressTracker(p =>
        {
            if (p.Stage == "verify" && p.CompletedBytes > 0) stop.Cancel();
        }, 0, 0);
        using (var interruptedRead = Session(drive, cancelDuringRead))
            Throws<OperationCanceledException>(() => interruptedRead.ResumePicture("fake", record, cancelDuringRead, stop.Token),
                "cancel during prefix verification");
        Require(drive.Writes.Count == before && drive.Tracks.Count == 2, "rejected recovery issues no WRITE or flush");
        using (var restarted = Session(drive, tracker))
            Require(restarted.ResumePicture("fake", record, tracker, default) == 1000, "resume from disk descriptor");
        Require(drive.Writes[before].Lba == start + 85 && drive.Tracks.Count == 3
            && drive.TrackData[^1].SequenceEqual(DataBytes(1000)) && drive.DiscStatus == 2,
            "full prefix verified, only missing suffix written once, disc finalized");
        int writes = drive.Writes.Count, closes = drive.Log.Count(s => s.StartsWith("close:"));
        using (var sealedDisc = Session(drive, tracker))
            Require(sealedDisc.ResumePicture("fake", record, tracker, default) == 1000, "finalized disc verifies only");
        drive.CorruptFinalLba = start + 999;
        using (var corrupted = Session(drive, tracker))
        {
            try { corrupted.ResumePicture("fake", record, tracker, default); throw new Exception("missed corruption"); }
            catch (IOException e) { Require(e.Message.Contains((start + 999).ToString()) && e.Message.Contains("17"), "bad LBA and byte"); }
        }
        drive.CorruptFinalLba = null;
        drive.ShortFinalLba = start + 811;
        using (var shortRead = Session(drive, tracker))
        {
            try { shortRead.ResumePicture("fake", record, tracker, default); throw new Exception("accepted short read"); }
            catch (IOException e) { Require(e.Message.Contains((start + 811).ToString()), "unreadable single LBA reported"); }
        }
        Require(writes == drive.Writes.Count && closes == drive.Log.Count(s => s.StartsWith("close:")),
            "all re-verifications are physically read-only");

        string fallback = Path.Combine(directory, "fingerprint");
        var noId = new FakeTaoRecorder { DiscId = null, DataTimeoutAfterBlocks = 0, RepeatedTimeout = true };
        using (var original = Session(noId, tracker))
            Throws<InvalidOperationException>(() => WindowsImapiBurner.BurnCdExtra(original,
                request with { RecoveryRoot = fallback }, tracker, default), "timeout before first picture sector");
        string fallbackRecord = Directory.GetFiles(fallback, CdExtraCheckpoint.FileName, SearchOption.AllDirectories).Single();
        noId.DataTimeoutAfterBlocks = null;
        noId.TrackData[0][75 * 2352] ^= 1;
        using (var otherAudio = Session(noId, tracker))
            Throws<IOException>(() => otherAudio.ResumePicture("fake", fallbackRecord, tracker, default), "different recorded music");
        noId.TrackData[0][75 * 2352] ^= 1;
        using (var sameAudio = Session(noId, tracker))
            Require(sameAudio.ResumePicture("fake", fallbackRecord, tracker, default) == 1000, "disc without ID identified by music");

        string awaitingClose = Path.Combine(directory, "awaiting-close");
        var closeFailure = new FakeTaoRecorder { RejectFinalClose = true };
        using (var beforeClose = Session(closeFailure, tracker))
            Throws<InvalidOperationException>(() => WindowsImapiBurner.BurnCdExtra(beforeClose,
                request with { RecoveryRoot = awaitingClose }, tracker, default), "completed track before final close");
        string closeRecord = Directory.GetFiles(awaitingClose, CdExtraCheckpoint.FileName, SearchOption.AllDirectories).Single();
        int written = closeFailure.Writes.Count;
        closeFailure.RejectFinalClose = false;
        using (var afterFlush = Session(closeFailure, tracker))
            Require(afterFlush.ResumePicture("fake", closeRecord, tracker, default) == 1000, "finish closing a complete track");
        Require(closeFailure.Writes.Count == written && closeFailure.DiscStatus == 2, "completed track was never rewritten");

        var failedVerify = new FakeTaoRecorder { CorruptFinalLba = start + 700 };
        using (var bad = Session(failedVerify, tracker))
        {
            try { WindowsImapiBurner.BurnCdExtra(bad, request, tracker, default); throw new Exception("falsely verified"); }
            catch (InvalidOperationException e)
            { Require(failedVerify.DiscStatus == 2 && e.Message.Contains("盘片已封盘") && e.Message.Contains((start + 700).ToString()),
                    "normal burn surfaces verification failure after finalization"); }
        }
        Console.WriteLine("cd-extra-checkpoint: reopen after timeout +85, disc and source identity, cancellation, complete picture readback, corrupt/short final sectors and sealed read-only retry passed");
    }

    private static void TestAutomaticReconnect()
    {
        const long first = 12_254;
        var request = Request(_ => DataContent(1000));
        List<OpticalBurnProgress> events = [];
        var tracker = new CdExtraProgressTracker(events.Add, request.AudioBytes, 1000 * 2048);
        var disc = new FakeTaoRecorder { TaoDataTypes = 0, DataTimeoutAfterBlocks = 7, TimeoutOffset = 64, RepeatedTimeout = true };
        int opens = 0, starts = 0, waits = 0;
        bool disposed = false;
        var lost = new RecorderLink(disc, dropOnTimeout: true);
        CdExtraBurnResult result = CdExtraReconnect.Burn(request, tracker, default, () =>
        {
            starts++;
            try { using var session = Session(lost, tracker); return WindowsImapiBurner.BurnCdExtra(session, request, tracker, default); }
            finally { disposed = true; }
        }, use =>
        {
            Require(disposed && lost.StaleCalls > 0, "old connection was unusable and disposed before reconnect");
            opens++;
            if (opens == 1) throw new IOException("recorder unplugged", unchecked((int)0x8007048f));
            if (opens == 2) throw new UnauthorizedAccessException("recorder still locked");
            int priorWrites = disc.Writes.Count;
            bool disconnectAgain = opens == 3;
            if (disconnectAgain) disc.TimeoutOffset = 135;
            else disc.DataTimeoutAfterBlocks = null;
            using var session = Session(new RecorderLink(disc, dropOnTimeout: disconnectAgain), tracker);
            try { use(session); }
            finally
            {
                Require(disc.Writes[priorWrites].Lba == first + (disconnectAgain ? 71 : 142),
                    "fresh handle starts exactly at authoritative NWA after a partial WRITE");
            }
        }, (_, _) => waits++);
        Require(starts == 1 && opens == 4 && waits == 4 && disc.InjectedTimeouts == 2
            && disc.DiscStatus == 2 && result.VerifiedPictureSectors == 1000 && result.AudioBytesWritten == request.AudioBytes
            && result.DataBytesWritten == 1000 * 2048 && disc.Tracks.Count == 3 && disc.TrackData[2].SequenceEqual(DataBytes(1000))
            && events.Any(e => e.Stage == "reconnect") && events.Any(e => e.Stage == "write" && e.CompletedBytes == request.AudioBytes + 142 * 2048)
            && events[^1].Stage == "complete", "one job survived two lost connections and verified the finished disc");

        var sealedDisc = new FakeTaoRecorder();
        int sealedOpens = 0;
        var sealedResult = CdExtraReconnect.Burn(request, tracker, default, () =>
        {
            using var firstHandle = Session(new RecorderLink(sealedDisc, dropOnSealedRead: true), tracker);
            return WindowsImapiBurner.BurnCdExtra(firstHandle, request, tracker, default);
        }, use =>
        {
            sealedOpens++;
            int writes = sealedDisc.Writes.Count, closes = sealedDisc.Log.Count(s => s.StartsWith("close:"));
            using var recovered = Session(sealedDisc, tracker);
            use(recovered);
            Require(writes == sealedDisc.Writes.Count && closes == sealedDisc.Log.Count(s => s.StartsWith("close:")),
                "lost connection after finalization recovers by reading only");
        }, (_, _) => {});
        Require(sealedOpens == 1 && sealedResult.VerifiedPictureSectors == 1000, "verified on the reopened sealed disc");

        foreach (string mode in new[] { "cancel", "offline", "same-nwa", "wrong-disc", "corrupt-prefix", "fatal" })
        {
            var medium = new FakeTaoRecorder { DataTimeoutAfterBlocks = mode == "same-nwa" ? 0 : 7,
                TimeoutOffset = 32, RepeatedTimeout = true, CorruptReadback = mode == "corrupt-prefix" };
            int reconnects = 0;
            using var cancel = new CancellationTokenSource();
            var job = request with { RecoveryRoot = Path.Combine(Scratch, mode) };
            Exception? stopped = null;
            try
            {
                CdExtraReconnect.Burn(job, tracker, cancel.Token, () =>
                {
                    using var session = Session(new RecorderLink(medium, fatalWrite: mode == "fatal"), tracker);
                    return WindowsImapiBurner.BurnCdExtra(session, job, tracker, cancel.Token);
                }, use =>
                {
                    reconnects++;
                    if (mode == "offline") throw new IOException("disconnected", unchecked((int)0x8007048f));
                    if (mode == "wrong-disc") medium.DiscId = 5;
                    using var session = Session(medium, tracker);
                    use(session);
                }, (_, _) => { if (mode == "cancel") cancel.Cancel(); });
            }
            catch (Exception e) { stopped = e; }
            if (stopped is null) throw new InvalidOperationException(mode + " incorrectly reported success");
            Require(medium.DiscStatus == 1 && !medium.Log.Contains("close:ms0")
                && Directory.GetFiles(job.RecoveryRoot!, CdExtraCheckpoint.FileName, SearchOption.AllDirectories).Length == 1,
                mode + " leaves the checkpoint and incomplete disc open");
            Require(mode switch
            {
                "cancel" => stopped is OperationCanceledException && reconnects == 0,
                "offline" => reconnects == CdExtraReconnect.MaxUnavailablePolls,
                "same-nwa" => reconnects == 4 && medium.InjectedTimeouts == 12 && stopped.Message.Contains("同一位置"),
                "wrong-disc" => reconnects == 1 && medium.InjectedTimeouts == 3 && stopped.Message.Contains("标识"),
                "corrupt-prefix" => reconnects == 0 && medium.InjectedTimeouts == 1,
                _ => reconnects == 0 && stopped.Message.Contains("0xC0AA0301"),
            }, mode + " handles only the eligible failure and has a bounded stop");
        }
        Console.WriteLine("cd-extra-auto-reconnect: one burn, two stale device handles, temporarily missing/locked recorder, " +
            "partial writes +71/+142, complete readback; sealed read-only, cancel, unavailable, unchanged NWA, wrong disc, corrupt prefix and fatal error passed");
    }

    private static void TestUnexpectedWriteResponse()
    {
        const long start = 12_254;
        const int hr = CdExtraWriteWindow.UnexpectedResponse;
        var request = Request(_ => DataContent(1000));
        var tracker = new CdExtraProgressTracker(_ => { }, request.AudioBytes, 1000 * 2048);
        var checkpoint = new CdExtraCheckpoint(1, "fake", 9, 40_000, [0], 1, null, start, 1000, new string('a', 64));
        CdExtraWriteWindow? Classify(Exception cause, int blockBytes = 2048)
            => CdExtraWriteWindow.FromCommand(new CdTaoWriteException(start, 32, blockBytes, cause), checkpoint);
        var raw = new System.Runtime.InteropServices.COMException("write response", hr);
        Require(Classify(raw) == new CdExtraWriteWindow(0x2a, start, start + 32) && Classify(raw, 2352) is null,
            "direct COM write error belongs only to a picture command");
        foreach (int key in new[] { 3, 4, 5, 7, 0x0d })
            Require(Classify(new MmcCommandException(0x2a, Sense(key, 0x0c, 0) with { HResult = hr })) is null,
                "definitive SCSI sense " + key);
        var deferred = Sense(0x0b, 0, 0) with { HResult = hr };
        deferred.Sense[0] = 0x71;
        var failedRead = new MmcCommandException(0x28, new(hr, [], []));
        Require(Classify(new MmcCommandException(0x2a, deferred)) is null && Classify(failedRead) is null &&
            Classify(new IOException("timeout reconciliation failed", failedRead)) is null, "deferred sense and read probes excluded");
        foreach (string mode in new[] { "zero", "partial", "whole", "final", "again", "stalled", "outside-high",
            "outside-low", "missing-nwa", "damaged", "mismatch", "swapped", "read-error", "cancel" })
        {
            int offset = mode switch { "zero" => 0, "final" => 992, _ => 64 };
            int accepted = mode switch { "zero" or "stalled" => 0, "whole" => 32, "final" => 8, _ => 7 };
            int opens = 0, firstWrites = 0;
            var disc = new FakeTaoRecorder { DataTimeoutAfterBlocks = accepted, TimeoutOffset = offset,
                InjectedWriteHResult = hr, RepeatedTimeout = mode is "stalled" or "again",
                InvalidRecoveryNwa = mode == "missing-nwa", CorruptReadback = mode == "mismatch" };
            using var cancel = new CancellationTokenSource();
            var job = request with { RecoveryRoot = Path.Combine(Scratch, "unexpected-" + mode) };
            CdExtraBurnResult? result = null;
            Exception? stopped = null;
            string Record() => Directory.GetFiles(job.RecoveryRoot!, CdExtraCheckpoint.FileName, SearchOption.AllDirectories).Single();
            try
            {
                result = CdExtraReconnect.Burn(job, tracker, cancel.Token, () =>
                {
                    try { using var session = Session(disc, tracker); return WindowsImapiBurner.BurnCdExtra(session, job, tracker, cancel.Token); }
                    finally { firstWrites = disc.Writes.Count; }
                }, use =>
                {
                    opens++;
                    long expectedStart = start + (mode == "again" && opens > 1 ? 103 : offset);
                    var saved = CdExtraCheckpoint.Load(Record()).UncertainCommand;
                    Require(saved == new CdExtraWriteWindow(0x2a, expectedStart,
                        expectedStart + Math.Min(32, (int)(start + 1000 - expectedStart))), "exact failed WRITE persisted: " + mode);
                    if (mode == "partial" && opens == 1) throw new IOException("missing drive", unchecked((int)0x8007048f));
                    if (mode == "again") disc.TimeoutOffset = opens == 1 ? 103 : 2000;
                    if (mode == "swapped") disc.DiscId = 5;
                    using var session = Session(new CommandView(disc, (cdb, execute) =>
                    {
                        if (mode == "read-error" && cdb[0] == 0x28) return new(hr, [], []);
                        var value = execute();
                        if (cdb[0] == 0x52 && value.Data[3] == 2)
                        {
                            if (mode is "outside-high" or "outside-low")
                                BinaryPrimitives.WriteUInt32BigEndian(value.Data.AsSpan(12), (uint)(expectedStart + (mode == "outside-high" ? 33 : -1)));
                            if (mode == "damaged") value.Data[5] |= 0x20;
                        }
                        return value;
                    }), tracker);
                    int before = disc.Writes.Count;
                    try { use(session); }
                    finally
                    {
                        if (disc.Writes.Count > before)
                            Require(disc.Writes[before].Lba == expectedStart + accepted, "first fresh WRITE at verified NWA: " + mode);
                    }
                }, (_, _) => { if (mode == "cancel") cancel.Cancel(); });
            }
            catch (Exception e) { stopped = e; }
            if (mode is "zero" or "partial" or "whole" or "final" or "again")
            {
                Require(stopped is null && result?.VerifiedPictureSectors == 1000 && disc.DiscStatus == 2 &&
                    opens == (mode is "partial" or "again" ? 2 : 1) && disc.TrackData[2].SequenceEqual(DataBytes(1000)),
                    mode + " complete: " + stopped);
                Require(mode != "final" || disc.Writes.Count == firstWrites, "last WRITE already accepted");
                Require(CdExtraCheckpoint.Load(Record()).UncertainCommand is null, "verified writable position consumed");
                continue;
            }
            string reason = mode switch { "stalled" => "重试", "outside-high" or "outside-low" => "允许的范围",
                "missing-nwa" => "可续写地址", "damaged" => "损坏", "swapped" => "标识", "mismatch" => "LBA",
                "read-error" => "0x28", _ => "取消" };
            Require(stopped?.Message.Contains(reason) == true && disc.DiscStatus == 1 && disc.Writes.Count == firstWrites
                && !disc.Log.Contains("close:ms0") && disc.InjectedTimeouts == (mode == "stalled" ? 2 : 1)
                && opens == (mode == "stalled" ? 2 : mode == "cancel" ? 0 : 1)
                && CdExtraCheckpoint.Load(Record()).UncertainCommand?.FirstLba == start + offset,
                mode + " must stop and retain bounded checkpoint: " + stopped);
            if (mode == "outside-low")
            {
                using var manual = Session(new CommandView(disc, (cdb, execute) =>
                {
                    var value = execute();
                    if (cdb[0] == 0x52 && value.Data[3] == 2)
                        BinaryPrimitives.WriteUInt32BigEndian(value.Data.AsSpan(12), (uint)(start + offset - 1));
                    return value;
                }), tracker);
                Throws<IOException>(() => manual.ResumePicture("fake", Record(), tracker, default), "reloaded interval still enforced");
                Require(disc.Writes.Count == firstWrites, "rejected manual retry sends no WRITE");
            }
        }
        Console.WriteLine("cd-extra-unexpected-write: 0xC0AA0301, zero/partial/full/final WRITE, repeated errors, missing drive, " +
            "bounds after restart, stalled, damaged, wrong disc, invalid NWA, mismatch, read error and cancellation passed");
    }

    private static void TestUnexpectedFinalizationResponse()
    {
        const int hr = CdExtraWriteWindow.UnexpectedResponse;
        const long end = 12_254 + 1000;
        var request = Request(_ => DataContent(1000));
        var tracker = new CdExtraProgressTracker(_ => { }, request.AudioBytes, 1000 * 2048);
        foreach (string mode in new[] { "flush", "flush-short", "close", "sealed", "sealed-bad", "read" })
        {
            var job = request with { RecoveryRoot = Path.Combine(Scratch, "control-" + mode) };
            var disc = new FakeTaoRecorder();
            int opens = 0, writes = 0, closes = 0;
            var first = new CommandView(disc, (cdb, execute) =>
            {
                if (disc.Writes.Any(w => w.BlockBytes == 2048))
                {
                    if (cdb[0] == 0x35 && mode.StartsWith("flush")) return new(hr, [], []);
                    if (cdb[0] == 0x5b && mode is "close" or "sealed" or "sealed-bad")
                    {
                        if (mode != "close") execute();
                        return new(hr, [], []);
                    }
                    if (cdb[0] == 0x28 && mode == "read") return new(hr, [], []);
                }
                return execute();
            });
            Exception? stopped = null;
            CdExtraBurnResult? result = null;
            try
            {
                result = CdExtraReconnect.Burn(job, tracker, default, () =>
                {
                    try { using var session = Session(first, tracker); return WindowsImapiBurner.BurnCdExtra(session, job, tracker, default); }
                    finally { writes = disc.Writes.Count; closes = disc.Log.Count(s => s == "close:ms0"); }
                }, use =>
                {
                    opens++;
                    var saved = CdExtraCheckpoint.Load(Directory.GetFiles(job.RecoveryRoot!, CdExtraCheckpoint.FileName, SearchOption.AllDirectories).Single());
                    Require(saved.UncertainCommand == new CdExtraWriteWindow(mode.StartsWith("flush") ? (byte)0x35 : (byte)0x5b, end, end),
                        "exact end required for finalization");
                    if (mode == "sealed-bad") disc.CorruptFinalLba = end - 1;
                    using var session = Session(new CommandView(disc, (cdb, execute) =>
                    {
                        var value = execute();
                        if (mode == "flush-short" && cdb[0] == 0x52 && value.Data[3] == 2)
                            BinaryPrimitives.WriteUInt32BigEndian(value.Data.AsSpan(12), (uint)(end - 1));
                        return value;
                    }), tracker);
                    use(session);
                }, (_, _) => {});
            }
            catch (Exception e) { stopped = e; }
            Require(opens == (mode == "read" ? 0 : 1) && disc.Writes.Count == writes, "no picture rewrite: " + mode);
            if (mode is "read" or "sealed-bad" or "flush-short")
                Require(stopped is not null && result is null && disc.Log.Count(s => s == "close:ms0") == closes,
                    "failed verification or short flush never retries finalization: " + mode);
            else Require(stopped is null && result?.VerifiedPictureSectors == 1000 && disc.Log.Count(s => s == "close:ms0") == 1,
                "final state verified: " + mode + " " + stopped);
        }
        Console.WriteLine("cd-extra-unexpected-finalization: flush/close, short track, sealed read-only success/failure, unrelated READ(10) passed");
    }

    private sealed class CommandView(FakeTaoRecorder disc, Func<byte[], Func<MmcCommandResult>, MmcCommandResult> executeCommand)
        : IMmcCommandTransport
    {
        public MmcWriteTransferLimits GetWriteTransferLimits() => disc.GetWriteTransferLimits();
        public MmcCommandResult Execute(byte[] cdb, int size, uint timeout)
            => executeCommand(cdb, () => disc.Execute(cdb, size, timeout));
        public MmcCommandResult SendData(byte[] cdb, IntPtr buffer, int size, uint timeout) => disc.SendData(cdb, buffer, size, timeout);
    }

    // A stale handle never recovers. New handles can still reach the same virtual medium.
    private sealed class RecorderLink(FakeTaoRecorder disc, bool dropOnTimeout = false, bool dropOnSealedRead = false,
        bool fatalWrite = false) : IMmcCommandTransport
    {
        private bool _offline;
        internal int StaleCalls { get; private set; }
        public MmcWriteTransferLimits GetWriteTransferLimits() => disc.GetWriteTransferLimits();
        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            if (dropOnSealedRead && disc.DiscStatus == 2 && cdb[0] == 0x28) _offline = true;
            if (!_offline) return disc.Execute(cdb, allocationLength, timeoutSeconds);
            StaleCalls++;
            return new(unchecked((int)0x8007048f), [], []);
        }
        public MmcCommandResult SendData(byte[] cdb, IntPtr buffer, int bufferLength, uint timeoutSeconds)
        {
            if (_offline) return new(unchecked((int)0x8007048f), [], []);
            if (fatalWrite && cdb[0] == 0x2a && Lba(cdb) >= 12_254)
                return Sense(3, 0x0c, 0) with { HResult = CdExtraWriteWindow.UnexpectedResponse };
            var response = disc.SendData(cdb, buffer, bufferLength, timeoutSeconds);
            if (dropOnTimeout && (uint)response.HResult == 0xc0aa020d) _offline = true;
            return response;
        }
    }

    private static long ResumePicture(this DynamicCdExtraSession session, string deviceId, string path,
        CdExtraProgressTracker tracker, CancellationToken token)
        => CdExtraReconnect.Resume(CdExtraCheckpoint.Load(path), path, deviceId, tracker, token, use => use(session)).Sectors;

    private static DynamicCdExtraSession Session(IMmcCommandTransport recorder, CdExtraProgressTracker? tracker)
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
            createData, 300 + 152 + 400 + 2 + 11_400, 1_000, new OpticalWriteSpeed(1200, false), TestWrite: testWrite)
        { RecoveryRoot = Path.Combine(Scratch, "other") };

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

        internal ushort TaoDataTypes { get; init; } = 0x0101;
        internal bool RejectMode1 { get; init; }
        internal bool IgnoreAudioSelection { get; init; }
        internal bool IgnoreTestBit { get; init; }
        internal long LeadoutStart { get; init; } = 40_000;
        internal long Session2Offset { get; init; }
        /// <summary>State of Last Session after a multi-session close: 0 (the tested BU20N)
        /// or 3 (complete, as cdrecord and libburn name it).</summary>
        internal int LastSessionStateAfterClose { get; init; }
        internal long? Session2FreeBlocks { get; init; }
        internal long? Session2FreeBlocksOnSecondRead { get; init; }
        internal bool ShiftNwaAfterFirstSession2Read { get; init; }
        internal int? DataTimeoutAfterBlocks { get; set; }
        internal int InjectedWriteHResult { get; init; } = unchecked((int)0xc0aa020d);
        internal int TimeoutOffset { get; set; }
        internal uint? DiscId { get; set; } = 0x11223344;
        internal long? CorruptFinalLba { get; set; }
        internal long? ShortFinalLba { get; set; }
        internal bool RepeatedTimeout { get; init; }
        internal bool CorruptReadback { get; set; }
        internal bool InvalidRecoveryNwa { get; init; }
        internal int InjectedTimeouts { get; private set; }
        internal int PictureReadbacks { get; private set; }
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
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14), TaoDataTypes);
                    return new(0, new byte[18], data);
                }
                case 0x51:
                {
                    byte[] data = new byte[34];
                    BinaryPrimitives.WriteUInt16BigEndian(data, 32);
                    data[2] = (byte)((_lastSessionState << 2) | DiscStatus);
                    data[3] = 1;
                    if (DiscId.HasValue) { data[7] = 0x80; BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), DiscId.Value); }
                    data[4] = (byte)_session;
                    data[5] = (byte)_firstTrackInSession;
                    data[6] = (byte)(Tracks.Count + (DiscStatus == 2 ? 0 : 1));
                    return new(0, new byte[18], data);
                }
                case 0xbe:
                {
                    long lba = Lba(cdb);
                    int n = Tracks.FindIndex(t => t.Session == 1 && lba >= t.Start && lba < t.Start + t.Length);
                    if (n < 0 || cdb[1] != 4 || cdb[8] != 1 || cdb[9] != 0x10) return Sense(5, 0x24, 0);
                    return new(0, [], TrackData[n].AsSpan((int)(lba - Tracks[n].Start) * 2352, 2352).ToArray());
                }
                case 0x28:
                {
                    long lba = Lba(cdb);
                    int written = Tracks.FindIndex(t => t.Session == 2 && lba >= t.Start && lba < t.Start + t.Length);
                    byte[] stored;
                    if (written >= 0)
                    {
                        int offset = checked((int)(lba - Tracks[written].Start) * 2048);
                        stored = TrackData[written].AsSpan(offset, allocationLength).ToArray();
                        if (ShortFinalLba is long shortLba && shortLba >= lba && shortLba < lba + Blocks(cdb))
                            return new(0, [], stored[..^1]);
                        if (CorruptFinalLba is long bad && bad >= lba && bad < lba + Blocks(cdb))
                            stored[(int)(bad - lba) * 2048 + 17] ^= 1;
                    }
                    else
                    {
                        PictureReadbacks++;
                        int offset = checked((int)(lba - _openStart!.Value) * 2048);
                        stored = _openData.SelectMany(chunk => chunk).Skip(offset).Take(allocationLength).ToArray();
                        if (CorruptReadback) stored[0] ^= 1;
                    }
                    return new(0, [], stored);
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
                if (RejectMode1 && (data[12] & 15) == 8) return Sense(5, 0x26, 0);
                if (IgnoreAudioSelection && (data[12] & 15) == 0) return Good();
                data.AsSpan(8, _page.Length).CopyTo(_page);
                if (IgnoreTestBit) _page[2] &= 0xef;
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
            bool inject = BlockBytes == 2048 && DataTimeoutAfterBlocks.HasValue && lba - _nwa >= TimeoutOffset
                && (InjectedTimeouts == 0 || RepeatedTimeout);
            if (inject)
            {
                InjectedTimeouts++;
                blocks = Math.Min(blocks, DataTimeoutAfterBlocks!.Value);
                if (blocks == 0) return new(InjectedWriteHResult, [], []);
                data = data[..(blocks * BlockBytes)];
            }
            _openStart ??= lba;
            _writePointer = lba + blocks;
            _openData.Add(data);
            Writes.Add((lba, blocks, BlockBytes));
            return inject ? new(InjectedWriteHResult, [], []) : Good();
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
                data[5] = _session == 2 && _openStart.HasValue ? (byte)4 : (byte)0;
                data[6] = _openStart.HasValue ? (byte)1 : (byte)0x40;
                data[7] = InvalidRecoveryNwa && InjectedTimeouts > 0 ? (byte)0 : (byte)1;
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)_nwa);
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), (uint)(_openStart.HasValue ? _writePointer : _nwa));
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)free);
            }
            else
            {
                (long start, long length, int session) = Tracks[track - 1];
                data[3] = (byte)session;
                data[5] = session == 2 ? (byte)4 : (byte)0;
                data[6] = session == 2 ? (byte)1 : (byte)0;
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

        internal bool RejectFinalClose { get; set; }

        private MmcCommandResult Close(byte[] cdb)
        {
            if ((cdb[2] & 7) != 2) return Sense(5, 0x24, 0);
            Log.Add($"close:ms{MultiSession}");
            if (TestWrite || Tracks.Count == 0 || MultiSession == 0 && RejectFinalClose) return Sense(5, 0x2c, 0);
            if (MultiSession == 3)
            {
                long leadout = Tracks[^1].Start + Tracks[^1].Length + 2;
                _nwa = leadout + 6_750 + 4_500 + 150 + Session2Offset;
                _session++;
                _firstTrackInSession = Tracks.Count + 1;
                _lastSessionState = LastSessionStateAfterClose;
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
        public void ConfirmTaoWriteFormats(bool testWrite) => Steps.Add("confirm-formats");
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
        public (uint? DiscId, string? Audio) ReadPictureIdentity(IReadOnlyList<long> audioStarts) => (1234, null);
        public void VerifyPicture(Stream source, long start, long sectors, Action<long> progress, CancellationToken token)
        {
            source.Position = 0;
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            Require(Data!.SequenceEqual(copy.ToArray()), "verify the recorded picture against the pinned source");
            progress(sectors);
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
