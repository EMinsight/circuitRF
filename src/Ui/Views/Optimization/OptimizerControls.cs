using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Optimization;

namespace CircuitRF.Ui.Views.Optimization;

// The Optimizer panel's four small drawings (brief-tuneopt-10 §2). Each paints from one or two
// properties and nothing else, so a run's per-iteration update is a property set and a redraw.

/// <summary>The best cost per iteration on a log scale — the header's sparkline. With <see cref="LogScale"/> off it is
/// the Yield panel's yield-vs-iteration chart (brief-yield-12 R-ya12-1): linear, 0 to 1.</summary>
public sealed class CostSparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<CostSparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<bool> LogScaleProperty =
        AvaloniaProperty.Register<CostSparkline, bool>(nameof(LogScale), true);

    static CostSparkline() => AffectsRender<CostSparkline>(ValuesProperty, LogScaleProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>A cost spans decades; a yield is a fraction, drawn on a fixed 0…1 axis.</summary>
    public bool LogScale
    {
        get => GetValue(LogScaleProperty);
        set => SetValue(LogScaleProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)), null, new Rect(b.Size), 2, 2);
        if (Values is not { Count: > 0 } values) return;

        // A cost of zero (every goal met) sits on the floor of the scale.
        double[] logs;
        double lo, hi;
        if (LogScale)
        {
            double floor = values.Where(v => v > 0).DefaultIfEmpty(1e-12).Min() / 10;
            logs = values.Select(v => Math.Log10(Math.Max(v, floor))).ToArray();
            lo = logs.Min();
            hi = logs.Max();
            if (hi - lo < 1e-9) { hi += 0.5; lo -= 0.5; }
        }
        else
        {
            logs = values.Select(v => Math.Clamp(v, 0, 1)).ToArray();
            (lo, hi) = (0, 1);
        }
        double w = b.Width - 4, h = b.Height - 4;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x3C, 0x8D, 0xD9)), 1.2);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < logs.Length; i++)
            {
                double x = 2 + (logs.Length == 1 ? w : w * i / (logs.Length - 1));
                double y = 2 + h * (hi - logs[i]) / (hi - lo);
                if (i == 0) g.BeginFigure(new Point(x, y), false);
                else g.LineTo(new Point(x, y));
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}

/// <summary>A goal's margin at the best point: green when met, red otherwise, its length the margin.</summary>
public sealed class MarginBar : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<MarginBar, double>(nameof(Fraction));

    public static readonly StyledProperty<bool> IsMetProperty =
        AvaloniaProperty.Register<MarginBar, bool>(nameof(IsMet));

    static MarginBar() => AffectsRender<MarginBar>(FractionProperty, IsMetProperty);

    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public bool IsMet { get => GetValue(IsMetProperty); set => SetValue(IsMetProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        double y = (b.Height - 6) / 2;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), null, new Rect(0, y, b.Width, 6), 2, 2);
        var fill = IsMet ? Color.FromRgb(0x3C, 0xB3, 0x71) : Color.FromRgb(0xD9, 0x4F, 0x4F);
        ctx.DrawRectangle(new SolidColorBrush(fill), null,
            new Rect(0, y, b.Width * Math.Clamp(Fraction, 0, 1), 6), 2, 2);
    }
}

/// <summary>A variable's range with its best value marked, and a mark at a railed end (D17).</summary>
public sealed class RangeMarkBar : Control
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<RangeMarkBar, double>(nameof(Position));

    public static readonly StyledProperty<bool> RailedMinProperty =
        AvaloniaProperty.Register<RangeMarkBar, bool>(nameof(RailedMin));

    public static readonly StyledProperty<bool> RailedMaxProperty =
        AvaloniaProperty.Register<RangeMarkBar, bool>(nameof(RailedMax));

    static RangeMarkBar() => AffectsRender<RangeMarkBar>(PositionProperty, RailedMinProperty, RailedMaxProperty);

    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public bool RailedMin { get => GetValue(RailedMinProperty); set => SetValue(RailedMinProperty, value); }
    public bool RailedMax { get => GetValue(RailedMaxProperty); set => SetValue(RailedMaxProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        double mid = b.Height / 2, w = b.Width - 8;
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), 1),
                     new Point(4, mid), new Point(4 + w, mid));
        var amber = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00));
        if (RailedMin) ctx.DrawRectangle(amber, null, new Rect(2, mid - 5, 3, 10));
        if (RailedMax) ctx.DrawRectangle(amber, null, new Rect(b.Width - 5, mid - 5, 3, 10));
        double x = 4 + w * Math.Clamp(Position, 0, 1);
        ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3C, 0x8D, 0xD9)), null, new Point(x, mid), 4, 4);
    }
}

