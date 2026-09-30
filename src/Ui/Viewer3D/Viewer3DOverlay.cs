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
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DOverlay : Control
{
    /// <summary>The axis indicator's arm length and its centre's inset from the corner, DIPs.</summary>
    public const double AxisArm = 30, AxisInset = 46;

    /// <summary>Half an axis letter (11 px text), and the reach of a click on the indicator: out to the far edge of a
    /// letter drawn on the ring.</summary>
    internal const double AxisLetterHalf = 7, AxisHitRadius = AxisArm + 8 + AxisLetterHalf;

    /// <summary>The scale bar aims for about this many DIPs.</summary>
    public const double ScaleBarTarget = 110;

    private static readonly IBrush XBrush = new SolidColorBrush(Color.FromRgb(220, 60, 60));
    private static readonly IBrush YBrush = new SolidColorBrush(Color.FromRgb(60, 170, 70));
    private static readonly IBrush ZBrush = new SolidColorBrush(Color.FromRgb(60, 110, 230));

    /// <summary>
    /// 3D editor bugs round 5 — CLIPPED to its own bounds, which are the pane's. Everything here is projected from the
    /// camera, and a projection has no reason to land inside the pane: a selected wire or edge running off screen, or the
    /// gizmo of an object panned half out of view, projects to points beyond the edge. An Avalonia control draws wherever
    /// its geometry goes unless it clips, so those strokes were painted over the object tree and the toolbar. The pane
    /// under it already clipped; the overlay never did.
    /// </summary>
    public Viewer3DOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    private Viewer3DViewModel? Vm => DataContext as Viewer3DViewModel;

    public override void Render(DrawingContext ctx)
    {
        if (Vm is { } vm) Paint(ctx, vm, Bounds.Width, Bounds.Height, _draw, picture: false);
    }

    /// <summary>
    /// The overlay, <paramref name="w"/> × <paramref name="h"/> DIPs. <paramref name="picture"/> paints it for a PICTURE of
    /// the view (3D editor bugs round 6: Copy and Export Picture… carried the GPU's pixels alone, so the measurement, the
    /// axis indicator, the scale bar and an Edge- or Vertex-mode selection were missing): what the view SHOWS — those, the
    /// selection's outline and the measurement's numbers (the card is a control, not overlay) — and none of what the POINTER
    /// is doing (the hover label and ring, the snap marker, the move gizmo's handles, the cycle prompt). The field's legend
    /// is left to FieldPicture, which paints it as the export options say.
    /// </summary>
    internal static void Paint(DrawingContext ctx, Viewer3DViewModel vm, double w, double h, Viewer3DDrawOverlay draw, bool picture)
    {
        bool dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        IBrush ink = dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(35, 38, 44));
        var inkPen = new Pen(ink, 1.5);
        var cam = vm.View.Camera;
        if (w < 10 || h < 10) return;

        if (vm.View.ShowAxisIndicator) AxisIndicator(ctx, cam, AxisIndicatorCentre(h), ink);
        if (vm.ShowScaleLegend) ScaleBar(ctx, cam, vm, w, h, inkPen, ink);

        foreach (var label in vm.GridLabels)
        {
            var (x, y, visible) = cam.Project(label.At, (float)w, (float)h);
            if (!visible) continue;
            Text(ctx, label.Text, new Point(x + 6, y - 18), ink, 11, dark);
        }

        if (!picture && vm.FieldLegendVisible) Legends(ctx, vm, w, h, ink, dark);

        // brief-em3d-75 R-em3d75-4c — the hot spot: a ring at the maximum of what is drawn, its temperature and its object.
        // brief-em3d-96 — one per drawn temperature plot.
        if (vm.ShowField)
            foreach (var (hot, label) in vm.HotSpots)
            {
                if (cam.Project(hot.At, (float)w, (float)h) is not (var hx0, var hy0, true)) continue;
                var at = new Point(hx0, hy0);
                ctx.DrawEllipse(null, new Pen(dark ? Brushes.Black : Brushes.White, 4), at, 7, 7);
                ctx.DrawEllipse(null, new Pen(HotBrush, 2), at, 7, 7);
                ctx.DrawLine(new Pen(HotBrush, 1.5), new Point(at.X - 11, at.Y), new Point(at.X - 4, at.Y));
                ctx.DrawLine(new Pen(HotBrush, 1.5), new Point(at.X + 4, at.Y), new Point(at.X + 11, at.Y));
                Text(ctx, label, new Point(at.X + 12, at.Y - 20), ink, 12, dark);
            }

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
            if (!picture && vm.HoveredVertex is { } hv && cam.Project(hv, (float)w, (float)h) is (var hx, var hy, true))
                ctx.DrawEllipse(Brushes.Transparent, new Pen(accent, 2), new Point(hx, hy), 4, 4);
        }
        // brief-em3d-67 R-em3d67-3c — Edge mode: each selected edge, then the hovered one, as a thick polyline projected from
        // the camera each frame (no GPU buffer changes when the hover moves).
        if (vm.SelectMode == CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Edge)
        {
            var halo = new Pen(dark ? Brushes.Black : Brushes.White, 5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var selected = new Pen(accent, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            foreach (var item in vm.Selection)
                if (vm.EdgePoints(item) is { } pts) Polyline(ctx, vm, pts, w, h, halo, selected);
            if (!picture && vm.HoveredItem is { IsEdge: true } he && !vm.Selection.Contains(he) && vm.EdgePoints(he) is { } hp)
                Polyline(ctx, vm, hp, w, h, halo, new Pen(RubberBrush, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
        }
        if (picture) SelectionOutline(ctx, vm, w, h);
        if (!picture && vm.CycleText.Length > 0) Text(ctx, vm.CycleText, new Point(10, 8), ink, 12, dark);

        if (vm.EditHost is { } host)
        {
            draw.Clear();
            host.FillDrawOverlay(draw);
            Drawing(ctx, vm, draw, w, h, dark);
        }

        Measurement(ctx, vm, w, h, dark);
        if (picture) MeasureTable(ctx, vm, h, ink, dark);
        if (picture) return;
        if (vm.GizmoNow() is { } gizmo) Gizmo(ctx, gizmo, vm.GizmoHover, vm.GizmoActive, dark);

        if (vm.Snap.IsSnap) SnapMarker(ctx, vm.Snap.Kind, new Point(vm.Snap.ScreenX, vm.Snap.ScreenY), dark, SnapColour(vm.Scene, vm.Snap));

        if (vm.HoverText.Length > 0 && vm.View.CursorX >= 0)
            Text(ctx, vm.HoverText, new Point(vm.View.CursorX + 14, vm.View.CursorY + 14), ink, 12, dark);
    }

    /// <summary>fs_edge's colour: what the GPU outlines a selection in.</summary>
    private static readonly Color OutlineColour = Color.FromRgb(255, 89, 255);

    /// <summary>
    /// 3D editor bugs round 6 — an Object- or Face-mode selection's outline, for a picture. On screen the GPU draws it
    /// (Scene3DFramePlan's edge passes, a pixel apart) two DEVICE pixels wide, whatever the picture's multiple: at Copy's 4×
    /// that is half a pixel of the window, and in a picture pasted at the window's size it had all but vanished. Here it is
    /// the same lines at the screen's width in DIPs, so it scales with the picture. As fs_edge draws them: no depth test,
    /// nothing on the clipped side, and in Face mode only the edges of a selected face.
    /// </summary>
    private static void SelectionOutline(DrawingContext ctx, Viewer3DViewModel vm, double w, double h)
    {
        var mode = vm.SelectMode;
        if (mode is not (CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Object or CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Face)) return;
        var view = vm.View;
        var scene = vm.Scene;
        if (view.Selection.Length == 0 || scene.EdgeBatches.Length == 0) return;
        var cam = view.Camera;
        var pen = new Pen(new SolidColorBrush(OutlineColour), 2, lineCap: PenLineCap.Round);
        var clip = view.Clip.Enabled ? view.Clip.Equation : (Vector4?)null;
        int limit = Math.Min(view.Selection.Length, Scene3DFramePlan.SelectionLimit);
        var done = new HashSet<uint>();
        for (int k = 0; k < limit; k++)
        {
            uint id = view.Selection[k].Object;
            if (!view.IsDrawn(id) || !done.Add(id)) continue;
            foreach (var eb in scene.EdgeBatches)
            {
                if (eb.ObjectId != id) continue;
                var offset = eb.Element >= 0 && eb.Element < scene.Elements.Length ? scene.Elements[eb.Element].Offset : Vector3.Zero;
                int end = Math.Min(eb.FirstVertex + eb.VertexCount, scene.LineVertices.Length);
                for (int i = eb.FirstVertex; i + 1 < end; i += 2)
                {
                    var va = scene.LineVertices[i];
                    if (mode == CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Face && !OnSelectedFace(view, id, va.Face)) continue;
                    var vb = scene.LineVertices[i + 1];
                    var a = new Vector3(va.X, va.Y, va.Z) + offset;
                    var b = new Vector3(vb.X, vb.Y, vb.Z) + offset;
                    if (clip is { } c && !ClipSegment(c, ref a, ref b)) continue;
                    var (ax, ay, fa) = cam.Project(a, (float)w, (float)h);
                    var (bx, by, fb) = cam.Project(b, (float)w, (float)h);
                    if (fa && fb) ctx.DrawLine(pen, new Point(ax, ay), new Point(bx, by));
                }
            }
        }
    }

    /// <summary>fs_edge's Face-mode test: an edge vertex carries its two faces packed, low and high 16 bits.</summary>
    private static bool OnSelectedFace(Viewer3DViewState view, uint id, uint packed)
    {
        foreach (var item in view.Selection)
            if (item.Object == id && item.Face >= 0 && ((packed & 0xFFFF) == (uint)item.Face || (packed >> 16) == (uint)item.Face)) return true;
        return false;
    }

    /// <summary>The part of segment a–b the clip plane keeps (fs_edge discards where dot(n, p) + d &gt; 0); false for none.</summary>
    internal static bool ClipSegment(Vector4 plane, ref Vector3 a, ref Vector3 b)
    {
        var n = new Vector3(plane.X, plane.Y, plane.Z);
        float da = Vector3.Dot(n, a) + plane.W, db = Vector3.Dot(n, b) + plane.W;
        if (da > 0 && db > 0) return false;
        if (da <= 0 && db <= 0) return true;
        var cut = a + (b - a) * (da / (da - db));
        if (da > 0) a = cut; else b = cut;
        return true;
    }

    /// <summary>
    /// 3D editor bugs round 6 — the Measure card's numbers, for a picture, where the card sits (bottom left, above the axis
    /// indicator): the card is a control over the pane, not overlay, so a picture of the view never had it.
    /// </summary>
    private static void MeasureTable(DrawingContext ctx, Viewer3DViewModel vm, double h, IBrush ink, bool dark)
    {
        if (vm.MeasureReadout is not { } r) return;
        const double pad = 8, label = 64, col = 100, line = 17;
        var rows = new List<string[]> { new[] { "Measure", "x", "y", "z" } };
        foreach (var row in r.Rows) rows.Add([row.Heading, row.X.Text, row.Y.Text, row.Z.Text]);
        if (r.Distance is { } d) rows.Add([r.DistanceHeading, d.Text]);
        double bw = 2 * pad + label + 3 * col, bh = 2 * pad + line * rows.Count;
        double x0 = 12, y0 = h - 104 - bh;
        ctx.DrawRectangle(new SolidColorBrush(dark ? Color.FromArgb(242, 40, 42, 46) : Color.FromArgb(242, 252, 252, 253)),
                          new Pen(new SolidColorBrush(dark ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(70, 0, 0, 0)), 1),
                          new Rect(x0, y0, bw, bh), 4, 4);
        for (int i = 0; i < rows.Count; i++)
        {
            double y = y0 + pad + i * line;
            for (int c = 0; c < rows[i].Length; c++)
            {
                bool heading = i == 0;
                var ft = new FormattedText(rows[i][c], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                           heading && c == 0 ? new Typeface(FontFamily.Default, weight: FontWeight.SemiBold) : Typeface.Default,
                                           heading && c > 0 ? 11 : 12, ink);
                double x = x0 + pad + (c == 0 ? 0 : label + (c - 1) * col);
                using (ctx.PushOpacity(heading && c > 0 ? 0.7 : 1)) ctx.DrawText(ft, new Point(x, y));
            }
        }
    }

    /// <summary>
    /// 3D editor bugs round 6 — the overlay for a picture <paramref name="pixelW"/> × <paramref name="pixelH"/> of a pane
    /// <paramref name="dipW"/> DIPs wide, painted at the picture's own resolution (its DPI is the picture's pixels per
    /// DIP), so a line is as thick and a label as large, relative to the view, as on screen. Premultiplied, rows top first,
    /// in the platform's byte order (<see cref="FieldPictureLayer.Bgra"/>), for FieldPicture to lay over the GPU's pixels.
    /// </summary>
    public static FieldPictureLayer PictureLayer(Viewer3DViewModel vm, double dipW, int pixelW, int pixelH)
    {
        double k = pixelW / Math.Max(1, dipW);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Avalonia.Vector(96 * k, 96 * k));
        using (var ctx = bitmap.CreateDrawingContext())
            Paint(ctx, vm, pixelW / k, pixelH / k, new Viewer3DDrawOverlay(), picture: true);
        var bytes = new byte[pixelW * pixelH * 4];
        unsafe
        {
            fixed (byte* p = bytes) bitmap.CopyPixels(new PixelRect(0, 0, pixelW, pixelH), (nint)p, bytes.Length, pixelW * 4);
        }
        return new FieldPictureLayer(bytes, bitmap.Format != Avalonia.Platform.PixelFormats.Rgba8888);
    }

    private readonly Viewer3DDrawOverlay _draw = new();

    private static readonly IBrush HotBrush = new SolidColorBrush(Color.FromRgb(255, 70, 40));
    private static readonly IBrush HeatBrush = new SolidColorBrush(Color.FromRgb(240, 120, 40));
    private static readonly IBrush ProbeBrush = new SolidColorBrush(Color.FromRgb(40, 200, 220));
    private static readonly IBrush RegionBrush = new SolidColorBrush(Color.FromRgb(170, 120, 230));
    /// <summary>brief-em3d-90 — a thermal symmetry plane: the colour the air box hatches a symmetry face in (Scene3DBuilder).</summary>
    private static readonly IBrush SymmetryBrush = new SolidColorBrush(Color.FromRgb(160, 80, 210));

    /// <summary>brief-em3d-67 — a world polyline, projected, drawn twice: a halo, then the stroke.</summary>
    private static void Polyline(DrawingContext ctx, Viewer3DViewModel vm, IReadOnlyList<CircuitRF.Engine.Em3d.Point3> pts, double w, double h,
                                 Pen halo, Pen stroke)
    {
        var cam = vm.View.Camera;
        var screen = new List<Point>(pts.Count);
        foreach (var p in pts)
        {
            var (x, y, front) = cam.Project(vm.Scene.ToLocal(p.X, p.Y, p.Z), (float)w, (float)h);
            if (!front) return;
            screen.Add(new Point(x, y));
        }
        if (screen.Count < 2) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(screen[0], false);
            for (int i = 1; i < screen.Count; i++) c.LineTo(screen[i]);
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, halo, g);
        ctx.DrawGeometry(null, stroke, g);
    }

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
        // brief-em3d-75 R-em3d75-1b — the thermal places: never solids, never faces to snap onto; drawn here only.
        Lines(d.HeatSources, new Pen(HeatBrush, 1.4));
        Lines(d.MeshRegions, new Pen(RegionBrush, 1.3) { DashStyle = new DashStyle([6, 4], 0) });
        Lines(d.Probes, new Pen(ProbeBrush, 1.6));
        Lines(d.SymmetryPlanes, new Pen(SymmetryBrush, 1.1));
        foreach (var p in d.ProbeMarks)
            if (Screen(p) is (var pp, true))
            {
                var diamond = new StreamGeometry();
                using (var g = diamond.Open())
                {
                    g.BeginFigure(new Point(pp.X, pp.Y - 5), true);
                    g.LineTo(new Point(pp.X + 5, pp.Y)); g.LineTo(new Point(pp.X, pp.Y + 5)); g.LineTo(new Point(pp.X - 5, pp.Y));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(ProbeBrush, new Pen(dark ? Brushes.Black : Brushes.White, 1), diamond);
            }
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

    /// <summary>The snap marker's colour when there is no material to take one from: an amber no material or selection
    /// uses, over a contrasting halo.</summary>
    internal static readonly Color SnapAmber = Color.FromRgb(255, 176, 0);

    /// <summary>
    /// 3D editor bugs round 2 — the marker is drawn in the colour of what it snaps to, as the layout editor draws its
    /// marker in the snapped layer's colour: the object's own material colour, opaque (a translucent dielectric's alpha
    /// would make the glyph faint). Amber for the grid, for an object with no material (drawn wireframe), and for
    /// anything that is not a material's object — a port, a box face.
    /// </summary>
    internal static Color SnapColour(Scene3DModel scene, CircuitRF.Render.Scene3D.Edit.Snap3DResult snap)
    {
        if (snap.Kind == CircuitRF.Render.Scene3D.Edit.Snap3DKind.Grid || scene.Object(snap.Object) is not { } o
            || o.Wireframe || o.Material is not { Length: > 0 })
            return SnapAmber;
        uint c = o.Rgba;
        return Color.FromRgb((byte)c, (byte)(c >> 8), (byte)(c >> 16));
    }

    /// <summary>brief-em3d-44 R-em3d44-5 — one glyph per kind, drawn twice: a halo, then the coloured stroke.</summary>
    private static void SnapMarker(DrawingContext ctx, CircuitRF.Render.Scene3D.Edit.Snap3DKind kind, Point p, bool dark, Color colour)
    {
        const double r = 6;
        var halo = new Pen(dark ? Brushes.Black : Brushes.White, 4, lineCap: PenLineCap.Round);
        var pen = new Pen(new SolidColorBrush(colour), 2, lineCap: PenLineCap.Round);
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
                // brief-em3d-67 R-em3d67-3e — a circle's or an arc's centre: the face-centre marker with a dot in it.
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Centre:
                    ctx.DrawEllipse(null, stroke, p, r, r);
                    ctx.DrawEllipse(stroke.Brush, null, p, 1.8, 1.8);
                    break;
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Grid:
                    ctx.DrawLine(stroke, new Point(p.X - r * 0.6, p.Y), new Point(p.X + r * 0.6, p.Y));
                    ctx.DrawLine(stroke, new Point(p.X, p.Y - r * 0.6), new Point(p.X, p.Y + r * 0.6));
                    break;
            }
        }
    }

    /// <summary>brief-em3d-29 R-em3d29-3c — the field's legend, top right: the quantity, a colour bar with the
    /// range at its ends, the range's percentile, the solution, and the phase when animated. brief-em3d-96 D3 — one per colour
    /// range, stacked down the right 8 DIPs apart, all one HELD width (the column's widest, so a drag moves none of them
    /// sideways); one that would run past the bottom is not drawn, and the last one drawn ends with "+N more".</summary>
    private static void Legends(DrawingContext ctx, Viewer3DViewModel vm, double w, double h, IBrush ink, bool dark)
    {
        var groups = vm.FieldLegendGroups;
        if (groups.Count == 0) return;
        const double barW = 220, barH = 12, pad = 8, line = 16, gap = 8, top = 12;
        var texts = groups.Select(g => g.Lines.Select(l => new FormattedText(l, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                                                              Typeface.Default, 12, ink)).ToList()).ToList();
        double widest = texts.Max(t => t.Count == 0 ? 0 : t.Max(x => x.Width));
        double bw = vm.HeldLegendWidth(Math.Max(barW, widest)) + 2 * pad;
        double Height(int k, bool more) => 2 * pad + barH + line * (texts[k].Count + 1 + (more ? 1 : 0));
        int shown = FieldPicture.StackCount([.. Enumerable.Range(0, groups.Count).Select(k => (float)Height(k, false))], (float)line,
                                            (float)gap, (float)top, (float)(h - top));
        var fill = new SolidColorBrush(dark ? Color.FromArgb(215, 28, 30, 34) : Color.FromArgb(225, 250, 250, 252));
        double x0 = w - bw - 12, y0 = top;
        for (int k = 0; k < shown; k++)
        {
            var g = groups[k];
            bool more = k == shown - 1 && shown < groups.Count;
            double bh = Height(k, more);
            ctx.FillRectangle(fill, new Rect(x0, y0, bw, bh), 4);
            double y = y0 + pad;
            if (texts[k].Count > 0) ctx.DrawText(texts[k][0], new Point(x0 + pad, y));
            y += line + 2;
            var stops = new GradientStops();
            foreach (var (t, r, gr, b) in g.Map.Stops) stops.Add(new GradientStop(Color.FromRgb(r, gr, b), t));
            var bar = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = stops,
            };
            ctx.FillRectangle(bar, new Rect(x0 + pad, y, barW, barH));
            y += barH + 2;
            var lo = new FormattedText(FieldColorScale.G(g.Scale.Lo), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
            var hi = new FormattedText(FieldColorScale.G(g.Scale.Hi), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
            ctx.DrawText(lo, new Point(x0 + pad, y));
            ctx.DrawText(hi, new Point(x0 + pad + barW - hi.Width, y));
            y += line;
            for (int i = 1; i < texts[k].Count; i++, y += line) ctx.DrawText(texts[k][i], new Point(x0 + pad, y));
            if (more)
                ctx.DrawText(new FormattedText(FieldPicture.MoreLine(groups.Count - shown), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                               Typeface.Default, 12, ink), new Point(x0 + pad, y));
            y0 += bh + gap;
        }
    }

    /// <summary>The axis indicator's centre in a view <paramref name="height"/> DIPs tall.</summary>
    public static Point AxisIndicatorCentre(double height) => new(AxisInset, height - AxisInset);

    /// <summary>
    /// 3D editor bugs round 2 — what a double-click on the axis indicator asks for, or null when <paramref name="p"/> is
    /// not on it. An axis (its arm or its letter) looks straight down that axis, the convention of the CAD tools' view
    /// triads: Z the Top view, Y the Front (the XZ plane — Front looks along +y), X the Right (the YZ plane). Asked again
    /// from that view it turns to the opposite one (Bottom, Back, Left) — an axis seen end-on is the dot at the ring's
    /// centre, so Top, double-clicked there, turns to Bottom. Anywhere else inside the ring is Isometric.
    /// <para>3D editor bugs round 3 — an arm in the view plane is drawn full length, its letter centred on the ring
    /// (AxisArm + 8), so half the letter lay outside the old reach (AxisArm + 10 from the centre, AxisArm + 12 along the
    /// arm): from the Right view a double-click on the Z letter asked for nothing. The reach now covers the whole letter.</para>
    /// </summary>
    public static StandardView3D? AxisIndicatorHit(in Camera3D cam, Point o, Point p)
    {
        var d = p - o;
        if (d.X * d.X + d.Y * d.Y > AxisHitRadius * AxisHitRadius) return null;
        var r = cam.Right; var u = cam.Up; var f = cam.Forward;
        Span<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        int best = -1;
        double bestDist = AxisLetterHalf, bestDepth = double.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            var dir = new Point(Vector3.Dot(r, axes[k]), -Vector3.Dot(u, axes[k]));
            double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            double dist;
            if (len < 0.2) dist = Math.Sqrt(d.X * d.X + d.Y * d.Y);   // seen end-on: a dot at the centre
            else
            {
                // Along the arm and out to its letter (drawn 8 DIPs past the tip): the distance to that segment. The
                // first 6 DIPs are left to an axis seen end-on, whose dot is there.
                double reach = AxisArm * len + 8 + AxisLetterHalf;
                var unit = new Point(dir.X / len, dir.Y / len);
                double t = Math.Clamp(d.X * unit.X + d.Y * unit.Y, 6, reach);
                double dx = d.X - unit.X * t, dy = d.Y - unit.Y * t;
                dist = Math.Sqrt(dx * dx + dy * dy);
            }
            double depth = Vector3.Dot(f, axes[k]);            // nearer the viewer wins a tie, as it is drawn on top
            if (dist < bestDist || (dist == bestDist && depth < bestDepth)) { best = k; bestDist = dist; bestDepth = depth; }
        }
        if (best < 0) return StandardView3D.Iso;
        // Already looking down this axis: the opposite view.
        var back = cam.Back;
        return best switch
        {
            0 => Vector3.Dot(back, Vector3.UnitX) > 0.999f ? StandardView3D.Left : StandardView3D.Right,
            1 => Vector3.Dot(back, -Vector3.UnitY) > 0.999f ? StandardView3D.Back : StandardView3D.Front,
            _ => Vector3.Dot(back, Vector3.UnitZ) > 0.999f ? StandardView3D.Bottom : StandardView3D.Top,
        };
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

    /// <summary>The scale bar's length, metres: <see cref="Em3dDrawingSheet.ScaleBarLength"/>, the rule `render --scale-bar` shares
    /// (brief-em3d-88).</summary>
    internal static double ScaleBarLength(double targetMetres, LayoutUnit unit, int dbuPerMicron)
        => Em3dDrawingSheet.ScaleBarLength(targetMetres, unit, dbuPerMicron);

    private static void ScaleBar(DrawingContext ctx, in Camera3D cam, Viewer3DViewModel vm, double w, double h, Pen pen, IBrush ink)
    {
        double perDip = cam.WorldPerPixel((float)h);
        if (!(perDip > 0) || double.IsInfinity(perDip)) return;
        double len = ScaleBarLength(ScaleBarTarget * perDip, vm.MeasureUnit, vm.MeasureDbuPerMicron);
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
