using System.IO;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class BluRayPatternRangeTests
{
    internal static void Run()
    {
        BluRayDiscParameters disc = new(128);
        BluRayPatternRangeMapping mapping = new(disc, static (_, _) => 0, 32, 96, invertMapping: false);
        byte[] payload = new byte[BluRayDiscParameters.BytesPerSector];
        mapping.FillScrambledPayload(new BluRaySectorContext(0, disc.GetPhysicalSectorNumber(0), disc), payload);
        Require(payload.AsSpan().IndexOfAnyExcept((byte)0) < 0, "before-range payload stays zero");
        mapping.FillScrambledPayload(new BluRaySectorContext(32, disc.GetPhysicalSectorNumber(32), disc), payload);
        Require(payload.AsSpan().IndexOfAnyExcept(BluRayRunLengthMapping.ThreeTByte) < 0,
            "drawing range carries the dark 3T byte");
        mapping.FillScrambledPayload(new BluRaySectorContext(96, disc.GetPhysicalSectorNumber(96), disc), payload);
        Require(payload.AsSpan().IndexOfAnyExcept((byte)0) < 0, "after-range payload stays zero");

        BluRayPatternRangeMapping invertedMapping = new(disc, static (_, _) => 0, 32, 96, invertMapping: true);
        invertedMapping.FillScrambledPayload(
            new BluRaySectorContext(32, disc.GetPhysicalSectorNumber(32), disc), payload);
        Require(payload.AsSpan().IndexOfAnyExcept(BluRayRunLengthMapping.SixTByte) < 0,
            "polarity swaps the in-range run class");

        Throws<ArgumentOutOfRangeException>(() => new BluRayPatternRangeMapping(
            disc, static (_, _) => 0, 31, 96, invertMapping: false), "unaligned start rejected");
        Throws<ArgumentOutOfRangeException>(() => new BluRayPatternRangeMapping(
            disc, static (_, _) => 0, 32, 160, invertMapping: false), "range past the disc rejected");

        TestParallelRangeMatchesScalar();
        TestWideBandStreamEdges();
        TestSequentialSamplerContract();
        TestProceduralGeometryAgainstScalar();
        TestCancellation();
        Console.WriteLine("blu-ray-pattern-range: passed (drawing range, polarity, parallel/scalar equality, sampler order, cancellation)");
    }

    private static void TestWideBandStreamEdges()
    {
        const uint sectors = BluRayDiscParameters.SectorsPerLayer;
        const uint drawingStart = 16384, drawingEnd = sectors - 32768;
        uint clusters = (drawingEnd - drawingStart) / 32;
        uint Boundary(long permille) => drawingStart + checked((uint)((clusters * permille + 999) / 1000 * 32));
        uint[] edges = [drawingStart, Boundary(35), Boundary(170), Boundary(305), Boundary(510),
            Boundary(705), Boundary(955), drawingEnd];
        foreach (var (inner, outer, invert) in new[] { (24.0, 58.0, false), (21.0, 54.5, false), (24.0, 58.0, true) })
        {
            var p = new CalibrationParameters(CalibrationDiscKind.BluRay, inner, outer,
                sectors, CalibrationParameters.BluRayChannelBitLengthNm, BluRayInvertMapping: invert,
                DrawingStartSector: drawingStart, DrawingEndSectorExclusive: drawingEnd);
            var target = CalibrationTarget.Create(p, artwork: CalibrationArtwork.WideAddressBands);
            var disc = CalibrationViewModel.BluRayParameters(p);
            var mapping = CalibrationViewModel.BluRayPatternMapping(target);
            byte light = invert ? BluRayRunLengthMapping.ThreeTByte : BluRayRunLengthMapping.SixTByte;
            byte dark = invert ? BluRayRunLengthMapping.SixTByte : BluRayRunLengthMapping.ThreeTByte;
            byte[] tones = [0, light, dark, light, dark, light, dark, light, 0];
            for (int i = 0; i < edges.Length; i++)
            {
                // Verify the complete output bytes of one cluster on either side of
                // every independently calculated address, including the blank margins.
                uint start = edges[i] - 32;
                using MemoryStream actual = new(), expected = new();
                BluRayStreamingGenerator.GenerateToStream(actual, new(disc, start, 64), mapping);
                BluRayStreamingGenerator.GenerateToStream(expected, new(disc, start, 32),
                    new BluRayConstantByteMapping(tones[i]));
                BluRayStreamingGenerator.GenerateToStream(expected, new(disc, edges[i], 32),
                    new BluRayConstantByteMapping(tones[i + 1]));
                Require(actual.ToArray().AsSpan().SequenceEqual(expected.ToArray()),
                    $"v12 BD host bytes either side of LBA {edges[i]} (invert={invert}, radii={inner}/{outer})");
            }
        }
    }

    private static void TestParallelRangeMatchesScalar()
    {
        // Thirty-five clusters cross the largest batch and leave a partial final
        // batch. Keep one blank cluster at each edge of the generated window.
        foreach ((BluRayDiscParameters disc, uint start) in new[]
        {
            (new BluRayDiscParameters(StartAngleDegrees: 31.5), 6_119_680U),
            (new BluRayDiscParameters(TotalSectors: 1184, StartAngleDegrees: -83), 0U),
        })
        {
            uint drawingStart = start + 32, drawingEnd = start + 1088;
            CalibrationTarget target = CalibrationTarget.Create(new(
                CalibrationDiscKind.BluRay, disc.InnerRadiusMm, disc.OuterRadiusMm,
                disc.TotalSectors, CalibrationParameters.BluRayChannelBitLengthNm,
                DrawingStartSector: drawingStart, DrawingEndSectorExclusive: drawingEnd));
            foreach (bool invert in new[] { false, true })
            {
                BluRayPatternRangeMapping parallel = new(disc, target.Sample,
                    drawingStart, drawingEnd, invert, allowParallelSampling: true);
                BluRayPatternRangeMapping sequential = new(disc, target.Sample,
                    drawingStart, drawingEnd, invert, allowParallelSampling: false);
                Require(((IBluRayBatchPayloadMapping)parallel).CanGenerateInParallel(disc),
                    "the range wrapper exposes its sampler's parallel opt-in");
                Require(!((IBluRayBatchPayloadMapping)sequential).CanGenerateInParallel(disc),
                    "the range wrapper preserves the sampler's sequential opt-out");

                BluRayStreamingOptions options = new(disc, start, 1120);
                using MemoryStream expected = new(), actual = new(), optedOut = new();
                // This strategy exposes only the sector API and the scalar
                // luminance mapper, independently bypassing both optimizations.
                BluRayStreamingGenerator.GenerateToStream(expected, options, new ScalarRange(
                    new(disc, target.Sample, new ScalarRunLength(invert)), drawingStart, drawingEnd));
                BluRayStreamingGenerator.GenerateToStream(optedOut, options, sequential);
                int caller = Environment.CurrentManagedThreadId;
                List<long> completed = [];
                BluRayStreamingGenerator.GenerateToStream(actual, options, parallel,
                    new InlineProgress(value =>
                    {
                        Require(Environment.CurrentManagedThreadId == caller,
                            "pattern progress stays on the caller thread");
                        Require(value.CompletedBytes == actual.Length,
                            "pattern progress follows ordered output");
                        completed.Add(value.CompletedBytes);
                    }));
                Require(actual.ToArray().SequenceEqual(expected.ToArray())
                    && optedOut.ToArray().SequenceEqual(expected.ToArray()),
                    $"pattern parallel and sequential bytes match the scalar reference at {start}, invert={invert}");
                Require(actual.Length == options.ContentLength && completed.Count == 36
                    && completed[0] == 0
                    && completed.Zip(completed.Skip(1), (a, b) => b - a).All(delta => delta == 65536),
                    "pattern output remains complete and ordered across batch boundaries");

                // Blank scrambled-domain payload still needs the ordinary XOR
                // transform. Compare the on-disc bytes, not literal stream zeros.
                using MemoryStream blankBefore = new(), blankAfter = new();
                BluRayStreamingGenerator.GenerateToStream(blankBefore, new(disc, start, 32),
                    new BluRayConstantByteMapping(0));
                BluRayStreamingGenerator.GenerateToStream(blankAfter, new(disc, drawingEnd, 32),
                    new BluRayConstantByteMapping(0));
                byte[] bytes = actual.ToArray();
                Require(bytes.AsSpan(0, 65536).SequenceEqual(blankBefore.ToArray())
                    && bytes.AsSpan(bytes.Length - 65536).SequenceEqual(blankAfter.ToArray()),
                    "parallel range output preserves the zero payload before and after the drawing interval");
            }
        }
    }

    private static void TestSequentialSamplerContract()
    {
        BluRayDiscParameters disc = new(StartAngleDegrees: 89.999999);
        const uint start = 6_119_680;
        int caller = Environment.CurrentManagedThreadId, samples = 0;
        BluRayImagePayloadMapping positions = new(disc, static (_, _) => 0, new ScalarRunLength(false));
        byte Sample(double x, double y)
        {
            Require(Environment.CurrentManagedThreadId == caller,
                "a sequential pattern sampler stays on its caller thread");
            BluRayTrackPoint expected = positions.GetPosition(start + 32 + (uint)(samples / 2048), samples % 2048).TrackPoint;
            Require(x == expected.Xmm && y == expected.Ymm,
                "four-point sampling preserves each scalar coordinate and sector/byte callback order");
            samples++;
            return 128;
        }
        BluRayPatternRangeMapping sequential = new(disc, Sample,
            start + 32, start + 64, invertMapping: false, allowParallelSampling: false);
        BluRayStreamingGenerator.GenerateToStream(Stream.Null, new(disc, start, 96), sequential);
        Require(samples == 32 * 2048,
            "only drawing-range bytes invoke the sampler, once per byte");

        samples = 0;
        BluRayPatternRangeMapping defaultMapping = new(disc, Sample,
            start + 32, start + 64, invertMapping: false);
        Require(!((IBluRayBatchPayloadMapping)defaultMapping).CanGenerateInParallel(disc),
            "an arbitrary pattern callback stays sequential without explicit opt-in");
        BluRayStreamingGenerator.GenerateToStream(Stream.Null, new(disc, start, 96), defaultMapping);
        Require(samples == 32 * 2048, "default callbacks preserve the same count and order");
    }

    private static void TestProceduralGeometryAgainstScalar()
    {
        // Real-disc geometry enables the four-point path; the tiny-capacity and
        // small-inner-radius cases retain the numerical scalar fallback.
        foreach (BluRayDiscParameters disc in new[]
        {
            new BluRayDiscParameters(StartAngleDegrees: -27),
            new BluRayDiscParameters(TotalSectors: 32, StartAngleDegrees: 173),
            new BluRayDiscParameters(InnerRadiusMm: 0.5),
        })
        {
            foreach (bool invert in new[] { false, true })
            {
                static byte Sample(double x, double y) => (byte)((int)Math.Abs(x * 31 + y * 19) & 255);
                BluRayImagePayloadMapping scalar = new(disc, Sample, new ScalarRunLength(invert));
                BluRayImagePayloadMapping optimized = new(disc, Sample, new BluRayRunLengthMapping(invert));
                byte[] expected = new byte[2048], actual = new byte[2048];
                foreach (uint sector in new[] { 0U, 1U, 31U, disc.TotalSectors / 2, disc.TotalSectors - 1 }.Distinct())
                {
                    BluRaySectorContext context = new(sector, disc.GetPhysicalSectorNumber(sector), disc);
                    scalar.FillScrambledPayload(context, expected);
                    optimized.FillScrambledPayload(context, actual);
                    Require(actual.SequenceEqual(expected),
                        $"procedural sampling matches scalar with capacity={disc.TotalSectors}, sector={sector}, invert={invert}");
                }
            }
        }
    }

    private static void TestCancellation()
    {
        BluRayDiscParameters disc = new();
        BluRayPatternRangeMapping pattern = new(disc, static (_, _) => 0, 0, 640,
            invertMapping: false, allowParallelSampling: true);
        BluRayStreamingOptions options = new(disc, 0, 640);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        using MemoryStream untouched = new();
        Throws<OperationCanceledException>(() => BluRayStreamingGenerator.GenerateToStream(untouched,
            options, pattern, cancellationToken: cancelled.Token), "pre-cancelled pattern generation is rejected");
        Require(untouched.Length == 0, "pre-cancelled pattern generation emits nothing");

        using CancellationTokenSource stopAfterWrite = new();
        using MemoryStream partial = new();
        Throws<OperationCanceledException>(() => BluRayStreamingGenerator.GenerateToStream(partial, options, pattern,
            new InlineProgress(value => { if (value.CompletedBytes > 0) stopAfterWrite.Cancel(); }), stopAfterWrite.Token),
            "pattern cancellation stops after the first emitted cluster");
        Require(partial.Length == 65536, "cancellation emits no subsequent prepared pattern clusters");
    }

    private sealed class ScalarRange(BluRayImagePayloadMapping image, uint start, uint end) : IBluRayPayloadMapping
    {
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            if (context.Sector < start || context.Sector >= end) destination.Clear();
            else image.FillScrambledPayload(context, destination);
        }
    }

    private sealed class ScalarRunLength(bool invert) : IBluRayLuminanceMapping
    {
        private readonly BluRayRunLengthMapping _mapping = new(invert);
        public byte Map(byte luminance, BluRayPayloadBytePosition position) => _mapping.Map(luminance, position);
    }

    private sealed class InlineProgress(Action<BluRayStreamingProgress> report) : IProgress<BluRayStreamingProgress>
    {
        public void Report(BluRayStreamingProgress value) => report(value);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Blu-ray pattern range: " + message + ".");
        }
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Blu-ray pattern range: " + message + ".");
    }
}
