using System.Globalization;
using System.Text.Json;
using DiscImageStudio.BluRay.Encoding;
using DiscImageStudio.Core;

namespace DiscImageStudio.BluRay;

public sealed class BluRayDiscModule : IOpticalDiscModule
{
    private readonly Func<string, CancellationToken, BluRayGray8Raster>? _imageLoader;

    public BluRayDiscModule(Func<string, CancellationToken, BluRayGray8Raster>? imageLoader = null)
        => _imageLoader = imageLoader;

    private static readonly DiscOptionDefinition[] GeometryOptions =
    [
        new("preset", "BD-R preset", DiscOptionValueType.Choice, DefaultValue: "bd-r-sl-25gb",
            Choices: BluRayDiscPreset.BuiltIn.Select(preset => preset.Id).ToArray()),
        new("total-sectors", "Disc total sectors", DiscOptionValueType.Integer, DefaultValue: "12219392"),
        new("r0", "Inner radius", DiscOptionValueType.Decimal, DefaultValue: "24", Unit: "mm"),
        new("r1", "Outer radius", DiscOptionValueType.Decimal, DefaultValue: "58", Unit: "mm"),
        new("start-angle", "Start angle", DiscOptionValueType.Decimal, DefaultValue: "0", Unit: "degrees"),
    ];

    public DiscModuleDescriptor Descriptor { get; } = new(
        "bluray-data", "Blu-ray BD-R", OpticalDiscFamily.BluRay,
        "ISO/IEC 30190 single-layer BD-R image generation using 3T/6T run-count bias, payload probes and ECC diagnostics.",
        SupportsCancellation: true,
        Commands:
        [
            new("bd-info", "BD-R parameters", "Describe nominal BD-R geometry; optionally export JSON.",
                DiscModuleCapabilities.None,
                [.. GeometryOptions, new("output", "JSON output", DiscOptionValueType.OutputFile),
                    new("invert", "Swap 3T/6T assignment", DiscOptionValueType.Boolean, DefaultValue: "false")]),
            new("bd-probe", "BD-R payload probe", "Export raw user sectors for an explicit scrambled-byte probe (no filesystem).",
                DiscModuleCapabilities.DataImageGeneration,
                [.. GeometryOptions,
                    new("output", "Payload output", DiscOptionValueType.OutputFile, Required: true),
                    new("start-sector", "First sector", DiscOptionValueType.Integer, DefaultValue: "0"),
                    new("sectors", "Sector count", DiscOptionValueType.Integer, DefaultValue: "32"),
                    new("byte", "Scrambled byte", DiscOptionValueType.String, Required: true)]),
            new("bd-encode", "BD-R ECC diagnostic", "Encode exactly 65536 payload bytes into 76880 ECC bytes; not a burn image.",
                DiscModuleCapabilities.ErrorCorrectionEncoding,
                [new("input", "Payload cluster", DiscOptionValueType.InputFile, Required: true),
                    new("output", "ECC cluster", DiscOptionValueType.OutputFile, Required: true),
                    new("psn", "First physical sector", DiscOptionValueType.String, DefaultValue: "0x100000")]),
            new("bd-generate", "BD-R picture generation",
                "Draw the full user-data area; selecting a files directory adds a UDF 2.01 filesystem at the inner or outer ring.",
                DiscModuleCapabilities.DataImageGeneration,
                [.. GeometryOptions, new("input", "Source image", DiscOptionValueType.InputFile, Required: true),
                    new("output", "ISO output", DiscOptionValueType.OutputFile, Required: true),
                    new("data-dir", "Files directory", DiscOptionValueType.InputFolder),
                    new("file-position", "File placement", DiscOptionValueType.Choice, DefaultValue: "inner", Choices: ["inner", "outer"]),
                    new("volume-label", "ISO volume label", DiscOptionValueType.String, DefaultValue: "BD_IMAGE"),
                    new("invert", "Swap 3T/6T assignment", DiscOptionValueType.Boolean, DefaultValue: "false"),
                    new("tone-dither", "Dither strength 0..1", DiscOptionValueType.Decimal, DefaultValue: "0"),
                    new("tone-levels", "Tone levels (BD is binary)", DiscOptionValueType.Integer, DefaultValue: "2"),
                    new("tone-brightness", "Brightness offset -64..64", DiscOptionValueType.Decimal, DefaultValue: "0"),
                    new("tone-gamma", "sRGB linearization", DiscOptionValueType.Boolean, DefaultValue: "false")]),
        ]);

