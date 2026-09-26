// brief-em3d-47 R-em3d47-3 — the general operations on a closed polyhedral solid: push/pull, a free face move and
// a vertex move; folding that keeps every face planar; and the self-intersection check that refuses what would
// not be a solid.
//
// PUSH/PULL KEEPS THE NEIGHBOURS' PLANES (R-em3d47-3a). At a vertex where exactly three faces meet, the moved
// vertex is the intersection of the moved plane with the other two — which is a point of the ONE edge at that
// vertex the moved face does not own, so it is computed along that edge's own integer vector: v + e·d·|N|/(N·e).
// Every point of that line lies on both neighbours, so they keep their planes up to the one rounding to DBU, and
// where the moved face is axis-aligned |N| is an integer and the whole step is Int128 arithmetic, rounded once.
// Where more than three faces meet, the neighbours' planes need not share a point, and the vertex is translated
// by d·n instead; its neighbours then fold (R-em3d47-3c).
//
// EVERY EDIT IS LOCAL (gate 6). The vertices an edit moves, the faces those vertices lie on (the TOUCHED faces),
// their planarity, their folding and their intersection tests are all the kernel does; the untouched faces are
// neither re-planed nor re-tested against each other, because they have not moved. Stats counts it.
//
// A REFUSAL LEAVES THE SOURCE AS IT WAS. The operations are functions: the B-rep handed in is never changed, and a
// refused one comes back as the refusal and the would-be solid (Attempt), which the editor draws red.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD.Kernel;

/// <summary>What one kernel operation did — gate 6's counters.</summary>
public sealed class C3dKernelStats
{
    /// <summary>Vertices whose position the operation computed.</summary>
    public int VerticesMoved { get; internal set; }
    /// <summary>Faces lying on a moved vertex: the only ones re-planed, folded and tested.</summary>
    public int FacesTouched { get; internal set; }
    /// <summary>Face pairs that reached the triangle–triangle test.</summary>
    public int PairsTested { get; internal set; }
    /// <summary>Faces split into planar triangles.</summary>
    public int FacesFolded { get; internal set; }
}

/// <summary>A kernel operation's outcome: the new solid, or why not (with the would-be solid, to draw red), and each
/// folded face's pieces by name.</summary>
public sealed record C3dKernelResult(C3dBrep? Brep, string? Refusal, IReadOnlyDictionary<string, IReadOnlyList<string>> Folds,
                                     C3dBrep? Attempt, C3dKernelStats Stats)
{
    public bool Ok => Brep is not null;
}

/// <summary>How far a push/pull may go before an edge of the solid reaches zero length: |d| stays strictly below
/// <see cref="Distance"/> (DBU), and <see cref="Face"/> is the neighbour that would vanish.</summary>
public readonly record struct C3dNormalLimit(double Distance, string Face);

public static class C3dKernel
{
    /// <summary>R-em3d47-3c — a face is planar when no vertex is further than this from its plane, DBU.</summary>
    public const double PlanarityDbu = 1.0;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoFolds = new Dictionary<string, IReadOnlyList<string>>();

    // ── push/pull (R-em3d47-3a) ──────────────────────────────────────────────────────────────

    /// <summary>How one vertex of a pushed face moves per unit of d: along the edge its two neighbours share, or
    /// straight along the normal.</summary>
    private readonly record struct Motion(int Vertex, C3dPoint3 Edge, Int128 Ne, bool Translate);

    private static List<Motion> Motions(C3dBrep b, int face)
    {
        var (nx, ny, nz) = b.Newell(face);
        var list = new List<Motion>();
        var seen = new HashSet<int>();
        foreach (var ring in b.Faces[face].Rings())
            foreach (int v in ring)
            {
                if (!seen.Add(v)) continue;
                var others = b.Topology.FacesAt(v).Where(f => f != face).ToArray();
                if (others.Length == 2 && SharedNeighbour(b, v, others[0], others[1], face) is int w)
                {
                    var e = b.Vertices[w] - b.Vertices[v];
                    Int128 ne = e.X * nx + e.Y * ny + e.Z * nz;
                    if (ne != 0) { list.Add(new Motion(v, e, ne, false)); continue; }
                }
                list.Add(new Motion(v, default, 0, true));
            }
        return list;
    }

