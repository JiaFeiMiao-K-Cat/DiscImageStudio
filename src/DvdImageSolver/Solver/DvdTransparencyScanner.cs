using DiscImageStudio.Core;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

/// <summary>A run of whole ECC Blocks that the image never touches, as an LBA interval.</summary>
public readonly record struct DvdFreeRegion(uint StartLba, uint EndLbaExclusive)
{
    public uint Sectors => EndLbaExclusive - StartLba;
}

/// <summary>
/// Finds ECC Blocks whose entire spiral footprint lies on transparent pixels. The image
/// generator leaves those payload bytes untouched, so they can carry file data instead.
/// The test is conservative: every bilinear neighbour of every sample must be transparent.
/// </summary>
public static class DvdTransparencyScanner
{
    /// <summary>Returns the free runs at or after <paramref name="minimumStartLba"/>, longest first.</summary>
    public static IReadOnlyList<DvdFreeRegion> FindFreeRuns(
        string imagePath, ImageMappingOptions mapping, uint minimumStartLba = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(mapping);
        RasterImage image = RasterImage.Load(imagePath);
        int sectorsPerBlock = DvdEccBlockEncoder.SectorCount;
        if (mapping.TotalSectors == 0 || mapping.TotalSectors % sectorsPerBlock != 0)
            throw new ArgumentException("Total sectors must be a positive multiple of 16.", nameof(mapping));
        double bitLengthMm = mapping.ChannelBitLengthNm * 1e-6;
        double trackLengthMm = mapping.TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector * bitLengthMm;
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(mapping.InnerRadiusMm, mapping.OuterRadiusMm,
            trackLengthMm, mapping.PitchLinear, mapping.PitchQuadratic, mapping.PitchCubic);
        double scale = (Math.Min(image.Width, image.Height) - 1) / (2.0 * mapping.OuterRadiusMm);
        // Half a pixel keeps one-pixel-wide opaque features from slipping between samples.
        int stepBits = Math.Max(1, (int)Math.Round(0.5 / scale / bitLengthMm));
        double startAngle = mapping.StartAngleDegrees * Math.PI / 180.0;
        double direction = mapping.Clockwise ? -1.0 : 1.0;
        int blocks = checked((int)(mapping.TotalSectors / sectorsPerBlock));
        bool[] used = new bool[blocks];

        Parallel.For(0, blocks, new ParallelOptions { CancellationToken = cancellationToken }, block =>
        {
            ulong firstBit = (ulong)block * DvdEccBlockEncoder.ChannelBitsPerBlock;
            for (int bit = 0; ; bit += stepBits)
            {
                bool last = bit >= DvdEccBlockEncoder.ChannelBitsPerBlock;
                double relative = last ? DvdEccBlockEncoder.ChannelBitsPerBlock : bit + 0.5;
                double arc = Math.Min((firstBit + relative) * bitLengthMm, geometry.TrackLengthMm);
                (double radius, double trackAngle) = geometry.AtArcLengthFast(arc);
                double polar = startAngle + (direction * trackAngle);
                double px = ((image.Width - 1) / 2.0) + (radius * Math.Cos(polar) * scale);
                double py = ((image.Height - 1) / 2.0) - (radius * Math.Sin(polar) * scale);
                if (px >= 0 && py >= 0 && px <= image.Width - 1 && py <= image.Height - 1
                    && image.AnyAlphaAtLeast((int)Math.Floor(px), (int)Math.Floor(py), mapping.AlphaThreshold))
                {
                    used[block] = true;
                    return;
                }

                if (last) return;
            }
        });

        int first = checked((int)((minimumStartLba + (uint)sectorsPerBlock - 1) / (uint)sectorsPerBlock));
        List<DvdFreeRegion> runs = [];
        int runStart = -1;
        for (int block = first; block <= blocks; block++)
        {
            bool free = block < blocks && !used[block];
            if (free)
            {
                if (runStart < 0) runStart = block;
            }
            else if (runStart >= 0)
            {
                runs.Add(new DvdFreeRegion((uint)runStart * (uint)sectorsPerBlock,
                    (uint)block * (uint)sectorsPerBlock));
                runStart = -1;
            }
        }

        return runs.OrderByDescending(run => run.Sectors).ThenBy(run => run.StartLba).ToList();
    }

    /// <summary>
    /// Picks the start of a file region of <paramref name="neededSectors"/> inside the longest
    /// free run that is large enough, or null when none fits.
    /// </summary>
    public static DvdFreeRegion? ChooseRegion(IReadOnlyList<DvdFreeRegion> runs, uint neededSectors)
    {
        ArgumentNullException.ThrowIfNull(runs);
        foreach (DvdFreeRegion run in runs)
        {
            if (run.Sectors >= neededSectors) return run;
        }

        return null;
    }
}
