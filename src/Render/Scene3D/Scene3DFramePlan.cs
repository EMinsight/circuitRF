// brief-em3d-28 R-em3d28-1 / §8.4's frame loop — WHAT one frame draws, decided below the firewall.
//
// A GPU backend (src/Ui/Viewer3D) is plumbing: it binds the buffers it uploaded for a generation,
// writes the uniform block, and issues the draws this plan lists, in order. Every decision — which
// objects are visible, the back-to-front order of the translucent ones, what hover and selection
// highlight, where the clip plane is, whether this frame carries a pick — is made here, once, for all
// three backends. So a later headless `render` of a 3D view can reuse it, and the gates can count
// draws without a GPU.
//
// ZERO ALLOCATION PER FRAME. Plan() writes into arrays sized when the scene changed; the translucent
// sort is in place. The frame loop is near-zero managed work (§8.4 point 1), which is also what keeps
// the owner's Debug build responsive.

using System.Numerics;
using System.Runtime.InteropServices;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Render.Scene3D;

/// <summary>The pipeline state a draw needs.</summary>
public enum Scene3DPipeline
{
    /// <summary>Triangles, depth write, no blend.</summary>
    Opaque,
    /// <summary>Triangles, depth test without write, blend (RGB src-alpha; alpha ONE, 1 − src-alpha).</summary>
    Translucent,
    /// <summary>A line list, depth write, no blend, unshaded.</summary>
    Lines,
    /// <summary>The ID pass: triangles into the R32Uint + position targets.</summary>
    Pick,
    /// <summary>brief-em3d-29 — a field's triangles (FieldVertex, not indexed), depth write, no blend,
    /// coloured by the field uniforms.</summary>
    Field,
    /// <summary>brief-em3d-43 — a selected object's feature edges (a line list from the scene's line
    /// buffer), depth test off, blend, drawn only where the selection says (fs_edge).</summary>
    Edges,
    /// <summary>brief-em3d-43 R-em3d43-4d — a selected face again, depth test off, blend at reduced
    /// opacity (fs_top), so a face behind others is seen through them.</summary>
    OnTop,
    /// <summary>brief-em3d-45 R-em3d45-2 — the drawing plane's grid: six vertices made by the vertex shader
    /// (vs_grid) covering the viewport, no vertex buffer, depth test without write, blend; fs_grid casts each
    /// fragment's ray at the plane and writes its depth.</summary>
    Grid,
}

/// <summary>Which buffer a draw reads: the scene's, one of the overlay slots, the field's, or none (the grid).</summary>
public enum Scene3DBuffer { Scene, SceneLines, Overlay0, Overlay1, Overlay2, Field, None }

/// <summary>One draw: <see cref="Count"/> indices (triangles) or vertices (lines) from <see cref="First"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Scene3DDraw
{
    public Scene3DPipeline Pipeline;
    public Scene3DBuffer Buffer;
    public int First, Count;
    /// <summary>brief-em3d-46 — the per-draw transform: a slot of <see cref="Scene3DFramePlan.Transforms"/>, 0 for the
    /// identity. A backend hands the slot's 80 bytes (the matrix, and brief-em3d-48's id offset) to the vertex stage before the draw (Metal setVertexBytes,
    /// a D3D11 constant buffer, Vulkan push constants), and only when it differs from the draw before.</summary>
    public int Transform;
}

/// <summary>
/// brief-em3d-46 R-em3d46-1a — a drag's PREVIEW: what is already drawn, drawn again under per-draw transforms. The
/// document is not touched and nothing uploads but the transforms. <see cref="Moving"/> marks the objects (by
/// ID − 1); each is drawn once per entry of <see cref="Copies"/> (row-vector matrices in scene-local metres), and
/// also where it is when <see cref="KeepOriginal"/> (a duplicate, an array). A moving object is left out of the
/// ID pass, so what the cursor finds — and snaps to — is what lies under it.
/// </summary>
public sealed class Scene3DPreview(bool[] moving, Matrix4x4[] copies, bool keepOriginal)
{
    public bool[] Moving { get; } = moving;
    public Matrix4x4[] Copies { get; set; } = copies;
    public bool KeepOriginal { get; } = keepOriginal;

    public bool IsMoving(uint id) => id >= 1 && id <= Moving.Length && Moving[id - 1];
}

