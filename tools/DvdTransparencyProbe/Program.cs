using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;

namespace DvdTransparencyProbe;

/// <summary>
/// Read-only analysis: for a PNG and DVD geometry, counts the ECC blocks whose whole
/// spiral footprint is transparent (alpha below the threshold). Such blocks are never
/// overwritten by the image generator, so they could hold file data.
/// Defaults are the Verbatim 43533 calibration used by DvdGrayProbe.
/// </summary>
internal static class Program
{
    private const int SectorsPerBlock = 16;
    private const int ChannelBitsPerSector = 38_688;
    private const int ChannelBitsPerBlock = SectorsPerBlock * ChannelBitsPerSector;
    private const long BytesPerBlock = SectorsPerBlock * 2048L;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Console.WriteLine("DvdTransparencyProbe --source IMAGE.png [--preview OUT.png] [--alpha-threshold 1]\n"
                    + "  [--step-mm N] [--skip-sectors N] [--sectors N] [--inner MM] [--outer MM] [--bit-nm NM]\n"
                    + "  [--start-angle DEG] [--clockwise true|false] [--pitch-linear X] [--pitch-quadratic X] [--pitch-cubic X]\n"
                    + "Counts ECC blocks that are fully transparent in the image. No disc data is written.");
                return 0;
            }

            Dictionary<string, string> options = new(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)
                    || !options.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException("Expected unique --name value options.");
            }

            string Text(string name, string fallback) => options.TryGetValue(name, out string? v) ? v : fallback;
            double Number(string name, double fallback) => options.TryGetValue(name, out string? v)
                ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

            string source = options.TryGetValue("--source", out string? s)
                ? Path.GetFullPath(s) : throw new ArgumentException("Missing --source.");
            uint sectors = (uint)Number("--sectors", 2297888);
            if (sectors % SectorsPerBlock != 0)
                throw new ArgumentException("--sectors must be a multiple of 16.");
            Probe probe = new(
                Image.Load(source),
                sectors,
                Number("--inner", 23.991460627731737),
                Number("--outer", 57.964893657925366),
                Number("--bit-nm", 133.3),
                Number("--start-angle", 0),
                bool.Parse(Text("--clockwise", "true")),
                (byte)Number("--alpha-threshold", 1),
                Number("--step-mm", 0),
                (uint)Number("--skip-sectors", 0),
                Number("--pitch-linear", -3.9256854319227684E-05),
                Number("--pitch-quadratic", 6.964795915429256E-05),
                Number("--pitch-cubic", -4.670918950137523E-05));
            Run(probe, options.TryGetValue("--preview", out string? preview) ? Path.GetFullPath(preview) : null);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Run(Probe probe, string? previewPath)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int blocks = checked((int)(probe.Sectors / SectorsPerBlock));
        bool[] used = new bool[blocks];
        Parallel.For(0, blocks, block => used[block] = probe.BlockUsed(block));

        int firstCandidate = checked((int)((probe.SkipSectors + SectorsPerBlock - 1) / SectorsPerBlock));
        int freeBlocks = 0;
        int runCount = 0;
        List<(int Start, int Length)> runs = [];
        int runStart = -1;
        for (int block = firstCandidate; block <= blocks; block++)
        {
            bool free = block < blocks && !used[block];
            if (free)
            {
                freeBlocks++;
                if (runStart < 0) runStart = block;
            }
            else if (runStart >= 0)
            {
                runs.Add((runStart, block - runStart));
                runCount++;
                runStart = -1;
            }
        }

        double mib = 1024.0 * 1024.0;
        Console.WriteLine($"image {probe.Image.Width}x{probe.Image.Height}, step {probe.StepMm:F4} mm, "
            + $"alpha threshold {probe.AlphaThreshold}, elapsed {stopwatch.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"blocks total {blocks}, candidate {blocks - firstCandidate}, "
            + $"fully transparent {freeBlocks} ({100.0 * freeBlocks / Math.Max(1, blocks - firstCandidate):F2}%)");
        Console.WriteLine($"free capacity {freeBlocks * BytesPerBlock / mib:F1} MiB of "
            + $"{(double)blocks * BytesPerBlock / mib:F1} MiB, contiguous runs {runCount}");
        foreach ((int start, int length) in runs.OrderByDescending(r => r.Length).Take(10))
        {
            Console.WriteLine($"  run LBA {(long)start * SectorsPerBlock}..{(long)(start + length) * SectorsPerBlock - 1}, "
                + $"{length} blocks, {length * BytesPerBlock / mib:F1} MiB, "
                + $"radius {probe.RadiusAtBlock(start):F2}..{probe.RadiusAtBlock(start + length):F2} mm");
        }
        Console.WriteLine("runs of >= 1 MiB: "
            + $"{runs.Count(r => r.Length * BytesPerBlock >= 1024 * 1024)}, "
            + $"capacity in them {runs.Where(r => r.Length * BytesPerBlock >= 1024 * 1024).Sum(r => r.Length * BytesPerBlock) / mib:F1} MiB");

        if (previewPath is not null)
        {
            probe.SavePreview(previewPath, used, firstCandidate);
            Console.WriteLine("preview: " + previewPath);
        }
    }
}

