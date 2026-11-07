using DiscImageStudio.Burning;

namespace DiscImageStudio.Services;

/// <summary>Combines an already resolved preset limit with the current medium's capacity.
/// Both inputs already exclude any reserved sectors; do not subtract them again.</summary>
internal static class StreamBurnCapacity
{
    internal static long Resolve(long usableSectors, long writableSectors, OpticalBurnMediaKind kind)
    {
        if (usableSectors <= 0 || writableSectors <= 0)
            throw new InvalidOperationException("The medium has no available sectors for this write.");
        int alignment = kind switch
        {
            OpticalBurnMediaKind.DvdData => 16,
            OpticalBurnMediaKind.BluRayData => 32,
            _ => 1,
        };
        long limit = Math.Min(usableSectors, writableSectors);
        limit -= limit % alignment;
        if (limit == 0)
            throw new InvalidOperationException("The medium has no complete recording block available.");
        return limit;
    }
}
