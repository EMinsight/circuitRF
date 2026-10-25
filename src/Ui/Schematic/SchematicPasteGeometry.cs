namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Where a pasted schematic fragment lands.
///
/// Paste used to be paste-IN-PLACE: the fragment kept its source coordinates, so copy-paste inside
/// one schematic dropped every object EXACTLY on top of its original. That is not merely untidy —
/// it is unusable: every pasted wire endpoint then coincides with an unselected original port, which
/// pins the pasted wires (they re-route instead of moving) and sprouts an auto-wire stub per pin the
/// moment the selection is dragged. Landing the fragment where the user is looking, and never
/// exactly on existing content, is what makes the pasted copy draggable at all.
///
/// <b>With the pointer over the canvas, a paste does not land at all until the user says where</b>
/// (brief DFR26-5 R-dfr26-5-1): it arms a ghost that follows the pointer and a click places it — the
/// layout editor's pattern (<c>SchematicViewModel.PasteGhost.cs</c>). This class then supplies only
/// the on-grid delta and the overlap nudge. Without a pointer on the canvas, the rule, in world
/// coordinates:
/// <list type="bullet">
/// <item>No viewport known (headless, or the canvas has never been laid out) → paste in place.</item>
/// <item>Otherwise → translate so the fragment's bbox centre lands on the viewport centre. A fragment
///       already in view used to stay where it was, offset by ONE connection-grid step — which put
///       its pins straight onto the original's wires and pins and shorted the nets (designer
///       feedback round 26, reported twice).</item>
/// </list>
/// The delta is always a whole multiple of the connection grid P, so every pasted connection point
/// stays exactly on grid (P is itself a whole multiple of the fine author grid p, so canvas objects
/// stay on their grid too). A final pass nudges the fragment diagonally if it would still land with
/// a component exactly on an existing component's origin.
/// </summary>
public static class SchematicPasteGeometry
{
    /// <summary>World-coordinate rectangle currently visible on the canvas.</summary>
    public readonly record struct ViewRect(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double CenterX => (MinX + MaxX) / 2.0;
        public double CenterY => (MinY + MaxY) / 2.0;
        public bool Contains(double minX, double minY, double maxX, double maxY) =>
            minX >= MinX && maxX <= MaxX && minY >= MinY && maxY <= MaxY;
    }

    /// <summary>
    /// Bounding box of a fragment, from component origins, wire vertices and canvas-object boxes.
    /// Returns null when the fragment has no geometry at all.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY)? BoundingBox(
        IReadOnlyList<EditableComponent> comps,
        IReadOnlyList<EditableWire> wires,
        IReadOnlyList<EditableCanvasObject> cobjs)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        void Add(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var c in comps) Add(c.X, c.Y);
        foreach (var w in wires)
            foreach (var (x, y) in w.Points) Add(x, y);
        foreach (var o in cobjs)
        {
            var bb = o.GetBoundingBox();
            Add(bb.MinX, bb.MinY);
            Add(bb.MaxX, bb.MaxY);
        }

        return double.IsInfinity(minX) ? null : (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Delta to apply to a pasted fragment. <paramref name="occupied"/> reports whether a component
    /// origin already exists at (x, y) in the destination model — used only to break an exact
    /// overlap; pass null to skip that pass.
    /// </summary>
    public static (double Dx, double Dy) Offset(
        IReadOnlyList<EditableComponent> comps,
        IReadOnlyList<EditableWire> wires,
        IReadOnlyList<EditableCanvasObject> cobjs,
        ViewRect? view,
        double gridP,
        Func<double, double, bool>? occupied = null)
    {
        if (BoundingBox(comps, wires, cobjs) is not { } bb) return (0, 0);
        double p = gridP > 0 ? gridP : 100.0;

        double dx, dy;
        if (view is not { } v)
        {
            dx = dy = 0;                                    // headless / no canvas — paste in place
        }
        else
        {
            double fx = (bb.MinX + bb.MaxX) / 2.0;
            double fy = (bb.MinY + bb.MaxY) / 2.0;
            dx = Math.Round((v.CenterX - fx) / p) * p;
            dy = Math.Round((v.CenterY - fy) / p) * p;
        }

        return Nudge(comps, dx, dy, p, occupied);
    }

    /// <summary>
    /// The delta (<paramref name="dx"/>, <paramref name="dy"/>) stepped diagonally by
    /// <paramref name="gridP"/> until no component of the fragment lands exactly on an existing
    /// component's origin (<paramref name="occupied"/>; null skips the pass). That is the state in
    /// which a dragged selection pins itself to the content underneath it.
    /// </summary>
    public static (double Dx, double Dy) Nudge(
        IReadOnlyList<EditableComponent> comps, double dx, double dy, double gridP,
        Func<double, double, bool>? occupied)
    {
        double p = gridP > 0 ? gridP : 100.0;
        if (occupied is null || comps.Count == 0) return (dx, dy);
        for (int step = 0; step < 20; step++)
        {
            bool clash = false;
            foreach (var c in comps)
                if (occupied(c.X + dx, c.Y + dy)) { clash = true; break; }
            if (!clash) break;
            dx += p;
            dy += p;
        }
        return (dx, dy);
    }

    /// <summary>Translates a fragment in place by (dx, dy). A free-standing net label moves with it — it names
    /// the pin it sits on, so left behind it would name the ORIGINAL's pin; an anchored one follows its wire.</summary>
    public static void Translate(
        IReadOnlyList<EditableComponent> comps,
        IReadOnlyList<EditableWire> wires,
        IReadOnlyList<EditableCanvasObject> cobjs,
        double dx, double dy,
        IReadOnlyList<EditableNetLabel>? netLabels = null)
    {
        if (dx == 0 && dy == 0) return;

        foreach (var c in comps) { c.X += dx; c.Y += dy; }
        foreach (var w in wires)
            for (int i = 0; i < w.Points.Count; i++)
                w.Points[i] = (w.Points[i].X + dx, w.Points[i].Y + dy);
        foreach (var o in cobjs) { o.X += dx; o.Y += dy; }
        foreach (var n in netLabels ?? [])
            if (!n.IsAnchored) { n.X += dx; n.Y += dy; }
    }
}
