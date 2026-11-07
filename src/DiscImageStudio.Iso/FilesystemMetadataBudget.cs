namespace DiscImageStudio.Iso;

/// <summary>
/// Image-independent metadata reservation shared by the designer's data-region guide and
/// dispersed (blank-area) file placement: how much extra filesystem space fragmented files
/// may need beyond the sequential layout plan. The budget depends only on the file tree,
/// never on picture content, so the design guide stays stable while artwork is edited and
/// the dispersion scan is not run on the Design page.
/// </summary>
public static class FilesystemMetadataBudget
{
    /// <summary>Upper bound on extents one file may occupy when dispersed into blank
    /// picture areas: blank holes are few, so 32 fragments per file covers any layout the
    /// dispersion planner can produce.</summary>
    public const int MaxExtentsPerFile = 32;

    /// <summary>Worst bytes one extra extent adds to the filesystem: ISO9660 directory
    /// record (34 + 31 identifier) plus Joliet record (34 + 60 identifier), plus one entry
    /// in each path table (~56 + ~85 bytes), rounded up for padding. UDF's 8-byte
    /// allocation descriptor is well inside the same bound.</summary>
    public const int WorstBytesPerExtraExtent = 320;

    /// <summary>Hard cap so a huge folder cannot turn the guide into most of the disc.</summary>
    public const int MaximumReserveSectors = 4096;

    /// <summary>Sectors to reserve on top of a sequential layout plan for the extra
    /// directory records/entries a file of up to <see cref="MaxExtentsPerFile"/> extents
    /// would need. Pure function of the file count: no image, no scan.</summary>
    public static int ReserveSectors(int fileCount)
    {
        if (fileCount <= 0)
        {
            return 0;
        }

        long bytes = (long)fileCount * (MaxExtentsPerFile - 1) * WorstBytesPerExtraExtent;
        int sectors = (int)((bytes + UdfImageWriter.LogicalSectorBytes - 1) / UdfImageWriter.LogicalSectorBytes);
        return Math.Min(sectors, MaximumReserveSectors);
    }
}
