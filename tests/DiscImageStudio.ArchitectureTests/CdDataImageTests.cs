using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;

internal static class CdDataImageTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cd-data-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { RunInDirectory(directory); }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("cd-data-image: passed (ISO/Joliet contents, inner/outer extents, scrambling, geometry, streaming, CLI and guards)");
    }

    private static void RunInDirectory(string directory)
    {
        string data = Path.Combine(directory, "data");
        Directory.CreateDirectory(Path.Combine(data, "资料"));
        byte[] root = Enumerable.Range(0, 2591).Select(index => (byte)(index * 37)).ToArray();
        byte[] nested = Enumerable.Range(0, 17009).Select(index => (byte)(index * 17 + 9)).ToArray();
        File.WriteAllBytes(Path.Combine(data, "root.bin"), root);
        File.WriteAllBytes(Path.Combine(data, "资料", "中文.bin"), nested);
        File.WriteAllBytes(Path.Combine(data, "empty.txt"), []);
        string image = Path.Combine(directory, "source.png");
        WriteImage(image, solid: false);
        CdDiscParameters parameters = new(24.5, 56.8, 128);
        byte[]? inner = null;
        uint innerStart = 0;
        foreach (bool outer in new[] { false, true })
        {
            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(data, parameters, "CD_TEST", outer);
            Require(plan.FilesAtOuterRing == outer && plan.FileCount == 3 && plan.DrawingSectors > 0, "layout mode and files");
            using MemoryStream bytes = new();
            using ForwardOnlyStream forward = new(bytes);
            CdProgress? last = null;
            CdGenerationSummary summary = CdDataImageGenerator.GenerateToStream(image, forward,
                parameters, plan, value => last = value);
            byte[] iso = bytes.ToArray();
            Require(iso.Length == 128 * 2048 && summary.BytesWritten == iso.Length && !summary.Interleaved,
                "cooked sector count and unmodified stream");
            Require(last is { Fraction: 1 } && last.CompletedBytes == iso.Length, "complete byte progress");
            Dictionary<string, Entry> entries = ReadJoliet(iso);
            Require(entries["root.bin"].Bytes.SequenceEqual(root), "root contents");
            Require(entries["资料/中文.bin"].Bytes.SequenceEqual(nested), "Unicode nested contents");
            Require(entries["empty.txt"].Bytes.Length == 0, "zero-byte file");
            foreach (Entry entry in entries.Values.Where(entry => entry.Bytes.Length > 0))
                Require(outer ? entry.Lba >= plan.DrawingEndLbaExclusive
                    : entry.Lba + (entry.Bytes.Length + 2047) / 2048 <= plan.DrawingStartLba,
                    "file extents stay outside drawing interval");
            if (!outer) { inner = iso; innerStart = plan.DrawingStartLba; }
            else
            {
                int commonStart = checked((int)Math.Max(innerStart, plan.DrawingStartLba) * 2048);
                int commonLength = checked((int)plan.DrawingEndLbaExclusive * 2048 - commonStart);
                Require(iso.AsSpan(commonStart, commonLength).SequenceEqual(inner!.AsSpan(commonStart, commonLength)),
                    "file placement preserves global drawing coordinates");
            }

            string target = Path.Combine(directory, outer ? "outer.iso" : "inner.iso");
            CdDataImageGenerator.Generate(image, target, parameters, plan);
            Require(File.ReadAllBytes(target).SequenceEqual(iso), "file/forward-only stream parity");
            CdDataImageMetadata metadata = CdDataImageMetadata.TryLoad(target)
                ?? throw new InvalidOperationException("CD data metadata missing");
            Require(metadata.Parameters == parameters && metadata.DrawingStartLba == plan.DrawingStartLba
                && metadata.DrawingEndLbaExclusive == plan.DrawingEndLbaExclusive
                && metadata.FilesAtOuterRing == outer, "sidecar geometry and extents");
            string warp = Path.Combine(directory, "warp.png");
            string readback = Path.Combine(directory, "readback.png");
            CdDataImageGenerator.PreviewWarp(image, warp, parameters, parameters, 64, 32,
                ToneProfile.LegacyBinary, plan);
            CdDataImageGenerator.PreviewTrack(target, readback, parameters, 64, 64);
            Require(File.ReadAllBytes(warp).SequenceEqual(File.ReadAllBytes(readback)),
                "source projection and cooked ISO readback use identical physical coordinates and palette");

            using MemoryStream canceled = new();
            Throws<OperationCanceledException>(() => CdDataImageGenerator.GenerateToStream(image,
                canceled, parameters, plan, cancellationToken: new CancellationToken(true)), "pre-cancel");
            Require(canceled.Length == 0, "pre-cancel preserves stream");
            Throws<ArgumentException>(() => CdDataImageGenerator.GenerateToStream(image, canceled,
                parameters with { Sectors = 127 }, plan), "mismatched layout");
        }

        VerifyScrambleMaskAndPhysicalMapping(directory, data, parameters);
        Throws<ArgumentException>(() => CdDataImageGenerator.PrepareLayout(data, parameters with { Sectors = 20 }),
            "capacity rejection");
        VerifyCli(directory, data, image, parameters);
        VerifyMetadataGuards(directory);
    }

    private static void VerifyScrambleMaskAndPhysicalMapping(string directory, string data, CdDiscParameters parameters)
    {
        string image = Path.Combine(directory, "black.png");
        WriteImage(image, solid: true);
        CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(data, parameters);
        using MemoryStream output = new();
        CdDataImageGenerator.GenerateToStream(image, output, parameters, plan);
        byte[] bytes = output.ToArray();
        // ECMA-130 Annex B first 16 scramble bytes are 01 80 00 60 00 28 00 1E 80 08 60 06 A8 02 FE 81.
        // Payload starts four bytes after the scrambling reset, after the address/mode header.
        byte[] expectedMask = [0x00, 0x28, 0x00, 0x1E, 0x80, 0x08, 0x60, 0x06, 0xA8, 0x02, 0xFE, 0x81];
        int start = checked((int)plan.DrawingStartLba * 2048);
        Require(bytes.AsSpan(start, expectedMask.Length).ToArray()
            .Select(value => (byte)(value ^ 0x0D)).SequenceEqual(expectedMask), "standard scrambler vector at payload start");
        Require(bytes.AsSpan(start, 2048).SequenceEqual(bytes.AsSpan(start + 2048, 2048)), "scrambler resets each sector");
        HashSet<long> locations = [];
        for (int offset = 0; offset < 2048; offset++)
        {
            long physical = CdDataImageGenerator.PhysicalByteForPayload(plan.DrawingStartLba, offset);
            Require(locations.Add(physical), "payload-to-CIRC positions are one to one");
            Require(CdDataImageGenerator.PhysicalByteForPayload(plan.DrawingStartLba + 1, offset) - physical == 2352,
                "CD geometry advances by raw 2352 bytes per cooked sector");
        }
        // Independent reference offsets from the documented 24-byte CIRC delay columns.
        for (int payloadOffset = 0; payloadOffset < 2048; payloadOffset++)
        {
            long fileByte = ((long)plan.DrawingStartLba * 2352 + 16 + payloadOffset) ^ 1;
            long physical = CdDataImageGenerator.PhysicalByteForPayload(plan.DrawingStartLba, payloadOffset);
            int column = (int)(physical % 24);
            int[] even = [0, 8, 16, 2, 10, 18], odd = [4, 12, 20, 6, 14, 22];
            bool isOdd = (column & 1) != 0;
            int delay = column < 12
                ? (isOdd ? 1 : 0) - 24 * (column * 4 + (isOdd ? 2 : 3)) + even[column / 2]
                : (isOdd ? 1 : 0) - 24 * ((column + 4) * 4 + (isOdd ? 0 : 1)) + odd[(column - 12) / 2];
            int delayColumn = (delay % 24 + 24) % 24;
            long emitted = physical + 24 * 111 - (delayColumn - delay) + delayColumn - column;
            Require(emitted == fileByte, "CD-ROM pair swap followed by inverse CIRC mapping");
        }
    }

    private static void VerifyCli(string directory, string data, string image, CdDiscParameters parameters)
    {
        CdDiscModule module = new();
        string iso = Path.Combine(directory, "cli.iso");
        DiscJobResult result = module.ExecuteAsync(new("cd-generate", ["--input", image,
            "--output", iso, "--data-dir", data, "--sectors", "128", "--file-position", "outer"])).GetAwaiter().GetResult();
        Require(result.Succeeded && CdDataImageMetadata.TryLoad(iso) is { FilesAtOuterRing: true }, "CLI files route to ISO");
        string wav = Path.Combine(directory, "audio.wav");
        result = module.ExecuteAsync(new("cd-generate", ["--input", image, "--output", wav,
            "--sectors", "8", "--interleave", "true"])).GetAwaiter().GetResult();
        Require(result.Succeeded && CdTrackMetadata.TryLoad(wav) is { Interleaved: true }
            && File.Exists(Path.ChangeExtension(wav, ".cue")), "no files preserves audio/CUE");
        byte[] sentinel = "existing output"u8.ToArray();
        File.WriteAllBytes(iso, sentinel);
        Throws<ArgumentException>(() => module.ExecuteAsync(new("cd-generate", ["--input", image,
            "--output", iso, "--data-dir", data, "--sectors", "128", "--file-position", "invalid"])),
            "CLI invalid file position");
        Require(File.ReadAllBytes(iso).SequenceEqual(sentinel), "invalid input preserves output");
        CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(data, parameters);
        Throws<ArgumentException>(() => CdDataImageGenerator.Generate(image,
            Path.Combine(data, "image.iso"), parameters, plan), "output inside input tree rejected");
    }

    private static void VerifyMetadataGuards(string directory)
    {
        string target = Path.Combine(directory, "bad.iso");
        foreach (string json in new[] { "null", "[]", "{}", "{\"format\":\"cd-da-track-v1\"}",
            "{\"format\":\"cd-rom-mode1-v1\",\"parameters\":null}" })
        {
            File.WriteAllText(target + ".json", json);
            Require(CdDataImageMetadata.TryLoad(target) is null, "invalid metadata is ignored");
        }
    }

    private sealed record Entry(uint Lba, byte[] Bytes);
    private static Dictionary<string, Entry> ReadJoliet(byte[] image)
    {
        Require(image.AsSpan(17 * 2048 + 1, 5).SequenceEqual("CD001"u8), "Joliet descriptor");
        Dictionary<string, Entry> files = new(StringComparer.Ordinal);
        ReadDirectory(17 * 2048 + 156, string.Empty);
        return files;
        void ReadDirectory(int record, string prefix)
        {
            int start = checked((int)ReadBoth(record + 2) * 2048);
            int end = checked(start + (int)ReadBoth(record + 10));
            for (int offset = start; offset < end;)
            {
                int length = image[offset];
                if (length == 0) { offset = (offset / 2048 + 1) * 2048; continue; }
                Require(length >= 34 && offset + length <= end, "bounded directory record");
                int identifierLength = image[offset + 32];
                if (identifierLength > 1)
                {
                    string name = Encoding.BigEndianUnicode.GetString(image, offset + 33, identifierLength);
                    if (name.EndsWith(";1", StringComparison.Ordinal)) name = name[..^2];
                    string path = prefix + name;
                    if ((image[offset + 25] & 2) != 0) ReadDirectory(offset, path + "/");
                    else
                    {
                        uint lba = ReadBoth(offset + 2), size = ReadBoth(offset + 10);
                        files.Add(path, new Entry(lba, image.AsSpan(checked((int)lba * 2048), checked((int)size)).ToArray()));
                    }
                }
                offset += length;
            }
        }
        uint ReadBoth(int offset)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
            Require(value == BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(offset + 4, 4)), "ISO both-endian value");
            return value;
        }
    }

    private static void WriteImage(string path, bool solid)
    {
        const int size = 64;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int index = (y * size + x) * 4;
            byte gray = solid ? (byte)0 : (byte)((x * 7 + y * 13) & 255);
            pixels[index] = pixels[index + 1] = pixels[index + 2] = gray;
            pixels[index + 3] = 255;
        }
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96,
            PixelFormats.Bgra32, null, pixels, size * 4)));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("CD data: " + message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("CD data expected " + typeof(T).Name + ": " + message);
    }
}
