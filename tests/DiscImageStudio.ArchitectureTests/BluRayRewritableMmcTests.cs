using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using DiscImageStudio.Burning;

internal static class BluRayRewritableMmcTests
{
    private const uint FullCount = BluRayRewritableMmcCommands.SingleLayerSectors;

    internal static void Run()
    {
        VerifyFormatAndLayout();
        CheckOptionalWriteProtectionQuery();
        CheckUnformattedWriteProtectionQuery();
        RejectUnsupportedOrProtectedMedia();
        RejectUnverifiedLayout();
        Console.WriteLine("blu-ray-rewritable-mmc: passed (single-layer DI, RENoSA, exact FORMAT UNIT, verified DDS and capacity; no hardware)");
    }

    private static void VerifyFormatAndLayout()
    {
        MediaTransport transport = new() { ReportMappedReadinessErrors = true };
        BluRayRewritableMmcCommands commands = new(transport);
        BluRayRewritableMedia media = commands.ReadMedia();
        Require(!media.IsFormatted && media.CanFormatWithoutSpareArea && !media.IsWriteProtected
            && media.CurrentSectors == FullCount && transport.FormatCount == 0,
            "unformatted preflight exposes the requested format, without changing the disc");
        BluRayRewritableLayout layout = commands.FormatWithoutSpareArea(CancellationToken.None);
        Require(layout == new BluRayRewritableLayout(0x100000, FullCount)
            && transport.FormatCount == 1 && transport.ReadinessChecks == 4,
            "successful FORMAT UNIT waits through the three Windows in-progress HRESULTs before layout verification");
        Require(commands.ReadLayout() == layout && transport.FormatCount == 1,
            "an already verified no-spare disc is read without reformatting");
    }

    private static void CheckOptionalWriteProtectionQuery()
    {
        var unsupportedSense = new MmcCommandResult(BluRayMmcCommands.CommandHasSenseData, [0x72, 5, 0x24, 0], []);
        var unsupportedHResult = new MmcCommandResult(unchecked((int)0xC0AA020E), [], []);
        foreach (MmcCommandResult unsupported in new[] { unsupportedSense, unsupportedHResult })
        {
            MediaTransport transport = new() { WriteProtectionFailure = unsupported };
            BluRayRewritableMmcCommands commands = new(transport);
            Require(commands.ReadMedia().CanFormatWithoutSpareArea && transport.FormatCount == 0,
                "an unsupported C0h query can use affirmative Formattable evidence");
            Require(commands.FormatWithoutSpareArea(CancellationToken.None).SectorCount == FullCount,
                "a drive without the optional DWP query still completes verified formatting");
        }

        MediaTransport inactive = new() { WriteProtectionFailure = unsupportedSense, FormattableCurrent = false };
        Throws<IOException>(() => new BluRayRewritableMmcCommands(inactive).FormatWithoutSpareArea(CancellationToken.None));
        Require(inactive.FormatCount == 0, "an unsupported C0h query without affirmative writable evidence never formats");

        var hardSense = new MmcCommandResult(unchecked((int)0xC0AA020E), [0x72, 7, 0x27, 0], []);
        MediaTransport protectedMedia = new() { WriteProtectionFailure = hardSense };
        Throws<MmcCommandException>(() => new BluRayRewritableMmcCommands(protectedMedia)
            .FormatWithoutSpareArea(CancellationToken.None));
        Require(protectedMedia.FormatCount == 0, "hard write-protection sense overrides a fallback HRESULT");
        Require(!new MmcCommandException(0, new(unchecked((int)0xC0AA0206), [0x72, 3, 0x31, 0], []))
            .IsOperationInProgress, "hard media sense overrides a mapped readiness HRESULT");
    }

    private static void CheckUnformattedWriteProtectionQuery()
    {
        var noFormatSense = new MmcCommandResult(BluRayMmcCommands.CommandHasSenseData,
            [0x70, 0, 3, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0x31, 0, 0, 0, 0, 0], []);
        MediaTransport virgin = new() { UnformattedWriteProtectionFailure = noFormatSense };
        BluRayRewritableMmcCommands commands = new(virgin);
        Require(commands.ReadMedia().CanFormatWithoutSpareArea && virgin.FormatCount == 0,
            "a virgin disc's C0h 03/31/00 uses affirmative Formattable evidence during read-only preflight");
        Require(commands.FormatWithoutSpareArea(CancellationToken.None).SectorCount == FullCount && virgin.FormatCount == 1,
            "the virgin disc formats once and verifies its reported layout");

        MediaTransport inactive = new() { UnformattedWriteProtectionFailure = noFormatSense, FormattableCurrent = false };
        Throws<IOException>(() => new BluRayRewritableMmcCommands(inactive).FormatWithoutSpareArea(CancellationToken.None));
        Require(inactive.FormatCount == 0, "the virgin-disc fallback still requires affirmative writable evidence");

        MediaTransport formatted = new() { Formatted = true, WriteProtectionFailure = noFormatSense };
        Throws<MmcCommandException>(() => new BluRayRewritableMmcCommands(formatted).ReadLayout());
        Require(formatted.FormatCount == 0, "the same C0h medium error is fatal on an already formatted disc");
    }

