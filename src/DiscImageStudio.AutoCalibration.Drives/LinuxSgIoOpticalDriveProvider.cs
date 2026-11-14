using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiscImageStudio.AutoCalibration.Drives;

public sealed class LinuxSgIoOpticalDriveProvider : IOpticalDriveProvider
{
    public IReadOnlyList<CalibrationDriveInfo> ListDrives(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux SG_IO is available only on Linux.");
        }

        var results = new List<CalibrationDriveInfo>();
        foreach (string id in Directory.EnumerateFiles("/dev", "sr*").OrderBy(static path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var transport = LinuxSgIoTransport.Open(id);
                results.Add(ScsiDriveMetadata.Read(id, transport, cancellationToken));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                results.Add(ScsiDriveMetadata.UnavailableForProvider(id));
            }
        }

        return results;
    }

    public ITimedOpticalDrive Open(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux SG_IO is available only on Linux.");
        }

        if (!IsOpticalDeviceId(id))
        {
            throw new ArgumentException("Expected a Linux optical-drive device ID such as /dev/sr0.", nameof(id));
        }

        return new LinuxTimedOpticalDrive(LinuxSgIoTransport.Open(id));
    }

    private static bool IsOpticalDeviceId(string id) =>
        id.StartsWith("/dev/sr", StringComparison.Ordinal)
        && id.Length > 7
        && id.AsSpan(7).ToString().All(static character => character is >= '0' and <= '9');
}

internal sealed class LinuxSgIoTransport : IScsiTransport
{
    private const uint SgIo = 0x2285;
    private const int DirectionNone = -1;
    private const int DirectionToDevice = -2;
    private const int DirectionFromDevice = -3;
    private const int SenseBytes = 64;
    private readonly SafeFileHandle _handle;

    private LinuxSgIoTransport(SafeFileHandle handle) => _handle = handle;

    public static LinuxSgIoTransport Open(string path)
    {
        int descriptor = NativeMethods.open(path, 2);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException($"Could not open optical drive {path} for SG_IO.", new System.ComponentModel.Win32Exception(error));
        }

        return new LinuxSgIoTransport(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true));
    }

    public byte[] Execute(ReadOnlySpan<byte> command, int transferLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Length is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        if (transferLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transferLength));
        }

        byte[] cdb = command.ToArray();
        byte[] sense = new byte[SenseBytes];
        byte[] response = new byte[transferLength];
        GCHandle commandPin = GCHandle.Alloc(cdb, GCHandleType.Pinned);
        GCHandle sensePin = GCHandle.Alloc(sense, GCHandleType.Pinned);
        GCHandle dataPin = default;
        if (transferLength > 0)
        {
            dataPin = GCHandle.Alloc(response, GCHandleType.Pinned);
        }

        try
        {
            var header = new SgIoHeader
            {
                InterfaceId = 'S',
                Direction = transferLength == 0 ? DirectionNone : DirectionFromDevice,
                CommandLength = (byte)cdb.Length,
                SenseLength = SenseBytes,
                TransferLength = (uint)transferLength,
                Transfer = transferLength == 0 ? IntPtr.Zero : dataPin.AddrOfPinnedObject(),
                Command = commandPin.AddrOfPinnedObject(),
                Sense = sensePin.AddrOfPinnedObject(),
                TimeoutMilliseconds = 30_000,
            };

            cancellationToken.ThrowIfCancellationRequested();
            int result = NativeMethods.ioctl(_handle, new UIntPtr(SgIo), ref header);
            cancellationToken.ThrowIfCancellationRequested();
            if (result < 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("SG_IO ioctl failed.", new System.ComponentModel.Win32Exception(error));
            }

            if (header.HostStatus != 0 || header.DriverStatus != 0 || header.Status != 0)
            {
                throw ScsiSense.Create("SG_IO", header.Status, sense);
            }

            if (header.Residual > 0 && header.Residual <= transferLength)
            {
                Array.Resize(ref response, transferLength - header.Residual);
            }

            return response;
        }
        finally
        {
            commandPin.Free();
            sensePin.Free();
            if (dataPin.IsAllocated)
            {
                dataPin.Free();
            }
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct SgIoHeader
    {
        public int InterfaceId;
        public int Direction;
        public byte CommandLength;
        public byte SenseLength;
        public ushort IovecCount;
        public uint TransferLength;
        public IntPtr Transfer;
        public IntPtr Command;
        public IntPtr Sense;
        public uint TimeoutMilliseconds;
        public uint Flags;
        public int PackId;
        public IntPtr UserPointer;
        public byte Status;
        public byte MaskedStatus;
        public byte MessageStatus;
        public byte SenseWritten;
        public ushort HostStatus;
        public ushort DriverStatus;
        public int Residual;
        public uint DurationMilliseconds;
        public uint Info;
    }

    private static class NativeMethods
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int open(string pathname, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int ioctl(SafeFileHandle fileDescriptor, UIntPtr request, ref SgIoHeader argument);
    }
}

internal static class ScsiSense
{
    public static ScsiCommandException Create(string transport, byte status, ReadOnlySpan<byte> sense)
    {
        byte key = 0;
        byte asc = 0;
        byte ascq = 0;
        if (sense.Length >= 14 && (sense[0] & 0x7F) is 0x70 or 0x71)
        {
            key = (byte)(sense[2] & 0x0F);
            asc = sense[12];
            ascq = sense[13];
        }
        else if (sense.Length >= 4 && (sense[0] & 0x7F) == 0x72)
        {
            key = (byte)(sense[1] & 0x0F);
            asc = sense[2];
            ascq = sense[3];
        }

        string details = key == 0 ? string.Empty : $" Sense key 0x{key:X2}, ASC/ASCQ 0x{asc:X2}/0x{ascq:X2}.";
        return new ScsiCommandException($"{transport} SCSI command failed (status 0x{status:X2}).{details}", key, asc, ascq);
    }
}
