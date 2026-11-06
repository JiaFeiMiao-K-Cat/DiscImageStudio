using DiscImageStudio.Burning;

internal static class CdDataSubchannelPatchTests
{
    internal static void Run()
    {
        VerifyCapturedTimingVectors();
        VerifyBoundariesAndUnrelatedChannels();
        VerifyExistingMetadata();
        Console.WriteLine("cd-data-subchannel-patch: passed (ADR3 replacement, captured timing/CRC vectors, BCD boundaries, TOC/ADR2 and P/R-W preservation)");
    }

    private static void VerifyCapturedTimingVectors()
    {
        // Native IMAPI mode 1 timing captures for a 512-sector track, with
        // CONTROL changed to data and independently calculated Q CRCs.
        // These fix the actual template's pregap countdown at 150..1.
        (long Lba, string Q)[] vectors =
        [
            (-150, "4101000002000000000082C3"),
            (-149, "41010000017400000001C882"),
            (-2, "4101000000020000017330C5"),
            (-1, "41010000000100000174AEF0"),
            (0, "410101000000000002002832"),
            (1, "410101000001000002019242"),
            (511, "410101000661000008618512"),
            (512, "41AA010000000000086297E8"),
            (513, "41AA01000001000008632D98"),
        ];
        // Actual default-ISRC audio Q captured at LBA -131. It has a valid
        // CRC but cannot be used as a data-track positioning packet.
        byte[] audioQ = Convert.FromHexString("03000000000000000019CD28");
        VerifyCrc(audioQ);
        foreach ((long lba, string expected) in vectors)
        {
            byte[] subchannel = PackQ(audioQ);
            CdDataRawImageStream.PatchDataSubchannel(subchannel, lba, 512);
            byte[] actual = DecodeQ(subchannel);
            Require(actual.AsSpan().SequenceEqual(Convert.FromHexString(expected)),
                $"complete captured time/CRC vector at LBA {lba}");
            VerifyCrc(actual);
        }
    }

    private static void VerifyBoundariesAndUnrelatedChannels()
    {
        // Nonzero metadata and audio flags ensure all time fields (including
        // ZERO) are rebuilt rather than retaining any ISRC payload bits.
        byte[] isrc = Convert.FromHexString("339ABCDEFEDCBA9876540000");
        foreach (long sectors in new long[] { 300, 512, 359845, 443100 })
        {
            long[] positions = [-150, -149, -76, -75, -74, -2, -1, 0, 1, 74, 75,
                4349, 4350, 4499, 4500, sectors - 1, sectors, sectors + 1,
                sectors + 74, sectors + 75, sectors + 6749];
            foreach (long lba in positions)
            {
                byte[] subchannel = PackQ(isrc);
                byte[] original = (byte[])subchannel.Clone();
                CdDataRawImageStream.PatchDataSubchannel(subchannel, lba, sectors);
                byte[] q = DecodeQ(subchannel);
                VerifyPosition(q, lba, sectors);
                VerifyCrc(q);
                VerifyOtherChannels(subchannel, original, lba >= 0 && lba < sectors);
                byte[] once = (byte[])subchannel.Clone();
                CdDataRawImageStream.PatchDataSubchannel(subchannel, lba, sectors);
                Require(subchannel.AsSpan().SequenceEqual(once), "converted data Q is stable on a second pass");
            }
        }
    }

    private static void VerifyExistingMetadata()
    {
        // Lead-in TOC uses a different layout. Keep its POINT and PMIN/PSEC/
        // PFRAME fields, and keep catalog-number ADR2 in every region.
        foreach (byte point in new byte[] { 1, 0xA0, 0xA1, 0xA2 })
        {
            byte[] toc = Convert.FromHexString("010000950000000102000000");
            toc[2] = point;
            VerifyPreserved(toc, -151);
        }
        foreach (long lba in new long[] { -22650, -151, -150, -1, 0, 299, 300, 7049 })
            VerifyPreserved(Convert.FromHexString("021234567890123000740000"), lba);

        VerifyPreserved(Convert.FromHexString("010101000000000002005A28"), 0);
        VerifyPreserved(Convert.FromHexString("01AA0100000000000600741A"), 300);
        // Do not interpret an unexpected lead-in packet as program timing.
        VerifyPreserved(Convert.FromHexString("03000000000000000019CD28"), -151);
    }

