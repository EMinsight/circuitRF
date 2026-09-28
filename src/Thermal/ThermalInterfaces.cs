// brief-em3d-76 R-em3d76-1b — a thermal boundary resistance between two touching solids, solved rather than noted.
//
// WHICH FACES. After the lowering's one fragment two touching solids share their contact surface — the SAME triangles and
// the same nodes on both sides (brief 72 Q3). So the solver finds a contact itself, from the tetrahedra alone: a face two
// tetrahedra of different regions share. Nothing about the contact has to survive Gmsh as a tag.
//
// THE SPLIT. A node on a resistive face is DUPLICATED: the tetrahedra around it (its star) fall into groups — two
// tetrahedra are in one group when they share a face through the node that is NOT resistive — and every group after the
// first is re-pointed at a copy of its own. So where three or more solids meet, a resistance-free contact among them keeps
// its nodes shared (the groups it joins are one group), and a node stays single wherever a perfect contact already
// connects the two sides round it. A zero resistance is perfect contact: it splits nothing (never a division by zero).
//
// THE ELEMENTS. Each resistive face becomes an interface element: its triangle on side A and the same triangle on side B
// (six nodes each at second order, in the triangle's node order), joined by a conductance h = 1/R″ through the CONSISTENT
// surface mass matrix, ∫h(T_A − T_B)(N_i^A − N_i^B) dA — not lumped, so a quadratic jump is carried exactly.
//
// Tagged surface triangles follow their tetrahedron: an exterior face has one, and an internal face (an embedded sheet, a
// probe on a face that is also a contact) takes the side the caller prefers for its tag, else the lower region.

namespace CircuitRF.Thermal;

/// <summary>A contact resistance between two regions, m²·K/W; 0 is perfect contact.</summary>
public readonly record struct InterfaceResistance(int RegionA, int RegionB, double ResistanceM2KW);

/// <summary>
/// The interface elements of a mesh: per element, <see cref="NodesPerSide"/> nodes on side A then as many on side B (the
/// same triangle, node for node), its conductance h = 1/R″ (W/(m²·K)) and which resistance it came from.
/// </summary>
public sealed record ThermalInterfaceElements(int[] Triangles, int NodesPerSide, double[] H, int[] Pair)
{
    public int Count => H.Length;
}

/// <summary>What a split did.</summary>
/// <param name="Mesh">The mesh with the copies and the interface elements.</param>
/// <param name="CopiedNodes">How many node copies were made.</param>
/// <param name="Parent">For every node of <paramref name="Mesh"/>, the node of the original mesh it is (a copy's original).</param>
/// <param name="Faces">Per resistance of the input list, how many faces carry it.</param>
/// <param name="AreaM2">Per resistance of the input list, the area those faces cover, m² (straight-sided).</param>
public sealed record InterfaceSplit(ThermalMesh Mesh, int CopiedNodes, int[] Parent, int[] Faces, double[] AreaM2);

public static class ThermalInterfaces
{
    /// <summary>The local corners of each face of a tetrahedron, face f being the one opposite corner f.</summary>
    private static readonly int[][] FaceCorners = [[1, 2, 3], [0, 2, 3], [0, 1, 3], [0, 1, 2]];

