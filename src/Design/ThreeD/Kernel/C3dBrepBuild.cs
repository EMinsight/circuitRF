// brief-em3d-47 R-em3d47-1b — every solid primitive as a B-rep with its FIXED face names (brief 41 §2c), and back.
//
// THE VERTEX LAYOUT IS PART OF THE CONTRACT. A box's vertex k is the corner with x, y, z at their max where bits
// 0, 1, 2 of k are set; a prism's ring vertex k is vertex 2(s + k) on the bottom and 2(s + k) + 1 on the top,
// where s counts the vertices of the rings before it (C3dLowering.Prism's order). The kernel never renumbers a
// vertex, so after an edit the SAME indices say whether the result is still that primitive (Read*) — which is
// how "can the primitive state it as a change of its own fields" is answered (R-em3d47-1b): not by a rule per
// operation, but by reading the kernel's own answer back.
//
// A CYLINDER is not faceted here unless asked (Convert to Polyhedron, R-em3d47-2): its faces are curved, and
// the tessellation's vertices are not design (R-em3d47-5).

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD.Kernel;

public static class C3dBrepBuild
{
    /// <summary>The world axes a drawing plane spans (u, v) and its normal (w): XY → (x, y) along z, YZ → (y, z) along
    /// x, XZ → (x, z) along y — the lowering's own convention.</summary>
    public static (int U, int V, int W) Axes(C3dPlane plane) => plane switch
    {
        C3dPlane.YZ => (1, 2, 0),
        C3dPlane.XZ => (0, 2, 1),
        _           => (0, 1, 2),
    };

    /// <summary>The plane normal to world axis <paramref name="axis"/> (0 x, 1 y, 2 z).</summary>
    public static C3dPlane PlaneNormalTo(int axis) => axis switch { 0 => C3dPlane.YZ, 1 => C3dPlane.XZ, _ => C3dPlane.XY };

    public static long Get(C3dPoint3 p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };

    public static C3dPoint3 With(C3dPoint3 p, int axis, long value) => axis switch
    {
        0 => p with { X = value },
        1 => p with { Y = value },
        _ => p with { Z = value },
    };

    /// <summary>(u, v) at height w on <paramref name="plane"/>, as a point of the object's frame.</summary>
    public static C3dPoint3 OnPlane(C3dPlane plane, long u, long v, long w)
    {
        var (a, b, c) = Axes(plane);
        var p = With(With(With(default, a, u), b, v), c, w);
        return p;
    }

    /// <summary>A point of the object's frame as (u, v, w) of <paramref name="plane"/>.</summary>
    public static (long U, long V, long W) ToPlane(C3dPlane plane, C3dPoint3 p)
    {
        var (a, b, c) = Axes(plane);
        return (Get(p, a), Get(p, b), Get(p, c));
    }

    /// <summary>The B-rep of a box, prism or polyhedron; null for anything else (a cylinder is curved; a sheet and a
    /// polyline are not solids).</summary>
    public static C3dBrep? Of(C3dObject o) => o switch
    {
        C3dBox b => Box(b),
        C3dPrism p => Prism(p),
        C3dPolyhedron h => Polyhedron(h),
        _ => null,
    };

    // ── box ──────────────────────────────────────────────────────────────────────────────────

    private static readonly int[][] BoxRings =
    [
        [0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6],
    ];

    public static C3dBrep Box(C3dBox b)
    {
        long x0 = Math.Min(b.Min.X, b.Min.X + b.Size.X), x1 = Math.Max(b.Min.X, b.Min.X + b.Size.X);
        long y0 = Math.Min(b.Min.Y, b.Min.Y + b.Size.Y), y1 = Math.Max(b.Min.Y, b.Min.Y + b.Size.Y);
        long z0 = Math.Min(b.Min.Z, b.Min.Z + b.Size.Z), z1 = Math.Max(b.Min.Z, b.Min.Z + b.Size.Z);
        var v = new C3dPoint3[8];
        for (int k = 0; k < 8; k++)
            v[k] = new C3dPoint3((k & 1) == 0 ? x0 : x1, (k & 2) == 0 ? y0 : y1, (k & 4) == 0 ? z0 : z1);
        var faces = new C3dBrepFace[6];
        for (int f = 0; f < 6; f++) faces[f] = new C3dBrepFace(C3dBox.FaceNameList[f], BoxRings[f], []);
        return new C3dBrep(v, new C3dBrepTopology(faces, 8));
    }

