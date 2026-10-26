using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DiscImageStudio.Iso;

/// <summary>
/// Plans a closed, read-only UDF 2.01 volume and writes it without seeking. The physical
/// partition contains only the files and may be placed at either end; the drawing area is reserved volume space
/// outside that partition. ECMA-167 (3rd edition), ECMA TR/112-4 (UDF 2.01).
/// </summary>
public static class UdfImageWriter
{
    public const int LogicalSectorBytes = 2048;
    public const uint MinimumVolumeSectors = 640;
    private const uint MainVdsLba = 32;
    private const uint VdsSectors = 16;
    private const uint IntegrityLba = 48;
    private const uint FirstPartitionLba = 288;
    private const uint MaximumExtentBytes = 0x3ffff800;
    private const string ImplementationId = "*DiscImageStudio";

    public sealed class LayoutPlan
    {
        internal LayoutPlan(string sourceDirectory, uint volumeSectors, string volumeLabel,
            DateTimeOffset timestamp, List<Node> nodes, uint partitionStart, uint dataEnd,
            uint drawingStart, uint drawingEnd, bool filesAtOuterRing)
        {
            SourceDirectory = sourceDirectory;
            VolumeSectors = volumeSectors;
            VolumeLabel = volumeLabel;
            Timestamp = timestamp;
            Nodes = nodes;
            PartitionStartLba = partitionStart;
            DataEndLbaExclusive = dataEnd;
            DrawingStartLba = drawingStart;
            DrawingEndLbaExclusive = drawingEnd;
            FilesAtOuterRing = filesAtOuterRing;
            FileCount = nodes.Count(node => !node.IsDirectory);
            DirectoryCount = nodes.Count - FileCount;
            FileBytes = nodes.Where(node => !node.IsDirectory).Sum(node => node.DataLength);
        }

        public string SourceDirectory { get; }
        public uint VolumeSectors { get; }
        public int FileCount { get; }
        public int DirectoryCount { get; }
        public long FileBytes { get; }
        public uint DataEndLbaExclusive { get; }
        public uint DrawingStartLba { get; }
        public uint DrawingEndLbaExclusive { get; }
        public bool FilesAtOuterRing { get; }
        public string VolumeLabel { get; }
        public DateTimeOffset Timestamp { get; }
        internal List<Node> Nodes { get; }
        internal uint ReserveVdsLba => VolumeSectors - 256;
        internal uint PartitionStartLba { get; }
        internal uint PartitionSectors => DataEndLbaExclusive - PartitionStartLba;
    }

