namespace DiscImageStudio.Core.Calibration;

/// <summary>A 50%-duty address clock within one band. Progress is a fraction of the
/// entire physical track. Its instantaneous angular rate is track length / virtual
/// radius. A photograph shows the widest stripes where this equals the real turn rate.</summary>
public sealed class CalibrationAngleSweepStation
{
    private readonly double _trackLength;
    private readonly double _squaredSlope;
    public double StartProgress { get; }
    public double EndProgress { get; }
    public double StartVirtualRadiusMm { get; }
    public double EndVirtualRadiusMm { get; }

    private CalibrationAngleSweepStation(double start, double end, double inner, double outer, double length)
    {
        StartProgress = start;
        EndProgress = end;
        StartVirtualRadiusMm = inner;
        EndVirtualRadiusMm = outer;
        _trackLength = length;
        _squaredSlope = (outer - inner) * (outer + inner) / (end - start);
    }

    public double VirtualRadiusAt(double progress) => Math.Sqrt(StartVirtualRadiusMm * StartVirtualRadiusMm
        + (progress - StartProgress) * _squaredSlope);

    /// <summary>Unwrapped clock phase in radians, starting at zero in each station.</summary>
    public double PhaseAt(double progress) =>
        2 * _trackLength * (progress - StartProgress) / (StartVirtualRadiusMm + VirtualRadiusAt(progress));

    // The analytic inverse avoids scanning billions of optical channel positions.
    internal double ProgressAtPhase(double radians)
    {
        double u = radians / _trackLength;
        return StartProgress + u * (StartVirtualRadiusMm + .25 * _squaredSlope * u);
    }

    internal static byte Tone(double radians) => Math.IEEERemainder(radians, Math.Tau) is >= 0 and < Math.PI
        ? (byte)0 : (byte)255;

    internal static IReadOnlyList<CalibrationAngleSweepStation> Create(CalibrationTarget target, CalibrationGeometry geometry)
    {
        var stations = new CalibrationAngleSweepStation[target.WideAddressBands.Count];
        for (int i = 0; i < stations.Length; i++)
        {
            var band = target.WideBandProgress(i);
            double guard = .14 * (band.EndExclusive - band.Start);
            double start = band.Start + guard, end = band.EndExclusive - guard;
            double offset = i % 2 == 0 ? .65 : -.65;
            stations[i] = new(start, end, geometry.AtProgress(start).Radius - offset,
                geometry.AtProgress(end).Radius + offset, target.Parameters.TrackLengthMm);
        }
        return Array.AsReadOnly(stations);
    }
}

/// <summary>Frozen v13 clock transitions in physical address order. Backends can map
/// these once to their own address grid and advance a cursor during sequential writes.</summary>
public sealed class CalibrationAngleSweepSampler
{
    private readonly (double Start, double EndExclusive)[] _bands;
    private readonly (double Progress, byte? Luminance)[] _switches;
    public IReadOnlyList<(double Progress, byte? Luminance)> Switches { get; }

    public CalibrationAngleSweepSampler(CalibrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.HasAngleSweep) throw new ArgumentException("Expected a v13 angle-sweep target.", nameof(target));
        _bands = Enumerable.Range(0, target.WideAddressBands.Count).Select(target.WideBandProgress).ToArray();
        var changes = new List<(double, byte?)>();
        foreach (var station in target.AngleSweepStations)
        {
            // [0, pi) is dark, [pi, 2pi) is light. Outside these windows the writer
            // must use the original band and its medium-specific physical alignment.
            for (int halfTurn = 0; ; halfTurn++)
            {
                double q = station.ProgressAtPhase(halfTurn * Math.PI);
                if (q >= station.EndProgress) break;
                changes.Add((q, (byte)((halfTurn & 1) == 0 ? 0 : 255)));
            }
            changes.Add((station.EndProgress, null));
        }
        _switches = changes.ToArray();
        Switches = Array.AsReadOnly(_switches);
    }

    public bool TryGetLuminance(double fullTrackProgress, out byte luminance)
    {
        int first = 0, end = _switches.Length;
        while (first < end)
        {
            int mid = first + (end - first) / 2;
            if (_switches[mid].Progress <= fullTrackProgress) first = mid + 1;
            else end = mid;
        }
        byte? found = first == 0 ? null : _switches[first - 1].Luminance;
        luminance = found ?? 255;
        return found.HasValue;
    }

    public byte Sample(double fullTrackProgress)
    {
        if (TryGetLuminance(fullTrackProgress, out byte luminance)) return luminance;
        foreach (var (start, end) in _bands)
            if (start <= fullTrackProgress && fullTrackProgress < end) return 0;
        return 255;
    }

    public byte Sample(long position, long totalUnits)
    {
        if (totalUnits <= 0 || position < 0 || position >= totalUnits) throw new ArgumentOutOfRangeException();
        return Sample(position / (double)totalUnits);
    }
}
