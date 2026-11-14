using System.Buffers.Binary;
using System.Text;

namespace DiscImageStudio.Iso;

internal readonly record struct IsoByteExtent(long ByteOffset, long ByteCount);

internal sealed class IsoTreeNode(string name, bool isDirectory, long dataLength, DateTimeOffset timestamp)
{
    public string Name { get; } = name;
    public bool IsDirectory { get; } = isDirectory;
    public long DataLength { get; set; } = dataLength;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public List<IsoTreeNode> Children { get; } = [];
    public List<IsoByteExtent> Extents { get; } = [];
}

internal sealed record Iso9660DescriptorIdentifiers(
    string SystemIdentifier,
    string VolumeSetIdentifier,
    string PublisherIdentifier,
    string DataPreparerIdentifier,
    string ApplicationIdentifier,
    string CopyrightFileIdentifier,
    string AbstractFileIdentifier,
    string BibliographicFileIdentifier,
    byte[]? CreationDateRaw,
    byte[]? ModificationDateRaw,
    byte[]? ExpirationDateRaw,
    byte[]? EffectiveDateRaw);

internal sealed record IsoVolumeMetadata(
    bool HasIso9660Pvd,
    bool HasJolietSvd,
    bool HasRockRidge,
    bool HasUdf,
    string PrimaryVolumeLabel,
    string JolietVolumeLabel,
    string UdfVolumeLabel,
    Iso9660DescriptorIdentifiers? PrimaryIdentifiers,
    Iso9660DescriptorIdentifiers? JolietIdentifiers,
    string UdfVolumeSetIdentifier,
    string UdfLvInfo1,
    string UdfLvInfo2,
    string UdfLvInfo3,
    DateTimeOffset? UdfRecordingTimestamp);

internal sealed class IsoVolumeTree(
    string sourceIsoPath,
    long sourceIsoBytes,
    DateTimeOffset sourceIsoLastWriteUtc,
    string volumeLabel,
    IsoTreeNode root,
    IsoVolumeMetadata metadata)
{
    public string SourceIsoPath { get; } = sourceIsoPath;
    public long SourceIsoBytes { get; } = sourceIsoBytes;
    public DateTimeOffset SourceIsoLastWriteUtc { get; } = sourceIsoLastWriteUtc;
    public string VolumeLabel { get; } = volumeLabel;
    public IsoTreeNode Root { get; } = root;
    public IsoVolumeMetadata Metadata { get; } = metadata;
}

internal static class IsoContentReader
{
    private const int SectorSize = 2048;
    private const int MaxDirectoryDepth = 256;
    private const int MaxContinuationDepth = 16;

    public static (long Length, DateTimeOffset LastWriteUtc) ValidateSourceIso(string sourceIsoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIsoPath);
        string fullPath = Path.GetFullPath(sourceIsoPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Source ISO file was not found.", fullPath);

        FileInfo info = new(fullPath);
        if (info.Length <= 0)
            throw new InvalidDataException($"Source ISO file '{fullPath}' is empty.");

        return (info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    public static IsoVolumeTree Read(string inputIsoPath)
    {
        (long fileLength, DateTimeOffset lastWriteUtc) = ValidateSourceIso(inputIsoPath);
        string fullPath = Path.GetFullPath(inputIsoPath);
        if (fileLength < 17L * SectorSize)
            throw new InvalidDataException($"Source ISO '{fullPath}' does not contain a readable UDF or ISO-9660 filesystem.");

        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);

        bool hasIso9660Pvd = TryFindIso9660Descriptors(stream, fileLength, out byte[]? pvd, out byte[]? svdJoliet);
        bool hasRockRidge = hasIso9660Pvd && DetectRockRidge(stream, fileLength, pvd!);
        ExtractIso9660Metadata(
            pvd,
            svdJoliet,
            out string primaryLabel,
            out string jolietLabel,
            out Iso9660DescriptorIdentifiers? primaryIds,
            out Iso9660DescriptorIdentifiers? jolietIds);

        if (TryReadUdf(stream, fileLength, out UdfVolumeInfo udfInfo, out IsoTreeNode? udfRoot) && udfRoot is not null)
        {
            IsoVolumeMetadata metadata = new(
                HasIso9660Pvd: hasIso9660Pvd,
                HasJolietSvd: svdJoliet is not null,
                HasRockRidge: hasRockRidge,
                HasUdf: true,
                PrimaryVolumeLabel: primaryLabel,
                JolietVolumeLabel: jolietLabel,
                UdfVolumeLabel: udfInfo.VolumeLabel,
                PrimaryIdentifiers: primaryIds,
                JolietIdentifiers: jolietIds,
                UdfVolumeSetIdentifier: udfInfo.VolumeSetIdentifier,
                UdfLvInfo1: udfInfo.LvInfo1,
                UdfLvInfo2: udfInfo.LvInfo2,
                UdfLvInfo3: udfInfo.LvInfo3,
                UdfRecordingTimestamp: udfInfo.RecordingTimestamp);
            return new IsoVolumeTree(fullPath, fileLength, lastWriteUtc, udfInfo.VolumeLabel, udfRoot, metadata);
        }

        if (hasIso9660Pvd && TryReadIso9660Tree(stream, fileLength, pvd!, svdJoliet, hasRockRidge, out IsoTreeNode? isoRoot)
            && isoRoot is not null)
        {
            string fallbackLabel = hasRockRidge || string.IsNullOrWhiteSpace(jolietLabel)
                ? primaryLabel
                : jolietLabel;
            IsoVolumeMetadata metadata = new(
                HasIso9660Pvd: true,
                HasJolietSvd: svdJoliet is not null,
                HasRockRidge: hasRockRidge,
                HasUdf: false,
                PrimaryVolumeLabel: primaryLabel,
                JolietVolumeLabel: jolietLabel,
                UdfVolumeLabel: string.Empty,
                PrimaryIdentifiers: primaryIds,
                JolietIdentifiers: jolietIds,
                UdfVolumeSetIdentifier: string.Empty,
                UdfLvInfo1: string.Empty,
                UdfLvInfo2: string.Empty,
                UdfLvInfo3: string.Empty,
                UdfRecordingTimestamp: null);
            return new IsoVolumeTree(fullPath, fileLength, lastWriteUtc, fallbackLabel, isoRoot, metadata);
        }

        throw new InvalidDataException($"Source ISO '{fullPath}' does not contain a readable UDF or ISO-9660 filesystem.");
    }

    public static void CopyExtents(
        FileStream isoStream,
        IReadOnlyList<IsoByteExtent> extents,
        long expectedLength,
        Stream output,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        long written = 0;
        for (int i = 0; i < extents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsoByteExtent extent = extents[i];
            long remaining = Math.Min(extent.ByteCount, Math.Max(0, expectedLength - written));
            if (remaining <= 0)
                continue;

            if (extent.ByteOffset < 0)
            {
                Array.Clear(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int chunk = (int)Math.Min(buffer.Length, remaining);
                    output.Write(buffer, 0, chunk);
                    remaining -= chunk;
                    written += chunk;
                }

                continue;
            }

            if (isoStream.Position != extent.ByteOffset)
                isoStream.Position = extent.ByteOffset;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = isoStream.Read(buffer, 0, toRead);
                if (read <= 0)
                    throw new EndOfStreamException("Source ISO ended unexpectedly while reading file extent.");

                output.Write(buffer, 0, read);
                remaining -= read;
                written += read;
            }
        }

        if (written != expectedLength)
            throw new EndOfStreamException($"Source ISO extent data ended early: expected {expectedLength} bytes, read {written} bytes.");
    }

