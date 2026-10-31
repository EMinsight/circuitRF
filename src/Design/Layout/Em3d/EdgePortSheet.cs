// brief-dfr32-2 R-dfr32-2-1 / R-dfr32-2-2 — how wide an edge port's 3D sheet is.
//
// ONE rule, read by the 3D generator when it builds the sheet (Em3dGenerator.BuildPorts, which the .c3d creation path
// also goes through) and by the layout editor when it draws the port's bar (LayoutRenderer), so the extent on screen is
// the extent the run builds. Units are the caller's: metres in the generator, DBU in the renderer.
//
// An edge port's sheet used to be the whole conductor edge it sits on. On a line end that is the line's width, which is
// right; on the edge of a ground pour it was millimetres of sheet nobody meant, and could reach a via the port was never
// near. Now: a stated width wins; otherwise an edge longer than LongEdgeOverHeight × H (H = the dielectric height to
// the port's return) gets a sheet BoundedWidthOverHeight × H wide, centred on the port's point. Either is clipped to the
// edge.

using Clipper2Lib;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>What decided an edge port's sheet width.</summary>
public enum EdgePortSheetRule
{
    /// <summary>The whole conductor edge: a line end, or an edge no longer than 4 × H.</summary>
    Edge,
    /// <summary>The edge is longer than 4 × H and no width is stated: 2 × H, centred on the port.</summary>
    Bounded,
    /// <summary>The port states its own width.</summary>
    Stated,
}

/// <summary>An edge port's sheet along its edge, and why it is that wide.</summary>
public readonly record struct EdgePortSheetSpan(double Lo, double Hi, EdgePortSheetRule Rule)
{
    public double Width => Hi - Lo;

    /// <summary>True when the sheet is narrower than the requested width because the edge ends first.</summary>
    public bool Clipped { get; init; }
}

public static class EdgePortSheet
{
    /// <summary>An edge longer than this many H is bounded.</summary>
    public const double LongEdgeOverHeight = 4.0;

    /// <summary>A bounded sheet is this many H wide.</summary>
    public const double BoundedWidthOverHeight = 2.0;

    /// <summary>
    /// The sheet's extent along the edge [<paramref name="lo"/>, <paramref name="hi"/>]: centred on
    /// <paramref name="at"/> (the port's own coordinate along the edge) when a width is stated or the edge is long,
    /// and clipped to the edge. <paramref name="height"/> is the dielectric height to the return; zero or less (not
    /// known) never bounds.
    /// </summary>
    public static EdgePortSheetSpan Span(double lo, double hi, double at, double height, double? stated)
    {
        double want;
        EdgePortSheetRule rule;
        if (stated is > 0 and var w) { want = w; rule = EdgePortSheetRule.Stated; }
        else if (height > 0 && hi - lo > LongEdgeOverHeight * height)
        {
            want = BoundedWidthOverHeight * height;
            rule = EdgePortSheetRule.Bounded;
        }
        else return new(lo, hi, EdgePortSheetRule.Edge);

        double centre = Math.Clamp(at, lo, hi);
        double a = Math.Max(lo, centre - want / 2), b = Math.Min(hi, centre + want / 2);
        if (!(b > a)) return new(lo, hi, EdgePortSheetRule.Edge);
        return new(a, b, rule) { Clipped = b - a < want * (1 - 1e-9) };
    }

