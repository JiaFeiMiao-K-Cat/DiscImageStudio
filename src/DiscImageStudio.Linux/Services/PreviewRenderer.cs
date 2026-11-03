using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DvdImageSolver;
using DvdImageSolver.Solver;

namespace DiscImageStudio.Linux.Services;

/// <summary>A radial band of the disc occupied by the filesystem rather than the picture.</summary>
public readonly record struct RadialBand(double InnerRadiusMm, double OuterRadiusMm);

/// <summary>What a preview image covers: its pixel square spans 2 × <see cref="ImageRadiusMm"/>
/// around the disc centre and the recorded ring lies between <see cref="InnerRadiusMm"/> and
/// <see cref="OuterRadiusMm"/>. The preview control clips to that ring and overlays the bands.</summary>
public sealed record PreviewInfo(
    string Path,
    double ImageRadiusMm,
    double InnerRadiusMm,
    double OuterRadiusMm,
    IReadOnlyList<RadialBand> FileBands);

/// <summary>Engine-backed previews for the three families. "Projection" shows the design
/// composite through the generation geometry and tone — what the generator will lay down —
/// optionally onto a disc with different measured radii; "read-back" decodes a generated
/// output file the way a scanner would see it.</summary>
public static class PreviewRenderer
{
    public static PreviewInfo RenderProjection(
        DiscSettings settings,
        string compositePath,
        string outputPath,
        int size,
        int samplesPerSector,
        double? measuredInnerMm,
        double? measuredOuterMm,
        CancellationToken cancellationToken)
    {
        double actualInner = measuredInnerMm ?? settings.InnerRadiusMm;
        double actualOuter = measuredOuterMm ?? settings.OuterRadiusMm;
        cancellationToken.ThrowIfCancellationRequested();
        switch (settings.Family)
        {
            case OpticalDiscFamily.Dvd:
            {
                DvdStreamingOptions dvd = settings.ToDvd();
                CalibrationPreviewOptions options = new(
                    dvd.TotalSectors, 0, dvd.TotalSectors,
                    dvd.InnerRadiusMm, dvd.OuterRadiusMm, actualInner, actualOuter,
                    dvd.ChannelBitLengthNm, Clockwise: true,
                    dvd.LuminanceThreshold, dvd.AlphaThreshold, size, samplesPerSector,
                    dvd.PitchLinear, dvd.PitchQuadratic, dvd.PitchCubic,
                    dvd.PitchLinear, dvd.PitchQuadratic, dvd.PitchCubic,
                    dvd.Tone);
                CalibrationPreviewRenderer.Render(compositePath, outputPath, options, cancellationToken);
                return new PreviewInfo(outputPath, actualOuter, actualInner, actualOuter, FileBands(settings, actualInner, actualOuter));
            }

            case OpticalDiscFamily.BluRay:
            {
                BluRayDiscParameters generated = settings.ToBluRay();
                BluRayDiscParameters actual = settings.ToBluRay(actualInner, actualOuter);
                (uint start, uint end) = BluRayDrawingRange(settings, generated);
                BluRayPreview.RenderProjection(compositePath, outputPath, generated, actual, start, end,
                    size, settings.InvertMapping, settings.EffectiveTone, cancellationToken);
                return new PreviewInfo(outputPath, actualOuter, actualInner, actualOuter, FileBands(settings, actualInner, actualOuter));
            }

            default:
            {
                CdDiscParameters generated = settings.ToCd();
                CdDiscParameters actual = generated with { InnerRadiusMm = actualInner, OuterRadiusMm = actualOuter };
                actual.Validate();
                if (settings.HasFiles)
                {
                    CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(settings.DataDirectory.Trim(), generated,
                        settings.EffectiveVolumeLabel, settings.FilesAtOuterRing);
                    CdDataImageGenerator.PreviewWarp(compositePath, outputPath, generated, actual, size,
                        samplesPerSector, settings.EffectiveTone, plan, cancellationToken);
                }
                else
                {
                    CdTrackGenerator.PreviewWarp(compositePath, outputPath, generated, actual, size,
                        samplesPerSector, settings.EffectiveTone, cancellationToken);
                }

                return new PreviewInfo(outputPath, actual.ImageOuterRadiusMm, actualInner, actualOuter, FileBands(settings, actualInner, actualOuter));
            }
        }
    }

