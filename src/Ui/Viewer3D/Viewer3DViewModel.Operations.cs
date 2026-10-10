// brief-em3d-46 — what a 3D pane does for the editor's object operations: the drag PREVIEW (per-draw transforms
// on what is already drawn — the document is not touched until the commit), the cursor event an operation
// follows, the Shift state a rotation reads (free rather than 15° steps), and the MOVE GIZMO's hover, press and
// release. The gizmo's geometry and hit test are Render's (GizmoGeometry), so they are tested headlessly.

using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    private bool[] _previewVisible = [];

    /// <summary>Raised after every frame's answer to the cursor (the pick, then the snap) — what an operation's
    /// preview follows. It is the same moment the snap marker moves.</summary>
    public event Action? CursorResolved;

    /// <summary>
    /// R-em3d46-1a — shows <paramref name="preview"/> (null ends it). Only the view's state changes: the next
    /// frame draws the moving objects under the preview's transforms and leaves them out of the ID pass, and the
    /// CPU's snap patch leaves them out too, so the snap finds what lies under the cursor.
    /// </summary>
    public void SetPreview(Scene3DPreview? preview)
    {
        View.Preview = preview;
        if (preview is not null)
        {
            if (_previewVisible.Length != View.Visible.Length) _previewVisible = new bool[View.Visible.Length];
            for (int i = 0; i < _previewVisible.Length; i++) _previewVisible[i] = View.Visible[i] && !preview.IsMoving((uint)i + 1);
        }
        FrameRequested?.Invoke();
    }

    /// <summary>What the ID pass and the snap may find: the visible objects, less a preview's moving ones.</summary>
    private bool[] PickVisible => View.Preview is not null && _previewVisible.Length == View.Visible.Length ? _previewVisible : View.Visible;

    /// <summary>Shift is held (the pointer's modifiers, or the keys) — a rotation turns freely rather than by 15°.</summary>
    public bool ShiftHeld { get; private set; }

    public void SetShiftHeld(bool held)
    {
        if (held == ShiftHeld) return;
        ShiftHeld = held;
        CursorResolved?.Invoke();
    }

    /// <summary>brief-em3d-48 R-em3d48-1b — Ctrl (Cmd on macOS) is held: a placement takes the child's bottom-centre as its
    /// handle instead of its origin.</summary>
    public bool CommandHeld { get; private set; }

    public void SetCommandHeld(bool held)
    {
        if (held == CommandHeld) return;
        CommandHeld = held;
        CursorResolved?.Invoke();
    }

    // ── the move gizmo (R-em3d46-5) ─────────────────────────────────────────────────────────

    private readonly GizmoLayout _gizmoLayout = new();

    /// <summary>The handle under the cursor, drawn highlighted.</summary>
    [ObservableProperty] private GizmoHandle _gizmoHover;

    /// <summary>The handle being dragged, or none — while it is, the gizmo shows that handle only.</summary>
    [ObservableProperty] private GizmoHandle _gizmoActive;

    /// <summary>The gizmo on screen now (DIPs), or null when the editor offers none (no selection, a gesture in
    /// progress, Face or Vertex mode) or its pivot is behind the camera.</summary>
    public GizmoLayout? GizmoNow()
    {
        if (EditHost?.GizmoPivot is not { } p || _viewW < 1 || _viewH < 1) return null;
        return GizmoGeometry.Layout(View.Camera, Scene.ToLocal(p.X, p.Y, p.Z), _viewW, _viewH, _gizmoLayout);
    }

    /// <summary>The hover: which handle, if any, is under (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private void HoverGizmo(float x, float y)
    {
        if (GizmoActive != GizmoHandle.None) return;
        var h = GizmoNow() is { } g ? GizmoGeometry.Hit(g, x, y) : GizmoHandle.None;
        if (h != GizmoHover)
        {
            GizmoHover = h;
            FrameRequested?.Invoke();
        }
    }

    /// <summary>A press: true when it landed on a handle and the editor started the constrained move — the pane
    /// then routes the drag here rather than orbiting.</summary>
    public bool PressGizmo()
    {
        if (GizmoHover == GizmoHandle.None) return PressPoint();
        if (EditHost is not { } host) return false;
        if (!host.GizmoDrag(GizmoHover)) return false;
        GizmoActive = GizmoHover;
        FrameRequested?.Invoke();
        return true;
    }

    /// <summary>A point drag is under way (<see cref="PressPoint"/>): the pane routes it as it routes a gizmo drag.</summary>
    private bool _pointDrag;

    /// <summary>Vertex mode — a press on the vertex under the cursor that the editor drags (a wire's point): the drag moves it
    /// and the release places it, so moving a point is one gesture and never orbits the camera under it.</summary>
    private bool PressPoint()
    {
        if (SelectMode != Scene3DSelectMode.Vertex || HoveredItem is not { Face: < 0, Object: > 0 } vertex || EditHost is not { } host) return false;
        if (!host.PointDrag(vertex)) return false;
        _pointDrag = true;
        FrameRequested?.Invoke();
        return true;
    }

    /// <summary>The release: the move commits where the cursor is. <paramref name="moved"/> — whether the drag went past the
    /// click slop — decides only a point drag's release (a gizmo's handle commits either way).</summary>
    public void ReleaseGizmo(bool moved = true)
    {
        if (_pointDrag)
        {
            _pointDrag = false;
            EditHost?.PointDragRelease(moved);
            FrameRequested?.Invoke();
            return;
        }
        if (GizmoActive == GizmoHandle.None) return;
        GizmoActive = GizmoHandle.None;
        EditHost?.GizmoRelease();
        FrameRequested?.Invoke();
    }

    /// <summary>The drag was lost (the capture went elsewhere): it ends as if Esc were pressed.</summary>
    public void CancelGizmo()
    {
        if (_pointDrag)
        {
            _pointDrag = false;
            EditHost?.PointDragCancel();
            FrameRequested?.Invoke();
            return;
        }
        if (GizmoActive == GizmoHandle.None) return;
        GizmoActive = GizmoHandle.None;
        EditHost?.GizmoCancel();
        FrameRequested?.Invoke();
    }
}
