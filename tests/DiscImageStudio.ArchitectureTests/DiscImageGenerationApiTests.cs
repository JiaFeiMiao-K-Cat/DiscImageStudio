using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;

internal static class DiscImageGenerationApiTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-image-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source image.png");
            WriteImage(source);
            TestCdAudio(source, directory);
            TestCdData(source, directory);
            TestBluRay(source, directory);
            TestFailures(source, directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("disc-image-api: passed (typed/CLI parity, sidecars, progress, original exceptions and cancellation)");
    }

    private static void TestCdAudio(string source, string directory)
    {
        string output = Path.Combine(directory, "audio track.wav");
        CdDiscParameters parameters = new(24.5, 56.8, 40, PitchLinear: 0.01, PitchQuadratic: -0.02, PitchCubic: 0.03);
        ToneProfile tone = new(0.25, ToneProfile.FourLevels, 8, true);
        DiscJobProgress? last = null;
        DiscImageResult result = CdImageGenerator.Generate(new(source, output, parameters, tone, Interleave: false),
            new InlineProgress<DiscJobProgress>(value => last = value));
        Require(result.OutputPath == output && result.Summary!.Contains("CD track complete", StringComparison.Ordinal),
            "typed CD returns the published output and summary");
        Require(last is { ModuleId: CdDiscModule.ModuleId, Stage: "generate", Fraction: 1 }
            && last.CompletedUnits == parameters.TotalBytes, "typed CD reports structured byte progress");
        byte[] bytes = File.ReadAllBytes(output);
        string metadata = File.ReadAllText(output + ".json");
        string cue = File.ReadAllText(Path.ChangeExtension(output, ".cue"));
        DiscJobResult command = new CdDiscModule().ExecuteAsync(new("cd-generate",
        [
            "--input", source, "--output", output, "--sectors", "40", "--interleave", "false",
            "--pitch-linear", "0.01", "--pitch-quadratic", "-0.02", "--pitch-cubic", "0.03",
            "--tone-dither", "0.25", "--tone-levels", "4", "--tone-brightness", "8", "--tone-gamma", "true",
        ])).GetAwaiter().GetResult();
        Require(command.Succeeded && command.OutputPath == result.OutputPath, "CD CLI delegates to the same file export");
        Require(File.ReadAllBytes(output).SequenceEqual(bytes), "CD typed/CLI WAV bytes match");
        Require(File.ReadAllText(output + ".json") == metadata
            && File.ReadAllText(Path.ChangeExtension(output, ".cue")) == cue, "CD typed/CLI metadata and CUE match");
    }

    private static void TestCdData(string source, string directory)
    {
        string files = Path.Combine(directory, "cd files");
        Directory.CreateDirectory(files);
        File.WriteAllBytes(Path.Combine(files, "data.bin"), [1, 2, 3, 4, 5]);
        string output = Path.Combine(directory, "cd data.iso");
        CdDiscParameters parameters = new(24.5, 56.8, 128);
        ToneProfile tone = ToneProfile.Photo;
        CdImageGenerator.Generate(new(source, output, parameters, tone,
            DataDirectory: files, VolumeLabel: "API_CD", FilesAtOuterRing: true));
        CdDataImageMetadata metadata = CdDataImageMetadata.TryLoad(output)
            ?? throw new InvalidOperationException("typed CD data metadata missing");
        Require(metadata.Parameters == parameters && metadata.Tone == tone && metadata.FilesAtOuterRing,
            "typed CD data retains geometry, tone and file placement");
        byte[] image = File.ReadAllBytes(output);
        DiscJobResult command = new CdDiscModule().ExecuteAsync(new("cd-generate",
        [
            "--input", source, "--output", output, "--sectors", "128", "--data-dir", files,
            "--volume-label", "API_CD", "--file-position", "outer", "--tone-dither", "0.75",
        ])).GetAwaiter().GetResult();
        Require(command.Succeeded && CdDataImageMetadata.TryLoad(output) == metadata,
            "CD data CLI and typed API preserve the same sidecar");
        int start = checked((int)metadata.DrawingStartLba * 2048);
        int count = checked((int)(metadata.DrawingEndLbaExclusive - metadata.DrawingStartLba) * 2048);
        // The ISO descriptors contain a fresh creation timestamp on each export.
        Require(File.ReadAllBytes(output).AsSpan(start, count).SequenceEqual(image.AsSpan(start, count)),
            "CD data CLI and typed API produce identical drawing bytes");
    }

    private static void TestBluRay(string source, string directory)
    {
        string output = Path.Combine(directory, "bd raw.iso");
        BluRayDiscParameters parameters = new(TotalSectors: 64, StartAngleDegrees: 30,
            PitchLinear: 0.01, PitchQuadratic: -0.02, PitchCubic: 0.03);
        ToneProfile tone = new(0.25, ToneProfile.TwoLevels, 8, true);
        int loads = 0;
        BluRayGray8Raster Load(string path, CancellationToken token)
        {
            Require(path == source, "BD loader receives the source path unchanged");
            token.ThrowIfCancellationRequested();
            loads++;
            return new(2, 2, [30, 130, 180, 230]);
        }
        BluRayImageGenerator generator = new(Load);
        DiscJobProgress? last = null;
        DiscImageResult result = generator.Generate(new(source, output, parameters, tone, InvertMapping: true),
            new InlineProgress<DiscJobProgress>(value => last = value));
        Require(loads == 1 && result.OutputPath == output, "typed BD decodes once and returns the output");
        Require(last is { ModuleId: BluRayDiscModule.ModuleId, Stage: "iso", Fraction: 1 }
            && last.CompletedUnits == parameters.ContentLength, "typed BD reports structured byte progress");
        byte[] bytes = File.ReadAllBytes(output);
        BluRayIsoMetadata metadata = BluRayIsoMetadata.TryLoad(output)
            ?? throw new InvalidOperationException("typed BD metadata missing");
        DiscJobResult command = new BluRayDiscModule(Load).ExecuteAsync(new("bd-generate",
        [
            "--input", source, "--output", output, "--total-sectors", "64", "--start-angle", "30",
            "--pitch-linear", "0.01", "--pitch-quadratic", "-0.02", "--pitch-cubic", "0.03",
            "--invert", "true", "--tone-dither", "0.25", "--tone-levels", "2",
            "--tone-brightness", "8", "--tone-gamma", "true",
        ])).GetAwaiter().GetResult();
        Require(command.Succeeded && loads == 2 && File.ReadAllBytes(output).SequenceEqual(bytes),
            "BD typed/CLI bytes match with nondefault geometry, polarity and tone");
        BluRayIsoMetadata commandMetadata = BluRayIsoMetadata.TryLoad(output)
            ?? throw new InvalidOperationException("BD CLI metadata missing");
        Require((commandMetadata with { GeneratedUtc = metadata.GeneratedUtc }) == metadata,
            "BD typed/CLI sidecars match apart from generation time");

        string files = Path.Combine(directory, "bd files");
        Directory.CreateDirectory(files);
        byte[] content = [1, 5, 9, 13, 17];
        File.WriteAllBytes(Path.Combine(files, "data.bin"), content);
        string udf = Path.Combine(directory, "bd files.iso");
        generator.Generate(new(source, udf, new(TotalSectors: 1024), tone,
            DataDirectory: files, VolumeLabel: "API_BD", FilesAtOuterRing: true));
        BluRayIsoMetadata udfMetadata = BluRayIsoMetadata.TryLoad(udf)
            ?? throw new InvalidOperationException("typed BD UDF metadata missing");
        Require(udfMetadata.FilesAtOuterRing && udfMetadata.DataDirectory == files && udfMetadata.Tone == tone,
            "typed BD retains file placement and tone in the sidecar");
        Dictionary<string, byte[]> restored = UdfFilesystemTests.Inspect(File.ReadAllBytes(udf), 1024, "API_BD");
        Require(restored.Count == 1 && restored["data.bin"].SequenceEqual(content),
            "typed BD preserves files in a valid outer-ring UDF volume");
    }

    private static void TestFailures(string source, string directory)
    {
        string cdOutput = Path.Combine(directory, "cancelled.wav");
        byte[] original = [3, 7, 11];
        File.WriteAllBytes(cdOutput, original);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        CdImageRequest cd = new(source, cdOutput, new(24.5, 56.8, 40), ToneProfile.LegacyBinary);
        OperationCanceledException cdCancelled = Throws<OperationCanceledException>(() =>
            CdImageGenerator.Generate(cd, cancellationToken: cancelled.Token));
        Require(cdCancelled.CancellationToken == cancelled.Token && File.ReadAllBytes(cdOutput).SequenceEqual(original),
            "typed CD pre-cancellation preserves the original token and output");
        IOException observerFailure = new("host progress observer failed");
        Require(ReferenceEquals(observerFailure, Throws<IOException>(() => CdImageGenerator.Generate(cd,
            new InlineProgress<DiscJobProgress>(_ => throw observerFailure)))),
            "typed CD preserves the original observer exception");

        string bdOutput = Path.Combine(directory, "cancelled.iso");
        File.WriteAllBytes(bdOutput, original);
        BluRayImageRequest bd = new(source, bdOutput, new(TotalSectors: 64), ToneProfile.LegacyBinary);
        InvalidDataException decodeFailure = new("source decoder failure", new IOException("nested decoder detail"));
        decodeFailure.Data["source"] = source;
        BluRayImageGenerator failed = new((_, _) => throw decodeFailure);
        Require(ReferenceEquals(decodeFailure, Throws<InvalidDataException>(() => failed.Generate(bd))),
            "typed BD preserves the original decoder exception, inner exception and diagnostic data");
        BluRayDiscModule failedModule = new((_, _) => throw decodeFailure);
        Require(ReferenceEquals(decodeFailure, Throws<InvalidDataException>(() => failedModule.ExecuteAsync(new("bd-generate",
            ["--input", source, "--output", bdOutput, "--total-sectors", "64"])).GetAwaiter().GetResult())),
            "BD CLI adapter preserves the same failure rather than converting it to an exit code");
        int loads = 0;
        BluRayImageGenerator generator = new((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            loads++;
            return new(1, 1, [0]);
        });
        Throws<OperationCanceledException>(() => generator.Generate(bd, cancellationToken: cancelled.Token));
        Throws<ArgumentException>(() => generator.Generate(bd with { Tone = new(0, ToneProfile.FourLevels, 0) }));
        Require(loads == 0 && File.ReadAllBytes(bdOutput).SequenceEqual(original),
            "typed BD pre-cancellation and validation avoid loading or replacing the output");
        using CancellationTokenSource duringWrite = new();
        OperationCanceledException bdCancelled = Throws<OperationCanceledException>(() => generator.Generate(bd,
            new InlineProgress<DiscJobProgress>(value =>
            {
                if (value.CompletedUnits > 0) duringWrite.Cancel();
            }), duringWrite.Token));
        Require(bdCancelled.CancellationToken == duringWrite.Token && File.ReadAllBytes(bdOutput).SequenceEqual(original)
            && !File.Exists(bdOutput + ".json"), "typed BD mid-write cancellation preserves the output and skips its sidecar");
    }

    private static void WriteImage(string path)
    {
        byte[] pixels = Enumerable.Range(0, 64).Select(value => (byte)(value * 37)).ToArray();
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8, 8, 96, 96,
            PixelFormats.Gray8, null, pixels, 8)));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
