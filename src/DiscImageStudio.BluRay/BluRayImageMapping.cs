using System.Runtime.Intrinsics;

namespace DiscImageStudio.BluRay;

/// <summary>Converts an image sample into a post-scrambler payload byte. The built-in
/// 3T/6T mapping is an image convention; optical polarity needs physical calibration.</summary>
public interface IBluRayLuminanceMapping
{
    byte Map(byte luminance, BluRayPayloadBytePosition position);
}

/// <summary>A user byte's location after LDC interleaving and BIS multiplexing.</summary>
/// <remarks>Channel position is a nominal byte midpoint using the 2:3 modulation rate,
/// including dc-control insertion, sync and recording-unit run-in. Variable-length 17PP
/// codewords mean this is not an exact channel-bit boundary or a predicted optical mark.</remarks>
public readonly record struct BluRayPayloadBytePosition(
    uint Sector,
    int ByteOffset,
    uint PhysicalSectorNumber,
    int RecordingFrame,
    int LdcColumn,
    int EccColumn,
    double NominalChannelBitPosition,
    BluRayTrackPoint TrackPoint);

/// <summary>Immutable, tightly packed Gray8 source raster. Values are image intensities,
/// not measured disc reflectance. Source data is copied on construction.</summary>
public sealed class BluRayGray8Raster
{
    private readonly byte[] _pixels;

    public BluRayGray8Raster(int width, int height, ReadOnlySpan<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        long length = (long)width * height;
        if (length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), "Raster dimensions are too large.");
        if (pixels.Length != length)
            throw new ArgumentException("A Gray8 raster needs exactly width × height bytes.", nameof(pixels));
        Width = width;
        Height = height;
        _pixels = pixels.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>Fits the longest image dimension to the disc diameter, preserving aspect
    /// ratio. Coordinates are millimetres, with positive Y toward the image top. Samples
    /// use the nearest pixel centre; regions outside the fitted image are white.</summary>
    public byte SampleDiscPoint(double xMm, double yMm, double outerRadiusMm)
    {
        if (!double.IsFinite(xMm)) throw new ArgumentOutOfRangeException(nameof(xMm));
        if (!double.IsFinite(yMm)) throw new ArgumentOutOfRangeException(nameof(yMm));
        if (!double.IsFinite(outerRadiusMm) || outerRadiusMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMm));
        double scale = Math.Max(Width, Height) / (2 * outerRadiusMm);
        return SampleWithScale(xMm, yMm, scale);
    }

    // Only the validated, finite geometry fast path supplies these coordinates.
    internal byte SampleWithScale(double xMm, double yMm, double scale)
    {
        double x = Width / 2.0 + xMm * scale;
        double y = Height / 2.0 - yMm * scale;
        if (x < 0 || y < 0 || x >= Width || y >= Height) return 255;
        return _pixels[(int)y * Width + (int)x];
    }

    internal byte SampleWithTravelMargin(double xMm, double yMm, double scale,
        double tangentX, double tangentY, double curvatureBound, double marginMm,
        double tangentMargin, out double travelMm)
    {
        double x = Width / 2.0 + xMm * scale;
        double y = Height / 2.0 - yMm * scale;
        // Image Y points down, opposite to the physical tangent's Y component.
        tangentY = -tangentY;
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            // Staying outside any one edge is enough to remain in white space.
            // Only edges actually crossed by the anchor participate in this max.
            travelMm = 0;
            if (x < 0) travelMm = Math.Max(travelMm, SafeTravel(-x / scale - marginMm, tangentX + tangentMargin, curvatureBound));
            if (x >= Width) travelMm = Math.Max(travelMm, SafeTravel((x - Width) / scale - marginMm, -tangentX + tangentMargin, curvatureBound));
            if (y < 0) travelMm = Math.Max(travelMm, SafeTravel(-y / scale - marginMm, tangentY + tangentMargin, curvatureBound));
            if (y >= Height) travelMm = Math.Max(travelMm, SafeTravel((y - Height) / scale - marginMm, -tangentY + tangentMargin, curvatureBound));
            return 255;
        }
        int ix = (int)x, iy = (int)y;
        double left = (x - ix) / scale - marginMm;
        double right = (ix + 1.0 - x) / scale - marginMm;
        double top = (y - iy) / scale - marginMm;
        double bottom = (iy + 1.0 - y) / scale - marginMm;
        if (left <= 0 || right <= 0 || top <= 0 || bottom <= 0)
        {
            travelMm = 0;
            return _pixels[iy * Width + ix];
        }
        travelMm = Math.Min(
            Math.Min(SafeTravel(left, -tangentX + tangentMargin, curvatureBound),
                SafeTravel(right, tangentX + tangentMargin, curvatureBound)),
            Math.Min(SafeTravel(top, -tangentY + tangentMargin, curvatureBound),
                SafeTravel(bottom, tangentY + tangentMargin, curvatureBound)));
        return _pixels[iy * Width + ix];
    }

    private static double SafeTravel(double edgeDistance, double tangent, double curvatureBound)
    {
        if (edgeDistance <= 0) return 0;
        // For an outward Archimedean spiral, curvature <= 2 / innerRadius,
        // hence each coordinate's Taylor remainder <= s*s / innerRadius.
        // Solve k*s*s + v*s < distance. Use the stable form for each sign of v
        // so a boundary behind the tangent does not suffer subtractive cancellation.
        double root = Math.Sqrt(tangent * tangent + 4 * curvatureBound * edgeDistance);
        return tangent >= 0 ? 2 * edgeDistance / (root + tangent)
            : (root - tangent) / (2 * curvatureBound);
    }
}

