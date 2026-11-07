using DiscImageStudio.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Dvd;
using DiscImageStudio.BluRay;
using DiscImageStudio.Services;

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
    public string GenerateButtonText => !IsCd || _state.HasCdFiles ? L.T("生成镜像") : L.T("生成音轨");

    /// <summary>The source card's jump into the project dialog, where the generation geometry
    /// is edited; every other workspace shows the same affordance for the same reason.</summary>
    public IAsyncRelayCommand OpenProjectSettingsCommand => _shell.EditProjectCommand;

    /// <summary>Workspace navigation for the page header's "下一步" action.</summary>
    public IRelayCommand<Workspace> NavigateCommand => _shell.NavigateCommand;

    public RelayCommand BrowseOutputCommand { get; }

    public RelayCommand BrowseDataCommand { get; }

    public IAsyncRelayCommand GenerateCommand { get; }

    [ObservableProperty]
    private string _sourceSummary = L.T("请先在设计页导入图片。");

    /// <summary>The action label and source row are the page's cached text; both are composed
    /// from the current state so they re-read cleanly.</summary>
    internal void Relocalize()
    {
        OnPropertyChanged(nameof(GenerateButtonText));
        UpdateSourceSummary();
    }

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
            string? iso = _shell.Dialogs?.PickSave(L.T("ISO 镜像|*.iso|所有文件|*.*"), ".iso", "bd-image.iso");
            if (iso is not null)
            {
                _state.BdOutputPath = iso;
            }
        }
        else if (IsDvd)
        {
            string? iso = _shell.Dialogs?.PickSave(L.T("ISO 镜像|*.iso|所有文件|*.*"), ".iso", "dvd-image.iso");
            if (iso is not null)
            {
                _state.DvdOutputPath = iso;
            }
        }
        else if (_state.HasCdFiles)
        {
            string? iso = _shell.Dialogs?.PickSave(L.T("ISO 镜像|*.iso|所有文件|*.*"), ".iso", "cd-image.iso");
            if (iso is not null)
            {
                _state.CdOutputPath = iso;
            }
        }
        else
        {
            string? wav = _shell.Dialogs?.PickSave(L.T("WAV 音轨（推荐）|*.wav|RAW 音轨（旧格式）|*.raw|所有文件|*.*"), ".wav", "cd-track.wav");
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
            string? picked = _shell.Dialogs?.PickFolder(L.T("选择要放入 CD 数据盘的文件夹"));
            if (picked is not null)
            {
                _state.CdDataDirectory = picked;
            }

            return;
        }

        if (IsBluRay)
        {
            string? picked = _shell.Dialogs?.PickFolder(L.T("选择要放入蓝光盘的文件夹"));
            if (picked is not null)
            {
                _state.BdDataDirectory = picked;
            }

            return;
        }

        string? dvdFolder = _shell.Dialogs?.PickFolder(L.T("选择要放入 DVD 的文件夹"));
        if (dvdFolder is not null)
        {
            _state.DvdDataDirectory = dvdFolder;
        }
    }

    private void UpdateSourceSummary()
    {
        if (!_shell.IsProjectOpen)
        {
            SourceSummary = L.T("请先打开或新建一个工程。");
            return;
        }

        int layerCount = _design.Layers.Count;
        SourceSummary = layerCount == 0
            ? L.T("请先在设计页导入图片。")
            : L.F($"设计页合成图源（{RingImageQuality.GenerationSize} px）");
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
            _shell.AppendLog(JobDiagnostics.FormatLog(L.T("任务未完成"), exception));
            if (exception is ArgumentException or FormatException or OverflowException)
                _shell.ShowValidationError(exception);
            else
                _shell.ShowToast(L.T("任务未完成"), L.T("详情见窗口底部的任务日志。"), ToastKind.Error);
        }
    }

    private async Task GenerateCdAsync(string compositePath)
    {
        string output = RequirePath(_state.CdOutputPath,
            _state.HasCdFiles ? L.T("请选择 CD ISO 输出位置。") : L.T("请选择 CD 音轨输出位置。"));
        CdDiscParameters parameters = new(
            ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, L.T("内半径")),
            ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, L.T("外半径")),
            ParameterParser.ParsePositiveLong(_state.CdSectors, L.T("总扇区数")),
            PitchLinear: ParameterParser.ParseDouble(_state.CdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.CdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.CdPitchCubic, L.T("相对三次项")),
            UsableSectors: _state.GetUsableSectors(OpticalDiscFamily.CompactDisc));
        CdImageRequest request = new(compositePath, output, parameters, _state.Tone,
            _state.CdInterleave, _state.CdWriteCue, _state.CdDataDirectory.Trim(),
            string.IsNullOrWhiteSpace(_state.CdVolumeLabel) ? "CD_IMAGE" : _state.CdVolumeLabel.Trim(),
            _state.CdFilesAtOuterRing);
        if (await _shell.RunImageJobAsync(
                _state.HasCdFiles ? L.T("正在生成 CD 数据盘 ISO…") : L.T("正在生成 CD 音轨…"),
                output, (progress, token) => CdImageGenerator.Generate(request, progress, token), request.ToString()))
            _state.CdTrackPath = output;
    }

    private async Task GenerateDvdAsync(string compositePath)
    {
        string output = RequirePath(_state.DvdOutputPath, L.T("请选择 DVD ISO 输出位置。"));
        DvdStreamingOptions options = new(
            checked((uint)ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, L.T("总扇区数"))),
            ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, L.T("内半径")),
            ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, L.T("外半径")),
            PitchLinear: ParameterParser.ParseDouble(_state.DvdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.DvdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.DvdPitchCubic, L.T("相对三次项")),
            UsableSectors: checked((uint)_state.GetUsableSectors(OpticalDiscFamily.Dvd)),
            Tone: _state.DvdTone,
            ReadCompatibility: _state.DvdReadCompatibility);
        DvdImageRequest request = new(compositePath, output, options, _state.DvdDataDirectory.Trim(),
            string.IsNullOrWhiteSpace(_state.DvdVolumeLabel) ? "DISC_IMAGE" : _state.DvdVolumeLabel.Trim(),
            !string.IsNullOrWhiteSpace(_state.DvdDataDirectory) && _state.DvdFilesAtOuterRing);
        if (await _shell.RunImageJobAsync(L.T("正在生成 DVD ISO…"), output,
                (progress, token) => DvdImageGenerator.Generate(request, progress, token), request.ToString()))
            _state.DvdIsoPath = output;
    }

    private async Task GenerateBluRayAsync(string compositePath)
    {
        string output = RequirePath(_state.BdOutputPath, L.T("请选择蓝光 ISO 输出位置。"));
        BluRayDiscParameters parameters = new(
            checked((uint)ParameterParser.ParsePositiveLong(_state.BdTotalSectors, L.T("总扇区数"))), 1,
            ParameterParser.ParsePositiveDouble(_state.BdInnerRadius, L.T("内半径")),
            ParameterParser.ParsePositiveDouble(_state.BdOuterRadius, L.T("外半径")),
            PitchLinear: ParameterParser.ParseDouble(_state.BdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.BdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.BdPitchCubic, L.T("相对三次项")),
            UsableSectors: checked((uint)_state.GetUsableSectors(OpticalDiscFamily.BluRay)));
        BluRayImageRequest request = new(compositePath, output, parameters, _state.BluRayTone,
            _state.BdInvertMapping, _state.BdDataDirectory.Trim(),
            string.IsNullOrWhiteSpace(_state.BdVolumeLabel) ? "BD_IMAGE" : _state.BdVolumeLabel.Trim(),
            _state.BdFilesAtOuterRing);
        BluRayImageGenerator generator = new(BluRayImageLoader.Load);
        if (await _shell.RunImageJobAsync(L.T("正在生成蓝光 ISO…"), output,
                (progress, token) => generator.Generate(request, progress, token), request.ToString()))
            _state.BdIsoPath = output;
    }

    private static string RequirePath(string raw, string message)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException(message) : value;
    }
}
