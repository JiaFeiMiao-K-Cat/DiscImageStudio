using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args is ["--test-parameter-persistence"])
{
    PresetApplicationTests.Run();
    ProjectDialogPresetTests.Run();
    CalibrationConfirmedFitTests.Run();
    AppCubicMediaTests.Run();
    UsableSectorPersistenceTests.Run();
    CalibrationStandaloneTests.Run();
    CalibrationPhotoViewModelTests.Run();
    CalibrationThreeStageTests.Run();
    CalibrationBoundaryWorkflowTests.Run();
    CalibrationFitLocalizationTests.Run();
    PopupLocalizationTests.Run();
    RelocalizationTests.Run();
    return;
}

if (args is ["--test-dvd-read-compatibility"])
{
    DvdReadCompatibilityGuardTests.Run();
    DvdReadCompatibilityIntegrationTests.Run();
    DvdReadCompatibilityUiTests.Run();
    return;
}

if (args is ["--test-capacity-ui"])
{
    DvdReadCompatibilityUiTests.Run();
    TonePreviewTests.Run();
    AppCubicMediaTests.Run();
    CalibrationBurnSourceTests.Run();
    BurnJobDockTests.Run();
    ImageJobWorkflowTests.Run();
    ViewModelFailureDiagnosticsTests.Run();
    LocalizationTests.Run();
    RelocalizationTests.Run();
    BurnWriteSpeedSelectionTests.Run();
    return;
}

if (args is ["--test-usable-sectors"])
{
    UsableSectorGenerationTests.Run();
    BluRayCubicPreviewTests.RunUsableCapacity();
    UsableSectorPersistenceTests.Run();
    StreamBurnCapacityTests.Run();
    MediaSectorCountTests.Run();
    CalibrationMediaReadTests.Run();
    return;
}

if (args is ["--benchmark-cd-pitch"])
{
    CdCubicPitchTests.Benchmark();
    return;
}
if (args is ["--benchmark-cd-parallel"])
{
    CdParallelGenerationBenchmark.Run();
    return;
}
if (args is ["--benchmark-bd-pitch"])
{
    BluRayCubicPitchTests.Benchmark();
    return;
}
if (args is ["--benchmark-dvd-sampling"])
{
    DvdSampleReuseBenchmark.Run();
    return;
}
if (args is ["--benchmark-dvd-read-compatibility"])
{
    DvdSampleReuseBenchmark.Run(readCompatibility: true);
    return;
}

DiscModuleCatalog catalog = new(
[
    new CdDiscModule(),
    new DvdDiscModule(),
    new BluRayDiscModule(),
]);

Equal(3, catalog.Modules.Count, "module count");
Equal(OpticalDiscFamily.CompactDisc, catalog.Resolve("cd-generate").Descriptor.Family, "CD routing");
Equal(OpticalDiscFamily.Dvd, catalog.Resolve("solve").Descriptor.Family, "DVD routing");
Equal(OpticalDiscFamily.BluRay, catalog.Resolve("bd-generate").Descriptor.Family, "Blu-ray routing");
Equal(OpticalDiscFamily.BluRay, catalog.Resolve("bd-probe").Descriptor.Family, "Blu-ray probe routing");
True(
    catalog.Resolve("cd-generate").Descriptor.SupportsCancellation,
    "CD module cancellation metadata");
True(
    !catalog.Resolve("solve").Descriptor.SupportsCancellation,
    "DVD module cancellation metadata");

DiscJobResult bluRayResult = await catalog.ExecuteAsync(
    new DiscJobRequest("bd-generate", ["--input", "source.png", "--output", "picture.iso", "--total-sectors", "1024"]));
True(!bluRayResult.Succeeded && bluRayResult.ExitCode == 2, "Blu-ray module without a host image loader reports missing decoder");
True(bluRayResult.Summary!.Contains("image loader", StringComparison.Ordinal), "Blu-ray reports image loading requirement");

Throws<ArgumentException>(
    () => new DiscModuleCatalog([new BluRayDiscModule(), new ConflictingModule()]),
    "duplicate command rejection");
Throws<ArgumentException>(() => catalog.Resolve("unknown-command"), "unknown command rejection");
MaximizedWindowBoundsTests.Run();
WindowWorkAreaNativeTests.Run();
BluRayModuleTests.Run();
BluRayEncodingTests.Run();
BluRayPreviewTests.Run();
BluRayImageMappingTests.Run();
BluRayParallelGenerationTests.Run();
BluRayPixelReuseTests.Run();
BluRayCubicPitchTests.Run();
BluRayCubicPreviewTests.Run();
BluRayIsoTests.Run();
BluRayDrawingModeTests.Run();
BluRaySidecarTests.Run();
BluRayFilePlacementTests.Run();
BluRayPatternRangeTests.Run();
BluRayReferenceRouteTests.Run();
UdfFilesystemTests.Run();
UdfLargeFileTests.Run();
GeneratedContentProgressTests.Run();
OpticalWriteProgressTests.Run();
OpticalBurnDiagnosticsTests.Run();
CdBurnPhaseDiagnosticsTests.Run();
DeviceDiagnosticsTests.Run();
CdDaoDetailedDiagnosticsTests.Run();
DvdDetailedDiagnosticsTests.Run();
BluRayDetailedDiagnosticsTests.Run();
CdProgressTelemetryTests.Run();
BurnStreamDiagnosticsTests.Run();
BurnProgressDisplayTests.Run();
OpticalBurnOperationGateTests.Run();
MediaSectorCountTests.Run();
BluRaySequentialBurnTests.Run();
BluRayFlushDiagnosticsTests.Run();
BluRaySequentialWriterTests.Run();
BluRaySessionCleanupTests.Run();
RunStreamingGenerationTests();
UsableSectorGenerationTests.Run();
UsableSectorPersistenceTests.Run();
StreamBurnCapacityTests.Run();
TestDiscPresets();
TestDiscPresetJson();
UserPresetDeleteTests.Run();
ProjectDialogPresetTests.Run();
PresetApplicationTests.Run();
CalibrationConfirmedFitTests.Run();
ProjectStoreTests.Run();
ProjectSessionTests.Run();
FilePlacementProjectTests.Run();
FilePlacementPreviewTests.Run();
DesignComposerTests.Run();
CdAudioCompatibilityTests.Run();
CdParallelAudioTests.Run();
CdDataImageTests.Run();
CdParallelDataTests.Run();
CdDataBurnTests.Run();
CdDataDaoWorkflowTests.Run();
CdMode1SectorTests.Run();
CdDataSubchannelPatchTests.Run();
CdDataRawImageTests.Run();
CdDataSubchannelModeTests.Run();
CdRawImageByteOrderTests.Run();
CdBinaryTests.Run();
CdPreviewMaskTests.Run();
CdSimdSamplingTests.Run();
CdCubicPitchTests.Run();
FourLevelPreviewTests.Run();
FourLevelDitherTests.Run();
ThreeLevelDitherTests.Run();
BinaryDitherTests.Run();
GammaDitherTests.Run();
DvdScrambledPatternTests.Run();
DvdReadCompatibilityGuardTests.Run();
DvdReadCompatibilityIntegrationTests.Run();
DvdReadCompatibilityUiTests.Run();
ToneTests.Run();
TonePreviewTests.Run();
SaveTrailTests.Run();
DvdCubicPipelineTests.Run();
DvdFilePlacementTests.Run();
DvdSimdSamplingTests.Run();
DvdStableRegionTests.Run();
DvdSampleReuseTests.Run();
AppCubicTests.Run();
AppCubicMediaTests.Run();
CalibrationEncoderTests.Run();
CalibrationCoreTests.Run();
CalibrationHalfDiscArtworkTests.Run();
CalibrationBoundaryFitterTests.Run();
CalibrationRingFitterTests.Run();
CalibrationClockArtworkTests.Run();
CalibrationWideBandArtworkTests.Run();
CalibrationRadialArcArtworkTests.Run();
CalibrationRadialArcFitterTests.Run();
CalibrationSectorScaleTests.Run();
CalibrationClockReaderTests.Run();
CalibrationAngleSweepReaderTests.Run();
CalibrationStandaloneTests.Run();
CalibrationMediaReadTests.Run();
CalibrationCubicPitchTests.Run();
CalibrationCrossExtensionTests.Run();
BluRayObservedCurveRegressionTests.Run();
CalibrationShapeSeedTests.Run();
CalibrationPhotoTests.Run();
CalibrationPhotoCanvasTests.Run();
DesignCanvasZoomTests.Run();
LayerPanelTests.Run();
CalibrationBurnWorkflowTests.Run();
CalibrationBurnSourceTests.Run();
BurnJobDockTests.Run();
ImageJobWorkflowTests.Run();
DiscImageGenerationApiTests.Run();
DvdTypedOperationTests.Run();
JobDiagnosticsTests.Run();
ViewModelFailureDiagnosticsTests.Run();
CalibrationOutputPublisherTests.Run();
CalibrationPhotoViewModelTests.Run();
CalibrationThreeStageTests.Run();
CalibrationBoundaryWorkflowTests.Run();
CalibrationFitLocalizationTests.Run();
PopupLocalizationTests.Run();
AppSettingsTests.Run();
LocalizationTests.Run();
ThemePaletteTests.Run();
TempFilesCacheTests.Run();
RelocalizationTests.Run();
CalibrationReturnButtonTests.Run();
BurnWriteSpeedSelectionTests.Run();

