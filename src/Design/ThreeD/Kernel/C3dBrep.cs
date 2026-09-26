// brief-em3d-47 R-em3d47-1a — the managed kernel's boundary representation: planar polygonal faces, in an
// object's OWN frame, in integer DBU (owner decision D2: no native dependency).
//
// A B-REP HERE IS GEOMETRY OVER A SHARED TOPOLOGY. The vertices are a fresh list per edit; the faces — rings of
// vertex indices and a NAME each — are a C3dBrepTopology that an edit which folds nothing hands on unchanged, so
// a drag of one face over a 10,000-face solid copies the vertex list and nothing else, and the adjacency (which
// faces meet at a vertex) and the face-bounds hierarchy are built once per gesture, not once per mouse move.
//
// EXACT WHERE IT CAN BE. A face's normal is Newell's vector in Int128 — exact for integer vertices, so an
// axis-aligned face is RECOGNISED as one (two zero components), not guessed within a tolerance — and six times
// the solid's volume is an Int128 sum, so "the signed volume is positive" is a sign, not a comparison with a
// threshold. Coordinates stay well inside ±2^40 DBU (a kilometre at 1 nm), which keeps every product below 2^127.
//
// DETERMINISTIC (Em3dTessellation's rules): nothing is ordered by a hash; every list is walked in index order.

namespace CircuitRF.Design.ThreeD.Kernel;

/// <summary>One face: an outer ring and hole rings of vertex indices, wound counter-clockwise seen from outside
/// (holes the other way), and the NAME that stays with it through every edit (overview §1e).</summary>
public sealed class C3dBrepFace(string name, int[] outer, int[][] holes)
{
    public string Name { get; } = name;
    public int[] Outer { get; } = outer;
    public int[][] Holes { get; } = holes;

    /// <summary>The outer ring, then each hole's.</summary>
    public IEnumerable<int[]> Rings()
    {
        yield return Outer;
        foreach (var h in Holes) yield return h;
    }

    /// <summary>Whether <paramref name="v"/> is on any of the face's rings.</summary>
    public bool Has(int v)
    {
        foreach (var r in Rings())
            if (Array.IndexOf(r, v) >= 0) return true;
        return false;
    }
}

/// <summary>The faces of a B-rep and what is derived from them alone: which faces meet at each vertex, and the
/// face index of each name. Immutable, so an edit that folds nothing shares it.</summary>
public sealed class C3dBrepTopology
{
    private int[][]? _vertexFaces;
    private Dictionary<string, int>? _byName;

    public C3dBrepTopology(IReadOnlyList<C3dBrepFace> faces, int vertexCount)
    {
        Faces = faces;
        VertexCount = vertexCount;
    }

    public IReadOnlyList<C3dBrepFace> Faces { get; }
    public int VertexCount { get; }

    /// <summary>The faces on whose rings vertex <paramref name="v"/> lies, ascending.</summary>
    public int[] FacesAt(int v)
    {
        if (_vertexFaces is null)
        {
            var lists = new List<int>[VertexCount];
            for (int f = 0; f < Faces.Count; f++)
                foreach (var ring in Faces[f].Rings())
                    foreach (int i in ring)
                    {
                        var l = lists[i] ??= [];
                        if (l.Count == 0 || l[^1] != f) l.Add(f);
                    }
            _vertexFaces = [.. lists.Select(l => l?.ToArray() ?? [])];
        }
        return _vertexFaces[v];
    }

    /// <summary>The index of the face called <paramref name="name"/>, or −1.</summary>
    public int IndexOf(string name)
    {
        if (_byName is null)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int f = 0; f < Faces.Count; f++) d.TryAdd(Faces[f].Name, f);
            _byName = d;
        }
        return _byName.TryGetValue(name, out int i) ? i : -1;
    }
}

/// <summary>A closed solid with planar polygonal faces, integer DBU, in an object's own frame.</summary>
public sealed class C3dBrep
{
    private C3dFaceBvh? _bvh;
    private Int128? _volume6;

