namespace DiscImageStudio.AutoCalibration;

public sealed record CalibrationDriveInfo(
    string Id,
    string Vendor,
    string Product,
    string Revision,
    bool MediaPresent,
    string MediaType,
    string MediaId,
    uint TotalSectors,
    bool IsBlank,
    bool Simulated,
    bool Supported);

public readonly record struct TimedSectorRead(double CompletionNanoseconds);

public interface ITimedOpticalDrive : IDisposable
{
    TimedSectorRead ReadTimed(uint lba, bool forceUnitAccess, CancellationToken cancellationToken);
    uint ReadCapacity(CancellationToken cancellationToken);
    void ReadSector(uint lba, Span<byte> destination, CancellationToken cancellationToken);
    bool TryReadRawSector(uint lba, Span<byte> destination, CancellationToken cancellationToken) => false;
    bool SupportsForceUnitAccess { get; }
    int ExpectedCdUserDataOffset => OperatingSystem.IsWindows() ? 24 : 16;
    void SetReadSpeed(ushort kilobytesPerSecond, CancellationToken cancellationToken);
    void Wait(TimeSpan duration, CancellationToken cancellationToken);
}

public interface IOpticalDriveProvider
{
    IReadOnlyList<CalibrationDriveInfo> ListDrives(CancellationToken cancellationToken);
    ITimedOpticalDrive Open(string id, CancellationToken cancellationToken);
}

public static class MmcCommandSet
{
    public static byte[] Read12(uint lba, uint sectors, bool fua = false)
    {
        byte[] command = new byte[12];
        command[0] = 0xA8;
        command[1] = fua ? (byte)0x08 : (byte)0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(2, 4), lba);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(6, 4), sectors);
        return command;
    }

    public static byte[] ReadCdRaw(uint lba)
    {
        byte[] command = new byte[12];
        command[0] = 0xBE;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(2, 4), lba);
        command[8] = 1;
        command[9] = 0xF8;
        return command;
    }

    public static byte[] ReadCapacity() => [0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static uint ParseCapacity(ReadOnlySpan<byte> response)
    {
        if (response.Length < 8)
        {
            throw new ArgumentException("READ CAPACITY 响应至少需要 8 个字节。", nameof(response));
        }

        uint lastLba = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(response);
        return checked(lastLba + 1);
    }

    public static (string Vendor, string Product, string Revision) ParseInquiry(ReadOnlySpan<byte> response)
    {
        if (response.Length < 36)
        {
            throw new ArgumentException("INQUIRY 响应至少需要 36 个字节。", nameof(response));
        }

        return (
            DecodeInquiryField(response.Slice(8, 8)),
            DecodeInquiryField(response.Slice(16, 16)),
            DecodeInquiryField(response.Slice(32, 4)));
    }

    private static string DecodeInquiryField(ReadOnlySpan<byte> bytes) =>
        System.Text.Encoding.ASCII.GetString(bytes).Trim();
}
