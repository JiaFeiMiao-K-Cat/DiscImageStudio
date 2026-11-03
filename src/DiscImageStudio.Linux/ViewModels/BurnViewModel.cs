using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

/// <summary>Burn page: writes a generated file with cdrecord/wodim/cdrskin, growisofs or
/// xorriso. The exact command is always shown so it can be checked or run in a terminal.</summary>
public partial class BurnViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private ProjectViewModel? _project;
    private OpticalDiscFamily? _familyOverride;

    [ObservableProperty] private string _sourceFile = string.Empty;
    [ObservableProperty] private OpticalDrive? _selectedDrive;
    [ObservableProperty] private BurnTool? _selectedTool;
    [ObservableProperty] private string _selectedSpeed = "Auto";
    [ObservableProperty] private bool _simulate;
    [ObservableProperty] private bool _eject = true;
    [ObservableProperty] private bool _acknowledged;
    [ObservableProperty] private string _commandText = string.Empty;
    [ObservableProperty] private string? _permissionWarning;
    [ObservableProperty] private string? _toolHint;
    [ObservableProperty] private string _sourceInfo = string.Empty;

    public BurnViewModel(MainViewModel main)
    {
        _main = main;
        main.ProjectChanged += () =>
        {
            _project = main.Project;
            _familyOverride = null;
            OnPropertyChanged(nameof(Project));
            SourceFile = _project?.OutputPath ?? string.Empty;
            NotifyMaterialChanged();
        };
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsBusy))
            {
                BurnCommand.NotifyCanExecuteChanged();
                MediaInfoCommand.NotifyCanExecuteChanged();
                EjectDiscCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public ProjectViewModel? Project => _project;

    public ObservableCollection<OpticalDrive> Drives { get; } = [];

    public ObservableCollection<BurnTool> Tools { get; } = [];

    [ObservableProperty] private IReadOnlyList<string> _speeds = ["Auto"];

    /// <summary>The disc family the source is burned as: the project's, unless another page handed
    /// over a file for a different family (a calibration target for a CD, say, while a DVD project is open).</summary>
    public OpticalDiscFamily? Family => _familyOverride ?? _project?.Family;

    public BurnMaterial? Material => Family is { } family ? BurnService.MaterialFor(family, SourceFile) : null;

    public string MaterialText => Material is { } material
        ? BurnService.Describe(material)
        : Family is not { } family ? "Open a project first."
        : $"Choose a generated {(family == OpticalDiscFamily.CompactDisc ? ".wav, .raw or .iso" : ".iso")} file for this {DiscFamilies.Label(family)} {(_familyOverride is null ? "project" : "target")}.";

    public bool HasDrives => Drives.Count > 0;

    public bool SourceExists => File.Exists(SourceFile);

    public string MediaAdvice => Material switch
    {
        BurnMaterial.CdAudio => "Blank CD-R. The track is written as CD-DA audio with -audio (no -swab), which keeps the byte order the picture was encoded with.",
        BurnMaterial.CdData => "Blank CD-R. The image fills the disc, so it is written disc-at-once without padding.",
        BurnMaterial.Dvd => "Blank single-layer DVD±R. The image fills the disc, so it is written disc-at-once without padding.",
        BurnMaterial.BluRay => "Blank single-layer BD-R. It must stay unformatted: growisofs runs with spare:none so no spare areas shift the sectors.",
        _ => string.Empty,
    };

    public void OnShown()
    {
        if (_project is not null && (string.IsNullOrWhiteSpace(SourceFile) || !File.Exists(SourceFile)) && File.Exists(_project.OutputPath))
        {
            SourceFile = _project.OutputPath;
        }

        RefreshDrives();
        RefreshTools();
    }

    /// <summary>Selects a file to burn. <paramref name="family"/> overrides the project's disc family
    /// for this file, until the user browses for another one or opens a different project.</summary>
    public void UseSource(string path, OpticalDiscFamily? family = null)
    {
        _familyOverride = family is { } value && value != _project?.Family ? value : null;
        if (SourceFile == path)
        {
            NotifyMaterialChanged();
            RefreshTools();
        }
        else
        {
            SourceFile = path;
        }
    }

    private void NotifyMaterialChanged()
    {
        OnPropertyChanged(nameof(Family));
        OnPropertyChanged(nameof(Material));
        OnPropertyChanged(nameof(MaterialText));
        OnPropertyChanged(nameof(MediaAdvice));
    }

    partial void OnSourceFileChanged(string value)
    {
        NotifyMaterialChanged();
        OnPropertyChanged(nameof(SourceExists));
        SourceInfo = File.Exists(value) ? $"{Num.Bytes(new FileInfo(value).Length)} · modified {File.GetLastWriteTime(value):yyyy-MM-dd HH:mm}" : "File not found";
        RefreshTools();
    }

    partial void OnSelectedDriveChanged(OpticalDrive? value)
    {
        PermissionWarning = value is { Accessible: false }
            ? $"You do not have read/write access to {value.Device}. Add yourself to its group and log in again:  sudo usermod -aG {BurnService.DeviceGroup(value.Device)} $USER"
            : null;
        if (value is not null)
        {
            _main.Settings.LastBurnDevice = value.Device;
        }

        UpdateCommand();
    }

    partial void OnSelectedToolChanged(BurnTool? value) => UpdateCommand();

    partial void OnSelectedSpeedChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            UpdateCommand();
        }
    }

    partial void OnSimulateChanged(bool value) => UpdateCommand();

    partial void OnEjectChanged(bool value) => UpdateCommand();

    partial void OnAcknowledgedChanged(bool value) => BurnCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void RefreshDrives()
    {
        string? previous = SelectedDrive?.Device ?? _main.Settings.LastBurnDevice;
        Drives.Clear();
        foreach (OpticalDrive drive in BurnService.FindDrives())
        {
            Drives.Add(drive);
        }

        SelectedDrive = Drives.FirstOrDefault(d => d.Device == previous) ?? Drives.FirstOrDefault();
        OnPropertyChanged(nameof(HasDrives));
        UpdateCommand();
    }

    private void RefreshTools()
    {
        string? previous = SelectedTool?.Id;
        Tools.Clear();
        ToolHint = null;
        if (Material is { } material)
        {
            foreach (BurnTool tool in BurnService.ToolsFor(material, SourceFile))
            {
                Tools.Add(tool);
            }

            if (Tools.Count == 0)
            {
                ToolHint = "No suitable burning program was found. " + BurnService.InstallHint(material);
            }
        }

        SelectedTool = Tools.FirstOrDefault(t => t.Id == previous) ?? Tools.FirstOrDefault();
        string speed = SelectedSpeed ?? "Auto";
        IReadOnlyList<string> speeds = Material switch
        {
            BurnMaterial.Dvd => ["Auto", "1", "2", "4", "6", "8", "16"],
            BurnMaterial.BluRay => ["Auto", "1", "2", "4", "6", "8"],
            _ => ["Auto", "1", "2", "4", "8", "10", "16", "24", "32", "48"],
        };
        if (!speeds.SequenceEqual(Speeds))
        {
            Speeds = speeds;
        }

        // Re-assert after the ComboBox reacted to the new item list.
        SelectedSpeed = string.Empty;
        SelectedSpeed = speeds.Contains(speed) ? speed : "Auto";
        UpdateCommand();
    }

    private BurnCommand? BuildCommand()
    {
        if (Material is not { } material || SelectedTool is null)
        {
            return null;
        }

        string device = SelectedDrive?.Device ?? "/dev/sr0";
        int? speed = int.TryParse(SelectedSpeed, out int value) ? value : null;
        return BurnService.Build(material, SelectedTool, device, SourceFile, speed, Simulate, Eject);
    }

    private void UpdateCommand()
    {
        CommandText = BuildCommand()?.ShellText ?? "Choose a generated file, a recorder and a recording program.";
        BurnCommand.NotifyCanExecuteChanged();
    }

    private bool CanBurn() => !_main.IsBusy && Acknowledged && SourceExists && SelectedDrive is not null && SelectedTool is not null && Material is not null;

    [RelayCommand(CanExecute = nameof(CanBurn))]
    private async Task Burn()
    {
        BurnCommand? command = BuildCommand();
        if (command is null || _main.Ui is null || SelectedDrive is null)
        {
            return;
        }

        if (!Simulate
            && await _main.Ui.AskAsync("Start burning?",
                $"This writes {Path.GetFileName(SourceFile)} to the disc in {SelectedDrive.Description}. The disc cannot be reused afterwards, and an interrupted burn usually ruins it.\n\nUse only blank, expendable media.",
                "Burn", null, "Cancel", danger: true) != DialogChoice.Primary)
        {
            return;
        }

        string title = Simulate ? "Test burn (laser off)" : "Burning disc";
        bool ok = await _main.RunJobAsync(title, async job =>
        {
            job.Log("$ " + command.ShellText);
            int exit = await BurnService.RunAsync(command, job.Log, fraction => job.Progress(fraction), job.Token);
            job.Token.ThrowIfCancellationRequested();
            if (exit != 0)
            {
                throw new InvalidOperationException($"{Path.GetFileName(command.Executable)} exited with code {exit}. See the log for details.");
            }
        }, cancellable: true);
        if (ok)
        {
            _main.ShowToast(ToastKind.Success, Simulate ? "Test burn finished" : "Burn finished",
                Simulate ? "Nothing was written. Turn off the test mode to burn for real." : "Let the disc cool down, then photograph it under a point light.");
            Acknowledged = false;
        }
    }

    private bool CanUseDrive() => !_main.IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseDrive))]
    private async Task MediaInfo()
    {
        if (SelectedDrive is null)
        {
            _main.ShowToast(ToastKind.Warning, "No optical drive selected", null);
            return;
        }

        BurnCommand? command = BurnService.MediaInfoCommand(SelectedDrive.Device);
        if (command is null)
        {
            _main.ShowToast(ToastKind.Warning, "No disc-information tool found", "Install xorriso, dvd+rw-tools or cdrtools.");
            return;
        }

        _main.IsLogOpen = true;
        await _main.RunJobAsync("Reading disc information", async job =>
        {
            job.Log("$ " + command.ShellText);
            await BurnService.RunAsync(command, job.Log, _ => { }, job.Token);
        }, cancellable: true);
    }

    [RelayCommand(CanExecute = nameof(CanUseDrive))]
    private async Task EjectDisc()
    {
        if (SelectedDrive is null || BurnService.EjectCommand(SelectedDrive.Device) is not { } command)
        {
            return;
        }

        await _main.RunJobAsync("Ejecting", async job =>
        {
            await BurnService.RunAsync(command, job.Log, _ => { }, job.Token);
        }, cancellable: false);
    }

    [RelayCommand]
    private async Task BrowseSource()
    {
        if (_main.Ui is null || Family is not { } family)
        {
            return;
        }

        IReadOnlyList<string> files = await _main.Ui.PickFilesAsync("Choose the file to burn",
            family == OpticalDiscFamily.CompactDisc ? [FileTypes.Burnable, FileTypes.All] : [FileTypes.Iso, FileTypes.All],
            Path.GetDirectoryName(SourceFile) ?? _project?.Folder, allowMultiple: false);
        if (files.Count == 1)
        {
            // A browsed file belongs to the open project; without one, keep the family we have.
            UseSource(files[0], _project is null ? family : null);
        }
    }

    [RelayCommand]
    private async Task CopyCommand()
    {
        if (_main.Ui is not null && CommandText.Length > 0)
        {
            await _main.Ui.CopyTextAsync(CommandText);
            _main.ShowToast(ToastKind.Info, "Command copied", "Paste it into a terminal to burn manually.");
        }
    }
}
