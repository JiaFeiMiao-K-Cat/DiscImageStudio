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
public static class GratingPaletteCommand
{
    internal static int Run(CliArguments args)
    {
        string imagePath = args.Require("image");
        string outputPath = args.Require("output");
        Write(
            imagePath,
            outputPath,
            args.Get("volume-label", "SUZUME-GRATING"),
            args.GetUInt("total-sectors", 2_297_888),
            args.GetDouble("inner-radius-mm", 24.063112765327595),
            args.GetDouble("outer-radius-mm", 58.22734793558852),
            args.GetDouble("cell-mm", 0.068),
            args.GetDouble("pitch-linear", -3.625995787598983e-11),
            args.GetDouble("pitch-quadratic", 5.59609470367389e-10),
            args.GetDouble("pitch-cubic", 4.337809583594884e-10));
        return 0;
    }

    /// <summary>Writes a complete ISO to a file, or to stdout when outputPath is "-".</summary>
    public static void Write(
        string imagePath,
        string outputPath,
        string volumeLabel,
        uint totalSectors,
        double inner,
        double outer,
        double cellMm,
        double pitchLinear,
        double pitchQuadratic,
        double pitchCubic)
    {
        if (totalSectors == 0 || totalSectors % 16 != 0)
            throw new ArgumentException("total-sectors must be a positive multiple of 16.");
        if (cellMm <= 0) throw new ArgumentException("cell-mm must be positive.");
        if (totalSectors > 4_000_000) throw new ArgumentException("total-sectors is too large.");

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

        FileStream? file = outputPath == "-" ? null : File.Create(outputPath);
        Stream output = file ?? Console.OpenStandardOutput();
        try
        {
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
            if (file is not null) file.SetLength((long)totalSectors * 2048);
            output.Flush();
        }
        finally
        {
            if (file is not null) file.Dispose();
        }
        Console.Error.WriteLine(
            $"done: {(outputPath == "-" ? "stdout" : outputPath)}, sectors={totalSectors}, "
            + $"drawingStart={drawingStart}, cell={cellMm * 1000:F1}um, palette={DvdGratingPalette.Motifs.Length}");
    }

    private static uint Align16(uint value) => (value + 15u) & ~15u;

    private sealed class CellPlan
    {
        private readonly int _cols;
        private readonly int _rows;
        private readonly double _origin;
        private readonly double _cell;
        private readonly byte[] _coverage;
        private readonly byte[] _motif;

        internal CellPlan(RasterImage image, double inner, double outer, double cellMm)
        {
            _cell = cellMm;
            _origin = -outer;
            _cols = (int)Math.Ceiling((2 * outer) / cellMm);
            _rows = _cols;
            _coverage = new byte[checked(_cols * _rows)];
            _motif = new byte[checked(_cols * _rows)];
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
                    int index = (row * _cols) + col;
                    _coverage[index] = (byte)Math.Clamp((int)Math.Round(luminance * 255), 0, 255);
                    // 1 = white (92), 2..6 = B/G/C/D/E. Black A5 is the off state.
                    _motif[index] = paletteIndex < 0 ? (byte)1 : (byte)(paletteIndex + 1);
                }
            }
        }

        internal byte Sample(DvdCodewordSample sample)
        {
            int col = (int)Math.Floor((sample.Xmm - _origin) / _cell);
            // The callback uses y-down (SamplePoint passes -yMm); the cell plan
            // and RasterImage.SampleDisc use mathematical y-up.
            int row = (int)Math.Floor(((-sample.Ymm) - _origin) / _cell);
            if (col < 0 || col >= _cols || row < 0 || row >= _rows) return 0;
            int index = (row * _cols) + col;
            byte coverage = _coverage[index];
            if (coverage == 0) return 0xA5;
            // Dither at payload-word resolution (~2.1 µm), much finer than the
            // 68 µm colour cell. The cell still determines hue and mean brightness.
            double threshold = WordDither(sample.GlobalCodewordIndex);
            if (threshold >= coverage / 255.0) return 0xA5;  // black motif
            byte value = _motif[index];
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

        private static double WordDither(ulong index)
        {
            ulong value = index * 0x9E3779B97F4A7C15UL;
            value ^= value >> 29;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 32;
            return (value & 0xFFFFFF) / 16777216.0;
        }
    }
}
