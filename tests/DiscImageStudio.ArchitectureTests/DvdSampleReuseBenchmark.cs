using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;

internal static class DvdSampleReuseBenchmark
{
    internal static void Run(bool readCompatibility = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dvd-sampling-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string binary = Path.Combine(directory, "binary.png");
            string colour = Path.Combine(directory, "colour-alpha.png");
            WriteImage(binary, colour: false);
            WriteImage(colour, colour: true);
            Console.WriteLine($"dvd-sampling environment: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; logical CPUs={Environment.ProcessorCount}");
            Console.WriteLine($"dvd-sampling read compatibility: {readCompatibility}");
            Console.WriteLine("dvd-sampling fixture: full 2297888-sector geometry, 24-58mm, 133.3nm, angle=0, legacy binary, default parallelism; cubic=0.00012/-0.00008/0.00004; image=4096x4096, reference=original 2400x2400; first 64 MiB, complete generator-call timing includes decoding/initialization/first and last batch work; one warmup then median 3; no disk output");
            foreach (string route in new[] { "reference", "binary", "colour-alpha" })
            foreach (bool cubic in new[] { false, true })
            foreach (bool reuse in new[] { false, true })
            {
                double a = cubic ? .00012 : 0, b = cubic ? -.00008 : 0, c = cubic ? .00004 : 0;
                CalibrationTarget target = CalibrationTarget.Create(
                    new(CalibrationDiscKind.Dvd, 24, 58, 2297888, 133.3, a, b, c,
                        DvdReadCompatibility: readCompatibility),
                    artwork: CalibrationArtwork.ReferenceCross);
                DvdStreamingOptions options = new(2297888, 24, 58,
                    PitchLinear: a, PitchQuadratic: b, PitchCubic: c, UseSampleReuse: reuse,
                    ReadCompatibility: readCompatibility);
                double[] samples = new double[3];
                for (int round = -1; round < samples.Length; round++)
                {
                    using PrefixSink output = new(64L * 1024 * 1024);
                    Stopwatch timer = Stopwatch.StartNew();
                    try
                    {
                        if (route == "reference") DvdStreamingGenerator.GeneratePattern(target, output, options);
                        else DvdStreamingGenerator.Generate(route == "binary" ? binary : colour, output, options);
                    }
                    catch (PrefixCompleteException) { }
                    timer.Stop();
                    if (round >= 0) samples[round] = output.BytesWritten / 1048576d / timer.Elapsed.TotalSeconds;
                }
                Array.Sort(samples);
                Console.WriteLine($"dvd-sampling route={route} cubic={cubic} reuse={reuse}: {samples[1]:F2} MiB/s (runs {string.Join(", ", samples.Select(x => x.ToString("F2")))})");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void WriteImage(string path, bool colour)
    {
        const int size = 4096;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int at = (y * size + x) * 4;
            if (colour)
            {
                pixels[at] = (byte)((x * 7 + y * 3) >> 4);
                pixels[at + 1] = (byte)((x * 3 + y * 5) >> 4);
                pixels[at + 2] = (byte)((x * 5 + y * 7) >> 4);
                pixels[at + 3] = (byte)(((x / 128 + y / 128) & 3) == 0 ? 128 : 255);
            }
            else
            {
                byte value = ((x / 13 + y / 17) & 1) == 0 ? (byte)0 : (byte)255;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96,
            PixelFormats.Bgra32, null, pixels, size * 4)));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private sealed class PrefixCompleteException : Exception { }
    private sealed class PrefixSink(long limit) : Stream
    {
        public long BytesWritten { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BytesWritten += buffer.Length;
            if (BytesWritten >= limit) throw new PrefixCompleteException();
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
