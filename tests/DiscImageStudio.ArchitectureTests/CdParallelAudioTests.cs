using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;

internal static class CdParallelAudioTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-cd-parallel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string image = Path.Combine(directory, "colour.png");
            WriteImage(image);
            TestBlockBoundaries(image);
            TestTargetsAndFiles(image, directory);
            TestCallbackOrder();
            TestCancellationAndFailure(image);
            TestBoundedPipeline();
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("cd-parallel-audio: passed (block/batch boundaries, exact output/progress, cubic/tone, frozen targets, file/stream, ordered callbacks, bounded workers, cancellation/failure)");
    }

    private static int BatchSectors => 64 * Math.Min(16, Math.Max(1, Environment.ProcessorCount - 1));

    private static void TestBlockBoundaries(string image)
    {
        foreach (int sectors in new[] { 31, 32, 33, BatchSectors - 1, BatchSectors, BatchSectors + 1 }.Distinct())
        foreach (bool cubic in new[] { false, true })
        foreach (bool interleave in new[] { false, true })
        foreach (CdAudioByteOrder order in Enum.GetValues<CdAudioByteOrder>())
        {
            CdDiscParameters parameters = Parameters(sectors, cubic);
            ToneProfile tone = (sectors + (interleave ? 1 : 0)) % 2 == 0
                ? ToneProfile.LegacyBinary : new(.79, ToneProfile.FourLevels, -9, true);
            Compare(image, parameters, interleave, order, tone, simd: true);
        }
        // Independent switches: scalar geometry is also safe to compute in parallel.
        Compare(image, Parameters(65, true), true, CdAudioByteOrder.LittleEndian,
            new(.43, ToneProfile.TwoLevels, 17, true), simd: false);
    }

    private static void Compare(string image, CdDiscParameters parameters, bool interleave,
        CdAudioByteOrder order, ToneProfile tone, bool simd)
    {
        using CheckedStream serial = new(), parallel = new();
        List<long> serialProgress = [], parallelProgress = [];
        int caller = Environment.CurrentManagedThreadId;
        CdTrackGenerator.GenerateToStream(image, serial, parameters, interleave,
            p => serialProgress.Add(p.CompletedBytes), audioByteOrder: order, tone: tone,
            useSimdSampling: simd, useParallelSampling: false);
        CdTrackGenerator.GenerateToStream(image, parallel, parameters, interleave, p =>
        {
            Check(Environment.CurrentManagedThreadId == caller, "progress stays on the caller thread");
            parallelProgress.Add(p.CompletedBytes);
        }, audioByteOrder: order, tone: tone, useSimdSampling: simd);
        Check(parallel.Length == parameters.TotalBytes && parallel.ToArray().AsSpan().SequenceEqual(serial.ToArray()),
            $"serial/parallel bytes: {parameters.Sectors} sectors, cubic={parameters.PitchLinear}, interleave={interleave}, order={order}");
        Check(parallelProgress.SequenceEqual(serialProgress), "original progress byte positions and final callback are preserved");
    }

    private static void TestTargetsAndFiles(string image, string directory)
    {
        foreach (CalibrationArtwork artwork in new[] { CalibrationArtwork.ReferenceCross,
            CalibrationArtwork.HalfDisc, CalibrationArtwork.WavyBoundary, CalibrationArtwork.SingleRadialLine,
            CalibrationArtwork.SweptReference })
        foreach (bool cubic in new[] { false, true })
        {
            CalibrationParameters geometry = new(CalibrationDiscKind.Cd, 24.5, 57.5, 65, 1200,
                cubic ? .3 : 0, cubic ? -.25 : 0, cubic ? .12 : 0);
            CalibrationTarget target = CalibrationTarget.Create(geometry, artwork: artwork);
            using CheckedStream serial = new(), parallel = new();
            CdTrackGenerator.GeneratePatternToStream(target.Sample, serial, CdDiscParameters.FromCalibration(target), true,
                audioByteOrder: CdAudioByteOrder.LittleEndian);
            CdTrackGenerator.GeneratePatternToStream(target, parallel, true, audioByteOrder: CdAudioByteOrder.LittleEndian);
            Check(parallel.ToArray().AsSpan().SequenceEqual(serial.ToArray()), "frozen target parallel output: " + artwork);
            if (artwork == CalibrationArtwork.ReferenceCross && cubic)
            {
                string path = Path.Combine(directory, "reference.wav");
                CdTrackGenerator.GeneratePattern(target, path, true);
                Check(File.ReadAllBytes(path).AsSpan(44).SequenceEqual(serial.ToArray()), "reference WAV uses parallel target pipeline");
            }
        }
        CdDiscParameters parameters = Parameters(65, true);
        foreach (bool wave in new[] { false, true })
        {
            string path = Path.Combine(directory, wave ? "image.wav" : "image.raw");
            using CheckedStream expected = new();
            CdTrackGenerator.GenerateToStream(image, expected, parameters, true,
                audioByteOrder: wave ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian,
                useParallelSampling: false);
            CdTrackGenerator.Generate(image, path, parameters, true);
            Check(File.ReadAllBytes(path).AsSpan(wave ? 44 : 0).SequenceEqual(expected.ToArray()),
                "ordinary image file wrapper uses the shared parallel stream pipeline");
        }
    }

    private static void TestCallbackOrder()
    {
        foreach (bool simd in new[] { false, true })
        {
            CdDiscParameters parameters = Parameters(65, true);
            using CheckedStream output = new();
            int caller = Environment.CurrentManagedThreadId;
            long calls = 0;
            CdTrackGenerator.GeneratePatternToStream((x, y) =>
            {
                Check(Environment.CurrentManagedThreadId == caller, "custom callback stays on caller thread");
                Check(output.Length == calls / CdDiscParameters.BytesPerSector * CdDiscParameters.BytesPerSector,
                    "custom callback remains interspersed with the original sector writes");
                calls++;
                return x < y ? (byte)0 : (byte)255;
            }, output, parameters, true, useSimdSampling: simd);
            Check(calls == parameters.TotalBytes, "custom callback is invoked once per byte");
        }
    }

    private static void TestCancellationAndFailure(string image)
    {
        CdDiscParameters parameters = Parameters(BatchSectors + 33, true);
        using CheckedStream complete = new();
        CdTrackGenerator.GenerateToStream(image, complete, parameters, true, useParallelSampling: false);
        byte[] expected = complete.ToArray();
        using CancellationTokenSource cancellation = new();
        using CheckedStream partial = new();
        bool cancelled = false;
        try
        {
            CdTrackGenerator.GenerateToStream(image, partial, parameters, true, p =>
            {
                if (p.CompletedBytes >= 33 * CdDiscParameters.BytesPerSector) cancellation.Cancel();
            }, cancellation.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && partial.Length > 0 && partial.Length < parameters.TotalBytes
            && expected.AsSpan(0, (int)partial.Length).SequenceEqual(partial.ToArray()) && partial.Flushes == 0,
            "cancellation preserves an exact prefix and does not flush pending interleave state");

        using CheckedStream failing = new(failOnWrite: 35);
        bool failed = false;
        try { CdTrackGenerator.GenerateToStream(image, failing, parameters, true); }
        catch (IOException) { failed = true; }
        Check(failed && failing.Writes == 35 && failing.Flushes == 0
            && expected.AsSpan(0, (int)failing.Length).SequenceEqual(failing.ToArray()),
            "write failure stops ordered output without flushing or appending delayed blocks");

        using CancellationTokenSource preCancelled = new();
        preCancelled.Cancel();
        using CheckedStream untouched = new();
        cancelled = false;
        try { CdTrackGenerator.GenerateToStream(image, untouched, parameters, true, cancellationToken: preCancelled.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && untouched.Length == 0 && untouched.Flushes == 0, "pre-cancellation writes nothing");
    }

    private static void TestBoundedPipeline()
    {
        MethodInfo run = typeof(CdTrackGenerator).Assembly.GetType("DiscImageStudio.Cd.CdBlockPipeline")!
            .GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!;
        int caller = Environment.CurrentManagedThreadId;
        int workers = Math.Min(16, Math.Max(1, Environment.ProcessorCount - 1));
        int active = 0, filled = 0, consumed = 0;
        long nextSector = 0;
        HashSet<byte[]> slots = [];
        Action<long, byte[], int> fill = (first, buffer, count) =>
        {
            Interlocked.Increment(ref active);
            try
            {
                for (int index = 0; index < count; index++) buffer[index] = (byte)(first + index);
                Interlocked.Increment(ref filled);
            }
            finally { Interlocked.Decrement(ref active); }
        };
        Action<long, byte[], int> consume = (first, buffer, count) =>
        {
            Check(Environment.CurrentManagedThreadId == caller && Volatile.Read(ref active) == 0,
                "all workers join before caller-thread consumption");
            Check(first == nextSector && filled - consumed <= 2 * workers,
                "blocks remain ordered and lookahead stays bounded under backpressure");
            slots.Add(buffer);
            Check(slots.Count <= 2 * workers, "fixed buffer slots are reused across batches");
            for (int index = 0; index < count; index++) Check(buffer[index] == (byte)(first + index), "reused block has fresh data");
            nextSector += count;
            consumed++;
        };
        long sectors = BatchSectors + 39;
        run.Invoke(null, [sectors, 1, fill, consume, CancellationToken.None]);
        Check(nextSector == sectors && active == 0, "pipeline consumes the final partial block and leaves no workers");

        Action<long, byte[], int> fail = (_, _, _) =>
        {
            Interlocked.Increment(ref active);
            try { throw new IOException("worker fixture"); }
            finally { Interlocked.Decrement(ref active); }
        };
        consumed = 0;
        bool failed = false;
        try { run.Invoke(null, [sectors, 1, fail, (Action<long, byte[], int>)((_, _, _) => consumed++), CancellationToken.None]); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException or AggregateException) { failed = true; }
        Check(failed && active == 0 && consumed == 0, "worker failure joins every worker before unwinding without output");
    }

    private static CdDiscParameters Parameters(int sectors, bool cubic)
        => new(24.5, 57.5, sectors, StartAngleRadians: -.7,
            PitchLinear: cubic ? .3 : 0, PitchQuadratic: cubic ? -.25 : 0, PitchCubic: cubic ? .12 : 0);

    private static void WriteImage(string path)
    {
        const int width = 97, height = 73;
        byte[] pixels = new byte[width * height * 4];
        new Random(57129).NextBytes(pixels);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, width * 4)));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private sealed class CheckedStream(int failOnWrite = int.MaxValue) : MemoryStream
    {
        private readonly int _owner = Environment.CurrentManagedThreadId;
        internal int Writes { get; private set; }
        internal int Flushes { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(Environment.CurrentManagedThreadId == _owner, "stream writes stay on caller thread");
            if (++Writes == failOnWrite) throw new IOException("sink fixture");
            base.Write(buffer);
        }
        public override void Flush()
        {
            Check(Environment.CurrentManagedThreadId == _owner, "flush stays on caller thread");
            Flushes++;
            base.Flush();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD parallel audio: " + message);
    }
}
