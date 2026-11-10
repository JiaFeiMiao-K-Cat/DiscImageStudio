using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DiscImageStudio.Burning;

internal sealed record OpticalDiscInformation(
    OpticalDiscState State,
    bool IsErasable,
    int Sessions,
    int LastTrackNumber);

internal sealed record OpticalAtipInformation(
    string LeadInTime,
    string LeadOutTime,
    long LeadOutSectors,
    bool IsRewritable);

internal sealed record OpticalTrackInformation(long NextWritableAddress, long FreeSectors);

internal sealed record OpticalBluRayTrackInformation(
    int TrackNumber,
    bool TrackIsBlank,
    bool NextWritableAddressValid,
    uint TrackStartAddress,
    uint NextWritableAddress,
    uint FreeSectors,
    uint TrackSize);

internal static class OpticalDriveMediaParsers
{
    private const int MaximumPlausibleSectors = 100_000_000;
    private const int MaximumPlausibleSpeedKbps = 10_000_000;

    internal static OpticalDiscInformation ParseDiscInformation(byte[] data)
    {
        RequireLength(data, 12, "READ DISC INFORMATION");
        OpticalDiscState state = (data[2] & 3) switch
        {
            0 => OpticalDiscState.Blank,
            1 => OpticalDiscState.Incomplete,
            2 => OpticalDiscState.Complete,
            _ => OpticalDiscState.Other,
        };
        return new(
            state,
            (data[2] & 0x10) != 0,
            data[9] << 8 | data[4],
            data[6] | data[11] << 8);
    }

    internal static OpticalAtipInformation ParseAtip(byte[] data)
    {
        RequireLength(data, 15, "READ TOC/PMA/ATIP");
        int declared = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)) + 2;
        if (declared < 15 || data.Length < Math.Min(64, declared))
            throw new IOException("光驱返回的 CD 预刻槽信息不完整。");
        int leadInMinutes = data[8], leadInSeconds = data[9], leadInFrames = data[10];
        int leadOutMinutes = data[12], leadOutSeconds = data[13], leadOutFrames = data[14];
        if (!IsValidMsf(leadInMinutes, leadInSeconds, leadInFrames)
            || !IsValidMsf(leadOutMinutes, leadOutSeconds, leadOutFrames))
            throw new IOException("光驱返回的 CD 预刻槽地址无效。");
        long leadOutSectors = MsfToSectors(leadOutMinutes, leadOutSeconds, leadOutFrames) - 150;
        if (leadOutSectors <= 0)
            throw new IOException("光驱返回的 CD 引出区地址无效。");
        return new(
            FormatMsf(leadInMinutes, leadInSeconds, leadInFrames),
            FormatMsf(leadOutMinutes, leadOutSeconds, leadOutFrames),
            leadOutSectors,
            (data[6] & 0x40) != 0);
    }

    internal static string? ParseDvdMinusMediaId(byte[] response)
    {
        ReadOnlySpan<byte> payload = GetDiscStructurePayload(response, 31);
        if (payload[16] != 3 || payload[24] != 4) return null;
        return CleanMediaCode(payload.Slice(17, 6), payload.Slice(25, 6));
    }

    internal static string? ParseDvdPlusMediaId(byte[] response)
    {
        ReadOnlySpan<byte> payload = GetDiscStructurePayload(response, 30);
        string? manufacturer = CleanMediaCode(payload.Slice(19, 8));
        string? type = CleanMediaCode(payload.Slice(27, 3));
        return manufacturer is null || type is null ? null : $"{manufacturer} {type}";
    }

    internal static OpticalTrackInformation? ParseTrackInformation(byte[] data)
    {
        RequireLength(data, 20, "READ TRACK INFORMATION");
        if ((data[7] & 1) == 0) return null;
        int nextWritable = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12, 4));
        uint freeSectors = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
        if (nextWritable is < 0 or > MaximumPlausibleSectors || freeSectors > MaximumPlausibleSectors)
            return null;
        return new(nextWritable, freeSectors);
    }

    internal static OpticalBluRayTrackInformation? ParseBluRayTrackInformation(byte[] data)
    {
        // The probe treats a short or inconsistent response as "layout unknown" and
        // reports no writable sectors instead of guessing from partial track data.
        if (data.Length < 36) return null;
        int trackNumber = data[2] | data[32] << 8;
        if (trackNumber <= 0) return null;
        uint nextWritable = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4));
        uint freeSectors = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
        uint trackSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(24, 4));
        if (freeSectors > MaximumPlausibleSectors || trackSize > MaximumPlausibleSectors)
            return null;
        return new(
            trackNumber,
            (data[6] & 0x40) != 0,
            (data[7] & 1) != 0,
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4)),
            nextWritable,
            freeSectors,
            trackSize);
    }

    internal static IReadOnlyList<int> ParseReadSpeeds(byte[] data)
        => ParseSpeeds(data, 4, includeRangeEnd: true);

    internal static IReadOnlyList<int> ParseWriteSpeeds(byte[] data)
        => ParseSpeeds(data, 12, includeRangeEnd: false);

    internal static string? DescribeMediaManufacturer(string? mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId)) return null;
        string normalized = mediaId.Trim().ToUpperInvariant();
        foreach ((string prefix, string manufacturer) in MediaManufacturers)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                return manufacturer;
        }

        return null;
    }

    // Prefixes are longest-first where a shorter prefix is also a known vendor.
    private static readonly (string Prefix, string Manufacturer)[] MediaManufacturers =
    [
        ("RICOHJPN", "Ricoh"),
        ("VERBATIM", "Verbatim"),
        ("MAXELL", "Maxell"),
        ("INFODISC", "Infodisc"),
        ("PRODISC", "Prodisc"),
        ("OPTODISC", "Optodisc"),
        ("PHILIPS", "Philips"),
        ("MBIPG101", "Moser Baer India"),
        ("CMCMAG", "CMC Magnetics"),
        ("LEADDATA", "Lead Data"),
        ("MCC", "Mitsubishi Chemical"),
        ("MKM", "Mitsubishi Kagaku Media"),
        ("TYG", "Taiyo Yuden"),
        ("YUDEN", "Taiyo Yuden"),
        ("RITEK", "Ritek"),
        ("RIDATA", "Ritek"),
        ("CMC", "CMC Magnetics"),
        ("MBI", "Moser Baer India"),
        ("SONY", "Sony"),
        ("TDK", "TDK"),
        ("GSC", "Gigastorage"),
        ("JVC", "JVC"),
        ("SKC", "SKC"),
        ("PRINCO", "Princo"),
        ("ANWELL", "Anwell"),
        ("FUJI", "Fujifilm"),
        ("KODAK", "Kodak"),
        ("IMATION", "Imation"),
        ("MEMOREX", "Memorex"),
        ("PANASONIC", "Panasonic"),
        ("PIONEER", "Pioneer"),
        ("BEALL", "BeAll Developers"),
        ("UME", "Umedisc"),
        ("LONGTEN", "Longten"),
    ];

    private static IReadOnlyList<int> ParseSpeeds(byte[] data, int speedOffset, bool includeRangeEnd)
    {
        if (data.Length < 16) return [];
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
        long available = Math.Min(data.Length, (long)declared + 4);
        int count = (int)Math.Max(0, (available - 8) / 16);
        if (count == 0) return [];
        SortedSet<int> speeds = [];
        for (int index = 0; index < count; index++)
        {
            int descriptor = 8 + index * 16;
            if (descriptor + 16 > available) break;
            AddSpeed(speeds, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(descriptor + speedOffset, 4)));
            if (includeRangeEnd)
                AddSpeed(speeds, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(descriptor + 12, 4)));
        }

        return speeds.ToArray();
    }

    private static void AddSpeed(SortedSet<int> speeds, uint value)
    {
        if (value is > 0 and <= MaximumPlausibleSpeedKbps)
            speeds.Add((int)value);
    }

    private static ReadOnlySpan<byte> GetDiscStructurePayload(byte[] response, int minimumPayload)
    {
        if (response.Length < minimumPayload + 4)
            throw new IOException("光驱返回的介质标识信息不完整。");
        int declared = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0, 2)) + 2;
        if (declared < minimumPayload + 4)
            throw new IOException("光驱返回的介质标识长度无效。");
        return response.AsSpan(4, Math.Min(response.Length - 4, declared - 4));
    }

    private static string? CleanMediaCode(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default)
    {
        StringBuilder builder = new(first.Length + second.Length);
        AppendPrintable(builder, first);
        AppendPrintable(builder, second);
        string value = builder.ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    private static void AppendPrintable(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is >= 0x20 and <= 0x7e)
                builder.Append((char)value);
        }
    }

    private static bool IsValidMsf(int minutes, int seconds, int frames)
        => minutes is >= 0 and < 100 && seconds is >= 0 and < 60 && frames is >= 0 and < 75;

    private static long MsfToSectors(int minutes, int seconds, int frames)
        => (minutes * 60L + seconds) * 75 + frames;

    private static string FormatMsf(int minutes, int seconds, int frames)
        => string.Create(CultureInfo.InvariantCulture, $"{minutes}m{seconds:00}s{frames:00}f");

    private static void RequireLength(byte[] data, int minimum, string command)
    {
        if (data.Length < minimum)
            throw new IOException($"{command} 返回的数据被截断（{data.Length} 字节）。");
    }
}

