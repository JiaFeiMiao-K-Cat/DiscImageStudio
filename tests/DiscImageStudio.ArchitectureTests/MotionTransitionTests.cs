using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DiscImageStudio.Controls;

internal static class MotionTransitionTests
{
    public static void Run() => WpfTestHost.Run(CheckTransitions);

    private static void CheckTransitions()
    {
        AnimationSettlementTests.Check();
        Require(((Freezable)Motion.EaseOut).IsFrozen && ((Freezable)Motion.EaseIn).IsFrozen,
            "shared easing curves are safe across UI threads");
        RevealPanel reveal = new()
        {
            Content = new Border { Height = 180 },
            VerticalAlignment = VerticalAlignment.Top,
        };
        ConfirmModal modal = new();
        Grid layout = new();
        layout.Children.Add(reveal);
        layout.Children.Add(modal);
        Window window = new()
        {
            Width = 640,
            Height = 480,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = layout,
        };
        try
        {
            window.Show();
            Pump(20);
            reveal.IsExpanded = true;
            Pump(45);
            if (Motion.Enabled)
            {
                Require(reveal.Height > 0 && reveal.Height < 180,
                    "SizeChanged does not cancel the panel's own height animation");
                double interrupted = reveal.Height;
                reveal.IsExpanded = false;
                double firstFrame = AnimationSettlementTests.ReadNextRenderedValue(() => reveal.Height);
                Require(firstFrame > 0 && firstFrame <= interrupted + 1,
                    "reversing an expansion continues from the displayed height on its first rendered frame");
                Pump(20);
            }
            reveal.IsExpanded = true;
            Pump(350);
            Require(double.IsNaN(reveal.Height) && reveal.Opacity == 1 && reveal.ActualHeight >= 180,
                "an expanded panel returns to automatic layout");
            reveal.IsExpanded = false;
            Pump(270);
            Require(reveal.Height == 0 && reveal.Opacity == 0,
                "collapse settles at zero height and opacity");

            Task<bool> confirmed = modal.ShowAsync("Delete", "Temporary test only", "Confirm");
            Button confirm = (Button)modal.FindName("ConfirmButton");
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => confirmed.IsCompleted, "confirmation exit");
            Console.WriteLine($"confirmation exit: completed={confirmed.IsCompletedSuccessfully}, result={(confirmed.IsCompletedSuccessfully ? confirmed.Result : null)}, open={modal.IsOpen}");
            Require(confirmed.IsCompletedSuccessfully && confirmed.Result && !modal.IsOpen,
                "repeated clicks complete a confirmation once after its exit");

            Task<bool> cancelled = modal.ShowPrimaryAsync("Cancel", "Temporary test only", "Continue");
            if (Motion.Enabled) confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            modal.CancelPending();
            Require(cancelled.IsCompletedSuccessfully && !cancelled.Result,
                "window shutdown cancels an in-flight confirmation immediately");
            Task<bool?> next = modal.ShowThreeWayAsync("Save", "Temporary test only", "Save", "Discard");
            Pump(240);
            Require(!next.IsCompleted, "an old exit callback cannot close the next confirmation");
            Require(modal.HandleKeyDown(System.Windows.Input.Key.Escape), "Escape is handled by the modal");
            WaitUntil(() => next.IsCompleted, "Escape exit");
            Require(next.IsCompletedSuccessfully && next.Result is null && !modal.IsOpen,
                "Escape preserves the three-way cancellation result");
        }
        finally
        {
            modal.CancelPending();
            window.Close();
        }
        Console.WriteLine("motion-transitions: panel reversal, final layout and modal completion checks passed");
    }

    private static void Pump(int milliseconds)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void WaitUntil(Func<bool> condition, string operation)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(3)) Pump(30);
        Require(condition(), operation + " completed within the deadline");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Motion check failed: " + description);
    }
}
