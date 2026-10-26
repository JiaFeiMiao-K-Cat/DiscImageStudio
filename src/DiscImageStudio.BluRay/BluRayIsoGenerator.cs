using System.Diagnostics;
using DiscImageStudio.Iso;

namespace DiscImageStudio.BluRay;

/// <summary>A full-disc drawing plan, optionally containing a fixed UDF filesystem snapshot.</summary>
public sealed class BluRayIsoPlan
{
    private readonly UdfImageWriter.LayoutPlan? _filesystem;

    internal BluRayIsoPlan(BluRayDiscParameters disc, UdfImageWriter.LayoutPlan filesystem, uint drawingStartSector)
    {
        Disc = disc;
        _filesystem = filesystem;
        DrawingStartSector = drawingStartSector;
        DrawingStartRadiusMm = new BluRayTrackGeometry(disc).AtSector(drawingStartSector).RadiusMm;
        DrawingEndSectorExclusive = filesystem.DrawingEndLbaExclusive;
        DrawingEndRadiusMm = new BluRayTrackGeometry(disc).AtSector(DrawingEndSectorExclusive).RadiusMm;
    }

    internal BluRayIsoPlan(BluRayDiscParameters disc)
    {
        Disc = disc;
        DrawingStartSector = 0;
        DrawingEndSectorExclusive = disc.TotalSectors;
        DrawingStartRadiusMm = disc.InnerRadiusMm;
        DrawingEndRadiusMm = disc.OuterRadiusMm;
    }

    internal BluRayIsoPlan(BluRayDiscParameters disc, uint drawingStartSector, uint drawingEndSectorExclusive)
    {
        disc.Validate();
        if (drawingStartSector >= drawingEndSectorExclusive
            || drawingEndSectorExclusive > disc.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(drawingEndSectorExclusive), "The drawing range must lie inside the disc.");
        }

        Disc = disc;
        DrawingStartSector = drawingStartSector;
        DrawingEndSectorExclusive = drawingEndSectorExclusive;
        DrawingStartRadiusMm = new BluRayTrackGeometry(disc).AtSector(drawingStartSector).RadiusMm;
        DrawingEndRadiusMm = new BluRayTrackGeometry(disc).AtSector(drawingEndSectorExclusive).RadiusMm;
    }

    internal UdfImageWriter.LayoutPlan Filesystem => _filesystem
        ?? throw new InvalidOperationException("This drawing plan has no filesystem.");
    public bool HasFileSystem => _filesystem is not null;
    public bool FilesAtOuterRing => _filesystem?.FilesAtOuterRing ?? false;
    public BluRayDiscParameters Disc { get; }
    public uint TotalSectors => Disc.TotalSectors;
    public long ContentLength => Disc.ContentLength;
    public string? DataDirectory => _filesystem?.SourceDirectory is { Length: > 0 } source ? source : null;
    public string? VolumeLabel => _filesystem?.VolumeLabel;
    public DateTimeOffset? UdfTimestamp => _filesystem?.Timestamp;
    public int FileCount => _filesystem?.FileCount ?? 0;
    public int DirectoryCount => _filesystem?.DirectoryCount ?? 0;
    public long FileBytes => _filesystem?.FileBytes ?? 0;
    public uint DrawingStartSector { get; }
    public double DrawingStartRadiusMm { get; }
    public uint DrawingEndSectorExclusive { get; }
    public double DrawingEndRadiusMm { get; }
}

/// <summary>Writes a UDF filesystem at the selected end, image payload in the remaining
/// clusters and backup volume metadata at the end. The drive performs physical encoding.</summary>
public static class BluRayIsoGenerator
{
    /// <summary>Draws every user-data sector when no directory is selected. Selecting
    /// a directory keeps a mountable UDF volume, including for an empty directory.</summary>
    /// <summary>A saved drawing range without a filesystem, used by calibration records and
    /// sidecar-driven previews.</summary>
    public static BluRayIsoPlan PrepareRaw(BluRayDiscParameters disc,
        uint drawingStartSector, uint drawingEndSectorExclusive)
        => new(disc, drawingStartSector, drawingEndSectorExclusive);

