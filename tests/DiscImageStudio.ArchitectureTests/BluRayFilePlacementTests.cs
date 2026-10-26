using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.Iso;
using Microsoft.Win32.SafeHandles;

internal static class BluRayFilePlacementTests
{
    private const int SectorBytes = 2048;
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-file-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestLayouts(directory);
            TestFailures(directory);
            TestLargeAllocation(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("blu-ray-file-placement: passed (outer UDF traversal, anchors, exact streams, default compatibility, CLI/sidecar, capacity, large allocations)");
    }

    private static void TestLayouts(string directory)
    {
        string source = Path.Combine(directory, "files");
        Directory.CreateDirectory(Path.Combine(source, "资料"));
        byte[] content = Enumerable.Range(0, 70_003).Select(i => (byte)(i * 37)).ToArray();
        byte[] unicode = Encoding.UTF8.GetBytes("蓝光外圈文件\n原样读取");
        File.WriteAllBytes(Path.Combine(source, "payload.bin"), content);
        File.WriteAllBytes(Path.Combine(source, "资料", "说明.txt"), unicode);
        File.WriteAllBytes(Path.Combine(source, "empty"), []);
        // Fresh NTFS directory timestamps may be refreshed by their first traversal.
        // Pin every source timestamp so this compares placement defaults only.
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, Timestamp.UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(source, "资料"), Timestamp.UtcDateTime);
        Directory.SetLastWriteTimeUtc(source, Timestamp.UtcDateTime);

        BluRayDiscParameters disc = new(TotalSectors: 1024);
        BluRayIsoPlan inner = BluRayIsoGenerator.Prepare(disc, source, "PLACEMENT", Timestamp);
        BluRayIsoPlan explicitInner = BluRayIsoGenerator.Prepare(disc, source, "PLACEMENT", Timestamp, filesAtOuterRing: false);
        byte[] innerImage = Generate(inner);
        Require(innerImage.SequenceEqual(Generate(explicitInner)), "omitting placement preserves explicit inner bytes");
        UdfFilesystemTests.Inspect(innerImage, disc.TotalSectors, "PLACEMENT", inner.DrawingStartSector, inner.DrawingEndSectorExclusive);

        BluRayIsoPlan outer = BluRayIsoGenerator.PrepareDrawing(disc, source, "PLACEMENT", Timestamp, filesAtOuterRing: true);
        using ForwardOnlyStream stream = new();
        AddressMapping mapping = new();
        BluRayIsoGenerator.GenerateToStream(stream, outer, mapping);
        byte[] image = stream.Bytes;
        Require(outer.FilesAtOuterRing && outer.HasFileSystem, "outer plan retains a real filesystem");
        Require(outer.DrawingStartSector == 288 && outer.DrawingEndSectorExclusive % 32 == 0,
            "outer drawing starts after the front anchor and ends on a full BD cluster");
        Require(mapping.Sectors.Keys.Min() == outer.DrawingStartSector
            && mapping.Sectors.Keys.Max() + 1 == outer.DrawingEndSectorExclusive
            && mapping.Sectors.Count == outer.DrawingEndSectorExclusive - outer.DrawingStartSector,
            "picture mapping only receives the complete middle drawing interval");
        Dictionary<string, byte[]> files = UdfFilesystemTests.Inspect(image, disc.TotalSectors, "PLACEMENT",
            drawingEndSector: outer.DrawingEndSectorExclusive);
        Require(files["payload.bin"].SequenceEqual(content) && files["资料/说明.txt"].SequenceEqual(unicode)
            && files["empty"].Length == 0, "independent UDF parser recovers Unicode directories and exact file bytes");
        ReadOnlySpan<byte> partition = FindDescriptor(image, 5);
        uint partitionStart = U32(partition, 188), partitionLength = U32(partition, 192);
        Require(partitionStart == outer.DrawingEndSectorExclusive
            && (ulong)partitionStart + partitionLength <= disc.TotalSectors - 257,
            "entire file partition is outside the drawing interval and before the tail anchor");
        Require(partitionStart > U32(FindDescriptor(innerImage, 5), 188), "file partition moved outward");

