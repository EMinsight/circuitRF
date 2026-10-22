// brief-em3d-104 R-em3d104-1 — THE one place a shading normal is made: smooth across a curved face, sharp across an edge.
// The realistic view (106) reads it from the shade stream (Scene3DShadeVertex); the CPU mirror (110) and the glTF writer (111)
// call this function too and keep no second copy.
//
// THE RULE, per vertex, over the triangles that use it:
//   a. Triangles are already grouped by FACE: the builder gives every (mesh vertex, face) a scene vertex of its own, so two
//      faces never share one. Nothing here needs to know the face. The builder also passes the WELD — which vertices sit at
//      one point — and the rule below runs over a point's corners, so an edge between two faces is smooth where (b) would
//      join them: a fillet meets the cylinder it rounds tangentially, and kept sharp its edge drew as a seam in the realistic
//      view. Each face still keeps its own vertex, with the group's normal.
//   b. Within a face, two triangles that share an edge through the vertex join one smoothing group unless their planes turn
//      by more than Em3dSectionScene.SharpEdgeDegrees (30°) — the feature-edge rule, the same constant, not a second literal.
//      A group is what is reachable through such joins, so a 32-facet cylinder (11.25° a facet) and the sphere are smooth and a
//      prism's creased corner is not.
//   c. A welded mesh (the unfaced path: ports, tints) takes the same rule.
//   d. The normal is the weighted sum of the group's unit triangle normals, normalised, each weighted at its corner by
//      sin α / (|e₁| |e₂|) — Max (1999), "Weights for computing vertex normals from facet normals". The brief asked for AREA
//      weighting, which cannot meet its own gates: a cylinder's side vertex is the corner of one half of the facet behind it and
//      both halves of the facet ahead, so area weights the two facets 1 : 2 and the normal leans 1.9° off radial (gate 1 asks
//      1e-6). Angle weighting fixes the cylinder but leans 0.3° beside a sphere's poles (gate 4 asks 1e-3). Max's weights give
//      each cylinder facet the same total (1 / (w h) either way a quad is split) and are EXACT for a vertex whose neighbours lie
//      on a sphere, which is every vertex of the sphere.
//      A degenerate triangle (height below 1e-5 of its longest edge, where its plane is rounding noise) contributes nothing
//      and splits nothing; a group left with no contribution takes its first triangle's plane normal, or +z. Never NaN.
//   e. Where (b) or (c) splits a vertex, the vertex is DUPLICATED: the first group it is in keeps it, each later group gets a
//      copy appended after every original (points ascending, groups in first-use order), and only the index of a triangle in
//      that group changes. The caller copies the vertex itself, so position, id, colour and face are identical and the default
//      view (which takes its normal from screen-space derivatives) draws the same pixels.
//   f. The normal is in the positions' own frame (the scene-local frame, for the builder).
//
// Pure and deterministic: arrays in index order, no hash-order iteration, double arithmetic.

using System.Numerics;

namespace CircuitRF.Render.Scene3D;

/// <summary>brief-em3d-104 — the shading normals of one mesh: <see cref="Normals"/> for every vertex, the originals first and then
/// each duplicate; <see cref="Duplicates"/>[k] is the ORIGINAL vertex (0-based) that duplicate k copies; <see cref="Indices"/> are
/// the triangles again, pointing at the duplicates where a vertex was split.</summary>
public sealed record ShadingNormalsResult(Vector3[] Normals, int[] Duplicates, uint[] Indices);

public static class ShadingNormals
{
    /// <summary>The crease: a turn sharper than this, between two triangles of one face, keeps the edge sharp.</summary>
    public static double CreaseDegrees => Em3dSectionScene.SharpEdgeDegrees;

    private static readonly double CosCrease = Math.Cos(CreaseDegrees * Math.PI / 180);

    /// <summary>A triangle whose height is below this fraction of its longest edge has no plane worth shading by.</summary>
    private const double DegenerateRatio = 1e-5;

    private static long _built;

    /// <summary>How many meshes have had their shading normals computed in this process — what proves a hover computes none.</summary>
    public static long Built => Interlocked.Read(ref _built);

