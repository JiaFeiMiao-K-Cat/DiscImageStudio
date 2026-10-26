using DiscImageStudio.Burning;
using System.Buffers.Binary;

internal static class CdMode1SectorTests
{
    internal static void Run()
    {
        TestKnownHeaderAndScramble();
        TestPhysicalAndImapiByteOrder();
        TestSectorStructureAndParity();
        TestInPlaceEncodingAndSubchannelBoundary();
        TestInvalidArgumentsLeaveOutputUntouched();
        Console.WriteLine("cd-mode1-sectors: passed (EDC and all P/Q syndromes verified)");
    }

    private static void TestKnownHeaderAndScramble()
    {
        byte[] raw = new byte[2352];
        CdMode1SectorEncoder.Encode(new byte[2048], 0, raw);
        // ECMA-130 Annex B starts 01 80 00 60 00 28 00 1E ...;
        // header 00:02:00 mode 1 remains in sector order for IMAPI RAW input.
        byte[] expectedPrefix =
        [
            0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0x00, 0x01, 0x82, 0x00, 0x61,
            0x00, 0x28, 0x00, 0x1E, 0x80, 0x08, 0x60, 0x06,
        ];
        Require(raw.AsSpan(0, expectedPrefix.Length).SequenceEqual(expectedPrefix), "known RAW sync/header/scramble bytes");
        byte[] sector = DecodeMainChannel(raw);
        Require(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(2064, 4)) == 0x2B6813C5u, "zero LBA 0 EDC vector");
    }

    private static void TestPhysicalAndImapiByteOrder()
    {
        byte[] payload = new byte[2048];
        new Random(8821).NextBytes(payload);
        byte[] imapiSector = new byte[2352];
        CdMode1SectorEncoder.Encode(payload, 12345, imapiSector);
        byte[] standardSector = DecodeMainChannel(imapiSector);

        // ECMA-130 creates physical F1 bytes by exchanging adjacent bytes
        // after scrambling. Convert each resulting big-endian sample to
        // the WAV little-endian layout required by IMAPI's audio contract.
        ApplyReferenceScramble(standardSector);
        byte[] physicalF1 = new byte[2352];
        byte[] expectedImapi = new byte[2352];
        for (int index = 0; index < standardSector.Length; index += 2)
        {
            physicalF1[index] = standardSector[index + 1];
            physicalF1[index + 1] = standardSector[index];
            ushort sample = BinaryPrimitives.ReadUInt16BigEndian(physicalF1.AsSpan(index, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(expectedImapi.AsSpan(index, 2), sample);
        }

        Require(expectedImapi.AsSpan().SequenceEqual(imapiSector), "physical CD-ROM byte exchange and IMAPI little-endian conversion cancel");
        Require(!physicalF1.AsSpan().SequenceEqual(imapiSector), "physical F1 bytes must not be passed to IMAPI unchanged");
    }

    private static void TestSectorStructureAndParity()
    {
        long[] addresses = [-150, -1, 0, 1, 74, 75, 4349, 4350, 359844, 449849];
        Random random = new(19770209);
        foreach (long lba in addresses)
        {
            byte[] payload = new byte[2048];
            random.NextBytes(payload);
            if (lba == 0)
            {
                Array.Clear(payload);
            }
            else if (lba == 1)
            {
                Array.Fill(payload, (byte)0xFF);
            }

            byte[] raw = new byte[2352];
            CdMode1SectorEncoder.Encode(payload, lba, raw);
            byte[] sector = DecodeMainChannel(raw);
            Require(sector[0] == 0 && sector[11] == 0, $"sync endpoints at {lba}");
            Require(sector.AsSpan(1, 10).ToArray().All(value => value == 0xFF), $"sync ones at {lba}");
            long absoluteFrame = DecodeBcd(sector[12]) * 60L * 75 + DecodeBcd(sector[13]) * 75 + DecodeBcd(sector[14]);
            Require(absoluteFrame == lba + 150 && sector[15] == 1, $"BCD Mode 1 address at {lba}");
            Require(sector.AsSpan(16, 2048).SequenceEqual(payload), $"payload round trip at {lba}");
            Require(sector.AsSpan(2068, 8).ToArray().All(value => value == 0), $"reserved bytes at {lba}");
            Require(EdcRemainder(sector.AsSpan(0, 2068)) == 0, $"EDC codeword polynomial remainder at {lba}");

            // Test the parity-check equations themselves, without using the
            // encoder's lookup tables or its two-parity calculation.
            for (int column = 0; column < 86; column++)
            {
                byte[] codeword = new byte[26];
                for (int row = 0; row < codeword.Length; row++)
                {
                    codeword[row] = sector[12 + row * 86 + column];
                }

                Require(HasZeroSyndromes(codeword), $"P column {column} at {lba}");
            }

            for (int diagonal = 0; diagonal < 52; diagonal++)
            {
                byte[] codeword = new byte[45];
                for (int row = 0; row < 43; row++)
                {
                    int sourceIndex = (diagonal / 2 * 86 + diagonal % 2 + row * 88) % 2236;
                    codeword[row] = sector[12 + sourceIndex];
                }

                codeword[43] = sector[2248 + diagonal];
                codeword[44] = sector[2300 + diagonal];
                Require(HasZeroSyndromes(codeword), $"Q diagonal {diagonal} at {lba}");
            }
        }
    }

    private static void TestInPlaceEncodingAndSubchannelBoundary()
    {
        byte[] payload = new byte[2048];
        new Random(310).NextBytes(payload);
        byte[] expected = new byte[2352];
        CdMode1SectorEncoder.Encode(payload, 12345, expected);

        byte[] rawWithSubchannels = new byte[2448];
        Array.Fill(rawWithSubchannels, (byte)0xA5);
        payload.CopyTo(rawWithSubchannels, 0);
        CdMode1SectorEncoder.Encode(rawWithSubchannels.AsSpan(0, 2048), 12345, rawWithSubchannels);
        Require(rawWithSubchannels.AsSpan(0, 2352).SequenceEqual(expected), "overlapping source can be encoded in place");
        Require(rawWithSubchannels.AsSpan(2352).ToArray().All(value => value == 0xA5), "96 subchannel bytes remain untouched");
    }

    private static void TestInvalidArgumentsLeaveOutputUntouched()
    {
        byte[] output = new byte[2352];
        Array.Fill(output, (byte)0xA5);
        ExpectArgumentError(() => CdMode1SectorEncoder.Encode(new byte[2047], 0, output));
        ExpectArgumentError(() => CdMode1SectorEncoder.Encode(new byte[2049], 0, output));
        ExpectArgumentError(() => CdMode1SectorEncoder.Encode(new byte[2048], -151, output));
        ExpectArgumentError(() => CdMode1SectorEncoder.Encode(new byte[2048], 449850, output));
        ExpectArgumentError(() => CdMode1SectorEncoder.Encode(new byte[2048], long.MaxValue, output));
        ExpectArgumentError(() => CdMode1SectorEncoder.Encode(new byte[2048], 0, output.AsSpan(0, 2351)));
        Require(output.All(value => value == 0xA5), "invalid arguments do not partially overwrite a sector");
    }

    private static byte[] DecodeMainChannel(byte[] raw)
    {
        byte[] sector = raw.AsSpan(0, 2352).ToArray();
        ApplyReferenceScramble(sector);
        return sector;
    }

    private static void ApplyReferenceScramble(byte[] sector)
    {
        // Independent bit-sequence definition: s[0]=1, s[1..14]=0,
        // s[n+15]=s[n+1] XOR s[n], packed least-significant bit first.
        bool[] sequence = new bool[2340 * 8 + 15];
        sequence[0] = true;
        for (int bit = 0; bit < 2340 * 8; bit++)
        {
            sequence[bit + 15] = sequence[bit + 1] ^ sequence[bit];
            if (sequence[bit])
            {
                sector[12 + bit / 8] ^= (byte)(1 << (bit % 8));
            }
        }

    }

    private static uint EdcRemainder(ReadOnlySpan<byte> codeword)
    {
        uint remainder = 0;
        foreach (byte value in codeword)
        {
            remainder ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                remainder = (remainder >> 1) ^ ((remainder & 1) == 0 ? 0 : 0xD8018001u);
            }
        }

        return remainder;
    }

    private static bool HasZeroSyndromes(byte[] codeword)
    {
        int atOne = 0;
        int atAlpha = 0;
        int power = 1;
        for (int index = codeword.Length - 1; index >= 0; index--)
        {
            atOne ^= codeword[index];
            atAlpha ^= MultiplyInField(codeword[index], power);
            power = MultiplyInField(power, 2);
        }

        return atOne == 0 && atAlpha == 0;
    }

    private static int MultiplyInField(int left, int right)
    {
        int product = 0;
        while (right != 0)
        {
            if ((right & 1) != 0)
            {
                product ^= left;
            }

            right >>= 1;
            left <<= 1;
            if (left >= 256)
            {
                left ^= 0x11D;
            }
        }

        return product;
    }

    private static int DecodeBcd(byte value)
    {
        Require((value >> 4) <= 9 && (value & 15) <= 9, "valid BCD digit");
        return (value >> 4) * 10 + (value & 15);
    }

    private static void ExpectArgumentError(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("CD Mode 1: expected invalid argument rejection.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"CD Mode 1: {message}.");
        }
    }
}
