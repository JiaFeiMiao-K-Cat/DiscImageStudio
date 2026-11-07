using DiscImageStudio.Localization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>The design page's tone module: the three tone parameters plus a disc projection
/// of the current composite through the same mapping the engines use. It lives next to the
/// composite pipeline because it is the last step of the same material: layers become a
/// picture, tone decides how that picture becomes pit/land bytes. Rendering waits for the
/// debounced composite and only runs while the design workspace is on screen; export, burn
/// and preview consume the shared <see cref="DiscParametersState"/> instead of rendering a
/// second copy.</summary>
public partial class ToneViewModel : ObservableObject
{
    private const int TonePreviewSize = 512;
    private const int TonePreviewSamplesPerSector = 32;
    private const int TonePreviewDvdSamplesPerSector = 8;

    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly DesignViewModel _design;
    private readonly SemaphoreSlim _tonePreviewGate = new(1, 1);

    private CancellationTokenSource? _tonePreviewCancellation;
    private int _tonePreviewRevision;
    private string? _compositePath;
    private string? _lastTonePreviewPath;
    private bool _tonePreviewReady;

    // A language switch must re-word the status on screen, not replace a running render or a
    // failure with the idle hint, so the current phase is kept next to the ready flag.
    private bool _tonePreviewRendering;
    private string? _tonePreviewError;

