// brief-em3d-42 R-em3d42-1 — the neutral problem's two new records: a POLYHEDRON (planar faces with holes)
// and a sheet in ANY plane (Em3dPlaneFrame), which a drawn 3D view needs and a layout never did
// (overview §1h). Metres, like everything in Em3dProblem.
//
// A face carries its NAME (overview §1e) so brief 49's face boundaries and the viewer's face picking can
// find it again after the problem is rebuilt; nothing here ever identifies a face by index alone.
//
// Validate asks three things of a polyhedron and reports ALL the problems, never the first
// (R-em3d3-1c): every face is planar (relative 1e-9 of the solid's extent), the surface is closed (every
// edge is used exactly twice, once in each direction — which also catches a hole wound the same way as
// its outline), and the volume is positive (faces wound outward). The volume is the divergence theorem
// over each face's vector area, so a non-convex face or one with holes needs no triangulation.

namespace CircuitRF.Engine.Em3d;

/// <summary>One planar face of a polyhedron: an outer ring and hole rings as indices into the
/// polyhedron's vertices, wound counter-clockwise seen from outside (holes the other way), and the
/// face's name.</summary>
public sealed record Em3dFace(IReadOnlyList<int> Outer, IReadOnlyList<IReadOnlyList<int>> Holes, string Name);

/// <summary>A closed solid with planar faces (R-em3d42-1a) — a rotated box, an oblique prism, an edited
/// solid: whatever no richer primitive states exactly (overview §1g).</summary>
public sealed record Em3dPolyhedron(IReadOnlyList<Point3> Vertices, IReadOnlyList<Em3dFace> Faces) : Em3dPrimitive
{
    /// <summary>The largest side of the polyhedron's bounding box — what planarity is relative to.</summary>
    public double Extent()
    {
        if (Vertices.Count == 0) return 0;
        double x0 = Vertices.Min(v => v.X), x1 = Vertices.Max(v => v.X);
        double y0 = Vertices.Min(v => v.Y), y1 = Vertices.Max(v => v.Y);
        double z0 = Vertices.Min(v => v.Z), z1 = Vertices.Max(v => v.Z);
        return Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
    }

    /// <summary>A face's vector area: its unit normal times its area, holes subtracted (Newell's method,
    /// summed over the rings as wound).</summary>
    public Point3 VectorArea(Em3dFace f)
    {
        double x = 0, y = 0, z = 0;
        foreach (var ring in f.Holes.Prepend(f.Outer))
            for (int i = 0; i < ring.Count; i++)
            {
                var a = Vertices[ring[i]];
                var b = Vertices[ring[(i + 1) % ring.Count]];
                x += (a.Y - b.Y) * (a.Z + b.Z);
                y += (a.Z - b.Z) * (a.X + b.X);
                z += (a.X - b.X) * (a.Y + b.Y);
            }
        return new Point3(x / 2, y / 2, z / 2);
    }

    /// <summary>The unit outward normal of a face, from its outer ring alone.</summary>
    public Point3 Normal(Em3dFace f)
    {
        var n = VectorArea(f with { Holes = [] });
        double l = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        return l > 0 ? new Point3(n.X / l, n.Y / l, n.Z / l) : new Point3(0, 0, 0);
    }

    /// <summary>The volume enclosed, m³ — positive when every face is wound outward.</summary>
    public double SignedVolume()
    {
        double v = 0;
        foreach (var f in Faces)
        {
            if (f.Outer.Count == 0) continue;
            var p = Vertices[f.Outer[0]];
            var s = VectorArea(f);
            v += p.X * s.X + p.Y * s.Y + p.Z * s.Z;
        }
        return v / 3;
    }

    /// <summary>True when every vertex of the face lies on the plane of its outer ring to
    /// <paramref name="tol"/> metres.</summary>
    public bool IsPlanar(Em3dFace f, double tol)
    {
        var n = Normal(f);
        if (n is { X: 0, Y: 0, Z: 0 }) return false;
        var o = Vertices[f.Outer[0]];
        foreach (var ring in f.Holes.Prepend(f.Outer))
            foreach (int i in ring)
            {
                var q = Vertices[i];
                if (Math.Abs((q.X - o.X) * n.X + (q.Y - o.Y) * n.Y + (q.Z - o.Z) * n.Z) > tol) return false;
            }
        return true;
    }

