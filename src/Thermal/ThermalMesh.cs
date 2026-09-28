// brief-em3d-74 R-em3d74-2a — the mesh the solver reads: nodes, tetrahedra with a region each, and tagged surface
// triangles. Metres. Four-node (P1) or ten-node (P2) tetrahedra, in Gmsh's node order — the order the meshes arrive in:
//
//   tetrahedron: corners 0..3, then the mid-edge nodes of (0,1) (1,2) (0,2) (0,3) (2,3) (1,3)
//   triangle:    corners 0..2, then the mid-edge nodes of (0,1) (1,2) (2,0)
//
// (checked against Gmsh 4.15.2's own output, not assumed). A second-order mesh's mid-edge nodes are used AS GIVEN, so a
// curved (isoparametric) element stays curved. A first-order mesh can be promoted to P2 with straight edges
// (ToSecondOrder), which is what a hand-built test mesh does.

namespace CircuitRF.Thermal;

/// <summary>A tetrahedral mesh for the thermal solver.</summary>
public sealed class ThermalMesh
{
    /// <summary>The mid-edge node pairs of a ten-node tetrahedron, in Gmsh's order.</summary>
    public static readonly (int A, int B)[] TetEdges = [(0, 1), (1, 2), (0, 2), (0, 3), (2, 3), (1, 3)];

    /// <summary>The mid-edge node pairs of a six-node triangle, in Gmsh's order.</summary>
    public static readonly (int A, int B)[] TriangleEdges = [(0, 1), (1, 2), (2, 0)];

