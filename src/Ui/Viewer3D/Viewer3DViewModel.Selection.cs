// brief-em3d-43 R-em3d43-2 / -3 / -4 / -5 — selection in EVERY 3D pane: the three modes, hover, click and
// Shift-click, B through what is behind, the keys, and the context menu's frame.
//
// ONE PANE, TWO USES (R-em3d43-1a). The read-only viewer a .cem opens and the .c3d editor are the same
// view model and the same pane; the editor is this plus a document, reached through IViewer3DEditHost.
// With no host, selecting is for measuring and reading: the status line names the face's area and normal,
// and every command that would write something is absent from the menu, not disabled.
//
// ALL OF IT IS INPUT-LOOP WORK (em-3d.md §8.4): a hover sets two uniforms, a click writes a selection list
// the shader reads, and nothing here tessellates, elaborates or uploads (gate 5). The one CPU geometry query
// is B's ray (RayHits), on the key press, never on a hover, over a hierarchy built once per scene; and
// Vertex mode's hover reads the vertices of the ONE face under the cursor (Scene3DFaces).
//
// SELECTION SURVIVES A REGENERATION BY NAME. An edit rebuilds the scene and may renumber its objects (a
// delete shifts every later ID), so the selection is carried across by object name and face index, and
// what no longer exists drops out of it.

using System.Globalization;
using System.Numerics;
using Avalonia.Input;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>What the editor adds to a 3D pane (null in the read-only viewer).</summary>
public interface IViewer3DEditHost
{
    /// <summary>The document kind of a scene object — <c>Box</c>, <c>Prism</c> … — or the instance it came
    /// from; null when the object is not the document's (never, in practice).</summary>
    string KindOf(Scene3DObject o);

    /// <summary>The instance path of an object inside an instance, or null for the document's own.</summary>
    string? InstanceOf(Scene3DObject o);

    /// <summary>The materials a drawn object may be given (the document's technology's).</summary>
    IReadOnlyList<string> Materials { get; }

    /// <summary>Hides <paramref name="objects"/> (<paramref name="hidden"/> true) or shows them, as ONE undoable
    /// edit of the document's <c>Hidden</c> flags. False when none of them is the document's to hide.</summary>
    bool SetHidden(IReadOnlyList<Scene3DObject> objects, bool hidden, string description);

    /// <summary>Shows every document object, and hides all but <paramref name="keep"/> when it is given.</summary>
    bool ShowAll(IReadOnlyList<Scene3DObject>? keep);

    void SetMaterial(IReadOnlyList<Scene3DObject> objects, string material);

    /// <summary>brief-em3d-53 — Assign Material…: the picker over the technology's materials, for these objects.</summary>
    void AssignMaterial(IReadOnlyList<Scene3DObject> objects) { }

    /// <summary>brief-em3d-53 — Material ▸ New Material…: the picker on a new row, for these objects.</summary>
    void NewMaterial(IReadOnlyList<Scene3DObject> objects) { }

    /// <summary>Deletes the selection's document objects: one undo entry. False when nothing was deletable.</summary>
    bool DeleteSelection();

    /// <summary>The Properties panel, shown (and its Name field focused when <paramref name="rename"/>).</summary>
    void ShowProperties(bool rename);

    /// <summary>brief-em3d-44 R-em3d44-4 / -5 — a snapped point as the status line shows it, in the document's
    /// display unit with <c>≈</c> when it is not an exact DBU point; null leaves it to the pane.</summary>
    string? SnapPointText(Snap3DResult snap) => null;

    /// <summary>brief-em3d-45 R-em3d45-1b / -3 — a click the editor's drawing takes: a tool is armed, or it is a
    /// drawing-plane gesture (Ctrl/Cmd-click a face, Ctrl/Cmd+Shift-click a snapped point). False leaves it to
    /// selection. <paramref name="clickCount"/> 2 is the second click of a double-click.</summary>
    bool DrawClick(KeyModifiers modifiers, int clickCount) => false;

    /// <summary>brief-em3d-45 — a key the drawing takes before the pane's own keys: Shift+A, and while a tool is armed
    /// its Esc, Enter, Tab and the digits that open the typed field.</summary>
    bool DrawKey(Key key, KeyModifiers modifiers) => false;

    /// <summary>brief-em3d-45 — what the drawing adds to the 2D overlay this frame: the rubber band, the document's
    /// construction polylines, a refused outline's crossing.</summary>
    void FillDrawOverlay(Viewer3DDrawOverlay overlay) { }

