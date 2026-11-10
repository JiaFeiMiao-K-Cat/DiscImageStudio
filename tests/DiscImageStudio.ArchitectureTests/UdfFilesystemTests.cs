using System.Buffers.Binary;
using System.IO;
using System.Text;
using DiscImageStudio.BluRay;
using DiscImageStudio.Iso;

internal static class UdfFilesystemTests
{
    private const int SectorSize = 2048;

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-udf-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestMetadataAlignment(directory);
            foreach (char unsupported in new[] { '\ufeff', '\ufffe' })
            {
                bool rejected = false;
                try { BluRayIsoGenerator.Prepare(new(TotalSectors: 1024), volumeLabel: "bad" + unsupported); }
                catch (ArgumentException) { rejected = true; }
                Require(rejected, "UDF CS0 forbidden code points are rejected in volume identifiers");
            }
            string source = Path.Combine(directory, "source");
            Directory.CreateDirectory(Path.Combine(source, "资料", "空目录"));
            Dictionary<string, byte[]> expected = new(StringComparer.Ordinal)
            {
                ["资料/说明.txt"] = Encoding.UTF8.GetBytes("蓝光 UDF 内圈文件\r\n"),
                ["empty.bin"] = [],
                ["several-blocks.bin"] = Enumerable.Range(0, 44001).Select(i => (byte)(i * 73)).ToArray()
            };
            // Enough FIDs to cross directory blocks, including non-ASCII CS0 strings.
            for (int index = 0; index < 40; index++)
                expected.Add($"资料/文件-{index:D2}-a-name-long-enough-to-span-a-directory-block.txt", [(byte)index]);
            foreach ((string path, byte[] bytes) in expected)
                File.WriteAllBytes(Path.Combine(source, path.Replace('/', Path.DirectorySeparatorChar)), bytes);

            BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(new(TotalSectors: 1024), source, "蓝光_UDF");
            RecordingMapping mapping = new();
            using ForwardOnlyStream streamed = new();
            BluRayIsoGenerator.GenerateToStream(streamed, plan, mapping);
            byte[] image = streamed.Bytes;
            Dictionary<string, byte[]> actual = Inspect(image, plan.TotalSectors, "蓝光_UDF", plan.DrawingStartSector,
                plan.DrawingEndSectorExclusive);
            Require(actual.Count == expected.Count && expected.All(pair => actual.TryGetValue(pair.Key, out byte[]? bytes)
                && bytes.SequenceEqual(pair.Value)), "independent traversal restores nested Unicode, empty and multi-block files");
            Require(mapping.FirstSector == plan.DrawingStartSector && mapping.LastSector + 1 == plan.DrawingEndSectorExclusive,
                "drawing mapping excludes both UDF prefix and reserve-anchor suffix");
            string output = Path.Combine(directory, "small-udf.iso");
            BluRayIsoGenerator.GenerateFile(output, plan, new RecordingMapping());
            Require(new FileInfo(output).Length == 2 * 1024 * 1024 && File.ReadAllBytes(output).SequenceEqual(image),
                "two MiB file and forward-only burn producer are byte-identical");
            using (CancellationTokenSource cancellation = new())
            {
                using ForwardOnlyStream cancelled = new();
                bool observed = false;
                try
                {
                    BluRayIsoGenerator.GenerateToStream(cancelled, plan, new RecordingMapping(),
                        new InlineProgress(value =>
                        {
                            if (value.CompletedBytes >= (long)plan.DrawingEndSectorExclusive * SectorSize)
                                cancellation.Cancel();
                        }), cancellation.Token);
                }
                catch (OperationCanceledException) { observed = true; }
                Require(observed && cancelled.Bytes.LongLength == (long)plan.DrawingEndSectorExclusive * SectorSize,
                    "cancellation at drawing completion prevents writing the UDF suffix to a forward-only burn stream");
            }

