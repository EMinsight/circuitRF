// brief-em3d-75 R-em3d75-4 / -5 — what Plot Temperature reads off a drawn field besides its colours: the hot spot, a face
// on its own, the temperature along a segment, and a wire coloured along its length.
//
// NOTHING HERE IS A SECOND READER. A thermal run's steps are FieldStep's (brief 74 writes Palace's layout), a value at a point
// is FieldSampler's (the element's own shape functions — not the drawn interpolation), and a wire is the SCENE's own
// triangles: its colour is a per-vertex scalar on them (FieldVertex's path), so nothing is tessellated a second time.
//
// A WIRE'S ARC LENGTH IS THE 3D ONE (overview §1d, owner): the resolved centreline (Em3dSweep.Path — the rings every solver and
// the viewer use) measured segment by segment in 3D, a ball's vertical neck included, never its plan length. Ring k of the
// sweep sits at path vertex k, so its arc length is the sum of the 3D segment lengths before it — exactly the s the 1D solve
// (brief 77) tabulates T against.

using System.Numerics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>The hottest point of what is drawn: its value, where it is (scene-local metres), and which surface holds it.</summary>
public readonly record struct FieldHotSpot(double Value, Vector3 At, int Surface)
{
    /// <summary>The maximum over every vertex of <paramref name="surfaces"/> of <paramref name="q"/>, or null when nothing is
    /// drawn. A vertex is where the solver's own value is (the drawn interpolation is linear between them), so the maximum of
    /// what is drawn is at one.</summary>
    public static FieldHotSpot? Of(FieldQuantity q, IReadOnlyList<FieldSurface> surfaces, IReadOnlyList<Vector3>? nudges = null)
    {
        FieldHotSpot? best = null;
        for (int si = 0; si < surfaces.Count; si++)
        {
            var s = surfaces[si];
            var n = nudges is not null && si < nudges.Count ? nudges[si] : Vector3.Zero;
            for (int v = 0; v < s.VertexCount; v++)
            {
                double e = q.Evaluate(s.Values.AsSpan(v * s.Channels, s.Channels));
                if (!double.IsFinite(e) || (best is { } b && e <= b.Value)) continue;
                best = new FieldHotSpot(e, new Vector3((float)s.Xyz[3 * v], (float)s.Xyz[3 * v + 1], (float)s.Xyz[3 * v + 2]) + n, si);
            }
        }
        return best;
    }
}

/// <summary>A drawn surface cut down to the triangles of one face.</summary>
public static class FieldFaces
{
    /// <summary>
    /// The triangles of <paramref name="surface"/> (a solid's region boundary) that lie on the face drawn by
    /// <paramref name="faceTriangles"/> (the scene's triangles of that face, scene-local metres): a triangle is kept when its
    /// centroid is within <paramref name="tol"/> of one of them and its normal runs the same way. The mesher's triangles and
    /// the display's are different tessellations of the same face, so they are matched by where they are, not by index; the
    /// normal test keeps a small triangle at an edge off the neighbouring face. The recipe is kept, so the result revalues.
    /// </summary>
    public static FieldSurface OnFace(FieldSurface surface, IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> faceTriangles, double tol)
    {
        var xyz = new List<double>();
        var val = new List<double>();
        var r = surface.Recipe;
        List<int>? ra = r is null ? null : [], rb = r is null ? null : [], rc = r is null ? null : [];
        List<double>? rt = r is null ? null : [];
        int ch = surface.Channels;
        for (int t = 0; t < surface.TriangleCount; t++)
        {
            var p = surface.Xyz.AsSpan(9 * t, 9);
            var a = new Vector3D(p[0], p[1], p[2]);
            var b = new Vector3D(p[3], p[4], p[5]);
            var c = new Vector3D(p[6], p[7], p[8]);
            var centroid = new Vector3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
            var normal = Normal(a, b, c);
            if (normal is not { } nt) continue;
            bool on = false;
            foreach (var (fa, fb, fc) in faceTriangles)
            {
                var A = new Vector3D(fa.X, fa.Y, fa.Z);
                var B = new Vector3D(fb.X, fb.Y, fb.Z);
                var C = new Vector3D(fc.X, fc.Y, fc.Z);
                if (Normal(A, B, C) is not { } nf || Math.Abs(nt.X * nf.X + nt.Y * nf.Y + nt.Z * nf.Z) < 0.9) continue;
                if (DistanceToTriangle(centroid, A, B, C) <= tol) { on = true; break; }
            }
            if (!on) continue;
            for (int k = 0; k < 9; k++) xyz.Add(p[k]);
            for (int k = 0; k < 3 * ch; k++) val.Add(surface.Values[3 * t * ch + k]);
            if (r is not null)
                for (int k = 0; k < 3; k++)
                {
                    int v = 3 * t + k;
                    ra!.Add(r.A[v]); rb!.Add(r.B[v]); rt!.Add(r.T[v]); rc!.Add(r.Cell[v]);
                }
        }
        var recipe = r is null ? null : new FieldRecipe
        {
            A = [.. ra!], B = [.. rb!], T = [.. rt!], Cell = [.. rc!], NodeCount = r.NodeCount, CellCount = r.CellCount,
        };
        return new FieldSurface { Channels = ch, Xyz = [.. xyz], Values = [.. val], Recipe = recipe };
    }

