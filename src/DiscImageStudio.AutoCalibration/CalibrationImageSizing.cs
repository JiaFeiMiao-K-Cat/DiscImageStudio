namespace DiscImageStudio.AutoCalibration;

public static class CalibrationImageSizing
{
    public const uint DefaultDvdImageSectors = 2_295_104;
    public const uint DefaultCdDiscSectors = 359_849;
    public const uint CdLeadOutMarginSectors = 64;
    public const uint MinimumCdImageSectors = 50_000;

    public static uint ResolveForExport(
        string mediaType,
        bool useManualParameters,
        long? manualDiscSectors,
        long? detectedBlankDiscSectors)
    {
        if (useManualParameters)
        {
            return FromDiscCapacity(mediaType, manualDiscSectors ?? 0);
        }

        if (detectedBlankDiscSectors is not { } sectors)
        {
            throw new InvalidOperationException("未读取到空白盘参数；请重读光盘或启用手动参数模式。");
        }

        return FromDiscCapacity(mediaType, sectors);
    }

    public static uint FromDiscCapacity(string mediaType, long discSectors)
    {
        switch (mediaType.ToLowerInvariant())
        {
            case "dvd":
                if (discSectors < DefaultDvdImageSectors)
                {
                    throw new InvalidOperationException(
                        $"DVD 总扇区数不足，至少需要 {DefaultDvdImageSectors:N0} 个扇区。");
                }

                return DefaultDvdImageSectors;
            case "cd":
                if (discSectors < MinimumCdImageSectors + CdLeadOutMarginSectors)
                {
                    throw new InvalidOperationException(
                        "CD 总扇区数过小，生成的标记镜像无法通过 Discratcher 校验。");
                }

                return checked((uint)(Math.Min(discSectors, DefaultCdDiscSectors) - CdLeadOutMarginSectors));
            default:
                throw new ArgumentException($"不支持的标记盘类型：{mediaType}", nameof(mediaType));
        }
    }
}
