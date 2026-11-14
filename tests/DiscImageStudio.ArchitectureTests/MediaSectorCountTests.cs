using System.Buffers.Binary;
using System.IO;
using DiscImageStudio.Burning;

internal static class MediaSectorCountTests
{
    internal static void Run()
    {
        IOpticalDiscBurner totalOnlyReader = new TotalOnlyReader();
        Throws<NotSupportedException>(() => totalOnlyReader.GetMediaCapacityAsync("reader", OpticalBurnMediaKind.DvdData).GetAwaiter().GetResult());
        // 79:59:74 is the first sector of lead-out, with LBA 0 at 00:02:00.
        // Never add one as if this were the last readable data LBA.
        byte[] atip = new byte[28];
        atip[1] = 26;
        atip[12] = 79; atip[13] = 59; atip[14] = 74;
        var cd = new ReadOnlyTransport([0x43, 2, 4, 0, 0, 0, 0, 0, 64, 0], atip);
        Require(WindowsImapiBurner.ReadCdAtipSectorCount(cd) == 359849);
        atip[14] = 70;
        Require(WindowsImapiBurner.ReadCdAtipSectorCount(cd) == 359845);
        atip[14] = 75;
        Throws<IOException>(() => WindowsImapiBurner.ReadCdAtipSectorCount(cd));
        atip[14] = 74; atip[1] = 62;
        Throws<IOException>(() => WindowsImapiBurner.ReadCdAtipSectorCount(cd));
        cd.HResult = BluRayMmcCommands.CommandHasSenseData;
        Throws<MmcCommandException>(() => WindowsImapiBurner.ReadCdAtipSectorCount(cd));

        Require(WindowsImapiBurner.ReadCdMediaCapacity(359845, physicallyBlank: true)
            == new OpticalMediaCapacity(359845, 359843));
        Require(WindowsImapiBurner.ReadCdMediaCapacity(359845, physicallyBlank: false)
            == new OpticalMediaCapacity(359845, 0));
        Throws<IOException>(() => WindowsImapiBurner.ReadCdMediaCapacity(2, physicallyBlank: true));

        Require(WindowsImapiBurner.ReadDvdSectorCount(0x9, 714_544) == 714_544);
        Require(WindowsImapiBurner.ReadDvdSectorCount(0x6, 2_295_104) == 2_295_104);
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadDvdSectorCount(0x9, 0));
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadDvdSectorCount(0xB, 4_171_712));
        // Neither the raw geometry nor an actual Free value receives the legacy estimate.
        Require(WindowsImapiBurner.ReadDvdMediaCapacity(0x9, 0x2, 714546, 713936)
            == new OpticalMediaCapacity(714546, 713936));
        Require(WindowsImapiBurner.ReadDvdMediaCapacity(0x6, 0x2, 2295104, 2295104)
            == new OpticalMediaCapacity(2295104, 2295104));
        Require(WindowsImapiBurner.ReadDvdMediaCapacity(0x9, 0x2, 714546, 0).WritableSectors == 0);
        Throws<IOException>(() => WindowsImapiBurner.ReadDvdMediaCapacity(0x9, 0x2, 714546, 714547));
        Throws<IOException>(() => WindowsImapiBurner.ReadDvdMediaCapacity(0x9, 0x2, 714546, -1));
        // A formatted erasable disc is overwritten from LBA 0, so its old free
        // extent no longer limits the writable payload.
        Require(WindowsImapiBurner.ReadDvdMediaCapacity(0x7, 0x1, 2295104, 0)
            == new OpticalMediaCapacity(2295104, 2295104));

        byte[] descriptor = new byte[12];
        descriptor[3] = 8;
        descriptor[8] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(4), 12_219_392);
        var bd = new ReadOnlyTransport([0x23, 0, 0, 0, 0, 0, 0, 1, 0, 0], descriptor);
        descriptor[10] = 16;
        Require(WindowsImapiBurner.ReadBluRaySectorCount(bd) == 12_219_392);
        descriptor[8] = 2;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRaySectorCount(bd));
        descriptor[10] = 0;
        Require(WindowsImapiBurner.ReadBluRaySectorCount(bd) == 12_219_392);
        descriptor[8] = 3;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRaySectorCount(bd));
        descriptor[8] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(4), 24_438_784);
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRaySectorCount(bd));

        var bdMedia = new BluRayReadOnlyTransport();
        Require(WindowsImapiBurner.ReadBluRayMediaCapacity(bdMedia)
            == new OpticalMediaCapacity(12_219_392, 12_219_392));
        Require(bdMedia.Commands.SequenceEqual(new byte[] { 0x46, 0x51, 0x23, 0x52 }));
        bdMedia.FreeBlocks -= 32;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRayMediaCapacity(bdMedia));
        bdMedia.FreeBlocks += 32;
        bdMedia.FormatStatus = 2;
        bdMedia.SpareClusters = 0;
        Require(WindowsImapiBurner.ReadBluRayMediaCapacity(bdMedia)
            == new OpticalMediaCapacity(12_219_392, 0));
        bdMedia.SpareClusters = 4096;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRayMediaCapacity(bdMedia));
        Console.WriteLine("calibration-media-capacity: separate geometry/payload limits, CD reservation, direct DVD Free, strict BD layout and read-only queries passed");
    }

    private sealed class TotalOnlyReader : IOpticalDiscBurner
    {
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => Task.FromResult(714546L);
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request, IProgress<OpticalBurnProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BluRayReadOnlyTransport : IMmcCommandTransport
    {
        internal uint FreeBlocks = 12_219_392;
        internal byte FormatStatus = 1;
        internal int SpareClusters = 4096;
        internal List<byte> Commands { get; } = [];

        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            Commands.Add(cdb[0]);
            Require(allocationLength == BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7, 2)));
            byte[] data;
            switch (cdb[0])
            {
                case 0x46:
                    data = new byte[8];
                    data[3] = 4;
                    data[7] = 0x41;
                    break;
                case 0x51:
                    data = new byte[12];
                    data[1] = 10;
                    data[6] = 1;
                    break;
                case 0x23:
                    data = new byte[12];
                    data[3] = 8;
                    data[8] = FormatStatus;
                    data[9] = (byte)(SpareClusters >> 16);
                    data[10] = (byte)(SpareClusters >> 8);
                    data[11] = (byte)SpareClusters;
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 12_219_392);
                    break;
                case 0x52:
                    Require(cdb[1] == 1 && BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2, 4)) == 1);
                    data = new byte[36];
                    data[1] = 34;
                    data[2] = 1;
                    data[6] = 0x40;
                    data[7] = 1;
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), FreeBlocks);
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 12_219_392);
                    break;
                default:
                    throw new InvalidOperationException("Capacity read must not issue a writing or preparation command.");
            }
            return new(0, [], data);
        }
    }

    private sealed class ReadOnlyTransport(byte[] expectedReadCommand, byte[] data) : IMmcCommandTransport
    {
        internal int HResult { get; set; }
        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            Require(cdb.SequenceEqual(expectedReadCommand)
                && allocationLength == BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7, 2)));
            return new(HResult, [], data);
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Unexpected calibration medium capacity or device command.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