    /// <summary>Every problem this polyhedron has, as sentences naming <paramref name="owner"/> and the
    /// face — all of them (R-em3d3-1c).</summary>
    public IReadOnlyList<string> Problems(string owner)
    {
        var problems = new List<string>();
        if (Vertices.Count < 4 || Faces.Count < 4)
        {
            problems.Add($"Polyhedron '{owner}' has {Vertices.Count} vertices and {Faces.Count} faces; a closed solid " +
                         "needs at least four of each.");
            return problems;
        }
        bool indicesOk = true;
        foreach (var f in Faces)
            foreach (var ring in f.Holes.Prepend(f.Outer))
            {
                if (ring.Count < 3)
                {
                    problems.Add($"Face '{f.Name}' of '{owner}' has a ring of {ring.Count} vertices; a ring needs three.");
                    indicesOk = false;
                }
                if (ring.Any(i => i < 0 || i >= Vertices.Count))
                {
                    problems.Add($"Face '{f.Name}' of '{owner}' names a vertex the polyhedron does not have.");
                    indicesOk = false;
                }
            }
        if (!indicesOk) return problems;

        double tol = 1e-9 * Math.Max(Extent(), 1e-12);
        foreach (var f in Faces)
            if (!IsPlanar(f, tol))
                problems.Add($"Face '{f.Name}' of '{owner}' is not planar: a vertex lies off the plane of its outline by " +
                             "more than a part in a billion of the solid's size.");

        // Closed: each directed edge once, and its reverse once.
        var edges = new Dictionary<(int, int), int>();
        foreach (var f in Faces)
            foreach (var ring in f.Holes.Prepend(f.Outer))
                for (int i = 0; i < ring.Count; i++)
                {
                    var e = (ring[i], ring[(i + 1) % ring.Count]);
                    edges[e] = edges.GetValueOrDefault(e) + 1;
                }
        int open = 0, doubled = 0;
        foreach (var ((a, b), n) in edges)
        {
            if (n > 1) doubled++;
            if (!edges.ContainsKey((b, a))) open++;
        }
        if (open > 0 || doubled > 0)
            problems.Add($"Polyhedron '{owner}' is not closed: {open} edge(s) belong to one face only" +
                         (doubled > 0 ? $", and {doubled} are used twice in the same direction (a face, or a hole, wound the wrong way)" : "") +
                         ". A solid's surface must meet itself along every edge.");

        if (!(SignedVolume() > 0))
            problems.Add($"Polyhedron '{owner}' encloses no positive volume: its faces must be wound counter-clockwise " +
                         "seen from outside.");
        return problems;
    }
}

/// <summary>
/// R-em3d42-1b — where a sheet's own (x, y) lie in the world: a point (x, y) of the outline, at the
/// sheet's Z, is <c>Origin + x·U + y·V + Z·(U × V)</c>. U and V are unit and perpendicular.
/// </summary>
public sealed record Em3dPlaneFrame(Point3 Origin, Point3 U, Point3 V)
{
    /// <summary>The frame's normal, U × V.</summary>
    public Point3 Normal => new(U.Y * V.Z - U.Z * V.Y, U.Z * V.X - U.X * V.Z, U.X * V.Y - U.Y * V.X);

    /// <summary>The world axis (0 x, 1 y, 2 z) the normal lies along, or null for an oblique plane.</summary>
    public int? NormalAxis
    {
        get
        {
            var n = Normal;
            double[] a = [Math.Abs(n.X), Math.Abs(n.Y), Math.Abs(n.Z)];
            for (int k = 0; k < 3; k++)
                if (a[k] > 1 - 1e-12 && a[(k + 1) % 3] < 1e-12 && a[(k + 2) % 3] < 1e-12) return k;
            return null;
        }
    }

    public Point3 World(double x, double y, double z)
    {
        var n = Normal;
        return new Point3(Origin.X + x * U.X + y * V.X + z * n.X,
                          Origin.Y + x * U.Y + y * V.Y + z * n.Y,
                          Origin.Z + x * U.Z + y * V.Z + z * n.Z);
    }
}