    /// <summary>The vertex joined to <paramref name="v"/> by an edge of both <paramref name="g"/> and
    /// <paramref name="h"/> — the edge the moved face <paramref name="skip"/> does not own.</summary>
    private static int? SharedNeighbour(C3dBrep b, int v, int g, int h, int skip)
    {
        foreach (int w in RingNeighbours(b.Faces[g], v))
            if (RingNeighbours(b.Faces[h], v).Contains(w) && !b.Faces[skip].Has(w)) return w;
        return null;
    }

    private static IEnumerable<int> RingNeighbours(C3dBrepFace f, int v)
    {
        foreach (var ring in f.Rings())
            for (int i = 0; i < ring.Length; i++)
                if (ring[i] == v)
                {
                    yield return ring[(i + ring.Length - 1) % ring.Length];
                    yield return ring[(i + 1) % ring.Length];
                }
    }

    /// <summary>The per-unit direction of a vertex's motion, DBU per DBU of d.</summary>
    private static (double X, double Y, double Z) Direction(C3dBrep b, int face, in Motion m)
    {
        var n = b.Normal(face);
        if (m.Translate) return n;
        double len = NewellLength(b, face);
        double t = len / (double)m.Ne;
        return (m.Edge.X * t, m.Edge.Y * t, m.Edge.Z * t);
    }