/// <summary>An overlay's lines (the Gmsh mesh, its section on the clip plane, the FDTD grid) and the
/// version that changes when they do — a backend uploads a slot only when its version moved.</summary>
public sealed class Scene3DOverlay(Scene3DVertex[] lines, long version)
{
    public Scene3DVertex[] Lines { get; } = lines;
    public long Version { get; } = version;
    public static readonly Scene3DOverlay None = new([], 0);
}

/// <summary>
/// Everything the view knows that is not geometry: the camera, per-object visibility, hover, selection,
/// the clip plane, which overlays are on, and a pending pick. Changed by the input loop, read by
/// <see cref="Scene3DFramePlan.Plan"/>. Owns no geometry, so changing it can never cause any.
/// </summary>
public sealed class Viewer3DViewState
{
    /// <summary>The camera. Starts with a real field of view and distance, so a frame planned before
    /// the first scene lands (or for a setup that refused) is a valid, empty picture.</summary>
    public Camera3D Camera = new() { FovY = Camera3D.DefaultFovY, Distance = 1e-2f, SceneRadius = 1e-3f, Yaw = -MathF.PI / 4, Pitch = 0.6f };
    public ClipPlane3D Clip;
    public bool[] Visible = [];
    public uint Hovered;
    /// <summary>brief-em3d-43 — the hovered object's face under the cursor (Face mode's hover), or −1.</summary>
    public int HoveredFace = -1;
    /// <summary>brief-em3d-43 R-em3d43-2 — what a click selects.</summary>
    public Scene3DSelectMode Mode;
    /// <summary>brief-em3d-43 — the selection, in the order it was made. The shader highlights the first
    /// <see cref="Scene3DFramePlan.SelectionLimit"/>; the rest are selected all the same.</summary>
    public Scene3DItem[] Selection = [];

    /// <summary>The first selected object, 0 for none; setting it selects that one object.</summary>
    public uint Selected
    {
        get => Selection.Length > 0 ? Selection[0].Object : 0;
        set => Selection = value == 0 ? [] : [Scene3DItem.OfObject(value)];
    }
    /// <summary>The cursor, device pixels from the top-left; negative when it is not over the view.</summary>
    public float CursorX = -1, CursorY = -1;
    public bool ShowMesh, ShowMeshSection, ShowGrid;
    /// <summary>brief-em3d-29 — draw the field geometry.</summary>
    public bool ShowField;
    /// <summary>Objects a field surface is drawn ON (a conductor, a solid's faces): their own triangles
    /// are left out while the field is shown, since the two would lie in one plane and fight.</summary>
    public bool[] FieldCovered = [];
    /// <summary>The field uniform block (FieldUniforms.Floats): the phase, the range, the mode and the
    /// colour map — what an animation changes, per frame, instead of any geometry.</summary>
    public readonly float[] Field = new float[Fields.FieldUniforms.Floats];
    /// <summary>brief-em3d-45 — the drawing plane's grid, or null (the read-only viewer draws none).</summary>
    public DrawingGridSettings? DrawingGrid;
    public bool ShowAxisIndicator = true;
    /// <summary>A camera gesture moved the camera since the last frame.</summary>
    public bool Orbiting;
    public (float R, float G, float B) Background = (0.12f, 0.13f, 0.15f);
    /// <summary>brief-em3d-46 — a drag's preview, or null.</summary>
    public Scene3DPreview? Preview;

    /// <summary>Resets visibility to the scene's defaults when the object list changed shape; keeps the
    /// user's toggles across a regeneration that kept the same objects.</summary>
    public void Adopt(Scene3DModel scene, IReadOnlyList<string>? previousNames)
    {
        var old = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (previousNames is not null)
            for (int i = 0; i < previousNames.Count && i < Visible.Length; i++) old[previousNames[i]] = Visible[i];
        var v = new bool[scene.Objects.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = old.TryGetValue(scene.Objects[i].Name, out bool was) ? was : scene.Objects[i].InitiallyVisible;
        Visible = v;
        if (Hovered > v.Length) { Hovered = 0; HoveredFace = -1; }
        if (Selection.Any(i => i.Object > v.Length)) Selection = [.. Selection.Where(i => i.Object <= v.Length)];
    }

    public bool IsVisible(uint id) => id >= 1 && id <= Visible.Length && Visible[id - 1];

    /// <summary>Drawn in the colour pass: visible, and not under a field surface being shown.</summary>
    public bool IsDrawn(uint id) => IsVisible(id) && !(ShowField && id <= FieldCovered.Length && FieldCovered[id - 1]);
}

/// <summary>One frame's uniform blocks and draw lists. Reused from frame to frame.</summary>
public sealed class Scene3DFramePlan
{
    /// <summary>brief-em3d-43 R-em3d43-5 — how many selected items the shader highlights: the uniform
    /// block's (object, face) list. A selection beyond it is still the selection — the tree, the
    /// Properties panel and every command see all of it — but only the first this many are drawn
    /// highlighted, and the status line says so.</summary>
    public const int SelectionLimit = 64;

