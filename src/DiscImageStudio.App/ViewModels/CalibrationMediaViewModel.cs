using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Burning;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRecorderChoice))]
    private IReadOnlyList<OpticalBurnDevice> _capacityRecorders = [];
    [ObservableProperty] private OpticalBurnDevice? _selectedCapacityRecorder;
    [ObservableProperty] private string _capacityStatus = string.Empty;
    [ObservableProperty] private bool _calibrateDualLayerDvd;
    private uint? _dvdMinusRTotalSectors;
    private uint? _dvdMinusRLayerBreak;
    [ObservableProperty] private string _calibrationLayerBreak = Dvd.DvdImageGenerator.MaxDvdPlusRLayer0Sectors.ToString(CultureInfo.InvariantCulture);
    public bool HasRecorderChoice => CapacityRecorders.Count > 1;
    public IAsyncRelayCommand ReadDiscSectorsCommand { get; private set; } = null!;

    [ObservableProperty] private string _calibrationDvdLayerFormat = DvdDualLayerDefaults.Plus;
    public IReadOnlyList<string> DvdLayerFormats => DvdDualLayerDefaults.Formats;

    partial void OnCalibrationDvdLayerFormatChanged(string value)
    {
        _dvdMinusRTotalSectors = null;
        _dvdMinusRLayerBreak = null;
        CalibrationLayerBreak = DvdDualLayerDefaults.Layer0Sectors(value).ToString(CultureInfo.InvariantCulture);
    }

    partial void OnSelectedCapacityRecorderChanged(OpticalBurnDevice? value) => CapacityStatus = string.Empty;

    private void InitializeCapacityCommands() =>
        ReadDiscSectorsCommand = new AsyncRelayCommand(ReadDiscSectorsAsync, () => !IsBusy);

    private void RefreshCapacityCommands() => ReadDiscSectorsCommand?.NotifyCanExecuteChanged();

    private async Task ReadCalibrationLayerBreakAsync(string family, bool wasDualLayer, string deviceId, CancellationToken token)
    {
        DvdLayerBoundary boundary = await _shell.OpticalBurner.ReadDvdLayerBoundaryAsync(deviceId, token);
        token.ThrowIfCancellationRequested();
        if (SelectedDiscType != family || SelectedCapacityRecorder?.Id != deviceId
            || (family == "DVD" && CalibrateDualLayerDvd != wasDualLayer))
        {
            _statusMessage = CapacityStatus = L.T("CalibrationMedia.SelectionChanged");
            return;
        }
        if (!boundary.DvdMinusRTotalSectors.HasValue && boundary.Layer0Sectors > Dvd.DvdImageGenerator.MaxDvdPlusRLayer0Sectors)
            throw new InvalidOperationException(L.T("光驱返回的 DVD+R DL 换层点无效。"));
        SelectedDiscType = "DVD";
        CalibrateDualLayerDvd = true;
        CalibrationDvdLayerFormat = boundary.DvdMinusRTotalSectors.HasValue ? DvdDualLayerDefaults.Minus : DvdDualLayerDefaults.Plus;
        _dvdMinusRTotalSectors = boundary.DvdMinusRTotalSectors;
        _dvdMinusRLayerBreak = boundary.DvdMinusRTotalSectors.HasValue ? boundary.Layer0Sectors : null;
        CalibrationLayerBreak = boundary.Layer0Sectors.ToString(CultureInfo.InvariantCulture);
        string status = boundary.IsLocked ? L.T("已锁定") : L.T("可设置");
        _statusMessage = CapacityStatus = L.F($"从 {boundary.DeviceName} 读取换层点 {boundary.Layer0Sectors}（{status}）。");
    }

    private async Task ReadDiscSectorsAsync()
    {
        string family = SelectedDiscType;
        bool dualLayer = family == "DVD" && CalibrateDualLayerDvd;
        var kind = family switch
        {
            "DVD" => OpticalBurnMediaKind.DvdData,
            "BD" => OpticalBurnMediaKind.BluRayData,
            _ => OpticalBurnMediaKind.CdAudio,
        };
        string? previousId = SelectedCapacityRecorder?.Id;
        await RunAsync(L.T("CalibrationMedia.Reading"), async token =>
        {
            CapacityStatus = L.T("CalibrationMedia.Reading");
            try
            {
                var devices = await _shell.OpticalBurner.GetDevicesAsync(token);
                token.ThrowIfCancellationRequested();
                var selected = devices.FirstOrDefault(d => d.Id == previousId)
                    ?? (devices.Count == 1 ? devices[0] : null);
                CapacityRecorders = devices;
                SelectedCapacityRecorder = selected;
                if (selected is null)
                {
                    _statusMessage = CapacityStatus = L.T(devices.Count == 0
                        ? "CalibrationMedia.NoDevices" : "CalibrationMedia.PickDevice");
                    return;
                }

                OpticalBurnMediaKind? detected = await _shell.OpticalBurner.DetectCalibrationMediaKindAsync(selected.Id, token);
                token.ThrowIfCancellationRequested();
                if (SelectedDiscType != family || SelectedCapacityRecorder?.Id != selected.Id
                    || (family == "DVD" && CalibrateDualLayerDvd != dualLayer))
                {
                    _statusMessage = CapacityStatus = L.T("CalibrationMedia.SelectionChanged");
                    return;
                }
                if (detected is { } actualKind)
                {
                    kind = actualKind;
                    family = actualKind switch
                    {
                        OpticalBurnMediaKind.DvdData => "DVD",
                        OpticalBurnMediaKind.BluRayData => "BD",
                        _ => "CD",
                    };
                    SelectedDiscType = family;
                    if (family == "DVD")
                        CalibrateDualLayerDvd = false;
                    dualLayer = false;
                }
                else if (dualLayer)
                {
                    await ReadCalibrationLayerBreakAsync(family, dualLayer, selected.Id, token);
                    return;
                }

                OpticalMediaCapacity capacity;
                try
                {
                    capacity = await _shell.OpticalBurner.GetMediaCapacityAsync(selected.Id, kind, token);
                }
                catch (DvdLayerBoundaryRequiredException)
                {
                    if (SelectedCapacityRecorder?.Id != selected.Id)
                    {
                        _statusMessage = CapacityStatus = L.T("CalibrationMedia.SelectionChanged");
                        return;
                    }
                    await ReadCalibrationLayerBreakAsync(family, dualLayer, selected.Id, token);
                    return;
                }
                long sectors = capacity.TotalSectors;
                token.ThrowIfCancellationRequested();
                if (family != SelectedDiscType || SelectedCapacityRecorder?.Id != selected.Id
                    || (family == "DVD" && CalibrateDualLayerDvd))
                {
                    _statusMessage = CapacityStatus = L.T("CalibrationMedia.SelectionChanged");
                    return;
                }
                if (sectors <= 0 || sectors > uint.MaxValue || (family == "BD"
                    && (sectors > BluRay.BluRayDiscParameters.SectorsPerLayer || sectors % BluRay.BluRayDiscParameters.SectorsPerCluster != 0)))
                    throw new InvalidOperationException(L.Format("CalibrationMedia.UnsupportedCapacity", sectors, family));

                long writable = capacity.WritableSectors;
                if (writable < 0 || writable > sectors)
                    throw new InvalidOperationException(L.Format("CalibrationMedia.UnsupportedCapacity", writable, family));
                // Keep the reported geometry unchanged. Only the output limit is
                // rounded down to complete encoding blocks.
                int alignment = family switch { "DVD" => 16, "BD" => 32, _ => 1 };
                writable -= writable % alignment;

                string value = sectors.ToString(CultureInfo.InvariantCulture);
                // Recorded media can still describe geometry for a future blank
                // disc. An empty limit uses the documented estimate; zero must
                // not be presented as a measured capacity for that new disc.
                string usable = writable == 0 ? string.Empty : writable.ToString(CultureInfo.InvariantCulture);
                switch (family)
                {
                    case "DVD":
                        Parameters.DvdTotalSectors = value;
                        Parameters.DvdUsableSectors = usable;
                        break;
                    case "BD":
                        Parameters.BdTotalSectors = value;
                        Parameters.BdUsableSectors = usable;
                        break;
                    default:
                        Parameters.CdSectors = value;
                        Parameters.CdUsableSectors = usable;
                        break;
                }
                _statusMessage = CapacityStatus = writable == 0
                    ? L.Format("CalibrationMedia.ReadGeometryOnly", selected.DisplayName, family, sectors)
                    : L.Format("CalibrationMedia.Read", selected.DisplayName, family, sectors, writable);
            }
            catch (OperationCanceledException)
            {
                CapacityStatus = string.Empty;
                throw;
            }
            catch (Exception ex)
            {
                _statusMessage = CapacityStatus = L.Format("CalibrationMedia.Failed", L.Error(ex));
                // RunAsync owns the failed dock state and complete diagnostics. Returning
                // normally here would label a failed read as a successful calibration task.
                throw;
            }
        });
    }
}
