// brief-em3d-43 R-em3d43-4a — every hit along a ray, by depth: the list B steps through.
//
// The GPU's ID pass answers "what is in FRONT under the cursor" and nothing else — a depth test keeps one
// fragment. B asks for what is BEHIND it, which only a CPU ray can answer, so on B (never on hover) a ray
// through the cursor collects every hit in the current mode, ignoring occlusion:
//   * Face   — every face crossed. A face crossed twice (entering and leaving a solid, or a cylinder's
//              one side face) appears twice: both are faces a user may want.
//   * Object — every object crossed, ONCE, at its nearest hit.
//   * Vertex — every vertex within the snap radius of the ray (measured on screen), by depth.
// What is hidden, not pickable (the air, the box's faces) or cut away by the clip plane is not a hit.
//
// A BOUNDING-VOLUME HIERARCHY over the objects' boxes, built once per scene (a scene is immutable, so its
// generation is its identity) and counted, finds the objects a ray can touch; only their triangles are
// tested. Brief 28's Scene3DPicking.Pick is the nearest-only answer and stays: it is what gate 9 compares
// the GPU against, and Nearest here must agree with it (gate 1).

using System.Numerics;
using System.Runtime.CompilerServices;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>A ray through a pixel, with what Vertex mode needs to measure on screen.</summary>
public readonly record struct Scene3DRayQuery(Camera3D Camera, float Px, float Py, float Width, float Height,
                                              float RadiusPixels = Scene3DSnap.RadiusPixels);

public static class RayHits
{
    private static readonly ConditionalWeakTable<Scene3DModel, Bvh> Trees = new();
    private static long _bvhBuilds;

    /// <summary>How many hierarchies have been built in this process — one per scene asked about.</summary>
    public static long BvhBuilds => Interlocked.Read(ref _bvhBuilds);

    /// <summary>Every hit of the ray through the query's pixel, nearest first (R-em3d43-4a).</summary>
    public static List<Scene3DHit> Collect(Scene3DModel scene, in Scene3DRayQuery q, Scene3DSelectMode mode,
                                           ReadOnlySpan<bool> visible, in ClipPlane3D clip = default)
    {
        var (o, d) = q.Camera.Ray(q.Px, q.Py, q.Width, q.Height);
        var hits = new List<Scene3DHit>();
        var tree = Trees.GetValue(scene, s => { Interlocked.Increment(ref _bvhBuilds); return new Bvh(s); });
        var candidates = new List<int>();
        // Vertex mode widens each box by the snap radius at the box's far depth, so a vertex just off the
        // silhouette of an object the ray misses is still found.
        tree.Query(o, d, q.Camera, q.Height, mode == Scene3DSelectMode.Vertex ? q.RadiusPixels : 0, candidates);

        foreach (int k in candidates)
        {
            var b = scene.Batches[k];
            uint id = b.ObjectId;
            if (!Visible(visible, id) || !scene.Objects[id - 1].Pickable) continue;
            switch (mode)
            {
                case Scene3DSelectMode.Vertex:
                    CollectVertices(scene, b, q, o, d, clip, hits);
                    break;
                default:
                    CollectTriangles(scene, b, o, d, clip, mode, hits);
                    break;
            }
        }
        hits.Sort((a, c) => a.Depth != c.Depth ? a.Depth.CompareTo(c.Depth) : a.Item.Object.CompareTo(c.Item.Object));
        return mode == Scene3DSelectMode.Vertex ? DedupVertices(hits) : hits;
    }

    /// <summary>The nearest hit, or null — what a click in the pane selects.</summary>
    public static Scene3DHit? Nearest(Scene3DModel scene, in Scene3DRayQuery q, Scene3DSelectMode mode,
                                      ReadOnlySpan<bool> visible, in ClipPlane3D clip = default)
    {
        var all = Collect(scene, q, mode, visible, clip);
        return all.Count > 0 ? all[0] : null;
    }

    private static void CollectTriangles(Scene3DModel scene, Scene3DBatch b, Vector3 o, Vector3 d, in ClipPlane3D clip,
                                         Scene3DSelectMode mode, List<Scene3DHit> hits)
    {
        var verts = scene.Vertices;
        // A ray through the shared edge of a face's two triangles hits both, at one depth: that is one
        // crossing of one face, so a repeat of the same face at (nearly) the same depth is dropped.
        int from = hits.Count;
        float nearest = float.MaxValue;
        Scene3DHit? nearestHit = null;
        for (int i = b.FirstIndex; i < b.FirstIndex + b.IndexCount; i += 3)
        {
            var a = verts[scene.Indices[i]];
            var v0 = new Vector3(a.X, a.Y, a.Z);
            var v1 = P(verts[scene.Indices[i + 1]]);
            var v2 = P(verts[scene.Indices[i + 2]]);
            if (!Scene3DPicking.Intersect(o, d, v0, v1, v2, out float t)) continue;
            var point = o + d * t;
            if (!clip.Keeps(point)) continue;
            var item = mode == Scene3DSelectMode.Face ? Scene3DItem.OfFace(b.ObjectId, (int)a.Face) : Scene3DItem.OfObject(b.ObjectId);
            if (mode == Scene3DSelectMode.Object)
            {
                if (t < nearest) { nearest = t; nearestHit = new Scene3DHit(item, t, point); }
                continue;
            }
            bool repeat = false;
            for (int k = from; k < hits.Count; k++)
                if (hits[k].Item == item && MathF.Abs(hits[k].Depth - t) <= 1e-5f * MathF.Max(1e-9f, MathF.Abs(t))) { repeat = true; break; }
            if (!repeat) hits.Add(new Scene3DHit(item, t, point));
        }
        if (nearestHit is { } n) hits.Add(n);
    }

