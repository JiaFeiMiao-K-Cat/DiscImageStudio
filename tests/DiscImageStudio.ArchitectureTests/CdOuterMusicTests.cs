using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;

/// <summary>Music on the outer rings: a single Disc-at-Once audio session whose picture is
/// the index 0 pre-gap of track 1. Covers the layout, the WAV/CUE export, the command line,
/// and the raw image the Windows image creator builds from one sequential content stream
/// (no recorder is involved).</summary>
internal static class CdOuterMusicTests
{
    private const int RawBytes = 2448;
    private const int LeadInSectors = 22_500;
    private const int FirstPregapSectors = 150;

    internal static void Run()
    {
        TestLayout();
        TestSlices();
        TestRawImage();
        TestBurnRequest();
        string directory = Path.Combine(Path.GetTempPath(), "cd-outer-music-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { TestExport(directory); }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("cd-outer-music: passed (layout and area shares, sequential track slices, native DAO image with the picture in the pre-gap of track 1, request validation, WAV/CUE export parity with whole-disc drawing, CLI; no hardware)");
    }

    private static void TestLayout()
    {
        CdDiscParameters parameters = new(24.5, 57.5, 300_000);
        CdExtraLayout layout = CdExtraLayout.PlanOuterMusic(parameters, [18_000, 300, 9_000]);
        Require(layout.MusicAtOuterRing && layout.PictureStartLba == 0 && layout.PictureEndLbaExclusive == 272_700
                && layout.MusicStartLba == 272_700 && layout.MusicEndLbaExclusive == 300_000
                && layout.Tracks.Select(track => track.StartLba).SequenceEqual(new long[] { 272_700, 290_700, 291_000 })
                && layout.MusicSectors == 27_300 && layout.PictureSectors == 272_700,
            "the picture starts at LBA 0 and the gapless music ends at the capacity");
        Require(layout.SessionGapAreaFraction == 0
                && Math.Abs(layout.MusicAreaFraction + layout.PictureAreaFraction - 1) < 1e-9
                && Math.Abs(layout.MusicAreaFraction - 27_300.0 / 300_000) < 1e-6
                && Math.Abs(layout.MusicOuterRadiusMm - 57.5) < 1e-6
                && Math.Abs(layout.PictureInnerRadiusMm - 24.5) < 1e-6
                && Math.Abs(layout.PictureOuterRadiusMm - layout.MusicInnerRadiusMm) < 1e-9,
            "music and picture share the disc without a session gap");

        CdExtraLayout limited = CdExtraLayout.PlanOuterMusic(parameters with { UsableSectors = 200_000 }, [300]);
        Require(limited.MusicEndLbaExclusive == 200_000 && limited.PictureSectors == 199_700,
            "the music ends at the usable capacity of the disc in the drive");
        Require(CdExtraLayout.PlanOuterMusic(parameters, [300_000 - 150]).PictureSectors == 150,
            "a two-second picture is the smallest pre-gap with content");
        Throws<CdExtraCapacityException>(() => CdExtraLayout.PlanOuterMusic(parameters, [300_000 - 149]),
            "music that leaves less than a two-second pre-gap");
        Throws<ArgumentOutOfRangeException>(() => CdExtraLayout.PlanOuterMusic(parameters, [299]), "a track under four seconds");

        // The inner (CD-Extra) layout keeps its meaning.
        CdExtraLayout inner = CdExtraLayout.Plan(parameters, [18_000, 300]);
        Require(!inner.MusicAtOuterRing && inner.MusicStartLba == 0 && inner.PictureStartLba == inner.Session2StartLba
                && inner.DiscEndLbaExclusive == inner.DataEndLbaExclusive && inner.SessionGapAreaFraction > 0,
            "the CD-Extra layout is unchanged");
    }

    private static void TestSlices()
    {
        byte[] bytes = new byte[10_000];
        new Random(5).NextBytes(bytes);
        SequentialSliceSource source = new(new ForwardOnlyContent(bytes));
        IStream first = source.Take(4_000), second = source.Take(6_000);
        Require(source.AssignedBytes == 10_000, "slices are consecutive");
        IntPtr count = Marshal.AllocCoTaskMem(sizeof(long));
        try
        {
            first.Stat(out STATSTG stat, 1);
            Require(stat.cbSize == 4_000, "a slice reports its own length");
            // A size probe (seek to the end and back) must not disturb the sequence.
            first.Seek(0, 2, count);
            Require(Marshal.ReadInt64(count) == 4_000, "seek to the end reports the slice length");
            first.Seek(0, 0, IntPtr.Zero);
            byte[] buffer = new byte[3_000];
            Throws<IOException>(() => second.Read(buffer, buffer.Length, count), "reading the second slice first");
            byte[] collected = new byte[10_000];
            int offset = 0;
            foreach (IStream slice in new[] { first, second })
            {
                while (true)
                {
                    slice.Read(buffer, buffer.Length, count);
                    int read = Marshal.ReadInt32(count);
                    if (read == 0) break;
                    buffer.AsSpan(0, read).CopyTo(collected.AsSpan(offset));
                    offset += read;
                }
            }
            Require(offset == 10_000 && collected.AsSpan().SequenceEqual(bytes),
                "the slices deliver the parent stream in order and stop at their ends");
        }
        finally
        {
            Marshal.FreeCoTaskMem(count);
            source.Release();
        }
    }

    /// <summary>The image the Windows raw CD image creator builds for a pre-gap picture and
    /// two tracks cut from one forward-only stream: content addresses, Q-channel track and
    /// index numbers, and the table of contents.</summary>
    private static void TestRawImage()
    {
        const int pregap = 400, firstTrack = 300, secondTrack = 350;
        const int sectors = pregap + firstTrack + secondTrack;
        byte[] pcm = new byte[sectors * 2352];
        new Random(20261110).NextBytes(pcm);
        foreach (int sectorType in new[] { CdRawSectorType.SubcodeIsCooked, CdRawSectorType.SubcodeIsRaw })
        {
            using DynamicCdRawImageSession session = new();
            IStream image = session.CreateImage(new ForwardOnlyContent(pcm), sectorType,
                new CdAudioDiscLayout(pregap, [firstTrack, secondTrack]));
            Require(session.StartOfLeadout == sectors, "the lead-out follows the last track");
            image.Stat(out STATSTG stat, 1);
            Require(stat.cbSize % RawBytes == 0, "2448-byte raw sectors");
            long total = stat.cbSize / RawBytes;
            Require(total > LeadInSectors + FirstPregapSectors + sectors, "lead-in, first pre-gap, program and lead-out");
            byte[] raw = new byte[RawBytes];
            Dictionary<int, int> toc = [];
            IntPtr count = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                for (long section = 0; section < total; section++)
                {
                    ReadExactly(image, raw, count);
                    long lba = section - LeadInSectors - FirstPregapSectors;
                    byte[] q = DecodeQ(raw);
                    bool position = (q[0] & 0x0f) == 1;
                    if (lba < -FirstPregapSectors)
                    {
                        if (position && q[1] == 0) toc[q[2]] = FromBcd(q[7]) * 4500 + FromBcd(q[8]) * 75 + FromBcd(q[9]);
                        continue;
                    }
                    if (lba < 0)
                    {
                        Require(raw.AsSpan(0, 2352).IndexOfAnyExcept((byte)0) < 0, $"LBA {lba}: the standard pre-gap is silent");
                        if (position) Require(q[1] == 0x01 && q[2] == 0x00, $"LBA {lba}: track 1 index 0");
                        continue;
                    }
                    if (lba >= sectors)
                    {
                        if (position) Require(q[1] == 0xAA, $"LBA {lba}: lead-out");
                        continue;
                    }
                    Require(raw.AsSpan(0, 2352).SequenceEqual(pcm.AsSpan(checked((int)lba * 2352), 2352)),
                        $"type {sectorType}, LBA {lba}: the content keeps its address across the pre-gap and both tracks");
                    if (!position) continue;
                    (int track, int index) = lba < pregap ? (1, 0) : lba < pregap + firstTrack ? (1, 1) : (2, 1);
                    Require(q[1] == track && q[2] == index && (q[0] & 0xf0) == 0,
                        $"LBA {lba}: audio track {track} index {index} in the Q channel");
                    Require(FromBcd(q[7]) * 4500 + FromBcd(q[8]) * 75 + FromBcd(q[9]) == lba + 150,
                        $"LBA {lba}: absolute time");
                }
            }
            finally { Marshal.FreeCoTaskMem(count); }

            Require(toc.TryGetValue(0x01, out int one) && one == pregap + 150
                    && toc.TryGetValue(0x02, out int two) && two == pregap + firstTrack + 150
                    && toc.TryGetValue(0xA2, out int leadOut) && leadOut == sectors + 150,
                "the table of contents starts track 1 behind the picture, so players begin with the music");
        }

        using DynamicCdRawImageSession single = new();
        Require(single.CreateImage(new ForwardOnlyContent(new byte[300 * 2352]), CdRawSectorType.SubcodeIsRaw, null) is not null
                && single.StartOfLeadout == 300, "without a layout the content is one track, as before");
    }

