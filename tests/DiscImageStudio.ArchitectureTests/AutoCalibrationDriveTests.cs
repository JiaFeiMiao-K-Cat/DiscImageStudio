using System.Buffers.Binary;
using System.Text;
using DiscImageStudio.AutoCalibration.Drives;

internal static class AutoCalibrationDriveTests
{
    public static void Run()
    {
        CheckDiscInfo(blankStatus: 0, expectedBlank: true);
        CheckDiscInfo(blankStatus: 2, expectedBlank: false);
        TimedReadsUseForceUnitAccess();
        Console.WriteLine("auto-calibration drives: current-profile query, blank status, and FUA timing reads passed");
    }

    private static void TimedReadsUseForceUnitAccess()
    {
        using var transport = new ReadTransport();
        using var drive = new WindowsTimedOpticalDrive(transport);
        _ = drive.ReadTimed(123, forceUnitAccess: true, CancellationToken.None);
        byte[]? command = transport.LastCommand;
        if (!drive.SupportsForceUnitAccess
            || command is null
            || command.Length != 12
            || command[0] != 0xA8
            || (command[1] & 0x08) == 0)
        {
            throw new InvalidOperationException("The Windows timed read did not issue READ(12) with FUA.");
        }
    }

    private static void CheckDiscInfo(byte blankStatus, bool expectedBlank)
    {
        using var transport = new MetadataTransport(blankStatus);
        DiscImageStudio.AutoCalibration.CalibrationDriveInfo info =
            ScsiDriveMetadata.Read(@"\\.\D:", transport, CancellationToken.None);

        byte[] expectedProfileQuery = [0x46, 0x01, 0, 0, 0, 0, 0, 0, 8, 0];
        if (!transport.Commands.Any(command => command.SequenceEqual(expectedProfileQuery))
            || info.MediaType != "CD-R"
            || info.TotalSectors != 100_000
            || info.IsBlank != expectedBlank
            || !info.Supported)
        {
            throw new InvalidOperationException("SCSI media metadata did not reflect the MMC response.");
        }
    }

    private sealed class MetadataTransport(byte discStatus) : IScsiTransport
    {
        public List<byte[]> Commands { get; } = [];

        public byte[] Execute(ReadOnlySpan<byte> command, int transferLength, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command.ToArray());
            byte[] response = new byte[transferLength];
            switch (command[0])
            {
                case 0x12:
                    Encoding.ASCII.GetBytes("TESTVEND").CopyTo(response, 8);
                    Encoding.ASCII.GetBytes("TEST CD-R       ").CopyTo(response, 16);
                    Encoding.ASCII.GetBytes("1.0 ").CopyTo(response, 32);
                    break;
                case 0x00:
                    break;
                case 0x46:
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6, 2), 0x0009);
                    break;
                case 0x25:
                    BinaryPrimitives.WriteUInt32BigEndian(response, 99_999);
                    BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4, 4), 2048);
                    break;
                case 0x51:
                    response[2] = discStatus;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected SCSI command 0x{command[0]:X2}.");
            }

            return response;
        }

        public void Dispose() { }
    }

    private sealed class ReadTransport : IScsiTransport
    {
        public byte[]? LastCommand { get; private set; }

        public byte[] Execute(ReadOnlySpan<byte> command, int transferLength, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCommand = command.ToArray();
            return new byte[transferLength];
        }

        public void Dispose() { }
    }
}
