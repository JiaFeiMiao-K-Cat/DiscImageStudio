using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;

internal static class CdParallelDataTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cd-parallel-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string image = Path.Combine(directory, "source.png");
            WriteImage(image);
            string files = Path.Combine(directory, "files");
            Directory.CreateDirectory(files);
            File.WriteAllBytes(Path.Combine(files, "payload.bin"), Enumerable.Range(0, 12071).Select(i => (byte)(i * 31)).ToArray());
            foreach (bool cubic in new[] { false, true })
            foreach (bool outer in new[] { false, true })
            foreach (var tone in new[] { ToneProfile.LegacyBinary, new ToneProfile(.83, ToneProfile.FourLevels, -7, true) })
            {
                // More than the maximum 32-block batch, with a partial final block.
                var disc = new CdDiscParameters(24.5, 56.8, 1137, StartAngleRadians: -.7,
                    PitchLinear: cubic ? .18 : 0, PitchQuadratic: cubic ? -.12 : 0, PitchCubic: cubic ? .06 : 0);
                var plan = CdDataImageGenerator.PrepareLayout(files, disc, "PARALLEL", outer);
                var scalar = Generate(image, disc, plan, tone, simd: false, parallel: false);
                foreach (var (simd, parallel) in new[] { (false, true), (true, false), (true, true) })
                {
                    var actual = Generate(image, disc, plan, tone, simd, parallel);
                    Require(actual.Bytes.AsSpan().SequenceEqual(scalar.Bytes),
                        "Mode 1 parallel/scalar/SIMD bytes, physical positions and dither agree");
                    Require(actual.Progress.SequenceEqual(scalar.Progress),
                        "each sequential progress boundary survives block generation");
                }
                if (cubic && outer && tone.IsLegacyBinary)
                {
                    string output = Path.Combine(directory, "parallel.iso");
                    CdDataImageGenerator.Generate(image, output, disc, plan, tone: tone);
                    Require(File.ReadAllBytes(output).AsSpan().SequenceEqual(scalar.Bytes),
                        "file export defaults to the same parallel stream");
                }
            }
            CheckCancellationAndFailures(image, files);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("cd-parallel-data: passed (Mode 1 bytes, cubic/tone, inner/outer files, caller-ordered output/progress, cancellation/errors)");
    }

    private static (byte[] Bytes, long[] Progress) Generate(string image, CdDiscParameters disc,
        CdDataLayoutPlan plan, ToneProfile tone, bool simd, bool parallel)
    {
        using var output = new CallerStream();
        int caller = Environment.CurrentManagedThreadId;
        List<long> completed = [];
        var summary = CdDataImageGenerator.GenerateToStream(image, output, disc, plan, progress: value =>
        {
            Require(Environment.CurrentManagedThreadId == caller && value.CompletedBytes == output.Completed,
                "progress follows the actual write on the caller thread");
            Require(value.TotalBytes == plan.ContentLength, "progress retains the entire ISO length");
            completed.Add(value.CompletedBytes);
        }, tone: tone, useSimdSampling: simd, useParallelSampling: parallel);
        Require(summary.BytesWritten == plan.ContentLength && output.Completed == plan.ContentLength,
            "stream writes the planned full ISO including file regions");
        Require(completed[^1] == plan.ContentLength && !output.WasDisposed, "final progress and caller-owned stream");
        return (output.Bytes, completed.ToArray());
    }

    private static void CheckCancellationAndFailures(string image, string files)
    {
        var disc = new CdDiscParameters(24.5, 56.8, 1137, PitchLinear: .18, PitchQuadratic: -.12, PitchCubic: .06);
        foreach (bool outer in new[] { false, true })
        foreach (bool parallel in new[] { false, true })
        {
            var plan = CdDataImageGenerator.PrepareLayout(files, disc, "CANCEL", outer);
            using var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            using var untouched = new CallerStream();
            Throws<OperationCanceledException>(() => CdDataImageGenerator.GenerateToStream(image, untouched,
                disc, plan, cancellationToken: alreadyCancelled.Token, useParallelSampling: parallel));
            Require(untouched.Completed == 0, "pre-cancel emits no metadata or drawing");

            long stopAt = ((long)plan.DrawingStartLba + 1) * CdDataImageGenerator.BytesPerSector;
            using var cancel = new CancellationTokenSource();
            using var stopped = new CallerStream { AfterWrite = count => { if (count == stopAt) cancel.Cancel(); } };
            Throws<OperationCanceledException>(() => CdDataImageGenerator.GenerateToStream(image, stopped,
                disc, plan, cancellationToken: cancel.Token, useParallelSampling: parallel));
            Require(stopped.Completed == stopAt, "cancellation emits no prepared subsequent drawing sectors or suffix");

            var writeError = new IOException("injected write failure");
            using var broken = new CallerStream { BeforeWrite = count => { if (count == stopAt) throw writeError; } };
            Exception observed = Throws<IOException>(() => CdDataImageGenerator.GenerateToStream(image, broken,
                disc, plan, useParallelSampling: parallel));
            Require(ReferenceEquals(observed, writeError) && broken.Completed == stopAt,
                "write failures remain unwrapped and stop at the same sector");

            var progressError = new InvalidOperationException("injected progress failure");
            using var reportFailure = new CallerStream();
            observed = Throws<InvalidOperationException>(() => CdDataImageGenerator.GenerateToStream(image, reportFailure,
                disc, plan, progress: value => { if (value.CompletedBytes >= stopAt) throw progressError; },
                useParallelSampling: parallel));
            Require(ReferenceEquals(observed, progressError) && !reportFailure.WasDisposed,
                "progress failures retain their exception and caller stream ownership");
        }
    }

    private static void WriteImage(string path)
    {
        const int size = 83;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int at = (y * size + x) * 4;
            pixels[at] = (byte)(x * 11 + y * 7);
            pixels[at + 1] = (byte)(x * 3 + y * 17);
            pixels[at + 2] = (byte)(x * 19 + y * 5);
            pixels[at + 3] = (byte)(x % 7 == 0 ? 0 : x % 3 == 0 ? 128 : 255);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96,
            PixelFormats.Bgra32, null, pixels, size * 4)));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private sealed class CallerStream : Stream
    {
        private readonly MemoryStream _output = new();
        private readonly int _caller = Environment.CurrentManagedThreadId;
        internal Action<long>? BeforeWrite { get; init; }
        internal Action<long>? AfterWrite { get; init; }
        internal long Completed => _output.Length;
        internal byte[] Bytes => _output.ToArray();
        internal bool WasDisposed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Require(Environment.CurrentManagedThreadId == _caller, "output runs only on the caller thread");
            BeforeWrite?.Invoke(Completed);
            _output.Write(buffer);
            AfterWrite?.Invoke(Completed);
        }
        public override void Flush() => Require(Environment.CurrentManagedThreadId == _caller, "flush stays on caller");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing) _output.Dispose();
            base.Dispose(disposing);
        }
    }

    private static Exception Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException("CD parallel data expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD parallel data: " + message);
    }
}