            BluRayIsoPlan empty = BluRayIsoGenerator.Prepare(new(TotalSectors: 640));
            using ForwardOnlyStream emptyOutput = new();
            BluRayIsoGenerator.GenerateToStream(emptyOutput, empty, new RecordingMapping());
            Require(Inspect(emptyOutput.Bytes, empty.TotalSectors, "BD_IMAGE", empty.DrawingStartSector,
                empty.DrawingEndSectorExclusive).Count == 0, "minimum small test volume contains a valid empty root");

            byte[] damaged = (byte[])image.Clone();
            damaged[256 * SectorSize + 4] ^= 1;
            Rejects(() => Inspect(damaged, plan.TotalSectors));
            damaged = (byte[])image.Clone();
            damaged[256 * SectorSize + 32] ^= 1;
            Rejects(() => Inspect(damaged, plan.TotalSectors));
            Console.WriteLine("udf-filesystem: passed (independent descriptor/CRC/extent reader, Unicode files, anchors, 2 MiB maximum ISO)");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void TestMetadataAlignment(string directory)
    {
        string source = Path.Combine(directory, "alignment-source");
        Directory.CreateDirectory(source);
        for (int index = 0; index < 47; index++)
            File.WriteAllBytes(Path.Combine(source, $"file-{index:D2}.bin"), [(byte)index]);

        UdfImageWriter.LayoutPlan plan = UdfImageWriter.Plan(source, 4096, "ALIGNMENT",
            filesAtOuterRing: true, revision: UdfImageWriter.Revision.V2_50);
        using MemoryStream suffix = new();
        UdfImageWriter.WriteSequentialSuffix(plan, suffix, plan.DrawingEndLbaExclusive);
        byte[] bytes = suffix.ToArray();
        int metadataEntries = 0;
        for (int offset = 0; offset + 2048 <= bytes.Length; offset += 2048)
        {
            ReadOnlySpan<byte> entry = bytes.AsSpan(offset, 2048);
            if (U16(entry, 0) != 266 || entry[27] is not (250 or 251)) continue;
            metadataEntries++;
            Require(U32(entry, 216) % UdfImageWriter.MetadataAllocationUnitSectors == 0,
                "UDF 2.50 metadata content is rounded to the allocation unit");
            Require(U32(entry, 220) % UdfImageWriter.MetadataAllocationUnitSectors == 0,
                "UDF 2.50 metadata main and mirror extents start on allocation-unit boundaries");
        }
        Require(metadataEntries == 2, "UDF 2.50 suffix contains main and mirror metadata entries");
    }

    // This reader follows ECMA-167 byte layouts and UDF 2.01 constraints, rather than
    // calling the writer's planner, descriptor builders, checksum or Unicode helpers.
    internal static Dictionary<string, byte[]> Inspect(byte[] image, uint sectors, string? label = null,
        uint? drawingStartSector = null, uint? drawingEndSector = null)
    {
        Reader reader = new(image, sectors, drawingStartSector, drawingEndSector);
        return reader.Read(label);
    }

    private sealed class Reader(byte[] image, uint sectors, uint? drawingStart, uint? drawingEnd)
    {
        private uint _partitionStart;
        private uint _partitionLength;
        private ushort _partitionNumber;
        private uint _metadataStart;
        private uint _metadataLength;
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly HashSet<uint> _visited = [];

