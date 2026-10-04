// brief-em3d-49 R-em3d49-4c — a boundary on a NAMED face of a NAMED solid (em-3d.md §4.1, §6.4).
//
// The face is named by the PRIMITIVE's own face name, which the problem already carries: a box's world
// xmin…zmax, an extrusion's bottom, top, side<k> and hole<h>.side<k>, a cylinder's bottom, top and side, and
// a polyhedron's Em3dFace.Name (split faces share one name, so a boundary covers every piece). Nothing here
// names a face by index — the index is recovered from the name every time the problem is lowered.
//
// WHERE A BOUNDARY IS ALLOWED (R-em3d49-4b): on a DIELECTRIC or AIR solid's face, as a perfect conductor or a
// conductive surface. A conductor is already a void bounded by its metal, so a boundary on it adds nothing;
// an absorbing, PMC or symmetry face is something both backends state only on the domain's outer boundary,
// which is the air box's. Validate names each refusal.
//
// Each backend lowers it its own way — Palace a PEC or Conductivity boundary on the face's surfaces, openEMS a
// zero-thickness sheet coincident with the face, the FDTD grid that sheet's lines — and all three read the face
// through Em3dFaceGeometry, so they cannot disagree about where it is.

namespace CircuitRF.Engine.Em3d;

/// <summary>What a face boundary makes a face. Only <see cref="Pec"/> and <see cref="Conductive"/> are allowed on a
/// solid's face; the other three are the air box's, and a face boundary stating one is refused naming why.</summary>
public enum Em3dFaceBoundaryKind { Pec, Conductive, Absorbing, Pmc, Symmetry }

/// <summary>
/// A boundary on face <paramref name="Face"/> (the primitive's own face name) of solid <paramref name="Object"/>.
/// <paramref name="Material"/> is a <see cref="Em3dFaceBoundaryKind.Conductive"/> face's metal, a material of the
/// problem; null otherwise.
/// </summary>
public sealed record Em3dFaceBoundary(string Object, string Face, Em3dFaceBoundaryKind Kind, string? Material = null)
{
    /// <summary>The name its surfaces go by in a mesh group or a sheet: <c>object/face</c>.</summary>
    public string Name => Object + "/" + Face;
}

/// <summary>One planar piece of a named face, in world metres: an outer ring wound counter-clockwise seen from
/// outside the solid, hole rings the other way, and the outward unit normal.</summary>
public sealed record Em3dFacePolygon(IReadOnlyList<Point3> Outer, IReadOnlyList<IReadOnlyList<Point3>> Holes, Point3 Normal)
{
    /// <summary>The world axis (0 x, 1 y, 2 z) the normal lies along, or null for an oblique face.</summary>
    public int? NormalAxis
    {
        get
        {
            double[] a = [Math.Abs(Normal.X), Math.Abs(Normal.Y), Math.Abs(Normal.Z)];
            for (int k = 0; k < 3; k++)
                if (a[k] > 1 - 1e-12 && a[(k + 1) % 3] < 1e-12 && a[(k + 2) % 3] < 1e-12) return k;
            return null;
        }
    }

    /// <summary>
    /// brief-em3d-65 R-em3d65-2b — a kernel face's tight box, as the geometry kernel measured it: what the piece's bound
    /// is, where the outline is only its tessellation's (a hole's circle, a curved face). Null for every other primitive's.
    /// </summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1)? TightBox { get; init; }

    /// <summary>brief-em3d-65 R-em3d65-3f — the surface kind of a CURVED kernel face (<c>cylinder</c>, <c>torus</c>, …), whose
    /// piece is a box and its triangles, not a polygon: Palace recovers it by the box, openEMS cannot state it. Null for a
    /// flat piece.</summary>
    public string? CurvedKind { get; init; }

    /// <summary>A curved kernel face's own triangles, for drawing it; null for a flat piece, which draws its polygon.</summary>
    public Em3dTriangleMesh? Mesh { get; init; }

