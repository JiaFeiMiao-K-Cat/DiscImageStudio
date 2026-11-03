using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio.Desktop;

/// <summary>Preview and exports consume the same project. Filesystem ranges are never painted as image data.</summary>
public static class PreviewService
{
    public static async Task Render(ProjectDocument document, string input, string output, bool readback,
        double inner, double outer, int size, int samples, IProgress<DiscJobProgress>? progress = null, CancellationToken token = default)
    {
        if (size is < 256 or > 8192 || samples is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(size));
        var d = document; var g = d.Geometry;
        uint start = 0, end = checked((uint)g.Sectors);
        bool files = !string.IsNullOrWhiteSpace(d.Outputs.DataDirectory);
        if (readback)
        {
            // Sidecars restore artifact geometry without modifying the current design project.
            if (d.IsCd && CdDataImageMetadata.TryLoad(input) is { } cdData)
            {
                g = new(cdData.Parameters.InnerRadiusMm, cdData.Parameters.OuterRadiusMm, cdData.Parameters.Sectors);
                start = cdData.DrawingStartLba; end = cdData.DrawingEndLbaExclusive; files = true;
            }
            else if (d.IsCd && CdTrackMetadata.TryLoad(input) is { } cdTrack)
            {
                g = new(cdTrack.InnerRadiusMm, cdTrack.OuterRadiusMm, cdTrack.Sectors); end = checked((uint)g.Sectors);
                d = d with { Outputs = d.Outputs with { Interleave = cdTrack.Interleaved } }; files = false;
            }
            else if (d.IsDvd && DvdImageMetadata.TryLoad(input) is { } dvd)
            {
                g = new(dvd.InnerRadiusMm, dvd.OuterRadiusMm, dvd.TotalSectors, dvd.PitchLinear, dvd.PitchQuadratic, dvd.PitchCubic);
                start = dvd.DrawingStartLba; end = dvd.DrawingEndLbaExclusive; files = dvd.HasFileSystem;
            }
            else if (d.IsBluRay && BluRayIsoMetadata.TryLoad(input) is { } bd)
            {
                g = new(bd.InnerRadiusMm, bd.OuterRadiusMm, bd.TotalSectors);
                start = bd.DrawingStartSector; end = bd.DrawingEndSectorExclusive; files = start != 0 || end != bd.TotalSectors;
            }
            else { files = false; }
            d = d with { Geometry = g };
        }
        var actual = d with { Geometry = g with { InnerRadiusMm = inner, OuterRadiusMm = outer } };
        ProjectJsonStore.Validate(actual);
        var dvdActual = new DvdStreamingOptions((uint)g.Sectors, inner, outer,
            PitchLinear: g.PitchLinear, PitchQuadratic: g.PitchQuadratic, PitchCubic: g.PitchCubic);
        if (!readback && files)
        {
            if (d.IsCd)
            {
                var layout = CdDataImageGenerator.PrepareLayout(d.Outputs.DataDirectory,
                    new(g.InnerRadiusMm, g.OuterRadiusMm, g.Sectors), d.Outputs.VolumeLabel, d.Outputs.FilesAtOuterRing);
                start = layout.DrawingStartLba; end = layout.DrawingEndLbaExclusive;
            }
            else if (d.IsDvd)
            {
                var layout = DvdStreamingGenerator.PrepareHybrid(d.Outputs.DataDirectory,
                    dvdActual with { InnerRadiusMm = g.InnerRadiusMm, OuterRadiusMm = g.OuterRadiusMm },
                    d.Outputs.VolumeLabel, d.Outputs.FilesAtOuterRing);
                start = layout.DrawingStartLba; end = layout.DrawingEndLbaExclusive;
            }
            else
            {
                var layout = BluRayIsoGenerator.PrepareDrawing(new((uint)g.Sectors, 1, g.InnerRadiusMm, g.OuterRadiusMm),
                    d.Outputs.DataDirectory, d.Outputs.VolumeLabel, filesAtOuterRing: d.Outputs.FilesAtOuterRing);
                start = layout.DrawingStartSector; end = layout.DrawingEndSectorExclusive;
            }
        }
        if (d.IsBluRay)
        {
            var generated = new BluRayDiscParameters((uint)g.Sectors, 1, g.InnerRadiusMm, g.OuterRadiusMm);
            var measured = generated with { InnerRadiusMm = inner, OuterRadiusMm = outer };
            if (readback) BluRayPreviewRenderer.RenderReadback(input, output, measured, size, token, start, end);
            else BluRayPreviewRenderer.RenderCalibration(input, output, generated, measured, start, end, size, d.BluRay?.InvertMapping ?? false, token, d.Tone);
            if (files) FileRegionPreviewOverlay.ApplyBluRay(output, measured, start, end, token);
        }
        else
        {
            var args = JobArguments.Preview(d, input, output, readback, inner, outer, size, samples).ToList();
            if (!readback && files)
            {
                if (d.IsCd) args.AddRange(["--data-dir", d.Outputs.DataDirectory, "--volume-label", d.Outputs.VolumeLabel, "--file-position", d.Outputs.FilesAtOuterRing ? "outer" : "inner"]);
                else
                {
                    args[args.IndexOf("--lba") + 1] = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    args[args.IndexOf("--fill-sectors") + 1] = (end - start).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            var result = await ConsoleModuleRunner.Catalog.ExecuteAsync(new(args[0], args.Skip(1)), progress, token);
            if (!result.Succeeded) throw new InvalidOperationException(result.Summary);
            if (files)
            {
                if (d.IsCd) FileRegionPreviewOverlay.ApplyCd(output, new(inner, outer, g.Sectors), start, end, token);
                else FileRegionPreviewOverlay.ApplyDvd(output, dvdActual, start, end, token);
            }
        }
    }
}
