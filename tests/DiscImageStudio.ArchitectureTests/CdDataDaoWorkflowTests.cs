using DiscImageStudio.Burning;
using DiscImageStudio.Core;

internal static class CdDataDaoWorkflowTests
{
    internal static void Run()
    {
        Require(OpticalBurnMedia.UsesCdDao(OpticalBurnMediaKind.CdData)
            && OpticalBurnMedia.UsesCdDao(OpticalBurnMediaKind.CdAudio)
            && !OpticalBurnMedia.UsesCdDao(OpticalBurnMediaKind.DvdData)
            && !OpticalBurnMedia.UsesCdDao(OpticalBurnMediaKind.BluRayData),
            "CD data and audio use the same DAO format for capacity, writing and speed queries");
        long sectors = DiscPresetJsonStore.LoadBuiltIn().CdPresets.Single(
            preset => preset.Id == "ritek-medical-aqua").Sectors;
        Require(sectors == 359845, "Ritek ATIP-calibrated preset is unchanged");
        OpticalBurnRequest request = new("fake-recorder", OpticalBurnMediaKind.CdData,
            sectors * 2048, (_, _) => { }, new OpticalWriteSpeed(1200, false));

        // These fakes never create a recorder. The source is not consumed by the
        // fake WriteMedia; raw-image encoding has its own byte-level tests.
        using GeneratedContentComStream content = new(2048, (output, _) => output.Write(new byte[2048]),
            null, CancellationToken.None);
        content.WaitUntilPrebuffered(CancellationToken.None);
        foreach (long remaining in new[] { sectors, sectors + 2, sectors + 3 })
        {
            RecordingCdRawSession session = new() { LastLeadoutSector = remaining };
            using RecordingCdRawImageSession image = new(session.Calls) { ImageStartOfLeadout = sectors };
            List<string> operations = [];
            WindowsImapiBurner.BurnCdDao(session, image, content, request, CancellationToken.None,
                reportOperation: operations.Add);
            Require(session.Calls.Contains("WriteMedia") && session.Calls[^1] == "ReleaseMedia",
                "data DAO follows audio lead-out capacity handling and closes the session");
            Require(session.Calls.IndexOf("PrepareMedia") < session.Calls.IndexOf("LastPossibleStartOfLeadout")
                && session.Calls.IndexOf("LastPossibleStartOfLeadout") < session.Calls.IndexOf("WriteMedia"),
                "DAO checks prepared-media lead-out before writing");
            Require(request.ContentLength == 359845L * 2048 && image.StartOfLeadout == sectors,
                "capacity handling preserves all ISO sectors and the preset's lead-out");
            Require(operations.Contains("构造 CD DAO 数据轨") && operations.Contains("写入 CD DAO 数据轨"),
                "data DAO is identified in diagnostics");
            Require(session.RequestedSectorType == CdRawSectorType.SubcodeIsCooked
                && image.ImageSectorType == CdRawSectorType.SubcodeIsCooked,
                "data DAO keeps cooked R-W when the recorder does not list raw P-W");
        }
        RecordingCdRawSession rawCapable = new() { LastLeadoutSector = sectors, ReportedSectorTypes = [1, 2, 3] };
        using RecordingCdRawImageSession rawImage = new(rawCapable.Calls) { ImageStartOfLeadout = sectors };
        WindowsImapiBurner.BurnCdDao(rawCapable, rawImage, content, request, CancellationToken.None);
        Require(rawCapable.RequestedSectorType == CdRawSectorType.SubcodeIsRaw
            && rawImage.ImageSectorType == CdRawSectorType.SubcodeIsRaw
            && rawCapable.Calls[^1] == "ReleaseMedia",
            "data DAO requests raw P-W and builds the matching template when the recorder lists it");
        foreach (int excess in new[] { 1, 2, 3, 4 })
        {
            RecordingCdRawSession tooSmall = new() { LastLeadoutSector = sectors - excess };
            using RecordingCdRawImageSession tooLarge = new(tooSmall.Calls) { ImageStartOfLeadout = sectors };
            try
            {
                WindowsImapiBurner.BurnCdDao(tooSmall, tooLarge, content, request, CancellationToken.None);
                throw new InvalidOperationException("Expected any excess over the DAO boundary to be rejected.");
            }
            catch (InvalidOperationException exception)
            {
                Require(exception.Message.Contains($"超出盘片边界 {excess} 个扇区")
                    && !tooSmall.Calls.Contains("WriteMedia") && tooSmall.Calls[^1] == "ReleaseMedia",
                    "data DAO refuses even a one-sector excess and releases the prepared medium");
            }
        }
        Console.WriteLine("cd-data-dao-workflow: passed (Ritek 359845 sectors, shared audio DAO capacity/cleanup, unchanged ISO length, raw P-W preferred over cooked R-W; mock recorder only)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD data DAO: " + message);
    }
}
