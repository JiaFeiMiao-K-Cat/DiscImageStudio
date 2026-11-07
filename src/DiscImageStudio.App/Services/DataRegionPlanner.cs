using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Iso;
using DiscImageStudio.ViewModels;
using DvdImageSolver.Encoding;

namespace DiscImageStudio.Services;

/// <summary>One guide computation captured from the parameter state on the UI thread, so
/// the folder scan can run on an STA worker without touching view-model fields.</summary>
internal sealed record DataRegionInputs(
    OpticalDiscFamily Family,
    string Directory,
    string VolumeLabel,
    bool FilesAtOuterRing,
    long TotalSectors,
    long UsableSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double PitchLinear,
    double PitchQuadratic,
    double PitchCubic);

/// <summary>The layout planner rejected the data because it cannot fit the disc (or the
/// family's format), as opposed to invalid geometry that hides the guide silently.</summary>
internal sealed class DataRegionCapacityException : Exception
{
    internal DataRegionCapacityException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Computes the design guide's occupied annuli with the same planners the export
/// and tone preview use, including the usable (writable) sector count rather than the
/// physical geometry. Metadata and file data stay separate regions: outer-ring files still
/// leave their filesystem metadata at the inner edge.</summary>
internal static class DataRegionPlanner
{
    internal static IReadOnlyList<DesignViewModel.DataRegionMm>? Compute(DataRegionInputs inputs)
    {
        if (inputs.Directory.Length == 0)
        {
            return null;
        }

        try
        {
            return inputs.Family switch
            {
                OpticalDiscFamily.Dvd => ComputeDvd(inputs),
                OpticalDiscFamily.BluRay => ComputeBluRay(inputs),
                _ => ComputeCd(inputs),
            };
        }
        catch (Exception exception) when (IsCapacityExceeded(exception))
        {
            throw new DataRegionCapacityException(exception.Message, exception);
        }
    }

    private static IReadOnlyList<DesignViewModel.DataRegionMm> ComputeCd(DataRegionInputs inputs)
    {
        CdDiscParameters parameters = new(
            inputs.InnerRadiusMm,
            inputs.OuterRadiusMm,
            inputs.TotalSectors,
            PitchLinear: inputs.PitchLinear,
            PitchQuadratic: inputs.PitchQuadratic,
            PitchCubic: inputs.PitchCubic,
            UsableSectors: inputs.UsableSectors);
        CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(
            inputs.Directory, parameters, inputs.VolumeLabel, inputs.FilesAtOuterRing);
        return IsoDataRegions(parameters.InnerRadiusMm, parameters.OuterRadiusMm,
            plan.MetadataEndLbaExclusive, plan.FileDataStartLba, plan.DataEndLbaExclusive,
            FilesystemMetadataBudget.ReserveSectors(plan.FileCount),
            lba => parameters.RadiusFromByte((long)lba * CdDiscParameters.BytesPerSector));
    }

    private static IReadOnlyList<DesignViewModel.DataRegionMm> ComputeDvd(DataRegionInputs inputs)
    {
        DvdStreamingOptions options = new(
            checked((uint)inputs.TotalSectors),
            inputs.InnerRadiusMm,
            inputs.OuterRadiusMm,
            PitchLinear: inputs.PitchLinear,
            PitchQuadratic: inputs.PitchQuadratic,
            PitchCubic: inputs.PitchCubic,
            UsableSectors: checked((uint)inputs.UsableSectors));
        DvdHybridStreamingPlan plan = DvdStreamingGenerator.PrepareHybrid(
            inputs.Directory, options, inputs.VolumeLabel, inputs.FilesAtOuterRing);

        // The solver maps sector positions over the physical track, not over the usable
        // capacity: unused writable sectors remain at the outer edge of the same spiral.
        double length = inputs.TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * DvdStreamingOptions.StandardChannelBitLengthNm * 1e-6;
        DvdTrackGeometry geometry = DvdTrackGeometry.Create(
            inputs.InnerRadiusMm, inputs.OuterRadiusMm, length,
            inputs.PitchLinear, inputs.PitchQuadratic, inputs.PitchCubic);
        return IsoDataRegions(inputs.InnerRadiusMm, inputs.OuterRadiusMm,
            plan.MetadataEndLbaExclusive, plan.FileDataStartLba, plan.FilesystemEndLbaExclusive,
            FilesystemMetadataBudget.ReserveSectors(plan.FileCount),
            lba => geometry.RadiusAtArcLength(length * lba / inputs.TotalSectors));
    }

