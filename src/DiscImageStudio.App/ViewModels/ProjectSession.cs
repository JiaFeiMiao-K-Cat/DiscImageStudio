using System.Globalization;
using DiscImageStudio.Core;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>The two directions between an on-disk <see cref="ProjectDocument"/> and the
/// shared <see cref="DiscParametersState"/>. Opening fills State from the document; saving
/// reads State back. LivePreview/Burn/Calibration keep binding State unchanged.</summary>
internal static class ProjectSession
{
    public static void ApplyToState(ProjectDocument document, DiscParametersState state)
    {
        ProjectGeometry geometry = document.Geometry;
        ProjectOutputs outputs = document.Outputs;
        string inner = Format(geometry.InnerRadiusMm);
        string outer = Format(geometry.OuterRadiusMm);
        string sectors = geometry.Sectors.ToString(CultureInfo.InvariantCulture);

        ToneProfile tone = (document.Tone ?? ToneProfile.LegacyBinary).Normalized();
        if (document.Family is OpticalDiscFamily.Dvd or OpticalDiscFamily.BluRay
            && tone.Levels == ToneProfile.FourLevels)
        {
            tone = tone with { Levels = ToneProfile.TwoLevels };
        }

        state.ToneDitherStrength = tone.DitherStrength;
        state.ToneLevels = tone.Levels;
        state.ToneBrightness = tone.Brightness;
        state.ToneSrgbLinearization = tone.SrgbLinearization;

        switch (document.Family)
        {
            case OpticalDiscFamily.Dvd:
                state.DvdInnerRadius = inner;
                state.DvdOuterRadius = outer;
                state.DvdTotalSectors = sectors;
                state.DvdPitchLinear = Format(geometry.PitchLinear);
                state.DvdPitchQuadratic = Format(geometry.PitchQuadratic);
                state.DvdPitchCubic = Format(geometry.PitchCubic);
                // The design projection starts neutral: measured geometry follows the project
                // until the calibration workflow supplies a real disc's radii.
                state.DvdActualInnerRadius = inner;
                state.DvdActualOuterRadius = outer;
                state.DvdActualPitchLinear = Format(geometry.PitchLinear);
                state.DvdActualPitchQuadratic = Format(geometry.PitchQuadratic);
                state.DvdActualPitchCubic = Format(geometry.PitchCubic);
                state.DvdOutputPath = outputs.OutputPath;
                state.DvdDataDirectory = outputs.DataDirectory;
                state.DvdFilesAtOuterRing = outputs.FilesAtOuterRing;
                state.DvdVolumeLabel = outputs.VolumeLabel;
                state.DvdImagePath = string.Empty;
                break;
            case OpticalDiscFamily.BluRay:
                state.BdInnerRadius = inner;
                state.BdOuterRadius = outer;
                state.BdTotalSectors = sectors;
                state.BdActualInnerRadius = inner;
                state.BdActualOuterRadius = outer;
                state.BdOutputPath = outputs.OutputPath;
                state.BdDataDirectory = outputs.DataDirectory;
                state.BdFilesAtOuterRing = outputs.FilesAtOuterRing;
                state.BdVolumeLabel = outputs.VolumeLabel;
                state.BdInvertMapping = document.BluRay?.InvertMapping ?? false;
                state.BdDrawingStartSector = "0";
                state.BdDrawingEndSectorExclusive = "0";
                state.BdImagePath = string.Empty;
                break;
            default:
                state.CdInnerRadius = inner;
                state.CdOuterRadius = outer;
                state.CdSectors = sectors;
                state.CdActualInnerRadius = inner;
                state.CdActualOuterRadius = outer;
                state.CdOutputPath = outputs.OutputPath;
                state.CdWriteCue = outputs.WriteCue;
                state.CdInterleave = outputs.Interleave;
                state.CdImagePath = string.Empty;
                break;
        }
    }

    public static ProjectGeometry CaptureGeometry(ProjectDocument current, DiscParametersState state)
    {
        switch (current.Family)
        {
            case OpticalDiscFamily.Dvd:
                return new ProjectGeometry(
                    ParameterParser.ParsePositiveDouble(state.DvdInnerRadius, "DVD 内半径"),
                    ParameterParser.ParsePositiveDouble(state.DvdOuterRadius, "DVD 外半径"),
                    ParameterParser.ParsePositiveLong(state.DvdTotalSectors, "DVD 总扇区数"),
                    ParameterParser.ParseDouble(state.DvdPitchLinear, "DVD 相对一次项"),
                    ParameterParser.ParseDouble(state.DvdPitchQuadratic, "DVD 相对二次项"),
                    ParameterParser.ParseDouble(state.DvdPitchCubic, "DVD 相对三次项"));
            case OpticalDiscFamily.BluRay:
                return new ProjectGeometry(
                    ParameterParser.ParsePositiveDouble(state.BdInnerRadius, "蓝光内半径"),
                    ParameterParser.ParsePositiveDouble(state.BdOuterRadius, "蓝光外半径"),
                    ParameterParser.ParsePositiveLong(state.BdTotalSectors, "蓝光总扇区数"));
            default:
                return new ProjectGeometry(
                    ParameterParser.ParsePositiveDouble(state.CdInnerRadius, "CD 内半径"),
                    ParameterParser.ParsePositiveDouble(state.CdOuterRadius, "CD 外半径"),
                    ParameterParser.ParsePositiveLong(state.CdSectors, "CD 扇区数"));
        }
    }

    public static ProjectOutputs CaptureOutputs(ProjectDocument current, DiscParametersState state)
    {
        switch (current.Family)
        {
            case OpticalDiscFamily.Dvd:
                return new ProjectOutputs(
                    RequireOutput(state.DvdOutputPath, "DVD ISO 输出位置"),
                    WriteCue: false,
                    state.DvdDataDirectory,
                    state.DvdVolumeLabel,
                    Interleave: false,
                    FilesAtOuterRing: state.DvdFilesAtOuterRing);
            case OpticalDiscFamily.BluRay:
                return new ProjectOutputs(
                    RequireOutput(state.BdOutputPath, "蓝光 ISO 输出位置"),
                    WriteCue: false,
                    state.BdDataDirectory,
                    state.BdVolumeLabel,
                    Interleave: false,
                    FilesAtOuterRing: state.BdFilesAtOuterRing);
            default:
                return new ProjectOutputs(
                    RequireOutput(state.CdOutputPath, "CD 音轨输出位置"),
                    state.CdWriteCue,
                    DataDirectory: string.Empty,
                    VolumeLabel: string.Empty,
                    state.CdInterleave);
        }
    }

    /// <summary>Tone fields as the project stores them. DVD and BD have only the two
    /// dispersion textures, so a 4-level setting carried over from a CD project is stored as 2.</summary>
    public static ToneProfile CaptureTone(ProjectDocument current, DiscParametersState state)
    {
        ToneProfile tone = state.Tone.Normalized();
        return current.Family is OpticalDiscFamily.Dvd or OpticalDiscFamily.BluRay
            && tone.Levels == ToneProfile.FourLevels
            ? tone with { Levels = ToneProfile.TwoLevels }
            : tone;
    }

    /// <summary>BD-only mapping polarity. CD/DVD projects never carry this section.</summary>
    public static ProjectBluRaySettings? CaptureBluRay(ProjectDocument current, DiscParametersState state)
        => current.Family == OpticalDiscFamily.BluRay
            ? new ProjectBluRaySettings(state.BdInvertMapping)
            : null;

    public static string Format(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static string RequireOutput(string raw, string field)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException($"请填写{field}。") : value;
    }
}