    /// <summary>The deepest a sampled face triangle is split (4^n pieces): a face far larger than the grid is still bounded.</summary>
    public const int MaxSampledDepth = 8;

    /// <summary>
    /// brief-em3d-82 R-em3d82-3 — a face painted by SAMPLING, for a field that carries no regions (openEMS's rectilinear dump,
    /// which <see cref="VtrReader.Mesh"/> turns into six tetrahedra per cell with attribute 0 throughout, so no region boundary
    /// or conductor group exists to cut down). Each of <paramref name="faceTriangles"/> (scene-local metres) is split into
    /// four until its longest edge is within the grid's local spacing, and every vertex is sampled
    /// (<see cref="SampleOffFace"/>) a nudge along <paramref name="direction"/> — the side of the face being shown. A vertex
    /// the field does not reach drops its triangle, never a guessed value. Positions stay ON the face; only the values are
    /// read off it. No recipe: the values are not the mesh's nodes.
    /// </summary>
    public static FieldSurface Sampled(FieldSampler sampler, FieldArray a, IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> faceTriangles,
                                       Vector3D direction, (double X, double Y, double Z) origin, CancellationToken ct = default)
    {
        int ch = a.Info.Channels;
        double toMetres = sampler.Mesh.ToMetres;
        var xyz = new List<double>();
        var val = new List<double>();
        Span<double> lo = stackalloc double[3], hi = stackalloc double[3];
        var memo = new Dictionary<Vector3D, double[]?>();
        double[]? At(Vector3D p)
        {
            if (memo.TryGetValue(p, out var have)) return have;
            var v = new double[ch];
            return memo[p] = SampleOffFace(sampler, a, p, direction, origin, v) ? v : null;
        }
        void Emit(Vector3D p0, Vector3D p1, Vector3D p2)
        {
            var v0 = At(p0); var v1 = At(p1); var v2 = At(p2);
            if (v0 is null || v1 is null || v2 is null) return;
            foreach (var (p, v) in new[] { (p0, v0), (p1, v1), (p2, v2) })
            {
                xyz.Add(p.X); xyz.Add(p.Y); xyz.Add(p.Z);
                val.AddRange(v);
            }
        }
        // The grid's spacing IN THE FACE's plane: the smallest extent of the cell there along an axis the face runs along
        // (a thin cell across the face — a substrate's — says nothing about how finely to split the face).
        double[] dir = [direction.X, direction.Y, direction.Z];
        double Spacing(Vector3D c, Span<double> l, Span<double> h)
        {
            double k = 1 / toMetres;
            if (!sampler.CellBounds((c.X + origin.X) * k, (c.Y + origin.Y) * k, (c.Z + origin.Z) * k, l, h)) return double.PositiveInfinity;
            double s = double.PositiveInfinity;
            for (int i = 0; i < 3; i++) if (Math.Abs(dir[i]) < 0.9 && h[i] > l[i]) s = Math.Min(s, h[i] - l[i]);
            return toMetres * s;
        }
        void Split(Vector3D p0, Vector3D p1, Vector3D p2, int depth, Span<double> l, Span<double> h)
        {
            var centroid = new Vector3D((p0.X + p1.X + p2.X) / 3, (p0.Y + p1.Y + p2.Y) / 3, (p0.Z + p1.Z + p2.Z) / 3);
            double longest = Math.Max(Dist(p0, p1), Math.Max(Dist(p1, p2), Dist(p2, p0)));
            if (depth >= MaxSampledDepth || longest <= Spacing(centroid, l, h)) { Emit(p0, p1, p2); return; }
            var m01 = Mid(p0, p1); var m12 = Mid(p1, p2); var m20 = Mid(p2, p0);
            Split(p0, m01, m20, depth + 1, l, h);
            Split(m01, p1, m12, depth + 1, l, h);
            Split(m20, m12, p2, depth + 1, l, h);
            Split(m01, m12, m20, depth + 1, l, h);
        }
        foreach (var (fa, fb, fc) in faceTriangles)
        {
            ct.ThrowIfCancellationRequested();
            Split(Vector3D.From(fa), Vector3D.From(fb), Vector3D.From(fc), 0, lo, hi);
        }
        return new FieldSurface { Channels = ch, Xyz = [.. xyz], Values = [.. val] };

        static double Dist(Vector3D p, Vector3D q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y) + (p.Z - q.Z) * (p.Z - q.Z));
        static Vector3D Mid(Vector3D p, Vector3D q) => new((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2);
    }

