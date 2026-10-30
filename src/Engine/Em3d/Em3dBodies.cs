// brief-dfr31-1 R-dfr31-1-1 — which conductor objects of a 3D problem are one piece of metal.
//
// A static solve holds a whole piece of metal at one potential, but the problem is built from OBJECTS: a top pad, the barrel
// under it, an inner landing pad and a bottom pad are four objects and one conductor. On a board whose nets were lost (an
// imported artwork) nothing else says which objects belong together, so this answers it from the geometry alone.
//
// Two objects join when they share a face or overlap in volume: their height ranges meet AND their footprints meet. That is
// exactly what joins a barrel to the copper it passes on every level it spans, and what keeps copper on two levels apart —
// a dielectric between them means their height ranges do not meet. A footprint is the object's real plan shape where the
// primitive has one (an extruded polygon with its holes, a box, a vertical barrel's disc, a ball's disc, a horizontal sheet),
// and its bounding rectangle otherwise (a swept wire per segment, a polyhedron, an ellipsoid, a tilted or curved sheet),
// which can join two objects that only come close but never parts two that touch.
//
// Candidate pairs come from a uniform grid over the plan (each footprint in the cells its rectangle covers, a pair tested
// once, in the cell holding the lower-left corner of the two rectangles' overlap). A footprint that would cover many cells —
// a plane — is tested against every other footprint instead, and there are few of those. So the cost is linear in the
// object count, with a term per plane; PairVisits counts it so a test can hold that without timing anything.

namespace CircuitRF.Engine.Em3d;

/// <summary>
/// brief-dfr31-1 — one piece of metal: its member objects in problem order, the nets they carry (first-seen order), and its
/// name — the nets joined, or <see cref="Em3dBodies.GeneratedPrefix"/> and its largest member's name when it carries none,
/// so the name survives a re-run.
/// </summary>
public sealed record Em3dBody(string Name, IReadOnlyList<string> Members, IReadOnlyList<string> Nets)
{
    /// <summary>True when the name was generated (the body carries no net).</summary>
    public bool Generated => Nets.Count == 0;
}

/// <summary>brief-dfr31-1 — the bodies of a problem's conductors, and which body each conductor is in.</summary>
public sealed class Em3dBodyGrouping
{
    private readonly Dictionary<string, int> _bodyOf;

    internal Em3dBodyGrouping(IReadOnlyList<Em3dBody> bodies, long pairVisits)
    {
        Bodies = bodies;
        PairVisits = pairVisits;
        _bodyOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int b = 0; b < bodies.Count; b++)
            foreach (string m in bodies[b].Members) _bodyOf[m] = b;
    }

    /// <summary>Every body, ordered by its first member's place in the problem.</summary>
    public IReadOnlyList<Em3dBody> Bodies { get; }

    /// <summary>How many candidate footprint pairs were examined: linear in the object count for a board, never its square.</summary>
    public long PairVisits { get; }

    /// <summary>The index of the body <paramref name="member"/> is in, or −1 when it is not a conductor of the problem.</summary>
    public int BodyOf(string member) => _bodyOf.GetValueOrDefault(member, -1);
}

public static class Em3dBodies
{
    /// <summary>What a body carrying no net is named by: this, then its largest member's name.</summary>
    public const string GeneratedPrefix = "body:";

    /// <summary>A footprint covering more grid cells than this is tested against every other one rather than gridded.</summary>
    private const int MaxCellsPerFootprint = 64;

    /// <summary>The bodies of every conductor of <paramref name="problem"/>.</summary>
    public static Em3dBodyGrouping Group(Em3dProblem problem, Func<string, IEnumerable<string>>? netsOf = null)
        => Group(problem.Solids, problem.Sheets, problem.CylinderSheets, netsOf);