        internal Dictionary<string, byte[]> Read(string? label)
        {
            Require(image.LongLength == (long)sectors * SectorSize, "physical image length matches requested sector count");
            string[] identifiers = ["BEA01", "NSR03", "TEA01"];
            for (int index = 0; index < identifiers.Length; index++)
            {
                ReadOnlySpan<byte> descriptor = Block((uint)(16 + index));
                Require(descriptor[0] == 0 && descriptor[6] == 1
                    && Encoding.ASCII.GetString(descriptor.Slice(1, 5)) == identifiers[index], "standard UDF volume recognition sequence");
            }

            uint mainStart = 0, mainLength = 0, reserveStart = 0, reserveLength = 0;
            foreach (uint sector in new uint[] { 256, sectors - 257, sectors - 1 })
            {
                ReadOnlySpan<byte> anchor = Block(sector);
                CheckTag(anchor, 2, sector);
                uint currentMainLength = U32(anchor, 16), currentMainStart = U32(anchor, 20);
                uint currentReserveLength = U32(anchor, 24), currentReserveStart = U32(anchor, 28);
                if (sector == 256)
                {
                    mainStart = currentMainStart; mainLength = currentMainLength;
                    reserveStart = currentReserveStart; reserveLength = currentReserveLength;
                }
                else Require(currentMainStart == mainStart && currentMainLength == mainLength
                    && currentReserveStart == reserveStart && currentReserveLength == reserveLength, "all three anchors refer to identical descriptor sequences");
            }
            Dictionary<ushort, byte[]> main = ReadSequence(mainStart, mainLength);
            Dictionary<ushort, byte[]> reserve = ReadSequence(reserveStart, reserveLength);
            Require(main.Keys.Order().SequenceEqual(reserve.Keys.Order()), "main and reserve sequence descriptor sets match");
            foreach ((ushort type, byte[] descriptor) in main)
                Require(descriptor.AsSpan(16).SequenceEqual(reserve[type].AsSpan(16)), "reserve sequence preserves complete volume descriptor bodies");
            Require(main.ContainsKey(1) && main.ContainsKey(4) && main.ContainsKey(5) && main.ContainsKey(6) && main.ContainsKey(8),
                "both sequences include PVD, implementation, partition, logical volume and terminator descriptors");

            ReadOnlySpan<byte> pvd = main[1];
            Require(U16(pvd, 56) == 1 && U16(pvd, 58) == 1, "single-volume sequence numbers");
            Require(U16(pvd, 60) == 2 && U16(pvd, 62) == 3, "UDF primary interchange levels");
            CheckCharacterSet(pvd.Slice(200, 64));
            ReadOnlySpan<byte> partition = main[5];
            _partitionNumber = U16(partition, 22);
            Require((U16(partition, 20) & 1) != 0 && U32(partition, 184) == 1, "allocated read-only UDF partition");
            Require(Encoding.ASCII.GetString(partition.Slice(25, 23)).TrimEnd('\0') == "+NSR03", "ECMA-167 third-edition partition contents");
            _partitionStart = U32(partition, 188);
            _partitionLength = U32(partition, 192);
            Require(_partitionLength > 0 && (ulong)_partitionStart + _partitionLength <= sectors, "partition extent lies within physical image");
            if (drawingStart is { } beginning)
                Require((ulong)_partitionStart + _partitionLength <= beginning, "entire UDF partition ends before the drawing region");
            if (drawingEnd is { } end)
                Require(reserveStart >= end && sectors - 257 >= end, "reserve descriptors and tail anchors lie after drawing");

            ReadOnlySpan<byte> logical = main[6];
            CheckCharacterSet(logical.Slice(20, 64));
            Require(U32(logical, 212) == SectorSize, "logical block size equals payload sector size");
            ushort udfRevision = CheckDomain(logical.Slice(216, 32));
            Require(udfRevision is 0x0201 or 0x0250, "supported UDF revision");
            ReadOnlySpan<byte> implementation = main[4];
            CheckTag(implementation, 4, mainStart + 1);
            Require(Encoding.ASCII.GetString(implementation.Slice(21, 23)).TrimEnd('\0') == "*UDF LV Info"
                && U16(implementation, 44) == udfRevision, "implementation identifier carries the selected UDF revision");
            Require(U32(logical, 264) == (udfRevision == 0x0250 ? 70u : 6u)
                && U32(logical, 268) == (udfRevision == 0x0250 ? 2u : 1u)
                && logical[440] == 1 && logical[441] == 6 && U16(logical, 442) == 1
                && U16(logical, 444) == _partitionNumber, "one physical type-1 partition map");
            if (udfRevision == 0x0250)
            {
                ReadOnlySpan<byte> map = logical.Slice(446, 64);
                Require(map[0] == 2 && map[1] == 64 && map[58] == 1
                    && Encoding.ASCII.GetString(map.Slice(5, 23)) == "*UDF Metadata Partition"
                    && U16(map, 28) == 0x0250 && U16(map, 36) == 1 && U16(map, 38) == 0
                    && U32(map, 40) == 0
                    && U32(map, 48) == uint.MaxValue && U32(map, 52) == 32 && U16(map, 56) == 32,
                    "BD5 type-2 metadata map");
                ReadOnlySpan<byte> metadata = PartitionBlock(0);
                Require(U16(metadata, 0) == 266 && metadata[27] == 250
                    && U16(metadata, 34) == 0x20 && U32(metadata, 212) == 8
                    && U64(metadata, 56) == U64(metadata, 64)
                    && U64(metadata, 72) == U32(metadata, 216) / SectorSize,
                    "metadata file entry uses a valid EFE with short allocation descriptors");
                _metadataStart = checked(_partitionStart + U32(metadata, 220));
                _metadataLength = U32(metadata, 216) / SectorSize;
                ReadOnlySpan<byte> mirror = Block(checked(_partitionStart + U32(map, 44)));
                Require(U16(mirror, 0) == 266 && mirror[27] == 251
                    && U16(mirror, 34) == 0x20 && U32(mirror, 212) == 8
                    && U64(mirror, 56) == U64(mirror, 64)
                    && U32(mirror, 220) != U32(metadata, 220), "metadata mirror is distinct");
            }
            string logicalName = DecodeDString(logical.Slice(84, 128));
            if (label is not null) Require(logicalName == label, "UDF logical volume label round trips through CS0");

            uint integrityStart = U32(logical, 436), integrityLength = U32(logical, 432);
            Require(integrityLength >= 2 * SectorSize && (ulong)integrityStart + integrityLength / SectorSize <= sectors,
                "integrity sequence extent is in volume");
            ReadOnlySpan<byte> integrity = Block(integrityStart);
            CheckTag(integrity, 9, integrityStart);
            Require(U32(integrity, 28) == 1 && U32(integrity, 72) == (udfRevision == 0x0250 ? 2u : 1u), "closed logical volume partition count");
            if (udfRevision == 0x0250)
                Require(U32(integrity, 80) == 0 && U32(integrity, 84) == 0
                    && U32(integrity, 88) == _partitionLength && U32(integrity, 92) == _metadataLength,
                    "integrity size/free tables include metadata partition");
            else Require(U32(integrity, 80) == 0 && U32(integrity, 84) == _partitionLength, "integrity tables match partition");
            CheckTag(Block(integrityStart + 1), 8, integrityStart + 1);

            uint fileSetBlock = ReadLongAllocation(logical.Slice(248, 16), out uint fileSetLength, out ushort fileSetMap);
            Require(fileSetLength >= 512, "file set descriptor referenced with sufficient extent length");
            ReadOnlySpan<byte> fileSet = PartitionBlock(fileSetBlock, fileSetMap);
            CheckTag(fileSet, 256, fileSetBlock);
            CheckTag(PartitionBlock(fileSetBlock + 1, fileSetMap), 8, fileSetBlock + 1);
            CheckCharacterSet(fileSet.Slice(48, 64));
            Require(DecodeDString(fileSet.Slice(112, 128)) == logicalName, "file set belongs to the selected logical volume");
            Require(CheckDomain(fileSet.Slice(416, 32)) == udfRevision,
                "file set domain revision matches the logical volume");
            uint root = ReadLongAllocation(fileSet.Slice(400, 16), out uint rootLength, out ushort rootMap);
            Require(rootLength == SectorSize, "root ICB covers one logical block");
            ReadEntry(root, "", expectedDirectory: true, rootMap);
            int implementationUse = udfRevision == 0x0250 ? 96 : 88;
            Require(U32(integrity, implementationUse + 32) == _files.Count
                && U32(integrity, implementationUse + 36) == _visited.Count - _files.Count,
                "integrity implementation data matches the traversed file and directory counts");
            Require(U16(integrity, implementationUse + 40) == udfRevision
                && U16(integrity, implementationUse + 42) == udfRevision
                && U16(integrity, implementationUse + 44) == udfRevision,
                "integrity revisions match the selected UDF revision");
            return _files;
        }

