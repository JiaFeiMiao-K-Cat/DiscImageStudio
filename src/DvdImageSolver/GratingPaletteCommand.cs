using System.Collections.Concurrent;
using DiscImageStudio.Iso;
using DvdImageSolver.Encoding;
using DvdImageSolver.Solver;

namespace DvdImageSolver;

/// <summary>
/// Writes a full DVD image whose payload words are grating motifs selected from a
/// flat source image. This intentionally uses the same dispersion writer and ISO
/// prefix as the proven monochrome path; only the payload-word callback changes.
/// </summary>
internal static class GratingPaletteCommand
{
    internal static int Run(CliArguments args)
    {
        string imagePath = args.Require("image");
        string outputPath = args.Require("output");
        string volumeLabel = args.Get("volume-label", "SUZUME-GRATING");
        uint totalSectors = args.GetUInt("total-sectors", 2_297_888);
        double inner = args.GetDouble("inner-radius-mm", 24.063112765327595);
        double outer = args.GetDouble("outer-radius-mm", 58.22734793558852);
        double cellMm = args.GetDouble("cell-mm", 0.068);
        double pitchLinear = args.GetDouble("pitch-linear", -3.625995787598983e-11);
        double pitchQuadratic = args.GetDouble("pitch-quadratic", 5.59609470367389e-10);
        double pitchCubic = args.GetDouble("pitch-cubic", 4.337809583594884e-10);

        if (totalSectors == 0 || totalSectors % 16 != 0)
            throw new ArgumentException("--total-sectors must be a positive multiple of 16.");
        if (cellMm <= 0) throw new ArgumentException("--cell-mm must be positive.");
        if (totalSectors > 4_000_000) throw new ArgumentException("--total-sectors is too large.");

        var image = RasterImage.Load(imagePath);
        var cells = new CellPlan(image, inner, outer, cellMm);

        Iso9660ImageWriter.LayoutPlan isoPlan = Iso9660ImageWriter.Plan(
            sourceDirectory: null, totalSectors, volumeLabel,
            timestamp: new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero));
        uint drawingStart = Align16(isoPlan.MetadataEndLbaExclusive);
        if (drawingStart >= totalSectors)
            throw new ArgumentException("Drawing area is empty.");

        ImageMappingOptions mapping = new(
            totalSectors, inner, outer, 133.3,
            0, true, 128, 1, 1,
            pitchLinear, pitchQuadratic, pitchCubic);

        MultiBlockSolveOptions options = new(
            StartLba: drawingStart,
            FillSectors: totalSectors - drawingStart,
            PsnOffset: 0x30000,
            IterationsPerBlock: 0,
            MutationBytes: 0,
            RandomSeed: 1,
            InitialTemperature: 0,
            FinalTemperature: 0,
            ZeroInitialPayload: true,
            ImageMapping: mapping,
            Algorithm: SolverAlgorithm.Dispersion,
            FastParallelism: 0)
        {
            UseSampleReuse = false,
            UseSimdSampling = false,
        };