    private sealed class UdfPartitionState(
        ushort partitionNumber,
        uint startSector,
        uint sectorCount)
    {
        public ushort PartitionNumber { get; } = partitionNumber;
        public uint StartSector { get; } = startSector;
        public uint SectorCount { get; } = sectorCount;
        public List<IsoByteExtent>? MetadataFileExtents { get; set; }
    }

    private sealed class UdfParseContext(FileStream stream, long fileLength)
    {
        public FileStream Stream { get; } = stream;
        public long FileLength { get; } = fileLength;
        public int BlockSize { get; set; } = SectorSize;
        public Dictionary<ushort, (uint StartSector, uint SectorCount)> PhysicalPartitions { get; } = [];
        public Dictionary<int, UdfPartitionState> PartitionMap { get; } = [];
    }

    private readonly record struct UdfVolumeInfo(
        string VolumeLabel,
        string VolumeSetIdentifier,
        string LvInfo1,
        string LvInfo2,
        string LvInfo3,
        DateTimeOffset? RecordingTimestamp);

    private static bool TryReadUdf(FileStream stream, long fileLength, out UdfVolumeInfo volumeInfo, out IsoTreeNode? root)
    {
        volumeInfo = default;
        root = null;

        long totalSectors = fileLength / SectorSize;
        if (totalSectors <= 256 && totalSectors < 32)
            return false;

        bool hasNsr = HasUdfVrsMarker(stream, fileLength);
        Span<byte> anchor = stackalloc byte[SectorSize];
        if (!TryFindUdfAnchor(stream, fileLength, totalSectors, anchor, out uint mainVdsLba, out uint mainVdsLen,
                out uint reserveVdsLba, out uint reserveVdsLen))
        {
            return false;
        }

        if (!hasNsr && mainVdsLen == 0)
            return false;

        UdfParseContext ctx = new(stream, fileLength);
        if (!TryReadUdfVds(ctx, mainVdsLba, mainVdsLen, out volumeInfo, out uint fsdBlock, out int fsdPart, out uint fsdLen,
                out List<(int PartRef, uint MetaLoc, uint MirrorLoc)> pendingMetadata)
            && !TryReadUdfVds(ctx, reserveVdsLba, reserveVdsLen, out volumeInfo, out fsdBlock, out fsdPart, out fsdLen,
                out pendingMetadata))
        {
            return false;
        }

        foreach ((int partRef, uint metaLoc, uint mirrorLoc) in pendingMetadata)
        {
            if (!ctx.PartitionMap.TryGetValue(partRef, out UdfPartitionState? partState))
                continue;

            List<IsoByteExtent>? metaExtents = TryReadUdfMetadataFileExtents(ctx, partState.StartSector, metaLoc);
            if (metaExtents is null && mirrorLoc != uint.MaxValue)
                metaExtents = TryReadUdfMetadataFileExtents(ctx, partState.StartSector, mirrorLoc);

            if (metaExtents is null)
                return false;

            partState.MetadataFileExtents = metaExtents;
        }

        if (fsdLen == 0)
            return false;

        byte[] fsdBuf = new byte[ctx.BlockSize];
        if (!TryReadUdfLogicalBlock(ctx, fsdPart, fsdBlock, fsdBuf, out _) || !VerifyUdfTag(fsdBuf, 256, fsdBlock))
            return false;

        uint rootIcbLen = BinaryPrimitives.ReadUInt32LittleEndian(fsdBuf.AsSpan(400, 4)) & 0x3FFF_FFFF;
        uint rootIcbBlock = BinaryPrimitives.ReadUInt32LittleEndian(fsdBuf.AsSpan(404, 4));
        ushort rootIcbPart = BinaryPrimitives.ReadUInt16LittleEndian(fsdBuf.AsSpan(408, 2));
        if (rootIcbLen == 0)
            return false;

        HashSet<(int PartRef, uint Block)> visitedDirs = [(rootIcbPart, rootIcbBlock)];
        IsoTreeNode? parsedRoot = TryReadUdfDirectoryNode(ctx, string.Empty, rootIcbPart, rootIcbBlock, visitedDirs, 0);
        if (parsedRoot is null)
            return false;

        root = parsedRoot;
        return true;
    }

    private static bool HasUdfVrsMarker(FileStream stream, long fileLength)
    {
        byte[] sector = new byte[SectorSize];
        for (int lba = 16; lba < 64; lba++)
        {
            long offset = (long)lba * SectorSize;
            if (offset + SectorSize > fileLength)
                break;

            if (!ReadExactAt(stream, offset, sector))
                break;

            string id = Encoding.ASCII.GetString(sector, 1, 5);
            if (id is "NSR02" or "NSR03")
                return true;
            if (id is "TEA01")
                return false;
        }

        return false;
    }

