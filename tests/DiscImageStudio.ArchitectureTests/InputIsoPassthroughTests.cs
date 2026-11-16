using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Iso;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using DvdImageSolver;

internal static class InputIsoPassthroughTests
{
    private const int SectorBytes = Iso9660ImageWriter.LogicalSectorBytes;
    private const int RawBootIsoSectors = 260;
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private static readonly (string Path, byte[] Content)[] SampleFiles =
    [
        ("README.txt", CreatePatternBytes(1200, 11)),
        ("中文说明.txt", CreatePatternBytes(3100, 29)),
        ("EFI/BOOT/BOOTX64.EFI", CreatePatternBytes(9000, 47)),
        ("SOURCES/INSTALL.BIN", CreatePatternBytes(18500, 73)),
    ];

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"input-iso-passthrough-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePng = Path.Combine(directory, "source.png");
            WritePatternPng(sourcePng, 64);

            string rawBootIso = Path.Combine(directory, "raw-boot-sectors.iso");
            byte[] rawBootBytes = CreateRawBootSectorIso(rawBootIso, RawBootIsoSectors);

            string bootableFsIso = Path.Combine(directory, "bootable-joliet-os.iso");
            byte[] bootableFsBytes = CreateBootableJolietIso(bootableFsIso, SampleFiles);

