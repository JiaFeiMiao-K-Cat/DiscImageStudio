using System.IO;
using System.Windows.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DvdImageSolver;

namespace DiscImageStudio.ViewModels;

public partial class DesignViewModel
{
    private DispatcherTimer? _dvdLayerOccupancyTimer;
    private long _dvdLayerOccupancyRevision;

    public ExportViewModel DvdExport => _shell.Export;
    public bool ShowDvdLayerOccupancy => IsDvd && DvdExport.ExportDualLayerDvd;
    public string? DvdLayerOccupancyText { get; private set; }
    public bool DvdLayerOccupancyUnavailable { get; private set; }
    internal (double Inner, double Outer)? DvdLayerOccupiedRing { get; private set; }

    private double GetDvdDrawingOuterRadius()
    {
        var (layerBreak, minusTotal) = DvdExport.GetDvdLayerLayout();
        return DvdExport.GetDvdOptions(layerBreak, minusTotal).OutputOuterRadiusMm;
    }

    internal void ConnectDvdLayerPreview(ExportViewModel export)
    {
        _dvdLayerOccupancyTimer = new DispatcherTimer { Interval = CompositeDebounce };
        _dvdLayerOccupancyTimer.Tick += async (_, _) =>
        {
            _dvdLayerOccupancyTimer.Stop();
            await RefreshDvdLayerOccupancyAsync();
        };
        export.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ExportViewModel.ExportDualLayerDvd)
                or nameof(ExportViewModel.DvdLayerBreak) or nameof(ExportViewModel.DvdLayerFormat))
                RefreshGeometry();
        };
        _state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DiscParametersState.DvdDataDirectory))
                ScheduleDvdLayerOccupancy();
        };
    }

    private void ScheduleDvdLayerOccupancy()
    {
        if (_dvdLayerOccupancyTimer is null)
            return;

        ++_dvdLayerOccupancyRevision;
        _dvdLayerOccupancyTimer.Stop();
        SetDvdLayerOccupancy(null, null);
        OnPropertyChanged(nameof(ShowDvdLayerOccupancy));
        if (ShowDvdLayerOccupancy)
            _dvdLayerOccupancyTimer.Start();
    }

    private async Task RefreshDvdLayerOccupancyAsync()
    {
        long revision = _dvdLayerOccupancyRevision;
        string directory = _state.DvdDataDirectory.Trim();
        try
        {
            var (layerBreak, minusTotal) = DvdExport.GetDvdLayerLayout();
            uint split = layerBreak!.Value;
            uint layer1Sectors = (minusTotal ?? checked(split * 2)) - split;
            DvdStreamingOptions options = DvdExport.GetDvdOptions(split, minusTotal);
            options.Validate();
            var (used, ring) = await Task.Run(() =>
            {
                if (directory.Length == 0)
                    return (0u, ((double Inner, double Outer)?)null);

                var layout = HybridIsoImageWriter.PlanDvdDoubleLayer(directory, split, "DISC_IMAGE", minusTotal);
                uint occupied = layout.DataEndLbaExclusive - layout.FileDataStartLba;
                double trackLength = layer1Sectors * 38688.0 * options.ChannelBitLengthNm * 1e-6;
                var geometry = DvdTrackGeometry.Create(options.InnerRadiusMm, options.OutputOuterRadiusMm,
                    trackLength, options.PitchLinear, options.PitchQuadratic, options.PitchCubic);
                // OTP reads Layer 1 from the outside in, so the final sectors lie at the inner edge.
                double outer = geometry.RadiusAtArcLength(trackLength * occupied / layer1Sectors);
                return (occupied, ((double Inner, double Outer)?)(options.InnerRadiusMm, outer));
            });
            if (revision == _dvdLayerOccupancyRevision)
                SetDvdLayerOccupancy(FormattableString.CurrentCulture(
                    $"{used / 512.0:N1} / {layer1Sectors / 512.0:N1} MiB"), ring);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or FormatException
            or OverflowException or UnauthorizedAccessException)
        {
            if (revision == _dvdLayerOccupancyRevision)
                SetDvdLayerOccupancy(null, null, unavailable: true);
        }
    }

    private void SetDvdLayerOccupancy(string? text, (double Inner, double Outer)? ring, bool unavailable = false)
    {
        DvdLayerOccupancyText = text;
        DvdLayerOccupiedRing = ring;
        DvdLayerOccupancyUnavailable = unavailable;
        OnPropertyChanged(nameof(DvdLayerOccupancyText));
        OnPropertyChanged(nameof(DvdLayerOccupancyUnavailable));
        CanvasChanged?.Invoke();
    }
}
