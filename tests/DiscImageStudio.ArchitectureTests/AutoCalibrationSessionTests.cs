using System.IO;
using DiscImageStudio.AutoCalibration;
using DiscImageStudio.Core.Calibration;

internal static class AutoCalibrationSessionTests
{
    internal static void Run()
    {
        var result = new AutoCalibrationResult(
            "dvd", 24.1, 58.1, 2_297_888, -0.000039, 0.000070, -0.000047, 0.08);
        IReadOnlyList<DiscImageStudio.Core.Calibration.CalibrationSession> sessions =
            CalibrationSessionExporter.CreateSessions(result);
        Require(sessions.Count == 3 && sessions.All(session => session.Photo is null),
            "the three stage records contain geometry and no photos");
        CalibrationParameters parameters = AutoCalibrationGeometry.ToParameters(result);
        Require(parameters.Kind == DiscImageStudio.Core.Calibration.CalibrationDiscKind.Dvd
            && parameters.Sectors == result.Sectors
            && parameters.LinearDensity == 133.3
            && parameters.PitchLinear == result.PitchLinear,
            "measured geometry maps onto the project calibration parameters");

        string directory = Directory.CreateTempSubdirectory("disc-auto-calibration-").FullName;
        try
        {
            IReadOnlyList<string> files = CalibrationSessionExporter.Export(result, "测试工程", directory);
            Require(files.SequenceEqual(new[]
            {
                "测试工程-粗标定-auto.calibration.json",
                "测试工程-边界标定-auto.calibration.json",
                "测试工程-精细标定-auto.calibration.json",
            }), "export filenames use the project and matching calibration stage");
            string[] contents = files.Select(file => File.ReadAllText(Path.Combine(directory, file))).ToArray();

            try
            {
                CalibrationSessionExporter.Export(result, "测试工程", directory);
                throw new InvalidOperationException("Auto-calibration export overwrote existing files.");
            }
            catch (IOException)
            {
                Require(files.Select(file => File.ReadAllText(Path.Combine(directory, file)))
                    .SequenceEqual(contents), "a repeated export does not alter existing records");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        string presetPath = Path.Combine(Path.GetTempPath(), $"discratcher-presets-{Guid.NewGuid():N}.json");
        string historyPath = Path.Combine(Path.GetTempPath(), $"discratcher-history-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(presetPath,
                """
                [{
                  "id":"dvd-1","media":"dvd","brand":"Test",
                  "totalSectors":2295104,"channelBitLengthNm":133.3,
                  "innerRadiusMm":24.1,"outerRadiusMm":58.1,
                  "pitchLinear":-0.000039,"pitchQuadratic":0.000070,
                  "pitchCubic":-0.000047,"rmsResidualDeg":0.08
                }]
                """);
            IReadOnlyList<ImportedDiscratcherPreset> presets = DiscratcherPresetImporter.Read(presetPath);
            Require(presets.Count == 1
                && presets[0].MediaType == "dvd"
                && presets[0].Geometry.Sectors == 2_295_104
                && presets[0].ChannelBitLengthNm == 133.3,
                "saved Discratcher preset arrays import their geometry");

            File.WriteAllText(historyPath,
                """
                [
                  {"status":"failed","preset":null},
                  {"status":"complete","preset":{
                    "id":"cd-1","media":"cd","brand":"Test CD",
                    "totalSectors":359779,"channelBitLengthNm":277.7,
                    "innerRadiusMm":24.84,"outerRadiusMm":58.0,
                    "pitchLinear":0.0015,"pitchQuadratic":0.0,
                    "pitchCubic":0.0,"rmsResidualDeg":0.1,
                    "alongTrackFrames":2.0
                  }}
                ]
                """);
            IReadOnlyList<ImportedDiscratcherPreset> history = DiscratcherPresetImporter.Read(historyPath);
            Require(history.Count == 1
                && history[0].MediaType == "cd"
                && history[0].AlongTrackFrames == 2.0,
                "calibration history imports nested presets and skips entries without geometry");
        }
        finally
        {
            File.Delete(presetPath);
            File.Delete(historyPath);
        }

        Require(CalibrationImageSizing.ResolveForExport(
                "cd", useManualParameters: true, manualDiscSectors: 359_779, detectedBlankDiscSectors: null)
                == 359_715,
            "manual CD capacity applies Discratcher's lead-out margin");
        Require(CalibrationImageSizing.ResolveForExport(
                "cd", useManualParameters: false, manualDiscSectors: null, detectedBlankDiscSectors: 359_849)
                == 359_785,
            "a detected blank CD uses its reported capacity");
        Require(CalibrationImageSizing.ResolveForExport(
                "dvd", useManualParameters: true, manualDiscSectors: null, detectedBlankDiscSectors: null)
                == CalibrationImageSizing.DefaultDvdImageSectors,
            "manual DVD mode uses the standard single-layer image size");
        Require(DiscratcherMeasurementOptions.IsValid(36, 32, 0.02, 0.96)
            && DiscratcherMeasurementOptions.IsValid(16, 16, 0.01, 0.80)
            && DiscratcherMeasurementOptions.IsValid(72, 60, 0.10, 0.98),
            "measurement options accept Discratcher's defaults and endpoints");
        Require(!DiscratcherMeasurementOptions.IsValid(15, 16, 0.02, 0.96)
            && !DiscratcherMeasurementOptions.IsValid(36, 62, 0.02, 0.96)
            && !DiscratcherMeasurementOptions.IsValid(36, 32, 0.005, 0.96)
            && !DiscratcherMeasurementOptions.IsValid(36, 32, 0.02, 0.99),
            "measurement options reject values outside Discratcher's UI ranges");
        try
        {
            CalibrationImageSizing.ResolveForExport(
                "cd", useManualParameters: false, manualDiscSectors: null, detectedBlankDiscSectors: null);
            throw new InvalidOperationException("Image sizing accepted missing blank-disc parameters.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("未读取到空白盘参数"))
        {
        }

        Console.WriteLine("auto-calibration: importer, image sizing and stage exports passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Auto-calibration sessions: {message}.");
        }
    }
}