    /// <summary>
    /// <paramref name="mesh"/> with every contact that <paramref name="resistances"/> gives a positive resistance split and
    /// joined by interface elements. <paramref name="tagRegion"/>, optional, says which region's side a surface tag's
    /// triangles take where they lie on a split contact.
    /// </summary>
    public static InterfaceSplit Split(ThermalMesh mesh, IReadOnlyList<InterfaceResistance> resistances,
                                       IReadOnlyDictionary<int, int>? tagRegion = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(resistances);
        if (mesh.Interfaces is not null) throw new ArgumentException("the mesh is already split", nameof(mesh));
        int nn = mesh.NodesPerTet, nf = mesh.NodesPerTriangle, n = mesh.NodeCount, ne = mesh.TetCount;
        var faces = new int[resistances.Count];
        var area = new double[resistances.Count];
        var pairOf = new Dictionary<(int, int), int>();
        for (int i = 0; i < resistances.Count; i++)
        {
            var r = resistances[i];
            if (!(r.ResistanceM2KW >= 0) || !double.IsFinite(r.ResistanceM2KW))
                throw new ArgumentOutOfRangeException(nameof(resistances), "a contact resistance is zero or positive and finite");
            if (r.RegionA == r.RegionB) throw new ArgumentException("a contact is between two regions", nameof(resistances));
            if (r.ResistanceM2KW == 0) continue;
            pairOf.TryAdd(Key(r.RegionA, r.RegionB), i);
        }
        if (pairOf.Count == 0) return new InterfaceSplit(mesh, 0, [.. Enumerable.Range(0, n)], faces, area);
        var inPair = new HashSet<int>(pairOf.Keys.SelectMany(k => new[] { k.Item1, k.Item2 }));

        // ── node → tetrahedra, every node of each (corners and mid-edge) ──
        var start = new int[n + 1];
        foreach (int v in mesh.Tets) start[v + 1]++;
        for (int i = 0; i < n; i++) start[i + 1] += start[i];
        var star = new int[mesh.Tets.Length];
        var fill = (int[])start.Clone();
        for (int e = 0; e < ne; e++)
            for (int k = 0; k < nn; k++)
            {
                int v = mesh.Tets[nn * e + k];
                if (fill[v] == start[v] || star[fill[v] - 1] != e) star[fill[v]++] = e;
            }
        // (a node appears once per tetrahedron, so the guard only matters for a malformed one)

        // ── the resistive faces: a face two tetrahedra of a resistive pair share ──
        var open = new Dictionary<(int, int, int), (int E, int F)>();
        var resistive = new List<(int EA, int FA, int EB, int FB, int Pair)>();
        var resistiveKeys = new HashSet<(int, int, int)>();
        for (int e = 0; e < ne; e++)
        {
            int re = mesh.TetRegion[e];
            if (!inPair.Contains(re)) continue;
            for (int f = 0; f < 4; f++)
            {
                var key = FaceKey(mesh, e, f);
                if (!open.Remove(key, out var other)) { open[key] = (e, f); continue; }
                int ro = mesh.TetRegion[other.E];
                if (ro == re || !pairOf.TryGetValue(Key(re, ro), out int p)) continue;
                bool aIsOther = resistances[p].RegionA == ro;
                resistive.Add(aIsOther ? (other.E, other.F, e, f, p) : (e, f, other.E, other.F, p));
                resistiveKeys.Add(key);
            }
        }
        if (resistive.Count == 0) return new InterfaceSplit(mesh, 0, [.. Enumerable.Range(0, n)], faces, area);

        // ── the nodes to split, and their groups ──
        var candidates = new SortedSet<int>();
        foreach (var (ea, fa, _, _, _) in resistive)
            foreach (int local in FaceLocals(nn, fa)) candidates.Add(mesh.Tets[nn * ea + local]);

        var tets = (int[])mesh.Tets.Clone();
        var parent = new List<int>(Enumerable.Range(0, n));
        int copies = 0;
        var local2 = new Dictionary<(int, int, int), int>();
        foreach (int v in candidates)
        {
            int s0 = start[v], count = fill[v] - s0;
            var up = new int[count];
            for (int i = 0; i < count; i++) up[i] = i;
            int Find(int i) { while (up[i] != i) i = up[i] = up[up[i]]; return i; }
            local2.Clear();
            for (int i = 0; i < count; i++)
            {
                int e = star[s0 + i];
                int at = IndexIn(mesh.Tets, nn, e, v);
                for (int f = 0; f < 4; f++)
                {
                    if (!FaceHolds(nn, f, at)) continue;
                    var key = FaceKey(mesh, e, f);
                    if (!local2.TryGetValue(key, out int j)) { local2[key] = i; continue; }
                    if (!resistiveKeys.Contains(key)) up[Find(i)] = Find(j);
                }
            }
            // the group of the first tetrahedron keeps the node; every other group gets a copy, in first-tetrahedron order
            var copyOf = new Dictionary<int, int>();
            int keep = Find(0);
            for (int i = 0; i < count; i++)
            {
                int g = Find(i);
                if (g == keep) continue;
                if (!copyOf.TryGetValue(g, out int id))
                {
                    id = copyOf[g] = parent.Count;
                    parent.Add(v);
                    copies++;
                }
                int e = star[s0 + i];
                tets[nn * e + IndexIn(mesh.Tets, nn, e, v)] = id;
            }
        }

        // ── the interface elements, node for node on the two sides ──
        var tris = new int[2 * nf * resistive.Count];
        var h = new double[resistive.Count];
        var pairs = new int[resistive.Count];
        for (int k = 0; k < resistive.Count; k++)
        {
            var (ea, fa, eb, fb, p) = resistive[k];
            var c = FaceCorners[fa].Select(i => mesh.Tets[nn * ea + i]).Order().ToArray();
            Side(ea, c, tris.AsSpan(2 * nf * k, nf));
            Side(eb, c, tris.AsSpan(2 * nf * k + nf, nf));
            h[k] = 1 / resistances[p].ResistanceM2KW;
            pairs[k] = p;
            faces[p]++;
            area[p] += FlatArea(mesh.Nodes, c[0], c[1], c[2]);
        }
        void Side(int e, int[] corners, Span<int> into)
        {
            for (int k = 0; k < 3; k++) into[k] = tets[nn * e + IndexIn(mesh.Tets, nn, e, corners[k])];
            if (nf == 3) return;
            for (int k = 0; k < 3; k++)
            {
                var (a, b) = ThermalMesh.TriangleEdges[k];
                into[3 + k] = tets[nn * e + 4 + EdgeIn(mesh.Tets, nn, e, corners[a], corners[b])];
            }
        }

        // ── the tagged surface triangles follow their tetrahedron ──
        var tri = (int[])mesh.Triangles.Clone();
        var split = new bool[n];
        foreach (int v in candidates) split[v] = true;
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            bool touches = false;
            for (int k = 0; k < nf && !touches; k++) touches = split[mesh.Triangles[nf * t + k]];
            if (!touches) continue;
            int c0 = mesh.Triangles[nf * t], c1 = mesh.Triangles[nf * t + 1], c2 = mesh.Triangles[nf * t + 2];
            int chosen = -1;
            int? prefer = tagRegion is not null && tagRegion.TryGetValue(mesh.TriangleTag[t], out int pr) ? pr : null;
            for (int s = start[c0]; s < fill[c0]; s++)
            {
                int e = star[s];
                if (IndexIn(mesh.Tets, nn, e, c1) < 0 || IndexIn(mesh.Tets, nn, e, c2) < 0) continue;
                if (chosen < 0) { chosen = e; continue; }
                int rc = mesh.TetRegion[chosen], re = mesh.TetRegion[e];
                bool better = prefer is { } want ? re == want && rc != want : re < rc;
                if (better) chosen = e;
            }
            if (chosen < 0) continue;        // a triangle on no tetrahedron: Compact drops those
            for (int k = 0; k < nf; k++)
            {
                int at = IndexIn(mesh.Tets, nn, chosen, mesh.Triangles[nf * t + k]);
                if (at >= 0) tri[nf * t + k] = tets[nn * chosen + at];
            }
        }

