using System.Buffers.Binary;
using System.Text;

namespace DiscImageStudio.AutoCalibration;

public enum CalibrationDiscKind
{
    None,
    Unsupported,
    Blank,
    Calibration,
    Other,
}

public sealed record CalibrationDiscCheck(uint Sectors, bool Foreign);

public readonly record struct CalibrationDiscCheckProgress(double Percent, uint Lba);

public static class CalibrationDiscChecker
{
    public const int SpotCheckCount = 32;

    public static (string Label, uint Sectors)? ParsePrimaryVolumeDescriptor(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < CalibrationMediaProfile.SectorBytes
            || sector[0] != 1
            || !sector.Slice(1, 5).SequenceEqual("CD001"u8))
        {
            return null;
        }

        string label = Encoding.ASCII.GetString(sector.Slice(40, 32)).Trim();
        uint sectors = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(80, 4));
        return (label, sectors);
    }

    public static IReadOnlyList<uint> GetSpotCheckLbas(uint sectors)
    {
        uint first = CalibrationImageGenerator.FileSystemLastLba + 1;
        uint last = sectors == 0 ? 0 : sectors - 1;
        if (last <= first)
        {
            return [Math.Min(first, last)];
        }

        var lbas = new uint[SpotCheckCount];
        for (int i = 0; i < lbas.Length; i++)
        {
            lbas[i] = first + (uint)((ulong)(last - first) * (uint)i / (SpotCheckCount - 1));
        }

        return lbas;
    }

    public static CalibrationDiscCheck Check(
        ITimedOpticalDrive drive,
        CalibrationMedia media,
        IProgress<CalibrationDiscCheckProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        Span<byte> sector = stackalloc byte[CalibrationMediaProfile.SectorBytes];
        drive.ReadSector(16, sector, cancellationToken);
        (string Label, uint Sectors) volume = ParsePrimaryVolumeDescriptor(sector)
            ?? throw new InvalidDataException("这张盘没有可识别的文件系统，不是校准盘。");
        if (!string.Equals(volume.Label, CalibrationImageGenerator.VolumeLabel, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"这张盘的卷标是「{volume.Label}」，不是校准盘。");
        }

        if (media == CalibrationMedia.Cd && volume.Sectors < CalibrationMediaProfile.Ladder(media).MinimumSectors)
        {
            throw new InvalidDataException($"这张校准盘只有 {volume.Sectors} 个扇区，太少了，测不了整张盘。");
        }

        if (media == CalibrationMedia.Dvd && volume.Sectors < CalibrationImageGenerator.DefaultDvdSectors / 100 * 99)
        {
            throw new InvalidDataException(
                $"这张校准盘只有 {volume.Sectors} 个扇区，镜像是按其他大小生成的，测不了整张盘。");
        }

        if (media == CalibrationMedia.Cd)
        {
            CheckCdLayout(drive, volume.Sectors / 2, cancellationToken);
        }

        uint? capacity = null;
        try
        {
            capacity = drive.ReadCapacity(cancellationToken);
        }
        catch (IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (capacity is { } actual && (ulong)actual + 16 < volume.Sectors)
        {
            throw new InvalidDataException(
                $"这张盘只写了 {actual} 个扇区，校准镜像有 {volume.Sectors} 个：刻录没有完成。");
        }

        IReadOnlyList<uint> lbas = GetSpotCheckLbas(volume.Sectors);
        int matched = 0;
        byte[] read = new byte[CalibrationMediaProfile.SectorBytes];
        for (int i = 0; i < lbas.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint lba = lbas[i];
            try
            {
                drive.ReadSector(lba, read, cancellationToken);
            }
            catch (IOException exception)
            {
                throw new IOException(
                    $"在盘面约 {(ulong)lba * 100 / volume.Sectors}% 处（LBA {lba}）读不出来，可能没刻完或有坏块：{exception.Message}",
                    exception);
            }

            if (read.AsSpan().SequenceEqual(CalibrationImageGenerator.CreateSector(lba, volume.Sectors)))
            {
                matched++;
            }

            progress?.Report(new((i + 1) * 100.0 / lbas.Count, lba));
        }

        if (matched == 0)
        {
            return new(volume.Sectors, Foreign: true);
        }

        if (matched != lbas.Count)
        {
            throw new InvalidDataException(
                $"抽查的 {lbas.Count} 个扇区里有 {lbas.Count - matched} 个内容不对，这张盘可能刻坏了。");
        }

        return new(volume.Sectors, Foreign: false);
    }

    private static void CheckCdLayout(ITimedOpticalDrive drive, uint lba, CancellationToken cancellationToken)
    {
        Span<byte> raw = stackalloc byte[2352];
        if (!drive.TryReadRawSector(lba, raw, cancellationToken))
        {
            return;
        }

        ReadOnlySpan<byte> sync = [0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0];
        int? dataOffset = raw.Length >= 24 && raw[..12].SequenceEqual(sync)
            ? raw[15] switch
            {
                1 => 16,
                2 when (raw[18] & 0x20) == 0 => 24,
                _ => null,
            }
            : null;
        if (dataOffset is null)
        {
            throw new InvalidDataException("这张 CD 不是数据盘（Mode 1 或 Mode 2 Form 1），无法标定。");
        }

        if (dataOffset != drive.ExpectedCdUserDataOffset)
        {
            throw new InvalidDataException(
                $"这台电脑把 CD 刻成了用户数据从第 {dataOffset} 字节开始的扇区，程序按第 {drive.ExpectedCdUserDataOffset} 字节计算，刻出的图案会变成噪点。");
        }
    }
}