    /// <summary>brief-em3d-45 — the drawing's entries for the context menu (Extrude, Drawing Plane from Face).</summary>
    IEnumerable<Viewer3DMenuItem> DrawMenuItems() => [];

    /// <summary>brief-em3d-46 R-em3d46-5 — where the move gizmo sits (world metres), or null for none: it is offered
    /// in Object mode with something movable selected and no gesture in progress.</summary>
    CircuitRF.Engine.Em3d.Point3? GizmoPivot => null;

    /// <summary>A press on gizmo handle <paramref name="handle"/>: starts the constrained move. False when it did not.</summary>
    bool GizmoDrag(GizmoHandle handle) => false;

    /// <summary>The gizmo drag's release: the move commits.</summary>
    void GizmoRelease() { }

    /// <summary>The gizmo drag was lost: the move is cancelled.</summary>
    void GizmoCancel() { }

    /// <summary>brief-em3d-46 R-em3d46-6a — the point a measurement click takes: the snap, as the document's exact
    /// point where it is one; else the drawing plane under the cursor. Null leaves it to the pane.</summary>
    Viewer3DMeasurePoint? MeasurePoint() => null;

    /// <summary>A measurement started: the editor disarms its tool (one gesture at a time).</summary>
    void MeasureStarted() { }

    /// <summary>3D round 1 — a drawing tool or an operation is armed: a plain left drag must not orbit (only
    /// Ctrl/Cmd + drag does), so a press that wobbles past the click slop still places its point.</summary>
    bool DrawArmed => false;
}

/// <summary>brief-em3d-45 — the drawing's 2D chrome for one frame, in world metres: the overlay projects it.</summary>
public sealed class Viewer3DDrawOverlay
{
    /// <summary>The tool's rubber band.</summary>
    public List<DrawSegment> Rubber { get; } = [];
    /// <summary>The document's construction polylines (never in the solved problem, so never in the scene).</summary>
    public List<DrawSegment> Construction { get; } = [];
    /// <summary>The polylines selected in the tree.</summary>
    public List<DrawSegment> Selected { get; } = [];
    /// <summary>The two edges a refused outline crosses at.</summary>
    public List<DrawSegment> Crossing { get; } = [];
    /// <summary>The points the gesture has fixed so far.</summary>
    public List<CircuitRF.Engine.Em3d.Point3> Fixed { get; } = [];
    /// <summary>brief-em3d-46 — an operation's pivot, drawn as a small cross.</summary>
    public List<CircuitRF.Engine.Em3d.Point3> Pivots { get; } = [];
    /// <summary>brief-em3d-48 R-em3d48-6b — an instance whose cell resolves to nothing: a dashed box.</summary>
    public List<DrawSegment> Missing { get; } = [];
    /// <summary>brief-em3d-48 — chrome text at a world point: a missing cell's name, an external instance's
    /// <c>[alias]</c> tag. Marking is chrome, never geometry (layout-view §7.1).</summary>
    public List<(CircuitRF.Engine.Em3d.Point3 At, string Text)> Labels { get; } = [];

    public void Clear()
    {
        Rubber.Clear(); Construction.Clear(); Selected.Clear(); Crossing.Clear(); Fixed.Clear(); Pivots.Clear(); Missing.Clear(); Labels.Clear();
    }
}

/// <summary>One context-menu entry. <see cref="Run"/> null and no children is a heading or a disabled item.</summary>
public sealed record Viewer3DMenuItem(string Header, Action? Run = null, bool Enabled = true, string? Tip = null,
                                      IReadOnlyList<Viewer3DMenuItem>? Children = null)
{
    public static readonly Viewer3DMenuItem Separator = new("-");
    public bool IsSeparator => Header == "-";
}

public sealed partial class Viewer3DViewModel
{
    /// <summary>B's list, cached per cursor, mode and scene (R-em3d43-4b).</summary>
    public Scene3DHitCycle HitCycle { get; } = new();

    /// <summary>The editor, or null in the read-only viewer.</summary>
    public IViewer3DEditHost? EditHost { get; set; }

    /// <summary>What a click would select now, or null.</summary>
    public Scene3DItem? HoveredItem { get; private set; }

    /// <summary>brief-em3d-45 — the (object, face) the ID pass last found under the cursor, in any mode; (0, −1) for none.</summary>
    public (uint Object, int Face) LastPick { get; private set; } = (0, -1);

