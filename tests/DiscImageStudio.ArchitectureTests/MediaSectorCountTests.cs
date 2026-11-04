using System.Buffers.Binary;
using System.IO;
using DiscImageStudio.Burning;

internal static class MediaSectorCountTests
{
    internal static void Run()
    {
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

        Require(WindowsImapiBurner.ReadDvdSectorCount(0x9, 714_544) == 714_544);
        Require(WindowsImapiBurner.ReadDvdSectorCount(0x6, 2_295_104) == 2_295_104);
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadDvdSectorCount(0x9, 0));
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadDvdSectorCount(0xB, 4_171_712));

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
        Console.WriteLine("calibration-media-capacity: CD ATIP, DVD totals, BD format capacity, read-only queries and unsupported layouts passed");
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
