// A board's side walls, DRAWN (brief-em3d-138). An inner copper layer is absorbed into the dielectric slab above it, so
// where it reaches the board's outline its side face lies on the slab's side wall. The slab drew with Scene3DDepthTie.Behind's
// polygon offset so the copper's side won that coincidence — and the copper's TOP face, hidden inside the slab and meeting the
// wall along a line, passed the depth test within the offset of that line: a dotted line of lit copper-top pixels along every
// inner layer, at 1× on every backend. Lowering the slope factor only thinned it (1/16 → 1/128 halved it each step), and at 0
// the copper's side fought the wall. Any face that ends on a biased face leaks.
//
// So, as Scene3DBores does for via pads: carve, don't bias. Where a conductor's side lies on a slab's outline wall, the wall is
// not drawn (the copper's side face IS that surface), and the wall round it coincides with nothing, so it is drawn with no
// polygon offset — those triangles are a run at the END of the mesh, and the builder gives them a batch of their own that ties
// None (Scene3DBatch.Free). A face that ends on an unbiased face whose edge it shares cannot come through. A conductor just
// above or below the slab, reaching its edge, is a cut of no height: its face on the slab's cap ends on the wall the same way.
//
// Only the wall ROUND each conductor is free — its rectangle grown by the conductor's thickness, a quarter of the slab's at
// most — and the rest of the rebuilt wall keeps its slab's bias. Cull is off on every backend, and a whole free wall seen from
// behind met its slab's biased cap along the cap's rim, where it came through as dark dots along the board's far edge: the same
// leak, mirrored. A free piece meets a biased one in the same plane without overlapping it, so that seam cannot.
//
// VIEW ONLY. Nothing here reaches a solver or the document. Only side faces that are planes we can see are carved: a conductor
// that is an extruded polygon or a box, one of whose edges is collinear with the wall's, on the same side. Any other conductor
// shape whose bound reaches the wall (a sweep, a STEP body, a sheet standing on its edge), or a prism lying against the wall
// from OUTSIDE, leaves that wall as it was — uncarved and biased: a missed coincidence on an unbiased wall would fight, which
// is worse than the dots. A vertical cylinder is tested by its drawn ring against what is left of the wall, so a via barrel
// standing in the notch Scene3DBores cut in the board's edge does not stop the carve, while one tangent to the wall does.
//
// The wall is rebuilt as strips between heights (the slab's own, each conductor's, and its free band's), each strip
// triangulated between the vertices its two lines need: the cap's own on the slab's top and bottom, every conductor vertex on
// the wall's line at each height the conductor reaches, and every strip end above or below. So no conductor vertex is a
// T-junction on a wall piece. The caps are not touched: a conductor's vertex on the slab's bottom or top line is one on the cap's
// rim. Splitting the cap triangle there into a fan was tried and drew a dot of its own — the fan's slivers take a different
// slope-scaled bias — where the T-junction drew nothing in any view rendered.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

/// <summary>The conductors that can lie on a slab's outline walls, and the surface each such slab is drawn as.</summary>
internal sealed class Scene3DWalls
{
    /// <summary>Metres: geometry closer than this is the same place (the generator writes both from one number), as Scene3DBores.</summary>
    private const double Tol = 1e-9;

    /// <summary>A conductor whose side faces are planes: its rings and heights.</summary>
    private sealed record Prism(IReadOnlyList<IReadOnlyList<Point2>> Rings, double Z0, double Z1, double X0, double Y0, double X1, double Y1);

    /// <summary>Any other conductor: its bound, and a vertical cylinder's ring as the 3D view draws it.</summary>
    private sealed record Other(double X0, double Y0, double Z0, double X1, double Y1, double Z1, Point2[]? Ring);