    /// <summary>
    /// The bodies of the conductor solids (role <see cref="Em3dRole.Conductor"/>), sheets and cylindrical sheets given.
    /// <paramref name="netsOf"/> gives an object's nets; null gives none.
    /// </summary>
    public static Em3dBodyGrouping Group(IEnumerable<Em3dSolid> solids, IEnumerable<Em3dSheet> sheets,
                                         IEnumerable<Em3dCylinderSheet>? cylinderSheets, Func<string, IEnumerable<string>>? netsOf)
    {
        var names = new List<string>();
        var parts = new List<Part>();
        foreach (var s in solids)
            if (s.Role == Em3dRole.Conductor) Add(names, parts, s.Name, PartsOf(s.Primitive));
        foreach (var sh in sheets) Add(names, parts, sh.Name, PartsOf(sh));
        foreach (var cs in cylinderSheets ?? []) Add(names, parts, cs.Name, [Part.Rect(cs.WorldBounds())]);

        var root = new int[names.Count];
        for (int k = 0; k < root.Length; k++) root[k] = k;
        int Find(int k)
        {
            while (root[k] != k) { root[k] = root[root[k]]; k = root[k]; }
            return k;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) root[Math.Max(ra, rb)] = Math.Min(ra, rb);    // the lower index is the root: deterministic
        }

        long visits = 0;
        if (parts.Count > 1)
        {
            double x0 = parts.Min(p => p.X0), y0 = parts.Min(p => p.Y0), x1 = parts.Max(p => p.X1), y1 = parts.Max(p => p.Y1);
            double z0 = parts.Min(p => p.Z0), z1 = parts.Max(p => p.Z1);
            double extent = Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
            double tol = Math.Max(1e-12, 1e-9 * extent);

            void Test(Part a, Part b)
            {
                visits++;
                if (a.Owner == b.Owner || Find(a.Owner) == Find(b.Owner)) return;
                if (Touch(a, b, tol)) Union(a.Owner, b.Owner);
            }

            // The grid's pitch: twice the median footprint, so an ordinary pad or barrel covers a cell or four.
            var sizes = parts.Select(p => Math.Max(p.X1 - p.X0, p.Y1 - p.Y0)).Order().ToArray();
            double h = 2 * sizes[sizes.Length / 2];
            if (!(h > 0)) h = Math.Max(extent / 64, tol);
            long Cell(double x) => (long)Math.Floor((x - x0) / h);
            const long Stride = 1L << 31;

            var big = new List<Part>();
            var small = new List<Part>();
            var grid = new Dictionary<long, List<Part>>();
            foreach (var p in parts)
            {
                long i0 = Cell(p.X0 - tol), i1 = Cell(p.X1 + tol), j0 = Cell(p.Y0 - tol), j1 = Cell(p.Y1 + tol);
                if ((i1 - i0 + 1) * (j1 - j0 + 1) > MaxCellsPerFootprint) { big.Add(p); continue; }
                small.Add(p);
                for (long i = i0; i <= i1; i++)
                    for (long j = j0; j <= j1; j++)
                        (grid.TryGetValue(i * Stride + j, out var list) ? list : grid[i * Stride + j] = []).Add(p);
            }
            foreach (var (key, list) in grid)
                for (int a = 0; a < list.Count; a++)
                    for (int b = a + 1; b < list.Count; b++)
                    {
                        var (p, q) = (list[a], list[b]);
                        // Once per pair: in the cell holding the lower-left corner of the two rectangles' overlap, which both
                        // are in whenever they overlap at all (and when they do not, they cannot touch).
                        if (Cell(Math.Max(p.X0, q.X0) - tol) * Stride + Cell(Math.Max(p.Y0, q.Y0) - tol) != key) continue;
                        Test(p, q);
                    }
            for (int a = 0; a < big.Count; a++)
            {
                for (int b = a + 1; b < big.Count; b++) Test(big[a], big[b]);
                foreach (var q in small) Test(big[a], q);
            }
        }

