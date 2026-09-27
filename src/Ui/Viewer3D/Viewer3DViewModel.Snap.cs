// brief-em3d-44 — snapping in a 3D pane: the patch the frame read back, turned into one snapped point, a
// marker and a status line.
//
// TIMING (R-em3d44-2d). The GPU's ID pass reads an N × N patch on the render thread, in the frame that
// answers the hover; the pane hands it here with the frame (OnPicked), and the query — bounded by the patch,
// never the scene — runs then. Nothing waits for the GPU: until the frame arrives the pane shows the last
// resolved snap, at most a frame behind the cursor. A backend that reads one texel (1 × 1) leaves the patch
// to the CPU (Scene3DIdPatch.Render), which is also what every test runs.
//
// THE RULES THE LAYOUT EDITOR LEARNED (R-em3d44-1) hold here unchanged: geometry beats the grid; what a
// gesture moves never attracts itself (SnapExclusion, set for the life of every gesture); the radius is in
// SCREEN pixels, the layout canvas's own (GeometrySnap.RadiusPixels); and a snap in force is always shown.
//
// ALT / OPTION suspends geometry snap while it is held; the grid still applies. The held state is taken from
// every pointer event's modifiers as well as from the key events, and cleared when the pane loses focus: a
// key-up delivered to another window would otherwise leave it latched, and snapping would silently stop.