/// <summary>Samples the source at each payload byte's nominal physical position, then
/// delegates intensity conversion to an explicitly injected mapping strategy.</summary>
/// <remarks>Only payload-byte positions are sampled. EDC, parity, BIS, sync, dc-control,
/// run-in and run-out are subsequently generated by the encoder. Their optical
/// contribution and drive defect relocation require separate physical calibration.</remarks>
public sealed class BluRayImagePayloadMapping : IBluRayBatchPayloadMapping
{
    private readonly BluRayDiscParameters _disc;
    private readonly Func<double, double, byte> _sampleLuminance;
    private readonly IBluRayLuminanceMapping _mapping;
    private readonly BluRayTrackGeometry _geometry;
    private readonly bool _supportsParallelGeneration;
    private readonly BluRayGray8Raster? _raster;
    private readonly double _rasterScale;

    public BluRayImagePayloadMapping(BluRayDiscParameters disc, BluRayGray8Raster raster,
        IBluRayLuminanceMapping mapping)
        : this(disc, CreateRasterSampler(disc, raster), mapping, allowParallelSampling: true)
    {
        _raster = raster;
        _rasterScale = Math.Max(raster.Width, raster.Height) / (2 * disc.OuterRadiusMm);
    }

    internal bool CanGenerateInParallel(BluRayDiscParameters disc)
        => _supportsParallelGeneration && _disc == disc;

    bool IBluRayBatchPayloadMapping.CanGenerateInParallel(BluRayDiscParameters disc)
        => CanGenerateInParallel(disc);

    bool IBluRayBatchPayloadMapping.TryFillScrambledCluster(BluRayDiscParameters disc, uint firstSector,
        Span<byte> destination, CancellationToken cancellationToken)
        => TryFillScrambledCluster(disc, firstSector, destination, cancellationToken);