            TestValidationGuards(directory, sourcePng, rawBootIso, bootableFsIso);
            TestIsoContentReaderFormats(directory);
            TestOuterVolumeMetadataPreservation(directory);
            TestCdPassthrough(directory, sourcePng, rawBootIso, rawBootBytes, bootableFsIso, bootableFsBytes);
            TestDvdPassthrough(directory, sourcePng, rawBootIso, rawBootBytes, bootableFsIso, bootableFsBytes);
            TestBluRayPassthrough(directory, sourcePng, rawBootIso, rawBootBytes, bootableFsIso, bootableFsBytes);
            TestProjectPersistence(directory, bootableFsIso);
            TestDataRegionAndState(directory, rawBootIso, bootableFsIso);
            WpfTestHost.Run(() => WaitOnDispatcher(TestWpfWorkflowsAsync(directory, sourcePng, bootableFsIso, bootableFsBytes)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("input-iso-passthrough: passed (CD/DVD/BD inner verbatim + outer readable ISO/UDF extract, CLI, .discproj, planner, UI previews, streaming burn)");
    }

    private static byte[] CreatePatternBytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)((i * 37 + seed) & 0xFF);
        return bytes;
    }

    private static byte[] CreateRawBootSectorIso(string path, int sectorCount)
    {
        byte[] image = new byte[sectorCount * SectorBytes];
        for (int s = 0; s < sectorCount; s++)
        {
            Span<byte> sector = image.AsSpan(s * SectorBytes, SectorBytes);
            uint state = (uint)(s * 747796405 + 2891336453);
            for (int i = 0; i < SectorBytes; i += 4)
            {
                state = unchecked(state * 1664525u + 1013904223u);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(i, 4), state);
            }
        }

        StampBootHeaders(image, pvdLabel: "RAW_BOOT_ONLY");
        if (sectorCount > 256)
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(256 * SectorBytes, SectorBytes), 2);

        File.WriteAllBytes(path, image);
        return image;
    }

    private static void StampBootHeaders(byte[] image, string pvdLabel)
    {
        Span<byte> mbr = image.AsSpan(0, SectorBytes);
        "BOOTMBR!"u8.CopyTo(mbr);
        mbr[510] = 0x55;
        mbr[511] = 0xAA;

        "EFI PART"u8.CopyTo(image.AsSpan(1 * SectorBytes, SectorBytes));

        Span<byte> pvd = image.AsSpan(16 * SectorBytes, SectorBytes);
        pvd[0] = 0x01;
        "CD001"u8.CopyTo(pvd.Slice(1));
        pvd[6] = 0x01;
        pvd.Slice(40, 32).Fill((byte)' ');
        Encoding.ASCII.GetBytes(pvdLabel).AsSpan(0, Math.Min(32, pvdLabel.Length)).CopyTo(pvd.Slice(40));

        Span<byte> elTorito = image.AsSpan(17 * SectorBytes, SectorBytes);
        elTorito[0] = 0x00;
        "CD001"u8.CopyTo(elTorito.Slice(1));
        elTorito[6] = 0x01;
        "EL TORITO SPECIFICATION"u8.CopyTo(elTorito.Slice(7));
    }

    /// <summary>Builds a hybrid bootable ISO with MBR(0), GPT(1), PVD(16), El Torito(17),
    /// Joliet SVD(18), Terminator(19), and valid directories/files starting at LBA 20.</summary>
    private static byte[] CreateBootableJolietIso(string path, IReadOnlyList<(string Path, byte[] Content)> files)
    {
        string tmpDir = Path.Combine(Path.GetDirectoryName(path)!, $"iso-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            foreach ((string relPath, byte[] content) in files)
            {
                string full = Path.Combine(tmpDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, content);
            }

            Iso9660ImageWriter.LayoutPlan plan = Iso9660ImageWriter.Plan(tmpDir, 280, "WIN_BOOT_ISO");
            using MemoryStream ms = new();
            Iso9660ImageWriter.WriteSequentialPrefix(plan, ms, 280);
            byte[] baseIso = ms.ToArray();

            // Shift all LBA 17..end content up by 1 sector so LBA 17 holds El Torito BRVD cleanly
            // while PVD stays at LBA 16 and Joliet SVD moves to LBA 18 (probing VD scanner LBA 16..48).
            byte[] shifted = new byte[(baseIso.Length / SectorBytes + 1) * SectorBytes];
            Buffer.BlockCopy(baseIso, 0, shifted, 0, 17 * SectorBytes);
            Buffer.BlockCopy(baseIso, 17 * SectorBytes, shifted, 18 * SectorBytes, baseIso.Length - 17 * SectorBytes);
            RelocateIso9660Lbas(shifted, delta: 1, vdSectors: [16, 18]);
            StampBootHeaders(shifted, "WIN_BOOT_ISO");

            File.WriteAllBytes(path, shifted);
            return shifted;
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static void RelocateIso9660Lbas(byte[] image, int delta, int[] vdSectors)
    {
        uint totalSectors = checked((uint)(image.Length / SectorBytes));
        HashSet<uint> shiftedDirs = [];
        foreach (int vdLba in vdSectors)
        {
            Span<byte> vd = image.AsSpan(vdLba * SectorBytes, SectorBytes);
            WriteBoth32(vd, 80, totalSectors);
            ShiftPathTableLba(vd, 140, 148, 132, delta);
            uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(vd.Slice(158, 4));
            ShiftDirRecordLba(vd.Slice(156, 34), delta);
            uint newRootLba = checked((uint)(rootLba + delta));
            uint rootLen = BinaryPrimitives.ReadUInt32LittleEndian(vd.Slice(166, 4));
            ShiftDirectoryTree(image, newRootLba, rootLen, delta, shiftedDirs);
        }

        static void ShiftPathTableLba(Span<byte> vd, int lbaLeOffset, int lbaBeOffset, int ptSizeOffset, int delta)
        {
            uint ptLeLba = checked((uint)(BinaryPrimitives.ReadUInt32LittleEndian(vd.Slice(lbaLeOffset, 4)) + delta));
            uint ptBeLba = checked((uint)(BinaryPrimitives.ReadUInt32LittleEndian(vd.Slice(lbaBeOffset, 4)) + delta));
            BinaryPrimitives.WriteUInt32LittleEndian(vd.Slice(lbaLeOffset, 4), ptLeLba);
            BinaryPrimitives.WriteUInt32BigEndian(vd.Slice(lbaBeOffset, 4), ptBeLba);
            _ = ptSizeOffset;
        }

        static void ShiftDirectoryTree(byte[] image, uint dirLba, uint dirLen, int delta, HashSet<uint> visited)
        {
            if (!visited.Add(dirLba))
                return;

            int baseOff = checked((int)dirLba * SectorBytes);
            int pos = 0;
            while (pos < dirLen)
            {
                byte len = image[baseOff + pos];
                if (len == 0)
                {
                    pos = ((pos / SectorBytes) + 1) * SectorBytes;
                    continue;
                }

                Span<byte> rec = image.AsSpan(baseOff + pos, len);
                pos += len;

                uint oldChildLba = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(2, 4));
                uint newChildLba = oldChildLba == 0 ? 0 : checked((uint)(oldChildLba + delta));
                WriteBoth32(rec, 2, newChildLba);

                byte fiLen = rec[32];
                if (fiLen == 1 && rec[33] is 0x00 or 0x01)
                    continue;

                if ((rec[25] & 2) != 0)
                {
                    uint childLen = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(10, 4));
                    ShiftDirectoryTree(image, newChildLba, childLen, delta, visited);
                }
            }
        }

        static void ShiftDirRecordLba(Span<byte> rec, int delta)
        {
            uint lba = checked((uint)(BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(2, 4)) + delta));
            WriteBoth32(rec, 2, lba);
        }
    }

    private static void TestValidationGuards(string directory, string sourcePng, string rawBootIso, string bootableFsIso)
    {
        string missingIso = Path.Combine(directory, "missing.iso");
        string emptyIso = Path.Combine(directory, "empty.iso");
        string unalignedIso = Path.Combine(directory, "unaligned.iso");
        File.WriteAllBytes(emptyIso, []);
        File.WriteAllBytes(unalignedIso, new byte[SectorBytes + 17]);

        Throws<FileNotFoundException>(() => RawIsoPassthrough.Plan(missingIso, 512), "missing ISO rejected");
        Throws<InvalidOperationException>(() => RawIsoPassthrough.Plan(emptyIso, 512), "empty ISO rejected");
        Throws<InvalidOperationException>(() => RawIsoPassthrough.Plan(unalignedIso, 512), "non-2048 ISO rejected");
        Throws<InvalidOperationException>(() => RawIsoPassthrough.Plan(rawBootIso, RawBootIsoSectors, sectorAlignment: 16),
            "full-volume ISO with zero room for artwork rejected");
        Throws<InvalidOperationException>(() => RawIsoPassthrough.Plan(rawBootIso, RawBootIsoSectors + 4, sectorAlignment: 16,
            minimumDrawingSectors: 16), "ISO leaving fewer than one aligned artwork block rejected");

        // Outer placement requires an actual readable UDF or ISO-9660 filesystem inside the ISO
        Throws<InvalidDataException>(() => CdDataImageGenerator.PreparePassthroughIsoLayout(
            rawBootIso, new(24.5, 56.8, 600), filesAtOuterRing: true),
            "CD outer ISO rejects non-filesystem raw image");
        Throws<InvalidDataException>(() => DvdStreamingGenerator.PreparePassthroughIso(
            rawBootIso, new(512, 24, 58, FastParallelism: 1), filesAtOuterRing: true),
            "DVD outer ISO rejects non-filesystem raw image");
        Throws<InvalidDataException>(() => BluRayIsoGenerator.PreparePassthroughIso(
            new(TotalSectors: 1024, UsableSectors: 960), rawBootIso, filesAtOuterRing: true),
            "BD outer ISO rejects non-filesystem raw image");

        string folder = Path.Combine(directory, "mutual-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), [1, 2, 3]);

        Throws<ArgumentException>(() => CdImageGenerator.Generate(new(sourcePng, Path.Combine(directory, "bad-cd.iso"),
            new(24.5, 56.8, 320), ToneProfile.LegacyBinary, DataDirectory: folder, InputIsoPath: bootableFsIso)),
            "CD rejects both DataDirectory and InputIsoPath");
        Throws<ArgumentException>(() => DvdImageGenerator.Generate(new(sourcePng, Path.Combine(directory, "bad-dvd.iso"),
            new(512, 24, 58, FastParallelism: 1), DataDirectory: folder, InputIsoPath: bootableFsIso)),
            "DVD rejects both DataDirectory and InputIsoPath");
        BluRayImageGenerator bdGenerator = new((_, _) => new(2, 2, [0, 128, 128, 255]));
        Throws<ArgumentException>(() => bdGenerator.Generate(new(sourcePng, Path.Combine(directory, "bad-bd.iso"),
            new(TotalSectors: 1024), ToneProfile.LegacyBinary, DataDirectory: folder, InputIsoPath: bootableFsIso)),
            "BD rejects both DataDirectory and InputIsoPath");

        // Self-overwrite guards across CD, DVD, BD (both inner and outer)
        foreach (bool outer in new[] { false, true })
        {
            Throws<ArgumentException>(() => CdImageGenerator.Generate(new(sourcePng, bootableFsIso,
                new(24.5, 56.8, 600), ToneProfile.LegacyBinary, FilesAtOuterRing: outer, InputIsoPath: bootableFsIso)),
                $"CD prevents overwriting source ISO (outer={outer})");
            Throws<ArgumentException>(() => DvdImageGenerator.Generate(new(sourcePng, bootableFsIso,
                new(512, 24, 58, FastParallelism: 1), FilesAtOuterRing: outer, InputIsoPath: bootableFsIso)),
                $"DVD prevents overwriting source ISO (outer={outer})");
            Throws<ArgumentException>(() => bdGenerator.Generate(new(sourcePng, bootableFsIso,
                new(TotalSectors: 1024), ToneProfile.LegacyBinary, FilesAtOuterRing: outer, InputIsoPath: bootableFsIso)),
                $"BD prevents overwriting source ISO (outer={outer})");
        }
    }

    private static void TestIsoContentReaderFormats(string directory)
    {
        CdDiscParameters cdParams = new(24.5, 56.8, 600);
        BluRayDiscParameters bdDisc = new(TotalSectors: 1024, UsableSectors: 960);

        // 1) Plain ISO-9660 with ;1 version suffixes + multi-extent file (flags & 0x80)
        string plainAndMultiIso = Path.Combine(directory, "plain-multi.iso");
        byte[] extA = CreatePatternBytes(4096, 17);
        byte[] extB = CreatePatternBytes(1500, 91);
        byte[] combinedMulti = [.. extA, .. extB];
        byte[] smallPlain = CreatePatternBytes(777, 5);
        BuildPlainAndMultiExtentIso(plainAndMultiIso, smallPlain, extA, extB);

        Dictionary<string, IsoEntry> plainCdOut = RenderOuterCdJoliet(plainAndMultiIso, cdParams, out Iso9660ImageWriter.LayoutPlan plainCdPlan);
        Require(plainCdOut.Count == 2
            && plainCdOut["BOOT.BIN"].Bytes.SequenceEqual(smallPlain)
            && plainCdOut["SUB/BIG.BIN"].Bytes.SequenceEqual(combinedMulti)
            && plainCdOut["BOOT.BIN"].Lba >= plainCdPlan.FileDataStartLba
            && plainCdOut["SUB/BIG.BIN"].Lba >= plainCdPlan.FileDataStartLba,
            "plain ISO strips ;1 suffix and reassembles multi-extent files at outer ring");

        // 2) Rock Ridge (SP, NM >64 chars + CE continuation, SL symlink skipped, CL -> rr_moved RE skipped)
        string rockRidgeIso = Path.Combine(directory, "rock-ridge.iso");
        string longRrName = "VeryLongRockRidgeFilename_ExceedingJolietLimit_CaseSensitive_0123456789.bin";
        byte[] rrFileBytes = CreatePatternBytes(2500, 33);
        byte[] relocBytes = CreatePatternBytes(1200, 61);
        BuildRockRidgeBridgeTestIso(rockRidgeIso, longRrName, rrFileBytes, relocBytes);

        // Verify through BD outer UDF so >64-char Rock Ridge filename is preserved in full
        Dictionary<string, byte[]> rrBdOut = RenderOuterBdUdf(rockRidgeIso, bdDisc, out _);
        Require(rrBdOut.Count == 2
            && rrBdOut[longRrName].SequenceEqual(rrFileBytes)
            && rrBdOut["deep/relocated.dat"].SequenceEqual(relocBytes)
            && !rrBdOut.ContainsKey("JOLIET_WRONG.TXT"),
            "Rock Ridge preserves >64-char NM+CE name, follows CL relocated dir, skips RE/symlink and overrides Joliet");

        // Also verify through CD outer Joliet
        Dictionary<string, IsoEntry> rrCdOut = RenderOuterCdJoliet(rockRidgeIso, cdParams, out Iso9660ImageWriter.LayoutPlan rrCdPlan);
        Require(rrCdOut.Count == 2
            && rrCdOut["deep/relocated.dat"].Bytes.SequenceEqual(relocBytes)
            && rrCdOut["deep/relocated.dat"].Lba >= rrCdPlan.FileDataStartLba,
            "Rock Ridge outer CD preserves nested relocated file data at outer ring");

        // 3) UDF 2.01 cross-disc conversion: source UDF 2.01 ISO -> CD outer ISO-9660/Joliet
        string udfSourceDir = Path.Combine(directory, "udf-src");
        Directory.CreateDirectory(Path.Combine(udfSourceDir, "assets"));
        File.WriteAllBytes(Path.Combine(udfSourceDir, "index.txt"), CreatePatternBytes(500, 3));
        File.WriteAllBytes(Path.Combine(udfSourceDir, "assets", "样本.dat"), CreatePatternBytes(6000, 19));
        string udfSourceIso = Path.Combine(directory, "source-udf201.iso");
        UdfImageWriter.LayoutPlan srcUdfPlan = UdfImageWriter.Plan(udfSourceDir, 640, "UDF_SRC");
        using (FileStream fs = File.Create(udfSourceIso))
        {
            UdfImageWriter.WriteSequentialPrefix(srcUdfPlan, fs, srcUdfPlan.DrawingStartLba);
            fs.Write(new byte[(srcUdfPlan.DrawingEndLbaExclusive - srcUdfPlan.DrawingStartLba) * SectorBytes]);
            UdfImageWriter.WriteSequentialSuffix(srcUdfPlan, fs, srcUdfPlan.DrawingEndLbaExclusive);
        }

        Dictionary<string, IsoEntry> cdOut = RenderOuterCdJoliet(udfSourceIso, cdParams, out _);
        Require(cdOut["index.txt"].Bytes.SequenceEqual(CreatePatternBytes(500, 3))
            && cdOut["assets/样本.dat"].Bytes.SequenceEqual(CreatePatternBytes(6000, 19)),
            "UDF 2.01 source ISO converts cleanly to outer CD ISO-9660/Joliet");

        // 4) UDF 2.50 Bridge ISO (decoy ISO-9660 README.TXT stub vs Metadata Partition + EFE 266 + inline + sparse + symlink skipped)
        string bridgeUdf250Iso = Path.Combine(directory, "win-bridge-udf250.iso");
        byte[] tinyInline = Encoding.UTF8.GetBytes("inline-udf250-cfg=ok\n");
        byte[] sparseHead = CreatePatternBytes(2048, 83);
        byte[] sparseTail = CreatePatternBytes(1024, 97);
        byte[] expectedSparse = [.. sparseHead, .. new byte[2048], .. sparseTail];
        BuildUdf250BridgeIso(bridgeUdf250Iso, tinyInline, sparseHead, sparseTail);

        Dictionary<string, byte[]> bdFiles = RenderOuterBdUdf(bridgeUdf250Iso, bdDisc, out BluRayIsoPlan bridgeBdPlan);
        Require(bridgeBdPlan.HasFileSystem && bridgeBdPlan.HasExternalIso && bridgeBdPlan.FileCount == 2
            && bdFiles.Count == 2
            && !bdFiles.ContainsKey("README.TXT")
            && bdFiles["tiny.cfg"].SequenceEqual(tinyInline)
            && bdFiles["sources/sparse.bin"].SequenceEqual(expectedSparse),
            "BD outer reconstructs UDF 2.50 Metadata Partition + inline + sparse files and overrides ISO-9660 stub");
    }

    private static void TestOuterVolumeMetadataPreservation(string directory)
    {
        const string isoLabel = "TRUE_ELYSIA";
        const string jolietAndUdfLabel = "致以无暇之人——真我之律者";
        const string volumeSet = "HONKAI_IMPACT_3RD";
        const string publisher = "HOYO-MIX";
        const string dataPreparer = "THIRTEEN_FLAME_CHASERS";
        const string copyright = "COPYRIGHT.TXT";
        const string abstractFile = "ABSTRACT.TXT";
        const string biblioFile = "BIBLIO.TXT";
        const string lvInfo3 = "contact@elysia.invalid";
        byte[] creationDate = [.. "2024091412345678"u8, 0x20];
        byte[] modDate = [.. "2024091501020304"u8, 0x20];
        byte[] expDate = [.. "0000000000000000"u8, 0x00];
        byte[] effDate = [.. "2024091400000000"u8, 0x00];

        string srcDir = Path.Combine(directory, "imgburn-meta-src");
        Directory.CreateDirectory(srcDir);
        byte[] trackData = CreatePatternBytes(3072, 11);
        File.WriteAllBytes(Path.Combine(srcDir, "track.bin"), trackData);

        // 1) ImgBurn-style Hybrid ISO (UDF + ISO-9660 PVD + Joliet SVD) with distinct labels and blank System/Application
        string hybridIso = Path.Combine(directory, "imgburn-hybrid.iso");
        DateTimeOffset udfRecordTs = new(2024, 9, 14, 12, 34, 56, TimeSpan.FromHours(8));
        byte[] hybridBytes = BuildHybridUdfIso(srcDir, jolietAndUdfLabel, udfRecordTs);
        // Move UDF VRS from LBA 16..18 -> 19..21 so ISO-9660 descriptors can live at LBA 16..18
        Array.Copy(hybridBytes, 16 * SectorBytes, hybridBytes, 19 * SectorBytes, 3 * SectorBytes);
        Array.Clear(hybridBytes, 16 * SectorBytes, 3 * SectorBytes);
        StampIsoDescriptor(hybridBytes, 16, joliet: false, rootLba: 22, rootLen: SectorBytes, volumeSectors: 640,
            volumeLabel: isoLabel, systemId: "", volumeSet: volumeSet, publisher: publisher, dataPreparer: dataPreparer,
            applicationId: "", copyright: copyright, abstractFile: abstractFile, biblioFile: biblioFile,
            creationDate: creationDate, modDate: modDate, expDate: expDate, effDate: effDate);
        StampIsoDescriptor(hybridBytes, 17, joliet: true, rootLba: 22, rootLen: SectorBytes, volumeSectors: 640,
            volumeLabel: jolietAndUdfLabel, systemId: "", volumeSet: volumeSet, publisher: publisher, dataPreparer: dataPreparer,
            applicationId: "", copyright: copyright, abstractFile: abstractFile, biblioFile: biblioFile,
            creationDate: creationDate, modDate: modDate, expDate: expDate, effDate: effDate);
        StampIsoTerminator(hybridBytes, 18);
        hybridBytes[22 * SectorBytes] = 0; // empty decoy ISO root dir (22*2048 cleared already, ensure explicit)
        _ = WriteIsoDirRecord(hybridBytes.AsSpan(22 * SectorBytes), "\0", 22, SectorBytes, isDir: true);
        // Stamp UDF PVD VolumeSet + IUVD LVInfo1..3 on both Main (32..35) and Reserve (384..387) VDS
        foreach (int vdsBase in new[] { 32, 640 - 256 })
        {
            Span<byte> uPvd = hybridBytes.AsSpan(vdsBase * SectorBytes, SectorBytes);
            WriteUdfCs0Dstring(uPvd.Slice(72, 128), "2024091404345600" + volumeSet);
            StampUdfTag(uPvd, 1, (uint)vdsBase, 512);

            Span<byte> uIuvd = hybridBytes.AsSpan((vdsBase + 1) * SectorBytes, SectorBytes);
            WriteUdfCs0Dstring(uIuvd.Slice(244, 36), dataPreparer);
            WriteUdfCs0Dstring(uIuvd.Slice(280, 36), publisher);
            WriteUdfCs0Dstring(uIuvd.Slice(316, 36), lvInfo3);
            StampUdfTag(uIuvd, 4, (uint)(vdsBase + 1), 512);
        }
        File.WriteAllBytes(hybridIso, hybridBytes);

        // Verify CD outer ring preserves distinct ISO-9660 & Joliet labels + identifiers + empty System/App + dates
        Iso9660ImageWriter.LayoutPlan cdOuterPlan = Iso9660ImageWriter.PlanFromIso(hybridIso, 700, filesAtOuterRing: true);
        using MemoryStream cdOutStream = new();
        Iso9660ImageWriter.WriteOuterRingPrefix(cdOuterPlan, cdOutStream, cdOuterPlan.MetadataEndLbaExclusive);
        cdOutStream.Write(new byte[(cdOuterPlan.FileDataStartLba - cdOuterPlan.MetadataEndLbaExclusive) * SectorBytes]);
        Iso9660ImageWriter.WriteOuterRingSuffix(cdOuterPlan, cdOutStream, cdOuterPlan.FileDataStartLba);
        byte[] cdOutBytes = cdOutStream.ToArray();

        VerifyIsoDescriptorMetadata(cdOutBytes, 16, joliet: false,
            isoLabel, "", volumeSet, publisher, dataPreparer, "", copyright, abstractFile, biblioFile,
            creationDate, modDate, expDate, effDate, "CD outer PVD");
        VerifyIsoDescriptorMetadata(cdOutBytes, 17, joliet: true,
            jolietAndUdfLabel, "", volumeSet, publisher, dataPreparer, "", copyright, abstractFile, biblioFile,
            creationDate, modDate, expDate, effDate, "CD outer Joliet SVD");
        Dictionary<string, IsoEntry> cdFiles = ReadJoliet(cdOutBytes);
        Require(cdFiles.Count == 1 && cdFiles["track.bin"].Bytes.SequenceEqual(trackData),
            "CD outer metadata-preserved image retains exact outer payload bytes");

        // Verify DVD outer ring preserves same descriptors via HybridIsoImageWriter
        HybridIsoImageWriter.LayoutPlan dvdOuterPlan = HybridIsoImageWriter.PlanFromIso(
            hybridIso, 768, filesAtOuterRing: true);
        using MemoryStream dvdOutStream = new();
        HybridIsoImageWriter.WriteOuterRingPrefix(dvdOuterPlan, dvdOutStream, dvdOuterPlan.MetadataEndLbaExclusive);
        dvdOutStream.Write(new byte[(dvdOuterPlan.FileDataStartLba - dvdOuterPlan.MetadataEndLbaExclusive) * SectorBytes]);
        HybridIsoImageWriter.WriteOuterRingSuffix(dvdOuterPlan, dvdOutStream, dvdOuterPlan.FileDataStartLba);
        byte[] dvdOutBytes = dvdOutStream.ToArray();
        VerifyIsoDescriptorMetadata(dvdOutBytes, 16, joliet: false,
            isoLabel, "", volumeSet, publisher, dataPreparer, "", copyright, abstractFile, biblioFile,
            creationDate, modDate, expDate, effDate, "DVD outer PVD");
        VerifyIsoDescriptorMetadata(dvdOutBytes, 17, joliet: true,
            jolietAndUdfLabel, "", volumeSet, publisher, dataPreparer, "", copyright, abstractFile, biblioFile,
            creationDate, modDate, expDate, effDate, "DVD outer Joliet SVD");

        // Verify BD-R outer ring preserves UDF label, VolumeSet, LVInfo1..3, recording timestamp, and files
        BluRayDiscParameters bdDisc = new(TotalSectors: 1024, UsableSectors: 960);
        Dictionary<string, byte[]> bdFiles = RenderOuterBdUdf(hybridIso, bdDisc, out BluRayIsoPlan bdPlan);
        Require(bdFiles.Count == 1 && bdFiles["track.bin"].SequenceEqual(trackData), "BD outer retains file data");
        using MemoryStream bdStream = new();
        BluRayIsoGenerator.GenerateToStream(bdStream, bdPlan, new RecordingPayloadMapping());
        byte[] bdOutBytes = bdStream.ToArray();
        UdfFilesystemTests.Inspect(bdOutBytes, bdDisc.OutputSectors, label: jolietAndUdfLabel);
        VerifyUdfDescriptorMetadata(bdOutBytes, 32,
            expectedVolumeSet: "2024091404345600" + volumeSet,
            expectedLvInfo1: dataPreparer,
            expectedLvInfo2: publisher,
            expectedLvInfo3: lvInfo3,
            expectedYear: 2024, expectedMonth: 9, expectedDay: 14, expectedHourUtc: 4,
            "BD outer UDF from hybrid");

        // 2) ISO-9660 + Joliet ONLY (no UDF) -> BD-R outer carries Unicode label + VolumeSet + Preparer/Publisher
        string isoOnlyPath = Path.Combine(directory, "iso9660-joliet-only.iso");
        byte[] isoOnly = new byte[32 * SectorBytes];
        StampIsoDescriptor(isoOnly, 16, joliet: false, rootLba: 20, rootLen: SectorBytes, volumeSectors: 32,
            volumeLabel: isoLabel, systemId: "LINUX", volumeSet: volumeSet, publisher: publisher,
            dataPreparer: dataPreparer, applicationId: "MKISOFS", copyright: "", abstractFile: "", biblioFile: "",
            creationDate: creationDate, modDate: modDate, expDate: expDate, effDate: effDate);
        StampIsoDescriptor(isoOnly, 17, joliet: true, rootLba: 21, rootLen: SectorBytes, volumeSectors: 32,
            volumeLabel: jolietAndUdfLabel, systemId: "LINUX", volumeSet: volumeSet, publisher: publisher,
            dataPreparer: dataPreparer, applicationId: "MKISOFS", copyright: "", abstractFile: "", biblioFile: "",
            creationDate: creationDate, modDate: modDate, expDate: expDate, effDate: effDate);
        StampIsoTerminator(isoOnly, 18);
        int rPvd = 20 * SectorBytes;
        rPvd += WriteIsoDirRecord(isoOnly.AsSpan(rPvd), "\0", 20, SectorBytes, isDir: true);
        rPvd += WriteIsoDirRecord(isoOnly.AsSpan(rPvd), "\x01", 20, SectorBytes, isDir: true);
        _ = WriteIsoDirRecord(isoOnly.AsSpan(rPvd), "TRACK.BIN;1", 22, (uint)trackData.Length, isDir: false);
        int rJol = 21 * SectorBytes;
        rJol += WriteIsoDirRecord(isoOnly.AsSpan(rJol), "\0", 21, SectorBytes, isDir: true, joliet: true);
        rJol += WriteIsoDirRecord(isoOnly.AsSpan(rJol), "\x01", 21, SectorBytes, isDir: true, joliet: true);
        _ = WriteIsoDirRecord(isoOnly.AsSpan(rJol), "track.bin", 22, (uint)trackData.Length, isDir: false, joliet: true);
        trackData.CopyTo(isoOnly.AsSpan(22 * SectorBytes));
        File.WriteAllBytes(isoOnlyPath, isoOnly);

        BluRayIsoPlan bdFromIsoOnlyPlan = BluRayIsoGenerator.PreparePassthroughIso(bdDisc, isoOnlyPath, filesAtOuterRing: true);
        using MemoryStream bdFromIsoOnlyStream = new();
        BluRayIsoGenerator.GenerateToStream(bdFromIsoOnlyStream, bdFromIsoOnlyPlan, new RecordingPayloadMapping());
        byte[] bdFromIsoOnlyBytes = bdFromIsoOnlyStream.ToArray();
        UdfFilesystemTests.Inspect(bdFromIsoOnlyBytes, bdDisc.OutputSectors, label: jolietAndUdfLabel);
        VerifyUdfDescriptorMetadata(bdFromIsoOnlyBytes, 32,
            expectedVolumeSet: volumeSet,
            expectedLvInfo1: dataPreparer,
            expectedLvInfo2: publisher,
            expectedLvInfo3: "",
            expectedYear: 2024, expectedMonth: 9, expectedDay: 14, expectedHourUtc: 4,
            "BD outer UDF from ISO-9660/Joliet");

        // Check non-empty System/Application also preserved on CD outer
        CdDiscParameters cdParams = new(24.5, 56.8, 700);
        Dictionary<string, IsoEntry> isoOnlyCdOut = RenderOuterCdJoliet(isoOnlyPath, cdParams, out Iso9660ImageWriter.LayoutPlan overridePlan);
        Require(isoOnlyCdOut.Count == 1 && isoOnlyCdOut["track.bin"].Bytes.SequenceEqual(trackData),
            "CD outer preserves files from ISO-9660/Joliet source");
        using MemoryStream isoOnlyCdStream = new();
        Iso9660ImageWriter.WriteOuterRingPrefix(overridePlan, isoOnlyCdStream, overridePlan.MetadataEndLbaExclusive);
        isoOnlyCdStream.Write(new byte[(overridePlan.FileDataStartLba - overridePlan.MetadataEndLbaExclusive) * SectorBytes]);
        Iso9660ImageWriter.WriteOuterRingSuffix(overridePlan, isoOnlyCdStream, overridePlan.FileDataStartLba);
        VerifyIsoDescriptorMetadata(isoOnlyCdStream.ToArray(), 16, joliet: false,
            isoLabel, "LINUX", volumeSet, publisher, dataPreparer, "MKISOFS", "", "", "",
            creationDate, modDate, expDate, effDate, "CD outer non-empty System/App PVD");

        // 3) Rock Ridge + truncated Joliet (>16 char PVD label) + explicit --volume-label override
        string rrPath = Path.Combine(directory, "rr-long-label.iso");
        const string fullRrLabel = "MY_DISTRO_2026_AMD64_1"; // 22 chars > 16
        byte[] rrBytes = new byte[32 * SectorBytes];
        StampIsoDescriptor(rrBytes, 16, joliet: false, rootLba: 20, rootLen: SectorBytes, volumeSectors: 32,
            volumeLabel: fullRrLabel, systemId: "LINUX", volumeSet: volumeSet, publisher: publisher,
            dataPreparer: dataPreparer, applicationId: "GENISOIMAGE", copyright: "", abstractFile: "", biblioFile: "",
            creationDate: creationDate, modDate: modDate, expDate: expDate, effDate: effDate);
        StampIsoDescriptor(rrBytes, 17, joliet: true, rootLba: 21, rootLen: SectorBytes, volumeSectors: 32,
            volumeLabel: fullRrLabel[..16], systemId: "LINUX", volumeSet: volumeSet, publisher: publisher,
            dataPreparer: dataPreparer, applicationId: "GENISOIMAGE", copyright: "", abstractFile: "", biblioFile: "",
            creationDate: creationDate, modDate: modDate, expDate: expDate, effDate: effDate);
        StampIsoTerminator(rrBytes, 18);
        byte[] spEntry = [(byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF, 0];
        int rrDir = 20 * SectorBytes;
        rrDir += WriteIsoDirRecord(rrBytes.AsSpan(rrDir), "\0", 20, SectorBytes, isDir: true, sysUse: spEntry);
        rrDir += WriteIsoDirRecord(rrBytes.AsSpan(rrDir), "\x01", 20, SectorBytes, isDir: true);
        _ = WriteIsoDirRecord(rrBytes.AsSpan(rrDir), "TRACK.BIN;1", 22, (uint)trackData.Length, isDir: false);
        int rrJol = 21 * SectorBytes;
        rrJol += WriteIsoDirRecord(rrBytes.AsSpan(rrJol), "\0", 21, SectorBytes, isDir: true, joliet: true);
        rrJol += WriteIsoDirRecord(rrBytes.AsSpan(rrJol), "\x01", 21, SectorBytes, isDir: true, joliet: true);
        _ = WriteIsoDirRecord(rrBytes.AsSpan(rrJol), "track.bin", 22, (uint)trackData.Length, isDir: false, joliet: true);
        trackData.CopyTo(rrBytes.AsSpan(22 * SectorBytes));
        File.WriteAllBytes(rrPath, rrBytes);

        BluRayIsoPlan bdFromRr = BluRayIsoGenerator.PreparePassthroughIso(bdDisc, rrPath, filesAtOuterRing: true);
        using MemoryStream bdRrStream = new();
        BluRayIsoGenerator.GenerateToStream(bdRrStream, bdFromRr, new RecordingPayloadMapping());
        UdfFilesystemTests.Inspect(bdRrStream.ToArray(), bdDisc.OutputSectors, label: fullRrLabel);

        // Explicit --volume-label overrides volume label while still keeping volume identifiers
        Iso9660ImageWriter.LayoutPlan explicitPlan = Iso9660ImageWriter.PlanFromIso(
            rrPath, 700, volumeLabel: "CUSTOM_OUT", filesAtOuterRing: true);
        using MemoryStream explicitStream = new();
        Iso9660ImageWriter.WriteOuterRingPrefix(explicitPlan, explicitStream, explicitPlan.MetadataEndLbaExclusive);
        explicitStream.Write(new byte[(explicitPlan.FileDataStartLba - explicitPlan.MetadataEndLbaExclusive) * SectorBytes]);
        Iso9660ImageWriter.WriteOuterRingSuffix(explicitPlan, explicitStream, explicitPlan.FileDataStartLba);
        byte[] explicitBytes = explicitStream.ToArray();
        VerifyIsoDescriptorMetadata(explicitBytes, 16, joliet: false,
            "CUSTOM_OUT", "LINUX", volumeSet, publisher, dataPreparer, "GENISOIMAGE", "", "", "",
            creationDate, modDate, expDate, effDate, "Explicit --volume-label keeps source identifiers");
    }

    private static byte[] BuildHybridUdfIso(string srcDir, string volumeLabel, DateTimeOffset timestamp)
    {
        UdfImageWriter.LayoutPlan plan = UdfImageWriter.Plan(srcDir, 640, volumeLabel, timestamp);
        using MemoryStream ms = new();
        UdfImageWriter.WriteSequentialPrefix(plan, ms, plan.DrawingStartLba);
        ms.Write(new byte[(plan.DrawingEndLbaExclusive - plan.DrawingStartLba) * SectorBytes]);
        UdfImageWriter.WriteSequentialSuffix(plan, ms, plan.DrawingEndLbaExclusive);
        return ms.ToArray();
    }

    private static void StampIsoDescriptor(
        byte[] image, int lba, bool joliet, uint rootLba, uint rootLen, uint volumeSectors,
        string volumeLabel, string systemId, string volumeSet, string publisher, string dataPreparer,
        string applicationId, string copyright, string abstractFile, string biblioFile,
        byte[] creationDate, byte[] modDate, byte[] expDate, byte[] effDate)
    {
        Span<byte> vd = image.AsSpan(lba * SectorBytes, SectorBytes);
        vd.Clear();
        vd[0] = (byte)(joliet ? 2 : 1);
        "CD001"u8.CopyTo(vd.Slice(1));
        vd[6] = 1;
        WriteIsoField(vd.Slice(8, 32), systemId, joliet);
        WriteIsoField(vd.Slice(40, 32), volumeLabel, joliet);
        if (joliet)
        {
            vd[88] = 0x25; vd[89] = 0x2F; vd[90] = 0x45;
        }
        WriteBoth32(vd, 80, volumeSectors);
        BinaryPrimitives.WriteUInt16LittleEndian(vd.Slice(128, 2), SectorBytes);
        BinaryPrimitives.WriteUInt16BigEndian(vd.Slice(130, 2), SectorBytes);
        _ = WriteIsoDirRecord(vd.Slice(156, 34), "\0", rootLba, rootLen, isDir: true);
        WriteIsoField(vd.Slice(190, 128), volumeSet, joliet);
        WriteIsoField(vd.Slice(318, 128), publisher, joliet);
        WriteIsoField(vd.Slice(446, 128), dataPreparer, joliet);
        WriteIsoField(vd.Slice(574, 128), applicationId, joliet);
        WriteIsoField(vd.Slice(702, 37), copyright, joliet);
        WriteIsoField(vd.Slice(739, 37), abstractFile, joliet);
        WriteIsoField(vd.Slice(776, 37), biblioFile, joliet);
        creationDate.CopyTo(vd.Slice(813, 17));
        modDate.CopyTo(vd.Slice(830, 17));
        expDate.CopyTo(vd.Slice(847, 17));
        effDate.CopyTo(vd.Slice(864, 17));
        vd[881] = 1;
    }

    private static void StampIsoTerminator(byte[] image, int lba)
    {
        Span<byte> vd = image.AsSpan(lba * SectorBytes, SectorBytes);
        vd.Clear();
        vd[0] = 255;
        "CD001"u8.CopyTo(vd.Slice(1));
        vd[6] = 1;
    }

    private static void WriteIsoField(Span<byte> dest, string value, bool joliet)
    {
        if (joliet)
        {
            int even = dest.Length & ~1;
            for (int i = 0; i + 1 < even; i += 2) { dest[i] = 0; dest[i + 1] = (byte)' '; }
            Encoding.BigEndianUnicode.GetBytes(value).AsSpan(0, Math.Min(even, value.Length * 2)).CopyTo(dest);
            return;
        }

        dest.Fill((byte)' ');
        Encoding.ASCII.GetBytes(value).AsSpan(0, Math.Min(dest.Length, value.Length)).CopyTo(dest);
    }

    private static void WriteUdfCs0Dstring(Span<byte> dest, string value)
    {
        dest.Clear();
        bool narrow = value.All(c => c <= byte.MaxValue);
        byte[] payload = narrow ? [8, .. Encoding.Latin1.GetBytes(value)] : [16, .. Encoding.BigEndianUnicode.GetBytes(value)];
        int len = Math.Min(payload.Length, dest.Length - 1);
        payload.AsSpan(0, len).CopyTo(dest);
        dest[^1] = checked((byte)len);
    }

    private static string ReadIsoField(byte[] image, int lba, int offset, int length, bool joliet)
    {
        int start = lba * SectorBytes + offset;
        return joliet
            ? Encoding.BigEndianUnicode.GetString(image, start, length & ~1).TrimEnd('\0', ' ')
            : Encoding.ASCII.GetString(image, start, length).TrimEnd('\0', ' ');
    }

    private static void VerifyIsoDescriptorMetadata(
        byte[] image, int lba, bool joliet,
        string expectedLabel, string expectedSystem, string expectedVolumeSet, string expectedPublisher,
        string expectedPreparer, string expectedApp, string expectedCopyright, string expectedAbstract, string expectedBiblio,
        byte[] expectedCreation, byte[] expectedMod, byte[] expectedExp, byte[] expectedEff,
        string context)
    {
        Require(ReadIsoField(image, lba, 40, 32, joliet) == expectedLabel, $"{context}: VolumeLabel mismatch");
        Require(ReadIsoField(image, lba, 8, 32, joliet) == expectedSystem, $"{context}: SystemIdentifier mismatch");
        Require(ReadIsoField(image, lba, 190, 128, joliet) == expectedVolumeSet, $"{context}: VolumeSetIdentifier mismatch");
        Require(ReadIsoField(image, lba, 318, 128, joliet) == expectedPublisher, $"{context}: PublisherIdentifier mismatch");
        Require(ReadIsoField(image, lba, 446, 128, joliet) == expectedPreparer, $"{context}: DataPreparerIdentifier mismatch");
        Require(ReadIsoField(image, lba, 574, 128, joliet) == expectedApp, $"{context}: ApplicationIdentifier mismatch");
        Require(ReadIsoField(image, lba, 702, 37, joliet) == expectedCopyright, $"{context}: CopyrightFileIdentifier mismatch");
        Require(ReadIsoField(image, lba, 739, 37, joliet) == expectedAbstract, $"{context}: AbstractFileIdentifier mismatch");
        Require(ReadIsoField(image, lba, 776, 37, joliet) == expectedBiblio, $"{context}: BibliographicFileIdentifier mismatch");
        int baseOffset = lba * SectorBytes;
        Require(image.AsSpan(baseOffset + 813, 17).SequenceEqual(expectedCreation), $"{context}: CreationDate mismatch");
        Require(image.AsSpan(baseOffset + 830, 17).SequenceEqual(expectedMod), $"{context}: ModificationDate mismatch");
        Require(image.AsSpan(baseOffset + 847, 17).SequenceEqual(expectedExp), $"{context}: ExpirationDate mismatch");
        Require(image.AsSpan(baseOffset + 864, 17).SequenceEqual(expectedEff), $"{context}: EffectiveDate mismatch");
    }

    private static void VerifyUdfDescriptorMetadata(
        byte[] image, int pvdLba,
        string expectedVolumeSet, string expectedLvInfo1, string expectedLvInfo2, string expectedLvInfo3,
        int expectedYear, int expectedMonth, int expectedDay, int expectedHourUtc,
        string context)
    {
        ReadOnlySpan<byte> pvd = image.AsSpan(pvdLba * SectorBytes, SectorBytes);
        Require(DecodeUdfDstring(pvd.Slice(72, 128)) == expectedVolumeSet, $"{context}: UDF VolumeSetIdentifier mismatch");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(pvd.Slice(378, 2)) == expectedYear
            && pvd[380] == expectedMonth && pvd[381] == expectedDay && pvd[382] == expectedHourUtc,
            $"{context}: UDF PVD RecordingDateAndTime mismatch");

        ReadOnlySpan<byte> iuvd = image.AsSpan((pvdLba + 1) * SectorBytes, SectorBytes);
        Require(DecodeUdfDstring(iuvd.Slice(244, 36)) == expectedLvInfo1, $"{context}: UDF LVInfo1 mismatch");
        Require(DecodeUdfDstring(iuvd.Slice(280, 36)) == expectedLvInfo2, $"{context}: UDF LVInfo2 mismatch");
        Require(DecodeUdfDstring(iuvd.Slice(316, 36)) == expectedLvInfo3, $"{context}: UDF LVInfo3 mismatch");
    }

    private static string DecodeUdfDstring(ReadOnlySpan<byte> field)
    {
        int len = field[^1];
        if (len <= 0 || len > field.Length - 1) return string.Empty;
        byte cid = field[0];
        ReadOnlySpan<byte> payload = field.Slice(1, len - 1);
        return cid == 8
            ? Encoding.Latin1.GetString(payload).TrimEnd('\0')
            : Encoding.BigEndianUnicode.GetString(payload).TrimEnd('\0');
    }

    private static Dictionary<string, IsoEntry> RenderOuterCdJoliet(
        string inputIsoPath, CdDiscParameters parameters, out Iso9660ImageWriter.LayoutPlan plan)
    {
        plan = Iso9660ImageWriter.PlanFromIso(inputIsoPath, checked((uint)parameters.OutputSectors), filesAtOuterRing: true);
        using MemoryStream ms = new();
        Iso9660ImageWriter.WriteOuterRingPrefix(plan, ms, plan.MetadataEndLbaExclusive);
        ms.Write(new byte[(plan.FileDataStartLba - plan.MetadataEndLbaExclusive) * SectorBytes]);
        Iso9660ImageWriter.WriteOuterRingSuffix(plan, ms, plan.FileDataStartLba);
        return ReadJoliet(ms.ToArray());
    }

    private static Dictionary<string, byte[]> RenderOuterBdUdf(
        string inputIsoPath, BluRayDiscParameters disc, out BluRayIsoPlan plan)
    {
        plan = BluRayIsoGenerator.PreparePassthroughIso(disc, inputIsoPath, filesAtOuterRing: true);
        Require(plan.PartitionStartSector >= plan.DrawingEndSectorExclusive
            && plan.DataEndSectorExclusive <= disc.OutputSectors,
            "BD outer UDF partition lies after artwork");
        RecordingPayloadMapping map = new();
        using MemoryStream ms = new();
        BluRayIsoGenerator.GenerateToStream(ms, plan, map);
        return UdfFilesystemTests.Inspect(ms.ToArray(), disc.OutputSectors);
    }

    private static void TestCdPassthrough(
        string directory, string sourcePng,
        string rawBootIso, byte[] rawBootBytes,
        string bootableFsIso, byte[] bootableFsBytes)
    {
        CdDiscParameters parameters = new(24.5, 56.8, 600);
        byte[]? innerIso = null;
        uint innerDrawStart = 0;
        uint innerDrawEnd = 0;

        // Inner: raw non-filesystem boot ISO works verbatim
        CdDataLayoutPlan rawInnerPlan = CdDataImageGenerator.PreparePassthroughIsoLayout(rawBootIso, parameters, filesAtOuterRing: false);
        using (MemoryStream rawMs = new())
        {
            CdDataImageGenerator.GenerateToStream(sourcePng, rawMs, parameters, rawInnerPlan);
            VerifyIsoSliceVerbatim(rawMs.ToArray(), 0, rawBootBytes, "CD raw inner");
        }

        foreach (bool outer in new[] { false, true })
        {
            CdDataLayoutPlan plan = CdDataImageGenerator.PreparePassthroughIsoLayout(bootableFsIso, parameters, outer);
            Require(plan.FilesAtOuterRing == outer && plan.HasExternalIso
                && plan.InputIsoPath == Path.GetFullPath(bootableFsIso),
                "CD ISO plan sets external ISO metadata");

            using MemoryStream ms = new();
            using ForwardOnlyStream forward = new(ms);
            CdDataImageGenerator.GenerateToStream(sourcePng, forward, parameters, plan);
            byte[] streamed = ms.ToArray();
            Require(streamed.Length == parameters.Sectors * SectorBytes, "CD output matches disc sector count");

            string outputIso = Path.Combine(directory, $"cd-pass-{outer}.iso");
            CdImageGenerator.Generate(new(sourcePng, outputIso, parameters, ToneProfile.LegacyBinary,
                WriteCue: false, FilesAtOuterRing: outer, InputIsoPath: bootableFsIso));
            byte[] fileBytes = File.ReadAllBytes(outputIso);
            Require(fileBytes.SequenceEqual(streamed), "CD file output matches forward-only stream byte-for-byte");

            uint fileStartLba = plan.FileDataStartLba;
            if (!outer)
            {
                innerIso = fileBytes;
                innerDrawStart = plan.DrawingStartLba;
                innerDrawEnd = plan.DrawingEndLbaExclusive;
                Require(fileStartLba == 0, "CD inner ISO starts at LBA 0");
                VerifyIsoSliceVerbatim(fileBytes, 0, bootableFsBytes, "CD inner bootable ISO");
                VerifyZeroRange(fileBytes, checked((uint)(bootableFsBytes.Length / SectorBytes)),
                    plan.DrawingStartLba - checked((uint)(bootableFsBytes.Length / SectorBytes)),
                    "CD inner alignment padding");
            }
            else
            {
                Require(plan.MetadataEndLbaExclusive > 19 && fileStartLba >= plan.DrawingEndLbaExclusive,
                    "CD outer ISO puts metadata at inner hub and files after artwork");
                VerifyZeroRange(fileBytes, plan.MetadataEndLbaExclusive, plan.DrawingStartLba - plan.MetadataEndLbaExclusive,
                    "CD outer inner pad");
                VerifyZeroRange(fileBytes, plan.DrawingEndLbaExclusive, fileStartLba - plan.DrawingEndLbaExclusive,
                    "CD outer outer pad");
                VerifyOuterJolietFiles(fileBytes, SampleFiles, plan.DrawingEndLbaExclusive, "CD outer");

                int overlapStart = checked((int)Math.Max(innerDrawStart, plan.DrawingStartLba) * SectorBytes);
                int overlapEnd = checked((int)Math.Min(innerDrawEnd, plan.DrawingEndLbaExclusive) * SectorBytes);
                Require(overlapEnd > overlapStart, "CD inner and outer share common drawing sectors");
                Require(fileBytes.AsSpan(overlapStart, overlapEnd - overlapStart)
                    .SequenceEqual(innerIso!.AsSpan(overlapStart, overlapEnd - overlapStart)),
                    "CD inner and outer preserve identical artwork sectors in overlap");
            }

            CdDataImageMetadata meta = CdDataImageMetadata.TryLoad(outputIso)
                ?? throw new InvalidOperationException("CD ISO sidecar missing");
            Require(meta.Parameters == parameters && meta.FilesAtOuterRing == outer
                && meta.DrawingStartLba == plan.DrawingStartLba && meta.DrawingEndLbaExclusive == plan.DrawingEndLbaExclusive,
                "CD sidecar records geometry, placement and drawing bounds");

            string warp = Path.Combine(directory, $"cd-warp-{outer}.png");
            string readback = Path.Combine(directory, $"cd-readback-{outer}.png");
            CdDataImageGenerator.PreviewWarp(sourcePng, warp, parameters, parameters, 64, 32, ToneProfile.LegacyBinary, plan);
            CdDataImageGenerator.PreviewTrack(outputIso, readback, parameters, 64, 64);
            Require(File.ReadAllBytes(warp).SequenceEqual(File.ReadAllBytes(readback)),
                "CD ISO PreviewWarp matches PreviewTrack");

            CdDiscModule cdModule = new();
            string cliIso = Path.Combine(directory, $"cd-cli-{outer}.iso");
            DiscJobResult cliGen = cdModule.ExecuteAsync(new("cd-generate", [
                "--input", sourcePng, "--output", cliIso, "--sectors", "600", "--write-cue", "false",
                "--input-iso", bootableFsIso, "--file-position", outer ? "outer" : "inner"
            ])).GetAwaiter().GetResult();
            Require(cliGen.Succeeded && File.ReadAllBytes(cliIso).SequenceEqual(fileBytes), "cd-generate CLI matches API");

            string cliWarp = Path.Combine(directory, $"cd-cli-warp-{outer}.png");
            DiscJobResult cliPreview = cdModule.ExecuteAsync(new("cd-preview-warp", [
                "--input", sourcePng, "--output", cliWarp, "--sectors", "600",
                "--actual-r0", "24.5", "--actual-r1", "56.8",
                "--input-iso", bootableFsIso, "--file-position", outer ? "outer" : "inner",
                "--size", "64", "--samples-per-sector", "32"
            ])).GetAwaiter().GetResult();
            Require(cliPreview.Succeeded && File.ReadAllBytes(cliWarp).SequenceEqual(File.ReadAllBytes(warp)),
                "cd-preview-warp CLI matches API");

            OpticalBurnRequest burnReq = BurnViewModel.CreateCdBurnRequest(sourcePng, parameters, null,
                "IGNORED", outer, interleave: true, ToneProfile.LegacyBinary, "mem-dev", new(150, false), bootableFsIso);
            burnReq.Validate();
            Require(burnReq.MediaKind == OpticalBurnMediaKind.CdData && burnReq.ContentLength == parameters.Sectors * SectorBytes,
                "CD ISO burn request uses 2048-byte data track");
            using MemoryStream burnStream = new();
            burnReq.ProduceContent(burnStream, CancellationToken.None);
            Require(burnStream.ToArray().SequenceEqual(fileBytes), "CD burn stream matches exported ISO");
        }
    }

    private static void TestDvdPassthrough(
        string directory, string sourcePng,
        string rawBootIso, byte[] rawBootBytes,
        string bootableFsIso, byte[] bootableFsBytes)
    {
        DvdStreamingOptions options = new(512, 24, 58, FastParallelism: 1);

        DvdHybridStreamingPlan rawInner = DvdStreamingGenerator.PreparePassthroughIso(rawBootIso, options, filesAtOuterRing: false);
        using (MemoryStream rawMs = new())
        {
            DvdStreamingGenerator.GenerateHybrid(sourcePng, rawMs, options, rawInner);
            VerifyIsoSliceVerbatim(rawMs.ToArray(), 0, rawBootBytes, "DVD raw inner");
        }

        foreach (bool outer in new[] { false, true })
        {
            DvdHybridStreamingPlan plan = DvdStreamingGenerator.PreparePassthroughIso(bootableFsIso, options, outer);
            Require(plan.HasExternalIso && plan.FilesAtOuterRing == outer && plan.DrawingStartLba % 16 == 0,
                "DVD ISO plan preserves ECC alignment");

            using MemoryStream ms = new();
            using ForwardOnlyStream forward = new(ms);
            DvdStreamingGenerator.GenerateHybrid(sourcePng, forward, options, plan);
            byte[] streamed = ms.ToArray();
            Require(streamed.Length == options.TotalSectors * SectorBytes, "DVD streamed ISO length exact");

            string fastIso = Path.Combine(directory, $"dvd-fast-{outer}.iso");
            DvdImageGenerator.Generate(new(sourcePng, fastIso, options, FilesAtOuterRing: outer, InputIsoPath: bootableFsIso));
            byte[] fastBytes = File.ReadAllBytes(fastIso);

            if (!outer)
            {
                Require(plan.FileDataStartLba == 0, "DVD inner ISO starts at LBA 0");
                VerifyIsoSliceVerbatim(streamed, 0, bootableFsBytes, "DVD stream inner");
                VerifyIsoSliceVerbatim(fastBytes, 0, bootableFsBytes, "DVD fast inner");
                uint rawSectors = checked((uint)(bootableFsBytes.Length / SectorBytes));
                VerifyZeroRange(streamed, rawSectors, plan.DrawingStartLba - rawSectors, "DVD inner ECC pad");
            }
            else
            {
                Require(plan.MetadataEndLbaExclusive > 19 && plan.FileDataStartLba >= plan.DrawingEndLbaExclusive,
                    "DVD outer ISO puts metadata at inner hub and files after artwork");
                VerifyOuterJolietFiles(streamed, SampleFiles, plan.DrawingEndLbaExclusive, "DVD stream outer");
                VerifyOuterJolietFiles(fastBytes, SampleFiles, plan.DrawingEndLbaExclusive, "DVD fast outer");
                VerifyZeroRange(streamed, plan.MetadataEndLbaExclusive, plan.DrawingStartLba - plan.MetadataEndLbaExclusive,
                    "DVD outer inner ECC pad");
                VerifyZeroRange(streamed, plan.DrawingEndLbaExclusive, plan.FileDataStartLba - plan.DrawingEndLbaExclusive,
                    "DVD outer outer pad");
            }

            DvdImageMetadata? meta = DvdImageMetadata.TryLoad(fastIso);
            Require(meta is { HasDataRegion: true, HasFileSystem: true }
                && meta.HasRawIso == !outer
                && meta.FilesAtOuterRing == outer
                && meta.DrawingStartLba == plan.DrawingStartLba
                && meta.DrawingEndLbaExclusive == plan.DrawingEndLbaExclusive,
                "DVD fast sidecar sets HasRawIso, mountableFilesystem and drawing bounds");

            string compatCopy = Path.Combine(directory, $"dvd-legacy-check-{outer}.iso");
            File.WriteAllBytes(compatCopy, []);
            JsonObject sidecarJson = JsonNode.Parse(File.ReadAllText(fastIso + ".json"))!.AsObject();
            sidecarJson["iso"]!.AsObject().Remove("mode");
            File.WriteAllText(compatCopy + ".json", sidecarJson.ToJsonString());
            Require(DvdImageMetadata.TryLoad(compatCopy) is { HasRawIso: false }, "missing iso.mode keeps HasRawIso false");

            string cliIso = Path.Combine(directory, $"dvd-cli-{outer}.iso");
            int exitCode = DvdCommandRunner.Run([
                "solve", "--image", sourcePng, "--iso-output", cliIso,
                "--total-sectors", "512", "--inner-radius-mm", "24", "--outer-radius-mm", "58",
                "--fast-output", "true", "--fast-parallelism", "1",
                "--input-iso", bootableFsIso, "--file-position", outer ? "outer" : "inner"
            ]);
            Require(exitCode == 0 && File.ReadAllBytes(cliIso).SequenceEqual(fastBytes),
                "DvdImageSolver --input-iso CLI matches DvdImageGenerator");

            string modIso = Path.Combine(directory, $"dvd-mod-{outer}.iso");
            DiscJobResult modResult = new DvdDiscModule().ExecuteAsync(new("solve", [
                "--image", sourcePng, "--iso-output", modIso,
                "--total-sectors", "512", "--inner-radius-mm", "24", "--outer-radius-mm", "58",
                "--fast-output", "true", "--fast-parallelism", "1",
                "--input-iso", bootableFsIso, "--file-position", outer ? "outer" : "inner"
            ])).GetAwaiter().GetResult();
            Require(modResult.Succeeded && File.ReadAllBytes(modIso).SequenceEqual(fastBytes),
                "DvdDiscModule solve --input-iso matches API");
        }

        int badExit = DvdCommandRunner.Run([
            "solve", "--image", sourcePng, "--iso-output", Path.Combine(directory, "bad-cli-dvd.iso"),
            "--total-sectors", "512", "--data-dir", directory, "--input-iso", bootableFsIso
        ]);
        Require(badExit != 0, "DvdImageSolver rejects simultaneous --data-dir and --input-iso");
    }

    private static void TestBluRayPassthrough(
        string directory, string sourcePng,
        string rawBootIso, byte[] rawBootBytes,
        string bootableFsIso, byte[] bootableFsBytes)
    {
        BluRayGray8Raster Load(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return new(4, 4, Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray());
        }

        BluRayImageGenerator generator = new(Load);
        BluRayDiscModule bdModule = new(Load);
        BluRayDiscParameters disc = new(TotalSectors: 1024, UsableSectors: 960);

        BluRayIsoPlan rawInner = BluRayIsoGenerator.PreparePassthroughIso(disc, rawBootIso, filesAtOuterRing: false);
        string rawOut = Path.Combine(directory, "bd-raw-inner.iso");
        generator.Generate(new(sourcePng, rawOut, disc, ToneProfile.LegacyBinary, FilesAtOuterRing: false, InputIsoPath: rawBootIso));
        VerifyIsoSliceVerbatim(File.ReadAllBytes(rawOut), 0, rawBootBytes, "BD raw inner");
        Require(!rawInner.HasFileSystem && rawInner.HasExternalIso, "BD raw inner has no reconstructed UDF");

        foreach (bool outer in new[] { false, true })
        {
            BluRayIsoPlan plan = BluRayIsoGenerator.PreparePassthroughIso(disc, bootableFsIso, outer);
            Require(plan.HasExternalIso && plan.HasDataRegion
                && plan.HasFileSystem == outer
                && plan.FilesAtOuterRing == outer
                && plan.InputIsoPath == Path.GetFullPath(bootableFsIso)
                && plan.DrawingStartSector % 32 == 0 && plan.DrawingEndSectorExclusive % 32 == 0,
                "BD ISO plan sets external ISO metadata, HasFileSystem and cluster alignment");

            RecordingPayloadMapping mapA = new();
            RecordingPayloadMapping mapB = new();
            using MemoryStream ms = new();
            using ForwardOnlyStream forward = new(ms);
            BluRayIsoGenerator.GenerateToStream(forward, plan, mapA);
            Require(mapA.Sectors.Keys.Min() == plan.DrawingStartSector
                && mapA.Sectors.Keys.Max() + 1 == plan.DrawingEndSectorExclusive
                && mapA.Sectors.Count == plan.DrawingEndSectorExclusive - plan.DrawingStartSector,
                "BD artwork maps exactly the drawing cluster interval");
            string directIso = Path.Combine(directory, $"bd-direct-{outer}.iso");
            BluRayIsoGenerator.GenerateFile(directIso, plan, mapB);
            Require(File.ReadAllBytes(directIso).SequenceEqual(ms.ToArray()),
                "BD file output matches forward-only stream");

            string genIso = Path.Combine(directory, $"bd-gen-{outer}.iso");
            generator.Generate(new(sourcePng, genIso, disc, ToneProfile.LegacyBinary,
                FilesAtOuterRing: outer, InputIsoPath: bootableFsIso));
            byte[] genBytes = File.ReadAllBytes(genIso);
            if (!outer)
            {
                Require(plan.PartitionStartSector == 0, "BD inner ISO starts at LBA 0");
                VerifyIsoSliceVerbatim(genBytes, 0, bootableFsBytes, "BD inner bootable ISO");
            }
            else
            {
                Require(plan.PartitionStartSector >= plan.DrawingEndSectorExclusive
                    && plan.DataEndSectorExclusive <= disc.OutputSectors,
                    "BD outer UDF partition stays after artwork and inside UsableSectors");
                VerifyOuterUdfFiles(genBytes, disc.OutputSectors, plan.DrawingStartSector,
                    plan.DrawingEndSectorExclusive, SampleFiles, "BD outer");
            }

            BluRayIsoMetadata meta = BluRayIsoMetadata.TryLoad(genIso)
                ?? throw new InvalidOperationException("BD ISO sidecar missing");
            Require(meta.InputIsoPath == Path.GetFullPath(bootableFsIso) && meta.FilesAtOuterRing == outer
                && meta.DrawingStartSector == plan.DrawingStartSector
                && meta.DrawingEndSectorExclusive == plan.DrawingEndSectorExclusive,
                "BD sidecar preserves InputIsoPath and drawing range");

            string cliIso = Path.Combine(directory, $"bd-cli-{outer}.iso");
            DiscJobResult cliRes = bdModule.ExecuteAsync(new("bd-generate", [
                "--input", sourcePng, "--output", cliIso,
                "--total-sectors", "1024", "--usable-sectors", "960",
                "--input-iso", bootableFsIso, "--file-position", outer ? "outer" : "inner"
            ])).GetAwaiter().GetResult();
            Require(cliRes.Succeeded && File.ReadAllBytes(cliIso).SequenceEqual(genBytes),
                "bd-generate --input-iso CLI matches API");
        }

        Throws<ArgumentException>(() => bdModule.ExecuteAsync(new("bd-generate", [
            "--input", sourcePng, "--output", Path.Combine(directory, "bad-bd-cli.iso"),
            "--total-sectors", "1024", "--data-dir", directory, "--input-iso", bootableFsIso
        ])).GetAwaiter().GetResult(), "bd-generate rejects both --data-dir and --input-iso");
    }

    private static void TestProjectPersistence(string directory, string mockIso)
    {
        foreach (string family in new[] { ProjectDocument.CdFamily, ProjectDocument.DvdFamily, ProjectDocument.BluRayFamily })
        {
            ProjectDocument doc = new(
                ProjectJsonStore.CurrentSchemaVersion, "ISO项目", family,
                new ProjectGeometry(24, 58, 2048), [],
                new ProjectOutputs(Path.Combine(directory, "out.iso"), false, "", "", false,
                    FilesAtOuterRing: true, InputIsoPath: mockIso),
                null, null, Timestamp, Timestamp);
            string projPath = Path.Combine(directory, family + "-iso.discproj");
            ProjectJsonStore.Save(projPath, doc);
            ProjectDocument loaded = ProjectJsonStore.Load(projPath);
            Require(loaded.Outputs.InputIsoPath == mockIso && loaded.Outputs.FilesAtOuterRing,
                $"{family} .discproj preserves InputIsoPath and FilesAtOuterRing");

            DiscParametersState state = new();
            ProjectSession.ApplyToState(loaded, state);
            (string dataDir, string isoPath, bool outer) = family switch
            {
                ProjectDocument.CdFamily => (state.CdDataDirectory, state.CdInputIsoPath, state.CdFilesAtOuterRing),
                ProjectDocument.DvdFamily => (state.DvdDataDirectory, state.DvdInputIsoPath, state.DvdFilesAtOuterRing),
                _ => (state.BdDataDirectory, state.BdInputIsoPath, state.BdFilesAtOuterRing),
            };
            bool useIso = family switch
            {
                ProjectDocument.CdFamily => state.CdUseInputIso,
                ProjectDocument.DvdFamily => state.DvdUseInputIso,
                _ => state.BdUseInputIso,
            };
            Require(useIso && isoPath == mockIso && dataDir.Length == 0 && outer,
                $"{family} ProjectSession activates ISO source tab");
            ProjectOutputs captured = ProjectSession.CaptureOutputs(loaded, state);
            Require(captured.InputIsoPath == mockIso && captured.DataDirectory.Length == 0 && captured.FilesAtOuterRing,
                $"{family} CaptureOutputs persists InputIsoPath");

            JsonObject legacyJson = JsonNode.Parse(File.ReadAllText(projPath))!.AsObject();
            legacyJson["outputs"]!.AsObject().Remove("inputIsoPath");
            legacyJson["outputs"]!.AsObject().Remove("filesAtOuterRing");
            File.WriteAllText(projPath, legacyJson.ToJsonString());
            ProjectDocument legacyDoc = ProjectJsonStore.Load(projPath);
            Require(legacyDoc.Outputs.InputIsoPath is null && !legacyDoc.Outputs.FilesAtOuterRing,
                "legacy project loads null InputIsoPath");
            ProjectSession.ApplyToState(legacyDoc, state);
            bool stillUseIso = family switch
            {
                ProjectDocument.CdFamily => state.CdUseInputIso,
                ProjectDocument.DvdFamily => state.DvdUseInputIso,
                _ => state.BdUseInputIso,
            };
            Require(!stillUseIso, "legacy project resets ISO source toggle");
            ProjectJsonStore.Save(projPath, legacyDoc);
            Require(!File.ReadAllText(projPath).Contains("inputIsoPath", StringComparison.Ordinal),
                "saving project without ISO omits inputIsoPath field");

            JsonObject badJson = JsonNode.Parse(File.ReadAllText(projPath))!.AsObject();
            badJson["outputs"]!.AsObject()["dataDirectory"] = directory;
            badJson["outputs"]!.AsObject()["inputIsoPath"] = mockIso;
            File.WriteAllText(projPath, badJson.ToJsonString());
            Throws<InvalidDataException>(() => ProjectJsonStore.Load(projPath),
                "ProjectJsonStore rejects both dataDirectory and inputIsoPath");
        }
    }

    private static void TestDataRegionAndState(string directory, string rawBootIso, string bootableFsIso)
    {
        IReadOnlyList<DesignViewModel.DataRegionMm>? cdInner = DataRegionPlanner.Compute(new(
            OpticalDiscFamily.CompactDisc, "", "", false, 600, 600, 24.5, 56.8, 0, 0, 0, InputIsoPath: bootableFsIso));
        Require(cdInner is { Count: 1 } && cdInner[0].InnerRadiusMm >= 24.5 && cdInner[0].OuterRadiusMm <= 56.8,
            "CD inner ISO DataRegionPlanner returns single inner hub annulus");
        IReadOnlyList<DesignViewModel.DataRegionMm>? cdOuter = DataRegionPlanner.Compute(new(
            OpticalDiscFamily.CompactDisc, "", "", true, 600, 600, 24.5, 56.8, 0, 0, 0, InputIsoPath: bootableFsIso));
        Require(cdOuter is { Count: 2 } && cdOuter[1].InnerRadiusMm > cdOuter[0].OuterRadiusMm,
            "CD outer ISO DataRegionPlanner returns inner metadata + outer data regions");

        IReadOnlyList<DesignViewModel.DataRegionMm>? dvdInner = DataRegionPlanner.Compute(new(
            OpticalDiscFamily.Dvd, "", "", false, 512, 512, 24, 58, 0, 0, 0, InputIsoPath: bootableFsIso));
        Require(dvdInner is { Count: 1 } && dvdInner[0].InnerRadiusMm >= 24 && dvdInner[0].OuterRadiusMm <= 58,
            "DVD inner ISO DataRegionPlanner returns single inner hub annulus");
        IReadOnlyList<DesignViewModel.DataRegionMm>? dvdOuter = DataRegionPlanner.Compute(new(
            OpticalDiscFamily.Dvd, "", "", true, 512, 512, 24, 58, 0, 0, 0, InputIsoPath: bootableFsIso));
        Require(dvdOuter is { Count: 2 } && dvdOuter[1].InnerRadiusMm > dvdOuter[0].OuterRadiusMm,
            "DVD outer ISO DataRegionPlanner returns inner metadata + outer data regions");

        IReadOnlyList<DesignViewModel.DataRegionMm>? bdInner = DataRegionPlanner.Compute(new(
            OpticalDiscFamily.BluRay, "", "", false, 1024, 960, 24, 58, 0, 0, 0, InputIsoPath: bootableFsIso));
        Require(bdInner is { Count: 1 } && bdInner[0].InnerRadiusMm >= 24 && bdInner[0].OuterRadiusMm <= 58,
            "BD inner ISO DataRegionPlanner returns inner annulus");
        IReadOnlyList<DesignViewModel.DataRegionMm>? bdOuter = DataRegionPlanner.Compute(new(
            OpticalDiscFamily.BluRay, "", "", true, 1024, 960, 24, 58, 0, 0, 0, InputIsoPath: bootableFsIso));
        Require(bdOuter is { Count: 1 } && bdOuter[0].InnerRadiusMm > 24 && bdOuter[0].OuterRadiusMm <= 58,
            "BD outer ISO DataRegionPlanner relocates UDF annulus to outer ring");

        DataRegionInputs overflowing = new(OpticalDiscFamily.BluRay, "", "", true,
            TotalSectors: 256, UsableSectors: 256, 24, 58, 0, 0, 0, InputIsoPath: bootableFsIso);
        Throws<DataRegionCapacityException>(() => DataRegionPlanner.Compute(overflowing),
            "DataRegionPlanner throws DataRegionCapacityException on outer ISO overflow");
        Throws<InvalidDataException>(() => DataRegionPlanner.Compute(new(
            OpticalDiscFamily.Dvd, "", "", true, 512, 512, 24, 58, 0, 0, 0, InputIsoPath: rawBootIso)),
            "DataRegionPlanner outer ISO surfaces InvalidDataException on raw non-filesystem image");

        DiscParametersState state = new() { CdOutputPath = Path.Combine(directory, "cd.wav") };
        state.CdUseInputIso = true;
        Require(state.IsCdAudioMode && !state.HasCdFiles && !state.HasCdDataDirectory && !state.ShowCdOuterIsoWarning,
            "empty ISO path stays in audio mode without warning");
        state.CdInputIsoPath = bootableFsIso;
        Require(state.HasCdFiles && !state.IsCdAudioMode && !state.HasCdDataDirectory
            && Path.GetExtension(state.CdOutputPath) == ".iso" && !state.ShowCdOuterIsoWarning,
            "selecting CD ISO switches to .iso output, hides volume label, and keeps inner warning off");
        state.CdFilesAtOuterRing = true;
        Require(state.ShowCdOuterIsoWarning, "CD outer ISO enables non-bootable warning");
        state.CdUseInputIso = false;
        Require(!state.ShowCdOuterIsoWarning && state.IsCdAudioMode,
            "switching back to empty folder tab clears warning and restores audio mode");
        state.CdUseInputIso = true;
        Require(state.ShowCdOuterIsoWarning && state.CdInputIsoPath == bootableFsIso,
            "switching back to ISO tab preserves path and warning");

        state.DvdUseInputIso = true;
        state.DvdInputIsoPath = bootableFsIso;
        state.DvdFilesAtOuterRing = true;
        Require(!state.ShowDvdVolumeLabel && state.ShowDvdOuterIsoWarning, "DVD ISO hides volume label and warns on outer placement");
        state.DvdFilesAtOuterRing = false;
        Require(!state.ShowDvdOuterIsoWarning, "DVD inner ISO clears non-bootable warning");

        state.BdUseInputIso = true;
        state.BdInputIsoPath = bootableFsIso;
        state.BdFilesAtOuterRing = true;
        Require(!state.ShowBdVolumeLabel && state.ShowBdOuterIsoWarning, "BD ISO hides volume label and warns on outer placement");
    }

    private static async Task TestWpfWorkflowsAsync(string directory, string sourcePng, string bootableFsIso, byte[] bootableFsBytes)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        string previousLang = L.CurrentLanguage;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        L.SetLanguage(L.Chinese);

        RecordingBurner burner = new();
        ShellViewModel shell = new(System.Windows.Threading.Dispatcher.CurrentDispatcher, burner);
        try
        {
            await shell.Burn.RefreshDevicesAsync();

            (string Family, OpticalDiscFamily Kind, string Total, string Usable, string Inner, string Outer)[] configs =
            [
                (ProjectDocument.CdFamily, OpticalDiscFamily.CompactDisc, "600", "600", "24.5", "56.8"),
                (ProjectDocument.DvdFamily, OpticalDiscFamily.Dvd, "512", "512", "24", "58"),
                (ProjectDocument.BluRayFamily, OpticalDiscFamily.BluRay, "1024", "960", "24", "58"),
            ];

            foreach ((string family, OpticalDiscFamily kind, string total, string usable, string inner, string outerRadius) in configs)
            {
                string outIso = Path.Combine(directory, $"ui-{kind}.iso");
                ProjectDocument doc = new(
                    ProjectJsonStore.CurrentSchemaVersion, "UI ISO", family,
                    new(double.Parse(inner, CultureInfo.InvariantCulture), double.Parse(outerRadius, CultureInfo.InvariantCulture),
                        int.Parse(total, CultureInfo.InvariantCulture),
                        UsableSectors: int.Parse(usable, CultureInfo.InvariantCulture)),
                    [new("artwork", "Asymmetric grayscale", sourcePng, true, 0, 0, 0, 1)],
                    new(outIso, false, "", "", false, FilesAtOuterRing: false, InputIsoPath: bootableFsIso),
                    null, null, Timestamp, Timestamp, ToneProfile.LegacyBinary);
                typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject), BindingFlags.Instance | BindingFlags.Public)!
                    .SetValue(shell, doc);
                shell.IsProjectOpen = true;
                shell.Design.SetDiscFamily(kind);
                ProjectSession.ApplyToState(doc, shell.State);
                shell.SelectWorkspace(Workspace.Design);
                shell.Design.LoadLayers(doc.Layers);

                ToneViewModel tone = shell.Design.Tone;
                await WaitToneReadyAsync(tone, $"{kind} inner");
                Require(HasBlueOverlayPixels(tone.TonePreviewImage!), $"{kind} tone preview paints blue ISO region");

                LivePreviewViewModel preview = shell.LivePreview;
                preview.SelectedFamily = kind;
                preview.IsCalibrationPreview = true;
                foreach (bool outer in new[] { false, true })
                {
                    SetOuter(shell.State, kind, outer);
                    SetPreviewSize(shell.State, kind);
                    await RunLivePreviewAsync(preview);
                    Require(preview.HasResultImage && preview.HasFileRegionOverlay && HasBlueOverlayPixels(preview.ResultImage!),
                        $"{kind} {(outer ? "outer" : "inner")} calibration projection shows blue ISO overlay");

                    string exported = Path.Combine(directory, $"ui-export-{kind}-{outer}.iso");
                    SetOutputPath(shell.State, kind, exported);
                    SetSourceImage(shell.State, kind, sourcePng);
                    shell.NavigateTo(Workspace.Export);
                    await shell.Export.GenerateCommand.ExecuteAsync(null);
                    Require(File.Exists(exported), $"{kind} ExportViewModel generated ISO");

                    preview.IsCalibrationPreview = false;
                    SetReadbackPath(shell.State, kind, exported);
                    await RunLivePreviewAsync(preview);
                    Require(preview.HasResultImage && preview.HasFileRegionOverlay && HasBlueOverlayPixels(preview.ResultImage!),
                        $"{kind} {(outer ? "outer" : "inner")} ISO readback shows blue ISO overlay");
                    preview.IsCalibrationPreview = true;

                    string? confirmMsg = null;
                    shell.ConfirmHandler = (_, msg, _) =>
                    {
                        confirmMsg = msg;
                        return Task.FromResult(true);
                    };
                    shell.NavigateTo(Workspace.Burn);
                    shell.Burn.ConfirmChecked = true;
                    await shell.Burn.StartBurnCommand.ExecuteAsync(null);
                    Require(confirmMsg is not null, $"{kind} burn displayed confirmation prompt");
                    bool hasOuterWarning = confirmMsg!.Contains("无法从该镜像引导", StringComparison.Ordinal);
                    Require(hasOuterWarning == outer, $"{kind} {(outer ? "outer" : "inner")} burn warning presence matches placement");

                    byte[] streamedBurn = burner.LastBurnedBytes!;
                    if (!outer)
                    {
                        VerifyIsoSliceVerbatim(streamedBurn, 0, bootableFsBytes, $"{kind} BurnViewModel inner");
                    }
                    else
                    {
                        VerifyUiBurnedOuterReadable(kind, streamedBurn, shell.State, bootableFsIso);
                    }
                }
            }
        }
        finally
        {
            shell.LivePreview.OnWindowClosed();
            L.SetLanguage(previousLang);
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static void VerifyUiBurnedOuterReadable(
        OpticalDiscFamily kind, byte[] burnedBytes, DiscParametersState state, string bootableFsIso)
    {
        if (kind == OpticalDiscFamily.CompactDisc)
        {
            CdDiscParameters parameters = new(
                double.Parse(state.CdInnerRadius, CultureInfo.InvariantCulture),
                double.Parse(state.CdOuterRadius, CultureInfo.InvariantCulture),
                uint.Parse(state.CdSectors, CultureInfo.InvariantCulture));
            CdDataLayoutPlan plan = CdDataImageGenerator.PreparePassthroughIsoLayout(bootableFsIso, parameters, true);
            VerifyOuterJolietFiles(burnedBytes, SampleFiles, plan.DrawingEndLbaExclusive, "CD BurnViewModel outer");
        }
        else if (kind == OpticalDiscFamily.Dvd)
        {
            DvdStreamingOptions opts = new(
                uint.Parse(state.DvdTotalSectors, CultureInfo.InvariantCulture),
                double.Parse(state.DvdInnerRadius, CultureInfo.InvariantCulture),
                double.Parse(state.DvdOuterRadius, CultureInfo.InvariantCulture));
            DvdHybridStreamingPlan plan = DvdStreamingGenerator.PreparePassthroughIso(bootableFsIso, opts, true);
            VerifyOuterJolietFiles(burnedBytes, SampleFiles, plan.DrawingEndLbaExclusive, "DVD BurnViewModel outer");
        }
        else
        {
            BluRayDiscParameters disc = new(
                TotalSectors: uint.Parse(state.BdTotalSectors, CultureInfo.InvariantCulture),
                UsableSectors: uint.Parse(state.BdUsableSectors, CultureInfo.InvariantCulture));
            BluRayIsoPlan plan = BluRayIsoGenerator.PreparePassthroughIso(disc, bootableFsIso, true);
            VerifyOuterUdfFiles(burnedBytes, disc.OutputSectors, plan.DrawingStartSector,
                plan.DrawingEndSectorExclusive, SampleFiles, "BD BurnViewModel outer");
        }
    }

    private static void VerifyOuterJolietFiles(
        byte[] image,
        IReadOnlyList<(string Path, byte[] Content)> expectedFiles,
        uint minimumFileLba,
        string label)
    {
        Dictionary<string, IsoEntry> entries = ReadJoliet(image);
        Require(entries.Count == expectedFiles.Count, $"{label}: Joliet entry count mismatch");
        foreach ((string path, byte[] expected) in expectedFiles)
        {
            Require(entries.TryGetValue(path, out IsoEntry? entry), $"{label}: missing outer file {path}");
            Require(entry!.Lba >= minimumFileLba, $"{label}: {path} placed at LBA {entry.Lba} inside drawing region");
            Require(entry.Bytes.SequenceEqual(expected), $"{label}: file bytes differ for {path}");
        }
    }

    private static void VerifyOuterUdfFiles(
        byte[] image,
        uint outputSectors,
        uint drawingStart,
        uint drawingEnd,
        IReadOnlyList<(string Path, byte[] Content)> expectedFiles,
        string label)
    {
        _ = drawingStart;
        _ = drawingEnd;
        Dictionary<string, byte[]> entries = UdfFilesystemTests.Inspect(image, outputSectors);
        Require(entries.Count == expectedFiles.Count, $"{label}: UDF entry count mismatch");
        foreach ((string path, byte[] expected) in expectedFiles)
        {
            Require(entries.TryGetValue(path, out byte[]? actual), $"{label}: missing outer UDF file {path}");
            Require(actual!.SequenceEqual(expected), $"{label}: UDF file bytes differ for {path}");
        }
    }

    private sealed record IsoEntry(uint Lba, byte[] Bytes);

    private static Dictionary<string, IsoEntry> ReadJoliet(byte[] image)
    {
        Require(image.AsSpan(17 * SectorBytes + 1, 5).SequenceEqual("CD001"u8), "Joliet descriptor");
        Dictionary<string, IsoEntry> files = new(StringComparer.Ordinal);
        ReadDirectory(17 * SectorBytes + 156, string.Empty);
        return files;

        void ReadDirectory(int record, string prefix)
        {
            int start = checked((int)ReadBoth(record + 2) * SectorBytes);
            int end = checked(start + (int)ReadBoth(record + 10));
            for (int offset = start; offset < end;)
            {
                int length = image[offset];
                if (length == 0) { offset = (offset / SectorBytes + 1) * SectorBytes; continue; }
                Require(length >= 34 && offset + length <= end, "bounded directory record");
                int idLength = image[offset + 32];
                if (idLength > 1 || (idLength == 1 && image[offset + 33] > 1))
                {
                    string name = Encoding.BigEndianUnicode.GetString(image, offset + 33, idLength);
                    if (name.EndsWith(";1", StringComparison.Ordinal)) name = name[..^2];
                    string path = prefix + name;
                    if ((image[offset + 25] & 2) != 0) ReadDirectory(offset, path + "/");
                    else
                    {
                        uint lba = ReadBoth(offset + 2), size = ReadBoth(offset + 10);
                        files.Add(path, new IsoEntry(lba, image.AsSpan(checked((int)lba * SectorBytes), checked((int)size)).ToArray()));
                    }
                }
                offset += length;
            }
        }

        uint ReadBoth(int offset)
        {
            uint le = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
            Require(le == BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(offset + 4, 4)), "ISO both-endian value");
            return le;
        }
    }

    private static void BuildPlainAndMultiExtentIso(string path, byte[] bootBytes, byte[] partA, byte[] partB)
    {
        // LBA: 16 PVD, 17 Term, 18 RootDir, 19 SubDir, 20 boot.bin, 21..22 extA, 23 extB (gap between)
        byte[] image = new byte[25 * SectorBytes];
        WriteIsoPvd(image, 16, "PLAIN_MULTI_VOL", joliet: false, rootLba: 18, rootLen: SectorBytes, volumeSectors: 25);
        image[17 * SectorBytes] = 255;
        "CD001"u8.CopyTo(image.AsSpan(17 * SectorBytes + 1));
        image[17 * SectorBytes + 6] = 1;

        int p = 18 * SectorBytes;
        p += WriteIsoDirRecord(image.AsSpan(p), "\0", 18, SectorBytes, isDir: true);
        p += WriteIsoDirRecord(image.AsSpan(p), "\x01", 18, SectorBytes, isDir: true);
        p += WriteIsoDirRecord(image.AsSpan(p), "BOOT.BIN;1", 20, (uint)bootBytes.Length, isDir: false);
        _ = WriteIsoDirRecord(image.AsSpan(p), "SUB", 19, SectorBytes, isDir: true);

        int s = 19 * SectorBytes;
        s += WriteIsoDirRecord(image.AsSpan(s), "\0", 19, SectorBytes, isDir: true);
        s += WriteIsoDirRecord(image.AsSpan(s), "\x01", 18, SectorBytes, isDir: true);
        s += WriteIsoDirRecord(image.AsSpan(s), "BIG.BIN;1", 21, (uint)partA.Length, isDir: false, moreExtents: true);
        _ = WriteIsoDirRecord(image.AsSpan(s), "BIG.BIN;1", 24, (uint)partB.Length, isDir: false, moreExtents: false);

        bootBytes.CopyTo(image.AsSpan(20 * SectorBytes));
        partA.CopyTo(image.AsSpan(21 * SectorBytes));
        partB.CopyTo(image.AsSpan(24 * SectorBytes));
        File.WriteAllBytes(path, image);
    }

    private static void BuildRockRidgeBridgeTestIso(string path, string longName, byte[] longBytes, byte[] relocBytes)
    {
        // LBA 16 PVD (Rock Ridge), 17 SVD (Joliet - must be overridden by Rock Ridge!), 18 Term,
        // 19 PVD Root, 20 Joliet Root (minimal), 21 Deep placeholder-real dir, 22 rr_moved dir,
        // 23 CE block, 24..25 longBytes, 26 relocBytes
        byte[] image = new byte[28 * SectorBytes];
        WriteIsoPvd(image, 16, "RR_VOL", joliet: false, rootLba: 19, rootLen: SectorBytes, volumeSectors: 28);
        WriteIsoPvd(image, 17, "JOLIET_SHADOW", joliet: true, rootLba: 20, rootLen: SectorBytes, volumeSectors: 28);
        image[18 * SectorBytes] = 255;
        "CD001"u8.CopyTo(image.AsSpan(18 * SectorBytes + 1));
        image[18 * SectorBytes + 6] = 1;

        // LBA 23 CE continuation holds second half of longName
        string half1 = longName[..35];
        string half2 = longName[35..];
        int ceLen = BuildNmEntry(image.AsSpan(23 * SectorBytes), half2);

        // LBA 19 PVD Root: '.', '..', longName (NM+CE), symlink (PX+SL), deep (CL->21), rr_moved (dir->22)
        int p = 19 * SectorBytes;
        byte[] sp = [(byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF, 0];
        p += WriteIsoDirRecord(image.AsSpan(p), "\0", 19, SectorBytes, isDir: true, sysUse: sp);
        p += WriteIsoDirRecord(image.AsSpan(p), "\x01", 19, SectorBytes, isDir: true);

        byte[] suLong = new byte[5 + half1.Length + 28];
        int n1 = BuildNmEntry(suLong, half1, flags: 1);
        BuildCeEntry(suLong.AsSpan(n1), 23, 0, (uint)ceLen);
        p += WriteIsoDirRecord(image.AsSpan(p), "VERYLONG.BIN;1", 24, (uint)longBytes.Length, isDir: false, sysUse: suLong[..(n1 + 28)]);

        byte[] suSym = BuildSymlinkSusp("sym.bin");
        p += WriteIsoDirRecord(image.AsSpan(p), "SYM.BIN;1", 24, 16, isDir: false, sysUse: suSym);

        byte[] suCl = new byte[5 + 4 + 12];
        int nDeep = BuildNmEntry(suCl, "deep");
        BuildClEntry(suCl.AsSpan(nDeep), 21);
        p += WriteIsoDirRecord(image.AsSpan(p), "DEEP", 20, 0, isDir: true, sysUse: suCl[..(nDeep + 12)]);

        byte[] suRr = new byte[5 + 8];
        int nRr = BuildNmEntry(suRr, "rr_moved");
        _ = WriteIsoDirRecord(image.AsSpan(p), "RR_MOVED", 22, SectorBytes, isDir: true, sysUse: suRr[..nRr]);

        // LBA 21 relocated dir ('deep')
        int d = 21 * SectorBytes;
        d += WriteIsoDirRecord(image.AsSpan(d), "\0", 21, SectorBytes, isDir: true);
        d += WriteIsoDirRecord(image.AsSpan(d), "\x01", 19, SectorBytes, isDir: true);
        byte[] suReloc = new byte[5 + 13];
        int nReloc = BuildNmEntry(suReloc, "relocated.dat");
        _ = WriteIsoDirRecord(image.AsSpan(d), "RELOCATED.DAT;1", 26, (uint)relocBytes.Length, isDir: false, sysUse: suReloc[..nReloc]);

        // LBA 22 rr_moved holding entry with RE flag -> must be ignored under rr_moved
        int m = 22 * SectorBytes;
        m += WriteIsoDirRecord(image.AsSpan(m), "\0", 22, SectorBytes, isDir: true);
        m += WriteIsoDirRecord(image.AsSpan(m), "\x01", 19, SectorBytes, isDir: true);
        byte[] suRe = [(byte)'R', (byte)'E', 4, 1];
        _ = WriteIsoDirRecord(image.AsSpan(m), "DIR1", 21, SectorBytes, isDir: true, sysUse: suRe);

        // Minimal Joliet Root at 20 so it differs visibly if wrongly selected
        int j = 20 * SectorBytes;
        j += WriteIsoDirRecord(image.AsSpan(j), "\0", 20, SectorBytes, isDir: true);
        j += WriteIsoDirRecord(image.AsSpan(j), "\x01", 20, SectorBytes, isDir: true);
        _ = WriteIsoDirRecord(image.AsSpan(j), "JOLIET_WRONG.TXT;1", 24, 5, isDir: false, joliet: true);

        longBytes.CopyTo(image.AsSpan(24 * SectorBytes));
        relocBytes.CopyTo(image.AsSpan(26 * SectorBytes));
        File.WriteAllBytes(path, image);
    }

    private static void BuildUdf250BridgeIso(string path, byte[] inlineCfg, byte[] sparseHead, byte[] sparseTail)
    {
        // Bridge ISO: ISO-9660 stub at 16/20 + UDF 2.50 VRS at 17..19, VDS at 32..39, AVDP at 256,
        // Physical Partition starting at LBA 64 (len 192 sectors):
        // Rel block 0: Metadata File FE (Tag 261) -> Metadata Partition starts at phys rel block 1 (len 10 blocks)
        // Meta block 0: FSD (Tag 256) -> Root ICB at meta block 1
        // Meta block 1: Root EFE (Tag 266) -> dir stream at meta block 2
        // Meta block 2: Root FIDs: 'sources' (dir->meta block 3), 'tiny.cfg' (file->meta block 6), 'badlink' (symlink->meta block 7)
        // Meta block 3: 'sources' EFE (Tag 266) -> dir stream at meta block 4
        // Meta block 4: 'sources' FID: 'sparse.bin' (file->meta block 5)
        // Meta block 5: 'sparse.bin' EFE (Tag 266, ShortAd): [2048 @ phys 20, 2048 unrecorded sparse, 1024 @ phys 22]
        // Meta block 6: 'tiny.cfg' EFE (Tag 266, allocType=3 inline!)
        // Meta block 7: 'badlink' EFE (Tag 266, FileType=12 symlink!)
        // Physical partition rel blocks 20 (LBA 84), 22 (LBA 86): file payload
        const uint totalSectors = 260;
        const uint partStart = 64;
        const uint partLen = 192;
        byte[] img = new byte[totalSectors * SectorBytes];

        // ISO-9660 decoy stub
        WriteIsoPvd(img, 16, "BRIDGE_STUB", joliet: false, rootLba: 20, rootLen: SectorBytes, volumeSectors: totalSectors);
        int sr = 20 * SectorBytes;
        sr += WriteIsoDirRecord(img.AsSpan(sr), "\0", 20, SectorBytes, isDir: true);
        sr += WriteIsoDirRecord(img.AsSpan(sr), "\x01", 20, SectorBytes, isDir: true);
        _ = WriteIsoDirRecord(img.AsSpan(sr), "README.TXT;1", 21, 16, isDir: false);
        "ISO9660_STUB\r\n  "u8.CopyTo(img.AsSpan(21 * SectorBytes));

        // UDF VRS at LBA 17..19
        WriteVrsSector(img, 17, "BEA01");
        WriteVrsSector(img, 18, "NSR03");
        WriteVrsSector(img, 19, "TEA01");

        // AVDP at LBA 256 pointing VDS at LBA 32 (8 sectors)
        Span<byte> avdp = img.AsSpan(256 * SectorBytes, SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(avdp.Slice(16, 4), 8 * SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(avdp.Slice(20, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(avdp.Slice(24, 4), 8 * SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(avdp.Slice(28, 4), 32);
        StampUdfTag(avdp, 2, 256, 512);

        // VDS: PVD(32), Partition(33), LVD(34), Terminating(35)
        Span<byte> uPvd = img.AsSpan(32 * SectorBytes, SectorBytes);
        WriteUdfDString(uPvd.Slice(24, 32), "WIN_UDF250");
        StampUdfTag(uPvd, 1, 32, 512);

        Span<byte> part = img.AsSpan(33 * SectorBytes, SectorBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(part.Slice(22, 2), 0); // partNum = 0
        BinaryPrimitives.WriteUInt32LittleEndian(part.Slice(188, 4), partStart);
        BinaryPrimitives.WriteUInt32LittleEndian(part.Slice(192, 4), partLen);
        StampUdfTag(part, 5, 33, 512);

        Span<byte> lvd = img.AsSpan(34 * SectorBytes, SectorBytes);
        WriteUdfDString(lvd.Slice(84, 128), "UDF250_WIN_VOL");
        BinaryPrimitives.WriteUInt32LittleEndian(lvd.Slice(212, 4), SectorBytes);
        // FSD at block 0, partRef 1 (Metadata Partition!)
        BinaryPrimitives.WriteUInt32LittleEndian(lvd.Slice(248, 4), SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(lvd.Slice(252, 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(lvd.Slice(256, 2), 1);
        // 2 Partition Maps: Map 0 = Type 1 (len 6), Map 1 = Type 2 Metadata Partition (len 64)
        BinaryPrimitives.WriteUInt32LittleEndian(lvd.Slice(264, 4), 70);
        BinaryPrimitives.WriteUInt32LittleEndian(lvd.Slice(268, 4), 2);
        lvd[440] = 1; lvd[441] = 6; // Type 1 -> partNum 0
        lvd[446] = 2; lvd[447] = 64;
        Encoding.ASCII.GetBytes("*UDF Metadata Partition").CopyTo(lvd.Slice(446 + 5));
        BinaryPrimitives.WriteUInt16LittleEndian(lvd.Slice(446 + 38, 2), 0); // phys partNum = 0
        BinaryPrimitives.WriteUInt32LittleEndian(lvd.Slice(446 + 40, 4), 0); // Metadata File FE at phys rel block 0
        BinaryPrimitives.WriteUInt32LittleEndian(lvd.Slice(446 + 44, 4), uint.MaxValue);
        StampUdfTag(lvd, 6, 34, 512);

        Span<byte> term = img.AsSpan(35 * SectorBytes, SectorBytes);
        StampUdfTag(term, 8, 35, 512);

        // Phys rel block 0 (LBA 64): Metadata File FE (Tag 261, FileType 250, ShortAd -> phys rel block 1, len 10 * 2048)
        Span<byte> metaFe = img.AsSpan((int)partStart * SectorBytes, SectorBytes);
        metaFe[27] = 250;
        BinaryPrimitives.WriteUInt64LittleEndian(metaFe.Slice(56, 8), 10 * SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(metaFe.Slice(172, 4), 8); // L_AD = 8
        BinaryPrimitives.WriteUInt32LittleEndian(metaFe.Slice(176, 4), 10 * SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(metaFe.Slice(180, 4), 1); // phys rel block 1
        StampUdfTag(metaFe, 261, 0, 184);

        uint MetaLba(uint mb) => partStart + 1 + mb;

        // Meta block 0: FSD (Tag 256) -> Root ICB @ (block 1, part 1)
        Span<byte> fsd = img.AsSpan((int)MetaLba(0) * SectorBytes, SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(fsd.Slice(400, 4), SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(fsd.Slice(404, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fsd.Slice(408, 2), 1);
        StampUdfTag(fsd, 256, 0, 512);

        // Build Root dir stream (Meta block 2)
        Span<byte> rootDir = img.AsSpan((int)MetaLba(2) * SectorBytes, SectorBytes);
        int rd = 0;
        rd += WriteUdfFid(rootDir[rd..], 2, "", isDir: true, isParent: true, icbBlock: 1, icbPart: 1);
        rd += WriteUdfFid(rootDir[rd..], 2, "sources", isDir: true, isParent: false, icbBlock: 3, icbPart: 1);
        rd += WriteUdfFid(rootDir[rd..], 2, "tiny.cfg", isDir: false, isParent: false, icbBlock: 6, icbPart: 1);
        rd += WriteUdfFid(rootDir[rd..], 2, "badlink", isDir: false, isParent: false, icbBlock: 7, icbPart: 1);

        // Meta block 1: Root EFE (Tag 266, FileType 4, ShortAd -> meta block 2)
        WriteUdfEfeWithAds(img.AsSpan((int)MetaLba(1) * SectorBytes, SectorBytes), 1, fileType: 4,
            infoLen: (ulong)rd, allocType: 0, [((uint)rd, 2u)]);

        // Meta block 4: sources dir stream
        Span<byte> srcDir = img.AsSpan((int)MetaLba(4) * SectorBytes, SectorBytes);
        int sd = 0;
        sd += WriteUdfFid(srcDir[sd..], 4, "", isDir: true, isParent: true, icbBlock: 1, icbPart: 1);
        sd += WriteUdfFid(srcDir[sd..], 4, "sparse.bin", isDir: false, isParent: false, icbBlock: 5, icbPart: 1);

        // Meta block 3: sources EFE
        WriteUdfEfeWithAds(img.AsSpan((int)MetaLba(3) * SectorBytes, SectorBytes), 3, fileType: 4,
            infoLen: (ulong)sd, allocType: 0, [((uint)sd, 4u)]);

        // Meta block 5: sparse.bin EFE (Tag 266, LongAd -> Part 0 (physical!) for payload: 2048 @ block 20, 2048 unrecorded, 1024 @ block 22)
        WriteUdfEfeSparseLongAds(img.AsSpan((int)MetaLba(5) * SectorBytes, SectorBytes), 5,
            (ulong)(sparseHead.Length + 2048 + sparseTail.Length),
            [(2048u, 20u, 0), ((1u << 30) | 2048u, 0u, 0), ((uint)sparseTail.Length, 22u, 0)]);

        // Meta block 6: tiny.cfg EFE (Tag 266, allocType = 3 inline immediate!)
        WriteUdfEfeInline(img.AsSpan((int)MetaLba(6) * SectorBytes, SectorBytes), 6, fileType: 5, inlineCfg);

        // Meta block 7: badlink EFE (Tag 266, FileType = 12 symlink!)
        WriteUdfEfeInline(img.AsSpan((int)MetaLba(7) * SectorBytes, SectorBytes), 7, fileType: 12, "target"u8);

        // Phys rel blocks 20 (LBA 84) & 22 (LBA 86)
        sparseHead.CopyTo(img.AsSpan((int)(partStart + 20) * SectorBytes));
        sparseTail.CopyTo(img.AsSpan((int)(partStart + 22) * SectorBytes));

        File.WriteAllBytes(path, img);
    }

    private static void WriteIsoPvd(byte[] image, int lba, string label, bool joliet, uint rootLba, uint rootLen, uint volumeSectors)
    {
        Span<byte> vd = image.AsSpan(lba * SectorBytes, SectorBytes);
        vd[0] = (byte)(joliet ? 2 : 1);
        "CD001"u8.CopyTo(vd.Slice(1));
        vd[6] = 1;
        if (joliet)
        {
            Span<byte> lbl = vd.Slice(40, 32);
            for (int i = 0; i < 32; i += 2) { lbl[i] = 0; lbl[i + 1] = (byte)' '; }
            Encoding.BigEndianUnicode.GetBytes(label).AsSpan(0, Math.Min(32, label.Length * 2)).CopyTo(lbl);
            vd[88] = 0x25; vd[89] = 0x2F; vd[90] = 0x45;
        }
        else
        {
            vd.Slice(40, 32).Fill((byte)' ');
            Encoding.ASCII.GetBytes(label).AsSpan(0, Math.Min(32, label.Length)).CopyTo(vd.Slice(40));
        }

        WriteBoth32(vd, 80, volumeSectors);
        BinaryPrimitives.WriteUInt16LittleEndian(vd.Slice(128, 2), SectorBytes);
        BinaryPrimitives.WriteUInt16BigEndian(vd.Slice(130, 2), SectorBytes);
        _ = WriteIsoDirRecord(vd.Slice(156, 34), "\0", rootLba, rootLen, isDir: true);
    }

    private static int WriteIsoDirRecord(
        Span<byte> dest, string name, uint lba, uint len, bool isDir,
        bool moreExtents = false, bool joliet = false, ReadOnlySpan<byte> sysUse = default)
    {
        byte[] idBytes = name == "\0" ? [0] : name == "\x01" ? [1]
            : joliet ? Encoding.BigEndianUnicode.GetBytes(name) : Encoding.ASCII.GetBytes(name);
        int pad = (idBytes.Length & 1) == 0 ? 1 : 0;
        int totalLen = 33 + idBytes.Length + pad + sysUse.Length;
        dest[0] = checked((byte)totalLen);
        WriteBoth32(dest, 2, lba);
        WriteBoth32(dest, 10, len);
        dest[18] = 126; dest[19] = 11; dest[20] = 14;
        byte flags = (byte)(isDir ? 2 : 0);
        if (moreExtents) flags |= 0x80;
        dest[25] = flags;
        dest[32] = checked((byte)idBytes.Length);
        idBytes.CopyTo(dest.Slice(33));
        if (!sysUse.IsEmpty)
            sysUse.CopyTo(dest.Slice(33 + idBytes.Length + pad));
        return totalLen;
    }

    private static void WriteBoth32(Span<byte> dest, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(offset, 4), value);
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(offset + 4, 4), value);
    }

    private static int BuildNmEntry(Span<byte> dest, string part, byte flags = 0)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(part);
        dest[0] = (byte)'N'; dest[1] = (byte)'M'; dest[2] = checked((byte)(5 + bytes.Length));
        dest[3] = 1; dest[4] = flags;
        bytes.CopyTo(dest.Slice(5));
        return dest[2];
    }

    private static void BuildCeEntry(Span<byte> dest, uint lba, uint offset, uint len)
    {
        dest[0] = (byte)'C'; dest[1] = (byte)'E'; dest[2] = 28; dest[3] = 1;
        WriteBoth32(dest, 4, lba);
        WriteBoth32(dest, 12, offset);
        WriteBoth32(dest, 20, len);
    }

    private static void BuildClEntry(Span<byte> dest, uint lba)
    {
        dest[0] = (byte)'C'; dest[1] = (byte)'L'; dest[2] = 12; dest[3] = 1;
        WriteBoth32(dest, 4, lba);
    }

    private static byte[] BuildSymlinkSusp(string name)
    {
        byte[] buf = new byte[5 + name.Length + 44 + 6];
        int n = BuildNmEntry(buf, name);
        buf[n] = (byte)'P'; buf[n + 1] = (byte)'X'; buf[n + 2] = 44; buf[n + 3] = 1;
        WriteBoth32(buf, n + 4, 0xA1FFu);
        n += 44;
        buf[n] = (byte)'S'; buf[n + 1] = (byte)'L'; buf[n + 2] = 6; buf[n + 3] = 1;
        return buf[..(n + 6)];
    }

    private static void WriteVrsSector(byte[] image, int lba, string id)
    {
        Span<byte> sector = image.AsSpan(lba * SectorBytes, SectorBytes);
        Encoding.ASCII.GetBytes(id).CopyTo(sector.Slice(1));
        sector[6] = 1;
    }

    private static void WriteUdfDString(Span<byte> dest, string value)
    {
        dest[0] = 8;
        byte[] latin = Encoding.Latin1.GetBytes(value);
        int len = Math.Min(latin.Length, dest.Length - 2);
        latin.AsSpan(0, len).CopyTo(dest.Slice(1));
        dest[^1] = checked((byte)(1 + len));
    }

    private static int WriteUdfFid(Span<byte> dest, uint dirLoc, string name, bool isDir, bool isParent, uint icbBlock, ushort icbPart)
    {
        byte[] cs0 = string.IsNullOrEmpty(name) ? [] : [8, .. Encoding.Latin1.GetBytes(name)];
        int padded = (38 + cs0.Length + 3) & ~3;
        dest[18] = (byte)((isDir ? 2 : 0) | (isParent ? 8 : 0));
        dest[19] = checked((byte)cs0.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(20, 4), SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(24, 4), icbBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(28, 2), icbPart);
        if (cs0.Length > 0) cs0.CopyTo(dest.Slice(38));
        StampUdfTag(dest, 257, dirLoc, padded);
        return padded;
    }

    private static void WriteUdfEfeWithAds(Span<byte> dest, uint blockLoc, byte fileType, ulong infoLen, int allocType,
        IReadOnlyList<(uint RawLen, uint Block)> ads)
    {
        dest[27] = fileType;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(34, 2), checked((ushort)allocType));
        BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(56, 8), infoLen);
        int lAd = ads.Count * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(212, 4), checked((uint)lAd));
        for (int i = 0; i < ads.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(216 + i * 8, 4), ads[i].RawLen);
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(220 + i * 8, 4), ads[i].Block);
        }
        StampUdfTag(dest, 266, blockLoc, 216 + lAd);
    }

    private static void WriteUdfEfeSparseLongAds(Span<byte> dest, uint blockLoc, ulong infoLen,
        IReadOnlyList<(uint RawLen, uint Block, ushort PartRef)> ads)
    {
        dest[27] = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(34, 2), 1); // LongAd
        BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(56, 8), infoLen);
        int lAd = ads.Count * 16;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(212, 4), checked((uint)lAd));
        for (int i = 0; i < ads.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(216 + i * 16, 4), ads[i].RawLen);
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(220 + i * 16, 4), ads[i].Block);
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(224 + i * 16, 2), ads[i].PartRef);
        }
        StampUdfTag(dest, 266, blockLoc, 216 + lAd);
    }

    private static void WriteUdfEfeInline(Span<byte> dest, uint blockLoc, byte fileType, ReadOnlySpan<byte> payload)
    {
        dest[27] = fileType;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(34, 2), 3); // Inline/Immediate
        BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(56, 8), (ulong)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(212, 4), checked((uint)payload.Length));
        payload.CopyTo(dest.Slice(216));
        StampUdfTag(dest, 266, blockLoc, 216 + payload.Length);
    }

    private static void StampUdfTag(Span<byte> dest, ushort tagId, uint location, int descriptorLen)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(0, 2), tagId);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(2, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(6, 2), 1);
        ushort crc = 0;
        for (int i = 16; i < descriptorLen; i++)
        {
            crc ^= (ushort)(dest[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(8, 2), crc);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(10, 2), checked((ushort)(descriptorLen - 16)));
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(12, 4), location);
        int sum = 0;
        for (int i = 0; i < 16; i++)
            if (i != 4)
                sum += dest[i];
        dest[4] = unchecked((byte)sum);
    }

    private static void SetOuter(DiscParametersState state, OpticalDiscFamily kind, bool outer)
    {
        if (kind == OpticalDiscFamily.CompactDisc) state.CdFilesAtOuterRing = outer;
        else if (kind == OpticalDiscFamily.Dvd) state.DvdFilesAtOuterRing = outer;
        else state.BdFilesAtOuterRing = outer;
    }

    private static void SetOutputPath(DiscParametersState state, OpticalDiscFamily kind, string path)
    {
        if (kind == OpticalDiscFamily.CompactDisc) state.CdOutputPath = path;
        else if (kind == OpticalDiscFamily.Dvd) state.DvdOutputPath = path;
        else state.BdOutputPath = path;
    }

    private static void SetSourceImage(DiscParametersState state, OpticalDiscFamily kind, string path)
    {
        if (kind == OpticalDiscFamily.CompactDisc) state.CdImagePath = path;
        else if (kind == OpticalDiscFamily.Dvd) state.DvdImagePath = path;
        else state.BdImagePath = path;
    }

    private static void SetReadbackPath(DiscParametersState state, OpticalDiscFamily kind, string path)
    {
        if (kind == OpticalDiscFamily.CompactDisc) state.CdTrackPath = path;
        else if (kind == OpticalDiscFamily.Dvd) state.DvdIsoPath = path;
        else state.BdIsoPath = path;
    }

    private static void SetPreviewSize(DiscParametersState state, OpticalDiscFamily kind)
    {
        if (kind == OpticalDiscFamily.CompactDisc) { state.CdPreviewSize = "128"; state.CdSamplesPerSector = "4"; }
        else if (kind == OpticalDiscFamily.Dvd) { state.DvdPreviewSize = "128"; state.DvdSamplesPerSector = "1"; }
        else state.BdPreviewSize = "128";
    }

    private static void WaitOnDispatcher(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!task.IsCompleted)
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static async Task WaitToneReadyAsync(ToneViewModel tone, string label)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (!tone.IsTonePreviewReady || tone.TonePreviewImage is null)
        {
            if (start.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException($"Tone preview timed out ({label}): {tone.TonePreviewStatus}");
            await Task.Delay(25);
        }
    }

    private static async Task RunLivePreviewAsync(LivePreviewViewModel preview)
    {
        FieldInfo revision = typeof(LivePreviewViewModel).GetField("_livePreviewRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        revision.SetValue(preview, (int)revision.GetValue(preview)! + 1);
        Task task = (Task)typeof(LivePreviewViewModel).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(preview, [])!;
        await task;
    }

    private static bool HasBlueOverlayPixels(BitmapSource bitmap)
    {
        if (bitmap.Format != PixelFormats.Bgra32)
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0xFF && pixels[i + 1] == 0x85 && pixels[i + 2] == 0x33 && pixels[i + 3] == 0xFF)
                return true;
        }
        return false;
    }

    private static void VerifyIsoSliceVerbatim(byte[] discImage, uint isoStartLba, byte[] expectedIso, string label)
    {
        int offset = checked((int)isoStartLba * SectorBytes);
        Require(discImage.Length >= offset + expectedIso.Length, $"{label}: image too short for ISO slice");
        Require(discImage.AsSpan(offset, expectedIso.Length).SequenceEqual(expectedIso),
            $"{label}: ISO bytes (MBR/GPT/PVD/El Torito/AVDP) differ from source ISO");
    }

    private static void VerifyZeroRange(byte[] discImage, uint startLba, uint sectorCount, string label)
    {
        int offset = checked((int)startLba * SectorBytes);
        int length = checked((int)sectorCount * SectorBytes);
        ReadOnlySpan<byte> slice = discImage.AsSpan(offset, length);
        for (int i = 0; i < slice.Length; i++)
        {
            if (slice[i] != 0)
                throw new InvalidOperationException($"{label}: expected zero padding at sector byte {i}.");
        }
    }

    private static void WritePatternPng(string path, int size)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            byte v = (byte)((x * 5 + y * 3) % 256);
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = v;
            pixels[offset + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Input ISO passthrough: {message}.");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Input ISO passthrough expected {typeof(T).Name}: {message}.");
    }

    private sealed class RecordingPayloadMapping : IBluRayPayloadMapping
    {
        internal System.Collections.Concurrent.ConcurrentDictionary<uint, byte> Sectors { get; } = new();

        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            Sectors.TryAdd(context.Sector, 0);
            destination.Fill((byte)(context.Sector & 0xFF));
        }
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    private sealed class RecordingBurner : IOpticalDiscBurner
    {
        internal byte[]? LastBurnedBytes { get; private set; }

        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([new("mock-recorder", "Mock Recorder", [])]);

        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => Task.FromResult(2048L);

        public Task<OpticalMediaCapacity> GetMediaCapacityAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => Task.FromResult(new OpticalMediaCapacity(2048, 2048));

        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalWriteSpeed>>([new(4500, false)]);

        public async Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            request.Validate();
            using MemoryStream ms = new();
            await Task.Run(() => request.ProduceContent(ms, cancellationToken), cancellationToken);
            LastBurnedBytes = ms.ToArray();
            return new OpticalBurnResult(request.DeviceId, request.MediaKind, request.ContentLength, TimeSpan.FromMilliseconds(50));
        }
    }
}
