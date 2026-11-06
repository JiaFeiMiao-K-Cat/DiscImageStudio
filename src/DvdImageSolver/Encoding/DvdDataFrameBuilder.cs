using System.Buffers.Binary;

namespace DvdImageSolver.Encoding;

internal static class DvdDataFrameBuilder
{
    internal const int PayloadBytes = 2048;
    internal const int FrameBytes = 2064;
    internal const int FrameRows = 12;
    internal const int DataColumns = 172;

    private static readonly ushort[] ScramblerPresets =
    [
        0x0001, 0x5500, 0x0002, 0x2A00,
        0x0004, 0x5400, 0x0008, 0x2800,
        0x0010, 0x5000, 0x0020, 0x2001,
        0x0040, 0x4002, 0x0080, 0x0005,
    ];
    private static readonly byte[][] PayloadScramblerMasks = BuildPayloadScramblerMasks();

    internal static byte[] BuildScrambledFrame(ReadOnlySpan<byte> payload, uint physicalSectorNumber)
    {
        byte[] frame = BuildDataFrame(payload, physicalSectorNumber);
        ScrambleMainData(frame, physicalSectorNumber);
        return frame;
    }

    internal static byte[] BuildDataFrame(ReadOnlySpan<byte> payload, uint physicalSectorNumber)
    {
        if (payload.Length != PayloadBytes)
        {
            throw new ArgumentException($"Payload must contain exactly {PayloadBytes} bytes.", nameof(payload));
        }

        if (physicalSectorNumber > 0x00FF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalSectorNumber), "DVD PSN is a 24-bit value.");
        }

        byte[] frame = new byte[FrameBytes];
        frame[0] = 0x00; // Single-layer Data Zone Sector Information.
        frame[1] = (byte)(physicalSectorNumber >> 16);
        frame[2] = (byte)(physicalSectorNumber >> 8);
        frame[3] = (byte)physicalSectorNumber;
        ReedSolomon.AppendIed(frame.AsSpan(0, 4), frame.AsSpan(4, 2));
        frame.AsSpan(6, 6).Clear(); // DVD-R RSV default when not specified by the application.
        payload.CopyTo(frame.AsSpan(12, PayloadBytes));

        uint edc = ComputeEdc(frame.AsSpan(0, 2060));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2060, 4), edc);

        return frame;
    }

    internal static void ScrambleMainData(Span<byte> frame, uint physicalSectorNumber)
    {
        if (frame.Length != FrameBytes)
        {
            throw new ArgumentException($"Frame must contain exactly {FrameBytes} bytes.", nameof(frame));
        }

        TransformPayloadScrambling(frame.Slice(12, PayloadBytes), physicalSectorNumber);
    }

    internal static void TransformPayloadScrambling(
        Span<byte> payload,
        uint physicalSectorNumber)
    {
        if (payload.Length != PayloadBytes)
        {
            throw new ArgumentException(
                $"Payload must contain exactly {PayloadBytes} bytes.",
                nameof(payload));
        }

        ReadOnlySpan<byte> mask = PayloadScramblerMask(physicalSectorNumber);
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] ^= mask[index];
        }
    }

    internal static ReadOnlySpan<byte> PayloadScramblerMask(uint physicalSectorNumber)
        => PayloadScramblerMasks[(int)((physicalSectorNumber >> 4) & 0xF)];

    private static byte[][] BuildPayloadScramblerMasks()
    {
        byte[][] masks = new byte[ScramblerPresets.Length][];
        for (int presetIndex = 0; presetIndex < ScramblerPresets.Length; presetIndex++)
        {
            byte[] mask = new byte[PayloadBytes];
            ushort scrambler = ScramblerPresets[presetIndex];
            for (int index = 0; index < mask.Length; index++)
            {
                mask[index] = (byte)scrambler;
                for (int shift = 0; shift < 8; shift++)
                {
                    int feedback = ((scrambler >> 14) ^ (scrambler >> 10)) & 1;
                    scrambler = (ushort)(((scrambler << 1) | feedback) & 0x7FFF);
                }
            }

            masks[presetIndex] = mask;
        }

        return masks;
    }

    internal static uint ComputeEdc(ReadOnlySpan<byte> data)
    {
        // ECMA-267 16.4: this feedback form already computes data(x) * x^32 mod G(x).
        const uint polynomialWithoutTopBit = 0x80000011;
        uint remainder = 0;
        foreach (byte value in data)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                uint input = (uint)((value >> bit) & 1);
                uint feedback = (remainder >> 31) ^ input;
                remainder <<= 1;
                if (feedback != 0)
                {
                    remainder ^= polynomialWithoutTopBit;
                }
            }
        }

        return remainder;
    }

    internal static bool HasValidEdc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameBytes)
        {
            return false;
        }

        uint expected = BinaryPrimitives.ReadUInt32BigEndian(frame[^4..]);
        return expected == ComputeEdc(frame[..^4]);
    }

}
