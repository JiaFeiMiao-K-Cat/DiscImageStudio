using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

public sealed class ImapiOpticalDriveProbe : IOpticalDriveProbe
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OpticalDriveProbeResult> ProbeAsync(
        string? preferredDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ImapiCom.RunOnStaThread(
                () => Probe(preferredDeviceId, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static OpticalDriveProbeResult Probe(
        string? preferredDeviceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OpticalDriveInfo> drives = EnumerateDrives(cancellationToken);
        if (drives.Count == 0)
        {
            return new([], null, null);
        }

        OpticalDriveInfo drive = drives.FirstOrDefault(candidate => candidate.Id == preferredDeviceId)
            ?? drives[0];
        return new(drives, drive, ReadMedia(drive.Id, cancellationToken));
    }

    private static IReadOnlyList<OpticalDriveInfo> EnumerateDrives(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic master = ImapiCom.CreateComObject("IMAPI2.MsftDiscMaster2");
        try
        {
            if (!(bool)master.IsSupportedEnvironment)
            {
                return [];
            }

            int count = (int)master.Count;
            List<OpticalDriveInfo> drives = new(count);
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = (string)master.Item(index);
                dynamic? recorder = null;
                try
                {
                    recorder = ImapiCom.CreateRecorder(id);
                    drives.Add(new OpticalDriveInfo(
                        id,
                        ReadText(recorder.VendorId),
                        ReadText(recorder.ProductId),
                        ReadText(recorder.ProductRevision),
                        ImapiCom.ReadStrings(recorder.VolumePathNames)));
                }
                catch (COMException)
                {
                    // The device disappeared between enumeration and property access.
                }
                finally
                {
                    if (recorder is not null) ImapiCom.ReleaseComObject((object)recorder);
                }
            }

            return drives;
        }
        finally
        {
            ImapiCom.ReleaseComObject((object)master);
        }
    }

    private static OpticalDriveMediaInfo? ReadMedia(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic recorder = ImapiCom.CreateRecorder(deviceId);
        try
        {
            var transport = new ImapiMmcCommandTransport((object)recorder);
            return ReadMedia(transport, () => ReadReportedCapacity(recorder), cancellationToken);
        }
        finally
        {
            ImapiCom.ReleaseComObject((object)recorder);
        }
    }

    internal static OpticalDriveMediaInfo? ReadMedia(
        IMmcCommandTransport transport,
        Func<(long Total, long Free)?> reportedCapacityProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int profile;
        try
        {
            profile = ReadCurrentProfile(transport);
        }
        catch (MmcCommandException exception) when (exception.IsMediumNotPresent)
        {
            // Some recorders reject GET CONFIGURATION instead of READ DISC INFORMATION
            // when the tray is empty; both mean there is no medium to describe.
            return null;
        }

        byte[] discData;
        try
        {
            discData = Read(transport, 0x51, 64);
        }
        catch (MmcCommandException) when (profile == 0)
        {
            // A few drives answer profile 0000h and ILLEGAL REQUEST when empty.
            return null;
        }
        catch (MmcCommandException exception) when (exception.IsMediumNotPresent)
        {
            return null;
        }

        OpticalDiscInformation disc = OpticalDriveMediaParsers.ParseDiscInformation(discData);
        cancellationToken.ThrowIfCancellationRequested();
        OpticalDriveMediaFamily family = OpticalDriveMediaProfiles.GetFamily(profile);
        string profileName = OpticalDriveMediaProfiles.Describe(profile);

        CdAtipInfo? atip = null;
        if (family == OpticalDriveMediaFamily.Cd || profile == 0)
        {
            try
            {
                OpticalAtipInformation parsed =
                    OpticalDriveMediaParsers.ParseAtip(ReadAtip(transport));
                atip = new CdAtipInfo(
                    parsed.LeadInTime, parsed.LeadOutTime, parsed.LeadOutSectors, parsed.IsRewritable);
                if (profile == 0)
                {
                    family = OpticalDriveMediaFamily.Cd;
                    profileName = parsed.IsRewritable ? "CD-RW" : "CD-R";
                }
            }
            catch (Exception exception) when (exception is IOException or COMException)
            {
                // Pressed CDs and some recorders do not expose ATIP data.
            }
        }

        // ATIP carries a lead-in/lead-out stamp, not a media ID; only DVD structures do.
        string? mediaId = null;
        if (family == OpticalDriveMediaFamily.Dvd
            && (profile is 0x11 or 0x13 or 0x14 or 0x15 or 0x16 or 0x1a or 0x1b or 0x2a or 0x2b))
        {
            try
            {
                mediaId = ReadDvdMediaId(transport, profile);
            }
            catch (Exception exception) when (exception is IOException or COMException)
            {
                // Media ID layout differs by book type and some drives omit it.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        (long Total, long Free)? reportedCapacity = reportedCapacityProvider();
        long? nextWritable = null;
        long? trackFree = null;
        byte[]? trackData = null;
        if (OpticalDriveMediaProfiles.PublishesNextWritableAddress(profile)
            || (profile == 0 && atip is not null))
        {
            try
            {
                trackData = ReadTrack(
                    transport, disc.LastTrackNumber > 0 ? disc.LastTrackNumber : 1);
                OpticalTrackInformation? track =
                    OpticalDriveMediaParsers.ParseTrackInformation(trackData);
                if (track is not null)
                {
                    nextWritable = track.NextWritableAddress;
                    trackFree = track.FreeSectors;
                }
            }
            catch (Exception exception) when (exception is IOException or COMException)
            {
                // The next writable address is optional detail information.
            }
        }

        long? total = reportedCapacity?.Total;
        long? free = reportedCapacity?.Free;
        long? usable;
        if (family == OpticalDriveMediaFamily.BluRay)
        {
            try
            {
                (uint blocks, int status, int spareClusters) =
                    new BluRayMmcCommands(transport).ReadFormatCapacity();
                if (blocks > 0) total = blocks;
                if (trackFree is not null) free = trackFree;
                usable = ReadBluRayUsableSectors(
                    profile, disc.State, blocks, status, spareClusters, trackData);
            }
            catch (Exception exception) when (exception is IOException or COMException)
            {
                if (trackFree is not null) free = trackFree;
                // The sequential writer cannot verify the physical layout without
                // READ FORMAT CAPACITIES, so no writable capacity is advertised.
                usable = 0;
            }
        }
        else if (family == OpticalDriveMediaFamily.Cd && atip is not null)
        {
            total = atip.LeadOutSectors;
            usable = disc.State == OpticalDiscState.Blank
                ? Math.Max(0, atip.LeadOutSectors - 2) : 0;
        }
        else if (family == OpticalDriveMediaFamily.Unknown)
        {
            // An unidentified medium must not inherit IMAPI's free-sector estimate
            // as an application writable limit.
            usable = 0;
        }
        else
        {
            usable = ClampFree(free, total);
        }

        IReadOnlyList<int> readSpeeds = [];
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            readSpeeds = OpticalDriveMediaParsers.ParseReadSpeeds(
                ReadPerformance(transport, read: true));
        }
        catch (Exception exception) when (exception is IOException or COMException)
        {
            // Not every recorder implements nominal read performance.
        }

        IReadOnlyList<int> writeSpeeds = [];
        // ATIP only exists on recordable CD-R/RW, so profile 0000h with ATIP is recordable
        // even though its numeric profile is not in the fixed profile list.
        if (OpticalDriveMediaProfiles.IsRecordable(profile)
            || (profile == 0 && atip is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                writeSpeeds = OpticalDriveMediaParsers.ParseWriteSpeeds(
                    ReadPerformance(transport, read: false));
            }
            catch (Exception exception) when (exception is IOException or COMException)
            {
                // Write performance is only available on some recorders.
            }
        }

        return new OpticalDriveMediaInfo(
            profile,
            profileName,
            family,
            disc.State,
            disc.IsErasable,
            disc.Sessions,
            mediaId,
            OpticalDriveMediaParsers.DescribeMediaManufacturer(mediaId),
            atip,
            total,
            usable,
            free,
            nextWritable,
            readSpeeds,
            writeSpeeds);
    }

    private static long ReadBluRayUsableSectors(
        int profile,
        OpticalDiscState state,
        uint maximumBlocks,
        int formatStatus,
        int spareClusters,
        byte[]? trackData)
    {
        // The card must advertise exactly the capacity the sequential BD-R writer accepts;
        // anything else stays informational with zero writable sectors.
        if (state != OpticalDiscState.Blank || trackData is null
            || OpticalDriveMediaParsers.ParseBluRayTrackInformation(trackData) is not { } track)
            return 0;
        BluRaySequentialMediaInfo layout = new(
            profile,
            true,
            formatStatus,
            track.TrackIsBlank,
            track.NextWritableAddressValid,
            track.TrackStartAddress,
            track.NextWritableAddress,
            track.FreeSectors,
            track.TrackSize,
            maximumBlocks,
            track.TrackNumber,
            spareClusters);
        return WindowsImapiBurner.IsBluRaySequentialWritable(layout, out _) ? layout.FreeBlocks : 0;
    }

    private static int ReadCurrentProfile(IMmcCommandTransport transport)
    {
        byte[] configuration = Read(transport, 0x46, 8);
        if (configuration.Length < 8)
            throw new IOException("GET CONFIGURATION 返回的数据被截断。");
        return BinaryPrimitives.ReadUInt16BigEndian(configuration.AsSpan(6, 2));
    }

    private static byte[] ReadAtip(IMmcCommandTransport transport)
    {
        const byte allocation = 64;
        byte[] cdb = [0x43, 0x02, 0x04, 0, 0, 0, 0, 0, allocation, 0];
        MmcCommandResult result = transport.Execute(cdb, allocation, 30);
        if (result.HResult < 0 || result.HResult == BluRayMmcCommands.CommandHasSenseData)
            throw new MmcCommandException(cdb[0], result);
        return result.Data;
    }

    private static string? ReadDvdMediaId(IMmcCommandTransport transport, int profile)
    {
        if (profile is 0x1a or 0x1b or 0x2a or 0x2b)
            return OpticalDriveMediaParsers.ParseDvdPlusMediaId(ReadDvdStructure(transport, 0x11, 34));
        return OpticalDriveMediaParsers.ParseDvdMinusMediaId(ReadDvdStructure(transport, 0x0e, 35));
    }

    private static byte[] ReadDvdStructure(
        IMmcCommandTransport transport,
        byte format,
        ushort allocationLength)
    {
        byte[] cdb = new byte[12];
        cdb[0] = 0xad;
        cdb[7] = format;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(8, 2), allocationLength);
        MmcCommandResult result = transport.Execute(cdb, allocationLength, 30);
        if (result.HResult < 0 || result.HResult == BluRayMmcCommands.CommandHasSenseData)
            throw new MmcCommandException(cdb[0], result);
        return result.Data;
    }

    private static byte[] ReadTrack(IMmcCommandTransport transport, int trackNumber)
    {
        const ushort allocation = 48;
        byte[] cdb = new byte[10];
        cdb[0] = 0x52;
        cdb[1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), checked((uint)trackNumber));
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), allocation);
        MmcCommandResult result = transport.Execute(cdb, allocation, 30);
        if (result.HResult < 0 || result.HResult == BluRayMmcCommands.CommandHasSenseData)
            throw new MmcCommandException(cdb[0], result);
        return result.Data;
    }

    private static byte[] ReadPerformance(IMmcCommandTransport transport, bool read)
    {
        const ushort maxDescriptors = 8;
        const ushort allocation = 8 + maxDescriptors * 16;
        byte[] cdb = new byte[12];
        cdb[0] = 0xac;
        // Data Type: 10b = nominal read, 14b = write speed (HLK write-speed descriptor).
        cdb[1] = read ? (byte)0x10 : (byte)0x14;
        // Write speed descriptors are queried from LBA 1; nominal read starts at 0.
        if (!read) BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(8, 2), maxDescriptors);
        cdb[10] = read ? (byte)0x00 : (byte)0x03;
        MmcCommandResult result = transport.Execute(cdb, allocation, 30);
        if (result.HResult < 0 || result.HResult == BluRayMmcCommands.CommandHasSenseData)
            throw new MmcCommandException(cdb[0], result);
        return result.Data;
    }

    private static byte[] Read(IMmcCommandTransport transport, byte opcode, ushort allocationLength)
    {
        byte[] cdb = new byte[10];
        cdb[0] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), allocationLength);
        MmcCommandResult result = transport.Execute(cdb, allocationLength, 30);
        if (result.HResult < 0 || result.HResult == BluRayMmcCommands.CommandHasSenseData)
            throw new MmcCommandException(opcode, result);
        return result.Data;
    }

    private static (long Total, long Free)? ReadReportedCapacity(dynamic recorder)
    {
        dynamic? format = null;
        try
        {
            format = ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            long total = Convert.ToInt64(format.TotalSectorsOnMedia);
            long free = Convert.ToInt64(format.FreeSectorsOnMedia);
            return total > 0 && free >= 0 ? (total, free) : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (format is not null) ImapiCom.ReleaseComObject((object)format);
        }
    }

    private static long? ClampFree(long? free, long? total)
    {
        if (free is null or < 0) return null;
        if (total is null) return free > 0 ? free : null;
        return free <= total ? free : null;
    }

    private static string ReadText(object? value)
        => Convert.ToString(value)?.Trim() ?? string.Empty;
}
