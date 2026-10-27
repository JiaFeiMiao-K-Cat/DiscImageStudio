using System.IO;
using System.Runtime.Intrinsics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;
using DvdImageSolver.Solver;

internal static class DvdSimdSamplingTests
{
    private const uint TotalSectors = 2_297_888;
    private const double InnerRadiusMm = 23.991460627731737;
    private const double OuterRadiusMm = 57.964893657925366;
    // The checked-in Verbatim DVD-R AZO 43533 measured preset.
    private const double MeasuredLinear = -3.9256854319227684E-05;
    private const double MeasuredQuadratic = 6.964795915429256E-05;
    private const double MeasuredCubic = -4.670918950137523E-05;

    internal static void Run()
    {
        TestConstantPitchAgainstLegacyFormula();
        TestGeometryLanes();
        TestAnalyticParity();
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-simd-sampling-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string binary = Path.Combine(directory, "opaque-binary.png");
            WriteImage(binary, 73, 61, (x, y) =>
            {
                byte value = ((x / 3 + y / 5) & 1) == 0 ? (byte)0 : (byte)255;
                return (value, value, value, (byte)255);
            });
            string translucent = Path.Combine(directory, "translucent-colour.png");
            byte[] alphas = [0, 64, 127, 128, 192, 255];
            WriteImage(translucent, 67, 79, (x, y) =>
                ((byte)((x * 13 + y * 7) % 256), (byte)((x * 5 + y * 19) % 256),
                    (byte)((x * 23 + y * 3) % 256), alphas[(x / 3 + y / 5) % alphas.Length]));

            TestRasterParity(binary, translucent);
            TestReusableBatchParity(translucent);
            TestAlphaThresholdEdges(directory);
            TestBinaryHalfPixelBoundaries(directory);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }

