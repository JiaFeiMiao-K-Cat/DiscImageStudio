using DiscImageStudio.Core;

namespace DiscImageStudio.BluRay;

/// <summary>A full-area BD drawing, optionally including an inner or outer UDF filesystem.</summary>
public sealed record BluRayImageRequest(
    string SourceImage,
    string OutputPath,
    BluRayDiscParameters Parameters,
    ToneProfile Tone,
    bool InvertMapping = false,
    string? DataDirectory = null,
    string VolumeLabel = "BD_IMAGE",
    bool FilesAtOuterRing = false);

public sealed class BluRayImageGenerator
{
    private readonly Func<string, CancellationToken, BluRayGray8Raster> _imageLoader;

    public BluRayImageGenerator(Func<string, CancellationToken, BluRayGray8Raster> imageLoader)
    {
        ArgumentNullException.ThrowIfNull(imageLoader);
        _imageLoader = imageLoader;
    }

    /// <summary>Writes the image and recovery sidecar, preserving exceptions for the host.</summary>
    public DiscImageResult Generate(BluRayImageRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BluRayIsoPlan plan = Prepare(request);
        cancellationToken.ThrowIfCancellationRequested();
        BluRayGray8Raster raster = _imageLoader(request.SourceImage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        raster = BluRayToneMapping.Apply(raster, request.Tone, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        BluRayImagePayloadMapping mapping = new(request.Parameters, raster,
            new BluRayRunLengthMapping(request.InvertMapping));
        TimeSpan nextProgressReport = TimeSpan.Zero;
        InlineProgress<BluRayStreamingProgress> report = new(value =>
        {
            if (value.Elapsed < nextProgressReport && value.CompletedBytes != value.TotalBytes) return;
            nextProgressReport = value.Elapsed + TimeSpan.FromMilliseconds(250);
            progress?.Report(new DiscJobProgress(BluRayDiscModule.ModuleId, "iso",
                $"BD {value.CompletedBytes}/{value.TotalBytes} bytes", value.Fraction,
                value.CompletedBytes, value.TotalBytes, value.Elapsed));
        });
        BluRayStreamingSummary summary = BluRayIsoGenerator.GenerateFile(request.OutputPath,
            plan, mapping, report, cancellationToken);
        BluRayIsoMetadata.FromPlan(plan, request.SourceImage, request.OutputPath,
            request.InvertMapping, request.Tone, DateTimeOffset.Now).Save(request.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();
        string interpretation = request.InvertMapping
            ? "dark-to-6T / light-to-3T run-count bias"
            : "dark-to-3T / light-to-6T run-count bias";
        return new DiscImageResult(Path.GetFullPath(request.OutputPath),
            $"BD {(plan.HasFileSystem ? "UDF 2.01" : "full-area drawing, no filesystem")}: {summary.BytesWritten} bytes, {request.Parameters.OutputSectors} sectors; {plan.FileCount} {(plan.FilesAtOuterRing ? "outer" : "inner")} files; drawing sectors {plan.DrawingStartSector}..{plan.DrawingEndSectorExclusive - 1}; {interpretation}.");
    }

    internal static BluRayIsoPlan Prepare(BluRayImageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        if (Path.GetFullPath(request.SourceImage).Equals(Path.GetFullPath(request.OutputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output paths must be different.");
        request.Parameters.Validate();
        request.Tone.Validate("tone");
        if (request.Tone.Levels != ToneProfile.TwoLevels)
            throw new ArgumentException("BD image generation accepts only two tone levels.", nameof(request));
        BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(request.Parameters, request.DataDirectory,
            request.VolumeLabel, filesAtOuterRing: request.FilesAtOuterRing);
        BluRayIsoGenerator.ValidateOutputPath(request.OutputPath, plan);
        return plan;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
