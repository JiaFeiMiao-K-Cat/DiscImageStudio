using System.IO;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class UsableSectorPersistenceTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-usable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestDefaultsAndBuiltIns();
            TestPresetAndDialogRoundtrip(directory);
            TestProjectRoundtrip(directory);
            TestCalibrationRoundtrip();
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("usable-sector-persistence: passed (legacy defaults, explicit limits, preset/project/calibration roundtrips)");
    }

    private static void TestDefaultsAndBuiltIns()
    {
        Require(CdDiscPreset.ResolveUsableSectors(359_845) == 359_843, "legacy CD reserve");
        Require(DvdDiscPreset.ResolveUsableSectors(714_546) == 713_936, "legacy DVD reserve and alignment");
        Require(BluRayDiscPreset.ResolveUsableSectors(1_025) == 1_024, "legacy BD alignment");
        Require(DvdDiscPreset.ResolveUsableSectors(2_297_888, 2_297_888) == 2_297_888,
            "explicit DVD capacity is not deducted again");

        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
        foreach (CdDiscPresetDefinition preset in builtIn.CdPresets)
            Require(preset.UsableSectors == preset.Sectors - 2, "built-in CD reserve");
        foreach (BluRayDiscPresetDefinition preset in builtIn.BluRayPresets!)
            Require(preset.UsableSectors == preset.TotalSectors, "built-in BD full capacity");
        foreach (DvdDiscPresetDefinition preset in builtIn.DvdPresets)
        {
            uint expected = preset.Id switch
            {
                "ritek-dvd-r-e-series" => 2_297_888,
                "dvd-5-80mm" => 713_936,
                _ => preset.TotalSectors,
            };
            Require(preset.UsableSectors == expected, "built-in DVD capacity " + preset.Id);
        }
    }

    private static void TestPresetAndDialogRoundtrip(string directory)
    {
        string path = Path.Combine(directory, "presets.json");
        DiscPresetJsonStore.Save(path, new(DiscPresetJsonStore.CurrentSchemaVersion, [], [], BluRayPresets: []));
        DiscParametersState state = new(path);
        state.LoadPresets();
        state.AddUserPreset(OpticalDiscFamily.Dvd, "unaligned geometry", 24, 38, 714_546,
            usableSectors: 713_936);
        DvdDiscPreset preset = state.DvdPresets.Single(value => value.DisplayName == "unaligned geometry");
        state.ApplyDvdDiscPreset(preset);
        Require(state.DvdTotalSectors == "714546" && state.DvdUsableSectors == "713936",
            "preset keeps geometry separate from output");
        Require(state.GetUsableSectors(OpticalDiscFamily.Dvd) == 713_936, "explicit preset does not reserve twice");

        // Reloading an edited catalog must not overwrite the open project's frozen values.
        DiscPresetJsonDocument changed = DiscPresetJsonStore.Load(path);
        DiscPresetJsonStore.Save(path, changed with
        {
            DvdPresets = changed.DvdPresets.Select(value => value with { UsableSectors = 713_920 }).ToArray(),
        });
        state.Relocalize();
        Require(state.DvdUsableSectors == "713936" && state.SelectedDvdPreset.Id == preset.Id,
            "relocalizing refreshes preset text without changing the live usable capacity");
        state.ApplyDvdDiscPreset(preset);
        state.LoadPresets();
        Require(state.DvdUsableSectors == "713936" && state.SelectedDvdPreset.IsCustom,
            "capacity-only catalog change leaves the live project intact and marks it custom");

        ProjectDialogViewModel dialog = new(state,
            new ProjectDialogRequest(EditMode: false, PresetId: preset.Id),
            new RecentTemplateStore(Path.Combine(directory, "recent.json")))
        {
            Name = "usable test",
            SaveDirectory = directory,
            ShowPresetSaveDialog = () => new PresetSaveResult("dialog saved"),
        };
        Require(dialog.SectorsText == "714546" && dialog.UsableSectorsText == "713920",
            "dialog reads the selected preset's explicit usable capacity");
        dialog.UsableSectorsText = "713904";
        dialog.SaveAsPresetCommand.Execute(null);
        dialog.ConfirmCommand.Execute(null);
        Require(dialog.Result?.UsableSectors == 713_904 && string.IsNullOrEmpty(dialog.ErrorMessage),
            "dialog confirmation includes the edited capacity");
        Require(DiscPresetJsonStore.Load(path).DvdPresets.Single(value => value.DisplayName == "dialog saved")
            .UsableSectors == 713_904, "save as preset keeps edited capacity");
        dialog.UsableSectorsText = "713905";
        dialog.ConfirmCommand.Execute(null);
        Require(!string.IsNullOrEmpty(dialog.ErrorMessage), "dialog rejects unaligned output");

        BluRayPresetStore bdStore = new(Path.Combine(directory, "bd.json"));
        bdStore.Save("short BD", new BluRayDiscParameters(2_048, UsableSectors: 1_024), false);
        BluRayDiscParameters bd = bdStore.Load().Single().CreateParameters();
        Require(bd.TotalSectors == 2_048 && bd.UsableSectors == 1_024,
            "independent BD catalog preserves output capacity");
    }

    private static void TestProjectRoundtrip(string directory)
    {
        foreach ((string family, long total, long expected) in new[]
        {
            (ProjectDocument.CdFamily, 359_845L, 359_843L),
            (ProjectDocument.DvdFamily, 714_546L, 713_936L),
            (ProjectDocument.BluRayFamily, 2_048L, 2_048L),
        })
        {
            ProjectDocument legacy = new(ProjectJsonStore.CurrentSchemaVersion, family, family,
                new ProjectGeometry(24, 38, total), [],
                new ProjectOutputs(Path.Combine(directory, family + ".iso"), false, "", "DISC", false),
                null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            string path = Path.Combine(directory, family + ".discproj");
            ProjectJsonStore.Save(path, legacy);
            Require(!File.ReadAllText(path).Contains("usableSectors", StringComparison.Ordinal),
                "missing field fixture remains an old document");
            DiscParametersState state = new();
            ProjectSession.ApplyToState(ProjectJsonStore.Load(path), state);
            Require(state.GetUsableSectors(legacy.Family) == expected, "old project default " + family);
            ProjectGeometry captured = ProjectSession.CaptureGeometry(legacy, state);
            Require(captured.Sectors == total && captured.UsableSectors == expected,
                "capture preserves geometry and stores resolved output " + family);
            ProjectJsonStore.Save(path, legacy with { Geometry = captured });
            ProjectSession.ApplyToState(ProjectJsonStore.Load(path), state);
            Require(state.GetUsableSectors(legacy.Family) == expected, "roundtrip does not deduct again " + family);
            Throws<InvalidDataException>(() => ProjectJsonStore.Validate(legacy with
            {
                Geometry = captured with { UsableSectors = total + 1 },
            }));
        }
    }

    private static void TestCalibrationRoundtrip()
    {
        CalibrationParameters legacy = new(CalibrationDiscKind.Dvd, 24, 38, 714_546, 133.3);
        CalibrationParameters limited = legacy with { UsableSectors = 713_936 };
        CalibrationTarget target = CalibrationTarget.Create(limited, artwork: CalibrationArtwork.HalfDisc);
        Require(limited.TrackLengthMm == legacy.TrackLengthMm && legacy.DrawingOuterRadiusMm == 38,
            "output reserve never alters the fitted geometry or old frozen target");
        Require(target.DrawingOuterRadiusMm < 38 && target.DrawingOuterRadiusMm > 37.9,
            "target stops at the physical end of writable data");
        CalibrationSession restored = CalibrationSessionJson.Deserialize(
            CalibrationSessionJson.Serialize(new(target, [])));
        Require(restored.Target.Parameters.Sectors == 714_546
            && restored.Target.Parameters.UsableSectors == 713_936
            && restored.Target.DrawingOuterRadiusMm == target.DrawingOuterRadiusMm,
            "calibration persists both geometry and frozen output boundary");
        Require(CalibrationWideBandPattern.Supports(limited), "DVD bands align output, not geometry sectors");
        Throws<ArgumentException>(() => (limited with { UsableSectors = 713_937 }).Validate());
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Usable sector persistence: " + message);
    }
}
