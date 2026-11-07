using DiscImageStudio.ImageIO;
using DiscImageStudio.Core;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed record CalibrationPreviewOptions(
    uint TotalSectors,
    uint StartLba,
    uint FillSectors,
    double GeneratedInnerRadiusMm,
    double GeneratedOuterRadiusMm,
    double ActualInnerRadiusMm,
    double ActualOuterRadiusMm,
    double ChannelBitLengthNm,
    bool Clockwise,
    byte LuminanceThreshold,
    byte AlphaThreshold,
    int PreviewSize,
    int SamplesPerSector,
    double GeneratedPitchLinear = 0,
    double GeneratedPitchQuadratic = 0,
    double GeneratedPitchCubic = 0,
    double ActualPitchLinear = 0,
    double ActualPitchQuadratic = 0,
    double ActualPitchCubic = 0,
    ToneProfile? Tone = null,
    double? SourceImageOuterRadiusMm = null,
    double? PreviewOuterRadiusMm = null);

public sealed record CalibrationPreviewSummary(
    string SourceImage,
    string OutputImage,
    int SourceWidth,
    int SourceHeight,
    int PreviewSize,
    uint TotalSectors,
    uint StartLba,
    uint FillSectors,
    double ChannelBitLengthNm,
    double TrackLengthMetres,
    double GeneratedInnerRadiusMm,
    double GeneratedOuterRadiusMm,
    double GeneratedTrackPitchMicrometres,
    double GeneratedTurns,
    double ActualInnerRadiusMm,
    double ActualOuterRadiusMm,
    double ActualTrackPitchMicrometres,
    double ActualTurns,
    string SpiralDirection,
    byte LuminanceThreshold,
    byte AlphaThreshold,
    int SamplesPerSector,
    string MappingMode,
    double GeneratedPitchLinear = 0,
    double GeneratedPitchQuadratic = 0,
    double GeneratedPitchCubic = 0,
    double ActualPitchLinear = 0,
    double ActualPitchQuadratic = 0,
    double ActualPitchCubic = 0,
    ToneProfile? Tone = null)
{
    public double GeneratedPitchLinearMicrometres => GeneratedTrackPitchMicrometres * GeneratedPitchLinear;
    public double GeneratedPitchQuadraticMicrometres => GeneratedTrackPitchMicrometres * GeneratedPitchQuadratic;
    public double GeneratedPitchCubicMicrometres => GeneratedTrackPitchMicrometres * GeneratedPitchCubic;
    public double ActualPitchLinearMicrometres => ActualTrackPitchMicrometres * ActualPitchLinear;
    public double ActualPitchQuadraticMicrometres => ActualTrackPitchMicrometres * ActualPitchQuadratic;
    public double ActualPitchCubicMicrometres => ActualTrackPitchMicrometres * ActualPitchCubic;
}

