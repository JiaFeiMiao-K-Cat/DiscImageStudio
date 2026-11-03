using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;

namespace DiscImageStudio.Linux.Services.Calibration;

/// <summary>Builds calibration targets from a project's geometry and writes the target disc
/// file with its .calibration.json record, using the same generators and mappings as the
/// Windows calibration page, so a disc burned from either app is read back the same way.</summary>
public static class CalibrationGenerator
{
    public static CalibrationTarget CreateTarget(DiscSettings settings)
    {
        CalibrationParameters parameters = settings.Family switch
        {
            OpticalDiscFamily.Dvd => new CalibrationParameters(CalibrationDiscKind.Dvd, settings.InnerRadiusMm, settings.OuterRadiusMm,
                settings.Sectors, DvdStreamingOptions.StandardChannelBitLengthNm,
                settings.PitchLinear, settings.PitchQuadratic, settings.PitchCubic),
            OpticalDiscFamily.BluRay => BluRayParameters(settings),
            _ => new CalibrationParameters(CalibrationDiscKind.Cd, settings.InnerRadiusMm, settings.OuterRadiusMm,
                settings.Sectors, CdDiscParameters.StandardLinearVelocityMmPerSecond),
        };
        Validate(parameters);
        return CalibrationTarget.Create(parameters);
    }

    public static OpticalDiscFamily Family(CalibrationDiscKind kind) => kind switch
    {
        CalibrationDiscKind.Dvd => OpticalDiscFamily.Dvd,
        CalibrationDiscKind.BluRay => OpticalDiscFamily.BluRay,
        _ => OpticalDiscFamily.CompactDisc,
    };

    /// <summary>Checks the parameters against the generator that will write them.</summary>
    public static void Validate(CalibrationParameters p)
    {
        p.Validate();
        switch (p.Kind)
        {
            case CalibrationDiscKind.Dvd:
                DvdOptions(p).Validate();
                break;
            case CalibrationDiscKind.BluRay:
                BluRayDisc(p);
                break;
            default:
                CdParameters(p).Validate();
                break;
        }
    }

    public static string DefaultExtension(CalibrationDiscKind kind) => kind == CalibrationDiscKind.Cd ? ".wav" : ".iso";

    public static string SessionPathFor(string outputPath) => Path.ChangeExtension(outputPath, ".calibration.json");

    /// <summary>Writes the target and its record. Files are produced in a staging folder next to
    /// the destination (so the CUE sheet names the final WAV) and moved into place at the end.</summary>
    public static void Generate(CalibrationSession session, string outputPath, Action<double> progress, CancellationToken cancellationToken)
    {
        CalibrationTarget target = session.Target;
        string path = Path.GetFullPath(outputPath);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        bool image = target.Parameters.Kind is CalibrationDiscKind.Dvd or CalibrationDiscKind.BluRay;
        if (image ? extension != ".iso" : extension is not (".wav" or ".raw"))
        {
            throw new ArgumentException(image
                ? "DVD and BD-R calibration targets are written as .iso files."
                : "CD calibration targets are written as .wav (with a CUE sheet) or .raw tracks.");
        }

        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string staging = Path.Combine(directory, $".calibration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string staged = Path.Combine(staging, Path.GetFileName(path));
            switch (target.Parameters.Kind)
            {
                case CalibrationDiscKind.Cd:
                    CdTrackGenerator.GeneratePattern(target.Sample, staged, CdParameters(target.Parameters), session.CdInterleave,
                        value => progress(value.Fraction), cancellationToken);
                    break;
                case CalibrationDiscKind.BluRay:
                {
                    BluRayDiscParameters disc = BluRayDisc(target.Parameters);
                    using FileStream output = new(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    BluRayStreamingGenerator.GenerateToStream(output, new BluRayStreamingOptions(disc, 0, disc.TotalSectors),
                        BluRayPatternMapping(target), new InlineProgress<BluRayStreamingProgress>(value => progress(value.Fraction)),
                        cancellationToken);
                    break;
                }

                default:
                {
                    using FileStream output = new(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    DvdStreamingGenerator.GeneratePattern(target.Sample, output, DvdOptions(target.Parameters),
                        value => progress(value.TotalBytes == 0 ? 0 : (double)value.CompletedBytes / value.TotalBytes), cancellationToken);
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            CalibrationSessionJson.Save(SessionPathFor(staged), session);
            foreach (string file in Directory.GetFiles(staging))
            {
                File.Move(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public static CdDiscParameters CdParameters(CalibrationParameters p)
        => new(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.LinearDensity, 0, p.OuterRadiusMm);

    public static DvdStreamingOptions DvdOptions(CalibrationParameters p)
        => new(checked((uint)p.Sectors), p.InnerRadiusMm, p.OuterRadiusMm, p.LinearDensity, 0,
            PitchLinear: p.PitchLinear, PitchQuadratic: p.PitchQuadratic, PitchCubic: p.PitchCubic);

    public static BluRayDiscParameters BluRayDisc(CalibrationParameters p)
    {
        if (p.Kind != CalibrationDiscKind.BluRay)
        {
            throw new ArgumentException("The calibration parameters do not describe a Blu-ray target.");
        }

        BluRayDiscParameters disc = new(checked((uint)p.Sectors), 1, p.InnerRadiusMm, p.OuterRadiusMm);
        disc.Validate();
        return disc;
    }

    /// <summary>Full-disc payload: the pattern inside the saved drawing range, zeros outside.</summary>
    public static BluRayPatternRangeMapping BluRayPatternMapping(CalibrationTarget target)
    {
        BluRayDiscParameters disc = BluRayDisc(target.Parameters);
        uint start = checked((uint)target.Parameters.DrawingStartSector);
        uint end = target.Parameters.DrawingEndSectorExclusive == 0
            ? disc.TotalSectors
            : checked((uint)target.Parameters.DrawingEndSectorExclusive);
        return new BluRayPatternRangeMapping(disc, target.Sample, start, end, target.Parameters.BluRayInvertMapping,
            allowParallelSampling: true);
    }

    private static CalibrationParameters BluRayParameters(DiscSettings settings)
    {
        if (settings.Sectors <= 0 || settings.Sectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The total sector count exceeds the Blu-ray engine's range.");
        }

        BluRayDiscParameters disc = new((uint)settings.Sectors, 1, settings.InnerRadiusMm, settings.OuterRadiusMm);
        disc.Validate();
        return new CalibrationParameters(CalibrationDiscKind.BluRay, disc.InnerRadiusMm, disc.OuterRadiusMm, disc.TotalSectors,
            BluRayDiscParameters.ChannelBitLengthNm, BluRayInvertMapping: settings.InvertMapping,
            DrawingStartSector: 0, DrawingEndSectorExclusive: disc.TotalSectors);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
