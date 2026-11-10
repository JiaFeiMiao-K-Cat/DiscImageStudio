using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Iso;

/// <summary>CD-Extra planning and the picture session image: Track-at-Once addresses, music
/// import, the absolute-LBA ISO volume, on-disc palette bytes at whole-disc coordinates,
/// the sidecar, the read-back preview and the command line.</summary>
internal static class CdExtraLayoutTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cd-extra-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestLayoutArithmetic();
            TestMusicFiles(directory);
            TestIsoBias(directory);
            TestSessionImage(directory);
            TestExportAndCli(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("cd-extra-layout: passed (TAO addresses, capacity, area shares, WAV import incl. extensible PCM, pinning, folder order, absolute ISO extents with files, palette parity with whole-disc drawing, sidecar, read-back, CLI incl. -msinfo input)");
    }

    private static void TestLayoutArithmetic()
    {
        CdDiscParameters parameters = new(24.5, 56.8, 359_849, UsableSectors: 359_847);
        long[] tracks = [13_500, 9_000, 300];
        CdExtraLayout layout = CdExtraLayout.Plan(parameters, tracks);
        Require(layout.Tracks.Select(track => track.StartLba).SequenceEqual(new long[] { 0, 13_652, 22_804 }),
            "Track-at-Once track starts follow two run-out blocks and a 150-block pause");
        Require(layout.MusicEndLbaExclusive == 23_104 && layout.Session1LeadoutLba == 23_106,
            "session 1 lead-out starts two run-out blocks after the music");
        Require(layout.Session2StartLba == 34_506 && !layout.Session2StartMeasured
                && CdExtraLayout.PredictSession2StartLba(tracks) == 34_506,
            "session 2 follows the 6,750 + 4,500 + 150 block gap");
        Require(layout.DataEndLbaExclusive == 359_847 && layout.DataTrackSectors == 325_341,
            "the data track runs to the usable end two run-out blocks before the lead-out");
        // Constant pitch: r^2 is linear in the LBA, so area shares equal sector shares.
        Near(layout.MusicAreaFraction, 23_104.0 / 359_847, 1e-9, "music area share");
        Near(layout.SessionGapAreaFraction, (34_506.0 - 23_104) / 359_847, 1e-9, "gap area share");
        Near(layout.PictureAreaFraction, (359_847.0 - 34_506) / 359_847, 1e-9, "picture area share");
        Near(layout.MusicOuterRadiusMm, parameters.RadiusFromByte(23_104L * 2352), 1e-12, "music ring radius");
        Near(layout.PictureInnerRadiusMm, Math.Sqrt(24.5 * 24.5 + 34_506.0 / 359_849 * (56.8 * 56.8 - 24.5 * 24.5)),
            1e-9, "picture ring radius from the area formula");

        CdExtraLayout measured = CdExtraLayout.Plan(parameters, tracks, 34_508, 300_000);
        Require(measured.Session2StartMeasured && measured.Session2StartLba == 34_508
                && measured.DataEndLbaExclusive == 334_508,
            "a measured address and the drive's free blocks replace the prediction");
        CdExtraLayout limitedByDisc = CdExtraLayout.Plan(parameters with { UsableSectors = null }, tracks);
        Require(limitedByDisc.DataEndLbaExclusive == 359_847, "without a usable limit the run-out still fits before the lead-out");

        Throws<CdExtraCapacityException>(() => CdExtraLayout.Plan(parameters, [348_000]), "music without room for a picture");
        Throws<ArgumentOutOfRangeException>(() => CdExtraLayout.Plan(parameters, [299]), "tracks shorter than four seconds");
        Throws<ArgumentOutOfRangeException>(() => CdExtraLayout.Plan(parameters, []), "no tracks");
        Throws<ArgumentOutOfRangeException>(() => CdExtraLayout.Plan(parameters, Enumerable.Repeat(300L, 99).ToArray()),
            "a data track must remain within 99 tracks");
        Throws<ArgumentOutOfRangeException>(() => CdExtraLayout.Plan(parameters, tracks, 23_106),
            "session 2 cannot start inside session 1");
        Throws<ArgumentOutOfRangeException>(() => CdExtraLayout.Plan(parameters, [300], 702),
            "a mistyped measured start before the music, lead-out and lead-in");
        Require(CdExtraLayout.Plan(parameters, [300], 300 + 6_750 + 4_500).Session2StartLba == CdExtraLayout.MinimumSession2StartLba,
            "the earliest physically possible session 2 start is accepted");
    }

    private static void TestMusicFiles(string directory)
    {
        string music = Path.Combine(directory, "music");
        Directory.CreateDirectory(music);
        string shortTrack = Path.Combine(music, "b-short.wav");
        byte[] shortPcm = WriteWave(shortTrack, frames: 588 * 10 + 7, seed: 3);
        string longTrack = Path.Combine(music, "a-long.wav");
        byte[] longPcm = WriteWave(longTrack, frames: 588 * 400 + 1, seed: 11);
        File.WriteAllText(Path.Combine(music, "notes.txt"), "not music");

        CdMusicTrackInfo info = CdMusicFile.Inspect(shortTrack);
        Require(info.PcmBytes == shortPcm.Length && info.PcmSectors == 11 && info.RecordedSectors == 300
                && info.PaddedToMinimumLength && info.PaddingBytes == 300 * 2352 - shortPcm.Length,
            "short tracks are padded to the four-second minimum");
        Near(info.Duration.TotalSeconds, shortPcm.Length / 176_400.0, 1e-7, "duration from PCM bytes (one tick)");
        using (MemoryStream padded = new())
        {
            CdMusicFile.WritePaddedPcm(info, padded);
            byte[] bytes = padded.ToArray();
            Require(bytes.Length == info.RecordedBytes && bytes.AsSpan(0, shortPcm.Length).SequenceEqual(shortPcm)
                    && bytes.AsSpan(shortPcm.Length).IndexOfAnyExcept((byte)0) < 0,
                "PCM is copied verbatim and followed by digital silence");
        }

        CdMusicTrackInfo longInfo = CdMusicFile.Inspect(longTrack);
        Require(longInfo.PcmSectors == 401 && longInfo.RecordedSectors == 401 && !longInfo.PaddedToMinimumLength,
            "longer tracks are rounded up to whole sectors only");
        using (MemoryStream padded = new())
        {
            CdMusicFile.WritePaddedPcm(longInfo, padded);
            Require(padded.Length == 401 * 2352 && padded.ToArray().AsSpan(0, longPcm.Length).SequenceEqual(longPcm),
                "long PCM stays byte-identical");
        }

        WriteWave(shortTrack, frames: 588 * 12, seed: 5);
        File.SetLastWriteTimeUtc(shortTrack, info.LastWriteTimeUtc.AddMinutes(1));
        Throws<IOException>(() => CdMusicFile.WritePaddedPcm(info, Stream.Null), "a replaced music file is not burned");

        string wrongRate = Path.Combine(directory, "48k.wav");
        WriteWave(wrongRate, frames: 4800, seed: 1, sampleRate: 48_000);
        Throws<InvalidDataException>(() => CdMusicFile.Inspect(wrongRate), "non-CD sample rates are rejected, not resampled");
        // Errors name the file, so a CLI run over a folder says which WAV is wrong.
        try { CdMusicFile.Inspect(wrongRate); }
        catch (InvalidDataException exception)
        {
            Require(exception.Message.Contains(wrongRate, StringComparison.OrdinalIgnoreCase), "a format error names the file");
        }
        string missing = Path.Combine(directory, "missing.wav");
        try
        {
            CdMusicFile.Inspect(missing);
            Require(false, "a missing file is refused");
        }
        catch (FileNotFoundException exception)
        {
            Require(exception.Message.Contains(missing, StringComparison.OrdinalIgnoreCase), "a missing file is named");
        }

        // WAVE_FORMAT_EXTENSIBLE with 16 valid bits and the PCM sub-format carries CD audio too.
        string extensible = Path.Combine(directory, "extensible.wav");
        byte[] extensiblePcm = WriteExtensibleWave(extensible, frames: 588 * 3, validBits: 16, pcm: true);
        CdMusicTrackInfo extensibleInfo = CdMusicFile.Inspect(extensible);
        Require(extensibleInfo.PcmBytes == extensiblePcm.Length && extensibleInfo.PcmOffset == 68,
            "an extensible 16-bit stereo PCM WAV is accepted");
        WriteExtensibleWave(extensible, frames: 588, validBits: 16, pcm: false);
        Throws<InvalidDataException>(() => CdMusicFile.Inspect(extensible), "an IEEE-float sub-format");
        WriteExtensibleWave(extensible, frames: 588, validBits: 12, pcm: true);
        Throws<InvalidDataException>(() => CdMusicFile.Inspect(extensible), "fewer than 16 valid bits");

        // Pinned files cannot be edited or deleted while the music session is written, and a
        // file edited after planning is refused before the burn starts.
        CdMusicTrackInfo pinnedInfo = CdMusicFile.Inspect(longTrack);
        using (CdMusicPin pin = CdMusicFile.PinUnchanged([pinnedInfo]))
        {
            Throws<IOException>(() => File.OpenWrite(longTrack).Dispose(), "rewriting a pinned music file");
            Throws<IOException>(() => File.Delete(longTrack), "deleting a pinned music file");
            using MemoryStream copy = new();
            CdMusicFile.WritePaddedPcm(pinnedInfo, copy);
            Require(copy.Length == pinnedInfo.RecordedBytes, "a pinned file is still read for the burn");
            // Share modes do not stop a timestamp change; the pin reads through its handle.
            File.SetLastWriteTimeUtc(longTrack, pinnedInfo.LastWriteTimeUtc.AddSeconds(3));
            using MemoryStream throughPin = new();
            pin.WritePaddedPcm(pinnedInfo, throughPin);
            Require(throughPin.ToArray().AsSpan().SequenceEqual(copy.ToArray()), "the pin reads the planned samples");
            Throws<IOException>(() => CdMusicFile.WritePaddedPcm(pinnedInfo, Stream.Null),
                "without the pin a changed timestamp is still refused");
            File.SetLastWriteTimeUtc(longTrack, pinnedInfo.LastWriteTimeUtc);
        }
        Throws<ObjectDisposedException>(() => CdMusicFile.PinUnchanged([pinnedInfo]).Also(pin => pin.Dispose())
            .WritePaddedPcm(pinnedInfo, Stream.Null), "a released pin");
        using (File.OpenWrite(longTrack)) { }
        File.SetLastWriteTimeUtc(longTrack, pinnedInfo.LastWriteTimeUtc.AddSeconds(5));
        Throws<IOException>(() => CdMusicFile.PinUnchanged([pinnedInfo]).Dispose(), "a file edited after planning");
        File.SetLastWriteTimeUtc(longTrack, pinnedInfo.LastWriteTimeUtc);

        // Folders: .wav in any case, natural order, no hidden or AppleDouble companions.
        string natural = Path.Combine(directory, "natural");
        Directory.CreateDirectory(natural);
        WriteWave(Path.Combine(natural, "10 Outro.wav"), frames: 588, seed: 1);
        WriteWave(Path.Combine(natural, "2 Intro.WAV"), frames: 588, seed: 2);
        File.WriteAllText(Path.Combine(natural, "._2 Intro.WAV"), "AppleDouble");
        string hidden = Path.Combine(natural, "1 hidden.wav");
        WriteWave(hidden, frames: 588, seed: 3);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Require(CdMusicFile.ResolveSource(natural).Select(Path.GetFileName).SequenceEqual(new[] { "2 Intro.WAV", "10 Outro.wav" }),
            "a folder lists .wav in any case, in natural order, without hidden or AppleDouble files");
        // Other audio formats and binary files are refused with a clear reason.
        string flac = Path.Combine(directory, "song.flac");
        File.WriteAllBytes(flac, [0x66, 0x4C, 0x61, 0x43, 0, 0, 0, 0]);
        RequireArgument(() => CdMusicFile.ResolveSource(flac), "convert", "a FLAC file asks for conversion");
        string binary = Path.Combine(directory, "song.bin");
        File.WriteAllBytes(binary, [0x52, 0x00, 0x49, 0x0A]);
        RequireArgument(() => CdMusicFile.ResolveSource(binary), "neither a .wav file nor a text playlist",
            "a binary file is not read as a playlist");
        string huge = Path.Combine(directory, "album.w64");
        using (FileStream stream = File.Create(huge)) stream.SetLength(5L * 1024 * 1024);
        RequireArgument(() => CdMusicFile.ResolveSource(huge), "neither a .wav file nor a text playlist",
            "a large binary file is refused before it is read");
        string utf8List = Path.Combine(directory, "list.m3u8");
        File.WriteAllText(utf8List, "曲目一.wav\n", new System.Text.UTF8Encoding(false));
        Require(CdMusicFile.ResolveSource(utf8List).Single() == Path.Combine(directory, "曲目一.wav"),
            "a UTF-8 .m3u8 resolves non-ASCII names");
        File.WriteAllBytes(utf8List, [0x66, 0xFF, 0x2E, 0x77, 0x61, 0x76]);
        RequireArgument(() => CdMusicFile.ResolveSource(utf8List), "not valid UTF-8", "an .m3u8 must be UTF-8");
        System.Text.Encoding ansi = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(
            System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage) ?? System.Text.Encoding.Latin1;
        const string accented = "Café.wav";
        if (ansi.CodePage != 65001 && ansi.GetString(ansi.GetBytes(accented)) == accented)
        {
            string classic = Path.Combine(directory, "classic.m3u");
            File.WriteAllBytes(classic, ansi.GetBytes("#EXTM3U\r\n" + accented + "\r\n"));
            Require(CdMusicFile.ResolveSource(classic).Single() == Path.Combine(directory, accented),
                "a classic .m3u in the ANSI code page resolves its names");
        }
        Require(CdMusicFile.CompareTrackFileNames(@"D:\a\9 x.wav", @"C:\z\10 x.wav") < 0
                && CdMusicFile.CompareTrackFileNames("B.wav", "a.wav") > 0
                && CdMusicFile.CompareTrackFileNames("track02.wav", "Track2.wav") != 0,
            "natural, case-insensitive file-name order with a stable tie-break");

        Require(CdMusicFile.ResolveSource(music).Select(Path.GetFileName).SequenceEqual(new[] { "a-long.wav", "b-short.wav" }),
            "a music folder lists its WAV files in name order");
        string playlist = Path.Combine(directory, "list.m3u");
        File.WriteAllLines(playlist, ["# album", "music/b-short.wav", "", $"\"{longTrack}\""]);
        Require(CdMusicFile.ResolveSource(playlist).SequenceEqual(new[] { shortTrack, longTrack }),
            "a playlist resolves relative and quoted absolute paths in order");
    }

    private static void TestIsoBias(string directory)
    {
        DateTimeOffset timestamp = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        Iso9660ImageWriter.LayoutPlan unbiased = Iso9660ImageWriter.Plan(null, 400, "CD_EXTRA", timestamp);
        Iso9660ImageWriter.LayoutPlan zero = Iso9660ImageWriter.Plan(null, 400, "CD_EXTRA", timestamp, lbaBias: 0);
        Require(WritePrefix(unbiased).SequenceEqual(WritePrefix(zero)), "a zero bias keeps the single-session bytes");
        Iso9660ImageWriter.LayoutPlan biased = Iso9660ImageWriter.Plan(null, 400, "CD_EXTRA", timestamp, lbaBias: 34_506);
        byte[] iso = WritePrefix(biased);
        foreach (int descriptor in new[] { 16, 17 })
        {
            ReadOnlySpan<byte> sector = iso.AsSpan(descriptor * 2048, 2048);
            Require(sector.Slice(1, 5).SequenceEqual("CD001"u8), "volume descriptor at relative sector " + descriptor);
            Require(BinaryPrimitives.ReadUInt32LittleEndian(sector[80..]) == 34_906
                    && BinaryPrimitives.ReadUInt32BigEndian(sector[84..]) == 34_906,
                "volume space size counts from disc LBA 0 (mkisofs -C convention)");
            uint root = BinaryPrimitives.ReadUInt32LittleEndian(sector[(156 + 2)..]);
            uint pathTable = BinaryPrimitives.ReadUInt32LittleEndian(sector[140..]);
            Require(root >= 34_506 + 19 && root < 34_506 + 25 && pathTable >= 34_506 + 19 && pathTable < 34_506 + 25,
                "root directory and path table point into the session");
            int rootRelative = (int)(root - 34_506);
            uint self = BinaryPrimitives.ReadUInt32LittleEndian(iso.AsSpan(rootRelative * 2048 + 2));
            Require(self == root, "directory records hold absolute extents");
        }

        // Files, a subdirectory and an empty file: every record and both path tables hold
        // absolute LBAs; an empty outer-ring file keeps its extent-0 placeholder.
        string source = Path.Combine(directory, "iso-source");
        Directory.CreateDirectory(Path.Combine(source, "SUB"));
        byte[] payload = Enumerable.Range(0, 5_000).Select(index => (byte)(index * 7 + 1)).ToArray();
        File.WriteAllBytes(Path.Combine(source, "A.BIN"), payload);
        File.WriteAllBytes(Path.Combine(source, "EMPTY.TXT"), []);
        File.WriteAllBytes(Path.Combine(source, "SUB", "B.BIN"), new byte[3_000]);
        const uint bias = 34_506;
        foreach (bool outer in new[] { false, true })
        {
            Iso9660ImageWriter.LayoutPlan plan = Iso9660ImageWriter.Plan(source, 400, "CD_EXTRA", timestamp, outer, bias);
            byte[] image;
            using (MemoryStream stream = new())
            {
                if (outer) Iso9660ImageWriter.WriteOuterRingPrefix(plan, stream, plan.MetadataEndLbaExclusive);
                else Iso9660ImageWriter.WriteSequentialPrefix(plan, stream, plan.DataEndLbaExclusive);
                image = stream.ToArray();
            }
            string layout = outer ? "outer" : "inner";
            bool InSession(uint lba) => lba >= bias && lba < bias + 400;
            ReadOnlySpan<byte> pvd = image.AsSpan(16 * 2048, 2048);
            uint root = BinaryPrimitives.ReadUInt32LittleEndian(pvd[(156 + 2)..]);
            List<(string Name, uint Extent, uint Length, bool Directory)> rootRecords = ReadDirectory(image, root, bias);
            Require(rootRecords[0].Extent == root && rootRecords[1].Extent == root, $"{layout}: '.' and '..' of the root");
            (string _, uint fileExtent, uint fileLength, bool _) = rootRecords.Single(record => record.Name == "A.BIN;1");
            Require(InSession(fileExtent) && fileLength == 5_000, $"{layout}: a file extent inside the session");
            if (!outer)
            {
                Require(image.AsSpan(checked((int)(fileExtent - bias) * 2048), payload.Length).SequenceEqual(payload),
                    "inner: the file data sits at its absolute extent minus the session start");
            }
            uint emptyExtent = rootRecords.Single(record => record.Name == "EMPTY.TXT;1").Extent;
            Require(outer ? emptyExtent == 0 : emptyExtent == 0 || InSession(emptyExtent),
                $"{layout}: an empty file keeps a valid placeholder extent");
            uint sub = rootRecords.Single(record => record.Name == "SUB" && record.Directory).Extent;
            List<(string Name, uint Extent, uint Length, bool Directory)> subRecords = ReadDirectory(image, sub, bias);
            Require(InSession(sub) && subRecords[0].Extent == sub && subRecords[1].Extent == root
                    && InSession(subRecords.Single(record => record.Name == "B.BIN;1").Extent),
                $"{layout}: subdirectory records are absolute");
            uint pathL = BinaryPrimitives.ReadUInt32LittleEndian(pvd[140..]);
            uint pathM = BinaryPrimitives.ReadUInt32BigEndian(pvd[148..]);
            int tableBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd[132..]);
            ReadOnlySpan<byte> tableL = image.AsSpan(checked((int)(pathL - bias) * 2048), tableBytes);
            ReadOnlySpan<byte> tableM = image.AsSpan(checked((int)(pathM - bias) * 2048), tableBytes);
            Require(BinaryPrimitives.ReadUInt32LittleEndian(tableL[2..]) == root
                    && BinaryPrimitives.ReadUInt32BigEndian(tableM[2..]) == root
                    && BinaryPrimitives.ReadUInt32LittleEndian(tableL[(10 + 2)..]) == sub
                    && BinaryPrimitives.ReadUInt32BigEndian(tableM[(10 + 2)..]) == sub,
                $"{layout}: L and M path tables hold the absolute root and subdirectory");
            ReadOnlySpan<byte> svd = image.AsSpan(17 * 2048, 2048);
            uint jolietRoot = BinaryPrimitives.ReadUInt32LittleEndian(svd[(156 + 2)..]);
            Require(InSession(jolietRoot) && ReadDirectory(image, jolietRoot, bias)[0].Extent == jolietRoot,
                $"{layout}: the Joliet tree is absolute too");
        }
    }

    private static List<(string Name, uint Extent, uint Length, bool Directory)> ReadDirectory(byte[] image, uint extent, uint bias)
    {
        List<(string, uint, uint, bool)> records = [];
        ReadOnlySpan<byte> sector = image.AsSpan(checked((int)(extent - bias) * 2048), 2048);
        int offset = 0;
        while (offset < sector.Length && sector[offset] != 0)
        {
            ReadOnlySpan<byte> record = sector.Slice(offset, sector[offset]);
            int nameLength = record[32];
            string name = record[33] is 0 or 1 && nameLength == 1
                ? record[33] == 0 ? "." : ".."
                : System.Text.Encoding.ASCII.GetString(record.Slice(33, nameLength));
            records.Add((name, BinaryPrimitives.ReadUInt32LittleEndian(record[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[10..]), (record[25] & 2) != 0));
            offset += record.Length;
        }
        return records;
    }

    private static void TestSessionImage(string directory)
    {
        string image = Path.Combine(directory, "source.png");
        WriteImage(image);
        // A small disc keeps the whole-disc reference image in memory; the session starts
        // after one 300-sector track as on a real CD-Extra disc. The source-image scale is
        // pinned so the two usable limits sample the same pixels.
        CdDiscParameters parameters = new(24.5, 27.5, 15_000, UsableSectors: 14_998, SourceImageOuterRadiusMm: 27.5);
        CdExtraLayout layout = CdExtraLayout.Plan(parameters, [300]);
        Require(layout.Session2StartLba == 11_702 && layout.DataTrackSectors == 3_296, "small-disc session layout");
        CdDataLayoutPlan session = CdDataImageGenerator.PrepareSessionLayout(parameters, layout.Session2StartLba,
            layout.DataTrackSectors, null, "CD_EXTRA");
        Require(session.IsLaterSession && session.VolumeStartLba == 11_702 && session.TrackSectors == 3_296
                && session.VolumeSectors == 3_146 && session.DrawingStartLba == 11_702 + 25
                && session.DrawingEndLbaExclusive == 14_998,
            "empty session volume: 25 metadata sectors, drawing through the post-gap");
        ToneProfile tone = new(0.75, ToneProfile.TwoLevels, 0);
        byte[] track = Generate(image, parameters, session, tone);
        Require(track.Length == 3_296 * 2048, "session stream length");

        // Whole-disc reference: a single-session data image whose drawing covers the same LBAs.
        string files = Path.Combine(directory, "files");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "a.txt"), "x");
        CdDiscParameters whole = parameters with { UsableSectors = 15_000 };
        CdDataLayoutPlan reference = CdDataImageGenerator.PrepareLayout(files, whole, "CD_REF", filesAtOuterRing: false);
        Require(reference.DrawingStartLba < session.DrawingStartLba, "reference drawing covers the session");
        RangeCaptureStream capture = new((long)session.DrawingStartLba * 2048, (long)session.DrawingEndLbaExclusive * 2048);
        CdDataImageGenerator.GenerateToStream(image, capture, whole, reference, tone: tone);
        byte[] expected = capture.Captured;
        int sessionOffset = checked((int)(session.DrawingStartLba - session.VolumeStartLba) * 2048);
        Require(track.AsSpan(sessionOffset, expected.Length).SequenceEqual(expected),
            "the picture session reproduces the whole-disc drawing at the same absolute LBAs");

        // The recorder scrambles cooked Mode 1 sectors; the stored payload must therefore
        // become palette bytes on the disc.
        byte[] mask = ScrambleMask();
        HashSet<byte> palette = [0x0D, 0xAA];
        for (int sector = (int)(session.DrawingStartLba - session.VolumeStartLba); sector < session.TrackSectors; sector += 97)
        {
            for (int offset = 0; offset < 2048; offset++)
                Require(palette.Contains((byte)(track[sector * 2048 + offset] ^ mask[offset + 4])),
                    "descrambled drawing payload is a palette byte");
        }
    }

    private static void TestExportAndCli(string directory)
    {
        string image = Path.Combine(directory, "source.png");
        string music = Path.Combine(directory, "export-music");
        Directory.CreateDirectory(music);
        WriteWave(Path.Combine(music, "01.wav"), frames: 588 * 300, seed: 7);
        WriteWave(Path.Combine(music, "02.wav"), frames: 588 * 320 + 9, seed: 8);
        CdDiscParameters parameters = new(24.5, 27.5, 15_000, UsableSectors: 14_998);
        string iso = Path.Combine(directory, "out", "cd-extra-session2.iso");
        CdImageRequest request = new(image, iso, parameters, ToneProfile.LegacyBinary,
            MusicTracks: CdMusicFile.ResolveSource(music));
        DiscImageResult result = CdImageGenerator.Generate(request);
        CdExtraLayout layout = CdImageGenerator.PlanCdExtra(parameters, request.MusicTracks!);
        Require(result.OutputPath == iso && new FileInfo(iso).Length == layout.DataTrackSectors * 2048
                && result.Summary?.Contains("CD-Extra", StringComparison.Ordinal) == true,
            "export writes the picture session ISO");
        CdExtraImageMetadata? metadata = CdExtraImageMetadata.TryLoad(iso);
        Require(metadata is not null && metadata.Session2StartLba == layout.Session2StartLba
                && metadata.Tracks.Count == 2 && metadata.Tracks[1].StartLba == 452
                && metadata.MusicEndLbaExclusive == layout.MusicEndLbaExclusive
                && metadata.TrackSectors == layout.DataTrackSectors && metadata.SectorFormat == "mode1",
            "the cd-extra sidecar records both sessions");
        Require(CdDataImageMetadata.TryLoad(iso) is null, "single-session readers ignore the CD-Extra sidecar");
        string preview = Path.Combine(directory, "readback.png");
        CdDataImageGenerator.PreviewTrack(iso, preview, parameters, 256, 512);
        Require(File.Exists(preview), "read-back renders a session ISO at its absolute address");

        string cliIso = Path.Combine(directory, "cli", "session2.iso");
        DiscJobResult cli = new CdDiscModule().ExecuteAsync(new DiscJobRequest("cd-generate",
            ["--input", image, "--output", cliIso, "--music", music, "--r0", "24.5", "--r1", "27.5",
             "--sectors", "15000", "--usable-sectors", "14998"]), null, CancellationToken.None).GetAwaiter().GetResult();
        Require(cli.ExitCode == 0 && File.Exists(cliIso) && CdExtraImageMetadata.TryLoad(cliIso) is { } cliMeta
                && cliMeta.Session2StartLba == layout.Session2StartLba,
            "cd-generate --music writes the same picture session");
        byte[] typed = File.ReadAllBytes(iso);
        byte[] fromCli = File.ReadAllBytes(cliIso);
        int drawing = checked((int)(metadata!.DrawingStartLba - metadata.Session2StartLba) * 2048);
        Require(typed.AsSpan(drawing).SequenceEqual(fromCli.AsSpan(drawing)), "typed API and CLI draw identical sectors");

        string measured = Path.Combine(directory, "cli", "measured.iso");
        new CdDiscModule().ExecuteAsync(new DiscJobRequest("cd-generate",
            ["--input", image, "--output", measured, "--music", music, "--r0", "24.5", "--r1", "27.5",
             "--sectors", "15000", "--usable-sectors", "14998",
             "--session2-start-lba", (layout.Session2StartLba + 3).ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            null, CancellationToken.None).GetAwaiter().GetResult();
        Require(CdExtraImageMetadata.TryLoad(measured) is { Session2StartMeasured: true } m && m.Session2StartLba == layout.Session2StartLba + 3,
            "a measured session address (cdrecord -msinfo) overrides the prediction");
        Throws<ArgumentException>(() => new CdDiscModule().ExecuteAsync(new DiscJobRequest("cd-generate",
                ["--input", image, "--output", measured, "--session2-start-lba", "1"]), null, CancellationToken.None)
            .GetAwaiter().GetResult(), "a session address without music");

        // The literal output of cdrecord -msinfo ("first,second") is accepted; junk is named.
        string msinfo = Path.Combine(directory, "cli", "msinfo.iso");
        new CdDiscModule().ExecuteAsync(new DiscJobRequest("cd-generate",
            ["--input", image, "--output", msinfo, "--music", music, "--r0", "24.5", "--r1", "27.5",
             "--sectors", "15000", "--usable-sectors", "14998",
             "--session2-start-lba", "0," + (layout.Session2StartLba + 5).ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            null, CancellationToken.None).GetAwaiter().GetResult();
        Require(CdExtraImageMetadata.TryLoad(msinfo) is { } fromMsinfo && fromMsinfo.Session2StartLba == layout.Session2StartLba + 5,
            "--session2-start-lba takes the second number of cdrecord -msinfo");
        try
        {
            new CdDiscModule().ExecuteAsync(new DiscJobRequest("cd-generate",
                ["--input", image, "--output", msinfo, "--music", music, "--session2-start-lba", "next"]),
                null, CancellationToken.None).GetAwaiter().GetResult();
            Require(false, "a non-numeric session address is refused");
        }
        catch (ArgumentException exception)
        {
            Require(exception.Message.Contains("msinfo", StringComparison.Ordinal), "the error explains which number is wanted");
        }
        Require(CdExtraLayout.TryParseSession2Start(" 0, 34506 ", out long parsed) && parsed == 34_506
                && CdExtraLayout.TryParseSession2Start("34506", out parsed) && parsed == 34_506
                && CdExtraLayout.TryParseSession2Start("34,506", out parsed) && parsed == 34_506
                && CdExtraLayout.TryParseSession2Start("1,234,567", out parsed) && parsed == 1_234_567
                && CdExtraLayout.TryParseSession2Start("12345,15055", out parsed) && parsed == 15_055
                && CdExtraLayout.TryParseSession2Start("11,550", out parsed) && parsed == 11_550
                && !CdExtraLayout.TryParseSession2Start("11, 702", out _)
                && !CdExtraLayout.TryParseSession2Start("1,1702", out _)
                && !CdExtraLayout.TryParseSession2Start("0,11,702", out _)
                && !CdExtraLayout.TryParseSession2Start("0034,506", out _)
                && !CdExtraLayout.TryParseSession2Start("-5", out _)
                && !CdExtraLayout.TryParseSession2Start("0", out _) && !CdExtraLayout.TryParseSession2Start("", out _),
            "measured session 2 start parsing: msinfo pairs and grouped LBAs");

        // Command metadata and catalog routing of the CD-Extra options.
        CdDiscModule module = new();
        foreach (string command in new[] { "cd-generate", "cd-preview-warp" })
        {
            IReadOnlyList<DiscOptionDefinition> options = module.Descriptor.Commands.First(c => c.Name == command).Options;
            Require(options.Any(o => o.Name == "music" && !o.Required)
                    && options.Any(o => o.Name == "session2-start-lba" && !o.Required
                        && o.ValueType == DiscOptionValueType.String),
                command + " metadata exposes the CD-Extra options; the start accepts the msinfo pair, so it is text");
        }
        DiscModuleCatalog catalog = new([module]);
        string warp = Path.Combine(directory, "cli", "warp.png");
        DiscJobResult previewed = catalog.ExecuteAsync(new DiscJobRequest("cd-preview-warp",
            ["--input", image, "--output", warp, "--music", music, "--r0", "24.5", "--r1", "27.5",
             "--sectors", "15000", "--usable-sectors", "14998", "--size", "128", "--samples-per-sector", "2"]))
            .GetAwaiter().GetResult();
        Require(previewed.Succeeded && new FileInfo(warp).Length > 0
                && previewed.Summary!.Contains("LBA " + layout.Session2StartLba.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal),
            "cd-preview-warp --music renders the picture session and reports its start");
        string routed = Path.Combine(directory, "cli", "routed.iso");
        Require(catalog.ExecuteAsync(new DiscJobRequest("cd-generate",
                    ["--input", image, "--output", routed, "--music", music, "--r0", "24.5", "--r1", "27.5",
                     "--sectors", "15000", "--usable-sectors", "14998"])).GetAwaiter().GetResult().Succeeded
                && CdExtraImageMetadata.TryLoad(routed) is { } routedMeta && routedMeta.Session2StartLba == layout.Session2StartLba,
            "cd-generate --music is routed through the module catalog");
    }

    private static byte[] WritePrefix(Iso9660ImageWriter.LayoutPlan plan)
    {
        using MemoryStream bytes = new();
        Iso9660ImageWriter.WriteSequentialPrefix(plan, bytes, plan.DataEndLbaExclusive);
        return bytes.ToArray();
    }

    private static byte[] Generate(string image, CdDiscParameters parameters, CdDataLayoutPlan plan, ToneProfile tone)
    {
        using MemoryStream bytes = new();
        CdDataImageGenerator.GenerateToStream(image, bytes, parameters, plan, tone: tone);
        return bytes.ToArray();
    }

    internal static byte[] WriteWave(string path, int frames, int seed, int sampleRate = 44_100)
    {
        byte[] pcm = new byte[frames * 4];
        for (int index = 0; index < pcm.Length; index++)
            pcm[index] = (byte)((index * 31 + seed * 17 + (index >> 7)) & 0xFF);
        using FileStream output = File.Create(path);
        using BinaryWriter writer = new(output);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return pcm;
    }

    internal static byte[] WriteExtensibleWave(string path, int frames, short validBits, bool pcm)
    {
        byte[] samples = new byte[frames * 4];
        for (int index = 0; index < samples.Length; index++) samples[index] = (byte)(index * 13 + 5);
        using FileStream output = File.Create(path);
        using BinaryWriter writer = new(output);
        writer.Write("RIFF"u8);
        writer.Write(60 + samples.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(40);
        writer.Write(unchecked((short)0xFFFE));
        writer.Write((short)2);
        writer.Write(44_100);
        writer.Write(176_400);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write((short)22);
        writer.Write(validBits);
        writer.Write(3);
        writer.Write(pcm ? 1 : 3); // KSDATAFORMAT_SUBTYPE_PCM / _IEEE_FLOAT
        writer.Write(new byte[] { 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        writer.Write("data"u8);
        writer.Write(samples.Length);
        writer.Write(samples);
        return samples;
    }

    internal static void WriteImage(string path)
    {
        const int size = 96;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int index = (y * size + x) * 4;
            byte gray = (byte)((x * 11 + y * 5) & 255);
            pixels[index] = pixels[index + 1] = pixels[index + 2] = gray;
            pixels[index + 3] = 255;
        }
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96,
            PixelFormats.Bgra32, null, pixels, size * 4)));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    // ECMA-130 Annex B, computed independently of the generator's table.
    private static byte[] ScrambleMask()
    {
        byte[] mask = new byte[2340];
        int register = 1;
        for (int index = 0; index < mask.Length; index++)
        {
            int value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                value |= (register & 1) << bit;
                int feedback = (register ^ (register >> 1)) & 1;
                register = (register >> 1) | (feedback << 14);
            }
            mask[index] = (byte)value;
        }
        return mask;
    }

    private sealed class RangeCaptureStream(long start, long end) : Stream
    {
        private readonly MemoryStream _captured = new();
        private long _position;
        internal byte[] Captured => _captured.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            long from = Math.Max(start, _position);
            long to = Math.Min(end, _position + buffer.Length);
            if (from < to) _captured.Write(buffer.Slice((int)(from - _position), (int)(to - from)));
            _position += buffer.Length;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static void RequireArgument(Action action, string fragment, string message)
    {
        try { action(); }
        catch (ArgumentException exception)
        {
            Require(exception.Message.Contains(fragment, StringComparison.Ordinal), message + ": " + exception.Message);
            return;
        }
        Require(false, message);
    }

    private static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }

    private static void Near(double actual, double expected, double tolerance, string message)
        => Require(Math.Abs(actual - expected) <= tolerance, $"{message}: {actual} vs {expected}");

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("CD-Extra layout: expected " + typeof(T).Name + " for " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD-Extra layout: " + message);
    }
}