    private static void CollectVertices(Scene3DModel scene, Scene3DBatch b, in Scene3DRayQuery q, Vector3 o, Vector3 d,
                                        in ClipPlane3D clip, List<Scene3DHit> hits)
    {
        float r2 = q.RadiusPixels * q.RadiusPixels;
        var verts = scene.Vertices;
        for (int i = b.FirstIndex; i < b.FirstIndex + b.IndexCount; i++)
        {
            var p = P(verts[scene.Indices[i]]);
            if (!clip.Keeps(p)) continue;
            var (x, y, inFront) = q.Camera.Project(p, q.Width, q.Height);
            if (!inFront || (x - q.Px) * (x - q.Px) + (y - q.Py) * (y - q.Py) > r2) continue;
            hits.Add(new Scene3DHit(Scene3DItem.OfVertex(b.ObjectId, p), Vector3.Dot(p - o, d), p));
        }
    }

    /// <summary>One entry per (object, point): a corner is several scene vertices (one per face it is on).</summary>
    private static List<Scene3DHit> DedupVertices(List<Scene3DHit> sorted)
    {
        var seen = new HashSet<Scene3DItem>();
        var list = new List<Scene3DHit>(sorted.Count);
        foreach (var h in sorted) if (seen.Add(h.Item)) list.Add(h);
        return list;
    }

    private static bool Visible(ReadOnlySpan<bool> visible, uint id)
        => visible.IsEmpty || (id - 1 < (uint)visible.Length && visible[(int)id - 1]);

    private static Vector3 P(in Scene3DVertex v) => new(v.X, v.Y, v.Z);

    /// <summary>A bounding-volume hierarchy over the scene's batches (one object each), split at the median
    /// of the longest axis. Leaves hold up to <see cref="LeafSize"/> batches.</summary>
    private sealed class Bvh
    {
        private const int LeafSize = 4;
        private readonly List<Node> _nodes = [];
        private readonly int[] _items;
        private readonly Vector3[] _min, _max;

        private readonly record struct Node(Vector3 Min, Vector3 Max, int Left, int Right, int First, int Count);

        public Bvh(Scene3DModel scene)
        {
            int n = scene.Batches.Length;
            _items = new int[n];
            _min = new Vector3[n];
            _max = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                _items[k] = k;
                var o = scene.Objects[scene.Batches[k].ObjectId - 1];
                _min[k] = o.Min; _max[k] = o.Max;
            }
            if (n > 0) Build(0, n);
        }

        private int Build(int first, int count)
        {
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            for (int k = first; k < first + count; k++) { lo = Vector3.Min(lo, _min[_items[k]]); hi = Vector3.Max(hi, _max[_items[k]]); }
            int at = _nodes.Count;
            _nodes.Add(new Node(lo, hi, -1, -1, first, count));
            if (count <= LeafSize) return at;
            var ext = hi - lo;
            int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
            float C(int item) => axis switch { 0 => _min[item].X + _max[item].X, 1 => _min[item].Y + _max[item].Y, _ => _min[item].Z + _max[item].Z };
            Array.Sort(_items, first, count, Comparer<int>.Create((a, b) => C(a).CompareTo(C(b))));
            int half = count / 2;
            int left = Build(first, half);
            int right = Build(first + half, count - half);
            _nodes[at] = new Node(lo, hi, left, right, first, count);
            return at;
        }

        public void Query(Vector3 o, Vector3 d, in Camera3D camera, float height, float radiusPx, List<int> into)
        {
            if (_nodes.Count == 0) return;
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var node = _nodes[stack.Pop()];
                var pad = new Vector3(radiusPx > 0 ? radiusPx * PerPixelAt(camera, height, node.Min, node.Max) : 0);
                if (!Slab(o, d, node.Min - pad, node.Max + pad)) continue;
                if (node.Left < 0)
                {
                    for (int k = node.First; k < node.First + node.Count; k++)
                    {
                        int item = _items[k];
                        var extra = new Vector3(radiusPx > 0 ? radiusPx * PerPixelAt(camera, height, _min[item], _max[item]) : 0);
                        if (Slab(o, d, _min[item] - extra, _max[item] + extra)) into.Add(item);
                    }
                    continue;
                }
                stack.Push(node.Left);
                stack.Push(node.Right);
            }
            into.Sort();
        }

        /// <summary>A pixel's world size at the far corner of a box, in perspective.</summary>
        private static float PerPixelAt(in Camera3D c, float height, Vector3 min, Vector3 max)
        {
            if (c.Projection == Projection3D.Orthographic) return c.WorldPerPixel(height);
            float far = MathF.Max(Vector3.Distance(c.Eye, min), Vector3.Distance(c.Eye, max));
            return 2f * far * MathF.Tan(c.FovY * 0.5f) / MathF.Max(1f, height);
        }

        private static bool Slab(Vector3 o, Vector3 d, Vector3 lo, Vector3 hi)
        {
            float t0 = float.MinValue, t1 = float.MaxValue;
            for (int a = 0; a < 3; a++)
            {
                float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
                float la = a == 0 ? lo.X : a == 1 ? lo.Y : lo.Z, ha = a == 0 ? hi.X : a == 1 ? hi.Y : hi.Z;
                if (MathF.Abs(da) < 1e-30f)
                {
                    if (oa < la || oa > ha) return false;
                    continue;
                }
                float ta = (la - oa) / da, tb = (ha - oa) / da;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
                if (t0 > t1) return false;
            }
            return t1 >= 0;
        }
    }
}
