using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

internal static class ProjectSessionTests
{
    internal static void Run()
    {
        TestBluRayRoundtrip();
        TestFamilyIsolation();
        TestCdAudioFormatPerProject();
        Console.WriteLine("project-session: passed (BD apply/capture, tone normalization, polarity, family isolation, CD RAW/WAV per project)");
    }

    private static void TestBluRayRoundtrip()
    {
        ProjectDocument document = Document(
            ProjectDocument.BluRayFamily,
            new ProjectGeometry(23.98822105616562, 58.023234731100764, 1024),
            new ProjectOutputs(@"C:\out\bd.iso", false, @"C:\data", "BD_IMAGE", false),
            new ToneProfile(0.4, ToneProfile.FourLevels, 6, SrgbLinearization: true),
            new ProjectBluRaySettings(InvertMapping: true));
        DiscParametersState state = new();
        ProjectSession.ApplyToState(document, state);

        Require(state.BdInnerRadius == "23.98822105616562", "BD inner radius");
        Require(state.BdOuterRadius == "58.023234731100764", "BD outer radius");
        Require(state.BdTotalSectors == "1024", "BD sectors");
        Require(state.BdActualInnerRadius == state.BdInnerRadius, "BD measured geometry starts neutral");
        Require(state.BdOutputPath == @"C:\out\bd.iso" && state.BdVolumeLabel == "BD_IMAGE", "BD outputs");
        Require(state.BdInvertMapping, "BD polarity");
        Require(state.ToneLevels == ToneProfile.TwoLevels, "BD tone is binary");
        Require(state.ToneSrgbLinearization && Math.Abs(state.ToneBrightness - 6) < 1e-12, "BD gamma and brightness");

        state.BdInnerRadius = "24.1";
        state.BdOuterRadius = "57.9";
        state.BdTotalSectors = "2048";
        state.BdOutputPath = @"C:\out\edited.iso";
        state.BdInvertMapping = false;
        ProjectGeometry captured = ProjectSession.CaptureGeometry(document, state);
        Require(captured.InnerRadiusMm == 24.1 && captured.OuterRadiusMm == 57.9
            && captured.Sectors == 2048 && captured.PitchLinear == 0, "BD geometry capture");
        Require(ProjectSession.CaptureBluRay(document, state)?.InvertMapping == false, "BD polarity capture");
        Require(ProjectSession.CaptureTone(document, state).Levels == ToneProfile.TwoLevels,
            "BD capture keeps two levels");
    }

    private static void TestFamilyIsolation()
    {
        ProjectDocument cd = Document(
            ProjectDocument.CdFamily,
            new ProjectGeometry(24.5, 56.8, 359_849),
            new ProjectOutputs(@"C:\out\cd.wav", true, string.Empty, string.Empty, true),
            new ToneProfile(0.5, ToneProfile.FourLevels, 0),
            BluRay: null);
        DiscParametersState state = new();
        ProjectSession.ApplyToState(cd, state);
        Require(state.CdSectors == "359849" && state.ToneLevels == ToneProfile.FourLevels,
            "CD session keeps four levels");
        Require(ProjectSession.CaptureBluRay(cd, state) is null, "CD project has no BD section");
        Require(ProjectSession.CaptureTone(cd, state).Levels == ToneProfile.FourLevels,
            "CD capture keeps four levels");

        ProjectDocument dvd = Document(
            ProjectDocument.DvdFamily,
            new ProjectGeometry(24.0, 58.0, 2_295_104, 0.001, 0, 0),
            new ProjectOutputs(@"C:\out\dvd.iso", false, string.Empty, "DISC_IMAGE", false),
            new ToneProfile(0.5, ToneProfile.FourLevels, 0),
            BluRay: null);
        ProjectSession.ApplyToState(dvd, state);
        Require(state.DvdTotalSectors == "2295104" && state.ToneLevels == ToneProfile.TwoLevels,
            "DVD session normalizes four levels to two");
    }

    /// <summary>Opening a project takes the RAW/WAV choice from that project's saved output
    /// path: a RAW project opened before must not turn this project's ISO into RAW once its
    /// disc files are removed.</summary>
    private static void TestCdAudioFormatPerProject()
    {
        DiscParametersState state = new();
        ProjectSession.ApplyToState(CdProject("first.raw", string.Empty), state);
        Require(state.CdOutputPath == "first.raw", "a RAW project opens as RAW");
        ProjectSession.ApplyToState(CdProject("second.iso", "files"), state);
        Require(state.CdOutputPath == "second.iso", "a project with disc files opens as an ISO");
        state.CdDataDirectory = string.Empty;
        Require(state.CdOutputPath == "second.wav",
            "removing the files of a project saved as an ISO gives WAV, not the previous project's RAW");

        ProjectSession.ApplyToState(CdProject("third.raw", string.Empty), state);
        state.CdDataDirectory = "files";
        Require(state.CdOutputPath == "third.iso", "disc files turn a RAW output into an ISO");
        state.CdDataDirectory = string.Empty;
        Require(state.CdOutputPath == "third.raw", "removing them again restores the RAW output");
    }

    private static ProjectDocument CdProject(string outputPath, string dataDirectory)
        => Document(
            ProjectDocument.CdFamily,
            new ProjectGeometry(24.5, 56.8, 359_849),
            new ProjectOutputs(outputPath, false, dataDirectory, string.Empty, true),
            new ToneProfile(0.5, ToneProfile.FourLevels, 0),
            BluRay: null);

    private static ProjectDocument Document(
        string family, ProjectGeometry geometry, ProjectOutputs outputs, ToneProfile tone,
        ProjectBluRaySettings? BluRay)
        => new(
            ProjectJsonStore.CurrentSchemaVersion,
            "session-test",
            family,
            geometry,
            [],
            outputs,
            CalibrationSessionPath: null,
            ThumbnailPngBase64: null,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            ModifiedUtc: DateTimeOffset.UnixEpoch,
            Tone: tone,
            BluRay: BluRay);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Project session: " + message + ".");
        }
    }
}