    /// <summary>Where the selection list starts in the block, in floats.</summary>
    private const int SelectionAt = 32;

    /// <summary>Where brief 29's field block starts, in floats.</summary>
    public const int FieldAt = SelectionAt + 2 * SelectionLimit;

    /// <summary>Where brief 45's grid block starts, in floats.</summary>
    public const int GridAt = FieldAt + Fields.FieldUniforms.Floats;

    /// <summary>Floats in the uniform block — the WGSL <c>U</c>: vp (16), eye (4), clip (4), the hovered
    /// (object, face), flags, the mode, the selection's count and three pads (128 bytes), the selection's
    /// (object, face) pairs (512 bytes), brief 29's field block (<see cref="Fields.FieldUniforms"/>,
    /// 288 bytes), then brief 45's grid block (<see cref="PlaneGrid.Floats"/>, 224 bytes). 1,152 bytes.</summary>
    public const int UniformFloats = GridAt + PlaneGrid.Floats;
    public const int UniformBytes = UniformFloats * 4;

    /// <summary>Flag bits in the uniform block's <c>flags</c>.</summary>
    public const uint FlagClip = 1, FlagCapBackFaces = 2;

    public readonly float[] Uniforms = new float[UniformFloats];

    /// <summary>brief-em3d-46 — the per-draw transforms, <see cref="TransformFloats"/> floats a slot: the WGSL <c>MX</c> —
    /// a <c>mat4x4f</c> (column-major, p' = M·p) and, brief-em3d-48, a <c>vec4u</c> whose x is added to every vertex's id
    /// (an array element's objects are the prototype's plus that). Slot 0 is the identity; then each element's slot and
    /// its box's (written once per scene); then a preview's copies. <see cref="TransformCount"/> slots are in use this
    /// frame.</summary>
    public float[] Transforms = IdentitySlots(8);
    public int TransformCount = 1;

    /// <summary>Floats a transform slot takes: the matrix's 16 and the id offset's vec4u.</summary>
    public const int TransformFloats = 20;

    /// <summary>Bytes of transform this frame hands the vertex stage for its preview draws: TransformBytesPerDraw each — what a
    /// drag's preview costs a frame, and all it costs (gate 1).</summary>
    public long TransformBytes;

    public const int TransformBytesPerDraw = TransformFloats * 4;

    /// <summary>The most copies a preview draws (an array's live preview beyond this shows its first copies; the
    /// accepted array writes them all). Also what a backend sizes its per-frame transform slots for.</summary>
    public const int MaxPreviewCopies = 256;
    public readonly float[] PickUniforms = new float[UniformFloats];
    public Scene3DDraw[] Draws = new Scene3DDraw[16];
    public int DrawCount;
    public Scene3DDraw[] PickDraws = new Scene3DDraw[16];
    public int PickDrawCount;
    /// <summary>Whether this frame carries an ID pass, and the pixel it reads.</summary>
    public bool Pick;
    public int PickX, PickY;
    /// <summary>brief-em3d-44 R-em3d44-2a — the ID pass reads an N × N patch centred on (PickX, PickY): N odd,
    /// set by the pane to what its backend reads back (1 for a backend that reads one texel).</summary>
    public int PickSize = 1;
    /// <summary>The viewport pixels per DIP the pane draws at — carried to the patch for the overlay.</summary>
    public float PickPixelsPerDip = 1;
    /// <summary>What the pick was planned with, handed back with its read-back (the patch's camera and cursor).</summary>
    public Camera3D PickCamera;
    public float PickCursorX, PickCursorY;
    public int Width, Height;
    public (float R, float G, float B) Clear;
    public long SceneGeneration = -1;