    /// <summary>The piece's bound, metres.</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Bounds()
    {
        if (TightBox is { } tight) return tight;
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var q in Outer)
        {
            x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y); z0 = Math.Min(z0, q.Z);
            x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y); z1 = Math.Max(z1, q.Z);
        }
        return (x0, y0, z0, x1, y1, z1);
    }

    /// <summary>
    /// The piece as a sheet coincident with it — what openEMS and the FDTD grid are given. An axis-aligned piece gets
    /// the axis's own frame (so openEMS's normal-to-an-axis polygon states it); an oblique one a frame on its first
    /// edge.
    /// </summary>
    public Em3dSheet AsSheet(string name, string material, double thicknessM, int order)
    {
        var o = Outer[0];
        Point3 u, v;
        switch (NormalAxis)
        {
            case 0: (u, v) = (new(0, 1, 0), new(0, 0, 1)); break;
            case 1: (u, v) = (new(1, 0, 0), new(0, 0, 1)); break;
            case 2: (u, v) = (new(1, 0, 0), new(0, 1, 0)); break;
            default:
            {
                var e = Sub(Outer[1], Outer[0]);
                u = Unit(e);
                v = Unit(Cross(Normal, u));
                break;
            }
        }
        var frame = new Em3dPlaneFrame(o, u, v);
        Point2 P(Point3 q) { var d = Sub(q, o); return new Point2(Dot(d, u), Dot(d, v)); }
        return new Em3dSheet(name, material, [.. Outer.Select(P)], [.. Holes.Select(h => (IReadOnlyList<Point2>)[.. h.Select(P)])],
                             0, thicknessM, order) { Frame = frame };
    }

    internal static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    internal static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static Point3 Cross(Point3 a, Point3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    internal static Point3 Unit(Point3 a)
    {
        double l = Math.Sqrt(Dot(a, a));
        return l > 0 ? new Point3(a.X / l, a.Y / l, a.Z / l) : a;
    }
}

public static class Em3dFaceGeometry
{
    /// <summary>
    /// A primitive's own face names, indexed by its face numbering (<see cref="Em3dTriangle.Face"/>'s): a box's
    /// <see cref="Em3dTessellation.BoxFaces"/>, an extrusion's bottom, top and ring edges, a cylinder's bottom, top and
    /// side, a polyhedron's faces by name. Empty for a primitive with no named faces (a sweep, a truncated ball).
    /// </summary>
    /// <para>brief-em3d-102 R-em3d102-4 — a sphere's one face is <c>surface</c>. Only a drawn sphere's provenance names it (a ball
    /// has none), so a boundary reaches it only on a drawn sphere, and <see cref="Pieces"/> refuses it as curved.</para>
    public static IReadOnlyList<string> FaceNames(Em3dPrimitive p) => p switch
    {
        Em3dBox => Em3dTessellation.BoxFaces,
        Em3dExtrudedPolygon e => ["bottom", "top", .. Enumerable.Range(0, e.Outline.Count).Select(k => $"side{k}"),
                                  .. e.Holes.SelectMany((h, i) => Enumerable.Range(0, h.Count).Select(k => $"hole{i}.side{k}"))],
        Em3dCylinder => ["bottom", "top", "side"],
        Em3dSphere => ["surface"],
        Em3dPolyhedron ph => [.. ph.Faces.Select(f => f.Name)],
        Em3dShapeSolid k => [.. k.Faces.Select(f => f.Name)],
        _ => [],
    };

