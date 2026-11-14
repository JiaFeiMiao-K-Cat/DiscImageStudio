using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;

namespace DiscImageStudio.AutoCalibration.Drives;

internal sealed class ScsiCommandException : IOException
{
    public ScsiCommandException(string message, byte senseKey = 0, byte asc = 0, byte ascq = 0)
        : base(message)
    {
        SenseKey = senseKey;
        Asc = asc;
        Ascq = ascq;
    }

    public byte SenseKey { get; }
    public byte Asc { get; }
    public byte Ascq { get; }
    public bool IsNoMedia => SenseKey == 0x02 && Asc == 0x3A;
    public bool IsUnsupportedCommand => SenseKey == 0x05 && (Asc == 0x20 || Asc == 0x24 || Asc == 0x26);
}

internal interface IScsiTransport : IDisposable
{
    byte[] Execute(ReadOnlySpan<byte> command, int transferLength, CancellationToken cancellationToken);
}

internal abstract class TimedScsiOpticalDrive : ITimedOpticalDrive
{
    private readonly IScsiTransport _transport;
    private readonly bool _supportsForceUnitAccess;
    private bool _disposed;

    protected TimedScsiOpticalDrive(IScsiTransport transport, bool supportsForceUnitAccess)
    {
        _transport = transport;
        _supportsForceUnitAccess = supportsForceUnitAccess;
    }

    public bool SupportsForceUnitAccess => _supportsForceUnitAccess;

    public TimedSectorRead ReadTimed(uint lba, bool forceUnitAccess, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (forceUnitAccess && !SupportsForceUnitAccess)
        {
            throw new NotSupportedException("The drive does not report READ(12) FUA support.");
        }

        _ = ExecuteExact(MmcCommandSet.Read12(lba, 1, forceUnitAccess), 2048, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new TimedSectorRead(ToNanoseconds(Stopwatch.GetTimestamp()));
    }

    public uint ReadCapacity(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        byte[] response = _transport.Execute(MmcCommandSet.ReadCapacity(), 8, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return MmcCommandSet.ParseCapacity(response);
    }

    public void ReadSector(uint lba, Span<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.Length < 2048)
        {
            throw new ArgumentException("A user-data sector destination must contain at least 2048 bytes.", nameof(destination));
        }

        byte[] response = ExecuteExact(MmcCommandSet.Read12(lba, 1), 2048, cancellationToken);
        response.AsSpan().CopyTo(destination);
    }

    public bool TryReadRawSector(uint lba, Span<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.Length < 2352)
        {
            throw new ArgumentException("A raw CD-sector destination must contain at least 2352 bytes.", nameof(destination));
        }

        try
        {
            byte[] response = ExecuteExact(MmcCommandSet.ReadCdRaw(lba), 2352, cancellationToken);
            response.AsSpan().CopyTo(destination);
            return true;
        }
        catch (ScsiCommandException exception) when (exception.IsUnsupportedCommand)
        {
            return false;
        }
    }

    public void SetReadSpeed(ushort kilobytesPerSecond, CancellationToken cancellationToken)
    {
        byte[] command = new byte[12];
        command[0] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(2, 2), kilobytesPerSecond);
        BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(4, 2), ushort.MaxValue);
        _ = _transport.Execute(command, 0, cancellationToken);
    }

    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (duration != TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(duration))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private byte[] ExecuteExact(byte[] command, int expectedLength, CancellationToken cancellationToken)
    {
        byte[] response = _transport.Execute(command, expectedLength, cancellationToken);
        if (response.Length != expectedLength)
        {
            throw new IOException(
                $"The SCSI command returned {response.Length} bytes; expected {expectedLength}.");
        }

        return response;
    }

    private static double ToNanoseconds(long timestamp) =>
        timestamp * (1_000_000_000.0 / Stopwatch.Frequency);
}

internal sealed class WindowsTimedOpticalDrive : TimedScsiOpticalDrive
{
    public WindowsTimedOpticalDrive(IScsiTransport transport, bool supportsForceUnitAccess)
        : base(transport, supportsForceUnitAccess) { }
}

