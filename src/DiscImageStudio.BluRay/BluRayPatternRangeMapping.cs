namespace DiscImageStudio.BluRay;

/// <summary>Calibration pattern confined to one drawing range; every other payload byte
/// stays zero so the burn still covers the whole disc address space. Custom
/// samplers stay sequential unless explicitly declared thread-safe.</summary>
public sealed class BluRayPatternRangeMapping : IBluRayBatchPayloadMapping
{
    private readonly BluRayDiscParameters _disc;
    private readonly BluRayImagePayloadMapping _image;
    private readonly uint _startSector;
    private readonly uint _endSectorExclusive;

    public BluRayPatternRangeMapping(
        BluRayDiscParameters disc,
        Func<double, double, byte> sampleLuminance,
        uint startSector,
        uint endSectorExclusive,
        bool invertMapping,
        bool allowParallelSampling = false)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(sampleLuminance);
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
        _image = new BluRayImagePayloadMapping(
            disc, sampleLuminance, new BluRayRunLengthMapping(invertMapping), allowParallelSampling);
        _startSector = startSector;
        _endSectorExclusive = endSectorExclusive;
    }

    bool IBluRayBatchPayloadMapping.CanGenerateInParallel(BluRayDiscParameters disc)
        => _image.CanGenerateInParallel(disc);

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
        if (firstSector < _startSector || firstSector >= _endSectorExclusive)
        {
            destination.Clear();
            return true;
        }
        return _image.TryFillScrambledCluster(disc, firstSector, destination, cancellationToken);
    }

    public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
    {
        if (context.Sector < _startSector || context.Sector >= _endSectorExclusive)
        {
            destination.Clear();
            return;
        }

        _image.FillScrambledPayload(context, destination);
    }
}