    /// <summary>
    /// The planar pieces of face <paramref name="face"/> of <paramref name="p"/>, or null with <paramref name="why"/>:
    /// the primitive has no such face, or the face is curved (a cylinder's side), which no box query recovers and no
    /// flat sheet covers.
    /// </summary>
    public static IReadOnlyList<Em3dFacePolygon>? Pieces(Em3dPrimitive p, string face, out string? why)
    {
        why = null;
        switch (p)
        {
            case Em3dBox b:
            {
                int k = Array.IndexOf(Em3dTessellation.BoxFaces, face);
                if (k < 0) break;
                var (lo, hi) = (b.Min, b.Max);
                Point3 V(int bits) => new((bits & 1) == 0 ? lo.X : hi.X, (bits & 2) == 0 ? lo.Y : hi.Y, (bits & 4) == 0 ? lo.Z : hi.Z);
                // Outward, counter-clockwise seen from outside — Em3dTessellation.Box's quads.
                int[] q = k switch
                {
                    0 => [0, 4, 6, 2], 1 => [1, 3, 7, 5], 2 => [0, 1, 5, 4],
                    3 => [2, 6, 7, 3], 4 => [0, 2, 3, 1], _ => [4, 5, 7, 6],
                };
                var n = k switch
                {
                    0 => new Point3(-1, 0, 0), 1 => new Point3(1, 0, 0), 2 => new Point3(0, -1, 0),
                    3 => new Point3(0, 1, 0), 4 => new Point3(0, 0, -1), _ => new Point3(0, 0, 1),
                };
                return [new Em3dFacePolygon([.. q.Select(V)], [], n)];
            }
            case Em3dExtrudedPolygon e:
            {
                IReadOnlyList<Point3> At(IReadOnlyList<Point2> ring, double z, bool wantCcw)
                {
                    var pts = ring.Select(q => new Point3(q.X, q.Y, z)).ToList();
                    if ((Em3dPolygonTriangulation.SignedArea2(ring) > 0) != wantCcw) pts.Reverse();
                    return pts;
                }
                if (face == "bottom")
                    return [new Em3dFacePolygon(At(e.Outline, e.ZBottom, false), [.. e.Holes.Select(h => At(h, e.ZBottom, true))], new(0, 0, -1))];
                if (face == "top")
                    return [new Em3dFacePolygon(At(e.Outline, e.ZTop, true), [.. e.Holes.Select(h => At(h, e.ZTop, false))], new(0, 0, 1))];
                IReadOnlyList<Point2>? ring = null;
                int edge = -1;
                bool isHole = false;
                if (face.StartsWith("side", StringComparison.Ordinal) && int.TryParse(face.AsSpan(4), out int s) && s >= 0 && s < e.Outline.Count)
                    (ring, edge) = (e.Outline, s);
                else if (face.StartsWith("hole", StringComparison.Ordinal) && face.IndexOf(".side", StringComparison.Ordinal) is int dot and > 4 &&
                         int.TryParse(face.AsSpan(4, dot - 4), out int h) && h >= 0 && h < e.Holes.Count &&
                         int.TryParse(face.AsSpan(dot + 5), out int hs) && hs >= 0 && hs < e.Holes[h].Count)
                    (ring, edge, isHole) = (e.Holes[h], hs, true);
                if (ring is null) break;
                var a = ring[edge];
                var c = ring[(edge + 1) % ring.Count];
                // Outward: the solid lies to the left of an outline walked counter-clockwise, and to the right of a hole's.
                bool ringCcw = Em3dPolygonTriangulation.SignedArea2(ring) > 0;
                bool forward = isHole ? !ringCcw : ringCcw;
                if (!forward) (a, c) = (c, a);
                double dx = c.X - a.X, dy = c.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                var nrm = len > 0 ? new Point3(dy / len, -dx / len, 0) : new Point3(0, 0, 0);
                return [new Em3dFacePolygon(
                    [new(a.X, a.Y, e.ZBottom), new(c.X, c.Y, e.ZBottom), new(c.X, c.Y, e.ZTop), new(a.X, a.Y, e.ZTop)], [], nrm)];
            }
            case Em3dCylinder cyl:
            {
                if (face == "side")
                {
                    why = "it is a cylinder's curved side, and a boundary is placed on a flat face";
                    return null;
                }
                if (face is not ("bottom" or "top")) break;
                var axis = Em3dFacePolygon.Sub(cyl.AxisEnd, cyl.AxisStart);
                var dir = Em3dFacePolygon.Unit(axis);
                var (u, w) = Perpendiculars(dir);
                var centre = face == "top" ? cyl.AxisEnd : cyl.AxisStart;
                var pts = new List<Point3>();
                int n = Em3dTessellation.CylinderSegments;
                for (int k = 0; k < n; k++)
                {
                    double t = 2 * Math.PI * k / n;
                    double cu = cyl.Radius * Math.Cos(t), cw = cyl.Radius * Math.Sin(t);
                    pts.Add(new Point3(centre.X + u.X * cu + w.X * cw, centre.Y + u.Y * cu + w.Y * cw, centre.Z + u.Z * cu + w.Z * cw));
                }
                var normal = face == "top" ? dir : new Point3(-dir.X, -dir.Y, -dir.Z);
                if (face == "bottom") pts.Reverse();
                return [new Em3dFacePolygon(pts, [], normal)];
            }
            case Em3dSphere when face == "surface":
                why = "it is a sphere's curved surface, and a boundary is placed on a flat face";
                return null;
            case Em3dPolyhedron ph:
            {
                var list = new List<Em3dFacePolygon>();
                foreach (var f in ph.Faces.Where(f => f.Name == face))
                    list.Add(new Em3dFacePolygon([.. f.Outer.Select(i => ph.Vertices[i])],
                                                 [.. f.Holes.Select(h => (IReadOnlyList<Point3>)[.. h.Select(i => ph.Vertices[i])])],
                                                 ph.Normal(f)));
                if (list.Count > 0) return list;
                break;
            }
            case Em3dShapeSolid k:
            {
                // brief-em3d-64 — a kernel face is recovered by name, every piece of it (zmax covers zmax#1 and zmax#2).
                // brief-em3d-65 R-em3d65-2c/3f — ONE piece per face entry, bounded by the kernel's tight box: Palace recovers
                // it by that box, and a flat one is its triangles' boundary loops for openEMS's sheet. A curved one is a box
                // and its triangles, which Palace takes and openEMS refuses (CurvedKind says which).
                var list = new List<Em3dFacePolygon>();
                foreach (var f in k.Faces.Where(f => f.Name == face || IsPiece(face, f.Name)))
                    list.Add(KernelPiece(k, f));
                if (list.Count > 0) return list;
                break;
            }
            default:
                why = "it has no named faces";
                return null;
        }
        why = $"it has no face named '{face}'";
        return null;
    }

