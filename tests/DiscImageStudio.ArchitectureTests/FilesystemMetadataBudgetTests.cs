using DiscImageStudio.Iso;

/// <summary>Covers the image-independent filesystem metadata reserve shared by the
/// designer's data-region guide and the #96 dispersed placement: monotone in the file
/// count, capped, and zero for an empty folder.</summary>
internal static class FilesystemMetadataBudgetTests
{
    public static void Run()
    {
        // An empty folder costs nothing beyond the base structures the writers always emit.
        Equal(0, FilesystemMetadataBudget.ReserveSectors(0), "empty folder reserve");

        // One file: up to MaxExtentsPerFile - 1 extra extents (the first extent is
        // already in the sequential plan) at the worst-case 320-byte record.
        Equal(
            (int)((FilesystemMetadataBudget.MaxExtentsPerFile - 1)
                * FilesystemMetadataBudget.WorstBytesPerExtraExtent + 2047) / 2048,
            FilesystemMetadataBudget.ReserveSectors(1),
            "single file reserve");

        // The reserve grows with the file count, never decreasing.
        for (int count = 1; count <= 1000; count += 7)
        {
            int before = FilesystemMetadataBudget.ReserveSectors(count);
            int after = FilesystemMetadataBudget.ReserveSectors(count + 1);
            True(after >= before, $"monotone reserve at {count} files");
        }

        // A huge folder can never push the guide past the cap.
        Equal(
            FilesystemMetadataBudget.MaximumReserveSectors,
            FilesystemMetadataBudget.ReserveSectors(1_000_000),
            "reserve cap");
        True(
            FilesystemMetadataBudget.ReserveSectors(1_000_000) <= 4096,
            "cap is bounded");
    }

    private static void Equal(int expected, int actual, string what)
    {
        if (expected != actual)
        {
            throw new Exception($"{what}: expected {expected}, got {actual}");
        }
    }

    private static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new Exception(what);
        }
    }
}