    /// <summary>Where a conductor lies on a wall: breaks <see cref="Lo"/>..<see cref="Hi"/> along it, heights <see cref="ZLo"/>..
    /// <see cref="ZHi"/> (indices into the wall's break and height lists); and the wall round it drawn with no bias, breaks
    /// <see cref="FLo"/>..<see cref="FHi"/> and heights <see cref="ZFLo"/>..<see cref="ZFHi"/>.</summary>
    private readonly record struct Cut(int Lo, int Hi, int ZLo, int ZHi, int FLo, int FHi, int ZFLo, int ZFHi);

    /// <summary>A strip of a rebuilt wall: its row, the breaks on its bottom and top lines, and whether it draws with no bias.</summary>
    private sealed record Strip(int Row, int[] Bottom, int[] Top, bool Free)
    {
        public int Triangles => Bottom.Length + Top.Length - 2;
    }

    /// <summary>A break along a wall: its distance from the wall's start, its exact point, and whether it is a vertex of the wall
    /// as it was (and so of the slab's caps).</summary>
    private readonly record struct Break(double T, Point2 P, bool Own);

    /// <summary>One wall to rebuild: its face, its breaks and heights, which break intervals were wall, and the cuts.</summary>
    private sealed record Wall(int Face, bool Outward, Break[] Breaks, double[] Heights, bool[] InSpan, Cut[] Cuts)
    {
        public Strip[] Strips { get; init; } = [];
    }

    private readonly Prism[] _prisms;
    private readonly Other[] _others;

    private Scene3DWalls(Prism[] prisms, Other[] others)
    {
        _prisms = prisms;
        _others = others;
    }

    /// <summary>The problem's conductors that can lie on a slab's wall, or null when nothing can be carved (no slab, or no prism
    /// conductor — which then costs nothing).</summary>
    public static Scene3DWalls? Of(Em3dProblem problem)
    {
        if (!problem.Solids.Any(s => s.Role == Em3dRole.Dielectric && s.Primitive is Em3dExtrudedPolygon or Em3dBox)) return null;
        var prisms = new List<Prism>();
        var others = new List<Other>();
        foreach (var s in problem.Solids)
        {
            if (s.Role != Em3dRole.Conductor) continue;
            var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(s.Primitive);
            switch (s.Primitive)
            {
                case Em3dExtrudedPolygon e:
                    prisms.Add(new Prism([e.Outline, .. e.Holes], e.ZBottom, e.ZTop, x0, y0, x1, y1));
                    break;
                case Em3dBox b:
                    prisms.Add(new Prism([Rectangle(b)], b.Min.Z, b.Max.Z, x0, y0, x1, y1));
                    break;
                case Em3dCylinder c when Vertical(c):
                    others.Add(new Other(x0, y0, z0, x1, y1, z1, Ring(c.AxisStart.X, c.AxisStart.Y, c.Radius)));
                    break;
                default:
                    others.Add(new Other(x0, y0, z0, x1, y1, z1, null));
                    break;
            }
        }
        // A sheet lying flat has no extent in z to lie on a wall with; one standing on its edge does.
        foreach (var sh in problem.Sheets)
            if (sh.Frame is not null && sh.WorldBounds() is var (x0, y0, z0, x1, y1, z1))
                others.Add(new Other(x0, y0, z0, x1, y1, z1, null));
        return prisms.Count == 0 ? null : new Scene3DWalls([.. prisms], [.. others]);
    }

