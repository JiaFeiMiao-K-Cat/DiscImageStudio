using System.IO;
using System.Text.Json.Nodes;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

internal static class FilePlacementProjectTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"file-placement-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string family in new[] { ProjectDocument.CdFamily, ProjectDocument.DvdFamily, ProjectDocument.BluRayFamily })
            {
                ProjectDocument document = new(
                    ProjectJsonStore.CurrentSchemaVersion, "文件位置", family,
                    new ProjectGeometry(24, 58, 2048), [],
                    new ProjectOutputs(Path.Combine(directory, "output.iso"), false,
                        Path.Combine(directory, "files"), "FILES", false, FilesAtOuterRing: true),
                    null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                string path = Path.Combine(directory, family + ".discproj");
                ProjectJsonStore.Save(path, document);
                ProjectDocument loaded = ProjectJsonStore.Load(path);
                Require(loaded.Outputs.FilesAtOuterRing, "outer placement survives save/load");
                DiscParametersState state = new();
                ProjectSession.ApplyToState(loaded, state);
                Require(FilesAtOuterRing(family, state),
                    "project applies placement to the matching family");
                Require(ProjectSession.CaptureOutputs(loaded, state).FilesAtOuterRing,
                    "saving captures the selected file position");
                Require(ProjectSession.CaptureOutputs(loaded, state).DataDirectory == loaded.Outputs.DataDirectory
                    && ProjectSession.CaptureOutputs(loaded, state).VolumeLabel == "FILES",
                    "saving retains the folder and volume label");

                // Existing project files do not carry the optional position field. Opening one
                // after an outer-ring project must reset that selection to the original inner mode.
                JsonObject legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                legacy["outputs"]!.AsObject().Remove("filesAtOuterRing");
                File.WriteAllText(path, legacy.ToJsonString());
                ProjectDocument old = ProjectJsonStore.Load(path);
                Require(!old.Outputs.FilesAtOuterRing, "missing position defaults to inner");
                ProjectSession.ApplyToState(old, state);
                Require(!FilesAtOuterRing(family, state),
                    "legacy project resets a previous outer selection");
                ProjectJsonStore.Save(path, old);
                Require(!File.ReadAllText(path).Contains("filesAtOuterRing", StringComparison.Ordinal),
                    "default position does not add a field to existing inner-ring projects");
            }
            TestCdModeSwitch(directory);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        Console.WriteLine("file-placement-project: passed (CD/DVD/BD persistence, CD mode switching, legacy defaults)");
    }

    private static bool FilesAtOuterRing(string family, DiscParametersState state) => family switch
    {
        ProjectDocument.CdFamily => state.CdFilesAtOuterRing,
        ProjectDocument.DvdFamily => state.DvdFilesAtOuterRing,
        _ => state.BdFilesAtOuterRing,
    };

    private static void TestCdModeSwitch(string directory)
    {
        DiscParametersState state = new() { CdOutputPath = Path.Combine(directory, "cd.wav") };
        Require(state.IsCdAudioMode && state.IsCdWaveOutput && !state.HasCdFiles,
            "CD starts in audio mode without files");
        List<string?> notifications = [];
        state.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        state.CdDataDirectory = Path.Combine(directory, "files");
        Require(state.HasCdFiles && !state.IsCdAudioMode && !state.IsCdWaveOutput,
            "adding files selects data mode and hides audio options");
        Require(Path.GetExtension(state.CdOutputPath) == ".iso", "adding files changes output to ISO");
        Require(notifications.Contains(nameof(DiscParametersState.HasCdFiles))
            && notifications.Contains(nameof(DiscParametersState.IsCdAudioMode))
            && notifications.Contains(nameof(DiscParametersState.IsCdWaveOutput)),
            "mode-dependent UI properties are refreshed");
        state.CdFilesAtOuterRing = true;
        state.CdDataDirectory = " ";
        Require(state.IsCdAudioMode && state.IsCdWaveOutput && state.CdFilesAtOuterRing,
            "clearing the folder restores audio while retaining placement preference");
        Require(Path.GetExtension(state.CdOutputPath) == ".wav", "clearing files restores WAV output");

        // Loading an older audio project after a file project must clear its data-mode fields.
        ProjectDocument audio = new(ProjectJsonStore.CurrentSchemaVersion, "audio", ProjectDocument.CdFamily,
            new ProjectGeometry(24.5, 56.8, 1000), [],
            new ProjectOutputs(Path.Combine(directory, "audio.raw"), false, string.Empty, string.Empty, true),
            null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        state.CdDataDirectory = Path.Combine(directory, "files");
        ProjectSession.ApplyToState(audio, state);
        Require(state.IsCdAudioMode && !state.CdFilesAtOuterRing && state.CdVolumeLabel == "CD_IMAGE"
            && state.CdOutputPath == audio.Outputs.OutputPath && !state.IsCdWaveOutput,
            "legacy CD project clears data settings and retains its RAW output");

        ProjectDocument files = audio with
        {
            Outputs = audio.Outputs with
            {
                DataDirectory = Path.Combine(directory, "files"),
                FilesAtOuterRing = true,
            },
        };
        ProjectSession.ApplyToState(files, state);
        Require(state.HasCdFiles && state.CdFilesAtOuterRing && Path.GetExtension(state.CdOutputPath) == ".iso",
            "opening a file project normalizes a stale audio output suffix");
        state.CdOutputPath = Path.Combine(directory, "stale.wav");
        ProjectSession.ApplyToState(files, state);
        Require(Path.GetExtension(state.CdOutputPath) == ".iso",
            "reopening the same data folder still normalizes the output suffix");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("File placement project: " + message);
    }
}
