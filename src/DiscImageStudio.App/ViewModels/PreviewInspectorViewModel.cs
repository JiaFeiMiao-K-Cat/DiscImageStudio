using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Core;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// The preview workspace's parameter panel, projected over whichever disc family the family
/// switch has selected. CD and DVD keep separate parameter sets in
/// <see cref="DiscParametersState"/> — they are different physical models — but the panel is one
/// set of rows that re-points when the family changes, instead of a second copy of itself. The
/// projections are read/write where the preview owns the value (measured geometry, preview
/// precision), so an edit lands on the family that is currently on screen.
/// </summary>
public class PreviewInspectorViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private OpticalDiscFamily _family = OpticalDiscFamily.CompactDisc;

    public PreviewInspectorViewModel(ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        _state.PropertyChanged += State_PropertyChanged;
    }

    /// <summary>The shared parameters, for the rows the panel edits directly (interleave).</summary>
    public DiscParametersState State => _state;

    public bool IsCd => _family == OpticalDiscFamily.CompactDisc;

    public bool IsCdAudioArtifact => IsCd && !Path.GetExtension(_state.CdTrackPath.Trim())
        .Equals(".iso", StringComparison.OrdinalIgnoreCase);

    public bool IsDvd => _family == OpticalDiscFamily.Dvd;

    public bool IsBluRay => _family == OpticalDiscFamily.BluRay;

    /// <summary>BD payload positions are sampled directly, so there is no per-sector knob.</summary>
    public bool HasSamplesPerSector => !IsBluRay;

    /// <summary>Re-points every row at the given family. Called by the preview view model when
    /// its family switch changes.</summary>
    internal void SetFamily(OpticalDiscFamily family)
    {
        if (_family == family)
        {
            return;
        }

        _family = family;
        foreach (string name in ProjectedProperties)
        {
            OnPropertyChanged(name);
        }
    }

    // ---- Read-back mode: the generated artifact is the input ----

    public string ArtifactLabel => _family switch
    {
        OpticalDiscFamily.Dvd => "生成的 DVD ISO",
        OpticalDiscFamily.BluRay => "生成的蓝光 ISO",
        _ => "生成的 CD 音轨或 ISO",
    };

    public string ArtifactHint => _family switch
    {
        OpticalDiscFamily.Dvd => "可直接粘贴或浏览已生成的 ISO 镜像。",
        OpticalDiscFamily.BluRay => "可直接粘贴或浏览已生成的 ISO；同名 sidecar 会带回几何与影调。",
        _ => "可选择 WAV / RAW 音轨或 CD 数据盘 ISO。",
    };

    public ICommand BrowseArtifactCommand => _family switch
    {
        OpticalDiscFamily.Dvd => _shell.BrowseDvdIsoCommand,
        OpticalDiscFamily.BluRay => _shell.BrowseBluRayIsoCommand,
        _ => _shell.BrowseCdTrackCommand,
    };

    public string ArtifactPath
    {
        get => _family switch
        {
            OpticalDiscFamily.Dvd => _state.DvdIsoPath,
            OpticalDiscFamily.BluRay => _state.BdIsoPath,
            _ => _state.CdTrackPath,
        };
        set
        {
            switch (_family)
            {
                case OpticalDiscFamily.Dvd:
                    _state.DvdIsoPath = value;
                    break;
                case OpticalDiscFamily.BluRay:
                    _state.BdIsoPath = value;
                    break;
                default:
                    _state.CdTrackPath = value;
                    break;
            }
        }
    }

    // ---- Generation geometry: owned by the project settings dialog ----

    public ICommand EditGeometryCommand => _shell.EditProjectCommand;

    // ---- Measured geometry: the preview's own calibration values ----

    public string MeasuredInnerRadius
    {
        get => _family switch
        {
            OpticalDiscFamily.Dvd => _state.DvdActualInnerRadius,
            OpticalDiscFamily.BluRay => _state.BdActualInnerRadius,
            _ => _state.CdActualInnerRadius,
        };
        set
        {
            switch (_family)
            {
                case OpticalDiscFamily.Dvd:
                    _state.DvdActualInnerRadius = value;
                    break;
                case OpticalDiscFamily.BluRay:
                    _state.BdActualInnerRadius = value;
                    break;
                default:
                    _state.CdActualInnerRadius = value;
                    break;
            }
        }
    }

    public string MeasuredOuterRadius
    {
        get => _family switch
        {
            OpticalDiscFamily.Dvd => _state.DvdActualOuterRadius,
            OpticalDiscFamily.BluRay => _state.BdActualOuterRadius,
            _ => _state.CdActualOuterRadius,
        };
        set
        {
            switch (_family)
            {
                case OpticalDiscFamily.Dvd:
                    _state.DvdActualOuterRadius = value;
                    break;
                case OpticalDiscFamily.BluRay:
                    _state.BdActualOuterRadius = value;
                    break;
                default:
                    _state.CdActualOuterRadius = value;
                    break;
            }
        }
    }

    public string MeasuredPitchLinear
    {
        get => _state.DvdActualPitchLinear;
        set => _state.DvdActualPitchLinear = value;
    }

    public string MeasuredPitchQuadratic
    {
        get => _state.DvdActualPitchQuadratic;
        set => _state.DvdActualPitchQuadratic = value;
    }

    public string MeasuredPitchCubic
    {
        get => _state.DvdActualPitchCubic;
        set => _state.DvdActualPitchCubic = value;
    }

    public string MeasuredPitchSummary => _state.DvdActualPitchSummary;

    // ---- Preview precision: inspection behaviour, not a project parameter ----

    public string PreviewSize
    {
        get => _family switch
        {
            OpticalDiscFamily.Dvd => _state.DvdPreviewSize,
            OpticalDiscFamily.BluRay => _state.BdPreviewSize,
            _ => _state.CdPreviewSize,
        };
        set
        {
            switch (_family)
            {
                case OpticalDiscFamily.Dvd:
                    _state.DvdPreviewSize = value;
                    break;
                case OpticalDiscFamily.BluRay:
                    _state.BdPreviewSize = value;
                    break;
                default:
                    _state.CdPreviewSize = value;
                    break;
            }
        }
    }

    public string SamplesPerSector
    {
        get => _family switch
        {
            OpticalDiscFamily.Dvd => _state.DvdSamplesPerSector,
            OpticalDiscFamily.BluRay => string.Empty,
            _ => _state.CdSamplesPerSector,
        };
        set
        {
            switch (_family)
            {
                case OpticalDiscFamily.Dvd:
                    _state.DvdSamplesPerSector = value;
                    break;
                case OpticalDiscFamily.BluRay:
                    break;
                default:
                    _state.CdSamplesPerSector = value;
                    break;
            }
        }
    }

    /// <summary>Every property whose value depends on the selected family. Kept as one list so
    /// the family switch and the state forwarder cannot disagree about what to refresh.</summary>
    private static readonly string[] ProjectedProperties =
    [
        nameof(IsDvd),
        nameof(IsCd),
        nameof(IsCdAudioArtifact),
        nameof(IsBluRay),
        nameof(HasSamplesPerSector),
        nameof(ArtifactLabel),
        nameof(ArtifactHint),
        nameof(BrowseArtifactCommand),
        nameof(ArtifactPath),
        nameof(MeasuredInnerRadius),
        nameof(MeasuredOuterRadius),
        nameof(MeasuredPitchLinear),
        nameof(MeasuredPitchQuadratic),
        nameof(MeasuredPitchCubic),
        nameof(MeasuredPitchSummary),
        nameof(PreviewSize),
        nameof(SamplesPerSector),
    ];

    /// <summary>Forwards the shared state's changes onto the projected rows. Grouped names
    /// (e.g. <c>CdGeometrySummary</c>) refresh every row that composes them.</summary>
    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Raise(string name) => OnPropertyChanged(name);

        switch (e.PropertyName)
        {
            case nameof(DiscParametersState.CdTrackPath):
                Raise(nameof(IsCdAudioArtifact));
                Raise(nameof(ArtifactPath));
                break;
            case nameof(DiscParametersState.DvdIsoPath):
            case nameof(DiscParametersState.BdIsoPath):
                Raise(nameof(ArtifactPath));
                break;
            case nameof(DiscParametersState.CdActualInnerRadius):
            case nameof(DiscParametersState.DvdActualInnerRadius):
            case nameof(DiscParametersState.BdActualInnerRadius):
                Raise(nameof(MeasuredInnerRadius));
                break;
            case nameof(DiscParametersState.CdActualOuterRadius):
            case nameof(DiscParametersState.DvdActualOuterRadius):
            case nameof(DiscParametersState.BdActualOuterRadius):
                Raise(nameof(MeasuredOuterRadius));
                break;
            case nameof(DiscParametersState.DvdActualPitchLinear):
                Raise(nameof(MeasuredPitchLinear));
                break;
            case nameof(DiscParametersState.DvdActualPitchQuadratic):
                Raise(nameof(MeasuredPitchQuadratic));
                break;
            case nameof(DiscParametersState.DvdActualPitchCubic):
                Raise(nameof(MeasuredPitchCubic));
                break;
            case nameof(DiscParametersState.DvdActualPitchSummary):
                Raise(nameof(MeasuredPitchSummary));
                break;
            case nameof(DiscParametersState.CdPreviewSize):
            case nameof(DiscParametersState.DvdPreviewSize):
            case nameof(DiscParametersState.BdPreviewSize):
                Raise(nameof(PreviewSize));
                break;
            case nameof(DiscParametersState.CdSamplesPerSector):
            case nameof(DiscParametersState.DvdSamplesPerSector):
                Raise(nameof(SamplesPerSector));
                break;
        }
    }
}
