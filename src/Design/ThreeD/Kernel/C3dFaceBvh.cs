// brief-em3d-47 R-em3d47-3d — the bounding-volume hierarchy of a B-rep's face bounds, and the exact tests the
// self-intersection check runs on the few candidate pairs it finds.
//
// THE HIERARCHY is built once per B-rep a gesture starts from and queried with the bounds of the faces an edit
// touched: the faces it did NOT touch have not moved, so their bounds in the tree are still theirs. That is what
// keeps the check's cost bounded by the edit's neighbourhood rather than by F² (gate 6).
//
// THE TESTS ARE EXACT. Vertices are integers, so orientation is the sign of a 3×3 determinant of differences,
// computed in Int128 (each factor below 2^42, each product below 2^126). Two triangles that are not coplanar
// meet exactly when an edge of one passes through the other; coplanar ones are tested in the plane they share,
// by segment crossings and containment. Touching counts as meeting: in a closed solid, two faces that share no
// vertex never touch.

namespace CircuitRF.Design.ThreeD.Kernel;

/// <summary>An axis-aligned bounding-box tree over a B-rep's faces.</summary>
public sealed class C3dFaceBvh
{
    private const int LeafSize = 4;

    // Node n: bounds [6n .. 6n+6), children or a leaf range.
    private readonly long[] _bounds;
    private readonly int[] _left, _right, _first, _count;
    private readonly int[] _faces;
    private readonly int _nodes;

    private C3dFaceBvh(long[] bounds, int[] left, int[] right, int[] first, int[] count, int[] faces, int nodes)
    {
        _bounds = bounds; _left = left; _right = right; _first = first; _count = count; _faces = faces; _nodes = nodes;
    }

    /// <summary>The tree over every face of <paramref name="b"/>.</summary>
    public static C3dFaceBvh Build(C3dBrep b)
    {
        int n = b.Faces.Count;
        var box = new long[6 * n];
        var centre = new double[3 * n];
        for (int f = 0; f < n; f++)
        {
            var (x0, y0, z0, x1, y1, z1) = b.Bounds(f);
            box[6 * f] = x0; box[6 * f + 1] = y0; box[6 * f + 2] = z0;
            box[6 * f + 3] = x1; box[6 * f + 4] = y1; box[6 * f + 5] = z1;
            centre[3 * f] = (x0 + (double)x1) / 2; centre[3 * f + 1] = (y0 + (double)y1) / 2; centre[3 * f + 2] = (z0 + (double)z1) / 2;
        }
        int cap = Math.Max(1, 2 * n);
        var bounds = new long[6 * cap];
        int[] left = new int[cap], right = new int[cap], first = new int[cap], count = new int[cap];
        var faces = Enumerable.Range(0, n).ToArray();
        int nodes = 0;

        int Node(int lo, int hi)
        {
            int me = nodes++;
            long x0 = long.MaxValue, y0 = long.MaxValue, z0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue, z1 = long.MinValue;
            for (int i = lo; i < hi; i++)
            {
                int f = faces[i];
                x0 = Math.Min(x0, box[6 * f]); y0 = Math.Min(y0, box[6 * f + 1]); z0 = Math.Min(z0, box[6 * f + 2]);
                x1 = Math.Max(x1, box[6 * f + 3]); y1 = Math.Max(y1, box[6 * f + 4]); z1 = Math.Max(z1, box[6 * f + 5]);
            }
            bounds[6 * me] = x0; bounds[6 * me + 1] = y0; bounds[6 * me + 2] = z0;
            bounds[6 * me + 3] = x1; bounds[6 * me + 4] = y1; bounds[6 * me + 5] = z1;
            if (hi - lo <= LeafSize)
            {
                left[me] = right[me] = -1;
                first[me] = lo; count[me] = hi - lo;
                return me;
            }
            // Split at the median of the widest axis; ties broken by face index, so the tree is deterministic.
            int axis = 0;
            long w0 = x1 - x0, w1 = y1 - y0, w2 = z1 - z0;
            if (w1 > w0 && w1 >= w2) axis = 1; else if (w2 > w0 && w2 > w1) axis = 2;
            Array.Sort(faces, lo, hi - lo, Comparer<int>.Create((a, c) =>
            {
                int k = centre[3 * a + axis].CompareTo(centre[3 * c + axis]);
                return k != 0 ? k : a.CompareTo(c);
            }));
            int mid = (lo + hi) / 2;
            int l = Node(lo, mid), r = Node(mid, hi);
            left[me] = l; right[me] = r;
            return me;
        }
        if (n > 0) Node(0, n);
        return new C3dFaceBvh(bounds, left, right, first, count, faces, nodes);
    }