    /// <summary>
    /// brief-em3d-82 R-em3d82-3 — array <paramref name="a"/>'s channels just off a face at <paramref name="p"/> (scene-local
    /// metres), on the side <paramref name="direction"/> (a unit vector) points to; false when the field reaches neither there
    /// nor at <paramref name="p"/> itself (a face on the dump's own edge).
    /// <para>
    /// THE NUDGE LEAVES THE WHOLE CELL THE FACE IS IN, not one ULP. openEMS's dump is at the grid's CELL CENTRES (DumpMode 2 —
    /// CsxcadWriter chose it because a node ON a metal face averages E across the metal), and the grid puts a line on every
    /// metal face, so a face lies midway between two dump nodes: one in the air, one in the metal. A point a hair off the face
    /// is still inside that dump cell and interpolates half-way to the metal's value. So the point moves along
    /// <paramref name="direction"/> to where it leaves the dump cell containing the face — the plane of the first dump node on
    /// the chosen side, half a simulation cell off the face — and the field is read there.
    /// </para>
    /// </summary>
    public static bool SampleOffFace(FieldSampler sampler, FieldArray a, Vector3D p, Vector3D direction, (double X, double Y, double Z) origin,
                                     Span<double> channels)
    {
        double k = 1 / sampler.Mesh.ToMetres;
        double x = (p.X + origin.X) * k, y = (p.Y + origin.Y) * k, z = (p.Z + origin.Z) * k;
        Span<double> lo = stackalloc double[3], hi = stackalloc double[3];
        if (sampler.CellBounds(x, y, z, lo, hi))
        {
            double[] q = [x, y, z], d = [direction.X, direction.Y, direction.Z];
            double t = double.PositiveInfinity, size = 0;
            for (int i = 0; i < 3; i++)
            {
                size = Math.Max(size, hi[i] - lo[i]);
                if (Math.Abs(d[i]) > 1e-9) t = Math.Min(t, ((d[i] > 0 ? hi[i] : lo[i]) - q[i]) / d[i]);
            }
            if (double.IsFinite(t))
            {
                t = Math.Max(0, t) + 1e-6 * size;       // onto the node plane, and a hair past it into the next cell
                if (sampler.Sample(a, x + t * d[0], y + t * d[1], z + t * d[2], channels)) return true;
            }
        }
        return sampler.Sample(a, x, y, z, channels);
    }

