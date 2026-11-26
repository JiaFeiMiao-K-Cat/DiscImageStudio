using System.Buffers.Binary;
using System.Dynamic;
using System.IO;
using System.Runtime.InteropServices;
using DiscImageStudio.Burning;

internal static class MediaSectorCountTests
{
    internal static void Run()
    {
        IOpticalDiscBurner totalOnlyReader = new TotalOnlyReader();
        Throws<NotSupportedException>(() => totalOnlyReader.GetMediaCapacityAsync("reader", OpticalBurnMediaKind.DvdData).GetAwaiter().GetResult());
        // 79:59:74 is the first sector of lead-out, with LBA 0 at 00:02:00.
        // Never add one as if this were the last readable data LBA.
        byte[] atip = new byte[28];
        atip[1] = 26;
        atip[12] = 79; atip[13] = 59; atip[14] = 74;
        var cd = new ReadOnlyTransport([0x43, 2, 4, 0, 0, 0, 0, 0, 64, 0], atip);
        Require(WindowsImapiBurner.ReadCdAtipSectorCount(cd) == 359849);
        atip[14] = 70;
        Require(WindowsImapiBurner.ReadCdAtipSectorCount(cd) == 359845);
        atip[14] = 75;
        Throws<IOException>(() => WindowsImapiBurner.ReadCdAtipSectorCount(cd));
        atip[14] = 74; atip[1] = 62;
        Throws<IOException>(() => WindowsImapiBurner.ReadCdAtipSectorCount(cd));
        cd.HResult = BluRayMmcCommands.CommandHasSenseData;
        Throws<MmcCommandException>(() => WindowsImapiBurner.ReadCdAtipSectorCount(cd));

        Require(WindowsImapiBurner.ReadCdMediaCapacity(359845, physicallyBlank: true, physicalMediaType: 0x2)
            == new OpticalMediaCapacity(359845, 359843));
        Require(WindowsImapiBurner.ReadCdMediaCapacity(359845, physicallyBlank: false, physicalMediaType: 0x2)
            == new OpticalMediaCapacity(359845, 0));
        Require(WindowsImapiBurner.ReadCdMediaCapacity(359845, physicallyBlank: true, physicalMediaType: 0x3)
            == new OpticalMediaCapacity(359845, 359843));
        Require(WindowsImapiBurner.ReadCdMediaCapacity(359845, physicallyBlank: false, physicalMediaType: 0x3)
            == new OpticalMediaCapacity(359845, 359843) { RequiresErase = true });
        Throws<IOException>(() => WindowsImapiBurner.ReadCdMediaCapacity(2, physicallyBlank: false, physicalMediaType: 0x3));
        TestCdRwWriterSupport();
        TestCdRwPreparation();

        Require(WindowsImapiBurner.ReadDvdSectorCount(0x9, 714_544) == 714_544);
        Require(!WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.DvdData, 0x5, [0x12]));
        Require(WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.DvdData, 0x7, [0x1A]));
        Require(WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.DvdData, 0xA, [0x13]));
        Require(WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.DvdData, 0xA, [0x14]));
        dynamic dvdFormat = new ExpandoObject();
        dvdFormat.CurrentPhysicalMediaType = 0xA;
        dvdFormat.CurrentMediaStatus = 0x4000; // Finalized, without ERASE_REQUIRED.
        Require(WindowsImapiBurner.DvdNeedsErase((object)dvdFormat));
        dvdFormat.CurrentMediaStatus = 0x2;
        Require(!WindowsImapiBurner.DvdNeedsErase((object)dvdFormat));
        dvdFormat.CurrentPhysicalMediaType = 0x9;
        dvdFormat.CurrentMediaStatus = 0x4000;
        Require(!WindowsImapiBurner.DvdNeedsErase((object)dvdFormat));
        Require(WindowsImapiBurner.ReadDvdSectorCount(0x6, 2_295_104) == 2_295_104);
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadDvdSectorCount(0x9, 0));
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadDvdSectorCount(0xB, 4_171_712));
        // Neither the raw geometry nor an actual Free value receives the legacy estimate.
        Require(WindowsImapiBurner.ReadDvdMediaCapacity(0x9, 0x2, 714546, 713936)
            == new OpticalMediaCapacity(714546, 713936));
        Require(WindowsImapiBurner.ReadDvdMediaCapacity(0x6, 0x2, 2295104, 2295104)
            == new OpticalMediaCapacity(2295104, 2295104));
        Require(WindowsImapiBurner.ReadDvdMediaCapacity(0x9, 0x2, 714546, 0).WritableSectors == 0);
        Throws<IOException>(() => WindowsImapiBurner.ReadDvdMediaCapacity(0x9, 0x2, 714546, 714547));
        Throws<IOException>(() => WindowsImapiBurner.ReadDvdMediaCapacity(0x9, 0x2, 714546, -1));
        // A formatted erasable disc is overwritten from LBA 0, so its old free
        // extent no longer limits the writable payload.
        Require(WindowsImapiBurner.ReadDvdMediaCapacity(0x7, 0x1, 2295104, 0)
            == new OpticalMediaCapacity(2295104, 2295104));

        byte[] descriptor = new byte[12];
        descriptor[3] = 8;
        descriptor[8] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(4), 12_219_392);
        var bd = new ReadOnlyTransport([0x23, 0, 0, 0, 0, 0, 0, 1, 0, 0], descriptor);
        descriptor[10] = 16;
        Require(WindowsImapiBurner.ReadBluRaySectorCount(bd) == 12_219_392);
        descriptor[8] = 2;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRaySectorCount(bd));
        descriptor[10] = 0;
        Require(WindowsImapiBurner.ReadBluRaySectorCount(bd) == 12_219_392);
        descriptor[8] = 3;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRaySectorCount(bd));
        descriptor[8] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(4), 24_438_784);
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRaySectorCount(bd));

        var bdMedia = new BluRayReadOnlyTransport();
        Require(WindowsImapiBurner.ReadBluRayMediaCapacity(bdMedia)
            == new OpticalMediaCapacity(12_219_392, 12_219_392));
        Require(bdMedia.Commands.SequenceEqual(new byte[] { 0x46, 0x51, 0x23, 0x52 }));
        bdMedia.FreeBlocks -= 32;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRayMediaCapacity(bdMedia));
        bdMedia.FreeBlocks += 32;
        bdMedia.FormatStatus = 2;
        bdMedia.SpareClusters = 0;
        Require(WindowsImapiBurner.ReadBluRayMediaCapacity(bdMedia)
            == new OpticalMediaCapacity(12_219_392, 0));
        bdMedia.SpareClusters = 4096;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ReadBluRayMediaCapacity(bdMedia));
        Console.WriteLine("calibration-media-capacity: separate geometry/payload limits, simulated CD-RW preparation, direct DVD Free, strict BD layout and read-only queries passed");
    }

    private static void TestCdRwWriterSupport()
    {
        const int cdRecordable = 0x2;
        const int cdRewritable = 0x3;
        const int rawMediaNotBlank = unchecked((int)0xC0AA0606);
        const int rawMediaNotSupported = unchecked((int)0xC0AA0607);
        const int rawRecorderNotSupported = unchecked((int)0xC0AA0610);
        var recorder = new object();
        int? recorderError = rawMediaNotSupported;
        bool recorderSupported = true;
        List<string> probes = [];
        dynamic format = new ExpandoObject();
        format.MediaPhysicallyBlank = false;
        format.IsRecorderSupported = new Func<object, bool>(_ =>
        {
            probes.Add("recorder");
            if (recorderError is int hresult)
                throw new COMException("RAW CD support probe failed.", hresult);
            return recorderSupported;
        });
        format.IsCurrentMediaSupported = new Func<object, bool>(_ =>
        {
            probes.Add("media");
            return true;
        });
        OpticalBurnRequest audio = new("simulated-cd", OpticalBurnMediaKind.CdAudio, 2352, (_, _) => { });
        OpticalBurnRequest authorized = audio with { ConfirmCdRwErase = () => true };

        Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateWriterSupportBeforeBurn(
            audio, cdRewritable, recorder, (object)format));
        Require(probes.Count == 0, "a used CD-RW without an erase confirmation callback is rejected before RAW support probes");
        Throws<COMException>(() => WindowsImapiBurner.ValidateWriterSupportBeforeBurn(
            audio, cdRecordable, recorder, (object)format));
        Require(probes.SequenceEqual(new[] { "recorder" }), "used CD-R still receives the RAW support check");

        foreach (int hresult in new[] { rawMediaNotSupported, rawMediaNotBlank })
        {
            probes.Clear();
            recorderError = hresult;
            Require(WindowsImapiBurner.ValidateWriterSupportBeforeBurn(authorized, cdRewritable, recorder, (object)format)
                && probes.SequenceEqual(new[] { "recorder" }),
                $"a used CD-RW with RAW media error 0x{hresult:X8} reaches preparation before the current-media probe");
        }

        probes.Clear();
        recorderError = rawRecorderNotSupported;
        Throws<COMException>(() => WindowsImapiBurner.ValidateWriterSupportBeforeBurn(
            authorized, cdRewritable, recorder, (object)format));
        Require(probes.SequenceEqual(new[] { "recorder" }), "a RAW recorder capability error blocks CD-RW preparation");

        probes.Clear();
        recorderError = null;
        recorderSupported = false;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.ValidateWriterSupportBeforeBurn(
            authorized, cdRewritable, recorder, (object)format));
        Require(probes.SequenceEqual(new[] { "recorder" }), "an unsupported recorder blocks CD-RW preparation");

        probes.Clear();
        recorderSupported = true;
        format.MediaPhysicallyBlank = true;
        Require(!WindowsImapiBurner.ValidateWriterSupportBeforeBurn(audio, cdRewritable, recorder, (object)format)
            && probes.SequenceEqual(new[] { "recorder", "media" }), "blank CD-RW uses both normal RAW support checks");
    }

    private static void TestCdRwPreparation()
    {
        const long atipSectors = 100;
        OpticalMediaCapacity used = WindowsImapiBurner.ReadCdMediaCapacity(atipSectors, physicallyBlank: false, physicalMediaType: 0x3);
        OpticalMediaCapacity blank = WindowsImapiBurner.ReadCdMediaCapacity(atipSectors, physicallyBlank: true, physicalMediaType: 0x3);
        OpticalMediaCapacity media = used;
        List<string> calls = [];
        OpticalBurnRequest audio = new("simulated-cd", OpticalBurnMediaKind.CdAudio,
            blank.WritableSectors * 2352, (_, _) => { }) { ConfirmCdRwErase = Confirm };
        OpticalBurnRequest data = audio with
        {
            MediaKind = OpticalBurnMediaKind.CdData,
            ContentLength = blank.WritableSectors * 2048,
        };

        foreach (OpticalBurnRequest request in new[] { audio, data })
        {
            calls.Clear();
            media = used;
            WindowsImapiBurner.PrepareCdRwForBurn(request, ReadCapacity, Erase, CancellationToken.None);
            Require(calls.SequenceEqual(new[] { "read capacity", "confirm", "erase", "read capacity" }),
                $"{request.MediaKind} validates before confirmation and checks the newly blank disc after erasing");
        }

        calls.Clear();
        WindowsImapiBurner.PrepareCdRwForBurn(audio, ReadCapacity, Erase, CancellationToken.None);
        Require(calls.SequenceEqual(new[] { "read capacity" }), "an already blank disc goes straight to writing");

        calls.Clear();
        media = used;
        Throws<InvalidOperationException>(() => WindowsImapiBurner.PrepareCdRwForBurn(
            audio with { ConfirmCdRwErase = null }, ReadCapacity, Erase, CancellationToken.None));
        Require(calls.SequenceEqual(new[] { "read capacity" }), "a used CD-RW is never erased without confirmation");

        calls.Clear();
        Throws<OperationCanceledException>(() => WindowsImapiBurner.PrepareCdRwForBurn(
            audio with { ConfirmCdRwErase = () => { calls.Add("decline"); return false; } },
            ReadCapacity, Erase, CancellationToken.None));
        Require(calls.SequenceEqual(new[] { "read capacity", "decline" }), "declining confirmation never erases");

        calls.Clear();
        using var cancellation = new CancellationTokenSource();
        Throws<OperationCanceledException>(() => WindowsImapiBurner.PrepareCdRwForBurn(
            audio with { ConfirmCdRwErase = () => { cancellation.Cancel(); return Confirm(); } },
            ReadCapacity, Erase, cancellation.Token));
        Require(calls.SequenceEqual(new[] { "read capacity", "confirm" }), "cancelling at confirmation never erases");

        foreach (OpticalMediaCapacity unusable in new[]
        {
            WindowsImapiBurner.ReadCdMediaCapacity(atipSectors, physicallyBlank: false, physicalMediaType: 0x2),
            used with { WritableSectors = used.WritableSectors - 1 },
        })
        {
            calls.Clear();
            media = unusable;
            Throws<InvalidOperationException>(() => WindowsImapiBurner.PrepareCdRwForBurn(
                audio, ReadCapacity, Erase, CancellationToken.None));
            Require(calls.SequenceEqual(new[] { "read capacity" }),
                "a used write-once CD-R or undersized CD-RW is rejected before confirmation or erase");
        }

        foreach (OpticalMediaCapacity unusable in new[] { used, blank with { WritableSectors = blank.WritableSectors - 1 } })
        {
            calls.Clear();
            media = used;
            Throws<InvalidOperationException>(() => WindowsImapiBurner.PrepareCdRwForBurn(audio, ReadCapacity, () =>
            {
                calls.Add("erase");
                media = unusable;
            }, CancellationToken.None));
            Require(calls.SequenceEqual(new[] { "read capacity", "confirm", "erase", "read capacity" }),
                "erase must leave an actually blank disc with adequate capacity before the burn can proceed");
        }

        OpticalMediaCapacity ReadCapacity()
        {
            calls.Add("read capacity");
            return media;
        }

        bool Confirm()
        {
            calls.Add("confirm");
            return true;
        }

        void Erase()
        {
            calls.Add("erase");
            media = blank;
        }
    }

    private sealed class TotalOnlyReader : IOpticalDiscBurner
    {
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => Task.FromResult(714546L);
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request, IProgress<OpticalBurnProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BluRayReadOnlyTransport : IMmcCommandTransport
    {
        internal uint FreeBlocks = 12_219_392;
        internal byte FormatStatus = 1;
        internal int SpareClusters = 4096;
        internal List<byte> Commands { get; } = [];

        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            Commands.Add(cdb[0]);
            Require(allocationLength == BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7, 2)));
            byte[] data;
            switch (cdb[0])
            {
                case 0x46:
                    data = new byte[8];
                    data[3] = 4;
                    data[7] = 0x41;
                    break;
                case 0x51:
                    data = new byte[12];
                    data[1] = 10;
                    data[6] = 1;
                    break;
                case 0x23:
                    data = new byte[12];
                    data[3] = 8;
                    data[8] = FormatStatus;
                    data[9] = (byte)(SpareClusters >> 16);
                    data[10] = (byte)(SpareClusters >> 8);
                    data[11] = (byte)SpareClusters;
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 12_219_392);
                    break;
                case 0x52:
                    Require(cdb[1] == 1 && BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2, 4)) == 1);
                    data = new byte[36];
                    data[1] = 34;
                    data[2] = 1;
                    data[6] = 0x40;
                    data[7] = 1;
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), FreeBlocks);
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 12_219_392);
                    break;
                default:
                    throw new InvalidOperationException("Capacity read must not issue a writing or preparation command.");
            }
            return new(0, [], data);
        }
    }

    private sealed class ReadOnlyTransport(byte[] expectedReadCommand, byte[] data) : IMmcCommandTransport
    {
        internal int HResult { get; set; }
        public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
        {
            Require(cdb.SequenceEqual(expectedReadCommand)
                && allocationLength == BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7, 2)));
            return new(HResult, [], data);
        }
    }

    private static void Require(bool condition, string message = "Unexpected calibration medium capacity or device command.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