    /// <summary>
    /// What slab <paramref name="s"/> is drawn as with its outline walls carved where a conductor's side lies on them — the cache
    /// key, the tessellation and the faces rebuilt — or null when no wall is. <paramref name="inner"/> is the surface it would be
    /// drawn as otherwise (its own, or Scene3DBores' carving), under <paramref name="innerKey"/>. The last <c>Free</c> triangles of
    /// the result are the wall round each conductor, drawn with no bias; the rest of a rebuilt wall keeps its slab's.
    /// </summary>
    public (object Key, Func<Em3dTriangleMesh> Make, IReadOnlySet<int> Faces, int Free)? Carved(Em3dSolid s, object innerKey, Em3dTriangleMesh inner)
    {
        if (s.Role != Em3dRole.Dielectric) return null;
        IReadOnlyList<Point2> outline;
        double z0, z1;
        Func<int, int> faceOf;
        switch (s.Primitive)
        {
            case Em3dExtrudedPolygon e: (outline, z0, z1, faceOf) = (e.Outline, e.ZBottom, e.ZTop, i => 2 + i); break;
            // Counter-clockwise from (min, min): its walls are y min, x max, y max, x min — box faces 2, 1, 3, 0 (as Scene3DBores).
            case Em3dBox b: (outline, z0, z1, faceOf) = (Rectangle(b), b.Min.Z, b.Max.Z, i => i switch { 0 => 2, 1 => 1, 2 => 3, _ => 0 }); break;
            default: return null;
        }
        double sx0 = double.MaxValue, sy0 = double.MaxValue, sx1 = double.MinValue, sy1 = double.MinValue;
        foreach (var p in outline) { sx0 = Math.Min(sx0, p.X); sy0 = Math.Min(sy0, p.Y); sx1 = Math.Max(sx1, p.X); sy1 = Math.Max(sy1, p.Y); }
        bool Near(double x0, double y0, double za, double x1, double y1, double zb, double overlap)
            => Math.Min(zb, z1) - Math.Max(za, z0) > overlap && x0 <= sx1 + Tol && x1 >= sx0 - Tol && y0 <= sy1 + Tol && y1 >= sy0 - Tol;
        // a prism just above or below the slab counts too: its face on the slab's cap ends on the wall
        var prisms = _prisms.Where(p => Near(p.X0, p.Y0, p.Z0, p.X1, p.Y1, p.Z1, -Tol)).ToList();
        if (prisms.Count == 0) return null;
        var others = _others.Where(o => Near(o.X0, o.Y0, o.Z0, o.X1, o.Y1, o.Z1, Tol)).ToList();

        bool ccw = Em3dPolygonTriangulation.SignedArea2(outline) > 0;
        Dictionary<int, List<Em3dTriangle>>? byFace = null;
        var walls = new List<Wall>();
        for (int i = 0; i < outline.Count; i++)
        {
            Point2 a = outline[i], b = outline[(i + 1) % outline.Count];
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (len <= Tol) continue;
            var u = new Point2((b.X - a.X) / len, (b.Y - a.Y) / len);
            var n = ccw ? new Point2(u.Y, -u.X) : new Point2(-u.Y, u.X);   // outward
            double T(Point2 p) => (p.X - a.X) * u.X + (p.Y - a.Y) * u.Y;
            double D(Point2 p) => (p.X - a.X) * n.X + (p.Y - a.Y) * n.Y;

            // Each prism's edges lying on the wall's line: on the inside, a cut; on the outside, the wall stays as it was. A prism
            // above or below the slab, reaching its edge, cuts nothing — its cap face ends on the wall, which leaks just the same
            // against a biased wall — and its vertices on the line are the wall's too (a cut of no height).
            var cuts = new List<(double Lo, Point2 PLo, double Hi, Point2 PHi, double Za, double Zb, double H, double M)>();
            bool blocked = false;
            foreach (var pr in prisms)
                for (int r = 0; r < pr.Rings.Count && !blocked; r++)
                {
                    bool within = Math.Min(z1, pr.Z1) - Math.Max(z0, pr.Z0) > Tol;
                    var ring = pr.Rings[r];
                    // the prism's outward normal is right of an edge on a counter-clockwise outline, left on a counter-clockwise hole
                    bool right = (Em3dPolygonTriangulation.SignedArea2(ring) > 0) == (r == 0);
                    for (int k = 0; k < ring.Count; k++)
                    {
                        Point2 p = ring[k], q = ring[(k + 1) % ring.Count];
                        if (Math.Abs(D(p)) > Tol || Math.Abs(D(q)) > Tol) continue;
                        double tp = T(p), tq = T(q);
                        double lo = Math.Max(0, Math.Min(tp, tq)), hi = Math.Min(len, Math.Max(tp, tq));
                        if (hi - lo <= Tol) continue;
                        double vx = q.X - p.X, vy = q.Y - p.Y;
                        double mx = right ? vy : -vy, my = right ? -vx : vx;
                        if (mx * n.X + my * n.Y < 0) { if (within) { blocked = true; break; } continue; }
                        Point2 At(double t) => Math.Abs(t - tp) <= Tol ? p : Math.Abs(t - tq) <= Tol ? q
                                             : t <= Tol ? a : t >= len - Tol ? b : new Point2(a.X + u.X * t, a.Y + u.Y * t);
                        // the free band round it: its thickness each way, a quarter of the slab at most — but not along the wall
                        // past the ends of one above or below, whose band lies on the cap's rim, which it covers only between them
                        double za = within ? Math.Max(z0, pr.Z0) : pr.Z1 <= z0 + Tol ? z0 : z1, h = Math.Min(pr.Z1 - pr.Z0, (z1 - z0) / 4);
                        cuts.Add((lo, At(lo), hi, At(hi), za, within ? Math.Min(z1, pr.Z1) : za, h, within ? h : 0));
                    }
                }
            if (blocked || cuts.Count == 0) continue;
            if (others.Any(o => o.Ring is null && SegmentMeetsBox(a, b, o))) continue;

            // The wall as the surface below drew it: its triangles (all of its face, all on its line), the breaks they have.
            byFace ??= Group(inner, outline.Count, faceOf);
            if (!byFace.TryGetValue(faceOf(i), out var tris)) continue;
            var raw = new List<(double T, Point2 P, bool Own)>();
            bool onLine = true;
            foreach (var t in tris)
                foreach (int v in (ReadOnlySpan<int>)[t.A, t.B, t.C])
                {
                    var w = inner.Vertices[v];
                    var p = new Point2(w.X, w.Y);
                    if (Math.Abs(D(p)) > Tol || (Math.Abs(w.Z - z0) > Tol && Math.Abs(w.Z - z1) > Tol)) onLine = false;
                    raw.Add((T(p), p, true));
                }
            if (!onLine) continue;
            Point2 On(double t) => t <= Tol ? a : t >= len - Tol ? b : new Point2(a.X + u.X * t, a.Y + u.Y * t);
            foreach (var c in cuts)
            {
                raw.Add((c.Lo, c.PLo, false)); raw.Add((c.Hi, c.PHi, false));
                raw.Add((Math.Max(0, c.Lo - c.M), On(Math.Max(0, c.Lo - c.M)), false));
                raw.Add((Math.Min(len, c.Hi + c.M), On(Math.Min(len, c.Hi + c.M)), false));
            }
            raw.Sort((x, y) => x.T != y.T ? x.T.CompareTo(y.T) : y.Own.CompareTo(x.Own));
            var breaks = new List<Break>();
            foreach (var (t, p, own) in raw)
                if (breaks.Count > 0 && t - breaks[^1].T <= Tol) { if (own && !breaks[^1].Own) breaks[^1] = breaks[^1] with { Own = true }; }
                else breaks.Add(new Break(t, p, own));
            int Index(double t)
            {
                int lo = 0, hi = breaks.Count - 1;
                while (lo < hi) { int m = (lo + hi) / 2; if (breaks[m].T < t - Tol) lo = m + 1; else hi = m; }
                return lo;
            }
            // Which break intervals were wall (a via's notch, cut by Scene3DBores, is not), tested at mid-height.
            double zm = (z0 + z1) / 2;
            var inSpan = new bool[breaks.Count - 1];
            for (int j = 0; j < inSpan.Length; j++)
            {
                double tm = (breaks[j].T + breaks[j + 1].T) / 2;
                inSpan[j] = tris.Any(t => Covers(T, inner, t, tm, zm));
            }
            // A vertical cylinder stops the carve where its drawn ring meets what is left of the wall, by more than a point at
            // a notch's end; the ring tangent to the wall meets it in a point that is not at one.
            bool Meets(Point2[] ring)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                for (int k = 0; k < ring.Length; k++)
                {
                    Point2 p = ring[k], q = ring[(k + 1) % ring.Length];
                    double dp = D(p), dq = D(q);
                    if (Math.Abs(dp) <= Tol) { lo = Math.Min(lo, T(p)); hi = Math.Max(hi, T(p)); }
                    if ((dp < -Tol && dq > Tol) || (dp > Tol && dq < -Tol))
                    {
                        double f = dp / (dp - dq), t = T(new Point2(p.X + f * (q.X - p.X), p.Y + f * (q.Y - p.Y)));
                        lo = Math.Min(lo, t); hi = Math.Max(hi, t);
                    }
                }
                if (lo > hi) return false;
                for (int j = 0; j < inSpan.Length; j++)
                    if (inSpan[j] && lo < breaks[j + 1].T - Tol && hi > breaks[j].T + Tol) return true;
                return false;
            }
            if (others.Any(o => o.Ring is { } ring && Meets(ring))) continue;

            var heights = new List<double> { z0, z1 };
            foreach (var c in cuts)
                foreach (double z in (ReadOnlySpan<double>)[c.Za, c.Zb, Math.Max(z0, c.Za - c.H), Math.Min(z1, c.Zb + c.H)])
                    if (heights.All(h => Math.Abs(h - z) > Tol)) heights.Add(z);
            heights.Sort();
            int Level(double z) => heights.FindIndex(h => Math.Abs(h - z) <= Tol);
            var wall = new Wall(faceOf(i), ccw, [.. breaks], [.. heights], inSpan,
                                [.. cuts.Select(c => new Cut(Index(c.Lo), Index(c.Hi), Level(c.Za), Level(c.Zb),
                                                             Index(Math.Max(0, c.Lo - c.M)), Index(Math.Min(len, c.Hi + c.M)),
                                                             Level(Math.Max(z0, c.Za - c.H)), Level(Math.Min(z1, c.Zb + c.H))))]);
            walls.Add(wall with { Strips = Plan(wall) });
        }
        if (walls.Count == 0) return null;

