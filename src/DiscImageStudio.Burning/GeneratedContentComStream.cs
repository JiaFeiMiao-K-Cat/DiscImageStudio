using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Channels;

namespace DiscImageStudio.Burning;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class GeneratedContentComStream : IStream, IDisposable
{
    internal const int ChunkBytes = OpticalBurnBufferOptions.ChunkBytes;
    internal const int BufferCapacityBytes = 64 * 1024 * 1024;
    internal const int PrebufferBytes = 32 * 1024 * 1024;
    private const int StgTypeStream = 2;
    private const int StgEInvalidFunction = unchecked((int)0x80030001);
    private const int ENotImpl = unchecked((int)0x80004001);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly long _length;
    private readonly Channel<byte[]> _channel;
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _prebufferReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _producerTask;
    private readonly Action<long, long>? _progress;
    private readonly Func<long> _getTimestamp;
    private ProducerOutputStream? _producerOutput;
    private Exception? _producerFailure;
    private int _producerState;
    private long _readBytes;
    private int _readerWaitingForData;
    private long _readerWaitCount;
    private byte[]? _currentChunk;
    private int _currentOffset;
    private long _position;
    private long _lastProgressTimestamp;
    private bool _hasRead;
    private bool _hasReportedProgress;
    private bool _disposed;
    internal OpticalBurnBufferOptions BufferOptions { get; }

    internal GeneratedContentComStream(
        long length,
        Action<Stream, CancellationToken> producer,
        Action<long, long>? progress,
        CancellationToken cancellationToken,
        OpticalBurnBufferOptions? bufferOptions = null,
        Func<long>? timestampProvider = null)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ArgumentNullException.ThrowIfNull(producer);
        BufferOptions = bufferOptions ?? OpticalBurnBufferOptions.Default;
        BufferOptions.Validate();
        _length = length;
        _progress = progress;
        _getTimestamp = timestampProvider ?? Stopwatch.GetTimestamp;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(BufferOptions.CapacityBytes / ChunkBytes)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        long prebufferBytes = Math.Min(length, BufferOptions.PrebufferBytes);
        _producerTask = Task.Factory.StartNew(
            () => Produce(producer, prebufferBytes),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    internal void WaitUntilPrebuffered(CancellationToken cancellationToken)
        => _prebufferReady.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();

    // Independent, approximate samples of already-owned state. Never query the
    // channel, wait for the producer, or use the seek position as bytes consumed:
    // IMAPI may seek to the end and back while inspecting a stream's size.
    internal GeneratedContentDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        ProducerOutputStream? output = Volatile.Read(ref _producerOutput);
        Exception? failure = Volatile.Read(ref _producerFailure);
        long read = Interlocked.Read(ref _readBytes);
        long published = output?.PublishedBytes ?? 0;
        long generated = output?.GeneratedBytes ?? 0;
        return new(
            _length, generated, published, read, Math.Max(0, published - read),
            BufferOptions.CapacityBytes, Math.Min(_length, BufferOptions.PrebufferBytes),
            _prebufferReady.Task.IsCompletedSuccessfully,
            Volatile.Read(ref _producerState) switch
            {
                1 => "Completed",
                2 => "Failed",
                3 => "Cancelled",
                _ => "Running",
            },
            _cancellation.IsCancellationRequested,
            output?.WaitingForBuffer ?? false,
            Volatile.Read(ref _readerWaitingForData) != 0,
            Interlocked.Read(ref _readerWaitCount),
            failure?.GetType().FullName, failure?.HResult);
    }