    public ToneViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design)
    {
        _shell = shell;
        _state = state;
        _design = design;
        ResetToneCommand = new RelayCommand(ResetTone, () => _shell.IsProjectOpen);
        RefreshTonePreviewCommand = new RelayCommand(
            RefreshTonePreviewNow,
            () => _shell.IsProjectOpen && _compositePath is not null);
        _shell.PropertyChanged += Shell_PropertyChanged;
        _state.PropertyChanged += State_PropertyChanged;
        _design.PropertyChanged += Design_PropertyChanged;
        _design.CompositeRendered += Design_CompositeRendered;
    }

    /// <summary>Exposed for the module's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;

    /// <summary>The module mirrors the open project's family: CD shows the 2/4-level switch,
    /// DVD and BD fix the palette at two levels.</summary>
    public bool IsCd => _design.IsCd;

    public bool IsDvd => _design.IsDvd;

    public bool IsBluRay => _design.IsBluRay;

    [ObservableProperty]
    private BitmapSource? _tonePreviewImage;

    [ObservableProperty]
    private bool _hasTonePreview;

    [ObservableProperty]
    private bool _hasFileRegionOverlay;

    [ObservableProperty]
    private string _tonePreviewStatus = L.T("导入图片后自动生成预览。");

    internal bool IsTonePreviewReady => _tonePreviewReady;

    /// <summary>The status line is the module's only cached string; the picture itself needs
    /// no redraw for a language switch.</summary>
    internal void Relocalize() => TonePreviewStatus = ComposeTonePreviewStatus();

    private string ComposeTonePreviewStatus()
    {
        if (_tonePreviewRendering)
        {
            return L.T("正在按当前影调渲染…");
        }

        if (_tonePreviewError is { } error)
        {
            return L.F($"影调预览失败：{error}");
        }

        return _tonePreviewReady ? L.T("实时更新") : L.T("导入图片后自动生成预览。");
    }

    public RelayCommand ResetToneCommand { get; }

    /// <summary>Redraws the tone preview without touching a parameter.</summary>
    public RelayCommand RefreshTonePreviewCommand { get; }

    private void Shell_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsProjectOpen)
            or nameof(ShellViewModel.CurrentProject))
        {
            OnPropertyChanged(nameof(IsCd));
            OnPropertyChanged(nameof(IsDvd));
            OnPropertyChanged(nameof(IsBluRay));
            ResetToneCommand.NotifyCanExecuteChanged();
            RefreshTonePreviewCommand.NotifyCanExecuteChanged();
            // The composite path belongs to the previous project until the new design
            // finishes its debounced render; showing it would flash the old disc.
            ClearTonePreview();
            UpdateToneAvailability();
        }
        else if (e.PropertyName is nameof(ShellViewModel.CurrentWorkspace))
        {
            UpdateToneAvailability();
        }
    }

    private void Design_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DesignViewModel.IsCd)
            or nameof(DesignViewModel.IsDvd)
            or nameof(DesignViewModel.IsBluRay)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsCd));
        OnPropertyChanged(nameof(IsDvd));
        OnPropertyChanged(nameof(IsBluRay));
        ClearTonePreview();
    }

    private void State_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiscParametersState.ToneDitherStrength)
            or nameof(DiscParametersState.ToneLevels)
            or nameof(DiscParametersState.ToneBrightness)
            or nameof(DiscParametersState.ToneSrgbLinearization)
            or nameof(DiscParametersState.BdInvertMapping)
            or nameof(DiscParametersState.BdInnerRadius)
            or nameof(DiscParametersState.BdOuterRadius)
            or nameof(DiscParametersState.BdUsableSectors)
            or nameof(DiscParametersState.BdPitchLinear)
            or nameof(DiscParametersState.BdPitchQuadratic)
            or nameof(DiscParametersState.BdPitchCubic)
            or nameof(DiscParametersState.BdTotalSectors)
            or nameof(DiscParametersState.BdDataDirectory)
            or nameof(DiscParametersState.BdVolumeLabel)
            or nameof(DiscParametersState.BdFilesAtOuterRing)
            or nameof(DiscParametersState.CdInnerRadius)
            or nameof(DiscParametersState.CdOuterRadius)
            or nameof(DiscParametersState.CdUsableSectors)
            or nameof(DiscParametersState.CdPitchLinear)
            or nameof(DiscParametersState.CdPitchQuadratic)
            or nameof(DiscParametersState.CdPitchCubic)
            or nameof(DiscParametersState.CdSectors)
            or nameof(DiscParametersState.CdDataDirectory)
            or nameof(DiscParametersState.CdVolumeLabel)
            or nameof(DiscParametersState.CdFilesAtOuterRing)
            or nameof(DiscParametersState.DvdInnerRadius)
            or nameof(DiscParametersState.DvdOuterRadius)
            or nameof(DiscParametersState.DvdUsableSectors)
            or nameof(DiscParametersState.DvdTotalSectors)
            or nameof(DiscParametersState.DvdPitchLinear)
            or nameof(DiscParametersState.DvdPitchQuadratic)
            or nameof(DiscParametersState.DvdPitchCubic)
            or nameof(DiscParametersState.DvdDataDirectory)
            or nameof(DiscParametersState.DvdVolumeLabel)
            or nameof(DiscParametersState.DvdFilesAtOuterRing))
        {
            ScheduleTonePreview();
        }
    }

    private void Design_CompositeRendered(string? compositePath)
    {
        _compositePath = compositePath;
        RefreshTonePreviewCommand.NotifyCanExecuteChanged();
        if (compositePath is null)
        {
            ClearTonePreview();
            return;
        }

        ScheduleTonePreview();
    }

    /// <summary>Only renders while the design page is the surface on screen: a slider edit
    /// elsewhere does not queue disc-sized work nobody is looking at, and returning to the
    /// page catches up through this same path.</summary>
    private void UpdateToneAvailability()
    {
        if (!_shell.IsProjectOpen)
        {
            ClearTonePreview();
            return;
        }

        if (_shell.CurrentWorkspace == Workspace.Design)
        {
            ScheduleTonePreview();
        }
    }

    private void ScheduleTonePreview()
    {
        if (!_shell.IsProjectOpen
            || _shell.CurrentWorkspace != Workspace.Design
            || _compositePath is null)
        {
            return;
        }

        _tonePreviewCancellation?.Cancel();
        _tonePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _tonePreviewCancellation = cancellation;
        int revision = ++_tonePreviewRevision;
        BeginTonePreview();
        _ = RenderTonePreviewAsync(revision, cancellation.Token);
    }

    private void RefreshTonePreviewNow()
    {
        if (_compositePath is null)
        {
            return;
        }

        _tonePreviewCancellation?.Cancel();
        _tonePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _tonePreviewCancellation = cancellation;
        int revision = ++_tonePreviewRevision;
        BeginTonePreview();
        _ = RenderTonePreviewAsync(revision, cancellation.Token);
    }

    private void BeginTonePreview()
    {
        _tonePreviewReady = false;
        _tonePreviewRendering = true;
        _tonePreviewError = null;
        TonePreviewStatus = ComposeTonePreviewStatus();
    }

    private async Task RenderTonePreviewAsync(int revision, CancellationToken cancellationToken)
    {
        string? sourcePath = _compositePath;
        if (sourcePath is null)
        {
            return;
        }

        string? outputPath = null;
        bool keepOutput = false;
        bool enteredGate = false;
        try
        {
            outputPath = TempFiles.GetCacheFilePath($"design-tone-{revision}.png");
            await Task.Delay(180, cancellationToken);
            // Layout planners scan synchronously. Let a cancelled scan finish before
            // starting the latest request instead of accumulating background workers.
            await _tonePreviewGate.WaitAsync(cancellationToken);
            enteredGate = true;
            cancellationToken.ThrowIfCancellationRequested();

            int samplesPerSector;
            bool hasFileRegionOverlay;
            if (IsBluRay)
            {
                BluRayDiscParameters parameters = ReadBluRayPreviewParameters();
                bool invert = _state.BdInvertMapping;
                ToneProfile tone = _state.BluRayTone;
                string dataDirectory = _state.BdDataDirectory.Trim();
                string volumeLabel = _state.BdVolumeLabel.Trim();
                bool filesAtOuterRing = dataDirectory.Length > 0 && _state.BdFilesAtOuterRing;
                hasFileRegionOverlay = dataDirectory.Length > 0;
                samplesPerSector = 0;
                await StaWorker.RunAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(parameters,
                        dataDirectory.Length == 0 ? null : dataDirectory,
                        volumeLabel.Length == 0 ? "BD_IMAGE" : volumeLabel,
                        filesAtOuterRing: filesAtOuterRing);
                    cancellationToken.ThrowIfCancellationRequested();
                    BluRayPreviewRenderer.RenderCalibration(
                        sourcePath,
                        outputPath,
                        parameters,
                        parameters,
                        plan.DrawingStartSector,
                        plan.DrawingEndSectorExclusive,
                        TonePreviewSize,
                        invert,
                        cancellationToken,
                        tone);
                    if (plan.HasFileSystem)
                        FileRegionPreviewOverlay.ApplyBluRay(outputPath, parameters,
                            plan.DrawingStartSector, plan.DrawingEndSectorExclusive, cancellationToken);
                    return 0;
                });
            }
            else if (IsDvd)
            {
                DvdStreamingOptions options = ReadDvdPreviewOptions();
                string dataDirectory = _state.DvdDataDirectory.Trim();
                string volumeLabel = _state.DvdVolumeLabel.Trim();
                bool filesAtOuterRing = _state.DvdFilesAtOuterRing;
                hasFileRegionOverlay = dataDirectory.Length > 0;
                samplesPerSector = TonePreviewDvdSamplesPerSector;
                await StaWorker.RunAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DvdHybridStreamingPlan? plan = dataDirectory.Length > 0
                        ? DvdStreamingGenerator.PrepareHybrid(dataDirectory, options,
                            volumeLabel.Length == 0 ? "DISC_IMAGE" : volumeLabel, filesAtOuterRing)
                        : null;
                    cancellationToken.ThrowIfCancellationRequested();
                    DvdTonePreview.Render(
                        sourcePath,
                        outputPath,
                        options,
                        TonePreviewSize,
                        samplesPerSector,
                        cancellationToken,
                        drawingStartLba: plan?.DrawingStartLba ?? 0,
                        drawingEndLbaExclusive: plan?.DrawingEndLbaExclusive);
                    if (plan is not null)
                        FileRegionPreviewOverlay.ApplyDvd(outputPath, options,
                            plan.DrawingStartLba, plan.DrawingEndLbaExclusive, cancellationToken);
                    return 0;
                });
            }
            else
            {
                CdDiscParameters parameters = ReadCdPreviewParameters();
                // Fit the output to the recording radius without changing the source
                // image's physical scale used by the CD generator.
                CdDiscParameters display = parameters with { ImageOuterRadiusMm = parameters.OutputOuterRadiusMm };
                ToneProfile tone = _state.Tone;
                samplesPerSector = TonePreviewSamplesPerSector;
                string dataDirectory = _state.CdDataDirectory.Trim();
                string volumeLabel = _state.CdVolumeLabel.Trim();
                bool filesAtOuterRing = _state.CdFilesAtOuterRing;
                hasFileRegionOverlay = dataDirectory.Length > 0;
                await StaWorker.RunAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CdDataLayoutPlan? plan = dataDirectory.Length > 0
                        ? CdDataImageGenerator.PrepareLayout(dataDirectory, parameters, volumeLabel, filesAtOuterRing)
                        : null;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (plan is not null)
                    {
                        CdDataImageGenerator.PreviewWarp(sourcePath, outputPath, parameters, display,
                            TonePreviewSize, samplesPerSector, tone, plan, cancellationToken);
                        FileRegionPreviewOverlay.ApplyCd(outputPath, display,
                            plan.DrawingStartLba, plan.DrawingEndLbaExclusive, cancellationToken);
                    }
                    else
                    {
                        CdTrackGenerator.PreviewWarp(sourcePath, outputPath, parameters, display,
                            TonePreviewSize, samplesPerSector, tone, cancellationToken);
                    }
                    return 0;
                });
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _tonePreviewRevision)
            {
                return;
            }

            BitmapImage bitmap = new();
            using (FileStream stream = new(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            string? previousPath = _lastTonePreviewPath;
            _lastTonePreviewPath = outputPath;
            keepOutput = true;
            TonePreviewImage = bitmap;
            HasTonePreview = true;
            HasFileRegionOverlay = hasFileRegionOverlay;
            _tonePreviewRendering = false;
            _tonePreviewError = null;
            _tonePreviewReady = true;
            TonePreviewStatus = ComposeTonePreviewStatus();
            TempFiles.TryDeleteTemporaryFile(previousPath);
        }
        catch (OperationCanceledException)
        {
            // A newer render or a cleared preview owns the status; this one must not touch it.
        }
        catch (Exception exception)
        {
            if (revision == _tonePreviewRevision)
            {
                _tonePreviewRendering = false;
                _tonePreviewError = exception.Message;
                TonePreviewImage = null;
                HasTonePreview = false;
                HasFileRegionOverlay = false;
                _tonePreviewReady = false;
                TempFiles.TryDeleteTemporaryFile(_lastTonePreviewPath);
                _lastTonePreviewPath = null;
                TonePreviewStatus = ComposeTonePreviewStatus();
                _shell.AppendLog(JobDiagnostics.FormatLog($"影调预览：{sourcePath}", exception));
            }
        }
        finally
        {
            if (enteredGate)
                _tonePreviewGate.Release();
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    private void ClearTonePreview()
    {
        _tonePreviewCancellation?.Cancel();
        _tonePreviewCancellation?.Dispose();
        _tonePreviewCancellation = null;
        _compositePath = null;
        RefreshTonePreviewCommand.NotifyCanExecuteChanged();
        // A render that is still in flight belongs to the discarded state; bumping the
        // revision keeps its completion from writing the status back.
        _tonePreviewRevision++;
        TonePreviewImage = null;
        HasTonePreview = false;
        HasFileRegionOverlay = false;
        _tonePreviewReady = false;
        _tonePreviewRendering = false;
        _tonePreviewError = null;
        TonePreviewStatus = ComposeTonePreviewStatus();
        TempFiles.TryDeleteTemporaryFile(_lastTonePreviewPath);
        _lastTonePreviewPath = null;
    }

    private void ResetTone()
    {
        _state.ToneDitherStrength = ToneProfile.Photo.DitherStrength;
        _state.ToneLevels = ToneProfile.Photo.Levels;
        _state.ToneBrightness = ToneProfile.Photo.Brightness;
        _state.ToneSrgbLinearization = ToneProfile.Photo.SrgbLinearization;
    }

    private BluRayDiscParameters ReadBluRayPreviewParameters()
    {
        long totalSectors = ParameterParser.ParsePositiveLong(_state.BdTotalSectors, L.T("蓝光总扇区数"));
        if (totalSectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(L.T("蓝光总扇区数"), L.T("总扇区数超出蓝光引擎支持范围。"));
        }

        BluRayDiscParameters parameters = new(
            checked((uint)totalSectors),
            1,
            ParameterParser.ParsePositiveDouble(_state.BdInnerRadius, L.T("蓝光内半径")),
            ParameterParser.ParsePositiveDouble(_state.BdOuterRadius, L.T("蓝光外半径")),
            PitchLinear: ParameterParser.ParseDouble(_state.BdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.BdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.BdPitchCubic, L.T("相对三次项")),
            UsableSectors: checked((uint)_state.GetUsableSectors(OpticalDiscFamily.BluRay)));
        parameters.Validate();
        return parameters;
    }

    private CdDiscParameters ReadCdPreviewParameters()
    {
        CdDiscParameters parameters = new(
            ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, L.T("内半径")),
            ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, L.T("外半径")),
            ParameterParser.ParsePositiveLong(_state.CdSectors, L.T("扇区数")),
            PitchLinear: ParameterParser.ParseDouble(_state.CdPitchLinear, L.T("相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.CdPitchQuadratic, L.T("相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.CdPitchCubic, L.T("相对三次项")),
            UsableSectors: _state.GetUsableSectors(OpticalDiscFamily.CompactDisc));
        parameters.Validate();
        return parameters;
    }

    private DvdStreamingOptions ReadDvdPreviewOptions()
    {
        long totalSectors = ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, L.T("总扇区数"));
        if (totalSectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(L.T("总扇区数"), L.T("总扇区数超出 DVD 引擎支持范围。"));
        }

        DvdStreamingOptions options = new(
            checked((uint)totalSectors),
            ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, L.T("内半径")),
            ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, L.T("外半径")),
            PitchLinear: ParameterParser.ParseDouble(_state.DvdPitchLinear, L.T("DVD 轨距一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(_state.DvdPitchQuadratic, L.T("DVD 轨距二次项")),
            PitchCubic: ParameterParser.ParseDouble(_state.DvdPitchCubic, L.T("DVD 轨距三次项")),
            UsableSectors: checked((uint)_state.GetUsableSectors(OpticalDiscFamily.Dvd)),
            Tone: _state.DvdTone);
        options.Validate();
        return options;
    }
}