    /// <summary>
    /// The layout editor's H for a port on drawing layer <paramref name="layer"/>: the dielectric between that
    /// conductor and the nearer of the nearest ground-reference conductors above and below it (the nearest conductor
    /// of any kind when the technology flags none), in the layout's DBU at <paramref name="dbuPerMicron"/>. A run walks
    /// the artwork for its return (Em3dGenerator); this reads the stack, which is the same answer wherever the return is
    /// a plane, and is what lets a port's bar be drawn with no setup open. Null when the layer is on no conductor or no
    /// conductor is above or below it.
    /// </summary>
    public static double? HeightToReturnDbu(Technology? tech, LayerKey? layer, int dbuPerMicron)
    {
        if (tech is null || layer is not { } key) return null;
        var layers = tech.Stackup.Layers;
        int own = layers.FindIndex(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(key));
        if (own < 0) return null;
        bool anyFlagged = layers.Any(l => l.Kind == StackupKind.Conductor && l.IsGroundReference);

        double? Walk(int step)
        {
            long between = 0;
            for (int i = own + step; i >= 0 && i < layers.Count; i += step)
            {
                var l = layers[i];
                if (l.Kind == StackupKind.Conductor && (l.IsGroundReference || !anyFlagged)) return between;
                if (l.Kind != StackupKind.Conductor) between += l.ThicknessDbu;
            }
            return null;
        }

        double? below = Walk(1), above = Walk(-1);
        double? h = below is { } b && above is { } a ? Math.Min(a, b) : below ?? above;
        return h is { } v && v > 0 ? v * dbuPerMicron / LayoutUnits.DefaultDbuPerMicron : null;
    }

    /// <summary>
    /// R-dfr32-2-1 — an edge port's marker hint narrowed to its sheet: the bar the layout editor draws is
    /// <see cref="Span"/> of the conductor end the hint measured, the port's point, H from the stack
    /// (<see cref="HeightToReturnDbu"/>) and the port's stated <see cref="LabelShape.PortWidth"/> — so a port whose
    /// sheet would be narrower than its edge is drawn as narrow as the run builds it. The hint unchanged when the
    /// sheet is the whole edge.
    /// </summary>
    public static LayoutPortDirection.PortHint Bar(LayoutPortDirection.PortHint hint, LabelShape label, Technology? tech,
                                                   int dbuPerMicron)
    {
        var (px, _) = LayoutPortDirection.PerpendicularVector(hint.Direction);
        bool acrossX = px != 0;
        double centre = acrossX ? hint.PlaneX : hint.PlaneY;
        double lo = centre - hint.WidthDbu / 2.0, hi = centre + hint.WidthDbu / 2.0;
        double h = HeightToReturnDbu(tech, label.PortLayer ?? label.Layer, dbuPerMicron) ?? 0;
        var span = Span(lo, hi, acrossX ? label.X : label.Y, h, label.PortWidth);
        if (span.Rule == EdgePortSheetRule.Edge) return hint;
        long c = (long)Math.Round((span.Lo + span.Hi) / 2);
        return hint with
        {
            WidthDbu = Math.Max(1, (long)Math.Round(span.Width)),
            PlaneX = acrossX ? c : hint.PlaneX,
            PlaneY = acrossX ? hint.PlaneY : c,
        };
    }

    /// <summary>Clipper's integer grid for <see cref="FlushTo"/>: 0.1 nm, so a board metres across stays far inside a long.</summary>
    private const double FlushGridPerMetre = 1e10;

