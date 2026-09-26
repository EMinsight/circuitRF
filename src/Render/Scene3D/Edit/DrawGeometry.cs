// brief-em3d-45 R-em3d45-3 — the geometry a drawing gesture needs and never stores: the rubber band a tool
// shows while it is in progress, and the exact test that refuses a self-intersecting outline at close.
//
// THE RUBBER BAND IS NOT THE DOCUMENT. It is a handful of world segments the 2D overlay projects each frame,
// so a gesture changes nothing until it commits — a cancelled gesture leaves no undo entry and uploads nothing
// (gate 8) — and the committed object then comes back through the elaboration, the one picture the editor
// draws (overview §0).
//
// THE CROSSING TEST IS EXACT. Outline points are integer DBU, and the orientation of three of them is an
// integer determinant; it is evaluated in 128 bits, so "touches" and "crosses" are never a tolerance.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>One segment of a rubber band, world metres.</summary>
public readonly record struct DrawSegment(Point3 A, Point3 B);

public static class DrawGeometry
{
    /// <summary>A circle is shown with this many sides.</summary>
    public const int CircleSides = 64;

    public static Point3 Metres(C3dPoint3 p, int dbuPerMicron)
        => new(C3dLowering.Metres(p.X, dbuPerMicron), C3dLowering.Metres(p.Y, dbuPerMicron), C3dLowering.Metres(p.Z, dbuPerMicron));

    /// <summary>The closed loop through <paramref name="points"/>, or the open chain when <paramref name="closed"/> is false.</summary>
    public static void Chain(IReadOnlyList<C3dPoint3> points, bool closed, int dbuPerMicron, List<DrawSegment> into)
    {
        for (int i = 0; i + 1 < points.Count; i++) into.Add(new(Metres(points[i], dbuPerMicron), Metres(points[i + 1], dbuPerMicron)));
        if (closed && points.Count > 2) into.Add(new(Metres(points[^1], dbuPerMicron), Metres(points[0], dbuPerMicron)));
    }

    /// <summary>The rectangle on <paramref name="plane"/> with opposite corners <paramref name="a"/> and <paramref name="b"/>
    /// (their u and v; the plane's offset is their w), lifted <paramref name="lift"/> along the normal.</summary>
    public static C3dPoint3[] Rectangle(DrawingPlane plane, C3dPoint2 a, C3dPoint2 b, long lift = 0)
    {
        long w = plane.OffsetDbu + lift;
        return [plane.FromUvw(a.U, a.V, w), plane.FromUvw(b.U, a.V, w), plane.FromUvw(b.U, b.V, w), plane.FromUvw(a.U, b.V, w)];
    }

    /// <summary>A box's twelve edges: the rectangle a–b on the plane, the same rectangle <paramref name="height"/> along the
    /// normal, and the four uprights.</summary>
    public static void Box(DrawingPlane plane, C3dPoint2 a, C3dPoint2 b, long height, int dbuPerMicron, List<DrawSegment> into)
    {
        var lo = Rectangle(plane, a, b);
        var hi = Rectangle(plane, a, b, height);
        Chain(lo, true, dbuPerMicron, into);
        Chain(hi, true, dbuPerMicron, into);
        for (int k = 0; k < 4; k++) into.Add(new(Metres(lo[k], dbuPerMicron), Metres(hi[k], dbuPerMicron)));
    }

    /// <summary>A circle of radius <paramref name="radius"/> about <paramref name="centre"/> on the plane, lifted
    /// <paramref name="lift"/>, as <see cref="CircleSides"/> segments (metres: its points are not DBU points).</summary>
    public static void Circle(DrawingPlane plane, C3dPoint2 centre, double radius, long lift, int dbuPerMicron, List<DrawSegment> into)
    {
        Point3 P(double t)
        {
            double u = centre.U + radius * Math.Cos(t), v = centre.V + radius * Math.Sin(t);
            var (au, av) = DrawingPlane.AxesOf(plane.Plane);
            double w = plane.OffsetDbu + lift;
            double x = 0, y = 0, z = 0;
            void Set(C3dAxis a, double val) { if (a == C3dAxis.X) x = val; else if (a == C3dAxis.Y) y = val; else z = val; }
            Set(au, u); Set(av, v); Set(plane.Normal, w);
            double m = C3dLowering.Metres(1, dbuPerMicron);
            return new Point3(x * m, y * m, z * m);
        }
        var first = P(0);
        var prev = first;
        for (int k = 1; k <= CircleSides; k++)
        {
            var next = k == CircleSides ? first : P(2 * Math.PI * k / CircleSides);
            into.Add(new(prev, next));
            prev = next;
        }
    }

