using System.Buffers.Binary;

namespace DiscImageStudio.Iso;

public sealed record RawIsoImageInfo(string Path, long SectorCount, string FileSystem);

/// <summary>Validates and copies logical-sector ISO images without rewriting their contents.</summary>
public static class RawIsoImage
{
    public const int LogicalSectorBytes = Iso9660ImageWriter.LogicalSectorBytes;
    private const int FirstVolumeDescriptorSector = 16;
    private const int MaximumVolumeDescriptorSectors = 64;

    public static RawIsoImageInfo Inspect(string path, long maximumSectors = long.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumSectors <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSectors));
        string fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Raw image sources must use the .iso extension.", nameof(path));

        FileInfo file = new(fullPath);
        if (!file.Exists) throw new FileNotFoundException("The selected ISO image does not exist.", fullPath);
        if (file.Length == 0 || file.Length % LogicalSectorBytes != 0)
            throw new InvalidDataException("ISO image length must be a non-zero multiple of 2048 bytes.");

        long sectors = file.Length / LogicalSectorBytes;
        if (sectors > maximumSectors)
            throw new ArgumentException($"ISO image needs {sectors:N0} sectors, but the target has only {maximumSectors:N0}.");
        if (sectors <= FirstVolumeDescriptorSector)
            throw new InvalidDataException("ISO image is too small to contain a recognized volume descriptor.");

        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            LogicalSectorBytes, FileOptions.RandomAccess);
        byte[] descriptor = new byte[LogicalSectorBytes];
        bool hasPrimaryVolumeDescriptor = false;
        uint declaredVolumeSectors = 0;
        bool hasUdfBeginning = false;
        bool hasUdfPartition = false;
        bool hasUdfTerminator = false;
        long descriptorEnd = Math.Min(sectors, MaximumVolumeDescriptorSectors);
        for (long sector = FirstVolumeDescriptorSector; sector < descriptorEnd; sector++)
        {
            stream.Position = checked(sector * LogicalSectorBytes);
            stream.ReadExactly(descriptor);

            if (descriptor[0] == 1
                && descriptor.AsSpan(1, 5).SequenceEqual("CD001"u8)
                && descriptor[6] == 1)
            {
                uint littleEndian = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(80, 4));
                uint bigEndian = BinaryPrimitives.ReadUInt32BigEndian(descriptor.AsSpan(84, 4));
                if (littleEndian == 0 || littleEndian != bigEndian || littleEndian > sectors)
                    throw new InvalidDataException("ISO primary volume descriptor has an invalid volume size.");
                hasPrimaryVolumeDescriptor = true;
                declaredVolumeSectors = littleEndian;
            }

            ReadOnlySpan<byte> identifier = descriptor.AsSpan(1, 5);
            hasUdfBeginning |= identifier.SequenceEqual("BEA01"u8);
            hasUdfPartition |= identifier.SequenceEqual("NSR02"u8) || identifier.SequenceEqual("NSR03"u8);
            hasUdfTerminator |= identifier.SequenceEqual("TEA01"u8);
        }

        string? fileSystem = hasPrimaryVolumeDescriptor ? "ISO9660" : null;
        if (hasUdfBeginning && hasUdfPartition && hasUdfTerminator)
            fileSystem = fileSystem is null ? "UDF" : "ISO9660/UDF";
        if (fileSystem is null)
            throw new InvalidDataException("The ISO contains neither a recognized ISO9660 volume descriptor nor a UDF recognition sequence.");

        return new(fullPath, sectors, fileSystem);
    }

    public static void CopyPrefixToFile(string sourcePath, string destinationPath, long destinationSectors,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (destinationSectors <= 0) throw new ArgumentOutOfRangeException(nameof(destinationSectors));
        RawIsoImageInfo source = Inspect(sourcePath, destinationSectors);
        string destination = Path.GetFullPath(destinationPath);
        if (destination.Equals(source.Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output must not overwrite the source ISO.", nameof(destinationPath));

        using FileStream output = new(destination, FileMode.Open, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.SequentialScan);
        long expectedLength = checked(destinationSectors * LogicalSectorBytes);
        if (output.Length != expectedLength)
            throw new ArgumentException("The output image length does not match the target sector count.", nameof(destinationPath));
        output.Position = 0;
        using FileStream input = new(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        CopyExactly(input, output, checked(source.SectorCount * LogicalSectorBytes), cancellationToken);
        output.Flush(flushToDisk: true);
    }

    public static RawIsoPrefixOverlayStream OverlayPrefix(Stream output, string sourcePath,
        long maximumSectors = long.MaxValue)
        => new(output, Inspect(sourcePath, maximumSectors));

    public static void CopyExactly(Stream input, Stream output, long byteCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead) throw new ArgumentException("A readable input stream is required.", nameof(input));
        if (!output.CanWrite) throw new ArgumentException("A writable output stream is required.", nameof(output));
        if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
        byte[] buffer = new byte[1024 * 1024];
        long remaining = byteCount;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = input.Read(buffer, 0, requested);
            if (read == 0) throw new EndOfStreamException("ISO image ended before its declared length.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}

/// <summary>Replaces the beginning of a forward-only output stream with exact source ISO bytes.</summary>
public sealed class RawIsoPrefixOverlayStream : Stream
{
    private const int CopyBufferBytes = 1024 * 1024;
    private readonly Stream _output;
    private readonly FileStream _source;
    private readonly byte[] _copyBuffer = new byte[CopyBufferBytes];
    private long _remainingSourceBytes;
    private bool _disposed;

    internal RawIsoPrefixOverlayStream(Stream output, RawIsoImageInfo source)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("ISO output stream must be writable.", nameof(output));
        _output = output;
        _source = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        _remainingSourceBytes = checked(source.SectorCount * RawIsoImage.LogicalSectorBytes);
    }

    public long RemainingSourceBytes => _remainingSourceBytes;

    public void Complete()
    {
        if (_remainingSourceBytes != 0)
            throw new IOException("The output ended before the complete source ISO prefix was copied.");
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && _output.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _output.Flush();

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int sourceBytes = (int)Math.Min(buffer.Length, _remainingSourceBytes);
        int remaining = sourceBytes;
        while (remaining > 0)
        {
            int count = Math.Min(remaining, _copyBuffer.Length);
            Span<byte> sourceBuffer = _copyBuffer.AsSpan(0, count);
            _source.ReadExactly(sourceBuffer);
            _output.Write(sourceBuffer);
            remaining -= count;
            _remainingSourceBytes -= count;
        }
        if (sourceBytes < buffer.Length)
            _output.Write(buffer[sourceBytes..]);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing) _source.Dispose();
        _disposed = true;
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