using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    private readonly Scene3DIdPatch _cpuPatch = new();

    /// <summary>The query, with its reusable scratch and its last counters.</summary>
    public SnapQuery3D SnapQuery { get; } = new();

    /// <summary>The snap in force, or none (<see cref="Snap3DResult.IsSnap"/> false).</summary>
    public Snap3DResult Snap { get; private set; }

    /// <summary>Queries run — one per frame that answered a hover while snapping was on.</summary>
    public long SnapQueries { get; private set; }

    /// <summary>Whether the last snap came from the CPU's patch (the backend read one texel).</summary>
    public bool SnapUsedCpuPatch { get; private set; }

    /// <summary>The drawing plane's grid (brief 45 owns the plane); off until a host sets it.</summary>
    public Snap3DGrid SnapGrid { get; set; }

    /// <summary>R-snpf-4 — what the gesture in progress moves; null when nothing is being moved.</summary>
    public Snap3DExclusion? SnapExclusion { get; set; }

    /// <summary>Alt / Option is held: geometry snap is suspended, the grid is not.</summary>
    public bool GeometrySnapSuspended { get; private set; }

    [ObservableProperty] private string _snapText = "";
    [ObservableProperty] private bool _snapEnabled;
    [ObservableProperty] private bool _snapVertex = true;
    [ObservableProperty] private bool _snapMidpoint = true;
    [ObservableProperty] private bool _snapEdge = true;
    [ObservableProperty] private bool _snapFaceCentre = true;
    [ObservableProperty] private bool _snapGridOn = true;

    /// <summary>Raised when a toggle changed — the editor stores them per user.</summary>
    public event Action? SnapTogglesChanged;

    partial void OnSnapEnabledChanged(bool value) => SnapToggled();
    partial void OnSnapVertexChanged(bool value) => SnapToggled();
    partial void OnSnapMidpointChanged(bool value) => SnapToggled();
    partial void OnSnapEdgeChanged(bool value) => SnapToggled();
    partial void OnSnapFaceCentreChanged(bool value) => SnapToggled();
    partial void OnSnapGridOnChanged(bool value) => SnapToggled();

    /// <summary>The kinds switched on.</summary>
    public Snap3DKinds SnapKinds
    {
        get => (SnapVertex ? Snap3DKinds.Vertex : 0) | (SnapMidpoint ? Snap3DKinds.Midpoint : 0) | (SnapEdge ? Snap3DKinds.Edge : 0)
             | (SnapFaceCentre ? Snap3DKinds.FaceCentre : 0) | (SnapGridOn ? Snap3DKinds.Grid : 0);
        set
        {
            _applyingKinds = true;
            try
            {
                SnapVertex = (value & Snap3DKinds.Vertex) != 0;
                SnapMidpoint = (value & Snap3DKinds.Midpoint) != 0;
                SnapEdge = (value & Snap3DKinds.Edge) != 0;
                SnapFaceCentre = (value & Snap3DKinds.FaceCentre) != 0;
                SnapGridOn = (value & Snap3DKinds.Grid) != 0;
            }
            finally { _applyingKinds = false; }
            SnapToggled();
        }
    }

    private bool _applyingKinds;

    private const Snap3DKinds GeometryKinds = Snap3DKinds.All & ~Snap3DKinds.Grid;

    /// <summary>The geometry kinds that were on when <see cref="ToggleGeometrySnap"/> last turned them off.</summary>
    private Snap3DKinds _geometryKindsBeforeOff = GeometryKinds;

    /// <summary>
    /// 3D round 1 — S or F3, the layout editor's geometry-snap keys: the geometry kinds (vertex, midpoint, edge, face
    /// centre) go off together and the grid still applies, as geometry snap off does in a layout; pressed again they
    /// come back as they were. With snapping off altogether it turns snapping on with them. The toolbar's own toggles
    /// show the result, and the per-user store keeps it (it is the same toggles).
    /// </summary>
    public void ToggleGeometrySnap()
    {
        var kinds = SnapKinds;
        var restore = _geometryKindsBeforeOff == 0 ? GeometryKinds : _geometryKindsBeforeOff;
        if (!SnapEnabled)
        {
            SnapKinds = kinds | ((kinds & GeometryKinds) == 0 ? restore : 0);
            SnapEnabled = true;
        }
        else if ((kinds & GeometryKinds) != 0)
        {
            _geometryKindsBeforeOff = kinds & GeometryKinds;
            SnapKinds = kinds & ~GeometryKinds;
        }
        else SnapKinds = kinds | restore;
    }

    /// <summary>3D round 1 — F9, the layout editor's grid-snap key: the grid kind on or off (with snapping off
    /// altogether, snapping on with the grid).</summary>
    public void ToggleGridSnap()
    {
        if (!SnapEnabled)
        {
            SnapGridOn = true;
            SnapEnabled = true;
        }
        else SnapGridOn = !SnapGridOn;
    }

    private void SnapToggled()
    {
        if (_applyingKinds) return;
        OnPropertyChanged(nameof(SnapKinds));
        // A toggle answers at once, at the cursor where it is — never on the next mouse move (R-snp-7).
        if (_lastSnapWasCpu || !SnapEnabled) ResolveSnap(null);
        FrameRequested?.Invoke();
        SnapTogglesChanged?.Invoke();
    }

    /// <summary>The pane's patch size for its next frame: N for the snap radius at the display's scale, capped
    /// by what the backend reads back; 1 when snapping is off.</summary>
    internal int PickSizeFor(double pixelsPerDip, int backendMax)
        => SnapEnabled ? Math.Max(1, Math.Min(backendMax, Scene3DIdPatch.SizeFor((float)(GeometrySnap.RadiusPixels * pixelsPerDip)))) : 1;

    /// <summary>Alt / Option pressed or released (a key event, or a pointer event's modifiers).</summary>
    public void SetGeometrySnapSuspended(bool held)
    {
        if (held == GeometrySnapSuspended) return;
        GeometrySnapSuspended = held;
        if (_lastSnapWasCpu) ResolveSnap(null);
        FrameRequested?.Invoke();
    }

    /// <summary>The pane lost focus: every held-key latch is cleared (the latched-key lesson).</summary>
    public void ClearHeldKeys() => SetGeometrySnapSuspended(false);

    private bool _lastSnapWasCpu;

    /// <summary>
    /// The frame arrived: resolve the snap from <paramref name="gpu"/>, the backend's read-back, when it is a
    /// patch of the current scene at least the radius wide — otherwise from a patch rendered on the CPU.
    /// </summary>
    internal void ResolveSnap(Scene3DIdPatch? gpu)
    {
        if (!SnapEnabled || View.CursorX < 0 || Scene.Objects.Length == 0 && !SnapGrid.IsOn)
        {
            SetSnap(default);
            return;
        }
        Scene3DIdPatch patch;
        float radius;
        if (gpu is { Valid: true } g && g.Generation == Scene.Generation
            && g.Size >= Scene3DIdPatch.SizeFor(GeometrySnap.RadiusPixels * g.PixelsPerDip))
        {
            patch = g;
            radius = GeometrySnap.RadiusPixels * g.PixelsPerDip;
            _lastSnapWasCpu = false;
        }
        else
        {
            radius = GeometrySnap.RadiusPixels;
            _cpuPatch.Render(Scene, View.Camera, View.CursorX, View.CursorY, _viewW, _viewH, Scene3DIdPatch.SizeFor(radius),
                             PickVisible, View.Clip);
            patch = _cpuPatch;
            _lastSnapWasCpu = true;
        }
        SnapUsedCpuPatch = _lastSnapWasCpu;
        SnapQueries++;
        var settings = new Snap3DSettings(SnapKinds, radius, GeometrySnapSuspended, SnapGrid);
        SetSnap(SnapQuery.Query(Scene, patch, settings, SnapExclusion, PickVisible, View.Clip));
    }

    private void SetSnap(Snap3DResult snap)
    {
        if (snap == Snap) return;
        Snap = snap;
        SnapText = snap.IsSnap ? SnapKindName(snap.Kind) + " · " + (EditHost?.SnapPointText(snap) ?? WorldText(snap.World)) : "";
        FrameRequested?.Invoke();
    }

    /// <summary>The kind as the status line names it.</summary>
    public static string SnapKindName(Snap3DKind kind) => kind switch
    {
        Snap3DKind.Vertex => "Vertex", Snap3DKind.Midpoint => "Midpoint", Snap3DKind.Edge => "Edge",
        Snap3DKind.FaceCentre => "Face centre", Snap3DKind.Grid => "Grid", _ => "",
    };

    private string WorldText(Point3 p)
        => $"({FormatLength(p.X)}, {FormatLength(p.Y)}, {FormatLength(p.Z)})";

    /// <summary>Forgets the snap — the cursor left the view.</summary>
    private void ClearSnap() => SetSnap(default);
}
