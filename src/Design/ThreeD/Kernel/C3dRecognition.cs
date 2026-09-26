// brief-em3d-47 R-em3d47-1c — the lowering recognises what it can. A document polyhedron that is EXACTLY an
// axis-aligned box, or exactly a prism along z, lowers as Em3dBox or Em3dExtrudedPolygon, so the FDTD path stays
// on primitives after the user has edited through a polyhedron and back. The DOCUMENT keeps what the user made:
// recognition lives here, in the lowering, and nowhere else.
//
// EXACT means integer equality, never a tolerance: a box is eight distinct corners, two values per axis, and six
// faces each lying at one of them; a z-prism is a bottom face and a top face at two heights, every other vertex
// directly above or below its twin, and every side a vertical quad joining one bottom edge to the top edge above
// it. The face names travel, indexed as the neutral primitive numbers its faces.
//
// A FACE THAT IS PLANAR TO A DBU BUT NOT EXACTLY (a vertex rounded once on a tilted face) is what `check` and the
// kernel both call planar (R-em3d47-3c), and what the neutral problem's own check (1e-9 of the solid's size) does
// not. The lowering splits such a face into triangles, every one carrying the face's name — so the solver gets
// exactly planar faces and nothing attached to the face is lost; the document is unchanged.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD.Kernel;

public static class C3dRecognition
{
    /// <summary>The polyhedron as an axis-aligned box (DBU) and its face names in <c>xmin … zmax</c> order, or null.</summary>
    public static (C3dPoint3 Min, C3dPoint3 Max, string[] Names)? Box(C3dPolyhedron h)
    {
        if (h.Faces.Count != 6 || h.Faces.Any(f => f.Outer.Count != 4 || f.Holes.Count > 0)) return null;
        var used = h.Faces.SelectMany(f => f.Outer).Distinct().ToList();
        if (used.Count != 8 || used.Any(i => i < 0 || i >= h.Vertices.Count)) return null;
        var pts = used.Select(i => h.Vertices[i]).ToList();
        if (pts.Distinct().Count() != 8) return null;
        long[] lo = new long[3], hi = new long[3];
        for (int a = 0; a < 3; a++)
        {
            var values = pts.Select(p => C3dBrepBuild.Get(p, a)).Distinct().ToList();
            if (values.Count != 2) return null;
            lo[a] = values.Min(); hi[a] = values.Max();
        }
        var names = new string?[6];
        foreach (var f in h.Faces)
        {
            int slot = -1;
            for (int a = 0; a < 3 && slot < 0; a++)
            {
                long v = C3dBrepBuild.Get(h.Vertices[f.Outer[0]], a);
                if (f.Outer.All(i => C3dBrepBuild.Get(h.Vertices[i], a) == v)) slot = 2 * a + (v == hi[a] ? 1 : 0);
            }
            if (slot < 0 || names[slot] is not null) return null;
            names[slot] = f.Name;
        }
        if (!Outward(h)) return null;
        return (new C3dPoint3(lo[0], lo[1], lo[2]), new C3dPoint3(hi[0], hi[1], hi[2]), [.. names.Select(n => n!)]);
    }