    /// <summary>Read-back of a generated file. CD and BD decode in-process; DVD goes through
    /// the engine's <c>simulate</c> command, which also restores the drawing range from the
    /// ISO's sidecar.</summary>
    public static async Task<PreviewInfo> RenderReadbackAsync(
        DiscSettings settings,
        string generatedFile,
        string outputPath,
        int size,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(generatedFile))
        {
            throw new FileNotFoundException("Generate the disc image on the Export page first.", generatedFile);
        }

        switch (settings.Family)
        {
            case OpticalDiscFamily.Dvd:
            {
                string[] arguments =
                [
                    "simulate",
                    "--iso", generatedFile,
                    "--output", outputPath,
                    "--inner-radius-mm", Num.Format(settings.InnerRadiusMm),
                    "--outer-radius-mm", Num.Format(settings.OuterRadiusMm),
                    "--pitch-linear", Num.Format(settings.PitchLinear),
                    "--pitch-quadratic", Num.Format(settings.PitchQuadratic),
                    "--pitch-cubic", Num.Format(settings.PitchCubic),
                    "--preview-size", Num.Format((long)size),
                    "--samples-per-sector", "16",
                ];
                DiscJobResult result = await EngineJobRunner.RunAsync(arguments, log, _ => { }, cancellationToken);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(result.Summary ?? "DVD read-back simulation failed.");
                }

                return new PreviewInfo(outputPath, settings.OuterRadiusMm, settings.InnerRadiusMm, settings.OuterRadiusMm, []);
            }

            case OpticalDiscFamily.BluRay:
                return await Task.Run(() =>
                {
                    BluRayIsoMetadata? metadata = BluRayIsoMetadata.TryLoad(generatedFile);
                    BluRayDiscParameters disc = metadata is null
                        ? settings.ToBluRay()
                        : new BluRayDiscParameters(metadata.TotalSectors, 1, metadata.InnerRadiusMm, metadata.OuterRadiusMm);
                    uint start = metadata?.DrawingStartSector ?? 0;
                    uint end = metadata?.DrawingEndSectorExclusive ?? disc.TotalSectors;
                    BluRayPreview.RenderReadback(generatedFile, outputPath, disc, size, start, end, cancellationToken);
                    return new PreviewInfo(outputPath, disc.OuterRadiusMm, disc.InnerRadiusMm, disc.OuterRadiusMm, []);
                }, cancellationToken);