    public static LayoutPlan Plan(string? sourceDirectory, uint volumeSectors, string volumeLabel,
        DateTimeOffset? timestamp = null, bool filesAtOuterRing = false)
    {
        if (volumeSectors < MinimumVolumeSectors || volumeSectors % 32 != 0)
            throw new ArgumentOutOfRangeException(nameof(volumeSectors),
                $"UDF drawing volumes need at least {MinimumVolumeSectors} sectors and a multiple of 32 sectors.");
        ArgumentNullException.ThrowIfNull(volumeLabel);
        DateTimeOffset recorded = timestamp ?? DateTimeOffset.UtcNow;
        string sourcePath = sourceDirectory is null ? string.Empty : Path.GetFullPath(sourceDirectory);
        if (sourceDirectory is not null && !Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Data directory does not exist: {sourcePath}");
        if (sourceDirectory is not null)
            RejectReparsePoint(sourcePath);

        Node root = new(sourcePath, string.Empty, true, null)
        {
            Timestamp = sourceDirectory is null ? recorded : Directory.GetLastWriteTimeUtc(sourcePath),
        };
        List<Node> nodes = [root];
        // Iterative traversal avoids a call-stack dependency on the depth of the source tree.
        for (int index = 0; index < nodes.Count; index++)
        {
            Node directory = nodes[index];
            if (!directory.IsDirectory || directory.SourcePath.Length == 0)
                continue;
            foreach (FileSystemInfo entry in new DirectoryInfo(directory.SourcePath)
                         .EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                RejectReparsePoint(entry.FullName);
                bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                Node child = new(entry.FullName, entry.Name, isDirectory, directory)
                {
                    Timestamp = entry.LastWriteTimeUtc,
                    Identifier = EncodeIdentifier(entry.Name),
                    DataLength = isDirectory ? 0 : ((FileInfo)entry).Length,
                    UniqueId = checked((ulong)nodes.Count + 15),
                };
                directory.Children.Add(child);
                nodes.Add(child);
            }
        }

        uint cursor = checked(FirstPartitionLba + 2);
        foreach (Node node in nodes)
        {
            node.EntryLba = cursor++;
            if (node.IsDirectory)
            {
                node.DataLength = FileIdentifierLength(0, 0);
                foreach (Node child in node.Children)
                    node.DataLength = checked(node.DataLength
                        + FileIdentifierLength(child.Identifier.Length, node.DataLength));
            }
            // A single-layer Blu-ray needs at most 24 descriptors. Fail explicitly if this
            // generic writer is ever used beyond one FE's capacity instead of truncating it.
            if ((node.DataLength + MaximumExtentBytes - 1) / MaximumExtentBytes
                > (LogicalSectorBytes - 176) / 8)
                throw new ArgumentException($"File needs more allocation descriptors than one UDF file entry supports: {node.SourcePath}");
            if (node.Children.Count(child => child.IsDirectory) >= ushort.MaxValue)
                throw new ArgumentException($"Too many child directories for a UDF link count: {node.SourcePath}");
        }
        foreach (Node node in nodes.Where(node => node.IsDirectory))
        {
            node.DataLba = cursor;
            cursor = checked(cursor + SectorCount(node.DataLength));
        }
        foreach (Node node in nodes.Where(node => !node.IsDirectory))
        {
            node.DataLba = cursor;
            cursor = checked(cursor + SectorCount(node.DataLength));
        }
        uint drawingEnd = (volumeSectors - 257) / 32 * 32;
        uint drawingStart = checked((cursor + 31) / 32 * 32);
        if (drawingStart >= drawingEnd)
            throw new ArgumentException($"Files and UDF metadata need {cursor} sectors, leaving no complete drawing cluster before the backup metadata at sector {drawingEnd}.");
        uint partitionStart = FirstPartitionLba;
        if (filesAtOuterRing)
        {
            // Relocate the entire physical partition, keeping picture bytes outside it.
            // Its relative addresses stay identical; only the descriptor's partition
            // origin and the absolute sequential write positions change.
            uint partitionSectors = cursor - FirstPartitionLba;
            partitionStart = (drawingEnd - partitionSectors) / 32 * 32;
            uint shift = partitionStart - FirstPartitionLba;
            foreach (Node node in nodes)
            {
                node.EntryLba = checked(node.EntryLba + shift);
                node.DataLba = checked(node.DataLba + shift);
            }
            cursor = checked(cursor + shift);
            drawingStart = FirstPartitionLba;
            drawingEnd = partitionStart;
        }
        string label = string.IsNullOrWhiteSpace(volumeLabel) ? "BD_IMAGE" : volumeLabel;
        // All descriptors use precisely the same logical volume identifier.
        label = FitDstring(label, 128);
        return new LayoutPlan(sourcePath, volumeSectors, label, recorded, nodes, partitionStart,
            cursor, drawingStart, drawingEnd, filesAtOuterRing);
    }

    public static void WriteSequentialPrefix(LayoutPlan plan, Stream output, uint endLbaExclusive,
        CancellationToken cancellationToken = default)
    {
        ValidateOutput(plan, output);
        uint minimumEnd = plan.FilesAtOuterRing ? plan.DrawingStartLba : plan.DataEndLbaExclusive;
        if (endLbaExclusive < minimumEnd || endLbaExclusive > plan.DrawingEndLbaExclusive)
            throw new ArgumentOutOfRangeException(nameof(endLbaExclusive));
        ValidateSources(plan, cancellationToken);
        SequentialWriter writer = new(output, 0, cancellationToken);
        writer.WriteAtLba(16, VolumeRecognition("BEA01"));
        writer.WriteAtLba(17, VolumeRecognition("NSR03"));
        writer.WriteAtLba(18, VolumeRecognition("TEA01"));
        WriteVolumeDescriptors(plan, writer, MainVdsLba);
        writer.WriteAtLba(IntegrityLba, IntegrityDescriptor(plan));
        writer.WriteAtLba(IntegrityLba + 1, Descriptor(8, IntegrityLba + 1, 512));
        writer.WriteAtLba(256, Anchor(plan, 256));
        if (!plan.FilesAtOuterRing)
            WritePartition(plan, writer);
        writer.PadToLba(endLbaExclusive);
        ValidateSources(plan, cancellationToken);
    }

    private static void WritePartition(LayoutPlan plan, SequentialWriter writer)
    {
        writer.WriteAtLba(plan.PartitionStartLba, FileSetDescriptor(plan));
        writer.WriteAtLba(plan.PartitionStartLba + 1, Descriptor(8, 1, 512));
        foreach (Node node in plan.Nodes)
            writer.WriteAtLba(node.EntryLba, FileEntry(plan, node));
        foreach (Node directory in plan.Nodes.Where(node => node.IsDirectory))
        {
            writer.PadToLba(directory.DataLba);
            long directoryOffset = 0;
            WriteIdentifier(directory.Parent ?? directory, [], parent: true);
            foreach (Node child in directory.Children)
                WriteIdentifier(child, child.Identifier, parent: false);

            void WriteIdentifier(Node target, byte[] identifier, bool parent)
            {
                uint location = checked(directory.DataLba - plan.PartitionStartLba
                    + (uint)(directoryOffset / LogicalSectorBytes));
                byte[] record = FileIdentifier(plan, target, identifier, parent, location, directoryOffset);
                writer.Write(record);
                directoryOffset += record.Length;
            }
        }
        foreach (Node file in plan.Nodes.Where(node => !node.IsDirectory))
            writer.WriteFile(file);
    }

    /// <summary>Appends the reserved tail after the caller has written the drawing range.</summary>
    public static void WriteSequentialSuffix(LayoutPlan plan, Stream output, uint startLba,
        CancellationToken cancellationToken = default)
    {
        ValidateOutput(plan, output);
        if (startLba != plan.DrawingEndLbaExclusive)
            throw new ArgumentOutOfRangeException(nameof(startLba), "The UDF suffix must start at DrawingEndLbaExclusive.");
        cancellationToken.ThrowIfCancellationRequested();
        SequentialWriter writer = new(output, startLba, cancellationToken);
        if (plan.FilesAtOuterRing)
        {
            ValidateSources(plan, cancellationToken);
            WritePartition(plan, writer);
            ValidateSources(plan, cancellationToken);
        }
        writer.WriteAtLba(plan.VolumeSectors - 257, Anchor(plan, plan.VolumeSectors - 257));
        WriteVolumeDescriptors(plan, writer, plan.ReserveVdsLba);
        writer.WriteAtLba(plan.VolumeSectors - 1, Anchor(plan, plan.VolumeSectors - 1));
        writer.PadToLba(plan.VolumeSectors);
    }

    private static void WriteVolumeDescriptors(LayoutPlan plan, SequentialWriter writer, uint start)
    {
        writer.WriteAtLba(start, PrimaryDescriptor(plan, start));
        writer.WriteAtLba(start + 1, ImplementationDescriptor(plan, start + 1));
        writer.WriteAtLba(start + 2, PartitionDescriptor(plan, start + 2));
        writer.WriteAtLba(start + 3, LogicalVolumeDescriptor(plan, start + 3));
        byte[] unallocated = new byte[LogicalSectorBytes];
        U32(unallocated, 16, 5);
        // Zero free extents: pattern bytes outside the partition are reserved volume space.
        SetTag(unallocated, 7, start + 4, 24);
        writer.WriteAtLba(start + 4, unallocated);
        writer.WriteAtLba(start + 5, Descriptor(8, start + 5, 512));
        writer.PadToLba(start + VdsSectors);
    }

    private static byte[] VolumeRecognition(string identifier)
    {
        byte[] sector = new byte[LogicalSectorBytes];
        Encoding.ASCII.GetBytes(identifier, sector.AsSpan(1, 5));
        sector[6] = 1;
        return sector;
    }

    private static byte[] PrimaryDescriptor(LayoutPlan plan, uint location)
    {
        byte[] data = new byte[LogicalSectorBytes];
        U32(data, 16, 1);
        Dstring(data.AsSpan(24, 32), FitDstring(plan.VolumeLabel, 32));
        U16(data, 56, 1);
        U16(data, 58, 1);
        U16(data, 60, 2);
        U16(data, 62, 3);
        U32(data, 64, 1);
        U32(data, 68, 1);
        string volumeSet = unchecked((uint)plan.Timestamp.ToUnixTimeSeconds()).ToString("X8", CultureInfo.InvariantCulture)
            + "00000000";
        Dstring(data.AsSpan(72, 128), volumeSet);
        Charspec(data.AsSpan(200, 64));
        Charspec(data.AsSpan(264, 64));
        Entity(data.AsSpan(344, 32), ImplementationId);
        Timestamp(data.AsSpan(376, 12), plan.Timestamp);
        Entity(data.AsSpan(388, 32), ImplementationId);
        U16(data, 488, 1);
        SetTag(data, 1, location, 512);
        return data;
    }

    private static byte[] ImplementationDescriptor(LayoutPlan plan, uint location)
    {
        byte[] data = new byte[LogicalSectorBytes];
        U32(data, 16, 2);
        Entity(data.AsSpan(20, 32), "*UDF LV Info", udfRevision: true);
        Charspec(data.AsSpan(52, 64));
        Dstring(data.AsSpan(116, 128), plan.VolumeLabel);
        Entity(data.AsSpan(352, 32), ImplementationId);
        SetTag(data, 4, location, 512);
        return data;
    }

    private static byte[] PartitionDescriptor(LayoutPlan plan, uint location)
    {
        byte[] data = new byte[LogicalSectorBytes];
        U32(data, 16, 3);
        U16(data, 20, 1);
        Entity(data.AsSpan(24, 32), "+NSR03");
        U32(data, 184, 1); // Read-only partition; there is no allocatable free space.
        U32(data, 188, plan.PartitionStartLba);
        U32(data, 192, plan.PartitionSectors);
        Entity(data.AsSpan(196, 32), ImplementationId);
        SetTag(data, 5, location, 512);
        return data;
    }

    private static byte[] LogicalVolumeDescriptor(LayoutPlan plan, uint location)
    {
        byte[] data = new byte[LogicalSectorBytes];
        U32(data, 16, 4);
        Charspec(data.AsSpan(20, 64));
        Dstring(data.AsSpan(84, 128), plan.VolumeLabel);
        U32(data, 212, LogicalSectorBytes);
        Entity(data.AsSpan(216, 32), "*OSTA UDF Compliant", domain: true);
        LongAd(data.AsSpan(248, 16), LogicalSectorBytes * 2, 0);
        U32(data, 264, 6);
        U32(data, 268, 1);
        Entity(data.AsSpan(272, 32), ImplementationId);
        Extent(data.AsSpan(432, 8), LogicalSectorBytes * 4, IntegrityLba);
        data[440] = 1;
        data[441] = 6;
        U16(data, 442, 1); // Volume sequence number; partition number is zero.
        SetTag(data, 6, location, 446);
        return data;
    }

    private static byte[] IntegrityDescriptor(LayoutPlan plan)
    {
        byte[] data = new byte[LogicalSectorBytes];
        Timestamp(data.AsSpan(16, 12), plan.Timestamp);
        U32(data, 28, 1); // Closed logical volume.
        U64(data, 40, checked((ulong)plan.Nodes.Count + 15));
        U32(data, 72, 1);
        U32(data, 76, 46);
        U32(data, 84, plan.PartitionSectors);
        Entity(data.AsSpan(88, 32), ImplementationId);
        U32(data, 120, checked((uint)plan.FileCount));
        U32(data, 124, checked((uint)plan.DirectoryCount));
        U16(data, 128, 0x0201);
        U16(data, 130, 0x0201);
        U16(data, 132, 0x0201);
        SetTag(data, 9, IntegrityLba, 134);
        return data;
    }

    private static byte[] Anchor(LayoutPlan plan, uint location)
    {
        byte[] data = new byte[LogicalSectorBytes];
        Extent(data.AsSpan(16, 8), VdsSectors * LogicalSectorBytes, MainVdsLba);
        Extent(data.AsSpan(24, 8), VdsSectors * LogicalSectorBytes, plan.ReserveVdsLba);
        SetTag(data, 2, location, 512);
        return data;
    }

    private static byte[] FileSetDescriptor(LayoutPlan plan)
    {
        byte[] data = new byte[LogicalSectorBytes];
        Timestamp(data.AsSpan(16, 12), plan.Timestamp);
        U16(data, 28, 3);
        U16(data, 30, 3);
        U32(data, 32, 1);
        U32(data, 36, 1);
        Charspec(data.AsSpan(48, 64));
        Dstring(data.AsSpan(112, 128), plan.VolumeLabel);
        Charspec(data.AsSpan(240, 64));
        Dstring(data.AsSpan(304, 32), FitDstring(plan.VolumeLabel, 32));
        LongAd(data.AsSpan(400, 16), LogicalSectorBytes, plan.Nodes[0].EntryLba - plan.PartitionStartLba);
        Entity(data.AsSpan(416, 32), "*OSTA UDF Compliant", domain: true);
        SetTag(data, 256, 0, 512);
        return data;
    }

    private static byte[] FileEntry(LayoutPlan plan, Node node)
    {
        byte[] data = new byte[LogicalSectorBytes];
        U16(data, 20, 4); // ICB strategy type 4, short allocation descriptors.
        U16(data, 24, 1);
        data[27] = node.IsDirectory ? (byte)4 : (byte)5;
        U32(data, 28, (node.Parent ?? node).EntryLba - plan.PartitionStartLba);
        U16(data, 34, 0); // AD type 0 (short_ad); no optional ICB flags.
        U32(data, 36, uint.MaxValue);
        U32(data, 40, uint.MaxValue);
        U32(data, 44, node.IsDirectory ? 0x14a5U : 0x1084U); // Read (and directory traverse).
        U16(data, 48, checked((ushort)(node.IsDirectory
            ? 1 + node.Children.Count(child => child.IsDirectory) : 1)));
        U64(data, 56, checked((ulong)node.DataLength));
        U64(data, 64, SectorCount(node.DataLength));
        Timestamp(data.AsSpan(72, 12), node.Timestamp);
        Timestamp(data.AsSpan(84, 12), node.Timestamp);
        Timestamp(data.AsSpan(96, 12), node.Timestamp);
        U32(data, 108, 1);
        Entity(data.AsSpan(128, 32), ImplementationId);
        U64(data, 160, node.UniqueId);
        int offset = 176;
        long remaining = node.DataLength;
        uint lbn = node.DataLba - plan.PartitionStartLba;
        while (remaining > 0)
        {
            uint length = (uint)Math.Min(remaining, MaximumExtentBytes);
            U32(data, offset, length);
            U32(data, offset + 4, lbn);
            remaining -= length;
            lbn = checked(lbn + SectorCount(length));
            offset += 8;
        }
        U32(data, 172, checked((uint)(offset - 176)));
        SetTag(data, 261, node.EntryLba - plan.PartitionStartLba, offset);
        return data;
    }

    private static byte[] FileIdentifier(LayoutPlan plan, Node target, byte[] identifier, bool parent, uint location,
        long directoryOffset)
    {
        byte[] data = new byte[FileIdentifierLength(identifier.Length, directoryOffset)];
        int implementationBytes = data.Length - ((38 + identifier.Length + 3) / 4 * 4);
        U16(data, 16, 1);
        data[18] = parent ? (byte)10 : target.IsDirectory ? (byte)2 : (byte)0;
        data[19] = checked((byte)identifier.Length);
        LongAd(data.AsSpan(20, 16), LogicalSectorBytes, target.EntryLba - plan.PartitionStartLba);
        // long_ad ImplementationUse: 2 flag bytes followed by the UDF unique ID.
        U32(data, 32, checked((uint)target.UniqueId));
        U16(data, 36, checked((ushort)implementationBytes));
        if (implementationBytes > 0)
            Entity(data.AsSpan(38, 32), ImplementationId);
        identifier.CopyTo(data, 38 + implementationBytes);
        SetTag(data, 257, location, data.Length);
        return data;
    }

    private static int FileIdentifierLength(int identifierBytes, long directoryOffset)
    {
        int length = (38 + identifierBytes + 3) / 4 * 4;
        long nextOffset = (directoryOffset + length) % LogicalSectorBytes;
        // UDF 2.01 section 2.3.4.4: on write-once media the next FID's 16-byte
        // descriptor tag cannot straddle a block. A nonempty IU field is at least32 bytes.
        return nextOffset > LogicalSectorBytes - 16 ? length + 32 : length;
    }
    private static uint SectorCount(long length) => checked((uint)((length + LogicalSectorBytes - 1) / LogicalSectorBytes));

    private static byte[] EncodeIdentifier(string text)
    {
        if (text.Any(IsUnsupportedCharacter))
            throw new ArgumentException($"UDF 2.01 filenames must use CS0 Unicode BMP characters without byte-order marks: {text}");
        bool narrow = text.All(character => character <= byte.MaxValue);
        int length = checked(1 + text.Length * (narrow ? 1 : 2));
        if (length > byte.MaxValue)
            throw new ArgumentException($"Filename exceeds the UDF CS0 identifier limit (254 single-byte or 127 two-byte characters): {text}");
        byte[] bytes = new byte[length];
        bytes[0] = narrow ? (byte)8 : (byte)16;
        if (narrow)
        {
            for (int i = 0; i < text.Length; i++)
                bytes[i + 1] = (byte)text[i];
        }
        else
            Encoding.BigEndianUnicode.GetBytes(text, bytes.AsSpan(1));
        return bytes;
    }

    private static string FitDstring(string text, int fieldBytes)
    {
        if (text.Any(IsUnsupportedCharacter))
            throw new ArgumentException("UDF volume labels must use CS0 Unicode BMP characters without byte-order marks.");
        int characters = text.All(character => character <= byte.MaxValue)
            ? fieldBytes - 2 : (fieldBytes - 2) / 2;
        return text.Length <= characters ? text : text[..characters];
    }

    private static void Dstring(Span<byte> field, string text)
    {
        if (text.Length == 0)
            return;
        byte[] bytes = EncodeIdentifier(text);
        if (bytes.Length >= field.Length)
            throw new ArgumentException("UDF dstring does not fit its descriptor field.");
        bytes.CopyTo(field);
        field[^1] = checked((byte)bytes.Length);
    }

    private static bool IsUnsupportedCharacter(char character)
        => char.IsSurrogate(character) || character is '\0' or '\uFEFF' or '\uFFFE';

    private static void Charspec(Span<byte> field)
        => Encoding.ASCII.GetBytes("OSTA Compressed Unicode", field[1..]);

    private static void Entity(Span<byte> field, string id, bool domain = false, bool udfRevision = false)
    {
        Encoding.ASCII.GetBytes(id, field.Slice(1, 23));
        if (domain || udfRevision)
            BinaryPrimitives.WriteUInt16LittleEndian(field.Slice(24, 2), 0x0201);
        if (domain)
            field[26] = 1; // Permanently closed volume; hard write protected.
    }

    private static void Timestamp(Span<byte> field, DateTimeOffset timestamp)
    {
        DateTime utc = timestamp.UtcDateTime;
        BinaryPrimitives.WriteUInt16LittleEndian(field, 0x1000); // Local time with UTC offset zero.
        BinaryPrimitives.WriteUInt16LittleEndian(field[2..], checked((ushort)utc.Year));
        field[4] = (byte)utc.Month;
        field[5] = (byte)utc.Day;
        field[6] = (byte)utc.Hour;
        field[7] = (byte)utc.Minute;
        field[8] = (byte)utc.Second;
        field[9] = (byte)(utc.Millisecond / 10);
        field[10] = (byte)((utc.Ticks / 1000) % 100);
        field[11] = (byte)((utc.Ticks / 10) % 100);
    }

    private static void Extent(Span<byte> data, uint length, uint lba)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data, length);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], lba);
    }

    private static void LongAd(Span<byte> data, uint length, uint lbn) => Extent(data, length, lbn);
    private static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    private static void U64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, 8), value);

    private static byte[] Descriptor(ushort id, uint location, int descriptorLength)
    {
        byte[] data = new byte[LogicalSectorBytes];
        SetTag(data, id, location, descriptorLength);
        return data;
    }

    private static void SetTag(byte[] data, ushort id, uint location, int descriptorLength)
    {
        U16(data, 0, id);
        U16(data, 2, 3); // ECMA-167 third edition.
        U16(data, 6, 1);
        ushort crc = 0;
        for (int i = 16; i < descriptorLength; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        U16(data, 8, crc);
        U16(data, 10, checked((ushort)(descriptorLength - 16)));
        U32(data, 12, location);
        int checksum = 0;
        for (int i = 0; i < 16; i++)
            if (i != 4)
                checksum += data[i];
        data[4] = unchecked((byte)checksum);
    }

    private static void ValidateOutput(LayoutPlan plan, Stream output)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("UDF output stream must be writable.", nameof(output));
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse points are not supported in the UDF data directory: {path}");
    }

    private static void ValidateSources(LayoutPlan plan, CancellationToken cancellationToken)
    {
        foreach (Node node in plan.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.SourcePath.Length == 0)
                continue;
            if (node.IsDirectory)
            {
                RejectReparsePoint(node.SourcePath);
                if (!Directory.Exists(node.SourcePath))
                    throw new IOException($"Source directory changed after UDF planning: {node.SourcePath}");
                // NTFS can return a stale last-write timestamp from parent enumeration.
                // Check the actual children instead, which also catches additions and removals.
                Dictionary<string, Node> expected = node.Children.ToDictionary(child => child.Name, StringComparer.Ordinal);
                int count = 0;
                foreach (FileSystemInfo entry in new DirectoryInfo(node.SourcePath).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0
                        || !expected.TryGetValue(entry.Name, out Node? child)
                        || ((entry.Attributes & FileAttributes.Directory) != 0) != child.IsDirectory)
                        throw new IOException($"Source directory changed after UDF planning: {node.SourcePath}");
                    count++;
                }
                if (count != expected.Count)
                    throw new IOException($"Source directory changed after UDF planning: {node.SourcePath}");
            }
            else
                ValidateFile(node);
        }
    }

    private static void ValidateFile(Node file)
    {
        for (Node? directory = file.Parent; directory is not null; directory = directory.Parent)
            RejectReparsePoint(directory.SourcePath);
        FileInfo current = new(file.SourcePath);
        if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0
            || current.Length != file.DataLength || current.LastWriteTimeUtc != file.Timestamp.UtcDateTime)
            throw new IOException($"Source file changed after UDF planning: {file.SourcePath}");
    }

    private sealed class SequentialWriter(Stream output, uint startLba, CancellationToken cancellationToken)
    {
        private readonly byte[] _buffer = new byte[128 * 1024];
        private readonly byte[] _zeroBuffer = new byte[128 * 1024];
        private long _position = checked((long)startLba * LogicalSectorBytes);

        internal void WriteAtLba(uint lba, ReadOnlySpan<byte> data)
        {
            PadToLba(lba);
            Write(data);
        }

        internal void Write(ReadOnlySpan<byte> data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(data);
            _position = checked(_position + data.Length);
        }

        internal void WriteFile(Node file)
        {
            PadToLba(file.DataLba);
            ValidateFile(file);
            using FileStream input = new(file.SourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, _buffer.Length, FileOptions.SequentialScan);
            if (input.Length != file.DataLength)
                throw new IOException($"Source file size changed while streaming UDF: {file.SourcePath}");
            long remaining = file.DataLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = input.Read(_buffer, 0, (int)Math.Min(remaining, _buffer.Length));
                if (count == 0)
                    throw new EndOfStreamException($"Source file ended early while streaming UDF: {file.SourcePath}");
                Write(_buffer.AsSpan(0, count));
                remaining -= count;
            }
            if (input.Length != file.DataLength)
                throw new IOException($"Source file size changed while streaming UDF: {file.SourcePath}");
            ValidateFile(file);
        }

        internal void PadToLba(uint lba)
        {
            long target = checked((long)lba * LogicalSectorBytes);
            if (target < _position)
                throw new InvalidOperationException($"Sequential UDF layout moved backwards from {_position} to {target}.");
            while (_position < target)
                Write(_zeroBuffer.AsSpan(0, (int)Math.Min(target - _position, _zeroBuffer.Length)));
        }
    }

    internal sealed class Node(string sourcePath, string name, bool isDirectory, Node? parent)
    {
        internal string SourcePath { get; } = sourcePath;
        internal string Name { get; } = name;
        internal bool IsDirectory { get; } = isDirectory;
        internal Node? Parent { get; } = parent;
        internal List<Node> Children { get; } = [];
        internal byte[] Identifier { get; init; } = [];
        internal DateTimeOffset Timestamp { get; init; }
        internal ulong UniqueId { get; init; }
        internal uint EntryLba { get; set; }
        internal uint DataLba { get; set; }
        internal long DataLength { get; set; }
    }
}