        var nodes = new double[3 * parent.Count];
        Array.Copy(mesh.Nodes, nodes, mesh.Nodes.Length);
        for (int i = n; i < parent.Count; i++)
            for (int c = 0; c < 3; c++) nodes[3 * i + c] = mesh.Nodes[3 * parent[i] + c];
        var result = new ThermalMesh(nodes, mesh.Order, tets, (int[])mesh.TetRegion.Clone(), tri, (int[])mesh.TriangleTag.Clone())
        {
            Interfaces = new ThermalInterfaceElements(tris, nf, h, pairs),
        };
        return new InterfaceSplit(result, copies, [.. parent], faces, area);
    }

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    private static (int, int, int) FaceKey(ThermalMesh m, int e, int f)
    {
        int nn = m.NodesPerTet;
        var c = FaceCorners[f];
        int a = m.Tets[nn * e + c[0]], b = m.Tets[nn * e + c[1]], d = m.Tets[nn * e + c[2]];
        if (a > b) (a, b) = (b, a);
        if (b > d) (b, d) = (d, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, d);
    }

    /// <summary>Every local node of face <paramref name="f"/>: its three corners and, at second order, its three mid-edges.</summary>
    private static IEnumerable<int> FaceLocals(int nn, int f)
    {
        foreach (int c in FaceCorners[f]) yield return c;
        if (nn == 4) yield break;
        for (int k = 0; k < 6; k++)
            if (ThermalMesh.TetEdges[k].A != f && ThermalMesh.TetEdges[k].B != f) yield return 4 + k;
    }

    /// <summary>Whether face <paramref name="f"/> (opposite corner f) holds the local node <paramref name="local"/>.</summary>
    private static bool FaceHolds(int nn, int f, int local)
    {
        if (local < 4) return local != f;
        var (a, b) = ThermalMesh.TetEdges[local - 4];
        return a != f && b != f;
    }

    private static int IndexIn(int[] tets, int nn, int e, int v)
    {
        for (int i = 0; i < nn; i++) if (tets[nn * e + i] == v) return i;
        return -1;
    }

    /// <summary>The local edge index (0..5) of tetrahedron e joining its corners with node ids <paramref name="a"/> and <paramref name="b"/>.</summary>
    private static int EdgeIn(int[] tets, int nn, int e, int a, int b)
    {
        int la = IndexIn(tets, nn, e, a), lb = IndexIn(tets, nn, e, b);
        for (int k = 0; k < 6; k++)
        {
            var (x, y) = ThermalMesh.TetEdges[k];
            if (x == la && y == lb || x == lb && y == la) return k;
        }
        throw new InvalidOperationException("a face edge is not an edge of its tetrahedron");
    }

    private static double FlatArea(double[] p, int a, int b, int c)
    {
        double ux = p[3 * b] - p[3 * a], uy = p[3 * b + 1] - p[3 * a + 1], uz = p[3 * b + 2] - p[3 * a + 2];
        double vx = p[3 * c] - p[3 * a], vy = p[3 * c + 1] - p[3 * a + 1], vz = p[3 * c + 2] - p[3 * a + 2];
        double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
        return 0.5 * Math.Sqrt(x * x + y * y + z * z);
    }
}
