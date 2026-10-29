// The lateral shape of a slab that the stackup states no outline for — a dielectric, a body drawn on no
// layer. A layout placed in a .c3d, and a planar (MoM) setup previewed in 3D, cut every such slab to the board
// outline, else to ONE rectangle: the box round what the layout draws (Em3dLayoutSolids.Bounded). Nothing
// reaches past the artwork; a board that is bigger than its copper draws its outline.
//
// The preview's slabs are also clipped to the air box it is drawn in. Coordinates are integer nanometres for
// Clipper, so the result is deterministic.

using Clipper2Lib;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em3d;

public static class SlabLateralBound
{
    private const double NmPerMetre = 1e9;

    /// <summary>
    /// <paramref name="polys"/> clipped to the rectangle — a planar preview's slab never reaches past the air box
    /// it is drawn in, whatever padding the setup states. Outer rings only.
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
}