Console.WriteLine("architecture-selftest: all checks passed");
return;

static void Equal<T>(T expected, T actual, string name)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{name}: expected {expected}, actual {actual}.");
    }
}

static void True(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{name}: condition was false.");
    }
}

static void Throws<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}.");
}

static void RunStreamingGenerationTests()
{
    string directory = Path.Combine(Path.GetTempPath(), $"disc-stream-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        string sourcePath = Path.Combine(directory, "source.png");
        WriteSolidPng(sourcePath, 80, 40, 0);
        TestStreamingGeneration(sourcePath);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void TestDiscPresets()
{
    DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
    Equal(4, builtIn.CdPresets.Count, "built-in CD preset count");
    Equal(8, builtIn.DvdPresets.Count, "built-in DVD preset count");
    Equal(5, builtIn.BluRayPresets!.Count, "built-in Blu-ray preset count");
    Equal("zh-CN", builtIn.FallbackLanguage!, "built-in preset fallback language");
    True(
        builtIn.CdPresets.All(value =>
            !string.IsNullOrWhiteSpace(value.DisplayNameResourceKey)
            && !string.IsNullOrWhiteSpace(value.DescriptionResourceKey)),
        "built-in CD presets reserve localization resource keys");
    True(
        builtIn.DvdPresets.All(value =>
            !string.IsNullOrWhiteSpace(value.DisplayNameResourceKey)
            && !string.IsNullOrWhiteSpace(value.DescriptionResourceKey)),
        "built-in DVD presets reserve localization resource keys");
    Equal(
        builtIn.CdPresets.Count,
        builtIn.CdPresets.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count(),
        "CD preset IDs are unique");
    Equal(
        builtIn.DvdPresets.Count,
        builtIn.DvdPresets.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count(),
        "DVD preset IDs are unique");
    True(
        builtIn.BluRayPresets!.All(value =>
            !string.IsNullOrWhiteSpace(value.DisplayName)
            && value.TotalSectors % ProjectDocument.BluRaySectorsPerCluster == 0),
        "built-in Blu-ray presets carry 32-sector-aligned geometry");
    True(CdDiscPreset.Manual.IsCustom, "CD manual state is separate from JSON presets");
    True(DvdDiscPreset.Manual.IsCustom, "DVD manual state is separate from JSON presets");
    True(BluRayDiscPreset.Manual.IsCustom, "Blu-ray manual state is separate from JSON presets");

    double cdVelocity = CdDiscParameters.StandardLinearVelocityMmPerSecond;
    True(
        cdVelocity is >= 1_200 and <= 1_400,
        "CD scanning speed stays in the ECMA-130 range");
    foreach (CdDiscPresetDefinition preset in builtIn.CdPresets)
    {
        new CdDiscParameters(
            preset.InnerRadiusMm,
            preset.OuterRadiusMm,
            preset.Sectors).Validate();
    }

    foreach (DvdDiscPresetDefinition preset in builtIn.DvdPresets)
    {
        new DvdStreamingOptions(
            preset.TotalSectors,
            preset.InnerRadiusMm,
            preset.OuterRadiusMm).Validate();
    }

    foreach (BluRayDiscPresetDefinition preset in builtIn.BluRayPresets!)
    {
        new DiscImageStudio.BluRay.BluRayDiscParameters(
            preset.TotalSectors,
            1,
            preset.InnerRadiusMm,
            preset.OuterRadiusMm).Validate();
    }

    CdDiscPresetDefinition cd80 = builtIn.CdPresets.Single(value => value.Id == "cd-80");
    True(
        cd80.DisplayName.StartsWith("标准", StringComparison.Ordinal),
        "standard CD preset announces its standard nature");
    Equal(359_849L, cd80.Sectors, "80-minute CD preset sectors");
    Equal(
        1_200.0,
        cd80.LinearVelocityMmPerSecond,
        "CD preset keeps the ECMA-130 linear velocity field");
    CdDiscPresetDefinition ritek = builtIn.CdPresets.Single(
        value => value.Id == "ritek-medical-aqua");
    Equal(359_845L, ritek.Sectors, "RITEK medical aqua preset sectors");
    Equal(24.911275, ritek.InnerRadiusMm, "RITEK medical aqua inner radius");
    Equal(57.931155, ritek.OuterRadiusMm, "RITEK medical aqua outer radius");
    CdDiscPresetDefinition verbatim = builtIn.CdPresets.Single(
        value => value.Id == "verbatim-azo-43438");
    Equal(359_848L, verbatim.Sectors, "Verbatim AZO 43438 preset sectors");
    Equal(24.837775, verbatim.InnerRadiusMm, "Verbatim AZO 43438 inner radius");
    Equal(58.020875, verbatim.OuterRadiusMm, "Verbatim AZO 43438 outer radius");
    DvdDiscPresetDefinition dvd120 = builtIn.DvdPresets.Single(
        value => value.Id == "dvd-5-120mm");
    True(
        dvd120.DisplayName.StartsWith("标准", StringComparison.Ordinal),
        "standard DVD preset announces its standard nature");
    Equal(2_295_104U, dvd120.TotalSectors, "120 mm DVD preset sectors");
    Equal(
        133.3,
        dvd120.ChannelBitLengthNm,
        "DVD preset keeps the standard channel-bit length field");
    DvdDiscPresetDefinition verbatimDvd = builtIn.DvdPresets.Single(
        value => value.Id == "verbatim-dvd-r-azo-43533");
    Equal(2_297_888U, verbatimDvd.TotalSectors, "Verbatim DVD-R AZO 43533 sectors");
    Equal(23.991460627731737, verbatimDvd.InnerRadiusMm, "Verbatim DVD-R AZO 43533 inner radius");
    Equal(57.964893657925366, verbatimDvd.OuterRadiusMm, "Verbatim DVD-R AZO 43533 outer radius");
    DvdDiscPresetDefinition verbatimDvd43523 = builtIn.DvdPresets.Single(
        value => value.Id == "verbatim-dvd-r-azo-43523");
    Equal(2_297_888U, verbatimDvd43523.TotalSectors, "Verbatim DVD-R AZO 43523 sectors");
    Equal(23.991460627731737, verbatimDvd43523.InnerRadiusMm, "Verbatim DVD-R AZO 43523 inner radius");
    Equal(57.964893657925366, verbatimDvd43523.OuterRadiusMm, "Verbatim DVD-R AZO 43523 outer radius");
    True(verbatimDvd43523.Contributor == "H4CNPE", "Verbatim DVD-R AZO 43523 contributor is recorded");
    DvdDiscPresetDefinition verbatimDvd43498 = builtIn.DvdPresets.Single(
        value => value.Id == "verbatim-dvd-plus-r-azo-43498");
    Equal(2_295_104U, verbatimDvd43498.TotalSectors, "Verbatim DVD+R AZO 43498 sectors");
    Equal(23.954070288547207, verbatimDvd43498.InnerRadiusMm, "Verbatim DVD+R AZO 43498 inner radius");
    Equal(57.981456053451275, verbatimDvd43498.OuterRadiusMm, "Verbatim DVD+R AZO 43498 outer radius");
    Equal(-1.1055153059816393E-05, verbatimDvd43498.PitchLinear, "Verbatim DVD+R AZO 43498 linear pitch");
    Equal(-1.7233535436312236E-06, verbatimDvd43498.PitchQuadratic, "Verbatim DVD+R AZO 43498 quadratic pitch");
    Equal(5.55123860136712E-06, verbatimDvd43498.PitchCubic, "Verbatim DVD+R AZO 43498 cubic pitch");
    True(verbatimDvd43498.Contributor == "Disappear9", "Verbatim DVD+R AZO 43498 contributor is recorded");
    DvdDiscPresetDefinition dvd80 = builtIn.DvdPresets.Single(
        value => value.Id == "dvd-5-80mm");
    Equal(714_544U, dvd80.TotalSectors, "80 mm DVD preset sectors");
    Equal(38.0, dvd80.OuterRadiusMm, "80 mm DVD preset outer radius");
    DvdDiscPresetDefinition philips = builtIn.DvdPresets.Single(
        value => value.Id == "philips-dvd-plus-r");
    Equal(2_295_104U, philips.TotalSectors, "PHILIPS DVD+R sectors");
    Equal(24.157723798864616, philips.InnerRadiusMm, "PHILIPS DVD+R inner radius");
    Equal(57.881039159257099, philips.OuterRadiusMm, "PHILIPS DVD+R outer radius");
    Equal(-0.00015487792452410765, philips.PitchLinear, "PHILIPS DVD+R linear pitch");
    Equal(0.0001962455822194293, philips.PitchQuadratic, "PHILIPS DVD+R quadratic pitch");
    Equal(-8.6683602888291885E-05, philips.PitchCubic, "PHILIPS DVD+R cubic pitch");
    True(philips.Contributor == "DBPolp", "PHILIPS DVD+R contributor is recorded");
    DvdDiscPresetDefinition ritekDvd = builtIn.DvdPresets.Single(
        value => value.Id == "ritek-dvd-plus-r-e-series");
    Equal(2_295_104U, ritekDvd.TotalSectors, "Ritek DVD+R E-Series sectors");
    Equal(24.048201325754512, ritekDvd.InnerRadiusMm, "Ritek DVD+R E-Series inner radius");
    Equal(58.02648816120639, ritekDvd.OuterRadiusMm, "Ritek DVD+R E-Series outer radius");
    Equal(0.00004448895078187968, ritekDvd.PitchLinear, "Ritek DVD+R E-Series linear pitch");
    Equal(-0.00010130233076827166, ritekDvd.PitchQuadratic, "Ritek DVD+R E-Series quadratic pitch");
    Equal(0.00006625808735161595, ritekDvd.PitchCubic, "Ritek DVD+R E-Series cubic pitch");
    True(ritekDvd.Contributor == "EternalEve", "Ritek DVD+R E-Series contributor is recorded");
    DvdDiscPresetDefinition ritekDvdR = builtIn.DvdPresets.Single(
        value => value.Id == "ritek-dvd-r-e-series");
    Equal(2_298_496U, ritekDvdR.TotalSectors, "Ritek DVD-R E-Series sectors");
    Equal(24.063109184084034, ritekDvdR.InnerRadiusMm, "Ritek DVD-R E-Series inner radius");
    Equal(58.23373375457525, ritekDvdR.OuterRadiusMm, "Ritek DVD-R E-Series outer radius");
    Equal(-3.2229430281730407E-06, ritekDvdR.PitchLinear, "Ritek DVD-R E-Series linear pitch");
    Equal(1.0507210681810583E-05, ritekDvdR.PitchQuadratic, "Ritek DVD-R E-Series quadratic pitch");
    Equal(-8.562064227346657E-06, ritekDvdR.PitchCubic, "Ritek DVD-R E-Series cubic pitch");
    True(ritekDvdR.Contributor == "bczhc", "Ritek DVD-R E-Series contributor is recorded");
    BluRayDiscPresetDefinition ritekBd12x = builtIn.BluRayPresets!.Single(
        value => value.Id == "bd-r-ritek-12x");
    Equal("铼德 BD-R 12x（实测）", ritekBd12x.DisplayName, "Ritek BD-R 12x preset name");
    Equal(12_219_392U, ritekBd12x.TotalSectors, "Ritek BD-R 12x sectors");
    Equal(23.98795, ritekBd12x.InnerRadiusMm, "Ritek BD-R 12x inner radius");
    Equal(58.02236, ritekBd12x.OuterRadiusMm, "Ritek BD-R 12x outer radius");
    True(!ritekBd12x.InvertMapping, "Ritek BD-R 12x preserves the recorded normal polarity");
    True(ritekBd12x.Contributor == "DeepAQ", "Ritek BD-R 12x contributor is recorded");
    BluRayDiscPreset cliRitek12x = BluRayDiscPreset.Find(ritekBd12x.Id);
    Equal(new BluRayDiscParameters(ritekBd12x.TotalSectors, 1, ritekBd12x.InnerRadiusMm, ritekBd12x.OuterRadiusMm, UsableSectors: ritekBd12x.UsableSectors),
        cliRitek12x.CreateParameters(), "GUI JSON and CLI Ritek BD-R 12x geometry agree exactly");
    Equal(ritekBd12x.InvertMapping, cliRitek12x.InvertMapping, "GUI JSON and CLI Ritek BD-R 12x polarity agree");
    True(cliRitek12x.Contributor == "DeepAQ", "CLI Ritek BD-R 12x keeps the contributor");
    BluRayDiscPresetDefinition bdStandard = builtIn.BluRayPresets!.Single(
        value => value.Id == "bd-r-sl-25gb");
    True(
        bdStandard.DisplayName.StartsWith("标准", StringComparison.Ordinal),
        "standard Blu-ray preset announces its standard nature");
    BluRayDiscPresetDefinition verbatimBd = builtIn.BluRayPresets!.Single(
        value => value.Id == "bd-r-verbatim-64099");
    Equal("威宝 BD-R (64099)（实测）", verbatimBd.DisplayName, "Verbatim BD-R 64099 preset name");
    Equal(12_219_392U, verbatimBd.TotalSectors, "Verbatim BD-R 64099 sectors");
    Equal(23.987042157339335, verbatimBd.InnerRadiusMm, "Verbatim BD-R 64099 saved inner radius");
    Equal(57.987960341783236, verbatimBd.OuterRadiusMm, "Verbatim BD-R 64099 saved outer radius");
    True(!verbatimBd.InvertMapping, "Verbatim BD-R 64099 preserves the recorded normal polarity");
    BluRayDiscPreset cliVerbatim = BluRayDiscPreset.Find(verbatimBd.Id);
    Equal(new BluRayDiscParameters(verbatimBd.TotalSectors, 1, verbatimBd.InnerRadiusMm, verbatimBd.OuterRadiusMm, UsableSectors: verbatimBd.UsableSectors),
        cliVerbatim.CreateParameters(), "GUI JSON and CLI Verbatim BD-R 64099 geometry agree exactly");
    Equal(verbatimBd.InvertMapping, cliVerbatim.InvertMapping, "GUI JSON and CLI Verbatim polarity agree");
    BluRayDiscPresetDefinition verbatimBd64293 = builtIn.BluRayPresets!.Single(
        value => value.Id == "bd-r-verbatim-64293");
    Equal("威宝 BD-R (64293)（实测）", verbatimBd64293.DisplayName, "Verbatim BD-R 64293 preset name");
    Equal(verbatimBd.TotalSectors, verbatimBd64293.TotalSectors, "Verbatim BD-R 64293 sectors match 64099");
    Equal(verbatimBd.InnerRadiusMm, verbatimBd64293.InnerRadiusMm, "Verbatim BD-R 64293 inner radius matches 64099");
    Equal(verbatimBd.OuterRadiusMm, verbatimBd64293.OuterRadiusMm, "Verbatim BD-R 64293 outer radius matches 64099");
    True(!verbatimBd64293.InvertMapping, "Verbatim BD-R 64293 preserves the recorded normal polarity");
    True(verbatimBd64293.Contributor == "bczhc", "Verbatim BD-R 64293 contributor is recorded");
    BluRayDiscPreset cliVerbatim64293 = BluRayDiscPreset.Find(verbatimBd64293.Id);
    Equal(new BluRayDiscParameters(verbatimBd64293.TotalSectors, 1, verbatimBd64293.InnerRadiusMm, verbatimBd64293.OuterRadiusMm, UsableSectors: verbatimBd64293.UsableSectors),
        cliVerbatim64293.CreateParameters(), "GUI JSON and CLI Verbatim BD-R 64293 geometry agree exactly");
    Equal(verbatimBd64293.InvertMapping, cliVerbatim64293.InvertMapping, "GUI JSON and CLI Verbatim BD-R 64293 polarity agree");
}

static void TestDiscPresetJson()
{
    string directory = Path.Combine(Path.GetTempPath(), $"disc-presets-test-{Guid.NewGuid():N}");
    string path = Path.Combine(directory, DiscPresetJsonStore.FileName);
    DiscPresetJsonDocument initialUser = DiscPresetJsonStore.CreateInitialUserDocument();

    try
    {
        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
        DiscPresetJsonDocument created = DiscPresetJsonStore.LoadOrCreate(path, initialUser);
        True(File.Exists(path), "user preset JSON is created on first load");
        Equal(1, created.CdPresets.Count, "initial user CD preset count");
        Equal(1, created.DvdPresets.Count, "initial user DVD preset count");
        Equal(1, created.BluRayPresets!.Count, "initial user Blu-ray preset count");
        True(
            File.ReadAllText(path).Contains("自定义 DVD 参数", StringComparison.Ordinal),
            "initial user JSON keeps custom preset names readable");
        True(
            !File.ReadAllText(path).Contains("威宝 DVD-R AZO (43533)", StringComparison.Ordinal),
            "user JSON does not snapshot built-in presets");
        True(
            !File.ReadAllText(path).Contains("displayNameResourceKey", StringComparison.Ordinal),
            "user presets do not require localization resource keys");
        True(
            !File.ReadAllText(path).Contains("imageOuterRadiusMm", StringComparison.Ordinal),
            "preset JSON excludes image layout parameters");
        True(
            File.ReadAllText(path).Contains("linearVelocityMmPerSecond", StringComparison.Ordinal)
            && File.ReadAllText(path).Contains("channelBitLengthNm", StringComparison.Ordinal),
            "user preset JSON keeps the fixed speed and channel-bit fields");

        DiscPresetJsonDocument merged = DiscPresetJsonStore.Merge(builtIn, created);
        Equal(5, merged.CdPresets.Count, "merged CD preset count");
        Equal(9, merged.DvdPresets.Count, "merged DVD preset count");
        Equal(6, merged.BluRayPresets!.Count, "merged Blu-ray preset count");
        DvdDiscPresetDefinition verbatim = merged.DvdPresets.Single(
            value => value.Id == "verbatim-dvd-r-azo-43533");
        Equal(2_297_888U, verbatim.TotalSectors, "merged Verbatim DVD sectors");

        DiscPresetJsonDocument updatedBuiltIn = builtIn with
        {
            CdPresets = builtIn.CdPresets
                .Select(value => value.Id == "cd-80" ? value with { Sectors = 360_000 } : value)
                .ToArray(),
        };
        DiscPresetJsonDocument mergedUpdate = DiscPresetJsonStore.Merge(updatedBuiltIn, created);
        Equal(
            360_000L,
            mergedUpdate.CdPresets.Single(value => value.Id == "cd-80").Sectors,
            "built-in updates flow through the user layer");

        DiscPresetJsonDocument userEdited = created with
        {
            CdPresets = created.CdPresets
                .Append(new CdDiscPresetDefinition(
                    "cd-80",
                    "用户覆盖的 CD 80",
                    "同 ID 的用户参数覆盖内置参数。",
                    350_000,
                    24.6,
                    57.0))
                .ToArray(),
            DisabledDvdPresetIds = ["dvd-5-80mm"],
        };
        DiscPresetJsonStore.Save(path, userEdited);
        DiscPresetJsonDocument reloaded = DiscPresetJsonStore.Load(path);
        DiscPresetJsonDocument mergedUserEdit = DiscPresetJsonStore.Merge(builtIn, reloaded);
        Equal(
            350_000L,
            mergedUserEdit.CdPresets.Single(value => value.Id == "cd-80").Sectors,
            "user preset overrides the same built-in ID");
        True(
            mergedUserEdit.CdPresets.Any(value => value.Id == "user-custom-cd"),
            "custom user CD preset survives merge");
        True(
            mergedUserEdit.DvdPresets.All(value => value.Id != "dvd-5-80mm"),
            "disabled built-in DVD preset is hidden");

        string validJson = File.ReadAllText(path);
        DiscPresetJsonDocument reservedId = reloaded with
        {
            DvdPresets = reloaded.DvdPresets
                .Append(reloaded.DvdPresets[0] with { Id = "__manual__" })
                .ToArray(),
        };
        Throws<InvalidDataException>(
            () => DiscPresetJsonStore.Save(path, reservedId),
            "reserved manual-state preset ID rejection");
        Equal(validJson, File.ReadAllText(path), "invalid preset save preserves valid JSON");

        DiscPresetJsonDocument blankContributor = reloaded with
        {
            DvdPresets = reloaded.DvdPresets
                .Append(new DvdDiscPresetDefinition(
                    "user-blank-contributor",
                    "空贡献者",
                    "测试用预设。",
                    2_295_104,
                    24.0,
                    58.0,
                    Contributor: "   "))
                .ToArray(),
        };
        Throws<InvalidDataException>(
            () => DiscPresetJsonStore.Save(path, blankContributor),
            "blank contributor rejection");
        Equal(validJson, File.ReadAllText(path), "blank contributor preserves valid JSON");

        File.WriteAllText(
            path,
            """
            {
              // 用户可以为实测盘片留下说明。
              "schemaVersion": 2,
              "cdPresets": [],
              "dvdPresets": [],
              "disabledCdPresetIds": [],
              "disabledDvdPresetIds": [],
            }
            """);
        DiscPresetJsonDocument commented = DiscPresetJsonStore.Load(path);
        Equal(0, commented.CdPresets.Count, "preset JSON comments and trailing commas");

        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 2,
              "cdPresets": [
                {
                  "id": "legacy-shape",
                  "displayName": "旧结构",
                  "description": "带保留字段的旧版本文件。",
                  "sectors": 350000,
                  "innerRadiusMm": 24.5,
                  "outerRadiusMm": 57.0,
                  "linearVelocityMmPerSecond": 1400
                }
              ],
              "dvdPresets": [
                {
                  "id": "legacy-dvd",
                  "displayName": "旧 DVD 结构",
                  "description": "带保留字段的旧版本文件。",
                  "totalSectors": 2295104,
                  "innerRadiusMm": 24.0,
                  "outerRadiusMm": 58.0,
                  "channelBitLengthNm": 133.33
                }
              ]
            }
            """);
        DiscPresetJsonDocument legacy = DiscPresetJsonStore.Load(path);
        Equal(
            1_400.0,
            legacy.CdPresets[0].LinearVelocityMmPerSecond,
            "legacy user preset keeps its stored linear velocity");
        DiscPresetJsonStore.Save(path, legacy);
        Equal(
            1_400.0,
            DiscPresetJsonStore.Load(path).CdPresets[0].LinearVelocityMmPerSecond,
            "stored linear velocity survives a save round trip");

        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 3,
              "cdPresets": [
                {
                  "id": "interim-shape",
                  "displayName": "临时结构",
                  "description": "临时版本删掉了保留字段。",
                  "sectors": 350000,
                  "innerRadiusMm": 24.5,
                  "outerRadiusMm": 57.0
                }
              ],
              "dvdPresets": [
                {
                  "id": "interim-dvd",
                  "displayName": "临时 DVD 结构",
                  "description": "临时版本删掉了保留字段。",
                  "totalSectors": 2295104,
                  "innerRadiusMm": 24.0,
                  "outerRadiusMm": 58.0
                }
              ]
            }
            """);
        DiscPresetJsonDocument interim = DiscPresetJsonStore.Load(path);
        Equal(
            1_200.0,
            interim.CdPresets[0].LinearVelocityMmPerSecond,
            "interim schema without the field falls back to the CD standard");
        Equal(
            133.3,
            interim.DvdPresets[0].ChannelBitLengthNm,
            "interim schema without the field falls back to the DVD standard");
        Equal(
            5,
            DiscPresetJsonStore.Merge(builtIn, interim).BluRayPresets!.Count,
            "pre-BD schema still receives the built-in Blu-ray catalog");

        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 4,
              "cdPresets": [],
              "dvdPresets": []
            }
            """);
        Throws<InvalidDataException>(
            () => DiscPresetJsonStore.Load(path),
            "schema 4 without a Blu-ray catalog is rejected");

        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 4,
              "cdPresets": [],
              "dvdPresets": [],
              "bluRayPresets": [
                {
                  "id": "bad-bd",
                  "displayName": "非法蓝光扇区",
                  "description": "扇区数必须按 32 对齐。",
                  "totalSectors": 1000,
                  "innerRadiusMm": 24.0,
                  "outerRadiusMm": 58.0
                }
              ]
            }
            """);
        Throws<InvalidDataException>(
            () => DiscPresetJsonStore.Load(path),
            "Blu-ray preset sectors must align to 32");

        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 2,
              "cdPresets": [
                {
                  "id": "bad-velocity",
                  "displayName": "非法线速度",
                  "description": "线速度必须是大于 0 的有限数值。",
                  "sectors": 350000,
                  "innerRadiusMm": 24.5,
                  "outerRadiusMm": 57.0,
                  "linearVelocityMmPerSecond": 0
                }
              ],
              "dvdPresets": []
            }
            """);
        Throws<InvalidDataException>(
            () => DiscPresetJsonStore.Load(path),
            "retained linear velocity is still validated");

        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 2,
              "cdPresets": [
                {
                  "id": "old-shape",
                  "displayName": "旧结构",
                  "description": "包含已移除的图片字段。",
                  "sectors": 350000,
                  "innerRadiusMm": 24.5,
                  "outerRadiusMm": 57.0,
                  "imageOuterRadiusMm": 58.0
                }
              ],
              "dvdPresets": []
            }
            """);
        Throws<InvalidDataException>(
            () => DiscPresetJsonStore.Load(path),
            "non-generation preset field rejection");

        File.WriteAllText(
            path,
            "{ \"schemaVersion\": 1, \"cdPresets\": [], \"dvdPresets\": [] }");
        Throws<InvalidDataException>(
            () => DiscPresetJsonStore.Load(path),
            "pre-layered schema rejection");

        File.WriteAllText(path, "{ \"schemaVersion\": 2, \"cdPresets\": [ }");
        Throws<InvalidDataException>(
            () => DiscPresetJsonStore.Load(path),
            "malformed preset JSON rejection");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestStreamingGeneration(string sourcePath)
{
    CdDiscParameters cdParameters = new(
        InnerRadiusMm: 24.5,
        OuterRadiusMm: 24.6,
        Sectors: 2,
        LinearVelocityMmPerSecond: 1200,
        ImageOuterRadiusMm: 58);
    using MemoryStream cdStream = new();
    CdGenerationSummary cdSummary = CdTrackGenerator.GenerateToStream(
        sourcePath,
        cdStream,
        cdParameters,
        interleave: true);
    Equal(2L * CdDiscParameters.BytesPerSector, cdStream.Length, "CD streaming byte length");
    Equal(cdStream.Length, cdSummary.BytesWritten, "CD streaming summary length");

    DvdStreamingOptions dvdOptions = new(
        TotalSectors: 16,
        InnerRadiusMm: 24,
        OuterRadiusMm: 58,
        ChannelBitLengthNm: 133.33,
        StartAngleDegrees: 0,
        FastParallelism: 1);
    using MemoryStream dvdStream = new();
    DvdStreamingSummary dvdSummary = DvdStreamingGenerator.Generate(
        sourcePath,
        dvdStream,
        dvdOptions);
    Equal(dvdOptions.ContentLength, dvdStream.Length, "DVD streaming byte length");
    Equal(dvdStream.Length, dvdSummary.BytesWritten, "DVD streaming summary length");
    True(dvdStream.ToArray().Distinct().Count() > 1, "DVD streaming content is generated");

    string hybridDirectory = Path.Combine(
        Path.GetDirectoryName(sourcePath)!,
        "hybrid-stream-data");
    string nestedDirectory = Path.Combine(hybridDirectory, "资料");
    Directory.CreateDirectory(nestedDirectory);
    byte[] hybridFile = Enumerable.Range(0, 3001)
        .Select(index => (byte)((index * 73 + 19) % 251))
        .ToArray();
    File.WriteAllBytes(Path.Combine(nestedDirectory, "stream-test.bin"), hybridFile);
    DvdStreamingOptions hybridOptions = dvdOptions with { TotalSectors = 64 };
    DvdHybridStreamingPlan hybridPlan = DvdStreamingGenerator.PrepareHybrid(
        hybridDirectory,
        hybridOptions,
        "STREAM_TEST");
    True(
        hybridPlan.DrawingStartLba >= hybridPlan.FilesystemEndLbaExclusive,
        "hybrid drawing starts after filesystem data");
    Equal(
        0U,
        (hybridPlan.DrawingStartLba + 0x30000U) & 0xFU,
        "hybrid drawing starts on an ECC Block boundary");
    using MemoryStream hybridBacking = new();
    using (ForwardOnlyWriteStream hybridStream = new(hybridBacking))
    {
        DvdStreamingSummary hybridSummary = DvdStreamingGenerator.GenerateHybrid(
            sourcePath,
            hybridStream,
            hybridOptions,
            hybridPlan);
        Equal(hybridOptions.ContentLength, hybridSummary.BytesWritten, "hybrid streaming summary length");
    }

    byte[] hybridBytes = hybridBacking.ToArray();
    Equal(hybridOptions.ContentLength, hybridBytes.LongLength, "hybrid streaming byte length");
    True(
        hybridBytes.AsSpan((16 * 2048) + 1, 5).SequenceEqual("CD001"u8),
        "hybrid stream contains an ISO9660 primary volume descriptor");
    int prefixBytes = checked((int)(hybridPlan.DrawingStartLba * 2048));
    True(
        hybridBytes.AsSpan(0, prefixBytes).IndexOf(hybridFile) >= 0,
        "hybrid stream contains the planned source file before drawing data");
    True(
        hybridBytes.AsSpan(prefixBytes).ToArray().Distinct().Count() > 1,
        "hybrid stream switches to generated drawing data at the planned LBA");

    new OpticalBurnRequest(
        "test-recorder",
        OpticalBurnMediaKind.CdAudio,
        cdStream.Length,
        (_, _) => { }).Validate();
    new OpticalBurnRequest(
        "test-recorder",
        OpticalBurnMediaKind.DvdData,
        dvdStream.Length,
        (_, _) => { }).Validate();
    Throws<ArgumentOutOfRangeException>(
        () => new OpticalBurnRequest(
            "test-recorder",
            OpticalBurnMediaKind.DvdData,
            dvdStream.Length - 1,
            (_, _) => { }).Validate(),
        "DVD streaming rejects partial sectors");
    OpticalWriteSpeed dvd16X = new(10820, true);
    True(
        Math.Abs(dvd16X.GetMultiplier(OpticalBurnMediaKind.DvdData) - 16.0) < 0.01,
        "DVD speed multiplier conversion");
    Equal(
        16.0,
        new OpticalWriteSpeed(1200, false).GetMultiplier(OpticalBurnMediaKind.CdAudio),
        "CD speed multiplier conversion");
    Throws<ArgumentOutOfRangeException>(
        () => new OpticalBurnRequest(
            "test-recorder",
            OpticalBurnMediaKind.CdAudio,
            2352,
            (_, _) => { },
            new OpticalWriteSpeed(0, false)).Validate(),
        "write speed rejects zero sectors per second");
    OpticalWriteSpeed requestedSpeed = new(1200, false);
    True(
        requestedSpeed == WindowsImapiBurner.ResolveWriteSpeed(
            requestedSpeed,
            [new OpticalWriteSpeed(300, false)]),
        "manual write speed is preserved");
    True(
        new OpticalWriteSpeed(300, false) == WindowsImapiBurner.ResolveWriteSpeed(
            requested: null,
            [
                new OpticalWriteSpeed(1200, false),
                new OpticalWriteSpeed(300, true),
                new OpticalWriteSpeed(300, false),
                new OpticalWriteSpeed(600, false),
            ]),
        "automatic write speed selects the lowest CLV configuration");

    byte[] expected = Enumerable.Range(0, CdDiscParameters.BytesPerSector * 2)
        .Select(index => (byte)(index % 251))
        .ToArray();
    using GeneratedContentComStream generated = new(
        expected.Length,
        (output, _) => output.Write(expected),
        progress: null,
        CancellationToken.None);
    Equal(256 * 1024, GeneratedContentComStream.ChunkBytes, "streaming buffer chunk size");
    Equal(
        64 * 1024 * 1024,
        GeneratedContentComStream.BufferCapacityBytes,
        "streaming buffer capacity");
    Equal(
        32 * 1024 * 1024,
        GeneratedContentComStream.PrebufferBytes,
        "streaming prebuffer threshold");
    generated.WaitUntilPrebuffered(CancellationToken.None);
    generated.Stat(out STATSTG stat, 0);
    Equal((long)expected.Length, stat.cbSize, "COM stream reports declared length");
    IntPtr countPointer = Marshal.AllocHGlobal(sizeof(long));
    try
    {
        generated.Seek(0, 2, countPointer);
        Equal((long)expected.Length, Marshal.ReadInt64(countPointer), "COM stream seek-to-end length");
        generated.Seek(0, 0, countPointer);
        byte[] actual = new byte[expected.Length];
        int offset = 0;
        while (offset < actual.Length)
        {
            byte[] chunk = new byte[Math.Min(777, actual.Length - offset)];
            generated.Read(chunk, chunk.Length, countPointer);
            int read = Marshal.ReadInt32(countPointer);
            Buffer.BlockCopy(chunk, 0, actual, offset, read);
            offset += read;
        }

        True(expected.SequenceEqual(actual), "buffered COM stream preserves generated bytes");
    }
    finally
    {
        Marshal.FreeHGlobal(countPointer);
    }

    TestCdRawDaoCallOrder();
    TestCdRawDaoSectorTypeSelection();
    TestCdRawImageConstruction();
}

