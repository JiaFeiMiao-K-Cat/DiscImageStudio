using System.Globalization;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using SkiaSharp;

namespace DiscImageStudio.Desktop;

/// <summary>Portable shell state. Documents and optical algorithms remain owned by Core and the modules.</summary>
public sealed class ProjectSession
{
    public ProjectDocument? Document { get; private set; }
    public string? ProjectPath { get; private set; }
    public bool IsDirty { get; private set; }
    public event Action? Changed;
    public static string ApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscImageStudio");
    // A private per-session directory avoids collisions between app instances. Only this directory is deleted.
    public string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "DiscImageStudio", Guid.NewGuid().ToString("N"));
    public double CanvasRadius => Document!.IsCd ? 57.5 : Document.Geometry.OuterRadiusMm;

    public void Create(string family, string name, ProjectGeometry geometry, bool invert = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var document = new ProjectDocument(2, name, family, geometry, [],
            new(Path.Combine(ProjectJsonStore.GetDefaultProjectsDirectory(), family == "cd" ? "cd-track.wav" : "disc-image.iso"), true, "", "DISC_IMAGE", true), null, null, now, now, ToneProfile.Photo,
            family == "bd" ? new(invert) : null);
        ProjectJsonStore.Validate(document);
        ProjectPath = null;
        Document = document;
        IsDirty = true;
        Changed?.Invoke();
    }

    public void Open(string path)
    {
        ProjectDocument document = ProjectJsonStore.Load(path);
        Document = document;
        ProjectPath = Path.GetFullPath(path);
        IsDirty = false;
        Changed?.Invoke();
    }

    public void Update(ProjectDocument document)
    {
        ProjectJsonStore.Validate(document);
        Document = document with { ModifiedUtc = DateTimeOffset.UtcNow };
        IsDirty = true;
        Changed?.Invoke();
    }

    public void Save(string path)
    {
        ProjectDocument document = Document ?? throw new InvalidOperationException("请先新建工程。");
        string? thumbnail = document.ThumbnailPngBase64;
        if (document.Layers.Count > 0)
        {
            string preview = CachePath("thumbnail.png");
            DesignComposer.Render(document, CanvasRadius, 256, preview);
            thumbnail = Convert.ToBase64String(File.ReadAllBytes(preview));
        }
        document = document with { ThumbnailPngBase64 = thumbnail };
        ProjectJsonStore.Save(path, document);
        Document = document;
        ProjectPath = Path.GetFullPath(path);
        IsDirty = false;
        Changed?.Invoke();
    }

    public string CachePath(string name)
    {
        Directory.CreateDirectory(CacheDirectory);
        return Path.Combine(CacheDirectory, name);
    }

    public string Compose(int size)
    {
        if (Document is null || !Document.Layers.Any(x => x.Visible))
            throw new InvalidOperationException("请先在设计页导入图片。");
        string path = CachePath($"composite-{size}.png");
        DesignComposer.Render(Document, CanvasRadius, size, path);
        return path;
    }

    public CalibrationParameters CalibrationParameters()
    {
        var d = Document!;
        var g = d.Geometry;
        return new(d.IsCd ? CalibrationDiscKind.Cd : d.IsDvd ? CalibrationDiscKind.Dvd : CalibrationDiscKind.BluRay,
            g.InnerRadiusMm, g.OuterRadiusMm, g.Sectors, d.IsCd ? 1200 : d.IsDvd ? 133.3 : 74.5,
            g.PitchLinear, g.PitchQuadratic, g.PitchCubic, d.BluRay?.InvertMapping ?? false);
    }

    public void CleanCache()
    {
        try { if (Directory.Exists(CacheDirectory)) Directory.Delete(CacheDirectory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Same mm placement and topmost-first ordering as the WPF design composer.</summary>
public static class DesignComposer
{
    public static void Render(ProjectDocument document, double radius, int size, string output)
    {
        if (!double.IsFinite(radius) || radius <= 0 || size is < 64 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(size));
        using SKBitmap bitmap = new(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(size / 2f, size / 2f);
        canvas.Scale((float)(size / (radius * 2)));
        using SKPaint paint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        foreach (ProjectLayer layer in document.Layers.Reverse().Where(layer => layer.Visible))
        {
            using var image = SKBitmap.Decode(layer.ImagePath)
                ?? throw new InvalidDataException($"无法读取图片：{layer.ImagePath}");
            // WPF fits the longest side to the full design diameter at scale 1.
            double fit = document.Geometry.OuterRadiusMm * 2 / Math.Max(image.Width, image.Height);
            float w = (float)(image.Width * fit * layer.Scale);
            float h = (float)(image.Height * fit * layer.Scale);
            canvas.Save();
            canvas.Translate((float)layer.TranslateXMm, (float)layer.TranslateYMm);
            canvas.RotateDegrees((float)layer.RotationDeg);
            canvas.DrawBitmap(image, new SKRect(-w / 2, -h / 2, w / 2, h / 2), paint);
            canvas.Restore();
        }
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(output);
        encoded.SaveTo(stream);
    }
}

public static class JobArguments
{
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static List<string> Geometry(ProjectDocument document)
    {
        var g = document.Geometry;
        return document.IsDvd
            ? ["--total-sectors", N(g.Sectors), "--inner-radius-mm", N(g.InnerRadiusMm), "--outer-radius-mm", N(g.OuterRadiusMm),
               "--pitch-linear", N(g.PitchLinear), "--pitch-quadratic", N(g.PitchQuadratic), "--pitch-cubic", N(g.PitchCubic)]
            : [document.IsCd ? "--sectors" : "--total-sectors", N(g.Sectors), "--r0", N(g.InnerRadiusMm), "--r1", N(g.OuterRadiusMm)];
    }

    public static List<string> Tone(ProjectDocument document)
    {
        var t = document.Tone ?? ToneProfile.LegacyBinary;
        return ["--tone-dither", N(t.DitherStrength), "--tone-levels", N(document.IsCd ? t.Levels : 2),
            "--tone-brightness", N(t.Brightness), "--tone-gamma", t.SrgbLinearization.ToString().ToLowerInvariant()];
    }

    public static string[] Generate(ProjectDocument document, string source, string output)
    {
        ProjectJsonStore.Validate(document);
        var o = document.Outputs;
        string extension = Path.GetExtension(output).ToLowerInvariant();
        bool audio = document.IsCd && string.IsNullOrWhiteSpace(o.DataDirectory);
        if (audio ? extension is not (".wav" or ".raw") : extension != ".iso")
            throw new ArgumentException(audio ? "CD 音轨请使用 .wav 或 .raw 后缀。" : "数据镜像请使用 .iso 后缀。");
        List<string> args = [document.IsCd ? "cd-generate" : document.IsDvd ? "solve" : "bd-generate",
            document.IsDvd ? "--image" : "--input", source, document.IsDvd ? "--iso-output" : "--output", output];
        args.AddRange(Geometry(document));
        args.AddRange(Tone(document));
        args.AddRange(["--volume-label", string.IsNullOrWhiteSpace(o.VolumeLabel) ? "DISC_IMAGE" : o.VolumeLabel]);
        if (!string.IsNullOrWhiteSpace(o.DataDirectory))
            args.AddRange(["--data-dir", o.DataDirectory, "--file-position", o.FilesAtOuterRing ? "outer" : "inner"]);
        if (document.IsCd)
            args.AddRange(["--interleave", o.Interleave.ToString().ToLowerInvariant(), "--cue", o.WriteCue.ToString().ToLowerInvariant()]);
        if (document.IsDvd)
        {
            args.AddRange(["--spiral-direction", "cw", "--algorithm", "dispersion", "--iterations-per-block", "1", "--fast-output", "true"]);
            if (string.IsNullOrWhiteSpace(o.DataDirectory)) args.AddRange(["--fill-sectors", N(document.Geometry.Sectors)]);
        }
        if (document.IsBluRay) args.AddRange(["--invert", (document.BluRay?.InvertMapping ?? false).ToString().ToLowerInvariant()]);
        return args.ToArray();
    }

    public static string[] Preview(ProjectDocument document, string input, string output, bool readback,
        double measuredInner, double measuredOuter, int size, int samples)
    {
        var g = document.Geometry;
        var measured = document with { Geometry = g with { InnerRadiusMm = measuredInner, OuterRadiusMm = measuredOuter } };
        ProjectJsonStore.Validate(measured);
        if (size is < 256 or > 8192 || samples is < 1 or > 256) throw new ArgumentException("预览尺寸须为 256–8192，采样须为 1–256。");
        List<string> args = [document.IsCd ? readback ? "cd-preview-track" : "cd-preview-warp" : readback ? "simulate" : "calibrate",
            document.IsCd ? readback ? "--track" : "--input" : readback ? "--iso" : "--image", input, "--output", output];
        args.AddRange(Geometry(readback ? measured : document));
        if (document.IsCd)
        {
            args.AddRange(["--size", N(size)]);
            if (readback) args.AddRange(["--deinterleave", document.Outputs.Interleave.ToString().ToLowerInvariant(), "--byte-step", N(Math.Max(1, 2352 / samples))]);
            else args.AddRange(["--actual-r0", N(measuredInner), "--actual-r1", N(measuredOuter), "--samples-per-sector", N(samples)]);
        }
        else
        {
            args.AddRange(["--preview-size", N(size), "--samples-per-sector", N(samples), "--spiral-direction", "cw"]);
            if (!readback) args.AddRange(["--generated-inner-radius-mm", N(g.InnerRadiusMm), "--generated-outer-radius-mm", N(g.OuterRadiusMm),
                "--actual-inner-radius-mm", N(measuredInner), "--actual-outer-radius-mm", N(measuredOuter),
                "--actual-pitch-linear", N(g.PitchLinear), "--actual-pitch-quadratic", N(g.PitchQuadratic), "--actual-pitch-cubic", N(g.PitchCubic),
                "--lba", "0", "--fill-sectors", N(g.Sectors)]);
        }
        if (!readback) args.AddRange(Tone(document));
        return args.ToArray();
    }
}
