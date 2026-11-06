using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;

internal static class CdCubicPitchTests
{
    internal static void Run()
    {
        TestZeroCompatibility();
        TestGeometryAndCopies();
        TestAnalyticParity();
        TestMetadataAndRaster();
        TestReferenceArtworkRouting();
        Console.WriteLine("cd-cubic-pitch: passed (legacy byte parity, independent pitch/length checks, scalar/SIMD coordinates, metadata, raster/data generation, reference artwork routing and preview)");
    }

    private static byte Pattern(double x, double y)
        => Math.Sin(x * .7) + Math.Cos(y * .9) > .15 ? (byte)0 : (byte)255;

    private static void TestZeroCompatibility()
    {
        CdDiscParameters parameters = new(24.5, 57.5, 24, StartAngleRadians: -.3);
        double rangeSquared = parameters.OuterRadiusMm * parameters.OuterRadiusMm
            - parameters.InnerRadiusMm * parameters.InnerRadiusMm;
        using MemoryStream output = new();
        CdTrackGenerator.GeneratePatternToStream(Pattern, output, parameters, false);
        byte[] bytes = output.ToArray();
        for (int i = 0; i < bytes.Length; i++)
        {
            // The pre-cubic CD formula, independent of the parameter helpers.
            double radius = Math.Sqrt(parameters.InnerRadiusMm * parameters.InnerRadiusMm
                + ((double)i / parameters.TotalBytes) * rangeSquared);
            double angle = parameters.StartAngleRadians
                + (2.0 * parameters.LinearVelocityMmPerSecond * (parameters.Sectors / 75.0) / rangeSquared)
                * (radius - parameters.InnerRadiusMm);
            var actual = parameters.PolarFromByte(i);
            Require(Bits(actual.RadiusMm) == Bits(radius) && Bits(actual.ThetaRadians) == Bits(angle),
                "zero coefficients retain each original floating-point operation");
            byte expected = Pattern(radius * Math.Cos(angle), radius * Math.Sin(angle)) < 128
                ? (byte)0x0D : (byte)0xAA;
            Require(bytes[i] == expected, "zero coefficients retain original audio bytes");
        }
    }