    private static void RejectUnsupportedOrProtectedMedia()
    {
        ForbidFormat(new() { NoSpareFeature = false });
        ForbidFormat(new() { NoSpareDescriptor = false });
        ForbidFormat(new() { NoSpareDescriptorBlockBytes = 512 });
        ForbidFormat(new() { WriteProtected = true });
        ForbidFormat(new() { LayerCount = 2 });
        ForbidFormat(new() { DiscSizeAndClass = 0x40 });
        ForbidFormat(new() { LastDataZoneAdip = 0x7ffff });
        ForbidFormat(new() { Profile = 0x41 });

        MediaTransport transport = new();
        Throws<OperationCanceledException>(() => new BluRayRewritableMmcCommands(transport)
            .FormatWithoutSpareArea(new CancellationToken(true)));
        Require(transport.FormatCount == 0, "a canceled request never formats");
    }

    private static void ForbidFormat(MediaTransport transport)
    {
        try
        {
            new BluRayRewritableMmcCommands(transport).FormatWithoutSpareArea(CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            Require(transport.FormatCount == 0, "incompatible preflight never formats");
            return;
        }
        throw new InvalidOperationException("BD-RE accepted a format on incompatible media.");
    }

    private static void RejectUnverifiedLayout()
    {
        MediaTransport sparesRemain = new() { ReportSparesAfterFormat = true };
        Throws<InvalidOperationException>(() => new BluRayRewritableMmcCommands(sparesRemain)
            .FormatWithoutSpareArea(CancellationToken.None));
        Require(sparesRemain.FormatCount == 1, "a failed post-format check never tries a second format");

        MediaTransport[] unverifiable =
        [
            new() { Formatted = true, FirstPhysicalSector = 0x120000 },
            new() { Formatted = true, DdsSpareClusters = 1 },
            new() { Formatted = true, ReadCapacityBlockBytes = 512 },
            new() { Formatted = true, DefectManagementActive = true },
            new() { Formatted = true, TruncateDds = true },
        ];
        foreach (MediaTransport transport in unverifiable)
        {
            Throws<IOException>(() => new BluRayRewritableMmcCommands(transport).ReadLayout());
            Require(transport.FormatCount == 0, "unverified mapping is rejected by a read-only operation");
        }
    }

    private sealed class MediaTransport : IMmcCommandTransport
    {
        internal ushort Profile { get; init; } = 0x43;
        internal bool Formatted { get; set; }
        internal bool NoSpareFeature { get; init; } = true;
        internal bool NoSpareDescriptor { get; init; } = true;
        internal bool FormattableCurrent { get; init; } = true;
        internal bool WriteProtected { get; init; }
        internal MmcCommandResult? WriteProtectionFailure { get; init; }
        internal MmcCommandResult? UnformattedWriteProtectionFailure { get; init; }
        internal bool ReportSparesAfterFormat { get; init; }
        internal bool ReportMappedReadinessErrors { get; init; }
        internal bool DefectManagementActive { get; init; }
        internal bool TruncateDds { get; init; }
        internal int LayerCount { get; init; } = 1;
        internal byte DiscSizeAndClass { get; init; } = 2;
        internal uint LastDataZoneAdip { get; init; } = 0x194e7e;
        internal uint FirstPhysicalSector { get; init; } = 0x100000;
        internal int NoSpareDescriptorBlockBytes { get; init; } = 2048;
        internal uint ReadCapacityBlockBytes { get; init; } = 2048;
        internal uint DdsSpareClusters { get; init; }
        internal int FormatCount { get; private set; }
        internal int ReadinessChecks { get; private set; }

        public MmcWriteTransferLimits GetWriteTransferLimits() => new(0xfff, 65536);

        public MmcCommandResult SendData(byte[] cdb, IntPtr buffer, int bufferLength, uint timeoutSeconds)
        {
            Require(cdb.AsSpan().SequenceEqual(new byte[] { 0x04, 0x11, 0, 0, 0, 0 })
                && bufferLength == 12 && timeoutSeconds >= 120 && (buffer.ToInt64() & 0xfff) == 0,
                "one aligned FORMAT UNIT with BD format code 1");
            byte[] format = new byte[bufferLength];
            Marshal.Copy(buffer, format, 0, format.Length);
            Require(format.AsSpan().SequenceEqual(new byte[]
                { 0x00, 0x00, 0x00, 0x08, 0x00, 0xba, 0x74, 0x00, 0xc4, 0x00, 0x08, 0x00 }),
                "exact synchronous format parameters for 31h subtype 00b and 2048-byte sectors");
            FormatCount++;
            Formatted = true;
            return new(0, [], []);
        }

        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            if (cdb[0] == 0)
            {
                ReadinessChecks++;
                return ReportMappedReadinessErrors && ReadinessChecks <= 3
                    ? new(unchecked((int)0xC0AA0204) + ReadinessChecks, [], [])
                    : new(0, [], []);
            }
            if (cdb[0] == 0xad && cdb[7] == 0xc0)
            {
                if (!Formatted && UnformattedWriteProtectionFailure is { } unformattedFailure)
                    return unformattedFailure;
                if (WriteProtectionFailure is { } failure)
                    return failure;
            }
            byte[] result = cdb[0] switch
            {
                0x23 => FormatCapacities(),
                0x25 => ReadCapacity(),
                0x46 => GetConfiguration(cdb),
                0xad => ReadDiscStructure(cdb, allocationLength),
                _ => throw new InvalidOperationException($"Unexpected direct MMC command {cdb[0]:X2}."),
            };
            return new(0, [], result);
        }

        private byte[] FormatCapacities()
        {
            bool withSpares = Formatted && ReportSparesAfterFormat;
            byte[] data = new byte[NoSpareDescriptor ? 28 : 20];
            data[3] = (byte)(data.Length - 4);
            PutUInt32(data, 4, withSpares ? FullCount - 4096 * 32 : FullCount);
            data[8] = Formatted ? (byte)2 : (byte)1;
            PutUInt24(data, 9, withSpares ? 4096 : Formatted ? 0 : 20480);

            // Descriptor 00h for default format precedes optional descriptor 31h.
            PutUInt32(data, 12, FullCount - 12288 * 32);
            PutUInt24(data, 17, 12288);
            if (NoSpareDescriptor)
            {
                PutUInt32(data, 20, FullCount);
                data[24] = 0xc4;
                PutUInt24(data, 25, NoSpareDescriptorBlockBytes);
            }
            return data;
        }

        private byte[] ReadCapacity()
        {
            byte[] data = new byte[8];
            PutUInt32(data, 0, FullCount - 1);
            PutUInt32(data, 4, ReadCapacityBlockBytes);
            return data;
        }

        private byte[] GetConfiguration(byte[] cdb)
        {
            ushort code = BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(2, 2));
            byte[] data = new byte[code == 0 ? 8 : code == 0x23 ? 20 : 16];
            PutUInt32(data, 0, (uint)(data.Length - 4));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6, 2), Profile);
            if (code > 0)
            {
                Require(cdb[1] == 2 && code is 0x23 or 0x24, "a named feature request");
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8, 2), code);
                data[10] = (byte)((code == 0x23 ? FormattableCurrent : DefectManagementActive) ? 1 : 0);
                data[11] = (byte)(data.Length - 12);
                if (code == 0x23 && NoSpareFeature) data[12] = 8;
            }
            return data;
        }

        private byte[] ReadDiscStructure(byte[] cdb, int allocation)
        {
            Require(cdb[1] == 1 && cdb[6] == 0 && BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(8, 2)) == allocation,
                "READ DISC STRUCTURE identifies BD layer zero and the requested response prefix");
            byte format = cdb[7];
            byte[] data = new byte[format == 0x08 && TruncateDds ? 36 : allocation];
            ushort advertised = format switch
            {
                0x00 => 4098,
                0x08 => 2050,
                0xc0 => 6,
                _ => throw new InvalidOperationException($"Unexpected READ DISC STRUCTURE format {format:X2}."),
            };
            BinaryPrimitives.WriteUInt16BigEndian(data, advertised);
            Span<byte> payload = data.AsSpan(4);
            switch (format)
            {
                case 0x00:
                    "DI"u8.CopyTo(payload);
                    payload[2] = 0x81;
                    payload[3] = 8;
                    payload[4] = (byte)LayerCount;
                    "BDW"u8.CopyTo(payload[8..]);
                    payload[11] = DiscSizeAndClass;
                    payload[12] = (byte)((LayerCount << 4) | 4);
                    payload[13] = 1;
                    PutUInt32(payload, 24, 0x20000);
                    PutUInt32(payload, 28, LastDataZoneAdip);
                    break;
                case 0x08 when !TruncateDds:
                    Require(Formatted, "the DDS is read only once the disc is formatted");
                    Require(FormatCount == 0 || ReadinessChecks >= (ReportMappedReadinessErrors ? 4 : 1),
                        "the DDS is read only after post-format readiness");
                    "DS"u8.CopyTo(payload);
                    PutUInt32(payload, 32, FirstPhysicalSector);
                    PutUInt32(payload, 36, FullCount - 1);
                    PutUInt32(payload, 40, DdsSpareClusters);
                    break;
                case 0xc0:
                    payload[0] = WriteProtected ? (byte)2 : (byte)0;
                    break;
            }
            return data;
        }
    }

    private static void PutUInt32(Span<byte> data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);

    private static void PutUInt24(Span<byte> data, int offset, int value)
    {
        data[offset] = (byte)(value >> 16);
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)value;
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} in the BD-RE command test.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"BD-RE MMC: {message}.");
    }
}