    private static Vector3D? Normal(Vector3D a, Vector3D b, Vector3D c)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return l > 0 ? new Vector3D(nx / l, ny / l, nz / l) : null;
    }

    /// <summary>The distance from <paramref name="p"/> to triangle (a, b, c) — the closest point on it, edges included.</summary>
    public static double DistanceToTriangle(Vector3D p, Vector3D a, Vector3D b, Vector3D c)
    {
        // Ericson, Real-Time Collision Detection, §5.1.5: the closest point by Voronoi region.
        static Vector3D S(Vector3D x, Vector3D y) => new(x.X - y.X, x.Y - y.Y, x.Z - y.Z);
        static double D(Vector3D x, Vector3D y) => x.X * y.X + x.Y * y.Y + x.Z * y.Z;
        static Vector3D M(Vector3D x, double k) => new(x.X * k, x.Y * k, x.Z * k);
        static Vector3D A(Vector3D x, Vector3D y) => new(x.X + y.X, x.Y + y.Y, x.Z + y.Z);
        static double L(Vector3D x) => Math.Sqrt(D(x, x));
        var ab = S(b, a); var ac = S(c, a); var ap = S(p, a);
        double d1 = D(ab, ap), d2 = D(ac, ap);
        if (d1 <= 0 && d2 <= 0) return L(ap);
        var bp = S(p, b);
        double d3 = D(ab, bp), d4 = D(ac, bp);
        if (d3 >= 0 && d4 <= d3) return L(bp);
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return L(S(p, A(a, M(ab, d1 / (d1 - d3)))));
        var cp = S(p, c);
        double d5 = D(ab, cp), d6 = D(ac, cp);
        if (d6 >= 0 && d5 <= d6) return L(cp);
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return L(S(p, A(a, M(ac, d2 / (d2 - d6)))));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return L(S(p, A(b, M(S(c, b), (d4 - d3) / ((d4 - d3) + (d5 - d6))))));
        double den = 1 / (va + vb + vc);
        return L(S(p, A(a, A(M(ab, vb * den), M(ac, vc * den)))));
    }
}

/// <summary>R-em3d75-4d — a quantity along a straight segment: what a line probe and <i>Temperature Along…</i> plot.</summary>
public sealed record FieldLine(double[] Distance, double[] Values)
{
    /// <summary>The segment's length, metres.</summary>
    public double Length => Distance.Length > 0 ? Distance[^1] : 0;

    /// <summary>
    /// <paramref name="q"/> of <paramref name="a"/> at <paramref name="samples"/> evenly spaced points from
    /// <paramref name="from"/> to <paramref name="to"/> (world metres), EXACTLY at each — the element's own shape functions
    /// (<see cref="FieldSampler"/>), not the drawn interpolation. A point in no cell is NaN: the segment may leave the solids.
    /// </summary>
    public static FieldLine Along(FieldSampler sampler, FieldArray a, FieldQuantity q, double toMetres,
                                  (double X, double Y, double Z) from, (double X, double Y, double Z) to, int samples)
    {
        samples = Math.Max(2, samples);
        double dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var s = new double[samples];
        var v = new double[samples];
        Span<double> ch = stackalloc double[a.Info.Channels];
        double k = 1 / toMetres;
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / (samples - 1);
            s[i] = t * length;
            v[i] = sampler.Sample(a, (from.X + t * dx) * k, (from.Y + t * dy) * k, (from.Z + t * dz) * k, ch) ? q.Evaluate(ch) : double.NaN;
        }
        return new FieldLine(s, v);
    }
}

/// <summary>R-em3d75-5 — one wire's temperature along its length: T at each arc length, metres from its start (brief 77
/// writes these; this brief draws them).</summary>
public sealed record WireTemperature(string Wire, double[] S, double[] T)
{
    /// <summary>T at arc length <paramref name="s"/>, linear between the table's rows and held at its ends.</summary>
    public double At(double s)
    {
        if (S.Length == 0) return double.NaN;
        if (s <= S[0]) return T[0];
        if (s >= S[^1]) return T[^1];
        int hi = Array.BinarySearch(S, s);
        if (hi >= 0) return T[hi];
        hi = ~hi;
        int lo = hi - 1;
        double f = (s - S[lo]) / (S[hi] - S[lo]);
        return T[lo] + f * (T[hi] - T[lo]);
    }
}

/// <summary>A wire drawn in its temperature: the scene's own triangles with T(s) at each vertex, and each vertex's s.</summary>
public sealed record FieldWireSurface(string Wire, FieldSurface Surface, double[] S);

