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
    public bool HasRecorderChoice => CapacityRecorders.Count > 1;
    public IAsyncRelayCommand ReadDiscSectorsCommand { get; private set; } = null!;

    partial void OnSelectedCapacityRecorderChanged(OpticalBurnDevice? value) => CapacityStatus = string.Empty;

    private void InitializeCapacityCommands() =>
        ReadDiscSectorsCommand = new AsyncRelayCommand(ReadDiscSectorsAsync, () => !IsBusy);

    private void RefreshCapacityCommands() => ReadDiscSectorsCommand?.NotifyCanExecuteChanged();

    private async Task ReadDiscSectorsAsync()
    {
        string family = SelectedDiscType;
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

                OpticalMediaCapacity capacity = await _shell.OpticalBurner.GetMediaCapacityAsync(selected.Id, kind, token);
                long sectors = capacity.TotalSectors;
                token.ThrowIfCancellationRequested();
                if (family != SelectedDiscType || SelectedCapacityRecorder?.Id != selected.Id)
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
