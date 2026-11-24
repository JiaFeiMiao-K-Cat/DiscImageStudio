using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

internal readonly record struct BluRayRewritableMedia(
    bool IsFormatted, bool CanFormatWithoutSpareArea, bool IsWriteProtected, long CurrentSectors, int SpareClusters);

internal readonly record struct BluRayRewritableLayout(uint FirstPhysicalSector, uint SectorCount);

/// <summary>Preflight and layout verification for direct writes to 120 mm single-layer BD-RE.</summary>
internal sealed class BluRayRewritableMmcCommands(IMmcCommandTransport transport)
{
    internal const uint SingleLayerSectors = 12_219_392;
    private const ushort BdReProfile = 0x0043;
    private const ushort FormattableFeature = 0x0023;
    private const byte NoSpareAreaFormat = 0x31;
    private const int SectorBytes = 2048;
    private const uint FirstDataZoneAdip = 0x00020000;
    private const uint LastDataZoneAdip = 0x00194E7E;

    internal BluRayRewritableMedia ReadMedia() => ReadMedia(out _, out _);

    private BluRayRewritableMedia ReadMedia(out uint firstDataZonePsn, out uint lastDataZonePsn)
    {
        if (new BluRayMmcCommands(transport).ReadCurrentProfile() != BdReProfile)
            throw new InvalidOperationException("当前盘片不是 BD-RE。");

        // ISO/IEC 30192:2021 §15.8.3 identifies diameter, layer count, channel-bit
        // length and pregroove addresses in the physical Disc Information header.
        ReadOnlySpan<byte> di = ReadDiscStructure(0x00, 32);
        uint firstAdip = ReadUInt32(di, 24);
        uint lastAdip = ReadUInt32(di, 28);
        if (!di[..2].SequenceEqual("DI"u8) || !di.Slice(8, 3).SequenceEqual("BDW"u8)
            || (di[3] & 7) != 0 || di[4] != 1 || (di[11] & 0xf0) != 0
            || di[12] != 0x14 || (di[13] & 0x0f) != 1
            || firstAdip != FirstDataZoneAdip || lastAdip != LastDataZoneAdip)
            throw new InvalidOperationException("目前仅支持可确认物理布局的 120 mm 单层 25 GB BD-RE。");

        // ISO/IEC 30192:2021 §17 maps these ADIP addresses to the physical Data Zone.
        firstDataZonePsn = 8 * firstAdip;
        lastDataZonePsn = 8 * lastAdip + 15;

        byte[] capacities = Read([0x23, 0, 0, 0, 0, 0, 0, 1, 0, 0], 256);
        if (capacities.Length < 12 || capacities[3] < 8 || capacities[3] % 8 != 0
            || capacities.Length < 4 + capacities[3])
            throw new IOException("BD-RE 格式容量描述符长度无效。");

        uint currentSectors = ReadUInt32(capacities, 4);
        int spareClusters = ReadUInt24(capacities, 9);
        bool formatted = (capacities[8] & 3) switch
        {
            1 => false,
            2 => true,
            _ => throw new IOException("BD-RE 未返回有效的格式化状态。"),
        };
        // For descriptor type 1 the spare count is a maximum, not an allocation.
        // For type 2 the user zone and already allocated spares partition the Data Zone.
        if (formatted
            ? currentSectors == 0 || (ulong)currentSectors + (uint)spareClusters * 32UL != SingleLayerSectors
            : currentSectors != SingleLayerSectors)
            throw new IOException("BD-RE 格式容量与单层 25 GB 数据区不一致。");

        bool noSpareDescriptor = false;
        for (int offset = 12; offset < 4 + capacities[3]; offset += 8)
        {
            if ((capacities[offset + 4] >> 2) != NoSpareAreaFormat) continue;
            if (ReadUInt32(capacities, offset) == SingleLayerSectors
                && ReadUInt24(capacities, offset + 5) == SectorBytes)
                noSpareDescriptor = true;
        }

        bool writeProtected;
        byte[]? formattable = null;
        try
        {
            // READ DISC STRUCTURE C0h reports all four active write-protection sources.
            writeProtected = (ReadDiscStructure(0xc0, 4)[0] & 0x0f) != 0;
        }
        catch (MmcCommandException exception) when (IsWriteProtectionStatusUnavailable(exception, formatted))
        {
            // C0h is conditional on optional DWP support in the BDA command set.
            // Table 3 also allows MEDIUM FORMAT CORRUPTED for never-formatted BD-RE.
            // A ready BD-RE's Formattable feature is current when writing is enabled.
            formattable = ReadFeature(FormattableFeature);
            if (formattable is not { Length: >= 5 } || (formattable[2] & 1) == 0)
                throw new IOException("光驱未能确认当前 BD-RE 已解除写保护。", exception);
            writeProtected = false;
        }
        bool canFormat = !writeProtected && noSpareDescriptor
            && (formattable ?? ReadFeature(FormattableFeature)) is { Length: >= 5 } feature
            && (feature[2] & 1) != 0 && (feature[4] & 0x08) != 0; // Current, RENoSA
        return new(formatted, canFormat, writeProtected, currentSectors, spareClusters);
    }

