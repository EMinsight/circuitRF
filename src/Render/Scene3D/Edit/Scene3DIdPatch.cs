// brief-em3d-44 R-em3d44-2a / -2e — the pick PATCH: the ID pass read back as N × N texels around the cursor
// instead of one, each holding the (object, face) that won the depth test there and its depth.
//
// One small read-back answers the two questions snapping asks, whatever the size of the scene:
//   * what is NEAR the cursor — the distinct (object, face) pairs in the patch;
//   * what is VISIBLE — a feature is hidden when the patch holds something nearer at its pixel.
//
// Two fills, one layout:
//   * the GPU's — the backend narrows the ID pass to the N × N window (Camera3D.ViewProjectionMatrix's pick
//     size) and copies its RG32Uint (object, face) and RGBA32Float (scene-local point) targets back; the
//     depth stored here is the point's VIEW depth, so both fills store the same measure;
//   * the CPU's (Render) — brief 28's IdAtPixel extended to a patch: the triangles of the objects whose
//     boxes come within the window of the cursor's ray (RayHits' hierarchy, never the whole scene), through
//     the SAME narrowed matrix, sampled at each texel's centre, nearest depth winning. It is what the gates
//     run, and what a backend that reads back one texel falls back to.
//
// A patch carries the camera, viewport and cursor it was read for, so a snap resolved from it a frame later
// projects with the frame's camera, not the one the user has since moved (R-em3d44-2d).

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Edit;

public sealed class Scene3DIdPatch
{
    /// <summary>The largest patch a backend reads back: a 32-pixel radius at any display scale.</summary>
    public const int MaxSize = 65;

    /// <summary>N, odd; 0 before the first fill.</summary>
    public int Size { get; private set; }
    public int Radius => Size / 2;
    /// <summary>The viewport pixel of texel (0, 0): the cursor's pixel less the radius, each axis.</summary>
    public int X0 { get; private set; }
    public int Y0 { get; private set; }
    /// <summary>The cursor, viewport pixels (continuous: pixel i covers [i, i + 1)).</summary>
    public float CursorX { get; private set; }
    public float CursorY { get; private set; }
    /// <summary>The camera and viewport the patch was read with.</summary>
    public Camera3D Camera { get; private set; }
    public float Width { get; private set; }
    public float Height { get; private set; }
    /// <summary>Viewport pixels per device-independent pixel — the overlay draws in DIPs.</summary>
    public float PixelsPerDip { get; private set; } = 1;
    /// <summary>The scene generation it was read from.</summary>
    public long Generation { get; private set; } = -1;
    /// <summary>Whether it holds a read-back at all.</summary>
    public bool Valid => Size > 0;

    public readonly uint[] Ids = new uint[MaxSize * MaxSize];
    public readonly uint[] Faces = new uint[MaxSize * MaxSize];
    /// <summary>View depth (<see cref="Camera3D.ViewDepth"/>); +∞ where nothing was hit.</summary>
    public readonly float[] Depth = new float[MaxSize * MaxSize];

    private readonly float[] _ndcZ = new float[MaxSize * MaxSize];
    private readonly List<int> _near = [];
    private readonly Stack<int> _stack = new();

    /// <summary>Triangles the last CPU fill rasterised — bounded by what is near the ray (gate 1).</summary>
    public long TrianglesRasterized { get; private set; }

    /// <summary>The patch size for a snap radius of <paramref name="radiusPixels"/> viewport pixels.</summary>
    public static int SizeFor(float radiusPixels) => Math.Min(MaxSize, 2 * (int)MathF.Ceiling(Math.Max(0, radiusPixels)) + 1);

    /// <summary>Starts a fill: the patch centred on the cursor's pixel, every texel background.</summary>
    public void Begin(int size, float cursorX, float cursorY, in Camera3D camera, float width, float height, long generation,
                      float pixelsPerDip = 1)
    {
        if (size < 1 || size > MaxSize || size % 2 == 0) throw new ArgumentOutOfRangeException(nameof(size));
        Size = size;
        CursorX = cursorX; CursorY = cursorY;
        X0 = (int)MathF.Floor(cursorX) - size / 2;
        Y0 = (int)MathF.Floor(cursorY) - size / 2;
        Camera = camera;
        Width = width; Height = height;
        Generation = generation;
        PixelsPerDip = pixelsPerDip > 0 ? pixelsPerDip : 1;
        int n = size * size;
        Array.Clear(Ids, 0, n);
        Array.Fill(Faces, Scene3DVertex.NoFace, 0, n);
        Array.Fill(Depth, float.PositiveInfinity, 0, n);
    }

    /// <summary>One texel of a GPU read-back: <paramref name="hit"/> false leaves it background.</summary>
    public void Set(int i, int j, uint id, uint face, Vector3 point, bool hit)
    {
        if (!hit || id == 0) return;
        int k = j * Size + i;
        Ids[k] = id;
        Faces[k] = face;
        Depth[k] = Camera.ViewDepth(point);
    }

    /// <summary>Forgets the read-back — a scene change or a cursor that left the view.</summary>
    public void Clear() { Size = 0; Generation = -1; }

