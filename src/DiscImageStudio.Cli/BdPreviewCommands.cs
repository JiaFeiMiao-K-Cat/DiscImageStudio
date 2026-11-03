using System.Globalization;
using System.Numerics;
using DiscImageStudio.BluRay;
using DiscImageStudio.BluRay.Encoding;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Services;

namespace DiscImageStudio.Cli;

/// <summary>
/// Portable Blue-ray preview commands for GUI clients. The projections mirror the WPF
/// services <c>BluRayPreviewRenderer.RenderCalibration</c> / <c>RenderReadback</c> without
/// referencing WPF: source decoding and PNG encoding go through
/// <see cref="DiscImageStudio.ImageIO.RasterCodec"/>, and the tone profile is applied at
/// source resolution by the shared image loader.
/// </summary>
internal static class BdPreviewCommand
{
    private const int DefaultSize = 2048;

    internal static int Run(IReadOnlyList<string> arguments)
    {
        CliOptionSet options = CliOptionSet.Parse(arguments, 1,
        [
            "input", "output", "total-sectors", "r0", "r1", "actual-r0", "actual-r1",
            "invert", "tone-dither", "tone-levels", "tone-brightness", "tone-gamma", "size",
        ]);
        string input = options.Require("input");
        string output = Path.GetFullPath(options.Require("output"));
        BluRayDiscParameters defaults = new();
        uint totalSectors = options.GetUInt("total-sectors", defaults.TotalSectors);
        double generatedInner = options.GetDouble("r0", defaults.InnerRadiusMm);
        double generatedOuter = options.GetDouble("r1", defaults.OuterRadiusMm);
        double actualInner = options.GetDouble("actual-r0", generatedInner);
        double actualOuter = options.GetDouble("actual-r1", generatedOuter);
        bool invert = options.GetBool("invert", false);
        int size = options.GetInt("size", DefaultSize);
        ToneProfile tone = options.ReadTone();
        BluRayDiscParameters generated = new(totalSectors, 1, generatedInner, generatedOuter);
        BluRayDiscParameters actual = new(totalSectors, 1, actualInner, actualOuter);
        generated.Validate();
        actual.Validate();
        BdPreviewRenderer.ValidateSize(size, "size");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        BdPreviewRenderer.RenderProjection(input, output, generated, actual, size, invert,
            tone, CancellationToken.None);
        Console.WriteLine(
            $"BD design projection: {size} px, {totalSectors} sectors, "
            + $"generated r0/r1={Format(generatedInner)}/{Format(generatedOuter)} mm, "
            + $"actual r0/r1={Format(actualInner)}/{Format(actualOuter)} mm, "
            + $"invert={(invert ? "true" : "false")}, output={output}");
        return 0;
    }