    private static bool TryFindUdfAnchor(
        FileStream stream,
        long fileLength,
        long totalSectors,
        Span<byte> sector,
        out uint mainLba,
        out uint mainLen,
        out uint reserveLba,
        out uint reserveLen)
    {
        mainLba = 0;
        mainLen = 0;
        reserveLba = 0;
        reserveLen = 0;

        uint[] candidates = totalSectors > 257
            ? [256u, checked((uint)(totalSectors - 1)), checked((uint)(totalSectors - 257)), 512u]
            : [256u, checked((uint)(totalSectors - 1))];

        foreach (uint candidate in candidates)
        {
            if (candidate >= totalSectors)
                continue;

            long offset = (long)candidate * SectorSize;
            if (offset + SectorSize > fileLength)
                continue;

            if (!ReadExactAt(stream, offset, sector))
                continue;

            if (!VerifyUdfTag(sector, 2, candidate))
                continue;

            mainLen = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(16, 4));
            mainLba = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(20, 4));
            reserveLen = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(24, 4));
            reserveLba = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(28, 4));
            return mainLen >= SectorSize;
        }

        return false;
    }

    private static bool TryReadUdfVds(
        UdfParseContext ctx,
        uint vdsLba,
        uint vdsBytes,
        out UdfVolumeInfo volumeInfo,
        out uint fsdBlock,
        out int fsdPart,
        out uint fsdLen,
        out List<(int PartRef, uint MetaLoc, uint MirrorLoc)> pendingMetadata)
    {
        volumeInfo = default;
        fsdBlock = 0;
        fsdPart = 0;
        fsdLen = 0;
        pendingMetadata = [];
        ctx.PhysicalPartitions.Clear();
        ctx.PartitionMap.Clear();

        if (vdsBytes < SectorSize)
            return false;

        int sectorCount = (int)Math.Min(64, vdsBytes / SectorSize);
        byte[] sector = new byte[SectorSize];
        string pvdLabel = string.Empty;
        string lvdLabel = string.Empty;
        string volumeSetId = string.Empty;
        string lvInfo1 = string.Empty;
        string lvInfo2 = string.Empty;
        string lvInfo3 = string.Empty;
        DateTimeOffset? recordingTimestamp = null;
        List<(int MapIndex, int MapType, ushort PartNumber, uint MetaLoc, uint MirrorLoc)> maps = [];

        for (int i = 0; i < sectorCount; i++)
        {
            uint lba = vdsLba + (uint)i;
            long offset = (long)lba * SectorSize;
            if (offset + SectorSize > ctx.FileLength || !ReadExactAt(ctx.Stream, offset, sector))
                break;

            if (!VerifyUdfTagChecksum(sector))
                continue;

            ushort tagId = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(0, 2));
            if (tagId == 8)
                break;

            if (tagId == 1)
            {
                pvdLabel = DecodeDString(sector.AsSpan(24, 32));
                volumeSetId = DecodeDString(sector.AsSpan(72, 128));
                if (TryParseUdfTimestamp(sector.AsSpan(376, 12), out DateTimeOffset ts))
                    recordingTimestamp = ts;
            }
            else if (tagId == 4)
            {
                string entityId = Encoding.ASCII.GetString(sector, 21, 23).TrimEnd('\0');
                if (string.Equals(entityId, "*UDF LV Info", StringComparison.Ordinal))
                {
                    lvInfo1 = DecodeDString(sector.AsSpan(244, 36));
                    lvInfo2 = DecodeDString(sector.AsSpan(280, 36));
                    lvInfo3 = DecodeDString(sector.AsSpan(316, 36));
                }
            }
            else if (tagId == 5)
            {
                ushort partNumber = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(22, 2));
                uint start = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(188, 4));
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(192, 4));
                ctx.PhysicalPartitions[partNumber] = (start, length);
            }
            else if (tagId == 6)
            {
                lvdLabel = DecodeDString(sector.AsSpan(84, 128));
                uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(212, 4));
                ctx.BlockSize = blockSize is >= 512 and <= 65536 ? (int)blockSize : SectorSize;
                fsdLen = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(248, 4)) & 0x3FFF_FFFF;
                fsdBlock = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(252, 4));
                fsdPart = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(256, 2));

                uint mapTableLen = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(264, 4));
                uint mapCount = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(268, 4));
                int mapPos = 440;
                int mapEnd = Math.Min(SectorSize, 440 + (int)mapTableLen);
                for (int m = 0; m < mapCount && mapPos + 2 <= mapEnd; m++)
                {
                    byte mapType = sector[mapPos];
                    byte mapLen = sector[mapPos + 1];
                    if (mapLen < 2 || mapPos + mapLen > mapEnd)
                        break;

                    if (mapType == 1 && mapLen >= 6)
                    {
                        ushort partNum = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(mapPos + 4, 2));
                        maps.Add((m, 1, partNum, 0, uint.MaxValue));
                    }
                    else if (mapType == 2 && mapLen >= 64)
                    {
                        string entityId = Encoding.ASCII.GetString(sector, mapPos + 5, 23).TrimEnd('\0');
                        ushort partNum = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(mapPos + 38, 2));
                        if (entityId.StartsWith("*UDF Metadata Partition", StringComparison.Ordinal))
                        {
                            uint metaLoc = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(mapPos + 40, 4));
                            uint mirrorLoc = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(mapPos + 44, 4));
                            maps.Add((m, 2, partNum, metaLoc, mirrorLoc));
                        }
                        else if (entityId.StartsWith("*UDF Sparable Partition", StringComparison.Ordinal))
                        {
                            maps.Add((m, 1, partNum, 0, uint.MaxValue));
                        }
                        else
                        {
                            return false;
                        }
                    }

                    mapPos += mapLen;
                }
            }
        }

        if (ctx.PhysicalPartitions.Count == 0 || maps.Count == 0)
            return false;

        foreach ((int mapIndex, int mapType, ushort partNum, uint metaLoc, uint mirrorLoc) in maps)
        {
            if (!ctx.PhysicalPartitions.TryGetValue(partNum, out (uint StartSector, uint SectorCount) phys))
                return false;

            ctx.PartitionMap[mapIndex] = new UdfPartitionState(partNum, phys.StartSector, phys.SectorCount);
            if (mapType == 2)
                pendingMetadata.Add((mapIndex, metaLoc, mirrorLoc));
        }

        string volumeLabel = !string.IsNullOrWhiteSpace(lvdLabel) ? lvdLabel : pvdLabel;
        volumeInfo = new UdfVolumeInfo(volumeLabel, volumeSetId, lvInfo1, lvInfo2, lvInfo3, recordingTimestamp);
        return fsdLen > 0 && ctx.PartitionMap.ContainsKey(fsdPart);
    }

    private static List<IsoByteExtent>? TryReadUdfMetadataFileExtents(
        UdfParseContext ctx,
        uint physStartSector,
        uint metaFileBlock)
    {
        if (metaFileBlock == uint.MaxValue)
            return null;

        byte[] sector = new byte[ctx.BlockSize];
        long feOffset = ((long)physStartSector + metaFileBlock) * ctx.BlockSize;
        if (feOffset < 0 || feOffset + ctx.BlockSize > ctx.FileLength || !ReadExactAt(ctx.Stream, feOffset, sector))
            return null;

        if (!TryParseUdfFileEntryHeader(ctx, sector, metaFileBlock, feOffset, out byte fileType, out _,
                out long infoLength, out _, out List<IsoByteExtent> extents, physStartOverride: physStartSector))
        {
            return null;
        }

        if (fileType != 250 && fileType != 251 && fileType != 252)
            return null;

        _ = infoLength;
        return extents;
    }

    private static IsoTreeNode? TryReadUdfDirectoryNode(
        UdfParseContext ctx,
        string name,
        int partRef,
        uint block,
        HashSet<(int PartRef, uint Block)> visitedDirs,
        int depth)
    {
        if (depth > MaxDirectoryDepth)
            return null;

        byte[] sector = new byte[ctx.BlockSize];
        if (!TryReadUdfLogicalBlock(ctx, partRef, block, sector, out long feOffset))
            return null;

        if (!TryParseUdfFileEntryHeader(ctx, sector, block, feOffset, out byte fileType, out DateTimeOffset timestamp,
                out long infoLength, out ushort defaultPartRef, out List<IsoByteExtent> dirExtents,
                defaultPartOverride: partRef))
        {
            return null;
        }

        if (fileType != 4)
            return null;

        _ = defaultPartRef;
        if (infoLength > 64L * 1024 * 1024)
            throw new InvalidDataException("UDF directory stream exceeds safe metadata bounds.");

        byte[] dirBytes = ReadUdfExtentsToArray(ctx, dirExtents, (int)infoLength);
        IsoTreeNode dirNode = new(name, isDirectory: true, dataLength: 0, timestamp);

        int pos = 0;
        while (pos + 38 <= dirBytes.Length)
        {
            if (!VerifyUdfTagChecksum(dirBytes.AsSpan(pos)) || BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos, 2)) != 257)
                break;

            byte characteristics = dirBytes[pos + 18];
            byte fiLen = dirBytes[pos + 19];
            uint icbLen = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(pos + 20, 4)) & 0x3FFF_FFFF;
            uint icbBlock = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(pos + 24, 4));
            ushort icbPart = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos + 28, 2));
            ushort iuLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos + 36, 2));

            int entryLen = (38 + iuLen + fiLen + 3) & ~3;
            if (pos + entryLen > dirBytes.Length)
                break;

            bool isParent = (characteristics & 0x08) != 0;
            bool isDeleted = (characteristics & 0x04) != 0;
            if (!isParent && !isDeleted && icbLen > 0)
            {
                string childName = DecodeCs0(dirBytes.AsSpan(pos + 38 + iuLen, fiLen));
                if (!string.IsNullOrEmpty(childName) && childName is not ("." or ".."))
                {
                    bool isDirFlag = (characteristics & 0x02) != 0;
                    if (isDirFlag)
                    {
                        if (visitedDirs.Add((icbPart, icbBlock)))
                        {
                            IsoTreeNode? childDir = TryReadUdfDirectoryNode(ctx, childName, icbPart, icbBlock, visitedDirs, depth + 1);
                            if (childDir is not null)
                                dirNode.Children.Add(childDir);
                        }
                    }
                    else
                    {
                        IsoTreeNode? childFile = TryReadUdfFileNode(ctx, childName, icbPart, icbBlock);
                        if (childFile is not null)
                            dirNode.Children.Add(childFile);
                    }
                }
            }

            pos += entryLen;
        }

        return dirNode;
    }

    private static IsoTreeNode? TryReadUdfFileNode(UdfParseContext ctx, string name, int partRef, uint block)
    {
        byte[] sector = new byte[ctx.BlockSize];
        if (!TryReadUdfLogicalBlock(ctx, partRef, block, sector, out long feOffset))
            return null;

        if (!TryParseUdfFileEntryHeader(ctx, sector, block, feOffset, out byte fileType, out DateTimeOffset timestamp,
                out long infoLength, out _, out List<IsoByteExtent> extents, defaultPartOverride: partRef))
        {
            return null;
        }

        if (fileType == 12)
            return null;

        if (fileType != 5 && fileType != 0)
            return null;

        IsoTreeNode fileNode = new(name, isDirectory: false, infoLength, timestamp);
        AppendClippedExtents(fileNode.Extents, extents, infoLength);
        return fileNode;
    }

    private static bool TryParseUdfFileEntryHeader(
        UdfParseContext ctx,
        ReadOnlySpan<byte> sector,
        uint expectedBlock,
        long fePhysicalOffset,
        out byte fileType,
        out DateTimeOffset timestamp,
        out long infoLength,
        out ushort defaultPartRef,
        out List<IsoByteExtent> extents,
        int defaultPartOverride = 0,
        uint? physStartOverride = null)
    {
        fileType = 0;
        timestamp = DateTimeOffset.UnixEpoch;
        infoLength = 0;
        defaultPartRef = checked((ushort)defaultPartOverride);
        extents = [];

        if (!VerifyUdfTagChecksum(sector))
            return false;

        ushort tagId = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(0, 2));
        if (tagId is not (261 or 266))
            return false;

        uint tagLoc = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(12, 4));
        if (tagLoc != expectedBlock)
            return false;

        ushort icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(34, 2));
        int allocType = icbFlags & 0x07;
        fileType = sector[27];
        infoLength = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(sector.Slice(56, 8)));

        int lEa;
        int lAd;
        int baseBody;
        if (tagId == 261)
        {
            timestamp = ParseUdfTimestamp(sector.Slice(84, 12));
            lEa = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(168, 4)));
            lAd = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(172, 4)));
            baseBody = 176;
        }
        else
        {
            timestamp = ParseUdfTimestamp(sector.Slice(92, 12));
            lEa = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(208, 4)));
            lAd = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(212, 4)));
            baseBody = 216;
        }

        int adStart = baseBody + lEa;
        if (lEa < 0 || lAd < 0 || adStart > sector.Length || adStart + lAd > sector.Length)
            return false;

        if (allocType == 3)
        {
            if (infoLength > 0)
            {
                long len = Math.Min(lAd, infoLength);
                extents.Add(new IsoByteExtent(fePhysicalOffset + adStart, len));
            }
            return true;
        }

        return TryParseUdfAllocationDescriptors(
            ctx,
            sector.Slice(adStart, lAd),
            allocType,
            defaultPartOverride,
            physStartOverride,
            extents,
            0);
    }

    private static bool TryParseUdfAllocationDescriptors(
        UdfParseContext ctx,
        ReadOnlySpan<byte> adBytes,
        int allocType,
        int defaultPartRef,
        uint? physStartOverride,
        List<IsoByteExtent> outputExtents,
        int chainDepth)
    {
        if (chainDepth > 64)
            return false;

        int stride = allocType switch
        {
            0 => 8,
            1 => 16,
            2 => 20,
            _ => 0,
        };
        if (stride == 0)
            return false;

        for (int pos = 0; pos + stride <= adBytes.Length; pos += stride)
        {
            uint rawLen = BinaryPrimitives.ReadUInt32LittleEndian(adBytes.Slice(pos, 4));
            int extentType = (int)((rawLen >> 30) & 0x03);
            uint extentBytes = rawLen & 0x3FFF_FFFF;
            if (extentBytes == 0)
                break;

            uint block;
            int partRef = defaultPartRef;
            if (allocType == 0)
            {
                block = BinaryPrimitives.ReadUInt32LittleEndian(adBytes.Slice(pos + 4, 4));
            }
            else if (allocType == 1)
            {
                block = BinaryPrimitives.ReadUInt32LittleEndian(adBytes.Slice(pos + 4, 4));
                partRef = BinaryPrimitives.ReadUInt16LittleEndian(adBytes.Slice(pos + 8, 2));
            }
            else
            {
                block = BinaryPrimitives.ReadUInt32LittleEndian(adBytes.Slice(pos + 12, 4));
                partRef = BinaryPrimitives.ReadUInt16LittleEndian(adBytes.Slice(pos + 16, 2));
            }

            if (extentType == 3)
            {
                byte[] aed = new byte[ctx.BlockSize];
                bool readOk = physStartOverride is { } physStart
                    ? ReadExactAt(ctx.Stream, ((long)physStart + block) * ctx.BlockSize, aed)
                    : TryReadUdfLogicalBlock(ctx, partRef, block, aed, out _);

                if (!readOk || !VerifyUdfTag(aed, 258, block))
                    return false;

                int contLen = Math.Min(ctx.BlockSize - 24, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(aed.AsSpan(20, 4))));
                if (contLen < 0)
                    return false;

                return TryParseUdfAllocationDescriptors(
                    ctx,
                    aed.AsSpan(24, contLen),
                    allocType,
                    defaultPartRef,
                    physStartOverride,
                    outputExtents,
                    chainDepth + 1);
            }

            if (extentType is 1 or 2)
            {
                outputExtents.Add(new IsoByteExtent(-1, extentBytes));
                continue;
            }

            if (physStartOverride is { } physStartSector)
            {
                long offset = ((long)physStartSector + block) * ctx.BlockSize;
                if (offset < 0 || offset + extentBytes > ctx.FileLength)
                    return false;
                outputExtents.Add(new IsoByteExtent(offset, extentBytes));
            }
            else if (!AppendUdfPartitionSlice(ctx, partRef, block, extentBytes, outputExtents))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AppendUdfPartitionSlice(
        UdfParseContext ctx,
        int partRef,
        uint startBlock,
        long byteCount,
        List<IsoByteExtent> output)
    {
        if (!ctx.PartitionMap.TryGetValue(partRef, out UdfPartitionState? state))
            return false;

        if (state.MetadataFileExtents is null)
        {
            long offset = ((long)state.StartSector + startBlock) * ctx.BlockSize;
            if (offset < 0 || offset + byteCount > ctx.FileLength)
                return false;
            output.Add(new IsoByteExtent(offset, byteCount));
            return true;
        }

        long logicalOffset = (long)startBlock * ctx.BlockSize;
        long remaining = byteCount;
        long cursor = 0;

        foreach (IsoByteExtent metaSlice in state.MetadataFileExtents)
        {
            if (remaining <= 0)
                break;

            long sliceEnd = cursor + metaSlice.ByteCount;
            if (logicalOffset < sliceEnd)
            {
                long innerOffset = logicalOffset - cursor;
                long take = Math.Min(remaining, metaSlice.ByteCount - innerOffset);
                output.Add(new IsoByteExtent(metaSlice.ByteOffset < 0 ? -1 : metaSlice.ByteOffset + innerOffset, take));
                logicalOffset += take;
                remaining -= take;
            }

            cursor = sliceEnd;
        }

        return remaining == 0;
    }

    private static bool TryReadUdfLogicalBlock(
        UdfParseContext ctx,
        int partRef,
        uint block,
        byte[] buffer,
        out long physicalOffset)
    {
        physicalOffset = -1;
        List<IsoByteExtent> single = [];
        if (!AppendUdfPartitionSlice(ctx, partRef, block, ctx.BlockSize, single) || single.Count != 1 || single[0].ByteOffset < 0)
            return false;

        physicalOffset = single[0].ByteOffset;
        return ReadExactAt(ctx.Stream, physicalOffset, buffer);
    }

    private static byte[] ReadUdfExtentsToArray(UdfParseContext ctx, IReadOnlyList<IsoByteExtent> extents, int byteLength)
    {
        byte[] data = new byte[byteLength];
        int written = 0;
        for (int i = 0; i < extents.Count && written < byteLength; i++)
        {
            int toCopy = (int)Math.Min(extents[i].ByteCount, byteLength - written);
            if (toCopy <= 0)
                continue;

            if (extents[i].ByteOffset < 0)
            {
                Array.Clear(data, written, toCopy);
            }
            else if (!ReadExactAt(ctx.Stream, extents[i].ByteOffset, data.AsSpan(written, toCopy)))
            {
                throw new EndOfStreamException("Unexpected end of source ISO while reading UDF directory stream.");
            }

            written += toCopy;
        }

        if (written != byteLength)
            throw new EndOfStreamException("Unexpected end of source ISO while reading UDF directory stream.");

        return data;
    }

    private static bool VerifyUdfTag(ReadOnlySpan<byte> sector, ushort expectedTagId, uint expectedLocation)
    {
        if (!VerifyUdfTagChecksum(sector))
            return false;

        ushort tagId = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(0, 2));
        uint tagLoc = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(12, 4));
        return tagId == expectedTagId && tagLoc == expectedLocation;
    }

    private static bool VerifyUdfTagChecksum(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 16)
            return false;

        int sum = 0;
        for (int i = 0; i < 4; i++)
            sum += sector[i];
        for (int i = 5; i < 16; i++)
            sum += sector[i];

        return (byte)(sum & 0xFF) == sector[4];
    }

    private static DateTimeOffset ParseUdfTimestamp(ReadOnlySpan<byte> bytes)
        => TryParseUdfTimestamp(bytes, out DateTimeOffset timestamp) ? timestamp : DateTimeOffset.UnixEpoch;

    private static bool TryParseUdfTimestamp(ReadOnlySpan<byte> bytes, out DateTimeOffset timestamp)
    {
        timestamp = DateTimeOffset.UnixEpoch;
        if (bytes.Length < 12)
            return false;

        ushort typeAndTz = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(0, 2));
        int rawTz = typeAndTz & 0x0FFF;
        int tzMinutes = (rawTz & 0x0800) != 0 ? rawTz - 0x1000 : rawTz;
        TimeSpan offset = tzMinutes is >= -840 and <= 840 ? TimeSpan.FromMinutes(tzMinutes) : TimeSpan.Zero;

        int year = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        int month = bytes[4];
        int day = bytes[5];
        int hour = bytes[6];
        int minute = bytes[7];
        int second = bytes[8];

        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        timestamp = new DateTimeOffset(year, month, day, hour, minute, second, offset);
        return true;
    }

    private static string DecodeDString(ReadOnlySpan<byte> field)
    {
        if (field.IsEmpty)
            return string.Empty;

        int len = field[^1];
        if (len <= 0 || len > field.Length - 1)
            return string.Empty;

        return DecodeCs0(field.Slice(0, len)).TrimEnd('\0');
    }

    private static string DecodeCs0(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        byte compressionId = bytes[0];
        ReadOnlySpan<byte> payload = bytes[1..];
        if (compressionId == 8)
            return Encoding.Latin1.GetString(payload).TrimEnd('\0');
        if (compressionId == 16)
            return Encoding.BigEndianUnicode.GetString(payload).TrimEnd('\0');

        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private static bool TryFindIso9660Descriptors(
        FileStream stream,
        long fileLength,
        out byte[]? pvd,
        out byte[]? svdJoliet)
    {
        pvd = null;
        svdJoliet = null;
        byte[] sector = new byte[SectorSize];

        for (int lba = 16; lba < 48; lba++)
        {
            long offset = (long)lba * SectorSize;
            if (offset + SectorSize > fileLength || !ReadExactAt(stream, offset, sector))
                break;

            if (Encoding.ASCII.GetString(sector, 1, 5) != "CD001")
                break;

            byte vdType = sector[0];
            if (vdType == 255)
                break;

            if (vdType == 1 && pvd is null)
                pvd = (byte[])sector.Clone();
            else if (vdType == 2 && svdJoliet is null && IsJolietEscape(sector.AsSpan(88, 32)))
                svdJoliet = (byte[])sector.Clone();
        }

        return pvd is not null;
    }

    private static void ExtractIso9660Metadata(
        byte[]? pvd,
        byte[]? svdJoliet,
        out string primaryLabel,
        out string jolietLabel,
        out Iso9660DescriptorIdentifiers? primaryIds,
        out Iso9660DescriptorIdentifiers? jolietIds)
    {
        primaryLabel = pvd is null ? string.Empty : DecodeIsoString(pvd, 40, 32, joliet: false);
        jolietLabel = svdJoliet is null ? string.Empty : DecodeIsoString(svdJoliet, 40, 32, joliet: true);
        primaryIds = pvd is null ? null : ReadIso9660Identifiers(pvd, joliet: false);
        jolietIds = svdJoliet is null ? null : ReadIso9660Identifiers(svdJoliet, joliet: true);
    }

    private static Iso9660DescriptorIdentifiers ReadIso9660Identifiers(byte[] sector, bool joliet)
        => new(
            SystemIdentifier: DecodeIsoString(sector, 8, 32, joliet),
            VolumeSetIdentifier: DecodeIsoString(sector, 190, 128, joliet),
            PublisherIdentifier: DecodeIsoString(sector, 318, 128, joliet),
            DataPreparerIdentifier: DecodeIsoString(sector, 446, 128, joliet),
            ApplicationIdentifier: DecodeIsoString(sector, 574, 128, joliet),
            CopyrightFileIdentifier: DecodeIsoString(sector, 702, 37, joliet),
            AbstractFileIdentifier: DecodeIsoString(sector, 739, 37, joliet),
            BibliographicFileIdentifier: DecodeIsoString(sector, 776, 37, joliet),
            CreationDateRaw: ReadIsoDateBytes(sector.AsSpan(813, 17)),
            ModificationDateRaw: ReadIsoDateBytes(sector.AsSpan(830, 17)),
            ExpirationDateRaw: ReadIsoDateBytes(sector.AsSpan(847, 17)),
            EffectiveDateRaw: ReadIsoDateBytes(sector.AsSpan(864, 17)));

    private static string DecodeIsoString(byte[] sector, int offset, int byteLength, bool joliet)
        => joliet
            ? Encoding.BigEndianUnicode.GetString(sector, offset, byteLength & ~1).TrimEnd('\0', ' ')
            : Encoding.ASCII.GetString(sector, offset, byteLength).TrimEnd('\0', ' ');

    private static byte[]? ReadIsoDateBytes(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != 17)
            return null;

        for (int i = 0; i < 16; i++)
        {
            if (raw[i] is < (byte)'0' or > (byte)'9')
                return null;
        }

        return raw.ToArray();
    }

    internal static bool TryParseIsoVolumeDate(ReadOnlySpan<byte> bytes, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (bytes.Length < 17)
            return false;

        for (int i = 0; i < 16; i++)
        {
            if (bytes[i] is < (byte)'0' or > (byte)'9')
                return false;
        }

        int year = ParseDigits(bytes.Slice(0, 4));
        int month = ParseDigits(bytes.Slice(4, 2));
        int day = ParseDigits(bytes.Slice(6, 2));
        int hour = ParseDigits(bytes.Slice(8, 2));
        int minute = ParseDigits(bytes.Slice(10, 2));
        int second = ParseDigits(bytes.Slice(12, 2));
        int centisecond = ParseDigits(bytes.Slice(14, 2));
        int tzQuarter = unchecked((sbyte)bytes[16]);
        TimeSpan offset = tzQuarter is >= -56 and <= 56 ? TimeSpan.FromMinutes(tzQuarter * 15) : TimeSpan.Zero;

        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        timestamp = new DateTimeOffset(year, month, day, hour, minute, second, centisecond * 10, offset);
        return true;
    }

    private static int ParseDigits(ReadOnlySpan<byte> digits)
    {
        int value = 0;
        for (int i = 0; i < digits.Length; i++)
            value = value * 10 + (digits[i] - '0');
        return value;
    }

    private static bool TryReadIso9660Tree(
        FileStream stream,
        long fileLength,
        byte[] pvd,
        byte[]? svdJoliet,
        bool hasRockRidge,
        out IsoTreeNode? root)
    {
        root = null;
        byte[] selectedVd = (hasRockRidge || svdJoliet is null) ? pvd : svdJoliet;
        bool useJoliet = ReferenceEquals(selectedVd, svdJoliet);

        uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(selectedVd.AsSpan(158, 4));
        uint rootLen = BinaryPrimitives.ReadUInt32LittleEndian(selectedVd.AsSpan(166, 4));
        if (rootLba == 0 || rootLen == 0)
            return false;

        DateTimeOffset rootTimestamp = ParseIsoDirectoryTimestamp(selectedVd.AsSpan(174, 7));
        HashSet<uint> visitedDirs = [rootLba];
        root = ReadIsoDirectory(stream, fileLength, string.Empty, rootLba, rootLen, rootTimestamp, useJoliet, visitedDirs, 0);
        return root is not null;
    }

    private static bool IsJolietEscape(ReadOnlySpan<byte> escapeSeq)
    {
        return escapeSeq.Length >= 3
            && escapeSeq[0] == 0x25
            && escapeSeq[1] == 0x2F
            && escapeSeq[2] is 0x40 or 0x43 or 0x45;
    }

    private static bool DetectRockRidge(FileStream stream, long fileLength, byte[] pvd)
    {
        uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
        uint rootLen = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166, 4));
        if (rootLba == 0 || rootLen < 34)
            return false;

        byte[] dir = new byte[Math.Min(rootLen, SectorSize)];
        if (!ReadExactAt(stream, (long)rootLba * SectorSize, dir))
            return false;

        int pos = 0;
        int checkedRecords = 0;
        while (pos < dir.Length && checkedRecords < 8)
        {
            byte recLen = dir[pos];
            if (recLen == 0)
                break;

            if (pos + recLen > dir.Length || recLen < 34)
                break;

            byte fiLen = dir[pos + 32];
            int sysUseOffset = 33 + fiLen + ((fiLen & 1) == 0 ? 1 : 0);
            int p = pos + sysUseOffset;
            int end = pos + recLen;
            while (p + 4 <= end)
            {
                byte sig0 = dir[p];
                byte sig1 = dir[p + 1];
                byte entryLen = dir[p + 2];
                if (entryLen < 4 || p + entryLen > end)
                    break;

                if ((sig0 == (byte)'S' && sig1 == (byte)'P' && entryLen >= 7 && dir[p + 4] == 0xBE && dir[p + 5] == 0xEF)
                    || (sig0 == (byte)'N' && sig1 == (byte)'M')
                    || (sig0 == (byte)'P' && sig1 == (byte)'X'))
                {
                    return true;
                }

                p += entryLen;
            }

            pos += recLen;
            checkedRecords++;
        }

        return false;
    }

    private sealed class ParsedIsoRecord
    {
        public required string Name { get; init; }
        public bool IsDirectory { get; init; }
        public bool IsSymlink { get; init; }
        public bool IsRelocated { get; init; }
        public bool HasMoreExtents { get; init; }
        public uint ExtentLba { get; init; }
        public uint DataLength { get; init; }
        public uint? ChildLocationLba { get; init; }
        public DateTimeOffset Timestamp { get; init; }
    }

    private static IsoTreeNode ReadIsoDirectory(
        FileStream stream,
        long fileLength,
        string name,
        uint dirLba,
        uint dirLength,
        DateTimeOffset timestamp,
        bool useJoliet,
        HashSet<uint> visitedDirs,
        int depth)
    {
        if (depth > MaxDirectoryDepth)
            throw new InvalidDataException("ISO-9660 directory tree exceeds maximum nesting depth.");

        if (dirLength > 64u * 1024 * 1024)
            throw new InvalidDataException("ISO-9660 directory table exceeds safe metadata bounds.");

        long byteOffset = (long)dirLba * SectorSize;
        if (byteOffset < 0 || byteOffset + dirLength > fileLength)
            throw new EndOfStreamException("ISO-9660 directory table extends past end of image.");

        byte[] dirBytes = new byte[dirLength];
        if (!ReadExactAt(stream, byteOffset, dirBytes))
            throw new EndOfStreamException("Unexpected end of source ISO while reading directory table.");

        IsoTreeNode dirNode = new(name, isDirectory: true, dataLength: 0, timestamp);
        List<ParsedIsoRecord> records = [];

        int pos = 0;
        while (pos < dirBytes.Length)
        {
            byte recLen = dirBytes[pos];
            if (recLen == 0)
            {
                pos = ((pos / SectorSize) + 1) * SectorSize;
                continue;
            }

            if (pos + recLen > dirBytes.Length || recLen < 34)
                break;

            ReadOnlySpan<byte> rec = dirBytes.AsSpan(pos, recLen);
            pos += recLen;

            byte fileIdLen = rec[32];
            if (33 + fileIdLen > recLen)
                continue;

            ReadOnlySpan<byte> rawFileId = rec.Slice(33, fileIdLen);
            if (fileIdLen == 1 && rawFileId[0] is 0x00 or 0x01)
                continue;

            byte flags = rec[25];
            uint extentLba = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(2, 4));
            uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(10, 4));
            DateTimeOffset recTime = ParseIsoDirectoryTimestamp(rec.Slice(18, 7));

            string baseName = useJoliet
                ? Encoding.BigEndianUnicode.GetString(rawFileId).TrimEnd('\0')
                : Encoding.ASCII.GetString(rawFileId).TrimEnd('\0');

            baseName = StripIsoVersionSuffix(baseName);

            bool isSymlink = false;
            bool isRelocated = false;
            uint? childLocationLba = null;
            StringBuilder? rockRidgeName = null;

            if (!useJoliet)
            {
                int sysUseOffset = 33 + fileIdLen + ((fileIdLen & 1) == 0 ? 1 : 0);
                if (sysUseOffset < recLen)
                {
                    ReadSuspArea(
                        stream,
                        fileLength,
                        rec[sysUseOffset..],
                        0,
                        ref rockRidgeName,
                        ref isSymlink,
                        ref isRelocated,
                        ref childLocationLba);
                }
            }

            string finalName = rockRidgeName is { Length: > 0 } ? rockRidgeName.ToString() : baseName;
            if (string.IsNullOrEmpty(finalName) || finalName is "." or "..")
                continue;

            if (isSymlink || isRelocated)
                continue;

            bool isDir = (flags & 0x02) != 0 || childLocationLba.HasValue;
            records.Add(new ParsedIsoRecord
            {
                Name = finalName,
                IsDirectory = isDir,
                HasMoreExtents = !isDir && (flags & 0x80) != 0,
                ExtentLba = extentLba,
                DataLength = dataLen,
                ChildLocationLba = childLocationLba,
                Timestamp = recTime,
            });
        }

        for (int i = 0; i < records.Count; i++)
        {
            ParsedIsoRecord record = records[i];
            if (record.IsDirectory)
            {
                uint targetDirLba = record.ChildLocationLba ?? record.ExtentLba;
                if (targetDirLba == 0 || !visitedDirs.Add(targetDirLba))
                    continue;

                uint targetDirLen = record.ChildLocationLba.HasValue
                    ? ReadIsoSelfEntryLength(stream, fileLength, targetDirLba)
                    : record.DataLength;

                if (targetDirLen == 0)
                    continue;

                dirNode.Children.Add(ReadIsoDirectory(
                    stream,
                    fileLength,
                    record.Name,
                    targetDirLba,
                    targetDirLen,
                    record.Timestamp,
                    useJoliet,
                    visitedDirs,
                    depth + 1));
                continue;
            }

            IsoTreeNode fileNode = new(record.Name, isDirectory: false, dataLength: 0, record.Timestamp);
            AppendIsoExtent(fileNode, fileLength, record);
            while (record.HasMoreExtents && i + 1 < records.Count && !records[i + 1].IsDirectory
                && string.Equals(records[i + 1].Name, record.Name, StringComparison.Ordinal))
            {
                i++;
                record = records[i];
                AppendIsoExtent(fileNode, fileLength, record);
            }

            dirNode.Children.Add(fileNode);
        }

        return dirNode;
    }

    private static void AppendIsoExtent(IsoTreeNode fileNode, long fileLength, ParsedIsoRecord record)
    {
        if (record.DataLength == 0)
            return;

        long byteOffset = (long)record.ExtentLba * SectorSize;
        if (byteOffset < 0 || byteOffset + record.DataLength > fileLength)
            throw new EndOfStreamException($"ISO-9660 file '{fileNode.Name}' extent exceeds image bounds.");

        fileNode.Extents.Add(new IsoByteExtent(byteOffset, record.DataLength));
        fileNode.DataLength += record.DataLength;
    }

    private static uint ReadIsoSelfEntryLength(FileStream stream, long fileLength, uint dirLba)
    {
        byte[] sector = new byte[SectorSize];
        long offset = (long)dirLba * SectorSize;
        if (offset < 0 || offset + SectorSize > fileLength || !ReadExactAt(stream, offset, sector))
            return 0;

        byte recLen = sector[0];
        if (recLen < 34)
            return 0;

        return BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(10, 4));
    }

    private static void ReadSuspArea(
        FileStream stream,
        long fileLength,
        ReadOnlySpan<byte> area,
        int ceDepth,
        ref StringBuilder? rockRidgeName,
        ref bool isSymlink,
        ref bool isRelocated,
        ref uint? childLocationLba)
    {
        int pos = 0;
        while (pos + 4 <= area.Length)
        {
            byte sig0 = area[pos];
            byte sig1 = area[pos + 1];
            byte len = area[pos + 2];
            if (len < 4 || pos + len > area.Length)
                break;

            ReadOnlySpan<byte> entry = area.Slice(pos, len);
            pos += len;

            if (sig0 == (byte)'N' && sig1 == (byte)'M' && len >= 5)
            {
                byte nmFlags = entry[4];
                if ((nmFlags & 0x06) == 0)
                {
                    rockRidgeName ??= new StringBuilder();
                    rockRidgeName.Append(Encoding.UTF8.GetString(entry[5..]));
                }
            }
            else if (sig0 == (byte)'P' && sig1 == (byte)'X' && len >= 12)
            {
                uint mode = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
                if ((mode & 0xF000u) == 0xA000u)
                    isSymlink = true;
            }
            else if (sig0 == (byte)'S' && sig1 == (byte)'L')
            {
                isSymlink = true;
            }
            else if (sig0 == (byte)'R' && sig1 == (byte)'E')
            {
                isRelocated = true;
            }
            else if (sig0 == (byte)'C' && sig1 == (byte)'L' && len >= 12)
            {
                childLocationLba = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
            }
            else if (sig0 == (byte)'C' && sig1 == (byte)'E' && len >= 28 && ceDepth < MaxContinuationDepth)
            {
                uint ceLba = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
                uint ceOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4));
                uint ceLen = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(20, 4));
                if (ceLen > 0 && ceOffset + ceLen <= SectorSize)
                {
                    long diskOffset = (long)ceLba * SectorSize + ceOffset;
                    if (diskOffset >= 0 && diskOffset + ceLen <= fileLength)
                    {
                        byte[] ceBuf = new byte[ceLen];
                        if (ReadExactAt(stream, diskOffset, ceBuf))
                        {
                            ReadSuspArea(
                                stream,
                                fileLength,
                                ceBuf,
                                ceDepth + 1,
                                ref rockRidgeName,
                                ref isSymlink,
                                ref isRelocated,
                                ref childLocationLba);
                        }
                    }
                }
            }
            else if (sig0 == (byte)'S' && sig1 == (byte)'T')
            {
                break;
            }
        }
    }

    private static string StripIsoVersionSuffix(string name)
    {
        int semi = name.LastIndexOf(';');
        if (semi <= 0)
            return name;

        for (int i = semi + 1; i < name.Length; i++)
        {
            if (name[i] is < '0' or > '9')
                return name;
        }

        return name[..semi];
    }

    private static DateTimeOffset ParseIsoDirectoryTimestamp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 7)
            return DateTimeOffset.UnixEpoch;

        int year = 1900 + bytes[0];
        int month = bytes[1];
        int day = bytes[2];
        int hour = bytes[3];
        int minute = bytes[4];
        int second = bytes[5];
        int tzQuarter = unchecked((sbyte)bytes[6]);
        TimeSpan offset = tzQuarter is >= -56 and <= 56 ? TimeSpan.FromMinutes(tzQuarter * 15) : TimeSpan.Zero;

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return DateTimeOffset.UnixEpoch;
        }

        return new DateTimeOffset(year, month, day, hour, minute, second, offset);
    }

    private static void AppendClippedExtents(List<IsoByteExtent> dest, IReadOnlyList<IsoByteExtent> source, long maxLength)
    {
        long accumulated = 0;
        for (int i = 0; i < source.Count && accumulated < maxLength; i++)
        {
            long take = Math.Min(source[i].ByteCount, maxLength - accumulated);
            if (take <= 0)
                continue;

            dest.Add(new IsoByteExtent(source[i].ByteOffset, take));
            accumulated += take;
        }
    }

    private static bool ReadExactAt(FileStream stream, long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > stream.Length)
            return false;

        stream.Position = offset;
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = stream.Read(destination[totalRead..]);
            if (read <= 0)
                return false;
            totalRead += read;
        }

        return true;
    }
}
