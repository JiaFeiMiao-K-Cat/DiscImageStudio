using DiscImageStudio.Localization;
using System.Collections.ObjectModel;
using System.IO;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Iso;
using DiscImageStudio.Services;
using DiscImageStudio.Dvd;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// Single shared parameter store: the DVD/CD pages and the live preview page bind to the
/// same properties, replacing the previous 27 textbox/2 combobox pairs with one source.
/// </summary>
public partial class DiscParametersState : ObservableObject
{
    private const string DefaultCdVolumeLabel = "CD_IMAGE";
    private const string DefaultDvdVolumeLabel = "DISC_IMAGE";
    private const string DefaultBluRayVolumeLabel = "BD_IMAGE";

    private static CdDiscPreset ManualCdPreset => CdDiscPreset.Manual with
    {
        DisplayName = L.T(CdDiscPreset.Manual.DisplayName),
        Description = L.T(CdDiscPreset.Manual.Description),
    };

    private static DvdDiscPreset ManualDvdPreset => DvdDiscPreset.Manual with
    {
        DisplayName = L.T(DvdDiscPreset.Manual.DisplayName),
        Description = L.T(DvdDiscPreset.Manual.Description),
    };

    private static BluRayDiscPreset ManualBluRayPreset => BluRayDiscPreset.Manual with
    {
        DisplayName = L.T(BluRayDiscPreset.Manual.DisplayName),
    };

    private readonly string _discPresetJsonPath;

    // The path overload is what keeps preset-editing tests off the real user catalog.
    public DiscParametersState()
        : this(DiscPresetJsonStore.GetDefaultPath())
    {
    }