    internal string GetDiagnosticSummary() => GetDiagnosticSnapshot().ToDiagnosticSummary();

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pv);
        if (cb < 0 || cb > pv.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(cb));
        }

        int totalRead = 0;
        while (totalRead < cb && _position < _length)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (_currentChunk is null || _currentOffset == _currentChunk.Length)
            {
                if (!TryReadNextChunk())
                {
                    break;
                }
            }

            int available = _currentChunk!.Length - _currentOffset;
            int count = Math.Min(cb - totalRead, available);
            Buffer.BlockCopy(_currentChunk, _currentOffset, pv, totalRead, count);
            _currentOffset += count;
            totalRead += count;
            _position += count;
            Interlocked.Add(ref _readBytes, count);
        }

        if (totalRead == 0 && _position < _length)
        {
            AwaitProducerCompletion();
            throw new EndOfStreamException(
                $"Generated stream ended at {_position} bytes; expected {_length} bytes.");
        }

        _hasRead |= totalRead > 0;

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbRead, totalRead);
        }

        if (totalRead > 0) ReportReadProgress();
    }

    private void ReportReadProgress()
    {
        if (_progress is null) return;
        // This remains synchronous on the IStream reader's thread. Throttle
        // before constructing/posting UI events, while always keeping the first
        // successful read and the last byte. EOF reads do not repeat completion.
        long now = _getTimestamp();
        if (_hasReportedProgress && _position != _length
            && Stopwatch.GetElapsedTime(_lastProgressTimestamp, now) < ProgressInterval)
            return;
        _lastProgressTimestamp = now;
        _hasReportedProgress = true;
        try
        {
            _progress(_position, _length);
        }
        catch
        {
            // UI progress must never interrupt the recorder's IStream read.
            // A failing observer must not bypass throttling on subsequent reads.
        }
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => ThrowReadOnly();

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        long requested = dwOrigin switch
        {
            0 => dlibMove,
            1 => checked(_position + dlibMove),
            2 => checked(_length + dlibMove),
            _ => -1,
        };
        bool canSeekBeforeReading = !_hasRead && (requested == 0 || requested == _length);
        if (requested < 0 || (requested != _position && !canSeekBeforeReading))
        {
            throw new COMException("The generated burn stream is forward-only.", StgEInvalidFunction);
        }

        _position = requested;

        if (plibNewPosition != IntPtr.Zero)
        {
            Marshal.WriteInt64(plibNewPosition, _position);
        }
    }

    public void SetSize(long libNewSize) => ThrowReadOnly();

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        ArgumentNullException.ThrowIfNull(pstm);
        byte[] buffer = new byte[64 * 1024];
        long remaining = cb;
        long readTotal = 0;
        while (remaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            long before = _position;
            Read(buffer, requested, IntPtr.Zero);
            int read = checked((int)(_position - before));
            if (read == 0)
            {
                break;
            }

            pstm.Write(buffer, read, IntPtr.Zero);
            remaining -= read;
            readTotal += read;
        }

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbRead, readTotal);
        }

        if (pcbWritten != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbWritten, readTotal);
        }
    }

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert() => ThrowNotImplemented();

    public void LockRegion(long libOffset, long cb, int dwLockType) => ThrowNotImplemented();

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => ThrowNotImplemented();

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            type = StgTypeStream,
            cbSize = _length,
            grfMode = 0,
        };
    }

    public void Clone(out IStream ppstm)
    {
        ppstm = null!;
        ThrowNotImplemented();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        try
        {
            _producerTask.GetAwaiter().GetResult();
        }
        catch
        {
            // A recorder error or explicit cancellation may stop the producer early.
        }

        _cancellation.Dispose();
    }

    private void Produce(Action<Stream, CancellationToken> producer, long prebufferBytes)
    {
        try
        {
            using ProducerOutputStream output = new(
                _channel.Writer,
                _length,
                prebufferBytes,
                _prebufferReady,
                _cancellation.Token);
            Volatile.Write(ref _producerOutput, output);
            producer(output, _cancellation.Token);
            output.Complete();
            Volatile.Write(ref _producerState, 1);
            _channel.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _producerFailure, exception);
            Volatile.Write(ref _producerState, exception is OperationCanceledException ? 3 : 2);
            _prebufferReady.TrySetException(exception);
            _channel.Writer.TryComplete(exception);
            throw;
        }
    }

    private bool TryReadNextChunk()
    {
        try
        {
            while (WaitForNextChunk())
            {
                if (_channel.Reader.TryRead(out byte[]? chunk))
                {
                    _currentChunk = chunk;
                    _currentOffset = 0;
                    return true;
                }
            }
        }
        catch
        {
            AwaitProducerCompletion();
            throw;
        }

        AwaitProducerCompletion();
        return false;
    }

    private bool WaitForNextChunk()
    {
        ValueTask<bool> wait = _channel.Reader.WaitToReadAsync(_cancellation.Token);
        if (wait.IsCompleted) return wait.GetAwaiter().GetResult();
        Interlocked.Increment(ref _readerWaitCount);
        Volatile.Write(ref _readerWaitingForData, 1);
        try { return wait.AsTask().GetAwaiter().GetResult(); }
        finally { Volatile.Write(ref _readerWaitingForData, 0); }
    }

    private void AwaitProducerCompletion()
    {
        try
        {
            _producerTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new COMException(
                $"Image generation failed while streaming to the recorder: {exception.Message}",
                exception.HResult);
        }
    }

    private static void ThrowReadOnly()
        => throw new COMException("The generated burn stream is read-only.", StgEInvalidFunction);

    private static void ThrowNotImplemented()
        => throw new COMException("Operation is not implemented for the generated burn stream.", ENotImpl);

    private sealed class ProducerOutputStream(
        ChannelWriter<byte[]> writer,
        long expectedLength,
        long prebufferBytes,
        TaskCompletionSource prebufferReady,
        CancellationToken cancellationToken) : Stream
    {
        private readonly byte[] _buffer = new byte[ChunkBytes];
        private int _buffered;
        private long _written;
        private bool _completed;
        private int _waitingForBuffer;

        internal long PublishedBytes => Interlocked.Read(ref _written);
        internal long GeneratedBytes
        {
            get
            {
                long published = PublishedBytes;
                // A publish and a snapshot can overlap. Clamp the independent
                // partial-block sample rather than synchronizing the writer.
                int partial = Volatile.Read(ref _buffered);
                return published + Math.Min(Math.Max(0, partial), expectedLength - published);
            }
        }
        internal bool WaitingForBuffer => Volatile.Read(ref _waitingForBuffer) != 0;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => expectedLength;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            // Before prebuffering, partial chunks must not fill the bounded queue
            // before its byte threshold is reached. Complete always publishes the tail.
            if (_written + _buffered >= prebufferBytes) PublishBuffer();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
            {
                throw new ArgumentException("Offset and count exceed the source buffer.");
            }

            while (count > 0)
            {
                int copy = Math.Min(count, _buffer.Length - _buffered);
                Buffer.BlockCopy(buffer, offset, _buffer, _buffered, copy);
                _buffered += copy;
                offset += copy;
                count -= copy;
                if (_buffered == _buffer.Length)
                {
                    PublishBuffer();
                }
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int copy = Math.Min(buffer.Length, _buffer.Length - _buffered);
                buffer[..copy].CopyTo(_buffer.AsSpan(_buffered));
                _buffered += copy;
                buffer = buffer[copy..];
                if (_buffered == _buffer.Length)
                {
                    PublishBuffer();
                }
            }
        }

        public override void WriteByte(byte value)
        {
            _buffer[_buffered++] = value;
            if (_buffered == _buffer.Length)
            {
                PublishBuffer();
            }
        }

        internal void Complete()
        {
            if (_completed)
            {
                return;
            }

            PublishBuffer();
            if (_written != expectedLength)
            {
                throw new InvalidDataException(
                    $"Generator produced {_written} bytes; expected {expectedLength} bytes.");
            }

            _completed = true;
            prebufferReady.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void PublishBuffer()
        {
            if (_buffered == 0)
            {
                return;
            }

            long nextWritten = checked(_written + _buffered);
            if (nextWritten > expectedLength)
            {
                throw new InvalidDataException(
                    $"Generator exceeded its declared length of {expectedLength} bytes.");
            }

            byte[] chunk = new byte[_buffered];
            Buffer.BlockCopy(_buffer, 0, chunk, 0, _buffered);
            ValueTask publish = writer.WriteAsync(chunk, cancellationToken);
            if (!publish.IsCompleted) Volatile.Write(ref _waitingForBuffer, 1);
            try { publish.AsTask().GetAwaiter().GetResult(); }
            finally { Volatile.Write(ref _waitingForBuffer, 0); }
            Interlocked.Exchange(ref _written, nextWritten);
            _buffered = 0;
            if (_written >= prebufferBytes)
            {
                prebufferReady.TrySetResult();
            }
        }
    }
}