    /// <summary>
    /// brief-em3d-65 — one kernel face as a piece. Flat: the boundary loops of its triangle range (edges used by one of its
    /// triangles only, chained in index order; the largest ring is the outline, the rest holes), the outward normal — an axis
    /// exactly when the tight box has no thickness along one. Curved: its triangles, with no normal.
    /// </summary>
    private static Em3dFacePolygon KernelPiece(Em3dShapeSolid k, Em3dShapeFace f)
    {
        var tris = new List<Em3dTriangle>(f.TriangleCount);
        for (int t = f.FirstTriangle; t < f.FirstTriangle + f.TriangleCount && t < k.Display.Triangles.Count; t++) tris.Add(k.Display.Triangles[t]);
        if (f.Kind != "plane")
        {
            // The mesh's own vertices, renumbered to this face.
            var at = new Dictionary<int, int>();
            var verts = new List<Point3>();
            int V(int i)
            {
                if (at.TryGetValue(i, out int j)) return j;
                at[i] = verts.Count;
                verts.Add(k.Display.Vertices[i]);
                return verts.Count - 1;
            }
            var mine = tris.Select(t => new Em3dTriangle(V(t.A), V(t.B), V(t.C), t.Solid, t.Face)).ToList();
            return new Em3dFacePolygon([], [], new Point3(0, 0, 0))
            {
                TightBox = f.Box, CurvedKind = f.Kind, Mesh = new Em3dTriangleMesh(verts, mine),
            };
        }

        // The normal: an axis when the box says so (exactly, so NormalAxis reads it), else the area-weighted sum.
        Point3 normal;
        if (Em3dShapeSolid.NormalAxis(f) is int axis)
        {
            double sum = 0;
            foreach (var t in tris)
            {
                var n = Em3dFacePolygon.Cross(Em3dFacePolygon.Sub(k.Display.Vertices[t.B], k.Display.Vertices[t.A]),
                                              Em3dFacePolygon.Sub(k.Display.Vertices[t.C], k.Display.Vertices[t.A]));
                sum += axis == 0 ? n.X : axis == 1 ? n.Y : n.Z;
            }
            double sign = sum < 0 ? -1 : 1;
            normal = axis == 0 ? new Point3(sign, 0, 0) : axis == 1 ? new Point3(0, sign, 0) : new Point3(0, 0, sign);
        }
        else
        {
            var acc = new Point3(0, 0, 0);
            foreach (var t in tris)
            {
                var n = Em3dFacePolygon.Cross(Em3dFacePolygon.Sub(k.Display.Vertices[t.B], k.Display.Vertices[t.A]),
                                              Em3dFacePolygon.Sub(k.Display.Vertices[t.C], k.Display.Vertices[t.A]));
                acc = new Point3(acc.X + n.X, acc.Y + n.Y, acc.Z + n.Z);
            }
            normal = Em3dFacePolygon.Unit(acc);
        }

        // Boundary loops: a directed edge whose reverse no triangle of the face uses. Outward triangles walk the outline
        // counter-clockwise seen from outside and each hole the other way — the orientation Em3dFacePolygon states.
        var directed = new HashSet<(int, int)>();
        foreach (var t in tris) { directed.Add((t.A, t.B)); directed.Add((t.B, t.C)); directed.Add((t.C, t.A)); }
        var next = new SortedDictionary<int, int>();
        foreach (var (a, b) in directed)
            if (!directed.Contains((b, a))) next.TryAdd(a, b);
        var rings = new List<IReadOnlyList<Point3>>();
        var used = new HashSet<int>();
        foreach (int start in next.Keys)
        {
            if (used.Contains(start)) continue;
            var ring = new List<Point3>();
            int v = start;
            while (used.Add(v) && next.TryGetValue(v, out int w))
            {
                ring.Add(k.Display.Vertices[v]);
                v = w;
            }
            if (ring.Count >= 3) rings.Add(ring);
        }
        if (rings.Count == 0) return new Em3dFacePolygon([], [], normal) { TightBox = f.Box };
        double Area(IReadOnlyList<Point3> r)
        {
            var acc = new Point3(0, 0, 0);
            for (int i = 0; i < r.Count; i++)
            {
                var c = Em3dFacePolygon.Cross(r[i], r[(i + 1) % r.Count]);
                acc = new Point3(acc.X + c.X, acc.Y + c.Y, acc.Z + c.Z);
            }
            return Math.Abs(Em3dFacePolygon.Dot(acc, normal));
        }
        int outer = 0;
        for (int i = 1; i < rings.Count; i++) if (Area(rings[i]) > Area(rings[outer])) outer = i;
        return new Em3dFacePolygon(rings[outer], [.. rings.Where((_, i) => i != outer)], normal) { TightBox = f.Box };
    }