        private Dictionary<ushort, byte[]> ReadSequence(uint start, uint length)
        {
            Require(length >= 16 * SectorSize && length % SectorSize == 0
                && (ulong)start + length / SectorSize <= sectors, "descriptor sequence reserves at least sixteen sectors within the volume");
            Dictionary<ushort, byte[]> descriptors = [];
            for (uint index = 0; index < length / SectorSize; index++)
            {
                ReadOnlySpan<byte> data = Block(start + index);
                ushort type = U16(data, 0);
                Require(type is >= 1 and <= 8, "volume descriptor sequence contains only volume descriptor types");
                CheckTag(data, type, start + index);
                Require(descriptors.TryAdd(type, data.ToArray()), "one prevailing descriptor of each emitted type");
                if (type == 8) return descriptors;
            }
            throw new InvalidOperationException("UDF test reader: descriptor sequence has no terminator.");
        }

        private void ReadEntry(uint block, string path, bool expectedDirectory, ushort map = 0)
        {
            Require(_visited.Add(block), "directory traversal has no ICB cycles or aliasing");
            ReadOnlySpan<byte> entry = PartitionBlock(block, map);
            CheckTag(entry, U16(entry, 0) == 266 ? (ushort)266 : (ushort)261, block);
            bool isDirectory = entry[27] == 4;
            Require(isDirectory == expectedDirectory && (isDirectory || entry[27] == 5), "FID and ICB file type agree");
            Require(U16(entry, 20) == 4 && U16(entry, 24) == 1, "ICB uses strategy type 4 with one entry");
            int allocationType = U16(entry, 34) & 7;
            Require(allocationType is 0 or 1, "regular UDF allocation descriptor kind");
            ulong informationLength = U64(entry, 56);
            bool extended = U16(entry, 0) == 266;
            uint extraAttributes = extended ? U32(entry, 208) : U32(entry, 168);
            uint allocationLength = extended ? U32(entry, 212) : U32(entry, 172);
            int offset = checked((extended ? 216 : 176) + (int)extraAttributes), size = allocationType == 0 ? 8 : 16;
            if (extended)
                Require(U64(entry, 64) == informationLength && U64(entry, 72) == (ulong)((informationLength + SectorSize - 1) / SectorSize),
                    "extended file entry object size and logical block count are valid");
            Require(offset <= SectorSize && allocationLength <= SectorSize - offset && allocationLength % size == 0,
                "file allocation table lies inside its file entry");
            using MemoryStream contents = new();
            List<uint> contentBlocks = [];
            ulong recordedBlocks = 0;
            int recordedBlocksOffset = extended ? 72 : 64;
            for (int cursor = offset; cursor < offset + allocationLength; cursor += size)
            {
                uint encodedLength = U32(entry, cursor), extentLength = encodedLength & 0x3fffffff;
                uint position = U32(entry, cursor + 4);
                Require((encodedLength >> 30) == 0 && extentLength is > 0 and <= 0x3ffff800,
                    "data extent is recorded and respects the UDF maximum length");
                ushort extentMap = size == 16 ? U16(entry, cursor + 8)
                    : extended && allocationType == 0 ? (ushort)1 : (ushort)0;
                uint blocks = (extentLength + SectorSize - 1) / SectorSize;
                uint extentStart = extentMap == 1 ? _metadataStart : _partitionStart;
                uint extentLimit = extentMap == 1 ? _metadataLength : _partitionLength;
                Require((ulong)position + blocks <= extentLimit, "file data extent is contained in its partition map");
                Require((ulong)contents.Length + extentLength <= informationLength,
                    $"extent chain does not overrun the information length (entry {block}, map {extentMap}, info {informationLength}, content {contents.Length}, extent {extentLength})");
                contents.Write(image.AsSpan(checked((int)(extentStart + position) * SectorSize), checked((int)extentLength)));
                for (uint index = 0; index < blocks; index++) contentBlocks.Add(position + index);
                recordedBlocks += blocks;
            }
            Require((ulong)contents.Length == informationLength && recordedBlocks == U64(entry, recordedBlocksOffset),
                "64-bit information length and recorded-block count match the allocation chain");
            byte[] bytes = contents.ToArray();
            if (!isDirectory) { _files.Add(path, bytes); return; }
            int parentEntries = 0;
            for (int cursor = 0; cursor < bytes.Length;)
            {
                if (bytes.AsSpan(cursor, Math.Min(16, bytes.Length - cursor)).IndexOfAnyExcept((byte)0) < 0)
                { cursor = (cursor / SectorSize + 1) * SectorSize; continue; }
                Require(bytes.Length - cursor >= 38, "complete file identifier header");
                int nameLength = bytes[cursor + 19], implementationLength = U16(bytes, cursor + 36);
                int fidLength = (38 + implementationLength + nameLength + 3) & ~3;
                Require(cursor + fidLength <= bytes.Length, "complete file identifier payload");
                ReadOnlySpan<byte> fid = bytes.AsSpan(cursor, fidLength);
                CheckTag(fid, 257, contentBlocks[cursor / SectorSize]);
                Require(U16(fid, 16) == 1 && (fid[18] & 4) == 0, "file identifier version and live status");
                uint child = ReadLongAllocation(fid.Slice(20, 16), out uint childLength, out ushort childMap);
                Require(childLength == SectorSize, "child ICB occupies one block");
                if ((fid[18] & 8) != 0)
                {
                    Require(nameLength == 0 && (fid[18] & 2) != 0, "parent FID has no name and denotes a directory");
                    parentEntries++;
                }
                else
                {
                    string name = DecodeCs0(fid.Slice(38 + implementationLength, nameLength));
                    Require(name.Length > 0 && !name.Contains('/') && !name.Contains('\\'), "valid CS0 path component");
                    ReadEntry(child, path.Length == 0 ? name : path + "/" + name, (fid[18] & 2) != 0, childMap);
                }
                cursor += fidLength;
            }
            Require(parentEntries == 1, "directory has exactly one parent FID, including the root");
        }