internal sealed class LinuxTimedOpticalDrive : TimedScsiOpticalDrive
{
    public LinuxTimedOpticalDrive(IScsiTransport transport, bool supportsForceUnitAccess)
        : base(transport, supportsForceUnitAccess) { }
}

internal static class ScsiDriveMetadata
{
    private static readonly Dictionary<ushort, string> Profiles = new()
    {
        [0x0008] = "CD-ROM",
        [0x0009] = "CD-R",
        [0x000A] = "CD-RW",
        [0x0010] = "DVD-ROM",
        [0x0011] = "DVD-R",
        [0x0013] = "DVD-RW",
        [0x0014] = "DVD-RW",
        [0x001A] = "DVD+RW",
        [0x001B] = "DVD+R",
    };

    public static CalibrationDriveInfo Read(string id, IScsiTransport transport, CancellationToken token)
    {
        byte[] inquiry;
        try
        {
            byte[] command = [0x12, 0, 0, 0, 96, 0];
            inquiry = transport.Execute(command, 96, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return UnavailableForProvider(id);
        }

        (string vendor, string product, string revision) = MmcCommandSet.ParseInquiry(inquiry);
        bool mediaPresent;
        try
        {
            _ = transport.Execute(new byte[6], 0, token);
            mediaPresent = true;
        }
        catch (ScsiCommandException exception) when (exception.IsNoMedia)
        {
            mediaPresent = false;
        }

        if (!mediaPresent)
        {
            return new CalibrationDriveInfo(
                id, vendor, product, revision, false, "No media", string.Empty, 0, false, false, true);
        }

        ushort profile = ReadProfile(transport, token);
        string mediaType = Profiles.TryGetValue(profile, out string? known)
            ? known
            : $"Unknown (0x{profile:X4})";

        uint sectors = 0;
        try
        {
            sectors = MmcCommandSet.ParseCapacity(transport.Execute(MmcCommandSet.ReadCapacity(), 8, token));
        }
        catch (ScsiCommandException exception) when (exception.IsNoMedia)
        {
            mediaPresent = false;
        }

        bool blank = false;
        string mediaId = $"{profile:X4}:{sectors}";
        try
        {
            byte[] discInfo = transport.Execute([0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0], 34, token);
            if (discInfo.Length > 2)
            {
                blank = (discInfo[2] & 0x03) == 0;
                mediaId = $"{mediaId}:{Convert.ToHexString(discInfo.AsSpan(4, Math.Min(12, discInfo.Length - 4)))}";
            }
        }
        catch (ScsiCommandException exception) when (exception.IsUnsupportedCommand || exception.IsNoMedia)
        {
            if (exception.IsNoMedia)
            {
                mediaPresent = false;
            }
        }

        bool supported = mediaPresent && CalibrationMediaProfile.IsSupported(mediaType);
        return new CalibrationDriveInfo(
            id, vendor, product, revision, mediaPresent, mediaType, mediaId, sectors, blank, false, supported);
    }

    private static ushort ReadProfile(IScsiTransport transport, CancellationToken token)
    {
        byte[] command = [0x46, 0x01, 0, 0, 0, 0, 0, 0, 8, 0];
        try
        {
            byte[] response = transport.Execute(command, 8, token);
            return response.Length >= 8 ? BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6, 2)) : (ushort)0;
        }
        catch (ScsiCommandException exception) when (exception.IsUnsupportedCommand)
        {
            return 0;
        }
    }

    public static CalibrationDriveInfo UnavailableForProvider(string id) =>
        new(id, string.Empty, string.Empty, string.Empty, false, "Unavailable", string.Empty, 0, false, false, false);

    public static bool ProbeForceUnitAccess(IScsiTransport transport, CancellationToken token)
    {
        byte[] command = [0x5A, 0x08, 0x3F, 0, 0, 0, 0, 0, 64, 0];
        try
        {
            byte[] response = transport.Execute(command, 64, token);
            return response.Length >= 4 && (response[3] & 0x10) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
