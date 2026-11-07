using DvdImageSolver.Solver;

namespace DiscImageStudio.Dvd;

/// <summary>
/// Source-side preview of the dispersion tone mapping. The generated geometry doubles as the
/// measured geometry, so the picture shows what the writer will lay down without reading an
/// ISO first; the read-back simulation stays the authoritative check.
/// </summary>
public static class DvdTonePreview
{
    public static CalibrationPreviewSummary Render(
        string imagePath,
        string outputPath,
        DvdStreamingOptions options,
        int previewSize,
        int samplesPerSector,
        CancellationToken cancellationToken = default,
        uint drawingStartLba = 0,
        uint? drawingEndLbaExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        uint drawingEnd = drawingEndLbaExclusive ?? options.OutputSectors;
        if (drawingStartLba >= drawingEnd || drawingEnd > options.OutputSectors)
            throw new ArgumentOutOfRangeException(nameof(drawingEndLbaExclusive),
                "The drawing range must lie inside the disc.");
        CalibrationPreviewOptions preview = new(
            options.TotalSectors,
            drawingStartLba,
            drawingEnd - drawingStartLba,
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            options.ChannelBitLengthNm,
            Clockwise: true,
            options.LuminanceThreshold,
            options.AlphaThreshold,
            previewSize,
            samplesPerSector,
            options.PitchLinear,
            options.PitchQuadratic,
            options.PitchCubic,
            options.PitchLinear,
            options.PitchQuadratic,
            options.PitchCubic,
            options.Tone,
            SourceImageOuterRadiusMm: options.SamplingImageOuterRadiusMm,
            PreviewOuterRadiusMm: options.OutputOuterRadiusMm);
        return CalibrationPreviewRenderer.Render(
            imagePath,
            outputPath,
            preview,
            cancellationToken);
    }
}
