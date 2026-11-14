using DiscImageStudio.Core;
using DiscImageStudio.Iso;

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
    bool FilesAtOuterRing = false,
    string? DataImagePath = null);

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
        BluRayStreamingSummary? generatedSummary = null;
        if (plan.SourceImagePath is { } sourceImage)
        {
            BluRayOutputPublisher.Write(request.OutputPath, stream =>
            {
                using RawIsoPrefixOverlayStream overlay = RawIsoImage.OverlayPrefix(
                    stream, sourceImage, plan.TotalSectors);
                generatedSummary = BluRayIsoGenerator.GenerateToStream(overlay, plan, mapping, report, cancellationToken);
                overlay.Complete();
            }, cancellationToken);
        }
        else
        {
            generatedSummary = BluRayIsoGenerator.GenerateFile(request.OutputPath,
                plan, mapping, report, cancellationToken);
        }
        BluRayStreamingSummary summary = generatedSummary
            ?? throw new InvalidOperationException("Blu-ray image generation completed without a summary.");
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
        string? dataDirectory = request.DataDirectory?.Trim();
        string? dataImagePath = request.DataImagePath?.Trim();
        if (!string.IsNullOrEmpty(dataDirectory) && !string.IsNullOrEmpty(dataImagePath))
            throw new ArgumentException("Choose either a data folder or an image source, not both.", nameof(request));
        BluRayIsoPlan plan = !string.IsNullOrEmpty(dataImagePath)
            ? BluRayIsoGenerator.PrepareRawImage(request.Parameters, dataImagePath)
            : BluRayIsoGenerator.PrepareDrawing(request.Parameters, dataDirectory,
                request.VolumeLabel, filesAtOuterRing: request.FilesAtOuterRing);
        BluRayIsoGenerator.ValidateOutputPath(request.OutputPath, plan);
        return plan;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
