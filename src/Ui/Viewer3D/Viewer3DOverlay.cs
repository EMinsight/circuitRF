// brief-em3d-28 R-em3d28-4e — the 2D chrome over the 3D pane: the AXIS INDICATOR in the lower-left
// corner (the toolbar and the A key turn it off), a SCALE BAR in the layout's display unit, the FDTD
// grid's smallest-cell labels (R-em3d28-3c), and the hover label naming what is under the cursor with
// its material values (R-em3d28-4b).
//
// brief-em3d-43 R-em3d43-5: Vertex mode's DOTS are drawn here too — the hovered candidate a dot, each
// selected vertex a larger one — because a dot that follows the cursor must cost a hover nothing on the
// GPU (gate 5), and the vertices of every object at once would be noise. B's status readout sits top left.
//
// brief-em3d-44 R-em3d44-5: the SNAP MARKER, one glyph per kind — a square on a vertex, a triangle on a
// midpoint, an × on an edge, a circle on a face centre and a small + on the grid — whenever a snap is in
// force, because a snap the user cannot see is one they cannot trust. It is the snap's own screen point,
// resolved from the frame's patch, so it costs a hover nothing either.
//
// brief-em3d-45: the DRAWING's chrome — a tool's rubber band, the points it has fixed, the document's construction
// polylines (never in the solved problem, so never in the scene), and a refused outline's crossing edges — is drawn
// here too, from world segments the editor hands over each frame (IViewer3DEditHost.FillDrawOverlay). A gesture
// in progress is therefore a few projected lines, never a document edit or an upload.
//
// brief-em3d-46: the MOVE GIZMO (three arrows in the axis indicator's colours and three plane squares, a constant
// size on screen, laid out by Render's GizmoGeometry so its hit test is the one tested headlessly), an operation's
// PIVOT cross, and a MEASUREMENT's line with a marker at each point — no extension lines, no arrowheads and no
// text in 3D: the numbers live on the card.
//
// It is drawn by Avalonia, in DIPs, from the camera alone — a redraw per presented frame is a handful of
// lines and a few text runs, and it touches no geometry. It takes no input: the pane under it does.

