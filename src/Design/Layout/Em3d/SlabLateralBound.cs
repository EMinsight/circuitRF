// The lateral shape of a slab that the stackup states no outline for — a dielectric, a body drawn on no
// layer, an instance's undrawn ground plane. ONE rule, shared by the two places that need it:
//
//   * a layout placed in a .c3d (Em3dLayoutSolids with Instance options): the FEM/FDTD problem is finite,
//     so the slab's shape IS part of what is solved;
//   * a planar (MoM) setup previewed in 3D: the planar solve treats every dielectric as laterally
//     infinite, and the picture trims it for viewing only — to the same shape, so the viewer shows what a
//     3D solver would use.
//
// The rule, in order: the board outline when the layout draws one; else the COPPER HULL of the copper
// directly above and below the slab; else (nothing drawn around it at all) the drawn geometry's bounding
// box. The hull is a morphological closing with an outward margin — offset out by R, union, drop every
// hole, offset in by R − M — with both distances tied to the dielectric stack's height H:
//
//   R = 5 H  closes every gap narrower than 10 H. A coplanar gap is a fraction of H to a few H, so the
//            substrate under a CPW gap is never removed; separate pieces of one board (pads a few H apart)
//            become one slab; holes (via clearances, antipads) are filled, since the board has substrate
//            there whatever the copper does.
//   M = 2 H  the outward margin past the outermost copper. A top-only trace's fringing field runs in the
//            substrate beside it and falls off over a few substrate heights; a slab cut flush at the copper
//            edge would lose that field and read the wrong impedance. When the copper IS the board edge (a
//            ground pour to the edge) the margin adds substrate where there is little field, which costs
//            mesh, not accuracy.
//
// Miter joins (limit 2) keep a rectangular board rectangular: a closing of a convex corner restores it
// exactly. Coordinates are integer nanometres for Clipper, so the result is deterministic.

using Clipper2Lib;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>Which bound a slab took — what the note reports per slab.</summary>
public enum SlabBoundKind
{
    /// <summary>The layout's board outline.</summary>
    Outline,
    /// <summary>The closed hull of the copper above and below it.</summary>
    CopperHull,
    /// <summary>Nothing drawn above or below: the drawn geometry's bounding box.</summary>
    BoundingBox,
}

public static class SlabLateralBound
{
    /// <summary>The closing radius, in dielectric stack heights.</summary>
    public const double CloseRadiusInStackHeights = 5.0;

    /// <summary>The outward margin past the outermost copper, in dielectric stack heights.</summary>
    public const double MarginInStackHeights = 2.0;

    private const double NmPerMetre = 1e9;

    /// <summary>
    /// The closed hull of <paramref name="copper"/>: every piece's outer ring unioned, grown by
    /// <paramref name="closeRadiusM"/>, holes dropped, shrunk back to <paramref name="marginM"/> past the
    /// copper. Hole rings of the input are ignored — a hole is filled either way. Empty in, empty out.
    /// </summary>
    public static IReadOnlyList<PlanarPolygon> CopperHull(IEnumerable<PlanarPolygon> copper, double closeRadiusM, double marginM)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(marginM);
        ArgumentOutOfRangeException.ThrowIfLessThan(closeRadiusM, marginM);
        var outers = new Paths64();
        foreach (var p in copper)
            if (p.Outer.Count >= 3)
                outers.Add(new Path64(p.Outer.Select(q => new Point64((long)Math.Round(q.X * NmPerMetre),
                                                                      (long)Math.Round(q.Y * NmPerMetre)))));
        if (outers.Count == 0) return [];

        var union = Clipper.Union(outers, FillRule.NonZero);
        var grown = Clipper.InflatePaths(union, closeRadiusM * NmPerMetre, JoinType.Miter, EndType.Polygon, 2.0);
        // Positive rings of the union are the outer contours; a negative one is a hole, and is dropped.
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, grown, new Paths64(), tree, FillRule.NonZero);
        var filled = new Paths64();
        for (int i = 0; i < tree.Count; i++) filled.Add(tree[i].Polygon!);
        var shrunk = Clipper.InflatePaths(filled, -(closeRadiusM - marginM) * NmPerMetre, JoinType.Miter, EndType.Polygon, 2.0);

        var result = new List<PlanarPolygon>(shrunk.Count);
        var outTree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, shrunk, new Paths64(), outTree, FillRule.NonZero);
        for (int i = 0; i < outTree.Count; i++)
            result.Add(new PlanarPolygon([.. outTree[i].Polygon!.Select(q => new EmPoint(q.X / NmPerMetre, q.Y / NmPerMetre))]));
        return result;
    }

    /// <summary>
    /// <paramref name="polys"/> clipped to the rectangle — a planar preview's slab never reaches past the air box
    /// it is drawn in, whatever padding the setup states. Outer rings only, as <see cref="CopperHull"/> makes them.
    /// </summary>
    public static IReadOnlyList<PlanarPolygon> ClipToRect(IReadOnlyList<PlanarPolygon> polys, double x0, double y0, double x1, double y1)
    {
        var subject = new Paths64(polys.Select(p => new Path64(p.Outer.Select(q =>
            new Point64((long)Math.Round(q.X * NmPerMetre), (long)Math.Round(q.Y * NmPerMetre))))));
        var rect = new Rect64((long)Math.Round(x0 * NmPerMetre), (long)Math.Round(y0 * NmPerMetre),
                              (long)Math.Round(x1 * NmPerMetre), (long)Math.Round(y1 * NmPerMetre));
        var clipped = Clipper.RectClip(rect, subject);
        return [.. clipped.Where(r => r.Count >= 3)
                          .Select(r => new PlanarPolygon([.. r.Select(q => new EmPoint(q.X / NmPerMetre, q.Y / NmPerMetre))]))];
    }

    /// <summary>A sentence naming the rule and its two distances, for the note a caller writes.</summary>
    public static string HullRule(double stackHeightM)
        => $"the outline of the copper above and below it, with gaps up to {Um(2 * CloseRadiusInStackHeights * stackHeightM)} µm " +
           $"closed, holes filled and {Um(MarginInStackHeights * stackHeightM)} µm of margin " +
           $"({CloseRadiusInStackHeights * 2:G} and {MarginInStackHeights:G} dielectric stack heights), so the substrate " +
           "stays in every coplanar gap and beside every edge where fringing field runs";

    private static string Um(double m) => (m * 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