internal sealed class Image
{
    private Image(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    internal int Width { get; }

    internal int Height { get; }

    internal byte[] Bgra { get; }

    internal static Image Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap converted = new(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return new Image(converted.PixelWidth, converted.PixelHeight, pixels);
    }
}

internal sealed class Probe
{
    private const int SectorsPerBlock = 16;
    private const int ChannelBitsPerSector = 38_688;
    private const int ChannelBitsPerBlock = SectorsPerBlock * ChannelBitsPerSector;

    private readonly DvdTrackGeometry _geometry;
    private readonly double _bitLengthMm;
    private readonly double _startAngleRadians;
    private readonly double _direction;
    private readonly double _outerRadiusMm;
    private readonly double _scale;
    private readonly int _stepBits;

    internal Probe(Image image, uint sectors, double innerMm, double outerMm, double bitNm,
        double startAngleDegrees, bool clockwise, byte alphaThreshold, double stepMm, uint skipSectors,
        double linear, double quadratic, double cubic)
    {
        Image = image;
        Sectors = sectors;
        AlphaThreshold = alphaThreshold;
        SkipSectors = skipSectors;
        _bitLengthMm = bitNm * 1e-6;
        _outerRadiusMm = outerMm;
        _startAngleRadians = startAngleDegrees * Math.PI / 180.0;
        _direction = clockwise ? -1.0 : 1.0;
        _scale = (Math.Min(image.Width, image.Height) - 1) / (2.0 * outerMm);
        // Half a pixel keeps even one-pixel-wide opaque features from slipping between samples.
        StepMm = stepMm > 0 ? stepMm : 0.5 / _scale;
        _stepBits = Math.Max(1, (int)Math.Round(StepMm / _bitLengthMm));
        double trackLengthMm = sectors * (double)ChannelBitsPerSector * _bitLengthMm;
        _geometry = DvdTrackGeometry.Create(innerMm, outerMm, trackLengthMm, linear, quadratic, cubic);
    }

    internal Image Image { get; }

    internal uint Sectors { get; }

    internal byte AlphaThreshold { get; }

    internal uint SkipSectors { get; }

    internal double StepMm { get; }

    internal double RadiusAtBlock(int block)
    {
        double arc = Math.Min((double)block * ChannelBitsPerBlock * _bitLengthMm, _geometry.TrackLengthMm);
        return _geometry.RadiusAtArcLength(arc);
    }

    /// <summary>True when any sample on the block's spiral touches an alpha >= threshold pixel.</summary>
    internal bool BlockUsed(int block)
    {
        bool used = false;
        VisitBlock(block, (x, y) =>
        {
            if (Opaque(x, y)) used = true;
            return !used;
        });
        return used;
    }

    internal void SavePreview(string path, bool[] used, int firstCandidate)
    {
        int width = Image.Width;
        int height = Image.Height;
        byte[] pixels = new byte[Image.Bgra.Length];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            bool opaque = Image.Bgra[offset + 3] >= AlphaThreshold;
            byte gray = opaque ? Image.Bgra[offset + 1] : (byte)24;
            pixels[offset] = gray;
            pixels[offset + 1] = gray;
            pixels[offset + 2] = gray;
            pixels[offset + 3] = 255;
        }

        for (int block = firstCandidate; block < used.Length; block++)
        {
            if (used[block]) continue;
            VisitBlock(block, (x, y) =>
            {
                int offset = ((y * width) + x) * 4;
                pixels[offset] = 40;
                pixels[offset + 1] = 220;
                pixels[offset + 2] = 40;
                return true;
            });
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private bool Opaque(int x, int y)
    {
        int width = Image.Width;
        int height = Image.Height;
        // The generator interpolates alpha bilinearly, so any of the four neighbours can contribute.
        int x1 = Math.Min(x + 1, width - 1);
        int y1 = Math.Min(y + 1, height - 1);
        return Image.Bgra[(((y * width) + x) * 4) + 3] >= AlphaThreshold
            || Image.Bgra[(((y * width) + x1) * 4) + 3] >= AlphaThreshold
            || Image.Bgra[(((y1 * width) + x) * 4) + 3] >= AlphaThreshold
            || Image.Bgra[(((y1 * width) + x1) * 4) + 3] >= AlphaThreshold;
    }

    /// <summary>Walks the block's spiral and reports each in-image pixel; stops when the callback returns false.</summary>
    private void VisitBlock(int block, Func<int, int, bool> visit)
    {
        ulong firstBit = (ulong)block * ChannelBitsPerBlock;
        for (int bit = 0; ; bit += _stepBits)
        {
            bool last = bit >= ChannelBitsPerBlock;
            double relative = last ? ChannelBitsPerBlock : bit + 0.5;
            double arc = Math.Min((firstBit + relative) * _bitLengthMm, _geometry.TrackLengthMm);
            (double radius, double trackAngle) = _geometry.AtArcLengthFast(arc);
            double polar = _startAngleRadians + (_direction * trackAngle);
            double pixelX = ((Image.Width - 1) / 2.0) + (radius * Math.Cos(polar) * _scale);
            double pixelY = ((Image.Height - 1) / 2.0) - (radius * Math.Sin(polar) * _scale);
            if (pixelX >= 0 && pixelY >= 0 && pixelX <= Image.Width - 1 && pixelY <= Image.Height - 1)
            {
                if (!visit((int)Math.Floor(pixelX), (int)Math.Floor(pixelY))) return;
            }

            if (last) return;
        }
    }
}
