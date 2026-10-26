using DiscImageStudio.Iso;

namespace DvdImageSolver;

internal sealed record HybridIsoWriteSummary(
    string Mode,
    uint VolumeSectors,
    int LogicalSectorBytes,
    string SourceDirectory,
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
        internal LayoutPlan(Iso9660ImageWriter.LayoutPlan layout) => Layout = layout;
        internal Iso9660ImageWriter.LayoutPlan Layout { get; }
        public string SourceDirectory => Layout.SourceDirectory;
        public uint VolumeSectors => Layout.VolumeSectors;
        public int FileCount => Layout.FileCount;
        public int DirectoryCount => Layout.DirectoryCount;
        public long FileBytes => Layout.FileBytes;
        public uint DataEndLbaExclusive => Layout.DataEndLbaExclusive;
        public uint MetadataEndLbaExclusive => Layout.MetadataEndLbaExclusive;
        public uint FileDataStartLba => Layout.FileDataStartLba;
        public bool FilesAtOuterRing => Layout.FilesAtOuterRing;
    }

    public static LayoutPlan Plan(string sourceDirectory, uint volumeSectors, string volumeLabel,
        bool filesAtOuterRing = false)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        return new(Iso9660ImageWriter.Plan(sourceDirectory, volumeSectors, volumeLabel,
            filesAtOuterRing: filesAtOuterRing));
    }

    public static void WriteOuterRingPrefix(LayoutPlan plan, Stream output, uint endLbaExclusive,
        CancellationToken cancellationToken = default)
        => Iso9660ImageWriter.WriteOuterRingPrefix(plan.Layout, output, endLbaExclusive, cancellationToken);

    public static void WriteOuterRingSuffix(LayoutPlan plan, Stream output, uint startLba,
        CancellationToken cancellationToken = default)
        => Iso9660ImageWriter.WriteOuterRingSuffix(plan.Layout, output, startLba, cancellationToken);

    public static void WriteSequentialPrefix(LayoutPlan plan, Stream output, uint endLbaExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Iso9660ImageWriter.WriteSequentialPrefix(plan.Layout, output, endLbaExclusive, cancellationToken);
    }

    internal static HybridIsoWriteSummary Create(string sourceDirectory, string outputPath,
        uint volumeSectors, string volumeLabel)
        => Create(Plan(sourceDirectory, volumeSectors, volumeLabel), outputPath);

    internal static HybridIsoWriteSummary Create(LayoutPlan plan, string outputPath)
    {
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
                    WriteOuterRingPrefix(plan, output, plan.MetadataEndLbaExclusive);
                    output.Position = checked((long)plan.FileDataStartLba * IsoImageWriter.LogicalSectorBytes);
                    WriteOuterRingSuffix(plan, output, plan.FileDataStartLba);
                }
                else
                {
                    WriteSequentialPrefix(plan, output, plan.DataEndLbaExclusive);
                }
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, outputFullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return new("hybrid-iso9660-joliet", volumeSectors, IsoImageWriter.LogicalSectorBytes,
            sourceFullPath, plan.FileCount, plan.DirectoryCount, plan.FileBytes,
            plan.DataEndLbaExclusive, sparse, Iso9660: true, Joliet: true,
            plan.FilesAtOuterRing, plan.MetadataEndLbaExclusive, plan.FileDataStartLba);
    }
}
