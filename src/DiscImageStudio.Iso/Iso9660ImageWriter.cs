using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using TextEncoding = System.Text.Encoding;

namespace DiscImageStudio.Iso;

public static class Iso9660ImageWriter
{
    public const int LogicalSectorBytes = 2048;
    private const uint PrimaryVolumeDescriptorLba = 16;
    private const uint SupplementaryVolumeDescriptorLba = 17;
    private const uint TerminatorLba = 18;
    private const uint FirstMetadataLba = 19;
    private const int MaximumPrimaryIdentifierCharacters = 31;
    private const int MaximumJolietIdentifierCharacters = 60;

    public sealed class LayoutPlan
    {
        internal readonly IsoNode _root;
        internal readonly List<IsoNode> _directories;
        internal readonly Layout _layout;
        internal readonly string _primaryVolumeLabel;
        internal readonly string _jolietVolumeLabel;
        internal readonly DateTimeOffset _timestamp;

        internal LayoutPlan(
            string sourceDirectory,
            uint volumeSectors,
            IsoNode root,
            List<IsoNode> directories,
            Layout layout,
            string primaryVolumeLabel,
            string jolietVolumeLabel,
            DateTimeOffset timestamp)
        {
            SourceDirectory = sourceDirectory;
            VolumeSectors = volumeSectors;
            _root = root;
            _directories = directories;
            _layout = layout;
            _primaryVolumeLabel = primaryVolumeLabel;
            _jolietVolumeLabel = jolietVolumeLabel;
            _timestamp = timestamp;
            List<IsoNode> files = EnumerateFiles(root).ToList();
            FileCount = files.Count;
            DirectoryCount = directories.Count;
            FileBytes = files.Sum(file => file.DataLength);
        }

        public string SourceDirectory { get; }

        public uint VolumeSectors { get; }

        public int FileCount { get; }

        public int DirectoryCount { get; }

        public long FileBytes { get; }

        public uint DataEndLbaExclusive => _layout.DataEndLbaExclusive;

        public uint MetadataEndLbaExclusive => _layout.MetadataEndLbaExclusive;

        public uint FileDataStartLba => _layout.FileDataStartLba;

        public bool FilesAtOuterRing => _layout.FilesAtOuterRing;
    }

