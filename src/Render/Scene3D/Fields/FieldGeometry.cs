// brief-em3d-29 R-em3d29-3a — drawing a field on SURFACES and CLIP PLANES only, never the volume (§8.5):
//
//   * the clip plane, as an EXACT slice of the tetrahedra, interpolating linearly within each sub-tet;
//   * the boundary faces of a solid (the faces of its region's tetrahedra with a different region, or
//     nothing, on the other side);
//   * the boundary collection's own faces (conductor surfaces, which is where Palace writes J_s).
//
// HOW AN ORDER-2 CELL IS DRAWN: split into LINEAR SUB-CELLS through its own nodes — a tetrahedron's ten
// into eight sub-tetrahedra (the four corners, and the inner octahedron cut on its e02–e13 diagonal), a
// triangle's six into four — and each sub-cell interpolates linearly. So a drawn value is the solver's own
// value at every node and linear between them; the value UNDER THE CURSOR is evaluated exactly instead
// (FieldSampler: the element's own quadratic shape functions, curved geometry included), and gate 3
// measures the gap between the two on Palace's own probes.
//
// Every value is carried as the array's CHANNELS — its components, then (complex) their imaginary parts —
// so a scalar, a vector, real or complex, all go through the same code.

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>A triangle list with values at its vertices: what a slice or a surface produces. Positions are
/// scene-local metres (relative to the scene's origin), in double so a slice's exactness can be tested.</summary>
public sealed class FieldSurface
{
    public required int Channels { get; init; }
    /// <summary>x, y, z per vertex; three vertices per triangle.</summary>
    public required double[] Xyz { get; init; }
    /// <summary><see cref="Channels"/> values per vertex.</summary>
    public required double[] Values { get; init; }
    public int VertexCount => Xyz.Length / 3;
    public int TriangleCount => VertexCount / 3;

    public static FieldSurface Empty(int channels) => new() { Channels = channels, Xyz = [], Values = [] };

    /// <summary>brief-em3d-75 R-em3d75-4c — how each vertex's values were made from the mesh's nodes, when the surface was
    /// cut or gathered from a <see cref="FieldMesh"/>; null otherwise (a synthetic set of tetrahedra, a wire).</summary>
    public FieldRecipe? Recipe { get; init; }

    /// <summary>
    /// brief-em3d-76 R-em3d76-4c — the same triangles reflected across the plane normal to <paramref name="axis"/> (0 x, 1 y,
    /// 2 z) at <paramref name="at"/> (scene-local metres), each triangle's winding reversed so it still faces out, with the
    /// same values — a symmetry plane's mirrored half. The recipe is reordered with the vertices, so a sweep step revalues
    /// the mirror exactly as it does the original.
    /// </summary>
    public FieldSurface Mirrored(int axis, double at)
    {
        int n = VertexCount, ch = Channels;
        var xyz = new double[Xyz.Length];
        var values = new double[Values.Length];
        FieldRecipe? recipe = Recipe is { } r
            ? new FieldRecipe { A = new int[n], B = new int[n], T = new double[n], Cell = new int[n], NodeCount = r.NodeCount, CellCount = r.CellCount }
            : null;
        for (int v = 0; v < n; v++)
        {
            int k = v % 3, from = k == 1 ? v + 1 : k == 2 ? v - 1 : v;       // (a, b, c) → (a, c, b)
            for (int c = 0; c < 3; c++) xyz[3 * v + c] = Xyz[3 * from + c];
            xyz[3 * v + axis] = 2 * at - Xyz[3 * from + axis];
            Array.Copy(Values, from * ch, values, v * ch, ch);
            if (recipe is not null)
            {
                recipe.A[v] = Recipe!.A[from]; recipe.B[v] = Recipe.B[from]; recipe.T[v] = Recipe.T[from]; recipe.Cell[v] = Recipe.Cell[from];
            }
        }
        return new FieldSurface { Channels = ch, Xyz = xyz, Values = values, Recipe = recipe };
    }

    internal static FieldSurface From(int channels, List<double> xyz, List<double> values, FieldRecipe? recipe = null)
        => new() { Channels = channels, Xyz = [.. xyz], Values = [.. values], Recipe = recipe };

