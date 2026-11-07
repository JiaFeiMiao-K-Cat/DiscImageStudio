using DiscImageStudio.ImageIO;
using DiscImageStudio.Core;
using System.Text.Json;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

/// <summary>
/// Read-back simulation over a generated ISO: classifies each sampled payload byte
/// through the DVD scrambler into the dispersion pool's black/white code words and
/// splats it at the spiral position derived from the measured disc geometry. This is
/// the calibration counterpart of the image-forward calibration preview: the source
/// is the burn artifact, so the measured radii are the only geometry inputs.
/// </summary>
public sealed record IsoReadbackOptions(
    uint TotalSectors,
    uint StartLba,
    uint FillSectors,
    uint PsnOffset,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double ChannelBitLengthNm,
    bool Clockwise,
    int PreviewSize,
    int SamplesPerSector,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    double? PreviewOuterRadiusMm = null);

public sealed record IsoReadbackSummary(
    string SourceIso,
    string OutputImage,
    int PreviewSize,
    uint TotalSectors,
    uint StartLba,
    uint FillSectors,
    double ChannelBitLengthNm,
    double TrackLengthMetres,
    double TrackPitchMicrometres,
    double TotalTurns,
    double InnerRadiusMm,
    double OuterRadiusMm,
    string SpiralDirection,
    int SamplesPerSector,
    long TotalSamples,
    long ClassifiedSamples,
    string MappingMode,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0)
{
    public double PitchLinearMicrometres => TrackPitchMicrometres * PitchLinear;
    public double PitchQuadraticMicrometres => TrackPitchMicrometres * PitchQuadratic;
    public double PitchCubicMicrometres => TrackPitchMicrometres * PitchCubic;
}

public static class IsoReadbackRenderer
{
    /// <summary>Half of the 16-bit payload code word the generator targets.</summary>
    private const int CodeWordCenterOffset = 8;