        string output = Path.Combine(directory, "outer.iso");
        BluRayIsoGenerator.GenerateFile(output, outer, new AddressMapping());
        Require(File.ReadAllBytes(output).SequenceEqual(image), "file and non-seekable streaming output match exactly");
        BluRayIsoMetadata metadata = BluRayIsoMetadata.FromPlan(outer, Path.Combine(directory, "source.png"),
            output, false, ToneProfile.LegacyBinary, Timestamp);
        metadata.Save(output);
        BluRayIsoMetadata restored = BluRayIsoMetadata.TryLoad(output)!;
        Require(restored.FilesAtOuterRing && restored.DrawingStartSector == outer.DrawingStartSector
            && restored.DrawingEndSectorExclusive == outer.DrawingEndSectorExclusive,
            "sidecar roundtrip preserves outer placement and drawing bounds");
        JsonObject oldSidecar = JsonNode.Parse(File.ReadAllText(BluRayIsoMetadata.PathFor(output)))!.AsObject();
        oldSidecar.Remove("filesAtOuterRing");
        File.WriteAllText(BluRayIsoMetadata.PathFor(output), oldSidecar.ToJsonString());
        Require(BluRayIsoMetadata.TryLoad(output) is { FilesAtOuterRing: false }, "old sidecars default to inner placement");