static void TestCdRawDaoCallOrder()
{
    byte[] sector = new byte[2352];
    using GeneratedContentComStream content = new(
        sector.Length,
        (output, _) => output.Write(sector),
        progress: null,
        CancellationToken.None);
    content.WaitUntilPrebuffered(CancellationToken.None);
    OpticalBurnRequest request = new(
        "test-recorder",
        OpticalBurnMediaKind.CdAudio,
        sector.Length,
        (_, _) => { },
        new OpticalWriteSpeed(1200, false));
    RecordingCdRawSession session = new();
    using RecordingCdRawImageSession imageSession = new(session.Calls);

    WindowsImapiBurner.BurnCdAudio(
        session,
        imageSession,
        content,
        request,
        CancellationToken.None);

    Equal(
        "PrepareMedia,MediaPhysicallyBlank,SupportedSectorTypes,"
        + "SetRequestedSectorType,SetWriteSpeed,"
        + "CurrentWriteSpeed,CurrentRotationTypeIsPureCav,"
        + "CreateImage,StartOfLeadout,LastPossibleStartOfLeadout,"
        + "WriteMedia,ReleaseMedia",
        string.Join(',', session.Calls),
        "CD Raw DAO preparation order");
    Equal(
        CdRawSectorType.SubcodeIsCooked,
        session.RequestedSectorType ?? 0,
        "CD Raw DAO keeps cooked R-W when raw P-W is not listed");
    Equal(
        CdRawSectorType.SubcodeIsCooked,
        imageSession.ImageSectorType ?? 0,
        "CD Raw DAO image matches the requested cooked R-W layout");

    RecordingCdRawSession fallbackSpeedSession = new();
    using RecordingCdRawImageSession fallbackSpeedImage = new(fallbackSpeedSession.Calls);
    WindowsImapiBurner.BurnCdAudio(
        fallbackSpeedSession,
        fallbackSpeedImage,
        content,
        request with { WriteSpeed = null },
        CancellationToken.None);
    Equal(
        1200,
        fallbackSpeedSession.LastRequestedWriteSpeed,
        "CD Raw DAO uses conservative fallback when no speed descriptor is available");

    foreach (int excess in new[] { 1, 3, 4 })
    {
        RecordingCdRawSession overburnSession = new() { LastLeadoutSector = 1 };
        using RecordingCdRawImageSession overburnImage = new(overburnSession.Calls)
        {
            ImageStartOfLeadout = 1 + excess,
        };
        try
        {
            WindowsImapiBurner.BurnCdAudio(overburnSession, overburnImage, content, request, CancellationToken.None);
            throw new InvalidOperationException("CD DAO boundary: expected InvalidOperationException.");
        }
        catch (InvalidOperationException exception)
        {
            True(exception.Message.Contains($"超出盘片边界 {excess} 个扇区", StringComparison.Ordinal),
                "CD DAO capacity error reports exact lead-out excess");
            True(!overburnSession.Calls.Contains(nameof(ICdRawSession.WriteMedia))
                && overburnSession.Calls[^1] == nameof(ICdRawSession.ReleaseMedia),
                "CD Raw DAO rejects any excess and releases the medium");
        }
    }

    RecordingCdRawSession failingSession = new()
    {
        WriteMediaError = new COMException(
            "original write failure",
            unchecked((int)0xC0AA0601)),
        ReleaseMediaError = new COMException(
            "cleanup says not prepared",
            unchecked((int)0xC0AA0602)),
    };
    using RecordingCdRawImageSession failingImageSession = new(failingSession.Calls);
    try
    {
        WindowsImapiBurner.BurnCdAudio(
            failingSession,
            failingImageSession,
            content,
            request,
            CancellationToken.None);
        throw new InvalidOperationException("CD write failure preservation: expected COMException.");
    }
    catch (COMException exception)
    {
        Equal(
            unchecked((int)0xC0AA0601),
            exception.HResult,
            "CD Raw DAO write failure is not masked by cleanup");
    }
}