    /// <summary>
    /// The polyhedron as a prism along z: its outline and holes (the top face's rings, which run as its outline does), its two
    /// heights, and its face names in the extrusion's numbering (bottom, top, then one per ring edge). Null when it is not
    /// exactly one.
    /// </summary>
    public static (List<List<C3dPoint2>> Rings, long Z0, long Z1, string[] Names)? ZPrism(C3dPolyhedron h)
    {
        if (h.Faces.Count < 5) return null;
        var v = h.Vertices;
        var zs = h.Faces.SelectMany(f => f.Outer.Concat(f.Holes.SelectMany(x => x))).Select(i => v[i].Z).Distinct().ToList();
        if (zs.Count != 2) return null;
        long z0 = zs.Min(), z1 = zs.Max();
        C3dFace? bottom = null, top = null;
        foreach (var f in h.Faces)
        {
            var all = f.Outer.Concat(f.Holes.SelectMany(x => x)).ToList();
            if (all.All(i => v[i].Z == z0)) { if (bottom is not null) return null; bottom = f; }
            else if (all.All(i => v[i].Z == z1)) { if (top is not null) return null; top = f; }
        }
        if (bottom is null || top is null) return null;
        // Every vertex has its twin directly below or above it.
        var below = new Dictionary<(long, long), int>();
        foreach (int i in bottom.Outer.Concat(bottom.Holes.SelectMany(x => x))) if (!below.TryAdd((v[i].X, v[i].Y), i)) return null;
        // The TOP's rings, which run the way the outline does seen from above (the bottom's run the other way).
        var topRings = new List<List<int>> { top.Outer };
        topRings.AddRange(top.Holes);
        int ringVertices = topRings.Sum(r => r.Count);
        if (below.Count != ringVertices || h.Faces.Count != 2 + ringVertices) return null;
        foreach (var r in topRings) foreach (int i in r) if (!below.ContainsKey((v[i].X, v[i].Y))) return null;

        // Each ring edge's side: the vertical quad on that top edge and the bottom edge below it.
        var sides = new Dictionary<(int, int), string>();
        foreach (var f in h.Faces)
        {
            if (ReferenceEquals(f, bottom) || ReferenceEquals(f, top)) continue;
            if (f.Holes.Count > 0 || f.Outer.Count != 4) return null;
            var lows = f.Outer.Where(i => v[i].Z == z0).ToList();
            var highs = f.Outer.Where(i => v[i].Z == z1).ToList();
            if (lows.Count != 2 || highs.Count != 2) return null;
            if (!lows.All(b => highs.Any(t => v[b].X == v[t].X && v[b].Y == v[t].Y))) return null;
            var key = (Math.Min(highs[0], highs[1]), Math.Max(highs[0], highs[1]));
            if (!sides.TryAdd(key, f.Name)) return null;
        }
        var names = new List<string> { bottom.Name, top.Name };
        var rings = new List<List<C3dPoint2>>();
        foreach (var r in topRings)
        {
            rings.Add([.. r.Select(i => new C3dPoint2(v[i].X, v[i].Y))]);
            for (int k = 0; k < r.Count; k++)
            {
                int a = r[k], b = r[(k + 1) % r.Count];
                if (!sides.TryGetValue((Math.Min(a, b), Math.Max(a, b)), out var n)) return null;
                names.Add(n);
            }
        }
        if (!Outward(h)) return null;
        return (rings, z0, z1, [.. names]);
    }

    /// <summary>Closed, and wound outward: a recognised solid must BE a solid, not only have the shape of one.</summary>
    private static bool Outward(C3dPolyhedron h)
    {
        var b = C3dBrepBuild.Polyhedron(h);
        return b.ClosureProblem() is null && b.Volume6 > 0;
    }

    /// <summary>
    /// R-em3d47-1c — a polyhedron's faces for the solver: each exactly planar face as it is, and each face planar only to a
    /// DBU split into triangles carrying its name. Null when every face is exactly planar (the common case: nothing to do).
    /// </summary>
    public static List<C3dFace>? ExactlyPlanarFaces(C3dPolyhedron h)
    {
        var b = C3dBrepBuild.Polyhedron(h);
        List<C3dFace>? faces = null;
        for (int f = 0; f < h.Faces.Count; f++)
        {
            bool bad = h.Faces[f].Outer.Count > 3 || h.Faces[f].Holes.Count > 0 ? !b.IsExactlyPlanar(f) : false;
            if (!bad) { faces?.Add(h.Faces[f]); continue; }
            faces ??= [.. h.Faces.Take(f)];
            var nd = b.Normal(f);
            foreach (var (a, c, e) in C3dKernel.Triangulate(b, f))
            {
                var (x, y, z) = C3dExact.Cross(b.Vertices[a], b.Vertices[c], b.Vertices[e]);
                bool flip = (double)x * nd.X + (double)y * nd.Y + (double)z * nd.Z < 0;
                faces.Add(new C3dFace { Name = h.Faces[f].Name, Outer = flip ? [a, e, c] : [a, c, e] });
            }
        }
        return faces;
    }

    /// <summary>A recognised box in metres.</summary>
    public static Em3dBox BoxMetres(C3dPoint3 min, C3dPoint3 max, int dbuPerMicron)
        => new(C3dBrepBuild.Metres(min, dbuPerMicron), C3dBrepBuild.Metres(max, dbuPerMicron));
}
