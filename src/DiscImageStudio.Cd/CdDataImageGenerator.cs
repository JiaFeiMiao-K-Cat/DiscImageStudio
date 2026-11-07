using System.Diagnostics;
using System.IO;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Iso;

namespace DiscImageStudio.Cd;

public sealed class CdDataLayoutPlan
{
    internal CdDataLayoutPlan(Iso9660ImageWriter.LayoutPlan layout)
    {
        IsoLayout = layout;
        DrawingStartLba = layout.FilesAtOuterRing ? layout.MetadataEndLbaExclusive : layout.DataEndLbaExclusive;
        DrawingEndLbaExclusive = layout.FilesAtOuterRing ? layout.FileDataStartLba : layout.VolumeSectors;
    }

    internal Iso9660ImageWriter.LayoutPlan IsoLayout { get; }
    public long TotalSectors => IsoLayout.VolumeSectors;
    public long ContentLength => TotalSectors * CdDataImageGenerator.BytesPerSector;
    public string SourceDirectory => IsoLayout.SourceDirectory;
    public uint DrawingStartLba { get; }
    public uint DrawingEndLbaExclusive { get; }
    public uint DrawingSectors => DrawingEndLbaExclusive - DrawingStartLba;

    /// <summary>Occupied regions, in LBAs: the filesystem metadata always starts at the
    /// inner edge, the file data sits at the chosen ring. The designer's data-region guide
    /// maps both to radii; the dispersion reserve is added by the caller.</summary>
    public uint MetadataEndLbaExclusive => IsoLayout.MetadataEndLbaExclusive;
    public uint FileDataStartLba => IsoLayout.FileDataStartLba;
    public uint DataEndLbaExclusive => IsoLayout.DataEndLbaExclusive;

    public bool FilesAtOuterRing => IsoLayout.FilesAtOuterRing;
    public int FileCount => IsoLayout.FileCount;
    public int DirectoryCount => IsoLayout.DirectoryCount;
    public long FileBytes => IsoLayout.FileBytes;
}

/// <summary>
/// Produces cooked 2048-byte Mode 1 sectors. The recording backend supplies headers, EDC/ECC,
/// scrambling and CIRC. Only the drawing payload is compensated for those reversible
/// byte transformations; filesystem bytes remain ordinary ISO9660/Joliet data.
/// </summary>
public static class CdDataImageGenerator
{
    public const int BytesPerSector = 2048;
    private const int PayloadOffset = 16;
    private static readonly byte[] ScrambleMask = BuildScrambleMask();
    private static readonly int[] PhysicalOffsets = BuildPhysicalOffsets();

    public static CdDataLayoutPlan PrepareLayout(string dataDirectory, CdDiscParameters parameters,
        string volumeLabel = "CD_IMAGE", bool filesAtOuterRing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        parameters.Validate();
        CdDataLayoutPlan plan = new(Iso9660ImageWriter.Plan(dataDirectory,
            checked((uint)parameters.OutputSectors), volumeLabel, filesAtOuterRing: filesAtOuterRing));
        if (plan.DrawingStartLba >= plan.DrawingEndLbaExclusive)
            throw new ArgumentException("The filesystem leaves no CD sectors for drawing.", nameof(dataDirectory));
        return plan;
    }