static void TestCdRawDaoSectorTypeSelection()
{
    Equal(
        CdRawSectorType.SubcodeIsRaw,
        WindowsImapiBurner.ResolveCdDaoSectorType([1, 2, 3]),
        "CD DAO prefers raw P-W when the recorder lists it");
    Equal(
        CdRawSectorType.SubcodeIsRaw,
        WindowsImapiBurner.ResolveCdDaoSectorType([3]),
        "CD DAO accepts a raw-only recorder");
    Equal(
        CdRawSectorType.SubcodeIsCooked,
        WindowsImapiBurner.ResolveCdDaoSectorType([1, 2]),
        "CD DAO falls back to cooked R-W");
    foreach (int[] unusable in new[] { new[] { 1 }, Array.Empty<int>() })
    {
        try
        {
            WindowsImapiBurner.ResolveCdDaoSectorType(unusable);
            throw new InvalidOperationException("CD DAO sector type: expected rejection.");
        }
        catch (InvalidOperationException exception)
        {
            True(
                exception.Message.Contains(
                    unusable.Length == 0 ? "光驱报告：无" : "光驱报告：1",
                    StringComparison.Ordinal),
                "CD DAO rejects PQ-only or empty sector type lists and reports them");
        }
    }

    Equal(
        "CD DAO 子通道格式：Raw P-W（IMAPI 类型 3）；光驱报告支持：1, 2, 3。",
        WindowsImapiBurner.DescribeCdDaoSectorType(CdRawSectorType.SubcodeIsRaw, [1, 2, 3]),
        "CD DAO log line names raw P-W and the reported types");
    Equal(
        "CD DAO 子通道格式：Cooked R-W（IMAPI 类型 2）；光驱报告支持：2。",
        WindowsImapiBurner.DescribeCdDaoSectorType(CdRawSectorType.SubcodeIsCooked, [2]),
        "CD DAO log line names cooked R-W");

    byte[] sector = new byte[2352];
    using GeneratedContentComStream content = new(
        sector.Length,
        (output, _) => output.Write(sector),
        progress: null,
        CancellationToken.None);
    content.WaitUntilPrebuffered(CancellationToken.None);
    OpticalBurnRequest request = new(
        "test-recorder",
        OpticalBurnMediaKind.CdAudio,
        sector.Length,
        (_, _) => { },
        new OpticalWriteSpeed(1200, false));
    foreach ((int[] reported, int expected) in new[]
    {
        (new[] { 1, 2, 3 }, CdRawSectorType.SubcodeIsRaw),
        (new[] { 3 }, CdRawSectorType.SubcodeIsRaw),
        (new[] { 1, 2 }, CdRawSectorType.SubcodeIsCooked),
    })
    {
        RecordingCdRawSession session = new() { ReportedSectorTypes = reported };
        using RecordingCdRawImageSession imageSession = new(session.Calls);
        List<string> diagnostics = [];
        WindowsImapiBurner.BurnCdAudio(
            session,
            imageSession,
            content,
            request,
            CancellationToken.None,
            reportDiagnostic: diagnostics.Add);
        string reportedText = string.Join(",", reported);
        Equal(expected, session.RequestedSectorType ?? 0, $"CD DAO requests type {expected} for [{reportedText}]");
        Equal(expected, imageSession.ImageSectorType ?? 0, $"CD DAO image uses type {expected} for [{reportedText}]");
        Equal(
            1,
            session.Calls.Count(call => call == nameof(ICdRawSession.SupportedSectorTypes)),
            "CD DAO reads the supported sector types once");
        True(
            diagnostics.Count(message => message == WindowsImapiBurner.DescribeCdDaoSectorType(expected, reported)) == 1,
            "CD DAO logs the chosen subchannel format once");
        True(session.Calls.Contains(nameof(ICdRawSession.WriteMedia)), "CD DAO writes with a usable sector type");
    }

    RecordingCdRawSession pqOnlySession = new() { ReportedSectorTypes = [1] };
    using RecordingCdRawImageSession pqOnlyImage = new(pqOnlySession.Calls);
    try
    {
        WindowsImapiBurner.BurnCdAudio(
            pqOnlySession,
            pqOnlyImage,
            content,
            request,
            CancellationToken.None);
        throw new InvalidOperationException("CD DAO PQ-only recorder: expected InvalidOperationException.");
    }
    catch (InvalidOperationException exception)
    {
        True(
            exception.Message.Contains("光驱报告：1", StringComparison.Ordinal),
            "CD DAO explains why a PQ-only recorder is rejected");
        True(
            !pqOnlySession.Calls.Contains(nameof(ICdRawSession.SetRequestedSectorType))
                && !pqOnlySession.Calls.Contains(nameof(ICdRawSession.SetWriteSpeed))
                && !pqOnlySession.Calls.Contains(nameof(ICdRawImageSession.CreateImage))
                && !pqOnlySession.Calls.Contains(nameof(ICdRawSession.WriteMedia))
                && pqOnlySession.Calls[^1] == nameof(ICdRawSession.ReleaseMedia),
            "CD DAO releases prepared media without writing when no P-W layout is available");
    }
}