public static class FieldWires
{
    /// <summary>The arc length at each vertex of <paramref name="path"/>: the running sum of its 3D segment lengths.</summary>
    public static double[] ArcLengths(IReadOnlyList<Point3> path)
    {
        var s = new double[path.Count];
        for (int k = 1; k < path.Count; k++)
        {
            double dx = path[k].X - path[k - 1].X, dy = path[k].Y - path[k - 1].Y, dz = path[k].Z - path[k - 1].Z;
            s[k] = s[k - 1] + Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return s;
    }

    /// <summary>
    /// The triangles <paramref name="indices"/> into <paramref name="positions"/> (scene-local, as the scene holds a wire's
    /// solid) with T(s) at every vertex: a vertex of ring k of <paramref name="sweep"/> takes ring k's arc length, and an end
    /// cap's centre its ring's. <paramref name="origin"/> is the scene's origin, world metres. A vertex on no ring (a scene
    /// that is not this sweep's) is refused by returning null — nothing is guessed.
    /// brief-em3d-77: the table's first row is the path's first vertex, and its s there is not 0 when the wire starts with a foot
    /// (s is 0 at the start HEEL), so the path's own arc length is shifted by the table's first s.
    /// </summary>
    public static FieldWireSurface? Colour(string wire, Em3dSweep sweep, IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices,
                                           (double X, double Y, double Z) origin, WireTemperature table)
    {
        var s = ArcLengths(sweep.Path);
        if (table.S.Length > 0) for (int k = 0; k < s.Length; k++) s[k] += table.S[0];
        var ring = new Dictionary<Vector3, int>();
        Vector3 Local(Point3 p) => new((float)(p.X - origin.X), (float)(p.Y - origin.Y), (float)(p.Z - origin.Z));
        for (int r = 0; r < sweep.Rings.Count; r++)
            foreach (var p in sweep.Rings[r]) ring.TryAdd(Local(p), r);
        static Point3 Centroid(IReadOnlyList<Point3> ring)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in ring) { x += p.X; y += p.Y; z += p.Z; }
            return new Point3(x / ring.Count, y / ring.Count, z / ring.Count);
        }
        ring.TryAdd(Local(Centroid(sweep.Rings[0])), 0);
        ring.TryAdd(Local(Centroid(sweep.Rings[^1])), sweep.Rings.Count - 1);

        // an array element's vertices are the prototype's plus a float offset, and float(a) + float(d) is not float(a + d):
        // a vertex that misses every key exactly takes the nearest ring point within a thousandth of the wire's width
        var first = sweep.Rings[0];
        double width = 0;
        foreach (var a in first) foreach (var b in first) width = Math.Max(width, Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z)));
        float near2 = (float)(1e-3 * width * 1e-3 * width);
        bool Nearest(Vector3 p, out int r)
        {
            r = -1;
            float best = near2;
            foreach (var (k, v) in ring)
            {
                float d2 = Vector3.DistanceSquared(k, p);
                if (d2 <= best) { best = d2; r = v; }
            }
            return r >= 0;
        }

        var xyz = new double[3 * indices.Count];
        var values = new double[indices.Count];
        var arc = new double[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            var p = positions[indices[i]];
            if (!ring.TryGetValue(p, out int r) && !Nearest(p, out r)) return null;
            xyz[3 * i] = p.X; xyz[3 * i + 1] = p.Y; xyz[3 * i + 2] = p.Z;
            arc[i] = s[r];
            values[i] = table.At(s[r]);
        }
        return new FieldWireSurface(wire, new FieldSurface { Channels = 1, Xyz = xyz, Values = values }, arc);
    }

    /// <summary>The same triangles at another table (a new sweep point): the arc lengths are kept, only T is re-read.</summary>
    public static FieldWireSurface Revalue(FieldWireSurface w, WireTemperature table)
    {
        var values = new double[w.S.Length];
        for (int i = 0; i < values.Length; i++) values[i] = table.At(w.S[i]);
        return w with { Surface = new FieldSurface { Channels = 1, Xyz = w.Surface.Xyz, Values = values } };
    }
}