    public C3dBrep(IReadOnlyList<C3dPoint3> vertices, C3dBrepTopology topology)
    {
        Vertices = vertices;
        Topology = topology;
    }

    public IReadOnlyList<C3dPoint3> Vertices { get; }
    public C3dBrepTopology Topology { get; }
    public IReadOnlyList<C3dBrepFace> Faces => Topology.Faces;

    /// <summary>The hierarchy of this B-rep's face bounds, built on first asking (R-em3d47-3d).</summary>
    public C3dFaceBvh Bvh => _bvh ??= C3dFaceBvh.Build(this);

    /// <summary>Six times the enclosed volume, DBU³ — positive when every face is wound outward.</summary>
    public Int128 Volume6 => _volume6 ??= ComputeVolume6();

    private Int128 ComputeVolume6()
    {
        Int128 v = 0;
        for (int f = 0; f < Faces.Count; f++) v += VolumeTerm(f);
        return v;
    }

    /// <summary>Face <paramref name="f"/>'s share of <see cref="Volume6"/>: a ring vertex dotted with twice the
    /// face's vector area — the divergence theorem, so no triangulation is needed.</summary>
    public Int128 VolumeTerm(int f)
    {
        var face = Faces[f];
        var (nx, ny, nz) = Newell(f);
        var p = Vertices[face.Outer[0]];
        return p.X * nx + p.Y * ny + p.Z * nz;
    }

    /// <summary>Twice face <paramref name="f"/>'s vector area (Newell's method over every ring as wound): exact.
    /// Its direction is the outward normal; its length twice the area, holes subtracted.</summary>
    public (Int128 X, Int128 Y, Int128 Z) Newell(int f)
    {
        Int128 x = 0, y = 0, z = 0;
        foreach (var ring in Faces[f].Rings())
            for (int i = 0; i < ring.Length; i++)
            {
                var a = Vertices[ring[i]];
                var b = Vertices[ring[(i + 1) % ring.Length]];
                x += (Int128)(a.Y - b.Y) * (a.Z + b.Z);
                y += (Int128)(a.Z - b.Z) * (a.X + b.X);
                z += (Int128)(a.X - b.X) * (a.Y + b.Y);
            }
        return (x, y, z);
    }

    /// <summary>Face <paramref name="f"/>'s outward unit normal (from every ring), or zero for a face with no area.</summary>
    public (double X, double Y, double Z) Normal(int f)
    {
        var (x, y, z) = Newell(f);
        double dx = (double)x, dy = (double)y, dz = (double)z;
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return l > 0 ? (dx / l, dy / l, dz / l) : (0, 0, 0);
    }

    /// <summary>The world axis face <paramref name="f"/>'s normal lies along (0 x, 1 y, 2 z) and its sign, or null
    /// when it is tilted — decided from the exact normal, never within a tolerance.</summary>
    public (int Axis, int Sign)? AxisOf(int f)
    {
        var (x, y, z) = Newell(f);
        if (y == 0 && z == 0 && x != 0) return (0, Math.Sign((double)x));
        if (x == 0 && z == 0 && y != 0) return (1, Math.Sign((double)y));
        if (x == 0 && y == 0 && z != 0) return (2, Math.Sign((double)z));
        return null;
    }