    private static string Format(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Renders a generated BD-R ISO back through its sidecar geometry and payload bytes.</summary>
internal static class BdPreviewReadbackCommand
{
    private const int DefaultSize = 2048;

    internal static int Run(IReadOnlyList<string> arguments)
    {
        CliOptionSet options = CliOptionSet.Parse(arguments, 1, ["iso", "output", "size"]);
        string isoPath = Path.GetFullPath(options.Require("iso"));
        string output = Path.GetFullPath(options.Require("output"));
        int size = options.GetInt("size", DefaultSize);
        BdPreviewRenderer.ValidateSize(size, "size");
        BluRayIsoMetadata? metadata = BluRayIsoMetadata.TryLoad(isoPath);
        if (metadata is null)
        {
            throw new ArgumentException(
                $"bd-preview-readback needs a valid BD metadata sidecar next to the ISO: "
                + $"'{BluRayIsoMetadata.PathFor(isoPath)}' is missing or unreadable.");
        }

        BluRayDiscParameters actual = new(
            metadata.TotalSectors, 1, metadata.InnerRadiusMm, metadata.OuterRadiusMm);
        actual.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        BdPreviewRenderer.RenderReadback(isoPath, output, actual, size,
            metadata.DrawingStartSector, metadata.DrawingEndSectorExclusive,
            markFileRegion: metadata.UdfTimestamp is not null, CancellationToken.None);
        Console.WriteLine(
            $"BD read-back preview: {size} px, {metadata.TotalSectors} sectors, "
            + $"drawing {metadata.DrawingStartSector}..{metadata.DrawingEndSectorExclusive - 1}, "
            + $"sidecar={BluRayIsoMetadata.PathFor(isoPath)}, output={output}");
        return 0;
    }
}

/// <summary>Strict <c>--name value</c> / <c>--name=value</c> option reader for auxiliary commands.</summary>
internal sealed class CliOptionSet
{
    private readonly Dictionary<string, string> _values;

    private CliOptionSet(Dictionary<string, string> values) => _values = values;

    internal static CliOptionSet Parse(
        IReadOnlyList<string> arguments, int startIndex, IReadOnlyCollection<string> allowed)
    {
        HashSet<string> names = new(allowed, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = startIndex; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{argument}'.");
            }

            int separator = argument.IndexOf('=');
            string name = separator < 0 ? argument[2..] : argument[2..separator];
            if (!names.Contains(name))
            {
                throw new ArgumentException($"Unknown option --{name}.");
            }

            string value;
            if (separator >= 0)
            {
                value = argument[(separator + 1)..];
            }
            else
            {
                if (++index >= arguments.Count)
                {
                    throw new ArgumentException($"Missing value for --{name}.");
                }

                value = arguments[index];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Empty value for --{name}.");
            }

            if (!values.TryAdd(name, value))
            {
                throw new ArgumentException($"Duplicate option --{name}.");
            }
        }

        return new(values);
    }

    internal string Require(string name)
        => _values.TryGetValue(name, out string? value)
            ? value
            : throw new ArgumentException($"Missing required option --{name}.");

    internal int GetInt(string name, int fallback)
        => _values.TryGetValue(name, out string? value)
            ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : throw new ArgumentException($"--{name} must be an integer.")
            : fallback;

    internal uint GetUInt(string name, uint fallback)
    {
        if (!_values.TryGetValue(name, out string? value))
        {
            return fallback;
        }

        bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return uint.TryParse(hex ? value[2..] : value,
            hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : throw new ArgumentException($"--{name} must be an unsigned integer (decimal or 0x hex).");
    }

    internal double GetDouble(string name, double fallback)
        => _values.TryGetValue(name, out string? value)
            ? double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
              && double.IsFinite(parsed)
                ? parsed
                : throw new ArgumentException($"--{name} must be a finite number.")
            : fallback;

    internal bool GetBool(string name, bool fallback)
        => _values.TryGetValue(name, out string? value)
            ? bool.TryParse(value, out bool parsed)
                ? parsed
                : throw new ArgumentException($"--{name} must be true or false.")
            : fallback;

    internal ToneProfile ReadTone()
    {
        ToneProfile tone = new(
            GetDouble("tone-dither", 0),
            GetInt("tone-levels", ToneProfile.TwoLevels),
            GetDouble("tone-brightness", 0),
            GetBool("tone-gamma", false));
        tone.Validate("tone");
        if (tone.Levels != ToneProfile.TwoLevels)
        {
            throw new ArgumentException("--tone-levels must be 2: BD-R is a binary medium.");
        }

        return tone;
    }
}

/// <summary>
/// Portable projection renderer. Black is the 3T/AA class, white the 6T/44 class and grey the
/// unknown/reserved space; this is a byte-class map, not an optical-reflectance prediction.
/// </summary>
internal static class BdPreviewRenderer
{
    private const byte UnknownLevel = 128;
    // Keep the offset/pixel sort buffer at 4 MiB even at 8192 px.
    private const int MaxReadSamplesPerBatch = 262_144;
    private static readonly Lazy<PayloadCell[]> PayloadCells = new(CreatePayloadCells);
    private static readonly Lazy<byte[][]> ScramblerBasis = new(CreateScramblerBasis);

    /// <summary>Full-area design projection: sample the tone-quantized source through the
    /// generated geometry, then project the resulting byte classes onto the measured one.</summary>
    internal static void RenderProjection(string sourcePath, string outputPath,
        BluRayDiscParameters generated, BluRayDiscParameters actual, int outputSize,
        bool invert, ToneProfile? tone, CancellationToken cancellationToken)
        => RenderCalibration(sourcePath, outputPath, generated, actual, 0, generated.TotalSectors,
            outputSize, invert, cancellationToken, tone);

    internal static void RenderCalibration(string sourcePath, string outputPath,
        BluRayDiscParameters generated, BluRayDiscParameters actual,
        uint drawingStartSector, uint drawingEndSectorExclusive, int outputSize,
        bool invert, CancellationToken cancellationToken, ToneProfile? tone = null)
    {
        ArgumentNullException.ThrowIfNull(generated);
        generated.Validate();
        ValidateDisc(actual);
        ValidateSize(outputSize, "size");
        if (drawingStartSector >= drawingEndSectorExclusive
            || drawingEndSectorExclusive > generated.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(drawingEndSectorExclusive), "The drawing range must lie inside the generated disc.");
        }

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
                if (!projection.TryGetSector(x, y, out double sector))
                {
                    continue;
                }

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

    /// <summary>Samples the generated host bytes after reapplying the BD scrambler and places
    /// them at the nominal post-LDC/BIS byte positions. Reads only selected sectors in bounded
    /// batches sorted by file offset instead of scanning the whole ISO.</summary>
    internal static void RenderReadback(string isoPath, string outputPath,
        BluRayDiscParameters actual, int outputSize, uint drawingStartSector,
        uint drawingEndSectorExclusive, bool markFileRegion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ValidateDisc(actual);
        ValidateSize(outputSize, "size");
        if (drawingStartSector >= drawingEndSectorExclusive
            || drawingEndSectorExclusive > actual.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(drawingEndSectorExclusive), "The drawing range must lie inside the disc.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using FileStream source = new(Path.GetFullPath(isoPath), FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1, FileOptions.RandomAccess);
        if (source.Length != actual.ContentLength)
        {
            throw new ArgumentException(
                $"BD ISO length does not match the sidecar: expected {actual.ContentLength:N0} bytes, "
                + $"found {source.Length:N0} bytes.", nameof(isoPath));
        }

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
                if (!projection.TryGetSector(x, y, out double sector))
                {
                    continue;
                }

                if (sector < drawingStartSector || sector >= drawingEndSectorExclusive)
                {
                    SetPixel(pixels, y * outputSize + x, UnknownLevel);
                    continue;
                }

                double clusterPosition = sector / BluRayDiscParameters.SectorsPerCluster;
                uint cluster = (uint)clusterPosition;
                double channel = (clusterPosition - cluster) * BluRayDiscParameters.ChannelBitsPerRecordingUnit;
                // EDC, parity, BIS, sync and run-in/out do not have a host payload byte.
                PayloadCell cell = FindNearestCell(cells, channel);
                long offset = (long)cluster * BluRayDiscParameters.PayloadBytesPerCluster + cell.PayloadOffset;
                samples.Add(new(offset, y * outputSize + x));
                if (samples.Count == MaxReadSamplesPerBatch)
                {
                    ReadBatch();
                }
            }
        }

        ReadBatch();
        if (markFileRegion)
        {
            ApplyFileRegionOverlay(pixels, outputSize, actual,
                drawingStartSector, drawingEndSectorExclusive, cancellationToken);
        }

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

    internal static void ValidateSize(int size, string name)
    {
        if (size is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(name, "BD preview size must be 64..8192 pixels.");
        }
    }

    private static void ValidateDisc(BluRayDiscParameters disc)
    {
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
    }

    /// <summary>Marks the non-drawing (filesystem) ring with the preview legend's amber
    /// (#FACC15), matching the Windows preview's presentation-only overlay.</summary>
    private static void ApplyFileRegionOverlay(byte[] pixels, int size, BluRayDiscParameters actual,
        uint drawingStartSector, uint drawingEndSectorExclusive, CancellationToken token)
    {
        BluRayTrackGeometry geometry = new(actual);
        double drawingInner = geometry.AtSector(drawingStartSector).RadiusMm;
        double drawingOuter = geometry.AtSector(drawingEndSectorExclusive).RadiusMm;
        double centre = (size - 1) / 2.0;
        double mmPerPixel = actual.OuterRadiusMm * 2 / size;
        double innerSquared = actual.InnerRadiusMm * actual.InnerRadiusMm;
        double outerSquared = actual.OuterRadiusMm * actual.OuterRadiusMm;
        double drawingInnerSquared = drawingInner * drawingInner;
        double drawingOuterSquared = drawingOuter * drawingOuter;
        for (int y = 0; y < size; y++)
        {
            token.ThrowIfCancellationRequested();
            double dy = (y - centre) * mmPerPixel;
            for (int x = 0; x < size; x++)
            {
                double dx = (x - centre) * mmPerPixel;
                double radiusSquared = dx * dx + dy * dy;
                if (radiusSquared < innerSquared || radiusSquared >= outerSquared
                    || (radiusSquared >= drawingInnerSquared && radiusSquared < drawingOuterSquared))
                {
                    continue;
                }

                int offset = y * size * 4 + x * 4;
                pixels[offset] = 21;
                pixels[offset + 1] = 204;
                pixels[offset + 2] = 250;
                pixels[offset + 3] = 255;
            }
        }
    }

    private static void SetPixel(byte[] pixels, int pixel, byte level)
    {
        int offset = pixel * 4;
        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = level;
        pixels[offset + 3] = 255;
    }

    private static void WritePng(string path, int size, byte[] pixels, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        RasterCodec.SavePng(fullPath, size, size, pixels, grayscale: false);
        token.ThrowIfCancellationRequested();
    }

    private static PayloadCell[] CreatePayloadCells()
    {
        BluRayDiscParameters disc = new();
        BluRayImagePayloadMapping mapping = new(
            disc, static (_, _) => 0, new BluRayRunLengthMapping());
        PayloadCell[] cells = new PayloadCell[BluRayDiscParameters.PayloadBytesPerCluster];
        for (uint sector = 0; sector < BluRayDiscParameters.SectorsPerCluster; sector++)
        {
            for (int offset = 0; offset < BluRayDiscParameters.BytesPerSector; offset++)
            {
                int index = (int)sector * BluRayDiscParameters.BytesPerSector + offset;
                cells[index] = new(mapping.GetPosition(sector, offset).NominalChannelBitPosition, index);
            }
        }

        Array.Sort(cells, static (a, b) => a.ChannelPosition.CompareTo(b.ChannelPosition));
        return cells;
    }

    private static PayloadCell FindNearestCell(PayloadCell[] cells, double channel)
    {
        int low = 0, high = cells.Length - 1;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (cells[mid].ChannelPosition < channel)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        if (low > 0 && channel - cells[low - 1].ChannelPosition < cells[low].ChannelPosition - channel)
        {
            low--;
        }

        return cells[low];
    }

    // The additive LFSR is linear. Sixteen 2048-byte basis sequences avoid building an entire
    // scrambler mask for every sparsely sampled recording unit.
    private static byte[][] CreateScramblerBasis()
    {
        byte[][] basis = new byte[16][];
        basis[15] = BluRayScrambler.CreateMask(0, BluRayDiscParameters.BytesPerSector);
        for (int bit = 0; bit < 15; bit++)
        {
            basis[bit] = BluRayScrambler.CreateMask(1u << (bit + 5), BluRayDiscParameters.BytesPerSector);
            for (int index = 0; index < basis[bit].Length; index++)
            {
                basis[bit][index] ^= basis[15][index];
            }
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

    /// <summary>Maps an output pixel onto the nearest turn of the actual clockwise spiral,
    /// preserving absolute sector position between the generated and measured discs.</summary>
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
            double x = (pixelX + 0.5 - (_size / 2.0)) * _mmPerPixel;
            double y = ((_size / 2.0) - pixelY - 0.5) * _mmPerPixel;
            double radius = Math.Sqrt(x * x + y * y);
            sector = 0;
            if (radius < _disc.InnerRadiusMm || radius >= _disc.OuterRadiusMm)
            {
                return false;
            }

            double spiralAngle = _angleOffset - ((radius - _disc.InnerRadiusMm) / _growth);
            double difference = Math.IEEERemainder(Math.Atan2(y, x) - spiralAngle, 2 * Math.PI);
            double nearestRadius = radius - (difference * _growth);
            if (nearestRadius < _disc.InnerRadiusMm || nearestRadius >= _disc.OuterRadiusMm)
            {
                return false;
            }

            sector = Math.Clamp((Primitive(nearestRadius) - _innerPrimitive) * _sectorsPerMm,
                0, Math.BitDecrement((double)_disc.TotalSectors));
            return true;
        }

        private double Primitive(double radius)
            => ((radius * Math.Sqrt((radius * radius) + (_growth * _growth)))
                + (_growth * _growth * Math.Asinh(radius / _growth))) / (2 * _growth);
    }
}