    /// <summary>Every face whose bounds meet (x0 … z1), appended to <paramref name="hits"/> in tree order.</summary>
    public void Query(long x0, long y0, long z0, long x1, long y1, long z1, List<int> hits)
    {
        if (_nodes == 0) return;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            var b = _bounds.AsSpan(6 * n, 6);
            if (b[0] > x1 || b[3] < x0 || b[1] > y1 || b[4] < y0 || b[2] > z1 || b[5] < z0) continue;
            if (_left[n] < 0)
            {
                for (int i = _first[n]; i < _first[n] + _count[n]; i++) hits.Add(_faces[i]);
                continue;
            }
            stack.Push(_right[n]);
            stack.Push(_left[n]);
        }
    }
}

/// <summary>Exact predicates on integer points.</summary>
public static class C3dExact
{
    /// <summary>The sign of det[b−a; c−a; d−a]: which side of the plane (a, b, c) d lies on.</summary>
    public static int Orient(C3dPoint3 a, C3dPoint3 b, C3dPoint3 c, C3dPoint3 d)
    {
        Int128 bx = b.X - a.X, by = b.Y - a.Y, bz = b.Z - a.Z;
        Int128 cx = c.X - a.X, cy = c.Y - a.Y, cz = c.Z - a.Z;
        Int128 dx = d.X - a.X, dy = d.Y - a.Y, dz = d.Z - a.Z;
        Int128 det = bx * (cy * dz - cz * dy) - by * (cx * dz - cz * dx) + bz * (cx * dy - cy * dx);
        return det.CompareTo(Int128.Zero);
    }

