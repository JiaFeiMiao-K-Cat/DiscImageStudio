using DiscImageStudio.Iso;

namespace DvdImageSolver;

internal sealed record HybridIsoWriteSummary(
    string Mode,
    uint VolumeSectors,
    int LogicalSectorBytes,
    string SourceDirectory,
    string FileSystem,
    int FileCount,
    int DirectoryCount,
    long FileBytes,
    uint DataEndLbaExclusive,
    bool SparseFile,
    bool Iso9660,
    bool Joliet,
    bool FilesAtOuterRing = false,
    uint MetadataEndLbaExclusive = 0,
    uint FileDataStartLba = 0);

/// <summary>Compatibility facade for the media-independent ISO9660/Joliet writer.</summary>
public static class HybridIsoImageWriter
{
    public sealed class LayoutPlan
    {
        internal LayoutPlan(Iso9660ImageWriter.LayoutPlan layout) { Layout = layout; Udf = null; }
        internal LayoutPlan(UdfImageWriter.LayoutPlan layout) { Layout = null; Udf = layout; }
        internal Iso9660ImageWriter.LayoutPlan? Layout { get; }
        internal UdfImageWriter.LayoutPlan? Udf { get; }
        public string SourceDirectory => Layout?.SourceDirectory ?? Udf!.SourceDirectory;
        public uint VolumeSectors => Layout?.VolumeSectors ?? Udf!.VolumeSectors;
        public int FileCount => Layout?.FileCount ?? Udf!.FileCount;
        public int DirectoryCount => Layout?.DirectoryCount ?? Udf!.DirectoryCount;
        public long FileBytes => Layout?.FileBytes ?? Udf!.FileBytes;
        public uint DataEndLbaExclusive => Layout?.DataEndLbaExclusive ?? Udf!.DataEndLbaExclusive;
        public uint MetadataEndLbaExclusive => Layout?.MetadataEndLbaExclusive ?? (Udf!.FilesAtOuterRing ? Udf.DrawingStartLba : Udf.DataEndLbaExclusive);
        public uint FileDataStartLba => Layout?.FileDataStartLba ?? Udf!.PartitionStart;
        public bool FilesAtOuterRing => Layout?.FilesAtOuterRing ?? Udf!.FilesAtOuterRing;
        public string FileSystem => Layout is null ? "udf250" : "cdfs";
        public bool IsUdf250 => Udf is not null;
        public uint DrawingEndLbaExclusive => Layout?.VolumeSectors ?? Udf!.DrawingEndLbaExclusive;
        public bool RequiresSuffixAfterDrawing => IsUdf250 && !FilesAtOuterRing;
    }

    public static LayoutPlan Plan(string sourceDirectory, uint volumeSectors, string volumeLabel,
        bool filesAtOuterRing = false, string fileSystem = "cdfs")
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        if (!fileSystem.Equals("cdfs", StringComparison.OrdinalIgnoreCase)
            && !fileSystem.Equals("udf250", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Filesystem must be cdfs or udf250.", nameof(fileSystem));
        return fileSystem.Equals("udf250", StringComparison.OrdinalIgnoreCase)
            ? new(UdfImageWriter.Plan(sourceDirectory, volumeSectors, volumeLabel,
                filesAtOuterRing: filesAtOuterRing, revision: UdfImageWriter.Revision.V2_50))
            : new(Iso9660ImageWriter.Plan(sourceDirectory, volumeSectors, volumeLabel,
                filesAtOuterRing: filesAtOuterRing));
    }

    public static void WriteOuterRingPrefix(LayoutPlan plan, Stream output, uint endLbaExclusive,
        CancellationToken cancellationToken = default)
    {
        if (plan.Layout is not null)
            Iso9660ImageWriter.WriteOuterRingPrefix(plan.Layout, output, endLbaExclusive, cancellationToken);
        else
            UdfImageWriter.WriteSequentialPrefix(plan.Udf!, output, endLbaExclusive, cancellationToken);
    }

    public static void WriteOuterRingSuffix(LayoutPlan plan, Stream output, uint startLba,
        CancellationToken cancellationToken = default)
    {
        if (plan.Layout is not null)
            Iso9660ImageWriter.WriteOuterRingSuffix(plan.Layout, output, startLba, cancellationToken);
        else
            UdfImageWriter.WriteSequentialSuffix(plan.Udf!, output, startLba, cancellationToken);
    }

    public static void WriteSequentialPrefix(LayoutPlan plan, Stream output, uint endLbaExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Layout is not null)
            Iso9660ImageWriter.WriteSequentialPrefix(plan.Layout, output, endLbaExclusive, cancellationToken);
        else
            UdfImageWriter.WriteSequentialPrefix(plan.Udf!, output, endLbaExclusive, cancellationToken);
    }

