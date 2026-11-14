using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace DiscImageStudio.Iso;

public sealed record RawIsoLayoutPlan(
    string SourceIsoPath,
    long SourceBytes,
    uint SourceSectors,
    uint VolumeSectors,
    uint DrawingStartLba,
    uint DrawingEndLbaExclusive,
    uint IsoStartLba,
    bool FilesAtOuterRing)
{
    public uint DrawingSectors => checked(DrawingEndLbaExclusive - DrawingStartLba);
}

/// <summary>Plans and streams a pre-built ISO verbatim alongside artwork sectors
/// without repacking its boot sectors or filesystem descriptors.</summary>
public static class RawIsoPassthrough
{
    private const int CopyBufferBytes = 1024 * 1024;
    private const int ZeroChunkSectors = 32;

    public static RawIsoLayoutPlan Plan(
        string inputIsoPath,
        uint volumeSectors,
        uint sectorAlignment = 1,
        bool filesAtOuterRing = false,
        uint minimumDrawingSectors = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIsoPath);
        ArgumentOutOfRangeException.ThrowIfZero(sectorAlignment);
        ArgumentOutOfRangeException.ThrowIfZero(minimumDrawingSectors);

        string fullPath = Path.GetFullPath(inputIsoPath.Trim());
        FileInfo info = new(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException($"Input ISO file '{fullPath}' does not exist.", fullPath);

        long length = info.Length;
        if (length == 0)
            throw new InvalidOperationException($"Input ISO file '{fullPath}' is empty.");
        if (length % Iso9660ImageWriter.LogicalSectorBytes != 0)
        {
            throw new InvalidOperationException(
                $"Input ISO file '{fullPath}' size ({length:N0} bytes) must be a multiple of {Iso9660ImageWriter.LogicalSectorBytes} bytes.");
        }

        long sectorsLong = length / Iso9660ImageWriter.LogicalSectorBytes;
        if (sectorsLong > uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"Input ISO requires {sectorsLong:N0} sectors, which fills or exceeds the {volumeSectors:N0} available sectors.");
        }

        uint sourceSectors = (uint)sectorsLong;
        if (volumeSectors == 0 || sourceSectors >= volumeSectors)
        {
            throw new InvalidOperationException(
                $"Input ISO requires {sourceSectors:N0} sectors, which fills or exceeds the {volumeSectors:N0} available sectors.");
        }

        if (!filesAtOuterRing)
        {
            uint remainder = sourceSectors % sectorAlignment;
            ulong alignedStart = remainder == 0
                ? sourceSectors
                : (ulong)sourceSectors + (sectorAlignment - remainder);
            if (alignedStart >= volumeSectors || volumeSectors - alignedStart < minimumDrawingSectors)
            {
                throw new InvalidOperationException(
                    $"Input ISO requires {sourceSectors:N0} sectors and leaves fewer than {minimumDrawingSectors:N0} aligned sectors for the artwork region.");
            }

            return new RawIsoLayoutPlan(
                fullPath,
                length,
                sourceSectors,
                volumeSectors,
                (uint)alignedStart,
                volumeSectors,
                IsoStartLba: 0u,
                FilesAtOuterRing: false);
        }

        uint availableBeforeIso = volumeSectors - sourceSectors;
        uint drawingEnd = (availableBeforeIso / sectorAlignment) * sectorAlignment;
        if (drawingEnd < minimumDrawingSectors)
        {
            throw new InvalidOperationException(
                $"Input ISO requires {sourceSectors:N0} sectors and leaves fewer than {minimumDrawingSectors:N0} aligned sectors for the artwork region.");
        }

        return new RawIsoLayoutPlan(
            fullPath,
            length,
            sourceSectors,
            volumeSectors,
            DrawingStartLba: 0u,
            DrawingEndLbaExclusive: drawingEnd,
            IsoStartLba: availableBeforeIso,
            FilesAtOuterRing: true);
    }

    public static void ValidateNotSameFile(string inputIsoPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIsoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string inputFull = Path.GetFullPath(inputIsoPath);
        string outputFull = Path.GetFullPath(outputPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(inputFull, outputFull, comparison))
            throw new ArgumentException("The output path must not overwrite the input ISO file.", nameof(outputPath));
    }

    public static void CopyFileToStream(
        string inputIsoPath,
        long expectedBytes,
        Stream output,
        Action<long>? onBytesCopied = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIsoPath);
        ArgumentNullException.ThrowIfNull(output);

        using FileStream input = new(
            inputIsoPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: CopyBufferBytes,
            FileOptions.SequentialScan);
        if (input.Length != expectedBytes)
        {
            throw new InvalidOperationException(
                $"Input ISO '{inputIsoPath}' changed on disk (expected {expectedBytes:N0} bytes, found {input.Length:N0} bytes).");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        long totalCopied = 0;
        try
        {
            while (totalCopied < expectedBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(CopyBufferBytes, expectedBytes - totalCopied);
                int read = input.Read(buffer, 0, toRead);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        $"Unexpected end of input ISO '{inputIsoPath}' after {totalCopied:N0} of {expectedBytes:N0} bytes.");
                }

                output.Write(buffer, 0, read);
                totalCopied += read;
                onBytesCopied?.Invoke(totalCopied);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static void WriteZeroSectors(
        Stream output,
        uint sectorCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (sectorCount == 0)
            return;

        int bufferBytes = Math.Min((int)sectorCount, ZeroChunkSectors) * Iso9660ImageWriter.LogicalSectorBytes;
        byte[] zeros = ArrayPool<byte>.Shared.Rent(bufferBytes);
        Array.Clear(zeros, 0, bufferBytes);
        try
        {
            long remaining = checked((long)sectorCount * Iso9660ImageWriter.LogicalSectorBytes);
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int chunk = (int)Math.Min(bufferBytes, remaining);
                output.Write(zeros, 0, chunk);
                remaining -= chunk;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    public static void WriteToFile(
        RawIsoLayoutPlan plan,
        string outputPath,
        Action<SafeFileHandle>? configureHandle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateNotSameFile(plan.SourceIsoPath, outputPath);
        string outputFullPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(outputFullPath)
            ?? throw new ArgumentException("ISO output path has no parent directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputFullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan))
            {
                configureHandle?.Invoke(output.SafeFileHandle);
                output.SetLength(checked((long)plan.VolumeSectors * Iso9660ImageWriter.LogicalSectorBytes));
                output.Position = checked((long)plan.IsoStartLba * Iso9660ImageWriter.LogicalSectorBytes);
                CopyFileToStream(plan.SourceIsoPath, plan.SourceBytes, output, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputFullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup; preserve any original failure.
            }
        }
    }
}