    /// <summary>
    /// brief-em3d-75 R-em3d75-4c / gate 7 — the SAME triangles with <paramref name="a"/>'s values: every vertex re-read
    /// through its recipe, nothing cut or gathered again. A thermal run writes every sweep point on one mesh (brief 74), so
    /// stepping the sweep is this and nothing else. Null when the surface has no recipe, or <paramref name="a"/> is not an
    /// array of the mesh the recipe was made on.
    /// </summary>
    public FieldSurface? Revalue(FieldArray a)
    {
        if (Recipe is not { } r) return null;
        int ch = a.Info.Channels;
        int count = a.Info.PerCell ? r.CellCount : r.NodeCount;
        if (ch != Channels || a.Re.Length != a.Components * count) return null;
        var values = new double[VertexCount * ch];
        Span<double> va = stackalloc double[ch], vb = stackalloc double[ch];
        for (int v = 0; v < VertexCount; v++)
        {
            FieldSampling.Channels(a, r.A[v], r.Cell[v], va);
            if (r.B[v] >= 0 && !a.Info.PerCell)
            {
                FieldSampling.Channels(a, r.B[v], r.Cell[v], vb);
                double t = r.T[v];
                for (int k = 0; k < ch; k++) values[v * ch + k] = va[k] + t * (vb[k] - va[k]);
            }
            else for (int k = 0; k < ch; k++) values[v * ch + k] = va[k];
        }
        return new FieldSurface { Channels = ch, Xyz = Xyz, Values = values, Recipe = r };
    }
}

/// <summary>
/// brief-em3d-75 — a surface's vertex values as the mesh made them: vertex v is node <see cref="A"/>[v], or — where a slice
/// cut an edge — <see cref="A"/>[v] + <see cref="T"/>[v]·(<see cref="B"/>[v] − <see cref="A"/>[v]); a per-cell array reads
/// cell <see cref="Cell"/>[v]. <see cref="NodeCount"/> and <see cref="CellCount"/> are the mesh's, so an array of another
/// mesh is refused rather than misread.
/// </summary>
public sealed class FieldRecipe
{
    public required int[] A { get; init; }
    public required int[] B { get; init; }
    public required double[] T { get; init; }
    public required int[] Cell { get; init; }
    public required int NodeCount { get; init; }
    public required int CellCount { get; init; }

    internal sealed class Builder(int nodes, int cells)
    {
        private readonly List<int> _a = [], _b = [], _cell = [];
        private readonly List<double> _t = [];
        public void Add(int a, int b, double t, int cell) { _a.Add(a); _b.Add(b); _t.Add(t); _cell.Add(cell); }
        public FieldRecipe Build() => new() { A = [.. _a], B = [.. _b], T = [.. _t], Cell = [.. _cell], NodeCount = nodes, CellCount = cells };
    }
}

/// <summary>Linear tetrahedra with values at their corners — what the slicer cuts.</summary>
public interface ILinearTets
{
    int Count { get; }
    int Channels { get; }
    /// <summary>Tetrahedron <paramref name="tet"/>'s four corners (12 values) and its corner values
    /// (4 × <see cref="Channels"/>, corner by corner).</summary>
    void Get(int tet, Span<double> xyz, Span<double> values);

    /// <summary>brief-em3d-75 — tetrahedron <paramref name="tet"/>'s four corners as MESH NODES and the cell it lies in,
    /// so a slice can be re-read on another step of the same mesh (<see cref="FieldSurface.Revalue"/>); false when these
    /// tetrahedra are not a mesh's.</summary>
    bool Nodes(int tet, Span<int> nodes, out int cell) { cell = -1; return false; }

    /// <summary>The mesh's node and cell counts, for a recipe; (0, 0) when these tetrahedra are not a mesh's.</summary>
    (int Nodes, int Cells) MeshSize => (0, 0);
}

