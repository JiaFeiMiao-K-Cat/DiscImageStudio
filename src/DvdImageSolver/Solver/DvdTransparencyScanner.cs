using DiscImageStudio.Core;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

/// <summary>A run of sectors that the image never touches, as an LBA interval.</summary>
public readonly record struct DvdFreeRegion(uint StartLba, uint EndLbaExclusive)
{
    public uint Sectors => EndLbaExclusive - StartLba;
}

/// <summary>
/// Finds sectors whose entire spiral footprint lies on transparent (or optionally white)
/// pixels. Those sectors carry no picture content, so file data can replace the generated
/// bytes. One sector is only about 5 mm of track, so a picture that covers part of the disc
/// still leaves many usable sectors beside it. The test is conservative: every bilinear
/// neighbour of every sample must be blank.
/// </summary>
public static class DvdTransparencyScanner
{
    /// <summary>Luminance at or above which an opaque pixel counts as blank paper.</summary>
    public const int DefaultWhiteLuminance = 250;

    /// <summary>Passing this as the white threshold makes only transparent pixels blank.</summary>
    public const int TransparentOnly = 256;

    /// <summary>
    /// Runs shorter than this are ignored: every extra ISO extent costs directory space, and a
    /// handful of sectors is not worth one.
    /// </summary>
    public const uint MinimumRunSectors = 4;

    /// <summary>
    /// A flat background colour must cover at least this share of the printable ring before
    /// it is treated as blank; busier pictures keep every pixel.
    /// </summary>
    public const double BackgroundMinimumFraction = 0.15;

    /// <summary>Per-channel difference still counted as the background colour.</summary>
    public const int BackgroundTolerance = 6;

    /// <summary>Returns the blank runs at or after <paramref name="minimumStartLba"/> in ascending LBA order.</summary>
    public static IReadOnlyList<DvdFreeRegion> FindFreeRuns(
        string imagePath, ImageMappingOptions mapping, uint minimumStartLba = 0,
        CancellationToken cancellationToken = default, int whiteLuminance = TransparentOnly,
        bool backgroundIsBlank = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(mapping);
        RasterImage image = RasterImage.Load(imagePath);
        BlankColour? background = backgroundIsBlank
            ? image.FindDominantColour(mapping.InnerRadiusMm, mapping.OuterRadiusMm,
                mapping.AlphaThreshold, BackgroundMinimumFraction, BackgroundTolerance)
            : null;
        int sectorsPerBlock = DvdEccBlockEncoder.SectorCount;
        if (mapping.TotalSectors == 0 || mapping.TotalSectors % sectorsPerBlock != 0)
            throw new ArgumentException("Total sectors must be a positive multiple of 16.", nameof(mapping));
        double bitLengthMm = mapping.ChannelBitLengthNm * 1e-6;
        double trackLengthMm = mapping.TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector * bitLengthMm;
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(mapping.InnerRadiusMm, mapping.OuterRadiusMm,
            trackLengthMm, mapping.PitchLinear, mapping.PitchQuadratic, mapping.PitchCubic);
        double scale = (Math.Min(image.Width, image.Height) - 1) / (2.0 * mapping.OuterRadiusMm);
        // Half a pixel keeps one-pixel-wide features from slipping between samples.
        int stepBits = Math.Max(1, (int)Math.Round(0.5 / scale / bitLengthMm));
        double startAngle = mapping.StartAngleDegrees * Math.PI / 180.0;
        double direction = mapping.Clockwise ? -1.0 : 1.0;
        int blocks = checked((int)(mapping.TotalSectors / sectorsPerBlock));
        bool[] used = new bool[mapping.TotalSectors];

        Parallel.For(0, blocks, new ParallelOptions { CancellationToken = cancellationToken }, block =>
        {
            for (int sector = 0; sector < sectorsPerBlock; sector++)
            {
                long sectorIndex = ((long)block * sectorsPerBlock) + sector;
                ulong firstBit = (ulong)sectorIndex * DvdEccBlockEncoder.ChannelBitsPerSector;
                for (int bit = 0; ; bit += stepBits)
                {
                    bool last = bit >= DvdEccBlockEncoder.ChannelBitsPerSector;
                    double relative = last ? DvdEccBlockEncoder.ChannelBitsPerSector : bit + 0.5;
                    double arc = Math.Min((firstBit + relative) * bitLengthMm, geometry.TrackLengthMm);
                    (double radius, double trackAngle) = geometry.AtArcLengthFast(arc);
                    double polar = startAngle + (direction * trackAngle);
                    double px = ((image.Width - 1) / 2.0) + (radius * Math.Cos(polar) * scale);
                    double py = ((image.Height - 1) / 2.0) - (radius * Math.Sin(polar) * scale);
                    if (px >= 0 && py >= 0 && px <= image.Width - 1 && py <= image.Height - 1
                        && image.AnyContentAt((int)Math.Floor(px), (int)Math.Floor(py), mapping.AlphaThreshold, whiteLuminance, background))
                    {
                        used[sectorIndex] = true;
                        break;
                    }

                    if (last) break;
                }
            }
        });

        long total = mapping.TotalSectors;
        List<DvdFreeRegion> runs = [];
        long runStart = -1;
        for (long sector = minimumStartLba; sector <= total; sector++)
        {
            bool free = sector < total && !used[sector];
            if (free)
            {
                if (runStart < 0) runStart = sector;
            }
            else if (runStart >= 0)
            {
                if (sector - runStart >= MinimumRunSectors)
                    runs.Add(new DvdFreeRegion((uint)runStart, (uint)sector));
                runStart = -1;
            }
        }

        return runs;
    }

    /// <summary>
    /// Packs the files against the outer edge of the disc: blank runs are taken from the highest
    /// LBA (outermost radius) downwards until they hold <paramref name="neededSectors"/>, and the
    /// innermost run taken is trimmed from its inner end. File data then occupies one compact
    /// outer band instead of being scattered over the whole picture. Returns the runs in
    /// ascending LBA order, or null when the blank area is too small.
    /// </summary>
    public static IReadOnlyList<DvdFreeRegion>? ChooseSegments(IReadOnlyList<DvdFreeRegion> runs, uint neededSectors)
    {
        ArgumentNullException.ThrowIfNull(runs);
        List<DvdFreeRegion> chosen = [];
        ulong remaining = neededSectors;
        foreach (DvdFreeRegion run in runs.OrderByDescending(run => run.StartLba))
        {
            if (remaining == 0) break;
            if (run.Sectors <= remaining)
            {
                chosen.Add(run);
                remaining -= run.Sectors;
            }
            else
            {
                // Run lengths and the request are whole ECC Blocks, so the trimmed start stays aligned.
                chosen.Add(new DvdFreeRegion(run.EndLbaExclusive - (uint)remaining, run.EndLbaExclusive));
                remaining = 0;
            }
        }

        return remaining == 0 ? chosen.OrderBy(run => run.StartLba).ToList() : null;
    }

    /// <summary>Total sectors held by the runs.</summary>
    public static ulong TotalSectors(IEnumerable<DvdFreeRegion> runs) => runs.Aggregate(0UL, (sum, run) => sum + run.Sectors);
}