    /// <summary>A cylinder: both end circles and four uprights.</summary>
    public static void Cylinder(DrawingPlane plane, C3dPoint2 centre, double radius, long height, int dbuPerMicron, List<DrawSegment> into)
    {
        Circle(plane, centre, radius, 0, dbuPerMicron, into);
        if (height == 0) return;
        Circle(plane, centre, radius, height, dbuPerMicron, into);
        for (int k = 0; k < 4; k++)
        {
            double t = Math.PI / 2 * k;
            var u = (long)Math.Round(centre.U + radius * Math.Cos(t));
            var v = (long)Math.Round(centre.V + radius * Math.Sin(t));
            into.Add(new(Metres(plane.FromUvw(u, v, plane.OffsetDbu), dbuPerMicron), Metres(plane.FromUvw(u, v, plane.OffsetDbu + height), dbuPerMicron)));
        }
    }

    // ── the exact crossing test (R-em3d45-3c) ────────────────────────────────────────────────

    /// <summary>
    /// The first pair of non-adjacent edges of the outline (closed: edge k runs from vertex k to k+1, the last back
    /// to 0) that touch or cross, or null when the outline is simple. A repeated vertex, a zero-length edge and an
    /// edge doubling back along its neighbour are crossings too: none of them bounds an area.
    /// </summary>
    public static (int EdgeA, int EdgeB)? FirstCrossing(IReadOnlyList<C3dPoint2> pts, bool closed = true)
    {
        int n = pts.Count;
        int edges = closed ? n : n - 1;
        if (edges < 2) return null;
        C3dPoint2 A(int e) => pts[e];
        C3dPoint2 B(int e) => pts[(e + 1) % n];
        for (int i = 0; i < edges; i++)
        {
            if (A(i) == B(i)) return (i, i);
            for (int j = i + 1; j < edges; j++)
            {
                bool adjacent = j == i + 1 || (closed && i == 0 && j == edges - 1);
                if (adjacent)
                {
                    // Neighbours share one vertex; they cross only when one runs back along the other.
                    var (s, p, q) = j == i + 1 ? (B(i), A(i), B(j)) : (A(i), B(i), A(j));
                    if (Orient(s, p, q) == 0 && Dot(p, s, q) > 0) return (i, j);
                    continue;
                }
                if (Touch(A(i), B(i), A(j), B(j))) return (i, j);
            }
        }
        return null;
    }

    private static int Orient(C3dPoint2 a, C3dPoint2 b, C3dPoint2 c)
    {
        Int128 d = (Int128)(b.U - a.U) * (c.V - a.V) - (Int128)(b.V - a.V) * (c.U - a.U);
        return d > 0 ? 1 : d < 0 ? -1 : 0;
    }

    /// <summary>The sign of (p − s)·(q − s): positive when p and q lie on the same side of s along a line.</summary>
    private static Int128 Dot(C3dPoint2 p, C3dPoint2 s, C3dPoint2 q)
        => (Int128)(p.U - s.U) * (q.U - s.U) + (Int128)(p.V - s.V) * (q.V - s.V);

    private static bool OnSegment(C3dPoint2 a, C3dPoint2 b, C3dPoint2 p)
        => Math.Min(a.U, b.U) <= p.U && p.U <= Math.Max(a.U, b.U) && Math.Min(a.V, b.V) <= p.V && p.V <= Math.Max(a.V, b.V);

    private static bool Touch(C3dPoint2 a, C3dPoint2 b, C3dPoint2 c, C3dPoint2 d)
    {
        int o1 = Orient(a, b, c), o2 = Orient(a, b, d), o3 = Orient(c, d, a), o4 = Orient(c, d, b);
        if (o1 * o2 < 0 && o3 * o4 < 0) return true;
        return (o1 == 0 && OnSegment(a, b, c)) || (o2 == 0 && OnSegment(a, b, d))
            || (o3 == 0 && OnSegment(c, d, a)) || (o4 == 0 && OnSegment(c, d, b));
    }

    /// <summary>Twice the signed area of a closed outline, exact.</summary>
    public static Int128 TwiceArea(IReadOnlyList<C3dPoint2> pts)
    {
        Int128 s = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Count];
            s += (Int128)p.U * q.V - (Int128)q.U * p.V;
        }
        return s;
    }
}
