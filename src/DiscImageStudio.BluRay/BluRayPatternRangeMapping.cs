namespace DiscImageStudio.BluRay;

/// <summary>Calibration pattern confined to one drawing range; every other payload byte
/// stays zero so the burn still covers the whole disc address space. Custom
/// samplers stay sequential unless explicitly declared thread-safe.</summary>
public sealed class BluRayPatternRangeMapping : IBluRayBatchPayloadMapping
{
    private readonly BluRayDiscParameters _disc;
    private readonly BluRayImagePayloadMapping? _image;
    private readonly (long Start, long EndExclusive)[]? _bands;
    private readonly byte _dark;
    private readonly byte _light;
    private readonly double[] _switchChannels = [];
    private readonly byte?[] _switchTones = [];
    private readonly uint _startSector;
    private readonly uint _endSectorExclusive;

    public BluRayPatternRangeMapping(
        BluRayDiscParameters disc,
        Func<double, double, byte> sampleLuminance,
        uint startSector,
        uint endSectorExclusive,
        bool invertMapping,
        bool allowParallelSampling = false)
        : this(disc, startSector, endSectorExclusive)
    {
        ArgumentNullException.ThrowIfNull(sampleLuminance);
        _image = new BluRayImagePayloadMapping(
            disc, sampleLuminance, new BluRayRunLengthMapping(invertMapping), allowParallelSampling);
    }

    /// <summary>An immutable source raster with exact squared-radius clipping.
    /// The range wrapper preserves raster batching and safe pixel reuse.</summary>
    public BluRayPatternRangeMapping(BluRayDiscParameters disc, BluRayGray8Raster raster,
        uint startSector, uint endSectorExclusive, bool invertMapping,
        double minimumRadiusSquared, double maximumRadiusSquared)
        : this(disc, startSector, endSectorExclusive)
    {
        _image = new BluRayImagePayloadMapping(disc, raster, new BluRayRunLengthMapping(invertMapping),
            minimumRadiusSquared, maximumRadiusSquared);
    }

    /// <summary>The broad edges align to recording units. Optional sweep transitions are
    /// fractions of the full physical track. A null luminance restores the enclosing band.</summary>
    public BluRayPatternRangeMapping(BluRayDiscParameters disc, uint startSector, uint endSectorExclusive,
        IReadOnlyList<(long Start, long EndExclusive)> bands, bool invertMapping,
        IReadOnlyList<(double Progress, byte? Luminance)>? angleSwitches = null)
        : this(disc, startSector, endSectorExclusive)
    {
        ArgumentNullException.ThrowIfNull(bands);
        _bands = bands.ToArray();
        long previousEnd = startSector;
        foreach (var (start, end) in _bands)
        {
            if (start < previousEnd || start >= end || end > endSectorExclusive
                || start % BluRayDiscParameters.SectorsPerCluster != 0 || end % BluRayDiscParameters.SectorsPerCluster != 0)
                throw new ArgumentException("Calibration bands must be ordered, cluster-aligned ranges within the drawing area.", nameof(bands));
            previousEnd = end;
        }
        var tone = new BluRayRunLengthMapping(invertMapping);
        _dark = tone.Map(0, default);
        _light = tone.Map(255, default);
        if (angleSwitches is null) return;
        _switchChannels = new double[angleSwitches.Count];
        _switchTones = new byte?[angleSwitches.Count];
        double channels = (long)(disc.TotalSectors / BluRayDiscParameters.SectorsPerCluster)
            * BluRayDiscParameters.ChannelBitsPerRecordingUnit;
        double previous = -1;
        for (int i = 0; i < angleSwitches.Count; i++)
        {
            var (q, luminance) = angleSwitches[i];
            if (!double.IsFinite(q) || q <= previous || q < 0 || q > 1)
                throw new ArgumentException("Sweep transitions must be ordered positions on the track.", nameof(angleSwitches));
            _switchChannels[i] = q * channels;
            _switchTones[i] = luminance is byte gray ? tone.Map(gray, default) : null;
            previous = q;
        }
    }

    private BluRayPatternRangeMapping(BluRayDiscParameters disc, uint startSector, uint endSectorExclusive)
    {
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
        if (startSector >= endSectorExclusive || endSectorExclusive > disc.TotalSectors
            || startSector % BluRayDiscParameters.SectorsPerCluster != 0
            || endSectorExclusive % BluRayDiscParameters.SectorsPerCluster != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endSectorExclusive),
                "The calibration drawing range must be 32-sector aligned and lie inside the disc.");
        }

        _disc = disc;
        _startSector = startSector;
        _endSectorExclusive = endSectorExclusive;
    }

    bool IBluRayBatchPayloadMapping.CanGenerateInParallel(BluRayDiscParameters disc)
        => _bands is not null ? disc == _disc : _image!.CanGenerateInParallel(disc);

    private bool TryFillAddressPattern(uint sector, Span<byte> destination)
    {
        if (sector < _startSector || sector >= _endSectorExclusive)
        {
            destination.Clear();
            return true;
        }
        if (_bands is null) return false;
        byte background = _light;
        foreach (var (start, end) in _bands)
        {
            if (sector >= start && sector < end)
            {
                background = _dark;
                break;
            }
        }
        if (_switchChannels.Length == 0) destination.Fill(background);
        else FillSweep(sector, destination, background);
        return true;
    }

    private void FillSweep(uint sector, Span<byte> destination, byte background)
    {
        double start = (long)(sector / BluRayDiscParameters.SectorsPerCluster)
            * BluRayDiscParameters.ChannelBitsPerRecordingUnit;
        double end = start + BluRayDiscParameters.ChannelBitsPerRecordingUnit;
        int index = Array.BinarySearch(_switchChannels, start);
        int next = index < 0 ? ~index : index + 1;
        byte initial = next == 0 ? background : _switchTones[next - 1] ?? background;
        if (next == _switchChannels.Length || _switchChannels[next] >= end)
        {
            destination.Fill(initial);
            return;
        }

        // Adjacent host payload bytes need not occupy adjacent track positions.
        // Only a cluster containing a transition needs the per-byte LDC layout.
        var offsets = BluRayImagePayloadMapping.PayloadChannelOffsets(sector, destination.Length);
        for (int i = 0; i < destination.Length; i++)
        {
            double position = start + offsets[i];
            byte value = initial;
            for (int j = next; j < _switchChannels.Length && _switchChannels[j] <= position; j++)
                value = _switchTones[j] ?? background;
            destination[i] = value;
        }
    }

    bool IBluRayBatchPayloadMapping.TryFillScrambledCluster(BluRayDiscParameters disc, uint firstSector,
        Span<byte> destination, CancellationToken cancellationToken)
    {
        if (disc != _disc || firstSector % BluRayDiscParameters.SectorsPerCluster != 0
            || firstSector >= disc.TotalSectors
            || destination.Length != BluRayDiscParameters.PayloadBytesPerCluster)
            throw new ArgumentException("A complete aligned pattern cluster must match the source disc.");
        cancellationToken.ThrowIfCancellationRequested();
        // Both range edges are cluster-aligned. Outside clusters remain zero in
        // the scrambled domain without invoking the pattern sampler at all.
        return TryFillAddressPattern(firstSector, destination)
            || _image!.TryFillScrambledCluster(disc, firstSector, destination, cancellationToken);
    }

    public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
    {
        if (!TryFillAddressPattern(context.Sector, destination))
        {
            _image!.FillScrambledPayload(context, destination);
        }
    }
}