        var key = new WalledKey(innerKey, string.Join(";", walls.Select(w => FormattableString.Invariant(
            $"{w.Face}:{string.Join(",", w.Cuts.Select(c => $"{w.Breaks[c.Lo].T:R}-{w.Breaks[c.Hi].T:R}@{w.Heights[c.ZLo]:R}-{w.Heights[c.ZHi]:R}"
                                                         + $"/{w.Breaks[c.FLo].T:R}-{w.Breaks[c.FHi].T:R}@{w.Heights[c.ZFLo]:R}-{w.Heights[c.ZFHi]:R}"))}"))));
        string name = s.Name;
        return (key, () => Rebuild(name, inner, walls), walls.Select(w => w.Face).ToHashSet(),
                walls.Sum(w => w.Strips.Where(t => t.Free).Sum(t => t.Triangles)));
    }

    /// <summary><paramref name="inner"/> with each wall's triangles replaced by its strips: the biased ones where the wall was, the
    /// free ones after every other triangle.</summary>
    private static Em3dTriangleMesh Rebuild(string name, Em3dTriangleMesh inner, List<Wall> walls)
    {
        var faces = walls.Select(w => w.Face).ToHashSet();
        var vertices = new List<Point3>(inner.Vertices);
        var triangles = inner.Triangles.Where(t => !faces.Contains(t.Face)).ToList();
        var index = walls.Select(_ => new Dictionary<(int, int), int>()).ToArray();
        foreach (bool free in (ReadOnlySpan<bool>)[false, true])
            for (int k = 0; k < walls.Count; k++)
                foreach (var strip in walls[k].Strips)
                    if (strip.Free == free) Emit(name, walls[k], strip, index[k], vertices, triangles);
        return new Em3dTriangleMesh(vertices, triangles);
    }