    public ThermalMesh(double[] nodes, int order, int[] tets, int[] tetRegion, int[] triangles, int[] triangleTag)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(tets);
        ArgumentNullException.ThrowIfNull(tetRegion);
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(triangleTag);
        if (order is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(order));
        if (nodes.Length % 3 != 0) throw new ArgumentException("three coordinates per node", nameof(nodes));
        int npt = order == 2 ? 10 : 4, npf = order == 2 ? 6 : 3;
        if (tets.Length != npt * tetRegion.Length) throw new ArgumentException("tets per region", nameof(tets));
        if (triangles.Length != npf * triangleTag.Length) throw new ArgumentException("triangles per tag", nameof(triangles));
        int n = nodes.Length / 3;
        foreach (int v in tets) if ((uint)v >= (uint)n) throw new ArgumentException("a tetrahedron names a node that does not exist", nameof(tets));
        foreach (int v in triangles) if ((uint)v >= (uint)n) throw new ArgumentException("a triangle names a node that does not exist", nameof(triangles));
        Nodes = nodes; Order = order; Tets = tets; TetRegion = tetRegion; Triangles = triangles; TriangleTag = triangleTag;
    }

    /// <summary>x, y, z per node, metres.</summary>
    public double[] Nodes { get; }
    /// <summary>1 (four-node tetrahedra) or 2 (ten-node).</summary>
    public int Order { get; }
    /// <summary><see cref="NodesPerTet"/> node indices per tetrahedron.</summary>
    public int[] Tets { get; }
    /// <summary>Each tetrahedron's region: an index into the problem's conductivities.</summary>
    public int[] TetRegion { get; }
    /// <summary><see cref="NodesPerTriangle"/> node indices per surface triangle.</summary>
    public int[] Triangles { get; }
    /// <summary>Each triangle's surface tag, which sources, boundaries and probes name.</summary>
    public int[] TriangleTag { get; }

    /// <summary>brief-em3d-76 R-em3d76-1b — the interface elements joining the two sides of each resistive contact, made by
    /// <see cref="ThermalInterfaces.Split"/>; null when there are none.</summary>
    public ThermalInterfaceElements? Interfaces { get; init; }

    public int NodeCount => Nodes.Length / 3;
    public int TetCount => TetRegion.Length;
    public int TriangleCount => TriangleTag.Length;
    public int NodesPerTet => Order == 2 ? 10 : 4;
    public int NodesPerTriangle => Order == 2 ? 6 : 3;

    /// <summary>
    /// The same mesh with only the nodes a tetrahedron uses, renumbered in their first-use order by node index. A Gmsh file
    /// can hold a node no element of the solve names (a geometry point on an entity that is not meshed); it would be a row
    /// of zeros in the matrix. <paramref name="map"/> gives each old node its new index, or −1.
    /// </summary>
    public ThermalMesh Compact(out int[] map)
    {
        map = new int[NodeCount];
        Array.Fill(map, -1);
        foreach (int v in Tets) map[v] = 0;
        int next = 0;
        for (int i = 0; i < map.Length; i++) if (map[i] == 0) map[i] = next++;
        if (next == NodeCount) { for (int i = 0; i < map.Length; i++) map[i] = i; return this; }
        var nodes = new double[3 * next];
        for (int i = 0; i < map.Length; i++)
            if (map[i] >= 0) { nodes[3 * map[i]] = Nodes[3 * i]; nodes[3 * map[i] + 1] = Nodes[3 * i + 1]; nodes[3 * map[i] + 2] = Nodes[3 * i + 2]; }
        var m = map;
        var tets = Tets.Select(v => m[v]).ToArray();
        // A triangle whose nodes no tetrahedron uses lies on no meshed solid; it is dropped with them.
        var keep = Enumerable.Range(0, TriangleCount)
                             .Where(t => Enumerable.Range(0, NodesPerTriangle).All(k => m[Triangles[t * NodesPerTriangle + k]] >= 0)).ToList();
        var tris = keep.SelectMany(t => Enumerable.Range(0, NodesPerTriangle).Select(k => m[Triangles[t * NodesPerTriangle + k]])).ToArray();
        var tags = keep.Select(t => TriangleTag[t]).ToArray();
        return new ThermalMesh(nodes, Order, tets, (int[])TetRegion.Clone(), tris, tags)
        {
            Interfaces = Interfaces is { } f ? f with { Triangles = [.. f.Triangles.Select(v => m[v])] } : null,
        };
    }

    /// <summary>
    /// A first-order mesh promoted to second order with a new node at the midpoint of every edge (straight edges). A
    /// second-order mesh is returned as it is.
    /// </summary>
    public ThermalMesh ToSecondOrder()
    {
        if (Order == 2) return this;
        var nodes = new List<double>(Nodes);
        var mid = new Dictionary<long, int>();
        int Mid(int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (mid.TryGetValue(key, out int m)) return m;
            m = nodes.Count / 3;
            for (int c = 0; c < 3; c++) nodes.Add(0.5 * (Nodes[3 * a + c] + Nodes[3 * b + c]));
            mid[key] = m;
            return m;
        }
        var tets = new int[10 * TetCount];
        for (int e = 0; e < TetCount; e++)
        {
            for (int k = 0; k < 4; k++) tets[10 * e + k] = Tets[4 * e + k];
            for (int k = 0; k < 6; k++) tets[10 * e + 4 + k] = Mid(Tets[4 * e + TetEdges[k].A], Tets[4 * e + TetEdges[k].B]);
        }
        var tris = new int[6 * TriangleCount];
        for (int t = 0; t < TriangleCount; t++)
        {
            for (int k = 0; k < 3; k++) tris[6 * t + k] = Triangles[3 * t + k];
            for (int k = 0; k < 3; k++) tris[6 * t + 3 + k] = Mid(Triangles[3 * t + TriangleEdges[k].A], Triangles[3 * t + TriangleEdges[k].B]);
        }
        ThermalInterfaceElements? faces = null;
        if (Interfaces is { } f)
        {
            // each side's mid-edge nodes are that side's own: its corners are its own copies, so Mid keys them apart
            var ft = new int[12 * f.Count];
            for (int t = 0; t < f.Count; t++)
                for (int side = 0; side < 2; side++)
                {
                    int src = 6 * t + 3 * side, dst = 12 * t + 6 * side;
                    for (int k = 0; k < 3; k++) ft[dst + k] = f.Triangles[src + k];
                    for (int k = 0; k < 3; k++) ft[dst + 3 + k] = Mid(f.Triangles[src + TriangleEdges[k].A], f.Triangles[src + TriangleEdges[k].B]);
                }
            faces = f with { Triangles = ft, NodesPerSide = 6 };
        }
        return new ThermalMesh([.. nodes], 2, tets, (int[])TetRegion.Clone(), tris, (int[])TriangleTag.Clone()) { Interfaces = faces };
    }

    /// <summary>The distinct surface tags, ascending.</summary>
    public IReadOnlyList<int> SurfaceTags() => [.. TriangleTag.Distinct().Order()];
}
