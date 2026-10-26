using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Imaging;

namespace DiscImageStudio.ViewModels;

/// <summary>Export workspace: turns the design page's composite into the disc artifact file.
/// The disc family comes from the open project, so the CD and DVD parameter pages collapse
/// into this one; the generation geometry lives in the project settings. Streaming that same
/// material to a blank disc is the burn workspace's job. The tone parameters live with the
/// design page, next to the composite they affect; this workspace only passes the shared
/// values to the engine.</summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly DesignViewModel _design;

    /// <summary>Exposed for the view's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;

    public ExportViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design)
    {
        _shell = shell;
        _state = state;
        _design = design;
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !_shell.IsBusy);
        BrowseDataCommand = new RelayCommand(BrowseData, () => !_shell.IsBusy);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !_shell.IsBusy);
        _shell.BusyChanged += NotifyCommands;
        _shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.IsProjectOpen)
                or nameof(ShellViewModel.CurrentProject))
            {
                OnPropertyChanged(nameof(IsCd));
                OnPropertyChanged(nameof(IsDvd));
                OnPropertyChanged(nameof(IsBluRay));
                OnPropertyChanged(nameof(FamilyLabel));
                OnPropertyChanged(nameof(GenerateButtonText));
                UpdateSourceSummary();
            }
        };
        _design.DesignEdited += UpdateSourceSummary;
        _design.CanvasChanged += UpdateSourceSummary;
        _state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiscParametersState.CdDataDirectory))
            {
                OnPropertyChanged(nameof(GenerateButtonText));
                OnPropertyChanged(nameof(CdOutputLabel));
                OnPropertyChanged(nameof(CdOutputHint));
            }
        };
    }

    public bool IsCd => _shell.CurrentProject?.IsCd ?? true;

    public bool IsDvd => _shell.CurrentProject?.IsDvd ?? false;

    public bool IsBluRay => _shell.CurrentProject?.IsBluRay ?? false;

    /// <summary>False while the design page has no layers; the source row then offers the
    /// jump to where the picture comes from instead of a dead end.</summary>
    public bool HasLayers => _design.Layers.Count > 0;

    /// <summary>Family chip in the page header.</summary>
    public string FamilyLabel => _shell.CurrentProject?.FamilyLabel ?? "CD";

    /// <summary>Primary action label: the artifact differs per family (WAV track vs ISO).</summary>
    public string GenerateButtonText => !IsCd || _state.HasCdFiles ? "生成镜像" : "生成音轨";

    public string CdOutputLabel => _state.HasCdFiles ? "输出数据盘 (.iso)" : "输出音轨 (.wav / .raw)";

    public string CdOutputHint => _state.HasCdFiles
        ? "包含随盘文件，导出为 CD 数据盘 ISO。"
        : "无随盘文件时生成音轨，推荐 WAV 格式。";

    /// <summary>The source card's jump into the project dialog, where the generation geometry
    /// is edited; every other workspace shows the same affordance for the same reason.</summary>
    public IAsyncRelayCommand OpenProjectSettingsCommand => _shell.EditProjectCommand;

    /// <summary>Workspace navigation for the page header's "下一步" action.</summary>
    public IRelayCommand<Workspace> NavigateCommand => _shell.NavigateCommand;

    public RelayCommand BrowseOutputCommand { get; }

    public RelayCommand BrowseDataCommand { get; }

    public IAsyncRelayCommand GenerateCommand { get; }

    [ObservableProperty]
    private string _sourceSummary = "请先在设计页导入图片。";

    private void NotifyCommands()
    {
        BrowseOutputCommand.NotifyCanExecuteChanged();
        BrowseDataCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private void BrowseOutput()
    {
        if (IsBluRay)
        {
            string? iso = _shell.Dialogs?.PickSave("ISO 镜像|*.iso|所有文件|*.*", ".iso", "bd-image.iso");
            if (iso is not null)
            {
                _state.BdOutputPath = iso;
            }
        }
        else if (IsDvd)
        {
            string? iso = _shell.Dialogs?.PickSave("ISO 镜像|*.iso|所有文件|*.*", ".iso", "dvd-image.iso");
            if (iso is not null)
            {
                _state.DvdOutputPath = iso;
            }
        }
        else if (_state.HasCdFiles)
        {
            string? iso = _shell.Dialogs?.PickSave("ISO 镜像|*.iso|所有文件|*.*", ".iso", "cd-image.iso");
            if (iso is not null)
            {
                _state.CdOutputPath = iso;
            }
        }
        else
        {
            string? wav = _shell.Dialogs?.PickSave("WAV 音轨（推荐）|*.wav|RAW 音轨（旧格式）|*.raw|所有文件|*.*", ".wav", "cd-track.wav");
            if (wav is not null)
            {
                _state.CdOutputPath = wav;
            }
        }
    }

    private void BrowseData()
    {
        if (IsCd)
        {
            string? picked = _shell.Dialogs?.PickFolder("选择要放入 CD 数据盘的文件夹");
            if (picked is not null)
            {
                _state.CdDataDirectory = picked;
            }

            return;
        }

        if (IsBluRay)
        {
            string? picked = _shell.Dialogs?.PickFolder("选择要放入蓝光盘的文件夹");
            if (picked is not null)
            {
                _state.BdDataDirectory = picked;
            }

            return;
        }

        string? dvdFolder = _shell.Dialogs?.PickFolder("选择要放入 DVD 的文件夹");
        if (dvdFolder is not null)
        {
            _state.DvdDataDirectory = dvdFolder;
        }
    }

    private void UpdateSourceSummary()
    {
        if (!_shell.IsProjectOpen)
        {
            SourceSummary = "请先打开或新建一个工程。";
            return;
        }

        int layerCount = _design.Layers.Count;
        SourceSummary = layerCount == 0
            ? "请先在设计页导入图片。"
            : $"设计页合成图源（{RingImageQuality.GenerationSize} px）";
        OnPropertyChanged(nameof(HasLayers));
    }

    private async Task GenerateAsync()
    {
        try
        {
            // The composite renders here at export quality even when the design page has not
            // been visited in this session, so a reopened project can generate immediately.
            string compositePath = await _design.EnsureExportCompositeAsync();
            if (IsBluRay)
            {
                await GenerateBluRayAsync(compositePath);
            }
            else if (IsDvd)
            {
                await GenerateDvdAsync(compositePath);
            }
            else
            {
                await GenerateCdAsync(compositePath);
            }
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    private async Task GenerateCdAsync(string compositePath)
    {
        string output = RequirePath(_state.CdOutputPath,
            _state.HasCdFiles ? "请选择 CD ISO 输出位置。" : "请选择 CD 音轨输出位置。");
        List<string> arguments = ["cd-generate", "--input", compositePath, "--output", output];
        // The CD engine defaults (1200 mm/s, 0°, 57.5 mm) cover velocity, start angle and the
        // image outer radius; only the user-visible geometry is passed through.
        ToneProfile tone = _state.Tone;
        arguments.AddRange(
        [
            "--r0", _state.CdInnerRadius.Trim(),
            "--r1", _state.CdOuterRadius.Trim(),
            "--sectors", _state.CdSectors.Trim(),
            "--tone-dither", tone.DitherStrength.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-levels", tone.Levels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--tone-brightness", tone.Brightness.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-gamma", tone.SrgbLinearization.ToString().ToLowerInvariant(),
        ]);
        if (_state.HasCdFiles)
        {
            arguments.AddRange(
            [
                "--data-dir", _state.CdDataDirectory.Trim(),
                "--volume-label", string.IsNullOrWhiteSpace(_state.CdVolumeLabel) ? "CD_IMAGE" : _state.CdVolumeLabel.Trim(),
                "--file-position", _state.CdFilesAtOuterRing ? "outer" : "inner",
            ]);
        }
        else
        {
            arguments.AddRange(
            [
                "--interleave", _state.CdInterleave.ToString().ToLowerInvariant(),
                "--cue", _state.CdWriteCue.ToString().ToLowerInvariant(),
            ]);
        }
        await _shell.RunDiscJobAsync(
            _state.HasCdFiles ? "正在生成 CD 数据盘 ISO…" : "正在生成 CD 音轨…",
            arguments.ToArray(),
            output);
        // The generated track is the live preview page's read-back input.
        _state.CdTrackPath = output;
    }

    private async Task GenerateDvdAsync(string compositePath)
    {
        string output = RequirePath(_state.DvdOutputPath, "请选择 DVD ISO 输出位置。");
        ToneProfile tone = _state.DvdTone;
        List<string> arguments =
        [
            "solve",
            "--image", compositePath,
            "--iso-output", output,
            "--total-sectors", _state.DvdTotalSectors.Trim(),
            "--inner-radius-mm", _state.DvdInnerRadius.Trim(),
            "--outer-radius-mm", _state.DvdOuterRadius.Trim(),
            "--pitch-linear", _state.DvdPitchLinear.Trim(),
            "--pitch-quadratic", _state.DvdPitchQuadratic.Trim(),
            "--pitch-cubic", _state.DvdPitchCubic.Trim(),
            "--spiral-direction", "cw",
            "--image-threshold", "128",
            "--alpha-threshold", "1",
            "--tone-dither", tone.DitherStrength.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-levels", "2",
            "--tone-brightness", tone.Brightness.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-gamma", tone.SrgbLinearization.ToString().ToLowerInvariant(),
            "--algorithm", "dispersion",
            "--iterations-per-block", "1",
            "--fast-output", "true",
            "--volume-label", string.IsNullOrWhiteSpace(_state.DvdVolumeLabel) ? "DISC_IMAGE" : _state.DvdVolumeLabel.Trim(),
        ];
        if (string.IsNullOrWhiteSpace(_state.DvdDataDirectory))
        {
            arguments.Add("--fill-sectors");
            arguments.Add(_state.DvdTotalSectors.Trim());
        }
        else
        {
            arguments.Add("--data-dir");
            arguments.Add(_state.DvdDataDirectory.Trim());
            arguments.Add("--file-position");
            arguments.Add(_state.DvdFilesAtOuterRing ? "outer" : "inner");
        }

        await _shell.RunDiscJobAsync(
            "正在生成 DVD ISO…",
            arguments.ToArray(),
            output);
        // The generated ISO is the live preview page's read-back input.
        _state.DvdIsoPath = output;
    }

    private async Task GenerateBluRayAsync(string compositePath)
    {
        string output = RequirePath(_state.BdOutputPath, "请选择蓝光 ISO 输出位置。");
        ToneProfile tone = _state.BluRayTone;
        List<string> arguments =
        [
            "bd-generate",
            "--input", compositePath,
            "--output", output,
            "--total-sectors", _state.BdTotalSectors.Trim(),
            "--r0", _state.BdInnerRadius.Trim(),
            "--r1", _state.BdOuterRadius.Trim(),
            "--invert", _state.BdInvertMapping.ToString().ToLowerInvariant(),
            "--volume-label", string.IsNullOrWhiteSpace(_state.BdVolumeLabel)
                ? "BD_IMAGE" : _state.BdVolumeLabel.Trim(),
            "--tone-dither", tone.DitherStrength.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-levels", "2",
            "--tone-brightness", tone.Brightness.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-gamma", tone.SrgbLinearization.ToString().ToLowerInvariant(),
        ];
        if (!string.IsNullOrWhiteSpace(_state.BdDataDirectory))
        {
            arguments.Add("--data-dir");
            arguments.Add(_state.BdDataDirectory.Trim());
            arguments.Add("--file-position");
            arguments.Add(_state.BdFilesAtOuterRing ? "outer" : "inner");
        }

        await _shell.RunDiscJobAsync(
            "正在生成蓝光 ISO…",
            arguments.ToArray(),
            output);
        // The generated ISO and its sidecar are the live preview page's read-back input.
        _state.BdIsoPath = output;
    }

    private static string RequirePath(string raw, string message)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException(message) : value;
    }
}