    /// <summary>
    /// The wall between each pair of heights, less the cuts covering it, as strips: each a run of break intervals that were wall
    /// and are all free or all biased, to be triangulated between the breaks its two lines need. A line needs the caps' own
    /// vertices on the slab's bottom and top, every strip end in the rows either side of it, and every vertex of a conductor
    /// reaching that height.
    /// <para>The FREE wall is the cut grown by the conductor's thickness each way (a quarter of the slab's at most): every line a
    /// hidden face of the conductor ends on, and the band beside it that face projects onto from any view. Not the whole wall —
    /// cull is off, and a free wall seen from behind meets its slab's biased cap along the cap's rim, where it came through as a
    /// dotted dark line along the board's far edge. A free piece meets a biased one in the same plane without overlapping it.</para>
    /// </summary>
    private static Strip[] Plan(Wall w)
    {
        int rows = w.Heights.Length - 1, spans = w.InSpan.Length;
        var present = new bool[rows][];
        var free = new bool[rows][];
        for (int r = 0; r < rows; r++)
        {
            present[r] = (bool[])w.InSpan.Clone();
            free[r] = new bool[spans];
            foreach (var c in w.Cuts)
            {
                if (c.ZLo <= r && c.ZHi >= r + 1)
                    for (int j = c.Lo; j < c.Hi; j++) present[r][j] = false;
                if (c.ZFLo <= r && c.ZFHi >= r + 1)
                    for (int j = c.FLo; j < c.FHi; j++) free[r][j] = true;
            }
        }
        var runs = new List<(int Row, int From, int To, bool Free)>();
        for (int r = 0; r < rows; r++)
            for (int j = 0; j < spans;)
            {
                if (!present[r][j]) { j++; continue; }
                int from = j;
                bool f = free[r][j];
                while (j < spans && present[r][j] && free[r][j] == f) j++;
                runs.Add((r, from, j, f));
            }
        var needs = new bool[rows + 1][];
        for (int k = 0; k <= rows; k++)
        {
            needs[k] = new bool[w.Breaks.Length];
            if (k == 0 || k == rows)
                for (int j = 0; j < w.Breaks.Length; j++) needs[k][j] = w.Breaks[j].Own;
            foreach (var c in w.Cuts)
                if (c.ZLo <= k && c.ZHi >= k) needs[k][c.Lo] = needs[k][c.Hi] = true;
        }
        foreach (var (r, from, to, _) in runs)
            foreach (int k in (ReadOnlySpan<int>)[r, r + 1]) needs[k][from] = needs[k][to] = true;
        Strip[] strips = [.. runs.Select(run => new Strip(run.Row,
            [.. Enumerable.Range(run.From, run.To - run.From + 1).Where(j => needs[run.Row][j])],
            [.. Enumerable.Range(run.From, run.To - run.From + 1).Where(j => needs[run.Row + 1][j])], run.Free))];
        return strips;
    }

