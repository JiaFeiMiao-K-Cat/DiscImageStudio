using System.IO;
using System.Text.Json;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.ImageIO;
using DvdImageSolver.Solver;

internal static class DvdReadCompatibilityIntegrationTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-read-compat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.png");
            byte[] pixels = new byte[64 * 64 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte gray = (byte)((i / 4 % 64) / 8 % 2 == 0 ? 255 : 0);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
                pixels[i + 3] = 255;
            }
            RasterCodec.SavePng(source, 64, 64, pixels, grayscale: false);
            var options = new DvdStreamingOptions(256, 24, 58, FastParallelism: 2, ReadCompatibility: true);
            using MemoryStream stream = new();
            var summary = DvdStreamingGenerator.Generate(source, stream, options);
            Require(summary.CompatibilityChanges > 0, "fixture exercises bridges");
            string iso = Path.Combine(directory, "typed.iso");
            DvdImageGenerator.Generate(new(source, iso, options));
            Require(File.ReadAllBytes(iso).SequenceEqual(stream.ToArray()), "typed ISO and streaming bytes match");
            using (JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(iso + ".json")))
            {
                Require(metadata.RootElement.GetProperty("readCompatibility").GetString() == "bridge-9b-v1"
                    && metadata.RootElement.GetProperty("compatibilityChanges").GetInt64() == summary.CompatibilityChanges,
                    "sidecar records version and exact change count");
                Require(metadata.RootElement.GetProperty("totalScore").ValueKind == JsonValueKind.Null,
                    "guarded texture is not claimed to have zero transition error");
            }
            Require(DvdImageMetadata.TryLoad(iso) is { ReadCompatibility: true }, "typed metadata recognizes bridge");
            string cli = Path.Combine(directory, "cli.iso");
            var result = new DvdDiscModule().ExecuteAsync(new DiscJobRequest("solve",
                ["--image", source, "--iso-output", cli, "--total-sectors", "256", "--fill-sectors", "256",
                 "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--read-compatibility", "true",
                 "--fast-parallelism", "2"])).GetAwaiter().GetResult();
            Require(result.Succeeded && File.ReadAllBytes(cli).SequenceEqual(stream.ToArray()), "product CLI parity");
            VerifyPreview(directory, iso);
            VerifyHybrid(directory, source, options);
            VerifyCalibration(directory, options);
            using var canceled = new MemoryStream();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                DvdStreamingGenerator.Generate(source, canceled, options, cancellationToken: cancellation.Token);
                throw new InvalidOperationException("Expected cancellation");
            }
            catch (OperationCanceledException) { Require(canceled.Length == 0, "canceled guarded stream writes nothing"); }
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("dvd-read-compatibility-integration: passed (file/CLI/stream, sidecar, preview, protected files, frozen calibration)");
    }

    private static void VerifyPreview(string directory, string iso)
    {
        var options = new IsoReadbackOptions(256, 0, 256, 0x30000, 24, 58, 133.3, true, 64, 2048);
        string metadata = File.ReadAllText(iso + ".json");
        var guarded = IsoReadbackRenderer.Render(iso, Path.Combine(directory, "guarded.png"), options);
        long? unguardedCount = null;
        foreach (string sidecar in new[] { "{}", "{\"readCompatibility\":\"unknown\"}", "{\"readCompatibility\":true}", "{bad" })
        {
            File.WriteAllText(iso + ".json", sidecar);
            Require(!IsoReadbackRenderer.ReadCompatibilityEnabled(iso), "no guessing bridge mode for old/unknown metadata");
            var legacy = IsoReadbackRenderer.Render(iso, Path.Combine(directory, "legacy.png"), options);
            Require(legacy.ClassifiedSamples < guarded.ClassifiedSamples, "only explicit bridge metadata classifies 9B");
            unguardedCount ??= legacy.ClassifiedSamples;
            Require(unguardedCount == legacy.ClassifiedSamples, "legacy preview classification preserved");
        }
        File.WriteAllText(iso + ".json", metadata);
    }

    private static void VerifyHybrid(string directory, string source, DvdStreamingOptions options)
    {
        string data = Path.Combine(directory, "data");
        Directory.CreateDirectory(data);
        // A deliberately texture-like file must never be treated as artwork.
        File.WriteAllBytes(Path.Combine(data, "texture.bin"), Enumerable.Range(0, 8192)
            .Select(i => (byte)(i % 64 < 32 ? 0x92 : 0xA5)).ToArray());
        foreach (bool outer in new[] { false, true })
        {
            var plan = DvdStreamingGenerator.PrepareHybrid(data, options, "GUARD", outer);
            using MemoryStream before = new(), after = new();
            DvdStreamingGenerator.GenerateHybrid(source, before, options with { ReadCompatibility = false }, plan);
            var result = DvdStreamingGenerator.GenerateHybrid(source, after, options, plan);
            Require(result.CompatibilityChanges > 0, "hybrid drawing is guarded");
            byte[] first = before.ToArray(), second = after.ToArray();
            int start = checked((int)plan.DrawingStartLba * 2048), end = checked((int)plan.DrawingEndLbaExclusive * 2048);
            Require(first.AsSpan(0, start).SequenceEqual(second.AsSpan(0, start))
                && first.AsSpan(end).SequenceEqual(second.AsSpan(end)), "filesystem, file extents and padding remain byte-identical");
        }
    }

    private static void VerifyCalibration(string directory, DvdStreamingOptions options)
    {
        var parameters = new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 58, 256, 133.3);
        foreach (var artwork in new[] { CalibrationArtwork.WideAddressBands, CalibrationArtwork.ReferenceCross })
        {
            // The target's frozen generation parameters win over the current project setting.
            var target = CalibrationTarget.Create(parameters, artwork: artwork);
            using MemoryStream legacy = new(), guarded = new(), replay = new();
            var off = DvdStreamingGenerator.GeneratePattern(target, legacy, options);
            var enabledTarget = CalibrationTarget.Create(parameters with { DvdReadCompatibility = true }, artwork: artwork);
            var on = DvdStreamingGenerator.GeneratePattern(enabledTarget, guarded, options with { ReadCompatibility = false });
            DvdStreamingGenerator.GeneratePattern(enabledTarget, replay, options);
            Require(off.CompatibilityChanges == 0 && on.CompatibilityChanges > 0, "frozen calibration opt-in used");
            Require(guarded.ToArray().SequenceEqual(replay.ToArray()), "calibration bytes independent of current project toggle");
            string path = Path.Combine(directory, artwork + ".iso");
            DvdImageGenerator.GenerateCalibration(enabledTarget, path, options with { ReadCompatibility = false });
            Require(File.ReadAllBytes(path).SequenceEqual(guarded.ToArray())
                && DvdImageMetadata.TryLoad(path) is { ReadCompatibility: true },
                "calibration file bytes match streaming and carry preview metadata");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("DVD read compatibility: " + message);
    }
}