        private uint ReadLongAllocation(ReadOnlySpan<byte> data, out uint length, out ushort map)
        {
            uint rawLength = U32(data, 0);
            map = U16(data, 8);
            Require((rawLength >> 30) == 0 && map is 0 or 1, "long allocation descriptor references a known partition map");
            length = rawLength & 0x3fffffff;
            uint block = U32(data, 4);
            uint limit = map == 1 ? _metadataLength : _partitionLength;
            Require((ulong)block + (length + SectorSize - 1) / SectorSize <= limit,
                "long allocation descriptor lies inside its partition map");
            return block;
        }

        private ReadOnlySpan<byte> Block(uint sector)
        {
            Require(sector < sectors, "descriptor address lies within the volume");
            return image.AsSpan(checked((int)sector * SectorSize), SectorSize);
        }

        private ReadOnlySpan<byte> PartitionBlock(uint block, ushort map = 0)
        {
            uint start = map == 1 ? _metadataStart : _partitionStart;
            uint length = map == 1 ? _metadataLength : _partitionLength;
            Require(block < length, "metadata block lies within partition map");
            return Block(checked(start + block));
        }
    }

    private static void CheckCharacterSet(ReadOnlySpan<byte> data) => Require(data[0] == 0
        && Encoding.ASCII.GetString(data[1..]).TrimEnd('\0') == "OSTA Compressed Unicode", "OSTA CS0 character set declaration");