    /// <summary>brief-em3d-45 — the ray through the cursor, world metres (a unit direction), or null off the view.</summary>
    public (CircuitRF.Engine.Em3d.Point3 Origin, CircuitRF.Engine.Em3d.Point3 Direction)? CursorRay()
    {
        if (View.CursorX < 0 || View.CursorY < 0) return null;
        // The snap query casts its ray through the cursor itself (Ray adds half a pixel): the same ray here.
        var cam = View.Camera;
        var (o, d) = cam.Ray(View.CursorX - 0.5f, View.CursorY - 0.5f, _viewW, _viewH);
        // 3D editor bugs round 2 — Ray starts at the NEAR CLIP PLANE, which brackets the scene's sphere; a drawing plane
        // nearer the camera than that (the lower half of a new, empty design's orthographic view) was "behind the camera"
        // and refused the click. The ray starts where the view does: at the eye in perspective, and in orthographic —
        // where every ray is a whole line and the grid is drawn along all of it — far enough back to precede any plane the
        // view can show short of edge-on. Moving the origin along the ray's own direction, in double, moves no hit point.
        double back = Vector3.Dot(o - cam.Eye, d);
        if (cam.Projection == Projection3D.Orthographic)
        {
            double half = cam.Distance * Math.Tan((cam.FovY > 0 ? cam.FovY : Camera3D.DefaultFovY) * 0.5) * Math.Max(1.0, _viewW / Math.Max(1f, _viewH));
            back += 2 * (cam.Distance + cam.SceneRadius) + 2 * half / DrawingPlane.EdgeOnCosine;
        }
        var (x, y, z) = Scene.ToWorld(o);
        return (new CircuitRF.Engine.Em3d.Point3(x - d.X * back, y - d.Y * back, z - d.Z * back), new CircuitRF.Engine.Em3d.Point3(d.X, d.Y, d.Z));
    }

    /// <summary>3D editor bugs round 2 — the nearest visible object under the cursor and where the cursor's ray meets it
    /// (world metres), found on the CPU from the scene itself: exact at the moment of a click, never a pick read back from an
    /// earlier frame. (0, null) off the view or over nothing.</summary>
    public (uint Object, (double X, double Y, double Z)? World) PickUnderCursor()
    {
        if (View.CursorX < 0 || View.CursorY < 0) return (0, null);
        var (id, point) = Scene3DPicking.Pick(Scene, View.Camera, View.CursorX - 0.5f, View.CursorY - 0.5f, _viewW, _viewH, View.Visible, View.Clip);
        return id == 0 ? (0, null) : (id, Scene.ToWorld(point));
    }

    /// <summary>The pane's size, DIPs, as the last resize said.</summary>
    public (float Width, float Height) ViewSize => (_viewW, _viewH);

    /// <summary>Vertex mode's candidate under the cursor (scene-local), for the overlay's dot.</summary>
    public Vector3? HoveredVertex => HoveredItem is { Face: < 0, Object: > 0 } h && SelectMode == Scene3DSelectMode.Vertex ? h.Point : null;

    /// <summary>The selection, in the order it was made.</summary>
    public IReadOnlyList<Scene3DItem> Selection => View.Selection;

    /// <summary>Raised whenever <see cref="Selection"/> changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>How many times Fit ran — gate 8 reads it, since there is no pixel to look at.</summary>
    public int Fits { get; private set; }

    [ObservableProperty] private Scene3DSelectMode _selectMode;
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private string _cycleText = "";

    partial void OnSelectModeChanged(Scene3DSelectMode value)
    {
        View.Mode = value;
        HitCycle.ModeChanged(value);
        // A selection means something only in the mode it was made in.
        HoveredItem = null;
        View.HoveredFace = -1;
        SetSelection([]);
        CycleText = "";
        OnPropertyChanged(nameof(IsObjectMode));
        OnPropertyChanged(nameof(IsFaceMode));
        OnPropertyChanged(nameof(IsVertexMode));
        OnPropertyChanged(nameof(HoveredVertex));
        FrameRequested?.Invoke();
    }