/// <summary>The local node indices of a cell's linear sub-cells, in VTK's Lagrange node order.</summary>
public static class FieldSubCells
{
    /// <summary>A 10-node tetrahedron's eight sub-tetrahedra. Nodes 4..9 are the edges (0,1),(1,2),(0,2),
    /// (0,3),(1,3),(2,3): the four corner tetrahedra, then the octahedron cut on its 6–8 (e02–e13) diagonal.</summary>
    public static ReadOnlySpan<int> Tet2 => [0, 4, 6, 7,  4, 1, 5, 8,  6, 5, 2, 9,  7, 8, 9, 3,
                                              6, 8, 4, 5,  6, 8, 5, 9,  6, 8, 9, 7,  6, 8, 7, 4];
    public static ReadOnlySpan<int> Tet1 => [0, 1, 2, 3];

    /// <summary>A 6-node triangle's four sub-triangles (nodes 3..5 on edges (0,1),(1,2),(2,0)).</summary>
    public static ReadOnlySpan<int> Tri2 => [0, 3, 5,  3, 1, 4,  5, 4, 2,  3, 4, 5];
    public static ReadOnlySpan<int> Tri1 => [0, 1, 2];

    /// <summary>A tetrahedron's four faces as 6-node triangles (corners, then edges (0,1),(1,2),(2,0) of the
    /// face) — for a 4-node one only the first three of each are used.</summary>
    public static ReadOnlySpan<int> TetFaces2 => [0, 1, 2, 4, 5, 6,  0, 1, 3, 4, 8, 7,  0, 2, 3, 6, 9, 7,  1, 2, 3, 5, 9, 8];

    public static ReadOnlySpan<int> TetSub(int order) => order == 2 ? Tet2 : Tet1;
    public static ReadOnlySpan<int> TriSub(int order) => order == 2 ? Tri2 : Tri1;
}

/// <summary>
/// A volume <see cref="FieldMesh"/>'s linear sub-tetrahedra with one array's values: positions in
/// scene-local metres (<paramref name="origin"/> is the scene's origin, world metres), values as channels.
/// A per-cell array (the error indicator) is constant over its cell. With no array, every value is 0.
/// </summary>
public readonly struct FieldMeshTets(FieldMesh mesh, FieldArray? array, (double X, double Y, double Z) origin) : ILinearTets
{
    private readonly int _sub = mesh.Order == 2 ? 8 : 1;
    public int Count => mesh.CellCount * _sub;
    public int Channels => array?.Info.Channels ?? 1;

    public void Get(int tet, Span<double> xyz, Span<double> values)
    {
        int cell = tet / _sub, s = tet % _sub;
        var local = FieldSubCells.TetSub(mesh.Order).Slice(4 * s, 4);
        int npc = mesh.NodesPerCell, ch = Channels;
        for (int k = 0; k < 4; k++)
        {
            int node = mesh.Cells[cell * npc + local[k]];
            FieldSampling.Position(mesh, node, origin, xyz.Slice(3 * k, 3));
            FieldSampling.Channels(array, node, cell, values.Slice(k * ch, ch));
        }
    }

    public bool Nodes(int tet, Span<int> nodes, out int cell)
    {
        cell = tet / _sub;
        var local = FieldSubCells.TetSub(mesh.Order).Slice(4 * (tet % _sub), 4);
        for (int k = 0; k < 4; k++) nodes[k] = mesh.Cells[cell * mesh.NodesPerCell + local[k]];
        return true;
    }

    public (int Nodes, int Cells) MeshSize => (mesh.NodeCount, mesh.CellCount);
}

/// <summary>Reading a node's position and values.</summary>
public static class FieldSampling
{
    public static void Position(FieldMesh mesh, int node, (double X, double Y, double Z) origin, Span<double> xyz)
    {
        double s = mesh.ToMetres;
        xyz[0] = mesh.Points[3 * node] * s - origin.X;
        xyz[1] = mesh.Points[3 * node + 1] * s - origin.Y;
        xyz[2] = mesh.Points[3 * node + 2] * s - origin.Z;
    }

    /// <summary>A node's (or, for a per-cell array, its cell's) channels.</summary>
    public static void Channels(FieldArray? a, int node, int cell, Span<double> ch)
    {
        if (a is null) { ch.Clear(); return; }
        int c = a.Components, at = a.Info.PerCell ? cell : node;
        for (int i = 0; i < c; i++) ch[i] = a.Re[at * c + i];
        if (a.Im is { } im) for (int i = 0; i < c; i++) ch[c + i] = im[at * c + i];
    }
}