/// <summary>The goal editor's preview: the expression over the range, the limit drawn over it.</summary>
public sealed class GoalPreviewPlot : Control
{
    public static readonly StyledProperty<GoalPreview?> PreviewProperty =
        AvaloniaProperty.Register<GoalPreviewPlot, GoalPreview?>(nameof(Preview));

    static GoalPreviewPlot() => AffectsRender<GoalPreviewPlot>(PreviewProperty);

    public GoalPreview? Preview { get => GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80)), null, new Rect(b.Size), 3, 3);
        if (Preview is not { X.Length: > 0 } p) return;

        var ys = p.Y.Where(double.IsFinite).ToList();
        foreach (var l in new[] { p.LimitLo, p.LimitHi, p.Upper }) if (l is { } v && double.IsFinite(v)) ys.Add(v);
        if (ys.Count == 0) return;
        double xlo = p.X.Min(), xhi = p.X.Max(), ylo = ys.Min(), yhi = ys.Max();
        if (xhi - xlo <= 0) { xlo -= 1; xhi += 1; }
        if (yhi - ylo <= 0) { ylo -= 1; yhi += 1; }
        double pad = (yhi - ylo) * 0.08;
        ylo -= pad; yhi += pad;
        Point P(double x, double y) => new(6 + (b.Width - 12) * (x - xlo) / (xhi - xlo),
                                           6 + (b.Height - 12) * (yhi - y) / (yhi - ylo));

        // Where the goal FAILS, hatched under the limit lines: above a ≤ limit, below a ≥ one, outside an
        // `in` band, between the edges of an `out` one. An `=` goal fails everywhere but on its line.
        if (p.LimitLo is { } lo0 && p.LimitHi is { } hi0)
        {
            double top = yhi, bottom = ylo;
            switch (p.Type)
            {
                case GoalType.Le:
                    Hatch(ctx, b, P(xlo, top), P(xhi, top), P(xhi, hi0), P(xlo, lo0));
                    break;
                case GoalType.Ge:
                    Hatch(ctx, b, P(xlo, lo0), P(xhi, hi0), P(xhi, bottom), P(xlo, bottom));
                    break;
                case GoalType.In when p.Upper is { } up:
                    Hatch(ctx, b, P(xlo, top), P(xhi, top), P(xhi, up), P(xlo, up));
                    Hatch(ctx, b, P(xlo, lo0), P(xhi, lo0), P(xhi, bottom), P(xlo, bottom));
                    break;
                case GoalType.Out when p.Upper is { } up:
                    Hatch(ctx, b, P(xlo, up), P(xhi, up), P(xhi, lo0), P(xlo, lo0));
                    break;
            }
        }

        var limitPen = new Pen(new SolidColorBrush(Color.FromRgb(0xD9, 0x4F, 0x4F)), 1, new DashStyle([4, 3], 0));
        if (p.LimitLo is { } a && p.LimitHi is { } c) ctx.DrawLine(limitPen, P(xlo, a), P(xhi, c));
        if (p.Upper is { } u) ctx.DrawLine(limitPen, P(xlo, u), P(xhi, u));

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x3C, 0x8D, 0xD9)), 1.4);
        if (p.X.Length == 1)
        {
            ctx.DrawEllipse(pen.Brush, null, P(p.X[0], p.Y[0]), 3, 3);
            return;
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool open = false;
            for (int i = 0; i < p.X.Length; i++)
            {
                if (!double.IsFinite(p.Y[i])) { if (open) g.EndFigure(false); open = false; continue; }
                if (!open) { g.BeginFigure(P(p.X[i], p.Y[i]), false); open = true; }
                else g.LineTo(P(p.X[i], p.Y[i]));
            }
            if (open) g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private static readonly IPen HatchPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0xD9, 0x4F, 0x4F)), 1);

    /// <summary>Diagonal hatching, 6 px apart, inside the quadrilateral a, b, c, d.</summary>
    private static void Hatch(DrawingContext ctx, Rect bounds, Point a, Point b, Point c, Point d)
    {
        var region = new StreamGeometry();
        using (var g = region.Open())
        {
            g.BeginFigure(a, true);
            g.LineTo(b); g.LineTo(c); g.LineTo(d);
            g.EndFigure(true);
        }
        using (ctx.PushGeometryClip(region))
        {
            double w = bounds.Width, h = bounds.Height;
            for (double x = -h; x < w; x += 6) ctx.DrawLine(HatchPen, new Point(x, h), new Point(x + h, 0));
        }
    }
}
