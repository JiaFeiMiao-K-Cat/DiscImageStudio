using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;

namespace DiscImageStudio.Linux.Services;

/// <summary>Immutable snapshot of everything generation and previews read from a project,
/// taken on the UI thread and handed to background work.</summary>
public sealed record DiscSettings(
    OpticalDiscFamily Family,
    double InnerRadiusMm,
    double OuterRadiusMm,
    long Sectors,
    double PitchLinear,
    double PitchQuadratic,
    double PitchCubic,
    ToneProfile Tone,
    bool InvertMapping,
    string DataDirectory,
    string VolumeLabel,
    bool FilesAtOuterRing,
    bool Interleave,
    bool WriteCue)
{
    public bool HasFiles => !string.IsNullOrWhiteSpace(DataDirectory);

    /// <summary>CD composes against the scheme's fixed image radius; DVD and BD against the
    /// generated outer radius, as on Windows.</summary>
    public double CanvasOuterRadiusMm => Family == OpticalDiscFamily.CompactDisc
        ? CdDiscParameters.StandardImageOuterRadiusMm
        : OuterRadiusMm;

    /// <summary>DVD and BD dispersion are binary; only CD offers four levels.</summary>
    public ToneProfile EffectiveTone => Family == OpticalDiscFamily.CompactDisc
        ? Tone
        : Tone with { Levels = ToneProfile.TwoLevels };

    public string DefaultVolumeLabel => Family switch
    {
        OpticalDiscFamily.Dvd => "DISC_IMAGE",
        OpticalDiscFamily.BluRay => "BD_IMAGE",
        _ => "CD_IMAGE",
    };

    public string EffectiveVolumeLabel => string.IsNullOrWhiteSpace(VolumeLabel) ? DefaultVolumeLabel : VolumeLabel.Trim();

    public CdDiscParameters ToCd(double? innerOverride = null, double? outerOverride = null)
    {
        CdDiscParameters parameters = new(innerOverride ?? InnerRadiusMm, outerOverride ?? OuterRadiusMm, Sectors);
        parameters.Validate();
        return parameters;
    }

    public DvdStreamingOptions ToDvd()
    {
        if (Sectors <= 0 || Sectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Sectors), "DVD total sectors are out of range.");
        }

        DvdStreamingOptions options = new(
            (uint)Sectors, InnerRadiusMm, OuterRadiusMm,
            PitchLinear: PitchLinear, PitchQuadratic: PitchQuadratic, PitchCubic: PitchCubic,
            Tone: EffectiveTone);
        options.Validate();
        return options;
    }

    public BluRayDiscParameters ToBluRay(double? innerOverride = null, double? outerOverride = null)
    {
        if (Sectors <= 0 || Sectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Sectors), "BD total sectors are out of range.");
        }

        BluRayDiscParameters parameters = new((uint)Sectors, 1, innerOverride ?? InnerRadiusMm, outerOverride ?? OuterRadiusMm);
        parameters.Validate();
        return parameters;
    }

    /// <summary>The engine command line that generates this disc, identical to the one the
    /// Windows export page builds (and therefore to what the CLI accepts).</summary>
    public string[] BuildGenerateCommand(string compositePath, string outputPath)
    {
        ToneProfile tone = EffectiveTone;
        List<string> arguments;
        switch (Family)
        {
            case OpticalDiscFamily.Dvd:
                arguments =
                [
                    "solve",
                    "--image", compositePath,
                    "--iso-output", outputPath,
                    "--total-sectors", Num.Format(Sectors),
                    "--inner-radius-mm", Num.Format(InnerRadiusMm),
                    "--outer-radius-mm", Num.Format(OuterRadiusMm),
                    "--pitch-linear", Num.Format(PitchLinear),
                    "--pitch-quadratic", Num.Format(PitchQuadratic),
                    "--pitch-cubic", Num.Format(PitchCubic),
                    "--spiral-direction", "cw",
                    "--image-threshold", "128",
                    "--alpha-threshold", "1",
                    "--tone-dither", Num.Format(tone.DitherStrength),
                    "--tone-levels", "2",
                    "--tone-brightness", Num.Format(tone.Brightness),
                    "--tone-gamma", Num.Bool(tone.SrgbLinearization),
                    "--algorithm", "dispersion",
                    "--iterations-per-block", "1",
                    "--fast-output", "true",
                    "--volume-label", EffectiveVolumeLabel,
                ];
                if (HasFiles)
                {
                    arguments.AddRange(["--data-dir", DataDirectory.Trim(), "--file-position", FilesAtOuterRing ? "outer" : "inner"]);
                }
                else
                {
                    arguments.AddRange(["--fill-sectors", Num.Format(Sectors)]);
                }

                break;

            case OpticalDiscFamily.BluRay:
                arguments =
                [
                    "bd-generate",
                    "--input", compositePath,
                    "--output", outputPath,
                    "--total-sectors", Num.Format(Sectors),
                    "--r0", Num.Format(InnerRadiusMm),
                    "--r1", Num.Format(OuterRadiusMm),
                    "--invert", Num.Bool(InvertMapping),
                    "--volume-label", EffectiveVolumeLabel,
                    "--tone-dither", Num.Format(tone.DitherStrength),
                    "--tone-levels", "2",
                    "--tone-brightness", Num.Format(tone.Brightness),
                    "--tone-gamma", Num.Bool(tone.SrgbLinearization),
                ];
                if (HasFiles)
                {
                    arguments.AddRange(["--data-dir", DataDirectory.Trim(), "--file-position", FilesAtOuterRing ? "outer" : "inner"]);
                }

                break;

            default:
                arguments =
                [
                    "cd-generate",
                    "--input", compositePath,
                    "--output", outputPath,
                    "--r0", Num.Format(InnerRadiusMm),
                    "--r1", Num.Format(OuterRadiusMm),
                    "--sectors", Num.Format(Sectors),
                    "--tone-dither", Num.Format(tone.DitherStrength),
                    "--tone-levels", Num.Format((long)tone.Levels),
                    "--tone-brightness", Num.Format(tone.Brightness),
                    "--tone-gamma", Num.Bool(tone.SrgbLinearization),
                ];
                if (HasFiles)
                {
                    arguments.AddRange(
                    [
                        "--data-dir", DataDirectory.Trim(),
                        "--volume-label", EffectiveVolumeLabel,
                        "--file-position", FilesAtOuterRing ? "outer" : "inner",
                    ]);
                }
                else
                {
                    arguments.AddRange(["--interleave", Num.Bool(Interleave), "--cue", Num.Bool(WriteCue)]);
                }

                break;
        }

        return arguments.ToArray();
    }
}
