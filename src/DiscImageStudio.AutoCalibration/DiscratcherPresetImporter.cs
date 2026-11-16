using System.Text.Json;

namespace DiscImageStudio.AutoCalibration;

public sealed record ImportedDiscratcherPreset(
    string MediaType,
    string DisplayName,
    AutoCalibrationResult Geometry,
    double ChannelBitLengthNm,
    double? AlongTrackFrames);

/// <summary>Reads Discratcher's saved preset array, a single exported preset, or calibration
/// history entries that contain a preset. Only geometry supported by DiscImageStudio is mapped.</summary>
public static class DiscratcherPresetImporter
{
    public static IReadOnlyList<ImportedDiscratcherPreset> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        IEnumerable<JsonElement> entries = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToArray(),
            JsonValueKind.Object => [root],
            _ => throw new JsonException("Discratcher 文件必须是 JSON 对象或数组。"),
        };

        var presets = new List<ImportedDiscratcherPreset>();
        foreach (JsonElement entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Discratcher 预设数组中包含非对象记录。");
            }

            JsonElement preset = entry;
            if (entry.TryGetProperty("preset", out JsonElement nested))
            {
                if (nested.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (nested.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Discratcher 标定记录中的 preset 必须是对象。");
                }

                preset = nested;
            }

            presets.Add(ParsePreset(preset));
        }

        if (presets.Count == 0)
        {
            throw new JsonException("文件中没有可导入的 Discratcher 几何预设。");
        }

        return presets;
    }

    private static ImportedDiscratcherPreset ParsePreset(JsonElement preset)
    {
        string mediaType = OptionalString(preset, "media")?.ToLowerInvariant() switch
        {
            null or "" or "dvd" => "dvd",
            "cd" => "cd",
            string value => throw new JsonException($"不支持导入 Discratcher 的「{value}」盘型。"),
        };
        uint sectors = RequiredUInt32(preset, "totalSectors");
        double innerRadius = RequiredDouble(preset, "innerRadiusMm");
        double outerRadius = RequiredDouble(preset, "outerRadiusMm");
        double channelBitLengthNm = RequiredDouble(preset, "channelBitLengthNm");
        double pitchLinear = RequiredDouble(preset, "pitchLinear");
        double pitchQuadratic = RequiredDouble(preset, "pitchQuadratic");
        double pitchCubic = RequiredDouble(preset, "pitchCubic");
        double rms = OptionalDouble(preset, "rmsResidualDeg") ?? 0;
        double? alongTrackFrames = OptionalDouble(preset, "alongTrackFrames");

        if (sectors == 0 || !double.IsFinite(channelBitLengthNm) || channelBitLengthNm <= 0
            || !double.IsFinite(rms) || rms < 0
            || (alongTrackFrames is { } frames && (!double.IsFinite(frames) || frames < 0)))
        {
            throw new JsonException("Discratcher 预设包含无效的扇区数、位长或残差。");
        }

        var geometry = new AutoCalibrationResult(
            mediaType,
            innerRadius,
            outerRadius,
            sectors,
            pitchLinear,
            pitchQuadratic,
            pitchCubic,
            rms);
        _ = AutoCalibrationGeometry.ToParameters(geometry);

        string label = string.Join(" · ", new[]
        {
            OptionalString(preset, "stamperCode"),
            OptionalString(preset, "brand"),
            OptionalString(preset, "id"),
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (label.Length == 0)
        {
            label = $"{mediaType.ToUpperInvariant()} · {sectors:N0} 扇区";
        }

        return new ImportedDiscratcherPreset(
            mediaType, label, geometry, channelBitLengthNm, alongTrackFrames);
    }

    private static uint RequiredUInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetUInt32(out uint parsed))
        {
            throw new JsonException($"Discratcher 预设缺少有效的 {name}。");
        }

        return parsed;
    }

    private static double RequiredDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double parsed)
            || !double.IsFinite(parsed))
        {
            throw new JsonException($"Discratcher 预设缺少有效的 {name}。");
        }

        return parsed;
    }

    private static double? OptionalDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double parsed)
            || !double.IsFinite(parsed))
        {
            throw new JsonException($"Discratcher 预设中的 {name} 无效。");
        }

        return parsed;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Discratcher 预设中的 {name} 必须是字符串。");
        }

        return value.GetString();
    }
}