public static class FieldSlicer
{
    /// <summary>
    /// The exact section of <paramref name="tets"/> by the plane n·p + d = 0: in each tetrahedron the plane
    /// crosses, a triangle (one corner apart) or two (a quadrilateral), each vertex where the plane crosses
    /// an edge, its values interpolated linearly along that edge. A corner ON the plane counts with the
    /// negative side, so a face lying in the plane is produced once, by the tetrahedron on its positive side.
    /// </summary>
    public static FieldSurface Slice<T>(T tets, Vector3D n, double d, CancellationToken ct = default) where T : ILinearTets
    {
        int ch = tets.Channels;
        var xyzOut = new List<double>();
        var valOut = new List<double>();
        Span<double> p = stackalloc double[12];
        Span<double> v = stackalloc double[4 * Math.Max(ch, 1)];
        Span<double> s = stackalloc double[4];
        Span<int> pos = stackalloc int[4];
        Span<int> neg = stackalloc int[4];
        Span<int> nodes = stackalloc int[4];
        var (meshNodes, meshCells) = tets.MeshSize;
        var recipe = meshNodes > 0 ? new FieldRecipe.Builder(meshNodes, meshCells) : null;
        int cell = -1;
        for (int t = 0; t < tets.Count; t++)
        {
            if ((t & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            tets.Get(t, p, v);
            if (recipe is not null && !tets.Nodes(t, nodes, out cell)) recipe = null;
            int np = 0, nn = 0;
            for (int k = 0; k < 4; k++)
            {
                s[k] = n.X * p[3 * k] + n.Y * p[3 * k + 1] + n.Z * p[3 * k + 2] + d;
                if (s[k] > 0) pos[np++] = k; else neg[nn++] = k;
            }
            if (np == 0 || nn == 0) continue;
            if (np == 1 || nn == 1)
            {
                // One corner apart: a triangle on its three edges.
                int apex = np == 1 ? pos[0] : neg[0];
                Span<int> other = np == 1 ? neg : pos;
                for (int k = 0; k < 3; k++) Cut(p, v, s, ch, apex, other[k], xyzOut, valOut, recipe, nodes, cell);
            }
            else
            {
                // Two and two: the quadrilateral (a,c) (a,d) (b,d) (b,c), as two triangles.
                int a = pos[0], b = pos[1], c = neg[0], e = neg[1];
                Cut(p, v, s, ch, a, c, xyzOut, valOut, recipe, nodes, cell); Cut(p, v, s, ch, a, e, xyzOut, valOut, recipe, nodes, cell);
                Cut(p, v, s, ch, b, e, xyzOut, valOut, recipe, nodes, cell);
                Cut(p, v, s, ch, a, c, xyzOut, valOut, recipe, nodes, cell); Cut(p, v, s, ch, b, e, xyzOut, valOut, recipe, nodes, cell);
                Cut(p, v, s, ch, b, c, xyzOut, valOut, recipe, nodes, cell);
            }
        }
        return FieldSurface.From(ch, xyzOut, valOut, recipe?.Build());
    }

    private static void Cut(ReadOnlySpan<double> p, ReadOnlySpan<double> v, ReadOnlySpan<double> s, int ch, int i, int j,
                            List<double> xyz, List<double> val, FieldRecipe.Builder? recipe, ReadOnlySpan<int> nodes, int cell)
    {
        double t = s[i] / (s[i] - s[j]);
        for (int k = 0; k < 3; k++) xyz.Add(p[3 * i + k] + t * (p[3 * j + k] - p[3 * i + k]));
        for (int k = 0; k < ch; k++) val.Add(v[i * ch + k] + t * (v[j * ch + k] - v[i * ch + k]));
        recipe?.Add(nodes[i], nodes[j], t, cell);
    }
}

/// <summary>A double-precision 3-vector: a plane normal kept exact for the slicer.</summary>
public readonly record struct Vector3D(double X, double Y, double Z)
{
    public static Vector3D From(Vector3 v) => new(v.X, v.Y, v.Z);
}

public static class FieldSurfaces
{
    /// <summary>
    /// The faces of a boundary collection's triangles whose attribute is in <paramref name="attributes"/>
    /// (a conductor's surface is its group's attribute), each order-2 triangle as its four sub-triangles.
    /// </summary>
    public static FieldSurface Boundary(FieldMesh tris, FieldArray? a, IReadOnlySet<int> attributes, (double X, double Y, double Z) origin)
    {
        if (tris.Shape != FieldCellShape.Triangle) throw new ArgumentException("a boundary collection holds triangles", nameof(tris));
        int ch = a?.Info.Channels ?? 1, npc = tris.NodesPerCell;
        var sub = FieldSubCells.TriSub(tris.Order).ToArray();
        var xyz = new List<double>();
        var val = new List<double>();
        var recipe = new FieldRecipe.Builder(tris.NodeCount, tris.CellCount);
        Span<double> p = stackalloc double[3];
        Span<double> v = stackalloc double[Math.Max(ch, 1)];
        for (int c = 0; c < tris.CellCount; c++)
        {
            if (!attributes.Contains(tris.Attribute[c])) continue;
            foreach (int local in sub)
            {
                int node = tris.Cells[c * npc + local];
                FieldSampling.Position(tris, node, origin, p);
                FieldSampling.Channels(a, node, c, v[..ch]);
                xyz.Add(p[0]); xyz.Add(p[1]); xyz.Add(p[2]);
                for (int k = 0; k < ch; k++) val.Add(v[k]);
                recipe.Add(node, -1, 0, c);
            }
        }
        return FieldSurface.From(ch, xyz, val, recipe.Build());
    }

    /// <summary>
    /// The boundary of the region whose cells carry an attribute in <paramref name="attributes"/> — a solid
    /// the user picked: every face of one of its tetrahedra whose other side is another region or nothing,
    /// with the values of the tetrahedron INSIDE the region (the field is not continuous across a material
    /// face, and the solid's own side is the one asked about). Faces are matched by their corners'
    /// coordinates, which every cell sharing a mesh vertex writes identically.
    /// </summary>
    public static FieldSurface RegionBoundary(FieldMesh tets, FieldArray? a, IReadOnlySet<int> attributes,
                                              (double X, double Y, double Z) origin, CancellationToken ct = default)
    {
        if (tets.Shape != FieldCellShape.Tetrahedron) throw new ArgumentException("a volume holds tetrahedra", nameof(tets));
        int npc = tets.NodesPerCell, ch = a?.Info.Channels ?? 1;
        var ids = CornerIds(tets);
        // Face key → (cell, face, in region) of the first cell seen; a second cell with the same face marks it shared.
        var faces = new Dictionary<(int, int, int), (int Cell, int Face, bool In, bool Shared, bool OtherIn)>();
        var faceCorners = FieldSubCells.TetFaces2;
        for (int c = 0; c < tets.CellCount; c++)
        {
            if ((c & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            bool inside = attributes.Contains(tets.Attribute[c]);
            for (int f = 0; f < 4; f++)
            {
                int a0 = ids[4 * c + faceCorners[6 * f]], a1 = ids[4 * c + faceCorners[6 * f + 1]], a2 = ids[4 * c + faceCorners[6 * f + 2]];
                var key = Sorted(a0, a1, a2);
                if (faces.TryGetValue(key, out var e))
                {
                    // Keep the region's own cell as the face's owner; note what is across.
                    faces[key] = e.In ? e with { Shared = true, OtherIn = inside } : (c, f, inside, true, e.In);
                }
                else faces[key] = (c, f, inside, false, false);
            }
        }
        var xyz = new List<double>();
        var val = new List<double>();
        var recipe = new FieldRecipe.Builder(tets.NodeCount, tets.CellCount);
        Span<double> p = stackalloc double[3];
        Span<double> v = stackalloc double[Math.Max(ch, 1)];
        var tri = FieldSubCells.TriSub(tets.Order).ToArray();
        foreach (var e in faces.Values)
        {
            if (!e.In || (e.Shared && e.OtherIn)) continue;
            foreach (int local in tri)
            {
                int node = tets.Cells[e.Cell * npc + faceCorners[6 * e.Face + local]];
                FieldSampling.Position(tets, node, origin, p);
                FieldSampling.Channels(a, node, e.Cell, v[..ch]);
                xyz.Add(p[0]); xyz.Add(p[1]); xyz.Add(p[2]);
                for (int k = 0; k < ch; k++) val.Add(v[k]);
                recipe.Add(node, -1, 0, e.Cell);
            }
        }
        return FieldSurface.From(ch, xyz, val, recipe.Build());
    }

    /// <summary>
    /// brief-em3d-75 R-em3d75-4a — every EXPOSED face of the volume (no cell on its other side), grouped by the attribute of
    /// the cell that owns it: <i>All Faces</i> of a thermal run, one surface per solid so the hot spot can name its object. A
    /// face between two solids is inside the model and is not drawn. One pass over the cells, whatever the solid count.
    /// </summary>
    public static Dictionary<int, FieldSurface> Exterior(FieldMesh tets, FieldArray? a, (double X, double Y, double Z) origin,
                                                         CancellationToken ct = default)
    {
        if (tets.Shape != FieldCellShape.Tetrahedron) throw new ArgumentException("a volume holds tetrahedra", nameof(tets));
        int npc = tets.NodesPerCell, ch = a?.Info.Channels ?? 1;
        var ids = CornerIds(tets);
        var faces = new Dictionary<(int, int, int), (int Cell, int Face, int Count)>();
        var faceCorners = FieldSubCells.TetFaces2;
        for (int c = 0; c < tets.CellCount; c++)
        {
            if ((c & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            for (int f = 0; f < 4; f++)
            {
                var key = Sorted(ids[4 * c + faceCorners[6 * f]], ids[4 * c + faceCorners[6 * f + 1]], ids[4 * c + faceCorners[6 * f + 2]]);
                faces[key] = faces.TryGetValue(key, out var e) ? e with { Count = e.Count + 1 } : (c, f, 1);
            }
        }
        var parts = new Dictionary<int, (List<double> Xyz, List<double> Val, FieldRecipe.Builder Recipe)>();
        Span<double> p = stackalloc double[3];
        Span<double> v = stackalloc double[Math.Max(ch, 1)];
        var tri = FieldSubCells.TriSub(tets.Order).ToArray();
        foreach (var e in faces.Values)
        {
            if (e.Count != 1) continue;
            int attr = tets.Attribute[e.Cell];
            if (!parts.TryGetValue(attr, out var part))
                parts[attr] = part = ([], [], new FieldRecipe.Builder(tets.NodeCount, tets.CellCount));
            foreach (int local in tri)
            {
                int node = tets.Cells[e.Cell * npc + faceCorners[6 * e.Face + local]];
                FieldSampling.Position(tets, node, origin, p);
                FieldSampling.Channels(a, node, e.Cell, v[..ch]);
                part.Xyz.Add(p[0]); part.Xyz.Add(p[1]); part.Xyz.Add(p[2]);
                for (int k = 0; k < ch; k++) part.Val.Add(v[k]);
                part.Recipe.Add(node, -1, 0, e.Cell);
            }
        }
        return parts.ToDictionary(kv => kv.Key, kv => FieldSurface.From(ch, kv.Value.Xyz, kv.Value.Val, kv.Value.Recipe.Build()));
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// <summary>Each cell's four corners as mesh-vertex ids, matched by exact coordinates.</summary>
    private static int[] CornerIds(FieldMesh m)
    {
        var map = new Dictionary<(float, float, float), int>();
        var ids = new int[4 * m.CellCount];
        for (int c = 0; c < m.CellCount; c++)
            for (int k = 0; k < 4; k++)
            {
                int node = m.Cells[c * m.NodesPerCell + k];
                var key = (m.Points[3 * node], m.Points[3 * node + 1], m.Points[3 * node + 2]);
                if (!map.TryGetValue(key, out int id)) map[key] = id = map.Count;
                ids[4 * c + k] = id;
            }
        return ids;
    }
}
