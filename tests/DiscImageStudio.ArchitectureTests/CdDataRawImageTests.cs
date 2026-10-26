using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class CdDataRawImageTests
{
    private const int Sectors = 300;
    private const int RawBytes = 2448;
    private const int ProgramStart = (5 * 60 + 2) * 75;

    internal static void Run()
    {
        // Only in-memory IMAPI image creation is used: no recorder, preparation,
        // WriteMedia, finalization, or other optical device operation occurs.
        VerifyNativeImage();
        VerifyAtipGeometryAndInputGuards();
        VerifyGeneratedContent();
        VerifyInnerAndOuterIsoRoundTrip();
        Console.WriteLine("cd-data-raw-image: passed (ATIP length, inner/outer ISO round-trip, Mode 1 bytes, data TOC/Q CRC, P pause, partial reads and forward-only streams)");
    }

    private static void VerifyNativeImage()
    {
        byte[] iso = new byte[Sectors * 2048];
        new Random(982349).NextBytes(iso);
        BufferStream source = new(iso, maxRead: 73, allowSeek: false);
        using CdDataRawImageSession session = new();
        IStream image = session.CreateImage(source);
        using DynamicCdRawImageSession referenceSession = new();
        IStream reference = referenceSession.CreateImage(new BufferStream(new byte[Sectors * 2352]));
        image.Stat(out STATSTG stat, 1);
        reference.Stat(out STATSTG referenceStat, 1);
        Require(session.StartOfLeadout == Sectors, "all ISO sectors remain in the program area");
        Require(stat.cbSize == referenceStat.cbSize, "DAO framing length matches the native template");
        Require(source.BytesRead == 0, "creating the image does not consume its ISO");

        // IMAPI probes IStream length this way before its first real read.
        image.Seek(0, 2, IntPtr.Zero);
        image.Seek(0, 0, IntPtr.Zero);
        Require(source.BytesRead == 0, "End-to-Start length probe leaves a forward-only ISO untouched");
        HashSet<byte> tocPoints = [];
        for (int index = 0; index < 18; index++)
        {
            byte[] actual = Read(image, RawBytes, fragmented: true);
            byte[] expected = Read(reference, RawBytes);
            VerifySubchannel(actual, expected, program: false);
            byte[] q = DecodeQ(actual);
            if ((q[0] & 15) == 1) tocPoints.Add(q[2]);
            VerifyMainHeader(actual, 427350 + index);
        }
        Require(new byte[] { 1, 0xA0, 0xA1, 0xA2 }.All(tocPoints.Contains), "track 1 and A0/A1/A2 TOC all identify data");

        image.Seek((long)(ProgramStart - 151) * RawBytes, 0, IntPtr.Zero);
        reference.Seek((long)(ProgramStart - 151) * RawBytes, 0, IntPtr.Zero);
        byte[] finalLeadin = Read(image, RawBytes, fragmented: true);
        VerifySubchannel(finalLeadin, Read(reference, RawBytes), program: false);
        VerifyMainHeader(finalLeadin, 449849); // 99:59:74.
        byte[] firstPregap = Read(image, RawBytes, fragmented: true);
        VerifySubchannel(firstPregap, Read(reference, RawBytes), program: false);
        VerifyMainHeader(firstPregap, -150); // 00:00:00 follows the address wrap.

        // A partial forward seek ends within the final pregap subchannel.
        image.Seek((long)ProgramStart * RawBytes - 17, 0, IntPtr.Zero);
        Require(source.BytesRead == 0, "lead-in/pregap forward seek does not consume program data");
        reference.Seek((long)(ProgramStart - 1) * RawBytes, 0, IntPtr.Zero);
        byte[] pregap = Read(reference, RawBytes);
        CdDataRawImageStream.PatchDataSubchannel(pregap.AsSpan(2352), program: false);
        Require(Read(image, 17, fragmented: true).AsSpan().SequenceEqual(pregap.AsSpan(RawBytes - 17)),
            "partial pregap read reaches the program boundary exactly");

        byte[] expectedMain = new byte[2352];
        for (int lba = 0; lba < Sectors; lba++)
        {
            byte[] actual = Read(image, RawBytes, fragmented: true);
            byte[] expected = Read(reference, RawBytes);
            VerifySubchannel(actual, expected, program: true);
            VerifyMainHeader(actual, lba);
            CdMode1SectorEncoder.Encode(iso.AsSpan(lba * 2048, 2048), lba, expectedMain);
            Require(actual.AsSpan(0, 2352).SequenceEqual(expectedMain), $"all Mode 1 bytes survive at LBA {lba}");
            Require(source.BytesRead == (long)(lba + 1) * 2048, "short source reads preserve ISO sector boundaries");
        }
        Require(source.SeekCalls == 0 && source.BytesRead == iso.Length, "ISO is read once, forward-only, with no trimming");
        byte[] leadout = Read(image, RawBytes, fragmented: true);
        byte[] expectedLeadout = Read(reference, RawBytes);
        VerifySubchannel(leadout, expectedLeadout, program: false);
        VerifyMainHeader(leadout, Sectors);
        Require(DecodeQ(leadout)[1] == 0xAA, "lead-out begins immediately after the last ISO sector");
        CdMode1SectorEncoder.Encode(new byte[2048], Sectors, expectedMain);
        Require(leadout.AsSpan(0, 2352).SequenceEqual(expectedMain), "lead-out main channel is Mode 1 with zero payload");
        Throws<COMException>(() => image.Seek(0, 0, IntPtr.Zero), "rewind is rejected after reading");

        image.Seek(-31, 2, IntPtr.Zero);
        Require(Read(image, 31, fragmented: true).Length == 31, "forward seek/read reaches exact declared end");
        byte[] eof = new byte[1];
        IntPtr read = Marshal.AllocCoTaskMem(sizeof(int));
        try { image.Read(eof, 1, read); Require(Marshal.ReadInt32(read) == 0, "EOF returns zero bytes"); }
        finally { Marshal.FreeCoTaskMem(read); }
    }

    private static void VerifyAtipGeometryAndInputGuards()
    {
        const long ritekMedicalAtip = 359845;
        BufferStream declaredIso = new([], declaredLength: ritekMedicalAtip * 2048, allowSeek: false);
        using CdDataRawImageSession session = new();
        IStream image = session.CreateImage(declaredIso);
        image.Stat(out STATSTG stat, 1);
        Require(session.StartOfLeadout == ritekMedicalAtip, "Ritek Medical ATIP 359845 is retained, without a two-sector deduction");
        Require(stat.cbSize == (ProgramStart + ritekMedicalAtip + 6750) * RawBytes, "ATIP geometry determines DAO length");
        Require(declaredIso.BytesRead == 0, "full-disc image construction is lazy");
        Throws<InvalidOperationException>(() => session.CreateImage(declaredIso), "session cannot be reused");

        foreach (long invalid in new[] { 299L * 2048, 300L * 2048 + 1 })
        {
            using CdDataRawImageSession rejected = new();
            Throws<ArgumentException>(() => rejected.CreateImage(new BufferStream([], declaredLength: invalid)),
                "short or partial tracks are rejected instead of padded");
        }
        using CdDataRawImageSession truncatedSession = new();
        IStream truncated = truncatedSession.CreateImage(new BufferStream(new byte[2047], declaredLength: Sectors * 2048L));
        truncated.Seek((long)ProgramStart * RawBytes, 0, IntPtr.Zero);
        Throws<EndOfStreamException>(() => truncated.Read(new byte[RawBytes], RawBytes, IntPtr.Zero),
            "truncated ISO is rejected instead of writing a partial sector");
    }

    private static void VerifyGeneratedContent()
    {
        byte[] iso = new byte[Sectors * 2048];
        new Random(84925).NextBytes(iso);
        using GeneratedContentComStream source = new(iso.Length, (output, _) => output.Write(iso), null, CancellationToken.None);
        source.WaitUntilPrebuffered(CancellationToken.None);
        using CdDataRawImageSession session = new();
        IStream image = session.CreateImage(source);
        // Skipping part of a data sector must consume only the corresponding
        // forward-only source; the buffered remainder is returned next.
        const int lba = 137;
        const int skip = 2301;
        image.Seek((long)(ProgramStart + lba) * RawBytes + skip, 0, IntPtr.Zero);
        byte[] expected = new byte[2352];
        CdMode1SectorEncoder.Encode(iso.AsSpan(lba * 2048, 2048), lba, expected);
        Require(Read(image, 2352 - skip, fragmented: true).AsSpan().SequenceEqual(expected.AsSpan(skip)),
            "GeneratedContentComStream works across a partial program-sector seek");
        BufferStream destination = new([], maxWrite: 11);
        IntPtr read = Marshal.AllocCoTaskMem(sizeof(long));
        IntPtr written = Marshal.AllocCoTaskMem(sizeof(long));
        try
        {
            image.CopyTo(destination, 96, read, written);
            Require(Marshal.ReadInt64(read) == 96 && Marshal.ReadInt64(written) == 96,
                "CopyTo accounts for short target writes");
            Require(destination.Written.Length == 96, "CopyTo retains the complete subchannel");
        }
        finally { Marshal.FreeCoTaskMem(read); Marshal.FreeCoTaskMem(written); }
    }

    private static void VerifyInnerAndOuterIsoRoundTrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cd-dao-round-trip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string data = Path.Combine(directory, "files");
            Directory.CreateDirectory(Path.Combine(data, "资料"));
            File.WriteAllBytes(Path.Combine(data, "资料", "非整扇区.bin"),
                Enumerable.Range(0, 17009).Select(index => (byte)(index * 71 + 3)).ToArray());
            File.WriteAllText(Path.Combine(data, "readme.txt"), "CD DAO 文件模式：内圈与外圈内容一致。");
            string picture = Path.Combine(directory, "source.png");
            byte[] pixels = Enumerable.Range(0, 16 * 16).Select(index => (byte)(index * 53)).ToArray();
            PngBitmapEncoder png = new();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96,
                PixelFormats.Gray8, null, pixels, 16)));
            using (FileStream output = File.Create(picture)) png.Save(output);

            // Independently expand the ECMA-130 x^15 + x + 1 sequence by
            // recurrence, instead of reusing the production encoder's LFSR.
            byte[] mask = new byte[2352 - 12];
            bool[] bits = new bool[mask.Length * 8];
            bits[0] = true;
            for (int bit = 15; bit < bits.Length; bit++) bits[bit] = bits[bit - 15] ^ bits[bit - 14];
            for (int bit = 0; bit < bits.Length; bit++)
                if (bits[bit]) mask[bit / 8] |= (byte)(1 << (bit % 8));
            Require(mask.AsSpan(0, 16).SequenceEqual(new byte[]
                { 0x01, 0x80, 0x00, 0x60, 0x00, 0x28, 0x00, 0x1E, 0x80, 0x08, 0x60, 0x06, 0xA8, 0x02, 0xFE, 0x81 }),
                "independent decoder matches the standard scrambling vector");

            CdDiscParameters parameters = new(24.5, 56.8, Sectors);
            foreach (bool outer in new[] { false, true })
            {
                CdDataLayoutPlan layout = CdDataImageGenerator.PrepareLayout(data, parameters, "DAO_TEST", outer);
                using MemoryStream expectedStream = new();
                CdDataImageGenerator.GenerateToStream(picture, expectedStream, parameters, layout);
                byte[] expectedIso = expectedStream.ToArray();
                using GeneratedContentComStream source = new(expectedIso.Length,
                    (output, cancellation) => CdDataImageGenerator.GenerateToStream(picture, output,
                        parameters, layout, cancellationToken: cancellation), null, CancellationToken.None);
                source.WaitUntilPrebuffered(CancellationToken.None);
                using CdDataRawImageSession session = new();
                IStream image = session.CreateImage(source);
                image.Seek((long)ProgramStart * RawBytes, 0, IntPtr.Zero);
                byte[] recoveredIso = new byte[expectedIso.Length];
                for (int lba = 0; lba < Sectors; lba++)
                {
                    byte[] raw = Read(image, RawBytes, fragmented: true);
                    Require(raw[0] == 0 && raw[11] == 0 && raw.AsSpan(1, 10).IndexOfAnyExcept((byte)0xFF) < 0,
                        "RAW data retains the standard unswapped sync sequence");
                    Require((raw[15] ^ mask[3]) == 1, "decoded main channel is Mode 1");
                    for (int offset = 0; offset < 2048; offset++)
                        recoveredIso[lba * 2048 + offset] = (byte)(raw[16 + offset] ^ mask[4 + offset]);
                }
                Require(recoveredIso.AsSpan().SequenceEqual(expectedIso),
                    $"{(outer ? "outer" : "inner")} ISO files, directories, drawing bytes and final sector round-trip exactly");
                Require(session.StartOfLeadout == Sectors && layout.FilesAtOuterRing == outer,
                    "file placement and radial sector geometry remain unchanged");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void VerifySubchannel(byte[] actual, byte[] template, bool program)
    {
        byte[] q = DecodeQ(actual);
        byte[] expected = DecodeQ(template);
        Require((q[0] & 0xF0) == 0x40 && (q[0] & 15) == (expected[0] & 15), "data control preserves Q ADR");
        Require(q.AsSpan(1, 9).SequenceEqual(expected.AsSpan(1, 9)), "Q track/index/timestamps and TOC addresses are unchanged");
        // A valid Q record including its complemented CRC has this CCITT residue.
        int residue = 0;
        foreach (byte value in q)
            for (int bit = 7; bit >= 0; bit--)
            {
                int carry = ((residue >> 15) ^ (value >> bit)) & 1;
                residue = ((residue << 1) & 0xFFFF) ^ (carry != 0 ? 0x1021 : 0);
            }
        Require(residue == 0x1D0F, "Q CRC is valid after setting data control");
        for (int bit = 0; bit < 96; bit++)
        {
            Require((actual[2352 + bit] & 0x3F) == (template[2352 + bit] & 0x3F), "R-W subchannels are preserved");
            Require((actual[2352 + bit] & 0x80) == (program ? 0 : template[2352 + bit] & 0x80),
                "P is clear throughout data, including its last sector, and retained outside data");
        }
    }

    private static void VerifyMainHeader(byte[] frame, int lba)
    {
        // First scramble bytes are fixed by ECMA-130: 01 80 00 60.
        Require(frame[0] == 0 && frame[11] == 0 && frame.AsSpan(1, 10).IndexOfAnyExcept((byte)0xFF) < 0,
            "lead-in, pregap, program and lead-out have Mode 1 synchronization");
        int minute = FromBcd(frame[12] ^ 0x01);
        int second = FromBcd(frame[13] ^ 0x80);
        int sector = FromBcd(frame[14]);
        Require(minute * 60 * 75 + second * 75 + sector == lba + 150,
            "independently decoded Mode 1 header follows absolute sector address");
        Require((frame[15] ^ 0x60) == 1, "independently decoded header selects Mode 1");
        static int FromBcd(int value)
        {
            Require((value >> 4) < 10 && (value & 15) < 10, "header uses valid BCD");
            return (value >> 4) * 10 + (value & 15);
        }
    }

    private static byte[] DecodeQ(byte[] frame)
    {
        byte[] q = new byte[12];
        for (int bit = 0; bit < 96; bit++)
            q[bit / 8] |= (byte)(((frame[2352 + bit] >> 6) & 1) << (7 - bit % 8));
        return q;
    }

    private static byte[] Read(IStream stream, int count, bool fragmented = false)
    {
        byte[] result = new byte[count];
        int[] fragments = [1, 7, 95, 2351, 97, 13];
        IntPtr read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            int offset = 0;
            int index = 0;
            while (offset < count)
            {
                int requested = Math.Min(count - offset, fragmented ? fragments[index++ % fragments.Length] : count);
                byte[] part = new byte[requested];
                stream.Read(part, requested, read);
                Require(Marshal.ReadInt32(read) == requested, "complete requested image fragment");
                part.CopyTo(result, offset);
                offset += requested;
            }
        }
        finally { Marshal.FreeCoTaskMem(read); }
        return result;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class BufferStream(byte[] bytes, int maxRead = int.MaxValue, bool allowSeek = true,
        long? declaredLength = null, int maxWrite = int.MaxValue) : IStream
    {
        private long _position;
        private readonly MemoryStream _written = new();
        public long BytesRead { get; private set; }
        public int SeekCalls { get; private set; }
        public byte[] Written => _written.ToArray();
        public void Read(byte[] buffer, int count, IntPtr read)
        {
            int taken = (int)Math.Min(Math.Min(count, maxRead), Math.Max(0, bytes.Length - _position));
            bytes.AsSpan(checked((int)_position), taken).CopyTo(buffer);
            _position += taken;
            BytesRead += taken;
            if (read != IntPtr.Zero) Marshal.WriteInt32(read, taken);
        }
        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            SeekCalls++;
            if (!allowSeek) throw new COMException("Test source is forward-only.");
            _position = origin switch { 0 => offset, 1 => _position + offset, 2 => (declaredLength ?? bytes.LongLength) + offset, _ => throw new ArgumentException() };
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, _position);
        }
        public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = declaredLength ?? bytes.LongLength };
        public void Write(byte[] buffer, int count, IntPtr written)
        {
            int taken = Math.Min(count, maxWrite);
            _written.Write(buffer, 0, taken);
            if (written != IntPtr.Zero) Marshal.WriteInt32(written, taken);
        }
        public void SetSize(long size) => throw new NotSupportedException();
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void Commit(int flags) { }
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void Clone(out IStream stream) => throw new NotSupportedException();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD data RAW image: " + message);
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("CD data RAW image: " + message);
    }
}
