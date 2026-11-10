using System.Windows;
using System.Windows.Media;

namespace DiscImageStudio.Controls;

/// <summary>
/// A skeuomorphic disc drawn procedurally instead of shipped as a bitmap: silver body,
/// label-area rings, a diagonal sheen and a genuinely transparent center hole. Vector
/// rendering stays crisp at every DPI, and the disc keeps physical colors in both themes
/// because it represents a real object rather than a themed surface.
/// </summary>
public sealed class OpticalDiscVisual : FrameworkElement
{
    private static readonly Brush BodyBrush = Freeze(new RadialGradientBrush
    {
        Center = new Point(0.5, 0.48),
        GradientOrigin = new Point(0.42, 0.36),
        RadiusX = 0.62,
        RadiusY = 0.62,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xF4, 0xF5, 0xF7), 0),
            new GradientStop(Color.FromRgb(0xE8, 0xEA, 0xEE), 0.18),
            new GradientStop(Color.FromRgb(0xD8, 0xDB, 0xE0), 0.42),
            new GradientStop(Color.FromRgb(0xC6, 0xCA, 0xD0), 0.66),
            new GradientStop(Color.FromRgb(0xB8, 0xBC, 0xC3), 0.82),
            new GradientStop(Color.FromRgb(0xD0, 0xD3, 0xD9), 0.91),
            new GradientStop(Color.FromRgb(0x9C, 0xA1, 0xA9), 0.965),
            new GradientStop(Color.FromRgb(0x7E, 0x83, 0x8B), 1),
        },
    });

    // Slight darkening toward the lower right rounds the face: the body gradient alone is
    // radially symmetric and reads as a flat decal without this.
    private static readonly Brush ShadeBrush = Freeze(new LinearGradientBrush
    {
        StartPoint = new Point(0.1, 0.05),
        EndPoint = new Point(0.95, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.45),
            new GradientStop(Color.FromArgb(0x18, 0x10, 0x12, 0x16), 1),
        },
    });

    // The clear plastic rim catches more light than the reflective data surface; a narrow
    // bright band just inside the edge makes the disc look like a physical object.
    private static readonly Brush RimGlowBrush = Freeze(new RadialGradientBrush
    {
        Center = new Point(0.5, 0.5),
        GradientOrigin = new Point(0.5, 0.5),
        RadiusX = 0.5,
        RadiusY = 0.5,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.93),
            new GradientStop(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF), 0.972),
            new GradientStop(Color.FromArgb(0x34, 0xFF, 0xFF, 0xFF), 0.988),
            new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 1),
        },
    });

    // Light diffracted by the spiral track forms a faint rainbow ring between the clamping
    // area and the label edge; concentric stops reproduce that banding without wedges.
    private static readonly Brush DiffractionBrush = Freeze(new RadialGradientBrush
    {
        Center = new Point(0.5, 0.5),
        GradientOrigin = new Point(0.5, 0.5),
        RadiusX = 0.5,
        RadiusY = 0.5,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0x00, 0xFF, 0xFF), 0.26),
            new GradientStop(Color.FromArgb(0x06, 0x00, 0xFF, 0xFF), 0.29),
            new GradientStop(Color.FromArgb(0x07, 0xFF, 0x00, 0xFF), 0.33),
            new GradientStop(Color.FromArgb(0x06, 0xFF, 0xFF, 0x00), 0.37),
            new GradientStop(Color.FromArgb(0x05, 0x00, 0xFF, 0x88), 0.41),
            new GradientStop(Color.FromArgb(0x06, 0x00, 0x80, 0xFF), 0.45),
            new GradientStop(Color.FromArgb(0x07, 0xFF, 0x00, 0x80), 0.49),
            new GradientStop(Color.FromArgb(0x06, 0xFF, 0xFF, 0x00), 0.53),
            new GradientStop(Color.FromArgb(0x04, 0x00, 0xFF, 0xFF), 0.57),
            new GradientStop(Color.FromArgb(0x00, 0x00, 0xFF, 0xFF), 0.60),
        },
    });

    private static readonly Brush SheenBrush = Freeze(new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.30),
            new GradientStop(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), 0.41),
            new GradientStop(Color.FromArgb(0x5C, 0xEA, 0xF7, 0xFF), 0.47),
            new GradientStop(Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF), 0.53),
            new GradientStop(Color.FromArgb(0x4D, 0xFF, 0xE2, 0xF4), 0.60),
            new GradientStop(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0.68),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.80),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
        },
    });

    // A second, much narrower highlight parallel to the broad sheen: a real disc mirrors a
    // window edge as a soft band plus a crisp streak, and the streak is what sells the gloss.
    private static readonly Brush StreakBrush = Freeze(new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.30),
            new GradientStop(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.345),
            new GradientStop(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF), 0.372),
            new GradientStop(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.398),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.44),
        },
    });

    private static readonly Brush CounterSheenBrush = Freeze(new LinearGradientBrush
    {
        StartPoint = new Point(1, 0),
        EndPoint = new Point(0, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.56),
            new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.72),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.88),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
        },
    });

    private static readonly Brush HubBrush = Freeze(new RadialGradientBrush
    {
        Center = new Point(0.5, 0.5),
        GradientOrigin = new Point(0.42, 0.4),
        RadiusX = 0.5,
        RadiusY = 0.5,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.45),
            new GradientStop(Color.FromArgb(0x10, 0x00, 0x00, 0x00), 0.78),
            new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1),
        },
    });

    // Contact shadow where the hub meets the hole: without it the transparent hole looks
    // punched through a flat decal instead of sitting in a molded ring.
    private static readonly Brush HubShadowBrush = Freeze(new RadialGradientBrush
    {
        Center = new Point(0.5, 0.5),
        GradientOrigin = new Point(0.5, 0.5),
        RadiusX = 0.5,
        RadiusY = 0.5,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.44),
            new GradientStop(Color.FromArgb(0x30, 0x00, 0x00, 0x00), 0.462),
            new GradientStop(Color.FromArgb(0x12, 0x00, 0x00, 0x00), 0.62),
            new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.86),
        },
    });

    private static readonly Pen EdgePen = FreezePen(Color.FromArgb(0x66, 0x00, 0x00, 0x00), 1);
    private static readonly Pen RimPen = FreezePen(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF), 1.1);
    private static readonly Pen HolePen = FreezePen(Color.FromArgb(0x66, 0x00, 0x00, 0x00), 1);
    private static readonly Pen RingPen = FreezePen(Color.FromArgb(0x16, 0x00, 0x00, 0x00), 0.8);
    private static readonly Pen RingFaintPen = FreezePen(Color.FromArgb(0x0C, 0x00, 0x00, 0x00), 0.8);

    protected override void OnRender(DrawingContext drawingContext)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4)
        {
            return;
        }

        Point center = new(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - 0.5;
        EllipseGeometry hole = new(center, radius * 0.115, radius * 0.115);
        CombinedGeometry disc = new(GeometryCombineMode.Exclude,
            new EllipseGeometry(center, radius, radius), hole);

        drawingContext.DrawGeometry(BodyBrush, EdgePen, disc);

        drawingContext.PushClip(disc);
        drawingContext.DrawEllipse(ShadeBrush, null, center, radius, radius);
        drawingContext.DrawEllipse(RimGlowBrush, null, center, radius, radius);
        drawingContext.DrawEllipse(SheenBrush, null, center, radius, radius);
        drawingContext.DrawEllipse(StreakBrush, null, center, radius, radius);
        drawingContext.DrawEllipse(CounterSheenBrush, null, center, radius, radius);
        drawingContext.Pop();

        // The diffraction band is clipped to an annulus but the brush stays mapped to the
        // full disc, so its stops keep their radius fractions.
        CombinedGeometry diffractionArea = new(GeometryCombineMode.Exclude,
            new EllipseGeometry(center, radius * 0.60, radius * 0.60),
            new EllipseGeometry(center, radius * 0.26, radius * 0.26));
        drawingContext.PushClip(diffractionArea);
        drawingContext.DrawEllipse(DiffractionBrush, null, center, radius, radius);
        drawingContext.Pop();

        // Label area: faint concentric boundaries, the way a real disc separates the hub,
        // the clamping area and the printed surface.
        drawingContext.DrawEllipse(null, RingFaintPen, center, radius * 0.90, radius * 0.90);
        drawingContext.DrawEllipse(null, RingPen, center, radius * 0.82, radius * 0.82);
        drawingContext.DrawEllipse(null, RingPen, center, radius * 0.56, radius * 0.56);
        drawingContext.DrawEllipse(null, RimPen, center, radius - 0.8, radius - 0.8);

        CombinedGeometry hub = new(GeometryCombineMode.Exclude,
            new EllipseGeometry(center, radius * 0.26, radius * 0.26), hole);
        drawingContext.DrawGeometry(HubBrush, null, hub);
        CombinedGeometry hubShade = new(GeometryCombineMode.Exclude,
            new EllipseGeometry(center, radius * 0.26, radius * 0.26),
            new EllipseGeometry(center, radius * 0.115, radius * 0.115));
        drawingContext.PushClip(hubShade);
        drawingContext.DrawEllipse(HubShadowBrush, null, center, radius, radius);
        drawingContext.Pop();
        drawingContext.DrawEllipse(null, RingFaintPen, center, radius * 0.20, radius * 0.20);
        drawingContext.DrawEllipse(null, HolePen, center, radius * 0.26, radius * 0.26);
        drawingContext.DrawEllipse(null, HolePen, center, radius * 0.115, radius * 0.115);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Pen FreezePen(Color color, double thickness)
    {
        Pen pen = new(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
