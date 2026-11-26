using System.IO;
using DiscImageStudio.Burning;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

/// <summary>Shared by the burn page and the CD Extra window.</summary>
internal static class CdTextReadback
{
    /// <summary>Compares the CD-TEXT the recorder read back with the requested album (the
    /// same text after encoding, so replaced characters do not count as differences) and
    /// reports every mismatch, including a changed track order, in the log and milestones.</summary>
    internal static string Report(BurnMilestonesViewModel milestones, CdTextAlbum album, CdTextBurnOutcome? outcome)
    {
        IReadOnlyList<string>? differences = null;
        if (outcome is { Written: true, ReadbackPacks: { Length: > 0 } packs, ReadbackError: null })
        {
            try
            {
                differences = CdTextPacks.Compare(album, CdTextPacks.Decode(packs));
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                outcome = outcome with { ReadbackError = L.F($"读回的 CD-TEXT 无法解析：{exception.Message}") };
            }
        }
        milestones.ReportCdText(outcome, differences);
        string line = outcome switch
        {
            null or { Written: false } => L.F($"CD-TEXT 未写入：{outcome?.SkippedReason ?? L.T("光驱不支持")}"),
            { ReadbackError: { } error } => L.F($"CD-TEXT 已写入，但读回失败：{error}"),
            _ when differences is { Count: > 0 } => L.F($"CD-TEXT 读回与设置不一致：{string.Join("；", differences)}"),
            _ => L.F($"CD-TEXT 读回一致：{outcome.ReadbackPacks!.Length / CdTextPacks.PackBytes} 个数据包，曲序正确。"),
        };
        return line;
    }

}