    /// <summary>One strip's triangles: a zipper along it, counter-clockwise in (along, up), which faces out of a counter-clockwise
    /// outline.</summary>
    private static void Emit(string name, Wall w, Strip strip, Dictionary<(int, int), int> index, List<Point3> vertices, List<Em3dTriangle> triangles)
    {
        int V(int j, int k)
        {
            if (index.TryGetValue((j, k), out int v)) return v;
            vertices.Add(new Point3(w.Breaks[j].P.X, w.Breaks[j].P.Y, w.Heights[k]));
            return index[(j, k)] = vertices.Count - 1;
        }
        void Tri(int a, int b, int c)
            => triangles.Add(w.Outward ? new Em3dTriangle(a, b, c, name, w.Face) : new Em3dTriangle(a, c, b, name, w.Face));
        int r = strip.Row, i = 0, k = 0;
        var (bottom, top) = (strip.Bottom, strip.Top);
        while (i < bottom.Length - 1 || k < top.Length - 1)
        {
            if (k == top.Length - 1 || (i < bottom.Length - 1 && w.Breaks[bottom[i + 1]].T <= w.Breaks[top[k + 1]].T))
            {
                Tri(V(bottom[i], r), V(bottom[i + 1], r), V(top[k], r + 1));
                i++;
            }
            else
            {
                Tri(V(bottom[i], r), V(top[k + 1], r + 1), V(top[k], r + 1));
                k++;
            }
        }
    }

