using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace DiscImageStudio.AutoCalibration;

public readonly record struct CalibrationImageProgress(int Percent, double MegabytesPerSecond);

public static class CalibrationImageGenerator
{
    public const string VolumeLabel = "DISCPROBE_RANDOM";
    public const uint DefaultDvdSectors = 2_295_104;
    public const uint DefaultCdSectors = 359_785;
    public const uint CdImageSectorLimit = 359_849;
    public const uint FileSystemLastLba = 19;
    private const int BatchSectors = 2_048;
    private const int BytesPerSector = CalibrationMediaProfile.SectorBytes;

    public static uint DefaultSectors(CalibrationMedia media) =>
        media == CalibrationMedia.Cd ? DefaultCdSectors : DefaultDvdSectors;

    public static byte[] CreateSector(uint lba, uint totalSectors)
    {
        byte[] sector = new byte[BytesPerSector];
        FillRandom(sector, lba);
        if (lba <= FileSystemLastLba)
        {
            byte[] header = CreateFileSystemHeader(totalSectors);
            header.AsSpan(checked((int)lba * BytesPerSector), BytesPerSector).CopyTo(sector);
        }

        return sector;
    }

    public static async Task GenerateAsync(
        string path,
        uint totalSectors,
        IProgress<CalibrationImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (totalSectors < FileSystemLastLba + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSectors), "扇区数太少。");
        }

        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4 << 20,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] header = CreateFileSystemHeader(totalSectors);
        byte[] batch = new byte[BatchSectors * BytesPerSector];
        var stopwatch = Stopwatch.StartNew();
        int lastPercent = -1;
        uint completed = 0;
        while (completed < totalSectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint count = Math.Min(BatchSectors, totalSectors - completed);
            int batchLength = checked((int)count * BytesPerSector);
            Span<byte> block = batch.AsSpan(0, batchLength);
            for (uint offset = 0; offset < count; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint lba = completed + offset;
                Span<byte> sector = block.Slice(checked((int)offset * BytesPerSector), BytesPerSector);
                FillRandom(sector, lba);
                if (lba <= FileSystemLastLba)
                {
                    header.AsSpan(checked((int)lba * BytesPerSector), BytesPerSector).CopyTo(sector);
                }
            }

            await output.WriteAsync(batch.AsMemory(0, batchLength), cancellationToken).ConfigureAwait(false);
            completed += count;
            int percent = (int)((ulong)completed * 100 / totalSectors);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                double throughput = stopwatch.Elapsed.TotalSeconds > 0
                    ? (double)completed * BytesPerSector / 1e6 / stopwatch.Elapsed.TotalSeconds
                    : 0;
                progress?.Report(new(percent, throughput));
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static byte[] CreateFileSystemHeader(uint totalSectors)
    {
        byte[] imageHead = new byte[(FileSystemLastLba + 1) * BytesPerSector];
        for (uint lba = 0; lba <= FileSystemLastLba; lba++)
        {
            FillRandom(imageHead.AsSpan(checked((int)lba * BytesPerSector), BytesPerSector), lba);
        }

        Span<byte> pvd = imageHead.AsSpan(16 * BytesPerSector, BytesPerSector);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..6]);
        pvd[6] = 1;
        pvd[8..40].Fill((byte)' ');
        pvd[40..72].Fill((byte)' ');
        Encoding.ASCII.GetBytes(VolumeLabel).CopyTo(pvd[40..72]);
        WriteBothEndian32(pvd[80..88], totalSectors);
        WriteBothEndian16(pvd[120..124], 1);
        WriteBothEndian16(pvd[124..128], 1);
        WriteBothEndian16(pvd[128..132], BytesPerSector);
        WriteBothEndian32(pvd[132..140], 10);
        BinaryPrimitives.WriteUInt32LittleEndian(pvd[140..144], 18);
        WriteDirectoryRecord(pvd[156..190], 19, BytesPerSector, 0);
        pvd[190..813].Fill((byte)' ');
        foreach (int start in new[] { 813, 830, 847, 864 })
        {
            pvd[start..(start + 16)].Fill((byte)'0');
        }

        pvd[881] = 1;

        Span<byte> terminator = imageHead.AsSpan(17 * BytesPerSector, BytesPerSector);
        terminator[0] = 255;
        "CD001"u8.CopyTo(terminator[1..6]);
        terminator[6] = 1;

        Span<byte> pathTable = imageHead.AsSpan(18 * BytesPerSector, BytesPerSector);
        pathTable[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(pathTable[2..6], 19);
        BinaryPrimitives.WriteUInt16LittleEndian(pathTable[6..8], 1);

        Span<byte> root = imageHead.AsSpan(19 * BytesPerSector, BytesPerSector);
        WriteDirectoryRecord(root[..34], 19, BytesPerSector, 0);
        WriteDirectoryRecord(root[34..68], 19, BytesPerSector, 1);
        return imageHead;
    }

    private static void WriteDirectoryRecord(Span<byte> destination, uint extent, uint size, byte name)
    {
        destination[0] = 34;
        WriteBothEndian32(destination[2..10], extent);
        WriteBothEndian32(destination[10..18], size);
        destination[25] = 0x02;
        WriteBothEndian16(destination[28..32], 1);
        destination[32] = 1;
        destination[33] = name;
    }

    private static void WriteBothEndian16(Span<byte> destination, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[..2], checked((ushort)value));
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..4], checked((ushort)value));
    }

    private static void WriteBothEndian32(Span<byte> destination, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], value);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], value);
    }

    private static void FillRandom(Span<byte> destination, uint lba)
    {
        var random = new SplitMix64(0x2026_1104_0000_0000UL ^ lba);
        for (int offset = 0; offset + sizeof(ulong) <= destination.Length; offset += sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, sizeof(ulong)), random.NextUInt64());
        }
    }

    private struct SplitMix64(ulong state)
    {
        private ulong _state = state;

        public ulong NextUInt64()
        {
            unchecked
            {
                _state += 0x9E37_79B9_7F4A_7C15UL;
                ulong value = _state;
                value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
