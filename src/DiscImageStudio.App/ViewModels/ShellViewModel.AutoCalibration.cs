using System;
using System.Globalization;
using System.Threading.Tasks;
using DiscImageStudio.AutoCalibration;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

/// <summary>Auto-calibration wizard wiring, kept in its own partial so the feature stays bounded to
/// this file. The main window installs <see cref="ShowAutoWizardHandler"/> which opens the modal
/// wizard; its measured geometry and three editable calibration sessions are then applied to the
/// fresh project.</summary>
public partial class ShellViewModel
{
    internal Func<ProjectDialogResult, AutoCalibrationWizardResult?>? ShowAutoWizardHandler { get; set; }

    internal async Task LaunchAutoCalibrationAsync(ProjectDialogResult project)
    {
        if (ShowAutoWizardHandler is null || CurrentProject is null || ProjectFilePath is null)
        {
            return;
        }

        AutoCalibrationWizardResult? calibrated = ShowAutoWizardHandler(project);
        if (calibrated is null)
        {
            return;
        }

        _isApplyingProject = true;
        try
        {
            ApplyAutoCalibrationGeometry(calibrated.Geometry);
            Calibration.ApplyAutoCalibrationSessions(calibrated.Sessions);
        }
        finally
        {
            _isApplyingProject = false;
        }

        await SaveToAsync(ProjectFilePath);
        ShowToast("自动标定已应用", L.T("测量出的几何参数已写入工程。"), ToastKind.Success);
    }

    private void ApplyAutoCalibrationGeometry(AutoCalibrationResult cal)
    {
        OpticalDiscFamily family = CurrentProject?.Family ?? OpticalDiscFamily.Dvd;
        string sectors = cal.Sectors.ToString(CultureInfo.InvariantCulture);
        switch (family)
        {
            case OpticalDiscFamily.Dvd:
                State.DvdInnerRadius = ProjectSession.Format(cal.InnerRadiusMm);
                State.DvdOuterRadius = ProjectSession.Format(cal.OuterRadiusMm);
                State.DvdTotalSectors = sectors;
                State.DvdPitchLinear = ProjectSession.Format(cal.PitchLinear);
                State.DvdPitchQuadratic = ProjectSession.Format(cal.PitchQuadratic);
                State.DvdPitchCubic = ProjectSession.Format(cal.PitchCubic);
                break;
            case OpticalDiscFamily.BluRay:
                State.BdInnerRadius = ProjectSession.Format(cal.InnerRadiusMm);
                State.BdOuterRadius = ProjectSession.Format(cal.OuterRadiusMm);
                State.BdTotalSectors = sectors;
                State.BdPitchLinear = ProjectSession.Format(cal.PitchLinear);
                State.BdPitchQuadratic = ProjectSession.Format(cal.PitchQuadratic);
                State.BdPitchCubic = ProjectSession.Format(cal.PitchCubic);
                break;
            default:
                State.CdInnerRadius = ProjectSession.Format(cal.InnerRadiusMm);
                State.CdOuterRadius = ProjectSession.Format(cal.OuterRadiusMm);
                State.CdSectors = sectors;
                State.CdPitchLinear = ProjectSession.Format(cal.PitchLinear);
                State.CdPitchQuadratic = ProjectSession.Format(cal.PitchQuadratic);
                State.CdPitchCubic = ProjectSession.Format(cal.PitchCubic);
                break;
        }
        State.SynchronizePreviewGeometry(family);
    }
}