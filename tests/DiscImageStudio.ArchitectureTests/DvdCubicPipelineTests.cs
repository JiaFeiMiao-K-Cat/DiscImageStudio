using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DvdImageSolver;

internal static class DvdCubicPipelineTests
{
    internal static void Run()
    {
        TestGeometryAgainstIndependentIntegration();
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-cubic-pipeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.png");
            WritePattern(source);
            string cubic = Path.Combine(directory, "cubic.iso");
            string constant = Path.Combine(directory, "constant.iso");
            string explicitZero = Path.Combine(directory, "zero.iso");
            string[] generate = ["solve", "--image", source, "--total-sectors", "160", "--fill-sectors", "160",
                "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--algorithm", "dispersion",
                "--fast-output", "true", "--fast-parallelism", "1"];
            Require(DvdCommandRunner.Run([.. generate, "--iso-output", cubic, "--pitch-linear", "0.18",
                "--pitch-quadratic", "-0.12", "--pitch-cubic", "0.06"]) == 0, "cubic CLI generation");
            Require(DvdCommandRunner.Run([.. generate, "--iso-output", constant]) == 0, "legacy CLI generation");
            Require(DvdCommandRunner.Run([.. generate, "--iso-output", explicitZero,
                "--pitch-linear", "0", "--pitch-quadratic", "0", "--pitch-cubic", "0"]) == 0, "zero CLI generation");
            Require(File.ReadAllBytes(constant).SequenceEqual(File.ReadAllBytes(explicitZero)), "zero coefficients preserve ISO bytes");
            Require(!File.ReadAllBytes(cubic).SequenceEqual(File.ReadAllBytes(constant)), "cubic pitch changes the encoded sampling positions");

            using MemoryStream stream = new();
            DvdStreamingGenerator.Generate(source, stream, new(160, 24, 58, FastParallelism: 1,
                PitchLinear: 0.18, PitchQuadratic: -0.12, PitchCubic: 0.06));
            Require(stream.ToArray().SequenceEqual(File.ReadAllBytes(cubic)), "file and stream use identical cubic geometry");
            string moduleIso = Path.Combine(directory, "module.iso");
            DiscJobResult moduleResult = new DvdDiscModule().ExecuteAsync(new DiscJobRequest("solve",
                ["--image", source, "--iso-output", moduleIso, "--total-sectors", "160", "--fill-sectors", "160",
                 "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--pitch-linear", "0.18",
                 "--pitch-quadratic", "-0.12", "--pitch-cubic", "0.06", "--fast-parallelism", "2"]))
                .GetAwaiter().GetResult();
            Require(moduleResult.Succeeded && File.ReadAllBytes(moduleIso).AsSpan().SequenceEqual(stream.ToArray()),
                "product module defaults use the same fast payload generation as direct streaming");
            using (JsonDocument moduleSummary = JsonDocument.Parse(File.ReadAllText(moduleIso + ".json")))
            {
                Require(moduleSummary.RootElement.GetProperty("fastOutput").GetBoolean()
                    && !moduleSummary.RootElement.GetProperty("exactEccEfmReplay").GetBoolean()
                    && moduleSummary.RootElement.GetProperty("fastParallelism").GetInt32() == 2,
                    "product module uses fast output and preserves requested parallelism without caller fast flags");
            }
            DvdImageMetadata metadata = DvdImageMetadata.TryLoad(cubic) ?? throw new InvalidOperationException("missing cubic metadata");
            Require(metadata.PitchLinear == 0.18 && metadata.PitchQuadratic == -0.12 && metadata.PitchCubic == 0.06,
                "ISO metadata retains the complete pitch shape");
            DvdImageMetadata zero = DvdImageMetadata.TryLoad(constant) ?? throw new InvalidOperationException("missing zero metadata");
            Require(zero.PitchLinear == 0 && zero.PitchQuadratic == 0 && zero.PitchCubic == 0, "constant metadata retains zero shape");

            string matching = Path.Combine(directory, "matching.png");
            string mismatching = Path.Combine(directory, "mismatching.png");
            string[] preview = ["calibrate", "--image", source, "--total-sectors", "160",
                "--generated-inner-radius-mm", "24", "--generated-outer-radius-mm", "58",
                "--actual-inner-radius-mm", "24", "--actual-outer-radius-mm", "58",
                "--pitch-linear", "0.18", "--pitch-quadratic", "-0.12", "--pitch-cubic", "0.06",
                "--preview-size", "64", "--samples-per-sector", "64"];
            Require(DvdCommandRunner.Run([.. preview, "--output", matching,
                "--actual-pitch-linear", "0.18", "--actual-pitch-quadratic", "-0.12", "--actual-pitch-cubic", "0.06"]) == 0,
                "cubic forward preview");
            Require(DvdCommandRunner.Run([.. preview, "--output", mismatching]) == 0, "constant actual forward preview");
            Require(!File.ReadAllBytes(matching).SequenceEqual(File.ReadAllBytes(mismatching)), "preview responds to independent actual pitch");
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(matching + ".json")))
            {
                Require(summary.RootElement.GetProperty("generatedPitchCubic").GetDouble() == 0.06
                    && summary.RootElement.GetProperty("actualPitchCubic").GetDouble() == 0.06, "preview records both models");
            }
            string readback = Path.Combine(directory, "readback.png");
            Require(DvdCommandRunner.Run(["simulate", "--iso", cubic, "--output", readback,
                "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--pitch-linear", "0.18",
                "--pitch-quadratic", "-0.12", "--pitch-cubic", "0.06", "--preview-size", "64"]) == 0,
                "cubic ISO readback");
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(readback + ".json")))
                Require(summary.RootElement.GetProperty("pitchCubic").GetDouble() == 0.06, "readback records actual model");

            using MemoryStream invalid = new();
            Throws(() => DvdStreamingGenerator.Generate(source, invalid, new(160, 24, 58, PitchLinear: -2)),
                "invalid pitch rejected by stream");
            Require(invalid.Length == 0, "invalid geometry rejected before output writes");
            // A strictly positive polynomial can still be too close to zero to resolve.
            // Validate before any producer (including a hybrid prefix) starts writing.
            Throws(() => new DvdStreamingOptions(2_295_104, 24, 58,
                PitchLinear: -3.999999999996, PitchQuadratic: 3.999999999996).Validate(),
                "numerically unresolved positive pitch rejected by options validation");
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        Console.WriteLine("dvd-cubic-pipeline: passed (independent integration, file/stream parity, metadata, preview and readback)");
    }

    private static void TestGeometryAgainstIndependentIntegration()
    {
        const double inner = 24, outer = 58, length = 2_295_104d * 38_688 * 133.3e-6;
        foreach (var coefficients in new[] { (0.0, 0.0, 0.0), (0.18, -0.12, 0.06), (-0.4, 0.3, 0.2) })
        {
            var geometry = DvdTrackGeometry.Create(inner, outer, length, coefficients.Item1, coefficients.Item2, coefficients.Item3);
            double Growth(double radius)
            {
                double x = (radius - inner) / (outer - inner);
                return geometry.RadialGrowthPerRadianMm * (1 + x * (coefficients.Item1 + x * (coefficients.Item2 + x * coefficients.Item3)));
            }
            double arc = Simpson(r => Math.Sqrt(1 + Math.Pow(r / Growth(r), 2)), inner, outer);
            Near(length, arc, length * 2e-12, "total arc length normalization");
            for (int index = 0; index <= 20; index++)
            {
                double radius = inner + (outer - inner) * index / 20;
                double independentArc = Simpson(r => Math.Sqrt(1 + Math.Pow(r / Growth(r), 2)), inner, radius);
                double independentAngle = Simpson(r => 1 / Growth(r), inner, radius);
                Near(radius, geometry.RadiusAtArcLength(Math.Clamp(independentArc, 0, length)), 2e-9, "independent radius inversion");
                Near(independentAngle, geometry.AngleAtRadius(radius), 2e-7, "independent phase integration");
                Near(independentArc, geometry.ArcLengthAtAngle(independentAngle), length * 2e-12, "angle to arc inversion");
            }
        }
        Throws(() => DvdTrackGeometry.Create(inner, outer, length, double.NaN), "nonfinite pitch");
        // Both ends are positive, but q(0.5) is negative: endpoint-only validation is insufficient.
        Throws(() => DvdTrackGeometry.Create(inner, outer, length, -5, 5), "negative interior pitch");
        Throws(() => DvdTrackGeometry.Create(inner, outer, length, -4, 4), "zero interior pitch");
    }

    private static double Simpson(Func<double, double> function, double start, double end)
    {
        const int intervals = 8192;
        double h = (end - start) / intervals;
        double sum = function(start) + function(end);
        for (int i = 1; i < intervals; i++) sum += (i % 2 == 0 ? 2 : 4) * function(start + i * h);
        return sum * h / 3;
    }

    private static void WritePattern(string path)
    {
        const int size = 65;
        byte[] pixels = new byte[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) pixels[y * size + x] = x > 32 && y < 44 ? (byte)0 : (byte)255;
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static void Throws(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(label + ": expected rejection");
    }

    private static void Near(double expected, double actual, double tolerance, string label)
        => Require(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"{label}: expected {expected:R}, actual {actual:R}, tolerance {tolerance:R}");

    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }
}