    /// <summary>The triangles of each outline wall's face (bottom 0 and top 1 are the caps; a bore's wall is no face).</summary>
    private static Dictionary<int, List<Em3dTriangle>> Group(Em3dTriangleMesh mesh, int edges, Func<int, int> faceOf)
    {
        var walls = Enumerable.Range(0, edges).Select(faceOf).ToHashSet();
        var by = new Dictionary<int, List<Em3dTriangle>>();
        foreach (var t in mesh.Triangles)
            if (walls.Contains(t.Face))
            {
                if (!by.TryGetValue(t.Face, out var list)) by[t.Face] = list = [];
                list.Add(t);
            }
        return by;
    }

    /// <summary>Whether wall triangle <paramref name="t"/> covers (<paramref name="tm"/>, <paramref name="zm"/>) in the wall's own
    /// (along, up) coordinates.</summary>
    private static bool Covers(Func<Point2, double> along, Em3dTriangleMesh mesh, Em3dTriangle t, double tm, double zm)
    {
        Point3 p0 = mesh.Vertices[t.A], p1 = mesh.Vertices[t.B], p2 = mesh.Vertices[t.C];
        double a0 = along(new Point2(p0.X, p0.Y)), a1 = along(new Point2(p1.X, p1.Y)), a2 = along(new Point2(p2.X, p2.Y));
        double Side(double ax, double az, double bx, double bz) => (bx - ax) * (zm - az) - (bz - az) * (tm - ax);
        double s0 = Side(a0, p0.Z, a1, p1.Z), s1 = Side(a1, p1.Z, a2, p2.Z), s2 = Side(a2, p2.Z, a0, p0.Z);
        return (s0 >= 0 && s1 >= 0 && s2 >= 0) || (s0 <= 0 && s1 <= 0 && s2 <= 0);
    }

    /// <summary>Whether segment <paramref name="a"/>→<paramref name="b"/> meets the bound of <paramref name="o"/> widened by Tol
    /// (Liang–Barsky).</summary>
    private static bool SegmentMeetsBox(Point2 a, Point2 b, Other o)
    {
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-300) return q >= 0;
            double r = q / p;
            if (p < 0) { if (r > t1) return false; t0 = Math.Max(t0, r); }
            else { if (r < t0) return false; t1 = Math.Min(t1, r); }
            return true;
        }
        return Clip(-dx, a.X - (o.X0 - Tol)) && Clip(dx, o.X1 + Tol - a.X) && Clip(-dy, a.Y - (o.Y0 - Tol)) && Clip(dy, o.Y1 + Tol - a.Y)
            && t0 <= t1;
    }

    private sealed record WalledKey(object Inner, string Walls);

    private static Point2[] Rectangle(Em3dBox b)
        => [new Point2(b.Min.X, b.Min.Y), new Point2(b.Max.X, b.Min.Y), new Point2(b.Max.X, b.Max.Y), new Point2(b.Min.X, b.Max.Y)];

    private static bool Vertical(Em3dCylinder c)
        => Math.Abs(c.AxisStart.X - c.AxisEnd.X) <= Tol && Math.Abs(c.AxisStart.Y - c.AxisEnd.Y) <= Tol && Math.Abs(c.AxisEnd.Z - c.AxisStart.Z) > Tol;

    /// <summary>A vertical cylinder's circle at the vertices the 3D view draws it with (Em3dTessellation; Scene3DBores' ring).</summary>
    private static Point2[] Ring(double x, double y, double r)
    {
        int n = Em3dTessellation.CylinderSegments;
        var ring = new Point2[n];
        for (int k = 0; k < n; k++)
        {
            double t = 2 * Math.PI * k / n;
            ring[k] = new Point2(x + r * Math.Cos(t), y + r * Math.Sin(t));
        }
        return ring;
    }
}