    public static void WriteSuffixAfterDrawing(LayoutPlan plan, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.RequiresSuffixAfterDrawing)
            return;
        UdfImageWriter.WriteSequentialSuffix(plan.Udf!, output, plan.DrawingEndLbaExclusive, cancellationToken);
    }

    public static void WriteSuffixAfterDrawing(LayoutPlan plan, string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.RequiresSuffixAfterDrawing)
            return;
        using FileStream output = new(outputPath, FileMode.Open, FileAccess.Write, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.SequentialScan);
        output.Position = checked((long)plan.DrawingEndLbaExclusive * IsoImageWriter.LogicalSectorBytes);
        WriteSuffixAfterDrawing(plan, output, cancellationToken);
        output.Flush(flushToDisk: true);
    }

    internal static HybridIsoWriteSummary Create(string sourceDirectory, string outputPath,
        uint volumeSectors, string volumeLabel)
        => Create(Plan(sourceDirectory, volumeSectors, volumeLabel), outputPath);

    internal static HybridIsoWriteSummary Create(LayoutPlan plan, string outputPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourceFullPath = plan.SourceDirectory;
        uint volumeSectors = plan.VolumeSectors;
        string outputFullPath = Path.GetFullPath(outputPath);
        if (Iso9660ImageWriter.IsWithinDirectory(outputFullPath, sourceFullPath))
            throw new ArgumentException("--iso-output must be outside --data-dir so the generated image cannot include itself.");
        string outputDirectory = Path.GetDirectoryName(outputFullPath)
            ?? throw new ArgumentException("ISO output path has no parent directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputFullPath)}.{Guid.NewGuid():N}.tmp");
        bool sparse = false;
        try
        {
            using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 128 * 1024, FileOptions.SequentialScan))
            {
                sparse = IsoImageWriter.TryMarkSparse(output.SafeFileHandle);
                output.SetLength(checked((long)volumeSectors * IsoImageWriter.LogicalSectorBytes));
                if (plan.FilesAtOuterRing)
                {
                    WriteOuterRingPrefix(plan, output, plan.MetadataEndLbaExclusive, cancellationToken);
                    output.Position = checked((long)plan.FileDataStartLba * IsoImageWriter.LogicalSectorBytes);
                    WriteOuterRingSuffix(plan, output, plan.FileDataStartLba, cancellationToken);
                }
                else
                {
                    WriteSequentialPrefix(plan, output, plan.DataEndLbaExclusive, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputFullPath, overwrite: true);
        }
        finally
        {
            // Cleanup must not replace the original write or cancellation exception.
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new(plan.FileSystem == "udf250" ? "hybrid-udf250" : "hybrid-iso9660-joliet",
            volumeSectors, IsoImageWriter.LogicalSectorBytes, sourceFullPath, plan.FileSystem,
            plan.FileCount, plan.DirectoryCount, plan.FileBytes, plan.DataEndLbaExclusive, sparse,
            Iso9660: plan.FileSystem == "cdfs", Joliet: plan.FileSystem == "cdfs",
            plan.FilesAtOuterRing, plan.MetadataEndLbaExclusive, plan.FileDataStartLba);
    }
}
