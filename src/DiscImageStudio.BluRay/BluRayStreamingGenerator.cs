using System.Diagnostics;
using System.Numerics;
using DiscImageStudio.BluRay.Encoding;

namespace DiscImageStudio.BluRay;

public readonly record struct BluRaySectorContext(uint Sector, uint PhysicalSectorNumber, BluRayDiscParameters Disc);

/// <summary>Fills all 2048 user-byte positions in the scrambled domain. An image mapping
/// may use any strategy; the stream writer does not choose an image mapping.</summary>
public interface IBluRayPayloadMapping
{
    void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination);
}

// Only built-in mappings expose batching capabilities. Wrappers must preserve
// the source's opt-in and sampling semantics instead of hiding its fast paths.
internal interface IBluRayBatchPayloadMapping : IBluRayPayloadMapping
{
    bool CanGenerateInParallel(BluRayDiscParameters disc);
    bool TryFillScrambledCluster(BluRayDiscParameters disc, uint firstSector,
        Span<byte> destination, CancellationToken cancellationToken);
}

/// <summary>Explicit numeric probe, not an optical luminance calibration.</summary>
public sealed class BluRayConstantByteMapping(byte value) : IBluRayPayloadMapping
{
    public byte Value { get; } = value;
    public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination) => destination.Fill(Value);
}

public sealed record BluRayStreamingOptions(BluRayDiscParameters Disc, uint StartSector = 0, uint SectorCount = 32)
{
    public long ContentLength => (long)SectorCount * BluRayDiscParameters.BytesPerSector;
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Disc);
        Disc.Validate();
        if (StartSector % 32 != 0 || SectorCount == 0 || SectorCount % 32 != 0
            || StartSector >= Disc.TotalSectors || SectorCount > Disc.TotalSectors - StartSector)
            throw new ArgumentOutOfRangeException(nameof(SectorCount), "BD range must be inside the disc, start on a 32-sector boundary, and contain a positive multiple of 32 sectors.");
    }
}

public sealed record BluRayStreamingProgress(long CompletedBytes, long TotalBytes, TimeSpan Elapsed)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

public sealed record BluRayStreamingSummary(uint StartSector, uint SectorCount, long BytesWritten, TimeSpan Elapsed);

public static class BluRayStreamingGenerator
{
    public const string PendingMappingMessage = "请提供蓝光数据映射；图片可使用 BluRayRunLengthMapping 的 3T / 6T 数量偏向策略。";

    public static BluRayStreamingSummary GenerateToStream(Stream output, BluRayStreamingOptions options,
        IBluRayPayloadMapping? mapping, IProgress<BluRayStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(output, options, mapping, cancellationToken);
        Stopwatch stopwatch = Stopwatch.StartNew();
        long completed = 0;
        progress?.Report(new(0, options.ContentLength, stopwatch.Elapsed));
        // Built-in raster and explicitly thread-safe pattern samplers share
        // the same bounded batches. Other mappings retain sequential invocation.
        int workers = mapping is IBluRayBatchPayloadMapping batch && batch.CanGenerateInParallel(options.Disc)
            ? Math.Min(16, Math.Max(1, Environment.ProcessorCount - 1)) : 1;
        int batchCapacity = (int)Math.Min(options.SectorCount / 32, (uint)(workers == 1 ? 1 : workers * 2));
        byte[][] clusters = new byte[batchCapacity][];
        for (int i = 0; i < clusters.Length; i++)
            clusters[i] = new byte[BluRayDiscParameters.PayloadBytesPerCluster];
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken };
        for (uint offset = 0; offset < options.SectorCount;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min((options.SectorCount - offset) / 32, (uint)batchCapacity);
            if (workers == 1 || count == 1)
                FillCluster(clusters[0], options.Disc, options.StartSector + offset, mapping!, cancellationToken);
            else
                Parallel.For(0, count, parallelOptions, index => FillCluster(clusters[index], options.Disc,
                    options.StartSector + offset + (uint)index * 32, mapping!, cancellationToken));
            // A batch contains at most 32 clusters (2 MiB). All output and
            // progress callbacks stay ordered on the calling thread. Workers
            // have joined before a slow consumer can block the next batch.
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(clusters[index]);
                completed += clusters[index].Length;
                progress?.Report(new(completed, options.ContentLength, stopwatch.Elapsed));
            }
            offset += (uint)count * 32;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(options.StartSector, options.SectorCount, completed, stopwatch.Elapsed);
    }

    private static void FillCluster(byte[] cluster, BluRayDiscParameters disc, uint firstSector,
        IBluRayPayloadMapping mapping, CancellationToken cancellationToken)
    {
        uint psn = disc.GetPhysicalSectorNumber(firstSector);
        // §13.4 resets to the SAME seed for every sector in a cluster.
        Span<byte> mask = stackalloc byte[BluRayDiscParameters.BytesPerSector];
        BluRayScrambler.FillMask(psn, mask);
        bool filledCluster = mapping is IBluRayBatchPayloadMapping batch
            && batch.TryFillScrambledCluster(disc, firstSector, cluster, cancellationToken);
        for (int sector = 0; sector < 32; sector++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> payload = cluster.AsSpan(sector * 2048, 2048);
            if (!filledCluster)
            {
                payload.Clear();
                mapping.FillScrambledPayload(new(firstSector + (uint)sector, psn + (uint)sector, disc), payload);
            }
            int index = 0;
            if (Vector.IsHardwareAccelerated)
            {
                for (; index <= payload.Length - Vector<byte>.Count; index += Vector<byte>.Count)
                {
                    Vector<byte> values = new(payload[index..]);
                    Vector<byte> scramble = new(mask[index..]);
                    (values ^ scramble).CopyTo(payload[index..]);
                }
            }
            for (; index < payload.Length; index++) payload[index] ^= mask[index];
        }
    }

    /// <summary>Publishes only a complete output; cancellation/failure preserves existing files.</summary>
    public static BluRayStreamingSummary GenerateFile(string outputPath, BluRayStreamingOptions options,
        IBluRayPayloadMapping? mapping, IProgress<BluRayStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (mapping is null) throw new InvalidOperationException(PendingMappingMessage);
        cancellationToken.ThrowIfCancellationRequested();
        BluRayStreamingSummary? summary = null;
        BluRayOutputPublisher.Write(outputPath, stream =>
            summary = GenerateToStream(stream, options, mapping, progress, cancellationToken), cancellationToken);
        return summary!;
    }

    private static void Validate(Stream output, BluRayStreamingOptions options, IBluRayPayloadMapping? mapping,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (mapping is null) throw new InvalidOperationException(PendingMappingMessage);
        if (!output.CanWrite) throw new ArgumentException("A writable output stream is required.", nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal static class BluRayOutputPublisher
{
    internal static void Write(string outputPath, Action<Stream> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string finalPath = Path.GetFullPath(outputPath);
        string directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