    /// <summary>The largest distance, DBU, of any of face <paramref name="f"/>'s vertices from the plane through its
    /// outer ring's centroid with its outer ring's Newell normal — the least-squares plane, and exactly the
    /// measure <c>check</c> uses (C3dValidation.Planarity), so a face the kernel keeps is one <c>check</c> passes.
    /// Infinity for a face whose outer ring has no area.</summary>
    public double Deviation(int f)
    {
        var face = Faces[f];
        var outer = face.Outer;
        double nx = 0, ny = 0, nz = 0, cx = 0, cy = 0, cz = 0;
        for (int k = 0; k < outer.Length; k++)
        {
            var a = Vertices[outer[k]];
            var b = Vertices[outer[(k + 1) % outer.Length]];
            nx += (double)(a.Y - b.Y) * (a.Z + b.Z);
            ny += (double)(a.Z - b.Z) * (a.X + b.X);
            nz += (double)(a.X - b.X) * (a.Y + b.Y);
            cx += a.X; cy += a.Y; cz += a.Z;
        }
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len == 0) return double.PositiveInfinity;
        nx /= len; ny /= len; nz /= len;
        cx /= outer.Length; cy /= outer.Length; cz /= outer.Length;
        double worst = 0;
        foreach (var ring in face.Rings())
            foreach (int i in ring)
            {
                var v = Vertices[i];
                worst = Math.Max(worst, Math.Abs(nx * (v.X - cx) + ny * (v.Y - cy) + nz * (v.Z - cz)));
            }
        return worst;
    }

    /// <summary>R-em3d47-3c — whether face <paramref name="f"/> is planar within <see cref="C3dKernel.PlanarityDbu"/>.</summary>
    public bool IsPlanar(int f) => Deviation(f) <= C3dKernel.PlanarityDbu;

    /// <summary>Whether every vertex of face <paramref name="f"/> lies EXACTLY on one plane (integer test).</summary>
    public bool IsExactlyPlanar(int f)
    {
        var (nx, ny, nz) = Newell(f);
        if (nx == 0 && ny == 0 && nz == 0) return false;
        var o = Vertices[Faces[f].Outer[0]];
        foreach (var ring in Faces[f].Rings())
            foreach (int i in ring)
            {
                var v = Vertices[i];
                if ((v.X - o.X) * nx + (v.Y - o.Y) * ny + (v.Z - o.Z) * nz != 0) return false;
            }
        return true;
    }

    /// <summary>Face <paramref name="f"/>'s bounds, DBU: (x0, y0, z0, x1, y1, z1).</summary>
    public (long X0, long Y0, long Z0, long X1, long Y1, long Z1) Bounds(int f)
    {
        long x0 = long.MaxValue, y0 = long.MaxValue, z0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue, z1 = long.MinValue;
        foreach (int i in Faces[f].Outer)
        {
            var v = Vertices[i];
            x0 = Math.Min(x0, v.X); y0 = Math.Min(y0, v.Y); z0 = Math.Min(z0, v.Z);
            x1 = Math.Max(x1, v.X); y1 = Math.Max(y1, v.Y); z1 = Math.Max(z1, v.Z);
        }
        return (x0, y0, z0, x1, y1, z1);
    }

    /// <summary>Face <paramref name="f"/>'s perimeter, DBU: every ring's edges.</summary>
    public double Perimeter(int f)
    {
        double p = 0;
        foreach (var ring in Faces[f].Rings())
            for (int i = 0; i < ring.Length; i++)
            {
                var a = Vertices[ring[i]];
                var b = Vertices[ring[(i + 1) % ring.Length]];
                double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
                p += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        return p;
    }

    /// <summary>Face <paramref name="f"/>'s area, DBU².</summary>
    public double Area(int f)
    {
        var (x, y, z) = Newell(f);
        double dx = (double)x, dy = (double)y, dz = (double)z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2;
    }

    /// <summary>Every problem with closure, as gate 1 states it: each directed edge is used once and its reverse once.
    /// Null when closed.</summary>
    public string? ClosureProblem()
    {
        var edges = new Dictionary<(int, int), int>();
        foreach (var f in Faces)
            foreach (var ring in f.Rings())
                for (int i = 0; i < ring.Length; i++)
                {
                    var e = (ring[i], ring[(i + 1) % ring.Length]);
                    edges[e] = edges.GetValueOrDefault(e) + 1;
                }
        foreach (var ((a, b), n) in edges.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2))
        {
            if (n > 1) return $"edge {a}→{b} is used {n} times in one direction";
            if (!edges.ContainsKey((b, a))) return $"edge {a}→{b} belongs to one face only";
        }
        return null;
    }
}