static void TestCdRawImageConstruction()
{
    const int sectorCount = 300;
    const int rawSectorBytes = 2448;
    byte[] sector = Enumerable.Repeat((byte)0x5A, 2352).ToArray();
    using GeneratedContentComStream cookedContent = CreateRepeatedSectorStream(sector, sectorCount);
    using GeneratedContentComStream rawContent = CreateRepeatedSectorStream(sector, sectorCount);
    using DynamicCdRawImageSession cookedSession = new();
    using DynamicCdRawImageSession rawSession = new();
    IStream cookedImage = cookedSession.CreateImage(cookedContent, CdRawSectorType.SubcodeIsCooked);
    IStream rawImage = rawSession.CreateImage(rawContent, CdRawSectorType.SubcodeIsRaw);
    cookedImage.Stat(out STATSTG cookedStat, 1);
    rawImage.Stat(out STATSTG stat, 1);
    Equal(300L, cookedSession.StartOfLeadout, "Raw CD image lead-out matches audio length");
    Equal(300L, rawSession.StartOfLeadout, "raw P-W CD image lead-out matches audio length");
    True(
        stat.cbSize > sectorCount * (long)sector.Length,
        "Raw CD image includes DAO lead-in, subcode, and lead-out");
    Equal(cookedStat.cbSize, stat.cbSize, "cooked R-W and raw P-W CD images have the same length");
    // The creator defaults to cooked R-W, so only the raw run proves the type reaches IMAPI.
    Equal(CdRawSectorType.SubcodeIsRaw, rawSession.ResultingImageType, "raw P-W type reaches the IMAPI image creator");
    Equal(0L, stat.cbSize % rawSectorBytes, "raw P-W CD image uses complete 2448-byte sectors");
    using DynamicCdRawImageSession pqOnlySession = new();
    Throws<ArgumentOutOfRangeException>(
        () => pqOnlySession.CreateImage(rawImage, 1),
        "PQ-only 2368-byte CD images are rejected");

    // IMAPI writes no R-W data, so both layouts must carry identical bytes; the
    // requested type then only changes the subchannel layout announced to the recorder.
    byte[] cookedChunk = new byte[rawSectorBytes * 64];
    byte[] rawChunk = new byte[cookedChunk.Length];
    for (long offset = 0; offset < stat.cbSize; offset += rawChunk.Length)
    {
        int count = (int)Math.Min(rawChunk.Length, stat.cbSize - offset);
        ReadComStreamExactly(cookedImage, cookedChunk, count);
        ReadComStreamExactly(rawImage, rawChunk, count);
        True(
            cookedChunk.AsSpan(0, count).SequenceEqual(rawChunk.AsSpan(0, count)),
            $"cooked R-W and raw P-W CD images match from byte {offset}");
        for (int subcode = 2352; subcode < count; subcode += rawSectorBytes)
        {
            for (int index = subcode; index < subcode + 96; index++)
            {
                True((rawChunk[index] & 0x3F) == 0, "IMAPI CD images carry no R-W data");
            }
        }
    }
}

