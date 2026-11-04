using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class CalibrationReturnButtonTests
{
    internal static void Run()
    {
        WpfTestHost.Run(CheckButton);
        Console.WriteLine("calibration-return: standalone/project navigation, grouped stages, return click and parameter isolation passed");
    }

    private static void CheckButton()
    {
        var shell = new ShellViewModel(Dispatcher.CurrentDispatcher, new NoDeviceBurner());
        Window? host = null;
        try
        {
            var page = new CalibrationView(shell.Calibration);
            host = new Window { DataContext = shell, Content = page };
            page.Measure(new Size(1200, 740));
            page.Arrange(new Rect(0, 0, 1200, 740));
            page.UpdateLayout();
            var header = ((Grid)page.Content).Children.OfType<PageHeader>().Single();
            var actions = header.Actions as Panel ?? throw new InvalidOperationException("Missing calibration header actions.");
            var back = actions.Children.OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "CalibrationBack");
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(back).GetPattern(PatternInterface.Invoke);
            foreach (bool createTarget in new[] { false, true })
            {
                shell.NavigateTo(Workspace.Calibration);
                RefreshStagesBinding(header);
                Require(!shell.IsProjectMode && header.Stages.Visibility == Visibility.Collapsed,
                    "standalone calibration hides project navigation");
                if (createTarget) shell.Calibration.CreateTargetCommand.Execute(null);
                var draft = shell.Calibration.Target;
                invoke.Invoke();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (shell.CurrentWorkspace != Workspace.Home || !ReferenceEquals(shell.Calibration.Target, draft))
                    throw new InvalidOperationException("Clicking the calibration Back button must navigate without completing or clearing setup.");
            }

            var burnPage = new BurnView(shell.CalibrationBurn);
            var burnHeader = ((StackPanel)((ScrollViewer)burnPage.Content).Content).Children.OfType<PageHeader>().Single();
            host.Content = burnPage;
            shell.NavigateTo(Workspace.CalibrationBurn);
            RefreshStagesBinding(burnHeader);
            Require(!shell.IsProjectMode && burnHeader.Stages.Visibility == Visibility.Collapsed,
                "standalone calibration recording also hides project navigation");

            // Keep this project fixture in memory so the test does not change recent-project history.
            var project = new ProjectDocument(ProjectJsonStore.CurrentSchemaVersion, "Navigation test", ProjectDocument.DvdFamily,
                new(24, 58, 2295104), [], new("", false, "", "TEST", false), null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, project);
            shell.IsProjectOpen = true;
            shell.IsDirty = true;
            shell.NavigateTo(Workspace.Export);
            host.Content = page;
            shell.NavigateTo(Workspace.Calibration);
            RefreshStagesBinding(header);
            Require(shell.IsProjectMode && header.Stages.Visibility == Visibility.Visible,
                "calibration entered from a project retains workspace navigation");

            string drawingRadius = shell.State.DvdOuterRadius;
            shell.Calibration.Parameters.DvdOuterRadius = "57.125";
            Require(!ReferenceEquals(shell.State, shell.Calibration.Parameters) && shell.State.DvdOuterRadius == drawingRadius,
                "editing calibration setup leaves the drawing geometry unchanged");
            var savedTarget = shell.Calibration.Target;

            host.Content = burnPage;
            shell.NavigateTo(Workspace.CalibrationBurn);
            RefreshStagesBinding(burnHeader);
            Require(shell.IsProjectMode && burnHeader.Stages.Visibility == Visibility.Visible,
                "calibration recording retains the open project's workspace navigation");
            burnHeader.Current = shell.CurrentWorkspace;
            var stages = ((IEnumerable<StageItem>)burnHeader.Stages.StageList.ItemsSource).ToArray();
            Require(stages.Select(item => item.Workspace).SequenceEqual(new[]
                { Workspace.Design, Workspace.Export, Workspace.Burn, Workspace.Preview, Workspace.Calibration })
                && stages.Count(item => item.IsGroupStart) == 1
                && stages.Single(item => item.IsGroupStart).Workspace == Workspace.Preview,
                "the stage rail keeps disc-making and preview/calibration grouped navigation");
            Require(stages.Single(item => item.IsCurrent).Workspace == Workspace.Calibration,
                "calibration recording remains highlighted under calibration");

            host.Content = page;
            shell.NavigateTo(Workspace.Calibration);
            invoke.Invoke();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Require(shell.CurrentWorkspace == Workspace.Export && shell.IsDirty
                && ReferenceEquals(savedTarget, shell.Calibration.Target) && shell.State.DvdOuterRadius == drawingRadius,
                "the header returns to the originating project page with its edits and calibration target intact");
        }
        finally
        {
            if (host is not null)
            {
                host.Content = null;
                host.Close();
            }
            shell.Calibration.OnWindowClosed();
            shell.LivePreview.OnWindowClosed();
        }
    }

    private static void RefreshStagesBinding(PageHeader header)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        BindingOperations.GetBindingExpression(header, PageHeader.ShowStagesProperty)!.UpdateTarget();
        header.UpdateLayout();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Calibration navigation: " + message);
    }

    private sealed class NoDeviceBurner : IOpticalDiscBurner
    {
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([]);

        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
