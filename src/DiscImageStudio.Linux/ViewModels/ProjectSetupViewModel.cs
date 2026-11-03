using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

/// <summary>New-project and project-settings dialog: disc family, preset and the generation
/// geometry. Numbers are edited as text so measured presets (15+ significant digits) round-
/// trip exactly.</summary>
public partial class ProjectSetupViewModel : ObservableObject
{
    private bool _applyingPreset;

    [ObservableProperty] private string _name = "My disc";
    [ObservableProperty] private OpticalDiscFamily _family = OpticalDiscFamily.CompactDisc;
    [ObservableProperty] private DiscPreset? _selectedPreset;
    [ObservableProperty] private string _innerText = string.Empty;
    [ObservableProperty] private string _outerText = string.Empty;
    [ObservableProperty] private string _sectorsText = string.Empty;
    [ObservableProperty] private string _pitchLinearText = "0";
    [ObservableProperty] private string _pitchQuadraticText = "0";
    [ObservableProperty] private string _pitchCubicText = "0";
    [ObservableProperty] private bool _invertMapping;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private string? _errorMessage;

    public ProjectSetupViewModel(bool isEditMode, string folder)
    {
        IsEditMode = isEditMode;
        _folder = folder;
        RefreshPresets(selectDefault: true);
    }

    public bool IsEditMode { get; }

    public bool IsCreateMode => !IsEditMode;

    public string Title => IsEditMode ? "Project settings" : "New project";

    public string ConfirmText => IsEditMode ? "Apply" : "Create project";

    public ObservableCollection<DiscPreset> Presets { get; } = [];

    public bool IsCd
    {
        get => Family == OpticalDiscFamily.CompactDisc;
        set { if (value) Family = OpticalDiscFamily.CompactDisc; }
    }

    public bool IsDvd
    {
        get => Family == OpticalDiscFamily.Dvd;
        set { if (value) Family = OpticalDiscFamily.Dvd; }
    }

    public bool IsBluRay
    {
        get => Family == OpticalDiscFamily.BluRay;
        set { if (value) Family = OpticalDiscFamily.BluRay; }
    }

    public string SectorsLabel => Family == OpticalDiscFamily.CompactDisc ? "Program sectors" : "Total sectors";

    public string SectorsHint => Family switch
    {
        OpticalDiscFamily.Dvd => "Multiple of 16 (one ECC block).",
        OpticalDiscFamily.BluRay => $"Multiple of 32, at most {ProjectDocument.BluRaySectorsPerLayer:N0} (single layer).",
        _ => "75 sectors per second of audio; 359,849 is a full 80-minute disc.",
    };

    public string? PresetDescription => SelectedPreset?.Description;

    public string ProjectFilePath => Path.Combine(Folder ?? string.Empty, SafeFileName(Name) + ProjectJsonStore.FileExtension);

    public bool ProjectFileExists => IsCreateMode && File.Exists(ProjectFilePath);

    /// <summary>Supplied by the dialog window, which owns the folder picker.</summary>
    public Func<string?, Task<string?>>? PickFolder { get; set; }

    public bool Accepted { get; private set; }

    public double InnerRadiusMm { get; private set; }

    public double OuterRadiusMm { get; private set; }

    public long Sectors { get; private set; }

    public double PitchLinear { get; private set; }

    public double PitchQuadratic { get; private set; }

    public double PitchCubic { get; private set; }

    public event Action? CloseRequested;

    public static ProjectSetupViewModel ForProject(ProjectViewModel project)
    {
        ProjectSetupViewModel setup = new(true, project.Folder ?? AppPaths.DocumentsDirectory)
        {
            Name = project.Name,
            Family = project.Family,
        };
        setup._applyingPreset = true;
        setup.InnerText = Num.Format(project.InnerRadiusMm);
        setup.OuterText = Num.Format(project.OuterRadiusMm);
        setup.SectorsText = Num.Format(project.Sectors);
        setup.PitchLinearText = Num.Format(project.PitchLinear);
        setup.PitchQuadraticText = Num.Format(project.PitchQuadratic);
        setup.PitchCubicText = Num.Format(project.PitchCubic);
        setup.InvertMapping = project.InvertMapping;
        setup._applyingPreset = false;
        setup.SelectMatchingPreset();
        return setup;
    }

