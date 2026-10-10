// A selected object's SILHOUETTE — the outline a curved surface has from where the camera is, which no feature edge draws.
//
// An Object-mode selection is outlined by its feature edges (fs_edge): where two faces meet, or a sheet's rim. A box is all
// feature edges; a sphere has none, so a selected sphere was told from an unselected one only by its fading. Its outline is
// the set of triangle edges whose two triangles face opposite ways from the eye — view-dependent, so found per frame from an
// adjacency built once per object.
//
// ONLY EDGES INSIDE ONE FACE. An edge between two different faces is a feature edge and is drawn already; one between two
// triangles of the same face (vertex Face ids equal) is a facet of a smooth surface, and is drawn only while it is on the
// silhouette. Vertices are welded by position, so a seam (a sphere's, a cylinder's), where the tessellation duplicates its
// vertices, joins like any other edge.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>An object's silhouette edges, from the eye.</summary>
public static class Scene3DSilhouette
{
    /// <summary>An object with more triangles than this has no silhouette drawn: its adjacency is not built (a large imported
    /// part would cost a second and memory on every selection), and its feature edges outline it.</summary>
    public const int MaxTriangles = 200_000;

    /// <summary>A smooth edge, scene-local metres (an element's offset added), and its two triangles' normals (unnormalised).</summary>
    public readonly record struct Smooth(Vector3 A, Vector3 B, Vector3 N0, Vector3 N1);

    private static readonly ConditionalWeakTable<Scene3DModel, Dictionary<uint, Smooth[]>> Cache = new();

    /// <summary>
    /// Appends to <paramref name="into"/> the segments of object <paramref name="id"/>'s silhouette seen by
    /// <paramref name="camera"/>, under <paramref name="transform"/> (a row-vector rigid transform: a drag preview's copy;
    /// the identity otherwise).
    /// </summary>
    public static void Collect(Scene3DModel scene, uint id, in Camera3D camera, in Matrix4x4 transform, List<(Vector3 A, Vector3 B)> into)
    {
        var edges = EdgesOf(scene, id);
        if (edges.Length == 0) return;
        bool identity = transform.IsIdentity;
        bool ortho = camera.Projection == Projection3D.Orthographic;
        var eye = camera.Eye;
        var forward = camera.Forward;
        foreach (var e in edges)
        {
            var a = identity ? e.A : Vector3.Transform(e.A, transform);
            var n0 = identity ? e.N0 : Vector3.TransformNormal(e.N0, transform);
            var n1 = identity ? e.N1 : Vector3.TransformNormal(e.N1, transform);
            // Both triangles' planes pass through the edge, so one point of it serves either's facing.
            var view = ortho ? forward : a - eye;
            if (Vector3.Dot(n0, view) < 0 == Vector3.Dot(n1, view) < 0) continue;
            into.Add((a, identity ? e.B : Vector3.Transform(e.B, transform)));
        }
    }

    /// <summary>Object <paramref name="id"/>'s smooth edges: built on first use, then kept as long as the scene is.</summary>
    public static Smooth[] EdgesOf(Scene3DModel scene, uint id)
    {
        var byObject = Cache.GetOrCreateValue(scene);
        lock (byObject)
        {
            if (byObject.TryGetValue(id, out var found)) return found;
            var built = Build(scene, id);
            byObject[id] = built;
            return built;
        }
    }

    private static Smooth[] Build(Scene3DModel scene, uint id)
    {
        int triangles = 0;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var b in scene.Batches)
        {
            if (b.ObjectId != id) continue;
            triangles += b.IndexCount / 3;
            for (int i = b.FirstIndex; i < b.FirstIndex + b.IndexCount; i++)
            {
                var p = Position(scene, scene.Indices[i]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        if (triangles == 0 || triangles > MaxTriangles) return [];

        // Weld at a millionth of the object's size: a seam's duplicated vertices are equal or a rounding apart.
        float quantum = MathF.Max((max - min).Length() * 1e-6f, 1e-12f);
        var weld = new Dictionary<(long, long, long), int>();
        int Weld(Vector3 p)
        {
            var key = ((long)MathF.Round(p.X / quantum), (long)MathF.Round(p.Y / quantum), (long)MathF.Round(p.Z / quantum));
            if (!weld.TryGetValue(key, out int w)) weld[key] = w = weld.Count;
            return w;
        }

        // Each welded edge: its first triangle (normal, face, ends), then whether a second one of the same face was found.
        var open = new Dictionary<(int, int), (Vector3 N, uint Face, Vector3 A, Vector3 B, int Count, Vector3 N1)>();
        foreach (var b in scene.Batches)
        {
            if (b.ObjectId != id) continue;
            for (int i = b.FirstIndex; i + 2 < b.FirstIndex + b.IndexCount; i += 3)
            {
                uint i0 = scene.Indices[i], i1 = scene.Indices[i + 1], i2 = scene.Indices[i + 2];
                var p0 = Position(scene, i0) + b.Offset;
                var p1 = Position(scene, i1) + b.Offset;
                var p2 = Position(scene, i2) + b.Offset;
                var n = Vector3.Cross(p1 - p0, p2 - p0);
                if (n == Vector3.Zero) continue;
                uint face = scene.Vertices[i0].Face;
                int w0 = Weld(p0), w1 = Weld(p1), w2 = Weld(p2);
                Add(open, w0, w1, p0, p1, n, face);
                Add(open, w1, w2, p1, p2, n, face);
                Add(open, w2, w0, p2, p0, n, face);
            }
        }
        var smooth = new List<Smooth>();
        foreach (var e in open.Values)
            if (e.Count == 2) smooth.Add(new Smooth(e.A, e.B, e.N, e.N1));
        return [.. smooth];
    }

    private static void Add(Dictionary<(int, int), (Vector3 N, uint Face, Vector3 A, Vector3 B, int Count, Vector3 N1)> open,
                            int wa, int wb, Vector3 a, Vector3 b, Vector3 n, uint face)
    {
        if (wa == wb) return;
        var key = wa < wb ? (wa, wb) : (wb, wa);
        if (!open.TryGetValue(key, out var e)) { open[key] = (n, face, a, b, 1, default); return; }
        // A second triangle of the same face makes the edge smooth; a different face's, or a third triangle, makes it no
        // silhouette edge of ours (a feature edge, or a non-manifold one).
        open[key] = e.Count == 1 && e.Face == face ? e with { Count = 2, N1 = n } : e with { Count = 3 };
    }

    private static Vector3 Position(Scene3DModel scene, uint index)
    {
        var v = scene.Vertices[index];
        return new Vector3(v.X, v.Y, v.Z);
    }
}
