using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

/// <summary>
/// Hands consecutive parts of one forward-only stream to a consumer that wants one stream
/// per part, as the IMAPI raw CD image creator wants one per track. The parts must be read
/// in order and each to its end before the next starts, which is how the creator reads its
/// tracks while the disc is written; a read out of order fails instead of delivering the
/// wrong bytes. The parent stream is not owned.
/// </summary>
internal sealed class SequentialSliceSource
{
    private readonly IStream _parent;
    private readonly IntPtr _countPointer = Marshal.AllocHGlobal(sizeof(long));
    private readonly object _gate = new();
    private long _parentPosition;
    private long _nextOffset;
    private bool _released;

    internal SequentialSliceSource(IStream parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _parent = parent;
    }

    /// <summary>The next <paramref name="length"/> bytes of the parent as a stream.</summary>
    internal IStream Take(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        long offset = _nextOffset;
        _nextOffset = checked(offset + length);
        return new Slice(this, offset, length);
    }

    internal long AssignedBytes => _nextOffset;

    internal void Release()
    {
        lock (_gate)
        {
            if (_released) return;
            _released = true;
            Marshal.FreeHGlobal(_countPointer);
        }
    }

    private int Read(long offset, byte[] buffer, int count)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            if (offset != _parentPosition)
            {
                throw new IOException(
                    $"CD 轨道流未按顺序读取：请求偏移 {offset:N0}，源流位于 {_parentPosition:N0}。");
            }

            Marshal.WriteInt32(_countPointer, 0);
            _parent.Read(buffer, count, _countPointer);
            int read = Marshal.ReadInt32(_countPointer);
            if (read < 0 || read > count) throw new IOException("CD 源流返回了无效的读取长度。");
            _parentPosition += read;
            return read;
        }
    }

    private sealed class Slice(SequentialSliceSource source, long offset, long length) : IStream
    {
        private long _position;

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            ArgumentNullException.ThrowIfNull(pv);
            if (cb < 0 || cb > pv.Length) throw new ArgumentOutOfRangeException(nameof(cb));
            int requested = (int)Math.Min(cb, length - _position);
            int read = requested <= 0 ? 0 : source.Read(offset + _position, pv, requested);
            _position += read;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long target = dwOrigin switch
            {
                0 => dlibMove,
                1 => checked(_position + dlibMove),
                2 => checked(length + dlibMove),
                _ => throw new ArgumentOutOfRangeException(nameof(dwOrigin)),
            };
            if (target < 0 || target > length) throw new IOException("CD 轨道流的定位超出范围。");
            // The position is only remembered: a read checks it against the parent, so a
            // size probe (seek to the end and back) is harmless and a real jump is refused.
            _position = target;
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, target);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag)
            => pstatstg = new STATSTG { type = 2, cbSize = length, grfMode = 0 };

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();

        public void SetSize(long libNewSize) => throw new NotSupportedException();

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();

        public void Commit(int grfCommitFlags)
        {
        }

        public void Revert() => throw new NotSupportedException();

        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

        public void Clone(out IStream ppstm) => throw new NotSupportedException();
    }
}
