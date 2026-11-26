using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Controls;
using DiscImageStudio.ViewModels;

/// <summary>The connected surface (settings, drive details) and the burn outcome strip.</summary>
internal static class FrontendMotionTests
{
    public static void Run()
    {
        WpfTestHost.Run(CheckConnectedSurface);
        CheckMilestones();
        Console.WriteLine("frontend-motion: connected surface reversal/settling and burn milestones passed");
    }

    private static void CheckConnectedSurface()
    {
        Button source = new() { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        ConnectedSurface surface = new() { Content = new Border { Width = 400, Height = 300 } };
        Border backdrop = new() { Visibility = Visibility.Collapsed };
        surface.Backdrop = backdrop;
        Grid root = new();
        root.Children.Add(source);
        root.Children.Add(backdrop);
        root.Children.Add(surface);
        Window window = new() { Width = 800, Height = 600, ShowInTaskbar = false, ShowActivated = false, Content = root };
        try
        {
            window.Show();
            Pump(30);
            Require(!surface.IsShown && surface.Progress == 0, "starts folded");
            surface.Open(() => source);
            Require(surface.IsShown && !surface.IsHitTestVisible, "opening surface is visible but not clickable while it travels");
            Pump(100);
            if (Motion.Enabled)
            {
                double mid = surface.Progress;
                Require(mid > 0 && mid < 1, "an open is animated");
                surface.Close();
                Pump(1);
                Require(surface.Progress <= mid + 0.05, "reversing continues from the displayed value");
                surface.Open(() => source);
            }

            Pump(450);
            Require(surface.Progress == 1 && surface.IsHitTestVisible && !surface.IsTransitioning
                    && (double)surface.ReadLocalValue(ConnectedSurface.ProgressProperty) == 1,
                "open settles at 1 with 1 as the base value (no jump back to the first frame)");
            Require(backdrop.Visibility == Visibility.Visible && backdrop.Opacity == 1, "backdrop follows the surface");
            Pump(300);
            Require(surface.Progress == 1, "nothing moves after settling");

            int folded = 0;
            surface.Folded += (_, _) => folded++;
            surface.Close();
            Pump(40);
            surface.Close();
            Pump(400);
            Require(!surface.IsShown && surface.Progress == 0 && folded == 1 && backdrop.Visibility == Visibility.Collapsed,
                "close settles folded once, backdrop removed");

            for (int i = 0; i < 9; i++)
            {
                surface.Toggle(() => source);
                Pump(15);
            }

            Pump(600);
            Require(surface.IsOpen ? surface.Progress == 1 && surface.IsShown : surface.Progress == 0 && !surface.IsShown,
                "a burst of toggles ends at rest in the last requested state");
            surface.Snap(false);
            Require(!surface.IsShown && surface.Progress == 0, "snap lands without a transition");
        }
        finally
        {
            window.Close();
        }
    }

    private static void CheckMilestones()
    {
        const long total = 1000;
        BurnMilestonesViewModel m = new();
        Require(!m.IsVisible, "hidden before any burn");
        m.Fail("请选择刻录机。", cancelled: false);
        Require(!m.IsVisible, "a validation error before a job never shows an outcome");

        // CD Extra success: write → close-session → flush → finalize → verify → complete.
        m.Begin();
        m.Observe(new("write", "w", 500, total));
        Require(State(m, 0) == MilestoneState.Active && State(m, 1) == MilestoneState.Pending, "writing");
        m.Observe(new("write", "w", total, total));
        Require(State(m, 0) == MilestoneState.Active, "100 % written bytes alone is not 'written'");
        m.Observe(new("close-session", "s1", total, total));
        Require(State(m, 1) == MilestoneState.Pending, "closing session 1 is not the final close");
        m.Observe(new("flush", "f", total, total));
        Require(State(m, 0) == MilestoneState.Done, "written once the recorder flushed");
        m.Observe(new("finalize", "fin", total, total));
        Require(State(m, 1) == MilestoneState.Active && State(m, 2) == MilestoneState.Pending, "finalizing");
        m.Observe(new("verify", "v", 100, total));
        Require(State(m, 1) == MilestoneState.Done && State(m, 2) == MilestoneState.Active, "finalized, verifying");
        m.Observe(new("complete", "ok", total, total));
        m.Succeed();
        Require(m.Items.All(item => item.State == MilestoneState.Done), "all three pass");

        // Verification error after finalizing: the third row fails, the first two stay done.
        m.Begin();
        m.Observe(new("write", "w", 1, total));
        m.Observe(new("flush", "f", total, total));
        m.Observe(new("finalize", "fin", total, total));
        m.Observe(new("verify", "v", 10, total));
        m.Fail("LBA 356,283 读取失败", cancelled: false);
        Require(State(m, 0) == MilestoneState.Done && State(m, 1) == MilestoneState.Done
                && State(m, 2) == MilestoneState.Failed && m.Items[2].Detail.Contains("356,283"),
            "a read-back error is reported as verification not completed, with the failing LBA");
        Require(m.Items[2].Title != m.Items[0].Title && m.Summary.Length > 0, "summary and titles set");

        // Error during the write: write fails, the others are not done.
        m.Begin();
        m.Observe(new("write", "w", 1, total));
        m.Fail("03/0C/00", cancelled: false);
        Require(State(m, 0) == MilestoneState.Failed && State(m, 1) == MilestoneState.Failed && State(m, 2) == MilestoneState.Failed,
            "nothing after a failed write is claimed");

        // A plain IMAPI burn without read-back: written + finalized, verification skipped.
        m.Begin();
        m.Observe(new("write", "w", 1, total));
        m.Observe(new("finalize", "fin", total, total));
        m.Observe(new("complete", "done", total, total));
        m.Succeed();
        Require(State(m, 0) == MilestoneState.Done && State(m, 1) == MilestoneState.Done && State(m, 2) == MilestoneState.Skipped,
            "no read-back is shown as not verified, never as passed");

        // Simulated write: only the first row can complete.
        m.Begin(testWrite: true);
        m.Observe(new("write", "w", 1, total));
        m.Observe(new("flush", "skip", total, total));
        m.Observe(new("complete", "sim", total, total));
        m.Succeed();
        Require(State(m, 0) == MilestoneState.Done && State(m, 1) == MilestoneState.Skipped && State(m, 2) == MilestoneState.Skipped,
            "a test write never claims finalize or verification");
    }

    private static MilestoneState State(BurnMilestonesViewModel model, int index) => model.Items[index].State;

    private static void Pump(int milliseconds)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Frontend check failed: " + description);
    }
}