    /// <summary>The texel index of viewport pixel (<paramref name="px"/>, <paramref name="py"/>), or −1.</summary>
    public int IndexOf(int px, int py)
    {
        int i = px - X0, j = py - Y0;
        return (uint)i < (uint)Size && (uint)j < (uint)Size ? j * Size + i : -1;
    }

    /// <summary>
    /// R-em3d44-2e — the CPU fill: what the GPU's N × N ID pass writes, computed in software through the very
    /// matrix the backend is handed. Only the objects the cursor's ray passes within the window of are
    /// rasterised. Allocation-free in steady state.
    /// </summary>
    public void Render(Scene3DModel scene, in Camera3D camera, float cursorX, float cursorY, float width, float height, int size,
                       ReadOnlySpan<bool> visible, in ClipPlane3D clip = default, float pixelsPerDip = 1)
    {
        Begin(size, cursorX, cursorY, camera, width, height, scene.Generation, pixelsPerDip);
        TrianglesRasterized = 0;
        int n = size * size;
        Array.Fill(_ndcZ, float.PositiveInfinity, 0, n);
        int px = (int)MathF.Floor(cursorX), py = (int)MathF.Floor(cursorY);
        var m = camera.ViewProjectionMatrix(width, height, px, py, size);
        var (o, d) = camera.Ray(px, py, width, height);
        // The window's corners are √2 radii out; a pixel's width more covers the texel's own extent.
        RayHits.BatchesNearRay(scene, o, d, camera, height, size * 0.75f + 1, _near, _stack);

        var verts = scene.Vertices;
        foreach (int k in _near)
        {
            var b = scene.Batches[k];
            if (!IsVisible(visible, b.ObjectId) || !scene.Objects[b.ObjectId - 1].Pickable) continue;
            for (int t = b.FirstIndex; t < b.FirstIndex + b.IndexCount; t += 3)
            {
                TrianglesRasterized++;
                var a0 = verts[scene.Indices[t]]; var a1 = verts[scene.Indices[t + 1]]; var a2 = verts[scene.Indices[t + 2]];
                var p0 = new Vector3(a0.X, a0.Y, a0.Z) + b.Offset; var p1 = new Vector3(a1.X, a1.Y, a1.Z) + b.Offset;
                var p2 = new Vector3(a2.X, a2.Y, a2.Z) + b.Offset;
                var c0 = Vector4.Transform(new Vector4(p0, 1), m);
                var c1 = Vector4.Transform(new Vector4(p1, 1), m);
                var c2 = Vector4.Transform(new Vector4(p2, 1), m);
                if (c0.W <= 0 || c1.W <= 0 || c2.W <= 0) continue;
                var n0 = new Vector3(c0.X, c0.Y, c0.Z) / c0.W;
                var n1 = new Vector3(c1.X, c1.Y, c1.Z) / c1.W;
                var n2 = new Vector3(c2.X, c2.Y, c2.Z) / c2.W;
                float area = Edge(n0, n1, n2.X, n2.Y);
                if (area == 0) continue;
                // The texels the triangle's box covers: NDC x ∈ [−1, 1] is texels 0 .. N, y runs down.
                float h = size * 0.5f;
                int i0 = Math.Max(0, (int)MathF.Floor((MathF.Min(n0.X, MathF.Min(n1.X, n2.X)) + 1) * h - 0.5f));
                int i1 = Math.Min(size - 1, (int)MathF.Ceiling((MathF.Max(n0.X, MathF.Max(n1.X, n2.X)) + 1) * h - 0.5f));
                int j0 = Math.Max(0, (int)MathF.Floor((1 - MathF.Max(n0.Y, MathF.Max(n1.Y, n2.Y))) * h - 0.5f));
                int j1 = Math.Min(size - 1, (int)MathF.Ceiling((1 - MathF.Min(n0.Y, MathF.Min(n1.Y, n2.Y))) * h - 0.5f));
                uint face = a0.Face;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        float sx = (i + 0.5f) / h - 1, sy = 1 - (j + 0.5f) / h;
                        float w0 = Edge(n1, n2, sx, sy) / area, w1 = Edge(n2, n0, sx, sy) / area, w2 = Edge(n0, n1, sx, sy) / area;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        float z = w0 * n0.Z + w1 * n1.Z + w2 * n2.Z;
                        int at = j * size + i;
                        // LessEqual, as the GPU's depth state: of two equal depths the later draw wins.
                        if (z < 0 || z > 1 || z > _ndcZ[at]) continue;
                        // The point itself, perspective-correct, for the clip plane and the view depth.
                        float q0 = w0 / c0.W, q1 = w1 / c1.W, q2 = w2 / c2.W, qs = q0 + q1 + q2;
                        var world = (p0 * q0 + p1 * q1 + p2 * q2) / qs;
                        if (!clip.Keeps(world)) continue;
                        _ndcZ[at] = z;
                        Ids[at] = b.ObjectId;
                        Faces[at] = face;
                        Depth[at] = camera.ViewDepth(world);
                    }
            }
        }
    }

    private static bool IsVisible(ReadOnlySpan<bool> visible, uint id)
        => visible.IsEmpty || (id - 1 < (uint)visible.Length && visible[(int)id - 1]);

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
}
