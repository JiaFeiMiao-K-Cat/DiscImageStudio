using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DvdImageSolver.Solver;

internal static class DvdSampleReuseTests
{
    private const uint Capacity = 2_297_888;

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dvd-sample-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string binary = WriteImage(directory, "binary", 65, 65, (x, y) =>
                Gray(((x / 8 + y / 7) & 1) == 0 ? (byte)0 : (byte)255));
            string gray = WriteImage(directory, "gray", 67, 67, (x, y) => Gray((byte)((x / 12 + y / 13) * 47)));
            string rectangular = WriteImage(directory, "edges", 73, 31, (x, y) => Gray((byte)((x / 9 + y / 8) * 83)));
            byte[] alpha = [0, 1, 64, 127, 128, 192, 255];
            string color = WriteImage(directory, "alpha-color", 67, 79, (x, y) =>
                ((byte)(x * 13 + y * 7), (byte)(x * 5 + y * 19), (byte)(x * 23 + y * 3), alpha[(x / 4 + y / 3) % alpha.Length]));
            string smooth = WriteImage(directory, "smooth-alpha-color", 97, 97, (x, y) =>
                ((byte)(32 + x), (byte)(64 + y), (byte)(96 + (x + y) / 4), (byte)(64 + (x + y) / 2)));
            string narrow = WriteImage(directory, "one-pixel-width", 1, 17, (_, y) => Gray((byte)(y * 15)));
            CheckRasterMatrix(binary, gray, rectangular, color, smooth, narrow);
            foreach (bool cubic in new[] { false, true })
            {
                AssertActualReuse(binary, null, Options((Capacity / 2) & ~15U, cubic));
                AssertActualReuse(smooth, null, Options((Capacity / 2) & ~15U, cubic));
                AssertActualReuse(null, Target(Mapping(cubic)), Options((Capacity / 2) & ~15U, cubic));
            }
            CheckPixelBoundaries(directory);
            CheckReferenceTargets();
            CheckReferenceRadiusBoundaries();
            CheckBatchesAndCancellation(binary);
            CheckReuseTransitions(directory);
            CheckStreamingAndHybrid(directory, binary);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("dvd-sample-reuse: passed (scalar byte/count parity, cubic/raster/reference boundaries, tone/alpha fallback, ordered batches, cancellation and hybrid routes)");
    }

    private static void CheckRasterMatrix(params string[] images)
    {
        foreach (bool cubic in new[] { false, true })
        foreach (uint first in new[] { 0U, (Capacity / 2) & ~15U, Capacity - 16 })
        foreach (string image in images)
        {
            var options = Options(first, cubic);
            CompareRaster(image, options, "raster constant/cubic first/middle/last");
            CompareRaster(image, options with { ImageMapping = options.ImageMapping with
                { Tone = new ToneProfile(.83, 2, -7, true), AlphaThreshold = 128, Clockwise = false, StartAngleDegrees = 37.25 } },
                "tone/gamma/dither, alpha and reverse rotation");
        }
        foreach (var mapping in new[]
        {
            Mapping(false) with { TotalSectors = 64, InnerRadiusMm = 1.25, OuterRadiusMm = 9.75 },
            Mapping(true) with { PitchLinear = -3.8, PitchQuadratic = 3.8, PitchCubic = 0 },
            Mapping(true) with { StartAngleDegrees = 1e15 },
        })
            CompareRaster(images[0], Options(0, false) with { ImageMapping = mapping }, "small/extreme geometry fallback");
    }

    private static void CheckPixelBoundaries(string directory)
    {
        string stripes = WriteImage(directory, "half-pixel", 65, 65,
            (x, _) => Gray((x & 1) == 0 ? (byte)0 : (byte)255));
        // A rectangular raster uses its shorter dimension to fit the disc.
        // Probe the tangent approach to that image edge at the outermost LBA.
        string shortImage = WriteImage(directory, "short-edge", 65, 17, (_, _) => Gray(0));
        foreach (bool cubic in new[] { false, true })
        foreach (uint first in new[] { 0U, (Capacity / 2) & ~15U, Capacity - 16 })
        {
            var options = Options(first, cubic);
            DvdCodewordSample sample = CaptureFirst(options);
            double radius = Math.Sqrt(sample.Xmm * sample.Xmm + sample.Ymm * sample.Ymm);
            double scale = 64 / (2 * options.ImageMapping.OuterRadiusMm);
            foreach (double pixel in new[] { Math.BitDecrement(32.5), 32.5, Math.BitIncrement(32.5), 32.5 - 1e-8, 32.5 + 1e-8 })
            {
                double angle = (Math.Acos((pixel - 32) / (scale * radius)) + sample.TrackAngleRadians) * 180 / Math.PI;
                CompareRaster(stripes, options with { ImageMapping = options.ImageMapping with { StartAngleDegrees = angle } },
                    "half-pixel and adjacent representable boundary inputs");
            }
            double tangentAngle = (Math.PI / 2 + sample.TrackAngleRadians) * 180 / Math.PI;
            foreach (double angle in new[] { Math.BitDecrement(tangentAngle), tangentAngle,
                Math.BitIncrement(tangentAngle), tangentAngle - 1e-8, tangentAngle + 1e-8 })
            {
                CompareRaster(shortImage, options with { ImageMapping = options.ImageMapping with { StartAngleDegrees = angle } },
                    "image-edge tangent and adjacent representable angles");
            }
        }
    }

    private static void AssertActualReuse(string? imagePath, CalibrationTarget? target, MultiBlockSolveOptions options)
    {
        // A byte parity test alone can pass while silently taking the fallback.
        // Inspect only the capability and returned interval, not its algorithm.
        Type samplerType = typeof(FastDispersionImageWriter).GetNestedType("PayloadImageSampler", BindingFlags.NonPublic)!;
        object[] arguments;
        if (target is not null) arguments = [target, options.ImageMapping, true];
        else
        {
            Type rasterType = typeof(FastDispersionImageWriter).Assembly.GetType("DvdImageSolver.Solver.RasterImage", true)!;
            object raster = rasterType.GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [imagePath])!;
            arguments = [raster, options.ImageMapping];
        }
        object sampler = Activator.CreateInstance(samplerType, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: arguments, culture: null)!;
        Require((bool)samplerType.GetProperty("SupportsSampleReuse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!,
            "standard DVD raster/reference geometry advertises reuse");
        MethodInfo sample = samplerType.GetMethod("SampleWithRegion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ulong first = CaptureFirst(options).GlobalCodewordIndex * 16 + 8;
        bool found = false;
        for (ulong offset = 0; offset < 32768 && !found; offset += 64)
        {
            ulong bit = first + offset;
            object[] parameters = [bit, 0d];
            sample.Invoke(sampler, parameters);
            found = (double)parameters[1] > bit + 64;
        }
        Require(found, "binary, smooth color/alpha and reference fixtures expose a nonempty reusable region");
    }

    private static void CheckReferenceTargets()
    {
        foreach (bool cubic in new[] { false, true })
        foreach (uint first in new[] { 0U, (Capacity / 2) & ~15U, Capacity - 16 })
        {
            var options = Options(first, cubic);
            var target = Target(options.ImageMapping);
            CompareReference(target, options, "fixed reference first/middle/last");
            CompareReference(target, options with { ImageMapping = options.ImageMapping with
                { Tone = new ToneProfile(.75, 2, 19, true), StartAngleDegrees = -73.25 } }, "reference tone and orientation");
        }
        var normal = Options(0, false);
        var reference = Target(normal.ImageMapping);
        var legacy = new CalibrationTarget(6, reference.Id, reference.Parameters, reference.Options, reference.Cells);
        CompareReference(legacy, normal, "legacy fixed reference output");
        var small = normal with { ImageMapping = normal.ImageMapping with { TotalSectors = 64, InnerRadiusMm = 1.25, OuterRadiusMm = 9.75 } };
        CompareReference(Target(small.ImageMapping), small, "small reference geometry fallback");
        var extreme = normal with { ImageMapping = normal.ImageMapping with { PitchLinear = -3.8, PitchQuadratic = 3.8 } };
        CompareReference(Target(extreme.ImageMapping), extreme, "extreme reference geometry fallback");
    }

    private static void CheckReferenceRadiusBoundaries()
    {
        foreach (bool cubic in new[] { false, true })
        {
            var options = Options((Capacity / 2) & ~15U, cubic);
            var sample = CaptureFirst(options);
            double squared = sample.Xmm * sample.Xmm + sample.Ymm * sample.Ymm;
            foreach (bool inner in new[] { false, true })
            {
                // Sample() compares squared radii with a 1e-10 tolerance. Move
                // the exact clipping threshold, not merely the nominal radius.
                double boundary = Math.Sqrt(squared + (inner ? 1e-10 : -1e-10));
                foreach (double radius in new[] { Math.BitDecrement(boundary), boundary, Math.BitIncrement(boundary) })
                {
                    var source = options.ImageMapping with
                    {
                        InnerRadiusMm = inner ? radius : options.ImageMapping.InnerRadiusMm,
                        OuterRadiusMm = inner ? options.ImageMapping.OuterRadiusMm : radius,
                    };
                    CompareReference(Target(source), options, "reference radial clipping threshold ± ULP");
                }
            }
        }
    }

    private static void CheckBatchesAndCancellation(string image)
    {
        var options = Options(Capacity - 7 * 16, true) with { FillSectors = 7 * 16 };
        CompareRaster(image, options, "parallel batch reuse and final partial batch", workers: 2);
        CompareReference(Target(options.ImageMapping), options, "parallel reference batch and tail", workers: 2);
        foreach (bool reference in new[] { false, true })
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            using var untouched = new CallerStream();
            ThrowsCancelled(() => Write(reference, image, untouched, options, null, cancelled.Token));
            Require(untouched.Completed == 0, "pre-cancelled reuse emits nothing");
            using var stop = new CancellationTokenSource();
            using var output = new CallerStream();
            ThrowsCancelled(() => Write(reference, image, output, options with { FastParallelism = 2 }, value =>
            {
                if (value.CompletedBlocks == 1) stop.Cancel();
            }, stop.Token));
            Require(output.Completed == 32768, "cancellation emits no later already-generated blocks");
        }
    }

    private static void CheckReuseTransitions(string directory)
    {
        // RGB (20,115,0) has luma exactly 86.5: the scalar rounding-sensitive
        // bands cannot certify reuse. Alternating opaque integer-gray bands can.
        // A physical revolution crosses both kinds repeatedly, forcing probing,
        // cooldown SIMD runs and resumed reuse at arbitrary payload offsets.
        string transitions = WriteImage(directory, "reuse-cooldown-bands", 257, 257,
            (x, y) => ((x / 7 + y / 11) & 1) == 0
                ? ((byte)0, (byte)115, (byte)20, (byte)255) : Gray(173));
        foreach (bool cubic in new[] { false, true })
        foreach (bool toned in new[] { false, true })
        {
            var options = Options((Capacity / 2) & ~15U, cubic) with { FillSectors = 7 * 16 };
            if (toned) options = options with { ImageMapping = options.ImageMapping with { Tone = new ToneProfile(.91, 2, 13, true) } };
            CompareRaster(transitions, options, "repeated reuse/cooldown transitions, arbitrary SIMD offsets and tail batch", workers: 2);
        }
    }

    private static FastDvdStreamSummary Write(bool reference, string image, Stream output, MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress, CancellationToken token)
        => reference ? FastDispersionImageWriter.WriteCalibrationReferenceStream(Target(options.ImageMapping), output, options, progress, token)
            : FastDispersionImageWriter.WriteStream(image, output, options, progress, token);

    private static void CheckStreamingAndHybrid(string directory, string image)
    {
        var large = new DvdStreamingOptions(Capacity, 24, 58, FastParallelism: 2,
            PitchLinear: .18, PitchQuadratic: -.12, PitchCubic: .06, Tone: new ToneProfile(.7, 2, 5, true));
        foreach (bool reference in new[] { false, true })
        {
            byte[] Prefix(bool reuse)
            {
                using var stop = new CancellationTokenSource();
                using var bytes = new CallerStream();
                var options = large with { UseSampleReuse = reuse };
                void Report(DvdStreamingProgress value) { if (value.CompletedBlocks == 3) stop.Cancel(); }
                ThrowsCancelled(() =>
                {
                    if (reference) DvdStreamingGenerator.GeneratePattern(Target(Mapping(true)), bytes, options, Report, stop.Token);
                    else DvdStreamingGenerator.Generate(image, bytes, options, Report, stop.Token);
                });
                Require(bytes.Completed == 3 * 32768, "GUI/stream entry cancels after exactly three blocks");
                return bytes.Bytes;
            }
            Require(Prefix(false).AsSpan().SequenceEqual(Prefix(true)), "standard-geometry generation entry retains exact bytes");
        }
        string files = Path.Combine(directory, "files");
        Directory.CreateDirectory(files);
        File.WriteAllBytes(Path.Combine(files, "payload.bin"), Enumerable.Range(0, 7013).Select(x => (byte)(x * 41)).ToArray());
        var small = large with { TotalSectors = 256 };
        foreach (bool outer in new[] { false, true })
        {
            var plan = DvdStreamingGenerator.PrepareHybrid(files, small, "REUSE", outer);
            using var scalar = new CallerStream();
            using var actual = new CallerStream();
            var expected = DvdStreamingGenerator.GenerateHybrid(image, scalar, small with { UseSampleReuse = false }, plan);
            var observed = DvdStreamingGenerator.GenerateHybrid(image, actual, small with { UseSampleReuse = true }, plan);
            Require(scalar.Bytes.AsSpan().SequenceEqual(actual.Bytes) && expected.ControlledWords == observed.ControlledWords,
                "hybrid inner/outer filesystem bytes and drawing counts survive reuse routing");
            Require(actual.Completed == small.ContentLength, "hybrid output remains a complete forward-only disc image");
        }
    }

    private static void CompareRaster(string image, MultiBlockSolveOptions options, string label, int workers = 1)
        => Compare(options, label,
            (stream, selected, report) => FastDispersionImageWriter.WriteStream(image, stream, selected, report),
            (stream, selected, report) => FastDispersionImageWriter.WriteStream(image, stream, selected, report), workers);

    private static void CompareReference(CalibrationTarget target, MultiBlockSolveOptions options, string label, int workers = 1)
        => Compare(options, label,
            (stream, selected, report) => FastDispersionImageWriter.WritePatternStream(target.Sample, stream, selected, report),
            (stream, selected, report) => FastDispersionImageWriter.WriteCalibrationReferenceStream(target, stream, selected, report), workers);

    private static void Compare(MultiBlockSolveOptions options, string label,
        Func<Stream, MultiBlockSolveOptions, Action<FastDvdStreamProgress>, FastDvdStreamSummary> oldWriter,
        Func<Stream, MultiBlockSolveOptions, Action<FastDvdStreamProgress>, FastDvdStreamSummary> newWriter, int workers)
    {
        using var scalar = new CallerStream();
        using var actual = new CallerStream();
        List<(long, int, uint)> oldProgress = [], newProgress = [];
        int caller = Environment.CurrentManagedThreadId;
        Action<FastDvdStreamProgress> Report(CallerStream output, List<(long, int, uint)> values) => value =>
        {
            Require(Environment.CurrentManagedThreadId == caller && output.Completed == value.BytesWritten,
                "progress follows the ordered caller-thread stream");
            values.Add((value.BytesWritten, value.CompletedBlocks, value.BlockLba));
        };
        var expected = oldWriter(scalar, options with { UseSampleReuse = false, UseSimdSampling = false, FastParallelism = 1 }, Report(scalar, oldProgress));
        var observed = newWriter(actual, options with { UseSampleReuse = true, UseSimdSampling = true, FastParallelism = workers }, Report(actual, newProgress));
        Require(scalar.Bytes.AsSpan().SequenceEqual(actual.Bytes), label + ": every output byte");
        Require(expected.ControlledWords == observed.ControlledWords && expected.CompletedBlocks == observed.CompletedBlocks
            && expected.BytesWritten == observed.BytesWritten && expected.SectorsWritten == observed.SectorsWritten,
            label + ": counts and selected range");
        Require(oldProgress.SequenceEqual(newProgress), label + ": progress order");
    }

    private static DvdCodewordSample CaptureFirst(MultiBlockSolveOptions options)
    {
        DvdCodewordSample first = default;
        bool captured = false;
        using var output = new MemoryStream();
        FastDispersionImageWriter.WriteCodewordPatternStream(sample =>
        {
            if (!captured) { first = sample; captured = true; }
            return 0;
        }, output, options with { UseSampleReuse = false, UseSimdSampling = false, FastParallelism = 1 });
        return first;
    }

    private static CalibrationTarget Target(ImageMappingOptions mapping)
        => CalibrationTarget.Create(new(CalibrationDiscKind.Dvd, mapping.InnerRadiusMm, mapping.OuterRadiusMm,
            mapping.TotalSectors, mapping.ChannelBitLengthNm, mapping.PitchLinear, mapping.PitchQuadratic, mapping.PitchCubic),
            artwork: CalibrationArtwork.ReferenceCross);

    private static MultiBlockSolveOptions Options(uint first, bool cubic)
        => new(first, 16, 0x30000, 1, 4, 842, 64, .25, false, Mapping(cubic),
            Algorithm: SolverAlgorithm.Dispersion, FastParallelism: 1);

    private static ImageMappingOptions Mapping(bool cubic)
        => new(Capacity, 24, 58, 133.3, 0, true, 128, 1,
            PitchLinear: cubic ? .18 : 0, PitchQuadratic: cubic ? -.12 : 0, PitchCubic: cubic ? .06 : 0);

    private static (byte Blue, byte Green, byte Red, byte Alpha) Gray(byte value) => (value, value, value, 255);
    private static string WriteImage(string directory, string name, int width, int height,
        Func<int, int, (byte Blue, byte Green, byte Red, byte Alpha)> pixel)
    {
        string path = Path.Combine(directory, name + ".png");
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var value = pixel(x, y);
            int index = (y * width + x) * 4;
            pixels[index] = value.Blue; pixels[index + 1] = value.Green;
            pixels[index + 2] = value.Red; pixels[index + 3] = value.Alpha;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, width * 4)));
        using var output = File.Create(path);
        encoder.Save(output);
        return path;
    }

    private sealed class CallerStream : Stream
    {
        private readonly MemoryStream _bytes = new();
        private readonly int _caller = Environment.CurrentManagedThreadId;
        internal long Completed => _bytes.Length;
        internal byte[] Bytes => _bytes.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Require(Environment.CurrentManagedThreadId == _caller, "stream output stays on the caller thread");
            _bytes.Write(buffer);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _bytes.Dispose(); base.Dispose(disposing); }
    }

    private static void ThrowsCancelled(Action action)
    {
        try { action(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("DVD sample reuse expected cancellation");
    }
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("DVD sample reuse: " + label);
    }
}
