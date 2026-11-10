using System.Buffers.Binary;
using System.IO;
using System.Text;
using DiscImageStudio.Burning;

internal static class OpticalDriveProbeTests
{
    internal static void Run()
    {
        TestCdAtipProbe();
        TestDvdMediaIdProbe();
        TestDvdPlusMediaIdProbe();
        TestPressedCdHidesAtip();
        TestNoMedia();
        TestEmptyDriveProfileFailure();
        TestUnknownProfileUsesAtip();
        TestUnknownProfileWithoutAtip();
        TestBluRayProbe();
        TestBluRayLayoutGate();
        TestParserEdgeCases();
        Console.WriteLine("optical-drive-probe: passed (CD ATIP, DVD media ID, pressed CD, no media, Blu-ray, read-only commands)");
    }

    private static void TestCdAtipProbe()
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Ok(Configuration(0x09)));
        transport.Expect(ReadCdb(0x51, 64), 64, 30, Ok(DiscInformation(0, erasable: false, 0, 1)));
        transport.Expect(AtipCdb(), 64, 30, Ok(Atip(97, 15, 17, 79, 59, 74, rewritable: false)));
        transport.Expect(TrackCdb(1), 48, 30, Ok(TrackInformation(0, 359_847)));
        transport.Expect(PerformanceCdb(read: true), 136, 30, Ok(ReadPerformance(8468)));
        transport.Expect(PerformanceCdb(read: false), 136, 30, Ok(WritePerformance(4234)));

        OpticalDriveMediaInfo media = Probe(transport, (359_849, 359_843));
        Require(media.Family == OpticalDriveMediaFamily.Cd && media.Profile == 0x09
            && media.ProfileName == "CD-R", "CD-R profile is identified");
        Require(media.State == OpticalDiscState.Blank && !media.IsErasable && media.Sessions == 0,
            "blank CD-R state, erasable flag and session count are parsed");
        Require(media.MediaId is null && media.MediaManufacturer is null,
            "CD ATIP lead-in is not promoted to a media ID");
        Require(media.Atip is
            {
                LeadInTime: "97m15s17f",
                LeadOutTime: "79m59s74f",
                LeadOutSectors: 359_849,
                IsRewritable: false,
            }, "ATIP lead-in and lead-out are exposed without duplication");
        Require(media.TotalSectors == 359_849 && media.UsableSectors == 359_847
            && media.FreeSectors == 359_843, "CD capacity uses ATIP geometry and the application writable limit");
        Require(media.NextWritableAddress == 0, "next writable address is read from track information");
        Require(media.ReadSpeedsKbps.SequenceEqual([8468]), "nominal read performance is parsed");
        Require(media.WriteSpeedsKbps.SequenceEqual([4234]), "write performance is parsed for recordable media");
        Require(transport.Commands.Any(cdb => cdb[0] == 0xac && cdb[1] == 0x14
                && BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2, 4)) == 1),
            "write performance uses data type 0x14 with starting LBA 1");
        RequireReadOnlyCommands(transport);
        Require(transport.Remaining == 0, "CD probe issues no unexpected command");
    }

    private static void TestDvdMediaIdProbe()
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Ok(Configuration(0x11)));
        transport.Expect(ReadCdb(0x51, 64), 64, 30, Ok(DiscInformation(0, erasable: false, 0, 1)));
        transport.Expect(DvdStructureCdb(0x0e, 35), 35, 30,
            Ok(DiscStructure(DvdMinusPayload("MCC 03", "RG20"))));
        transport.Expect(TrackCdb(1), 48, 30, Ok(TrackInformation(32, 2_297_888)));
        transport.Expect(PerformanceCdb(read: true), 136, 30, Ok(ReadPerformance(22160)));
        transport.Expect(PerformanceCdb(read: false), 136, 30, Ok(WritePerformance(11080)));

        OpticalDriveMediaInfo media = Probe(transport, (2_298_496, 2_297_888));
        Require(media.MediaId == "MCC 03RG20" && media.MediaManufacturer == "Mitsubishi Chemical",
            "DVD-R media ID and known manufacturer are resolved");
        Require(media.TotalSectors == 2_298_496 && media.UsableSectors == 2_297_888
            && media.FreeSectors == 2_297_888, "DVD capacity keeps geometry and free sectors separate");
        Require(media.NextWritableAddress == 32, "DVD next writable address is parsed");
        Require(media.ProfileName == "DVD-R" && media.Family == OpticalDriveMediaFamily.Dvd,
            "DVD-R profile is identified");
        RequireReadOnlyCommands(transport);
        Require(transport.Remaining == 0, "DVD probe issues no unexpected command");
    }

    private static void TestDvdPlusMediaIdProbe()
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Ok(Configuration(0x1b)));
        transport.Expect(ReadCdb(0x51, 64), 64, 30, Ok(DiscInformation(0, erasable: false, 0, 1)));
        transport.Expect(DvdStructureCdb(0x11, 34), 34, 30,
            Ok(DiscStructure(DvdPlusPayload("MBIPG101", "R05"))));
        transport.Expect(TrackCdb(1), 48, 30, Ok(TrackInformation(0, 2_297_888)));
        transport.Expect(PerformanceCdb(read: true), 136, 30, Ok(ReadPerformance(22160)));
        transport.Expect(PerformanceCdb(read: false), 136, 30, Ok(WritePerformance(11080)));

        OpticalDriveMediaInfo media = Probe(transport, (2_298_496, 2_297_888));
        Require(media.MediaId == "MBIPG101 R05" && media.MediaManufacturer == "Moser Baer India",
            "DVD+R media ID uses the full three-byte media type");
        Require(media.ProfileName == "DVD+R" && media.Family == OpticalDriveMediaFamily.Dvd,
            "DVD+R profile is identified");
        RequireReadOnlyCommands(transport);
        Require(transport.Remaining == 0, "DVD+R probe issues no unexpected command");
    }

    private static void TestPressedCdHidesAtip()
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Ok(Configuration(0x08)));
        transport.Expect(ReadCdb(0x51, 64), 64, 30, Ok(DiscInformation(2, erasable: false, 1, 1)));
        transport.Expect(AtipCdb(), 64, 30, Failed(5, 0x24, 0));
        transport.Expect(PerformanceCdb(read: true), 136, 30, Ok(ReadPerformance(4234)));

        OpticalDriveMediaInfo media = Probe(transport, (204_800, 0));
        Require(media.Family == OpticalDriveMediaFamily.Cd && media.ProfileName == "CD-ROM",
            "pressed CD keeps its profile");
        Require(media.State == OpticalDiscState.Complete && media.Sessions == 1,
            "pressed CD finalized state is reported");
        Require(media.Atip is null && media.MediaId is null && media.MediaManufacturer is null,
            "pressed CD hides ATIP and MID instead of fabricating values");
        Require(media.TotalSectors == 204_800 && media.UsableSectors == 0,
            "pressed CD reports geometry with no writable capacity");
        Require(media.ReadSpeedsKbps.SequenceEqual([4234]) && media.WriteSpeedsKbps.Count == 0,
            "pressed CD reads speed but does not request write performance");
        RequireReadOnlyCommands(transport);
        Require(transport.Remaining == 0, "pressed CD probe issues no unexpected command");
    }

    private static void TestNoMedia()
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Ok(Configuration(0)));
        transport.Expect(ReadCdb(0x51, 64), 64, 30, Failed(2, 0x3a, 0));
        bool capacityRequested = false;
        OpticalDriveMediaInfo? media = ImapiOpticalDriveProbe.ReadMedia(
            transport, () => { capacityRequested = true; return (1, 1); }, CancellationToken.None);
        Require(media is null, "medium-not-present sense data means an empty drive");
        Require(!capacityRequested, "an empty drive does not query capacity or issue further commands");
        Require(transport.Remaining == 0, "no-media probe issues no unexpected command");
    }

    private static void TestEmptyDriveProfileFailure()
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Failed(2, 0x3a, 0));
        OpticalDriveMediaInfo? media = ImapiOpticalDriveProbe.ReadMedia(
            transport, () => throw new InvalidOperationException("capacity must not be read"), CancellationToken.None);
        Require(media is null, "medium-not-present during GET CONFIGURATION means an empty drive");
        Require(transport.Remaining == 0, "an empty drive stops after the profile query");
    }

    private static void TestUnknownProfileUsesAtip()
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Ok(Configuration(0)));
        transport.Expect(ReadCdb(0x51, 64), 64, 30, Ok(DiscInformation(0, erasable: true, 0, 1)));
        transport.Expect(AtipCdb(), 64, 30, Ok(Atip(97, 15, 17, 79, 59, 74, rewritable: true)));
        transport.Expect(TrackCdb(1), 48, 30, Ok(TrackInformation(0, 359_847)));
        transport.Expect(PerformanceCdb(read: true), 136, 30, Ok(ReadPerformance()));
        transport.Expect(PerformanceCdb(read: false), 136, 30, Ok(WritePerformance(4234)));

        OpticalDriveMediaInfo media = Probe(transport, (359_849, 359_843));
        Require(media.Profile == 0 && media.Family == OpticalDriveMediaFamily.Cd
            && media.ProfileName == "CD-RW", "ATIP can identify CD media when the profile is unknown");
        Require(media.Atip?.IsRewritable == true && media.Atip.LeadInTime == "97m15s17f"
            && media.MediaId is null,
            "rewritable ATIP media keeps its lead-in stamp but fabricates no media ID");
        Require(media.WriteSpeedsKbps.SequenceEqual([4234]),
            "ATIP on an unknown profile still requests write performance");
        Require(transport.Remaining == 0, "unknown-profile probe issues no unexpected command");
    }

    private static void TestUnknownProfileWithoutAtip()
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Ok(Configuration(0)));
        transport.Expect(ReadCdb(0x51, 64), 64, 30, Ok(DiscInformation(0, erasable: false, 0, 1)));
        transport.Expect(AtipCdb(), 64, 30, Failed(5, 0x24, 0));
        transport.Expect(PerformanceCdb(read: true), 136, 30, Ok(ReadPerformance(4234)));

        OpticalDriveMediaInfo media = Probe(transport, (359_849, 359_843));
        Require(media.Family == OpticalDriveMediaFamily.Unknown && media.Atip is null
            && media.ProfileName == "0x0000", "an unidentified medium stays unknown");
        Require(media.UsableSectors == 0,
            "an unidentified medium never inherits reported free sectors as writable");
        Require(media.WriteSpeedsKbps.Count == 0,
            "an unidentified medium does not request write performance");
        Require(transport.Remaining == 0, "ATIP-less unknown-profile probe issues no unexpected command");
    }

    private static void TestBluRayProbe()
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Ok(Configuration(0x41)));
        transport.Expect(ReadCdb(0x51, 64), 64, 30, Ok(DiscInformation(0, erasable: false, 0, 1)));
        transport.Expect(TrackCdb(1), 48, 30,
            Ok(TrackInformation(0, 12_219_392, trackSize: 12_219_392)));
        transport.Expect(ReadCdb(0x23, 256), 256, 30, Ok(FormatCapacity(12_219_392, 1)));
        transport.Expect(PerformanceCdb(read: true), 136, 30, Ok(ReadPerformance(11080)));
        transport.Expect(PerformanceCdb(read: false), 136, 30, Ok(WritePerformance(4495)));

        OpticalDriveMediaInfo media = Probe(transport, (12_219_392, 12_219_392));
        Require(media.Family == OpticalDriveMediaFamily.BluRay && media.ProfileName == "BD-R",
            "BD-R profile is identified");
        Require(media.MediaId is null && media.Atip is null,
            "Blu-ray does not show a fabricated MID or ATIP data");
        Require(media.TotalSectors == 12_219_392 && media.UsableSectors == 12_219_392
            && media.FreeSectors == 12_219_392, "blank BD-R uses format capacity and free blocks");
        RequireReadOnlyCommands(transport);
        Require(transport.Remaining == 0, "Blu-ray probe issues no unexpected command");
    }

    private static void TestBluRayLayoutGate()
    {
        RequireBluRayUsable(profile: 0x43, formatStatus: 1, spareClusters: 0,
            trackSize: 12_219_392, blocks: 12_219_392, expectedUsable: 0,
            "BD-RE never advertises writable sectors for the sequential BD-R workflow");
        RequireBluRayUsable(profile: 0x41, formatStatus: 2, spareClusters: 0,
            trackSize: 12_219_392, blocks: 12_219_392, expectedUsable: 0,
            "formatted BD-R does not advertise writable sectors");
        RequireBluRayUsable(profile: 0x41, formatStatus: 1, spareClusters: 0,
            trackSize: 12_219_360, blocks: 12_219_392, expectedUsable: 0,
            "BD-R with a short track does not advertise writable sectors");
        RequireBluRayUsable(profile: 0x41, formatStatus: 2, spareClusters: 4096,
            trackSize: 12_219_392, blocks: 12_219_392, expectedUsable: 0,
            "BD-R with allocated spare areas does not advertise writable sectors");
    }

    private static void RequireBluRayUsable(
        int profile,
        int formatStatus,
        int spareClusters,
        int trackSize,
        uint blocks,
        long expectedUsable,
        string message)
    {
        FakeTransport transport = new();
        transport.Expect(ReadCdb(0x46, 8), 8, 30, Ok(Configuration(profile)));
        transport.Expect(ReadCdb(0x51, 64), 64, 30, Ok(DiscInformation(0, erasable: false, 0, 1)));
        if (profile == 0x41)
        {
            transport.Expect(TrackCdb(1), 48, 30,
                Ok(TrackInformation(0, (int)blocks, trackSize: trackSize)));
        }

        transport.Expect(ReadCdb(0x23, 256), 256, 30,
            Ok(FormatCapacity(blocks, formatStatus, spareClusters)));
        transport.Expect(PerformanceCdb(read: true), 136, 30, Ok(ReadPerformance(11080)));
        transport.Expect(PerformanceCdb(read: false), 136, 30, Ok(WritePerformance(4495)));

        OpticalDriveMediaInfo media = Probe(transport, (blocks, blocks));
        Require(media.UsableSectors == expectedUsable, message);
        Require(transport.Remaining == 0, "Blu-ray layout probe issues no unexpected command");
    }

    private static void TestParserEdgeCases()
    {
        byte[] disc = new byte[64];
        disc[2] = 0x10 | 1;
        disc[4] = 2;
        disc[6] = 0x34;
        disc[9] = 1;
        disc[11] = 0x12;
        OpticalDiscInformation discInfo = OpticalDriveMediaParsers.ParseDiscInformation(disc);
        Require(discInfo.State == OpticalDiscState.Incomplete && discInfo.IsErasable
            && discInfo.Sessions == 258 && discInfo.LastTrackNumber == 0x1234,
            "disc information uses high bytes for sessions and 16-bit track numbers");

        Throws<IOException>(
            () => OpticalDriveMediaParsers.ParseAtip(Atip(97, 15, 17, 79, 59, 75, false)),
            "invalid ATIP frames are rejected");

        byte[] badMarkers = DvdMinusPayload("MCC 03", "RG20");
        badMarkers[16] = 0;
        Require(OpticalDriveMediaParsers.ParseDvdMinusMediaId(DiscStructure(badMarkers)) is null,
            "DVD-R structure without the expected sixpack markers yields no ID");
        Require(OpticalDriveMediaParsers.ParseDvdPlusMediaId(
            DiscStructure(DvdPlusPayload("MBIPG101", "R05"))) == "MBIPG101 R05",
            "DVD+R media ID combines the ADIP manufacturer and media type");

        Require(OpticalDriveMediaParsers.ParseTrackInformation(
            TrackInformation(0, 0, nwaValid: false)) is null, "invalid NWA hides track information");
        Require(OpticalDriveMediaParsers.ParseTrackInformation(
            TrackInformation(0, 0x7fffffff)) is null, "implausible free sectors hide track information");

        Require(OpticalDriveMediaParsers.ParseReadSpeeds(
                ReadPerformance(8468, 7056, 8468)).SequenceEqual([7056, 8468]),
            "performance speeds are distinct and sorted");
        byte[] truncated = ReadPerformance(8468);
        BinaryPrimitives.WriteUInt32BigEndian(truncated, 1000);
        Require(OpticalDriveMediaParsers.ParseReadSpeeds(truncated).SequenceEqual([8468]),
            "performance parsing never reads beyond the returned payload");

        Require(OpticalDriveMediaParsers.DescribeMediaManufacturer("MCC 03RG20") == "Mitsubishi Chemical"
            && OpticalDriveMediaParsers.DescribeMediaManufacturer("MBIPG101 R05") == "Moser Baer India"
            && OpticalDriveMediaParsers.DescribeMediaManufacturer("UNKNOWN") is null,
            "known media code prefixes map to manufacturers without guessing");

        Require(OpticalDriveMediaProfiles.GetFamily(0x08) == OpticalDriveMediaFamily.Cd
            && OpticalDriveMediaProfiles.GetFamily(0x11) == OpticalDriveMediaFamily.Dvd
            && OpticalDriveMediaProfiles.GetFamily(0x41) == OpticalDriveMediaFamily.BluRay
            && OpticalDriveMediaProfiles.GetFamily(0x43) == OpticalDriveMediaFamily.BluRay
            && OpticalDriveMediaProfiles.GetFamily(0x51) == OpticalDriveMediaFamily.HdDvd
            && OpticalDriveMediaProfiles.Describe(0x2b) == "DVD+R DL"
            && OpticalDriveMediaProfiles.Describe(0x43) == "BD-RE"
            && OpticalDriveMediaProfiles.Describe(0x50) == "HD DVD-ROM",
            "profile families and labels cover CD, DVD, Blu-ray and HD DVD");
    }

    private static OpticalDriveMediaInfo Probe(FakeTransport transport, (long Total, long Free)? capacity)
        => ImapiOpticalDriveProbe.ReadMedia(transport, () => capacity, CancellationToken.None)
            ?? throw new InvalidOperationException("Expected media information.");

    private static void RequireReadOnlyCommands(FakeTransport transport)
    {
        foreach (byte[] cdb in transport.Commands)
        {
            Require(cdb[0] is not (0x04 or 0x2a or 0x35 or 0x55 or 0x5b or 0xa1 or 0xaa),
                $"probe must not issue modifying command 0x{cdb[0]:X2}");
        }
    }

    private static byte[] Configuration(int profile)
    {
        byte[] data = new byte[8];
        data[3] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6, 2), (ushort)profile);
        return data;
    }

    private static byte[] DiscInformation(int state, bool erasable, int sessions, int lastTrack)
    {
        byte[] data = new byte[64];
        data[1] = 62;
        data[2] = (byte)(state | (erasable ? 0x10 : 0));
        data[4] = (byte)sessions;
        data[6] = (byte)lastTrack;
        data[9] = (byte)(sessions >> 8);
        data[11] = (byte)(lastTrack >> 8);
        return data;
    }

    private static byte[] Atip(
        int leadInMinutes,
        int leadInSeconds,
        int leadInFrames,
        int leadOutMinutes,
        int leadOutSeconds,
        int leadOutFrames,
        bool rewritable)
    {
        byte[] data = new byte[64];
        data[1] = 62;
        data[6] = rewritable ? (byte)0x40 : (byte)0;
        data[8] = (byte)leadInMinutes;
        data[9] = (byte)leadInSeconds;
        data[10] = (byte)leadInFrames;
        data[12] = (byte)leadOutMinutes;
        data[13] = (byte)leadOutSeconds;
        data[14] = (byte)leadOutFrames;
        return data;
    }

    private static byte[] TrackInformation(int nextWritable, int freeSectors, bool nwaValid = true,
        int trackNumber = 1, int trackStart = 0, int trackSize = 0, bool trackBlank = true)
    {
        byte[] data = new byte[48];
        data[1] = 46;
        data[2] = (byte)trackNumber;
        data[6] = trackBlank ? (byte)0x40 : (byte)0;
        data[7] = nwaValid ? (byte)1 : (byte)0;
        data[32] = (byte)(trackNumber >> 8);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8, 4), trackStart);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12, 4), nextWritable);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16, 4), (uint)freeSectors);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(24, 4), trackSize);
        return data;
    }

    private static byte[] ReadPerformance(params int[] speeds)
    {
        byte[] data = new byte[8 + speeds.Length * 16];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)(4 + speeds.Length * 16));
        for (int index = 0; index < speeds.Length; index++)
        {
            int descriptor = 8 + index * 16;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(descriptor + 4, 4), (uint)speeds[index]);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(descriptor + 12, 4), (uint)speeds[index]);
        }

        return data;
    }

    private static byte[] WritePerformance(params int[] speeds)
    {
        byte[] data = new byte[8 + speeds.Length * 16];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)(4 + speeds.Length * 16));
        for (int index = 0; index < speeds.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(
                data.AsSpan(8 + index * 16 + 12, 4), (uint)speeds[index]);
        }

        return data;
    }

    private static byte[] DvdMinusPayload(string manufacturerPart, string mediaTypePart)
    {
        byte[] payload = new byte[31];
        payload[16] = 3;
        Encoding.ASCII.GetBytes(manufacturerPart).CopyTo(payload, 17);
        payload[24] = 4;
        Encoding.ASCII.GetBytes(mediaTypePart).CopyTo(payload, 25);
        return payload;
    }

    private static byte[] DvdPlusPayload(string manufacturer, string mediaType)
    {
        byte[] payload = new byte[30];
        Encoding.ASCII.GetBytes(manufacturer).CopyTo(payload, 19);
        Encoding.ASCII.GetBytes(mediaType).CopyTo(payload, 27);
        return payload;
    }

    private static byte[] DiscStructure(byte[] payload)
    {
        byte[] data = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)(payload.Length + 2));
        payload.CopyTo(data, 4);
        return data;
    }

    private static byte[] FormatCapacity(uint blocks, int formatStatus, int spareClusters = 0)
    {
        byte[] data = new byte[12];
        data[3] = 8;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), blocks);
        data[8] = (byte)formatStatus;
        data[9] = (byte)(spareClusters >> 16);
        data[10] = (byte)(spareClusters >> 8);
        data[11] = (byte)spareClusters;
        return data;
    }

    private static byte[] ReadCdb(byte opcode, ushort allocationLength)
    {
        byte[] cdb = new byte[10];
        cdb[0] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), allocationLength);
        return cdb;
    }

    private static byte[] AtipCdb() => [0x43, 0x02, 0x04, 0, 0, 0, 0, 0, 64, 0];

    private static byte[] TrackCdb(int trackNumber)
    {
        byte[] cdb = ReadCdb(0x52, 48);
        cdb[1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), (uint)trackNumber);
        return cdb;
    }

    private static byte[] DvdStructureCdb(byte format, ushort allocationLength)
    {
        byte[] cdb = new byte[12];
        cdb[0] = 0xad;
        cdb[7] = format;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(8, 2), allocationLength);
        return cdb;
    }

    private static byte[] PerformanceCdb(bool read)
    {
        byte[] cdb = new byte[12];
        cdb[0] = 0xac;
        cdb[1] = read ? (byte)0x10 : (byte)0x14;
        if (!read) BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(8, 2), 8);
        cdb[10] = read ? (byte)0 : (byte)0x03;
        return cdb;
    }

    private static MmcCommandResult Ok(byte[] data) => new(0, [], data);

    private static MmcCommandResult Failed(int senseKey, int asc, int ascq)
    {
        byte[] sense = new byte[18];
        sense[0] = 0x70;
        sense[2] = (byte)senseKey;
        sense[12] = (byte)asc;
        sense[13] = (byte)ascq;
        return new(BluRayMmcCommands.CommandHasSenseData, sense, []);
    }

    private sealed class FakeTransport : IMmcCommandTransport
    {
        private readonly Queue<(byte[] Cdb, int Allocation, uint Timeout, MmcCommandResult Result)> _expected = new();
        internal List<byte[]> Commands { get; } = [];
        internal int Remaining => _expected.Count;

        internal void Expect(byte[] cdb, int allocation, uint timeout, MmcCommandResult? result = null)
            => _expected.Enqueue((cdb, allocation, timeout, result ?? Ok([])));

        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            Require(_expected.Count > 0, $"unexpected MMC command 0x{cdb[0]:X2}");
            var expected = _expected.Dequeue();
            Require(cdb.AsSpan().SequenceEqual(expected.Cdb)
                && allocationLength == expected.Allocation && timeoutSeconds == expected.Timeout,
                $"MMC command 0x{cdb[0]:X2} matches the expected CDB, allocation and timeout");
            Commands.Add(cdb.ToArray());
            return expected.Result;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }
}
