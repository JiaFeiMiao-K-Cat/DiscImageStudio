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
            foreach (string family in new[] { ProjectDocument.DvdFamily, ProjectDocument.BluRayFamily })
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
                Require(family == ProjectDocument.DvdFamily ? state.DvdFilesAtOuterRing : state.BdFilesAtOuterRing,
                    "project applies placement to the matching family");
                Require(ProjectSession.CaptureOutputs(loaded, state).FilesAtOuterRing,
                    "saving captures the selected file position");

                // Existing project files do not carry the optional position field. Opening one
                // after an outer-ring project must reset that selection to the original inner mode.
                JsonObject legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                legacy["outputs"]!.AsObject().Remove("filesAtOuterRing");
                File.WriteAllText(path, legacy.ToJsonString());
                ProjectDocument old = ProjectJsonStore.Load(path);
                Require(!old.Outputs.FilesAtOuterRing, "missing position defaults to inner");
                ProjectSession.ApplyToState(old, state);
                Require(!(family == ProjectDocument.DvdFamily ? state.DvdFilesAtOuterRing : state.BdFilesAtOuterRing),
                    "legacy project resets a previous outer selection");
                ProjectJsonStore.Save(path, old);
                Require(!File.ReadAllText(path).Contains("filesAtOuterRing", StringComparison.Ordinal),
                    "default position does not add a field to existing inner-ring projects");

                ProjectDocument cd = document with
                {
                    DiscFamily = ProjectDocument.CdFamily,
                    BluRay = null,
                };
                try
                {
                    ProjectJsonStore.Validate(cd);
                    throw new InvalidOperationException("CD must reject an outer-files setting.");
                }
                catch (InvalidDataException) { }
            }
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        Console.WriteLine("file-placement-project: passed (DVD/BD persistence, legacy defaults, family isolation)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("File placement project: " + message);
    }
}