    public static IsoReadbackSummary Render(
        string isoPath,
        string outputPath,
        IsoReadbackOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(options);
        string isoFullPath = Path.GetFullPath(isoPath);
        string? readCompatibilityMode = ReadCompatibilityMode(isoFullPath);
        bool readCompatibility = readCompatibilityMode is not null;
        long isoSectors = new FileInfo(isoFullPath).Length / DvdEccBlockEncoder.PayloadBytesPerSector;
        if ((ulong)options.StartLba + options.FillSectors > (ulong)isoSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The visible sector range exceeds the ISO image contents.");
        }

        double bitLengthMm = options.ChannelBitLengthNm * 1e-6;
        double trackLengthMm = options.TotalSectors
            * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * bitLengthMm;
        ArchimedeanSpiral spiral = ArchimedeanSpiral.Create(
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            trackLengthMm,
            options.PitchLinear,
            options.PitchQuadratic,
            options.PitchCubic);

        int size = options.PreviewSize;
        int stride = checked(size * 4);
        byte[] pixels = new byte[checked(stride * size)];
        Array.Fill(pixels, (byte)255);
        int[] levelSums = new int[checked(size * size)];
        int[] sampleCounts = new int[checked(size * size)];
        double centre = (size - 1) / 2.0;
        double direction = options.Clockwise ? -1.0 : 1.0;
        int samplesPerSector = options.SamplesPerSector;
        double byteStep = (double)DvdEccBlockEncoder.PayloadBytesPerSector / samplesPerSector;
        long totalSamples = checked((long)options.FillSectors * samplesPerSector);
        long classifiedSamples = 0;
        int workerCount = Math.Min(
            Environment.ProcessorCount,
            (int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        if (workerCount < 1)
        {
            workerCount = 1;
        }

        int totalBlocks = checked((int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        Parallel.For(0, workerCount, new ParallelOptions { CancellationToken = cancellationToken }, worker =>
        {
            byte[] block = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
            using FileStream iso = new(
                isoFullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: block.Length,
                FileOptions.SequentialScan);
            for (int blockIndex = worker; blockIndex < totalBlocks; blockIndex += workerCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint blockStartLba = options.StartLba
                    + checked((uint)blockIndex * DvdEccBlockEncoder.SectorCount);
                iso.Seek(
                    checked((long)blockStartLba * DvdEccBlockEncoder.PayloadBytesPerSector),
                    SeekOrigin.Begin);
                iso.ReadExactly(block);
                for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
                {
                    uint psn = blockStartLba + options.PsnOffset + (uint)sector;
                    ReadOnlySpan<byte> mask = DvdDataFrameBuilder.PayloadScramblerMask(psn);
                    ReadOnlySpan<byte> payload = block.AsSpan(
                        sector * DvdEccBlockEncoder.PayloadBytesPerSector,
                        DvdEccBlockEncoder.PayloadBytesPerSector);
                    for (int sample = 0; sample < samplesPerSector; sample++)
                    {
                        int byteIndex = (int)((sample + 0.5) * byteStep);
                        if (byteIndex >= payload.Length)
                        {
                            byteIndex = payload.Length - 1;
                        }

                        byte scrambled = (byte)(payload[byteIndex] ^ mask[byteIndex]);
                        // Compatibility guards replace white texture bytes, so show their intended
                        // tone only for a sidecar identifying that algorithm. This is
                        // an artwork preview, not an optical reflectance simulation.
                        byte level;
                        if (readCompatibility && scrambled == 0x9B)
                            level = 255;
                        else if (!DvdGrayscalePalette.TryGetLuminance(scrambled, out level))
                        {
                            continue;
                        }

                        int payloadIndex = (sector * DvdEccBlockEncoder.PayloadBytesPerSector)
                            + byteIndex;
                        // The generator targets the image at the code word centre
                        // (FastDispersionImageWriter.PayloadChannelCenters), so the read-back
                        // samples the same bit; without the half word the position would differ
                        // by eight channel bits (about 1 um of track).
                        long globalBit = ((long)blockStartLba
                            * DvdEccBlockEncoder.ChannelBitsPerSector)
                            + DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex)
                            + CodeWordCenterOffset;
                        double arcLengthMm = globalBit * bitLengthMm;
                        (double radiusMm, double trackAngle) = spiral.AtArcLengthFast(arcLengthMm);
                        double polarAngle = direction * trackAngle;
                        double pixelRadius = centre * radiusMm / (options.PreviewOuterRadiusMm ?? options.OuterRadiusMm);
                        int pixelX = (int)Math.Round(centre + (pixelRadius * Math.Cos(polarAngle)));
                        int pixelY = (int)Math.Round(centre - (pixelRadius * Math.Sin(polarAngle)));
                        if ((uint)pixelX >= (uint)size || (uint)pixelY >= (uint)size)
                        {
                            continue;
                        }

                        int pixelIndex = (pixelY * size) + pixelX;
                        Interlocked.Add(ref levelSums[pixelIndex], level);
                        Interlocked.Add(ref sampleCounts[pixelIndex], 1);
                        Interlocked.Increment(ref classifiedSamples);
                    }
                }
            }
        });

        for (int pixelIndex = 0; pixelIndex < sampleCounts.Length; pixelIndex++)
        {
            if ((pixelIndex & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            int count = sampleCounts[pixelIndex];
            if (count == 0)
            {
                continue;
            }

            byte level = (byte)((levelSums[pixelIndex] + (count / 2)) / count);
            int offset = checked(pixelIndex * 4);
            pixels[offset] = level;
            pixels[offset + 1] = level;
            pixels[offset + 2] = level;
        }

        cancellationToken.ThrowIfCancellationRequested();
        WritePng(Path.GetFullPath(outputPath), size, pixels);
        return new IsoReadbackSummary(
            isoFullPath,
            Path.GetFullPath(outputPath),
            size,
            options.TotalSectors,
            options.StartLba,
            options.FillSectors,
            options.ChannelBitLengthNm,
            trackLengthMm / 1000.0,
            spiral.TrackPitchMm * 1000.0,
            spiral.TotalAngleRadians / (2 * Math.PI),
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            options.Clockwise ? "cw" : "ccw",
            samplesPerSector,
            totalSamples,
            classifiedSamples,
            readCompatibility ? $"payload-scramble-classify-splat-{readCompatibilityMode}" : "payload-scramble-classify-splat",
            options.PitchLinear,
            options.PitchQuadratic,
            options.PitchCubic);
    }

    /// <summary>Restores the drawing interval used by CLI simulation. A missing or malformed
    /// JSON sidecar falls back to the visible ISO length; I/O errors remain observable.</summary>
    public static (uint StartLba, uint FillSectors) ReadDrawingRange(string isoPath, uint totalSectors)
    {
        var summary = ReadSolveSummary(isoPath + ".json");
        long isoSectors = new FileInfo(isoPath).Length / DvdEccBlockEncoder.PayloadBytesPerSector;
        uint fillSectors = summary.FillSectors != 0 ? summary.FillSectors
            : checked((uint)Math.Min(isoSectors, totalSectors) - summary.StartLba);
        return (summary.StartLba, fillSectors);
    }

    internal static (uint StartLba, uint FillSectors, uint TotalSectors) ReadSolveSummary(string sidecarPath)
    {
        if (!File.Exists(sidecarPath)) return (0, 0, 0);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            JsonElement root = document.RootElement;
            return (GetUInt(root, "startLba"), GetUInt(root, "fillSectors"), GetUInt(root, "totalSectors"));
        }
        catch (JsonException) { return (0, 0, 0); }
    }

    /// <summary>Only a known, explicitly recorded protection version changes preview classification.</summary>
    public static bool ReadCompatibilityEnabled(string isoPath)
        => ReadCompatibilityMode(isoPath) is not null;

    /// <summary>Returns the recorded protection version, or null for absent or unknown modes.</summary>
    public static string? ReadCompatibilityMode(string isoPath)
    {
        string sidecarPath = isoPath + ".json";
        if (!File.Exists(sidecarPath)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("readCompatibility", out JsonElement mode)
                && mode.ValueKind == JsonValueKind.String
                && DvdReadCompatibilityModes.IsSupported(mode.GetString())
                ? mode.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static uint GetUInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint result) ? result : 0u;

    private static void WritePng(string outputPath, int size, byte[] pixels)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        RasterCodec.SavePng(outputPath, size, size, pixels, grayscale: false);
    }

    private static void Validate(IsoReadbackOptions options)
    {
        if (options.TotalSectors < DvdEccBlockEncoder.SectorCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Total sector count must cover at least one ECC Block.");
        }

        if (options.FillSectors == 0
            || options.FillSectors % DvdEccBlockEncoder.SectorCount != 0
            || options.StartLba % DvdEccBlockEncoder.SectorCount != 0
            || (ulong)options.StartLba + options.FillSectors > options.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The visible range must be ECC-Block aligned and fit inside the disc.");
        }

        if (!double.IsFinite(options.ChannelBitLengthNm) || options.ChannelBitLengthNm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Channel-bit length must be positive.");
        }

        if (options.PreviewSize is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Preview size must be 64..8192 pixels.");
        }

        if (options.SamplesPerSector is < 1 or > DvdEccBlockEncoder.PayloadBytesPerSector)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Preview samples per sector must be 1..{DvdEccBlockEncoder.PayloadBytesPerSector}.");
        }
    }
}
