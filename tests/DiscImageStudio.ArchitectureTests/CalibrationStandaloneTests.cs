using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class CalibrationStandaloneTests
{
    internal static void Run()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), $"standalone-calibration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try { CheckIsolation(directory); }
            catch (Exception ex) { error = ex; }
            finally { Directory.Delete(directory, recursive: true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        Console.WriteLine("standalone-calibration: project-free setup, frozen target, portable record and reusable preset passed");
    }

    private static void CheckIsolation(string directory)
    {
        var shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
        var model = shell.Calibration;
        var setup = model.Parameters;
        var project = shell.State;
        string previousRadius = project.DvdOuterRadius;
        string previousSectors = project.DvdTotalSectors;
        shell.NavigateTo(Workspace.Calibration);
        Require(!shell.IsProjectOpen && shell.CurrentWorkspace == Workspace.Calibration && model.Target is null
            && !ReferenceEquals(setup, project), "calibration opens at setup, without a drawing project");

        model.SelectedDiscType = "DVD";
        setup.SelectedDvdPreset = setup.DvdPresets.Single(p => p.Id == "dvd-5-80mm");
        Require(!model.CreateTargetCommand.CanExecute(null), "mini DVD has insufficient space for the v13 angle sweep");
        model.SelectFineModeCommand.Execute(null);
        model.CreateTargetCommand.Execute(null);
        var target = model.Target ?? throw new InvalidOperationException(model.LastStatusMessage);
        Require(target.Parameters is { Kind: CalibrationDiscKind.Dvd, InnerRadiusMm: 24, OuterRadiusMm: 38, Sectors: 714544 }
            && target.HasCrossArms, "standalone fine setup can still generate a mini DVD");
        string frozenSummary = model.TargetSummary;
        string sessionPath = Path.Combine(directory, "small.calibration.json");
        model.SaveSessionTo(sessionPath);
        setup.DvdOuterRadius = "37";
        model.SelectedDiscType = "BD";
        shell.NavigateTo(Workspace.Home);
        shell.NavigateTo(Workspace.Calibration);
        Require(ReferenceEquals(model.Target, target) && model.TargetSummary == frozenSummary
            && model.IsBluRaySetup && !shell.OpenOutputEnabled, "changing setup or visiting Home preserves the target and project output state");
        model.LoadSessionFromAsync(sessionPath).GetAwaiter().GetResult();
        Require(model.Target?.Id == target.Id && model.IsDvdSetup && double.Parse(setup.DvdOuterRadius, CultureInfo.InvariantCulture) == 38
            && setup.DvdTotalSectors == "714544", "loading a standalone record restores its setup and snapshot");

        // Write the named preset to a temporary catalog, then share its list in memory.
        var isolatedCatalog = new DiscParametersState(Path.Combine(directory, "presets.json"));
        isolatedCatalog.LoadPresets();
        var saver = new CalibrationViewModel(shell, isolatedCatalog) { SelectedDiscType = "DVD" };
        isolatedCatalog.SelectedDvdPreset = isolatedCatalog.DvdPresets.Single(p => p.Id == "dvd-5-80mm");
        isolatedCatalog.DvdOuterRadius = "37.995";
        saver.ShowPresetSaveDialog = () => new("Small DVD calibrated");
        saver.SaveAsPresetCommand.Execute(null);
        var preset = project.DvdPresets.Single(p => p.DisplayName == "Small DVD calibrated");
        Require(preset.OuterRadiusMm == 37.995 && preset.TotalSectors == 714544 && project.DvdOuterRadius == previousRadius
            && project.DvdTotalSectors == previousSectors && !shell.IsDirty, "a saved calibration preset becomes available without applying it to the drawing");
        saver.OnWindowClosed();
        model.OnWindowClosed();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Standalone calibration: " + message);
    }
}
