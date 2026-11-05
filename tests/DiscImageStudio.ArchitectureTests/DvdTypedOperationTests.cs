using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.ImageIO;
using DvdImageSolver;
using DvdImageSolver.Solver;

internal static class DvdTypedOperationTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-typed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.png");
            WriteImage(source);
            string data = Path.Combine(directory, "data");
            Directory.CreateDirectory(data);
            File.WriteAllBytes(Path.Combine(data, "sample.bin"), Enumerable.Range(0, 2500).Select(i => (byte)i).ToArray());
            foreach (string mode in new[] { "raw", "inner", "outer" })
                VerifyCliParity(directory, source, data, mode);
            VerifyFailuresAndCancellation(directory, source, data);
            VerifyPreviews(directory, source);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("dvd-typed-operations: passed (CLI bytes/metadata, direct errors, cancellation, previews and Console independence)");
    }

    private static void VerifyCliParity(string directory, string source, string data, string mode)
    {
        string typed = Path.Combine(directory, mode + "-typed.iso");
        string cli = Path.Combine(directory, mode + "-cli.iso");
        DvdStreamingOptions options = new(256, 24, 58, FastParallelism: 1,
            PitchLinear: .02, PitchQuadratic: -.01, Tone: new(.75, 2, -3, true));
        List<DiscJobProgress> progress = [];
        DiscImageResult result = WithoutConsole(() => DvdImageGenerator.Generate(new(source, typed,
            options, mode == "raw" ? null : data, "TYPED", mode == "outer"), new InlineProgress(progress.Add)));
        Require(result.OutputPath == typed && File.Exists(typed + ".json"), "typed output and sidecar");
        Require(progress.First().Stage == "prepare" && progress.Last().Stage == "complete"
            && progress.Any(p => p.Stage == "generate" && p.CompletedUnits > 0), "typed progress includes actual work");
        List<string> arguments = ["solve", "--image", source, "--iso-output", cli,
            "--total-sectors", "256", "--inner-radius-mm", "24", "--outer-radius-mm", "58",
            "--pitch-linear", "0.02", "--pitch-quadratic", "-0.01", "--algorithm", "dispersion",
            "--fast-output", "true", "--fast-parallelism", "1", "--spiral-direction", "cw",
            "--tone-dither", "0.75", "--tone-levels", "2", "--tone-brightness", "-3", "--tone-gamma", "true"];
        if (mode == "raw") arguments.AddRange(["--fill-sectors", "256"]);
        else arguments.AddRange(["--data-dir", data, "--volume-label", "TYPED", "--file-position", mode]);
        Require(DvdCommandRunner.Run(arguments.ToArray()) == 0, "CLI comparison generation");
        byte[] actual = File.ReadAllBytes(typed), expected = File.ReadAllBytes(cli);
        if (mode != "raw")
        {
            NormalizeVolumeTimestamps(actual);
            NormalizeVolumeTimestamps(expected);
        }
        Require(actual.SequenceEqual(expected), mode + " ISO payload and filesystem parity");
        JsonObject typedMetadata = JsonNode.Parse(File.ReadAllText(typed + ".json"))!.AsObject();
        JsonObject cliMetadata = JsonNode.Parse(File.ReadAllText(cli + ".json"))!.AsObject();
        foreach (string variable in new[] { "isoOutput", "elapsedSeconds" })
        {
            typedMetadata.Remove(variable);
            cliMetadata.Remove(variable);
        }
        Require(JsonNode.DeepEquals(typedMetadata, cliMetadata), mode + " complete sidecar schema and values");
    }

    private static void VerifyFailuresAndCancellation(string directory, string source, string data)
    {
        DvdStreamingOptions options = new(256, 24, 58, FastParallelism: 1);
        string output = Path.Combine(directory, "failure.iso");
        string missing = Path.Combine(directory, "missing.png");
        FileNotFoundException error = Capture<FileNotFoundException>(() => WithoutConsole(() =>
            DvdImageGenerator.Generate(new(missing, output, options))));
        // Runtime/JIT inlining may omit RasterCodec's forwarding frame. The I/O
        // exception must still retain its native code, filename and throw stack.
        Require(error.FileName == missing && error.HResult == unchecked((int)0x80070002)
            && !string.IsNullOrWhiteSpace(error.StackTrace),
            "source exception type, filename, HRESULT and throw stack survive");
        Require(!File.Exists(output + ".json"), "failure does not publish a success sidecar");

        byte[] sentinel = "preserve existing output"u8.ToArray();
        File.WriteAllBytes(output, sentinel);
        using CancellationTokenSource alreadyCanceled = new();
        alreadyCanceled.Cancel();
        foreach (string? folder in new string?[] { null, data })
        {
            OperationCanceledException canceled = Capture<OperationCanceledException>(() =>
                DvdImageGenerator.Generate(new(source, output, options, folder), cancellationToken: alreadyCanceled.Token));
            Require(canceled.CancellationToken == alreadyCanceled.Token && File.ReadAllBytes(output).SequenceEqual(sentinel),
                "pre-canceled raw and hybrid requests preserve the existing output");
        }

        using CancellationTokenSource cancellation = new();
        bool completed = false;
        Capture<OperationCanceledException>(() => DvdImageGenerator.Generate(new(source, output, options),
            new InlineProgress(p =>
            {
                if (p.Stage == "generate") cancellation.Cancel();
                completed |= p.Stage == "complete";
            }), cancellation.Token));
        Require(!completed && !File.Exists(output + ".json"), "in-flight cancellation avoids completion and metadata");

        IOException callbackError = new("progress callback failed", new InvalidOperationException("inner diagnostic"));
        callbackError.Data["operation"] = "test progress";
        IOException observed = Capture<IOException>(() => DvdImageGenerator.Generate(new(source, output, options),
            new InlineProgress(p => { if (p.Stage == "generate") ThrowProgressFailure(callbackError); })));
        Require(ReferenceEquals(observed, callbackError) && observed.InnerException is not null
            && Equals(observed.Data["operation"], "test progress")
            && observed.StackTrace?.Contains(nameof(ThrowProgressFailure), StringComparison.Ordinal) == true,
            "callback exceptions retain identity, inner, Data and the original throw frame");
    }

    // Unlike an ordinary engine forwarding method, this is a stable throw-frame
    // anchor even under optimized/tiered JIT. A boundary using `throw ex` fails it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowProgressFailure(Exception exception) => throw exception;

    private static void VerifyPreviews(string directory, string source)
    {
        string iso = Path.Combine(directory, "outer-typed.iso");
        var range = IsoReadbackRenderer.ReadDrawingRange(iso, 256);
        Require(range.StartLba > 0 && range.FillSectors < 256, "root sidecar drawing range restored");
        string typed = Path.Combine(directory, "readback-typed.png");
        string cli = Path.Combine(directory, "readback-cli.png");
        IsoReadbackOptions readback = new(256, range.StartLba, range.FillSectors, 0x30000,
            24, 58, 133.3, true, 64, 16);
        WithoutConsole(() => IsoReadbackRenderer.Render(iso, typed, readback));
        Require(DvdCommandRunner.Run(["simulate", "--iso", iso, "--output", cli,
            "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--preview-size", "64"]) == 0,
            "CLI readback comparison");
        Require(SamePixels(typed, cli), "typed readback matches CLI including hybrid range");

        CalibrationPreviewOptions calibration = new(256, 0, 256, 24, 58, 24.1, 58.1,
            133.3, true, 128, 1, 64, 16);
        typed = Path.Combine(directory, "calibrate-typed.png");
        cli = Path.Combine(directory, "calibrate-cli.png");
        WithoutConsole(() => CalibrationPreviewRenderer.Render(source, typed, calibration));
        Require(DvdCommandRunner.Run(["calibrate", "--image", source, "--output", cli,
            "--total-sectors", "256", "--generated-inner-radius-mm", "24", "--generated-outer-radius-mm", "58",
            "--actual-inner-radius-mm", "24.1", "--actual-outer-radius-mm", "58.1", "--preview-size", "64"]) == 0,
            "CLI calibration comparison");
        Require(SamePixels(typed, cli), "typed calibration preview matches CLI");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        string canceled = Path.Combine(directory, "canceled.png");
        Capture<OperationCanceledException>(() => CalibrationPreviewRenderer.Render("missing", canceled, calibration, cancellation.Token));
        Capture<OperationCanceledException>(() => IsoReadbackRenderer.Render("missing", canceled, readback, cancellation.Token));
        Require(!File.Exists(canceled), "pre-canceled previews do not load sources or create output");
        Capture<FileNotFoundException>(() => IsoReadbackRenderer.Render(Path.Combine(directory, "missing.iso"), canceled, readback));

        string external = Path.Combine(directory, "external.iso");
        File.Copy(iso, external);
        Require(IsoReadbackRenderer.ReadDrawingRange(external, 256) == (0u, 256u), "missing sidecar fallback");
        foreach (string json in new[] { "{bad", "[]", "null" })
        {
            File.WriteAllText(external + ".json", json);
            Require(IsoReadbackRenderer.ReadDrawingRange(external, 256) == (0u, 256u), "malformed sidecar fallback");
        }
        File.WriteAllText(external + ".json", "{\"startLba\":32,\"fillSectors\":64}");
        Require(IsoReadbackRenderer.ReadDrawingRange(external, 256) == (32u, 64u), "range does not require imageMapping metadata");
    }

    private static bool SamePixels(string first, string second)
        => RasterCodec.LoadBgra32(first).Pixels.SequenceEqual(RasterCodec.LoadBgra32(second).Pixels);

    private static T WithoutConsole<T>(Func<T> action)
    {
        TextWriter output = Console.Out, error = Console.Error;
        try { Console.SetOut(new RejectConsole()); Console.SetError(new RejectConsole()); return action(); }
        finally { Console.SetOut(output); Console.SetError(error); }
    }

    private sealed class RejectConsole : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => throw new InvalidOperationException("Typed operations must not write to Console.");
    }

    private sealed class InlineProgress(Action<DiscJobProgress> report) : IProgress<DiscJobProgress>
    {
        public void Report(DiscJobProgress value) => report(value);
    }

    private static T Capture<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("DVD typed operations: " + message);
    }

    private static void NormalizeVolumeTimestamps(byte[] bytes)
    {
        foreach (int sector in new[] { 16, 17 })
        {
            bytes.AsSpan(sector * 2048 + 174, 7).Clear();
            bytes.AsSpan(sector * 2048 + 813, 34).Clear();
        }
    }

    private static void WriteImage(string path)
    {
        byte[] pixels = new byte[32 * 32 * 4];
        for (int i = 0; i < 32 * 32; i++)
        {
            byte gray = (byte)(i * 37);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = gray;
            pixels[i * 4 + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }
}