    private static double NewellLength(C3dBrep b, int face)
    {
        var (x, y, z) = b.Newell(face);
        double dx = (double)x, dy = (double)y, dz = (double)z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Where a vertex goes for a push/pull by <paramref name="d"/>: exact Int128 arithmetic rounded once when
    /// the face is axis-aligned, doubles rounded once otherwise.</summary>
    private static C3dPoint3 Moved(C3dBrep b, int face, in Motion m, long d)
    {
        var p = b.Vertices[m.Vertex];
        var axis = b.AxisOf(face);
        if (m.Translate)
        {
            if (axis is var (a, s)) return Add(p, a, s * d);
            var n = b.Normal(face);
            return new C3dPoint3(p.X + R(n.X * d), p.Y + R(n.Y * d), p.Z + R(n.Z * d));
        }
        if (axis is not null)
        {
            // |N| is the one non-zero component's magnitude: an integer, so t = d·|N| / (N·e) is a ratio of integers.
            var (nx, ny, nz) = b.Newell(face);
            Int128 len = Int128.Abs(nx + ny + nz);
            Int128 k = (Int128)d * len;
            return new C3dPoint3(p.X + RoundDiv(m.Edge.X * k, m.Ne), p.Y + RoundDiv(m.Edge.Y * k, m.Ne), p.Z + RoundDiv(m.Edge.Z * k, m.Ne));
        }
        double t = d * NewellLength(b, face) / (double)m.Ne;
        return new C3dPoint3(p.X + R(m.Edge.X * t), p.Y + R(m.Edge.Y * t), p.Z + R(m.Edge.Z * t));
    }

    /// <summary>
    /// R-em3d47-3a — the clamp: how far face <paramref name="face"/> may be pushed (<paramref name="sign"/> +1, outward)
    /// or pulled (−1) before an edge it moves reaches zero length, and the neighbour that edge belongs to. Null when
    /// nothing limits it.
    /// </summary>
    public static C3dNormalLimit? NormalLimit(C3dBrep b, int face, int sign)
    {
        var motions = Motions(b, face);
        var dir = new Dictionary<int, (double X, double Y, double Z)>();
        foreach (var m in motions) dir[m.Vertex] = Direction(b, face, m);
        double best = double.PositiveInfinity;
        string? name = null;
        foreach (var m in motions)
        {
            int v = m.Vertex;
            foreach (int f in b.Topology.FacesAt(v))
                foreach (int w in RingNeighbours(b.Faces[f], v))
                {
                    if (dir.ContainsKey(w) && w < v) continue;               // a moved–moved edge once
                    var uv = dir[v];
                    (double X, double Y, double Z) uw = dir.TryGetValue(w, out var q) ? q : (0.0, 0.0, 0.0);
                    double ex = b.Vertices[w].X - b.Vertices[v].X, ey = b.Vertices[w].Y - b.Vertices[v].Y, ez = b.Vertices[w].Z - b.Vertices[v].Z;
                    double dx = uw.X - uv.X, dy = uw.Y - uv.Y, dz = uw.Z - uv.Z;
                    double dd = dx * dx + dy * dy + dz * dz;
                    if (dd < 1e-24) continue;
                    double t = -(ex * dx + ey * dy + ez * dz) / dd;
                    double rx = ex + t * dx, ry = ey + t * dy, rz = ez + t * dz;
                    double e = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                    if (Math.Sqrt(rx * rx + ry * ry + rz * rz) > 1e-9 * e + 1e-9) continue;   // never collapses
                    double at = t * sign;
                    if (at <= 0 || at >= best) continue;
                    best = at;
                    name = EdgeOwner(b, v, w, face);
                }
        }
        return name is null ? null : new C3dNormalLimit(best, name);
    }

    /// <summary>The first face other than <paramref name="skip"/> owning edge v–w, else <paramref name="skip"/>'s name.</summary>
    private static string EdgeOwner(C3dBrep b, int v, int w, int skip)
    {
        foreach (int f in b.Topology.FacesAt(v))
            if (f != skip && RingNeighbours(b.Faces[f], v).Contains(w)) return b.Faces[f].Name;
        return b.Faces[skip].Name;
    }

    /// <summary>
    /// Push/pull: face <paramref name="face"/>'s plane moves <paramref name="d"/> DBU along its outward normal, and each
    /// of its vertices is recomputed where the moved plane meets its neighbours (R-em3d47-3a). Refused at or beyond
    /// the clamp, naming the neighbour; <paramref name="length"/> spells the distance.
    /// </summary>
    public static C3dKernelResult PushPull(C3dBrep b, int face, long d, Func<double, string>? length = null)
    {
        var stats = new C3dKernelStats();
        if (d == 0) return new C3dKernelResult(b, null, NoFolds, null, stats);
        var motions = Motions(b, face);
        var vertices = b.Vertices.ToList();
        foreach (var m in motions) vertices[m.Vertex] = Moved(b, face, m, d);
        stats.VerticesMoved = motions.Count;
        if (NormalLimit(b, face, Math.Sign(d)) is { } limit && Math.Abs(d) >= limit.Distance)
        {
            string at = length?.Invoke(limit.Distance) ?? $"{limit.Distance:0.###} DBU";
            return new C3dKernelResult(null, $"'{limit.Face}' would vanish at {at}.", NoFolds, new C3dBrep(vertices, b.Topology), stats);
        }
        return Finish(b, vertices, [.. motions.Select(m => m.Vertex)], face, stats);
    }

    // ── the free moves (R-em3d47-3b, -3e) ───────────────────────────────────────────────────

    /// <summary>Every vertex of face <paramref name="face"/> translated by <paramref name="by"/>; the neighbours tilt, or fold.</summary>
    public static C3dKernelResult MoveFace(C3dBrep b, int face, C3dPoint3 by)
    {
        var stats = new C3dKernelStats();
        if (by == default) return new C3dKernelResult(b, null, NoFolds, null, stats);
        var moved = b.Faces[face].Rings().SelectMany(r => r).Distinct().ToArray();
        var vertices = b.Vertices.ToList();
        foreach (int v in moved) vertices[v] = b.Vertices[v] + by;
        stats.VerticesMoved = moved.Length;
        return Finish(b, vertices, moved, face, stats);
    }

    /// <summary>Vertex <paramref name="vertex"/> moved to <paramref name="to"/>; the faces on it tilt, or fold.</summary>
    public static C3dKernelResult MoveVertex(C3dBrep b, int vertex, C3dPoint3 to)
    {
        var stats = new C3dKernelStats();
        if (b.Vertices[vertex] == to) return new C3dKernelResult(b, null, NoFolds, null, stats);
        var vertices = b.Vertices.ToList();
        vertices[vertex] = to;
        stats.VerticesMoved = 1;
        return Finish(b, vertices, [vertex], -1, stats);
    }

    // ── folding and the check (R-em3d47-3c, -3d) ─────────────────────────────────────────────

    private static C3dKernelResult Finish(C3dBrep source, List<C3dPoint3> vertices, int[] moved, int primary, C3dKernelStats stats)
    {
        var topo = source.Topology;
        var touchedSet = new SortedSet<int>();
        foreach (int v in moved) foreach (int f in topo.FacesAt(v)) touchedSet.Add(f);
        int[] touched = [.. touchedSet];
        stats.FacesTouched = touched.Length;

        // Fold what is no longer flat. A face that merely tilts stays one face.
        var probe = new C3dBrep(vertices, topo);
        Dictionary<int, List<C3dBrepFace>>? pieces = null;
        var folds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (int t in touched)
        {
            if (probe.IsPlanar(t)) continue;
            var p = Fold(probe, t);
            (pieces ??= [])[t] = p;
            folds[topo.Faces[t].Name] = [.. p.Select(q => q.Name)];
            stats.FacesFolded++;
        }

        C3dBrep result;
        int[] newOfOld;                   // an untouched or unfolded face's new index
        var touchedNew = new List<int>();
        if (pieces is null)
        {
            result = probe;
            newOfOld = [.. Enumerable.Range(0, topo.Faces.Count)];
            touchedNew.AddRange(touched);
        }
        else
        {
            var faces = new List<C3dBrepFace>(topo.Faces.Count + pieces.Values.Sum(p => p.Count));
            newOfOld = new int[topo.Faces.Count];
            for (int f = 0; f < topo.Faces.Count; f++)
            {
                if (pieces.TryGetValue(f, out var p))
                {
                    newOfOld[f] = -1;
                    foreach (var q in p) { touchedNew.Add(faces.Count); faces.Add(q); }
                    continue;
                }
                newOfOld[f] = faces.Count;
                if (touchedSet.Contains(f)) touchedNew.Add(faces.Count);
                faces.Add(topo.Faces[f]);
            }
            result = new C3dBrep(vertices, new C3dBrepTopology(faces, vertices.Count));
        }

        if (Check(source, result, touched, touchedSet, touchedNew, newOfOld, primary, stats) is { } why)
            return new C3dKernelResult(null, why, folds, result, stats);
        return new C3dKernelResult(result, null, folds, null, stats);
    }

    /// <summary>
    /// Face <paramref name="f"/> split into planar triangles by ear clipping in its best-fit plane (its holes kept out of
    /// every triangle), each wound as the face was and named <c>&lt;face&gt;.&lt;i&gt;</c>.
    /// </summary>
    private static List<C3dBrepFace> Fold(C3dBrep b, int f)
    {
        var face = b.Faces[f];
        var nd = b.Normal(f);
        var list = new List<C3dBrepFace>();
        int i = 0;
        foreach (var (a, c, e) in Triangulate(b, f))
        {
            var (x, y, z) = C3dExact.Cross(b.Vertices[a], b.Vertices[c], b.Vertices[e]);
            bool flip = (double)x * nd.X + (double)y * nd.Y + (double)z * nd.Z < 0;
            string name;
            do name = $"{face.Name}.{i++}"; while (b.Topology.IndexOf(name) >= 0);
            list.Add(new C3dBrepFace(name, flip ? [a, e, c] : [a, c, e], []));
        }
        return list;
    }

    /// <summary>Face <paramref name="f"/>'s triangles, as vertex indices: its rings projected along the dominant axis of
    /// its normal and ear-clipped (Em3dPolygonTriangulation, holes bridged).</summary>
    public static List<(int A, int B, int C)> Triangulate(C3dBrep b, int f)
    {
        var face = b.Faces[f];
        var (nx, ny, nz) = b.Newell(f);
        var ax = Int128.Abs(nx); var ay = Int128.Abs(ny); var az = Int128.Abs(nz);
        int drop = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        Point2 P(int v)
        {
            var p = b.Vertices[v];
            return drop switch { 0 => new Point2(p.Y, p.Z), 1 => new Point2(p.Z, p.X), _ => new Point2(p.X, p.Y) };
        }
        var index = new List<int>(face.Outer);
        foreach (var h in face.Holes) index.AddRange(h);
        var tris = Em3dPolygonTriangulation.Triangulate([.. face.Outer.Select(P)],
                                                        [.. face.Holes.Select(h => (IReadOnlyList<Point2>)[.. h.Select(P)])]);
        return [.. tris.Select(t => (index[t.A], index[t.B], index[t.C]))];
    }

    private static string? Check(C3dBrep source, C3dBrep result, int[] touchedOld, SortedSet<int> touchedOldSet, List<int> touchedNew,
                                 int[] newOfOld, int primary, C3dKernelStats stats)
    {
        // 1. Every touched face has positive area, and no edge of it has shrunk to nothing.
        foreach (int t in touchedNew)
        {
            var face = result.Faces[t];
            bool degenerate = face.Rings().Any(r => r.Where((v, i) => result.Vertices[v] == result.Vertices[r[(i + 1) % r.Length]]).Any());
            var (x, y, z) = result.Newell(t);
            if (degenerate || (x == 0 && y == 0 && z == 0)) return $"'{face.Name}' would vanish.";
        }

        // 2. No two faces that share no vertex meet: the touched against the untouched (whose bounds are the source's,
        // so the source's hierarchy finds them), and the touched against each other.
        var triangles = new Dictionary<int, List<(int A, int B, int C)>>();
        List<(int A, int B, int C)> Tris(int f) => triangles.TryGetValue(f, out var l) ? l : triangles[f] = Triangulate(result, f);
        var hits = new List<int>();
        foreach (int t in touchedNew)
        {
            var own = new HashSet<int>(result.Faces[t].Rings().SelectMany(r => r));
            var bt = result.Bounds(t);
            hits.Clear();
            source.Bvh.Query(bt.X0, bt.Y0, bt.Z0, bt.X1, bt.Y1, bt.Z1, hits);
            hits.Sort();
            foreach (int o in hits)
            {
                if (touchedOldSet.Contains(o)) continue;
                int on = newOfOld[o];
                if (result.Faces[on].Rings().Any(r => r.Any(own.Contains))) continue;
                stats.PairsTested++;
                if (FacesMeet(result, t, on, Tris)) return $"'{result.Faces[t].Name}' would pass through '{result.Faces[on].Name}'.";
            }
        }
        for (int i = 0; i < touchedNew.Count; i++)
        {
            int t = touchedNew[i];
            var own = new HashSet<int>(result.Faces[t].Rings().SelectMany(r => r));
            var bt = result.Bounds(t);
            for (int j = i + 1; j < touchedNew.Count; j++)
            {
                int u = touchedNew[j];
                var bu = result.Bounds(u);
                if (bu.X0 > bt.X1 || bu.X1 < bt.X0 || bu.Y0 > bt.Y1 || bu.Y1 < bt.Y0 || bu.Z0 > bt.Z1 || bu.Z1 < bt.Z0) continue;
                if (result.Faces[u].Rings().Any(r => r.Any(own.Contains))) continue;
                stats.PairsTested++;
                if (FacesMeet(result, t, u, Tris)) return $"'{result.Faces[t].Name}' would pass through '{result.Faces[u].Name}'.";
            }
        }

        // 3. The volume stays positive, and no face that kept its shape turned over. Only the touched faces' terms change.
        Int128 volume = source.Volume6;
        foreach (int t in touchedOld) volume -= source.VolumeTerm(t);
        foreach (int t in touchedNew) volume += result.VolumeTerm(t);
        string? flipped = null;
        foreach (int t in touchedOld)
            if (newOfOld[t] >= 0 && Dot(source.Normal(t), result.Normal(newOfOld[t])) < 0) { flipped = source.Faces[t].Name; break; }
        if (volume > 0 && flipped is null) return null;
        if (primary >= 0 && PassedThrough(source, result, primary, newOfOld, touchedOldSet) is { } other)
            return $"'{source.Faces[primary].Name}' would pass through '{other}'.";
        return flipped is not null ? $"'{flipped}' would turn inside out." : "The solid would turn inside out.";
    }

    /// <summary>The untouched face, facing the moved one, whose plane the moved face crossed — the words for "you pushed
    /// it through the other side".</summary>
    private static string? PassedThrough(C3dBrep source, C3dBrep result, int primary, int[] newOfOld, SortedSet<int> touched)
    {
        if (newOfOld[primary] < 0) return null;
        var n = source.Normal(primary);
        var p0 = source.Vertices[source.Faces[primary].Outer[0]];
        var p1 = result.Vertices[result.Faces[newOfOld[primary]].Outer[0]];
        for (int o = 0; o < source.Faces.Count; o++)
        {
            if (touched.Contains(o)) continue;
            var m = source.Normal(o);
            if (Dot(n, m) > -1 + 1e-9) continue;
            var q = source.Vertices[source.Faces[o].Outer[0]];
            double before = n.X * (p0.X - q.X) + n.Y * (p0.Y - q.Y) + n.Z * (p0.Z - q.Z);
            double after = n.X * (p1.X - q.X) + n.Y * (p1.Y - q.Y) + n.Z * (p1.Z - q.Z);
            if (before > 0 && after <= 0) return source.Faces[o].Name;
        }
        return null;
    }

    private static bool FacesMeet(C3dBrep b, int f, int g, Func<int, List<(int A, int B, int C)>> tris)
    {
        var tf = tris(f);
        var tg = tris(g);
        var v = b.Vertices;
        foreach (var (a0, a1, a2) in tf)
        {
            var (ax0, ax1) = (Math.Min(v[a0].X, Math.Min(v[a1].X, v[a2].X)), Math.Max(v[a0].X, Math.Max(v[a1].X, v[a2].X)));
            var (ay0, ay1) = (Math.Min(v[a0].Y, Math.Min(v[a1].Y, v[a2].Y)), Math.Max(v[a0].Y, Math.Max(v[a1].Y, v[a2].Y)));
            var (az0, az1) = (Math.Min(v[a0].Z, Math.Min(v[a1].Z, v[a2].Z)), Math.Max(v[a0].Z, Math.Max(v[a1].Z, v[a2].Z)));
            foreach (var (b0, b1, b2) in tg)
            {
                if (Math.Max(v[b0].X, Math.Max(v[b1].X, v[b2].X)) < ax0 || Math.Min(v[b0].X, Math.Min(v[b1].X, v[b2].X)) > ax1) continue;
                if (Math.Max(v[b0].Y, Math.Max(v[b1].Y, v[b2].Y)) < ay0 || Math.Min(v[b0].Y, Math.Min(v[b1].Y, v[b2].Y)) > ay1) continue;
                if (Math.Max(v[b0].Z, Math.Max(v[b1].Z, v[b2].Z)) < az0 || Math.Min(v[b0].Z, Math.Min(v[b1].Z, v[b2].Z)) > az1) continue;
                if (C3dExact.TrianglesMeet(v[a0], v[a1], v[a2], v[b0], v[b1], v[b2])) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The whole check on a solid nobody vouched for (a primitive's result, a document's polyhedron): every face planar,
    /// closed, no two faces that share no vertex meeting, positive volume. Null when it is a solid.
    /// </summary>
    public static string? Validate(C3dBrep b)
    {
        if (b.ClosureProblem() is { } open) return $"Not closed: {open}.";
        for (int f = 0; f < b.Faces.Count; f++)
        {
            if (!b.IsPlanar(f)) return $"'{b.Faces[f].Name}' is not planar.";
            var (x, y, z) = b.Newell(f);
            if (x == 0 && y == 0 && z == 0) return $"'{b.Faces[f].Name}' has no area.";
        }
        var triangles = new Dictionary<int, List<(int A, int B, int C)>>();
        List<(int A, int B, int C)> Tris(int f) => triangles.TryGetValue(f, out var l) ? l : triangles[f] = Triangulate(b, f);
        var hits = new List<int>();
        for (int f = 0; f < b.Faces.Count; f++)
        {
            var own = new HashSet<int>(b.Faces[f].Rings().SelectMany(r => r));
            var bf = b.Bounds(f);
            hits.Clear();
            b.Bvh.Query(bf.X0, bf.Y0, bf.Z0, bf.X1, bf.Y1, bf.Z1, hits);
            foreach (int g in hits)
            {
                if (g <= f || b.Faces[g].Rings().Any(r => r.Any(own.Contains))) continue;
                if (FacesMeet(b, f, g, Tris)) return $"'{b.Faces[f].Name}' passes through '{b.Faces[g].Name}'.";
            }
        }
        return b.Volume6 > 0 ? null : "It encloses no positive volume.";
    }

    // ── arithmetic ───────────────────────────────────────────────────────────────────────────

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    private static C3dPoint3 Add(C3dPoint3 p, int axis, long by) => axis switch
    {
        0 => p with { X = p.X + by },
        1 => p with { Y = p.Y + by },
        _ => p with { Z = p.Z + by },
    };

    /// <summary>n / d rounded to the nearest integer, halves away from zero — exactly.</summary>
    public static long RoundDiv(Int128 n, Int128 d)
    {
        if (d < 0) { n = -n; d = -d; }
        Int128 q = Int128.DivRem(n, d).Quotient, r = n - q * d;
        if (2 * Int128.Abs(r) >= d) q += n >= 0 ? 1 : -1;
        return (long)q;
    }
}
