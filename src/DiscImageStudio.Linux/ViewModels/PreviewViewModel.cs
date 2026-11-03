using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

/// <summary>Preview page. "Design projection" runs the design through the engine geometry and
/// tone (optionally onto a disc with different measured radii, to see how a mismatch bends
/// the picture); "Read-back" decodes the generated file as a reader would follow the spiral.</summary>
public partial class PreviewViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private ProjectViewModel? _project;
    private bool _stale = true;

    [ObservableProperty] private bool _isReadback;
    [ObservableProperty] private bool _useMeasuredRadii;
    [ObservableProperty] private string _measuredInnerText = string.Empty;
    [ObservableProperty] private string _measuredOuterText = string.Empty;
    [ObservableProperty] private int _selectedSize = 1600;
    [ObservableProperty] private string _readbackFile = string.Empty;

    public PreviewViewModel(MainViewModel main)
    {
        _main = main;
        main.ProjectChanged += OnProjectChanged;
    }

    public ProjectViewModel? Project => _project;

    public PreviewSlot Slot { get; } = new(TimeSpan.FromMilliseconds(350));

    public IReadOnlyList<int> Sizes { get; } = [1024, 1600, 2048, 3072];

    public bool IsProjection
    {
        get => !IsReadback;
        set => IsReadback = !value;
    }

    public string ModeDescription => IsReadback
        ? "Decodes the generated file and draws each byte where the reader meets it on the spiral. Grey marks data that is not part of the picture (filesystem, ECC, reserved space)."
        : "Projects the design through the same geometry and tone mapping the generator uses. Turn on measured radii to see how the picture bends on a disc whose real track geometry differs.";

    public string ReadbackHint => _project?.Family switch
    {
        OpticalDiscFamily.Dvd => "Reads the ISO and its .json sidecar written by the generator.",
        OpticalDiscFamily.BluRay => "Reads the ISO and its .json sidecar written by the generator.",
        _ => "Reads a WAV/RAW track (undoing the CD-DA interleave) or a Mode 1 data ISO.",
    };

    public void OnShown()
    {
        if (_project is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ReadbackFile) || !File.Exists(ReadbackFile))
        {
            ReadbackFile = _project.OutputPath;
        }

        if (_stale)
        {
            Render();
        }
    }

    /// <summary>Called after the Export page writes a new file.</summary>
    public void ShowReadbackOf(string file)
    {
        ReadbackFile = file;
        IsReadback = true;
        _stale = true;
    }

    partial void OnIsReadbackChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProjection));
        OnPropertyChanged(nameof(ModeDescription));
        Invalidate();
    }

    partial void OnUseMeasuredRadiiChanged(bool value) => Invalidate();

    partial void OnMeasuredInnerTextChanged(string value)
    {
        if (UseMeasuredRadii)
        {
            Invalidate();
        }
    }

    partial void OnMeasuredOuterTextChanged(string value)
    {
        if (UseMeasuredRadii)
        {
            Invalidate();
        }
    }

    partial void OnSelectedSizeChanged(int value) => Invalidate();

    partial void OnReadbackFileChanged(string value)
    {
        if (IsReadback)
        {
            Invalidate();
        }
    }

    private void OnProjectChanged()
    {
        if (_project is not null)
        {
            _project.PreviewInputsChanged -= OnInputsChanged;
        }

        _project = _main.Project;
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(ReadbackHint));
        Slot.Clear("Open the Preview page to render.");
        _stale = true;
        IsReadback = false;
        UseMeasuredRadii = false;
        if (_project is not null)
        {
            _project.PreviewInputsChanged += OnInputsChanged;
            MeasuredInnerText = Num.Format(_project.InnerRadiusMm);
            MeasuredOuterText = Num.Format(_project.OuterRadiusMm);
            ReadbackFile = _project.OutputPath;
        }
    }

    private void OnInputsChanged()
    {
        if (!IsReadback)
        {
            Invalidate();
        }
    }

    private void Invalidate()
    {
        _stale = true;
        if (_main.IsPreviewPage)
        {
            Render();
        }
    }

    [RelayCommand]
    private void Render()
    {
        ProjectViewModel? project = _project;
        if (project is null)
        {
            return;
        }

        _stale = false;
        DiscSettings settings = project.Snapshot();
        int size = SelectedSize;
        if (IsReadback)
        {
            string file = ReadbackFile.Trim();
            if (!File.Exists(file))
            {
                Slot.Clear("No generated file yet. Generate one on the Export page, or choose an existing file.");
                return;
            }

            Slot.Schedule(async token =>
            {
                string output = AppPaths.NewWorkFile("readback", ".png");
                return await PreviewRenderer.RenderReadbackAsync(settings, file, output, size, _ => { }, token);
            });
            return;
        }

        if (!project.HasVisibleImages)
        {
            Slot.Clear("Import an image on the Design page to see a preview.");
            return;
        }

        double? inner = null, outer = null;
        if (UseMeasuredRadii)
        {
            if (!Num.TryParse(MeasuredInnerText, out double measuredInner) || !Num.TryParse(MeasuredOuterText, out double measuredOuter)
                || measuredInner <= 0 || measuredOuter <= measuredInner || measuredOuter > 60)
            {
                Slot.Clear("Enter measured radii in millimetres (inner < outer ≤ 60).");
                return;
            }

            inner = measuredInner;
            outer = measuredOuter;
        }

        Slot.Schedule(async token =>
        {
            string? composite = await project.EnsurePreviewCompositeAsync(token);
            if (composite is null)
            {
                return null;
            }

            string output = AppPaths.NewWorkFile("projection", ".png");
            return await Task.Run(() => PreviewRenderer.RenderProjection(settings, composite, output, size, 16, inner, outer, token), token);
        });
    }

    [RelayCommand]
    private async Task BrowseReadback()
    {
        if (_main.Ui is null || _project is null)
        {
            return;
        }

        IReadOnlyList<string> files = await _main.Ui.PickFilesAsync("Choose a generated file",
            _project.IsCd ? [FileTypes.Burnable, FileTypes.All] : [FileTypes.Iso, FileTypes.All],
            Path.GetDirectoryName(ReadbackFile) ?? _project.Folder, allowMultiple: false);
        if (files.Count == 1)
        {
            ReadbackFile = files[0];
        }
    }

    [RelayCommand]
    private void ResetMeasured()
    {
        if (_project is not null)
        {
            MeasuredInnerText = Num.Format(_project.InnerRadiusMm);
            MeasuredOuterText = Num.Format(_project.OuterRadiusMm);
        }
    }
}
