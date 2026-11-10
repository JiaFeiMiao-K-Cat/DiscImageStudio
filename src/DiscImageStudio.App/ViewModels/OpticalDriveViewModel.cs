using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Burning;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public sealed record DriveOption(string Id, string DisplayName);

public sealed record DriveDetailRow(string Label, string Value);

/// <summary>
/// Polls the optical drives for the sidebar card. The card is presentation only: every
/// probe is read-only, polling pauses while a burn owns the recorder, and a missing drive
/// collapses the whole module so the sidebar looks exactly as it did before.
/// </summary>
public partial class OpticalDriveViewModel : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly ShellViewModel _shell;
    private readonly IOpticalDriveProbe _probe;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _probeCancellation;
    private OpticalDriveInfo? _drive;
    private OpticalDriveMediaInfo? _media;
    private bool _isRefreshing;
    private bool _refreshQueued;
    private bool _isClosing;
    private bool _isSyncingSelection;
    private bool _isInitialProbeComplete;
    private bool _probeFailureReported;
    private string? _manualDriveId;
    private string? _lastBurnDeviceId;

    internal OpticalDriveViewModel(ShellViewModel shell, Dispatcher dispatcher, IOpticalDriveProbe probe)
    {
        _shell = shell;
        _probe = probe;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = PollInterval,
        };
        _timer.Tick += (_, _) => Poll();
        _shell.BusyChanged += OnBusyChanged;
        _shell.PropertyChanged += Shell_PropertyChanged;
        _shell.Burn.PropertyChanged += Burn_PropertyChanged;
        _shell.CalibrationBurn.PropertyChanged += Burn_PropertyChanged;
    }

    [ObservableProperty]
    private bool _hasDrive;

    [ObservableProperty]
    private bool _hasMedia;

    [ObservableProperty]
    private bool _hasMediaId;

    [ObservableProperty]
    private bool _hasDriveMeta;

    [ObservableProperty]
    private bool _isMultipleDrives;

    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private string _driveName = string.Empty;

    [ObservableProperty]
    private string _driveMeta = string.Empty;

    [ObservableProperty]
    private string _mediaSummary = string.Empty;

    [ObservableProperty]
    private string _mediaIdText = string.Empty;

    [ObservableProperty]
    private string _totalSectorsText = "—";

    [ObservableProperty]
    private string _usableSectorsText = "—";

    [ObservableProperty]
    private IReadOnlyList<DriveOption> _drives = [];

    [ObservableProperty]
    private DriveOption? _selectedDrive;

    [ObservableProperty]
    private IReadOnlyList<DriveDetailRow> _details = [];

    /// <summary>Snapshot hook: true once the first probe attempt (or a deliberate skip)
    /// has finished, so the capture can wait for a rendered card without touching hardware.</summary>
    public bool IsInitialProbeComplete => _isInitialProbeComplete;

    public void Start()
    {
        if (_isClosing || _timer.IsEnabled)
        {
            return;
        }

        _timer.Start();
        Poll();
    }

    public void OnWindowClosed()
    {
        _isClosing = true;
        _timer.Stop();
        _probeCancellation?.Cancel();
    }

    internal void Relocalize() => UpdateText();

    partial void OnSelectedDriveChanged(DriveOption? value)
    {
        if (_isSyncingSelection || value is null || !HasDrive)
        {
            return;
        }

        if (string.Equals(_manualDriveId, value.Id, StringComparison.Ordinal))
        {
            return;
        }

        _manualDriveId = value.Id;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    private void Poll()
    {
        if (_isClosing || _isRefreshing)
        {
            return;
        }

        if (!_shell.IsProjectMode || IsBurnActive())
        {
            MarkInitialProbeComplete();
            return;
        }

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_isClosing)
        {
            return;
        }

        if (!_shell.IsProjectMode || IsBurnActive())
        {
            MarkInitialProbeComplete();
            return;
        }

        if (_isRefreshing)
        {
            _refreshQueued = true;
            return;
        }

        _isRefreshing = true;
        try
        {
            ReconcileBurnFollow();
            string? preferred = _manualDriveId ?? _lastBurnDeviceId ?? SelectedDrive?.Id;
            using CancellationTokenSource cancellation = new();
            _probeCancellation = cancellation;
            try
            {
                OpticalDriveProbeResult result =
                    await _probe.ProbeAsync(preferred, cancellation.Token);
                if (_isClosing)
                {
                    return;
                }

                Apply(result);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Window closed or a burn took the recorder mid-probe.
            }
            catch (Exception exception)
            {
                if (!_probeFailureReported)
                {
                    _probeFailureReported = true;
                    _shell.AppendLog(JobDiagnostics.FormatLog("光驱信息读取失败", exception, warning: true));
                }

                // A transient read failure keeps the last readable card; a drive that is
                // really gone disappears with the next successful enumeration.
            }
            finally
            {
                if (ReferenceEquals(_probeCancellation, cancellation))
                {
                    _probeCancellation = null;
                }
            }
        }
        finally
        {
            _isRefreshing = false;
            MarkInitialProbeComplete();
            if (_refreshQueued)
            {
                _refreshQueued = false;
                if (!_isClosing)
                {
                    _ = RefreshAsync();
                }
            }
        }
    }

    private void MarkInitialProbeComplete()
    {
        if (_isInitialProbeComplete)
        {
            return;
        }

        _isInitialProbeComplete = true;
        OnPropertyChanged(nameof(IsInitialProbeComplete));
    }

    private void Apply(OpticalDriveProbeResult result)
    {
        _probeFailureReported = false;
        _drive = result.Drive;
        _media = result.Media;
        if (_drive is null)
        {
            HasDrive = false;
            HasMedia = false;
            HasDriveMeta = false;
            IsMultipleDrives = false;
            IsDetailsOpen = false;
            Drives = [];
            _isSyncingSelection = true;
            SelectedDrive = null;
            _isSyncingSelection = false;
            UpdateText();
            return;
        }

        HasDrive = true;
        HasMedia = _media is not null;
        DriveOption[] options = result.Drives
            .Select(drive => new DriveOption(drive.Id, DescribeDriveOption(drive)))
            .ToArray();
        Drives = options;
        IsMultipleDrives = options.Length > 1;
        if (_manualDriveId is not null && options.All(option => option.Id != _manualDriveId))
        {
            _manualDriveId = null;
        }
        _isSyncingSelection = true;
        SelectedDrive = options.FirstOrDefault(option => option.Id == _drive.Id) ?? options[0];
        _isSyncingSelection = false;
        UpdateText();
    }

    private void UpdateText()
    {
        if (_drive is null)
        {
            DriveName = string.Empty;
            DriveMeta = string.Empty;
            HasDriveMeta = false;
            MediaSummary = string.Empty;
            HasMediaId = false;
            MediaIdText = string.Empty;
            TotalSectorsText = "—";
            UsableSectorsText = "—";
            Details = [];
            return;
        }

        DriveName = DescribeDriveName(_drive);
        DriveMeta = DescribeDriveMeta(_drive);
        HasDriveMeta = DriveMeta.Length > 0;
        OpticalDriveMediaInfo? media = _media;
        if (media is null)
        {
            MediaSummary = L.T("DriveInfo.Empty");
            HasMediaId = false;
            MediaIdText = string.Empty;
            TotalSectorsText = "—";
            UsableSectorsText = "—";
            Details = BuildDetails(_drive, media: null);
            return;
        }

        string state = DescribeState(media.State);
        MediaSummary = media.ProfileName + " · " + state;
        HasMediaId = !string.IsNullOrWhiteSpace(media.MediaId);
        MediaIdText = HasMediaId ? media.MediaId! : string.Empty;
        TotalSectorsText = FormatSectors(media.TotalSectors);
        UsableSectorsText = FormatSectors(media.UsableSectors);
        Details = BuildDetails(_drive, media);
    }

    private static IReadOnlyList<DriveDetailRow> BuildDetails(
        OpticalDriveInfo drive,
        OpticalDriveMediaInfo? media)
    {
        List<DriveDetailRow> rows = [];
        rows.Add(new(L.T("DriveInfo.Model"), DescribeDriveName(drive)));
        if (drive.FirmwareRevision.Length > 0)
        {
            rows.Add(new(L.T("DriveInfo.Firmware"), drive.FirmwareRevision));
        }

        if (drive.VolumePaths.Count > 0)
        {
            rows.Add(new(L.T("DriveInfo.VolumePaths"), string.Join(" ", drive.VolumePaths)));
        }

        if (media is null)
        {
            rows.Add(new(L.T("DriveInfo.State"), L.T("DriveInfo.Empty")));
            return rows;
        }

        rows.Add(new(L.T("DriveInfo.Profile"), media.ProfileName));
        rows.Add(new(L.T("DriveInfo.State"), DescribeState(media.State)));
        if (media.Sessions > 0)
        {
            rows.Add(new(L.T("DriveInfo.Sessions"), FormatSectors(media.Sessions)));
        }

        if (!string.IsNullOrWhiteSpace(media.MediaId))
        {
            rows.Add(new(L.T("DriveInfo.MediaId"), media.MediaId));
        }

        if (!string.IsNullOrWhiteSpace(media.MediaManufacturer))
        {
            rows.Add(new(L.T("DriveInfo.Manufacturer"), media.MediaManufacturer));
        }

        if (media.Atip is { } atip)
        {
            rows.Add(new(L.T("DriveInfo.AtipLeadIn"), atip.LeadInTime));
            rows.Add(new(L.T("DriveInfo.AtipLeadOut"), atip.LeadOutTime));
            rows.Add(new(L.T("DriveInfo.AtipRewritable"),
                atip.IsRewritable ? L.T("DriveInfo.Yes") : L.T("DriveInfo.No")));
        }

        if (media.FreeSectors is { } free)
        {
            rows.Add(new(L.T("DriveInfo.FreeSectors"), FormatSectors(free)));
        }

        if (media.NextWritableAddress is { } nextWritable)
        {
            rows.Add(new(L.T("DriveInfo.NextWritable"), FormatSectors(nextWritable)));
        }

        if (FormatSpeeds(media.ReadSpeedsKbps, media.Family) is { } read)
        {
            rows.Add(new(L.T("DriveInfo.ReadSpeeds"), read));
        }

        if (FormatSpeeds(media.WriteSpeedsKbps, media.Family) is { } write)
        {
            rows.Add(new(L.T("DriveInfo.WriteSpeeds"), write));
        }

        return rows;
    }

    private static string? FormatSpeeds(IReadOnlyList<int> speeds, OpticalDriveMediaFamily family)
    {
        if (speeds.Count == 0)
        {
            return null;
        }

        double singleSpeedKbps = family switch
        {
            OpticalDriveMediaFamily.Cd => 176.4,
            OpticalDriveMediaFamily.Dvd => 1385.0,
            OpticalDriveMediaFamily.BluRay => 4495.625,
            OpticalDriveMediaFamily.HdDvd => 4568.75,
            _ => 0,
        };
        IEnumerable<string> values = speeds.Select(speed => singleSpeedKbps > 0
            ? string.Create(CultureInfo.CurrentCulture,
                $"{FormatMultiplier(speed / singleSpeedKbps)}× ({speed:N0} kB/s)")
            : string.Create(CultureInfo.CurrentCulture, $"{speed:N0} kB/s"));
        return string.Join("\n", values);
    }

    private static string FormatMultiplier(double multiplier)
        => multiplier >= 10
            ? multiplier.ToString("0", CultureInfo.CurrentCulture)
            : Math.Round(multiplier, 1).ToString("0.#", CultureInfo.CurrentCulture);

    private static string FormatSectors(long? value)
        => value is { } sectors
            ? sectors.ToString("N0", CultureInfo.CurrentCulture)
            : "—";

    private static string DescribeState(OpticalDiscState state) => state switch
    {
        OpticalDiscState.Blank => L.T("DriveInfo.StateBlank"),
        OpticalDiscState.Incomplete => L.T("DriveInfo.StateIncomplete"),
        OpticalDiscState.Complete => L.T("DriveInfo.StateComplete"),
        _ => L.T("DriveInfo.StateOther"),
    };

    private static string DescribeDriveName(OpticalDriveInfo drive)
    {
        string name = (drive.Vendor + " " + drive.Product).Trim();
        return name.Length > 0 ? name : L.T("DriveInfo.Drive");
    }

    private static string DescribeDriveOption(OpticalDriveInfo drive)
    {
        string name = DescribeDriveName(drive);
        if (drive.VolumePaths.Count > 0)
        {
            name += " · " + string.Join(" ", drive.VolumePaths);
        }

        return name;
    }

    private static string DescribeDriveMeta(OpticalDriveInfo drive)
    {
        List<string> parts = [];
        if (drive.FirmwareRevision.Length > 0)
        {
            parts.Add(L.T("DriveInfo.Firmware") + " " + drive.FirmwareRevision);
        }

        if (drive.VolumePaths.Count > 0)
        {
            parts.Add(string.Join(" ", drive.VolumePaths));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The burn page owns drive choice when it has one; a manual pick in the card
    /// survives until that selection changes or the chosen drive disappears.</summary>
    private void ReconcileBurnFollow()
    {
        BurnViewModel activeBurn = _shell.CurrentWorkspace == Workspace.CalibrationBurn
            ? _shell.CalibrationBurn
            : _shell.Burn;
        string? burnDeviceId = activeBurn.SelectedDevice?.Id ?? _shell.Burn.SelectedDevice?.Id;
        if (string.Equals(burnDeviceId, _lastBurnDeviceId, StringComparison.Ordinal))
        {
            return;
        }

        _lastBurnDeviceId = burnDeviceId;
        _manualDriveId = null;
    }

    private bool IsBurnActive()
        => _shell.Burn.IsBurnRunning || _shell.CalibrationBurn.IsBurnRunning || _shell.IsBusy;

    private void OnBusyChanged()
    {
        if (_shell.IsBusy)
        {
            _probeCancellation?.Cancel();
            return;
        }

        if (!_shell.IsProjectMode)
        {
            return;
        }

        _ = RefreshAsync();
    }

    private void Burn_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BurnViewModel.SelectedDevice))
        {
            // The card follows the burn page's choice; pick it up without waiting for the poll.
            if (_shell.IsProjectMode && !IsBurnActive())
            {
                _ = RefreshAsync();
            }

            return;
        }

        if (e.PropertyName == nameof(BurnViewModel.IsBurnRunning)
            && (_shell.Burn.IsBurnRunning || _shell.CalibrationBurn.IsBurnRunning))
        {
            // A burn owns the recorder now; stop probing it and discard any in-flight result.
            _probeCancellation?.Cancel();
            return;
        }

        if (e.PropertyName != nameof(BurnViewModel.IsBurnRunning)
            || _shell.Burn.IsBurnRunning || _shell.CalibrationBurn.IsBurnRunning)
        {
            return;
        }

        if (_shell.IsProjectMode)
        {
            _ = RefreshAsync();
        }
    }

    private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ShellViewModel.IsProjectMode)
            or nameof(ShellViewModel.CurrentWorkspace)))
        {
            return;
        }

        if (!_shell.IsProjectMode)
        {
            IsDetailsOpen = false;
            return;
        }

        _ = RefreshAsync();
    }
}
