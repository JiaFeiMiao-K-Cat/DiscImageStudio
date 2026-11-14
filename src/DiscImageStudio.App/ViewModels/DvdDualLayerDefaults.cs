using DiscImageStudio.Dvd;

namespace DiscImageStudio.ViewModels;

internal static class DvdDualLayerDefaults
{
    internal const string Plus = "DVD+R DL";
    internal const string Minus = "DVD-R DL";
    internal static IReadOnlyList<string> Formats { get; } = Array.AsReadOnly(new[] { Plus, Minus });

    // Typical 120 mm DVD-R DL sequential layout. The drive can supply actual values.
    internal const uint MinusLayer0Sectors = 2_092_896;
    internal const uint MinusTotalSectors = 4_171_712;
    internal const double ChannelBitLengthNm = 147.0;

    internal static uint Layer0Sectors(string format) => format == Minus
        ? MinusLayer0Sectors : DvdImageGenerator.MaxDvdPlusRLayer0Sectors;
}
