namespace DiscImageStudio.AutoCalibration;

internal sealed class CalibrationRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E37_79B9_7F4A_7C15UL;
            ulong value = _state;
            value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return value ^ (value >> 31);
        }
    }

    public double NextUnit() => (NextUInt64() >> 11) / (double)(1UL << 53);

    public double NextGaussian()
    {
        double radius = Math.Sqrt(-2 * Math.Log(Math.Max(NextUnit(), 1e-300)));
        return radius * Math.Cos(2 * Math.PI * NextUnit());
    }
}
