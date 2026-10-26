using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.BluRay.Encoding;
using DiscImageStudio.Core;

namespace DiscImageStudio.Services;

/// <summary>Fast nominal BD payload projection. Black denotes AA (3T bias), white
/// denotes 44 (6T bias), and gray denotes other payload or reserved space. This is
/// neither an optical reflectance prediction nor an ECC/17PP channel simulation.</summary>
public static class BluRayPreviewRenderer
{
    private const byte UnknownLevel = 128;
    // Keep the offset/pixel sort buffer at 4 MiB even at 8192 px. The output
    // bitmap retains the requested resolution; only the read scheduling is batched.
    private const int MaxReadSamplesPerBatch = 262_144;
    private static readonly Lazy<PayloadCell[]> PayloadCells = new(CreatePayloadCells);
    private static readonly Lazy<byte[][]> ScramblerBasis = new(CreateScramblerBasis);

    /// <summary>Projects the source picture through one disc geometry and the current tone
    /// and polarity: the design-side preview. The tone profile is already applied to the
    /// raster; the only remaining step is the 128 threshold and 3T/6T swap.</summary>
    public static void RenderProjection(string sourcePath, string outputPath,
        BluRayDiscParameters disc, int outputSize, bool invert, ToneProfile? tone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
        Validate(disc, outputSize);
        cancellationToken.ThrowIfCancellationRequested();
        BluRayGray8Raster source = BluRayImageLoader.Load(sourcePath, tone, cancellationToken);
        BluRayTrackGeometry geometry = new(disc);
        PixelProjection projection = new(disc, outputSize);
        byte[] pixels = new byte[checked(outputSize * outputSize * 4)];
        for (int y = 0; y < outputSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < outputSize; x++)
            {
                if (!projection.TryGetSector(x, y, out double sector)) continue;
                BluRayTrackPoint point = geometry.AtSector(sector);
                bool dark = source.SampleDiscPoint(point.Xmm, point.Ymm, disc.OuterRadiusMm) < 128;
                SetPixel(pixels, (y * outputSize) + x, dark != invert ? (byte)0 : (byte)255);
            }
        }

