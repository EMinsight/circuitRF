// Body-drag snapping (brief-dfr22-3-layout-round-trip.md R-dfr22-3-1/2). Before this, only a drag started ON a snap
// marker snapped; a line dragged next to a tee by its body landed a hair apart, the gap was invisible until zoomed
// in, and recognition read three open ends. A body drag now pulls the selection so that a dragged PIN lands on a
// stationary pin or port, else a dragged EDGE lands flush on a stationary parallel edge facing it.

using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    /// <summary>Above this many outline edges a selection snaps by its pins only (R-dfr22-3-2).</summary>
    internal const int BodySnapMaxEdges = 64;

    /// <summary>At most this many of a selection's pins are tried per pointer move.</summary>
    internal const int BodySnapMaxPins = 32;

    /// <summary>Stationary edges read per pointer move, at most.</summary>
    private const int BodySnapMaxStationaryEdges = 4096;

    /// <summary>Parallel within ~0.06°, and facing: outward normals within that of opposite.</summary>
    private const double BodySnapParallelCos = 0.999999;

    /// <summary>What the selection carries into a body drag, read once at its first move: its pins and outline edges
    /// at rest, in world coordinates.</summary>
    private sealed record BodySnapSource(
        IReadOnlyList<(long X, long Y)> Pins, IReadOnlyList<SnapEdge> Edges, IReadOnlySet<LayerKey> Layers);

    private BodySnapSource? _bodySnapSource;

    /// <summary>Snap queries the last pointer move of a body drag ran — one per pin tried, plus one for the edges.
    /// A COUNTER, not a timing (R-dfr22-3-2): it is bounded by <see cref="BodySnapMaxPins"/> + 1 whatever the
    /// layout holds.</summary>
    internal int BodySnapQueriesLastMove { get; private set; }

    /// <summary>The snap tolerance the current drag tick was given, DBU.</summary>
    private long _moveSnapTolDbu;

    /// <summary>
    /// Pulls a body drag's delta onto the nearest contact within <paramref name="tolDbu"/>: a pin of the selection
    /// onto a stationary pin or port (both axes), else an outline edge flush onto a facing parallel edge (across the
    /// edge only — along it the pointer's own, grid-snapped, delta stands). Returns false, leaving the delta alone,
    /// when nothing is in range. The contact point becomes the snap marker.
    /// </summary>
    private bool TryBodySnap(ref long dx, ref long dy, long tolDbu)
    {
        BodySnapQueriesLastMove = 0;
        if (tolDbu <= 0) return false;
        var src = _bodySnapSource ??= ReadBodySnapSource();
        if (src.Pins.Count == 0 && src.Edges.Count == 0) return false;
        var (excludeShapes, excludeInstances) = ComputeSnapExclusions();

        // ── a pin onto a pin or a port ───────────────────────────────────────────────────────────
        double bestD2 = double.MaxValue;
        (long X, long Y, long Px, long Py)? bestPin = null;
        foreach (var (pxr, pyr) in src.Pins.Take(BodySnapMaxPins))
        {
            long qx = pxr + dx, qy = pyr + dy;
            BodySnapQueriesLastMove++;
            var counters = new SnapQueryCounters();
            foreach (var c in LayoutSnapQuery.FindCandidates(Model, DrawTechnology, InstanceBaseDir, qx, qy, tolDbu,
                                                             false, excludeShapes, excludeInstances, ref counters))
            {
                if (c.Kind != SnapFeatureKind.Pin) continue;
                double d2 = (double)(c.X - qx) * (c.X - qx) + (double)(c.Y - qy) * (c.Y - qy);
                if (d2 < bestD2) { bestD2 = d2; bestPin = (c.X, c.Y, pxr, pyr); }
            }
            foreach (var port in PortsNear(qx, qy, tolDbu, excludeShapes))
            {
                double d2 = (double)(port.X - qx) * (port.X - qx) + (double)(port.Y - qy) * (port.Y - qy);
                if (d2 < bestD2) { bestD2 = d2; bestPin = (port.X, port.Y, pxr, pyr); }
            }
        }
        if (bestPin is { } pin)
        {
            dx = pin.X - pin.Px;
            dy = pin.Y - pin.Py;
            ShowBodySnapMarker(SnapFeatureKind.Pin, pin.X, pin.Y);
            return true;
        }

        // ── an edge flush onto a facing parallel edge ────────────────────────────────────────────
        if (src.Edges.Count == 0 || src.Edges.Count > BodySnapMaxEdges) return false;
        var moved = Bbox.Empty;
        foreach (var e in src.Edges)
            moved = moved.Union(new Bbox((long)Math.Floor(Math.Min(e.X0, e.X1)) + dx, (long)Math.Floor(Math.Min(e.Y0, e.Y1)) + dy,
                                         (long)Math.Ceiling(Math.Max(e.X0, e.X1)) + dx, (long)Math.Ceiling(Math.Max(e.Y0, e.Y1)) + dy));
        var region = new Bbox(moved.MinX - tolDbu, moved.MinY - tolDbu, moved.MaxX + tolDbu, moved.MaxY + tolDbu);
        BodySnapQueriesLastMove++;
        var edgeCounters = new SnapQueryCounters();
        var stationary = LayoutSnapQuery.EdgesNear(Model, DrawTechnology, InstanceBaseDir, region, src.Layers,
                                                   excludeShapes, excludeInstances, BodySnapMaxStationaryEdges, ref edgeCounters);

        double bestGap = double.MaxValue;
        (double Gap, SnapEdge Dragged, double Mx, double My)? best = null;
        foreach (var e in src.Edges)
        {
            double ex0 = e.X0 + dx, ey0 = e.Y0 + dy, ex1 = e.X1 + dx, ey1 = e.Y1 + dy;
            double ux = ex1 - ex0, uy = ey1 - ey0, len = Math.Sqrt(ux * ux + uy * uy);
            if (len <= 0) continue;
            ux /= len; uy /= len;
            foreach (var f in stationary)
            {
                if (f.Layer != e.Layer) continue;
                if (e.Nx * f.Nx + e.Ny * f.Ny > -BodySnapParallelCos) continue;   // not facing
                // How far f's line lies beyond e along e's outward normal: positive is a gap, negative an overlap.
                double gap = (f.X0 - ex0) * e.Nx + (f.Y0 - ey0) * e.Ny;
                if (Math.Abs(gap) > tolDbu || Math.Abs(gap) >= Math.Abs(bestGap)) continue;
                // They must share some length along the edge, or they only line up.
                double t0 = (f.X0 - ex0) * ux + (f.Y0 - ey0) * uy, t1 = (f.X1 - ex0) * ux + (f.Y1 - ey0) * uy;
                double lo = Math.Max(0, Math.Min(t0, t1)), hi = Math.Min(len, Math.Max(t0, t1));
                if (hi <= lo) continue;
                double mid = 0.5 * (lo + hi);
                bestGap = gap;
                best = (gap, e, ex0 + mid * ux + gap * e.Nx, ey0 + mid * uy + gap * e.Ny);
            }
        }
        if (best is not { } hit) return false;
        dx += (long)Math.Round(hit.Gap * hit.Dragged.Nx);
        dy += (long)Math.Round(hit.Gap * hit.Dragged.Ny);
        ShowBodySnapMarker(SnapFeatureKind.Nearest, (long)Math.Round(hit.Mx), (long)Math.Round(hit.My));
        return true;
    }

    /// <summary>Ports — port labels at the top level — whose anchor is within <paramref name="tolDbu"/> of a point,
    /// found through the document's spatial index.</summary>
    private IEnumerable<(long X, long Y)> PortsNear(long x, long y, long tolDbu, IReadOnlySet<int>? excludeShapes)
    {
        var query = new Bbox(x - tolDbu, y - tolDbu, x + tolDbu, y + tolDbu);
        foreach (int i in Model.SpatialIndex.QueryIntersecting(Model.Shapes, query))
        {
            if (i < 0 || i >= Model.Shapes.Count || excludeShapes?.Contains(i) == true) continue;
            if (Model.Shapes[i] is LabelShape { IsPort: true } port
                && Math.Abs(port.X - x) <= tolDbu && Math.Abs(port.Y - y) <= tolDbu)
                yield return (port.X, port.Y);
        }
    }

    private void ShowBodySnapMarker(SnapFeatureKind kind, long x, long y)
    {
        _currentSnapCandidate = new SnapCandidate(kind, x, y, default, false, -1);
        _snapCandidateIsRealTarget = true;
    }

    /// <summary>The selected instances' pins and the selection's outline edges, at rest. Instances whose cell does
    /// not resolve contribute nothing.</summary>
    private BodySnapSource ReadBodySnapSource()
    {
        var pins = new List<(long X, long Y)>();
        var edges = new List<SnapEdge>();
        var counters = new SnapQueryCounters();
        int edgeCap = BodySnapMaxEdges + 1;   // one past the cap is enough to know it was passed
        foreach (int i in _selectedInstanceIndices)
        {
            if (i < 0 || i >= Model.Instances.Count) continue;
            var inst = Model.Instances[i];
            var res = CellLayoutResolver.Resolve(inst, InstanceBaseDir, DrawTechnology);
            if (res is { State: CellLayoutState.Resolved, View: { } sub })
            {
                int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
                foreach (var pin in CellPins.Resolve(sub, DrawTechnology))
                    for (int r = 0; r < rows && pins.Count < BodySnapMaxPins; r++)
                    for (int c = 0; c < cols && pins.Count < BodySnapMaxPins; c++)
                        pins.Add(LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, r, c));
            }
            if (edges.Count < edgeCap)
                LayoutSnapQuery.AppendInstanceOutlineEdges(inst, InstanceBaseDir, DrawTechnology, null, edges, edgeCap, ref counters);
        }
        foreach (int i in MovableSelectedIndices(_selectedIndices))
            if (edges.Count < edgeCap && i >= 0 && i < Model.Shapes.Count)
                LayoutSnapQuery.AppendOutlineEdges(Model.Shapes[i], DrawTechnology, null, 0, 0, edges, edgeCap);
        return new BodySnapSource(pins, edges, edges.Select(e => e.Layer).ToHashSet());
    }
}
