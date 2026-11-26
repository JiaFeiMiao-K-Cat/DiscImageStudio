using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;


/// <summary>
/// CD-TEXT: pack encoding against the libburn reference vector, Japanese text, the SAO cue
/// sheet and its TAO-equal addresses, a scripted SAO recorder running the real CD-Extra and
/// audio-CD orchestration (lead-in, pre-gaps, session-2 append, read-back and track order),
/// the fallback when the recorder cannot write CD-TEXT, and the project, burn-package and
/// CUE/CDT round trips. No drive is involved: what only a real recorder can prove is listed
/// in docs/CD-TEXT-hardware-test.md.
/// </summary>
internal static class CdTextTests
{
    internal static void Run()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "dis-cdtext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            TestLibburnVector();
            TestEncoding();
            TestTags(scratch);
            TestLayout();
            TestCdExtraSessionAtOnce();
            TestFallback();
            TestAudioCd();
            TestReadbackReport();
            TestProject(scratch);
            TestPackage();
            TestCueExport(scratch);
        }
        finally { try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } }
        Console.WriteLine("cd-text: packs, Japanese, tags, SAO layout, CD-Extra SAO + session 2, fallback, audio CD, read-back, project/package/CUE passed");
    }

    private static byte[] A(string s) => Encoding.Latin1.GetBytes(s);

    // libburn doc/cdtext.txt, "Example": the 22 packs cdrecord writes for this album.
    private static void TestLibburnVector()
    {
        string[] expected =
        [
            "80 00 00 00 4a 6f 79 66 75 6c 20 4e 69 67 68 74 f0 f7",
            "80 00 01 0c 73 00 53 6f 6e 67 20 6f 66 20 4a 6f 43 1c",
            "80 01 02 0a 79 00 48 75 6d 70 74 79 20 44 75 6d 43 f9",
            "80 02 03 0a 70 74 79 00 4d 65 65 20 4f 77 77 77 24 72",
            "80 03 04 08 77 00 00 00 00 00 00 00 00 00 00 00 6e af",
            "81 00 05 00 00 46 65 6c 69 78 20 61 6e 64 20 54 4d 51",
            "81 01 06 0b 68 65 20 50 75 72 72 73 00 43 61 74 a7 40",
            "81 02 07 03 77 61 6c 6b 20 42 65 61 75 74 69 65 59 80",
            "81 02 08 0f 73 00 4d 69 61 20 4b 69 74 74 65 6e 30 c9",
            "81 03 09 0a 00 00 00 00 00 00 00 00 00 00 00 00 ad 19",
            "82 00 0a 00 00 46 72 69 65 64 72 69 63 68 20 53 70 8f",
            "82 01 0b 0b 63 68 69 6c 6c 65 72 00 4d 6f 74 68 33 43",
            "82 02 0c 04 65 72 20 47 6f 6f 73 65 00 4d 69 61 d6 f5",
            "82 03 0d 03 20 4b 69 74 74 65 6e 00 00 00 00 00 f5 83",
            "8e 00 0e 00 31 32 33 34 35 36 37 38 39 30 31 32 92 3e",
            "8e 00 0f 0c 33 00 58 59 42 4c 47 31 31 30 31 32 c0 2b",
            "8e 01 10 0a 33 34 00 58 59 42 4c 47 31 31 30 30 bb b3",
            "8e 02 11 09 30 30 35 00 58 59 42 4c 47 31 31 30 f3 bf",
            "8e 03 12 08 30 30 30 36 00 00 00 00 00 00 00 00 5b 5c",
            "8f 00 13 00 00 01 03 00 05 05 04 00 00 00 00 00 9b fe",
            "8f 01 14 00 00 00 00 00 00 00 05 03 15 00 00 00 11 0b",
            "8f 02 15 00 00 00 00 00 09 00 00 00 00 00 00 00 da 77",
        ];
        List<(byte, IReadOnlyList<byte[]>)> types =
        [
            (0x80, [A("Joyful Nights"), A("Song of Joy"), A("Humpty Dumpty"), A("Mee Owwww")]),
            (0x81, [A(""), A("Felix and The Purrs"), A("Catwalk Beauties"), A("Mia Kitten")]),
            (0x82, [A(""), A("Friedrich Schiller"), A("Mother Goose"), A("Mia Kitten")]),
            (0x8e, [A("1234567890123"), A("XYBLG1101234"), A("XYBLG1100005"), A("XYBLG1100006")]),
        ];
        byte[] built = CdTextPacks.Build(types, CdTextCharacterCode.Iso8859_1, 1, 3, CdTextPacks.LanguageEnglish);
        Require(built.Length == expected.Length * 18, "libburn example: 22 packs");
        for (int i = 0; i < expected.Length; i++)
        {
            byte[] want = Convert.FromHexString(expected[i].Replace(" ", string.Empty));
            Require(built.AsSpan(i * 18, 18).SequenceEqual(want), $"libburn example pack {i} matches byte for byte");
        }

        byte[] sony = Convert.FromHexString("8F002A000101030006050405070601024865");
        Require(CdTextPacks.HasValidCrc(sony), "the CRC of the Sony example pack is valid");
        byte[] symbols = CdTextPacks.ToSubchannel(sony);
        Require(Convert.ToHexString(symbols) == "2330002A0000040100300006011010050130180100242125",
            "an 18-byte pack becomes 24 six-bit R-W symbols (libburn cookbook)");
        Require(CdTextPacks.FromSubchannel(symbols).SequenceEqual(sony), "subchannel symbols decode back to the pack");
        Require(new CdTextLeadIn(sony).ToSubchannel().SequenceEqual(symbols), "the burner's lead-in conversion is the same");
    }

    private static void TestEncoding()
    {
        CdTextAlbum english = new("Joyful Nights", "United Cat Orchestra",
            [new("Song of Joy", "Felix"), new("Café Noël", "Felix"), new("Mee", "Felix")]);
        CdTextEncoding latin = CdTextPacks.Encode(english);
        CdTextReadResult read = CdTextPacks.Decode(latin.Packs);
        Require(latin.CharacterCode == CdTextCharacterCode.Iso8859_1 && latin.LanguageCode == CdTextPacks.LanguageEnglish
                && latin.Unrepresentable.Count == 0 && CdTextPacks.Compare(english, read).Count == 0
                && read.Album.Tracks[1].Title == "Café Noël" && read.Album.Tracks[2].Performer == "Felix",
            "Latin-1 text round-trips, repeated performers included");

        CdTextAlbum japanese = new("風の谷 ベスト", "久石譲",
            [new("風の谷のナウシカ", "安田成美"), new("ﾅｳｼｶ・レクイエム", "久石譲"), new("Track 3 – 鳥の人", "久石譲")]);
        CdTextEncoding jis = CdTextPacks.Encode(japanese);
        CdTextReadResult jread = CdTextPacks.Decode(jis.Packs);
        Require(jis.CharacterCode == CdTextCharacterCode.MsJis && jis.LanguageCode == CdTextPacks.LanguageJapanese
                && jis.Unrepresentable.Count == 0 && jread.BadCrcPacks == 0
                && CdTextPacks.Compare(japanese, jread).Count == 0
                && jread.Album.Tracks[0].Title == "風の谷のナウシカ" && jread.Album.Title!.StartsWith("風の谷", StringComparison.Ordinal),
            "Japanese titles are written in MS-JIS (language 0x69) and read back unchanged");

        CdTextReadResult swapped = jread with { Album = jread.Album with { Tracks = [jread.Album.Tracks[1], jread.Album.Tracks[0], jread.Album.Tracks[2]] } };
        Require(CdTextPacks.Compare(japanese, swapped).Count >= 2, "a changed track order is reported");
        byte[] corrupt = (byte[])latin.Packs.Clone();
        corrupt[18 + 6] ^= 0x20;
        CdTextReadResult damaged = CdTextPacks.Decode(corrupt);
        Require(damaged.BadCrcPacks == 1, "a damaged pack fails its CRC");

        CdTextEncoding chinese = CdTextPacks.Encode(new CdTextAlbum("这是专辑", null, [new("们的歌")]));
        Require(chinese.Unrepresentable.Count > 0, "Simplified Chinese outside JIS X 0208 is reported, not silently dropped");
        Throws<CdTextTooLongException>(() => CdTextPacks.Encode(new CdTextAlbum(new string('x', 300), null,
            Enumerable.Range(1, 99).Select(i => new CdTextTrack(new string('a', 40) + i, "P" + i)).ToArray())),
            "text beyond one block is refused");
    }

    private static void TestTags(string directory)
    {
        static byte[] Chunk(string id, byte[] data)
        {
            byte[] chunk = new byte[8 + data.Length + (data.Length & 1)];
            Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), data.Length);
            data.CopyTo(chunk, 8);
            return chunk;
        }
        static byte[] Info(string id, string value) => Chunk(id, [.. Encoding.UTF8.GetBytes(value), 0]);
        byte[] list = Chunk("LIST", [.. "INFO"u8, .. Info("INAM", "風の谷のナウシカ"), .. Info("IART", "安田成美"), .. Info("IPRD", "風の谷")]);
        string wav = Path.Combine(directory, "tagged.wav");
        CdExtraLayoutTests.WriteWave(wav, frames: 588 * 10, seed: 1);
        byte[] file = File.ReadAllBytes(wav);
        byte[] tagged = [.. file, .. list];
        BinaryPrimitives.WriteInt32LittleEndian(tagged.AsSpan(4), tagged.Length - 8);
        File.WriteAllBytes(wav, tagged);
        AudioTags tags = AudioTagReader.Read(wav);
        Require(tags.Title == "風の谷のナウシカ" && tags.Artist == "安田成美" && tags.Album == "風の谷",
            "WAV LIST/INFO tags are read as UTF-8");
        Require(AudioTagReader.Read(new MemoryStream([1, 2, 3])).IsEmpty, "an unknown file yields no tags and no exception");
    }

    private static void TestLayout()
    {
        CdSaoLayout layout = new([300, 400], HasCdText: true);
        CdDiscParameters disc = new(24.5, 56.8, 359_849);
        CdExtraLayout tao = CdExtraLayout.Plan(disc, [300, 400]);
        Require(layout.TrackStarts.SequenceEqual(tao.Tracks.Select(t => t.StartLba))
                && layout.LeadoutLba == tao.Session1LeadoutLba,
            "SAO track starts and lead-out equal the TAO plan, so session 2 lands where the design predicted");
        byte[] cue = layout.BuildCueSheet();
        byte[] expectedCue =
        [
            0x01, 0x00, 0x00, 0x41, 0, 0, 0, 0, // lead-in, CD-TEXT in the R-W subcode
            .. Entry(1, 0, -150), .. Entry(1, 1, 0), // track 1: INDEX 00 at 00:00:00, INDEX 01 at 00:02:00
            .. Entry(2, 0, 300), .. Entry(2, 1, 452), // track 2 behind a 152-block silent pre-gap
            .. Entry(0xaa, 1, 854, 0x01), // lead-out
        ];
        Require(cue.SequenceEqual(expectedCue) && Convert.ToHexString(cue, 8, 16) == "01010000000000000101010000000200",
            "cue sheet: lead-in with CD-TEXT, two tracks with 150/152-block silent pre-gaps, lead-out at the TAO address");
        Require(Convert.ToHexString(new CdSaoLayout([300], false).BuildCueSheet())[..16] == "0100000100000000",
            "without CD-TEXT the lead-in entry has no R-W flag");
        Require(new CdSaoLayout([300], true, TrailingSilence: 0).LeadoutLba == 300, "a finalized audio CD has no trailing silence");
        Require(CdSaoWriter.CreateWriteCommand(-11_634, 32).AsSpan(2, 4).SequenceEqual(new byte[] { 0xff, 0xff, 0xd2, 0x8e }),
            "lead-in addresses are sent as 32-bit two's complement");
    }

    private static byte[] Entry(byte track, byte index, long lba, byte form = 0)
    {
        (byte m, byte s, byte f) = CdSaoLayout.Msf(lba);
        return [0x01, track, index, form, 0, m, s, f];
    }

    private static CdTextAlbum Album() => new("Cat Album", "United Cats",
        [new("Song of Joy", "Felix"), new("ナウシカ", "Mia")]);

    private static CdExtraBurnRequest Request(CdTextAlbum? album, bool testWrite = false, Func<string, bool>? confirm = null)
        => new CdExtraBurnRequest("fake",
            [new CdExtraAudioTrack(300, Producer(Pcm(300, 1))), new CdExtraAudioTrack(400, Producer(Pcm(400, 2)))],
            _ => new CdExtraDataContent(1_000, Producer(new byte[1_000 * 2048])),
            300 + 152 + 400 + 2 + 11_400, 1_000, new OpticalWriteSpeed(1200, false), TestWrite: testWrite)
        {
            RecoveryRoot = Path.Combine(Path.GetTempPath(), "dis-cdtext-recovery"),
            CdText = album is null ? null : new CdTextLeadIn(CdTextPacks.Encode(album).Packs),
            ConfirmWithoutCdText = confirm,
        };

    private static void TestCdExtraSessionAtOnce()
    {
        CdTextAlbum album = Album();
        SaoRecorder recorder = new();
        CdExtraBurnRequest request = Request(album);
        CdExtraBurnResult result = WindowsImapiBurner.BurnCdExtra(recorder, request,
            new CdExtraProgressTracker(null, request.AudioBytes, 1_000 * 2048), CancellationToken.None);
        string[] expected =
        [
            "acquire", "media", "speed", "sao?", "lead-in?", "begin-sao:6:False:False", $"lead-in:{recorder.LeadInStart}",
            "silence:-150:150", "audio:0:300", "silence:300:152", "audio:452:400", "silence:852:2", "complete-sao:False",
            "nwa", "data:1000:12258:False", "close:True", "read-cdtext", "release",
        ];
        Require(recorder.Steps.SequenceEqual(expected),
            "SAO session 1 (lead-in, pre-gaps, tracks, closing silence), then the unchanged TAO picture session and finalize: "
            + string.Join(",", recorder.Steps));
        Require(recorder.LeadInBlocks == -150 - recorder.LeadInStart && recorder.Audio[0].SequenceEqual(Pcm(300, 1))
                && recorder.Audio[1].SequenceEqual(Pcm(400, 2)),
            "the whole lead-in carries CD-TEXT and every music byte reaches the recorder");
        Require(result.AudioTrackStarts.SequenceEqual(new long[] { 0, 452 }) && result.Session2StartLba == 12_258,
            "track starts as planned; session 2 is appended at the recorder's measured address");
        Require(result.CdText is { Written: true, ReadbackError: null, ReadbackPacks.Length: > 0 },
            "the CD-TEXT is read back after finalizing");
        CdTextReadResult back = CdTextPacks.Decode(result.CdText!.ReadbackPacks!);
        Require(CdTextPacks.Compare(album, back).Count == 0 && back.Album.Tracks[1].Title == "ナウシカ",
            "text and track order read back from the lead-in match the request");

        SaoRecorder simulated = new();
        CdExtraBurnResult test = WindowsImapiBurner.BurnCdExtra(simulated, Request(album, testWrite: true),
            new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        Require(simulated.Steps.Contains("begin-sao:6:False:True") && !simulated.Steps.Any(s => s.StartsWith("close", StringComparison.Ordinal))
                && test.CdText is { Written: false },
            "a SAO test write closes nothing and reports that no CD-TEXT was written");

        SaoRecorder failing = new() { FailAudio = true };
        InvalidOperationException? failure = null;
        try { WindowsImapiBurner.BurnCdExtra(failing, Request(album), new CdExtraProgressTracker(null, 1, 1), CancellationToken.None); }
        catch (InvalidOperationException exception) { failure = exception; }
        Require(failure is not null && !failing.Steps.Any(s => s.StartsWith("complete", StringComparison.Ordinal) || s == "nwa")
                && failing.Steps[^1] == "release",
            "a failed SAO write never continues to session 2");
    }

    private static void TestFallback()
    {
        // No SAO: the user is asked, and on yes the original TAO sequence runs unchanged.
        string? asked = null;
        SaoRecorder tao = new() { Capability = CdSaoCapability.None };
        CdExtraBurnResult result = WindowsImapiBurner.BurnCdExtra(tao, Request(Album(), confirm: reason => { asked = reason; return true; }),
            new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        int saoQuery = tao.Steps.IndexOf("sao?");
        Require(asked is not null && saoQuery >= 0 && saoQuery < tao.Steps.IndexOf("tao-audio:300")
                && tao.Steps.SkipWhile(step => step != "tao-audio:300").SequenceEqual(new[]
                {
                    "tao-audio:300", "tao-audio:400", "close:False", "nwa", "data:1000:12258:False", "close:True", "release",
                }),
            "without SAO the user is asked first, then the music is written Track-at-Once exactly as before: " + string.Join(",", tao.Steps));
        Require(!tao.Steps.Any(s => s.StartsWith("lead-in:", StringComparison.Ordinal) || s.StartsWith("begin-sao", StringComparison.Ordinal))
                && result.CdText is { Written: false, SkippedReason: not null },
            "the TAO fallback writes no lead-in and reports why the text is missing");

        // The user declines: nothing is written.
        SaoRecorder declined = new() { Capability = CdSaoCapability.None };
        Throws<OperationCanceledException>(() => WindowsImapiBurner.BurnCdExtra(declined, Request(Album(), confirm: _ => false),
            new CdExtraProgressTracker(null, 1, 1), CancellationToken.None), "declining the fallback cancels");
        Require(!declined.Steps.Any(s => s.Contains("audio", StringComparison.Ordinal)) && declined.Steps[^1] == "release",
            "declining writes nothing to the disc");

        // The recorder refuses the cue sheet: still nothing written, the same question.
        SaoRecorder refuses = new() { RefuseCue = true };
        bool askedAgain = false;
        WindowsImapiBurner.BurnCdExtra(refuses, Request(Album(), confirm: _ => askedAgain = true),
            new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        Require(askedAgain && refuses.Steps.Contains("tao-audio:300") && !refuses.Steps.Any(s => s.StartsWith("lead-in:", StringComparison.Ordinal)),
            "a refused cue sheet falls back to TAO on the still blank disc");

        // Without CD-TEXT the SAO code is never consulted.
        SaoRecorder plain = new();
        WindowsImapiBurner.BurnCdExtra(plain, Request(null), new CdExtraProgressTracker(null, 1, 1), CancellationToken.None);
        Require(!plain.Steps.Contains("sao?") && plain.Steps.Contains("tao-audio:300"), "a burn without CD-TEXT is the unchanged TAO burn");
    }

    private static void TestAudioCd()
    {
        CdTextAlbum album = new("Picture", "Disc Image Studio", [new("Artwork", "Disc Image Studio")]);
        OpticalBurnRequest request = new("fake", OpticalBurnMediaKind.CdAudio, 600 * 2352, Producer(Pcm(600, 3)),
            new OpticalWriteSpeed(1200, false))
        { CdText = new CdTextLeadIn(CdTextPacks.Encode(album).Packs) };
        SaoRecorder recorder = new();
        (OpticalBurnResult? result, string? skipped) = WindowsImapiBurner.BurnCdAudioSessionAtOnce(recorder, request,
            new CdExtraProgressTracker(null, request.ContentLength, 0), CancellationToken.None);
        Require(result is not null && skipped is null && recorder.Steps.SequenceEqual(new[]
                {
                    "acquire", "media", "speed", "sao?", "lead-in?", "begin-sao:4:True:False", $"lead-in:{recorder.LeadInStart}",
                    "silence:-150:150", "audio:0:600", "complete-sao:True", "read-cdtext", "release",
                }),
            "picture-only audio CD with CD-TEXT: one SAO track, finalized: " + string.Join(",", recorder.Steps));
        Require(CdTextPacks.Compare(album, CdTextPacks.Decode(result!.CdText!.ReadbackPacks!)).Count == 0,
            "the audio CD's CD-TEXT reads back");

        SaoRecorder none = new() { Capability = CdSaoCapability.None };
        (OpticalBurnResult? fallback, string? reason) = WindowsImapiBurner.BurnCdAudioSessionAtOnce(none,
            request with { ConfirmWithoutCdText = _ => true }, new CdExtraProgressTracker(null, 1, 0), CancellationToken.None);
        Require(fallback is null && reason is not null && !none.Steps.Any(s => s.Contains("audio", StringComparison.Ordinal)),
            "without SAO nothing is written and the caller takes the unchanged IMAPI path");
    }

    private static void TestReadbackReport()
    {
        CdTextAlbum album = Album();
        byte[] packs = CdTextPacks.Encode(album).Packs;
        BurnMilestonesViewModel milestones = new();
        milestones.Begin(cdText: true);
        Require(milestones.Items.Count == 4, "a CD-TEXT burn has a fourth milestone");
        string ok = CdTextReadback.Report(milestones, album, new CdTextBurnOutcome(true, packs, null));
        Require(milestones.Items[3].State == MilestoneState.Done && ok.Contains("曲序正确", StringComparison.Ordinal),
            "a matching read-back completes the CD-TEXT milestone");
        CdTextAlbum swapped = album with { Tracks = [album.Tracks[1], album.Tracks[0]] };
        milestones.Begin(cdText: true);
        CdTextReadback.Report(milestones, swapped, new CdTextBurnOutcome(true, packs, null));
        Require(milestones.Items[3].State == MilestoneState.Failed, "a different track order fails the CD-TEXT milestone");
        milestones.Begin(cdText: true);
        CdTextReadback.Report(milestones, album, new CdTextBurnOutcome(false, null, null, "no SAO"));
        Require(milestones.Items[3].State == MilestoneState.Skipped && milestones.Items[3].Detail == "no SAO",
            "a fallback burn shows why the disc has no text");
        milestones.Begin();
        Require(milestones.Items.Count == 3, "burns without CD-TEXT keep their three milestones");
    }

    private static void TestProject(string directory)
    {
        ProjectDocument plain = new(ProjectJsonStore.CurrentSchemaVersion, "CD-TEXT", ProjectDocument.CdFamily,
            new ProjectGeometry(24.5, 56.8, 359_849), [],
            new ProjectOutputs("D:\\out\\cd.wav", true, string.Empty, "CD_IMAGE", true),
            null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ToneProfile.Photo, null,
            new ProjectCdExtraSettings(true, [new("D:\\music\\01.wav"), new("D:\\music\\02.wav")]));
        Require(ProjectJsonStore.RequiredSchemaVersion(plain) == ProjectJsonStore.CdExtraSchemaVersion,
            "a project without CD-TEXT keeps schema 3");
        DiscParametersState state = new();
        ProjectSession.ApplyToState(plain, state);
        state.CdText = new ProjectCdText(true, "風の谷", "久石譲");
        state.CdTrackTexts = new Dictionary<string, CdTextTrack>(StringComparer.OrdinalIgnoreCase)
        {
            ["D:\\music\\01.wav"] = new("風の谷のナウシカ", "安田成美"),
            ["D:\\music\\02.wav"] = new("鳥の人", null),
        };
        ProjectDocument withText = plain with
        {
            CdExtra = ProjectSession.CaptureCdExtra(plain, state),
            CdText = ProjectSession.CaptureCdText(plain, state),
        };
        string path = Path.Combine(directory, "cdtext.discproj");
        ProjectJsonStore.Save(path, withText);
        JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Require((int)json["schemaVersion"]! == ProjectJsonStore.CdTextSchemaVersion
                && (string?)json["cdText"]!["albumTitle"] == "風の谷"
                && (string?)json["cdExtra"]!["tracks"]![0]!["title"] == "風の谷のナウシカ",
            "CD-TEXT is saved in the .discproj with schema 4");
        DiscParametersState reopened = new();
        ProjectSession.ApplyToState(ProjectJsonStore.Load(path), reopened);
        CdTextAlbum? effective = reopened.EffectiveCdText();
        Require(effective is { Title: "風の谷", Performer: "久石譲" } && effective.Tracks.Count == 2
                && effective.Tracks[0].Title == "風の谷のナウシカ" && effective.Tracks[1].Performer is null,
            "reopening the project restores album and track text in track order");
        reopened.CdMusicTracks = [reopened.CdMusicTracks[1], reopened.CdMusicTracks[0]];
        Require(reopened.EffectiveCdText()!.Tracks[0].Title == "鳥の人", "track text follows a reordered track");
        reopened.CdText = reopened.CdText with { Enabled = false };
        Require(reopened.EffectiveCdText() is null, "switched off, no CD-TEXT reaches export or burn");

        ProjectDocument noText = plain with { CdText = ProjectSession.CaptureCdText(plain, new DiscParametersState()) };
        Require(noText.CdText is null, "an untouched project carries no cdText block");
        Throws<InvalidDataException>(() => ProjectJsonStore.Validate(plain with { DiscFamily = ProjectDocument.DvdFamily, CdExtra = null,
            CdText = new ProjectCdText(true, "x") }), "cdText on a DVD project is rejected");
    }

    private static void TestPackage()
    {
        CdExtraPackage package = new(1, new CdDiscParameters(24.5, 56.8, 359_849), ToneProfile.Photo, "00",
            [new("track-01.wav", "01.flac", "AA", 300), new("track-02.wav", "02.flac", "BB", 400)],
            new CdExtraPackageCdText("風の谷", "久石譲", [new("風の谷のナウシカ", "安田成美"), new("鳥の人", "久石譲")]));
        string json = JsonSerializer.Serialize(package, CdExtraPackage.Json);
        CdExtraPackage back = JsonSerializer.Deserialize<CdExtraPackage>(json, CdExtraPackage.Json)!;
        Require(json.Contains("\"CdText\"", StringComparison.Ordinal) && back.CdText!.Tracks[0].Title == "風の谷のナウシカ"
                && CdTextPacks.Compare(package.CdText!.ToAlbum(), CdTextPacks.Decode(CdTextPacks.Encode(back.CdText.ToAlbum()).Packs)).Count == 0,
            "package.cdextra.json keeps the CD-TEXT");
        string old = JsonSerializer.Serialize(package with { CdText = null }, CdExtraPackage.Json);
        Require(!old.Contains("CdText", StringComparison.Ordinal)
                && JsonSerializer.Deserialize<CdExtraPackage>(old, CdExtraPackage.Json)!.CdText is null,
            "a package without CD-TEXT keeps the earlier manifest shape and loads unchanged");
    }

    private static void TestCueExport(string directory)
    {
        string cue = Path.Combine(directory, "disc.cue");
        File.WriteAllText(cue, "FILE \"disc.wav\" WAVE\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00\r\n");
        CdTextAlbum album = new("Picture \"One\"", "Studio", [new("Artwork", "Studio")]);
        string cdt = CdTextExport.WriteCdt(Path.ChangeExtension(cue, ".cdt"), album);
        CdTextExport.AddToCue(cue, album, cdt);
        string text = File.ReadAllText(cue);
        Require(text.StartsWith("CDTEXTFILE \"disc.cdt\"\r\nTITLE \"Picture 'One'\"\r\nPERFORMER \"Studio\"\r\nFILE \"disc.wav\" WAVE\r\n  TRACK 01 AUDIO\r\n    TITLE \"Artwork\"\r\n",
                StringComparison.Ordinal),
            "the CUE sheet carries disc and track TITLE/PERFORMER and the CDT reference: " + text);
        byte[] file = File.ReadAllBytes(cdt);
        Require(BinaryPrimitives.ReadUInt16BigEndian(file) == file.Length - 2 && (file.Length - 4) % 18 == 0
                && CdTextPacks.Compare(album, CdTextPacks.Decode(file.AsSpan(4))).Count == 0,
            "the .cdt holds the same packs behind a cdrecord length header");
    }

    private static Action<Stream, CancellationToken> Producer(byte[] bytes) => (output, _) => output.Write(bytes);

    private static byte[] Pcm(long sectors, int seed)
        => Enumerable.Range(0, checked((int)(sectors * 2352))).Select(index => (byte)(index * seed + seed)).ToArray();

    /// <summary>A recorder that understands both TAO and SAO. Its lead-in keeps the R-W
    /// symbols the host wrote, and READ TOC format 5 returns the packs a drive would decode
    /// from them, so the read-back reflects what was actually written.</summary>
    private sealed class SaoRecorder : ICdExtraSession
    {
        internal List<string> Steps { get; } = [];
        internal List<byte[]> Audio { get; } = [];
        internal CdSaoCapability Capability { get; init; } = new(true, true, true, true, true, true, 0);
        internal long LeadInStart { get; } = -11_634;
        internal bool RefuseCue { get; init; }
        internal bool FailAudio { get; init; }
        internal long LeadInBlocks { get; private set; }
        private byte[]? _leadInSymbols;
        private bool _finalized;
        private long _nextTao;

        public void Acquire() => Steps.Add("acquire");
        public CdExtraMediaInfo ReadMedia()
        {
            Steps.Add("media");
            return new(0x0009, 0, 0, 359_845, new CdTaoFeature(0x0009, true, true, true, (1 << 0) | (1 << 8)), 52);
        }
        public void ConfirmTaoWriteFormats(bool testWrite) { }
        public OpticalWriteSpeed ConfigureWrite(OpticalWriteSpeed? requested) { Steps.Add("speed"); return requested ?? new(1200, false); }
        public long WriteAudioTrack(IStream pcm, long sectors, bool testWrite)
        {
            Steps.Add($"tao-audio:{sectors}");
            Audio.Add(ReadAll(pcm));
            long start = _nextTao;
            _nextTao += sectors + 152;
            return start;
        }
        public void CloseSession(bool finalize) { Steps.Add($"close:{finalize}"); _finalized |= finalize; }
        public (long NextWritableAddress, long FreeBlocks) ReadNextSessionStart() { Steps.Add("nwa"); return (12_258, 300_000); }
        public long WriteDataTrack(IStream data, long sectors, long expectedStartLba, bool testWrite)
        {
            Steps.Add($"data:{sectors}:{expectedStartLba}:{testWrite}");
            _ = ReadAll(data);
            return expectedStartLba;
        }
        public (uint? DiscId, string? Audio) ReadPictureIdentity(IReadOnlyList<long> audioStarts) => (1234, null);
        public void VerifyPicture(Stream source, long start, long sectors, Action<long> progress, CancellationToken token) => progress(sectors);
        public void Flush() => Steps.Add("flush");
        public void CancelWrite() { }
        public void Release() => Steps.Add("release");
        public void Dispose() { }

        public CdSaoCapability ReadSaoCapability() { Steps.Add("sao?"); return Capability; }
        public long ReadLeadInStart() { Steps.Add("lead-in?"); return LeadInStart; }
        public void BeginSessionAtOnce(byte[] cueSheet, bool finalize, bool testWrite)
        {
            if (RefuseCue) throw new IOException("ILLEGAL REQUEST: invalid cue sheet");
            Steps.Add($"begin-sao:{cueSheet.Length / 8}:{finalize}:{testWrite}");
            if (cueSheet[3] != 0x41) throw new IOException("lead-in entry lacks the CD-TEXT flag");
        }
        public void WriteSaoLeadIn(long leadInStart, CdTextLeadIn cdText)
        {
            Steps.Add($"lead-in:{leadInStart}");
            LeadInBlocks = -150 - leadInStart;
            _leadInSymbols = cdText.ToSubchannel();
        }
        public void WriteSaoSilence(long lba, long sectors) => Steps.Add($"silence:{lba}:{sectors}");
        public void WriteSaoAudio(IStream pcm, long lba, long sectors)
        {
            if (FailAudio) throw new IOException("medium error");
            Steps.Add($"audio:{lba}:{sectors}");
            Audio.Add(ReadAll(pcm));
        }
        public void CompleteSessionAtOnce(bool finalize) { Steps.Add($"complete-sao:{finalize}"); _finalized |= finalize; }
        public byte[] ReadCdTextPacks()
        {
            Steps.Add("read-cdtext");
            return _leadInSymbols is null ? [] : CdTextPacks.FromSubchannel(_leadInSymbols);
        }

        private static byte[] ReadAll(IStream stream)
        {
            stream.Stat(out STATSTG stat, 1);
            byte[] bytes = new byte[stat.cbSize];
            IntPtr read = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    byte[] chunk = new byte[Math.Min(65_536, bytes.Length - offset)];
                    stream.Read(chunk, chunk.Length, read);
                    int count = Marshal.ReadInt32(read);
                    if (count <= 0) break;
                    chunk.AsSpan(0, count).CopyTo(bytes.AsSpan(offset));
                    offset += count;
                }
            }
            finally { Marshal.FreeHGlobal(read); }
            return bytes;
        }
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD-TEXT test failed: " + message);
    }
}
