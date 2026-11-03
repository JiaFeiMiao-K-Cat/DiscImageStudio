using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DiscImageStudio.Desktop;

/// <summary>Original 24-unit outline icons, with consistent stroke and optical alignment.</summary>
public sealed class StudioIcon : Control
{
    public string Symbol { get; init; } = "disc";
    public IBrush? Ink { get; set; }
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["home"] = "M3 10L12 3L21 10 M5 9V21H10V15H14V21H19V9",
        ["design"] = "M4 4H20V20H4Z M4 15L9 10L14 15L17 12L20 15 M15 8h.01",
        ["preview"] = "M2 12C6 5 18 5 22 12C18 19 6 19 2 12Z M15 12A3 3 0 1 1 9 12A3 3 0 1 1 15 12",
        ["export"] = "M12 3V15 M7 8L12 3L17 8 M4 14V20H20V14",
        ["burn"] = "M14 3C15 7 10 8 12 12C9 12 8 10 8 8C2 14 5 21 12 21C20 21 22 12 14 3Z",
        ["calibrate"] = "M12 2V6 M12 18V22 M2 12H6 M18 12H22 M19 12A7 7 0 1 1 5 12A7 7 0 1 1 19 12 M14 12A2 2 0 1 1 10 12A2 2 0 1 1 14 12",
        ["plus"] = "M12 5V19 M5 12H19",
        ["open"] = "M3 8V5H9L11 7H21V10 M3 9H22L19 20H2Z",
        ["save"] = "M4 3H17L21 7V21H3V3Z M7 3V9H16V3 M7 21V14H17V21",
        ["settings"] = "M4 7H20 M4 17H20 M9 4V10 M15 14V20",
        ["geometry"] = "M5 3H19V21H5Z M5 7H10 M5 12H9 M5 17H10",
        ["more"] = "M5 12h.01 M12 12h.01 M19 12h.01",
        ["arrow"] = "M5 12H19 M13 6L19 12L13 18",
        ["up"] = "M12 19V5 M6 11L12 5L18 11",
        ["down"] = "M12 5V19 M6 13L12 19L18 13",
        ["trash"] = "M3 6H21 M9 6V3H15V6 M5 6L6 21H18L19 6 M10 10V17 M14 10V17",
        ["image"] = "M4 4H20V20H4Z M4 15L9 10L14 15L17 12L20 15 M15 8h.01",
        ["log"] = "M5 4H19V20H5Z M9 8H15 M9 12H15 M9 16H12",
        ["refresh"] = "M20 9A8 8 0 1 0 20 16 M20 3V9H14",
        ["check"] = "M5 12L10 17L20 7",
        ["disc"] = "M21 12A9 9 0 1 1 3 12A9 9 0 1 1 21 12 M15 12A3 3 0 1 1 9 12A3 3 0 1 1 15 12 M7 8L8 7 M16 17L17 16"
    };
    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        using var transform = context.PushTransform(Matrix.CreateScale(size / 24, size / 24));
        var pen = new Pen(Ink ?? Foreground(), 1.65, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (Symbol == "more")
            foreach (double x in new[] { 5d, 12d, 19d }) context.DrawEllipse(Ink ?? Foreground(), null, new Point(x, 12), 1.5, 1.5);
        else context.DrawGeometry(null, pen, Geometry.Parse(Paths.GetValueOrDefault(Symbol, Paths["disc"])));
    }
    private IBrush Foreground() => this.FindResource("StudioInk") as IBrush ?? Brush.Parse("#58627A");
}

/// <summary>Small, vector-rendered disc mark used in the shell and media cards.</summary>
public sealed class DiscMark : Control
{
    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        using var transform = context.PushTransform(Matrix.CreateScale(size / 40, size / 40));
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = [new GradientStop(Color.Parse("#6688FF"), 0), new GradientStop(Color.Parse("#3453CE"), 1)]
        }, null, new RoundedRect(new Rect(0, 0, 40, 40), 12));
        context.DrawEllipse(null, new Pen(Brush.Parse("#94B0FF"), 1), new Point(20, 20), 14, 14);
        context.DrawEllipse(null, new Pen(Brushes.White, 2.2), new Point(20, 20), 11, 11);
        context.DrawEllipse(Brushes.White, null, new Point(20, 20), 3.3, 3.3);
        context.DrawLine(new Pen(Brush.Parse("#B7CEFF"), 2, lineCap: PenLineCap.Round), new Point(11, 11), new Point(14, 14));
    }
}