    public static LayoutPlan Plan(
        string? sourceDirectory,
        uint volumeSectors,
        string volumeLabel,
        DateTimeOffset? timestamp = null,
        bool filesAtOuterRing = false)
    {
        if (volumeSectors <= FirstMetadataLba)
        {
            throw new ArgumentOutOfRangeException(
                nameof(volumeSectors),
                "Hybrid ISO volume is too small for filesystem metadata.");
        }

        ArgumentNullException.ThrowIfNull(volumeLabel);
        string sourceFullPath = sourceDirectory is null ? string.Empty : Path.GetFullPath(sourceDirectory);
        if (sourceDirectory is not null && !Directory.Exists(sourceFullPath))
            throw new DirectoryNotFoundException($"Data directory does not exist: {sourceFullPath}");
        if (sourceDirectory is not null && (File.GetAttributes(sourceFullPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse points are not supported in --data-dir: {sourceFullPath}");

        IsoNode root = sourceDirectory is null
            ? new IsoNode(string.Empty, string.Empty, isDirectory: true, parent: null) { Timestamp = timestamp ?? DateTimeOffset.UtcNow }
            : ReadTree(sourceFullPath);
        List<IsoNode> directories = AssignDirectoryNumbers(root);
        AssignIdentifiers(directories);
        Layout layout = BuildLayout(directories, volumeSectors, filesAtOuterRing);
        return new LayoutPlan(
            sourceFullPath,
            volumeSectors,
            root,
            directories,
            layout,
            NormalizePrimaryIdentifier(volumeLabel, 32, "DVD_IMAGE"),
            NormalizeJolietIdentifier(volumeLabel, 16, "DVD_IMAGE"),
            timestamp ?? DateTimeOffset.UtcNow);
    }

    public static void WriteSequentialPrefix(
        LayoutPlan plan,
        Stream output,
        uint endLbaExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("Hybrid ISO output stream must be writable.", nameof(output));
        }

        if (endLbaExclusive < plan.DataEndLbaExclusive || endLbaExclusive > plan.VolumeSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endLbaExclusive),
                $"Sequential prefix must end between LBA {plan.DataEndLbaExclusive} and {plan.VolumeSectors}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (IsoNode file in EnumerateFiles(plan._root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSourceFile(file);
        }
        SequentialIsoWriter writer = new(output, cancellationToken);
        WriteFilesystem(plan, writer);
        writer.PadToLba(endLbaExclusive);
    }

    private static void WriteFilesystem(LayoutPlan plan, SequentialIsoWriter output)
    {
        WriteMetadata(plan, output);
        WriteFiles(plan, output);
    }

    /// <summary>Writes only the front metadata, leaving the planned drawing interval free.</summary>
    public static void WriteOuterRingPrefix(LayoutPlan plan, Stream output, uint endLbaExclusive,
        CancellationToken cancellationToken = default)
    {
        ValidateOuterRange(plan, output, endLbaExclusive);
        ValidateSources(plan, cancellationToken);
        SequentialIsoWriter writer = new(output, cancellationToken);
        WriteMetadata(plan, writer);
        writer.PadToLba(endLbaExclusive);
    }

    /// <summary>Appends padding and file contents after the caller has produced the drawing.</summary>
    public static void WriteOuterRingSuffix(LayoutPlan plan, Stream output, uint startLba,
        CancellationToken cancellationToken = default)
    {
        ValidateOuterRange(plan, output, startLba);
        ValidateSources(plan, cancellationToken);
        SequentialIsoWriter writer = new(output, cancellationToken, startLba);
        WriteFiles(plan, writer);
        writer.PadToLba(plan.VolumeSectors);
    }

    private static void ValidateOuterRange(LayoutPlan plan, Stream output, uint boundary)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("ISO output must be writable.", nameof(output));
        if (!plan.FilesAtOuterRing || boundary < plan.MetadataEndLbaExclusive || boundary > plan.FileDataStartLba)
            throw new ArgumentOutOfRangeException(nameof(boundary), "Outer-file drawing boundary overlaps metadata or files.");
    }

