using System.Numerics;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Cd;

/// <summary>The six guide rings are fixed fractions of track time. Outside the rings, the
/// dithered carrier repeats on the original byte clock, without resetting its phase.</summary>
internal sealed class CdClockRulerSampler
{
    private const int Period = CalibrationClockRulerPattern.PeriodBytes;
    private const int TableLength = Period * 257;
    private static readonly byte[] Carrier = CreateCarrier();
    private readonly (long EndExclusive, byte? Gray)[] _regions;
    private int _region;

    internal CdClockRulerSampler(long totalBytes)
    {
        var regions = new List<(long, byte?)>();
        foreach (double station in CalibrationClockRulerPattern.Stations)
        {
            regions.Add(((long)Math.Ceiling((station - CalibrationClockRulerPattern.GutterHalfWidth) * totalBytes), null));
            regions.Add(((long)Math.Ceiling((station - CalibrationClockRulerPattern.InkHalfWidth) * totalBytes), 255));
            regions.Add(((long)Math.Floor((station + CalibrationClockRulerPattern.InkHalfWidth) * totalBytes) + 1, 0));
            regions.Add(((long)Math.Floor((station + CalibrationClockRulerPattern.GutterHalfWidth) * totalBytes) + 1, 255));
        }
        regions.Add((totalBytes, null));
        _regions = regions.ToArray();
    }

    // GenerateSamplesToStream walks the logical track in order, with a fresh sampler per operation.
    internal byte SampleGray(long logicalByte)
    {
        while (logicalByte >= _regions[_region].EndExclusive) _region++;
        return _regions[_region].Gray ?? Carrier[(int)(logicalByte % TableLength)];
    }

    private static byte[] CreateCarrier()
    {
        // PCG64 XSL-RR, with the state and stream obtained from NumPy's default_rng(73477).
        // Each draw uses the top 53 bits, just like Generator.random(dtype=float64).
        UInt128 state = new(0xfb4f57c60253da18, 0x952ecf629b240551);
        UInt128 increment = new(0x4073a918a227baab, 0x1881615d9e8585cf);
        UInt128 multiplier = new(0x2360ed051fc65da4, 0x4385df649fccf645);
        var density = new double[Period];
        for (int i = 0; i < density.Length; i++)
        {
            double phase = i * (Math.Tau / Period);
            density[i] = CalibrationClockRulerPattern.PilotInkFraction(phase);
        }

        var carrier = new byte[TableLength];
        for (int i = 0; i < carrier.Length; i++)
        {
            state = unchecked(state * multiplier + increment);
            ulong bits = BitOperations.RotateRight((ulong)(state >> 64) ^ (ulong)state, (int)(state >> 122));
            double draw = (bits >> 11) * (1.0 / (1L << 53));
            carrier[i] = draw < density[i % Period] ? (byte)0 : (byte)255;
        }
        return carrier;
    }
}