static GeneratedContentComStream CreateRepeatedSectorStream(byte[] sector, int sectorCount)
{
    GeneratedContentComStream content = new(
        sectorCount * (long)sector.Length,
        (output, _) =>
        {
            for (int index = 0; index < sectorCount; index++)
            {
                output.Write(sector);
            }
        },
        progress: null,
        CancellationToken.None);
    content.WaitUntilPrebuffered(CancellationToken.None);
    return content;
}

static void ReadComStreamExactly(IStream stream, byte[] buffer, int count)
{
    IntPtr read = Marshal.AllocCoTaskMem(sizeof(int));
    try
    {
        int offset = 0;
        while (offset < count)
        {
            byte[] part = offset == 0 ? buffer : new byte[count - offset];
            stream.Read(part, count - offset, read);
            int got = Marshal.ReadInt32(read);
            True(got > 0, "COM stream returns data before its declared end");
            if (offset != 0)
            {
                Buffer.BlockCopy(part, 0, buffer, offset, got);
            }

            offset += got;
        }
    }
    finally
    {
        Marshal.FreeCoTaskMem(read);
    }
}

static void WriteSolidPng(string path, int width, int height, byte level)
{
    int stride = checked(width * 4);
    byte[] pixels = new byte[checked(stride * height)];
    for (int offset = 0; offset < pixels.Length; offset += 4)
    {
        pixels[offset] = level;
        pixels[offset + 1] = level;
        pixels[offset + 2] = level;
        pixels[offset + 3] = 255;
    }

    BitmapSource bitmap = BitmapSource.Create(
        width,
        height,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        pixels,
        stride);
    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using FileStream output = File.Create(path);
    encoder.Save(output);
}