        Console.WriteLine(Vector256.IsHardwareAccelerated
            ? "dvd-simd-sampling: passed (scalar/SIMD payload and count parity, measured cubic geometry, alpha and half-pixel boundaries, parallel ordering)"
            : "dvd-simd-sampling: passed (scalar/automatic fallback parity; SIMD hardware assertions skipped)");
    }

    private static void TestAnalyticParity()
    {
        foreach (bool measured in new[] { false, true })
        foreach (uint start in new[] { 0u, (TotalSectors / 2) & ~15u, TotalSectors - 16 })
        foreach (int kind in new[] { 0, 1, 2 })
        {
            MultiBlockSolveOptions options = Options(start, measured) with
            {
                ImageMapping = Mapping(measured) with
                {
                    StartAngleDegrees = start == 0 ? 37.25 : 1e12,
                    Clockwise = start != 0,
                },
            };
            List<DvdCodewordSample> expectedCoordinates = [];
            using MemoryStream scalar = new(), enabled = new();
            WriteAnalytic(kind, sample =>
            {
                expectedCoordinates.Add(sample);
                return AnalyticValue(sample);
            }, scalar, options with { UseSimdSampling = false });
            int calls = 0;
            WriteAnalytic(kind, sample =>
            {
                Require(calls < expectedCoordinates.Count, "analytic sampling does not add callbacks");
                DvdCodewordSample expected = expectedCoordinates[calls++];
                AssertSameBits(expected.Xmm, sample.Xmm, "analytic SIMD X coordinate");
                AssertSameBits(expected.Ymm, sample.Ymm, "analytic SIMD Y coordinate");
                AssertSameBits(expected.TrackAngleRadians, sample.TrackAngleRadians, "analytic SIMD track angle");
                Require(expected.GlobalCodewordIndex == sample.GlobalCodewordIndex,
                    "analytic SIMD retains physical codeword indices and callback order");
                return AnalyticValue(sample);
            }, enabled, options with { UseSimdSampling = true });
            Require(calls == 32768 && enabled.ToArray().AsSpan().SequenceEqual(scalar.ToArray()),
                "analytic scalar/SIMD bytes and exactly-once callback counts agree");
        }

        // Five blocks reuse bounded slots and leave a partial two-worker batch.
        // The callback is pure; both paths retain their existing parallel contract.
        foreach (int kind in new[] { 0, 1, 2 })
        {
            MultiBlockSolveOptions options = Options(TotalSectors - 80, measured: true) with { FillSectors = 80 };
            using MemoryStream scalar = new(), parallel = new();
            WriteAnalytic(kind, AnalyticValue, scalar, options with { UseSimdSampling = false, FastParallelism = 1 });
            WriteAnalytic(kind, AnalyticValue, parallel, options with { UseSimdSampling = true, FastParallelism = 2 });
            Require(parallel.Length == 80 * 2048L && parallel.ToArray().AsSpan().SequenceEqual(scalar.ToArray()),
                "analytic SIMD parallel batches preserve payload ordering and reused buffers");
        }
    }

    private static byte AnalyticValue(DvdCodewordSample sample)
        => ((BitConverter.DoubleToInt64Bits(sample.Xmm) ^ BitConverter.DoubleToInt64Bits(sample.Ymm)
            ^ BitConverter.DoubleToInt64Bits(sample.TrackAngleRadians) ^ (long)sample.GlobalCodewordIndex) & 1) == 0
            ? (byte)0 : (byte)255;

    private static void WriteAnalytic(int kind, Func<DvdCodewordSample, byte> sample, Stream output,
        MultiBlockSolveOptions options)
    {
        if (kind == 0)
            FastDispersionImageWriter.WritePatternStream((x, y) => sample(new(x, y, 0, 0)), output, options);
        else if (kind == 1)
            FastDispersionImageWriter.WriteCodewordPatternStream(sample, output, options);
        else
            FastDispersionImageWriter.WriteScrambledCodewordPatternStream(sample, output, options);
    }

    private static void TestRasterParity(string binary, string translucent)
    {
        uint[] starts = [0, (TotalSectors / 2) & ~15u, TotalSectors - 16];
        double[] angles = [0, 37.25, -121.5];
        byte[] thresholds = [1, 128, 255];
        bool sawPartiallyControlledImage = false;
        foreach (bool measured in new[] { false, true })
        for (int range = 0; range < starts.Length; range++)
        foreach (bool clockwise in new[] { false, true })
        {
            MultiBlockSolveOptions options = Options(starts[range], measured) with
            {
                ImageMapping = Mapping(measured) with
                {
                    StartAngleDegrees = angles[range],
                    Clockwise = clockwise,
                    AlphaThreshold = thresholds[range],
                },
            };
            string label = $"{(measured ? "measured cubic" : "constant")}, LBA {starts[range]}, clockwise={clockwise}";
            FastDvdStreamSummary opaque = CompareOutputs(binary, options, "opaque binary " + label);
            Require(opaque.ControlledWords == options.FillSectors * 2048L,
                "opaque binary sampling controls every payload word: " + label);
            FastDvdStreamSummary partial = CompareOutputs(translucent, options, "translucent colour " + label);
            sawPartiallyControlledImage |= partial.ControlledWords > 0
                && partial.ControlledWords < options.FillSectors * 2048L;
        }
        Require(sawPartiallyControlledImage, "translucent fixture exercises both controlled and preserved random payload bytes");

        // Three blocks cross the two-block serial batch boundary and leave a
        // partial parallel batch; order, PSN masks and random seeds must survive.
        MultiBlockSolveOptions multiple = Options(TotalSectors - 48, measured: true) with
        {
            FillSectors = 48,
            ImageMapping = Mapping(measured: true) with
            {
                Clockwise = false,
                StartAngleDegrees = 213.75,
                AlphaThreshold = 128,
            },
        };
        CompareOutputs(translucent, multiple, "parallel three-block outer range", enabledParallelism: 2);
    }

    private static void TestReusableBatchParity(string imagePath)
    {
        const int blockCount = 7;
        foreach (bool zeroInitial in new[] { false, true })
        {
            MultiBlockSolveOptions options = Options(TotalSectors - blockCount * 16, measured: true) with
            {
                FillSectors = blockCount * 16,
                ZeroInitialPayload = zeroInitial,
                ImageMapping = Mapping(measured: true) with { AlphaThreshold = 128 },
            };
            using MemoryStream independentBlocks = new();
            long controlledWords = 0;
            for (int block = 0; block < blockCount; block++)
            {
                // A fresh one-block call cannot inherit data from a reused slot.
                // Preserve the original per-block seed and physical LBA.
                FastDvdStreamSummary isolated = FastDispersionImageWriter.WriteStream(imagePath, independentBlocks,
                    options with
                    {
                        StartLba = options.StartLba + (uint)(block * 16),
                        FillSectors = 16,
                        RandomSeed = unchecked(options.RandomSeed + block * 1_000_003),
                        FastParallelism = 1,
                        UseSimdSampling = false,
                    });
                controlledWords += isolated.ControlledWords;
            }
            foreach (int parallelism in new[] { 1, 2 })
            {
                // Seven blocks force slot reuse and a partial last batch for
                // both the two-slot serial and four-slot parallel batches.
                using MemoryStream reused = new();
                FastDvdStreamSummary summary = FastDispersionImageWriter.WriteStream(imagePath, reused,
                    options with { FastParallelism = parallelism });
                Require(reused.ToArray().AsSpan().SequenceEqual(independentBlocks.ToArray()),
                    $"reused batch bytes match fresh blocks, zero={zeroInitial}, workers={parallelism}");
                Require(summary.BytesWritten == blockCount * 32768L
                    && summary.ControlledWords == controlledWords && summary.CompletedBlocks == blockCount,
                    "reused buffers preserve exact output length, order and controlled count");
            }
        }
    }

    private static FastDvdStreamSummary CompareOutputs(string path, MultiBlockSolveOptions options,
        string label, int enabledParallelism = 1, Action<byte[]>? inspectScalar = null)
    {
        using MemoryStream scalar = new(), enabled = new();
        FastDvdStreamSummary expected = FastDispersionImageWriter.WriteStream(path, scalar,
            options with { UseSimdSampling = false, FastParallelism = 1 });
        FastDvdStreamSummary actual = FastDispersionImageWriter.WriteStream(path, enabled,
            options with { UseSimdSampling = true, FastParallelism = enabledParallelism });
        byte[] expectedBytes = scalar.ToArray();
        byte[] actualBytes = enabled.ToArray();
        Require(expectedBytes.SequenceEqual(actualBytes), "SIMD/fallback preserves every payload byte: " + label);
        Require(expected.ControlledWords == actual.ControlledWords,
            "SIMD/fallback preserves ControlledWords: " + label);
        Require(actual.BytesWritten == options.FillSectors * 2048L
            && scalar.Length == actual.BytesWritten && enabled.Length == actual.BytesWritten
            && expected.BytesWritten == actual.BytesWritten
            && expected.CompletedBlocks == actual.CompletedBlocks
            && actual.CompletedBlocks == options.FillSectors / 16
            && expected.SectorsWritten == actual.SectorsWritten
            && actual.SectorsWritten == options.FillSectors,
            "both samplers report the complete selected range without an LBA prefix: " + label);
        if (options.ZeroInitialPayload && actual.ControlledWords == 0)
            Require(actualBytes.All(value => value == 0), "excluded alpha samples retain initial zero payload: " + label);
        inspectScalar?.Invoke(expectedBytes);
        return actual;
    }

    private static void TestBinaryHalfPixelBoundaries(string directory)
    {
        const int size = 65;
        const double halfPixel = 32.5;
        double scale = (size - 1) / (2 * OuterRadiusMm);
        string path = Path.Combine(directory, "binary-half-pixel-stripes.png");
        WriteImage(path, size, size, (x, _) =>
        {
            byte value = (x & 1) == 0 ? (byte)0 : (byte)255;
            return (value, value, value, (byte)255);
        });
        uint[] starts = [0, (TotalSectors / 2) & ~15u, TotalSectors - 16];
        int[] selectedPayloads = [0, 1, 3];
        double[] pixelOffsets = [-1e-8, 0, 1e-8];
        for (int range = 0; range < starts.Length; range++)
        {
            MultiBlockSolveOptions options = Options(starts[range], measured: true);
            DvdCodewordSample selected = default;
            int payloadIndex = selectedPayloads[range];
            int callbacks = 0;
            using (MemoryStream capture = new())
            {
                FastDispersionImageWriter.WriteCodewordPatternStream(sample =>
                {
                    if (callbacks++ == payloadIndex) selected = sample;
                    return 0;
                }, capture, options with { UseSimdSampling = false });
            }
            Require(callbacks == 32768, "capture an actual payload sample for the half-pixel boundary fixture");
            double radius = Math.Sqrt(selected.Xmm * selected.Xmm + selected.Ymm * selected.Ymm);
            byte[] selectedBytes = new byte[pixelOffsets.Length];
            for (int offset = 0; offset < pixelOffsets.Length; offset++)
            {
                double targetPixel = halfPixel + pixelOffsets[offset];
                double startAngle = AlignStartAngle(radius, selected.TrackAngleRadians, targetPixel, scale);
                double polar = startAngle * Math.PI / 180 - selected.TrackAngleRadians;
                double actualPixel = (size - 1) / 2d + radius * Math.Cos(polar) * scale;
                Require(Math.Abs(actualPixel - targetPixel) < 5e-9,
                    "adversarial sample stays within five billionths of the requested pixel position");
                if (pixelOffsets[offset] != 0)
                    Require(Math.Sign(actualPixel - halfPixel) == Math.Sign(pixelOffsets[offset]),
                        "boundary neighbours lie on opposite sides of nearest-pixel rounding");
                CompareOutputs(path, options with
                {
                    ImageMapping = options.ImageMapping with { StartAngleDegrees = startAngle },
                }, $"binary half-pixel boundary, LBA {starts[range]}, offset {pixelOffsets[offset]:R}",
                    inspectScalar: payload => selectedBytes[offset] = payload[payloadIndex]);
            }
            Require(selectedBytes[0] != selectedBytes[2],
                "half-pixel neighbours select different black/white pixels at the chosen physical sample");
        }

        MultiBlockSolveOptions largeAngle = Options(TotalSectors - 16, measured: true);
        CompareOutputs(path, largeAngle with
        {
            ImageMapping = largeAngle.ImageMapping with { StartAngleDegrees = 1e15 },
        }, "large start angle preserves scalar trigonometric fallback");
    }

    private static double AlignStartAngle(double radius, double trackAngle, double pixel, double scale)
    {
        double desiredPolar = Math.Acos((pixel - 32) / (scale * radius));
        double initial = (desiredPolar + trackAngle) * 180 / Math.PI;
        double best = initial;
        double bestError = double.PositiveInfinity;
        double below = initial, above = initial;
        // A full-disc unwrapped angle loses a few low bits during conversion to
        // degrees and back. Search adjacent representable inputs so the test
        // targets the rounding boundary rather than that conversion error.
        for (int step = 0; step < 17; step++)
        {
            Consider(below);
            Consider(above);
            below = Math.BitDecrement(below);
            above = Math.BitIncrement(above);
        }
        return best;

        void Consider(double candidate)
        {
            double polar = candidate * Math.PI / 180 - trackAngle;
            double error = Math.Abs(32 + radius * Math.Cos(polar) * scale - pixel);
            if (error >= bestError) return;
            bestError = error;
            best = candidate;
        }
    }

    private static void TestAlphaThresholdEdges(string directory)
    {
        foreach (var sample in new[]
        {
            (Alpha: (byte)0, Threshold: (byte)0, ControlsAll: true),
            (Alpha: (byte)0, Threshold: (byte)1, ControlsAll: false),
            (Alpha: (byte)128, Threshold: (byte)128, ControlsAll: true),
            (Alpha: (byte)128, Threshold: (byte)129, ControlsAll: false),
            (Alpha: (byte)255, Threshold: (byte)255, ControlsAll: true),
        })
        {
            string path = Path.Combine(directory, $"alpha-{sample.Alpha}-{sample.Threshold}.png");
            WriteImage(path, 31, 37, (_, _) => ((byte)37, (byte)149, (byte)203, sample.Alpha));
            MultiBlockSolveOptions options = Options(0, measured: true) with
            {
                ZeroInitialPayload = true,
                ImageMapping = Mapping(measured: true) with { AlphaThreshold = sample.Threshold },
            };
            FastDvdStreamSummary summary = CompareOutputs(path, options,
                $"alpha {sample.Alpha}, threshold {sample.Threshold}");
            Require(summary.ControlledWords == (sample.ControlsAll ? options.FillSectors * 2048L : 0),
                "alpha equality is included and alpha below threshold is excluded");
        }
    }

    private static void TestGeometryLanes()
    {
        const double length = TotalSectors * 38_688d * 133.3e-6;
        foreach (var coefficients in new[]
        {
            (0d, 0d, 0d),
            (MeasuredLinear, MeasuredQuadratic, MeasuredCubic),
            (0.18, -0.12, 0.06),
            (-0.4, 0.3, 0.2),
            (-3.8, 3.8, 0d),
        })
        {
            DvdTrackGeometry geometry = DvdTrackGeometry.Create(InnerRadiusMm, OuterRadiusMm,
                length, coefficients.Item1, coefficients.Item2, coefficients.Item3);
            // Always check the scalar fallback, including endpoint clamping and
            // non-monotonic lanes spanning different interpolation segments.
            AssertGeometryLanes(geometry, Vector256.Create(-1d, 0d, length, length + 1), "endpoint fallback");
            AssertGeometryLanes(geometry, Vector256.Create(length * .9, length * .1, length * .7, length * .3),
                "non-monotonic cross-segment lanes");
            if (!Vector256.IsHardwareAccelerated) continue;

            for (int index = 1; index < 64; index++)
            {
                double arc = length * index / 64;
                AssertGeometryLanes(geometry, Vector256.Create(arc, arc + .01, arc + .02, arc + .03),
                    "adjacent payload samples");

                // Adaptive cubic tables split the radius interval in halves.
                // Probe dyadic radii and their adjacent representable arc values
                // without relying on the private table representation.
                double radius = InnerRadiusMm + (OuterRadiusMm - InnerRadiusMm) * index / 64;
                double boundaryArc = geometry.ArcLengthAtRadius(radius);
                AssertGeometryLanes(geometry, Vector256.Create(Math.BitDecrement(boundaryArc), boundaryArc,
                    Math.BitIncrement(boundaryArc), boundaryArc + .01), "cubic boundary neighbours");
            }
        }
    }

    private static void TestConstantPitchAgainstLegacyFormula()
    {
        // Keep the pre-optimization solver and Newton expression independent of
        // production geometry. Scalar/SIMD parity alone would miss a shared change.
        foreach (var profile in new[]
        {
            (Inner: 24d, Outer: 58d, Length: 2_295_104d * 38_688 * 133.3e-6),
            (Inner: InnerRadiusMm, Outer: OuterRadiusMm, Length: TotalSectors * 38_688d * 133.3e-6),
            (Inner: 24d, Outer: 38d, Length: 714_544d * 38_688 * 133.3e-6),
            (Inner: 1.25, Outer: 9.75, Length: 1024.125),
            (Inner: .125, Outer: .25, Length: 100d),
            (Inner: 120d, Outer: 120.25, Length: 50_000d),
            // Both short spirals require the original upper-bound doubling loop.
            (Inner: 24d, Outer: 58d, Length: 34.1),
            (Inner: 1d, Outer: 2d, Length: 1.001),
        })
        {
            double b = LegacyConstantGrowth(profile.Inner, profile.Outer, profile.Length);
            DvdTrackGeometry geometry = DvdTrackGeometry.Create(profile.Inner, profile.Outer, profile.Length);
            string profileLabel = $"r={profile.Inner:R}..{profile.Outer:R}, length={profile.Length:R}";
            AssertSameBits(b, geometry.RadialGrowthPerRadianMm, "legacy constant growth: " + profileLabel);
            AssertSameBits((profile.Outer - profile.Inner) / b, geometry.TotalAngleRadians,
                "legacy constant total angle: " + profileLabel);

            List<double> arcs =
            [
                0d, -0d, Math.BitIncrement(0d), Math.BitDecrement(0d),
                Math.BitDecrement(profile.Length), profile.Length, Math.BitIncrement(profile.Length),
                -1d, profile.Length + 1d, -profile.Length, profile.Length * 2,
                // The fast API historically clamps some out-of-range inputs and
                // propagates NaN for others; preserve it rather than substituting
                // the checked RadiusAtArcLength API's validation contract.
                -profile.Inner * profile.Inner / (4 * b),
                -profile.Inner * profile.Inner / b,
                double.MinValue, double.MaxValue,
                double.NegativeInfinity, double.PositiveInfinity, double.NaN,
            ];
            for (int index = 1; index < 64; index++)
                arcs.Add(profile.Length * index / 64);

            foreach (double arc in arcs)
            {
                double expectedRadius = LegacyConstantFastRadius(profile.Inner, profile.Outer, b, arc);
                double expectedAngle = (expectedRadius - profile.Inner) / b;
                string label = $"{profileLabel}, arc={arc:R}";
                AssertSameBits(expectedRadius, geometry.RadiusAtArcLengthFast(arc), "legacy fast radius: " + label);
                var actual = geometry.AtArcLengthFast(arc);
                AssertSameBits(expectedRadius, actual.Radius, "legacy scalar tuple radius: " + label);
                AssertSameBits(expectedAngle, actual.Angle, "legacy scalar tuple angle: " + label);
            }

            for (int first = 0; first < arcs.Count; first += 4)
            {
                Vector256<double> input = Vector256.Create(arcs[first], arcs[Math.Min(first + 1, arcs.Count - 1)],
                    arcs[Math.Min(first + 2, arcs.Count - 1)], arcs[Math.Min(first + 3, arcs.Count - 1)]);
                var actual = geometry.AtArcLengthFast(input);
                for (int lane = 0; lane < Vector256<double>.Count; lane++)
                {
                    double expectedRadius = LegacyConstantFastRadius(profile.Inner, profile.Outer, b, input.GetElement(lane));
                    AssertSameBits(expectedRadius, actual.Radius.GetElement(lane),
                        $"legacy vector/fallback radius, lane {lane}: {profileLabel}");
                    AssertSameBits((expectedRadius - profile.Inner) / b, actual.Angle.GetElement(lane),
                        $"legacy vector/fallback angle, lane {lane}: {profileLabel}");
                }
            }
        }
    }

    private static double LegacyConstantGrowth(double inner, double outer, double length)
    {
        double low = 1e-15;
        double high = Math.Max(outer, 1.0);
        while (LegacyConstantArc(inner, outer, high) > length) high *= 2;
        for (int iteration = 0; iteration < 120; iteration++)
        {
            double middle = (low + high) / 2;
            if (LegacyConstantArc(inner, outer, middle) > length) low = middle;
            else high = middle;
        }
        return (low + high) / 2;
    }

    private static double LegacyConstantFastRadius(double inner, double outer, double b, double arc)
    {
        double radius = Math.Clamp(Math.Sqrt(inner * inner + 2 * b * arc), inner, outer);
        double error = LegacyConstantArc(inner, radius, b) - arc;
        double derivative = Math.Sqrt(radius * radius + b * b) / b;
        return Math.Clamp(radius - error / derivative, inner, outer);
    }

    private static double LegacyConstantArc(double first, double second, double b)
        => (LegacyPrimitive(second, b) - LegacyPrimitive(first, b)) / b;

    private static double LegacyPrimitive(double radius, double b)
        => .5 * (radius * Math.Sqrt(radius * radius + b * b) + b * b * Math.Asinh(radius / b));

    private static void AssertSameBits(double expected, double actual, string label)
        => Require(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{label}: expected {expected:R} (0x{BitConverter.DoubleToInt64Bits(expected):X16}), "
            + $"actual {actual:R} (0x{BitConverter.DoubleToInt64Bits(actual):X16})");

    private static void AssertGeometryLanes(DvdTrackGeometry geometry, Vector256<double> arcs, string label)
    {
        var actual = geometry.AtArcLengthFast(arcs);
        for (int lane = 0; lane < Vector256<double>.Count; lane++)
        {
            var expected = geometry.AtArcLengthFast(arcs.GetElement(lane));
            Require(actual.Radius.GetElement(lane) == expected.Radius,
                $"vector geometry radius equals scalar lane {lane}: {label}");
            Require(actual.Angle.GetElement(lane) == expected.Angle,
                $"vector geometry angle equals scalar lane {lane}: {label}");
        }
    }

    private static MultiBlockSolveOptions Options(uint startLba, bool measured)
        => new(StartLba: startLba, FillSectors: 16, PsnOffset: 0x30000,
            IterationsPerBlock: 1, MutationBytes: 4, RandomSeed: 842,
            InitialTemperature: 64, FinalTemperature: .25, ZeroInitialPayload: false,
            ImageMapping: Mapping(measured), Algorithm: SolverAlgorithm.Dispersion, FastParallelism: 1);

    private static ImageMappingOptions Mapping(bool measured)
        => new(TotalSectors, InnerRadiusMm, OuterRadiusMm, ChannelBitLengthNm: 133.3,
            StartAngleDegrees: 0, Clockwise: true, LuminanceThreshold: 128, AlphaThreshold: 1,
            PitchLinear: measured ? MeasuredLinear : 0,
            PitchQuadratic: measured ? MeasuredQuadratic : 0,
            PitchCubic: measured ? MeasuredCubic : 0);

    private static void WriteImage(string path, int width, int height,
        Func<int, int, (byte Blue, byte Green, byte Red, byte Alpha)> sample)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var pixel = sample(x, y);
            int offset = (y * width + x) * 4;
            pixels[offset] = pixel.Blue;
            pixels[offset + 1] = pixel.Green;
            pixels[offset + 2] = pixel.Red;
            pixels[offset + 3] = pixel.Alpha;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, width * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }
}
