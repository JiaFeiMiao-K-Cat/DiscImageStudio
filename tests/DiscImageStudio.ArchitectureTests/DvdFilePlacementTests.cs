using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Dvd;
using DiscImageStudio.Iso;
using DvdImageSolver;

internal static class DvdFilePlacementTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-file-placement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            VerifyMetadataParsing(directory);
            string data = Path.Combine(directory, "data");
            Directory.CreateDirectory(Path.Combine(data, "资料"));
            byte[] rootBytes = Enumerable.Range(0, 2591).Select(index => (byte)(index * 37)).ToArray();
            byte[] nestedBytes = Enumerable.Range(0, 65547).Select(index => (byte)(index * 17 + 9)).ToArray();
            File.WriteAllBytes(Path.Combine(data, "root.bin"), rootBytes);
            File.WriteAllBytes(Path.Combine(data, "资料", "中文.bin"), nestedBytes);
            File.WriteAllBytes(Path.Combine(data, "empty.txt"), []);
            string image = Path.Combine(directory, "source.png");
            WriteImage(image);

            // The optional placement flag must leave the original layout and bytes intact.
            var legacy = Iso9660ImageWriter.Plan(data, 256, "PLACEMENT", DateTimeOffset.UnixEpoch);
            var explicitInner = Iso9660ImageWriter.Plan(data, 256, "PLACEMENT", DateTimeOffset.UnixEpoch, false);
            using MemoryStream legacyBytes = new();
            using MemoryStream innerBytes = new();
            Iso9660ImageWriter.WriteSequentialPrefix(legacy, legacyBytes, 256);
            Iso9660ImageWriter.WriteSequentialPrefix(explicitInner, innerBytes, 256);
            Require(legacyBytes.ToArray().SequenceEqual(innerBytes.ToArray()), "default inner layout preserves bytes");

            byte[]? innerImage = null;
            uint innerDrawingStart = 0;
            foreach (bool outer in new[] { false, true })
            {
                DvdStreamingOptions options = new(256, 24, 58, FastParallelism: 1);
                DvdHybridStreamingPlan plan = DvdStreamingGenerator.PrepareHybrid(data, options, "PLACEMENT", outer);
                Require(plan.FilesAtOuterRing == outer && plan.DrawingStartLba % 16 == 0
                    && plan.DrawingEndLbaExclusive % 16 == 0 && plan.DrawingSectors >= 16,
                    "aligned, nonempty drawing interval");
                using MemoryStream output = new();
                using ForwardOnlyStream forward = new(output);
                DvdStreamingSummary summary = DvdStreamingGenerator.GenerateHybrid(image, forward, options, plan);
                Require(summary.BytesWritten == 256 * 2048 && output.Length == summary.BytesWritten,
                    "forward-only output fills the complete disc");
                byte[] streamBytes = output.ToArray();
                if (!outer)
                {
                    innerImage = streamBytes;
                    innerDrawingStart = plan.DrawingStartLba;
                }
                else
                {
                    int commonStart = checked((int)Math.Max(innerDrawingStart, plan.DrawingStartLba) * 2048);
                    int commonLength = checked((int)plan.DrawingEndLbaExclusive * 2048 - commonStart);
                    Require(commonLength > 0 && streamBytes.AsSpan(commonStart, commonLength)
                        .SequenceEqual(innerImage!.AsSpan(commonStart, commonLength)),
                        "placement never rescales or rotates drawing at the same full-disc LBA");
                }
                Dictionary<string, Entry> entries = ReadJoliet(streamBytes);
                Require(entries["root.bin"].Bytes.SequenceEqual(rootBytes), "root file bytes");
                Require(entries["资料/中文.bin"].Bytes.SequenceEqual(nestedBytes), "Unicode nested file bytes");
                Require(entries["empty.txt"].Bytes.Length == 0, "empty file remains readable");
                foreach (Entry entry in entries.Values.Where(entry => entry.Bytes.Length > 0))
                {
                    uint end = entry.Lba + (uint)((entry.Bytes.Length + 2047) / 2048);
                    Require(outer ? entry.Lba >= plan.DrawingEndLbaExclusive : end <= plan.DrawingStartLba,
                        "all file extents stay outside the drawing");
                }

                string iso = Path.Combine(directory, outer ? "outer.iso" : "inner.iso");
                Require(DvdCommandRunner.Run(GenerateArguments(image, data, iso,
                    outer ? "outer" : "inner")) == 0, "CLI generation");
                byte[] fileBytes = File.ReadAllBytes(iso);
                NormalizeVolumeTimestamps(streamBytes);
                NormalizeVolumeTimestamps(fileBytes);
                Require(streamBytes.SequenceEqual(fileBytes), "file and nonseek stream layouts and drawing match");
                DvdImageMetadata metadata = DvdImageMetadata.TryLoad(iso)
                    ?? throw new InvalidOperationException("Missing DVD placement metadata.");
                Require(metadata.FilesAtOuterRing == outer && metadata.HasFileSystem
                    && metadata.DrawingStartLba == plan.DrawingStartLba
                    && metadata.DrawingEndLbaExclusive == plan.DrawingEndLbaExclusive,
                    "sidecar retains file placement and exact drawing interval");
                string preview = Path.Combine(directory, outer ? "outer.png" : "inner.png");
                Require(DvdCommandRunner.Run(["simulate", "--iso", iso, "--output", preview,
                    "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--preview-size", "64"])
                    == 0, "CLI readback accepts the sidecar range");
                using JsonDocument readback = JsonDocument.Parse(File.ReadAllText(preview + ".json"));
                Require(readback.RootElement.GetProperty("startLba").GetUInt32() == plan.DrawingStartLba
                    && readback.RootElement.GetProperty("fillSectors").GetUInt32() == plan.DrawingSectors,
                    "readback excludes metadata and file payloads");

                using MemoryStream canceled = new();
                using CancellationTokenSource cancellation = new();
                cancellation.Cancel();
                Throws<OperationCanceledException>(() => DvdStreamingGenerator.GenerateHybrid(
                    image, canceled, options, plan, cancellationToken: cancellation.Token), "cancellation");
                Require(canceled.Length == 0, "pre-canceled generation writes no prefix");
            }

            Throws<ArgumentException>(() => DvdStreamingGenerator.PrepareHybrid(data,
                new(64, 24, 58), "PLACEMENT", true), "full filesystem leaves no ECC drawing block");
            Throws<ArgumentException>(() => DvdStreamingGenerator.PrepareHybrid(data,
                new(32, 24, 58), "PLACEMENT", true), "metadata and files exceed capacity");
            string rejected = Path.Combine(directory, "preserved.iso");
            byte[] sentinel = "existing output"u8.ToArray();
            File.WriteAllBytes(rejected, sentinel);
            Require(DvdCommandRunner.Run(GenerateArguments(image, data, rejected, "outside")) != 0,
                "invalid placement rejected");
            Require(DvdCommandRunner.Run(["solve", "--image", image, "--iso-output", rejected,
                "--total-sectors", "256", "--file-position", "outer"]) != 0,
                "outer placement without data folder rejected");
            string[] noDrawingSpace = GenerateArguments(image, data, rejected, "outer");
            noDrawingSpace[Array.IndexOf(noDrawingSpace, "--total-sectors") + 1] = "64";
            Require(DvdCommandRunner.Run(noDrawingSpace) != 0, "capacity rejected before creating ISO");
            Require(File.ReadAllBytes(rejected).SequenceEqual(sentinel), "invalid requests preserve existing output");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("dvd-file-placement: passed (ISO/Joliet extents, Unicode contents, full geometry, streaming parity, ranges and guards)");
    }

    private static string[] GenerateArguments(string image, string data, string iso, string position)
        => ["solve", "--image", image, "--data-dir", data, "--iso-output", iso,
            "--volume-label", "PLACEMENT", "--total-sectors", "256", "--inner-radius-mm", "24",
            "--outer-radius-mm", "58", "--algorithm", "dispersion", "--fast-output", "true",
            "--fast-parallelism", "1", "--spiral-direction", "cw", "--file-position", position];

    private static void VerifyMetadataParsing(string directory)
    {
        string iso = Path.Combine(directory, "metadata.iso");
        Dictionary<string, object?> Mapping() => new()
        {
            ["totalSectors"] = 256u, ["innerRadiusMm"] = 24d, ["outerRadiusMm"] = 58d
        };
        DvdImageMetadata? Load(object? document)
        {
            File.WriteAllText(iso + ".json", JsonSerializer.Serialize(document));
            return DvdImageMetadata.TryLoad(iso);
        }
        Dictionary<string, object?> Document() => new() { ["imageMapping"] = Mapping() };

        DvdImageMetadata? legacy = Load(Document());
        Require(legacy is { DrawingStartLba: 0, DrawingEndLbaExclusive: 256 },
            "legacy sidecar without range uses full disc");
        Dictionary<string, object?> startOnly = Document();
        startOnly["startLba"] = 32u;
        Require(Load(startOnly) is { DrawingStartLba: 32, DrawingEndLbaExclusive: 256 },
            "missing fill count uses remaining sectors");
        Dictionary<string, object?> countOnly = Document();
        countOnly["fillSectors"] = 16u;
        Require(Load(countOnly) is { DrawingStartLba: 0, DrawingEndLbaExclusive: 16 },
            "missing start uses LBA zero");

        object?[] wrongTypes = [null, "32", true, new[] { 32 }, new Dictionary<string, object?>()];
        foreach (object? invalid in wrongTypes)
        {
            Require(Load(invalid) is null, "non-object sidecar root is rejected without throwing");
            Require(Load(new Dictionary<string, object?> { ["imageMapping"] = invalid }) is null,
                "non-object image mapping is rejected without throwing");
            foreach (string field in new[] { "totalSectors", "innerRadiusMm", "outerRadiusMm", "pitchLinear" })
            {
                Dictionary<string, object?> mapping = Mapping();
                mapping[field] = invalid;
                Require(Load(new Dictionary<string, object?> { ["imageMapping"] = mapping }) is null,
                    "invalid geometry field is rejected without throwing: " + field);
            }
        }
        foreach (string field in new[] { "startLba", "fillSectors" })
        {
            foreach (object? invalid in wrongTypes.Concat(new object?[] { -1, 1.5, 4294967296L, 1e100 }))
            {
                Dictionary<string, object?> document = Document();
                document[field] = invalid;
                Require(Load(document) is null, "explicit invalid range field is rejected: " + field);
            }
        }
        foreach ((uint start, uint count) in new[] { (0u, 0u), (257u, 1u), (240u, 32u), (uint.MaxValue, uint.MaxValue) })
        {
            Dictionary<string, object?> document = Document();
            document["startLba"] = start;
            document["fillSectors"] = count;
            Require(Load(document) is null, "empty, out-of-disc or overflowing range is rejected");
        }
    }

    private sealed record Entry(uint Lba, byte[] Bytes);

    // Independent ISO/Joliet traversal uses only on-disc descriptors and directory records.
    private static Dictionary<string, Entry> ReadJoliet(byte[] image)
    {
        Require(image.AsSpan(17 * 2048 + 1, 5).SequenceEqual("CD001"u8), "Joliet descriptor signature");
        Dictionary<string, Entry> files = new(StringComparer.Ordinal);
        ReadDirectory(17 * 2048 + 156, string.Empty);
        return files;

        void ReadDirectory(int record, string prefix)
        {
            uint extent = ReadBoth(record + 2);
            uint bytes = ReadBoth(record + 10);
            int begin = checked((int)extent * 2048);
            int end = checked(begin + (int)bytes);
            Require(end <= image.Length, "directory extent in volume");
            for (int offset = begin; offset < end;)
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
                        uint lba = ReadBoth(offset + 2);
                        uint count = ReadBoth(offset + 10);
                        int position = checked((int)lba * 2048);
                        Require((long)position + count <= image.Length, "file extent in volume");
                        files.Add(path, new Entry(lba, image.AsSpan(position, checked((int)count)).ToArray()));
                    }
                }
                offset += length;
            }
        }

        uint ReadBoth(int offset)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
            Require(value == BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(offset + 4, 4)), "dual-endian extent consistency");
            return value;
        }
    }

    private static void NormalizeVolumeTimestamps(byte[] bytes)
    {
        // Each independent plan records its creation time; all other bytes must match.
        foreach (int sector in new[] { 16, 17 })
        {
            bytes.AsSpan(sector * 2048 + 174, 7).Clear();
            bytes.AsSpan(sector * 2048 + 813, 34).Clear();
        }
    }

    private static void WriteImage(string path)
    {
        byte[] pixels = new byte[32 * 32 * 4];
        for (int i = 0; i < 32 * 32; i++)
        {
            byte gray = (byte)(i * 37);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = gray;
            pixels[i * 4 + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("DVD file placement: " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