using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DOverlay : Control
{
    /// <summary>The axis indicator's arm length and its centre's inset from the corner, DIPs.</summary>
    public const double AxisArm = 30, AxisInset = 46;

    /// <summary>The scale bar aims for about this many DIPs.</summary>
    public const double ScaleBarTarget = 110;

    private static readonly IBrush XBrush = new SolidColorBrush(Color.FromRgb(220, 60, 60));
    private static readonly IBrush YBrush = new SolidColorBrush(Color.FromRgb(60, 170, 70));
    private static readonly IBrush ZBrush = new SolidColorBrush(Color.FromRgb(60, 110, 230));

    public Viewer3DOverlay() => IsHitTestVisible = false;

    private Viewer3DViewModel? Vm => DataContext as Viewer3DViewModel;

    public override void Render(DrawingContext ctx)
    {
        if (Vm is not { } vm) return;
        bool dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        IBrush ink = dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(35, 38, 44));
        var inkPen = new Pen(ink, 1.5);
        var cam = vm.View.Camera;
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 10 || h < 10) return;

        if (vm.View.ShowAxisIndicator) AxisIndicator(ctx, cam, new Point(AxisInset, h - AxisInset), ink);
        ScaleBar(ctx, cam, vm, w, h, inkPen, ink);

        foreach (var label in vm.GridLabels)
        {
            var (x, y, visible) = cam.Project(label.At, (float)w, (float)h);
            if (!visible) continue;
            Text(ctx, label.Text, new Point(x + 6, y - 18), ink, 11, dark);
        }

        if (vm.FieldLegendVisible) Legend(ctx, vm, w, ink, dark);

        // Vertex mode: a dot for the candidate, a larger one for each selected vertex.
        var accent = new SolidColorBrush(Color.FromRgb(255, 90, 255));
        var ring = new Pen(dark ? Brushes.Black : Brushes.White, 1.5);
        if (vm.SelectMode == CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Vertex)
        {
            foreach (var item in vm.Selection)
            {
                var (x, y, visible) = cam.Project(item.Point, (float)w, (float)h);
                if (visible) ctx.DrawEllipse(accent, ring, new Point(x, y), 6, 6);
            }
            if (vm.HoveredVertex is { } hv && cam.Project(hv, (float)w, (float)h) is (var hx, var hy, true))
                ctx.DrawEllipse(Brushes.Transparent, new Pen(accent, 2), new Point(hx, hy), 4, 4);
        }
        if (vm.CycleText.Length > 0) Text(ctx, vm.CycleText, new Point(10, 8), ink, 12, dark);

        if (vm.EditHost is { } host)
        {
            _draw.Clear();
            host.FillDrawOverlay(_draw);
            Drawing(ctx, vm, _draw, w, h, dark);
        }

        Measurement(ctx, vm, w, h, dark);
        if (vm.GizmoNow() is { } gizmo) Gizmo(ctx, gizmo, vm.GizmoHover, vm.GizmoActive, dark);

        if (vm.Snap.IsSnap) SnapMarker(ctx, vm.Snap.Kind, new Point(vm.Snap.ScreenX, vm.Snap.ScreenY), dark);

        if (vm.HoverText.Length > 0 && vm.View.CursorX >= 0)
            Text(ctx, vm.HoverText, new Point(vm.View.CursorX + 14, vm.View.CursorY + 14), ink, 12, dark);
    }

    private readonly Viewer3DDrawOverlay _draw = new();

    private static readonly IBrush RubberBrush = new SolidColorBrush(Color.FromRgb(255, 196, 40));
    private static readonly IBrush CrossingBrush = new SolidColorBrush(Color.FromRgb(235, 40, 40));

    /// <summary>brief-em3d-45 — the drawing's chrome: construction dashed, a selected polyline in the selection colour,
    /// the rubber band in amber over a halo, fixed points as dots, a crossing in red.</summary>
    private static void Drawing(DrawingContext ctx, Viewer3DViewModel vm, Viewer3DDrawOverlay d, double w, double h, bool dark)
    {
        var cam = vm.View.Camera;
        var scene = vm.Scene;
        (Point P, bool Ok) Screen(CircuitRF.Engine.Em3d.Point3 p)
        {
            var (x, y, front) = cam.Project(scene.ToLocal(p.X, p.Y, p.Z), (float)w, (float)h);
            return (new Point(x, y), front);
        }
        void Lines(List<CircuitRF.Render.Scene3D.Edit.DrawSegment> segs, Pen pen)
        {
            foreach (var s in segs)
            {
                var (a, oa) = Screen(s.A);
                var (b, ob) = Screen(s.B);
                if (oa && ob) ctx.DrawLine(pen, a, b);
            }
        }
        var construction = new Pen(dark ? new SolidColorBrush(Color.FromArgb(200, 200, 205, 215)) : new SolidColorBrush(Color.FromArgb(200, 70, 75, 85)), 1.2)
        {
            DashStyle = new DashStyle([4, 3], 0),
        };
        Lines(d.Construction, construction);
        // brief-em3d-48 R-em3d48-6b — a cell that resolves to nothing: its last known box, dashed, and its name.
        Lines(d.Missing, new Pen(CrossingBrush, 1.4) { DashStyle = new DashStyle([5, 4], 0) });
        foreach (var (at, text) in d.Labels)
            if (Screen(at) is (var lp, true)) Text(ctx, text, new Point(lp.X + 4, lp.Y - 18), dark ? Brushes.White : Brushes.Black, 11, dark);
        Lines(d.Selected, new Pen(new SolidColorBrush(Color.FromRgb(255, 90, 255)), 2));
        if (d.Rubber.Count > 0)
        {
            Lines(d.Rubber, new Pen(dark ? Brushes.Black : Brushes.White, 3.5, lineCap: PenLineCap.Round));
            Lines(d.Rubber, new Pen(RubberBrush, 1.5, lineCap: PenLineCap.Round));
        }
        foreach (var p in d.Fixed)
            if (Screen(p) is (var sp, true)) ctx.DrawEllipse(RubberBrush, new Pen(dark ? Brushes.Black : Brushes.White, 1), sp, 3, 3);
        Lines(d.Crossing, new Pen(CrossingBrush, 3, lineCap: PenLineCap.Round));
        // brief-em3d-46 R-em3d46-3a — a rotation's pivot: a small cross while the gesture lasts.
        foreach (var p in d.Pivots)
        {
            if (Screen(p) is not (var c, true)) continue;
            foreach (var stroke in new[] { new Pen(dark ? Brushes.Black : Brushes.White, 4), new Pen(RubberBrush, 1.8) })
            {
                ctx.DrawLine(stroke, new Point(c.X - 7, c.Y), new Point(c.X + 7, c.Y));
                ctx.DrawLine(stroke, new Point(c.X, c.Y - 7), new Point(c.X, c.Y + 7));
            }
        }
    }

    private static readonly IBrush MeasureBrush = new SolidColorBrush(Color.FromRgb(40, 200, 220));

    /// <summary>brief-em3d-46 R-em3d46-6e — a thin line between the measured points and a small marker at each.</summary>
    private static void Measurement(DrawingContext ctx, Viewer3DViewModel vm, double w, double h, bool dark)
    {
        if (vm.MeasureP1 is not { } a) return;
        var cam = vm.View.Camera;
        (Point P, bool Ok) Screen(Viewer3DMeasurePoint p)
        {
            var (x, y, front) = cam.Project(vm.Scene.ToLocal(p.X, p.Y, p.Z), (float)w, (float)h);
            return (new Point(x, y), front);
        }
        var halo = new Pen(dark ? Brushes.Black : Brushes.White, 3);
        var pen = new Pen(MeasureBrush, 1.2) { DashStyle = vm.MeasureP2Fixed ? null : new DashStyle([5, 3], 0) };
        var (pa, oa) = Screen(a);
        if (vm.MeasureP2 is { } b && Screen(b) is (var pb, true) && oa)
        {
            ctx.DrawLine(halo, pa, pb);
            ctx.DrawLine(pen, pa, pb);
            ctx.DrawEllipse(MeasureBrush, new Pen(dark ? Brushes.Black : Brushes.White, 1), pb, 3.5, 3.5);
        }
        if (oa) ctx.DrawEllipse(MeasureBrush, new Pen(dark ? Brushes.Black : Brushes.White, 1), pa, 3.5, 3.5);
    }

    private static readonly IBrush GizmoHot = new SolidColorBrush(Color.FromRgb(255, 196, 40));

    /// <summary>brief-em3d-46 R-em3d46-5 — the move gizmo: arrows in the axis colours, plane squares tinted by their
    /// normal; the hovered handle in amber; during a drag only the active handle.</summary>
    private static void Gizmo(DrawingContext ctx, CircuitRF.Render.Scene3D.Edit.GizmoLayout g, CircuitRF.Render.Scene3D.Edit.GizmoHandle hover,
                              CircuitRF.Render.Scene3D.Edit.GizmoHandle active, bool dark)
    {
        IBrush[] axis = [XBrush, YBrush, ZBrush];
        var halo = new Pen(dark ? Brushes.Black : Brushes.White, 5, lineCap: PenLineCap.Round);
        var c = new Point(g.Centre.X, g.Centre.Y);
        bool dragging = active != CircuitRF.Render.Scene3D.Edit.GizmoHandle.None;
        for (int k = 0; k < 3; k++)
        {
            var h = CircuitRF.Render.Scene3D.Edit.GizmoHandle.PlaneX + k;
            if (!g.SquareUsable[k] || (dragging && active != h)) continue;
            var q = g.Squares[k];
            var geo = new StreamGeometry();
            using (var sc = geo.Open())
            {
                sc.BeginFigure(new Point(q[0].X, q[0].Y), true);
                for (int i = 1; i < 4; i++) sc.LineTo(new Point(q[i].X, q[i].Y));
                sc.EndFigure(true);
            }
            bool hot = hover == h || active == h;
            var col = ((SolidColorBrush)axis[k]).Color;
            ctx.DrawGeometry(new SolidColorBrush(hot ? Color.FromArgb(200, 255, 196, 40) : Color.FromArgb(90, col.R, col.G, col.B)),
                             new Pen(hot ? GizmoHot : axis[k], 1), geo);
        }
        for (int k = 0; k < 3; k++)
        {
            var h = CircuitRF.Render.Scene3D.Edit.GizmoHandle.AxisX + k;
            if (!g.AxisUsable[k] || (dragging && active != h)) continue;
            var tip = new Point(g.Tips[k].X, g.Tips[k].Y);
            var from = new Point(c.X + g.Directions[k].X * 6, c.Y + g.Directions[k].Y * 6);
            bool hot = hover == h || active == h;
            var pen = new Pen(hot ? GizmoHot : axis[k], hot ? 3.5 : 2.5, lineCap: PenLineCap.Round);
            ctx.DrawLine(halo, from, tip);
            ctx.DrawLine(pen, from, tip);
            // The arrowhead: a small triangle at the tip.
            var d = g.Directions[k];
            var n = new System.Numerics.Vector2(-d.Y, d.X);
            var head = new StreamGeometry();
            using (var sc = head.Open())
            {
                sc.BeginFigure(new Point(tip.X + d.X * 8, tip.Y + d.Y * 8), true);
                sc.LineTo(new Point(tip.X + n.X * 4, tip.Y + n.Y * 4));
                sc.LineTo(new Point(tip.X - n.X * 4, tip.Y - n.Y * 4));
                sc.EndFigure(true);
            }
            ctx.DrawGeometry(hot ? GizmoHot : axis[k], null, head);
        }
        if (!dragging) ctx.DrawEllipse(dark ? Brushes.WhiteSmoke : Brushes.DimGray, null, c, 3, 3);
    }

    /// <summary>The snap marker's colour: an amber no material or selection uses, over a contrasting halo.</summary>
    private static readonly IBrush SnapBrush = new SolidColorBrush(Color.FromRgb(255, 176, 0));

    /// <summary>brief-em3d-44 R-em3d44-5 — one glyph per kind, drawn twice: a halo, then the amber stroke.</summary>
    private static void SnapMarker(DrawingContext ctx, CircuitRF.Render.Scene3D.Edit.Snap3DKind kind, Point p, bool dark)
    {
        const double r = 6;
        var halo = new Pen(dark ? Brushes.Black : Brushes.White, 4, lineCap: PenLineCap.Round);
        var pen = new Pen(SnapBrush, 2, lineCap: PenLineCap.Round);
        foreach (var stroke in new[] { halo, pen })
        {
            switch (kind)
            {
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Vertex:
                    ctx.DrawRectangle(null, stroke, new Rect(p.X - r, p.Y - r, 2 * r, 2 * r));
                    break;
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Midpoint:
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(p.X, p.Y - r * 1.1), false);
                        c.LineTo(new Point(p.X + r, p.Y + r * 0.8));
                        c.LineTo(new Point(p.X - r, p.Y + r * 0.8));
                        c.EndFigure(true);
                    }
                    ctx.DrawGeometry(null, stroke, g);
                    break;
                }
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Edge:
                    ctx.DrawLine(stroke, new Point(p.X - r, p.Y - r), new Point(p.X + r, p.Y + r));
                    ctx.DrawLine(stroke, new Point(p.X - r, p.Y + r), new Point(p.X + r, p.Y - r));
                    break;
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.FaceCentre:
                    ctx.DrawEllipse(null, stroke, p, r, r);
                    break;
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Grid:
                    ctx.DrawLine(stroke, new Point(p.X - r * 0.6, p.Y), new Point(p.X + r * 0.6, p.Y));
                    ctx.DrawLine(stroke, new Point(p.X, p.Y - r * 0.6), new Point(p.X, p.Y + r * 0.6));
                    break;
            }
        }
    }

    /// <summary>brief-em3d-29 R-em3d29-3c — the field's legend, top right: the quantity, a colour bar with the
    /// range at its ends, the range's percentile, the solution, and the phase when animated.</summary>
    private static void Legend(DrawingContext ctx, Viewer3DViewModel vm, double w, IBrush ink, bool dark)
    {
        var lines = vm.FieldLegendLines();
        if (lines.Count == 0 || vm.FieldScale is not { } range) return;
        const double barW = 220, barH = 12, pad = 8, line = 16;
        var texts = lines.Select(l => new FormattedText(l, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, ink)).ToList();
        double bw = Math.Max(barW, texts.Max(t => t.Width)) + 2 * pad;
        double bh = 2 * pad + barH + line * (lines.Count + 1);
        double x0 = w - bw - 12, y0 = 12;
        ctx.FillRectangle(new SolidColorBrush(dark ? Color.FromArgb(215, 28, 30, 34) : Color.FromArgb(225, 250, 250, 252)),
                          new Rect(x0, y0, bw, bh), 4);
        double y = y0 + pad;
        ctx.DrawText(texts[0], new Point(x0 + pad, y));
        y += line + 2;
        var stops = new GradientStops();
        foreach (var (t, r, g, b) in vm.FieldMap.Stops) stops.Add(new GradientStop(Color.FromRgb(r, g, b), t));
        var bar = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = stops,
        };
        ctx.FillRectangle(bar, new Rect(x0 + pad, y, barW, barH));
        y += barH + 2;
        var lo = new FormattedText(FieldColorScale.G(range.Lo), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
        var hi = new FormattedText(FieldColorScale.G(range.Hi), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
        ctx.DrawText(lo, new Point(x0 + pad, y));
        ctx.DrawText(hi, new Point(x0 + pad + barW - hi.Width, y));
        y += line;
        for (int i = 1; i < texts.Count; i++, y += line) ctx.DrawText(texts[i], new Point(x0 + pad, y));
    }

    private static void AxisIndicator(DrawingContext ctx, in Camera3D cam, Point o, IBrush ink)
    {
        var r = cam.Right; var u = cam.Up; var f = cam.Forward;
        Span<(Vector3 Axis, IBrush Brush, string Name)> axes =
            [(Vector3.UnitX, XBrush, "X"), (Vector3.UnitY, YBrush, "Y"), (Vector3.UnitZ, ZBrush, "Z")];
        // Farthest first, so the axis pointing at the viewer is drawn on top.
        Span<float> depth = [Vector3.Dot(f, axes[0].Axis), Vector3.Dot(f, axes[1].Axis), Vector3.Dot(f, axes[2].Axis)];
        Span<int> order = [0, 1, 2];
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                if (depth[order[j]] > depth[order[i]]) (order[i], order[j]) = (order[j], order[i]);
        ctx.DrawEllipse(null, new Pen(ink, 0.6) { DashStyle = DashStyle.Dot }, o, AxisArm + 8, AxisArm + 8);
        foreach (int k in order)
        {
            var (axis, brush, name) = axes[k];
            var tip = new Point(o.X + AxisArm * Vector3.Dot(r, axis), o.Y - AxisArm * Vector3.Dot(u, axis));
            ctx.DrawLine(new Pen(brush, 2.5, lineCap: PenLineCap.Round), o, tip);
            var ft = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
            var d = tip - o;
            double len = Math.Max(1e-6, Math.Sqrt(d.X * d.X + d.Y * d.Y));
            var at = tip + new Point(d.X / len * 8, d.Y / len * 8) - new Point(ft.Width / 2, ft.Height / 2);
            ctx.DrawText(ft, at);
        }
    }

    private static void ScaleBar(DrawingContext ctx, in Camera3D cam, Viewer3DViewModel vm, double w, double h, Pen pen, IBrush ink)
    {
        double perDip = cam.WorldPerPixel((float)h);
        if (!(perDip > 0) || double.IsInfinity(perDip)) return;
        double target = ScaleBarTarget * perDip, p10 = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double len = new[] { 1.0, 2.0, 5.0, 10.0 }.Select(m => m * p10).Last(v => v <= target * 1.4);
        double dips = len / perDip;
        var right = new Point(w - 20, h - 22);
        var left = new Point(right.X - dips, right.Y);
        ctx.DrawLine(pen, left, right);
        ctx.DrawLine(pen, left + new Point(0, -5), left + new Point(0, 5));
        ctx.DrawLine(pen, right + new Point(0, -5), right + new Point(0, 5));
        var ft = new FormattedText(vm.FormatLength(len), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
        ctx.DrawText(ft, new Point((left.X + right.X - ft.Width) / 2, right.Y - ft.Height - 5));
        if (cam.Projection == Projection3D.Perspective)
        {
            var note = new FormattedText("at the orbit centre", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9, ink);
            ctx.DrawText(note, new Point(right.X - note.Width, right.Y + 4));
        }
    }

    private static void Text(DrawingContext ctx, string text, Point at, IBrush ink, double size, bool dark)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, ink);
        var box = new Rect(at.X - 4, at.Y - 3, ft.Width + 8, ft.Height + 6);
        ctx.FillRectangle(new SolidColorBrush(dark ? Color.FromArgb(200, 30, 32, 36) : Color.FromArgb(215, 250, 250, 252)), box, 3);
        ctx.DrawText(ft, at);
    }
}