    internal DiscParametersState(string discPresetJsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discPresetJsonPath);
        _discPresetJsonPath = discPresetJsonPath;
    }

    private IReadOnlyList<CdDiscPreset> _cdPresets = [ManualCdPreset];
    private IReadOnlyList<DvdDiscPreset> _dvdPresets = [ManualDvdPreset];
    private IReadOnlyList<BluRayDiscPreset> _bluRayPresets = [ManualBluRayPreset];

    private bool _isApplyingDiscPreset;

    // CD page / live preview shared fields. Linear velocity, start angle and the image
    // outer radius are physical constants of the scheme (1200 mm/s, 0°, 57.5 mm) and are
    // deliberately not parameters. The live preview reads back the generated track, so
    // CdTrackPath points at the burn artifact while CdImagePath stays generation-only.
    [ObservableProperty] private string _cdImagePath = string.Empty;
    [ObservableProperty] private string _cdTrackPath = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCdWaveOutput))]
    private string _cdOutputPath = string.Empty;
    [ObservableProperty] private bool _cdWriteCue = true;
    [ObservableProperty] private string _cdInnerRadius = "24.5";
    [ObservableProperty] private string _cdOuterRadius = "56.8";
    [ObservableProperty] private string _cdPitchLinear = "0";
    [ObservableProperty] private string _cdPitchQuadratic = "0";
    [ObservableProperty] private string _cdPitchCubic = "0";
    [ObservableProperty] private string _cdSectors = "359849";
    [ObservableProperty] private string _cdUsableSectors = string.Empty;
    [ObservableProperty] private string _cdPreviewSize = "1600";
    [ObservableProperty] private string _cdSamplesPerSector = "16";
    [ObservableProperty] private bool _cdInterleave = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCdFiles), nameof(IsCdAudioMode), nameof(IsCdWaveOutput),
        nameof(CdOutputLabel), nameof(CdOutputHint))]
    private string _cdDataDirectory = string.Empty;
    [ObservableProperty] private bool _cdFilesAtOuterRing;
    [ObservableProperty] private string _cdVolumeLabel = DefaultCdVolumeLabel;

    // Second parameter set of the live preview page's calibration mode: the disc the reader
    // follows, as opposed to the generated geometry above. It is preview-only and never
    // feeds a generation job, so it is not a preset field either.
    [ObservableProperty] private string _cdActualInnerRadius = "24.3";
    [ObservableProperty] private string _cdActualOuterRadius = "56.6";
    [ObservableProperty] private string _cdActualPitchLinear = "0";
    [ObservableProperty] private string _cdActualPitchQuadratic = "0";
    [ObservableProperty] private string _cdActualPitchCubic = "0";

    // DVD page / live preview shared fields. Channel-bit length (133.3 nm) and start
    // angle (0°) are fixed physical constants, same as the CD constants above. The live
    // preview reads back the generated ISO through DvdIsoPath.
    [ObservableProperty] private string _dvdImagePath = string.Empty;
    [ObservableProperty] private string _dvdIsoPath = string.Empty;
    [ObservableProperty] private string _dvdOutputPath = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDvdFiles))] private string _dvdDataDirectory = string.Empty;
    [ObservableProperty] private bool _dvdFilesAtOuterRing;
    [ObservableProperty] private bool _dvdReadCompatibility;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary), nameof(DvdActualPitchSummary))] private string _dvdTotalSectors = "2295104";
    [ObservableProperty] private string _dvdUsableSectors = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdInnerRadius = "24.0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdOuterRadius = "58.0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdPitchLinear = "0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdPitchQuadratic = "0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdPitchCubic = "0";
    [ObservableProperty] private string _dvdVolumeLabel = DefaultDvdVolumeLabel;
    [ObservableProperty] private string _dvdPreviewSize = "1200";
    [ObservableProperty] private string _dvdSamplesPerSector = "4";

    /// <summary>Second parameter set of the DVD calibration preview, same role as the CD one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualInnerRadius = "24.0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualOuterRadius = "58.0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualPitchLinear = "0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualPitchQuadratic = "0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualPitchCubic = "0";

    // BD-R page / live preview shared fields. Channel-bit length (74.5 nm), start angle
    // (0°) and single-layer capacity are fixed physical constants of the current module.
    [ObservableProperty] private string _bdImagePath = string.Empty;
    [ObservableProperty] private string _bdIsoPath = string.Empty;
    [ObservableProperty] private string _bdOutputPath = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBluRayFiles))] private string _bdDataDirectory = string.Empty;
    [ObservableProperty] private bool _bdFilesAtOuterRing;
    [ObservableProperty] private string _bdVolumeLabel = DefaultBluRayVolumeLabel;
    [ObservableProperty] private string _bdTotalSectors = "12219392";
    [ObservableProperty] private string _bdUsableSectors = string.Empty;
    [ObservableProperty] private string _bdInnerRadius = "24.0";
    [ObservableProperty] private string _bdOuterRadius = "58.0";
    [ObservableProperty] private string _bdPitchLinear = "0";
    [ObservableProperty] private string _bdPitchQuadratic = "0";
    [ObservableProperty] private string _bdPitchCubic = "0";
    [ObservableProperty] private string _bdPreviewSize = "2048";
    [ObservableProperty] private string _bdActualInnerRadius = "24.0";
    [ObservableProperty] private string _bdActualOuterRadius = "58.0";
    [ObservableProperty] private string _bdActualPitchLinear = "0";
    [ObservableProperty] private string _bdActualPitchQuadratic = "0";
    [ObservableProperty] private string _bdActualPitchCubic = "0";
    [ObservableProperty] private bool _bdInvertMapping;
    // Preview-only drawing range restored from a sidecar; "0"/"0" means the full data zone.
    [ObservableProperty] private string _bdDrawingStartSector = "0";
    [ObservableProperty] private string _bdDrawingEndSectorExclusive = "0";

    [ObservableProperty] private string _cdPresetHint = string.Empty;
    [ObservableProperty] private string _dvdPresetHint = string.Empty;
    [ObservableProperty] private string _bluRayPresetHint = string.Empty;
    [ObservableProperty] private CdDiscPreset _selectedCdPreset = ManualCdPreset;
    [ObservableProperty] private DvdDiscPreset _selectedDvdPreset = ManualDvdPreset;
    [ObservableProperty] private BluRayDiscPreset _selectedBluRayPreset = ManualBluRayPreset;

    // Tone mapping shared by both families. New projects open at 75% dither: enough to keep
    // midtones at viewing distance without the grain full strength adds. A project file
    // written before tone mapping existed applies its absent section as 0, so old projects
    // keep their original hard-threshold output until the user moves the slider.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToneDitherText), nameof(ToneSummary))]
    private double _toneDitherStrength = 0.75;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFourLevelSelected), nameof(ToneSummary))]
    private int _toneLevels = ToneProfile.TwoLevels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToneBrightnessText), nameof(ToneSummary))]
    private double _toneBrightness;

    /// <summary>Decode the source through the sRGB transfer function before thresholding.
    /// This changes the black/white dot density, not the generated output level.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToneSummary))]
    private bool _toneSrgbLinearization;

    /// <summary>Two-way flag of the 2/4-level segmented control (the right option is 4 levels).</summary>
    public bool IsFourLevelSelected
    {
        get => ToneLevels == ToneProfile.FourLevels;
        set => ToneLevels = value ? ToneProfile.FourLevels : ToneProfile.TwoLevels;
    }

    /// <summary>Snapshot of the UI fields for the generators; clamped so a
    /// mid-edit slider value can never reach the engines.</summary>
    public ToneProfile Tone => new ToneProfile(
        Math.Clamp(ToneDitherStrength, 0, 1),
        ToneLevels == ToneProfile.FourLevels ? ToneProfile.FourLevels : ToneProfile.TwoLevels,
        Math.Clamp(ToneBrightness, ToneProfile.MinimumBrightness, ToneProfile.MaximumBrightness),
        ToneSrgbLinearization);

    /// <summary>DVD's dispersion pool has only the black and white textures; a CD project's
    /// 4-level setting must not leak into a DVD job.</summary>
    public ToneProfile DvdTone => Tone with { Levels = ToneProfile.TwoLevels };

    /// <summary>BD runs a single binary pixel pipeline; intermediate CD levels do not apply.</summary>
    public ToneProfile BluRayTone => Tone with { Levels = ToneProfile.TwoLevels };

    public string ToneDitherText => $"{ToneDitherStrength * 100:0}%";

    public string ToneBrightnessText => ToneBrightness > 0
        ? $"+{ToneBrightness:0}"
        : $"{ToneBrightness:0}";

    public string ToneSummary => L.F($"{ToneLevels} 级 · {ToneDitherText} 抖动")
        + (ToneSrgbLinearization ? L.T(" · sRGB 线性化") : string.Empty);

    /// <summary>A CUE sheet only accompanies PCM WAV tracks, so the CD page offers its
    /// toggle only while the chosen output is a .wav file.</summary>
    public bool IsCdWaveOutput => IsCdAudioMode && Path.GetExtension(CdOutputPath.Trim())
        .Equals(".wav", StringComparison.OrdinalIgnoreCase);

    public bool HasCdFiles => !string.IsNullOrWhiteSpace(CdDataDirectory);

    public bool IsCdAudioMode => !HasCdFiles;

    /// <summary>Label of the CD output the design page's pipeline card offers; the artifact
    /// switches between a data ISO and an audio track as the on-disc folder appears.</summary>
    public string CdOutputLabel => HasCdFiles ? L.T("输出数据盘 (.iso)") : L.T("输出音轨 (.wav / .raw)");

    public string CdOutputHint => HasCdFiles
        ? L.T("包含随盘文件，导出为 CD 数据盘 ISO。")
        : L.T("无随盘文件时生成音轨，推荐 WAV 格式。");

    partial void OnCdDataDirectoryChanged(string value) => NormalizeCdOutputFormat();

    internal void NormalizeCdOutputFormat()
    {
        string output = CdOutputPath.Trim();
        if (output.Length == 0) return;
        string extension = Path.GetExtension(output);
        if (HasCdFiles && (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".raw", StringComparison.OrdinalIgnoreCase)))
        {
            CdOutputPath = Path.ChangeExtension(output, ".iso");
        }
        else if (!HasCdFiles && extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            CdOutputPath = Path.ChangeExtension(output, ".wav");
        }
    }

    public bool HasDvdFiles => !string.IsNullOrWhiteSpace(DvdDataDirectory);

    public bool HasBluRayFiles => !string.IsNullOrWhiteSpace(BdDataDirectory);

    public string DvdPitchSummary => PitchSummary(actual: false);
    public string DvdActualPitchSummary => PitchSummary(actual: true);

    public string GetPitchSummary(OpticalDiscFamily family, bool actual)
    {
        if (family == OpticalDiscFamily.Dvd) return PitchSummary(actual);
        try
        {
            bool bd = family == OpticalDiscFamily.BluRay;
            return DvdPitchDisplay.Format(new(bd ? CalibrationDiscKind.BluRay : CalibrationDiscKind.Cd,
                ParameterParser.ParsePositiveDouble(bd ? (actual ? BdActualInnerRadius : BdInnerRadius) : (actual ? CdActualInnerRadius : CdInnerRadius), L.T("内半径")),
                ParameterParser.ParsePositiveDouble(bd ? (actual ? BdActualOuterRadius : BdOuterRadius) : (actual ? CdActualOuterRadius : CdOuterRadius), L.T("外半径")),
                ParameterParser.ParsePositiveLong(bd ? BdTotalSectors : CdSectors, L.T("总扇区数")),
                bd ? BluRayDiscParameters.ChannelBitLengthNm : CdDiscParameters.StandardLinearVelocityMmPerSecond,
                ParameterParser.ParseDouble(bd ? (actual ? BdActualPitchLinear : BdPitchLinear) : (actual ? CdActualPitchLinear : CdPitchLinear), L.T("相对一次项")),
                ParameterParser.ParseDouble(bd ? (actual ? BdActualPitchQuadratic : BdPitchQuadratic) : (actual ? CdActualPitchQuadratic : CdPitchQuadratic), L.T("相对二次项")),
                ParameterParser.ParseDouble(bd ? (actual ? BdActualPitchCubic : BdPitchCubic) : (actual ? CdActualPitchCubic : CdPitchCubic), L.T("相对三次项"))));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return L.T("轨距模型无效：请检查半径、扇区数及系数，整个刻录区的轨距必须为正。");
        }
    }

    private string PitchSummary(bool actual)
    {
        try
        {
            return DvdPitchDisplay.Format(new(CalibrationDiscKind.Dvd,
                ParameterParser.ParsePositiveDouble(actual ? DvdActualInnerRadius : DvdInnerRadius, L.T("内半径")),
                ParameterParser.ParsePositiveDouble(actual ? DvdActualOuterRadius : DvdOuterRadius, L.T("外半径")),
                ParameterParser.ParsePositiveLong(DvdTotalSectors, L.T("总扇区数")),
                DvdStreamingOptions.StandardChannelBitLengthNm,
                ParameterParser.ParseDouble(actual ? DvdActualPitchLinear : DvdPitchLinear, L.T("相对一次项")),
                ParameterParser.ParseDouble(actual ? DvdActualPitchQuadratic : DvdPitchQuadratic, L.T("相对二次项")),
                ParameterParser.ParseDouble(actual ? DvdActualPitchCubic : DvdPitchCubic, L.T("相对三次项"))));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return L.T("轨距模型无效：请检查半径、扇区数及系数，整个刻录区的轨距必须为正。");
        }
    }

    public IReadOnlyList<CdDiscPreset> CdPresets => _cdPresets;

    public IReadOnlyList<DvdDiscPreset> DvdPresets => _dvdPresets;

    public IReadOnlyList<BluRayDiscPreset> BluRayPresets => _bluRayPresets;

    public long GetUsableSectors(OpticalDiscFamily family)
    {
        string totalText = family == OpticalDiscFamily.Dvd ? DvdTotalSectors
            : family == OpticalDiscFamily.BluRay ? BdTotalSectors : CdSectors;
        string usableText = family == OpticalDiscFamily.Dvd ? DvdUsableSectors
            : family == OpticalDiscFamily.BluRay ? BdUsableSectors : CdUsableSectors;
        long total = ParameterParser.ParsePositiveLong(totalText, L.T("总扇区数"));
        return ParseUsableSectors(family, total, usableText);
    }

    internal static long ParseUsableSectors(OpticalDiscFamily family, long total, string text)
        => ResolveUsableSectors(family, total, string.IsNullOrWhiteSpace(text) ? null
            : ParameterParser.ParsePositiveLong(text, L.T("可用扇区数")));

    internal static long ResolveUsableSectors(OpticalDiscFamily family, long total, long? usable = null)
    {
        long resolved = family switch
        {
            OpticalDiscFamily.Dvd => DvdDiscPreset.ResolveUsableSectors(checked((uint)total),
                usable is { } dvd ? checked((uint)dvd) : null),
            OpticalDiscFamily.BluRay => BluRayDiscPreset.ResolveUsableSectors(checked((uint)total),
                usable is { } bd ? checked((uint)bd) : null),
            _ => CdDiscPreset.ResolveUsableSectors(total, usable),
        };
        int alignment = family == OpticalDiscFamily.Dvd ? 16 : family == OpticalDiscFamily.BluRay ? 32 : 1;
        if (resolved <= 0 || resolved > total || resolved % alignment != 0)
            throw new ArgumentException(L.F($"可用扇区数必须大于 0、不超过总扇区数，且是 {alignment} 的倍数。"));
        return resolved;
    }

    /// <summary>Trims the label for the selected family and supplies the placeholder when it
    /// is empty. CD/DVD labels live in Joliet's 32-byte UCS-2 field, so anything longer than
    /// 16 characters is rejected instead of being silently cut at write time. Blu-ray uses
    /// UDF and keeps its wider label.</summary>
    internal string ResolveVolumeLabel(OpticalDiscFamily family)
    {
        if (family == OpticalDiscFamily.BluRay)
        {
            string bluRayLabel = BdVolumeLabel.Trim();
            return bluRayLabel.Length == 0 ? DefaultBluRayVolumeLabel : bluRayLabel;
        }

        bool dvd = family == OpticalDiscFamily.Dvd;
        string label = (dvd ? DvdVolumeLabel : CdVolumeLabel).Trim();
        if (label.Length == 0) return dvd ? DefaultDvdVolumeLabel : DefaultCdVolumeLabel;
        if (label.Length > Iso9660ImageWriter.MaximumJolietVolumeLabelCharacters)
        {
            throw new ArgumentException(
                L.F($"卷标最长 {Iso9660ImageWriter.MaximumJolietVolumeLabelCharacters} 个字符（Joliet 标准）。"));
        }

        return label;
    }

    /// <summary>
    /// Loads built-in + user presets (LoadBuiltIn + LoadOrCreate + Merge). Throws when the
    /// JSON cannot be parsed; the caller keeps the previously valid lists in that case.
    /// </summary>
    public void LoadPresets()
    {
        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
        DiscPresetJsonDocument user = DiscPresetJsonStore.LoadOrCreate(
            _discPresetJsonPath,
            DiscPresetJsonStore.CreateInitialUserDocument());
        ApplyPresetDocuments(MergePresets(builtIn, user));
    }

    /// <summary>
    /// Startup load with the original fallback semantics: on failure keep the built-in
    /// catalog only and return the warning text for the shell to surface.
    /// </summary>
    public string? TryLoadPresets()
    {
        try
        {
            LoadPresets();
            return null;
        }
        catch (Exception exception)
        {
            DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
            DiscPresetJsonDocument emptyUser = new(
                DiscPresetJsonStore.CurrentSchemaVersion,
                [],
                [],
                FallbackLanguage: "zh-CN",
                BluRayPresets: []);
            ApplyPresetDocuments(MergePresets(builtIn, emptyUser));
            return L.F($"用户预设 JSON 无法加载，已继续使用程序内置参数。\n\n{L.Error(exception)}\n\n文件位置：\n{_discPresetJsonPath}");
        }
    }

    public void EnsurePresetsJsonExists()
    {
        if (!File.Exists(_discPresetJsonPath))
        {
            DiscPresetJsonStore.Save(
                _discPresetJsonPath,
                DiscPresetJsonStore.CreateInitialUserDocument());
        }
    }

    /// <summary>
    /// Appends one parameter set to the user preset JSON and refreshes the catalog. Only the
    /// display name comes from the save dialog; the description is derived from the geometry
    /// so hand-edited catalogs still explain each entry. The id carries a timestamp plus a
    /// random suffix so two saves within the same second stay distinct. Refreshing uses
    /// <see cref="LoadPresets"/> on purpose: it swaps the lists without re-applying the
    /// selected preset. Project settings can explicitly apply the newly saved preset with
    /// <paramref name="applyPreset"/> after the write succeeds. Throws when
    /// the name is blank or the JSON cannot be read or written; the caller surfaces the
    /// message.
    /// </summary>
    public string AddUserPreset(
        OpticalDiscFamily family,
        string displayName,
        double innerRadiusMm,
        double outerRadiusMm,
        long sectors,
        double pitchLinear = 0,
        double pitchQuadratic = 0,
        double pitchCubic = 0,
        bool invertMapping = false,
        long? usableSectors = null,
        bool applyPreset = false)
    {
        string name = displayName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException(L.T("请填写预设名称。"), nameof(displayName));
        }

        long usable = ResolveUsableSectors(family, sectors, usableSectors);

        EnsurePresetsJsonExists();
        DiscPresetJsonDocument user = DiscPresetJsonStore.Load(_discPresetJsonPath);
        DateTime now = DateTime.Now;
        string familyToken = family switch
        {
            OpticalDiscFamily.Dvd => "dvd",
            OpticalDiscFamily.BluRay => "bd",
            _ => "cd",
        };
        string id = $"user-{familyToken}-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        string note = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"用户预设：内半径 {innerRadiusMm:0.###} mm、外半径 {outerRadiusMm:0.###} mm、{sectors:N0} 扇区。");
        DiscPresetJsonDocument updated = family switch
        {
            OpticalDiscFamily.Dvd => user with
            {
                SchemaVersion = DiscPresetJsonStore.CurrentSchemaVersion,
                DvdPresets =
                [
                    .. user.DvdPresets,
                    new DvdDiscPresetDefinition(
                        id,
                        name,
                        note,
                        checked((uint)sectors),
                        innerRadiusMm,
                        outerRadiusMm,
                        PitchLinear: pitchLinear,
                        PitchQuadratic: pitchQuadratic,
                        PitchCubic: pitchCubic,
                        UsableSectors: checked((uint)usable)),
                ],
            },
            OpticalDiscFamily.BluRay => user with
            {
                SchemaVersion = DiscPresetJsonStore.CurrentSchemaVersion,
                BluRayPresets =
                [
                    .. user.BluRayPresets ?? [],
                    new BluRayDiscPresetDefinition(
                        id,
                        name,
                        note,
                        checked((uint)sectors),
                        innerRadiusMm,
                        outerRadiusMm,
                        invertMapping,
                        PitchLinear: pitchLinear, PitchQuadratic: pitchQuadratic, PitchCubic: pitchCubic,
                        UsableSectors: checked((uint)usable)),
                ],
            },
            _ => user with
            {
                SchemaVersion = DiscPresetJsonStore.CurrentSchemaVersion,
                CdPresets =
                [
                    .. user.CdPresets,
                    new CdDiscPresetDefinition(
                        id,
                        name,
                        note,
                        sectors,
                        innerRadiusMm,
                        outerRadiusMm,
                        PitchLinear: pitchLinear, PitchQuadratic: pitchQuadratic, PitchCubic: pitchCubic,
                        UsableSectors: usable),
                ],
            },
        };

        DiscPresetJsonStore.Save(_discPresetJsonPath, updated);
        LoadPresets();
        if (applyPreset)
        {
            switch (family)
            {
                case OpticalDiscFamily.Dvd:
                    ApplyDvdDiscPreset(DvdPresets.Single(preset => preset.Id == id));
                    break;
                case OpticalDiscFamily.BluRay:
                    ApplyBluRayDiscPreset(BluRayPresets.Single(preset => preset.Id == id));
                    break;
                default:
                    ApplyCdDiscPreset(CdPresets.Single(preset => preset.Id == id));
                    break;
            }
        }
        return displayName;
    }

    /// <summary>
    /// Removes a user preset from the editable catalog and refreshes the lists. Only ids
    /// this store created (<c>user-*</c>) are removable: built-in presets live in the
    /// embedded resource, never in the user file. Returns the removed display name so the
    /// caller can confirm the action; throws when the file cannot be read or written.
    /// </summary>
    public string RemoveUserPreset(string presetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        if (!presetId.StartsWith("user-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(L.T("内置预设不能删除。"));
        }

        EnsurePresetsJsonExists();
        DiscPresetJsonDocument user = DiscPresetJsonStore.Load(_discPresetJsonPath);
        IReadOnlyList<BluRayDiscPresetDefinition> bluRayPresets = user.BluRayPresets ?? [];
        string? displayName = user.CdPresets.FirstOrDefault(p => p.Id == presetId)?.DisplayName
            ?? user.DvdPresets.FirstOrDefault(p => p.Id == presetId)?.DisplayName
            ?? bluRayPresets.FirstOrDefault(p => p.Id == presetId)?.DisplayName;
        DiscPresetJsonDocument updated = user with
        {
            SchemaVersion = DiscPresetJsonStore.CurrentSchemaVersion,
            CdPresets = user.CdPresets.Where(p => p.Id != presetId).ToList(),
            DvdPresets = user.DvdPresets.Where(p => p.Id != presetId).ToList(),
            BluRayPresets = bluRayPresets.Where(p => p.Id != presetId).ToList(),
        };

        DiscPresetJsonStore.Save(_discPresetJsonPath, updated);
        LoadPresets();
        return displayName ?? presetId;
    }

    private (IReadOnlyList<CdDiscPreset> Cd, IReadOnlyList<DvdDiscPreset> Dvd, IReadOnlyList<BluRayDiscPreset> BluRay) MergePresets(
        DiscPresetJsonDocument builtIn,
        DiscPresetJsonDocument user)
    {
        DiscPresetJsonDocument merged = DiscPresetJsonStore.Merge(builtIn, user);
        List<CdDiscPreset> cdPresets = merged.CdPresets
            .Select(value => new CdDiscPreset(
                value.Id,
                LocalizeBuiltInText(value.DisplayName,
                    builtIn.CdPresets.FirstOrDefault(preset => preset.Id == value.Id)?.DisplayName),
                LocalizeBuiltInText(value.Description,
                    builtIn.CdPresets.FirstOrDefault(preset => preset.Id == value.Id)?.Description),
                value.Sectors,
                value.InnerRadiusMm,
                value.OuterRadiusMm,
                value.DisplayNameResourceKey,
                value.DescriptionResourceKey,
                Contributor: value.Contributor,
                PitchLinear: value.PitchLinear, PitchQuadratic: value.PitchQuadratic, PitchCubic: value.PitchCubic,
                UsableSectors: value.UsableSectors))
            .ToList();
        cdPresets.Add(ManualCdPreset);

        List<DvdDiscPreset> dvdPresets = merged.DvdPresets
            .Select(value => new DvdDiscPreset(
                value.Id,
                LocalizeBuiltInText(value.DisplayName,
                    builtIn.DvdPresets.FirstOrDefault(preset => preset.Id == value.Id)?.DisplayName),
                LocalizeBuiltInText(value.Description,
                    builtIn.DvdPresets.FirstOrDefault(preset => preset.Id == value.Id)?.Description),
                value.TotalSectors,
                value.InnerRadiusMm,
                value.OuterRadiusMm,
                value.DisplayNameResourceKey,
                value.DescriptionResourceKey,
                PitchLinear: value.PitchLinear,
                PitchQuadratic: value.PitchQuadratic,
                PitchCubic: value.PitchCubic,
                Contributor: value.Contributor,
                UsableSectors: value.UsableSectors))
            .ToList();
        dvdPresets.Add(ManualDvdPreset);

        List<BluRayDiscPreset> bluRayPresets = (merged.BluRayPresets ?? [])
            .Select(value => new BluRayDiscPreset(
                value.Id,
                LocalizeBuiltInText(value.DisplayName,
                    builtIn.BluRayPresets?.FirstOrDefault(preset => preset.Id == value.Id)?.DisplayName),
                1,
                new BluRayDiscParameters(
                    value.TotalSectors,
                    1,
                    value.InnerRadiusMm,
                    value.OuterRadiusMm,
                    PitchLinear: value.PitchLinear, PitchQuadratic: value.PitchQuadratic, PitchCubic: value.PitchCubic,
                    UsableSectors: value.UsableSectors),
                value.InvertMapping,
                Contributor: value.Contributor))
            .ToList();
        bluRayPresets.Add(ManualBluRayPreset);
        return (cdPresets, dvdPresets, bluRayPresets);
    }

    // User-edited names and descriptions remain content. Only unchanged built-in text is translated.
    private static string LocalizeBuiltInText(string value, string? builtInValue)
        => value == builtInValue ? L.T(value) : value;

    private void ApplyPresetDocuments(
        (IReadOnlyList<CdDiscPreset> Cd, IReadOnlyList<DvdDiscPreset> Dvd, IReadOnlyList<BluRayDiscPreset> BluRay) presets)
    {
        var cd = SelectedCdPreset;
        var dvd = SelectedDvdPreset;
        var bd = SelectedBluRayPreset;
        _cdPresets = presets.Cd;
        _dvdPresets = presets.Dvd;
        _bluRayPresets = presets.BluRay;
        // Replacing ItemsSource can transiently clear a bound ComboBox selection. Updating
        // the catalog must never apply a preset's geometry to an open project or calibration.
        bool wasApplying = _isApplyingDiscPreset;
        _isApplyingDiscPreset = true;
        try
        {
            OnPropertyChanged(nameof(CdPresets));
            OnPropertyChanged(nameof(DvdPresets));
            OnPropertyChanged(nameof(BluRayPresets));
            SelectedCdPreset = _cdPresets.FirstOrDefault(p => p == cd) ?? _cdPresets.Last(p => p.IsCustom);
            SelectedDvdPreset = _dvdPresets.FirstOrDefault(p => p == dvd) ?? _dvdPresets.Last(p => p.IsCustom);
            SelectedBluRayPreset = _bluRayPresets.FirstOrDefault(p => p == bd) ?? _bluRayPresets.Last(p => p.IsCustom);
        }
        finally { _isApplyingDiscPreset = wasApplying; }
    }

    internal void UsePresetCatalog(DiscParametersState source) =>
        ApplyPresetDocuments((source.CdPresets, source.DvdPresets, source.BluRayPresets));

    /// <summary>Initial selection: the first JSON preset for each disc family.</summary>
    public void SelectDefaultPresets()
    {
        ApplyCdDiscPreset(_cdPresets[0]);
        ApplyDvdDiscPreset(_dvdPresets[0]);
        ApplyBluRayDiscPreset(_bluRayPresets[0]);
    }

    /// <summary>Rebuilds the localized parts of the shared state after a language switch:
    /// preset names/descriptions, the selected preset objects (so the selectors show the new
    /// text) and the computed summaries. Geometry strings are never written here: a preset
    /// file edited on disk must not change the parameters of the running project.</summary>
    public void Relocalize()
    {
        CdDiscPreset cd = SelectedCdPreset;
        DvdDiscPreset dvd = SelectedDvdPreset;
        BluRayDiscPreset bd = SelectedBluRayPreset;
        try
        {
            LoadPresets();
        }
        catch (Exception)
        {
            // A broken user JSON falls back to the catalog already in memory; the switch must
            // not turn into a preset-load failure.
        }

        ApplyCdDiscPreset(_cdPresets.FirstOrDefault(preset => preset.Id == cd.Id) ?? ManualCdPreset, writeGeometry: false);
        ApplyDvdDiscPreset(_dvdPresets.FirstOrDefault(preset => preset.Id == dvd.Id) ?? ManualDvdPreset, writeGeometry: false);
        ApplyBluRayDiscPreset(_bluRayPresets.FirstOrDefault(preset => preset.Id == bd.Id) ?? ManualBluRayPreset, writeGeometry: false);
        OnPropertyChanged(nameof(ToneSummary));
        OnPropertyChanged(nameof(CdOutputLabel));
        OnPropertyChanged(nameof(CdOutputHint));
        OnPropertyChanged(nameof(DvdPitchSummary));
        OnPropertyChanged(nameof(DvdActualPitchSummary));
    }

    // Only the four generation fields per disc family switch the preset to "custom"
    // when edited by hand, matching the previous TextChanged subscriptions.
    partial void OnCdSectorsChanged(string value) => OnCdParameterEdited(presetField: true);
    partial void OnCdUsableSectorsChanged(string value) => OnCdParameterEdited(presetField: true);
    partial void OnCdPitchLinearChanged(string value) => OnCdParameterEdited(presetField: true);
    partial void OnCdPitchQuadraticChanged(string value) => OnCdParameterEdited(presetField: true);
    partial void OnCdPitchCubicChanged(string value) => OnCdParameterEdited(presetField: true);


    partial void OnCdInnerRadiusChanged(string value) => OnCdParameterEdited(presetField: true);

    partial void OnCdOuterRadiusChanged(string value) => OnCdParameterEdited(presetField: true);

    partial void OnDvdTotalSectorsChanged(string value) => OnDvdParameterEdited(presetField: true);
    partial void OnDvdUsableSectorsChanged(string value) => OnDvdParameterEdited(presetField: true);

    partial void OnDvdInnerRadiusChanged(string value) => OnDvdParameterEdited(presetField: true);

    partial void OnDvdOuterRadiusChanged(string value) => OnDvdParameterEdited(presetField: true);

    partial void OnDvdPitchLinearChanged(string value) => OnDvdParameterEdited(presetField: true);
    partial void OnDvdPitchQuadraticChanged(string value) => OnDvdParameterEdited(presetField: true);
    partial void OnDvdPitchCubicChanged(string value) => OnDvdParameterEdited(presetField: true);

    partial void OnBdTotalSectorsChanged(string value) => OnBluRayParameterEdited();
    partial void OnBdUsableSectorsChanged(string value) => OnBluRayParameterEdited();
    partial void OnBdPitchLinearChanged(string value) => OnBluRayParameterEdited();
    partial void OnBdPitchQuadraticChanged(string value) => OnBluRayParameterEdited();
    partial void OnBdPitchCubicChanged(string value) => OnBluRayParameterEdited();


    partial void OnBdInnerRadiusChanged(string value) => OnBluRayParameterEdited();

    partial void OnBdOuterRadiusChanged(string value) => OnBluRayParameterEdited();

    partial void OnBdInvertMappingChanged(bool value) => OnBluRayParameterEdited();

    /// <summary>Writing through the selector applies the preset, mirroring SelectionChanged.</summary>
    partial void OnSelectedCdPresetChanged(CdDiscPreset value)
    {
        if (!_isApplyingDiscPreset)
        {
            ApplyCdDiscPreset(value);
        }
    }

    partial void OnSelectedDvdPresetChanged(DvdDiscPreset value)
    {
        if (!_isApplyingDiscPreset)
        {
            ApplyDvdDiscPreset(value);
        }
    }

    partial void OnSelectedBluRayPresetChanged(BluRayDiscPreset value)
    {
        if (!_isApplyingDiscPreset)
        {
            ApplyBluRayDiscPreset(value);
        }
    }

    private void OnCdParameterEdited(bool presetField)
    {
        if (_isApplyingDiscPreset || !presetField || SelectedCdPreset.IsCustom)
        {
            return;
        }

        SelectedCdPreset = ManualCdPreset;
    }

    private void OnDvdParameterEdited(bool presetField)
    {
        if (_isApplyingDiscPreset || !presetField || SelectedDvdPreset.IsCustom)
        {
            return;
        }

        SelectedDvdPreset = ManualDvdPreset;
    }

    private void OnBluRayParameterEdited()
    {
        if (_isApplyingDiscPreset || SelectedBluRayPreset.IsCustom)
        {
            return;
        }

        SelectedBluRayPreset = ManualBluRayPreset;
    }

    public void ApplyCdDiscPreset(CdDiscPreset preset, bool writeGeometry = true)
    {
        bool previous = _isApplyingDiscPreset;
        _isApplyingDiscPreset = true;
        try
        {
            SelectedCdPreset = preset;
            CdPresetHint = preset.Description;
            if (writeGeometry && !preset.IsCustom)
            {
                CdSectors = preset.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                CdUsableSectors = preset.ResolvedUsableSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                CdInnerRadius = FormatPresetNumber(preset.InnerRadiusMm);
                CdOuterRadius = FormatPresetNumber(preset.OuterRadiusMm);
                CdPitchLinear = FormatPresetNumber(preset.PitchLinear);
                CdPitchQuadratic = FormatPresetNumber(preset.PitchQuadratic);
                CdPitchCubic = FormatPresetNumber(preset.PitchCubic);
                SynchronizePreviewGeometry(OpticalDiscFamily.CompactDisc);
            }
        }
        finally
        {
            _isApplyingDiscPreset = previous;
        }
    }

    public void ApplyDvdDiscPreset(DvdDiscPreset preset, bool writeGeometry = true)
    {
        bool previous = _isApplyingDiscPreset;
        _isApplyingDiscPreset = true;
        try
        {
            SelectedDvdPreset = preset;
            DvdPresetHint = preset.Description;
            if (writeGeometry && !preset.IsCustom)
            {
                DvdTotalSectors = preset.TotalSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                DvdUsableSectors = preset.ResolvedUsableSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                DvdInnerRadius = FormatPresetNumber(preset.InnerRadiusMm);
                DvdOuterRadius = FormatPresetNumber(preset.OuterRadiusMm);
                DvdPitchLinear = FormatPresetNumber(preset.PitchLinear);
                DvdPitchQuadratic = FormatPresetNumber(preset.PitchQuadratic);
                DvdPitchCubic = FormatPresetNumber(preset.PitchCubic);
                SynchronizePreviewGeometry(OpticalDiscFamily.Dvd);
            }
        }
        finally
        {
            _isApplyingDiscPreset = previous;
        }
    }

    public void ApplyBluRayDiscPreset(BluRayDiscPreset preset, bool writeGeometry = true)
    {
        bool previous = _isApplyingDiscPreset;
        _isApplyingDiscPreset = true;
        try
        {
            SelectedBluRayPreset = preset;
            BluRayPresetHint = preset.IsCustom ? string.Empty : preset.DisplayName;
            if (writeGeometry && !preset.IsCustom)
            {
                BluRayDiscParameters geometry = preset.CreateParameters();
                BdTotalSectors = geometry.TotalSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                BdUsableSectors = preset.ResolvedUsableSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                BdInnerRadius = FormatPresetNumber(geometry.InnerRadiusMm);
                BdOuterRadius = FormatPresetNumber(geometry.OuterRadiusMm);
                BdPitchLinear = FormatPresetNumber(geometry.PitchLinear);
                BdPitchQuadratic = FormatPresetNumber(geometry.PitchQuadratic);
                BdPitchCubic = FormatPresetNumber(geometry.PitchCubic);
                BdInvertMapping = preset.InvertMapping;
                SynchronizePreviewGeometry(OpticalDiscFamily.BluRay);
            }
        }
        finally
        {
            _isApplyingDiscPreset = previous;
        }
    }

    internal void SynchronizePreviewGeometry(OpticalDiscFamily family)
    {
        switch (family)
        {
            case OpticalDiscFamily.Dvd:
                DvdActualInnerRadius = DvdInnerRadius;
                DvdActualOuterRadius = DvdOuterRadius;
                DvdActualPitchLinear = DvdPitchLinear;
                DvdActualPitchQuadratic = DvdPitchQuadratic;
                DvdActualPitchCubic = DvdPitchCubic;
                break;
            case OpticalDiscFamily.BluRay:
                BdActualInnerRadius = BdInnerRadius;
                BdActualOuterRadius = BdOuterRadius;
                BdActualPitchLinear = BdPitchLinear;
                BdActualPitchQuadratic = BdPitchQuadratic;
                BdActualPitchCubic = BdPitchCubic;
                break;
            default:
                CdActualInnerRadius = CdInnerRadius;
                CdActualOuterRadius = CdOuterRadius;
                CdActualPitchLinear = CdPitchLinear;
                CdActualPitchQuadratic = CdPitchQuadratic;
                CdActualPitchCubic = CdPitchCubic;
                break;
        }
    }

    private static string FormatPresetNumber(double value)
        => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