    private static void TestBurnRequest()
    {
        Action<Stream, CancellationToken> produce = (_, _) => { };
        CdAudioDiscLayout layout = new(400, [300, 350]);
        new OpticalBurnRequest("drive", OpticalBurnMediaKind.CdAudio, 1050L * 2352, produce, CdAudioLayout: layout).Validate();
        Throws<ArgumentException>(() => new OpticalBurnRequest("drive", OpticalBurnMediaKind.CdAudio, 1051L * 2352,
            produce, CdAudioLayout: layout).Validate(), "a layout that does not cover the content");
        Throws<ArgumentException>(() => new OpticalBurnRequest("drive", OpticalBurnMediaKind.CdData, 1050L * 2048,
            produce, CdAudioLayout: layout).Validate(), "a track layout on a data burn");
        Throws<ArgumentOutOfRangeException>(() => new CdAudioDiscLayout(149, [300]).Validate(), "a pre-gap under two seconds");
        Throws<ArgumentOutOfRangeException>(() => new CdAudioDiscLayout(400, [299]).Validate(), "a track under four seconds");
        Throws<ArgumentOutOfRangeException>(() => new CdAudioDiscLayout(400, []).Validate(), "no tracks");
        new CdAudioDiscLayout(0, [300]).Validate();
        Require(CdAudioDiscLayout.SectorBytes == CdDiscParameters.BytesPerSector
                && CdAudioDiscLayout.MinimumPregapSectors == CdExtraLayout.MinimumHiddenPictureSectors
                && CdAudioDiscLayout.MinimumTrackSectors == CdExtraLayout.MinimumTrackSectors,
            "Burning and Cd agree on the audio-disc limits");
    }

