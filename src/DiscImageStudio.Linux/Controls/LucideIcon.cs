using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DiscImageStudio.Linux.Controls;

/// <summary>Draws one <see cref="Icons"/> outline in the inherited foreground colour.</summary>
public sealed class LucideIcon : Control
{
    private static readonly ConcurrentDictionary<string, Geometry> Cache = new();

    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<LucideIcon, string?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LucideIcon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LucideIcon, double>(nameof(StrokeThickness), 2.0);

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        WidthProperty.OverrideDefaultValue<LucideIcon>(18);
        HeightProperty.OverrideDefaultValue<LucideIcon>(18);
    }

    public string? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Data) || Foreground is null)
        {
            return;
        }

        Geometry geometry = Cache.GetOrAdd(Data, static data => StreamGeometry.Parse(data));
        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        if (scale <= 0)
        {
            return;
        }

        double offsetX = (Bounds.Width - 24 * scale) / 2;
        double offsetY = (Bounds.Height - 24 * scale) / 2;
        Pen pen = new(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
