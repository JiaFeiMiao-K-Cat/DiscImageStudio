using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiscImageStudio.AutoCalibration.Drives;

public sealed class WindowsSptiOpticalDriveProvider : IOpticalDriveProvider
{
    public IReadOnlyList<CalibrationDriveInfo> ListDrives(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<CalibrationDriveInfo>();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drive.DriveType != DriveType.CDRom)
            {
                continue;
            }

            string letter = Path.GetPathRoot(drive.Name)!.TrimEnd('\\');
            string id = $@"\\.\{letter}";
            try
            {
                using var transport = WindowsSptiTransport.Open(id);
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
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows SPTI is available only on Windows.");
        }

        if (!IsDeviceId(id))
        {
            throw new ArgumentException(@"Expected a Windows optical-drive ID such as \\.\D:.", nameof(id));
        }

        return new WindowsTimedOpticalDrive(WindowsSptiTransport.Open(id));
    }

    private static bool IsDeviceId(string id) =>
        id.Length == 6
        && id.StartsWith(@"\\.\", StringComparison.Ordinal)
        && char.IsLetter(id[4])
        && id[5] == ':';
}

internal sealed class WindowsSptiTransport : IScsiTransport
{
    private const uint IoctlScsiPassThroughDirect = 0x0004D014;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const byte DataIn = 1;
    private const byte NoData = 2;
    private const int SenseBytes = 32;
    private readonly SafeFileHandle _handle;

    private WindowsSptiTransport(SafeFileHandle handle) => _handle = handle;

    public static WindowsSptiTransport Open(string id)
    {
        SafeFileHandle handle = NativeMethods.CreateFile(
            id,
            GenericRead | GenericWrite,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open optical drive {id} for SPTI.", new System.ComponentModel.Win32Exception(error));
        }

        return new WindowsSptiTransport(handle);
    }

    public byte[] Execute(ReadOnlySpan<byte> command, int transferLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Length is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "SPTI CDB length must be between 1 and 16 bytes.");
        }

        if (transferLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transferLength));
        }

        byte[] response = new byte[transferLength];
        GCHandle dataPin = default;
        if (transferLength > 0)
        {
            dataPin = GCHandle.Alloc(response, GCHandleType.Pinned);
        }

        int packetSize = Marshal.SizeOf<ScsiPassThroughDirect>();
        IntPtr packetBuffer = Marshal.AllocHGlobal(packetSize + SenseBytes);
        try
        {
            unsafe
            {
                ScsiPassThroughDirect packet = new()
                {
                    Length = (ushort)packetSize,
                    CdbLength = (byte)command.Length,
                    SenseInfoLength = SenseBytes,
                    DataIn = transferLength == 0 ? NoData : DataIn,
                    DataTransferLength = (uint)transferLength,
                    TimeOutValue = 30,
                    DataBuffer = transferLength == 0 ? IntPtr.Zero : dataPin.AddrOfPinnedObject(),
                    SenseInfoOffset = (uint)packetSize,
                };
                byte* cdb = packet.Cdb;
                command.CopyTo(new Span<byte>(cdb, 16));

                Marshal.StructureToPtr(packet, packetBuffer, false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool success = NativeMethods.DeviceIoControl(
                _handle,
                IoctlScsiPassThroughDirect,
                packetBuffer,
                (uint)(packetSize + SenseBytes),
                packetBuffer,
                (uint)(packetSize + SenseBytes),
                out _,
                IntPtr.Zero);
            cancellationToken.ThrowIfCancellationRequested();

            ScsiPassThroughDirect result = Marshal.PtrToStructure<ScsiPassThroughDirect>(packetBuffer);
            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("SPTI command transport failed.", new System.ComponentModel.Win32Exception(error));
            }

            if (result.ScsiStatus != 0)
            {
                byte[] sense = new byte[SenseBytes];
                Marshal.Copy(IntPtr.Add(packetBuffer, (int)result.SenseInfoOffset), sense, 0, sense.Length);
                throw ScsiSense.Create("SPTI", result.ScsiStatus, sense);
            }

            if (transferLength > 0 && result.DataTransferLength < transferLength)
            {
                Array.Resize(ref response, checked((int)result.DataTransferLength));
            }

            return response;
        }
        finally
        {
            Marshal.FreeHGlobal(packetBuffer);
            if (dataPin.IsAllocated)
            {
                dataPin.Free();
            }
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ScsiPassThroughDirect
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        public fixed byte Cdb[16];
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            IntPtr inputBuffer,
            uint inputBufferSize,
            IntPtr outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}
