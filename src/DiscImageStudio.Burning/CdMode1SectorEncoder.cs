using System.Buffers.Binary;

namespace DiscImageStudio.Burning;

/// <summary>Builds the main channel of a Mode 1 CD-ROM sector for RAW DAO writing.</summary>
internal static class CdMode1SectorEncoder
{
    internal const int PayloadBytes = 2048;
    internal const int SectorBytes = 2352;
    private const int HeaderOffset = 12;
    private const int PayloadOffset = 16;
    private const int EdcOffset = 2064;
    private const int ParityPOffset = 2076;
    private const int ParityQOffset = 2248;
    private const long MaximumLba = 100 * 60 * 75 - 151;
    private static readonly uint[] EdcTable = CreateEdcTable();
    private static readonly byte[] MultiplyByTwo = CreateMultiplyByTwo();
    private static readonly byte[] DivideByThree = CreateDivideByThree();
    private static readonly byte[] ScrambleMask = CreateScrambleMask();

    /// <summary>
    /// Encodes an LBA-addressed 2048-byte payload, including EDC, P/Q parity,
    /// scrambling, in the little-endian main-channel order expected by IMAPI.
    /// The result is ready to occupy the first 2352 bytes of an IMAPI RAW sector.
    /// </summary>
    internal static void Encode(ReadOnlySpan<byte> payload, long lba, Span<byte> destination)
    {
        if (payload.Length != PayloadBytes)
        {
            throw new ArgumentException("A Mode 1 payload must contain exactly 2048 bytes.", nameof(payload));
        }

        if (destination.Length < SectorBytes)
        {
            throw new ArgumentException("A RAW main-channel sector requires 2352 bytes.", nameof(destination));
        }

        if (lba < -150 || lba > MaximumLba)
        {
            throw new ArgumentOutOfRangeException(nameof(lba), "The sector address must fit a two-digit CD minute field.");
        }

        Span<byte> sector = destination[..SectorBytes];
        // Copy first so callers may also encode an overlapping payload in place.
        payload.CopyTo(sector.Slice(PayloadOffset, PayloadBytes));
        sector[..PayloadOffset].Clear();
        sector.Slice(1, 10).Fill(0xFF);
        int address = checked((int)(lba + 150));
        sector[12] = ToBcd(address / (60 * 75));
        sector[13] = ToBcd(address / 75 % 60);
        sector[14] = ToBcd(address % 75);
        sector[15] = 1;

        uint edc = 0;
        foreach (byte value in sector[..EdcOffset])
        {
            edc = (edc >> 8) ^ EdcTable[(byte)(edc ^ value)];
        }

        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(EdcOffset, sizeof(uint)), edc);
        sector.Slice(EdcOffset + sizeof(uint), 8).Clear();

        // ECMA-130 Annex A: P consists of 86 RS(26,24) words; Q consists of
        // 52 RS(45,43) diagonal words and includes the newly generated P bytes.
        WriteParity(sector[HeaderOffset..ParityPOffset], 86, 24, 2, 86, sector[ParityPOffset..ParityQOffset]);
        WriteParity(sector[HeaderOffset..ParityQOffset], 52, 43, 86, 88, sector[ParityQOffset..]);

        // ECMA-130 sections 15-16 scramble after sync and exchange adjacent
        // bytes for the physical F1/CIRC representation. IMAPI consumes the
        // little-endian (WAV) audio representation: converting physical F1
        // bytes to that representation exchanges them a second time. These
        // two exchanges cancel, leaving scrambled bytes in sector order.
        // This also matches cdrdao's RAW MMC path: lec_scramble's exchange is
        // reversed by dao.cc for GenericMMC::bigEndianSamples() == 0.
        // Subchannels are outside this 2352-byte region.
        for (int index = HeaderOffset; index < SectorBytes; index++)
        {
            sector[index] ^= ScrambleMask[index - HeaderOffset];
        }

    }

    private static void WriteParity(
        ReadOnlySpan<byte> source,
        int wordCount,
        int dataLength,
        int wordStep,
        int symbolStep,
        Span<byte> parity)
    {
        for (int word = 0; word < wordCount; word++)
        {
            int position = word / 2 * wordStep + word % 2;
            byte sum = 0;
            byte polynomialAtTwo = 0;
            for (int symbol = 0; symbol < dataLength; symbol++)
            {
                byte value = source[position];
                sum ^= value;
                polynomialAtTwo = (byte)(MultiplyByTwo[polynomialAtTwo] ^ value);
                position += symbolStep;
                if (position >= source.Length)
                {
                    position -= source.Length;
                }
            }

            // Solve C(1)=C(alpha)=0, alpha=2, for the two trailing parity
            // symbols over GF(256), with primitive polynomial x^8+x^4+x^3+x^2+1.
            byte first = DivideByThree[MultiplyByTwo[MultiplyByTwo[polynomialAtTwo]] ^ sum];
            parity[word] = first;
            parity[word + wordCount] = (byte)(first ^ sum);
        }
    }

    private static byte ToBcd(int value) => (byte)((value / 10 << 4) | value % 10);

    private static uint[] CreateEdcTable()
    {
        uint[] table = new uint[256];
        for (int value = 0; value < table.Length; value++)
        {
            uint remainder = (uint)value;
            for (int bit = 0; bit < 8; bit++)
            {
                remainder = (remainder >> 1) ^ ((remainder & 1) != 0 ? 0xD8018001u : 0);
            }

            table[value] = remainder;
        }

        return table;
    }

    private static byte[] CreateMultiplyByTwo()
    {
        byte[] table = new byte[256];
        for (int value = 0; value < table.Length; value++)
        {
            int product = value << 1;
            table[value] = (byte)(product ^ ((product & 0x100) != 0 ? 0x11D : 0));
        }

        return table;
    }

    private static byte[] CreateDivideByThree()
    {
        byte[] table = new byte[256];
        for (int value = 0; value < table.Length; value++)
        {
            table[value ^ MultiplyByTwo[value]] = (byte)value;
        }

        return table;
    }

    private static byte[] CreateScrambleMask()
    {
        byte[] mask = new byte[SectorBytes - HeaderOffset];
        int register = 1;
        for (int index = 0; index < mask.Length; index++)
        {
            int value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                value |= (register & 1) << bit;
                register = (register >> 1) | (((register ^ (register >> 1)) & 1) << 14);
            }

            mask[index] = (byte)value;
        }

        return mask;
    }
}