    private static void TestGeometryAndCopies()
    {
        CdDiscParameters parameters = new(24.5, 57.5, 350_000, StartAngleRadians: .2,
            PitchLinear: .3, PitchQuadratic: -.25, PitchCubic: .12);
        parameters.Validate();
        double length = parameters.Sectors / 75.0 * parameters.LinearVelocityMmPerSecond;
        DvdTrackGeometry reference = DvdTrackGeometry.Create(parameters.InnerRadiusMm,
            parameters.OuterRadiusMm, length, parameters.PitchLinear, parameters.PitchQuadratic, parameters.PitchCubic);
        double lastRadius = 0, lastAngle = double.NegativeInfinity;
        for (int sample = 0; sample <= 128; sample++)
        {
            long position = parameters.TotalBytes * sample / 128;
            var actual = parameters.PolarFromByte(position);
            var expected = reference.AtArcLengthFast(Math.Min(length,
                position * parameters.LinearVelocityMmPerSecond / (75.0 * CdDiscParameters.BytesPerSector)));
            Require(Math.Abs(actual.RadiusMm - expected.Radius) < 1e-12
                && Math.Abs(actual.ThetaRadians - parameters.StartAngleRadians - expected.Angle) < 1e-9,
                "CD byte clock maps to the full physical track length");
            Require(actual.RadiusMm > lastRadius && actual.ThetaRadians > lastAngle,
                "cubic geometry stays monotone");
            lastRadius = actual.RadiusMm; lastAngle = actual.ThetaRadians;
        }
        Require(parameters.RadiusFromByte(-1) == parameters.InnerRadiusMm
            && parameters.RadiusFromByte(parameters.TotalBytes) == parameters.OuterRadiusMm
            && parameters.RadiusFromByte(parameters.TotalBytes + 1) == parameters.OuterRadiusMm,
            "CIRC positions outside the track retain endpoint clamping");
        // Independent local differential: dr/dtheta must vary as the requested polynomial.
        double Growth(double x)
        {
            double r = parameters.InnerRadiusMm + x * (parameters.OuterRadiusMm - parameters.InnerRadiusMm);
            const double h = .0001;
            return 2 * h / (parameters.ThetaFromRadius(r + h) - parameters.ThetaFromRadius(r - h));
        }
        double Shape(double x) => 1 + parameters.PitchLinear * x
            + parameters.PitchQuadratic * x * x + parameters.PitchCubic * x * x * x;
        Require(Math.Abs(Growth(.8) / Growth(.2) - Shape(.8) / Shape(.2)) < 1e-7,
            "local radial pitch follows the cubic shape");
        CdDiscParameters copy = parameters with { OuterRadiusMm = 58, PitchCubic = -.05 };
        CdDiscParameters fresh = new(copy.InnerRadiusMm, copy.OuterRadiusMm, copy.Sectors,
            copy.LinearVelocityMmPerSecond, copy.StartAngleRadians, copy.ImageOuterRadiusMm,
            copy.PitchLinear, copy.PitchQuadratic, copy.PitchCubic);
        Require(copy.PolarFromByte(100_000) == fresh.PolarFromByte(100_000)
            && copy == fresh && parameters != copy, "with copies do not retain stale cached geometry or change equality");
        CalibrationTarget target = CalibrationTarget.Create(new(CalibrationDiscKind.Cd,
            24.5, 57.5, 350_000, 1200, .3, -.25, .12));
        CdDiscParameters calibrated = CdDiscParameters.FromCalibration(target);
        Require(calibrated.PitchLinear == .3 && calibrated.PitchQuadratic == -.25 && calibrated.PitchCubic == .12,
            "calibration coefficients reach the encoder");
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, -2d })
        {
            bool rejected = false;
            try { (parameters with { PitchLinear = value, PitchQuadratic = 0, PitchCubic = 0 }).Validate(); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "invalid or nonpositive pitch is rejected");
        }
    }

    private static void TestAnalyticParity()
    {
        foreach (double velocity in new[] { 1200d, 6_000_000d })
        foreach (bool interleave in new[] { false, true })
        foreach (CdAudioByteOrder order in Enum.GetValues<CdAudioByteOrder>())
        {
            CdDiscParameters parameters = new(24.5, 57.5, 16, velocity, -.7,
                PitchLinear: -.4, PitchQuadratic: .25, PitchCubic: .08);
            using MemoryStream scalar = new(), vector = new();
            List<(long X, long Y)> positions = [];
            CdTrackGenerator.GeneratePatternToStream((x, y) =>
            {
                positions.Add((Bits(x), Bits(y)));
                return Pattern(x, y);
            }, scalar, parameters, interleave, audioByteOrder: order, useSimdSampling: false);
            int index = 0;
            CdTrackGenerator.GeneratePatternToStream((x, y) =>
            {
                Require(positions[index++] == (Bits(x), Bits(y)), "SIMD keeps exact analytic coordinates and callback order");
                return Pattern(x, y);
            }, vector, parameters, interleave, audioByteOrder: order, useSimdSampling: true);
            Require(index == parameters.TotalBytes && scalar.ToArray().AsSpan().SequenceEqual(vector.ToArray()),
                "cubic scalar/SIMD streams match with both interleave and byte orders");
        }
    }

    private static void TestMetadataAndRaster()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-cd-cubic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const int size = 127;
            byte[] pixels = new byte[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) pixels[y * size + x] = Pattern(x * .4, y * .4);
            string image = Path.Combine(directory, "source.png");
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96,
                PixelFormats.Gray8, null, pixels, size)));
            using (FileStream file = File.Create(image)) encoder.Save(file);
            CdDiscParameters parameters = new(24.5, 57.5, 96, PitchLinear: .3, PitchQuadratic: -.25, PitchCubic: .12);
            using MemoryStream scalar = new(), vector = new();
            CdTrackGenerator.GenerateToStream(image, scalar, parameters, true, useSimdSampling: false);
            CdTrackGenerator.GenerateToStream(image, vector, parameters, true, useSimdSampling: true);
            Require(scalar.ToArray().AsSpan().SequenceEqual(vector.ToArray()), "cubic raster SIMD output matches scalar");
            string track = Path.Combine(directory, "track.raw");
            CdTrackGenerator.Generate(image, track, parameters, false);
            CdTrackMetadata metadata = CdTrackMetadata.TryLoad(track)!;
            Require(metadata is not null && metadata.CreateParameters() == parameters,
                "audio metadata round-trips all pitch coefficients");
            CdTrackGenerator.PreviewWarp(image, Path.Combine(directory, "warp.png"), parameters, parameters, 128, 32);
            CdTrackGenerator.PreviewTrack(track, Path.Combine(directory, "track.png"), parameters, 128, 48, false);
            string data = Path.Combine(directory, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "readme.txt"), "CD cubic fixture");
            CdDataLayoutPlan layout = CdDataImageGenerator.PrepareLayout(data, parameters);
            using MemoryStream dataScalar = new(), dataVector = new();
            CdDataImageGenerator.GenerateToStream(image, dataScalar, parameters, layout, useSimdSampling: false);
            CdDataImageGenerator.GenerateToStream(image, dataVector, parameters, layout, useSimdSampling: true);
            Require(dataScalar.ToArray().AsSpan().SequenceEqual(dataVector.ToArray()),
                "cubic Mode 1 payload SIMD matches scalar across nonconsecutive CIRC positions");
            CdTrackMetadata old = JsonSerializer.Deserialize<CdTrackMetadata>(
                "{\"SourceImage\":\"old.png\",\"OutputTrack\":\"old.raw\",\"InnerRadiusMm\":24.5,\"OuterRadiusMm\":57.5,\"Sectors\":96,\"LinearVelocityMmPerSecond\":1200,\"ImageOuterRadiusMm\":57.5}")!;
            Require(old.CreateParameters() == new CdDiscParameters(24.5, 57.5, 96),
                "old metadata defaults to the unchanged constant-pitch model");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestReferenceArtworkRouting()
    {
        CalibrationParameters[] geometries =
        [
            new(CalibrationDiscKind.Cd, 24.5, 57.5, 16, 1200),
            new(CalibrationDiscKind.Cd, 24.5, 57.5, 16, 1200, .3, -.25, .12),
            new(CalibrationDiscKind.Cd, 24.5, 57.5, 16, 6_000_000),
            new(CalibrationDiscKind.Cd, 24.5, 57.5, 16, 6_000_000, -.4, .25, .08),
            new(CalibrationDiscKind.Cd, 24.5, 57.5, 4, 6e10),
            // The first pixel lies at x = 1690.5, on and around a rounding tie.
            new(CalibrationDiscKind.Cd, 57.5 * 490.5 / 1200 - 1e-12, 57.5, 4, 1200),
            new(CalibrationDiscKind.Cd, 57.5 * 490.5 / 1200, 57.5, 4, 1200),
            new(CalibrationDiscKind.Cd, 57.5 * 490.5 / 1200 + 1e-12, 57.5, 4, 1200),
        ];
        foreach (CalibrationParameters geometry in geometries)
        {
            CalibrationTarget current = CalibrationTarget.Create(geometry, artwork: CalibrationArtwork.ReferenceCross);
            CalibrationTarget legacy = new(6, Guid.NewGuid().ToString("N"), geometry, current.Options, current.Cells);
            foreach (CalibrationTarget target in new[] { current, legacy })
            foreach (bool interleave in new[] { false, true })
            foreach (CdAudioByteOrder order in Enum.GetValues<CdAudioByteOrder>())
            {
                CdDiscParameters parameters = CdDiscParameters.FromCalibration(target);
                using MemoryStream expected = new(), actual = new();
                CdTrackGenerator.GeneratePatternToStream(target.Sample, expected, parameters, interleave,
                    audioByteOrder: order, useSimdSampling: false);
                long completed = 0;
                CdTrackGenerator.GeneratePatternToStream(target, actual, interleave,
                    p => completed = p.CompletedBytes, audioByteOrder: order);
                Require(completed == parameters.TotalBytes && actual.ToArray().AsSpan().SequenceEqual(expected.ToArray()),
                    "reference raster route preserves frozen PNG bytes, annulus, orientation, interleave and byte order");
            }
        }

        CalibrationTarget fileTarget = CalibrationTarget.Create(geometries[1], artwork: CalibrationArtwork.ReferenceCross);
        string directory = Path.Combine(Path.GetTempPath(), "disc-cd-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (bool wave in new[] { false, true })
            {
                string path = Path.Combine(directory, wave ? "reference.wav" : "reference.raw");
                using MemoryStream expected = new();
                CdTrackGenerator.GeneratePatternToStream(fileTarget.Sample, expected,
                    CdDiscParameters.FromCalibration(fileTarget), true,
                    audioByteOrder: wave ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian,
                    useSimdSampling: false);
                CdTrackGenerator.GeneratePattern(fileTarget, path, true);
                byte[] actual = File.ReadAllBytes(path);
                Require(actual.AsSpan(wave ? 44 : 0).SequenceEqual(expected.ToArray()),
                    "WAV/RAW target entry points use the same reference raster route");
                if (wave) Require(File.Exists(Path.ChangeExtension(path, ".cue")), "reference WAV keeps its CUE");
            }
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            using MemoryStream output = new();
            bool rejected = false;
            try { CdTrackGenerator.GeneratePatternToStream(fileTarget, output, false, cancellationToken: cancelled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Require(rejected && output.Length == 0, "reference raster route honours pre-cancellation");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static void Benchmark()
    {
        const int size = 1024, sectors = 2048, rounds = 5;
        string directory = Path.Combine(Path.GetTempPath(), "disc-cd-pitch-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] pixels = new byte[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) pixels[y * size + x] = Pattern(x * .1, y * .1);
            string image = Path.Combine(directory, "source.png");
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96,
                PixelFormats.Gray8, null, pixels, size)));
            using (FileStream file = File.Create(image)) encoder.Save(file);
            Console.WriteLine($"cd-pitch-benchmark environment: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; logical CPUs={Environment.ProcessorCount}; Vector256={Vector256.IsHardwareAccelerated}");
            Console.WriteLine($"cd-pitch-benchmark fixture: {sectors} sectors, {sectors * CdDiscParameters.BytesPerSector} bytes, radii=24.5/57.5mm, velocity=1200mm/s, angle=0, cubic=0.3/-0.25/0.12, no interleave, big-endian, Stream.Null; raster={size}x{size} Gray8; one full warmup, median of {rounds}");
            foreach (bool raster in new[] { false, true })
            foreach (bool cubic in new[] { false, true })
            foreach (bool simd in new[] { false, true })
            {
                CdDiscParameters parameters = new(24.5, 57.5, sectors,
                    PitchLinear: cubic ? .3 : 0, PitchQuadratic: cubic ? -.25 : 0, PitchCubic: cubic ? .12 : 0);
                void Generate()
                {
                    if (raster)
                        CdTrackGenerator.GenerateToStream(image, Stream.Null, parameters, false, useSimdSampling: simd);
                    else
                        CdTrackGenerator.GeneratePatternToStream(Pattern, Stream.Null, parameters, false, useSimdSampling: simd);
                }
                Generate();
                double[] seconds = new double[rounds];
                for (int round = 0; round < rounds; round++)
                {
                    Stopwatch timer = Stopwatch.StartNew();
                    Generate();
                    seconds[round] = timer.Elapsed.TotalSeconds;
                }
                Array.Sort(seconds);
                double median = seconds[rounds / 2];
                Console.WriteLine($"cd-pitch-benchmark raster={raster} cubic={cubic} simd={simd}: {parameters.TotalBytes / 1048576d / median:0.00} MiB/s (median {median:0.000}s)");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD cubic pitch: " + message);
    }
}