internal static class OpticalDriveMediaProfiles
{
    internal static OpticalDriveMediaFamily GetFamily(int profile) => profile switch
    {
        0x08 or 0x09 or 0x0a => OpticalDriveMediaFamily.Cd,
        >= 0x10 and <= 0x2f => OpticalDriveMediaFamily.Dvd,
        0x40 or 0x41 or 0x42 or 0x43 or 0x58 => OpticalDriveMediaFamily.BluRay,
        0x50 or 0x51 or 0x52 => OpticalDriveMediaFamily.HdDvd,
        _ => OpticalDriveMediaFamily.Unknown,
    };

    internal static bool IsRecordable(int profile) => profile is
        0x09 or 0x0a or 0x11 or 0x12 or 0x13 or 0x14 or 0x15 or 0x16
        or 0x1a or 0x1b or 0x2a or 0x2b or 0x41 or 0x42 or 0x43 or 0x58
        or 0x51 or 0x52;

    internal static bool PublishesNextWritableAddress(int profile) => profile is
        0x09 or 0x0a or 0x11 or 0x14 or 0x15 or 0x16 or 0x1b or 0x2b or 0x41;

    internal static string Describe(int profile) => profile switch
    {
        0x08 => "CD-ROM",
        0x09 => "CD-R",
        0x0a => "CD-RW",
        0x10 => "DVD-ROM",
        0x11 => "DVD-R",
        0x12 => "DVD-RAM",
        0x13 => "DVD-RW",
        0x14 => "DVD-RW",
        0x15 or 0x16 => "DVD-R DL",
        0x1a => "DVD+RW",
        0x1b => "DVD+R",
        0x2a => "DVD+RW DL",
        0x2b => "DVD+R DL",
        0x40 => "BD-ROM",
        0x41 or 0x42 or 0x58 => "BD-R",
        0x43 => "BD-RE",
        0x50 => "HD DVD-ROM",
        0x51 => "HD DVD-R",
        0x52 => "HD DVD-RW",
        _ => $"0x{profile:X4}",
    };
}
