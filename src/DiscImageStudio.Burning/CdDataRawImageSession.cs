using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

/// <summary>
/// Converts a cooked Mode 1 ISO into a DAO-96 image. IMAPI's image creator only
/// accepts audio, so it is used solely for the standard lead-in/TOC/lead-out and
/// subchannel timing. Every program sector is replaced by a fully encoded Mode 1
/// sector, and Q identifies a data track throughout the session.
/// </summary>
internal sealed class CdDataRawImageSession : ICdRawImageSession
{
    private readonly DynamicCdRawImageSession _templateSession = new();
    private CdDataRawImageStream? _image;
    private bool _disposed;

    public long StartOfLeadout => _templateSession.StartOfLeadout;

    public IStream CreateImage(IStream isoContent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(isoContent);
        if (_image is not null) throw new InvalidOperationException("The CD DAO image has already been created.");
        isoContent.Stat(out STATSTG stat, 1);
        if (stat.cbSize < 300L * 2048 || stat.cbSize % 2048 != 0)
            throw new ArgumentException("CD DAO data tracks require at least 300 complete 2048-byte sectors.", nameof(isoContent));
        long sectors = stat.cbSize / 2048;
        IStream template = _templateSession.CreateImage(new ZeroAudioStream(checked(sectors * 2352)));
        if (_templateSession.StartOfLeadout != sectors)
            throw new InvalidOperationException("IMAPI changed the requested CD data track length.");
        _image = new CdDataRawImageStream(template, isoContent, sectors);
        return _image;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _image?.Dispose();
        _templateSession.Dispose();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ZeroAudioStream(long length) : IStream
    {
        private long _position;
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            ArgumentNullException.ThrowIfNull(pv);
            if ((uint)cb > pv.Length) throw new ArgumentOutOfRangeException(nameof(cb));
            int count = (int)Math.Min(cb, length - _position);
            Array.Clear(pv, 0, count);
            _position += count;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, count);
        }
        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            long target = origin switch { 0 => offset, 1 => checked(_position + offset), 2 => checked(length + offset), _ => -1 };
            if (target < 0 || target > length) throw new COMException("Invalid template position.", unchecked((int)0x80030001));
            _position = target;
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, _position);
        }
        public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = length, grfMode = 0 };
        public void Write(byte[] p, int c, IntPtr n) => throw new NotSupportedException();
        public void SetSize(long size) => throw new NotSupportedException();
        public void CopyTo(IStream stream, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void Commit(int flags) { }
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void Clone(out IStream stream) => throw new NotSupportedException();
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class CdDataRawImageStream : IStream, IDisposable
{
    internal const int RawSectorBytes = 2448;
    // CreateResultImage starts at MSF 95:00:00; program LBA 0 is MSF 00:02:00.
    // https://learn.microsoft.com/windows/win32/api/imapi2/nf-imapi2-irawcdimagecreator-createresultimage
    internal const int ProgramStartSector = (5 * 60 + 2) * 75;
    private readonly IStream _template;
    private readonly IStream _iso;
    private readonly long _sectors;
    private readonly long _length;
    private readonly byte[] _frame = new byte[RawSectorBytes];
    private readonly byte[] _payload = new byte[2048];
    private readonly IntPtr _readCount;
    private long _position;
    private int _frameOffset = RawSectorBytes;
    private bool _hasRead;
    private bool _disposed;

    internal CdDataRawImageStream(IStream template, IStream iso, long sectors)
    {
        _template = template;
        _iso = iso;
        _sectors = sectors;
        template.Stat(out STATSTG stat, 1);
        _length = stat.cbSize;
        if (_length % RawSectorBytes != 0 || _length <= (ProgramStartSector + sectors) * RawSectorBytes)
            throw new InvalidOperationException("IMAPI returned an invalid DAO-96 template length.");
        _readCount = Marshal.AllocCoTaskMem(sizeof(int));
    }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pv);
        if ((uint)cb > pv.Length) throw new ArgumentOutOfRangeException(nameof(cb));
        int copied = 0;
        while (copied < cb && _position < _length)
        {
            if (_frameOffset == RawSectorBytes) ReadFrame();
            int take = Math.Min(cb - copied, RawSectorBytes - _frameOffset);
            Buffer.BlockCopy(_frame, _frameOffset, pv, copied, take);
            _frameOffset += take;
            _position += take;
            copied += take;
        }
        _hasRead |= copied > 0;
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, copied);
    }

    private void ReadFrame()
    {
        ReadExactly(_template, _frame);
        long lba = _position / RawSectorBytes - ProgramStartSector;
        bool program = lba >= 0 && lba < _sectors;
        if (program) ReadExactly(_iso, _payload);
        else Array.Clear(_payload);
        // The lead-in main channel is structured data too. Its 95..99 minute
        // addresses wrap to 00:00:00 at the start of the 150-sector pregap.
        long addressLba = lba < -150 ? lba + 100 * 60 * 75 : lba;
        CdMode1SectorEncoder.Encode(_payload, addressLba, _frame.AsSpan(0, 2352));
        PatchDataSubchannel(_frame.AsSpan(2352, 96), program);
        _frameOffset = 0;
    }

    private void ReadExactly(IStream input, byte[] destination)
    {
        // Both the native template and GeneratedContentComStream fill requests,
        // but accept a short-read IStream without losing sector boundaries.
        int offset = 0;
        byte[] buffer = destination;
        while (offset < destination.Length)
        {
            int needed = destination.Length - offset;
            if (offset > 0) buffer = new byte[needed];
            Marshal.WriteInt32(_readCount, 0);
            input.Read(buffer, needed, _readCount);
            int count = Marshal.ReadInt32(_readCount);
            if (count <= 0 || count > needed) throw new EndOfStreamException("CD DAO source ended inside a sector.");
            if (offset > 0) Buffer.BlockCopy(buffer, 0, destination, offset, count);
            offset += count;
        }
    }

    internal static void PatchDataSubchannel(Span<byte> subchannel, bool program)
    {
        if (subchannel.Length != 96) throw new ArgumentException("Expected a 96-byte subchannel block.");
        Span<byte> q = stackalloc byte[12];
        q.Clear();
        for (int bit = 0; bit < 96; bit++)
            q[bit / 8] |= (byte)(((subchannel[bit] >> 6) & 1) << (7 - bit % 8));
        q[0] = (byte)((q[0] & 0x0F) | 0x40); // Data, no pre-emphasis, no digital-copy flag.
        ushort crc = 0;
        for (int index = 0; index < 10; index++)
        {
            crc ^= (ushort)(q[index] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        crc = (ushort)~crc;
        q[10] = (byte)(crc >> 8);
        q[11] = (byte)crc;
        for (int bit = 0; bit < 96; bit++)
        {
            // Keep the template's P pause timing outside the data track and its
            // R-W bits everywhere. Data in the final 150 sectors is still data.
            int mask = program ? 0x3F : 0xBF;
            subchannel[bit] = (byte)((subchannel[bit] & mask) | (((q[bit / 8] >> (7 - bit % 8)) & 1) << 6));
        }
    }

    public void Seek(long offset, int origin, IntPtr newPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = origin switch { 0 => offset, 1 => checked(_position + offset), 2 => checked(_length + offset), _ => -1 };
        if (target < 0 || target > _length) throw new COMException("Invalid DAO image position.", unchecked((int)0x80030001));
        if (!_hasRead && (target == 0 || target == _length))
        {
            _template.Seek(target, 0, IntPtr.Zero);
            _position = target;
            _frameOffset = RawSectorBytes;
        }
        else if (target < _position)
            throw new COMException("The CD data DAO image is forward-only.", unchecked((int)0x80030001));
        else if (target > _position)
        {
            byte[] discard = new byte[64 * 1024];
            while (_position < target) Read(discard, (int)Math.Min(discard.Length, target - _position), IntPtr.Zero);
        }
        if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, _position);
    }

    public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = _length, grfMode = 0 };
    public void CopyTo(IStream target, long count, IntPtr read, IntPtr written)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        long totalWritten = 0;
        while (total < count && _position < _length)
        {
            long before = _position;
            Read(buffer, (int)Math.Min(buffer.Length, count - total), IntPtr.Zero);
            int taken = checked((int)(_position - before));
            total += taken;
            int offset = 0;
            while (offset < taken)
            {
                byte[] remaining = offset == 0 ? buffer : buffer.AsSpan(offset, taken - offset).ToArray();
                Marshal.WriteInt32(_readCount, 0);
                target.Write(remaining, taken - offset, _readCount);
                int copied = Marshal.ReadInt32(_readCount);
                if (copied <= 0 || copied > taken - offset)
                    throw new IOException("The CD DAO copy target did not accept the requested bytes.");
                offset += copied;
                totalWritten += copied;
            }
        }
        if (read != IntPtr.Zero) Marshal.WriteInt64(read, total);
        if (written != IntPtr.Zero) Marshal.WriteInt64(written, totalWritten);
    }
    public void Write(byte[] p, int c, IntPtr n) => throw new NotSupportedException();
    public void SetSize(long size) => throw new NotSupportedException();
    public void Commit(int flags) { }
    public void Revert() => throw new NotSupportedException();
    public void LockRegion(long offset, long count, int type) => throw new NotSupportedException();
    public void UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
    public void Clone(out IStream stream) => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marshal.FreeCoTaskMem(_readCount);
    }
}
