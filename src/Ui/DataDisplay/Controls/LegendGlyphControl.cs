using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CircuitRF.Ui.DataDisplay.Controls;

/// <summary>
/// Draws the legend-placement glyphs of the Plot Inspector's legend button group: a plot frame with a
/// curve, plus the legend where <see cref="LegendPlacement"/> puts it — nothing (Off), a small box in
/// the frame's top-right corner (On), or entry rows under a shortened frame (Below). Geometry is
/// laid out on a 16-unit grid and scaled to the control, so all of it lives here for owner tweaking.
/// </summary>
public class LegendGlyphControl : Control
{
    public static readonly StyledProperty<LegendPlacement> KindProperty =
        AvaloniaProperty.Register<LegendGlyphControl, LegendPlacement>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<LegendGlyphControl, IBrush?>(nameof(Stroke));

    static LegendGlyphControl()
    {
        AffectsRender<LegendGlyphControl>(KindProperty, StrokeProperty);
    }

    public LegendPlacement Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var b = Bounds;
        double s = Math.Min(b.Width, b.Height) / 16.0;
        if (s <= 0) return;

        // 16-unit grid → pixels, centred in the control.
        double ox = (b.Width  - 16 * s) / 2.0;
        double oy = (b.Height - 16 * s) / 2.0;
        Point P(double x, double y) => new(ox + x * s, oy + y * s);

        var brush = Stroke ?? new SolidColorBrush(Color.FromArgb(180, 128, 128, 128));
        var pen   = new Pen(brush, 1.0, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        // Below shortens the frame to leave room for the rows under it.
        double frameBottom = Kind == LegendPlacement.Below ? 10.5 : 14.5;
        context.DrawRectangle(null, pen, new Rect(P(1.5, 1.5), P(14.5, frameBottom)), 1.5 * s, 1.5 * s);

        if (Kind == LegendPlacement.Below)
        {
            // A curve across the short frame, then two entries: a line sample and its name.
            DrawCurve(context, pen, P, [(3, 8.5), (6, 4.5), (9, 7), (13, 3.5)]);
            context.DrawLine(pen, P(2, 13), P(5, 13));
            context.DrawLine(pen, P(7, 13), P(14, 13));
            context.DrawLine(pen, P(2, 15.5), P(5, 15.5));
            context.DrawLine(pen, P(7, 15.5), P(12, 15.5));
            return;
        }

        if (Kind == LegendPlacement.Inside)
        {
            // The curve stays low-left, clear of the corner box.
            DrawCurve(context, pen, P, [(3, 12.5), (5.5, 9), (8, 11), (10.5, 9.5)]);
            context.DrawRectangle(null, pen, new Rect(P(7.5, 3.5), P(12.5, 7.5)));
            context.DrawLine(pen, P(9, 5),   P(11, 5));
            context.DrawLine(pen, P(9, 6.2), P(11, 6.2));
            return;
        }

        DrawCurve(context, pen, P, [(3, 12), (6, 6), (9, 10), (13, 4)]);
    }

    private static void DrawCurve(DrawingContext ctx, IPen pen, Func<double, double, Point> P,
        (double X, double Y)[] pts)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(P(pts[0].X, pts[0].Y), isFilled: false);
            for (int i = 1; i < pts.Length; i++) g.LineTo(P(pts[i].X, pts[i].Y));
            g.EndFigure(isClosed: false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}