    private static ushort CheckDomain(ReadOnlySpan<byte> data)
    {
        Require(data[0] == 0
        && Encoding.ASCII.GetString(data.Slice(1, 23)).TrimEnd('\0') == "*OSTA UDF Compliant"
        && U16(data, 24) is 0x0201 or 0x0250, "OSTA UDF domain identifier");
        return U16(data, 24);
    }

    private static string DecodeDString(ReadOnlySpan<byte> data)
    {
        int length = data[^1];
        Require(length < data.Length, "D-string used length fits fixed field");
        return length == 0 ? "" : DecodeCs0(data[..length]);
    }

    private static string DecodeCs0(ReadOnlySpan<byte> data)
    {
        Require(data.Length > 0 && data[0] is 8 or 16, "OSTA compressed Unicode encoding identifier");
        if (data[0] == 8) return Encoding.Latin1.GetString(data[1..]);
        Require((data.Length & 1) == 1, "16-bit compressed Unicode contains complete code units");
        return Encoding.BigEndianUnicode.GetString(data[1..]);
    }

    private static void CheckTag(ReadOnlySpan<byte> descriptor, ushort type, uint location)
    {
        Require(descriptor.Length >= 16 && U16(descriptor, 0) == type && U16(descriptor, 2) == 3
            && descriptor[5] == 0 && U32(descriptor, 12) == location,
            $"descriptor type/version/location and reserved byte (expected {type}@{location}, got {U16(descriptor, 0)}@{U32(descriptor, 12)})");
        int checksum = 0;
        for (int index = 0; index < 16; index++) if (index != 4) checksum += descriptor[index];
        Require((byte)checksum == descriptor[4], "descriptor tag checksum");
        int length = U16(descriptor, 10);
        Require(length <= descriptor.Length - 16, "descriptor CRC length is in bounds");
        uint descriptorLength = type switch
        {
            1 or 2 or 4 or 5 or 8 or 256 => 512,
            6 => 440 + U32(descriptor, 264),
            7 => 24 + 8 * U32(descriptor, 20),
            9 => 80 + 8 * U32(descriptor, 72) + U32(descriptor, 76),
            257 => checked((uint)descriptor.Length),
            261 => 176 + U32(descriptor, 168) + U32(descriptor, 172),
            266 => 216 + U32(descriptor, 208) + U32(descriptor, 212),
            _ => throw new InvalidOperationException("Unexpected UDF descriptor type.")
        };
        Require(length == descriptorLength - 16, $"CRC covers the complete descriptor body (type {type}, crc {length}, body {descriptorLength - 16})");
        ushort crc = 0;
        foreach (byte value in descriptor.Slice(16, length))
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        Require(crc == U16(descriptor, 8), "descriptor CRC-16-CCITT");
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);

    private sealed class RecordingMapping : IBluRayPayloadMapping
    {
        internal uint FirstSector { get; private set; } = uint.MaxValue;
        internal uint LastSector { get; private set; }
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            FirstSector = Math.Min(FirstSector, context.Sector);
            LastSector = context.Sector;
            destination.Fill((byte)(context.Sector * 19));
        }
    }

    private sealed class InlineProgress(Action<BluRayStreamingProgress> action) : IProgress<BluRayStreamingProgress>
    { public void Report(BluRayStreamingProgress value) => action(value); }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _bytes = new();
        internal byte[] Bytes => _bytes.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _bytes.Write(buffer, offset, count);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("UDF test reader: " + message); }

    private static void Rejects(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("UDF test reader accepted deliberately corrupt metadata.");
    }
}