    /// <summary>
    /// The shading normals of <paramref name="positions"/> under <paramref name="indices"/> (triangles; each index is
    /// <paramref name="baseVertex"/> plus a position's index, and so is every index returned — a duplicate is numbered
    /// <paramref name="baseVertex"/> + <c>positions.Count</c> + k). <paramref name="split"/> false keeps every vertex whole (one
    /// group per vertex) — the switch that proves the default view is unchanged by duplication. <paramref name="weld"/>, when
    /// given, names each vertex's representative (the lowest-numbered vertex at its point, 0-based): the vertices of one point
    /// are then grouped together, so two faces that meet at a turn within the crease shade smooth across their edge while each
    /// keeps its own vertices.
    /// </summary>
    public static ShadingNormalsResult Compute(IReadOnlyList<Vector3> positions, ReadOnlySpan<uint> indices, uint baseVertex = 0,
                                               bool split = true, IReadOnlyList<int>? weld = null)
    {
        Interlocked.Increment(ref _built);
        int n = positions.Count, tris = indices.Length / 3;
        var local = new int[tris * 3];
        for (int k = 0; k < local.Length; k++) local[k] = (int)(indices[k] - baseVertex);
        var rep = new int[n];
        for (int v = 0; v < n; v++) rep[v] = weld is null ? v : weld[v];

        // Each triangle's unit normal (zero when degenerate) and its plane's normal however small (the fallback).
        var unit = new Vector3D[tris];
        var plane = new Vector3D[tris];
        var degenerate = new bool[tris];
        for (int t = 0; t < tris; t++)
        {
            var a = P(positions[local[3 * t]]); var b = P(positions[local[3 * t + 1]]); var c = P(positions[local[3 * t + 2]]);
            var cr = Vector3D.Cross(b - a, c - a);
            double len = cr.Length;
            double longest = Math.Max((b - a).LengthSquared, Math.Max((c - b).LengthSquared, (a - c).LengthSquared));
            plane[t] = len > 0 && double.IsFinite(len) ? cr / len : default;
            degenerate[t] = !(len > DegenerateRatio * longest) || !double.IsFinite(len);
            unit[t] = degenerate[t] ? default : plane[t];
        }

        // Every point's corners (a point is a vertex, or the vertices welded to one), in index order (CSR): corner c is
        // triangle c / 3, slot c % 3.
        var start = new int[n + 1];
        foreach (int v in local) start[rep[v] + 1]++;
        for (int v = 0; v < n; v++) start[v + 1] += start[v];
        var corners = new int[local.Length];
        var fill = (int[])start.Clone();
        for (int c = 0; c < local.Length; c++) corners[fill[rep[local[c]]]++] = c;

        var normals = new Vector3[n];
        Array.Fill(normals, Vector3.UnitZ);
        var dups = new List<int>();
        var outIdx = new uint[local.Length];
        var dupNormals = new List<Vector3>();
        int[] parent = [], groupOf = [];
        var byOther = new Dictionary<int, int>();       // lookups only: never iterated, so its order cannot leak out
        var target = new Dictionary<(int V, int G), uint>();
        var kept = new HashSet<int>();
        var roots = new List<int>();
        for (int r = 0; r < n; r++)
        {
            int first = start[r], count = start[r + 1] - first;
            if (count == 0) continue;
            if (parent.Length < count) { parent = new int[Math.Max(count, 2 * parent.Length)]; groupOf = new int[parent.Length]; }
            for (int i = 0; i < count; i++) parent[i] = i;

            if (!split) for (int i = 1; i < count; i++) Union(parent, 0, i);
            else
            {
                // b. Join triangles sharing an edge through the point (keyed by the edge's other point) that turn by no more than
                // the crease. A degenerate corner then joins its first non-degenerate edge neighbour (so it can never bridge a
                // crease), else the point's first non-degenerate corner, else the first corner.
                byOther.Clear();
                int firstSolid = -1;
                for (int i = 0; i < count; i++)
                {
                    int ti = corners[first + i] / 3;
                    if (degenerate[ti]) continue;
                    if (firstSolid < 0) firstSolid = i;
                    for (int s = 0; s < 3; s++)
                    {
                        int x = rep[local[3 * ti + s]];
                        if (x == r) continue;
                        if (!byOther.TryGetValue(x, out int j)) byOther[x] = i;
                        else if (Vector3D.Dot(unit[ti], unit[corners[first + j] / 3]) >= CosCrease) Union(parent, j, i);
                    }
                }
                for (int i = 0; i < count; i++)
                {
                    int ti = corners[first + i] / 3;
                    if (!degenerate[ti]) continue;
                    int joined = firstSolid >= 0 ? firstSolid : 0;
                    for (int s = 0; s < 3; s++)
                        if (rep[local[3 * ti + s]] is int x && x != r && byOther.TryGetValue(x, out int j)) { joined = j; break; }
                    Union(parent, i, joined);
                }
            }

            // Groups in first-use order; a vertex keeps its own index in the first group it is in, and is copied for each later.
            roots.Clear();
            for (int i = 0; i < count; i++)
            {
                int root = Find(parent, i), g = roots.IndexOf(root);
                if (g < 0) { g = roots.Count; roots.Add(root); }
                groupOf[i] = g;
            }
            target.Clear();
            kept.Clear();
            for (int g = 0; g < roots.Count; g++)
            {
                var sum = default(Vector3D);
                int firstTri = -1;
                for (int i = 0; i < count; i++)
                {
                    if (groupOf[i] != g) continue;
                    int c = corners[first + i], t = c / 3;
                    if (firstTri < 0) firstTri = t;
                    if (degenerate[t]) continue;
                    sum += unit[t] * CornerWeight(positions, local, c);
                }
                double len = sum.Length;
                var nrm = len > 0 && double.IsFinite(len) ? sum / len : plane[firstTri].LengthSquared > 0 ? plane[firstTri] : new Vector3D(0, 0, 1);
                var f = new Vector3((float)nrm.X, (float)nrm.Y, (float)nrm.Z);
                for (int i = 0; i < count; i++)
                {
                    if (groupOf[i] != g) continue;
                    int c = corners[first + i], v = local[c];
                    if (!target.TryGetValue((v, g), out uint to))
                    {
                        if (kept.Add(v)) { normals[v] = f; to = (uint)v; }
                        else { to = (uint)(n + dups.Count); dups.Add(v); dupNormals.Add(f); }
                        target[(v, g)] = to;
                    }
                    outIdx[c] = baseVertex + to;
                }
            }
        }
        return new ShadingNormalsResult([.. normals, .. dupNormals], [.. dups], outIdx);
    }