/// <summary>
/// A toolbar glyph for a standard view: an isometric cube with the viewed face filled. The three faces
/// the iso view shows (top, front, right) are drawn with the cube's near corner; the three it hides
/// (bottom, back, left) with its far corner, so every face of the family is a distinct picture. No
/// Material icon says "look at this face", which is why this one is drawn (owner, 2026-09-25).
/// </summary>
public sealed class Viewer3DViewGlyph : Control
{
    public static readonly StyledProperty<StandardView3D> FaceProperty =
        AvaloniaProperty.Register<Viewer3DViewGlyph, StandardView3D>(nameof(Face));

    public StandardView3D Face { get => GetValue(FaceProperty); set => SetValue(FaceProperty, value); }

    static Viewer3DViewGlyph() => AffectsRender<Viewer3DViewGlyph>(FaceProperty, TextElement.ForegroundProperty);

    public Viewer3DViewGlyph() { Width = 16; Height = 16; }

    // The hexagon of an isometric cube in a 24-unit box; C is where the near (and far) corner lands.
    private static readonly Point T = new(12, 2), UR = new(21, 7), LR = new(21, 17), B = new(12, 22), LL = new(3, 17), UL = new(3, 7), C = new(12, 12);

    public override void Render(DrawingContext ctx)
    {
        var fg = TextElement.GetForeground(this) ?? Brushes.Gray;
        double s = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        Point P(Point p) => new(p.X * s, p.Y * s);
        Point[] face = Face switch
        {
            StandardView3D.Top    => [C, UL, T, UR],
            StandardView3D.Front  => [C, UL, LL, B],
            StandardView3D.Right  => [C, UR, LR, B],
            StandardView3D.Bottom => [C, LL, B, LR],
            StandardView3D.Back   => [C, T, UR, LR],
            StandardView3D.Left   => [C, T, UL, LL],
            _                     => [],
        };
        bool hidden = Face is StandardView3D.Bottom or StandardView3D.Back or StandardView3D.Left;
        if (face.Length > 0)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(P(face[0]), true);
                for (int i = 1; i < face.Length; i++) c.LineTo(P(face[i]));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(fg, null, g);
        }
        var pen = new Pen(fg, 1.6 * s, lineJoin: PenLineJoin.Round);
        Point[] hex = [T, UR, LR, B, LL, UL];
        for (int i = 0; i < 6; i++) ctx.DrawLine(pen, P(hex[i]), P(hex[(i + 1) % 6]));
        // The three edges from the near corner (visible faces) or the far corner (hidden faces).
        var dashed = new Pen(fg, 1.2 * s) { DashStyle = hidden ? new DashStyle([1.5, 1.5], 0) : null };
        foreach (var e in hidden ? new[] { T, LL, LR } : [UL, UR, B])
            ctx.DrawLine(dashed, P(C), P(e));
    }
}