    internal bool TryFillScrambledCluster(BluRayDiscParameters disc, uint firstSector,
        Span<byte> destination, CancellationToken cancellationToken)
    {
        if (_raster is null || _mapping is not BluRayRunLengthMapping runLength
            || disc != _disc || !_geometry.SupportsPixelReuse
            || _geometry.PixelReuseSafetyMarginMm >= 1 / (_rasterScale * 16))
            return false;
        if (firstSector % 32 != 0 || firstSector >= disc.TotalSectors
            || destination.Length != BluRayDiscParameters.PayloadBytesPerCluster)
            throw new ArgumentException("A complete aligned image cluster is required.");

        double clusterBase = (long)(firstSector / 32) * BluRayDiscParameters.ChannelBitsPerRecordingUnit;
        double margin = _geometry.PixelReuseSafetyMarginMm;
        double mmPerBit = _geometry.MillimetresPerChannelBit;
        double curvatureBound = 1 / _disc.InnerRadiusMm;
        double tangentMargin = 4 * margin / _disc.InnerRadiusMm + 64 * 2.2204460492503131e-16;
        double safeUntil = double.NegativeInfinity;
        byte luminance = 255;
        // LDC payload order jumps around the spiral. Walk physical positions in
        // ascending order, then scatter back to the original payload addresses.
        // Each anchor uses the original scalar geometry. A later point can reuse
        // its pixel only while a tangent + curvature envelope stays inside every
        // pixel edge, after allowing for position and tangent rounding. Points
        // near a boundary return to scalar sampling. The bound holds for the
        // entire intervening arc, including quadrants and image borders.
        int[] order = PayloadLayout.PhysicalOrder;
        double[] offsets = PayloadLayout.ChannelOffsets;
        for (int index = 0; index < order.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            int payload = order[index];
            double channel = offsets[payload];
            if (channel >= safeUntil)
            {
                BluRayTrackPoint point = _geometry.AtSector((clusterBase + channel)
                    / (BluRayDiscParameters.ChannelBitsPerRecordingUnit / 32.0));
                double x = point.Xmm, y = point.Ymm;
                (double tangentX, double tangentY) = _geometry.UnitTangent(x, y, point.RadiusMm);
                luminance = _raster.SampleWithTravelMargin(x, y, _rasterScale,
                    tangentX, tangentY, curvatureBound, margin, tangentMargin, out double travelMm);
                safeUntil = Math.Min(BluRayDiscParameters.ChannelBitsPerRecordingUnit,
                    channel + travelMm / mmPerBit);
            }
            destination[payload] = luminance;
        }
        runLength.MapLuminances(destination);
        return true;
    }