    public static CdGenerationSummary Generate(string imagePath, string outputPath,
        CdDiscParameters parameters, CdDataLayoutPlan plan, Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default, ToneProfile? tone = null, bool useSimdSampling = true,
        bool useParallelSampling = true)
    {
        Validate(parameters, plan);
        cancellationToken.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(imagePath);
        string target = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(target).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CD data output must be a Mode 1 .iso image.", nameof(outputPath));
        if (target.Equals(source, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(Path.TrimEndingDirectorySeparator(plan.SourceDirectory)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output must be outside the source data folder and must not overwrite the source image.", nameof(outputPath));
        RasterImage image = RasterImage.Load(source);
        ToneProfile effectiveTone = (tone ?? ToneProfile.LegacyBinary).Normalized();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        CdGenerationSummary result;
        using (FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.SequentialScan))
        {
            result = GenerateImageToStream(source, image, output, parameters, plan, progress,
                cancellationToken, effectiveTone, useSimdSampling, useParallelSampling) with { OutputTrack = target };
        }
        CdDataImageMetadata.Save(target, new(parameters, plan.DrawingStartLba,
            plan.DrawingEndLbaExclusive, plan.FilesAtOuterRing, effectiveTone));
        return result;
    }

    public static CdGenerationSummary GenerateToStream(string imagePath, Stream output,
        CdDiscParameters parameters, CdDataLayoutPlan plan, Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default, ToneProfile? tone = null, bool useSimdSampling = true,
        bool useParallelSampling = true)
    {
        Validate(parameters, plan);
        cancellationToken.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(imagePath);
        return GenerateImageToStream(source, RasterImage.Load(source), output, parameters, plan,
            progress, cancellationToken, (tone ?? ToneProfile.LegacyBinary).Normalized(), useSimdSampling, useParallelSampling);
    }

    private static CdGenerationSummary GenerateImageToStream(string source, RasterImage image,
        Stream output, CdDiscParameters parameters, CdDataLayoutPlan plan,
        Action<CdProgress>? progress, CancellationToken cancellationToken, ToneProfile tone, bool useSimdSampling,
        bool useParallelSampling)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("CD data output must be writable.", nameof(output));
        CdImageSampler sampler = new(image, parameters);
        bool sampleFour = useSimdSampling && sampler.SupportsSimd;
        Stopwatch stopwatch = Stopwatch.StartNew();
        ProgressStream writer = new(output, plan.ContentLength, stopwatch, progress, cancellationToken);
        if (plan.FilesAtOuterRing)
            Iso9660ImageWriter.WriteOuterRingPrefix(plan.IsoLayout, writer, plan.DrawingStartLba, cancellationToken);
        else
            Iso9660ImageWriter.WriteSequentialPrefix(plan.IsoLayout, writer, plan.DrawingStartLba, cancellationToken);

        if (useParallelSampling)
        {
            // Only immutable raster sampling and tone conversion run on workers.
            // Filesystem bytes, stream writes and progress remain on the caller.
            CdBlockPipeline.Run(plan.DrawingSectors, BytesPerSector,
                (firstSector, buffer, sectorCount) =>
                {
                    for (int index = 0; index < sectorCount; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FillDrawingSector(sampler, tone, sampleFour, plan.DrawingStartLba + firstSector + index,
                            buffer.AsSpan(index * BytesPerSector, BytesPerSector));
                    }
                },
                (_, buffer, sectorCount) =>
                {
                    for (int index = 0; index < sectorCount; index++)
                        writer.Write(buffer.AsSpan(index * BytesPerSector, BytesPerSector));
                }, cancellationToken);
        }
        else
        {
            byte[] sector = new byte[BytesPerSector];
            for (uint lba = plan.DrawingStartLba; lba < plan.DrawingEndLbaExclusive; lba++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FillDrawingSector(sampler, tone, sampleFour, lba, sector);
                writer.Write(sector);
            }
        }
        if (plan.FilesAtOuterRing)
            Iso9660ImageWriter.WriteOuterRingSuffix(plan.IsoLayout, writer, plan.DrawingEndLbaExclusive, cancellationToken);
        writer.Flush();
        stopwatch.Stop();
        progress?.Invoke(new CdProgress(writer.CompletedBytes, plan.ContentLength, stopwatch.Elapsed));
        return new CdGenerationSummary(source, "direct-burn-stream", parameters.OutputSectors,
            writer.CompletedBytes, false, stopwatch.Elapsed);
    }

    private static void FillDrawingSector(CdImageSampler sampler, ToneProfile tone, bool sampleFour,
        long lba, Span<byte> sector)
    {
        Span<long> positions = stackalloc long[4];
        int offset = 0;
        if (sampleFour)
        {
            for (; offset <= BytesPerSector - 4; offset += 4)
            {
                // CIRC delays and the audio pair swap make these physical
                // positions non-contiguous even within one Mode 1 payload.
                for (int lane = 0; lane < 4; lane++)
                    positions[lane] = PhysicalByteForPayload(lba, offset + lane);
                uint samples = sampler.SampleFour(positions[0], positions[1], positions[2], positions[3]);
                for (int lane = 0; lane < 4; lane++)
                {
                    sector[offset + lane] = (byte)(CdToneMapping.Map((byte)samples, tone, positions[lane])
                        ^ ScrambleMask[offset + lane + PayloadOffset - 12]);
                    samples >>= 8;
                }
            }
        }
        for (; offset < BytesPerSector; offset++)
        {
            long physicalByte = PhysicalByteForPayload(lba, offset);
            sector[offset] = (byte)(CdToneMapping.Map(sampler.Sample(physicalByte), tone, physicalByte)
                ^ ScrambleMask[offset + PayloadOffset - 12]);
        }
    }

    private static void Validate(CdDiscParameters parameters, CdDataLayoutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(plan);
        parameters.Validate();
        if (parameters.OutputSectors != plan.TotalSectors)
            throw new ArgumentException("CD layout and geometry must have the same sector count.", nameof(plan));
    }

    /// <summary>
    /// Locates a payload byte in the existing CD drawing coordinate convention. ECMA-130
    /// clauses 14–17: 16-byte header, scrambling, adjacent byte swap, then CIRC. Invert
    /// the shared logical-to-file delay map to sample the byte's eventual disc location.
    /// </summary>
    public static long PhysicalByteForPayload(long lba, int payloadOffset)
    {
        if (lba < 0 || (uint)payloadOffset >= BytesPerSector) throw new ArgumentOutOfRangeException(nameof(payloadOffset));
        long frameByte = checked(lba * CdDiscParameters.BytesPerSector + PayloadOffset + payloadOffset) ^ 1L;
        return frameByte + PhysicalOffsets[frameByte % CddaInterleaveTable.BytesPerFrame];
    }

    private static int[] BuildPhysicalOffsets()
    {
        int[] inverse = new int[CddaInterleaveTable.BytesPerFrame];
        for (int logical = 0; logical < inverse.Length; logical++)
        {
            int displacement = CddaInterleaveTable.FileOffsetFor(logical);
            inverse[(logical + displacement) % inverse.Length] = -displacement;
        }
        return inverse;
    }

    // ECMA-130 Annex B: x^15 + x + 1, seed 1 at byte 12, least significant bit first.
    // https://ecma-international.org/wp-content/uploads/ECMA-130_2nd_edition_june_1996.pdf
    private static byte[] BuildScrambleMask()
    {
        byte[] mask = new byte[CdDiscParameters.BytesPerSector - 12];
        int state = 1;
        for (int index = 0; index < mask.Length; index++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                mask[index] |= (byte)((state & 1) << bit);
                state = (state >> 1) | (((state ^ (state >> 1)) & 1) << 14);
            }
        }
        return mask;
    }