// PublishedBytes counts blocks accepted by the host queue; BufferedBytes also
// includes any unconsumed part of the reader's current block. QueueCapacityBytes
// limits only the channel, not that block or the producer's private staging block.
internal sealed record GeneratedContentDiagnosticSnapshot(
    long TotalBytes,
    long GeneratedBytes,
    long PublishedBytes,
    long ReadBytes,
    long BufferedBytes,
    int QueueCapacityBytes,
    long PrebufferThresholdBytes,
    bool PrebufferReady,
    string ProducerState,
    bool CancellationRequested,
    bool ProducerWaitingForBuffer,
    bool ReaderWaitingForData,
    long ReaderWaitCount,
    string? ProducerExceptionType,
    int? ProducerHResult)
{
    internal string ToDiagnosticSummary()
        => FormattableString.Invariant($"生成流缓存（各字段为瞬时近似值；不代表盘片写入量）：TotalBytes={TotalBytes}；GeneratedBytes={GeneratedBytes}；PublishedBytes={PublishedBytes}；ReadBytes={ReadBytes}；BufferedBytes={BufferedBytes}；")
            + FormattableString.Invariant($"QueueCapacityBytes={QueueCapacityBytes}；PrebufferThresholdBytes={PrebufferThresholdBytes}；PrebufferReady={PrebufferReady}；ProducerState={ProducerState}；CancellationRequested={CancellationRequested}；")
            + FormattableString.Invariant($"ProducerWaitingForBuffer={ProducerWaitingForBuffer}；ReaderWaitingForData={ReaderWaitingForData}；ReaderWaitCount={ReaderWaitCount}；ProducerExceptionType={ProducerExceptionType ?? "none"}；ProducerHRESULT={(ProducerHResult is int value ? $"0x{value:X8}" : "none")}。");
}