    /// <summary>brief-em3d-45 — the grid this frame drew (MinorDbu 0: none), and how many frames drew one.</summary>
    public PlaneGridSpacing GridSpacing;
    public long GridFrames;

    private float[] _keys = [];
    private int[] _order = [];
    private Scene3DModel? _sized;
    /// <summary>Per object (by ID − 1): its edge batch's index, or −1; and its triangle batch's.</summary>
    private int[] _edgeOf = [], _batchOf = [];
    private bool[] _marked = [];

    /// <summary>brief-em3d-48 R-em3d48-3c — the most triangles a frame draws before array elements, farthest from the eye
    /// first, are drawn as their bounding boxes. The owner sets it (Settings); the status line says when it bites.</summary>
    public long TriangleBudget = DefaultTriangleBudget;
    public const long DefaultTriangleBudget = 20_000_000;

    /// <summary>brief-em3d-48 — how many elements this frame drew as boxes, and how many the scene has.</summary>
    public int LodBoxedElements;
    public int ElementCount;

    /// <summary>brief-em3d-48 — slot of element <paramref name="e"/>'s transform, and of its bounding box's.</summary>
    public static int ElementSlot(int e) => 1 + e;
    private int BoxSlot(int e) => 1 + _elements + e;
    private int _elements, _copyBase = 1;
    private float[] _elemKeys = [];
    private int[] _elemOrder = [];
    private bool[] _boxed = [];
    private long _ownedTriangles, _elementTriangles;
    private int[] _comboStart = [];
    private readonly List<int> _comboUsed = [];
    private int _nextSlot;

