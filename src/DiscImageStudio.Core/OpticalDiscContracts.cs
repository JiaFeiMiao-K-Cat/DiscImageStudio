namespace DiscImageStudio.Core;

public enum OpticalDiscFamily
{
    CompactDisc,
    Dvd,
    BluRay,
}

[Flags]
public enum DiscModuleCapabilities
{
    None = 0,
    RawTrackGeneration = 1 << 0,
    DataImageGeneration = 1 << 1,
    GeometryPreview = 1 << 2,
    EncodedOutputPreview = 1 << 3,
    HybridFilesystem = 1 << 4,
    ErrorCorrectionEncoding = 1 << 5,
}

public enum DiscOptionValueType
{
    String,
    InputFile,
    OutputFile,
    InputFolder,
    Integer,
    Decimal,
    Boolean,
    Choice,
}

public sealed record DiscOptionDefinition(
    string Name,
    string DisplayName,
    DiscOptionValueType ValueType,
    bool Required = false,
    string? DefaultValue = null,
    string? Unit = null,
    IReadOnlyList<string>? Choices = null);

public sealed record DiscCommandDescriptor(
    string Name,
    string DisplayName,
    string Description,
    DiscModuleCapabilities Capabilities,
    IReadOnlyList<DiscOptionDefinition> Options);

public sealed record DiscModuleDescriptor(
    string Id,
    string DisplayName,
    OpticalDiscFamily Family,
    string Description,
    bool SupportsCancellation,
    IReadOnlyList<DiscCommandDescriptor> Commands);

public sealed class DiscJobRequest
{
    public DiscJobRequest(
        string command,
        IEnumerable<string> arguments,
        string? requestedOutputPath = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A disc job command is required.", nameof(command));
        }

        Command = command.Trim().ToLowerInvariant();
        Arguments = arguments?.ToArray()
            ?? throw new ArgumentNullException(nameof(arguments));
        RequestedOutputPath = requestedOutputPath;
    }

    public string Command { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? RequestedOutputPath { get; }
}

public sealed record DiscJobProgress(
    string ModuleId,
    string Stage,
    string Message,
    double? Fraction = null,
    long? CompletedUnits = null,
    long? TotalUnits = null,
    TimeSpan? Elapsed = null);

public sealed record DiscJobResult(
    string ModuleId,
    string Command,
    int ExitCode,
    string? Summary = null,
    string? OutputPath = null)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>A completed file-generation operation. Failures propagate as exceptions;
/// command-line exit codes belong to the CLI adapter, not the engine API.</summary>
public sealed record DiscImageResult(string OutputPath, string? Summary = null);

public interface IOpticalDiscModule
{
    DiscModuleDescriptor Descriptor { get; }

    Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