        // Members per root, in problem order; bodies in their first member's order.
        var areaOf = new double[names.Count];
        foreach (var p in parts) areaOf[p.Owner] += p.Area;
        var members = new Dictionary<int, List<int>>();
        for (int k = 0; k < names.Count; k++)
            (members.TryGetValue(Find(k), out var l) ? l : members[Find(k)] = []).Add(k);
        var bodies = new List<Em3dBody>();
        foreach (var (_, ks) in members.OrderBy(kv => kv.Value[0]))
        {
            var nets = new List<string>();
            foreach (int k in ks)
                foreach (string n in netsOf?.Invoke(names[k]) ?? [])
                    if (n is { Length: > 0 } && !nets.Contains(n, StringComparer.Ordinal)) nets.Add(n);
            int largest = ks[0];
            double area = -1;
            foreach (int k in ks)
            {
                if (areaOf[k] > area) (area, largest) = (areaOf[k], k);
            }
            bodies.Add(new Em3dBody(nets.Count > 0 ? string.Join(" + ", nets) : GeneratedPrefix + names[largest],
                                    [.. ks.Select(k => names[k])], nets));
        }
        return new Em3dBodyGrouping(bodies, visits);
    }

    private static void Add(List<string> names, List<Part> parts, string name, IEnumerable<Part> of)
    {
        int owner = names.Count;
        names.Add(name);
        foreach (var p in of) parts.Add(p with { Owner = owner });
    }

    // ── Footprints ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private enum Kind { Polygon, Disc }

    /// <summary>A plan footprint between two heights: a polygon with holes, or a disc. A rectangle is a four-vertex polygon.</summary>
    private sealed record Part(Kind Kind, Point2[] Outer, Point2[][] Holes, double Cx, double Cy, double R,
                               double X0, double Y0, double X1, double Y1, double Z0, double Z1)
    {
        public int Owner { get; init; }

        public double Area => Kind == Kind.Disc ? Math.PI * R * R
            : Math.Max(0, Math.Abs(Signed(Outer)) - Holes.Sum(h => Math.Abs(Signed(h))));

        public static Part Polygon(IReadOnlyList<Point2> outer, IReadOnlyList<IReadOnlyList<Point2>> holes, double z0, double z1)
        {
            double x0 = outer.Min(q => q.X), y0 = outer.Min(q => q.Y), x1 = outer.Max(q => q.X), y1 = outer.Max(q => q.Y);
            return new(Kind.Polygon, [.. outer], [.. holes.Select(h => h.ToArray())], 0, 0, 0, x0, y0, x1, y1,
                       Math.Min(z0, z1), Math.Max(z0, z1));
        }

        public static Part Rect((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
            => Polygon([new(b.X0, b.Y0), new(b.X1, b.Y0), new(b.X1, b.Y1), new(b.X0, b.Y1)], [], b.Z0, b.Z1);

        public static Part Disc(double cx, double cy, double r, double z0, double z1)
            => new(Kind.Disc, [], [], cx, cy, r, cx - r, cy - r, cx + r, cy + r, Math.Min(z0, z1), Math.Max(z0, z1));

        private EdgeIndex? _index;
        private bool _indexed;

        /// <summary>A large polygon's edges and holes on a grid (a plane cleared round thousands of vias), built on first use;
        /// null for a small one, whose edges are simply walked.</summary>
        private EdgeIndex? Index
        {
            get
            {
                if (_indexed) return _index;
                _indexed = true;
                int edges = Outer.Length + Holes.Sum(h => h.Length);
                return _index = Kind == Kind.Polygon && edges > IndexedEdges ? new EdgeIndex(this) : null;
            }
        }

        /// <summary>Every edge whose rectangle meets the given one (all of them for a small polygon).</summary>
        public IEnumerable<(Point2 A, Point2 B)> EdgesNear(double x0, double y0, double x1, double y1)
        {
            if (Index is { } ix) return ix.Edges(x0, y0, x1, y1);
            return Rings(this).SelectMany(r => Enumerable.Range(0, r.Length).Select(k => (r[k], r[(k + 1) % r.Length])));
        }

        /// <summary>In the outer ring and in none of the holes (even-odd).</summary>
        public bool Contains(double x, double y)
        {
            if (x < X0 || x > X1 || y < Y0 || y > Y1) return false;
            if (Index is { } ix) return ix.Contains(x, y);
            if (!InRing(Outer, x, y)) return false;
            foreach (var h in Holes) if (h.Length >= 3 && InRing(h, x, y)) return false;
            return true;
        }
    }

    /// <summary>Above this many edges a polygon's edges and holes are gridded.</summary>
    private const int IndexedEdges = 64;

    private sealed class EdgeIndex
    {
        private const int MaxCellsPerItem = 64;
        private readonly double _x0, _y0, _hx, _hy;
        private readonly int _nx, _ny;
        private readonly (Point2 A, Point2 B)[] _edges;
        private readonly List<int>?[] _edgeCells;
        private readonly List<int> _longEdges = [];
        private readonly Point2[][] _holes;
        private readonly List<int>?[] _holeCells;
        private readonly List<int> _longHoles = [];
        private readonly Point2[] _outer;
        private readonly int[] _seen;
        private int _stamp;

        public EdgeIndex(Part p)
        {
            _outer = p.Outer;
            _holes = [.. p.Holes.Where(h => h.Length >= 3)];
            var edges = new List<(Point2, Point2)>();
            foreach (var r in Rings(p))
                for (int k = 0; k < r.Length; k++) edges.Add((r[k], r[(k + 1) % r.Length]));
            _edges = [.. edges];
            _seen = new int[_edges.Length];
            int side = Math.Clamp((int)Math.Ceiling(Math.Sqrt(_edges.Length)), 1, 256);
            (_nx, _ny, _x0, _y0) = (side, side, p.X0, p.Y0);
            _hx = Math.Max((p.X1 - p.X0) / side, 1e-30);
            _hy = Math.Max((p.Y1 - p.Y0) / side, 1e-30);
            _edgeCells = new List<int>?[_nx * _ny];
            _holeCells = new List<int>?[_nx * _ny];
            for (int e = 0; e < _edges.Length; e++)
            {
                var (a, b) = _edges[e];
                Put(_edgeCells, _longEdges, e, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            }
            for (int h = 0; h < _holes.Length; h++)
                Put(_holeCells, _longHoles, h, _holes[h].Min(q => q.X), _holes[h].Min(q => q.Y), _holes[h].Max(q => q.X), _holes[h].Max(q => q.Y));
        }

        private (int I0, int J0, int I1, int J1) Cells(double x0, double y0, double x1, double y1)
            => (Math.Clamp((int)Math.Floor((x0 - _x0) / _hx), 0, _nx - 1), Math.Clamp((int)Math.Floor((y0 - _y0) / _hy), 0, _ny - 1),
                Math.Clamp((int)Math.Floor((x1 - _x0) / _hx), 0, _nx - 1), Math.Clamp((int)Math.Floor((y1 - _y0) / _hy), 0, _ny - 1));

        private void Put(List<int>?[] cells, List<int> longList, int id, double x0, double y0, double x1, double y1)
        {
            var (i0, j0, i1, j1) = Cells(x0, y0, x1, y1);
            if ((i1 - i0 + 1) * (j1 - j0 + 1) > MaxCellsPerItem) { longList.Add(id); return; }
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++) (cells[i * _ny + j] ??= []).Add(id);
        }

        public List<(Point2 A, Point2 B)> Edges(double x0, double y0, double x1, double y1)
        {
            var found = new List<(Point2, Point2)>();
            int stamp = ++_stamp;
            foreach (int e in _longEdges) { _seen[e] = stamp; found.Add(_edges[e]); }
            var (i0, j0, i1, j1) = Cells(x0, y0, x1, y1);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    if (_edgeCells[i * _ny + j] is { } list)
                        foreach (int e in list)
                            if (_seen[e] != stamp) { _seen[e] = stamp; found.Add(_edges[e]); }
            return found;
        }

        public bool Contains(double x, double y)
        {
            if (!InRing(_outer, x, y)) return false;
            var (i, j, _, _) = Cells(x, y, x, y);
            foreach (int h in _longHoles) if (InRing(_holes[h], x, y)) return false;
            if (_holeCells[i * _ny + j] is { } list)
                foreach (int h in list) if (InRing(_holes[h], x, y)) return false;
            return true;
        }
    }

    private static IEnumerable<Part> PartsOf(Em3dPrimitive p)
    {
        switch (p)
        {
            case Em3dExtrudedPolygon e when e.Outline.Count >= 3:
                yield return Part.Polygon(e.Outline, e.Holes, e.ZBottom, e.ZTop);
                break;
            case Em3dBox b:
                yield return Part.Rect((b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z));
                break;
            case Em3dCylinder c when c.AxisStart.X == c.AxisEnd.X && c.AxisStart.Y == c.AxisEnd.Y:
                yield return Part.Disc(c.AxisStart.X, c.AxisStart.Y, c.Radius, c.AxisStart.Z, c.AxisEnd.Z);
                break;
            case Em3dSphere s:
                yield return Part.Disc(s.Center.X, s.Center.Y, s.Radius, s.Center.Z - s.Radius, s.Center.Z + s.Radius);
                break;
            case Em3dTruncatedSphere t:
            {
                var (_, _, z0, _, _, z1) = Em3dProblem.Bounds(t);
                yield return Part.Disc(t.Center.X, t.Center.Y, t.Radius, z0, z1);
                break;
            }
            case Em3dSweep w when w.Rings.Count >= 2:
                // One rectangle per segment, round the two rings that bound it.
                for (int k = 0; k + 1 < w.Rings.Count; k++)
                {
                    var q = w.Rings[k].Concat(w.Rings[k + 1]).ToList();
                    yield return Part.Rect((q.Min(v => v.X), q.Min(v => v.Y), q.Min(v => v.Z), q.Max(v => v.X), q.Max(v => v.Y), q.Max(v => v.Z)));
                }
                break;
            default:
                yield return Part.Rect(Em3dProblem.Bounds(p));
                break;
        }
    }

    private static IEnumerable<Part> PartsOf(Em3dSheet sh)
    {
        if (sh.Frame is null && sh.Outline.Count >= 3) yield return Part.Polygon(sh.Outline, sh.Holes, sh.Z, sh.Z);
        else yield return Part.Rect(sh.WorldBounds());
    }

    // ── Contact ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool Touch(Part a, Part b, double tol)
    {
        if (a.Z0 > b.Z1 + tol || b.Z0 > a.Z1 + tol) return false;
        if (a.X0 > b.X1 + tol || b.X0 > a.X1 + tol || a.Y0 > b.Y1 + tol || b.Y0 > a.Y1 + tol) return false;
        return (a.Kind, b.Kind) switch
        {
            (Kind.Disc, Kind.Disc) => Sq(a.Cx - b.Cx) + Sq(a.Cy - b.Cy) <= Sq(a.R + b.R + tol),
            (Kind.Disc, Kind.Polygon) => PolygonDisc(b, a.Cx, a.Cy, a.R, tol),
            (Kind.Polygon, Kind.Disc) => PolygonDisc(a, b.Cx, b.Cy, b.R, tol),
            _ => PolygonPolygon(a, b, tol),
        };
    }

    private static bool PolygonDisc(Part poly, double cx, double cy, double r, double tol)
    {
        if (poly.Contains(cx, cy)) return true;
        double reach = r + tol;
        foreach (var (a, b) in poly.EdgesNear(cx - reach, cy - reach, cx + reach, cy + reach))
            if (SegmentDistanceSq(cx, cy, a, b) <= reach * reach) return true;
        return false;
    }

    private static bool PolygonPolygon(Part a, Part b, double tol)
    {
        // An edge of one meets an edge of the other, or (no edges meeting) one lies inside the other.
        foreach (var (p, q) in a.EdgesNear(b.X0 - tol, b.Y0 - tol, b.X1 + tol, b.Y1 + tol))
            foreach (var (u, v) in b.EdgesNear(Math.Min(p.X, q.X) - tol, Math.Min(p.Y, q.Y) - tol,
                                               Math.Max(p.X, q.X) + tol, Math.Max(p.Y, q.Y) + tol))
                if (SegmentsWithin(p, q, u, v, tol)) return true;
        return b.Contains(a.Outer[0].X, a.Outer[0].Y) || a.Contains(b.Outer[0].X, b.Outer[0].Y);
    }

    private static IEnumerable<Point2[]> Rings(Part p)
    {
        yield return p.Outer;
        foreach (var h in p.Holes) if (h.Length >= 2) yield return h;
    }

    private static bool InRing(Point2[] ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            if ((ring[i].Y > y) != (ring[j].Y > y) &&
                x < (ring[j].X - ring[i].X) * (y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        return inside;
    }

    private static bool SegmentsWithin(Point2 a, Point2 b, Point2 c, Point2 d, double tol)
    {
        double o1 = Cross(a, b, c), o2 = Cross(a, b, d), o3 = Cross(c, d, a), o4 = Cross(c, d, b);
        if (((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0)) && ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0))) return true;
        double t2 = tol * tol;
        return SegmentDistanceSq(c.X, c.Y, a, b) <= t2 || SegmentDistanceSq(d.X, d.Y, a, b) <= t2 ||
               SegmentDistanceSq(a.X, a.Y, c, d) <= t2 || SegmentDistanceSq(b.X, b.Y, c, d) <= t2;
    }

    private static double Cross(Point2 a, Point2 b, Point2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static double SegmentDistanceSq(double x, double y, Point2 a, Point2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        double t = len > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len, 0, 1) : 0;
        return Sq(x - (a.X + t * dx)) + Sq(y - (a.Y + t * dy));
    }

    private static double Signed(Point2[] ring)
    {
        double s = 0;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++) s += (ring[j].X + ring[i].X) * (ring[j].Y - ring[i].Y);
        return s / 2;
    }

    private static double Sq(double v) => v * v;
}