    private static void TestExport(string directory)
    {
        string image = Path.Combine(directory, "source.png");
        CdExtraLayoutTests.WriteImage(image);
        string music = Path.Combine(directory, "music");
        Directory.CreateDirectory(music);
        string first = Path.Combine(music, "01.wav"), second = Path.Combine(music, "02.wav");
        CdExtraLayoutTests.WriteWave(first, frames: 588 * 320, seed: 3);
        CdExtraLayoutTests.WriteWave(second, frames: 588 * 400 + 100, seed: 4);
        CdDiscParameters parameters = new(24.5, 27.5, 15_000, UsableSectors: 14_998, SourceImageOuterRadiusMm: 27.5);
        ToneProfile tone = new(0.75, ToneProfile.TwoLevels, 0);

        string output = Path.Combine(directory, "disc.wav");
        DiscImageResult result = CdImageGenerator.Generate(new CdImageRequest(image, output, parameters, tone,
            Interleave: false, MusicTracks: [first, second], MusicAtOuterRing: true));
        CdExtraLayout layout = CdImageGenerator.PlanOuterMusic(parameters, [first, second]);
        Require(layout.Tracks.Select(track => track.Sectors).SequenceEqual(new long[] { 320, 401 })
                && layout.MusicEndLbaExclusive == 14_998 && layout.PictureSectors == 14_998 - 721,
            "the layout pads the second track to whole sectors");
        byte[] file = File.ReadAllBytes(output);
        Require(file.Length == 44 + 14_998 * 2352 && result.OutputPath == output, "one WAV holds the picture and the music");

        // The picture part is the whole-disc audio drawing, cut where the music starts.
        string whole = Path.Combine(directory, "whole.wav");
        CdTrackGenerator.Generate(image, whole, parameters, interleave: false, writeCue: false, tone: tone);
        byte[] reference = File.ReadAllBytes(whole);
        int pictureBytes = checked((int)layout.PictureSectors * 2352);
        Require(file.AsSpan(44, pictureBytes).SequenceEqual(reference.AsSpan(44, pictureBytes)),
            "the picture bytes equal whole-disc drawing at the same addresses");
        byte[] firstPcm = File.ReadAllBytes(first)[44..];
        byte[] secondPcm = File.ReadAllBytes(second)[44..];
        Require(file.AsSpan(44 + pictureBytes, firstPcm.Length).SequenceEqual(firstPcm)
                && file.AsSpan(44 + pictureBytes + 320 * 2352, secondPcm.Length).SequenceEqual(secondPcm)
                && file.AsSpan(44 + pictureBytes + 320 * 2352 + secondPcm.Length).IndexOfAnyExcept((byte)0) < 0,
            "the music samples follow unchanged and the last sector is padded with silence");

        string cue = File.ReadAllText(Path.ChangeExtension(output, ".cue"));
        string expected = "FILE \"disc.wav\" WAVE\r\n  TRACK 01 AUDIO\r\n    INDEX 00 00:00:00\r\n    INDEX 01 "
            + Time(layout.Tracks[0].StartLba) + "\r\n  TRACK 02 AUDIO\r\n    INDEX 01 " + Time(layout.Tracks[1].StartLba) + "\r\n";
        Require(cue == expected, "the CUE sheet puts the picture in index 0 of track 1");
        Require(CdTrackMetadata.TryLoad(output) is { UsableSectors: 14_998, Sectors: 15_000 },
            "the sidecar describes the whole recording for the read-back preview");
        string readBack = Path.Combine(directory, "readback.png");
        CdTrackGenerator.PreviewTrack(output, readBack, CdTrackMetadata.TryLoad(output)!.CreateParameters(), 128, 512, false);
        Require(new FileInfo(readBack).Length > 0, "the read-back preview renders the exported file");

        Throws<ArgumentException>(() => CdImageGenerator.Generate(new CdImageRequest(image, Path.Combine(directory, "disc.iso"),
            parameters, tone, MusicTracks: [first], MusicAtOuterRing: true)), "an ISO output for outer music");
        Throws<ArgumentException>(() => CdImageGenerator.Generate(new CdImageRequest(image, Path.Combine(directory, "disc.raw"),
            parameters, tone, MusicTracks: [first], MusicAtOuterRing: true)), "a RAW output for outer music");
        Throws<ArgumentException>(() => CdImageGenerator.Generate(new CdImageRequest(image, output, parameters, tone,
            DataDirectory: music, MusicTracks: [first], MusicAtOuterRing: true)), "disc files with outer music");
        Throws<ArgumentException>(() => CdImageGenerator.Generate(new CdImageRequest(image, output, parameters, tone,
            MusicTracks: [first], Session2StartLba: 12_000, MusicAtOuterRing: true)), "a session 2 start with outer music");
        Throws<ArgumentException>(() => CdImageGenerator.Generate(new CdImageRequest(image, first, parameters, tone,
            MusicTracks: [first], MusicAtOuterRing: true)), "an output that overwrites a music file");

        // Command line: the outer position is the default.
        CdDiscModule module = new();
        foreach (string command in new[] { "cd-generate", "cd-preview-warp" })
        {
            Require(module.Descriptor.Commands.First(c => c.Name == command).Options.Any(o => o.Name == "music-position"
                    && o.ValueType == DiscOptionValueType.Choice && o.DefaultValue == "outer"
                    && o.Choices!.SequenceEqual(new[] { "outer", "inner" })),
                command + " metadata exposes the music position with the outer default");
        }
        DiscModuleCatalog catalog = new([module]);
        string[] geometry = ["--r0", "24.5", "--r1", "27.5", "--sectors", "15000", "--usable-sectors", "14998"];
        string cli = Path.Combine(directory, "cli", "disc.wav");
        DiscJobResult generated = catalog.ExecuteAsync(new DiscJobRequest("cd-generate",
            ["--input", image, "--output", cli, "--music", music, "--interleave", "false", "--tone-dither", "0.75", .. geometry]))
            .GetAwaiter().GetResult();
        Require(generated.Succeeded && File.ReadAllBytes(cli).AsSpan(44 + pictureBytes).SequenceEqual(file.AsSpan(44 + pictureBytes))
                && File.Exists(Path.ChangeExtension(cli, ".cue")),
            "cd-generate --music writes the WAV and CUE with the music on the outer rings by default");
        string warp = Path.Combine(directory, "cli", "warp.png");
        DiscJobResult previewed = catalog.ExecuteAsync(new DiscJobRequest("cd-preview-warp",
            ["--input", image, "--output", warp, "--music", music, "--size", "128", "--samples-per-sector", "2", .. geometry]))
            .GetAwaiter().GetResult();
        Require(previewed.Succeeded && new FileInfo(warp).Length > 0
                && previewed.Summary!.Contains("music from LBA " + layout.MusicStartLba, StringComparison.Ordinal),
            "cd-preview-warp --music previews the picture in front of the music");
        ThrowsArgument(() => catalog.ExecuteAsync(new DiscJobRequest("cd-generate",
            ["--input", image, "--output", cli, "--music", music, "--session2-start-lba", "12000", .. geometry]))
            .GetAwaiter().GetResult(), "inner", "a session 2 start needs the inner position");
        ThrowsArgument(() => catalog.ExecuteAsync(new DiscJobRequest("cd-generate",
            ["--input", image, "--output", cli, "--music", music, "--music-position", "middle", .. geometry]))
            .GetAwaiter().GetResult(), "outer", "an unknown position names the choices");
        ThrowsArgument(() => catalog.ExecuteAsync(new DiscJobRequest("cd-generate",
            ["--input", image, "--output", cli, "--music-position", "inner", .. geometry]))
            .GetAwaiter().GetResult(), "--music", "a position without music");
        ThrowsArgument(() => catalog.ExecuteAsync(new DiscJobRequest("cd-generate",
            ["--input", image, "--output", Path.Combine(directory, "cli", "x.iso"), "--music", music, .. geometry]))
            .GetAwaiter().GetResult(), "inner", "an ISO output points to the inner position");
    }

