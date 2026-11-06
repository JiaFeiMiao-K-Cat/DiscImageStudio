using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

/// <summary>Native IMAPI image creation only. No recorder or recording interface is created.</summary>
internal static class CdDataSubchannelModeTests
{
    private const int Sectors = 512;
    private const int RawBytes = 2448;
    private const int ProgramStart = (5 * 60 + 2) * 75;

    internal static void Run()
    {
        foreach (int sectorType in new[] { CdRawSectorType.SubcodeIsCooked, CdRawSectorType.SubcodeIsRaw })
            CheckNativeSubchannels(sectorType);
        Console.WriteLine("cd-data-subchannel-modes: passed (complete native DAO images, data ADR/CRC/timing/cadence, pregap and lead-out, independent audio byte-for-byte isolation)");
    }

    private static void CheckNativeSubchannels(int sectorType)
    {
        byte[] pcm = new byte[Sectors * 2352];
        new Random(9032026).NextBytes(pcm);
        for (int offset = 0; offset < pcm.Length; offset += 2)
            if (pcm[offset] == pcm[offset + 1]) pcm[offset + 1] ^= 0xFF;
        using var audioSession = new DynamicCdRawImageSession();
        IStream audio = audioSession.CreateImage(new MemoryContent(pcm), sectorType);
        // This reference calls native IMAPI directly, independently of the production
        // wrapper. It must retain the existing audio behavior across the entire image.
        using var nativeAudio = new NativeAudioReference(pcm, sectorType);
        using var dataSession = new CdDataRawImageSession();
        IStream data = dataSession.CreateImage(new MemoryContent(new byte[Sectors * 2048]), sectorType);
        audio.Stat(out STATSTG audioStat, 1);
        nativeAudio.Image.Stat(out STATSTG nativeStat, 1);
        data.Stat(out STATSTG dataStat, 1);
        Require(audioStat.cbSize == nativeStat.cbSize && dataStat.cbSize == nativeStat.cbSize
            && nativeStat.cbSize % RawBytes == 0, "data and audio retain the native complete DAO geometry");

        byte[] audioRaw = new byte[RawBytes], nativeRaw = new byte[RawBytes], dataRaw = new byte[RawBytes];
        int[] audioModeThreeByArea = new int[4];
        int[] sectionsByArea = new int[4];
        int[] replacedByArea = new int[4];
        int[] cadence = new int[10];
        int cadenceCount = 0;
        IntPtr readCount = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            for (long section = 0; section < nativeStat.cbSize / RawBytes; section++)
            {
                ReadExactly(audio, audioRaw, readCount);
                ReadExactly(nativeAudio.Image, nativeRaw, readCount);
                ReadExactly(data, dataRaw, readCount);
                long lba = section - ProgramStart;
                int area = lba < -150 ? 0 : lba < 0 ? 1 : lba < Sectors ? 2 : 3;
                sectionsByArea[area]++;
                Require(audioRaw.AsSpan().SequenceEqual(nativeRaw),
                    $"type {sectorType}, LBA {lba}: audio output is byte-identical to direct native IMAPI");
                if (area == 2)
                    Require(audioRaw.AsSpan(0, 2352).SequenceEqual(pcm.AsSpan(checked((int)lba * 2352), 2352)),
                        $"LBA {lba}: asymmetric audio PCM remains in its original byte order");

                byte[] audioQ = DecodeQ(audioRaw);
                byte[] dataQ = DecodeQ(dataRaw);
                Require(ValidCrc(audioQ) && ValidCrc(dataQ), $"LBA {lba}: audio and data Q CRC are valid");
                int audioMode = audioQ[0] & 15, dataMode = dataQ[0] & 15;
                Require((dataQ[0] & 0xF0) == 0x40 && dataMode is 1 or 2,
                    $"LBA {lba}: the data track contains only data-control ADR1/ADR2 packets");

                cadence[section % cadence.Length] = dataMode == 1 ? 1 : 0;
                cadenceCount = Math.Min(cadence.Length, cadenceCount + 1);
                if (cadenceCount == cadence.Length)
                    Require(cadence.Sum() >= 9, $"LBA {lba}: every ten consecutive data sections contain at least nine ADR1 packets");

                if (audioMode == 3)
                {
                    audioModeThreeByArea[area]++;
                    Require((audioQ[0] & 0x40) == 0,
                        $"LBA {lba}: native ADR3 remains an audio packet");
                    Require(area != 0 && dataMode == 1,
                        $"LBA {lba}: audio ISRC is replaced by a complete data timing packet");
                    replacedByArea[area]++;
                }
                else
                {
                    Require(dataMode == audioMode && dataQ.AsSpan(1, 9).SequenceEqual(audioQ.AsSpan(1, 9)),
                        $"LBA {lba}: existing ADR1/ADR2 data, including TOC/catalog fields, is preserved");
                }

                if (area != 0 && dataMode == 1) CheckTiming(dataQ, lba);
                // Replacing Q must leave every R-W bit intact. The existing data policy
                // clears P during program data and preserves native pause timing elsewhere.
                int preservedMask = area == 2 ? 0x3F : 0xBF;
                bool preserved = true, programPauseClear = true;
                for (int index = 2352; index < RawBytes; index++)
                {
                    preserved &= (dataRaw[index] & preservedMask) == (audioRaw[index] & preservedMask);
                    programPauseClear &= (dataRaw[index] & 0x80) == 0;
                }
                Require(preserved, $"LBA {lba}: replacing Q preserves the other subchannels");
                if (area == 2) Require(programPauseClear, "data program P remains clear");
            }
            Require(sectionsByArea[0] > 0 && sectionsByArea[1] == 150
                && sectionsByArea[2] == Sectors && sectionsByArea[3] > 0,
                "the scan covers all lead-in, all 150 pregap sections, program data and the complete lead-out");
            Require(audioModeThreeByArea[1] > 0 && audioModeThreeByArea[2] > 0
                && replacedByArea[1] == audioModeThreeByArea[1]
                && replacedByArea[2] == audioModeThreeByArea[2],
                "the native fixture exercises and repairs actual pregap and program ADR3 without removing it from audio");
            foreach (IStream stream in new[] { audio, nativeAudio.Image, data })
            {
                stream.Read(audioRaw, 1, readCount);
                Require(Marshal.ReadInt32(readCount) == 0, "the complete native image ends at its declared length");
            }
        }
        finally { Marshal.FreeCoTaskMem(readCount); }
    }

    private static void CheckTiming(byte[] q, long lba)
    {
        bool pregap = lba < 0, leadout = lba >= Sectors;
        Require(q[1] == (leadout ? 0xAA : 0x01) && q[2] == (pregap ? 0x00 : 0x01)
            && q[6] == 0, $"LBA {lba}: ADR1 track/index/reserved fields match the region");
        // Native IMAPI counts the 150-sector pregap down from 150 through 1.
        long relative = pregap ? -lba : leadout ? lba - Sectors : lba;
        Require(DecodeMsf(q.AsSpan(3, 3)) == relative && DecodeMsf(q.AsSpan(7, 3)) == lba + 150,
            $"LBA {lba}: ADR1 contains the complete relative and absolute time");
    }

    private static long DecodeMsf(ReadOnlySpan<byte> msf)
    {
        static int Bcd(byte value)
        {
            Require((value >> 4) <= 9 && (value & 15) <= 9, "time fields contain valid BCD digits");
            return (value >> 4) * 10 + (value & 15);
        }
        int minutes = Bcd(msf[0]), seconds = Bcd(msf[1]), frames = Bcd(msf[2]);
        Require(seconds < 60 && frames < 75, "time fields use valid second/frame ranges");
        return (minutes * 60L + seconds) * 75 + frames;
    }

    private static byte[] DecodeQ(byte[] raw)
    {
        byte[] q = new byte[12];
        for (int bit = 0; bit < 96; bit++)
            q[bit / 8] |= (byte)(((raw[2352 + bit] >> 6) & 1) << (7 - bit % 8));
        return q;
    }

    private static bool ValidCrc(byte[] q)
    {
        uint crc = 0;
        for (int index = 0; index < 10; index++)
        {
            crc ^= (uint)q[index] << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = ((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021u : 0)) & 0xFFFF;
        }
        return (ushort)~crc == (q[10] << 8 | q[11]);
    }

    private static void ReadExactly(IStream stream, byte[] buffer, IntPtr count)
    {
        Marshal.WriteInt32(count, -1);
        stream.Read(buffer, buffer.Length, count);
        Require(Marshal.ReadInt32(count) == buffer.Length, "native image returns a complete DAO-96 section");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD data Q modes: " + message);
    }

    private sealed class NativeAudioReference : IDisposable
    {
        private readonly object _creator;
        internal IStream Image { get; }

        internal NativeAudioReference(byte[] pcm, int sectorType)
        {
            // Only MsftRawCDImageCreator is instantiated, never DiscMaster,
            // DiscRecorder, DiscFormat2RawCD, or any device-facing COM class.
            _creator = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftRawCDImageCreator", true)!)!;
            dynamic creator = _creator;
            creator.ResultingImageType = sectorType;
            creator.AddTrack(0, new MemoryContent(pcm));
            Image = (IStream)creator.CreateResultImage();
        }

        public void Dispose()
        {
            if (Marshal.IsComObject(Image)) Marshal.FinalReleaseComObject(Image);
            if (Marshal.IsComObject(_creator)) Marshal.FinalReleaseComObject(_creator);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class MemoryContent(byte[] bytes) : IStream
    {
        private long _position;
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int count = (int)Math.Min(cb, bytes.Length - _position);
            bytes.AsSpan((int)_position, count).CopyTo(pv);
            _position += count;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, count);
        }
        public void Seek(long offset, int origin, IntPtr result)
        {
            long position = origin switch { 0 => offset, 1 => _position + offset, 2 => bytes.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            if (position < 0 || position > bytes.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            _position = position;
            if (result != IntPtr.Zero) Marshal.WriteInt64(result, position);
        }
        public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = bytes.Length };
        public void Write(byte[] a, int b, IntPtr c) => throw new NotSupportedException();
        public void SetSize(long a) => throw new NotSupportedException();
        public void CopyTo(IStream a, long b, IntPtr c, IntPtr d) => throw new NotSupportedException();
        public void Commit(int a) { }
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long a, long b, int c) => throw new NotSupportedException();
        public void UnlockRegion(long a, long b, int c) => throw new NotSupportedException();
        public void Clone(out IStream stream) => throw new NotSupportedException();
    }
}