    /// <summary>
    /// Plans a <paramref name="width"/> × <paramref name="height"/> frame of <paramref name="scene"/>.
    /// <paramref name="flipY"/> for an API whose framebuffer y runs down. <paramref name="pick"/> asks
    /// for an ID pass at the cursor. Allocates only when the scene changed.
    /// </summary>
    public void Plan(Scene3DModel scene, Viewer3DViewState view, int width, int height, bool flipY, bool pick,
                     Scene3DOverlay mesh, Scene3DOverlay section, Scene3DOverlay grid, Fields.Scene3DFieldGeometry? field = null)
    {
        if (!ReferenceEquals(_sized, scene)) Size(scene);
        Width = width; Height = height;
        Clear = view.Background;
        SceneGeneration = scene.Generation;
        uint flags = view.Clip.Enabled ? FlagClip | FlagCapBackFaces : 0;
        Fill(Uniforms, view, width, height, flipY, -1, -1, flags);

        DrawCount = 0;
        TransformBytes = 0;
        var preview = view.Preview;
        WriteTransforms(preview);
        ChooseDetail(scene, view);
        var batches = scene.Batches;
        int owned = scene.OwnedBatches;
        for (int k = 0; k < owned; k++)
        {
            var b = batches[k];
            if (!b.Translucent && view.IsDrawn(b.ObjectId))
                AddMoved(preview, b.ObjectId, Scene3DPipeline.Opaque, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true);
        }
        // brief-em3d-48 R-em3d48-3a — each element: ONE draw of its prototype's opaque triangles under its own transform
        // when the whole element is drawn as it is; object by object when part of it is hidden or moving.
        for (int e = 0; e < _elements; e++)
        {
            if (_boxed[e]) continue;
            var el = scene.Elements[e];
            var g = scene.Groups[el.Group];
            if (Whole(scene, view, preview, el, g, pickPass: false))
            {
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Opaque, Scene3DBuffer.Scene, g.OpaqueFirst, g.OpaqueCount, ElementSlot(e));
                continue;
            }
            for (int k = el.FirstBatch; k < el.FirstBatch + el.BatchCount; k++)
            {
                var b = batches[k];
                if (!b.Translucent && view.IsDrawn(b.ObjectId))
                    AddMoved(preview, b.ObjectId, Scene3DPipeline.Opaque, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true, e);
            }
        }
        // brief-em3d-29 — the field's slice and surfaces, one draw, opaque, before anything translucent.
        if (view.ShowField && field is { Vertices.Length: > 0 } f)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Field, Scene3DBuffer.Field, 0, f.Vertices.Length);
        foreach (var lb in scene.LineBatches)
            if (view.IsVisible(lb.ObjectId))
                AddMoved(preview, lb.ObjectId, Scene3DPipeline.Lines, Scene3DBuffer.SceneLines, lb.FirstVertex, lb.VertexCount, identity: true);
        // brief-em3d-48 R-em3d48-3c — an element over the budget: its bounding box, stated on screen.
        if (LodBoxedElements > 0)
            for (int e = 0; e < _elements; e++)
                if (_boxed[e])
                    Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.SceneLines, scene.UnitBox.FirstVertex, scene.UnitBox.VertexCount, BoxSlot(e));
        if (view.ShowMesh && mesh.Lines.Length > 0)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay0, 0, mesh.Lines.Length);
        if (view.ShowMeshSection && view.Clip.Enabled && section.Lines.Length > 0)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay1, 0, section.Lines.Length);
        if (view.ShowGrid && grid.Lines.Length > 0)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay2, 0, grid.Lines.Length);

        // brief-em3d-45 R-em3d45-2b — the drawing grid after everything opaque (so solids hide it) and before
        // the translucent objects (so it shows faintly through a dielectric). Uniforms only: nothing uploads.
        GridSpacing = default;
        if (view.DrawingGrid is { Visible: true } dg)
        {
            GridSpacing = PlaneGrid.Fill(Uniforms.AsSpan(GridAt), scene, view.Camera, dg, width, height, flipY);
            if (GridSpacing.MinorDbu > 0)
            {
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Grid, Scene3DBuffer.None, 0, 6);
                GridFrames++;
            }
        }
        else Uniforms.AsSpan(GridAt, PlaneGrid.Floats).Clear();

        // Translucent objects back to front, one draw each (brief 27 §2.4: per object, not per triangle).
        // Keyed on VIEW DEPTH, not distance from the eye: orthographic allows a negative near plane, so
        // after zooming in the eye sits inside the scene and objects behind it are still drawn — the
        // nearest to the viewer, though the eye is closer to them than to what they cover.
        var eye = view.Camera.Eye;
        var forward = view.Camera.Forward;
        int n = 0;
        for (int i = 0; i < batches.Length; i++)
        {
            if (!batches[i].Translucent || !view.IsDrawn(batches[i].ObjectId)) continue;
            if (batches[i].Element >= 0 && _boxed[batches[i].Element]) continue;
            _order[n] = i;
            _keys[n] = -Vector3.Dot(scene.Objects[batches[i].ObjectId - 1].Centroid - eye, forward);
            n++;
        }
        Array.Sort(_keys, _order, 0, n);
        for (int k = 0; k < n; k++)
        {
            var b = batches[_order[k]];
            AddMoved(preview, b.ObjectId, Scene3DPipeline.Translucent, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true, b.Element);
        }

        // brief-em3d-43 — the selection, last: the edges of each selected object (Object mode) or of each
        // object with a selected face (Face mode), then those objects' triangles again for the selected
        // face drawn on top. The shaders pick out what is selected; the plan only chooses which batches.
        if (view.Mode != Scene3DSelectMode.Vertex && view.Selection.Length > 0)
        {
            int limit = Math.Min(view.Selection.Length, SelectionLimit);
            for (int k = 0; k < limit; k++)
            {
                uint id = view.Selection[k].Object;
                if (id < 1 || id > _marked.Length || _marked[id - 1] || !view.IsDrawn(id)) continue;
                _marked[id - 1] = true;
                // A moving object's outline follows its copies: the outline marks what is being placed.
                if (_edgeOf[id - 1] is int e and >= 0)
                {
                    var eb = scene.EdgeBatches[e];
                    AddMoved(preview, id, Scene3DPipeline.Edges, Scene3DBuffer.SceneLines, eb.FirstVertex, eb.VertexCount, identity: false, eb.Element);
                }
                if (view.Mode == Scene3DSelectMode.Face && _batchOf[id - 1] is int t and >= 0)
                {
                    var tb = batches[t];
                    AddMoved(preview, id, Scene3DPipeline.OnTop, Scene3DBuffer.Scene, tb.FirstIndex, tb.IndexCount, identity: false, tb.Element);
                }
            }
            for (int k = 0; k < limit; k++)
            {
                uint id = view.Selection[k].Object;
                if (id >= 1 && id <= _marked.Length) _marked[id - 1] = false;
            }
        }

        Pick = pick && view.CursorX >= 0 && view.CursorY >= 0 && view.CursorX < width && view.CursorY < height;
        PickDrawCount = 0;
        if (Pick)
        {
            PickX = (int)view.CursorX; PickY = (int)view.CursorY;
            PickCamera = view.Camera;
            PickCursorX = view.CursorX; PickCursorY = view.CursorY;
            Fill(PickUniforms, view, width, height, flipY, PickX, PickY, view.Clip.Enabled ? FlagClip : 0, PickSize);
            for (int k = 0; k < owned; k++)
            {
                var b = batches[k];
                if (view.IsVisible(b.ObjectId) && scene.Objects[b.ObjectId - 1].Pickable && preview?.IsMoving(b.ObjectId) != true)
                    Add(ref PickDraws, ref PickDrawCount, Scene3DPipeline.Pick, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount);
            }
            for (int e = 0; e < _elements; e++)
            {
                if (_boxed[e]) continue;
                var el = scene.Elements[e];
                var g = scene.Groups[el.Group];
                bool whole = Whole(scene, view, preview, el, g, pickPass: true);
                if (whole) Add(ref PickDraws, ref PickDrawCount, Scene3DPipeline.Pick, Scene3DBuffer.Scene, g.OpaqueFirst, g.OpaqueCount, ElementSlot(e));
                for (int k = el.FirstBatch; k < el.FirstBatch + el.BatchCount; k++)
                {
                    var b = batches[k];
                    if (whole && !b.Translucent) continue;
                    if (view.IsVisible(b.ObjectId) && scene.Objects[b.ObjectId - 1].Pickable && preview?.IsMoving(b.ObjectId) != true)
                        Add(ref PickDraws, ref PickDrawCount, Scene3DPipeline.Pick, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, ElementSlot(e));
                }
            }
        }
        TransformCount = _nextSlot;
    }

    /// <summary>Whether element <paramref name="el"/> draws its opaque triangles in ONE draw this frame: its group's opaque
    /// range is one contiguous run, every one of its opaque objects is drawn (visible and pickable, in the ID pass) and
    /// none of its objects is moving.</summary>
    private static bool Whole(Scene3DModel scene, Viewer3DViewState view, Scene3DPreview? preview, in Scene3DElement el,
                              in Scene3DInstanceGroup g, bool pickPass)
    {
        if (g.OpaqueCount <= 0 || el.Count != g.Count) return false;
        for (uint id = el.FirstId; id < el.FirstId + (uint)el.Count; id++)
        {
            if (preview?.IsMoving(id) == true) return false;
            // Only the opaque objects are in the one draw; a translucent one is drawn (and sorted) on its own either way.
            if (scene.Objects[id - 1].Translucent) continue;
            if (pickPass ? !view.IsVisible(id) || !scene.Objects[id - 1].Pickable : !view.IsDrawn(id)) return false;
        }
        return true;
    }

    /// <summary>R-em3d48-3c — which elements are over the triangle budget: none while the whole scene fits; else the
    /// farthest from the eye, until the rest fits.</summary>
    private void ChooseDetail(Scene3DModel scene, Viewer3DViewState view)
    {
        LodBoxedElements = 0;
        if (_elements == 0) return;
        Array.Clear(_boxed);
        long room = TriangleBudget - _ownedTriangles;
        if (_elementTriangles <= room) return;
        var eye = view.Camera.Eye;
        for (int e = 0; e < _elements; e++)
        {
            var el = scene.Elements[e];
            var g = scene.Groups[el.Group];
            var c = (g.Min + g.Max) * 0.5f + el.Offset;
            _elemKeys[e] = Vector3.DistanceSquared(c, eye);
            _elemOrder[e] = e;
        }
        Array.Sort(_elemKeys, _elemOrder, 0, _elements);
        long used = 0;
        for (int k = 0; k < _elements; k++)
        {
            int e = _elemOrder[k];
            used += scene.Groups[scene.Elements[e].Group].Triangles;
            if (used > room) { _boxed[e] = true; LodBoxedElements++; }
        }
    }

    private void Size(Scene3DModel scene)
    {
        int need = scene.Batches.Length + scene.LineBatches.Length + 6 + 2 * Math.Min(scene.Objects.Length, SelectionLimit);
        if (Draws.Length < need) Draws = new Scene3DDraw[need];
        if (PickDraws.Length < need) PickDraws = new Scene3DDraw[need];
        _keys = new float[scene.Batches.Length];
        _order = new int[scene.Batches.Length];
        _edgeOf = new int[scene.Objects.Length];
        _batchOf = new int[scene.Objects.Length];
        _marked = new bool[scene.Objects.Length];
        Array.Fill(_edgeOf, -1);
        Array.Fill(_batchOf, -1);
        for (int k = 0; k < scene.EdgeBatches.Length; k++) _edgeOf[scene.EdgeBatches[k].ObjectId - 1] = k;
        for (int k = 0; k < scene.Batches.Length; k++) _batchOf[scene.Batches[k].ObjectId - 1] = k;

        // brief-em3d-48 — the element slots and their boxes' slots are written once per scene; a frame writes only a
        // preview's copies after them.
        _elements = ElementCount = scene.Elements.Length;
        _copyBase = 1 + 2 * _elements;
        _elemKeys = new float[_elements];
        _elemOrder = new int[_elements];
        _boxed = new bool[_elements];
        _comboStart = new int[_elements];
        Array.Fill(_comboStart, -1);
        _ownedTriangles = 0;
        for (int k = 0; k < scene.OwnedBatches; k++) _ownedTriangles += scene.Batches[k].IndexCount / 3;
        _elementTriangles = 0;
        foreach (var el in scene.Elements) _elementTriangles += scene.Groups[el.Group].Triangles;
        EnsureSlots(_copyBase);
        for (int e = 0; e < _elements; e++)
        {
            var el = scene.Elements[e];
            var g = scene.Groups[el.Group];
            WriteSlot(ElementSlot(e), Matrix4x4.CreateTranslation(el.Offset), el.IdOffset);
            var size = Vector3.Max(g.Max - g.Min, new Vector3(1e-12f));
            WriteSlot(BoxSlot(e), Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(g.Min + el.Offset), 0);
        }
        _sized = scene;
    }

    private static void Add(ref Scene3DDraw[] list, ref int count, Scene3DPipeline p, Scene3DBuffer buf, int first, int n, int transform = 0)
    {
        if (count == list.Length) Array.Resize(ref list, list.Length * 2);
        list[count++] = new Scene3DDraw { Pipeline = p, Buffer = buf, First = first, Count = n, Transform = transform };
    }

    /// <summary>A draw of object <paramref name="id"/>'s batch: as it is when nothing moves it (under its element's
    /// transform, for an element's object); under each of the preview's copies when it moves — and also as it is when
    /// the preview keeps the original and <paramref name="identity"/> says the original is drawn in this pass.</summary>
    private void AddMoved(Scene3DPreview? preview, uint id, Scene3DPipeline p, Scene3DBuffer buf, int first, int n, bool identity,
                          int element = -1)
    {
        int own = element >= 0 ? ElementSlot(element) : 0;
        if (preview is null || !preview.IsMoving(id))
        {
            Add(ref Draws, ref DrawCount, p, buf, first, n, own);
            return;
        }
        if (identity && preview.KeepOriginal) Add(ref Draws, ref DrawCount, p, buf, first, n, own);
        int copies = Math.Min(preview.Copies.Length, MaxPreviewCopies);
        int start = element >= 0 ? ComboSlots(preview, element, copies) : _copyBase;
        for (int k = 0; k < copies; k++)
        {
            Add(ref Draws, ref DrawCount, p, buf, first, n, start + k);
            TransformBytes += TransformBytesPerDraw;
        }
    }

    /// <summary>The slots of element <paramref name="element"/> under each of the preview's copies: the element's own
    /// translation, then the copy — made on first asking in a frame, after every slot before them.</summary>
    private int ComboSlots(Scene3DPreview preview, int element, int copies)
    {
        if (_comboStart[element] >= 0) return _comboStart[element];
        int start = _nextSlot;
        EnsureSlots(start + copies);
        var own = ReadMatrix(ElementSlot(element), out uint idOff);
        for (int k = 0; k < copies; k++) WriteSlot(start + k, own * preview.Copies[k], idOff);
        _nextSlot = start + copies;
        _comboStart[element] = start;
        _comboUsed.Add(element);
        return start;
    }

    /// <summary>Slot 0 the identity, the element slots (written with the scene), then the preview's copies.</summary>
    private void WriteTransforms(Scene3DPreview? preview)
    {
        foreach (int e in _comboUsed) _comboStart[e] = -1;
        _comboUsed.Clear();
        int copies = Math.Min(preview?.Copies.Length ?? 0, MaxPreviewCopies);
        EnsureSlots(_copyBase + copies);
        for (int k = 0; k < copies; k++) WriteSlot(_copyBase + k, preview!.Copies[k], 0);
        _nextSlot = _copyBase + copies;
    }

    /// <summary>Room for <paramref name="slots"/> slots; a grown array keeps what the old one held.</summary>
    private void EnsureSlots(int slots)
    {
        if (Transforms.Length >= TransformFloats * slots) return;
        var grown = IdentitySlots(Math.Max(slots, 2 * Transforms.Length / TransformFloats));
        Transforms.AsSpan().CopyTo(grown);
        Transforms = grown;
    }

    private void WriteSlot(int slot, in Matrix4x4 m, uint idOffset)
    {
        var dst = Transforms.AsSpan(TransformFloats * slot, TransformFloats);
        WriteMatrix(m, dst);
        dst[16] = BitConverter.UInt32BitsToSingle(idOffset);
        dst[17] = dst[18] = dst[19] = 0;
    }

    private Matrix4x4 ReadMatrix(int slot, out uint idOffset)
    {
        var s = Transforms.AsSpan(TransformFloats * slot, TransformFloats);
        idOffset = BitConverter.SingleToUInt32Bits(s[16]);
        return new Matrix4x4(s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8], s[9], s[10], s[11], s[12], s[13], s[14], s[15]);
    }

    private static float[] IdentitySlots(int slots)
    {
        var t = new float[TransformFloats * slots];
        for (int k = 0; k < slots; k++) WriteMatrix(Matrix4x4.Identity, t.AsSpan(TransformFloats * k, 16));
        return t;
    }

    /// <summary>A row-vector matrix into the WGSL layout: stored row-major, it is the column-vector transpose stored
    /// column-major — the camera's own convention (Camera3D.WriteViewProjection).</summary>
    public static void WriteMatrix(in Matrix4x4 m, Span<float> dst)
    {
        dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
        dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
        dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
        dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
    }

    private static void Fill(float[] u, Viewer3DViewState view, int w, int h, bool flipY, float px, float py, uint flags, int pickSize = 1)
    {
        view.Camera.WriteViewProjection(u.AsSpan(0, 16), w, h, flipY, px, py, pickSize);
        var eye = view.Camera.Eye;
        if (view.Camera.Projection == Projection3D.Orthographic)
        {
            // An orthographic eye is at infinity: shade by the view direction, not a point.
            var back = view.Camera.Back;
            eye = view.Camera.Target + back * (view.Camera.Distance * 1e4f);
        }
        u[16] = eye.X; u[17] = eye.Y; u[18] = eye.Z; u[19] = 1;
        var c = view.Clip.Equation;
        u[20] = c.X; u[21] = c.Y; u[22] = c.Z; u[23] = c.W;
        var bits = MemoryMarshal.Cast<float, uint>(u.AsSpan());
        bits[24] = view.Hovered;
        bits[25] = view.HoveredFace < 0 ? Scene3DVertex.NoFace : (uint)view.HoveredFace;
        bits[26] = flags;
        bits[27] = (uint)view.Mode;
        int nsel = Math.Min(view.Selection.Length, SelectionLimit);
        bits[28] = (uint)nsel;
        bits[29] = bits[30] = bits[31] = 0;
        for (int k = 0; k < SelectionLimit; k++)
        {
            bool on = k < nsel;
            bits[SelectionAt + 2 * k] = on ? view.Selection[k].Object : 0;
            bits[SelectionAt + 2 * k + 1] = on && view.Selection[k].Face >= 0 ? (uint)view.Selection[k].Face : Scene3DVertex.NoFace;
        }
        view.Field.AsSpan().CopyTo(u.AsSpan(FieldAt));
    }
}
