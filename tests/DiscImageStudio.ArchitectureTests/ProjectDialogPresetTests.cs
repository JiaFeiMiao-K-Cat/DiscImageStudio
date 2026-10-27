using System.IO;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class ProjectDialogPresetTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-dialog-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestSwitchedBluRayPolarity(directory, originalPolarity: false);
            TestSwitchedBluRayPolarity(directory, originalPolarity: true);
            TestDeletingRecentAcrossFamilies(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("project-dialog-presets: passed (switched BD polarity, cross-family recent deletion)");
    }

    private static void TestSwitchedBluRayPolarity(string directory, bool originalPolarity)
    {
        string presetPath = Path.Combine(directory, $"polarity-{originalPolarity}.json");
        DiscPresetJsonStore.Save(presetPath, DiscPresetJsonStore.CreateInitialUserDocument());
        DiscParametersState state = new(presetPath);
        state.LoadPresets();
        state.BdInvertMapping = originalPolarity;
        state.AddUserPreset(
            OpticalDiscFamily.BluRay, "切换目标", 24, 58, 2048,
            invertMapping: !originalPolarity);
        string targetId = state.BluRayPresets.Single(preset => preset.DisplayName == "切换目标").Id;
        RecentTemplateStore recent = new(Path.Combine(directory, $"recent-polarity-{originalPolarity}.json"));
        ProjectDialogViewModel dialog = new(
            state, new ProjectDialogRequest(EditMode: true, PreselectFamily: OpticalDiscFamily.BluRay), recent)
        {
            ConfirmPresetSwitch = _ => Task.FromResult(true),
            ShowPresetSaveDialog = () => new PresetSaveResult("另存的预设"),
            SaveDirectory = directory,
        };

        dialog.SelectedTemplate = dialog.AllTemplates.Single(tile => tile.PresetId == targetId);
        Require(dialog.SectorsText == "2048", "confirmed switch applies the target geometry");
        Require(state.BdInvertMapping == originalPolarity, "dialog leaves the open project untouched until confirmation");
        dialog.SaveAsPresetCommand.Execute(null);
        Require(string.IsNullOrEmpty(dialog.ErrorMessage), "saving the switched preset succeeds");
        var saved = DiscPresetJsonStore.Load(presetPath).BluRayPresets!
            .Single(preset => preset.DisplayName == "另存的预设");
        Require(saved.InvertMapping == !originalPolarity, "saved preset keeps the switched BD polarity");
        dialog.ConfirmCommand.Execute(null);
        Require(dialog.Result?.BluRayInvertMapping == saved.InvertMapping,
            "confirming the project and saving its preset use the same polarity");
    }

    private static void TestDeletingRecentAcrossFamilies(string directory)
    {
        string presetPath = Path.Combine(directory, "delete-presets.json");
        DiscPresetJsonStore.Save(presetPath, DiscPresetJsonStore.CreateInitialUserDocument());
        DiscParametersState state = new(presetPath);
        state.LoadPresets();
        RecentTemplateStore recent = new(Path.Combine(directory, "recent-delete.json"));
        recent.Record("bd-r-sl-25gb");
        recent.Record("user-custom-cd");
        ProjectDialogViewModel dialog = new(state, new ProjectDialogRequest(EditMode: false), recent)
        {
            SaveDirectory = directory,
        };
        Require(dialog.TemplateCategoryIndex == ProjectDialogViewModel.RecentCategoryIndex
            && dialog.Family == OpticalDiscFamily.CompactDisc, "recent CD preset starts selected");
        dialog.DeletePresetCommand.Execute(dialog.SelectedTemplate);
        Require(string.IsNullOrEmpty(dialog.ErrorMessage), "deleting the selected recent preset succeeds");
        Require(dialog.SelectedTemplate?.PresetId == "bd-r-sl-25gb", "next recent preset becomes selected");
        Require(dialog.Family == OpticalDiscFamily.BluRay, "next recent preset changes the form's disc family");
        Require(dialog.SectorsText == "12219392", "next recent preset applies the matching geometry");
        dialog.ConfirmCommand.Execute(null);
        Require(dialog.Result?.Family == OpticalDiscFamily.BluRay
            && dialog.Result.Sectors == 12219392, "created project matches the highlighted recent preset");
        Require(recent.Load().SequenceEqual(new[] { "bd-r-sl-25gb" }), "deleted preset leaves recent history");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Project dialog presets: " + message + ".");
        }
    }
}
