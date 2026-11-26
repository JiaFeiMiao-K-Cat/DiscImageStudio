using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DiscImageStudio.Controls;

internal static class AnimationSettlementTests
{
    internal static void Check()
    {
        Border surface = new() { Width = 160, Height = 80, Background = Brushes.SteelBlue,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Grid host = new();
        host.Children.Add(surface);
        Window window = new() { Content = host, Width = 360, Height = 260,
            ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            Pump(30);
            Motion.Enter(surface, 6);
            TranslateTransform entry = (TranslateTransform)surface.RenderTransform;
            Pump(100);
            Console.WriteLine($"entry during: Y={entry.Y:F4}, base={entry.GetAnimationBaseValue(TranslateTransform.YProperty)}, opacity={surface.Opacity:F4}");
            Pump(350);
            Point end = surface.TranslatePoint(new Point(), host);
            Console.WriteLine($"entry settled: Y={entry.Y:F4}, base={entry.GetAnimationBaseValue(TranslateTransform.YProperty)}, renderedY={end.Y:F4}, opacity={surface.Opacity:F4}");
            Require(Near(entry.Y, 0) && Near(end.Y, 0), "entrance stays at its destination after its clock stops");
            Pump(160);
            Require(Near(entry.Y, 0) && Near(surface.TranslatePoint(new Point(), host).Y, end.Y),
                "an idle frame does not move the entered UI back to its first frame");

            TranslateTransform slide = new(12, 0);
            surface.RenderTransform = slide;
            Motion.Animate(slide, TranslateTransform.XProperty, 0, Motion.Slow);
            Pump(420);
            Require(Near(slide.X, 0), "a toast-style entrance retains its final X coordinate");
            Motion.Animate(slide, TranslateTransform.XProperty, -6, Motion.Fast);
            Pump(240);
            Require(Near(slide.X, -6), "hover motion retains its nonzero destination");
            Pump(120);
            Require(Near(slide.X, -6), "hover does not snap back after completion");

            Motion.Animate(slide, TranslateTransform.XProperty, 32, Motion.Slow);
            Pump(70);
            double interrupted = slide.X;
            Motion.Animate(slide, TranslateTransform.XProperty, -12, Motion.Slow);
            double firstFrame = ReadNextRenderedValue(() => slide.X);
            Console.WriteLine($"reversal: before={interrupted:F4}, first-rendered={firstFrame:F4}");
            Require(firstFrame <= interrupted + 0.75 && firstFrame >= -12,
                "the first rendered reversal frame continues toward its new destination");
            if (Motion.Enabled)
                Require(firstFrame > -11, "reversal does not jump directly to its destination");
            Pump(420);
            Require(Near(slide.X, -12), "reversal retains the new destination after completion");

            surface.Opacity = 0;
            Motion.Fade(surface, 1, Motion.Base);
            Pump(350);
            Require(Near(surface.Opacity, 1), "a toast-style fade-in remains visible after completion");
            int completions = 0;
            Motion.Fade(surface, 0, Motion.Exit, exiting: true, completed: () => completions++);
            Pump(280);
            Require(Near(surface.Opacity, 0) && completions == 1, "fade-out stays transparent and completes once");
            Console.WriteLine($"animation-settlement: destination, rendered position, hover, reversal and fade checks passed; animations={Motion.Enabled}");
        }
        finally { window.Close(); }
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;
    internal static double ReadNextRenderedValue(Func<double> read)
    {
        DispatcherFrame frame = new();
        double value = double.NaN;
        EventHandler handler = (_, _) => { value = read(); frame.Continue = false; };
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(2) };
        timeout.Tick += (_, _) => frame.Continue = false;
        CompositionTarget.Rendering += handler;
        timeout.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); CompositionTarget.Rendering -= handler; }
        if (double.IsNaN(value)) throw new TimeoutException("No WPF render frame was produced.");
        return value;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Animation settlement failed: " + message);
    }
    private static void Pump(int milliseconds)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
