using System.IO;
using DiscImageStudio.Burning;

/// <summary>
/// Covers the ForceOverwrite path that lets a formatted DVD+RW be written again.
/// IMAPI reports IMAPI_FORMAT2_DATA_MEDIA_STATE_OVERWRITE_ONLY (0x1) for such a disc
/// instead of BLANK (0x2), because erasing a DVD+RW only replaces its contents.
/// </summary>
internal static class DvdRewritableOverwriteTests
{
    private const int DvdRwTotalSectors = 2_295_104;

    internal static void Run()
    {
        OpticalBurnRequest request = new("memory-only-device", OpticalBurnMediaKind.DvdData,
            DvdRwTotalSectors * 2048L, (_, _) => { });

        // A blank disc keeps the previous behaviour.
        WindowsImapiBurner.ValidateDataMedia(request, 0x2, DvdRwTotalSectors);

        // A formatted erasable disc stays rejected until the user opts in.
        Throws<InvalidOperationException>(
            () => WindowsImapiBurner.ValidateDataMedia(request, 0x1, DvdRwTotalSectors),
            "a formatted DVD+RW is rejected without explicit overwrite permission");

        OpticalBurnRequest overwrite = request with { OverwriteRewritableMedia = true };
        WindowsImapiBurner.ValidateDataMedia(overwrite, 0x1, 0, DvdRwTotalSectors);
        WindowsImapiBurner.ValidateDataMedia(overwrite, 0x1, DvdRwTotalSectors, DvdRwTotalSectors);

        // Overwriting starts again at LBA 0, so the disc total bounds the payload
        // instead of the free tail behind the previous recording.
        Throws<InvalidOperationException>(
            () => WindowsImapiBurner.ValidateDataMedia(overwrite, 0x1, 0, DvdRwTotalSectors - 1),
            "the overwrite path still enforces the disc's total capacity");

        // Appendable and finalized write-once media are never overwritten.
        Throws<InvalidOperationException>(
            () => WindowsImapiBurner.ValidateDataMedia(overwrite, 0x4, 100, DvdRwTotalSectors),
            "an appendable write-once disc is not overwritable");
        Throws<InvalidOperationException>(
            () => WindowsImapiBurner.ValidateDataMedia(overwrite, 0x8, 100, DvdRwTotalSectors),
            "a finalized write-once disc is not overwritable");
        Throws<InvalidOperationException>(
            () => WindowsImapiBurner.ValidateWritableDataCapacity(
                OpticalBurnMediaKind.DvdData, 0x4, DvdRwTotalSectors, overwrite: true),
            "the overwrite validator rejects media that is not erasable");

        // Capacity reporting uses the whole data area for an overwrite-only disc.
        OpticalMediaCapacity formatted = WindowsImapiBurner.ReadDvdMediaCapacity(
            0x7, 0x1, DvdRwTotalSectors, 0);
        Require(formatted.TotalSectors == DvdRwTotalSectors
                && formatted.WritableSectors == DvdRwTotalSectors,
            "a formatted DVD+RW reports the whole data area as writable");
        OpticalMediaCapacity blank = WindowsImapiBurner.ReadDvdMediaCapacity(
            0x7, 0x2, DvdRwTotalSectors, DvdRwTotalSectors - 608);
        Require(blank.WritableSectors == DvdRwTotalSectors - 608,
            "a blank disc still reports IMAPI's free sector count");
        Throws<IOException>(
            () => WindowsImapiBurner.ReadDvdMediaCapacity(0x7, 0x1, DvdRwTotalSectors, DvdRwTotalSectors + 1),
            "an impossible free count is rejected");

        Console.WriteLine("dvd-rewritable-overwrite: passed (blank, overwrite opt-in, total capacity, write-once rejection)");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"DVD rewritable overwrite: {message}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"DVD rewritable overwrite: {message}.");
    }
}