    /// <summary>The toolbar's three exclusive toggles. Unchecking the checked one does nothing: a mode is
    /// always on.</summary>
    public bool IsObjectMode { get => SelectMode == Scene3DSelectMode.Object; set { if (value) SelectMode = Scene3DSelectMode.Object; else OnPropertyChanged(); } }
    public bool IsFaceMode   { get => SelectMode == Scene3DSelectMode.Face;   set { if (value) SelectMode = Scene3DSelectMode.Face;   else OnPropertyChanged(); } }
    public bool IsVertexMode { get => SelectMode == Scene3DSelectMode.Vertex; set { if (value) SelectMode = Scene3DSelectMode.Vertex; else OnPropertyChanged(); } }

    // ── the selection ───────────────────────────────────────────────────────────────────────

    private bool _selectingFromTree;

    /// <summary>Replaces the selection. Duplicates are dropped; order is kept.</summary>
    public void SetSelection(IEnumerable<Scene3DItem> items)
    {
        var list = new List<Scene3DItem>();
        foreach (var i in items) if (i.Object != 0 && !list.Contains(i)) list.Add(i);
        if (list.SequenceEqual(View.Selection)) { RefreshSelectionText(); return; }
        View.Selection = [.. list];
        RefreshSelectionText();
        if (!_selectingFromTree)
        {
            _selectingFromTree = true;
            try { SelectedItem = list.Count > 0 && _items.TryGetValue(list[0].Object, out var it) ? it : null; }
            finally { _selectingFromTree = false; }
        }
        SelectionChanged?.Invoke();
        FrameRequested?.Invoke();
    }

    /// <summary>The selection's objects, each once, in selection order.</summary>
    public IReadOnlyList<Scene3DObject> SelectedObjects()
        => [.. View.Selection.Select(i => Scene.Object(i.Object)).OfType<Scene3DObject>().Distinct()];

    /// <summary>A click: select what is under the cursor (Shift adds or removes it). A click on nothing
    /// clears, unless Shift is held.</summary>
    public void Click(bool shift, KeyModifiers modifiers = KeyModifiers.None, int clickCount = 1)
    {
        HitCycle.Reset();
        CycleText = "";
        // brief-em3d-45 — the drawing has the click first: an armed tool places a point, a plane gesture moves the plane.
        // brief-em3d-46 — a measurement takes the click before anything, and selects nothing.
        if (MeasureActive) { MeasureClick(); return; }
        if (EditHost?.DrawClick(modifiers | (shift ? KeyModifiers.Shift : KeyModifiers.None), clickCount) == true) return;
        var item = HoveredItem;
        if (item is not { } it)
        {
            if (!shift) SetSelection([]);
            return;
        }
        if (shift)
            SetSelection(View.Selection.Contains(it) ? View.Selection.Where(s => s != it) : [.. View.Selection, it]);
        else SetSelection([it]);
        if (_items.TryGetValue(it.Object, out var treeItem)) RevealRequested?.Invoke(treeItem);
    }

    /// <summary>B (<paramref name="direction"/> +1) or Shift+B (−1): the next thing behind, or in front, along
    /// the line of sight through the cursor (R-em3d43-4). True when the key did something.</summary>
    public bool Cycle(int direction)
    {
        if (View.CursorX < 0 || Scene.Objects.Length == 0) return false;
        Scene3DItem? current = View.Selection.Length == 1 ? View.Selection[0] : null;
        var q = new Scene3DRayQuery(View.Camera, View.CursorX, View.CursorY, _viewW, _viewH);
        var scene = Scene;
        var mode = SelectMode;
        var visible = View.Visible;
        var clip = View.Clip;
        var hit = HitCycle.Step(direction, View.CursorX, View.CursorY, mode, scene.Generation, current,
                                () =>
                                {
                                    var hits = RayHits.Collect(scene, q, mode, visible, clip);
                                    // brief-em3d-49 — the air-box faces after every solid face: lowest priority.
                                    if (mode == Scene3DSelectMode.Face)
                                        foreach (var (id, depth, point) in Scene3DPicking.PickLastHits(scene, q.Camera, q.Px, q.Py, q.Width, q.Height, visible))
                                            hits.Add(new Scene3DHit(Scene3DItem.OfFace(id, 0), depth, point));
                                    return hits;
                                });
        if (hit is not { } h)
        {
            CycleText = "Nothing under the cursor to step through.";
            return true;
        }
        SetSelection([h.Item]);
        // R-em3d43-4c: without a readout, cycling feels like a glitch.
        CycleText = $"{Name(h.Item)} · {HitCycle.Index + 1} of {HitCycle.Count}";
        return true;
    }

    private readonly Dictionary<string, string> _renames = new(StringComparer.Ordinal);