    public static void PreviewWarp(string imagePath, string outputPath, CdDiscParameters generated,
        CdDiscParameters actual, int outputSize, int samplesPerSector, ToneProfile tone,
        CdDataLayoutPlan? plan = null, CancellationToken cancellationToken = default)
    {
        ValidatePreview(actual, outputSize, samplesPerSector);
        generated.Validate();
        if (plan is not null) Validate(generated, plan);
        tone = tone.Normalized();
        CdImageSampler sampler = new(RasterImage.Load(Path.GetFullPath(imagePath)), generated);
        DataPreview preview = new(outputSize, actual);
        long start = plan?.DrawingStartLba ?? 0;
        long end = plan?.DrawingEndLbaExclusive ?? generated.OutputSectors;
        int step = Math.Max(1, BytesPerSector / samplesPerSector);
        for (long lba = start; lba < end; lba++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int offset = 0; offset < BytesPerSector; offset += step)
            {
                long physicalByte = PhysicalByteForPayload(lba, offset);
                if (physicalByte < 0) continue;
                preview.Splat(actual, physicalByte, CdToneMapping.Map(sampler.Sample(physicalByte), tone, physicalByte));
            }
        }
        preview.Save(outputPath);
    }

    public static void PreviewTrack(string isoPath, string outputPath, CdDiscParameters actual,
        int outputSize, int byteStep, CancellationToken cancellationToken = default)
    {
        ValidatePreview(actual, outputSize, byteStep);
        using FileStream input = new(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        if (input.Length % BytesPerSector != 0 || input.Length != checked(actual.OutputSectors * BytesPerSector))
            throw new ArgumentException("CD data ISO length must match the selected 2048-byte sector count.", nameof(isoPath));
        CdDataImageMetadata? metadata = CdDataImageMetadata.TryLoad(isoPath);
        if (metadata?.Parameters.OutputSectors != actual.OutputSectors) metadata = null;
        long start = metadata?.DrawingStartLba ?? 0;
        long end = metadata?.DrawingEndLbaExclusive ?? actual.OutputSectors;
        input.Position = start * BytesPerSector;
        byte[] sector = new byte[BytesPerSector];
        DataPreview preview = new(outputSize, actual);
        for (long lba = start; lba < end; lba++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            input.ReadExactly(sector);
            for (int offset = 0; offset < BytesPerSector; offset += byteStep)
            {
                long physicalByte = PhysicalByteForPayload(lba, offset);
                if (physicalByte < 0) continue;
                preview.Splat(actual, physicalByte, (byte)(sector[offset] ^ ScrambleMask[offset + PayloadOffset - 12]));
            }
        }
        preview.Save(outputPath);
    }

    private static void ValidatePreview(CdDiscParameters parameters, int size, int interval)
    {
        parameters.Validate();
        if (size is < 64 or > 8192) throw new ArgumentOutOfRangeException(nameof(size), "Preview size must be 64..8192.");
        if (interval <= 0) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    private sealed class DataPreview(int size, CdDiscParameters actual)
    {
        private readonly long[] _sum = new long[checked(size * size)];
        private readonly int[] _count = new int[checked(size * size)];
        internal void Splat(CdDiscParameters parameters, long physicalByte, byte palette)
        {
            (double x, double y) = parameters.ImagePointFromByte(physicalByte, size);
            int px = (int)Math.Round(x), py = (int)Math.Round(y);
            if ((uint)px >= size || (uint)py >= size) return;
            int index = py * size + px;
            _sum[index] += CdToneMapping.DisplayGray(palette);
            _count[index]++;
        }
        internal void Save(string path)
        {
            byte[] pixels = new byte[checked(size * size * 4)];
            Array.Fill(pixels, (byte)255);
            for (int index = 0; index < _count.Length; index++)
            {
                if (_count[index] == 0) continue;
                byte gray = (byte)((_sum[index] + _count[index] / 2) / _count[index]);
                pixels[index * 4] = pixels[index * 4 + 1] = pixels[index * 4 + 2] = gray;
            }
            CdPreviewMask.Apply(pixels, size, actual);
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            RasterCodec.SavePng(fullPath, size, size, pixels, grayscale: false);
        }
    }

    private sealed class ProgressStream(Stream output, long total, Stopwatch stopwatch,
        Action<CdProgress>? progress, CancellationToken cancellationToken) : Stream
    {
        private long _nextProgress;
        public long CompletedBytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => CompletedBytes; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer);
            CompletedBytes += buffer.Length;
            if (CompletedBytes < _nextProgress) return;
            progress?.Invoke(new CdProgress(CompletedBytes, total, stopwatch.Elapsed));
            _nextProgress = CompletedBytes + Math.Max(BytesPerSector, total / 200);
        }
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
