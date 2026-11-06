using DiscImageStudio.ImageIO;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

/// <summary>Reads a burned/generated DVD ISO and colours payload words by grating motif.</summary>
public sealed record GratingReadbackOptions(
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
    bool MonochromePalette = false,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0);

public sealed record GratingReadbackSummary(
    string SourceIso,
    string OutputImage,
    int PreviewSize,
    uint TotalSectors,
    uint StartLba,
    uint FillSectors,
    long TotalSamples,
    long ColouredSamples,
    long OffSamples,
    double InnerRadiusMm,
    double OuterRadiusMm,
    string SpiralDirection);

public static class GratingReadbackRenderer
{
    private const int CodeWordCenterOffset = 8;

    private static readonly (string Name, byte R, byte G, byte B)[] Palette =
    [
        ("A", 128, 80, 220),
        ("B", 60, 190, 90),
        ("G", 230, 190, 50),
        ("C", 220, 60, 60),
        ("D", 60, 170, 140),
        ("E", 60, 130, 230),
        ("F", 230, 100, 50),
    ];

    public static GratingReadbackSummary Render(
        string isoPath, string outputPath, GratingReadbackOptions options)
    {
        Validate(options);
        string isoFullPath = Path.GetFullPath(isoPath);
        long isoSectors = new FileInfo(isoFullPath).Length / DvdEccBlockEncoder.PayloadBytesPerSector;
        if ((ulong)options.StartLba + options.FillSectors > (ulong)isoSectors)
            throw new ArgumentOutOfRangeException(nameof(options),
                "The visible sector range exceeds the ISO image contents.");

        double bitLengthMm = options.ChannelBitLengthNm * 1e-6;
        double trackLengthMm = options.TotalSectors
            * (double)DvdEccBlockEncoder.ChannelBitsPerSector * bitLengthMm;
        ArchimedeanSpiral spiral = ArchimedeanSpiral.Create(
            options.InnerRadiusMm, options.OuterRadiusMm, trackLengthMm,
            options.PitchLinear, options.PitchQuadratic, options.PitchCubic);

        int size = options.PreviewSize;
        byte[] pixels = new byte[checked(size * size * 4)];
        Array.Fill(pixels, (byte)24);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        int[] sumR = new int[checked(size * size)];
        int[] sumG = new int[checked(size * size)];
        int[] sumB = new int[checked(size * size)];
        int[] count = new int[checked(size * size)];

        double centre = (size - 1) / 2.0;
        double direction = options.Clockwise ? -1.0 : 1.0;
        double byteStep = (double)DvdEccBlockEncoder.PayloadBytesPerSector / options.SamplesPerSector;
        long totalSamples = checked((long)options.FillSectors * options.SamplesPerSector);
        long coloured = 0, off = 0;
        int totalBlocks = checked((int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        byte[] block = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        using FileStream iso = new(isoFullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: block.Length, FileOptions.SequentialScan);

        for (int blockIndex = 0; blockIndex < totalBlocks; blockIndex++)
        {
            uint blockLba = options.StartLba
                + checked((uint)blockIndex * DvdEccBlockEncoder.SectorCount);
            iso.Seek(checked((long)blockLba * DvdEccBlockEncoder.PayloadBytesPerSector),
                SeekOrigin.Begin);
            iso.ReadExactly(block);
            for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
            {
                uint psn = blockLba + options.PsnOffset + (uint)sector;
                ReadOnlySpan<byte> mask = DvdDataFrameBuilder.PayloadScramblerMask(psn);
                ReadOnlySpan<byte> payload = block.AsSpan(
                    sector * DvdEccBlockEncoder.PayloadBytesPerSector,
                    DvdEccBlockEncoder.PayloadBytesPerSector);
                for (int sample = 0; sample < options.SamplesPerSector; sample++)
                {
                    int byteIndex = (int)((sample + 0.5) * byteStep);
                    if (byteIndex >= payload.Length) byteIndex = payload.Length - 1;
                    byte q = (byte)(payload[byteIndex] ^ mask[byteIndex]);
                    if (!DvdGratingPalette.TryColour(q, options.MonochromePalette, out var colour))
                    {
                        if (q == 0) off++;
                        continue;
                    }

                    int payloadIndex = (sector * DvdEccBlockEncoder.PayloadBytesPerSector) + byteIndex;
                    long globalBit = ((long)blockLba * DvdEccBlockEncoder.ChannelBitsPerSector)
                        + DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex)
                        + CodeWordCenterOffset;
                    (double radiusMm, double trackAngle) = spiral.AtArcLengthFast(globalBit * bitLengthMm);
                    double polarAngle = direction * trackAngle;
                    double pixelRadius = centre * radiusMm / options.OuterRadiusMm;
                    int x = (int)Math.Round(centre + (pixelRadius * Math.Cos(polarAngle)));
                    int y = (int)Math.Round(centre - (pixelRadius * Math.Sin(polarAngle)));
                    if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
                    int index = (y * size) + x;
                    sumR[index] += colour.R;
                    sumG[index] += colour.G;
                    sumB[index] += colour.B;
                    count[index]++;
                    coloured++;
                }
            }
        }

        for (int i = 0; i < count.Length; i++)
        {
            int n = count[i];
            if (n == 0) continue;
            int offset = i * 4;
            pixels[offset] = (byte)(sumB[i] / n);
            pixels[offset + 1] = (byte)(sumG[i] / n);
            pixels[offset + 2] = (byte)(sumR[i] / n);
            pixels[offset + 3] = 255;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        RasterCodec.SavePng(Path.GetFullPath(outputPath), size, size, pixels, grayscale: false);
        return new GratingReadbackSummary(
            isoFullPath, Path.GetFullPath(outputPath), size,
            options.TotalSectors, options.StartLba, options.FillSectors,
            totalSamples, coloured, off, options.InnerRadiusMm, options.OuterRadiusMm,
            options.Clockwise ? "cw" : "ccw");
    }

    private static void Validate(GratingReadbackOptions options)
    {
        if (options.TotalSectors < DvdEccBlockEncoder.SectorCount)
            throw new ArgumentOutOfRangeException(nameof(options), "Total sector count is too small.");
        if (options.FillSectors == 0
            || options.FillSectors % DvdEccBlockEncoder.SectorCount != 0
            || options.StartLba % DvdEccBlockEncoder.SectorCount != 0
            || (ulong)options.StartLba + options.FillSectors > options.TotalSectors)
            throw new ArgumentOutOfRangeException(nameof(options), "Visible range is not ECC-block aligned.");
        if (options.PreviewSize is < 64 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(options), "Preview size must be 64..8192.");
        if (options.SamplesPerSector is < 1 or > DvdEccBlockEncoder.PayloadBytesPerSector)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid samples per sector.");
    }
}
