using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class PresetApplicationTests
{
    internal static void Run()
    {
        WpfTestHost.Run(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "preset-application-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
            try
            {
                foreach (OpticalDiscFamily family in Enum.GetValues<OpticalDiscFamily>())
                    CheckFamily(shell, family, directory);
            }
            finally
            {
                shell.Calibration.OnWindowClosed();
                Directory.Delete(directory, recursive: true);
            }
        });
        Console.WriteLine("preset-application: passed (CD/DVD/BD save-and-apply, cancellation, same-name IDs, preview sync, rename isolation, BD polarity)");
    }

    private static void CheckFamily(ShellViewModel shell, OpticalDiscFamily family, string directory)
    {
        string presetPath = Path.Combine(directory, family + ".json");
        typeof(DiscParametersState).GetField("_discPresetJsonPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(shell.State, presetPath);
        DiscParametersState state = shell.State;
        state.LoadPresets();
        long total = family switch { OpticalDiscFamily.Dvd => 2_295_104, OpticalDiscFamily.BluRay => 12_219_392, _ => 359_849 };
        long usable = DiscParametersState.ResolveUsableSectors(family, total);
        ProjectGeometry original = new(24, 58, total, .0001, -.00008, .00004, usable);
        ProjectGeometry fitted = original with
        {
            InnerRadiusMm = 23.991460702437305, OuterRadiusMm = 57.93579168028281,
            PitchLinear = -2.6724564452606465E-05, PitchQuadratic = 4.0503386747536064E-05,
            PitchCubic = -2.771840423519746E-05,
        };
        ProjectDocument document = new(ProjectJsonStore.CurrentSchemaVersion, "original",
            family switch { OpticalDiscFamily.Dvd => "dvd", OpticalDiscFamily.BluRay => "bd", _ => "cd" },
            original, [], new(Path.Combine(directory, "image.iso"), false, "", "TEST", false),
            null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        ProjectSession.ApplyToState(document, state);
        typeof(ShellViewModel).GetProperty(nameof(shell.CurrentProject))!.SetValue(shell, document);
        shell.IsProjectOpen = true;
        PreviewInspectorViewModel preview = new(shell, state);
        preview.SetFamily(family);

        ProjectDialogViewModel Dialog(bool edit) => new(state, new(edit, family),
            new RecentTemplateStore(Path.Combine(directory, "recent.json")))
        {
            Name = "unconfirmed rename", SaveDirectory = directory,
            InnerRadiusText = Text(fitted.InnerRadiusMm), OuterRadiusText = Text(fitted.OuterRadiusMm),
            SectorsText = total.ToString(CultureInfo.InvariantCulture), UsableSectorsText = usable.ToString(CultureInfo.InvariantCulture),
            PitchLinearText = Text(fitted.PitchLinear), PitchQuadraticText = Text(fitted.PitchQuadratic),
            PitchCubicText = Text(fitted.PitchCubic),
        };

        ProjectDialogViewModel create = Dialog(false);
        create.ShowPresetSaveDialog = () => new("same name");
        create.SaveAsPresetCommand.Execute(null);
        Require(ProjectSession.CaptureGeometry(document, state) == original, "new-project save leaves open project untouched");

        ProjectDialogViewModel edit = Dialog(true);
        edit.ShowPresetSaveDialog = () => null;
        edit.SaveAsPresetCommand.Execute(null);
        Require(ProjectSession.CaptureGeometry(document, state) == original, "cancelled preset name leaves project untouched");
        string validCatalog = File.ReadAllText(presetPath);
        File.WriteAllText(presetPath, "{}");
        edit.ShowPresetSaveDialog = () => new("same name");
        edit.SaveAsPresetCommand.Execute(null);
        Require(!string.IsNullOrEmpty(edit.ErrorMessage) && ProjectSession.CaptureGeometry(document, state) == original,
            "failed preset save leaves project untouched");
        File.WriteAllText(presetPath, validCatalog);
        edit.SaveAsPresetCommand.Execute(null);
        Require(string.IsNullOrEmpty(edit.ErrorMessage), edit.ErrorMessage);
        Require(ProjectSession.CaptureGeometry(document, state) == fitted, "saving in project settings applies immediately");
        Require(shell.IsDirty && shell.CurrentProject!.Name == "original", "save applies geometry and marks dirty without committing name");
        RequirePreview(preview, fitted);
        string selectedId = family switch { OpticalDiscFamily.Dvd => state.SelectedDvdPreset.Id,
            OpticalDiscFamily.BluRay => state.SelectedBluRayPreset.Id, _ => state.SelectedCdPreset.Id };
        Require(edit.SelectedTemplate?.PresetId == selectedId && edit.AllTemplates.Count(t => t.Title == "same name") == 2,
            "newly saved preset is selected by ID even with a duplicate name");
        edit.InnerRadiusText = "25";
        shell.ShowProjectDialogHandler = _ => null; // Close after saving; later form edits are discarded.
        shell.EditProjectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(ProjectSession.CaptureGeometry(document, state) == fitted, "cancel after saving keeps only the saved geometry");

        preview.MeasuredPitchLinear = "0.0003";
        ProjectDialogResult same = new("renamed", family, fitted.InnerRadiusMm, fitted.OuterRadiusMm, total,
            fitted.PitchLinear, fitted.PitchQuadratic, fitted.PitchCubic, "unused", UsableSectors: usable);
        shell.ShowProjectDialogHandler = _ => same;
        shell.EditProjectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(preview.MeasuredPitchLinear == "0.0003", "rename alone preserves manual measured geometry");
        shell.ShowProjectDialogHandler = _ => same with { SynchronizePreview = true, BluRayInvertMapping = true };
        shell.EditProjectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        RequirePreview(preview, fitted);
        if (family == OpticalDiscFamily.BluRay) Require(state.BdInvertMapping, "confirmed BD preset applies polarity");

        // A manual geometry edit also starts the preview from the new geometry.
        preview.MeasuredPitchCubic = "0.0006";
        shell.ShowProjectDialogHandler = _ => same with { InnerRadiusMm = 24.01, PitchLinear = .00015 };
        shell.EditProjectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        RequirePreview(preview, fitted with { InnerRadiusMm = 24.01, PitchLinear = .00015 });
    }

    private static void RequirePreview(PreviewInspectorViewModel preview, ProjectGeometry geometry)
    {
        Require(Number(preview.MeasuredInnerRadius) == geometry.InnerRadiusMm
            && Number(preview.MeasuredOuterRadius) == geometry.OuterRadiusMm
            && Number(preview.MeasuredPitchLinear) == geometry.PitchLinear
            && Number(preview.MeasuredPitchQuadratic) == geometry.PitchQuadratic
            && Number(preview.MeasuredPitchCubic) == geometry.PitchCubic, "advanced preview matches project geometry");
    }
    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Preset application: " + message);
    }
}
