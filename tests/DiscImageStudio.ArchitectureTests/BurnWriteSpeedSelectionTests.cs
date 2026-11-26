using System.Dynamic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Localization;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class BurnWriteSpeedSelectionTests
{
    internal static void Run()
    {
        CheckRewritableMediaSpeeds();
        WpfTestHost.Run(CheckSelection);
        Console.WriteLine("burn-write-speed-selection: rewritable media, repeated selections, refresh and language changes passed (no hardware)");
    }

    private static void CheckRewritableMediaSpeeds()
    {
        object recorder = new();
        dynamic format = new ExpandoObject();
        format.MediaHeuristicallyBlank = false;
        format.IsRecorderSupported = new Func<object, bool>(_ => false);
        format.IsCurrentMediaSupported = new Func<object, bool>(_ => false);
        dynamic fourTimes = new ExpandoObject();
        fourTimes.WriteSpeed = 2705;
        fourTimes.RotationTypeIsPureCAV = false;
        format.SupportedWriteSpeedDescriptors = new object[] { fourTimes };
        var rewritableMedia = new (OpticalBurnMediaKind Kind, int Type, int State, string Description)[]
        {
            (OpticalBurnMediaKind.CdData, 0x3, 0x4000, "a finalized CD-RW"),
            (OpticalBurnMediaKind.DvdData, 0x7, 0x1, "a formatted DVD+RW"),
            (OpticalBurnMediaKind.DvdData, 0xA, 0x1, "an overwritable DVD-RW"),
            (OpticalBurnMediaKind.DvdData, 0xA, 0x4000, "a finalized DVD-RW"),
            (OpticalBurnMediaKind.DvdData, 0xD, 0x1, "a formatted DVD+RW DL"),
            (OpticalBurnMediaKind.BluRayData, 0x13, 0x1, "a formatted BD-RE"),
            (OpticalBurnMediaKind.BluRayData, 0x13, 0x0, "an unformatted BD-RE"),
        };
        OpticalWriteSpeed[] expected = [new(2705, false)];
        foreach (var media in rewritableMedia)
        {
            format.CurrentPhysicalMediaType = media.Type;
            format.CurrentMediaStatus = media.State;
            Require(WindowsImapiBurner.ReadCurrentMediaWriteSpeeds(
                media.Kind, media.Type, recorder, (object)format, default).SequenceEqual(expected),
                $"{media.Description} advertises its speed while the data formatter reports it cannot write");
        }

        format.CurrentPhysicalMediaType = 0xA;
        format.CurrentMediaStatus = 0x2;
        format.SupportedWriteSpeedDescriptors = Array.Empty<object>();
        format.SupportedWriteSpeeds = new[] { 2705 };
        Require(WindowsImapiBurner.ReadCurrentMediaWriteSpeeds(
            OpticalBurnMediaKind.DvdData, 0xA, recorder, (object)format, default).SequenceEqual(expected),
            "a blank DVD-RW advertises its speed immediately");

        format.CurrentMediaStatus = 0x4000;
        format.SupportedWriteSpeeds = Array.Empty<int>();
        Require(WindowsImapiBurner.ReadCurrentMediaWriteSpeeds(
            OpticalBurnMediaKind.DvdData, 0xA, recorder, (object)format, default).Count == 0,
            "a used DVD-RW with empty speed lists leaves the automatic choice available");

        var unavailableDescriptors = new UnavailableSpeedDescriptors([2705]);
        Require(WindowsImapiBurner.ReadCurrentMediaWriteSpeeds(
            OpticalBurnMediaKind.DvdData, 0xA, recorder, unavailableDescriptors, default).SequenceEqual(expected),
            "a used DVD-RW uses the simple speed list when the descriptor query fails");

        COMException? failure = null;
        try
        {
            WindowsImapiBurner.ReadCurrentMediaWriteSpeeds(
                OpticalBurnMediaKind.DvdData, 0xA, recorder, new UnavailableSpeedDescriptors(null), default);
        }
        catch (COMException exception)
        {
            failure = exception;
        }
        Require(failure?.Message == UnavailableSpeedDescriptors.SimpleListError,
            "a used DVD-RW reports the existing preview error if neither speed query works");
    }

    private static void CheckSelection()
    {
        string previousLanguage = L.CurrentLanguage;
        var burner = new FakeBurner();
        var shell = new ShellViewModel(Dispatcher.CurrentDispatcher, burner);
        var host = new Window
        {
            Width = 1200,
            Height = 800,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            L.Initialize(L.Chinese);
            CheckLabelBindingIdentity();
            host.Show();
            foreach (BurnViewModel burn in new[] { shell.Burn, shell.CalibrationBurn })
            {
                burn.SelectedDevice = burner.Device;
                var view = new BurnView(burn);
                host.Content = view;
                Pump();
                ComboBox speeds = Descendants(view).OfType<ComboBox>()
                    .Single(combo => ReferenceEquals(combo.ItemsSource, burn.Speeds));
                foreach (int index in new[] { 1, 2, 0, 2, 1, 0 })
                    Select(speeds, burn, index);

                Select(speeds, burn, 2);
                burn.RefreshWriteSpeedsAsync().GetAwaiter().GetResult();
                Pump();
                Require(speeds.Items.Count == 3 && burn.SelectedSpeed?.Speed == burner.Supported[1]
                    && ReferenceEquals(speeds.SelectedItem, burn.SelectedSpeed),
                    "refresh preserves the supported selection without narrowing the available speeds");

                L.SetLanguage(L.English);
                burn.Relocalize();
                Pump();
                Require(ReferenceEquals(speeds.SelectedItem, burn.SelectedSpeed)
                    && burn.SelectedSpeed?.Speed == burner.Supported[1],
                    "relocalizing labels keeps the selected speed");
                foreach (int index in new[] { 0, 1, 2, 0 })
                    Select(speeds, burn, index);
                L.SetLanguage(L.Chinese);
                burn.Relocalize();
            }
            Require(burner.BurnCalls == 0, "changing or refreshing a speed never starts a device write");
        }
        finally
        {
            host.Close();
            L.Initialize(previousLanguage);
        }
    }

    private static void CheckLabelBindingIdentity()
    {
        var option = new BurnViewModel.BurnWriteSpeedOption(new(300, false), "4×");
        var indexed = new Dictionary<BurnViewModel.BurnWriteSpeedOption, string> { [option] = "selected" };
        int originalHash = option.GetHashCode();
        var label = new TextBlock();
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(option.DisplayName)) { Source = option });
        Pump();
        Require(label.Text == "4×", "the display binding is active");
        Require(option.GetHashCode() == originalHash && indexed.Remove(option),
            "WPF label subscriptions cannot invalidate an indexed speed option");
        BindingOperations.ClearBinding(label, TextBlock.TextProperty);
        Pump();
        Require(option.GetHashCode() == originalHash, "removing a label binding preserves option identity");
    }

    private static void Select(ComboBox combo, BurnViewModel burn, int index)
    {
        combo.IsDropDownOpen = true;
        Pump();
        var item = combo.ItemContainerGenerator.ContainerFromIndex(index) as ComboBoxItem
            ?? throw new InvalidOperationException("The speed dropdown did not create an item container.");
        item.SetCurrentValue(ComboBoxItem.IsSelectedProperty, true);
        combo.IsDropDownOpen = false;
        Pump();
        Require(combo.SelectedIndex == index
            && ReferenceEquals(combo.SelectedItem, burn.Speeds[index])
            && ReferenceEquals(burn.SelectedSpeed, burn.Speeds[index]),
            $"selecting speed {index} updates the dropdown and the pending burn request");
    }

    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Burn speed selection: " + message);
    }

    private sealed class UnavailableSpeedDescriptors(int[]? fallbackSpeeds) : DynamicObject
    {
        internal const string SimpleListError = "Simple speed list unavailable.";

        public override bool TryGetMember(GetMemberBinder binder, out object? result)
        {
            if (binder.Name == "SupportedWriteSpeedDescriptors")
                throw new COMException("Speed descriptors unavailable.");
            if (binder.Name == "SupportedWriteSpeeds")
            {
                result = fallbackSpeeds ?? throw new COMException(SimpleListError);
                return true;
            }
            result = null;
            return false;
        }
    }

    private sealed class FakeBurner : IOpticalDiscBurner
    {
        internal OpticalBurnDevice Device { get; } = new("speed-test", "Test recorder", []);
        internal IReadOnlyList<OpticalWriteSpeed> Supported { get; } = [new(300, false), new(600, false)];
        internal int BurnCalls { get; private set; }
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([Device]);
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => Task.FromResult(64L);
        public Task<OpticalMediaCapacity> GetMediaCapacityAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => Task.FromResult(new OpticalMediaCapacity(64, 64));
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
            => Task.FromResult(Supported);
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            BurnCalls++;
            throw new InvalidOperationException("The selection test must not start a burn.");
        }
    }
}