    public static BluRayIsoPlan PrepareDrawing(BluRayDiscParameters disc, string? dataDirectory = null,
        string volumeLabel = "BD_IMAGE", DateTimeOffset? timestamp = null, bool filesAtOuterRing = false)
    {
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
        if (filesAtOuterRing && string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Outer-ring files require a data directory.", nameof(dataDirectory));
        return string.IsNullOrWhiteSpace(dataDirectory)
            ? new BluRayIsoPlan(disc)
            : Prepare(disc, dataDirectory, volumeLabel, timestamp, filesAtOuterRing);
    }

    // Explicit UDF preparation remains available for existing saved calibration
    // records whose captured drawing bounds exclude filesystem metadata.
    public static BluRayIsoPlan Prepare(BluRayDiscParameters disc, string? dataDirectory = null,
        string volumeLabel = "BD_IMAGE", DateTimeOffset? timestamp = null, bool filesAtOuterRing = false)
    {
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
        if (dataDirectory is not null) ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        UdfImageWriter.LayoutPlan filesystem = UdfImageWriter.Plan(dataDirectory, disc.TotalSectors,
            volumeLabel, timestamp, filesAtOuterRing);
        uint drawingStart = filesystem.DrawingStartLba;
        if (drawingStart >= filesystem.DrawingEndLbaExclusive)
            throw new ArgumentException("UDF metadata and files must leave at least 32 sectors for drawing.", nameof(dataDirectory));
        return new(disc, filesystem, drawingStart);
    }

    /// <summary>The same sequential byte producer is used by ISO files and live burns.
    /// No seek, Position, Length or SetLength is required on the destination.</summary>
    public static BluRayStreamingSummary GenerateToStream(Stream output, BluRayIsoPlan plan,
        IBluRayPayloadMapping mapping, IProgress<BluRayStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mapping);
        if (!output.CanWrite) throw new ArgumentException("A writable output stream is required.", nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
        if (!plan.HasFileSystem)
            return BluRayStreamingGenerator.GenerateToStream(output,
                new(plan.Disc, 0, plan.TotalSectors), mapping, progress, cancellationToken);
        Stopwatch stopwatch = Stopwatch.StartNew();
        progress?.Report(new(0, plan.ContentLength, stopwatch.Elapsed));
        using ProgressStream destination = new(output, plan.ContentLength, stopwatch, progress, cancellationToken);
        UdfImageWriter.WriteSequentialPrefix(plan.Filesystem, destination, plan.DrawingStartSector, cancellationToken);
        BluRayStreamingGenerator.GenerateToStream(destination,
            new(plan.Disc, plan.DrawingStartSector, plan.DrawingEndSectorExclusive - plan.DrawingStartSector), mapping,
            cancellationToken: cancellationToken);
        UdfImageWriter.WriteSequentialSuffix(plan.Filesystem, destination, plan.DrawingEndSectorExclusive, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.BytesWritten != plan.ContentLength)
            throw new IOException("Blu-ray ISO generation did not produce the planned byte length.");
        return new(0, plan.TotalSectors, destination.BytesWritten, stopwatch.Elapsed);
    }

    /// <summary>Publishes only a complete ISO; cancellation or failure preserves an existing file.</summary>
    public static BluRayStreamingSummary GenerateFile(string outputPath, BluRayIsoPlan plan,
        IBluRayPayloadMapping mapping, IProgress<BluRayStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mapping);
        ValidateOutputPath(outputPath, plan);
        cancellationToken.ThrowIfCancellationRequested();
        BluRayStreamingSummary? summary = null;
        BluRayOutputPublisher.Write(outputPath, stream =>
            summary = GenerateToStream(stream, plan, mapping, progress, cancellationToken), cancellationToken);
        return summary!;
    }

    public static void ValidateOutputPath(string outputPath, BluRayIsoPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.DataDirectory is not { } source) return;
        string destination = Path.GetFullPath(outputPath);
        if (Iso9660ImageWriter.IsWithinDirectory(destination, source)
            || Iso9660ImageWriter.IsWithinDirectory(ResolveDirectoryLinks(destination), ResolveDirectoryLinks(source)))
            throw new ArgumentException("ISO output must be outside the source directory to preserve source files.", nameof(outputPath));
    }

    private static string ResolveDirectoryLinks(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        string current = root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }

    private sealed class ProgressStream(Stream output, long totalBytes, Stopwatch stopwatch,
        IProgress<BluRayStreamingProgress>? progress, CancellationToken token) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer);
            BytesWritten = checked(BytesWritten + buffer.Length);
            progress?.Report(new(BytesWritten, totalBytes, stopwatch.Elapsed));
        }
        // The caller owns the destination, including when streaming directly to a drive.
    }
}