public static class CalibrationPreviewRenderer
{
    public static CalibrationPreviewSummary Render(
        string imagePath,
        string outputPath,
        CalibrationPreviewOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(options);
        string sourceFullPath = Path.GetFullPath(imagePath);
        string outputFullPath = Path.GetFullPath(outputPath);
        RasterImage source = RasterImage.Load(sourceFullPath);
        double trackLengthMm = options.TotalSectors
            * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * options.ChannelBitLengthNm
            * 1e-6;
        ArchimedeanSpiral generated = ArchimedeanSpiral.Create(
            options.GeneratedInnerRadiusMm,
            options.GeneratedOuterRadiusMm,
            trackLengthMm,
            options.GeneratedPitchLinear,
            options.GeneratedPitchQuadratic,
            options.GeneratedPitchCubic);
        ArchimedeanSpiral actual = ArchimedeanSpiral.Create(
            options.ActualInnerRadiusMm,
            options.ActualOuterRadiusMm,
            trackLengthMm,
            options.ActualPitchLinear,
            options.ActualPitchQuadratic,
            options.ActualPitchCubic);

        int size = options.PreviewSize;
        int stride = checked(size * 4);
        byte[] pixels = new byte[checked(stride * size)];
        int[] weightedLevelSums = new int[checked(size * size)];
        int[] alphaSums = new int[checked(size * size)];
        double centre = (size - 1) / 2.0;
        double previewOuterRadius = options.PreviewOuterRadiusMm ?? options.ActualOuterRadiusMm;
        double millimetresPerPixel = (2 * previewOuterRadius) / (size - 1);
        double direction = options.Clockwise ? -1.0 : 1.0;
        const double fullTurn = 2 * Math.PI;
        double visibleStartMm = options.StartLba
            * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * options.ChannelBitLengthNm
            * 1e-6;
        double visibleEndMm = (options.StartLba + (double)options.FillSectors)
            * DvdEccBlockEncoder.ChannelBitsPerSector
            * options.ChannelBitLengthNm
            * 1e-6;
        double visibleStartRadiusMm = actual.RadiusAtArcLengthFast(visibleStartMm);
        double visibleEndRadiusMm = actual.RadiusAtArcLengthFast(visibleEndMm);

        // As in the CD preview, walk forward through the logical track: sample the
        // source at the generated position, then splat that same channel position at
        // the guessed actual geometry. Multiple track samples landing in one preview
        // pixel are averaged instead of allowing the last writer to win.
        long totalSamples = checked((long)options.FillSectors * options.SamplesPerSector);
        double channelBitsPerSample = DvdEccBlockEncoder.ChannelBitsPerSector
            / (double)options.SamplesPerSector;
        double firstGlobalChannelBit = options.StartLba
            * (double)DvdEccBlockEncoder.ChannelBitsPerSector;
        int workerCount = Environment.ProcessorCount;
        ParallelOptions parallelOptions = new() { CancellationToken = cancellationToken };
        Parallel.For(0, workerCount, parallelOptions, worker =>
        {
            for (long sampleIndex = worker, iteration = 0;
                 sampleIndex < totalSamples;
                 sampleIndex += workerCount, iteration++)
            {
                if ((iteration & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                double globalChannelBit = firstGlobalChannelBit
                    + ((sampleIndex + 0.5) * channelBitsPerSample);
                double arcLengthMm = globalChannelBit * options.ChannelBitLengthNm * 1e-6;
                (double generatedRadiusMm, double generatedTrackAngle) = generated.AtArcLengthFast(arcLengthMm);
                double generatedPolarAngle = direction * generatedTrackAngle;
                PixelSample sample = source.SampleDisc(
                    generatedRadiusMm * Math.Cos(generatedPolarAngle),
                    generatedRadiusMm * Math.Sin(generatedPolarAngle),
                    options.SourceImageOuterRadiusMm ?? generated.OuterRadiusMm,
                    preserveDiscreteGrayscale: true);
                if (sample.Alpha < options.AlphaThreshold)
                {
                    continue;
                }

                (double actualRadiusMm, double actualTrackAngle) = actual.AtArcLengthFast(arcLengthMm);
                double actualPolarAngle = direction * actualTrackAngle;
                double pixelRadius = centre * actualRadiusMm / previewOuterRadius;
                int pixelX = (int)Math.Round(
                    centre + (pixelRadius * Math.Cos(actualPolarAngle)));
                int pixelY = (int)Math.Round(
                    centre - (pixelRadius * Math.Sin(actualPolarAngle)));
                if ((uint)pixelX >= (uint)size || (uint)pixelY >= (uint)size)
                {
                    continue;
                }

                int pixelIndex = (pixelY * size) + pixelX;
                int binaryLevel = DvdToneMapping.UseWhite(
                    sample.Luminance,
                    options.LuminanceThreshold,
                    options.Tone,
                    (long)(globalChannelBit / 16.0))
                    ? 255
                    : 0;
                Interlocked.Add(
                    ref weightedLevelSums[pixelIndex],
                    binaryLevel * sample.Alpha);
                Interlocked.Add(ref alphaSums[pixelIndex], sample.Alpha);
            }
        });

        cancellationToken.ThrowIfCancellationRequested();
        Parallel.For(0, size, parallelOptions, pixelY =>
        {
            double yMm = (centre - pixelY) * millimetresPerPixel;
            int rowOffset = pixelY * stride;
            for (int pixelX = 0; pixelX < size; pixelX++)
            {
                double xMm = (pixelX - centre) * millimetresPerPixel;
                double radiusMm = Math.Sqrt((xMm * xMm) + (yMm * yMm));
                if (radiusMm < visibleStartRadiusMm || radiusMm > visibleEndRadiusMm)
                {
                    continue;
                }

                int pixelIndex = (pixelY * size) + pixelX;
                int alphaSum = alphaSums[pixelIndex];
                byte level = alphaSum == 0
                    ? (byte)255
                    : (byte)Math.Clamp(
                        (weightedLevelSums[pixelIndex] + (alphaSum / 2)) / alphaSum,
                        0,
                        255);
                int offset = rowOffset + (pixelX * 4);
                pixels[offset] = level;
                pixels[offset + 1] = level;
                pixels[offset + 2] = level;
                pixels[offset + 3] = 255;
            }
        });

        cancellationToken.ThrowIfCancellationRequested();
        WritePng(outputFullPath, size, pixels);
        return new CalibrationPreviewSummary(
            sourceFullPath,
            outputFullPath,
            source.Width,
            source.Height,
            size,
            options.TotalSectors,
            options.StartLba,
            options.FillSectors,
            options.ChannelBitLengthNm,
            trackLengthMm / 1000.0,
            generated.InnerRadiusMm,
            generated.OuterRadiusMm,
            generated.TrackPitchMm * 1000.0,
            generated.TotalAngleRadians / fullTurn,
            actual.InnerRadiusMm,
            actual.OuterRadiusMm,
            actual.TrackPitchMm * 1000.0,
            actual.TotalAngleRadians / fullTurn,
            options.Clockwise ? "cw" : "ccw",
            options.LuminanceThreshold,
            options.AlphaThreshold,
            options.SamplesPerSector,
            "forward-channel-splat-average",
            options.GeneratedPitchLinear,
            options.GeneratedPitchQuadratic,
            options.GeneratedPitchCubic,
            options.ActualPitchLinear,
            options.ActualPitchQuadratic,
            options.ActualPitchCubic,
            options.Tone);
    }

    private static void WritePng(string outputPath, int size, byte[] pixels)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        RasterCodec.SavePng(outputPath, size, size, pixels, grayscale: false);
    }

    private static void Validate(CalibrationPreviewOptions options)
    {
        if (options.TotalSectors == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Total sector count must be positive.");
        }

        if (options.FillSectors == 0
            || (ulong)options.StartLba + options.FillSectors > options.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Preview sector range exceeds the disc size.");
        }

        if (!double.IsFinite(options.ChannelBitLengthNm) || options.ChannelBitLengthNm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Channel-bit length must be positive.");
        }

        if (options.PreviewSize is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Preview size must be 64..8192 pixels.");
        }

        if (options.SamplesPerSector is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Preview samples per sector must be 1..4096.");
        }

        if (options.AlphaThreshold == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Alpha threshold must be 1..255.");
        }
    }
}