    public Task<DiscJobResult> ExecuteAsync(DiscJobRequest request, IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Execute(request, progress, cancellationToken), cancellationToken);
    }

    private DiscJobResult Execute(DiscJobRequest request, IProgress<DiscJobProgress>? progress, CancellationToken token)
    {
        DiscCommandDescriptor command = Descriptor.Commands.FirstOrDefault(c => c.Name == request.Command)
            ?? throw new ArgumentException($"Unknown Blu-ray command '{request.Command}'.");
        Dictionary<string, string> options = Parse(request.Arguments, command);
        if (request.RequestedOutputPath is not null && !options.ContainsKey("output"))
            options["output"] = request.RequestedOutputPath;
        foreach (DiscOptionDefinition option in command.Options.Where(o => o.Required))
            if (!options.ContainsKey(option.Name)) throw new ArgumentException($"Missing --{option.Name}.");
        string? output = options.GetValueOrDefault("output");
        if (output is not null) ArgumentException.ThrowIfNullOrWhiteSpace(output);

        if (command.Name == "bd-encode")
        {
            string input = options["input"];
            if (Path.GetFullPath(input).Equals(Path.GetFullPath(output!), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Input and output paths must be different.");
            uint psn = UInt(options.GetValueOrDefault("psn", "0x100000"), "psn");
            bool validPsn = psn >= BluRayDiscParameters.FirstLayer0PhysicalSector && psn <= 0x00CA73E0;
            if (!validPsn || psn % 32 != 0)
                throw new ArgumentOutOfRangeException("psn", "PSN must start a complete, aligned BD-R data-zone cluster.");
            byte[] payload = new byte[BluRayDiscParameters.PayloadBytesPerCluster];
            using (FileStream source = File.OpenRead(input))
            {
                if (source.Length != payload.Length)
                    throw new ArgumentException("bd-encode input must contain exactly 65536 bytes (32 user sectors).");
                source.ReadExactly(payload);
            }
            token.ThrowIfCancellationRequested();
            byte[] encoded = BluRayEccEncoder.Encode(payload, psn);
            BluRayOutputPublisher.Write(output!, stream => stream.Write(encoded), token);
            return new(Descriptor.Id, command.Name, 0,
                $"BD ECC diagnostic: {encoded.Length} bytes; no 17PP/RUB or filesystem in this file.", Path.GetFullPath(output!));
        }

        BluRayDiscPreset preset = BluRayDiscPreset.Find(options.GetValueOrDefault("preset", "bd-r-sl-25gb"));
        BluRayDiscParameters defaults = preset.CreateParameters();
        BluRayDiscParameters disc = new(options.TryGetValue("total-sectors", out string? totalSectors)
            ? UInt(totalSectors, "total-sectors") : preset.TotalSectors, preset.LayerCount,
            options.TryGetValue("r0", out string? inner) ? Number(inner, "r0") : defaults.InnerRadiusMm,
            options.TryGetValue("r1", out string? outer) ? Number(outer, "r1") : defaults.OuterRadiusMm,
            options.TryGetValue("start-angle", out string? angle) ? Number(angle, "start-angle") : defaults.StartAngleDegrees);
        disc.Validate();
        if (command.Name == "bd-info")
        {
            bool invert = options.TryGetValue("invert", out string? polarity) ? Boolean(polarity, "invert") : preset.InvertMapping;
            string json = JsonSerializer.Serialize(new
            {
                standard = "ISO/IEC 30190:2021", preset = preset.Id,
                mappingStatus = "enabled", parameters = disc,
                luminanceMapping = new
                {
                    strategy = "3T/6T run-count bias", threshold = 128,
                    dark = new { run = invert ? "6T" : "3T", scrambledByte = $"0x{(invert ? BluRayRunLengthMapping.SixTByte : BluRayRunLengthMapping.ThreeTByte):X2}" },
                    light = new { run = invert ? "3T" : "6T", scrambledByte = $"0x{(invert ? BluRayRunLengthMapping.ThreeTByte : BluRayRunLengthMapping.SixTByte):X2}" },
                    invert,
                    invertSupported = true,
                    note = "Run-count bias is not an optical-reflectance calibration.",
                },
                payloadBytesPerSector = 2048, sectorsPerCluster = 32,
                channelBitsPerRecordingUnit = BluRayDiscParameters.ChannelBitsPerRecordingUnit,
                channelBitLengthNm = BluRayDiscParameters.ChannelBitLengthNm,
                nominalTrackPitchMicrometers = BluRayDiscParameters.NominalTrackPitchMicrometers,
                effectiveTrackPitchMicrometers = new BluRayTrackGeometry(disc).EffectiveTrackPitchMicrometers,
                addressModel = "Nominal data zones; drive spare areas and defect remapping require calibration.",
                physicalLayerStarts = Enumerable.Range(0, disc.LayerCount)
                    .Select(layer => disc.GetPhysicalSectorNumber((uint)layer * BluRayDiscParameters.SectorsPerLayer)).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true });
            if (output is not null)
                BluRayOutputPublisher.Write(output, stream => JsonSerializer.Serialize(stream,
                    JsonSerializer.Deserialize<JsonElement>(json), new JsonSerializerOptions { WriteIndented = true }), token);
            return new(Descriptor.Id, command.Name, 0, json, output is null ? null : Path.GetFullPath(output));
        }

        bool isPicture = command.Name == "bd-generate";
        uint start = isPicture ? 0 : UInt(options.GetValueOrDefault("start-sector", "0"), "start-sector");
        if (start >= disc.TotalSectors)
            throw new ArgumentOutOfRangeException("start-sector", "First sector must be inside the disc.");
        uint count = options.TryGetValue("sectors", out string? sectorCount)
            ? UInt(sectorCount, "sectors") : isPicture ? disc.TotalSectors : 32;
        BluRayStreamingOptions streaming = new(disc, start, count);
        streaming.Validate();
        IBluRayPayloadMapping mapping;
        string interpretation;
        BluRayIsoPlan? isoPlan = null;
        bool generationInvert = false;
        ToneProfile tone = ToneProfile.LegacyBinary;
        if (isPicture)
        {
            string input = options["input"];
            if (Path.GetFullPath(input).Equals(Path.GetFullPath(output!), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Input and output paths must be different.");
            generationInvert = options.TryGetValue("invert", out string? polarity)
                ? Boolean(polarity, "invert") : preset.InvertMapping;
            bool filesAtOuterRing = options.GetValueOrDefault("file-position", "inner") switch
            {
                "inner" => false,
                "outer" => true,
                _ => throw new ArgumentException("--file-position must be inner or outer."),
            };
            isoPlan = BluRayIsoGenerator.PrepareDrawing(disc, options.GetValueOrDefault("data-dir"),
                options.GetValueOrDefault("volume-label", "BD_IMAGE"), filesAtOuterRing: filesAtOuterRing);
            BluRayIsoGenerator.ValidateOutputPath(output!, isoPlan);
            token.ThrowIfCancellationRequested();
            if (_imageLoader is null)
                return new(Descriptor.Id, command.Name, 2,
                    "BD picture generation requires an image loader. Run bd-generate through the application or provide a BluRayGray8Raster loader.");
            BluRayGray8Raster raster = _imageLoader(input, token);
            token.ThrowIfCancellationRequested();
            int levels = options.TryGetValue("tone-levels", out string? toneLevels)
                ? (int)UInt(toneLevels, "tone-levels") : ToneProfile.TwoLevels;
            tone = new ToneProfile(
                options.TryGetValue("tone-dither", out string? dither) ? Number(dither, "tone-dither") : 0,
                levels,
                options.TryGetValue("tone-brightness", out string? brightness) ? Number(brightness, "tone-brightness") : 0,
                options.TryGetValue("tone-gamma", out string? gamma) && Boolean(gamma, "tone-gamma"));
            tone.Validate("tone");
            if (levels != ToneProfile.TwoLevels)
                throw new ArgumentException("bd-generate accepts only --tone-levels 2.");
            raster = BluRayToneMapping.Apply(raster, tone, token);
            token.ThrowIfCancellationRequested();
            mapping = new BluRayImagePayloadMapping(disc, raster, new BluRayRunLengthMapping(generationInvert));
            interpretation = generationInvert
                ? "dark-to-6T / light-to-3T run-count bias"
                : "dark-to-3T / light-to-6T run-count bias";
        }
        else
        {
            uint value = UInt(options["byte"], "byte");
            if (value > 255) throw new ArgumentOutOfRangeException("byte", "Probe byte must be 0..255 or 0x00..0xFF.");
            mapping = new BluRayConstantByteMapping((byte)value);
            interpretation = $"scrambled byte 0x{value:X2}; no black/white interpretation";
        }
        TimeSpan nextProgressReport = TimeSpan.Zero;
        InlineProgress<BluRayStreamingProgress> report = new(p =>
            {
                if (p.Elapsed < nextProgressReport && p.CompletedBytes != p.TotalBytes) return;
                nextProgressReport = p.Elapsed + TimeSpan.FromMilliseconds(250);
                progress?.Report(new(Descriptor.Id, isPicture ? "iso" : "payload", $"BD {p.CompletedBytes}/{p.TotalBytes} bytes",
                    p.Fraction, p.CompletedBytes, p.TotalBytes, p.Elapsed));
            });
        BluRayStreamingSummary summary = isPicture
            ? BluRayIsoGenerator.GenerateFile(output!, isoPlan!, mapping, report, token)
            : BluRayStreamingGenerator.GenerateFile(output!, streaming, mapping, report, token);
        if (isPicture)
        {
            BluRayIsoMetadata.FromPlan(
                    isoPlan!, options["input"], output!, generationInvert, tone, DateTimeOffset.Now)
                .Save(output!);
            token.ThrowIfCancellationRequested();
        }

        return new(Descriptor.Id, command.Name, 0,
            isPicture
                ? $"BD {(isoPlan!.HasFileSystem ? "UDF 2.01" : "full-area drawing, no filesystem")}: {summary.BytesWritten} bytes, {disc.TotalSectors} sectors; {isoPlan.FileCount} {(isoPlan.FilesAtOuterRing ? "outer" : "inner")} files; drawing sectors {isoPlan.DrawingStartSector}..{isoPlan.DrawingEndSectorExclusive - 1}; {interpretation}."
                : $"BD probe: {summary.BytesWritten} raw payload bytes, sectors {start}..{start + count - 1}; {interpretation}. No filesystem.",
            Path.GetFullPath(output!));
    }

    private static Dictionary<string, string> Parse(IReadOnlyList<string> args, DiscCommandDescriptor command)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowed = new(command.Options.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Expected a named option, got '{argument}'.");
            int separator = argument.IndexOf('=');
            string name = separator < 0 ? argument[2..] : argument[2..separator];
            if (command.Name == "bd-generate" && (name.Equals("sectors", StringComparison.OrdinalIgnoreCase)
                || name.Equals("start-sector", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("bd-generate now outputs a complete ISO. Use --total-sectors for disc capacity; raw ranges remain available in bd-probe.");
            if (!allowed.Contains(name)) throw new ArgumentException($"Unknown option --{name} for {command.Name}.");
            string value;
            if (separator >= 0) value = argument[(separator + 1)..];
            else if (++i < args.Count && !args[i].StartsWith("--", StringComparison.Ordinal)) value = args[i];
            else throw new ArgumentException($"Missing value for --{name}.");
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Empty value for --{name}.");
            if (!result.TryAdd(name, value)) throw new ArgumentException($"Duplicate option --{name}.");
        }
        return result;
    }

    private static uint UInt(string value, string name)
    {
        bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out uint result))
            throw new ArgumentException($"--{name} must be an unsigned integer (decimal or 0x hex).");
        return result;
    }

    private static double Number(string value, string name)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)
            ? result : throw new ArgumentException($"--{name} must be a finite number.");

    private static bool Boolean(string value, string name)
        => bool.TryParse(value, out bool result)
            ? result : throw new ArgumentException($"--{name} must be true or false.");

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
