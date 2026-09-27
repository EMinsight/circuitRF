// brief-em3d-44 R-em3d44-3 — an object's snap features, built once, beside its tessellation.
//
// WHAT A FEATURE IS. A corner, an edge, an edge's midpoint and a face's centre — of the SOLID, not of its
// triangles. An edge is a feature where two different faces meet (a box has twelve, not the eighteen its
// triangles have) or where a face has no neighbour (a sheet's rim); a corner is an end of a feature edge; a
// face's centre is the area-weighted centroid of its triangles, and only a PLANAR face has one — a
// cylinder's side would put it inside the solid, off every surface a user can see. A sweep or a sphere
// names no faces (Scene3DBuilder.FaceUnknown) and so has no features at all.
//
// WHY BY FACE. The pick patch (Scene3DIdPatch) names the (object, face) pairs near the cursor, so the
// query needs "this face's corners, edges and centre" and nothing else: each is a slice of a flat array,
// found by face index in O(1) (compressed rows: FaceVertexStart[f] .. FaceVertexStart[f + 1]).
//
// DOUBLE PRECISION, WORLD METRES. The scene's float vertices are scene-local and resolve ~6 nm on a 0.1 m
// model; a snapped corner must come back as the DBU it was drawn at EXACTLY (R-em3d44-4), so the table is
// built from the tessellation's own doubles, which are the elaboration's metres unchanged.
//
// SHARED BY INSTANCES (R-em3d44-3b). Every element of an array, and every placement of one child under the
// same rotation, is the same mesh moved by a translation. The builder keeps ONE table per (child, object,
// rotation) and gives each element only its offset; a query adds the offset to the few features it
// examines. A table is also kept per MESH between builds, so an edit rebuilds the edited object's only.

using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>One object's snap features, in world metres (of the object it was built for).</summary>
public sealed class Scene3DFeatureTable
{
    private static readonly ConditionalWeakTable<Em3dTriangleMesh, Scene3DFeatureTable> Kept = new();
    private static long _builds;

    /// <summary>Tables built in this process — one per mesh ever asked about (gate 2 reads it).</summary>
    public static long Builds => Interlocked.Read(ref _builds);

    /// <summary>The corners: every end of a feature edge, once.</summary>
    public required Point3[] Vertices { get; init; }
    /// <summary>The feature edges, as corner pairs.</summary>
    public required int[] EdgeA { get; init; }
    public required int[] EdgeB { get; init; }
    /// <summary>Each edge's two faces; <see cref="EdgeFace1"/> is −1 on a rim.</summary>
    public required int[] EdgeFace0 { get; init; }
    public required int[] EdgeFace1 { get; init; }
    /// <summary>Face slots: face numbers 0 .. FaceCount − 1.</summary>
    public required int FaceCount { get; init; }
    /// <summary>Face f's corners are FaceVertices[FaceVertexStart[f] .. FaceVertexStart[f + 1]].</summary>
    public required int[] FaceVertexStart { get; init; }
    public required int[] FaceVertices { get; init; }
    /// <summary>Face f's edges are FaceEdges[FaceEdgeStart[f] .. FaceEdgeStart[f + 1]].</summary>
    public required int[] FaceEdgeStart { get; init; }
    public required int[] FaceEdges { get; init; }
    /// <summary>Corner v's faces are VertexFaces[VertexFaceStart[v] .. VertexFaceStart[v + 1]].</summary>
    public required int[] VertexFaceStart { get; init; }
    public required int[] VertexFaces { get; init; }
    /// <summary>Each face's centre; meaningful only where <see cref="HasCentre"/>.</summary>
    public required Point3[] FaceCentres { get; init; }
    public required bool[] HasCentre { get; init; }

    /// <summary>brief-em3d-67 R-em3d67-2 — the object's NAMED edges: runs of these segments (a managed object), or the
    /// kernel's edge table (a kernel object, whose snapping then reads it). Empty when the builder gave no names.</summary>
    /// <remarks>A managed object's runs are made on FIRST READ, not with the table (designer feedback 02). Naming every run of
    /// every object in the build took most of a cold build on an imported board — a copper pour with ~18,000 hole vertices
    /// is ~18,000 side faces to pair and walk — and delayed the first picture by seconds for something only Edge mode, a
    /// fillet and a kernel snap ever read, one hovered object at a time. The kernel's table is cheap and stays eager.</remarks>
    public Scene3DEdges Named => _named?.Value ?? Scene3DEdges.Empty;

    private Lazy<Scene3DEdges>? _named;

    /// <summary>Whether <see cref="Named"/> is the kernel's edge table — known without making a managed object's runs.</summary>
    public bool NamedFromKernel { get; private set; }

    /// <summary>Whether <see cref="Named"/> has been made yet — what shows a build no longer makes it.</summary>
    public bool NamedMade => _named?.IsValueCreated ?? false;