    internal BluRayRewritableLayout ReadLayout()
    {
        BluRayRewritableMedia media = ReadMedia(out uint firstDataZonePsn, out uint lastDataZonePsn);
        if (media.IsWriteProtected)
            throw new InvalidOperationException("BD-RE 盘片处于写保护状态。");
        if (!media.IsFormatted || media.SpareClusters != 0)
            throw new InvalidOperationException("绘图刻录需要已验证的 BD-RE 无备用区格式。");

        ReadOnlySpan<byte> dds = ReadDiscStructure(0x08, 60);
        uint firstPsn = ReadUInt32(dds, 32);
        uint lastLsn = ReadUInt32(dds, 36);
        if (!dds[..2].SequenceEqual("DS"u8) || dds[2] != 0
            || ReadUInt32(dds, 40) != 0 || ReadUInt32(dds, 44) != 0 || ReadUInt32(dds, 48) != 0
            || firstPsn != firstDataZonePsn || (ulong)firstPsn + lastLsn != lastDataZonePsn
            || (ulong)lastLsn + 1 != (ulong)media.CurrentSectors)
            throw new IOException("BD-RE 盘片定义结构未确认单层连续无备用区映射，不能刻录绘图。");

        // Check the device-visible logical extent and access capabilities. The BD-RE
        // profile requires Defect Management to be inactive when no spares are allocated.
        byte[] capacity = Read([0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0], 8);
        if (capacity.Length != 8 || ReadUInt32(capacity, 0) != lastLsn || ReadUInt32(capacity, 4) != SectorBytes
            || ReadFeature(0x0024) is { } defects && (defects[2] & 1) != 0)
            throw new IOException("BD-RE 逻辑容量与盘片定义不一致，或光驱仍启用了缺陷重映射。");

        return new(firstPsn, lastLsn + 1);
    }

    internal BluRayRewritableLayout FormatWithoutSpareArea(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BluRayRewritableMedia media = ReadMedia();
        if (media.IsWriteProtected)
            throw new InvalidOperationException("BD-RE 盘片处于写保护状态。");
        if (!media.CanFormatWithoutSpareArea)
            throw new NotSupportedException("光驱和当前 BD-RE 未共同报告无备用区格式化能力（RENoSA／格式 31h）。");

        // MMC BD format code 001b, short format list and type 31h/subtype 00b.
        // Keep IMMED clear so the recorder holds the format command until completion.
        byte[] parameters = [0, 0, 0, 8, 0, 0, 0, 0, NoSpareAreaFormat << 2, 0, 0x08, 0];
        BinaryPrimitives.WriteUInt32BigEndian(parameters.AsSpan(4, 4), SingleLayerSectors);
        using AlignedMmcWriteBuffer buffer = new(parameters.Length, transport.GetWriteTransferLimits());
        Marshal.Copy(parameters, 0, buffer.Pointer, parameters.Length);
        cancellationToken.ThrowIfCancellationRequested();
        MmcCommandResult result = transport.SendData([0x04, 0x11, 0, 0, 0, 0], buffer.Pointer, parameters.Length, 600);
        ThrowIfFailed(0x04, result);

        new BluRayMmcCommands(transport).WaitUntilReady();
        cancellationToken.ThrowIfCancellationRequested();
        return ReadLayout();
    }

    private byte[]? ReadFeature(ushort featureCode)
    {
        const ushort allocation = 264;
        byte[] cdb = [0x46, 2, 0, 0, 0, 0, 0, 0, 0, 0]; // Return only the named feature.
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(2, 2), featureCode);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), allocation);
        byte[] data = Read(cdb, allocation);
        if (data.Length < 8 || ReadUInt32(data, 0) < 4
            || (ulong)ReadUInt32(data, 0) + 4 > (uint)data.Length
            || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2)) != BdReProfile)
            throw new IOException("BD-RE 功能描述符的长度或当前介质类型无效。");
        if (ReadUInt32(data, 0) == 4) return null;
        if (data.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8, 2)) != featureCode
            || ReadUInt32(data, 0) != 8U + data[11])
            throw new IOException($"BD-RE 功能描述符 0x{featureCode:X4} 无效。");
        return data.AsSpan(8, 4 + data[11]).ToArray();
    }

    private ReadOnlySpan<byte> ReadDiscStructure(byte format, ushort payloadBytes)
    {
        ushort allocation = checked((ushort)(4 + payloadBytes));
        byte[] cdb = [0xad, 1, 0, 0, 0, 0, 0, format, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(8, 2), allocation);
        byte[] data = Read(cdb, allocation);
        if (data.Length < allocation || BinaryPrimitives.ReadUInt16BigEndian(data) < allocation - 2)
            throw new IOException($"BD-RE 盘片结构 0x{format:X2} 返回的数据被截断。");
        return data.AsSpan(4, payloadBytes);
    }

    private byte[] Read(byte[] cdb, int allocation)
    {
        MmcCommandResult result = transport.Execute(cdb, allocation, 30);
        ThrowIfFailed(cdb[0], result);
        return result.Data;
    }

    private static void ThrowIfFailed(byte operation, MmcCommandResult result)
    {
        if (result.HResult < 0 || result.HResult == BluRayMmcCommands.CommandHasSenseData)
            throw new MmcCommandException(operation, result);
    }

    private static bool IsWriteProtectionStatusUnavailable(MmcCommandException exception, bool formatted)
        => exception.HasValidSense
            ? exception.AdditionalSenseQualifier == 0
                && ((exception.SenseKey == 5 && exception.AdditionalSenseCode is 0x20 or 0x24)
                    || (!formatted && exception.SenseKey == 3 && exception.AdditionalSenseCode == 0x31))
            : exception.HResult == unchecked((int)0xC0AA020E); // E_IMAPI_RECORDER_DVD_STRUCTURE_NOT_PRESENT

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));

    private static int ReadUInt24(ReadOnlySpan<byte> data, int offset)
        => (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
}