    public void SelectPreset(DiscPreset preset)
    {
        Family = preset.Family;
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == preset.Id) ?? SelectedPreset;
        if (!IsEditMode)
        {
            Name = preset.Name;
        }
    }

    partial void OnFamilyChanged(OpticalDiscFamily value)
    {
        OnPropertyChanged(nameof(IsCd));
        OnPropertyChanged(nameof(IsDvd));
        OnPropertyChanged(nameof(IsBluRay));
        OnPropertyChanged(nameof(SectorsLabel));
        OnPropertyChanged(nameof(SectorsHint));
        RefreshPresets(selectDefault: true);
    }

    partial void OnSelectedPresetChanged(DiscPreset? value)
    {
        OnPropertyChanged(nameof(PresetDescription));
        if (value is null || value.Id == CustomId)
        {
            return;
        }

        _applyingPreset = true;
        InnerText = Num.Format(value.InnerRadiusMm);
        OuterText = Num.Format(value.OuterRadiusMm);
        SectorsText = Num.Format(value.Sectors);
        PitchLinearText = Num.Format(value.PitchLinear);
        PitchQuadraticText = Num.Format(value.PitchQuadratic);
        PitchCubicText = Num.Format(value.PitchCubic);
        InvertMapping = value.InvertMapping;
        _applyingPreset = false;
        ErrorMessage = null;
    }

    partial void OnNameChanged(string value) => NotifyPath();

    partial void OnFolderChanged(string value) => NotifyPath();

    partial void OnInnerTextChanged(string value) => GeometryEdited();

    partial void OnOuterTextChanged(string value) => GeometryEdited();

    partial void OnSectorsTextChanged(string value) => GeometryEdited();

    partial void OnPitchLinearTextChanged(string value) => GeometryEdited();

    partial void OnPitchQuadraticTextChanged(string value) => GeometryEdited();

    partial void OnPitchCubicTextChanged(string value) => GeometryEdited();

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (PickFolder is null)
        {
            return;
        }

        string? picked = await PickFolder(Folder);
        if (!string.IsNullOrEmpty(picked))
        {
            Folder = picked;
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        try
        {
            Validate();
            Accepted = true;
            CloseRequested?.Invoke();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FormatException)
        {
            ErrorMessage = Translator.T(exception.Message);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Accepted = false;
        CloseRequested?.Invoke();
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Enter a project name.");
        }

        if (IsCreateMode)
        {
            if (string.IsNullOrWhiteSpace(Folder))
            {
                throw new ArgumentException("Choose a folder for the project.");
            }

            Folder = AppPaths.ResolveUserPath(Folder, AppPaths.DocumentsDirectory);
        }

        InnerRadiusMm = ParsePositive(InnerText, "Inner radius");
        OuterRadiusMm = ParsePositive(OuterText, "Outer radius");
        if (OuterRadiusMm <= InnerRadiusMm)
        {
            throw new ArgumentException("The outer radius must be larger than the inner radius.");
        }

        if (OuterRadiusMm > 60)
        {
            throw new ArgumentException("The outer radius cannot exceed 60 mm (a 120 mm disc).");
        }

        if (!Num.TryParse(SectorsText, out long sectors) || sectors <= 0)
        {
            throw new ArgumentException($"{SectorsLabel} must be a positive whole number.");
        }

        Sectors = sectors;
        if (Family == OpticalDiscFamily.Dvd && sectors % 16 != 0)
        {
            throw new ArgumentException("DVD total sectors must be a multiple of 16.");
        }

        if (Family == OpticalDiscFamily.BluRay)
        {
            if (sectors % ProjectDocument.BluRaySectorsPerCluster != 0)
            {
                throw new ArgumentException("BD total sectors must be a multiple of 32.");
            }

            if (sectors > ProjectDocument.BluRaySectorsPerLayer)
            {
                throw new ArgumentException($"BD total sectors cannot exceed {ProjectDocument.BluRaySectorsPerLayer:N0}.");
            }
        }

        PitchLinear = PitchQuadratic = PitchCubic = 0;
        if (Family == OpticalDiscFamily.Dvd)
        {
            PitchLinear = ParseAny(PitchLinearText, "Linear pitch coefficient");
            PitchQuadratic = ParseAny(PitchQuadraticText, "Quadratic pitch coefficient");
            PitchCubic = ParseAny(PitchCubicText, "Cubic pitch coefficient");
            DvdTrackGeometry.ValidatePitchCoefficients(PitchLinear, PitchQuadratic, PitchCubic);
        }
    }

    private const string CustomId = "custom";

    private void RefreshPresets(bool selectDefault)
    {
        Presets.Clear();
        foreach (DiscPreset preset in PresetCatalog.For(Family))
        {
            Presets.Add(preset);
        }

        Presets.Add(new DiscPreset(CustomId, Family, "Custom geometry", "Keep the values entered below.", 0, 0, 0));
        if (selectDefault)
        {
            SelectedPreset = Presets[0];
        }
    }

    private void SelectMatchingPreset()
    {
        DiscPreset? match = Presets.FirstOrDefault(preset => preset.Id != CustomId && Matches(preset));
        _applyingPreset = true;
        SelectedPreset = match ?? Presets[^1];
        _applyingPreset = false;
    }

    private bool Matches(DiscPreset preset)
        => Num.TryParse(InnerText, out double inner) && inner == preset.InnerRadiusMm
           && Num.TryParse(OuterText, out double outer) && outer == preset.OuterRadiusMm
           && Num.TryParse(SectorsText, out long sectors) && sectors == preset.Sectors
           && (Family != OpticalDiscFamily.Dvd
               || (Num.TryParse(PitchLinearText, out double linear) && linear == preset.PitchLinear
                   && Num.TryParse(PitchQuadraticText, out double quadratic) && quadratic == preset.PitchQuadratic
                   && Num.TryParse(PitchCubicText, out double cubic) && cubic == preset.PitchCubic));

    private void GeometryEdited()
    {
        ErrorMessage = null;
        if (_applyingPreset)
        {
            return;
        }

        if (SelectedPreset is { } preset && preset.Id != CustomId && !Matches(preset))
        {
            _applyingPreset = true;
            SelectedPreset = Presets[^1];
            _applyingPreset = false;
        }
    }

    private void NotifyPath()
    {
        OnPropertyChanged(nameof(ProjectFilePath));
        OnPropertyChanged(nameof(ProjectFileExists));
    }

    private static double ParsePositive(string text, string label)
        => Num.TryParse(text, out double value) && value > 0
            ? value
            : throw new ArgumentException($"{label} must be a positive number in millimetres.");

    private static double ParseAny(string text, string label)
        => string.IsNullOrWhiteSpace(text) ? 0
            : Num.TryParse(text, out double value) ? value
            : throw new ArgumentException($"{label} must be a number.");

    public static string SafeFileName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        return trimmed.Length == 0 ? "Untitled" : trimmed;
    }
}