    /// <summary>Corner <paramref name="c"/>'s weight: sin α / (|e₁| |e₂|), the angle α between its two edges e₁, e₂ — Max (1999),
    /// <c>|e₁ × e₂| / (|e₁|² |e₂|²)</c>.</summary>
    private static double CornerWeight(IReadOnlyList<Vector3> positions, int[] local, int c)
    {
        int t = c / 3, s = c % 3;
        var p = P(positions[local[3 * t + s]]);
        var e1 = P(positions[local[3 * t + (s + 1) % 3]]) - p;
        var e2 = P(positions[local[3 * t + (s + 2) % 3]]) - p;
        return Vector3D.Cross(e1, e2).Length / (e1.LengthSquared * e2.LengthSquared);
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i) i = parent[i] = parent[parent[i]];
        return i;
    }

    /// <summary>Unions toward the lower index, so a group's root is its first corner whatever order the joins came in.</summary>
    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra == rb) return;
        if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
    }

    private static Vector3D P(Vector3 v) => new(v.X, v.Y, v.Z);

    private readonly record struct Vector3D(double X, double Y, double Z)
    {
        public double LengthSquared => X * X + Y * Y + Z * Z;
        public double Length => Math.Sqrt(LengthSquared);
        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vector3D operator /(Vector3D a, double s) => new(a.X / s, a.Y / s, a.Z / s);
        public static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vector3D Cross(Vector3D a, Vector3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