    private static IReadOnlyList<DesignViewModel.DataRegionMm> ComputeBluRay(DataRegionInputs inputs)
    {
        BluRayDiscParameters disc = new(
            checked((uint)inputs.TotalSectors),
            1,
            inputs.InnerRadiusMm,
            inputs.OuterRadiusMm,
            PitchLinear: inputs.PitchLinear,
            PitchQuadratic: inputs.PitchQuadratic,
            PitchCubic: inputs.PitchCubic,
            UsableSectors: checked((uint)inputs.UsableSectors));
        BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(
            disc, inputs.Directory, inputs.VolumeLabel,
            filesAtOuterRing: inputs.FilesAtOuterRing);

        // UDF relocates the whole partition (metadata and file data together), so the
        // occupied span stays one annulus at whichever ring was chosen. The shared reserve
        // grows the partition away from its fixed anchor: inner placement extends outward,
        // outer placement extends inward from the drawing's outer edge.
        int reserve = FilesystemMetadataBudget.ReserveSectors(plan.FileCount);
        double startSector = inputs.FilesAtOuterRing
            ? Math.Max(0, (double)plan.PartitionStartSector - reserve)
            : plan.PartitionStartSector;
        double endSector = inputs.FilesAtOuterRing
            ? plan.DataEndSectorExclusive
            : Math.Min(plan.DataEndSectorExclusive + (double)reserve, disc.OutputSectors);
        BluRayTrackGeometry geometry = new(disc);
        double innerMm = geometry.AtSector(startSector).RadiusMm;
        double outerMm = geometry.AtSector(endSector).RadiusMm;
        return outerMm > innerMm
            ? [new DesignViewModel.DataRegionMm(innerMm, Math.Min(outerMm, disc.OuterRadiusMm))]
            : [];
    }

    /// <summary>Maps a filesystem plan's occupied LBAs to mm annuli: metadata (plus the
    /// shared dispersion reserve) and file data, merged when they are contiguous at the
    /// inner edge. <paramref name="radiusAtLba"/> is the family's LBA-to-mm projection.</summary>
    private static IReadOnlyList<DesignViewModel.DataRegionMm> IsoDataRegions(
        double innerMm, double outerMm, uint metadataEndLba, uint fileDataStartLba,
        uint dataEndLbaExclusive, int reserveSectors, Func<uint, double> radiusAtLba)
    {
        uint metadataEnd = checked(metadataEndLba + (uint)reserveSectors);
        double metadataOuter = radiusAtLba(metadataEnd);
        List<DesignViewModel.DataRegionMm> regions = new(2);
        if (metadataOuter > innerMm)
        {
            regions.Add(new DesignViewModel.DataRegionMm(innerMm, metadataOuter));
        }

        if (fileDataStartLba < dataEndLbaExclusive)
        {
            double fileInner = radiusAtLba(fileDataStartLba);
            double fileOuter = radiusAtLba(dataEndLbaExclusive);
            if (fileDataStartLba <= metadataEnd)
            {
                // Contiguous with the metadata zone: one annulus to the further edge.
                DesignViewModel.DataRegionMm merged = new(innerMm, Math.Max(metadataOuter, fileOuter));
                if (regions.Count > 0)
                {
                    regions[0] = merged;
                }
                else
                {
                    regions.Add(merged);
                }
            }
            else if (fileOuter > fileInner)
            {
                regions.Add(new DesignViewModel.DataRegionMm(fileInner, fileOuter));
            }
        }

        return regions
            .Select(region => new DesignViewModel.DataRegionMm(
                Math.Clamp(region.InnerRadiusMm, innerMm, outerMm),
                Math.Clamp(region.OuterRadiusMm, innerMm, outerMm)))
            .Where(region => region.OuterRadiusMm > region.InnerRadiusMm)
            .ToList();
    }

    /// <summary>The layout planners report "does not fit" with these exact (unlocalized)
    /// messages; every other exception means invalid geometry or a missing folder and hides
    /// the guide without the capacity warning. If a planner changes its wording the guide
    /// degrades to hidden, never to a false warning.</summary>
    private static bool IsCapacityExceeded(Exception exception) =>
        exception is ArgumentException
        && (exception.Message.Contains("leaves no CD sectors for drawing")
            || exception.Message.Contains("no complete ECC Block for drawing")
            || exception.Message.Contains("must leave at least")
            || exception.Message.Contains("exceed the disc capacity")
            || exception.Message.Contains("disc has only")
            || exception.Message.Contains("no complete drawing cluster")
            || exception.Message.Contains("too small for filesystem metadata")
            || exception.Message.Contains("UDF drawing volumes need at least")
            // A single file too large for the format (ISO9660's 4 GiB limit) can never be
            // placed on this disc family: report it as capacity, not as a geometry problem.
            || exception.Message.Contains("larger than 4 GiB"));
}