        using FileStream output = File.Create(outputPath);
        Iso9660ImageWriter.WriteSequentialPrefix(isoPlan, output, drawingStart);
        FastDispersionImageWriter.WriteScrambledCodewordPatternStream(
            cells.Sample,
            output,
            options,
            progress =>
            {
                if (progress.CompletedBlocks == 1
                    || progress.CompletedBlocks == progress.TotalBlocks
                    || progress.CompletedBlocks % 5000 == 0)
                {
                    Console.Error.WriteLine(
                        $"block {progress.CompletedBlocks}/{progress.TotalBlocks}, "
                        + $"LBA={progress.BlockLba}, bytes={progress.BytesWritten}, "
                        + $"elapsed={progress.Elapsed.TotalSeconds:F1}s");
                    Console.Error.Flush();
                }
            });
        output.SetLength((long)totalSectors * 2048);
        output.Flush();
        Console.Error.WriteLine(
            $"done: {outputPath}, sectors={totalSectors}, drawingStart={drawingStart}, "
            + $"cell={cellMm * 1000:F1}um, palette8={DvdGratingPalette.Motifs.Length}");
        return 0;
    }

    private static uint Align16(uint value) => (value + 15u) & ~15u;

    private sealed class CellPlan
    {
        private readonly int _cols;
        private readonly int _rows;
        private readonly double _origin;
        private readonly double _cell;
        private readonly byte[] _values;

        internal CellPlan(RasterImage image, double inner, double outer, double cellMm)
        {
            _cell = cellMm;
            _origin = -outer;
            _cols = (int)Math.Ceiling((2 * outer) / cellMm);
            _rows = _cols;
            _values = new byte[checked(_cols * _rows)];
            for (int row = 0; row < _rows; row++)
            {
                double y = _origin + ((row + 0.5) * cellMm);
                for (int col = 0; col < _cols; col++)
                {
                    double x = _origin + ((col + 0.5) * cellMm);
                    double radius = Math.Sqrt((x * x) + (y * y));
                    if (radius < inner || radius > outer) continue;
                    var color = image.SampleDiscColor(x, y, outer);
                    if (color.Alpha == 0) continue;
                    double blue = color.Blue / 255.0;
                    double green = color.Green / 255.0;
                    double red = color.Red / 255.0;
                    double luminance = 0.2126 * Linear(red) + 0.7152 * Linear(green) + 0.0722 * Linear(blue);
                    double max = Math.Max(red, Math.Max(green, blue));
                    double min = Math.Min(red, Math.Min(green, blue));
                    double delta = max - min;
                    double hue = delta < 1e-9 ? 0 : max == red
                        ? 60 * (((green - blue) / delta) % 6)
                        : max == green ? 60 * (((blue - red) / delta) + 2)
                        : 60 * (((red - green) / delta) + 4);
                    if (hue < 0) hue += 360;
                    double saturation = max > 1e-9 ? delta / max : 0;
                    double theta = ((Math.Atan2(y, x) * 180 / Math.PI) + 90 + 360) % 360;
                    int wedge = (int)(theta / 120) % 3;
                    int paletteIndex = wedge == 0
                        ? PaletteForHue(hue)
                        : wedge == 1 ? AccentForHue(hue, saturation) : -1;
                    bool on = Bayer(col, row) < luminance;
                    // 0 = black (A5), 1 = white (92), 2..6 = B/G/C/D/E.
                    byte cell = on ? (paletteIndex < 0 ? (byte)1 : (byte)(paletteIndex + 1)) : (byte)0;
                    _values[(row * _cols) + col] = cell;
                }
            }
        }

        internal byte Sample(DvdCodewordSample sample)
        {
            int col = (int)Math.Floor((sample.Xmm - _origin) / _cell);
            int row = (int)Math.Floor((sample.Ymm - _origin) / _cell);
            if (col < 0 || col >= _cols || row < 0 || row >= _rows) return 0;
            byte value = _values[(row * _cols) + col];
            if (value == 0) return 0xA5;       // black motif
            if (value == 1) return 0x92;       // white motif
            byte[] motifs = DvdGratingPalette.Motifs[value - 1];
            return motifs[(int)(sample.GlobalCodewordIndex % (ulong)motifs.Length)];
        }

        private static double Linear(double value)
            => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        private static int PaletteForHue(double hue) => hue switch
        {
            >= 330 or < 20 => 3,   // C red
            >= 20 and < 55 => 2,   // G yellow
            >= 55 and < 150 => 1,  // B green
            >= 150 and < 195 => 4, // D teal
            >= 195 and < 330 => 5, // E blue (violet approximated)
            _ => 3,
        };

        private static int AccentForHue(double hue, double saturation)
        {
            if (saturation < 0.22) return 1;
            return hue switch
            {
                >= 20 and < 55 => 2,
                >= 55 and < 150 => 1,
                >= 195 and < 260 => 5,
                _ => 1,
            };
        }

        private static double Bayer(int x, int y)
        {
            int[,] matrix =
            {
                { 0, 32, 8, 40, 2, 34, 10, 42 },
                { 48, 16, 56, 24, 50, 18, 58, 26 },
                { 12, 44, 4, 36, 14, 46, 6, 38 },
                { 60, 28, 52, 20, 62, 30, 54, 22 },
                { 3, 35, 11, 43, 1, 33, 9, 41 },
                { 51, 19, 59, 27, 49, 17, 57, 25 },
                { 15, 47, 7, 39, 13, 45, 5, 37 },
                { 63, 31, 55, 23, 61, 29, 53, 21 },
            };
            return (matrix[y & 7, x & 7] + 0.5) / 64.0;
        }
    }
}