    /// <summary>Every feature it holds: corners, edges, midpoints and centres.</summary>
    public int FeatureCount => Vertices.Length + 2 * EdgeA.Length + HasCentre.Count(h => h);

    /// <summary>Whether corner <paramref name="v"/> lies on face <paramref name="face"/>.</summary>
    public bool VertexOnFace(int v, int face)
    {
        for (int k = VertexFaceStart[v]; k < VertexFaceStart[v + 1]; k++)
            if (VertexFaces[k] == face) return true;
        return false;
    }

    /// <summary>The table for <paramref name="mesh"/>, built on first asking and kept while the mesh lives.
    /// <paramref name="sheet"/>: the whole mesh is face 0 (Scene3DBuilder's rule for a sheet).</summary>
    public static Scene3DFeatureTable Of(Em3dTriangleMesh mesh, bool sheet) => Of(mesh, sheet, null);

    /// <summary>brief-em3d-67 — the table, with its named edges made from <paramref name="edges"/> when it is first built
    /// (a mesh is one object's, so the names that come with its first asking are its names).</summary>
    public static Scene3DFeatureTable Of(Em3dTriangleMesh mesh, bool sheet, Scene3DEdgeSource? edges)
        => Kept.GetValue(mesh, m =>
        {
            Interlocked.Increment(ref _builds);
            var t = Build(m, sheet);
            if (edges is not null && !ReferenceEquals(t, Empty))
            {
                if (edges.Kernel is { } k)
                {
                    var named = Scene3DEdges.OfKernel(k, edges.FaceNames, t.FaceCount);
                    t._named = new Lazy<Scene3DEdges>(named);
                    t.NamedFromKernel = true;
                }
                else
                    t._named = new Lazy<Scene3DEdges>(() => Scene3DEdges.OfSegments(t, edges.FaceNames, edges.ToOwn),
                                                      LazyThreadSafetyMode.ExecutionAndPublication);
            }
            return t;
        });

    private static Scene3DFeatureTable Build(Em3dTriangleMesh mesh, bool sheet)
    {
        if (!sheet && !mesh.Triangles.Any(t => t.Face >= 0)) return Empty;
        // Weld by position: a tessellation may repeat a corner once per face.
        var weld = new Dictionary<Point3, int>();
        var welded = new int[mesh.Vertices.Count];
        var points = new List<Point3>();
        for (int i = 0; i < welded.Length; i++)
        {
            var p = mesh.Vertices[i];
            if (!weld.TryGetValue(p, out int w)) { w = points.Count; points.Add(p); weld[p] = w; }
            welded[i] = w;
        }

        int faceCount = 0;
        var edges = new Dictionary<(int, int), (int F0, int F1, int Count)>();
        var centroid = new List<(Vector3d Sum, double Area, Vector3d Normal, double NormalLength)>();
        foreach (var t in mesh.Triangles)
        {
            int f = sheet ? 0 : t.Face;
            if (f < 0) continue;                                    // a sweep or a sphere: no named faces
            faceCount = Math.Max(faceCount, f + 1);
            while (centroid.Count < faceCount) centroid.Add(default);
            int a = welded[t.A], b = welded[t.B], c = welded[t.C];
            Edge(a, b, f); Edge(b, c, f); Edge(c, a, f);
            var pa = V(points[a]); var pb = V(points[b]); var pc = V(points[c]);
            var n = Vector3d.Cross(pb - pa, pc - pa);
            double area = 0.5 * n.Length();
            var e = centroid[f];
            centroid[f] = (e.Sum + (pa + pb + pc) * (area / 3), e.Area + area, e.Normal + n, e.NormalLength + n.Length());
        }

        void Edge(int a, int c, int f)
        {
            var key = a < c ? (a, c) : (c, a);
            edges[key] = edges.TryGetValue(key, out var e)
                ? (e.F0, e.Count == 1 || e.F1 == e.F0 ? f : e.F1, e.Count + 1)
                : (f, -1, 1);
        }

        // Feature edges: a rim (one triangle), or where two different faces meet.
        var used = new Dictionary<int, int>();
        var corners = new List<Point3>();
        var ea = new List<int>(); var eb = new List<int>(); var ef0 = new List<int>(); var ef1 = new List<int>();
        int Corner(int w)
        {
            if (!used.TryGetValue(w, out int k)) { k = corners.Count; corners.Add(points[w]); used[w] = k; }
            return k;
        }
        foreach (var ((a, c), (f0, f1, count)) in edges)
        {
            bool rim = count == 1;
            if (!rim && f1 == f0) continue;
            ea.Add(Corner(a)); eb.Add(Corner(c));
            ef0.Add(f0); ef1.Add(rim ? -1 : f1);
        }

        // Rows per face: its edges, and the corners those edges end at.
        var faceEdges = new List<int>[faceCount];
        var faceVerts = new List<int>[faceCount];
        for (int f = 0; f < faceCount; f++) { faceEdges[f] = []; faceVerts[f] = []; }
        for (int e = 0; e < ea.Count; e++)
            foreach (int f in (ReadOnlySpan<int>)[ef0[e], ef1[e]])
            {
                if (f < 0) continue;
                faceEdges[f].Add(e);
                if (!faceVerts[f].Contains(ea[e])) faceVerts[f].Add(ea[e]);
                if (!faceVerts[f].Contains(eb[e])) faceVerts[f].Add(eb[e]);
            }
        var vertexFaces = new List<int>[corners.Count];
        for (int v = 0; v < corners.Count; v++) vertexFaces[v] = [];
        for (int f = 0; f < faceCount; f++)
            foreach (int v in faceVerts[f]) vertexFaces[v].Add(f);

        var centres = new Point3[faceCount];
        var has = new bool[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            var (sum, area, normal, nl) = centroid[f];
            // Planar when the triangles' normals all agree: their sum is as long as their lengths' sum.
            if (area <= 0 || normal.Length() < (1 - 1e-6) * nl) continue;
            var c = sum * (1 / area);
            centres[f] = new Point3(c.X, c.Y, c.Z);
            has[f] = true;
        }

        var (fvs, fv) = Rows(faceVerts);
        var (fes, fe) = Rows(faceEdges);
        var (vfs, vf) = Rows(vertexFaces);
        return new Scene3DFeatureTable
        {
            Vertices = [.. corners], EdgeA = [.. ea], EdgeB = [.. eb], EdgeFace0 = [.. ef0], EdgeFace1 = [.. ef1],
            FaceCount = faceCount, FaceVertexStart = fvs, FaceVertices = fv, FaceEdgeStart = fes, FaceEdges = fe,
            VertexFaceStart = vfs, VertexFaces = vf, FaceCentres = centres, HasCentre = has,
        };
    }

