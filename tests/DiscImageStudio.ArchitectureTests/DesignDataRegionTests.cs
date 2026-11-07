using System.IO;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Iso;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using DvdImageSolver.Encoding;

/// <summary>Covers the design page's data-region guide: the real ISO/UDF planners, the
/// usable (writable) capacity instead of the physical geometry, per-family radius mapping,
/// and the split annuli that keep outer-ring metadata marked near the hub.</summary>
internal static class DesignDataRegionTests
{
    private const double ToleranceMm = 1e-6;
    private const long CdTotalSectors = 333_000;
    private const long CdUsableSectors = 330_000;
    private const double CdInnerMm = 25;
    private const double CdOuterMm = 58;

    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"design-data-region-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string data = Path.Combine(root, "data");
            Directory.CreateDirectory(Path.Combine(data, "nested"));
            File.WriteAllBytes(Path.Combine(data, "payload.bin"),
                Enumerable.Range(0, 70_003).Select(index => (byte)(index * 37)).ToArray());
            File.WriteAllBytes(Path.Combine(data, "nested", "notes.txt"), "design guide"u8.ToArray());
            File.WriteAllBytes(Path.Combine(data, "empty"), []);

            TestEmptyFolder();
            TestCd(data);
            TestDvd(data);
            TestBluRay(data);
            TestCapacityFailures(root, data);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("design-data-region: passed (empty folder, CD/DVD/BD plans, outer-ring metadata split, usable capacity, overflow)");
    }

    private static void TestEmptyFolder()
    {
        DataRegionInputs inputs = new(OpticalDiscFamily.CompactDisc, string.Empty, "GUIDE", false,
            CdTotalSectors, CdUsableSectors, CdInnerMm, CdOuterMm, 0, 0, 0);
        Require(DataRegionPlanner.Compute(inputs) is null, "no data folder means no guide");
    }

    private static void TestCd(string data)
    {
        CdDiscParameters parameters = new(CdInnerMm, CdOuterMm, CdTotalSectors,
            UsableSectors: CdUsableSectors);
        CdDataLayoutPlan inner = CdDataImageGenerator.PrepareLayout(data, parameters, "GUIDE", false);
        IReadOnlyList<DesignViewModel.DataRegionMm> sequential =
            Compute(OpticalDiscFamily.CompactDisc, data, false, CdTotalSectors, CdUsableSectors, CdInnerMm, CdOuterMm);
        Require(sequential.Count == 1, "CD inner placement draws one annulus");
        Near(CdInnerMm, sequential[0].InnerRadiusMm, "CD inner annulus starts at the hub");
        Near(RadiusAt(parameters, inner.DataEndLbaExclusive), sequential[0].OuterRadiusMm,
            "CD inner annulus ends with the last file sector");

        CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(data, parameters, "GUIDE", true);
        IReadOnlyList<DesignViewModel.DataRegionMm> outer =
            Compute(OpticalDiscFamily.CompactDisc, data, true, CdTotalSectors, CdUsableSectors, CdInnerMm, CdOuterMm);
        int reserve = FilesystemMetadataBudget.ReserveSectors(plan.FileCount);
        Require(outer.Count == 2, "CD outer placement keeps metadata and files apart");
        Near(CdInnerMm, outer[0].InnerRadiusMm, "CD metadata annulus starts at the hub");
        Near(RadiusAt(parameters, plan.MetadataEndLbaExclusive + (uint)reserve), outer[0].OuterRadiusMm,
            "CD metadata annulus includes the dispersion reserve");
        Near(RadiusAt(parameters, plan.FileDataStartLba), outer[1].InnerRadiusMm,
            "CD file annulus starts where file data begins");
        Near(RadiusAt(parameters, plan.DataEndLbaExclusive), outer[1].OuterRadiusMm,
            "CD file annulus ends at the usable edge");
        Require(outer[0].OuterRadiusMm < outer[1].InnerRadiusMm,
            "CD picture gap stays visible between the annuli");

        const long smallerUsable = 250_000;
        IReadOnlyList<DesignViewModel.DataRegionMm> smaller =
            Compute(OpticalDiscFamily.CompactDisc, data, true, CdTotalSectors, smallerUsable, CdInnerMm, CdOuterMm);
        Require(smaller.Count == 2 && smaller[1].OuterRadiusMm < outer[1].OuterRadiusMm,
            "CD usable capacity pulls outer-ring files inward");
    }

    private static void TestDvd(string data)
    {
        const uint totalSectors = 4096;
        const uint usableSectors = 4000;
        const double innerMm = 24;
        const double outerMm = 58;
        DvdTrackGeometry geometry = DvdGeometry(totalSectors, innerMm, outerMm);
        DvdStreamingOptions options = new(totalSectors, innerMm, outerMm, UsableSectors: usableSectors);

        DvdHybridStreamingPlan inner = DvdStreamingGenerator.PrepareHybrid(data, options, "GUIDE", false);
        IReadOnlyList<DesignViewModel.DataRegionMm> sequential =
            Compute(OpticalDiscFamily.Dvd, data, false, totalSectors, usableSectors, innerMm, outerMm);
        Require(sequential.Count == 1, "DVD inner placement draws one annulus");
        Near(innerMm, sequential[0].InnerRadiusMm, "DVD inner annulus starts at the hub");
        Near(DvdRadiusAt(geometry, totalSectors, inner.FilesystemEndLbaExclusive),
            sequential[0].OuterRadiusMm, "DVD inner annulus ends with the last file sector");

        DvdHybridStreamingPlan plan = DvdStreamingGenerator.PrepareHybrid(data, options, "GUIDE", true);
        IReadOnlyList<DesignViewModel.DataRegionMm> outer =
            Compute(OpticalDiscFamily.Dvd, data, true, totalSectors, usableSectors, innerMm, outerMm);
        int reserve = FilesystemMetadataBudget.ReserveSectors(plan.FileCount);
        Require(outer.Count == 2, "DVD outer placement keeps metadata and files apart");
        Near(innerMm, outer[0].InnerRadiusMm, "DVD metadata annulus starts at the hub");
        Near(DvdRadiusAt(geometry, totalSectors, plan.MetadataEndLbaExclusive + (uint)reserve),
            outer[0].OuterRadiusMm, "DVD metadata annulus includes the dispersion reserve");
        Near(DvdRadiusAt(geometry, totalSectors, plan.FileDataStartLba), outer[1].InnerRadiusMm,
            "DVD file annulus starts where file data begins");
        Near(DvdRadiusAt(geometry, totalSectors, plan.FilesystemEndLbaExclusive),
            outer[1].OuterRadiusMm, "DVD file annulus ends with the file data");
        Require(outer[0].OuterRadiusMm < outer[1].InnerRadiusMm,
            "DVD picture gap stays visible between the annuli");
    }

    private static void TestBluRay(string data)
    {
        const uint totalSectors = 1024;
        const uint usableSectors = 992;
        const double innerMm = 24;
        const double outerMm = 58;
        BluRayDiscParameters disc = new(totalSectors, 1, innerMm, outerMm, UsableSectors: usableSectors);
        BluRayTrackGeometry geometry = new(disc);

        BluRayIsoPlan inner = BluRayIsoGenerator.PrepareDrawing(disc, data, "GUIDE");
        IReadOnlyList<DesignViewModel.DataRegionMm> sequential =
            Compute(OpticalDiscFamily.BluRay, data, false, totalSectors, usableSectors, innerMm, outerMm);
        int innerReserve = FilesystemMetadataBudget.ReserveSectors(inner.FileCount);
        uint innerEnd = Math.Min(inner.DataEndSectorExclusive + (uint)innerReserve, disc.OutputSectors);
        Require(sequential.Count == 1, "BD inner placement draws one annulus");
        Near(geometry.AtSector(inner.PartitionStartSector).RadiusMm, sequential[0].InnerRadiusMm,
            "BD inner annulus starts at the partition");
        Near(geometry.AtSector(innerEnd).RadiusMm, sequential[0].OuterRadiusMm,
            "BD inner annulus ends at the partition data plus reserve");

        BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc, data, "GUIDE",
            filesAtOuterRing: true);
        IReadOnlyList<DesignViewModel.DataRegionMm> outer =
            Compute(OpticalDiscFamily.BluRay, data, true, totalSectors, usableSectors, innerMm, outerMm);
        int reserve = FilesystemMetadataBudget.ReserveSectors(plan.FileCount);
        Require(outer.Count == 1, "BD moves the whole partition as one annulus");
        Near(geometry.AtSector(plan.PartitionStartSector - (double)reserve).RadiusMm, outer[0].InnerRadiusMm,
            "BD outer annulus grows its partition inward for the dispersion reserve");
        Near(geometry.AtSector(plan.DataEndSectorExclusive).RadiusMm, outer[0].OuterRadiusMm,
            "BD outer annulus keeps the relocated partition's outer edge");
        Require(outer[0].InnerRadiusMm > geometry.AtSector(usableSectors / 2).RadiusMm,
            "BD outer partition sits on the outer half of the writable area");
    }

    private static void TestCapacityFailures(string root, string data)
    {
        ThrowsCapacity(
            () => Compute(OpticalDiscFamily.CompactDisc, data, false, 32, 32, CdInnerMm, CdOuterMm),
            "CD folder larger than the disc");
        ThrowsCapacity(
            () => Compute(OpticalDiscFamily.Dvd, data, true, 64, 64, 24, 58),
            "DVD outer files leave no full ECC block");
        ThrowsCapacity(
            () => Compute(OpticalDiscFamily.BluRay, data, true, 640, 640, 24, 58),
            "BD outer files leave no drawing cluster");

        string missing = Path.Combine(root, "missing");
        Throws<DirectoryNotFoundException>(
            () => Compute(OpticalDiscFamily.CompactDisc, missing, false, CdTotalSectors, CdUsableSectors, CdInnerMm, CdOuterMm),
            "missing folder is not a capacity verdict");
    }

    private static IReadOnlyList<DesignViewModel.DataRegionMm> Compute(
        OpticalDiscFamily family, string directory, bool filesAtOuterRing,
        long totalSectors, long usableSectors, double innerMm, double outerMm)
        => DataRegionPlanner.Compute(new DataRegionInputs(family, directory, "GUIDE", filesAtOuterRing,
            totalSectors, usableSectors, innerMm, outerMm, 0, 0, 0))
            ?? throw new InvalidOperationException("Expected the guide to return regions.");

    private static double RadiusAt(CdDiscParameters parameters, uint lba)
        => parameters.RadiusFromByte((long)lba * CdDiscParameters.BytesPerSector);

    private static DvdTrackGeometry DvdGeometry(uint totalSectors, double innerMm, double outerMm)
        => DvdTrackGeometry.Create(innerMm, outerMm, DvdLength(totalSectors), 0, 0, 0);

    private static double DvdRadiusAt(DvdTrackGeometry geometry, uint totalSectors, uint lba)
        => geometry.RadiusAtArcLength(DvdLength(totalSectors) * lba / totalSectors);

    private static double DvdLength(uint totalSectors)
        => totalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * DvdStreamingOptions.StandardChannelBitLengthNm * 1e-6;

    private static void Near(double expected, double actual, string what)
    {
        if (Math.Abs(expected - actual) > ToleranceMm)
        {
            throw new InvalidOperationException($"{what}: expected {expected} mm, got {actual} mm");
        }
    }

    private static void ThrowsCapacity(Action action, string what)
    {
        try
        {
            action();
        }
        catch (DataRegionCapacityException)
        {
            return;
        }

        throw new InvalidOperationException($"{what}: expected a capacity rejection");
    }

    private static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"{what}: expected {typeof(TException).Name}");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException(what);
        }
    }
}
