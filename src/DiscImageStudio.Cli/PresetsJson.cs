using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cli;

/// <summary>
/// Prints the built-in + user disc preset catalog that the GUI home page uses as templates.
/// The merge/disable rules stay in <see cref="DiscPresetJsonStore"/>; this command only
/// projects the merged schema 4 document to stdout.
/// </summary>
internal static class PresetsCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static int Run(IReadOnlyList<string> arguments)
    {
        bool json = false;
        string? requestedPath = null;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument.Equals("--json", StringComparison.Ordinal))
            {
                if (json)
                {
                    throw new ArgumentException("--json was specified more than once.");
                }

                json = true;
                continue;
            }

            if (argument.Equals("--path", StringComparison.Ordinal))
            {
                if (requestedPath is not null)
                {
                    throw new ArgumentException("--path was specified more than once.");
                }

                if (++index >= arguments.Count)
                {
                    throw new ArgumentException("Missing value for --path.");
                }

                requestedPath = arguments[index];
                continue;
            }

            if (argument.StartsWith("--path=", StringComparison.Ordinal))
            {
                if (requestedPath is not null)
                {
                    throw new ArgumentException("--path was specified more than once.");
                }

                requestedPath = argument["--path=".Length..];
                continue;
            }

            throw new ArgumentException(
                $"presets does not accept '{argument}'; supported options are --json and --path FILE.");
        }

        string userPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(requestedPath)
                ? DiscPresetJsonStore.GetDefaultPath()
                : requestedPath);
        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
        bool userFilePresent = File.Exists(userPath);
        DiscPresetJsonDocument user = userFilePresent
            ? DiscPresetJsonStore.Load(userPath)
            : EmptyUserDocument();
        DiscPresetJsonDocument merged = DiscPresetJsonStore.Merge(builtIn, user);
        DiscPresetJsonDocument document = new(
            DiscPresetJsonStore.CurrentSchemaVersion,
            merged.CdPresets,
            merged.DvdPresets,
            DisabledCdPresetIds: user.DisabledCdPresetIds ?? [],
            DisabledDvdPresetIds: user.DisabledDvdPresetIds ?? [],
            FallbackLanguage: merged.FallbackLanguage,
            BluRayPresets: merged.BluRayPresets ?? [],
            DisabledBluRayPresetIds: user.DisabledBluRayPresetIds ?? []);
        if (json)
        {
            WriteJson(document);
        }
        else
        {
            WriteSummary(document, userPath, userFilePresent);
        }

        return 0;
    }

    private static DiscPresetJsonDocument EmptyUserDocument()
        => new(
            DiscPresetJsonStore.CurrentSchemaVersion,
            [],
            [],
            DisabledCdPresetIds: [],
            DisabledDvdPresetIds: [],
            BluRayPresets: [],
            DisabledBluRayPresetIds: []);

    private static void WriteJson(DiscPresetJsonDocument document)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        using Stream standardOutput = Console.OpenStandardOutput();
        standardOutput.Write(payload, 0, payload.Length);
        standardOutput.WriteByte((byte)'\n');
        standardOutput.Flush();
    }

    private static void WriteSummary(
        DiscPresetJsonDocument document, string userPath, bool userFilePresent)
    {
        Console.WriteLine(
            $"Disc Image Studio disc presets (schema {document.SchemaVersion}, "
            + $"{document.CdPresets.Count} CD, {document.DvdPresets.Count} DVD, "
            + $"{(document.BluRayPresets?.Count ?? 0)} Blu-ray)");
        Console.WriteLine(userFilePresent
            ? $"source: built-in + {userPath}"
            : $"source: built-in (no user file at {userPath})");
        WriteSection("CD", document.CdPresets.Select(preset => new PresetLine(
            preset.Id,
            preset.DisplayName,
            preset.Description,
            $"sectors={preset.Sectors.ToString(CultureInfo.InvariantCulture)}, "
            + $"r0={Format(preset.InnerRadiusMm)} mm, r1={Format(preset.OuterRadiusMm)} mm")));
        WriteSection("DVD", document.DvdPresets.Select(preset => new PresetLine(
            preset.Id,
            preset.DisplayName,
            preset.Description,
            $"totalSectors={preset.TotalSectors.ToString(CultureInfo.InvariantCulture)}, "
            + $"r0={Format(preset.InnerRadiusMm)} mm, r1={Format(preset.OuterRadiusMm)} mm, "
            + $"pitch={Format(preset.PitchLinear)}/{Format(preset.PitchQuadratic)}/{Format(preset.PitchCubic)}")));
        WriteSection("Blu-ray", (document.BluRayPresets ?? []).Select(preset => new PresetLine(
            preset.Id,
            preset.DisplayName,
            preset.Description,
            $"totalSectors={preset.TotalSectors.ToString(CultureInfo.InvariantCulture)}, "
            + $"r0={Format(preset.InnerRadiusMm)} mm, r1={Format(preset.OuterRadiusMm)} mm, "
            + $"invert={(preset.InvertMapping ? "true" : "false")}")));
    }

    private static void WriteSection(string title, IEnumerable<PresetLine> presets)
    {
        List<PresetLine> lines = presets.ToList();
        Console.WriteLine();
        Console.WriteLine($"{title} presets ({lines.Count})");
        foreach (PresetLine preset in lines)
        {
            Console.WriteLine($"  {preset.Id} - {preset.DisplayName}");
            Console.WriteLine($"    {preset.Description}");
            Console.WriteLine($"    {preset.Geometry}");
        }
    }

    private static string Format(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record PresetLine(string Id, string DisplayName, string Description, string Geometry);
}
