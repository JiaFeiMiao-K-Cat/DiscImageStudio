using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TextEncoding = System.Text.Encoding;

namespace DvdImageSolver;

internal sealed record IsoWriteSummary(
    string Mode,
    uint VolumeSectors,
    int LogicalSectorBytes,
    uint PayloadLba,
    int PayloadSectorCount,
    uint? VisibleFileLba,
    bool SparseFile,
    string? TemplatePath);

internal static class IsoImageWriter
{
    internal const int LogicalSectorBytes = 2048;
    private const int PayloadSectorCount = 16;
    private const uint PrimaryVolumeDescriptorLba = 16;
    private const uint TerminatorLba = 17;
    private const uint FsctlSetSparse = 0x000900C4;

    internal static uint GetTemplateSectorCount(string templatePath)
    {
        long length = new FileInfo(templatePath).Length;
        if (length == 0 || length % LogicalSectorBytes != 0)
        {
            throw new ArgumentException("ISO template size must be a non-zero multiple of 2048 bytes.");
        }

        long sectors = length / LogicalSectorBytes;
        if (sectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(templatePath), "ISO template has too many sectors.");
        }

        return (uint)sectors;
    }

    internal static IsoWriteSummary PatchTemplate(
        string templatePath,
        string outputPath,
        ReadOnlySpan<byte> payloads,
        uint payloadLba)
    {
        ValidatePayload(payloads);
        string templateFullPath = Path.GetFullPath(templatePath);
        string outputFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(templateFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--iso-template and --iso-output must be different files.");
        }

        uint volumeSectors = GetTemplateSectorCount(templateFullPath);
        ValidatePayloadRange(payloadLba, volumeSectors, protectVolumeDescriptors: false);
        string temporaryPath = CreateTemporaryOutputPath(outputFullPath);
        try
        {
            File.Copy(templateFullPath, temporaryPath, overwrite: true);
            using (FileStream output = new(temporaryPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                output.Position = checked((long)payloadLba * LogicalSectorBytes);
                output.Write(payloads);
                output.Flush(flushToDisk: true);
            }

            Commit(temporaryPath, outputFullPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        return new IsoWriteSummary(
            "template",
            volumeSectors,
            LogicalSectorBytes,
            payloadLba,
            PayloadSectorCount,
            VisibleFileLba: null,
            SparseFile: false,
            templateFullPath);
    }

    internal static IsoWriteSummary CreateMinimal(
        string outputPath,
        ReadOnlySpan<byte> payloads,
        uint payloadLba,
        uint volumeSectors,
        string volumeLabel)
    {
        ValidatePayload(payloads);
        ValidatePayloadRange(payloadLba, volumeSectors, protectVolumeDescriptors: true);
        string normalizedLabel = NormalizeIdentifier(volumeLabel, 32, "DVD_IMAGE");

        HashSet<uint> reserved = [PrimaryVolumeDescriptorLba, TerminatorLba];
        for (uint sector = payloadLba; sector < (ulong)payloadLba + PayloadSectorCount; sector++)
        {
            reserved.Add(sector);
        }

        uint typeLPathTableLba = FindFreeSector(volumeSectors, reserved);
        reserved.Add(typeLPathTableLba);
        uint typeMPathTableLba = FindFreeSector(volumeSectors, reserved);
        reserved.Add(typeMPathTableLba);
        uint rootDirectoryLba = FindFreeSector(volumeSectors, reserved);
        reserved.Add(rootDirectoryLba);

        uint visibleFileLba = payloadLba >= PrimaryVolumeDescriptorLba
            ? payloadLba
            : FindFreeExtent(volumeSectors, PayloadSectorCount, reserved);
        string outputFullPath = Path.GetFullPath(outputPath);
        string temporaryPath = CreateTemporaryOutputPath(outputFullPath);
        bool sparse = false;
        try
        {
            using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.RandomAccess))
            {
                sparse = TryMarkSparse(output.SafeFileHandle);
                output.SetLength(checked((long)volumeSectors * LogicalSectorBytes));
                DateTimeOffset timestamp = DateTimeOffset.UtcNow;
                WriteSector(output, PrimaryVolumeDescriptorLba, BuildPrimaryVolumeDescriptor(
                    volumeSectors,
                    normalizedLabel,
                    typeLPathTableLba,
                    typeMPathTableLba,
                    rootDirectoryLba,
                    timestamp));
                WriteSector(output, TerminatorLba, BuildTerminator());
                WriteSector(output, typeLPathTableLba, BuildPathTable(rootDirectoryLba, littleEndian: true));
                WriteSector(output, typeMPathTableLba, BuildPathTable(rootDirectoryLba, littleEndian: false));
                WriteSector(output, rootDirectoryLba, BuildRootDirectory(rootDirectoryLba, visibleFileLba, timestamp));
                WritePayload(output, payloadLba, payloads);
                if (visibleFileLba != payloadLba)
                {
                    WritePayload(output, visibleFileLba, payloads);
                }

                output.Flush(flushToDisk: true);
            }

            Commit(temporaryPath, outputFullPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        return new IsoWriteSummary(
            "minimal-iso9660",
            volumeSectors,
            LogicalSectorBytes,
            payloadLba,
            PayloadSectorCount,
            visibleFileLba,
            sparse,
            TemplatePath: null);
    }

    private static byte[] BuildPrimaryVolumeDescriptor(
        uint volumeSectors,
        string volumeLabel,
        uint typeLPathTableLba,
        uint typeMPathTableLba,
        uint rootDirectoryLba,
        DateTimeOffset timestamp)
    {
        byte[] sector = new byte[LogicalSectorBytes];
        sector[0] = 1;
        WriteAscii(sector, 1, "CD001");
        sector[6] = 1;
        WritePaddedAscii(sector, 8, 32, "DVDIMAGE");
        WritePaddedAscii(sector, 40, 32, volumeLabel);
        WriteBothEndian32(sector, 80, volumeSectors);
        WriteBothEndian16(sector, 120, 1);
        WriteBothEndian16(sector, 124, 1);
        WriteBothEndian16(sector, 128, LogicalSectorBytes);
        WriteBothEndian32(sector, 132, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(140, 4), typeLPathTableLba);
        BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(148, 4), typeMPathTableLba);
        BuildDirectoryRecord(rootDirectoryLba, LogicalSectorBytes, isDirectory: true, [0], timestamp)
            .CopyTo(sector, 156);
        WritePaddedAscii(sector, 190, 128, volumeLabel);
        WritePaddedAscii(sector, 318, 128, string.Empty);
        WritePaddedAscii(sector, 446, 128, "DVD IMAGE SOLVER");
        WritePaddedAscii(sector, 574, 128, "DVD IMAGE SOLVER");
        WritePaddedAscii(sector, 702, 37, string.Empty);
        WritePaddedAscii(sector, 739, 37, string.Empty);
        WritePaddedAscii(sector, 776, 37, string.Empty);
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

    private static byte[] BuildPathTable(uint rootDirectoryLba, bool littleEndian)
    {
        byte[] sector = new byte[LogicalSectorBytes];
        sector[0] = 1;
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(2, 4), rootDirectoryLba);
            BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(6, 2), 1);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(2, 4), rootDirectoryLba);
            BinaryPrimitives.WriteUInt16BigEndian(sector.AsSpan(6, 2), 1);
        }

        sector[8] = 0;
        return sector;
    }

    private static byte[] BuildRootDirectory(
        uint rootDirectoryLba,
        uint visibleFileLba,
        DateTimeOffset timestamp)
    {
        byte[] sector = new byte[LogicalSectorBytes];
        int offset = 0;
        foreach (byte[] record in new[]
                 {
                     BuildDirectoryRecord(rootDirectoryLba, LogicalSectorBytes, isDirectory: true, [0], timestamp),
                     BuildDirectoryRecord(rootDirectoryLba, LogicalSectorBytes, isDirectory: true, [1], timestamp),
                     BuildDirectoryRecord(
                         visibleFileLba,
                         PayloadSectorCount * LogicalSectorBytes,
                         isDirectory: false,
                         TextEncoding.ASCII.GetBytes("SOLVED.BIN;1"),
                         timestamp),
                 })
        {
            record.CopyTo(sector, offset);
            offset += record.Length;
        }

        return sector;
    }

    private static byte[] BuildDirectoryRecord(
        uint extentLba,
        int dataLength,
        bool isDirectory,
        ReadOnlySpan<byte> identifier,
        DateTimeOffset timestamp)
    {
        int padding = identifier.Length % 2 == 0 ? 1 : 0;
        int length = 33 + identifier.Length + padding;
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

    private static void WritePayload(FileStream output, uint lba, ReadOnlySpan<byte> payloads)
    {
        output.Position = checked((long)lba * LogicalSectorBytes);
        output.Write(payloads);
    }

    private static void WriteSector(FileStream output, uint lba, ReadOnlySpan<byte> sector)
    {
        if (sector.Length != LogicalSectorBytes)
        {
            throw new ArgumentException("ISO logical sector must contain 2048 bytes.", nameof(sector));
        }

        output.Position = checked((long)lba * LogicalSectorBytes);
        output.Write(sector);
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
        destination[0] = checked((byte)(utc.Year - 1900));
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

    private static string NormalizeIdentifier(string value, int maximumLength, string fallback)
    {
        StringBuilder result = new(maximumLength);
        foreach (char character in value.ToUpperInvariant())
        {
            char normalized = character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'
                ? character
                : '_';
            result.Append(normalized);
            if (result.Length == maximumLength)
            {
                break;
            }
        }

        return result.Length == 0 ? fallback : result.ToString();
    }

    private static uint FindFreeSector(uint volumeSectors, HashSet<uint> reserved)
    {
        for (uint sector = TerminatorLba + 1; sector < volumeSectors; sector++)
        {
            if (!reserved.Contains(sector))
            {
                return sector;
            }
        }

        throw new ArgumentException("ISO volume is too small for filesystem metadata.");
    }

    private static uint FindFreeExtent(uint volumeSectors, int sectorCount, HashSet<uint> reserved)
    {
        for (uint start = TerminatorLba + 1; (ulong)start + (uint)sectorCount <= volumeSectors; start++)
        {
            bool free = true;
            for (uint sector = start; sector < (ulong)start + (uint)sectorCount; sector++)
            {
                if (reserved.Contains(sector))
                {
                    free = false;
                    break;
                }
            }

            if (free)
            {
                return start;
            }
        }

        throw new ArgumentException("ISO volume is too small for the visible SOLVED.BIN extent.");
    }

    private static void ValidatePayload(ReadOnlySpan<byte> payloads)
    {
        if (payloads.Length != PayloadSectorCount * LogicalSectorBytes)
        {
            throw new ArgumentException("ISO output needs exactly 16 consecutive 2048-byte payloads.", nameof(payloads));
        }
    }

    private static void ValidatePayloadRange(uint payloadLba, uint volumeSectors, bool protectVolumeDescriptors)
    {
        ulong end = (ulong)payloadLba + PayloadSectorCount;
        if (end > volumeSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLba), "Solved ECC Block exceeds the ISO volume size.");
        }

        if (protectVolumeDescriptors
            && payloadLba <= TerminatorLba
            && end > PrimaryVolumeDescriptorLba)
        {
            throw new ArgumentException(
                "The solved ECC Block overlaps ISO9660 volume descriptors in sectors 16 and 17. "
                + "Use LBA 0, an LBA at or above 18, or an existing template whose structure you control.");
        }
    }

    private static string CreateTemporaryOutputPath(string outputFullPath)
    {
        string? directory = Path.GetDirectoryName(outputFullPath);
        if (directory is null)
        {
            throw new ArgumentException("ISO output path has no parent directory.", nameof(outputFullPath));
        }

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $".{Path.GetFileName(outputFullPath)}.{Guid.NewGuid():N}.tmp");
    }

    private static void Commit(string temporaryPath, string outputFullPath)
        => File.Move(temporaryPath, outputFullPath, overwrite: true);

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal static bool TryMarkSparse(SafeFileHandle handle)
        => OperatingSystem.IsWindows() && DeviceIoControl(
            handle,
            FsctlSetSparse,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