sealed class ForwardOnlyWriteStream(Stream destination) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => destination.Flush();

    public override void Write(byte[] buffer, int offset, int count)
        => destination.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer)
        => destination.Write(buffer);

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();
}

sealed class RecordingCdRawSession : ICdRawSession
{
    internal List<string> Calls { get; } = [];

    internal long LastLeadoutSector { get; init; } = 1;

    internal IReadOnlyList<int> ReportedSectorTypes { get; init; } = [CdRawSectorType.SubcodeIsCooked];

    internal Exception? WriteMediaError { get; init; }

    internal Exception? ReleaseMediaError { get; init; }

    internal int LastRequestedWriteSpeed { get; private set; }

    internal int? RequestedSectorType { get; private set; }

    public bool MediaPhysicallyBlank
    {
        get
        {
            Calls.Add(nameof(MediaPhysicallyBlank));
            return true;
        }
    }

    public long LastPossibleStartOfLeadout
    {
        get
        {
            Calls.Add(nameof(LastPossibleStartOfLeadout));
            return LastLeadoutSector;
        }
    }

    public IReadOnlyList<int> SupportedSectorTypes
    {
        get
        {
            Calls.Add(nameof(SupportedSectorTypes));
            return ReportedSectorTypes;
        }
    }

    public int CurrentWriteSpeed
    {
        get
        {
            Calls.Add(nameof(CurrentWriteSpeed));
            return 75;
        }
    }

