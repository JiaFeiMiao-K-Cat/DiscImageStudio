using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;

internal static class CdParallelGenerationBenchmark
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cd-parallel-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const int size = 4096;
            byte[] pixels = new byte[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = ((x / 13 + y / 17) & 1) == 0 ? (byte)0 : (byte)255;
            string image = Path.Combine(directory, "source.png");
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96,
                PixelFormats.Gray8, null, pixels, size)));
            using (FileStream file = File.Create(image)) encoder.Save(file);
            string data = Path.Combine(directory, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "readme.txt"), "CD parallel generation benchmark");
            Console.WriteLine($"cd-parallel environment: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; logical CPUs={Environment.ProcessorCount}");
            Console.WriteLine("cd-parallel fixture: full 359847-sector geometry, 24-58mm, 1200mm/s, angle=0, legacy binary; audio interleaved/big endian; cubic=0.00012/-0.00008/0.00004; raster=4096x4096, reference=original 2400x2400; first 64 MiB, timed around the complete generator call including decode/initialization/first fill and any remaining batch work; one warmup then median 3; no disk output");
            foreach (string route in new[] { "raster", "reference", "Mode1" })
            foreach (bool cubic in new[] { false, true })
            foreach (bool parallel in new[] { false, true })
            {
                double a = cubic ? .00012 : 0, b = cubic ? -.00008 : 0, c = cubic ? .00004 : 0;
                CdDiscParameters disc = new(24, 58, 359847, ImageOuterRadiusMm: 58,
                    PitchLinear: a, PitchQuadratic: b, PitchCubic: c);
                CalibrationTarget target = CalibrationTarget.Create(
                    new(CalibrationDiscKind.Cd, 24, 58, 359847, 1200, a, b, c),
                    artwork: CalibrationArtwork.ReferenceCross);
                CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(data, disc);
                const long bytes = 64L * 1024 * 1024;
                double[] samples = new double[3];
                for (int round = -1; round < samples.Length; round++)
                {
                    using PrefixSink output = new(bytes);
                    Stopwatch timer = Stopwatch.StartNew();
                    try
                    {
                        if (route == "reference")
                            CdTrackGenerator.GeneratePatternToStream(target, output, true, useParallelSampling: parallel);
                        else if (route == "Mode1")
                            CdDataImageGenerator.GenerateToStream(image, output, disc, plan, useParallelSampling: parallel);
                        else
                            CdTrackGenerator.GenerateToStream(image, output, disc, true, useParallelSampling: parallel);
                    }
                    catch (PrefixCompleteException) { }
                    timer.Stop();
                    if (round >= 0) samples[round] = output.BytesWritten / 1048576d / timer.Elapsed.TotalSeconds;
                }
                Array.Sort(samples);
                Console.WriteLine($"cd-parallel route={route} cubic={cubic} parallel={parallel}: {samples[1]:F2} MiB/s (runs {string.Join(", ", samples.Select(x => x.ToString("F2")))})");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class PrefixCompleteException : Exception { }

    private sealed class PrefixSink(long limit) : Stream
    {
        public long BytesWritten { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BytesWritten += buffer.Length;
            if (BytesWritten >= limit)
            {
                throw new PrefixCompleteException();
            }
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