            default:
                return await Task.Run(() =>
                {
                    if (Path.GetExtension(generatedFile).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        CdDataImageMetadata? metadata = CdDataImageMetadata.TryLoad(generatedFile);
                        CdDiscParameters parameters = metadata?.Parameters ?? settings.ToCd();
                        CdDataImageGenerator.PreviewTrack(generatedFile, outputPath, parameters, size, 48, cancellationToken);
                        return new PreviewInfo(outputPath, parameters.ImageOuterRadiusMm, parameters.InnerRadiusMm, parameters.OuterRadiusMm, []);
                    }

                    CdDiscParameters cd = settings.ToCd();
                    CdTrackGenerator.PreviewTrack(generatedFile, outputPath, cd, size, 48, settings.Interleave, cancellationToken);
                    return new PreviewInfo(outputPath, cd.ImageOuterRadiusMm, cd.InnerRadiusMm, cd.OuterRadiusMm, []);
                }, cancellationToken);
        }
    }

    /// <summary>Radial bands holding the filesystem when a files folder is set, measured on
    /// a disc with the given radii (the generated radii unless a measured disc is previewed).
    /// Scans the folder, so call it off the UI thread.</summary>
    public static IReadOnlyList<RadialBand> FileBands(DiscSettings settings, double? innerMm = null, double? outerMm = null)
    {
        if (!settings.HasFiles || !Directory.Exists(settings.DataDirectory.Trim()))
        {
            return [];
        }

        string directory = settings.DataDirectory.Trim();
        double inner = innerMm ?? settings.InnerRadiusMm;
        double outer = outerMm ?? settings.OuterRadiusMm;
        try
        {
            switch (settings.Family)
            {
                case OpticalDiscFamily.Dvd:
                {
                    DvdStreamingOptions dvd = settings.ToDvd();
                    HybridIsoImageWriter.LayoutPlan plan = HybridIsoImageWriter.Plan(directory, dvd.TotalSectors,
                        settings.EffectiveVolumeLabel, settings.FilesAtOuterRing);
                    double length = dvd.TotalSectors * 38688.0 * dvd.ChannelBitLengthNm * 1e-6;
                    DvdTrackGeometry geometry = DvdTrackGeometry.Create(inner, outer, length,
                        dvd.PitchLinear, dvd.PitchQuadratic, dvd.PitchCubic);
                    double Radius(uint lba) => geometry.RadiusAtArcLength(length * Math.Min(1.0, (double)lba / dvd.TotalSectors));
                    uint drawingStart = AlignUp16(settings.FilesAtOuterRing ? plan.MetadataEndLbaExclusive : plan.DataEndLbaExclusive);
                    List<RadialBand> bands = [new RadialBand(inner, Radius(drawingStart))];
                    if (settings.FilesAtOuterRing)
                    {
                        bands.Add(new RadialBand(Radius(plan.FileDataStartLba), outer));
                    }

                    return bands;
                }

                case OpticalDiscFamily.BluRay:
                {
                    BluRayDiscParameters generated = settings.ToBluRay();
                    (uint start, uint end) = BluRayDrawingRange(settings, generated);
                    BluRayTrackGeometry geometry = new(settings.ToBluRay(inner, outer));
                    return Bands(inner, outer, geometry.AtSector(start).RadiusMm, geometry.AtSector(end).RadiusMm);
                }

                default:
                {
                    CdDiscParameters generated = settings.ToCd();
                    CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(directory, generated,
                        settings.EffectiveVolumeLabel, settings.FilesAtOuterRing);
                    CdDiscParameters actual = generated with { InnerRadiusMm = inner, OuterRadiusMm = outer };
                    return Bands(inner, outer,
                        actual.RadiusFromByte((long)plan.DrawingStartLba * CdDiscParameters.BytesPerSector),
                        actual.RadiusFromByte((long)plan.DrawingEndLbaExclusive * CdDiscParameters.BytesPerSector));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            return [];
        }
    }

    private static IReadOnlyList<RadialBand> Bands(double inner, double outer, double drawingStart, double drawingEnd)
    {
        List<RadialBand> bands = [];
        if (drawingStart > inner + 1e-6)
        {
            bands.Add(new RadialBand(inner, drawingStart));
        }

        if (drawingEnd < outer - 1e-6)
        {
            bands.Add(new RadialBand(drawingEnd, outer));
        }

        return bands;
    }

    private static (uint Start, uint End) BluRayDrawingRange(DiscSettings settings, BluRayDiscParameters generated)
    {
        if (!settings.HasFiles)
        {
            return (0, generated.TotalSectors);
        }

        BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(generated, settings.DataDirectory.Trim(),
            settings.EffectiveVolumeLabel, filesAtOuterRing: settings.FilesAtOuterRing);
        return (plan.DrawingStartSector, plan.DrawingEndSectorExclusive);
    }

    private static uint AlignUp16(uint lba) => (lba + 15u) & ~15u;
}
