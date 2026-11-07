using DiscImageStudio.Core;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed record ImageMappingOptions(
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double ChannelBitLengthNm,
    double StartAngleDegrees,
    bool Clockwise,
    byte LuminanceThreshold,
    byte AlphaThreshold,
    int SampleEveryChannelBits = 1,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    ToneProfile? Tone = null,
    uint? UsableSectors = null,
    double? ImageOuterRadiusMm = null)
{
    public uint OutputSectors => UsableSectors ?? TotalSectors;
    public double SamplingOuterRadiusMm => ImageOuterRadiusMm ?? OuterRadiusMm;
}

public sealed record ImageMappingSummary(
    string? SourceImage,
    int ImageWidth,
    int ImageHeight,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double ChannelBitLengthNm,
    double StartAngleDegrees,
    string SpiralDirection,
    byte LuminanceThreshold,
    byte AlphaThreshold,
    int SampleEveryChannelBits,
    double TrackLengthMetres,
    double TrackPitchMicrometres,
    double TotalTurns,
    double BlockStartRadiusMm,
    double BlockEndRadiusMm,
    double BlockStartTrackTurns,
    double BlockEndTrackTurns,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    ToneProfile? Tone = null)
{
    public double PitchLinearMicrometres => TrackPitchMicrometres * PitchLinear;
    public double PitchQuadraticMicrometres => TrackPitchMicrometres * PitchQuadratic;
    public double PitchCubicMicrometres => TrackPitchMicrometres * PitchCubic;
}

internal static class ImageTargetMapper
{
    internal static ImageConstraint Map(
        string imagePath,
        uint initialLba,
        ImageMappingOptions options)
        => Map(RasterImage.Load(imagePath), initialLba, options, Path.GetFullPath(imagePath));

    internal static ImageConstraint Map(
        RasterImage image,
        uint initialLba,
        ImageMappingOptions options)
        => Map(image, initialLba, options, sourceImage: null);

    private static ImageConstraint Map(
        RasterImage image,
        uint initialLba,
        ImageMappingOptions options,
        string? sourceImage)
    {
        Validate(initialLba, options);
        double bitLengthMm = options.ChannelBitLengthNm * 1e-6;
        double trackLengthMm = options.TotalSectors
            * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * bitLengthMm;
        ArchimedeanSpiral spiral = ArchimedeanSpiral.Create(
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            trackLengthMm,
            options.PitchLinear,
            options.PitchQuadratic,
            options.PitchCubic);

        double startAngleRadians = options.StartAngleDegrees * Math.PI / 180.0;
        double direction = options.Clockwise ? -1.0 : 1.0;
        sbyte[] targets = new sbyte[DvdEccBlockEncoder.ChannelBitsPerBlock];
        Array.Fill(targets, ImageConstraint.Unconstrained);
        ulong firstGlobalBit = (ulong)initialLba * DvdEccBlockEncoder.ChannelBitsPerSector;
        PixelSample SampleAt(double relativeChannelBit, bool preserveDiscreteGrayscale = false)
        {
            double arcLengthMm = (firstGlobalBit + relativeChannelBit) * bitLengthMm;
            double radiusMm = spiral.RadiusAtArcLength(arcLengthMm);
            double trackAngle = spiral.AngleAtRadius(radiusMm);
            double polarAngle = startAngleRadians + (direction * trackAngle);
            return image.SampleDisc(
                radiusMm * Math.Cos(polarAngle),
                radiusMm * Math.Sin(polarAngle),
                options.SamplingOuterRadiusMm,
                preserveDiscreteGrayscale);
        }

        for (int bitIndex = 0; bitIndex < targets.Length; bitIndex += options.SampleEveryChannelBits)
        {
            PixelSample sample = SampleAt(bitIndex + 0.5);
            if (sample.Alpha >= options.AlphaThreshold)
            {
                targets[bitIndex] = DvdToneMapping.UseWhite(
                    sample.Luminance,
                    options.LuminanceThreshold,
                    options.Tone,
                    checked((long)(firstGlobalBit + (ulong)bitIndex)))
                    ? (sbyte)1
                    : (sbyte)0;
            }
        }

        sbyte[] channelWordTargets = new sbyte[DvdEccBlockEncoder.ChannelBitsPerBlock / 16];
        Array.Fill(channelWordTargets, ImageConstraint.Unconstrained);
        sbyte[] grayscaleTargets = new sbyte[channelWordTargets.Length];
        Array.Fill(grayscaleTargets, ImageConstraint.Unconstrained);
        for (int wordIndex = 0; wordIndex < channelWordTargets.Length; wordIndex++)
        {
            double wordCenter = (wordIndex * 16) + 8.0;
            PixelSample sample = SampleAt(wordCenter);
            if (sample.Alpha >= options.AlphaThreshold)
            {
                bool white = DvdToneMapping.UseWhite(
                    sample.Luminance,
                    options.LuminanceThreshold,
                    options.Tone,
                    checked((long)(firstGlobalBit + (ulong)(wordIndex * 16 + 8))));
                channelWordTargets[wordIndex] = white ? (sbyte)16 : (sbyte)0;
                PixelSample grayscaleSample = image.HasBinaryGrayscalePalette
                    ? SampleAt(wordCenter, preserveDiscreteGrayscale: true)
                    : sample;
                grayscaleTargets[wordIndex] = options.Tone is { IsLegacyBinary: false }
                    ? (sbyte)(white ? 1 : 0)
                    : (sbyte)BinaryGrayscale.GetLevelIndex(grayscaleSample.Luminance);
            }
        }

        double blockStartArc = firstGlobalBit * bitLengthMm;
        double blockEndArc = (firstGlobalBit + (ulong)targets.Length) * bitLengthMm;
        double blockStartRadius = spiral.RadiusAtArcLength(blockStartArc);
        double blockEndRadius = spiral.RadiusAtArcLength(blockEndArc);
        ImageMappingSummary summary = new(
            sourceImage,
            image.Width,
            image.Height,
            options.TotalSectors,
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            options.ChannelBitLengthNm,
            options.StartAngleDegrees,
            options.Clockwise ? "cw" : "ccw",
            options.LuminanceThreshold,
            options.AlphaThreshold,
            options.SampleEveryChannelBits,
            trackLengthMm / 1000.0,
            spiral.TrackPitchMm * 1000.0,
            spiral.TotalAngleRadians / (2 * Math.PI),
            blockStartRadius,
            blockEndRadius,
            spiral.AngleAtRadius(blockStartRadius) / (2 * Math.PI),
            spiral.AngleAtRadius(blockEndRadius) / (2 * Math.PI),
            options.PitchLinear,
            options.PitchQuadratic,
            options.PitchCubic,
            options.Tone);
        return ImageConstraint.Create(targets, summary, channelWordTargets, grayscaleTargets);
    }

    private static void Validate(uint initialLba, ImageMappingOptions options)
    {
        if (options.TotalSectors < DvdEccBlockEncoder.SectorCount
            || (ulong)initialLba + DvdEccBlockEncoder.SectorCount > options.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialLba),
                "The selected 16-sector ECC Block must fit inside the reported total sector count.");
        }

        if (!double.IsFinite(options.ChannelBitLengthNm) || options.ChannelBitLengthNm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Channel-bit length must be positive.");
        }

        if (!double.IsFinite(options.StartAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Start angle must be finite.");
        }

        if (options.AlphaThreshold == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Alpha threshold must be between 1 and 255.");
        }

        if (options.SampleEveryChannelBits < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image constraint step must be positive.");
        }
    }
}