    /// <summary>No features: a sweep's or a sphere's (their faces have no names).</summary>
    private static readonly Scene3DFeatureTable Empty = new()
    {
        Vertices = [], EdgeA = [], EdgeB = [], EdgeFace0 = [], EdgeFace1 = [], FaceCount = 0,
        FaceVertexStart = [0], FaceVertices = [], FaceEdgeStart = [0], FaceEdges = [],
        VertexFaceStart = [0], VertexFaces = [], FaceCentres = [], HasCentre = [],
    };

    private static (int[] Start, int[] Items) Rows(List<int>[] rows)
    {
        var start = new int[rows.Length + 1];
        for (int i = 0; i < rows.Length; i++) start[i + 1] = start[i] + rows[i].Count;
        var items = new int[start[^1]];
        for (int i = 0; i < rows.Length; i++) rows[i].CopyTo(items, start[i]);
        return (start, items);
    }

    private static Vector3d V(Point3 p) => new(p.X, p.Y, p.Z);

    /// <summary>A double-precision 3-vector — just what the build needs.</summary>
    private readonly record struct Vector3d(double X, double Y, double Z)
    {
        public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3d operator *(Vector3d a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
    }
}

/// <summary>
/// An object's features: a table, and the translation from where the table was built to where this object
/// is (zero unless the table is an instance's shared one). Default: the object has no features.
/// </summary>
public readonly record struct Scene3DFeatureRef(Scene3DFeatureTable? Table, double Dx, double Dy, double Dz, bool Shared)
{
    /// <summary>Corner <paramref name="v"/> of the table, where this object has it.</summary>
    public Point3 Vertex(int v)
    {
        var p = Table!.Vertices[v];
        return new Point3(p.X + Dx, p.Y + Dy, p.Z + Dz);
    }

    /// <summary>Face <paramref name="f"/>'s centre, where this object has it.</summary>
    public Point3 Centre(int f)
    {
        var p = Table!.FaceCentres[f];
        return new Point3(p.X + Dx, p.Y + Dy, p.Z + Dz);
    }
}

/// <summary>brief-em3d-67 — what an object's edges are named from: its face names (by the mesh's face numbering), the
/// kernel's edge table when it is a kernel solid, and the map from world metres into its own frame — where the runs of
/// one face pair are numbered — or null for the identity.</summary>
public sealed record Scene3DEdgeSource(IReadOnlyList<string> FaceNames, IReadOnlyList<Em3dShapeEdge>? Kernel = null,
                                       Func<Point3, Point3>? ToOwn = null);

/// <summary>
/// brief-em3d-44 R-em3d44-3b — what lets instances share a table: objects with equal <paramref name="Key"/>
/// are the same mesh moved by a translation, and (<paramref name="Tx"/>, <paramref name="Ty"/>,
/// <paramref name="Tz"/>) is this one's translation, world metres. The key must hold the rotation.
/// </summary>
public sealed record Scene3DFeatureShare(object Key, double Tx, double Ty, double Tz);
