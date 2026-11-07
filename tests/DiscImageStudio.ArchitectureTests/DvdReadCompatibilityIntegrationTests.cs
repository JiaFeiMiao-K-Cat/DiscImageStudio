using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            Require(summary.CompatibilityChanges > 0, "fixture exercises periodic protection");
            string iso = Path.Combine(directory, "typed.iso");
            DvdImageGenerator.Generate(new(source, iso, options));
            Require(File.ReadAllBytes(iso).SequenceEqual(stream.ToArray()), "typed ISO and streaming bytes match");
            using (JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(iso + ".json")))
            {
                Require(metadata.RootElement.GetProperty("readCompatibility").GetString() == DvdReadCompatibilityModes.Periodic9B16
                    && metadata.RootElement.GetProperty("compatibilityChanges").GetInt64() == summary.CompatibilityChanges,
                    "sidecar records version and exact change count");
                Require(metadata.RootElement.GetProperty("totalScore").ValueKind == JsonValueKind.Null,
                    "guarded texture is not claimed to have zero transition error");
            }
            Require(DvdImageMetadata.TryLoad(iso) is
                { ReadCompatibility: true, ReadCompatibilityMode: DvdReadCompatibilityModes.Periodic9B16 },
                "typed metadata recognizes the periodic mode");
            string cli = Path.Combine(directory, "cli.iso");
            var result = new DvdDiscModule().ExecuteAsync(new DiscJobRequest("solve",
                ["--image", source, "--iso-output", cli, "--total-sectors", "256", "--fill-sectors", "256",
                 "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--read-compatibility", "true",
                 "--fast-parallelism", "2"])).GetAwaiter().GetResult();
            Require(result.Succeeded && File.ReadAllBytes(cli).SequenceEqual(stream.ToArray()), "product CLI parity");
            Require(DvdImageMetadata.TryLoad(cli) is
                { ReadCompatibility: true, ReadCompatibilityMode: DvdReadCompatibilityModes.Periodic9B16 },
                "CLI sidecar records the periodic mode");
            VerifyPreview(directory, iso);
            VerifyHybrid(directory, source, options);
            VerifyCalibration(directory, options);
            VerifyModeValidation(directory, source, options);
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
        Console.WriteLine("dvd-read-compatibility-integration: passed (file/CLI/stream, versioned sidecar, preview, protected files, frozen calibration)");
    }

    private static void VerifyPreview(string directory, string iso)
    {
        var options = new IsoReadbackOptions(256, 0, 256, 0x30000, 24, 58, 133.3, true, 64, 2048);
        string metadata = File.ReadAllText(iso + ".json");
        var guarded = IsoReadbackRenderer.Render(iso, Path.Combine(directory, "guarded.png"), options);
        Require(guarded.MappingMode == "payload-scramble-classify-splat-" + DvdReadCompatibilityModes.Periodic9B16,
            "preview reports the actual periodic mode");
        JsonObject oldMetadata = JsonNode.Parse(metadata)!.AsObject();
        oldMetadata["readCompatibility"] = DvdReadCompatibilityModes.LegacyBridge;
        File.WriteAllText(iso + ".json", oldMetadata.ToJsonString());
        Require(IsoReadbackRenderer.ReadCompatibilityEnabled(iso)
            && DvdImageMetadata.TryLoad(iso) is
                { ReadCompatibility: true, ReadCompatibilityMode: DvdReadCompatibilityModes.LegacyBridge },
            "legacy bridge metadata remains recognized");
        var oldBridge = IsoReadbackRenderer.Render(iso, Path.Combine(directory, "old-bridge.png"), options);
        Require(oldBridge.ClassifiedSamples == guarded.ClassifiedSamples
            && oldBridge.MappingMode == "payload-scramble-classify-splat-" + DvdReadCompatibilityModes.LegacyBridge,
            "legacy white guards remain visible and report their original mode");
        long? unguardedCount = null;
        foreach (string modeJson in new[] { "\"off\"", "\"unknown\"", "true", "null" })
        {
            JsonObject unknownMetadata = JsonNode.Parse(metadata)!.AsObject();
            unknownMetadata["readCompatibility"] = JsonNode.Parse(modeJson);
            File.WriteAllText(iso + ".json", unknownMetadata.ToJsonString());
            Require(DvdImageMetadata.TryLoad(iso) is { ReadCompatibility: false, ReadCompatibilityMode: null },
                "typed metadata does not infer a protection version");
            VerifyUnclassifiedGuards();
        }
        foreach (string sidecar in new[] { "{}", "{bad", "[]" })
        {
            File.WriteAllText(iso + ".json", sidecar);
            VerifyUnclassifiedGuards();
        }
        File.Delete(iso + ".json");
        VerifyUnclassifiedGuards();
        File.WriteAllText(iso + ".json", metadata);

        void VerifyUnclassifiedGuards()
        {
            Require(!IsoReadbackRenderer.ReadCompatibilityEnabled(iso)
                && IsoReadbackRenderer.ReadCompatibilityMode(iso) is null,
                "no guessing a protection mode from missing or unknown metadata");
            var legacy = IsoReadbackRenderer.Render(iso, Path.Combine(directory, "legacy.png"), options);
            Require(legacy.ClassifiedSamples < guarded.ClassifiedSamples, "only explicit supported metadata classifies 9B");
            Require(legacy.MappingMode == "payload-scramble-classify-splat", "unknown mode is not reported as a known algorithm");
            unguardedCount ??= legacy.ClassifiedSamples;
            Require(unguardedCount == legacy.ClassifiedSamples, "legacy preview classification preserved");
        }
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
            string iso = Path.Combine(directory, outer ? "hybrid-outer.iso" : "hybrid-inner.iso");
            DvdImageGenerator.Generate(new(source, iso, options, data, "GUARD", outer));
            byte[] generated = File.ReadAllBytes(iso);
            // Separate plans record their own creation timestamps in the filesystem.
            Require(generated.Length == second.Length
                && generated.AsSpan(start, end - start).SequenceEqual(second.AsSpan(start, end - start)),
                "hybrid ISO and streaming drawing protection match");
            Require(DvdImageMetadata.TryLoad(iso) is
                { ReadCompatibility: true, ReadCompatibilityMode: DvdReadCompatibilityModes.Periodic9B16, HasFileSystem: true },
                "hybrid sidecar identifies the filesystem and periodic mode");
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
            Require(enabledTarget.DvdReadCompatibilityMode == DvdReadCompatibilityModes.Periodic9B16
                && enabledTarget.EffectiveDvdReadCompatibilityMode == DvdReadCompatibilityModes.Periodic9B16,
                "new calibration freezes the periodic mode");
            var on = DvdStreamingGenerator.GeneratePattern(enabledTarget, guarded, options with { ReadCompatibility = false });
            var restored = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(enabledTarget, []))).Target;
            Require(restored.DvdReadCompatibilityMode == DvdReadCompatibilityModes.Periodic9B16,
                "saved calibration preserves the protection version");
            DvdStreamingGenerator.GeneratePattern(restored, replay, options with
            {
                ReadCompatibilityMode = DvdReadCompatibilityModes.LegacyBridge,
            });
            Require(off.CompatibilityChanges == 0 && on.CompatibilityChanges > 0, "frozen calibration opt-in used");
            Require(guarded.ToArray().SequenceEqual(replay.ToArray()), "calibration bytes independent of current project toggle");
            string path = Path.Combine(directory, artwork + ".iso");
            DvdImageGenerator.GenerateCalibration(enabledTarget, path, options with { ReadCompatibility = false });
            Require(File.ReadAllBytes(path).SequenceEqual(guarded.ToArray())
                && DvdImageMetadata.TryLoad(path) is
                    { ReadCompatibility: true, ReadCompatibilityMode: DvdReadCompatibilityModes.Periodic9B16 },
                "calibration file bytes match streaming and carry preview metadata");

            // Records created before mode versioning contain the boolean opt-in only.
            var oldTarget = new CalibrationTarget(enabledTarget.SchemaVersion, enabledTarget.Id,
                enabledTarget.Parameters, enabledTarget.Options, enabledTarget.Cells, enabledTarget.Artwork);
            var oldRestored = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(oldTarget, []))).Target;
            Require(oldRestored.DvdReadCompatibilityMode is null
                && oldRestored.EffectiveDvdReadCompatibilityMode == DvdReadCompatibilityModes.LegacyBridge,
                "unversioned enabled calibration retains the original bridge mode");
            using MemoryStream oldStream = new(), oldReplay = new();
            var oldSummary = DvdStreamingGenerator.GeneratePattern(oldTarget, oldStream, options);
            DvdStreamingGenerator.GeneratePattern(oldRestored, oldReplay, options with { ReadCompatibility = false });
            Require(oldSummary.CompatibilityChanges > 0 && oldSummary.CompatibilityChanges < on.CompatibilityChanges
                && oldStream.ToArray().SequenceEqual(oldReplay.ToArray()),
                "old calibration replays its original sparse bridge independently of current options");
            string oldPath = Path.Combine(directory, artwork + "-old.iso");
            DvdImageGenerator.GenerateCalibration(oldRestored, oldPath, options with { ReadCompatibilityMode = "future-mode" });
            Require(File.ReadAllBytes(oldPath).SequenceEqual(oldStream.ToArray())
                && DvdImageMetadata.TryLoad(oldPath) is
                    { ReadCompatibility: true, ReadCompatibilityMode: DvdReadCompatibilityModes.LegacyBridge },
                "legacy calibration export preserves bytes and reports the original mode");
        }
    }

    private static void VerifyModeValidation(string directory, string source, DvdStreamingOptions options)
    {
        var unknown = options with { ReadCompatibilityMode = "future-mode" };
        using MemoryStream output = new();
        int samples = 0;
        try
        {
            DvdStreamingGenerator.GeneratePattern((_, _) => { samples++; return 255; }, output, unknown);
            throw new InvalidOperationException("Expected unsupported protection mode rejection");
        }
        catch (ArgumentException)
        {
            Require(output.Length == 0 && samples == 0, "unknown enabled mode fails before sampling or writing");
        }
        string iso = Path.Combine(directory, "unknown-mode.iso");
        try
        {
            DvdImageGenerator.Generate(new(source, iso, unknown));
            throw new InvalidOperationException("Expected unsupported protection mode rejection");
        }
        catch (ArgumentException)
        {
            Require(!File.Exists(iso) && !File.Exists(iso + ".json"), "unknown enabled mode creates no ISO or metadata");
        }
        using MemoryStream off = new();
        var offSummary = DvdStreamingGenerator.GeneratePattern((_, _) => 255, off, options with
        {
            ReadCompatibility = false,
            ReadCompatibilityMode = DvdReadCompatibilityModes.Off,
        });
        Require(offSummary.CompatibilityChanges == 0 && off.Length == options.ContentLength,
            "disabled protection accepts the explicit off mode");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("DVD read compatibility: " + message);
    }
}