    private static void ValidateSources(LayoutPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (IsoNode file in EnumerateFiles(plan._root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSourceFile(file);
        }
    }

    private static void WriteMetadata(LayoutPlan plan, SequentialIsoWriter output)
    {
        output.WriteAtLba(PrimaryVolumeDescriptorLba, BuildVolumeDescriptor(
            type: 1,
            plan.VolumeSectors,
            plan._primaryVolumeLabel,
            plan._layout.PrimaryPathTableBytes,
            plan._layout.PrimaryLittlePathTableLba,
            plan._layout.PrimaryBigPathTableLba,
            plan._root.PrimaryDirectoryLba,
            plan._root.PrimaryDirectoryBytes,
            joliet: false,
            plan._timestamp));
        output.WriteAtLba(SupplementaryVolumeDescriptorLba, BuildVolumeDescriptor(
            type: 2,
            plan.VolumeSectors,
            plan._jolietVolumeLabel,
            plan._layout.JolietPathTableBytes,
            plan._layout.JolietLittlePathTableLba,
            plan._layout.JolietBigPathTableLba,
            plan._root.JolietDirectoryLba,
            plan._root.JolietDirectoryBytes,
            joliet: true,
            plan._timestamp));
        output.WriteAtLba(TerminatorLba, BuildTerminator());
        output.WriteAtLba(
            plan._layout.PrimaryLittlePathTableLba,
            BuildPathTable(plan._directories, littleEndian: true, joliet: false));
        output.WriteAtLba(
            plan._layout.PrimaryBigPathTableLba,
            BuildPathTable(plan._directories, littleEndian: false, joliet: false));
        output.WriteAtLba(
            plan._layout.JolietLittlePathTableLba,
            BuildPathTable(plan._directories, littleEndian: true, joliet: true));
        output.WriteAtLba(
            plan._layout.JolietBigPathTableLba,
            BuildPathTable(plan._directories, littleEndian: false, joliet: true));

        foreach (IsoNode directory in plan._directories)
        {
            output.WriteAtLba(
                directory.PrimaryDirectoryLba,
                BuildDirectoryExtent(directory, joliet: false));
        }

        foreach (IsoNode directory in plan._directories)
        {
            output.WriteAtLba(
                directory.JolietDirectoryLba,
                BuildDirectoryExtent(directory, joliet: true));
        }

    }

    private static void WriteFiles(LayoutPlan plan, SequentialIsoWriter output)
    {
        foreach (IsoNode file in EnumerateFiles(plan._root))
        {
            output.WriteFileAtLba(file);
        }
    }

    private static IsoNode ReadTree(string sourceFullPath)
    {
        IsoNode root = new(sourceFullPath, string.Empty, isDirectory: true, parent: null)
        {
            Timestamp = Directory.GetLastWriteTimeUtc(sourceFullPath),
        };
        PopulateDirectory(root);
        return root;
    }

    private static void PopulateDirectory(IsoNode directory)
    {
        DirectoryInfo source = new(directory.SourcePath);
        foreach (FileSystemInfo entry in source.EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Reparse points are not supported in --data-dir: {entry.FullName}");
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                IsoNode child = new(entry.FullName, entry.Name, isDirectory: true, directory)
                {
                    Timestamp = entry.LastWriteTimeUtc,
                };
                directory.Children.Add(child);
                PopulateDirectory(child);
            }
            else
            {
                FileInfo file = (FileInfo)entry;
                if ((ulong)file.Length > uint.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(directory),
                        $"ISO9660 files larger than 4 GiB minus 1 byte are not supported: {file.FullName}");
                }

                directory.Children.Add(new IsoNode(
                    file.FullName,
                    file.Name,
                    isDirectory: false,
                    directory)
                {
                    DataLength = file.Length,
                    Timestamp = file.LastWriteTimeUtc,
                });
            }
        }
    }

    private static List<IsoNode> AssignDirectoryNumbers(IsoNode root)
    {
        List<IsoNode> directories = [];
        Queue<IsoNode> pending = new();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            IsoNode directory = pending.Dequeue();
            if (directories.Count == ushort.MaxValue)
            {
                throw new ArgumentException("ISO contains too many directories.");
            }

            directory.DirectoryNumber = checked((ushort)(directories.Count + 1));
            directories.Add(directory);
            foreach (IsoNode child in directory.Children.Where(child => child.IsDirectory))
            {
                pending.Enqueue(child);
            }
        }

        return directories;
    }

    private static void AssignIdentifiers(IEnumerable<IsoNode> directories)
    {
        foreach (IsoNode directory in directories)
        {
            HashSet<string> primaryUsed = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> jolietUsed = new(StringComparer.OrdinalIgnoreCase);
            foreach (IsoNode child in directory.Children)
            {
                string primaryBase = NormalizePrimaryIdentifier(
                    child.Name,
                    child.IsDirectory
                        ? MaximumPrimaryIdentifierCharacters
                        : MaximumPrimaryIdentifierCharacters - 2,
                    child.IsDirectory ? "DIRECTORY" : "FILE");
                child.PrimaryIdentifier = MakeUnique(
                    primaryBase,
                    primaryUsed,
                    child.IsDirectory ? string.Empty : ";1",
                    MaximumPrimaryIdentifierCharacters);

                string jolietBase = NormalizeJolietIdentifier(
                    child.Name,
                    child.IsDirectory
                        ? MaximumJolietIdentifierCharacters
                        : MaximumJolietIdentifierCharacters - 2,
                    child.IsDirectory ? "Directory" : "File");
                child.JolietIdentifier = MakeUnique(
                    jolietBase,
                    jolietUsed,
                    child.IsDirectory ? string.Empty : ";1",
                    MaximumJolietIdentifierCharacters);
            }
        }
    }

    private static Layout BuildLayout(List<IsoNode> directories, uint volumeSectors, bool filesAtOuterRing)
    {
        int primaryPathTableBytes = PathTableLength(directories, joliet: false);
        int jolietPathTableBytes = PathTableLength(directories, joliet: true);
        uint cursor = FirstMetadataLba;
        uint primaryLittlePathTableLba = Allocate(ref cursor, primaryPathTableBytes);
        uint primaryBigPathTableLba = Allocate(ref cursor, primaryPathTableBytes);
        uint jolietLittlePathTableLba = Allocate(ref cursor, jolietPathTableBytes);
        uint jolietBigPathTableLba = Allocate(ref cursor, jolietPathTableBytes);

        foreach (IsoNode directory in directories)
        {
            directory.PrimaryDirectoryBytes = DirectoryExtentLength(directory, joliet: false);
            directory.PrimaryDirectoryLba = Allocate(ref cursor, directory.PrimaryDirectoryBytes);
        }

        foreach (IsoNode directory in directories)
        {
            directory.JolietDirectoryBytes = DirectoryExtentLength(directory, joliet: true);
            directory.JolietDirectoryLba = Allocate(ref cursor, directory.JolietDirectoryBytes);
        }

        uint metadataEnd = cursor;
        List<IsoNode> files = EnumerateFiles(directories[0]).ToList();
        ulong fileSectors = 0;
        foreach (IsoNode file in files) fileSectors = checked(fileSectors + SectorCount(file.DataLength));
        if ((ulong)metadataEnd + fileSectors > volumeSectors)
            throw new ArgumentException("Data directory and filesystem metadata exceed the disc capacity.");
        if (filesAtOuterRing) cursor = checked(volumeSectors - (uint)fileSectors);
        uint fileDataStart = cursor;
        foreach (IsoNode file in files)
        {
            file.DataLba = filesAtOuterRing && file.DataLength == 0 ? 0 : cursor;
            cursor = checked(cursor + SectorCount(file.DataLength));
        }

        if (cursor > volumeSectors)
        {
            throw new ArgumentException(
                $"Data directory needs {cursor} sectors including filesystem metadata, "
                + $"but the disc has only {volumeSectors} sectors.");
        }

        return new Layout(
            primaryLittlePathTableLba,
            primaryBigPathTableLba,
            jolietLittlePathTableLba,
            jolietBigPathTableLba,
            primaryPathTableBytes,
            jolietPathTableBytes,
            cursor,
            metadataEnd,
            fileDataStart,
            filesAtOuterRing);
    }

    private static uint Allocate(ref uint cursor, long byteCount)
    {
        uint lba = cursor;
        cursor = checked(cursor + SectorCount(byteCount));
        return lba;
    }

    private static uint SectorCount(long byteCount)
        => checked((uint)((byteCount + LogicalSectorBytes - 1)
            / LogicalSectorBytes));

    private static int PathTableLength(IEnumerable<IsoNode> directories, bool joliet)
    {
        int length = 0;
        foreach (IsoNode directory in directories)
        {
            int identifierBytes = directory.Parent is null
                ? 1
                : IdentifierBytes(directory, joliet).Length;
            length = checked(length + 8 + identifierBytes + (identifierBytes & 1));
        }

        return length;
    }

    private static int DirectoryExtentLength(IsoNode directory, bool joliet)
    {
        List<int> recordLengths =
        [
            DirectoryRecordLength(1),
            DirectoryRecordLength(1),
        ];
        recordLengths.AddRange(directory.Children.Select(child =>
            DirectoryRecordLength(IdentifierBytes(child, joliet).Length)));
        return PackedRecordLength(recordLengths);
    }

    private static int PackedRecordLength(IEnumerable<int> recordLengths)
    {
        int offset = 0;
        foreach (int recordLength in recordLengths)
        {
            int sectorOffset = offset % LogicalSectorBytes;
            if (sectorOffset + recordLength > LogicalSectorBytes)
            {
                offset = checked(offset + LogicalSectorBytes - sectorOffset);
            }

            offset = checked(offset + recordLength);
        }

        return checked((int)(SectorCount(offset) * LogicalSectorBytes));
    }

    private static byte[] BuildPathTable(
        IEnumerable<IsoNode> directories,
        bool littleEndian,
        bool joliet)
    {
        using MemoryStream table = new();
        byte[] numeric = new byte[6];
        foreach (IsoNode directory in directories)
        {
            byte[] identifier = directory.Parent is null ? [0] : IdentifierBytes(directory, joliet);
            table.WriteByte(checked((byte)identifier.Length));
            table.WriteByte(0);
            uint extentLba = joliet ? directory.JolietDirectoryLba : directory.PrimaryDirectoryLba;
            ushort parentNumber = directory.Parent?.DirectoryNumber ?? 1;
            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(numeric.AsSpan(0, 4), extentLba);
                BinaryPrimitives.WriteUInt16LittleEndian(numeric.AsSpan(4, 2), parentNumber);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(numeric.AsSpan(0, 4), extentLba);
                BinaryPrimitives.WriteUInt16BigEndian(numeric.AsSpan(4, 2), parentNumber);
            }

            table.Write(numeric);
            table.Write(identifier);
            if ((identifier.Length & 1) != 0)
            {
                table.WriteByte(0);
            }
        }

        return table.ToArray();
    }

    private static byte[] BuildDirectoryExtent(IsoNode directory, bool joliet)
    {
        int extentBytes = joliet ? directory.JolietDirectoryBytes : directory.PrimaryDirectoryBytes;
        byte[] extent = new byte[extentBytes];
        int offset = 0;
        uint currentLba = joliet ? directory.JolietDirectoryLba : directory.PrimaryDirectoryLba;
        IsoNode parent = directory.Parent ?? directory;
        uint parentLba = joliet ? parent.JolietDirectoryLba : parent.PrimaryDirectoryLba;
        int parentBytes = joliet ? parent.JolietDirectoryBytes : parent.PrimaryDirectoryBytes;
        AddDirectoryRecord(extent, ref offset, BuildDirectoryRecord(
            currentLba,
            extentBytes,
            isDirectory: true,
            [0],
            directory.Timestamp));
        AddDirectoryRecord(extent, ref offset, BuildDirectoryRecord(
            parentLba,
            parentBytes,
            isDirectory: true,
            [1],
            parent.Timestamp));
        foreach (IsoNode child in directory.Children)
        {
            uint childLba = child.IsDirectory
                ? joliet ? child.JolietDirectoryLba : child.PrimaryDirectoryLba
                : child.DataLba;
            long childBytes = child.IsDirectory
                ? joliet ? child.JolietDirectoryBytes : child.PrimaryDirectoryBytes
                : child.DataLength;
            AddDirectoryRecord(extent, ref offset, BuildDirectoryRecord(
                childLba,
                childBytes,
                child.IsDirectory,
                IdentifierBytes(child, joliet),
                child.Timestamp));
        }

        return extent;
    }

    private static void AddDirectoryRecord(byte[] extent, ref int offset, byte[] record)
    {
        int sectorOffset = offset % LogicalSectorBytes;
        if (sectorOffset + record.Length > LogicalSectorBytes)
        {
            offset += LogicalSectorBytes - sectorOffset;
        }

        record.CopyTo(extent, offset);
        offset += record.Length;
    }

    private static byte[] BuildVolumeDescriptor(
        byte type,
        uint volumeSectors,
        string volumeLabel,
        int pathTableBytes,
        uint littlePathTableLba,
        uint bigPathTableLba,
        uint rootDirectoryLba,
        int rootDirectoryBytes,
        bool joliet,
        DateTimeOffset timestamp)
    {
        byte[] sector = new byte[LogicalSectorBytes];
        sector[0] = type;
        WriteAscii(sector, 1, "CD001");
        sector[6] = 1;
        if (joliet)
        {
            WritePaddedJoliet(sector, 8, 32, "DVDIMAGE");
            WritePaddedJoliet(sector, 40, 32, volumeLabel);
            sector[88] = (byte)'%';
            sector[89] = (byte)'/';
            sector[90] = (byte)'E';
        }
        else
        {
            WritePaddedAscii(sector, 8, 32, "DVDIMAGE");
            WritePaddedAscii(sector, 40, 32, volumeLabel);
        }

        WriteBothEndian32(sector, 80, volumeSectors);
        WriteBothEndian16(sector, 120, 1);
        WriteBothEndian16(sector, 124, 1);
        WriteBothEndian16(sector, 128, LogicalSectorBytes);
        WriteBothEndian32(sector, 132, checked((uint)pathTableBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(140, 4), littlePathTableLba);
        BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(148, 4), bigPathTableLba);
        BuildDirectoryRecord(
            rootDirectoryLba,
            rootDirectoryBytes,
            isDirectory: true,
            [0],
            timestamp).CopyTo(sector, 156);
        WriteVolumeDate(sector.AsSpan(813, 17), timestamp);
        WriteVolumeDate(sector.AsSpan(830, 17), timestamp);
        WriteUnspecifiedVolumeDate(sector.AsSpan(847, 17));
        WriteUnspecifiedVolumeDate(sector.AsSpan(864, 17));
        sector[881] = 1;
        return sector;
    }

    private static byte[] BuildTerminator()
    {
        byte[] sector = new byte[LogicalSectorBytes];
        sector[0] = 255;
        WriteAscii(sector, 1, "CD001");
        sector[6] = 1;
        return sector;
    }

    private static byte[] BuildDirectoryRecord(
        uint extentLba,
        long dataLength,
        bool isDirectory,
        ReadOnlySpan<byte> identifier,
        DateTimeOffset timestamp)
    {
        int length = DirectoryRecordLength(identifier.Length);
        byte[] record = new byte[length];
        record[0] = checked((byte)length);
        WriteBothEndian32(record, 2, extentLba);
        WriteBothEndian32(record, 10, checked((uint)dataLength));
        WriteDirectoryDate(record.AsSpan(18, 7), timestamp);
        record[25] = isDirectory ? (byte)0x02 : (byte)0x00;
        WriteBothEndian16(record, 28, 1);
        record[32] = checked((byte)identifier.Length);
        identifier.CopyTo(record.AsSpan(33));
        return record;
    }

    private static int DirectoryRecordLength(int identifierBytes)
        => checked(33 + identifierBytes + (identifierBytes % 2 == 0 ? 1 : 0));

    private static byte[] IdentifierBytes(IsoNode node, bool joliet)
        => joliet
            ? TextEncoding.BigEndianUnicode.GetBytes(node.JolietIdentifier)
            : TextEncoding.ASCII.GetBytes(node.PrimaryIdentifier);

    private static IEnumerable<IsoNode> EnumerateFiles(IsoNode root)
    {
        foreach (IsoNode child in root.Children)
        {
            if (child.IsDirectory)
            {
                foreach (IsoNode nested in EnumerateFiles(child))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private static string MakeUnique(
        string baseName,
        HashSet<string> used,
        string suffix,
        int maximumCharacters)
    {
        string candidate = Truncate(baseName, maximumCharacters - suffix.Length) + suffix;
        if (used.Add(candidate))
        {
            return candidate;
        }

        for (int index = 2; index < 1_000_000; index++)
        {
            string discriminator = $"~{index}";
            candidate = Truncate(
                baseName,
                maximumCharacters - suffix.Length - discriminator.Length)
                + discriminator
                + suffix;
            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        throw new ArgumentException($"Too many colliding ISO identifiers derived from '{baseName}'.");
    }

    private static void ValidateSourceFile(IsoNode file)
    {
        FileInfo current = new(file.SourcePath);
        if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0
            || current.Length != file.DataLength || current.LastWriteTimeUtc != file.Timestamp.UtcDateTime)
            throw new IOException($"Source file changed after ISO planning: {file.SourcePath}");
    }

    private static string NormalizePrimaryIdentifier(
        string value,
        int maximumLength,
        string fallback)
    {
        StringBuilder result = new(maximumLength);
        foreach (char character in value.Normalize(NormalizationForm.FormKC).ToUpperInvariant())
        {
            char normalized = character is >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_'
                or '.'
                ? character
                : '_';
            result.Append(normalized);
            if (result.Length == maximumLength)
            {
                break;
            }
        }

        string normalizedValue = result.ToString().Trim('.');
        return normalizedValue.Length == 0 ? fallback : normalizedValue;
    }

    private static string NormalizeJolietIdentifier(
        string value,
        int maximumLength,
        string fallback)
    {
        StringBuilder result = new(maximumLength);
        foreach (char character in value.Normalize(NormalizationForm.FormC))
        {
            char normalized = character is '\0' or '/' or '\\' or ':' or ';'
                ? '_'
                : char.IsSurrogate(character)
                    ? '_'
                    : character;
            result.Append(normalized);
            if (result.Length == maximumLength)
            {
                break;
            }
        }

        return result.Length == 0 ? fallback : result.ToString();
    }

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    public static bool IsWithinDirectory(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static void WriteBothEndian16(Span<byte> destination, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, 2), value);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset + 2, 2), value);
    }

    private static void WriteBothEndian32(Span<byte> destination, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), value);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(offset + 4, 4), value);
    }

    private static void WriteDirectoryDate(Span<byte> destination, DateTimeOffset timestamp)
    {
        DateTime utc = timestamp.UtcDateTime;
        int year = Math.Clamp(utc.Year, 1900, 2155);
        destination[0] = checked((byte)(year - 1900));
        destination[1] = checked((byte)utc.Month);
        destination[2] = checked((byte)utc.Day);
        destination[3] = checked((byte)utc.Hour);
        destination[4] = checked((byte)utc.Minute);
        destination[5] = checked((byte)utc.Second);
        destination[6] = 0;
    }

    private static void WriteVolumeDate(Span<byte> destination, DateTimeOffset timestamp)
    {
        string digits = timestamp.UtcDateTime.ToString("yyyyMMddHHmmssff", CultureInfo.InvariantCulture);
        TextEncoding.ASCII.GetBytes(digits, destination[..16]);
        destination[16] = 0;
    }

    private static void WriteUnspecifiedVolumeDate(Span<byte> destination)
    {
        destination[..16].Fill((byte)'0');
        destination[16] = 0;
    }

    private static void WriteAscii(Span<byte> destination, int offset, string value)
        => TextEncoding.ASCII.GetBytes(value, destination[offset..]);

    private static void WritePaddedAscii(Span<byte> destination, int offset, int length, string value)
    {
        Span<byte> field = destination.Slice(offset, length);
        field.Fill((byte)' ');
        TextEncoding.ASCII.GetBytes(value, field);
    }

    private static void WritePaddedJoliet(Span<byte> destination, int offset, int length, string value)
    {
        Span<byte> field = destination.Slice(offset, length);
        for (int index = 0; index < field.Length; index += 2)
        {
            field[index] = 0;
            field[index + 1] = (byte)' ';
        }

        TextEncoding.BigEndianUnicode.GetBytes(value, field);
    }

    private sealed class SequentialIsoWriter
    {
        private const int CopyBufferBytes = 128 * 1024;

        private readonly Stream _output;
        private readonly CancellationToken _cancellationToken;
        private readonly byte[] _zeroBuffer = new byte[CopyBufferBytes];
        private long _position;

        internal SequentialIsoWriter(Stream output, CancellationToken cancellationToken, uint startLba = 0)
        {
            _output = output;
            _cancellationToken = cancellationToken;
            _position = checked((long)startLba * LogicalSectorBytes);
        }

        internal void WriteAtLba(uint lba, ReadOnlySpan<byte> bytes)
        {
            PadToPosition(checked((long)lba * LogicalSectorBytes));
            _cancellationToken.ThrowIfCancellationRequested();
            _output.Write(bytes);
            _position = checked(_position + bytes.Length);
        }

        internal void WriteFileAtLba(IsoNode file)
        {
            if (file.DataLength == 0)
            {
                ValidateSourceFile(file);
                return;
            }
            PadToPosition(checked((long)file.DataLba * LogicalSectorBytes));
            ValidateSourceFile(file);
            using FileStream input = new(
                file.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: CopyBufferBytes,
                FileOptions.SequentialScan);
            if (input.Length != file.DataLength)
            {
                throw new IOException($"Source file size changed while streaming ISO: {file.SourcePath}");
            }

            byte[] buffer = new byte[CopyBufferBytes];
            long remaining = file.DataLength;
            while (remaining > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = input.Read(buffer, 0, requested);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Source file ended early while streaming ISO: {file.SourcePath}");
                }

                _output.Write(buffer, 0, read);
                _position = checked(_position + read);
                remaining -= read;
            }
            ValidateSourceFile(file);
            if (input.Length != file.DataLength)
                throw new IOException($"Source file size changed while streaming ISO: {file.SourcePath}");
        }

        internal void PadToLba(uint lba)
            => PadToPosition(checked((long)lba * LogicalSectorBytes));

        private void PadToPosition(long target)
        {
            if (target < _position)
            {
                throw new InvalidOperationException(
                    $"Sequential ISO layout moved backwards from byte {_position} to {target}.");
            }

            long remaining = target - _position;
            while (remaining > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(_zeroBuffer.Length, remaining);
                _output.Write(_zeroBuffer, 0, count);
                _position = checked(_position + count);
                remaining -= count;
            }
        }
    }

    internal sealed class IsoNode(
        string sourcePath,
        string name,
        bool isDirectory,
        IsoNode? parent)
    {
        internal string SourcePath { get; } = sourcePath;
        internal string Name { get; } = name;
        internal bool IsDirectory { get; } = isDirectory;
        internal IsoNode? Parent { get; } = parent;
        internal List<IsoNode> Children { get; } = [];
        internal string PrimaryIdentifier { get; set; } = string.Empty;
        internal string JolietIdentifier { get; set; } = string.Empty;
        internal ushort DirectoryNumber { get; set; }
        internal uint PrimaryDirectoryLba { get; set; }
        internal int PrimaryDirectoryBytes { get; set; }
        internal uint JolietDirectoryLba { get; set; }
        internal int JolietDirectoryBytes { get; set; }
        internal uint DataLba { get; set; }
        internal long DataLength { get; set; }
        internal DateTimeOffset Timestamp { get; set; }
    }

    internal sealed record Layout(
        uint PrimaryLittlePathTableLba,
        uint PrimaryBigPathTableLba,
        uint JolietLittlePathTableLba,
        uint JolietBigPathTableLba,
        int PrimaryPathTableBytes,
        int JolietPathTableBytes,
        uint DataEndLbaExclusive,
        uint MetadataEndLbaExclusive,
        uint FileDataStartLba,
        bool FilesAtOuterRing);
}