    public bool CurrentRotationTypeIsPureCav
    {
        get
        {
            Calls.Add(nameof(CurrentRotationTypeIsPureCav));
            return false;
        }
    }

    public void PrepareMedia() => Calls.Add(nameof(PrepareMedia));

    public void SetRequestedSectorType(int sectorType)
    {
        Calls.Add(nameof(SetRequestedSectorType));
        RequestedSectorType = sectorType;
    }

    public void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav)
    {
        Calls.Add(nameof(SetWriteSpeed));
        LastRequestedWriteSpeed = sectorsPerSecond;
    }

    public void WriteMedia(IStream content)
    {
        Calls.Add(nameof(WriteMedia));
        if (WriteMediaError is not null)
        {
            throw WriteMediaError;
        }
    }

    public void CancelWrite() => Calls.Add(nameof(CancelWrite));

    public void ReleaseMedia()
    {
        Calls.Add(nameof(ReleaseMedia));
        if (ReleaseMediaError is not null)
        {
            throw ReleaseMediaError;
        }
    }
}

sealed class RecordingCdRawImageSession(List<string> calls) : ICdRawImageSession
{
    internal long ImageStartOfLeadout { get; init; } = 1;

    internal int? ImageSectorType { get; private set; }

    public long StartOfLeadout
    {
        get
        {
            calls.Add(nameof(StartOfLeadout));
            return ImageStartOfLeadout;
        }
    }

    public IStream CreateImage(IStream audioContent, int sectorType)
    {
        calls.Add(nameof(CreateImage));
        ImageSectorType = sectorType;
        return audioContent;
    }

    public void Dispose()
    {
    }
}

sealed class ConflictingModule : IOpticalDiscModule
{
    public DiscModuleDescriptor Descriptor { get; } = new(
        "conflict-test",
        "Conflicting module",
        OpticalDiscFamily.BluRay,
        "Test-only duplicate command.",
        SupportsCancellation: false,
        Commands:
        [
            new DiscCommandDescriptor(
                "bd-generate",
                "Conflict",
                "Duplicate command.",
                DiscModuleCapabilities.None,
                []),
        ]);

    public Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
