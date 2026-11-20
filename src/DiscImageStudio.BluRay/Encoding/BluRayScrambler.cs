namespace DiscImageStudio.BluRay.Encoding;

/// <summary>BD-R and BD-RE additive data-frame scrambler (ISO/IEC 30190 and 30192, §13.4).</summary>
public static class BluRayScrambler
{
    /// <summary>PSN bits 19..5 seed s14..s0; s15 is one. All 32 frames in a cluster share a mask.</summary>
    public static byte[] CreateMask(uint physicalSectorNumber, int length = BluRayEdc.DataFrameBytes)
    {
        if (length < 0 || length > BluRayEdc.DataFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(length));
        byte[] mask = new byte[length];
        FillMask(physicalSectorNumber, mask);
        return mask;
    }

    public static void FillMask(uint physicalSectorNumber, Span<byte> destination)
    {
        if (destination.Length > BluRayEdc.DataFrameBytes)
            throw new ArgumentException("A scrambling sequence cannot exceed one 2052-byte data frame.", nameof(destination));
        ushort state = (ushort)(0x8000 | ((physicalSectorNumber >> 5) & 0x7fff));
        for (int index = 0; index < destination.Length; index++)
        {
            destination[index] = (byte)state;
            for (int bit = 0; bit < 8; bit++)
            {
                int feedback = ((state >> 15) ^ (state >> 14) ^ (state >> 12) ^ (state >> 3)) & 1;
                state = (ushort)((state << 1) | feedback);
            }
        }
    }

    /// <summary>Scrambling and descrambling are the same XOR operation, starting at frame byte zero.</summary>
    public static void Transform(Span<byte> data, uint physicalSectorNumber)
    {
        if (data.Length > BluRayEdc.DataFrameBytes)
            throw new ArgumentException("Transform one data frame at a time.", nameof(data));
        Span<byte> mask = stackalloc byte[BluRayEdc.DataFrameBytes];
        FillMask(physicalSectorNumber, mask[..data.Length]);
        for (int i = 0; i < data.Length; i++) data[i] ^= mask[i];
    }
}