    /// <summary>The fold rule: <paramref name="candidate"/> is a piece of <paramref name="face"/> when it is <c>face#n</c>.</summary>
    public static bool IsPiece(string face, string candidate)
        => candidate.Length > face.Length + 1 && candidate.StartsWith(face, StringComparison.Ordinal) && candidate[face.Length] == '#'
           && candidate.AsSpan(face.Length + 1).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>Two unit vectors perpendicular to <paramref name="a"/> and to each other — Em3dTessellation's frame.</summary>
    private static (Point3 U, Point3 W) Perpendiculars(Point3 a)
    {
        var helper = Math.Abs(a.Z) < 0.9 ? new Point3(0, 0, 1) : new Point3(1, 0, 0);
        var u = Em3dFacePolygon.Unit(Em3dFacePolygon.Cross(helper, a));
        var w = Em3dFacePolygon.Cross(a, u);
        return (u, w);
    }
}

/// <summary>
/// brief-em3d-49 R-em3d49-4c — face boundaries as the FDTD backend states them: each piece a sheet coincident with its
/// face, of PEC or of its metal, at a priority above every solid, so the cell it lies on is the boundary's whatever the
/// solid is. openEMS's conducting sheet takes a thickness, and a face boundary is the outside of a THICK conductor, so a
/// Conductive face is given three skin depths at the problem's top frequency: electrically thick, and still a sheet.
/// </summary>
public static class Em3dFaceSheets
{
    /// <summary>The material a perfectly conducting face is made of: infinite σ, which the writer states as metal.</summary>
    public const string PecMaterial = "(perfect conductor)";

    /// <summary>Skin depths a conductive face's sheet is thick.</summary>
    public const double SkinDepths = 3;

    /// <summary><paramref name="problem"/> with its face boundaries as sheets — the same instance when it has none.</summary>
    public static Em3dProblem Apply(Em3dProblem problem)
    {
        if (problem.FaceBoundaries.Count == 0) return problem;
        int order = Math.Max(problem.Solids.Select(s => s.Order).DefaultIfEmpty(0).Max(),
                             problem.Sheets.Select(s => s.Order).DefaultIfEmpty(0).Max()) + 1;
        var sheets = new List<Em3dSheet>(problem.Sheets);
        var materials = new List<Em3dMaterial>(problem.Materials);
        double f = problem.Type == Em3dProblemType.Eigenmode ? problem.EigenmodeTargetHz : problem.Frequency.StopHz;
        foreach (var (b, pieces) in problem.FaceBoundaryPieces())
        {
            string material;
            double thickness = 0;
            if (b.Kind == Em3dFaceBoundaryKind.Pec)
            {
                if (!materials.Any(m => m.Name == PecMaterial))
                    materials.Add(new Em3dMaterial(PecMaterial, 1, null, 0, 1, double.PositiveInfinity));
                material = PecMaterial;
            }
            else
            {
                var metal = materials.First(m => m.Name == b.Material);
                material = metal.Name;
                double mu0 = 4e-7 * Math.PI;
                thickness = SkinDepths / Math.Sqrt(Math.PI * Math.Max(f, 1) * mu0 * metal.Mur * metal.SigmaSm);
            }
            // brief-em3d-65 — a curved kernel face is no sheet; the writer refuses the boundary for openEMS, naming it.
            if (pieces.Any(pc => pc.CurvedKind is not null)) continue;
            for (int k = 0; k < pieces.Count; k++)
                sheets.Add(pieces[k].AsSheet(pieces.Count == 1 ? b.Name : $"{b.Name}#{k + 1}", material, thickness, order));
        }
        return problem with { Sheets = sheets, Materials = materials, FaceBoundaries = [] };
    }
}