    private static void VerifyPreserved(byte[] q, long lba)
    {
        byte[] subchannel = PackQ(q);
        byte[] original = (byte[])subchannel.Clone();
        CdDataRawImageStream.PatchDataSubchannel(subchannel, lba, 300);
        byte[] actual = DecodeQ(subchannel);
        Require(actual[0] == ((q[0] & 15) | 0x40), "existing Q mode is preserved with data CONTROL");
        Require(actual.AsSpan(1, 9).SequenceEqual(q.AsSpan(1, 9)), "existing metadata is byte-for-byte unchanged");
        VerifyCrc(actual);
        VerifyOtherChannels(subchannel, original, lba >= 0 && lba < 300);
    }

    internal static void VerifyPosition(byte[] q, long lba, long sectors)
    {
        Require(q[0] == 0x41, "replacement uses data CONTROL and positioning ADR1");
        Require(q[1] == (lba < sectors ? 1 : 0xAA), "track number distinguishes track 1 and lead-out");
        Require(q[2] == (lba < 0 ? 0 : 1), "index distinguishes pregap from program/lead-out");
        long relative = DecodeTime(q.AsSpan(3, 3));
        Require(lba < 0 ? relative + lba == 0 : lba < sectors ? relative == lba : relative + sectors == lba,
            $"relative time follows the native single-track timeline at LBA {lba}");
        Require(q[6] == 0, "ZERO byte does not retain ISRC bits");
        Require(DecodeTime(q.AsSpan(7, 3)) - 150 == lba, "absolute time identifies the current LBA");
    }

    private static int DecodeTime(ReadOnlySpan<byte> msf)
    {
        int[] values = new int[3];
        for (int index = 0; index < 3; index++)
        {
            byte value = msf[index];
            Require((value & 15) <= 9 && (value >> 4) <= 9, "time is valid BCD");
            values[index] = (value >> 4) * 10 + (value & 15);
        }
        Require(values[1] < 60 && values[2] < 75, "time fields fit MSF");
        return (values[0] * 60 + values[1]) * 75 + values[2];
    }

    private static void VerifyCrc(byte[] q)
    {
        // Independent polynomial long division of the full codeword after
        // undoing the transmitted CRC complement: x^16 + x^12 + x^5 + 1.
        bool[] dividend = new bool[96];
        for (int bit = 0; bit < dividend.Length; bit++)
            dividend[bit] = (((q[bit / 8] >> (7 - bit % 8)) & 1) != 0) ^ (bit >= 80);
        for (int bit = 0; bit < 80; bit++)
            if (dividend[bit])
                foreach (int offset in new[] { 0, 4, 11, 16 }) dividend[bit + offset] ^= true;
        Require(!dividend.Any(value => value), "Q codeword has zero independent CRC remainder");
    }

    private static byte[] PackQ(byte[] q)
    {
        byte[] subchannel = new byte[96];
        for (int bit = 0; bit < 96; bit++)
            subchannel[bit] = (byte)(((bit * 29 + 83) & 0xBF)
                | (((q[bit / 8] >> (7 - bit % 8)) & 1) << 6));
        return subchannel;
    }

    private static byte[] DecodeQ(byte[] subchannel)
    {
        byte[] q = new byte[12];
        for (int bit = 0; bit < 96; bit++)
            q[bit / 8] |= (byte)(((subchannel[bit] >> 6) & 1) << (7 - bit % 8));
        return q;
    }

    private static void VerifyOtherChannels(byte[] actual, byte[] original, bool program)
    {
        for (int bit = 0; bit < 96; bit++)
        {
            Require((actual[bit] & 0x3F) == (original[bit] & 0x3F), "R-W bits are unchanged");
            Require((actual[bit] & 0x80) == (program ? 0 : original[bit] & 0x80), "P pause handling is unchanged");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD data subchannel patch: " + message);
    }
}