    /// <summary>The editor is renaming <paramref name="from"/> to <paramref name="to"/>: the next scene's
    /// object of that name is the same object, and stays selected.</summary>
    public void ExpectRename(string from, string to) => _renames[from] = to;

    /// <summary>The selection's scene objects were renumbered by a regeneration: carry it across by name.</summary>
    private void RemapSelection(Scene3DModel from, Scene3DModel to)
    {
        var renames = new Dictionary<string, string>(_renames, StringComparer.Ordinal);
        _renames.Clear();
        if (View.Selection.Length == 0 && HoveredItem is null) return;
        var byName = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var o in to.Objects) byName.TryAdd(o.Name, o.Id);
        Scene3DItem? Map(Scene3DItem i)
            => from.Object(i.Object) is { } o && byName.TryGetValue(renames.GetValueOrDefault(o.Name, o.Name), out uint id)
               ? i with { Object = id } : null;
        var mapped = View.Selection.Select(Map).OfType<Scene3DItem>().ToList();
        HoveredItem = HoveredItem is { } h ? Map(h) : null;
        View.Selection = [];          // SetSelection compares against it; force the notifications
        SetSelection(mapped);
    }

    // ── hover (the ID pass's answer) ────────────────────────────────────────────────────────

    /// <summary>
    /// Frame loop → UI: the ID pass's (object, face) under the cursor. Sets what a click would select —
    /// in Vertex mode the nearest corner of that face on screen, within the snap radius (R-em3d43-3b) —
    /// and the tooltip. Touches no geometry beyond that one face's corners — and then resolves the snap from
    /// the frame's pick <paramref name="patch"/> (brief-em3d-44; null renders one on the CPU).
    /// </summary>
    internal void OnPicked(uint id, uint face, Vector3 point, bool hit, Scene3DIdPatch? patch = null)
    {
        if (CameraGesture) return;
        int f = face == Scene3DVertex.NoFace ? -1 : (int)face;
        Scene3DItem? item = null;
        // brief-em3d-48 R-em3d48-4a — the dimmed parent around a pushed-in child is under the cursor for the snap and for
        // a drawing-plane pick, never for hover or selection.
        bool selectable = Scene.Object(id)?.Selectable == true;
        // brief-em3d-49 R-em3d49-3b — in Face mode, where nothing pickable is under the cursor, the nearest air-box face
        // the editor offers (picked last: a solid face in front always wins).
        if (id == 0 && SelectMode == Scene3DSelectMode.Face && PickLastUnderCursor() is { } last)
        {
            (id, f, selectable) = (last, 0, true);
        }
        if (id != 0 && selectable)
            item = SelectMode switch
            {
                Scene3DSelectMode.Face => f >= 0 ? Scene3DItem.OfFace(id, f) : null,
                Scene3DSelectMode.Vertex => Scene3DFaces.NearestVertexOnScreen(Scene, id, f, View.Camera, View.CursorX, View.CursorY,
                                                                              _viewW, _viewH, Scene3DSnap.RadiusPixels) is { } v
                                            ? Scene3DItem.OfVertex(id, v) : null,
                _ => Scene3DItem.OfObject(id),
            };
        LastPick = (id, f);
        int hoveredFace = SelectMode == Scene3DSelectMode.Face ? f : -1;
        if (item != HoveredItem || View.HoveredFace != hoveredFace)
        {
            HoveredItem = item;
            View.HoveredFace = hoveredFace;
            OnPropertyChanged(nameof(HoveredVertex));
            FrameRequested?.Invoke();
        }
        OnPicked(selectable ? id : 0, point, hit);
        ResolveSnap(patch);
        // brief-em3d-46 — an operation's preview and a measurement's rubber band follow the cursor from here.
        MeasureFollow();
        CursorResolved?.Invoke();
    }

    /// <summary>brief-em3d-49 — the nearest pick-last object (an air-box face) under the cursor, or null.</summary>
    private uint? PickLastUnderCursor()
    {
        if (View.CursorX < 0 || !Scene.Objects.Any(o => o.PickLast)) return null;
        var hits = Scene3DPicking.PickLastHits(Scene, View.Camera, View.CursorX, View.CursorY, _viewW, _viewH, View.Visible);
        return hits.Count > 0 ? hits[0].Id : null;
    }

    // ── keys (owner decision D3) ────────────────────────────────────────────────────────────

    /// <summary>3D round 1 — a plain left drag orbits only when nothing is waiting for a click: while a tool, an operation
    /// or Measure is armed, Ctrl/Cmd + drag orbits and a plain press is always that gesture's click.</summary>
    public bool OrbitNeedsCommand => MeasureActive || EditHost?.DrawArmed == true;

    /// <summary>
    /// R-em3d43-2a — every 3D pane's keys. Mode keys act only with no modifier and no gesture in progress
    /// (<paramref name="gestureInProgress"/>), so Cmd+V is still paste. Esc with a gesture is the pane's to
    /// cancel (false here); with none it clears the selection. Delete and Backspace delete — in the editor.
    /// </summary>
    public bool HandleKey(Key key, KeyModifiers modifiers, bool gestureInProgress)
    {
        // brief-em3d-45 — the drawing's keys come first: while a tool gesture is in progress a digit opens the typed
        // field instead of choosing a standard view, and Esc cancels the gesture rather than the selection.
        if (!gestureInProgress && EditHost?.DrawKey(key, modifiers) == true) return true;
        bool plain = modifiers == KeyModifiers.None;
        if (key == Key.B && (plain || modifiers == KeyModifiers.Shift)) return Cycle(plain ? +1 : -1);
        if (!plain) return false;
        StandardView3D? standard = key switch
        {
            Key.D1 or Key.NumPad1 => StandardView3D.Iso, Key.D2 or Key.NumPad2 => StandardView3D.Top,
            Key.D3 or Key.NumPad3 => StandardView3D.Front, Key.D4 or Key.NumPad4 => StandardView3D.Right,
            Key.D5 or Key.NumPad5 => StandardView3D.Back, Key.D6 or Key.NumPad6 => StandardView3D.Left,
            Key.D7 or Key.NumPad7 => StandardView3D.Bottom, _ => null,
        };
        if (standard is { } v) { StandardViewCommand.Execute(v); return true; }
        switch (key)
        {
            case Key.O when !gestureInProgress: SelectMode = Scene3DSelectMode.Object; return true;
            case Key.F when !gestureInProgress: SelectMode = Scene3DSelectMode.Face; return true;
            case Key.V when !gestureInProgress: SelectMode = Scene3DSelectMode.Vertex; return true;
            case Key.Home: FitCommand.Execute(null); return true;
            // 3D round 1 — the layout editor's snap keys: S or F3 geometry snap, F9 the grid.
            case Key.S or Key.F3: ToggleGeometrySnap(); return true;
            case Key.F9: ToggleGridSnap(); return true;
            case Key.M when !gestureInProgress: ToggleMeasure(); return true;
            case Key.P: IsPerspective = !IsPerspective; return true;
            case Key.C: ClipEnabled = !ClipEnabled; return true;
            case Key.A: ShowAxisIndicator = !ShowAxisIndicator; return true;
            case Key.Escape:
                if (gestureInProgress) return false;
                // 3D round 1 — Esc unwinds one step at a time: a measurement's points go first (Measure stays armed),
                // then Measure itself, and only then the selection.
                if (MeasureActive && MeasureP1 is not null) { ClearMeasurement(); return true; }
                if (MeasureActive || MeasureP1 is not null) { EndMeasure(); return true; }
                HitCycle.Reset();
                CycleText = "";
                SetSelection([]);
                return true;
            case Key.Delete or Key.Back:
                return EditHost?.DeleteSelection() ?? false;
        }
        return false;
    }

    // ── words ───────────────────────────────────────────────────────────────────────────────

    /// <summary>An object as the status line names it: <c>Box "lid"</c>, or its scene name in the viewer.</summary>
    public string ObjectName(Scene3DObject o)
        => EditHost?.KindOf(o) is { } kind ? $"{kind} \"{o.Name}\"" : $"\"{o.Name}\"";

    /// <summary><c>Face top · Box "lid"</c>, <c>Vertex (x, y, z) · Box "lid"</c> or <c>Box "lid"</c>.</summary>
    public string Name(Scene3DItem item)
    {
        if (Scene.Object(item.Object) is not { } o) return "";
        string owner = ObjectName(o);
        if (item.Face >= 0) return $"Face {o.FaceName(item.Face)} · {owner}";
        if (SelectMode == Scene3DSelectMode.Vertex) return $"Vertex {Point(item.Point)} · {owner}";
        return owner;
    }

    private string Point(Vector3 local)
    {
        var (x, y, z) = Scene.ToWorld(local);
        return $"({FormatLength(x)}, {FormatLength(y)}, {FormatLength(z)})";
    }

    /// <summary>R-em3d43-2c / -6b — what is selected, in the document's display unit: a face's name, area
    /// and normal; a vertex's coordinates; an object's material.</summary>
    private void RefreshSelectionText()
    {
        var sel = View.Selection;
        if (sel.Length == 0) { SelectionText = ""; return; }
        string more = sel.Length > 1 ? $"  (+{sel.Length - 1} more selected)" : "";
        if (sel.Length > Scene3DFramePlan.SelectionLimit)
            more += $" — the first {Scene3DFramePlan.SelectionLimit} are drawn highlighted";
        var first = sel[0];
        string text = Name(first);
        if (Scene.Object(first.Object) is { } o)
        {
            if (first.Face >= 0)
            {
                var (area, normal) = Scene3DFaces.AreaAndNormal(Scene, o.Id, first.Face);
                text += $" · area {FormatArea(area)} · normal " +
                        (normal is { } n ? $"({Num(n.X)}, {Num(n.Y)}, {Num(n.Z)})" : "varies (a curved face)");
            }
            if (o.Material is { } m) text += $" · {m}";
        }
        SelectionText = text + more;
    }

    private static string Num(float v) => (MathF.Abs(v) < 5e-7f ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>An area in the display unit, squared: through the length formatter, so it follows the unit.</summary>
    private string FormatArea(double m2)
    {
        // The formatter spells a LENGTH; an area is its value in unit² — found by formatting 1 m and reading
        // the unit's scale off it would be fragile, so the scale comes from a length of one unit instead.
        string one = FormatLength(1);
        int space = one.LastIndexOf(' ');
        string unit = space > 0 ? one[(space + 1)..] : "m";
        double perMetre = space > 0 && double.TryParse(one[..space], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 1;
        return (m2 * perMetre * perMetre).ToString("G5", CultureInfo.InvariantCulture) + " " + unit + "²";
    }

    /// <summary>Every text that prints a length again, after the display unit changed.</summary>
    public void RefreshLengthTexts()
    {
        RefreshSelectionText();
        if (_lastCursorWorld is { } w) CursorText = $"x {FormatLength(w.X)}   y {FormatLength(w.Y)}   z {FormatLength(w.Z)}";
        CycleText = "";
        OnPropertyChanged(nameof(Status));
    }

    // ── the context menu (R-em3d43-4e: it acts on the current item) ─────────────────────────

    /// <summary>
    /// A right-click: selects what is under the cursor first when it is not selected — unless the selection
    /// came from B at this cursor, which the menu then acts on even though the cursor is over the front face.
    /// Returns the menu for the selection.
    /// </summary>
    public IReadOnlyList<Viewer3DMenuItem> OpenContextMenu()
    {
        if (!HitCycle.Active && HoveredItem is { } h && !View.Selection.Contains(h)) SetSelection([h]);
        return ContextMenuItems();
    }

    /// <summary>The menu for the current selection, per mode. Brief 43 builds the frame and the operations
    /// that need no geometry; briefs 46, 47 and 49 add theirs.</summary>
    public IReadOnlyList<Viewer3DMenuItem> ContextMenuItems()
    {
        var items = new List<Viewer3DMenuItem>();
        var objects = SelectedObjects();
        var host = EditHost;
        if (View.Selection.Length > 0)
        {
            string title = View.Selection.Length == 1 ? Name(View.Selection[0]) : $"{View.Selection.Length} selected";
            items.Add(new Viewer3DMenuItem(title, Enabled: false));
            items.Add(Viewer3DMenuItem.Separator);
        }
        bool any = objects.Count > 0;
        bool inInstance = host is not null && objects.Any(o => host.InstanceOf(o) is not null);
        bool own = host is not null && any && !inInstance;

        if (SelectMode != Scene3DSelectMode.Object && any)
        {
            if (inInstance)
            {
                // Measuring from a die's pad is the point; editing the child from here is refused by
                // construction — the menu does not offer it (R-em3d43-3c).
                if (SelectMode == Scene3DSelectMode.Face)
                    items.Add(new Viewer3DMenuItem("Copy Face as Sheet", Enabled: false, Tip: "Comes with face editing (brief 47)."));
                items.Add(new Viewer3DMenuItem("Select Owning Instance", SelectOwningInstance));
                items.Add(new Viewer3DMenuItem("Push into Cell to Edit", Enabled: false, Tip: "Comes with hierarchy editing (brief 48)."));
            }
            else items.Add(new Viewer3DMenuItem("Select Owning Object", SelectOwningObject));
            items.Add(Viewer3DMenuItem.Separator);
        }

        if (SelectMode == Scene3DSelectMode.Object && own && host is not null)
        {
            items.Add(new Viewer3DMenuItem("Rename…", () => host.ShowProperties(rename: true), Enabled: objects.Count == 1));
            var mats = host.Materials;
            // brief-em3d-53 R-em3d53-5 — ends in New Material…, so a technology with no materials is not a dead end.
            items.Add(new Viewer3DMenuItem("Material",
                Children: [.. mats.Select(m => new Viewer3DMenuItem(m, () => host.SetMaterial(objects, m))),
                           .. mats.Count > 0 ? [Viewer3DMenuItem.Separator] : Array.Empty<Viewer3DMenuItem>(),
                           new Viewer3DMenuItem("New Material…", () => host.NewMaterial(objects),
                               Tip: "Make a material in the technology or one of its libraries, and give it to the selection.")]));
            items.Add(new Viewer3DMenuItem("Assign Material…", () => host.AssignMaterial(objects),
                Tip: "Choose the material from the technology's materials — each with its role and source — or make a new one."));
        }
        if (any)
        {
            items.Add(new Viewer3DMenuItem("Hide", () => Hide(objects)));
            items.Add(new Viewer3DMenuItem("Isolate", () => Isolate(objects)));
        }
        items.Add(new Viewer3DMenuItem("Show All", ShowAll));
        // brief-em3d-46 R-em3d46-6 — in the read-only viewer as in the editor, on anything or nothing.
        items.Add(new Viewer3DMenuItem(MeasureActive ? "End Measure  (M)" : "Measure  (M)", ToggleMeasure));
        if (SelectMode == Scene3DSelectMode.Object && own && host is not null)
            items.Add(new Viewer3DMenuItem("Delete", () => host.DeleteSelection()));
        if (host?.DrawMenuItems().ToList() is { Count: > 0 } draw)
        {
            items.Add(Viewer3DMenuItem.Separator);
            items.AddRange(draw);
        }
        if (host is not null && any)
        {
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Properties", () => host.ShowProperties(rename: false)));
        }
        return items;
    }

    public void SelectOwningObject()
    {
        var ids = View.Selection.Select(i => i.Object).Distinct().ToList();
        SelectMode = Scene3DSelectMode.Object;
        SetSelection(ids.Select(Scene3DItem.OfObject));
    }

    public void SelectOwningInstance()
    {
        if (EditHost is not { } host) return;
        var paths = SelectedObjects().Select(host.InstanceOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        SelectMode = Scene3DSelectMode.Object;
        SetSelection(Scene.Objects.Where(o => host.InstanceOf(o) is { } p && paths.Contains(p)).Select(o => Scene3DItem.OfObject(o.Id)));
    }

    /// <summary>Hide: the editor writes the document's <c>Hidden</c> (undoable); the viewer only draws less.</summary>
    public void Hide(IReadOnlyList<Scene3DObject> objects)
    {
        if (EditHost?.SetHidden(objects, true, objects.Count == 1 ? $"Hide {objects[0].Name}" : $"Hide {objects.Count} objects") == true) return;
        foreach (var o in objects) SetVisibleEverywhere(o.Id, false);
        SetSelection([]);
    }

    public void Isolate(IReadOnlyList<Scene3DObject> objects)
    {
        if (EditHost?.ShowAll(objects) == true) return;
        var keep = objects.Select(o => o.Id).ToHashSet();
        foreach (var o in Scene.Objects.Where(o => o.Pickable)) SetVisibleEverywhere(o.Id, keep.Contains(o.Id));
    }

    public void ShowAll()
    {
        if (EditHost?.ShowAll(null) == true) return;
        foreach (var o in Scene.Objects.Where(o => o.Pickable)) SetVisibleEverywhere(o.Id, true);
    }

    /// <summary>A visibility the view holds (not the document): the tree's checkbox follows.</summary>
    internal void SetVisibleEverywhere(uint id, bool visible)
    {
        SetVisible(id, visible);
        if (_items.TryGetValue(id, out var it)) it.Sync(visible);
    }

    /// <summary>Called from Fit: counted for gate 8.</summary>
    private void CountFit() => Fits++;
}