    private static string Time(long sectors) => $"{sectors / 4500:00}:{sectors / 75 % 60:00}:{sectors % 75:00}";

    private static int FromBcd(byte value) => (value >> 4) * 10 + (value & 15);

    private static byte[] DecodeQ(byte[] raw)
    {
        byte[] q = new byte[12];
        for (int bit = 0; bit < 96; bit++)
            q[bit / 8] |= (byte)(((raw[2352 + bit] >> 6) & 1) << (7 - bit % 8));
        return q;
    }

    private static void ReadExactly(IStream stream, byte[] buffer, IntPtr count)
    {
        int filled = 0;
        byte[] chunk = buffer;
        while (filled < buffer.Length)
        {
            if (filled > 0) chunk = new byte[buffer.Length - filled];
            stream.Read(chunk, chunk.Length, count);
            int read = Marshal.ReadInt32(count);
            if (read <= 0) throw new EndOfStreamException("The raw image ended early.");
            if (!ReferenceEquals(chunk, buffer)) chunk.AsSpan(0, read).CopyTo(buffer.AsSpan(filled));
            filled += read;
        }
    }

    /// <summary>Like the generated burn stream: readable once, front to back.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ForwardOnlyContent(byte[] bytes) : IStream
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

    private static void ThrowsArgument(Action action, string contains, string message)
    {
        try { action(); }
        catch (ArgumentException exception)
        {
            Require(exception.Message.Contains(contains, StringComparison.Ordinal), message + " (message: " + exception.Message + ")");
            return;
        }
        throw new InvalidOperationException("CD outer music: expected ArgumentException for " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("CD outer music: expected " + typeof(T).Name + " for " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD outer music: " + message);
    }
}