    /// <summary>Twice the vector area of triangle (a, b, c).</summary>
    public static (Int128 X, Int128 Y, Int128 Z) Cross(C3dPoint3 a, C3dPoint3 b, C3dPoint3 c)
    {
        Int128 ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        Int128 vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    /// <summary>Whether triangles (a0, a1, a2) and (b0, b1, b2) share any point. A degenerate triangle meets
    /// nothing: it has no interior, and its edges are its neighbours' edges.</summary>
    public static bool TrianglesMeet(C3dPoint3 a0, C3dPoint3 a1, C3dPoint3 a2, C3dPoint3 b0, C3dPoint3 b1, C3dPoint3 b2)
    {
        var na = Cross(a0, a1, a2);
        var nb = Cross(b0, b1, b2);
        if (IsZero(na) || IsZero(nb)) return false;
        int s0 = Orient(b0, b1, b2, a0), s1 = Orient(b0, b1, b2, a1), s2 = Orient(b0, b1, b2, a2);
        if (s0 == s1 && s1 == s2 && s0 != 0) return false;
        int t0 = Orient(a0, a1, a2, b0), t1 = Orient(a0, a1, a2, b1), t2 = Orient(a0, a1, a2, b2);
        if (t0 == t1 && t1 == t2 && t0 != 0) return false;
        if (s0 == 0 && s1 == 0 && s2 == 0)
            return Coplanar(a0, a1, a2, b0, b1, b2, na);
        return SegmentMeetsTriangle(a0, a1, b0, b1, b2, nb) || SegmentMeetsTriangle(a1, a2, b0, b1, b2, nb)
            || SegmentMeetsTriangle(a2, a0, b0, b1, b2, nb) || SegmentMeetsTriangle(b0, b1, a0, a1, a2, na)
            || SegmentMeetsTriangle(b1, b2, a0, a1, a2, na) || SegmentMeetsTriangle(b2, b0, a0, a1, a2, na);
    }

    private static bool IsZero((Int128 X, Int128 Y, Int128 Z) v) => v.X == 0 && v.Y == 0 && v.Z == 0;

    private static bool SegmentMeetsTriangle(C3dPoint3 p, C3dPoint3 q, C3dPoint3 a, C3dPoint3 b, C3dPoint3 c,
                                             (Int128 X, Int128 Y, Int128 Z) n)
    {
        int sp = Orient(a, b, c, p), sq = Orient(a, b, c, q);
        if (sp == sq && sp != 0) return false;
        if (sp == 0 && sq == 0)
        {
            // The segment lies in the triangle's plane: a crossing of the triangle's edges, or an end inside it.
            int axis = DominantAxis(n);
            var (p2, q2, a2, b2, c2) = (P2(p, axis), P2(q, axis), P2(a, axis), P2(b, axis), P2(c, axis));
            return Inside2(p2, a2, b2, c2) || Inside2(q2, a2, b2, c2) || Seg2(p2, q2, a2, b2) || Seg2(p2, q2, b2, c2) || Seg2(p2, q2, c2, a2);
        }
        int o1 = Orient(p, q, a, b), o2 = Orient(p, q, b, c), o3 = Orient(p, q, c, a);
        return (o1 >= 0 && o2 >= 0 && o3 >= 0) || (o1 <= 0 && o2 <= 0 && o3 <= 0);
    }

    private static bool Coplanar(C3dPoint3 a0, C3dPoint3 a1, C3dPoint3 a2, C3dPoint3 b0, C3dPoint3 b1, C3dPoint3 b2,
                                 (Int128 X, Int128 Y, Int128 Z) n)
    {
        int axis = DominantAxis(n);
        (long, long)[] a = [P2(a0, axis), P2(a1, axis), P2(a2, axis)];
        (long, long)[] b = [P2(b0, axis), P2(b1, axis), P2(b2, axis)];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (Seg2(a[i], a[(i + 1) % 3], b[j], b[(j + 1) % 3])) return true;
        return Inside2(a[0], b[0], b[1], b[2]) || Inside2(b[0], a[0], a[1], a[2]);
    }

    private static int DominantAxis((Int128 X, Int128 Y, Int128 Z) n)
    {
        var ax = Int128.Abs(n.X); var ay = Int128.Abs(n.Y); var az = Int128.Abs(n.Z);
        return ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
    }

    /// <summary>The point with axis <paramref name="drop"/> left out.</summary>
    private static (long, long) P2(C3dPoint3 p, int drop) => drop switch
    {
        0 => (p.Y, p.Z),
        1 => (p.Z, p.X),
        _ => (p.X, p.Y),
    };

    private static int Orient2((long X, long Y) a, (long X, long Y) b, (long X, long Y) c)
    {
        Int128 d = (Int128)(b.X - a.X) * (c.Y - a.Y) - (Int128)(b.Y - a.Y) * (c.X - a.X);
        return d.CompareTo(Int128.Zero);
    }

    private static bool OnSegment2((long X, long Y) a, (long X, long Y) b, (long X, long Y) p)
        => Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);

    /// <summary>Whether segments pq and rs share a point.</summary>
    private static bool Seg2((long X, long Y) p, (long X, long Y) q, (long X, long Y) r, (long X, long Y) s)
    {
        int d1 = Orient2(r, s, p), d2 = Orient2(r, s, q), d3 = Orient2(p, q, r), d4 = Orient2(p, q, s);
        if (d1 * d2 < 0 && d3 * d4 < 0) return true;
        return (d1 == 0 && OnSegment2(r, s, p)) || (d2 == 0 && OnSegment2(r, s, q))
            || (d3 == 0 && OnSegment2(p, q, r)) || (d4 == 0 && OnSegment2(p, q, s));
    }

    /// <summary>Whether p lies in triangle (a, b, c), boundary included.</summary>
    private static bool Inside2((long X, long Y) p, (long X, long Y) a, (long X, long Y) b, (long X, long Y) c)
    {
        int o1 = Orient2(a, b, p), o2 = Orient2(b, c, p), o3 = Orient2(c, a, p);
        return (o1 >= 0 && o2 >= 0 && o3 >= 0) || (o1 <= 0 && o2 <= 0 && o3 <= 0);
    }
}