    /// <summary>
    /// brief-dfr32-2 R-dfr32-2-3 — <paramref name="poly"/> cut flat at an edge port's sheet: the metal OUTSIDE the sheet's
    /// plane (<paramref name="at"/> on the port's normal axis, outside meaning away from the conductor <paramref name="side"/>
    /// names) and across the sheet's width [<paramref name="lo"/>, <paramref name="hi"/>] is removed, so the sheet meets a
    /// straight face. A port on an arc otherwise stands its flat sheet against a polyline that leaves the sheet's plane at a
    /// grazing angle near each end of the sheet, and the mesher resolves the slivers between them.
    ///
    /// <para>Null when there is nothing outside the plane there (a straight end, which is every line end), or when cutting
    /// would leave the metal in more than one piece — then the conductor is left as drawn. <paramref name="depth"/> is the
    /// furthest the removed metal reached beyond the plane.</para>
    ///
    /// <para><b>The cut face and the sheet must END together, not merely nearly.</b> A sheet's width is read just inside
    /// the conductor end, and on an arc the metal there is a few nanometres wider than on the plane itself; the clipping
    /// grid adds its own tenth of a nanometre. Either leaves an edge of that length where the sheet's corner meets the
    /// face's, and the mesher resolves it (measured: a 7 nm smallest edge before the cut, 0.02 nm with the cut alone). So
    /// every vertex within a part in 10⁴ of the sheet's width of the plane is put ON it, and
    /// [<paramref name="faceLo"/>, <paramref name="faceHi"/>] is the face's own extent — the width the sheet then takes.</para>
    /// </summary>
    public static PlanarPolygon? FlushTo(PlanarPolygon poly, PlanarPortSide side, double at, double lo, double hi, out double depth,
                                         out double faceLo, out double faceHi)
    {
        depth = 0;
        faceLo = lo;
        faceHi = hi;
        bool alongX = side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
        bool outsideIsLow = side is PlanarPortSide.MinX or PlanarPortSide.MinY;
        var (x0, y0, x1, y1) = poly.Bounds();
        double far = outsideIsLow ? (alongX ? x0 : y0) : (alongX ? x1 : y1);
        if (outsideIsLow ? far >= at : far <= at) return null;
        double margin = Math.Max(x1 - x0, y1 - y0);
        double n0 = outsideIsLow ? far - margin : at, n1 = outsideIsLow ? at : far + margin;

        static long G(double m) => (long)Math.Round(m * FlushGridPerMetre);
        Path64 Ring(IReadOnlyList<EmPoint> ring) => new(ring.Select(q => new Point64(G(q.X), G(q.Y))));
        var subject = new Paths64 { Ring(poly.Outer) };
        foreach (var h in poly.HoleRings) subject.Add(Ring(h));
        var box = alongX
            ? new Path64 { new(G(n0), G(lo)), new(G(n1), G(lo)), new(G(n1), G(hi)), new(G(n0), G(hi)) }
            : new Path64 { new(G(lo), G(n0)), new(G(hi), G(n0)), new(G(hi), G(n1)), new(G(lo), G(n1)) };
        var clip = new Paths64 { box };

        var removed = Clipper.Intersect(subject, clip, FillRule.EvenOdd);
        double removedArea = removed.Sum(r => Math.Abs(Clipper.Area(r))) / (FlushGridPerMetre * FlushGridPerMetre);
        if (removedArea <= 1e-9 * poly.Area()) return null;

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Difference, subject, clip, tree, FillRule.EvenOdd);
        if (tree.Count != 1) return null;
        var outer = tree[0];
        double tol = Math.Max(1e-9, 1e-4 * (hi - lo));
        double fLo = double.PositiveInfinity, fHi = double.NegativeInfinity;
        List<EmPoint> Snap(Path64 path)
        {
            var ring = new List<EmPoint>(path.Count);
            foreach (var q in path)
            {
                double n = (alongX ? q.X : q.Y) / FlushGridPerMetre, t = (alongX ? q.Y : q.X) / FlushGridPerMetre;
                if (Math.Abs(n - at) <= tol && t >= lo - tol && t <= hi + tol)
                {
                    n = at;
                    fLo = Math.Min(fLo, t);
                    fHi = Math.Max(fHi, t);
                }
                var e = alongX ? new EmPoint(n, t) : new EmPoint(t, n);
                if (ring.Count == 0 || ring[^1] != e) ring.Add(e);
            }
            if (ring.Count > 1 && ring[0] == ring[^1]) ring.RemoveAt(ring.Count - 1);
            return ring;
        }
        var outerRing = Snap(outer.Polygon!);
        var holes = new List<IReadOnlyList<EmPoint>>();
        for (int i = 0; i < outer.Count; i++)
        {
            if (outer[i].Count > 0) return null;
            holes.Add(Snap(outer[i].Polygon!));
        }
        if (!(fHi > fLo)) return null;
        (faceLo, faceHi) = (fLo, fHi);

        foreach (var r in removed)
            foreach (var q in r)
            {
                double n = (alongX ? q.X : q.Y) / FlushGridPerMetre;
                depth = Math.Max(depth, Math.Abs(n - at));
            }
        return new PlanarPolygon(outerRing, holes.Count == 0 ? null : holes);
    }
}
