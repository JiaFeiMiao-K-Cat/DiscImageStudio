using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>What the shell asks of the project dialog: create a new project (optionally
/// preselecting a family and template preset) or edit the open project's settings.</summary>
public sealed record ProjectDialogRequest(
    bool EditMode,
    OpticalDiscFamily? PreselectFamily = null,
    string? PresetId = null);

/// <summary>The dialog's outcome. Null (from the handler) means the user cancelled.</summary>
public sealed record ProjectDialogResult(
    string Name,
    OpticalDiscFamily Family,
    double InnerRadiusMm,
    double OuterRadiusMm,
    long Sectors,
    double PitchLinear,
    double PitchQuadratic,
    double PitchCubic,
    string SavePath,
    bool BluRayInvertMapping = false);

/// <summary>The name the user gave a preset in the save dialog. Null (from the handler)
/// means the user cancelled.</summary>
public sealed record PresetSaveResult(string Name);

/// <summary>Backing state for ProjectDialogWindow. One instance per dialog; the window binds
/// straight to these properties and calls <see cref="ConfirmCommand"/> / cancel.</summary>
public partial class ProjectDialogViewModel : ObservableObject
{
    private readonly DiscParametersState _state;
    private readonly RecentTemplateStore _recentTemplates;
    private readonly bool _editMode;

    /// <summary>Left-pane tab order: recently confirmed templates first, then one category
    /// per disc family. User-saved presets live inside their family's category.</summary>
    public const int RecentCategoryIndex = 0;

    public IReadOnlyList<string> TemplateCategories { get; } = ["最近", "CD", "DVD", "蓝光"];

    public ProjectDialogViewModel(DiscParametersState state, ProjectDialogRequest request)
        : this(state, request, new RecentTemplateStore(), string.Empty)
    {
    }

    internal ProjectDialogViewModel(
        DiscParametersState state,
        ProjectDialogRequest request,
        RecentTemplateStore recentTemplates,
        string defaultProjectDirectory = "")
    {
        _state = state;
        _recentTemplates = recentTemplates;
        _editMode = request.EditMode;
        // Project settings start from the open project's BD polarity; switching to a preset
        // replaces it along with the geometry.
        _templateInvertMapping = _state.BdInvertMapping;
        // Commands first: the prefill below writes observable properties whose partial
        // changed handlers notify those commands.
        ConfirmCommand = new RelayCommand(Confirm, CanConfirm);
        BrowseSavePathCommand = new RelayCommand(BrowseSavePath);
        SaveAsPresetCommand = new RelayCommand(SaveAsPreset);
        DeletePresetCommand = new RelayCommand<ProjectTemplateTile>(DeletePreset, tile => tile is { IsUserPreset: true });
        FamilyIndex = (int)(request.PreselectFamily ?? OpticalDiscFamily.CompactDisc);
        BuildTemplates();
        if (_editMode)
        {
            Name = "工程设置";
        }
        else
        {
            ProjectTemplateTile? preselect = null;
            if (request.PresetId is not null)
            {
                preselect = AllTemplates.FirstOrDefault(t => t.PresetId == request.PresetId);
            }

            if (preselect is null && request.PreselectFamily is { } family)
            {
                preselect = BlankTileFor(family);
            }

            if (preselect is not null)
            {
                TemplateCategoryIndex = CategoryForFamily(preselect.Family);
                Family = preselect.Family;
                SelectedTemplate = preselect;
            }
            else
            {
                // No preselection: open on the most recently used template, falling back to
                // the CD category when nothing has been confirmed yet.
                ProjectTemplateTile? recent = RecentVisibleTemplates().FirstOrDefault();
                if (recent is not null)
                {
                    TemplateCategoryIndex = RecentCategoryIndex;
                    Family = recent.Family;
                    SelectedTemplate = recent;
                }
                else
                {
                    TemplateCategoryIndex = CategoryForFamily(OpticalDiscFamily.CompactDisc);
                    Family = OpticalDiscFamily.CompactDisc;
                    SelectedTemplate = BlankTileFor(OpticalDiscFamily.CompactDisc);
                }
            }
        }

        // The configured default is a preference, not a hard requirement: a folder that was
        // deleted or lives on a disconnected drive falls back to Documents instead of
        // pointing the save path at an invalid location.
        SaveDirectory = !string.IsNullOrWhiteSpace(defaultProjectDirectory)
                        && Directory.Exists(defaultProjectDirectory)
            ? defaultProjectDirectory
            : ProjectJsonStore.GetDefaultProjectsDirectory();
    }

    public bool EditMode => _editMode;

    public string DialogTitle => _editMode ? "工程设置" : "新建工程";

    public string ConfirmLabel => _editMode ? "确定" : "创建工程";

    /// <summary>Category tabs belong to the create path only; project settings keep the
    /// project's family and list just that family's presets.</summary>
    public System.Windows.Visibility TemplateTabsVisibility => _editMode
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    public System.Windows.Visibility IsDvdFieldsVisibility => IsDvd
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    /// <summary>Live values for the annulus diagram; unparsable fields fall back to the
    /// blank-project defaults so the diagram never blanks out mid-edit.</summary>
    public double InnerRadiusValue => TryParseMm(InnerRadiusText)
        ?? (IsBluRay ? 24.0 : IsDvd ? 24.0 : 24.5);

    public double OuterRadiusValue => TryParseMm(OuterRadiusText)
        ?? (IsBluRay ? 58.0 : IsDvd ? 58.0 : 56.8);

    public string SaveFileNameHint
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(Name) ? "未命名" : Name.Trim();
            return $"将保存为：{SanitizeFileName(name)}{ProjectJsonStore.FileExtension}";
        }
    }

    /// <summary>Template picking exists on both paths; in project settings the family is fixed
    /// and the list only offers presets of that family.</summary>
    public System.Windows.Visibility EditModeVisibility => _editMode
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    // ---- Preset catalog ----

    [ObservableProperty]
    private string _savedPresetMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCd), nameof(IsDvd), nameof(IsBluRay), nameof(FamilyIndex),
        nameof(IsDvdFieldsVisibility), nameof(IsBluRayFieldsVisibility),
        nameof(InnerRadiusValue), nameof(OuterRadiusValue), nameof(FamilyBadge))]
    private OpticalDiscFamily _family = OpticalDiscFamily.CompactDisc;

    public bool IsCd => Family == OpticalDiscFamily.CompactDisc;

    public bool IsDvd => Family == OpticalDiscFamily.Dvd;

    public bool IsBluRay => Family == OpticalDiscFamily.BluRay;

    /// <summary>Family chip for the edit mode's parameter card (create mode shows the family
    /// through the open tab and the selected card instead).</summary>
    public string FamilyBadge => IsDvd ? "DVD" : IsBluRay ? "BD" : "CD";

    /// <summary>Three-option segmented selector binding.</summary>
    public int FamilyIndex
    {
        get => (int)Family;
        set => Family = (OpticalDiscFamily)Math.Clamp(value, 0, 2);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveFileNameHint))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InnerRadiusValue))]
    private string _innerRadiusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OuterRadiusValue))]
    private string _outerRadiusText = string.Empty;

    [ObservableProperty]
    private string _sectorsText = string.Empty;

    [ObservableProperty]
    private string _pitchLinearText = "0";

    [ObservableProperty]
    private string _pitchQuadraticText = "0";

    [ObservableProperty]
    private string _pitchCubicText = "0";

    [ObservableProperty]
    private string _saveDirectory;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SaveFileNameHint))]
    private ProjectTemplateTile? _selectedTemplate;

    /// <summary>Which left-pane tab is open: 最近, CD, DVD or 蓝光.</summary>
    [ObservableProperty]
    private int _templateCategoryIndex = RecentCategoryIndex;

    public bool HasSelection => SelectedTemplate is not null;

    /// <summary>The open tab's cards; the 最近 tab joins the persisted preset ids against the
    /// catalog, so deleted presets simply vanish from the list.</summary>
    public IReadOnlyList<ProjectTemplateTile> VisibleTemplates { get; private set; } = [];

    public bool HasVisibleTemplates => VisibleTemplates.Count > 0;

    public string EmptyCategoryHint => TemplateCategoryIndex == RecentCategoryIndex
        ? "还没有最近使用的模板。"
        : string.Empty;

    public System.Windows.Visibility EmptyCategoryVisibility => HasVisibleTemplates
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    public System.Windows.Visibility IsBluRayFieldsVisibility => IsBluRay
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public IReadOnlyList<ProjectTemplateTile> AllTemplates { get; private set; } = [];

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand BrowseSavePathCommand { get; }

    /// <summary>Edit mode only: writes the fields' current geometry into the user preset
    /// catalog, so a tuned project's parameters can seed future projects.</summary>
    public RelayCommand SaveAsPresetCommand { get; }

    /// <summary>Removes a user preset from the catalog; the context menu only offers it on
    /// cards carrying the 自定 badge, matching the recent-project remove affordance.</summary>
    public RelayCommand<ProjectTemplateTile> DeletePresetCommand { get; }

    /// <summary>Set by the window: asks for the preset's name before a save lands in the
    /// catalog. Null result means the user cancelled.</summary>
    internal Func<PresetSaveResult?>? ShowPresetSaveDialog { get; set; }

    /// <summary>Set by the window: folder picker for the save location.</summary>
    internal Func<string, string?>? PickSaveDirectory { get; set; }

    /// <summary>Set by the window: closes the dialog with a positive result.</summary>
    internal Action? CloseWithConfirm { get; set; }

    /// <summary>Set by the window: asks the user to confirm that switching to another preset
    /// replaces the values currently in the form.</summary>
    internal Func<ProjectTemplateTile, Task<bool>>? ConfirmPresetSwitch { get; set; }

    /// <summary>The result, valid after ConfirmCommand succeeded and the dialog closed.</summary>
    public ProjectDialogResult? Result { get; private set; }

    private bool _templateInvertMapping;
    private bool _suppressFamilyApply;
    private bool _suppressPresetConfirm;
    // Rebuilding the catalog replaces every tile instance, so the ListBox clears and restores
    // its selection around the rebuild. Without this guard those transient events would
    // re-apply a preset over values the user has already edited (or over the open project).
    private bool _suppressTemplateApply;

    /// <summary>Edit mode: the preset whose values the form currently shows. Null until the
    /// user switches to one; the open project's own values are not a catalog preset.</summary>
    private ProjectTemplateTile? _appliedTemplate;

    partial void OnTemplateCategoryIndexChanged(int value)
    {
        if (_editMode)
        {
            return;
        }

        BuildVisibleTemplates();
        // Keep a selection that lives in the new tab; otherwise snap to its first card so
        // the parameter pane always shows the template the user is looking at.
        if (SelectedTemplate is null || !VisibleTemplates.Contains(SelectedTemplate))
        {
            SelectedTemplate = VisibleTemplates.FirstOrDefault();
        }
    }

    partial void OnSelectedTemplateChanged(ProjectTemplateTile? value)
    {
        if (_suppressTemplateApply)
        {
            return;
        }

        if (_editMode)
        {
            // Clicking a card in project settings replaces hand-tuned values, so nothing is
            // applied until the user accepts the overwrite through the modal.
            if (!_suppressPresetConfirm && value is not null
                && !ReferenceEquals(value, _appliedTemplate))
            {
                _ = ApplySwitchedPresetAsync(value);
            }

            return;
        }

        ApplySelectedTemplate(value);
    }

    private void ApplySelectedTemplate(ProjectTemplateTile? value)
    {
        if (value is not null && value.Family != Family)
        {
            // Selecting another family's template switches the form too, without letting
            // the family handler re-apply the previous template over the new selection.
            _suppressFamilyApply = true;
            try
            {
                Family = value.Family;
            }
            finally
            {
                _suppressFamilyApply = false;
            }
        }

        ApplyTemplate();
    }

    partial void OnFamilyChanged(OpticalDiscFamily value)
    {
        if (_suppressFamilyApply)
        {
            return;
        }

        if (_editMode)
        {
            // The project's family arrives after construction (MainWindow prefills it), so
            // the preset column follows it instead of applying geometry over the fields.
            BuildVisibleTemplates();
            return;
        }

        ApplyTemplate();
    }

    /// <summary>Asks before the switch; a refusal puts the card selection back so the form and
    /// the highlighted card keep describing the same values.</summary>
    private async Task ApplySwitchedPresetAsync(ProjectTemplateTile tile)
    {
        ProjectTemplateTile? previous = _appliedTemplate;
        bool confirmed;
        try
        {
            // No hook (snapshots) or a confirmation that cannot open both count as a refusal:
            // the parameter fields must never change without the user's answer.
            confirmed = ConfirmPresetSwitch is not null && await ConfirmPresetSwitch(tile);
        }
        catch (InvalidOperationException)
        {
            confirmed = false;
        }

        if (!confirmed)
        {
            _suppressPresetConfirm = true;
            try
            {
                SelectedTemplate = previous;
            }
            finally
            {
                _suppressPresetConfirm = false;
            }

            return;
        }

        _appliedTemplate = tile;
        SavedPresetMessage = string.Empty;
        ErrorMessage = string.Empty;
        ApplyTemplate();
    }

    partial void OnNameChanged(string value) => ConfirmCommand.NotifyCanExecuteChanged();

    private int CategoryForFamily(OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.Dvd => 2,
        OpticalDiscFamily.BluRay => 3,
        _ => 1,
    };

    /// <summary>The blank card reads differently per path: it creates a project from the
    /// family defaults, or in project settings resets the fields to them.</summary>
    private ProjectTemplateTile BlankTileFor(OpticalDiscFamily family)
    {
        string title = _editMode ? "默认参数" : "空白工程";
        string detail = _editMode ? "恢复该盘型的默认几何" : "从默认参数开始";
        return new(null, title, detail, family);
    }

    private void BuildTemplates()
    {
        // Every catalog preset is listed under its family tab, user-saved ones included:
        // a preset saved from the project settings reappears in the category it belongs to.
        List<ProjectTemplateTile> templates = [BlankTileFor(OpticalDiscFamily.CompactDisc)];
        foreach (CdDiscPreset preset in _state.CdPresets.Where(p => !p.IsCustom))
        {
            templates.Add(new(
                preset.Id,
                preset.DisplayName,
                $"{preset.Sectors:N0} 扇区 · {preset.InnerRadiusMm:0.#}–{preset.OuterRadiusMm:0.#} mm",
                OpticalDiscFamily.CompactDisc));
        }

        templates.Add(BlankTileFor(OpticalDiscFamily.Dvd));
        foreach (DvdDiscPreset preset in _state.DvdPresets.Where(p => !p.IsCustom))
        {
            templates.Add(new(
                preset.Id,
                preset.DisplayName,
                $"{preset.TotalSectors:N0} 扇区 · {preset.InnerRadiusMm:0.#}–{preset.OuterRadiusMm:0.#} mm",
                OpticalDiscFamily.Dvd));
        }

        templates.Add(BlankTileFor(OpticalDiscFamily.BluRay));
        foreach (BluRayDiscPreset preset in _state.BluRayPresets.Where(p => !p.IsCustom))
        {
            BluRayDiscParameters geometry = preset.CreateParameters();
            templates.Add(new(
                preset.Id,
                preset.DisplayName,
                $"{geometry.TotalSectors:N0} 扇区 · {geometry.InnerRadiusMm:0.#}–{geometry.OuterRadiusMm:0.#} mm"
                + (preset.InvertMapping ? " · 反转 3T/6T" : string.Empty),
                OpticalDiscFamily.BluRay));
        }

        AllTemplates = templates;
        OnPropertyChanged(nameof(AllTemplates));
        BuildVisibleTemplates();
    }

    private List<ProjectTemplateTile> RecentVisibleTemplates()
    {
        List<ProjectTemplateTile> tiles = [];
        foreach (string presetId in _recentTemplates.Load())
        {
            ProjectTemplateTile? tile = AllTemplates.FirstOrDefault(t => t.PresetId == presetId);
            if (tile is not null)
            {
                tiles.Add(tile);
            }
        }

        return tiles;
    }

    private void BuildVisibleTemplates()
    {
        VisibleTemplates = _editMode
            ? AllTemplates.Where(t => t.Family == Family).ToList()
            : TemplateCategoryIndex == RecentCategoryIndex
                ? RecentVisibleTemplates()
                : AllTemplates.Where(t => CategoryForFamily(t.Family) == TemplateCategoryIndex).ToList();
        OnPropertyChanged(nameof(VisibleTemplates));
        OnPropertyChanged(nameof(HasVisibleTemplates));
        OnPropertyChanged(nameof(EmptyCategoryHint));
        OnPropertyChanged(nameof(EmptyCategoryVisibility));
    }

    /// <summary>Template or family switches write the preset's geometry into the fields;
    /// keeping them as text lets the user adjust before confirming. The project name only
    /// follows the template on the create path: project settings keep the open name.</summary>
    private void ApplyTemplate()
    {
        ProjectTemplateTile? tile = SelectedTemplate;
        if (!_editMode && tile is not null && string.IsNullOrWhiteSpace(Name))
        {
            Name = tile.PresetId is null ? "未命名" : tile.Title;
        }

        _templateInvertMapping = Family == OpticalDiscFamily.BluRay && _state.BdInvertMapping;
        if (tile?.PresetId is null)
        {
            switch (Family)
            {
                case OpticalDiscFamily.Dvd:
                    InnerRadiusText = "24.0";
                    OuterRadiusText = "58.0";
                    SectorsText = "2295104";
                    PitchLinearText = "0";
                    PitchQuadraticText = "0";
                    PitchCubicText = "0";
                    break;
                case OpticalDiscFamily.BluRay:
                    InnerRadiusText = "24.0";
                    OuterRadiusText = "58.0";
                    SectorsText = "12219392";
                    PitchLinearText = "0";
                    PitchQuadraticText = "0";
                    PitchCubicText = "0";
                    _templateInvertMapping = false;
                    break;
                default:
                    InnerRadiusText = "24.5";
                    OuterRadiusText = "56.8";
                    SectorsText = "359849";
                    break;
            }
        }
        else if (Family == OpticalDiscFamily.Dvd)
        {
            DvdDiscPreset preset = _state.DvdPresets.FirstOrDefault(p => p.Id == tile.PresetId)
                ?? DvdDiscPreset.Manual;
            if (!preset.IsCustom)
            {
                InnerRadiusText = preset.InnerRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                OuterRadiusText = preset.OuterRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                SectorsText = preset.TotalSectors.ToString(CultureInfo.InvariantCulture);
                PitchLinearText = preset.PitchLinear.ToString("R", CultureInfo.InvariantCulture);
                PitchQuadraticText = preset.PitchQuadratic.ToString("R", CultureInfo.InvariantCulture);
                PitchCubicText = preset.PitchCubic.ToString("R", CultureInfo.InvariantCulture);
            }
        }
        else if (Family == OpticalDiscFamily.BluRay)
        {
            BluRayDiscPreset preset = _state.BluRayPresets.FirstOrDefault(p => p.Id == tile.PresetId)
                ?? BluRayDiscPreset.Manual;
            if (!preset.IsCustom)
            {
                BluRayDiscParameters geometry = preset.CreateParameters();
                InnerRadiusText = geometry.InnerRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                OuterRadiusText = geometry.OuterRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                SectorsText = geometry.TotalSectors.ToString(CultureInfo.InvariantCulture);
                PitchLinearText = "0";
                PitchQuadraticText = "0";
                PitchCubicText = "0";
                _templateInvertMapping = preset.InvertMapping;
            }
        }
        else
        {
            CdDiscPreset preset = _state.CdPresets.FirstOrDefault(p => p.Id == tile.PresetId)
                ?? CdDiscPreset.Manual;
            if (!preset.IsCustom)
            {
                InnerRadiusText = preset.InnerRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                OuterRadiusText = preset.OuterRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                SectorsText = preset.Sectors.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private bool CanConfirm() => !string.IsNullOrWhiteSpace(Name);

    private void BrowseSavePath()
    {
        string? picked = PickSaveDirectory?.Invoke(SaveDirectory);
        if (picked is not null)
        {
            SaveDirectory = picked;
        }
    }

    private void Confirm()
    {
        try
        {
            Result = BuildResult();
            ErrorMessage = string.Empty;
            // The confirmed template seeds the 最近 tab for the next new project. Project
            // settings only contribute when a preset was switched to; the open values are not
            // a catalog entry of their own.
            ProjectTemplateTile? recorded = _editMode ? _appliedTemplate : SelectedTemplate;
            if (recorded?.PresetId is { } presetId)
            {
                _recentTemplates.Record(presetId);
            }

            CloseWithConfirm?.Invoke();
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or FormatException
                                              or OverflowException
                                              or ArgumentOutOfRangeException)
        {
            ErrorMessage = exception.Message;
        }
    }

    private ProjectDialogResult BuildResult()
    {
        string name = Name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("请填写工程名。");
        }

        (double inner, double outer, long sectors, double pitchLinear, double pitchQuadratic, double pitchCubic)
            = ParseGeometry();
        string directory = SaveDirectory.Trim();
        if (directory.Length == 0)
        {
            throw new ArgumentException("请选择保存位置。");
        }

        string fileName = $"{SanitizeFileName(name)}{ProjectJsonStore.FileExtension}";
        string savePath = Path.Combine(directory, fileName);
        return new ProjectDialogResult(
            name,
            Family,
            inner,
            outer,
            sectors,
            pitchLinear,
            pitchQuadratic,
            pitchCubic,
            savePath,
            BluRayInvertMapping: Family == OpticalDiscFamily.BluRay && _templateInvertMapping);
    }

    /// <summary>Shared by confirming the dialog and saving a preset: the geometry fields with
    /// the same rules both paths enforce.</summary>
    private (double Inner, double Outer, long Sectors, double PitchLinear, double PitchQuadratic, double PitchCubic)
        ParseGeometry()
    {
        double inner = ParseField(InnerRadiusText, "内半径");
        double outer = ParseField(OuterRadiusText, "外半径");
        long sectors = long.Parse(SectorsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (sectors <= 0)
        {
            throw new ArgumentException("扇区数必须是正整数。");
        }

        if (IsDvd && sectors % 16 != 0)
        {
            throw new ArgumentException("DVD 的扇区数必须是 16 的倍数。");
        }

        if (IsBluRay)
        {
            if (sectors % ProjectDocument.BluRaySectorsPerCluster != 0)
            {
                throw new ArgumentException("蓝光的扇区数必须是 32 的倍数。");
            }

            if (sectors > ProjectDocument.BluRaySectorsPerLayer)
            {
                throw new ArgumentException(
                    $"蓝光扇区数不能超过单层容量 {ProjectDocument.BluRaySectorsPerLayer:N0}。");
            }

            if (outer > 60)
            {
                throw new ArgumentException("蓝光外半径不能超过 60 mm。");
            }
        }

        if (outer <= inner)
        {
            throw new ArgumentException("外半径必须大于内半径。");
        }

        double pitchLinear = 0, pitchQuadratic = 0, pitchCubic = 0;
        if (IsDvd)
        {
            // Pitch coefficients are deltas, so zero is the normal default; only finiteness
            // is required here, unlike the strictly positive radii.
            pitchLinear = ParseNumber(PitchLinearText, "相对一次项");
            pitchQuadratic = ParseNumber(PitchQuadraticText, "相对二次项");
            pitchCubic = ParseNumber(PitchCubicText, "相对三次项");
            DvdTrackGeometry.ValidatePitchCoefficients(pitchLinear, pitchQuadratic, pitchCubic);
        }

        return (inner, outer, sectors, pitchLinear, pitchQuadratic, pitchCubic);
    }

    private void SaveAsPreset()
    {
        try
        {
            // Geometry validates first: an invalid set reports here instead of after the
            // user has already typed a name into the save dialog.
            (double inner, double outer, long sectors, double pitchLinear, double pitchQuadratic, double pitchCubic)
                = ParseGeometry();
            PresetSaveResult? confirmed = ShowPresetSaveDialog?.Invoke();
            if (confirmed is null)
            {
                return;
            }

            string displayName = _state.AddUserPreset(
                Family,
                confirmed.Name,
                inner,
                outer,
                sectors,
                pitchLinear,
                pitchQuadratic,
                pitchCubic,
                invertMapping: Family == OpticalDiscFamily.BluRay && _templateInvertMapping);
            ErrorMessage = string.Empty;
            SavedPresetMessage = $"已保存为预设：{displayName}";
            if (_editMode)
            {
                RefreshTemplatesAfterSave();
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or FormatException
                                              or OverflowException
                                              or ArgumentOutOfRangeException
                                              or InvalidOperationException
                                              or InvalidDataException)
        {
            SavedPresetMessage = string.Empty;
            ErrorMessage = $"无法保存预设：{exception.Message}";
        }
    }

    /// <summary>Rebuilds the catalog after a save so the new preset appears in the list it was
    /// saved from. The rebuild replaces the tile instances, so the highlight and the applied
    /// preset follow their ids instead of their old references.</summary>
    private void RefreshTemplatesAfterSave()
    {
        string? appliedId = _appliedTemplate?.PresetId;
        BuildTemplates();
        if (_appliedTemplate is null)
        {
            return;
        }

        ProjectTemplateTile? refreshed = AllTemplates.FirstOrDefault(
            t => t.Family == Family && t.PresetId == appliedId);
        _suppressPresetConfirm = true;
        try
        {
            SelectedTemplate = refreshed;
        }
        finally
        {
            _suppressPresetConfirm = false;
        }

        _appliedTemplate = refreshed;
    }

    /// <summary>Deletes one user preset and rebuilds the catalog. The open form keeps its
    /// values when another card was removed; removing the card whose values the form shows
    /// leaves those values in place but drops the highlight with the catalog entry.</summary>
    private void DeletePreset(ProjectTemplateTile? tile)
    {
        if (tile?.PresetId is not { } presetId || !tile.IsUserPreset)
        {
            return;
        }

        string? selectedId = SelectedTemplate?.PresetId;
        bool wasSelected = string.Equals(selectedId, presetId, StringComparison.Ordinal);
        string? appliedId = _appliedTemplate?.PresetId;
        bool appliedWasBlank = _appliedTemplate is { PresetId: null };
        bool wasApplied = string.Equals(appliedId, presetId, StringComparison.Ordinal);

        try
        {
            string removedName = _state.RemoveUserPreset(presetId);
            _recentTemplates.Remove(presetId);
            ErrorMessage = string.Empty;

            _suppressTemplateApply = true;
            try
            {
                BuildTemplates();
                if (_editMode)
                {
                    _appliedTemplate = wasApplied
                        ? null
                        : appliedWasBlank
                            ? AllTemplates.FirstOrDefault(t => t.Family == Family && t.PresetId is null)
                            : appliedId is null
                                ? null
                                : AllTemplates.FirstOrDefault(
                                    t => t.Family == Family && t.PresetId == appliedId);
                    SelectedTemplate = _appliedTemplate;
                }
                else
                {
                    SelectedTemplate = wasSelected
                        ? VisibleTemplates.FirstOrDefault()
                        : selectedId is null
                            ? null
                            : VisibleTemplates.FirstOrDefault(t => t.PresetId == selectedId);
                }
            }
            finally
            {
                _suppressTemplateApply = false;
            }

            // A removed selected card resets the fields to the card that takes its place;
            // every other deletion must leave the hand-tuned values alone.
            if (!_editMode && wasSelected)
            {
                ApplySelectedTemplate(SelectedTemplate);
            }

            SavedPresetMessage = $"已删除预设：{removedName}";
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or NotSupportedException
                                              or InvalidOperationException)
        {
            SavedPresetMessage = string.Empty;
            ErrorMessage = $"无法删除预设：{exception.Message}";
        }
    }

    private static double? TryParseMm(string raw)
    {
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            if (double.IsFinite(value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static double ParseField(string raw, string field)
        => TryParseMm(raw) ?? throw new FormatException($"{field}不是有效数字。");

    private static double ParseNumber(string raw, string field)
    {
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            if (double.IsFinite(value))
            {
                return value;
            }
        }

        throw new FormatException($"{field}不是有效数字。");
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "工程" : cleaned;
    }
}

/// <summary>One template card in the dialog: the default card (PresetId null) or a preset
/// from the editable catalog, listed under its family's tab or column.</summary>
public sealed record ProjectTemplateTile(
    string? PresetId,
    string Title,
    string Detail,
    OpticalDiscFamily Family)
{
    public string FamilyLabel => Family switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };

    public bool IsUserPreset => PresetId?.StartsWith("user-", StringComparison.Ordinal) == true;

    public System.Windows.Visibility UserBadgeVisibility => IsUserPreset
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public string IconKind => PresetId is null
        ? "FilePlus"
        : Family switch
        {
            OpticalDiscFamily.Dvd => "Disc2",
            OpticalDiscFamily.BluRay => "Disc3",
            _ => "Disc",
        };
}