    /// <summary>Samples a procedural pattern in millimetres: positive X is right,
    /// positive Y is up, using the same physical positions as raster images.
    /// Set allowParallelSampling only when the sampler is immutable/thread-safe;
    /// the default preserves sequential callback invocation. Parallel generation
    /// additionally requires the sealed built-in run-length mapping.</summary>
    public BluRayImagePayloadMapping(BluRayDiscParameters disc, Func<double, double, byte> sampleLuminance,
        IBluRayLuminanceMapping mapping, bool allowParallelSampling = false)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(sampleLuminance);
        ArgumentNullException.ThrowIfNull(mapping);
        disc.Validate();
        _disc = disc;
        _sampleLuminance = sampleLuminance;
        _mapping = mapping;
        _geometry = new(disc);
        _supportsParallelGeneration = allowParallelSampling && mapping is BluRayRunLengthMapping;
    }

    private static Func<double, double, byte> CreateRasterSampler(BluRayDiscParameters disc, BluRayGray8Raster raster)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(raster);
        return (x, y) => raster.SampleDiscPoint(x, y, disc.OuterRadiusMm);
    }

    public BluRayPayloadBytePosition GetPosition(uint sector, int byteOffset)
    {
        if (sector >= _disc.TotalSectors) throw new ArgumentOutOfRangeException(nameof(sector));
        if (byteOffset is < 0 or >= BluRayDiscParameters.BytesPerSector)
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        uint physicalSector = _disc.GetPhysicalSectorNumber(sector);
        return GetPositionCore(sector, byteOffset, physicalSector);
    }

    private BluRayPayloadBytePosition GetPositionCore(uint sector, int byteOffset, uint physicalSector)
    {
        // EDC adds four bytes after each 2048-byte payload before column-wise LDC encoding.
        int serial = (int)(sector % 32) * 2052 + byteOffset;
        int column = serial / 216;
        int row = serial % 216;
        int frame = 2 * row + column % 2;
        int ldcColumn = (column / 2 + 152 - (row * 3) % 152) % 152;
        // BIS inserts one byte after each of the first three groups of 38 LDC bytes.
        int eccColumn = ldcColumn + ldcColumn / 38;
        int dataBitMidpoint = eccColumn * 8 + 4;
        int precedingDcBits = dataBitMidpoint < 25 ? 0 : 1 + (dataBitMidpoint - 25) / 45;
        double channelPosition = (long)(sector / 32) * BluRayDiscParameters.ChannelBitsPerRecordingUnit
            + 2760 + frame * 1932 + 30 + (dataBitMidpoint + precedingDcBits) * 1.5;
        double sectorPosition = channelPosition / (BluRayDiscParameters.ChannelBitsPerRecordingUnit / 32.0);
        return new(sector, byteOffset, physicalSector, frame, ldcColumn, eccColumn,
            channelPosition, _geometry.AtSector(sectorPosition));
    }

    public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
    {
        if (destination.Length != BluRayDiscParameters.BytesPerSector)
            throw new ArgumentException("A Blu-ray payload contains exactly 2048 bytes.", nameof(destination));
        if (context.Disc != _disc || context.Sector >= _disc.TotalSectors
            || context.PhysicalSectorNumber != _disc.GetPhysicalSectorNumber(context.Sector))
            throw new ArgumentException("Sector context does not match the image mapping's disc and address.", nameof(context));
        if (_mapping is BluRayRunLengthMapping runLength)
        {
            // Layout repeats every cluster. Its half-bit offsets and the cluster
            // base are exactly representable doubles throughout supported discs.
            // Keep both divisions in the same order as GetPositionCore/AtSector.
            ReadOnlySpan<double> offsets = PayloadLayout.ChannelOffsets.AsSpan(
                (int)(context.Sector % 32) * BluRayDiscParameters.BytesPerSector,
                BluRayDiscParameters.BytesPerSector);
            double clusterBase = (long)(context.Sector / 32) * BluRayDiscParameters.ChannelBitsPerRecordingUnit;
            int offset = 0;
            if (_geometry.SupportsFourPointSampling)
            {
                for (; offset <= destination.Length - 4; offset += 4)
                {
                    Vector256<double> channels = Vector256.Create(clusterBase)
                        + Vector256.Create(offsets[offset], offsets[offset + 1], offsets[offset + 2], offsets[offset + 3]);
                    Vector256<double> sectors = channels
                        / Vector256.Create(BluRayDiscParameters.ChannelBitsPerRecordingUnit / 32.0);
                    (Vector256<double> radii, Vector256<double> angles) = _geometry.AtSectorsFour(sectors);
                    for (int lane = 0; lane < 4; lane++)
                    {
                        double radius = radii.GetElement(lane), angle = angles.GetElement(lane);
                        // Procedural calibration keeps its original callback and
                        // per-lane trig, sharing only the exact geometry arithmetic.
                        double x = radius * Math.Cos(angle), y = radius * Math.Sin(angle);
                        destination[offset + lane] = _raster is not null
                            ? _raster.SampleWithScale(x, y, _rasterScale)
                            : _sampleLuminance(x, y);
                    }
                }
            }
            for (; offset < destination.Length; offset++)
            {
                double sectorPosition = (clusterBase + offsets[offset])
                    / (BluRayDiscParameters.ChannelBitsPerRecordingUnit / 32.0);
                BluRayTrackPoint point = _geometry.AtSector(sectorPosition);
                destination[offset] = _sampleLuminance(point.Xmm, point.Ymm);
            }
            runLength.MapLuminances(destination);
        }
        else
        {
            for (int offset = 0; offset < destination.Length; offset++)
            {
                BluRayPayloadBytePosition position = GetPositionCore(context.Sector, offset, context.PhysicalSectorNumber);
                byte luminance = _sampleLuminance(position.TrackPoint.Xmm, position.TrackPoint.Ymm);
                destination[offset] = _mapping.Map(luminance, position);
            }
        }
    }

    private static class PayloadLayout
    {
        internal static readonly double[] ChannelOffsets = CreateChannelOffsets();
        internal static readonly int[] PhysicalOrder = CreatePhysicalOrder();

        private static int[] CreatePhysicalOrder()
        {
            int[] order = Enumerable.Range(0, BluRayDiscParameters.PayloadBytesPerCluster).ToArray();
            Array.Sort(order, (left, right) => ChannelOffsets[left].CompareTo(ChannelOffsets[right]));
            return order;
        }

        private static double[] CreateChannelOffsets()
        {
            double[] offsets = new double[BluRayDiscParameters.PayloadBytesPerCluster];
            for (int sector = 0; sector < 32; sector++)
            {
                for (int index = 0; index < BluRayDiscParameters.BytesPerSector; index++)
                {
                    int serial = sector * 2052 + index;
                    int column = serial / 216, row = serial % 216;
                    int frame = 2 * row + column % 2;
                    int ldcColumn = (column / 2 + 152 - (row * 3) % 152) % 152;
                    int eccColumn = ldcColumn + ldcColumn / 38;
                    int midpoint = eccColumn * 8 + 4;
                    int dc = midpoint < 25 ? 0 : 1 + (midpoint - 25) / 45;
                    offsets[sector * BluRayDiscParameters.BytesPerSector + index]
                        = 2760 + frame * 1932 + 30 + (midpoint + dc) * 1.5;
                }
            }
            return offsets;
        }
    }
}