        BluRayDiscModule module = new((_, _) => new(1, 1, [128]));
        string cliOutput = Path.Combine(directory, "outer-cli.iso");
        DiscJobResult result = module.ExecuteAsync(new("bd-generate", ["--input", "source.png", "--output", cliOutput,
            "--total-sectors", "1024", "--data-dir", source, "--file-position", "outer"]))
            .GetAwaiter().GetResult();
        Require(result.Succeeded && BluRayIsoMetadata.TryLoad(cliOutput) is { FilesAtOuterRing: true },
            "CLI forwards outer file placement to image and sidecar");
        UdfFilesystemTests.Inspect(File.ReadAllBytes(cliOutput), 1024, "BD_IMAGE");
    }

    private static void TestFailures(string directory)
    {
        string source = Path.Combine(directory, "failure-files");
        Directory.CreateDirectory(source);
        string file = Path.Combine(source, "data.bin");
        File.WriteAllBytes(file, new byte[256 * SectorBytes]);
        Throws<ArgumentException>(() => BluRayIsoGenerator.Prepare(new(TotalSectors: 640), source,
            filesAtOuterRing: true));
        Throws<ArgumentException>(() => BluRayIsoGenerator.PrepareDrawing(new(TotalSectors: 1024),
            filesAtOuterRing: true));
        BluRayDiscModule module = new((_, _) => new(1, 1, [0]));
        foreach (string position in new[] { "outside", "OUTER", "" })
        {
            Throws<ArgumentException>(() => module.ExecuteAsync(new("bd-generate", ["--input", "source.png", "--output",
                Path.Combine(directory, "bad.iso"), "--total-sectors", "1024", "--data-dir", source,
                "--file-position", position])).GetAwaiter().GetResult());
        }
        Throws<ArgumentException>(() => module.ExecuteAsync(new("bd-generate", ["--input", "source.png", "--output",
            Path.Combine(directory, "bad.iso"), "--total-sectors", "1024", "--file-position", "outer"]))
            .GetAwaiter().GetResult());

        File.WriteAllBytes(file, [1, 2, 3]);
        BluRayIsoPlan outer = BluRayIsoGenerator.Prepare(new(TotalSectors: 1024), source, filesAtOuterRing: true);
        string output = Path.Combine(directory, "preserved.iso");
        File.WriteAllBytes(output, [7, 8, 9]);
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateFile(output, outer,
            new AddressMapping(), cancellationToken: cancel.Token));
        Require(File.ReadAllBytes(output).SequenceEqual(new byte[] { 7, 8, 9 }), "cancellation preserves the existing ISO");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        Throws<IOException>(() => BluRayIsoGenerator.GenerateFile(output, outer, new AddressMapping()));
        Require(File.ReadAllBytes(output).SequenceEqual(new byte[] { 7, 8, 9 }), "changed sources preserve the existing ISO");
    }

    private static void TestLargeAllocation(string directory)
    {
        string source = Path.Combine(directory, "large-source");
        Directory.CreateDirectory(source);
        string file = Path.Combine(source, "large.bin");
        const long length = (long)uint.MaxValue + 131_074;
        using (FileStream sparse = new(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            if (!DeviceIoControl(sparse.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Sparse-file support is required before extending the test file.");
            sparse.SetLength(length);
        }
        UdfImageWriter.LayoutPlan plan = UdfImageWriter.Plan(source, BluRayDiscParameters.SectorsPerLayer,
            "LARGE_OUTER", Timestamp, filesAtOuterRing: true);
        using CancellationTokenSource cancel = new();
        using PrefixCapture suffix = new(cancel);
        Throws<OperationCanceledException>(() => UdfImageWriter.WriteSequentialSuffix(plan, suffix,
            plan.DrawingEndLbaExclusive, cancel.Token));
        byte[] bytes = suffix.Bytes;
        Require(bytes.Length < 256 * 1024, "large-file allocation check captures bounded metadata without copying gigabytes");
        ReadOnlySpan<byte> fsd = bytes.AsSpan(0, SectorBytes);
        uint root = U32(fsd, 404);
        ReadOnlySpan<byte> rootEntry = bytes.AsSpan(checked((int)root * SectorBytes), SectorBytes);
        uint rootData = U32(rootEntry, 180);
        ReadOnlySpan<byte> fid = bytes.AsSpan(checked((int)rootData * SectorBytes));
        int firstLength = (38 + U16(fid, 36) + fid[19] + 3) & ~3;
        fid = fid[firstLength..];
        Require(U16(fid, 0) == 257 && (fid[18] & 8) == 0, "outer directory contains the large file entry");
        uint fileEntry = U32(fid, 24);
        ReadOnlySpan<byte> entry = bytes.AsSpan(checked((int)fileEntry * SectorBytes), SectorBytes);
        Require(U16(entry, 0) == 261 && BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(56, 8)) == (ulong)length,
            "outer file entry retains 64-bit information length");
        int count = checked((int)U32(entry, 172) / 8);
        Require(count == 5, "large outer file retains five allocation descriptors");
        long described = 0;
        uint previousEnd = 0;
        for (int index = 0; index < count; index++)
        {
            uint extentLength = U32(entry, 176 + index * 8), block = U32(entry, 180 + index * 8);
            Require(extentLength is > 0 and <= 0x3ffff800, "recorded extent respects 30-bit length");
            Require(index == 0 || block == previousEnd, "large-file extents remain contiguous after relocation");
            previousEnd = checked(block + (extentLength + SectorBytes - 1) / SectorBytes);
            Require((ulong)plan.DrawingEndLbaExclusive + previousEnd <= plan.DataEndLbaExclusive,
                "every large-file extent fits within the outer partition");
            described += extentLength;
        }
        Require(described == length, "outer allocation descriptors cover every large-file byte");
    }

    private static byte[] Generate(BluRayIsoPlan plan)
    {
        using MemoryStream stream = new();
        BluRayIsoGenerator.GenerateToStream(stream, plan, new AddressMapping());
        return stream.ToArray();
    }

    private static ReadOnlySpan<byte> FindDescriptor(byte[] image, ushort type)
    {
        ReadOnlySpan<byte> anchor = image.AsSpan(256 * SectorBytes, SectorBytes);
        uint start = U32(anchor, 20), count = U32(anchor, 16) / SectorBytes;
        for (uint index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> descriptor = image.AsSpan(checked((int)(start + index) * SectorBytes), SectorBytes);
            if (U16(descriptor, 0) == type) return descriptor;
        }
        throw new InvalidDataException("Missing UDF descriptor.");
    }

    private sealed class AddressMapping : IBluRayPayloadMapping
    {
        internal ConcurrentDictionary<uint, byte> Sectors { get; } = new();
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            Sectors.TryAdd(context.Sector, 0);
            destination.Fill((byte)context.Sector);
        }
    }

    private class ForwardOnlyStream : Stream
    {
        protected readonly MemoryStream Buffer = new();
        internal byte[] Bytes => Buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Buffer.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { if (disposing) Buffer.Dispose(); base.Dispose(disposing); }
    }

    private sealed class PrefixCapture(CancellationTokenSource cancellation) : ForwardOnlyStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            if (Buffer.Length >= 64 * 1024) cancellation.Cancel();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr inputBuffer, uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Blu-ray file placement: " + message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
