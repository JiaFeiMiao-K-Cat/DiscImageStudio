using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

// Optional COM reads happen before writing, never in event/failure callbacks.
internal static class BurnDeviceDiagnostics
{
    internal static string DescribeEnvironment(OpticalBurnRequest request)
    {
        Assembly assembly = typeof(WindowsImapiBurner).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        int sectorBytes = OpticalBurnMedia.GetSectorBytes(request.MediaKind);
        return $"刻录环境：Build={version}；OS={RuntimeInformation.OSDescription}；"
            + $"Runtime={RuntimeInformation.FrameworkDescription}；Architecture={RuntimeInformation.ProcessArchitecture}；"
            + $"MediaKind={request.MediaKind}；ContentBytes={request.ContentLength}；"
            + $"SectorBytes={sectorBytes}；TrackSectors={request.ContentLength / sectorBytes}。";
    }

    internal static string DescribeRecorder(object recorderObject)
    {
        dynamic recorder = recorderObject;
        return "刻录设备：Vendor=" + Read(() => recorder.VendorId)
            + "；Product=" + Read(() => recorder.ProductId)
            + "；Firmware=" + Read(() => recorder.ProductRevision) + "。";
    }

    internal static string DescribePhysicalMedia(object formatObject)
    {
        dynamic format = formatObject;
        return "盘片物理信息：PhysicalMediaType=" + ReadPhysicalMediaType(formatObject)
            + "；MediaPhysicallyBlank=" + Read(() => format.MediaPhysicallyBlank)
            + "；MediaHeuristicallyBlank=" + Read(() => format.MediaHeuristicallyBlank) + "（写入前检查）。";
    }

    internal static string DescribePreparedMedia(object formatObject)
    {
        dynamic format = formatObject;
        return "CD DAO 介质设置：PhysicalMediaType=" + ReadPhysicalMediaType(formatObject)
            + "；RequestedSectorType=" + Read(() => format.RequestedSectorType)
            + "；BufferUnderrunFreeDisabled=" + Read(() => format.BufferUnderrunFreeDisabled) + "。";
    }

    internal static string DescribeDvdSettings(object formatObject)
    {
        dynamic format = formatObject;
        return "DVD 刻录设置：BufferUnderrunFreeDisabled=" + Read(() => format.BufferUnderrunFreeDisabled)
            + "；ForceMediaToBeClosed=" + Read(() => format.ForceMediaToBeClosed)
            + "；ForceOverwrite=" + Read(() => format.ForceOverwrite) + "。";
    }

    // IMAPI_MEDIA_PHYSICAL_TYPE, not the recorder's supported profiles or the
    // selected project preset. Keep unknown/vendor-defined values in the log.
    // https://learn.microsoft.com/windows/win32/api/imapi2/ne-imapi2-imapi_media_physical_type
    internal static string DescribeMediaType(int type)
        => (type switch
        {
            0x00 => "Unknown",
            0x01 => "CD-ROM/CD-R/CD-RW (unspecified)",
            0x02 => "CD-R",
            0x03 => "CD-RW",
            0x04 => "DVD-ROM",
            0x05 => "DVD-RAM",
            0x06 => "DVD+R",
            0x07 => "DVD+RW",
            0x08 => "DVD+R DL",
            0x09 => "DVD-R",
            0x0A => "DVD-RW",
            0x0B => "DVD-R DL",
            0x0C => "Random-access disk",
            0x0D => "DVD+RW DL",
            0x0E => "HD DVD-ROM",
            0x0F => "HD DVD-R",
            0x10 => "HD DVD-RAM",
            0x11 => "BD-ROM",
            0x12 => "BD-R",
            0x13 => "BD-RE",
            _ => "Unknown",
        })
            + $" ({type})";

    internal static string DescribeSpeed(OpticalWriteSpeed? speed)
        => speed is null ? "auto/fallback"
            : $"{speed.SectorsPerSecond} sectors/s, PureCAV={speed.RotationTypeIsPureCav}";

    internal static void Report(Action<string>? report, Func<string?> describe)
    {
        if (report is null) return;
        try
        {
            if (describe() is { } message) report(message);
        }
        catch { /* Diagnostics must not change writing or hide its original exception. */ }
    }

    private static string ReadPhysicalMediaType(object formatObject)
    {
        dynamic format = formatObject;
        return Read(() =>
        {
            object? value = format.CurrentPhysicalMediaType;
            return value is null ? null : DescribeMediaType(Convert.ToInt32(value, CultureInfo.InvariantCulture));
        });
    }

    private static string Read(Func<object?> read)
    {
        try { return Convert.ToString(read(), CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } value ? value : "unknown"; }
        catch (Exception exception) { return $"unavailable (0x{exception.HResult:X8})"; }
    }
}
