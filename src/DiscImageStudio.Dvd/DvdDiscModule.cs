using DiscImageStudio.Core;
using DvdImageSolver;

namespace DiscImageStudio.Dvd;

public sealed class DvdDiscModule : IOpticalDiscModule
{
    public const string ModuleId = "dvd-data";

    private static readonly DiscModuleDescriptor ModuleDescriptor = new(
        ModuleId,
        "DVD data disc",
        OpticalDiscFamily.Dvd,
        "DVD fast-dispersion ISO writer and clockwise geometry calibration adapter.",
        SupportsCancellation: false,
        Commands:
        [
            new DiscCommandDescriptor(
                "solve",
                "Generate DVD image",
                "Generates a DVD image with the fixed fast dispersion mapping in CW direction.",
                DiscModuleCapabilities.DataImageGeneration
                    | DiscModuleCapabilities.HybridFilesystem
                    | DiscModuleCapabilities.ErrorCorrectionEncoding,
                [
                    new("image", "Source image", DiscOptionValueType.InputFile),
                    new("iso-output", "Output ISO", DiscOptionValueType.OutputFile),
                    new("data-dir", "Hybrid data folder", DiscOptionValueType.InputFolder),
                    new("file-position", "File placement", DiscOptionValueType.Choice, DefaultValue: "inner", Choices: ["inner", "outer"]),
                ]),
            new DiscCommandDescriptor(
                "encode",
                "Encode DVD payload",
                "Encodes one DVD ECC block into packed NRZI channel levels.",
                DiscModuleCapabilities.ErrorCorrectionEncoding,
                [
                    new("input", "Payload", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Channel output", DiscOptionValueType.OutputFile, Required: true),
                ]),
            new DiscCommandDescriptor(
                "calibrate",
                "Preview DVD geometry",
                "Renders generated geometry through measured disc radii.",
                DiscModuleCapabilities.GeometryPreview,
                [
                    new("image", "Source image", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Preview PNG", DiscOptionValueType.OutputFile, Required: true),
                ]),
            new DiscCommandDescriptor(
                "simulate",
                "Simulate DVD read-back",
                "Renders a generated ISO's payload sectors under measured disc radii.",
                DiscModuleCapabilities.EncodedOutputPreview,
                [
                    new("iso", "Generated ISO image", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Preview PNG", DiscOptionValueType.OutputFile, Required: true),
                ]),
            new DiscCommandDescriptor(
                "selftest",
                "Run DVD engine self-tests",
                "Runs the DVD engine's deterministic regression suite.",
                DiscModuleCapabilities.None,
                []),
        ]);

    public DiscModuleDescriptor Descriptor => ModuleDescriptor;

    public Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ModuleDescriptor.Commands.Any(
            command => command.Name.Equals(request.Command, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"DVD module does not support command '{request.Command}'.",
                nameof(request));
        }

        progress?.Report(new DiscJobProgress(
            ModuleId,
            "execute",
            $"Running DVD command {request.Command}…"));
        IReadOnlyList<string> normalizedArguments = NormalizeArguments(
            request.Command,
            request.Arguments);
        string[] commandLine = [request.Command, .. normalizedArguments];
        int exitCode = DvdCommandRunner.Run(commandLine);
        string? outputPath = FindOutputPath(request.Arguments);
        string summary = exitCode == 0
            ? $"DVD command {request.Command} completed."
            : $"DVD command {request.Command} failed with exit code {exitCode}.";
        return Task.FromResult(new DiscJobResult(
            ModuleId,
            request.Command,
            exitCode,
            summary,
            outputPath));
    }

    private static string? FindOutputPath(IReadOnlyList<string> arguments)
    {
        string? output = FindOption(arguments, "iso-output") ?? FindOption(arguments, "output");
        return string.IsNullOrWhiteSpace(output) ? null : Path.GetFullPath(output);
    }

    private static IReadOnlyList<string> NormalizeArguments(
        string command,
        IReadOnlyList<string> arguments)
    {
        (string Key, string Value)[] fixedOptions = command.ToLowerInvariant() switch
        {
            "solve" =>
            [
                ("algorithm", "dispersion"),
                ("iterations-per-block", "1"),
                ("fast-output", "true"),
                ("spiral-direction", "cw"),
            ],
            "calibrate" => [("spiral-direction", "cw")],
            "simulate" => [("spiral-direction", "cw")],
            _ => [],
        };
        if (fixedOptions.Length == 0)
        {
            return arguments;
        }

        HashSet<string> fixedKeys = new(
            fixedOptions.Select(option => option.Key),
            StringComparer.OrdinalIgnoreCase);
        List<string> normalized = [];
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!TryReadOptionName(argument, out string optionName)
                || !fixedKeys.Contains(optionName))
            {
                normalized.Add(argument);
                continue;
            }

            if (!argument.Contains('=', StringComparison.Ordinal)
                && index + 1 < arguments.Count
                && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                index++;
            }
        }

        foreach ((string key, string value) in fixedOptions)
        {
            normalized.Add("--" + key);
            normalized.Add(value);
        }

        return normalized;
    }

    private static bool TryReadOptionName(string argument, out string optionName)
    {
        optionName = string.Empty;
        if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
        {
            return false;
        }

        int equalsIndex = argument.IndexOf('=');
        optionName = equalsIndex < 0 ? argument[2..] : argument[2..equalsIndex];
        return optionName.Length > 0;
    }

    private static string? FindOption(IReadOnlyList<string> arguments, string key)
    {
        string prefix = "--" + key + "=";
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }

            if (argument.Equals("--" + key, StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Count)
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