        WritePng(outputPath, outputSize, pixels, cancellationToken);
    }

    public static void RenderCalibration(string sourcePath, string outputPath,
        BluRayDiscParameters generated, BluRayDiscParameters actual,
        uint drawingStartSector, uint drawingEndSectorExclusive, int outputSize,
        bool invert, CancellationToken cancellationToken = default, ToneProfile? tone = null)
    {
        ArgumentNullException.ThrowIfNull(generated);
        generated.Validate();
        Validate(actual, outputSize);
        if (drawingStartSector >= drawingEndSectorExclusive || drawingEndSectorExclusive > generated.TotalSectors)
            throw new ArgumentOutOfRangeException(nameof(drawingEndSectorExclusive), "The drawing range must lie inside the generated disc.");
        cancellationToken.ThrowIfCancellationRequested();
        BluRayGray8Raster source = BluRayImageLoader.Load(sourcePath, tone, cancellationToken);
        BluRayTrackGeometry sourceGeometry = new(generated);
        PixelProjection projection = new(actual, outputSize);
        byte[] pixels = new byte[checked(outputSize * outputSize * 4)];
        for (int y = 0; y < outputSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < outputSize; x++)
            {
                if (!projection.TryGetSector(x, y, out double sector)) continue;
                byte level = UnknownLevel;
                if (sector >= drawingStartSector && sector < drawingEndSectorExclusive)
                {
                    BluRayTrackPoint point = sourceGeometry.AtSector(sector);
                    bool dark = source.SampleDiscPoint(point.Xmm, point.Ymm, generated.OuterRadiusMm) < 128;
                    level = dark != invert ? (byte)0 : (byte)255;
                }
                SetPixel(pixels, y * outputSize + x, level);
            }
        }
        WritePng(outputPath, outputSize, pixels, cancellationToken);
    }

    /// <summary>Samples generated host bytes after reapplying the BD scrambler and
    /// places them at the nominal post-LDC/BIS byte positions. Reads only selected
    /// sectors in bounded batches sorted by file offset, instead of scanning the
    /// entire ISO or allocating one read request for every output pixel at once.</summary>
    public static void RenderReadback(string isoPath, string outputPath,
        BluRayDiscParameters actual, int outputSize, CancellationToken cancellationToken = default,
        uint drawingStartSector = 0, uint? drawingEndSectorExclusive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        Validate(actual, outputSize);
        uint drawingEnd = drawingEndSectorExclusive ?? actual.TotalSectors;
        if (drawingStartSector >= drawingEnd || drawingEnd > actual.TotalSectors)
            throw new ArgumentOutOfRangeException(nameof(drawingEndSectorExclusive), "The drawing range must lie inside the disc.");
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream source = new(Path.GetFullPath(isoPath), FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1, FileOptions.RandomAccess);
        if (source.Length != actual.ContentLength)
            throw new ArgumentException($"BD ISO 长度与总扇区数不符：需要 {actual.ContentLength:N0} 字节，实际 {source.Length:N0} 字节。", nameof(isoPath));

        PixelProjection projection = new(actual, outputSize);
        byte[] pixels = new byte[checked(outputSize * outputSize * 4)];
        List<ReadSample> samples = new(Math.Min(outputSize * outputSize, MaxReadSamplesPerBatch));
        PayloadCell[] cells = PayloadCells.Value;
        byte[] sectorBuffer = new byte[BluRayDiscParameters.BytesPerSector];
        long loadedSector = -1;
        byte[][] basis = ScramblerBasis.Value;
        for (int y = 0; y < outputSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < outputSize; x++)
            {
                if (!projection.TryGetSector(x, y, out double sector)) continue;
                if (sector < drawingStartSector || sector >= drawingEnd)
                {
                    SetPixel(pixels, y * outputSize + x, UnknownLevel);
                    continue;
                }
                double clusterPosition = sector / BluRayDiscParameters.SectorsPerCluster;
                uint cluster = (uint)clusterPosition;
                double channel = (clusterPosition - cluster) * BluRayDiscParameters.ChannelBitsPerRecordingUnit;
                // EDC, parity, BIS, sync and run-in/out do not have a host payload byte.
                // Select the nearest payload midpoint, just as the projection uses the
                // nearest spiral turn; the preview does not invent their optical values.
                PayloadCell cell = FindNearestCell(cells, channel);
                long offset = (long)cluster * BluRayDiscParameters.PayloadBytesPerCluster + cell.PayloadOffset;
                samples.Add(new(offset, y * outputSize + x));
                if (samples.Count == MaxReadSamplesPerBatch) ReadBatch();
            }
        }
        ReadBatch();
        WritePng(outputPath, outputSize, pixels, cancellationToken);

        void ReadBatch()
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
            foreach (ReadSample sample in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long sector = sample.Offset / BluRayDiscParameters.BytesPerSector;
                if (sector != loadedSector)
                {
                    source.Position = sector * BluRayDiscParameters.BytesPerSector;
                    source.ReadExactly(sectorBuffer);
                    loadedSector = sector;
                }
                int byteOffset = (int)(sample.Offset % BluRayDiscParameters.BytesPerSector);
                uint psn = BluRayDiscParameters.FirstLayer0PhysicalSector + (uint)sector;
                byte value = (byte)(sectorBuffer[byteOffset] ^ ScramblerByte(basis, psn, byteOffset));
                byte level = value switch
                {
                    BluRayRunLengthMapping.ThreeTByte => 0,
                    BluRayRunLengthMapping.SixTByte => 255,
                    _ => UnknownLevel,
                };
                SetPixel(pixels, sample.Pixel, level);
            }
            samples.Clear();
        }
    }

    private static void Validate(BluRayDiscParameters disc, int size)
    {
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
        if (size is < 64 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(size), "BD preview size must be 64..8192 pixels.");
    }

    private static void SetPixel(byte[] pixels, int pixel, byte level)
    {
        int offset = pixel * 4;
        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = level;
        pixels[offset + 3] = 255;
    }

    private static void WritePng(string path, int size, byte[] pixels, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        token.ThrowIfCancellationRequested();
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using FileStream output = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        token.ThrowIfCancellationRequested();
    }

    private static PayloadCell[] CreatePayloadCells()
    {
        BluRayDiscParameters disc = new();
        BluRayImagePayloadMapping mapping = new(disc, static (_, _) => 0, new BluRayRunLengthMapping());
        PayloadCell[] cells = new PayloadCell[BluRayDiscParameters.PayloadBytesPerCluster];
        for (uint sector = 0; sector < BluRayDiscParameters.SectorsPerCluster; sector++)
            for (int offset = 0; offset < BluRayDiscParameters.BytesPerSector; offset++)
            {
                int index = (int)sector * BluRayDiscParameters.BytesPerSector + offset;
                cells[index] = new(mapping.GetPosition(sector, offset).NominalChannelBitPosition, index);
            }
        Array.Sort(cells, static (a, b) => a.ChannelPosition.CompareTo(b.ChannelPosition));
        return cells;
    }

    private static PayloadCell FindNearestCell(PayloadCell[] cells, double channel)
    {
        int low = 0, high = cells.Length - 1;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (cells[mid].ChannelPosition < channel) low = mid + 1;
            else high = mid;
        }
        if (low > 0 && channel - cells[low - 1].ChannelPosition < cells[low].ChannelPosition - channel)
            low--;
        return cells[low];
    }

    // The additive LFSR is linear. Sixteen 2048-byte basis sequences avoid building
    // an entire scrambler mask for every sparsely sampled recording unit.
    private static byte[][] CreateScramblerBasis()
    {
        byte[][] basis = new byte[16][];
        basis[15] = BluRayScrambler.CreateMask(0, BluRayDiscParameters.BytesPerSector);
        for (int bit = 0; bit < 15; bit++)
        {
            basis[bit] = BluRayScrambler.CreateMask(1u << (bit + 5), BluRayDiscParameters.BytesPerSector);
            for (int index = 0; index < basis[bit].Length; index++) basis[bit][index] ^= basis[15][index];
        }
        return basis;
    }

    private static byte ScramblerByte(byte[][] basis, uint physicalSector, int offset)
    {
        byte value = basis[15][offset];
        uint seed = (physicalSector >> 5) & 0x7fff;
        while (seed != 0)
        {
            int bit = BitOperations.TrailingZeroCount(seed);
            value ^= basis[bit][offset];
            seed &= seed - 1;
        }
        return value;
    }

    private readonly record struct PayloadCell(double ChannelPosition, int PayloadOffset);
    private readonly record struct ReadSample(long Offset, int Pixel);

    /// <summary>Maps an output pixel onto the nearest turn of the actual clockwise
    /// spiral, preserving absolute sector position between generated/measured discs.</summary>
    private sealed class PixelProjection
    {
        private readonly BluRayDiscParameters _disc;
        private readonly int _size;
        private readonly double _mmPerPixel;
        private readonly double _growth;
        private readonly double _innerPrimitive;
        private readonly double _sectorsPerMm;
        private readonly double _angleOffset;

        internal PixelProjection(BluRayDiscParameters disc, int size)
        {
            _disc = disc;
            _size = size;
            _mmPerPixel = disc.OuterRadiusMm * 2 / size;
            BluRayTrackGeometry geometry = new(disc);
            _growth = geometry.EffectiveTrackPitchMicrometers / (2 * Math.PI * 1000);
            _innerPrimitive = Primitive(disc.InnerRadiusMm);
            _sectorsPerMm = disc.TotalSectors / geometry.TrackLengthPerLayerMm;
            _angleOffset = disc.StartAngleDegrees % 360 * Math.PI / 180;
        }

        internal bool TryGetSector(int pixelX, int pixelY, out double sector)
        {
            double x = (pixelX + 0.5 - _size / 2.0) * _mmPerPixel;
            double y = (_size / 2.0 - pixelY - 0.5) * _mmPerPixel;
            double radius = Math.Sqrt(x * x + y * y);
            sector = 0;
            if (radius < _disc.InnerRadiusMm || radius >= _disc.OuterRadiusMm) return false;
            double spiralAngle = _angleOffset - (radius - _disc.InnerRadiusMm) / _growth;
            double difference = Math.IEEERemainder(Math.Atan2(y, x) - spiralAngle, 2 * Math.PI);
            double nearestRadius = radius - difference * _growth;
            if (nearestRadius < _disc.InnerRadiusMm || nearestRadius >= _disc.OuterRadiusMm) return false;
            sector = Math.Clamp((Primitive(nearestRadius) - _innerPrimitive) * _sectorsPerMm,
                0, Math.BitDecrement((double)_disc.TotalSectors));
            return true;
        }

        private double Primitive(double radius)
            => (radius * Math.Sqrt(radius * radius + _growth * _growth)
                + _growth * _growth * Math.Asinh(radius / _growth)) / (2 * _growth);
    }
}