    /// <summary>A kernel result read back as a box, with the vertex layout <see cref="Box"/> made: every x the same
    /// where bit 0 is, and so on. Null when it is not one.</summary>
    public static (C3dPoint3 Min, C3dPoint3 Size)? ReadBox(C3dBrep r)
    {
        if (r.Vertices.Count != 8 || r.Faces.Count != 6) return null;
        var v = r.Vertices;
        long x0 = v[0].X, x1 = v[1].X, y0 = v[0].Y, y1 = v[2].Y, z0 = v[0].Z, z1 = v[4].Z;
        for (int k = 0; k < 8; k++)
            if (v[k].X != ((k & 1) == 0 ? x0 : x1) || v[k].Y != ((k & 2) == 0 ? y0 : y1) || v[k].Z != ((k & 4) == 0 ? z0 : z1))
                return null;
        if (x1 <= x0 || y1 <= y0 || z1 <= z0) return null;
        return (new C3dPoint3(x0, y0, z0), new C3dPoint3(x1 - x0, y1 - y0, z1 - z0));
    }

    // ── prism ────────────────────────────────────────────────────────────────────────────────

    private static Int128 Area2(IReadOnlyList<C3dPoint2> ring)
    {
        Int128 a = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            a += (Int128)p.U * q.V - (Int128)q.U * p.V;
        }
        return a;
    }

    public static C3dBrep Prism(C3dPrism p)
    {
        var rings = new List<IReadOnlyList<C3dPoint2>> { p.Outline };
        rings.AddRange(p.Holes);
        var names = p.FaceNames();
        var vertices = new List<C3dPoint3>();
        var bottom = new List<int[]>();
        var top = new List<int[]>();
        foreach (var r in rings)
        {
            var b = new int[r.Count];
            var t = new int[r.Count];
            for (int k = 0; k < r.Count; k++)
            {
                b[k] = vertices.Count; vertices.Add(OnPlane(p.Plane, r[k].U, r[k].V, p.Offset));
                t[k] = vertices.Count; vertices.Add(OnPlane(p.Plane, r[k].U + p.Shear.U, r[k].V + p.Shear.V, p.Offset + p.Height));
            }
            bottom.Add(b); top.Add(t);
        }
        // Counter-clockwise in (u, v) is positive: the outline wants that on top, a hole the other way (C3dLowering.Prism).
        bool Ccw(int i) => Area2(rings[i]) > 0;
        static int[] Maybe(int[] ring, bool reverse) => reverse ? [.. ring.Reverse()] : ring;
        var faces = new List<C3dBrepFace>
        {
            new(names[0], Maybe(bottom[0], Ccw(0)), [.. Enumerable.Range(1, p.Holes.Count).Select(i => Maybe(bottom[i], !Ccw(i)))]),
            new(names[1], Maybe(top[0], !Ccw(0)), [.. Enumerable.Range(1, p.Holes.Count).Select(i => Maybe(top[i], Ccw(i)))]),
        };
        int side = 2;
        for (int i = 0; i < rings.Count; i++)
        {
            int n = rings[i].Count;
            bool outward = i == 0 ? Ccw(i) : !Ccw(i);
            for (int k = 0; k < n; k++)
            {
                int j = (k + 1) % n;
                faces.Add(new C3dBrepFace(names[side++], Maybe([bottom[i][k], bottom[i][j], top[i][j], top[i][k]], !outward), []));
            }
        }
        var brep = new C3dBrep(vertices, new C3dBrepTopology(faces, vertices.Count));
        return brep.Volume6 < 0 ? Reversed(brep) : brep;
    }

    /// <summary>A kernel result read back as <paramref name="source"/>'s prism: every bottom vertex at one height,
    /// every top vertex at another on the same side, and the top the bottom moved by one shear. Null when it is not.</summary>
    public static C3dPrism? ReadPrism(C3dBrep r, C3dPrism source)
    {
        int count = source.Outline.Count + source.Holes.Sum(h => h.Count);
        if (r.Vertices.Count != 2 * count || r.Faces.Count != 2 + count) return null;
        var b0 = ToPlane(source.Plane, r.Vertices[0]);
        var t0 = ToPlane(source.Plane, r.Vertices[1]);
        long w0 = b0.W, w1 = t0.W, height = w1 - w0;
        if (height == 0 || Math.Sign(height) != Math.Sign(source.Height)) return null;
        var shear = new C3dPoint2(t0.U - b0.U, t0.V - b0.V);
        var rings = new List<List<C3dPoint2>>();
        int at = 0;
        foreach (int n in new[] { source.Outline.Count }.Concat(source.Holes.Select(h => h.Count)))
        {
            var ring = new List<C3dPoint2>(n);
            for (int k = 0; k < n; k++, at++)
            {
                var b = ToPlane(source.Plane, r.Vertices[2 * at]);
                var t = ToPlane(source.Plane, r.Vertices[2 * at + 1]);
                if (b.W != w0 || t.W != w1 || t.U - b.U != shear.U || t.V - b.V != shear.V) return null;
                ring.Add(new C3dPoint2(b.U, b.V));
            }
            rings.Add(ring);
        }
        var copy = (C3dPrism)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(source));
        copy.Offset = w0;
        copy.Height = height;
        copy.Shear = shear;
        copy.Outline = rings[0];
        copy.Holes = [.. rings.Skip(1)];
        return copy;
    }

    // ── cylinder (faceted only when asked) ───────────────────────────────────────────────────

    /// <summary>
    /// A cylinder as <paramref name="facets"/> flat sides, each corner rounded to DBU once — the caps stay exactly flat
    /// (one height each) and every side is exactly a rectangle (top = bottom + the axis). The caps keep their names;
    /// the side's pieces are <c>side.&lt;k&gt;</c>, as a fold names them. Null, with the reason, when rounding makes two
    /// corners one.
    /// </summary>
    public static C3dBrep? Cylinder(C3dCylinder c, int facets, out string? refusal)
    {
        refusal = null;
        int a = (int)c.Axis;
        int u = (a + 1) % 3, v = (a + 2) % 3;            // right-handed about the axis
        var bottom = new int[facets];
        var top = new int[facets];
        var vertices = new List<C3dPoint3>();
        for (int k = 0; k < facets; k++)
        {
            double t = 2 * Math.PI * k / facets;
            long du = (long)Math.Round(c.Radius * Math.Cos(t), MidpointRounding.AwayFromZero);
            long dv = (long)Math.Round(c.Radius * Math.Sin(t), MidpointRounding.AwayFromZero);
            var p = With(With(c.Base, u, Get(c.Base, u) + du), v, Get(c.Base, v) + dv);
            if (k > 0 && vertices[bottom[k - 1]] == p)
            {
                refusal = $"'{c.Name}' is too small to cut into {facets} sides at one DBU: two corners would coincide.";
                return null;
            }
            bottom[k] = vertices.Count; vertices.Add(p);
            top[k] = vertices.Count; vertices.Add(With(p, a, Get(p, a) + c.Length));
        }
        var faces = new List<C3dBrepFace>
        {
            new("bottom", [.. bottom.Reverse()], []),
            new("top", top, []),
        };
        for (int k = 0; k < facets; k++)
        {
            int j = (k + 1) % facets;
            faces.Add(new C3dBrepFace($"side.{k}", [bottom[k], bottom[j], top[j], top[k]], []));
        }
        var brep = new C3dBrep(vertices, new C3dBrepTopology(faces, vertices.Count));
        return brep.Volume6 < 0 ? Reversed(brep) : brep;
    }

    // ── polyhedron ───────────────────────────────────────────────────────────────────────────

    public static C3dBrep Polyhedron(C3dPolyhedron h)
        => new(h.Vertices.ToList(),
               new C3dBrepTopology([.. h.Faces.Select(f => new C3dBrepFace(f.Name, [.. f.Outer], [.. f.Holes.Select(x => x.ToArray())]))],
                                   h.Vertices.Count));

    /// <summary><paramref name="r"/> as a polyhedron object carrying <paramref name="source"/>'s name, material, role,
    /// placement, visibility and unread keys — everything but its geometry.</summary>
    public static C3dPolyhedron ToPolyhedron(C3dBrep r, C3dObject source)
    {
        var template = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(source));
        return new C3dPolyhedron
        {
            Name = template.Name,
            Material = template.Material,
            Role = template.Role,
            Placement = template.Placement,
            Hidden = template.Hidden,
            Unread = template.Unread,
            Vertices = [.. r.Vertices],
            Faces = [.. r.Faces.Select(f => new C3dFace { Name = f.Name, Outer = [.. f.Outer], Holes = [.. f.Holes.Select(x => x.ToList())] })],
        };
    }

    /// <summary>Every ring of every face turned the other way.</summary>
    public static C3dBrep Reversed(C3dBrep b)
        => new(b.Vertices, new C3dBrepTopology([.. b.Faces.Select(f => new C3dBrepFace(f.Name, [.. f.Outer.Reverse()],
                                                                                      [.. f.Holes.Select(h => h.Reverse().ToArray())]))],
                                               b.Vertices.Count));

    /// <summary>The metres of a DBU point, for the lowering and the overlay.</summary>
    public static Point3 Metres(C3dPoint3 p, int dbuPerMicron)
        => new(C3dLowering.Metres(p.X, dbuPerMicron), C3dLowering.Metres(p.Y, dbuPerMicron), C3dLowering.Metres(p.Z, dbuPerMicron));
}
