// brief-em3d-45 — drawing in the 3D editor: the drawing plane and its grid, the tools, the typed field, Extrude.
//
// THE PLANE (R-em3d45-1) is editor state: XY, YZ or XZ at an integer-DBU offset, chosen from the toolbar, the 3D
// menu, a face (Face mode's context menu, or Ctrl/Cmd-click with a tool armed) or a snapped point (Ctrl/Cmd+Shift-
// click moves the offset through it). It is saved per document in the workspace's window state, never in the .c3d.
// The camera never moves it; drawing on it edge-on is refused (R-em3d45-1c).
//
// THE GRID (R-em3d45-2) is the pane's (Viewer3DViewState.DrawingGrid, a shader over one quad). The SNAP grid is
// the same plane at the document's SnapDbu — which is not necessarily the drawn minor spacing, so the status line
// shows both when they differ (R-em3d45-2d).
//
// THE TOOLS (R-em3d45-3) are one class each (Tools/). A click goes to the armed tool with the cursor resolved into a
// C3dDrawInput here; what a tool finishes comes back as ONE object, inserted as one undo entry. Nothing is written
// while a gesture runs, so Esc leaves no entry (gate 8). Arming a tool keeps the selection mode; Esc disarms.
//
// THE TYPED FIELD (R-em3d45-4, §8.2 point 5): while a gesture is in progress a digit opens it — never a dialog —
// prefilled with the current values of the dimensions the next click fixes; Tab moves between them, Enter accepts
// the step as if clicked, Esc returns to the mouse. An entry that does not parse stays in the field, red, and
// nothing is written; an expression is recognised and deferred to brief 51, also in red.

using System.Collections.ObjectModel;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel : IC3dDrawHost
{
    /// <summary>R-em3d45-3 — the Shift+A popup: one letter arms each tool (the owner confirms them at the owner check).</summary>
    public static IReadOnlyList<(C3dToolKind Kind, char Letter, string Icon)> DrawTools { get; } =
    [
        (C3dToolKind.Box, 'B', "CubeUnfolded"),
        (C3dToolKind.Sheet, 'S', "VectorRectangle"),
        (C3dToolKind.Polygon, 'G', "VectorPolygon"),
        (C3dToolKind.Polyline, 'L', "VectorPolyline"),
        (C3dToolKind.Cylinder, 'Y', "Database"),
    ];

    public const string EdgeOnFormat = "the {0} plane is edge-on; orbit or choose another plane";

    private DrawingPlane _plane = DrawingPlane.Default;
    private readonly DrawingGridSettings _grid = new();
    private C3dDrawTool? _tool;
    private (DrawSegment A, DrawSegment B)? _crossing;

    /// <summary>Raised when the Shift+A popup should open at the cursor.</summary>
    public event Action? DrawMenuRequested;

    /// <summary>Raised when the typed field opened and should take the keyboard.</summary>
    public event Action? FieldFocusRequested;

    /// <summary>Raised when the drawing plane changed (the shell stores it in the window state).</summary>
    public event Action? DrawingPlaneChanged;

    // ── the drawing plane (R-em3d45-1) ───────────────────────────────────────────────────────

    public DrawingPlane Plane => _plane;

    public int DbuPerMicron => Document.DbuPerMicron;

    /// <summary>Sets the plane. The camera is not touched; the grid and the snap follow at once.</summary>
    public void SetPlane(DrawingPlane plane)
    {
        if (plane == _plane) { SyncPlaneTexts(); return; }
        _plane = plane;
        ApplySnapGrid();
        SyncPlaneTexts();
        Viewer.RequestFrame();
        DrawingPlaneChanged?.Invoke();
    }

    public bool IsPlaneXY { get => _plane.Plane == C3dPlane.XY; set { if (value) SetPlane(_plane with { Plane = C3dPlane.XY }); else OnPropertyChanged(); } }
    public bool IsPlaneYZ { get => _plane.Plane == C3dPlane.YZ; set { if (value) SetPlane(_plane with { Plane = C3dPlane.YZ }); else OnPropertyChanged(); } }
    public bool IsPlaneXZ { get => _plane.Plane == C3dPlane.XZ; set { if (value) SetPlane(_plane with { Plane = C3dPlane.XZ }); else OnPropertyChanged(); } }

    /// <summary>The offset field, in the display unit (a suffix is honoured). Committed with <see cref="CommitPlaneOffset"/>.</summary>
    [ObservableProperty] private string _planeOffsetText = "0";
    [ObservableProperty] private string? _planeOffsetError;

    /// <summary>Commits the offset field: exact, or red and unchanged.</summary>
    public void CommitPlaneOffset()
    {
        var d = C3dDimension.Parse(PlaneOffsetText, Document.DisplayUnit, Document.DbuPerMicron);
        if (d.Kind != C3dDimensionKind.Value) { PlaneOffsetError = d.Why; return; }
        PlaneOffsetError = null;
        SetPlane(_plane with { OffsetDbu = d.Dbu });
    }

    private void SyncPlaneTexts()
    {
        PlaneOffsetText = C3dDimension.Spell(_plane.OffsetDbu, Document.DisplayUnit, Document.DbuPerMicron);
        PlaneOffsetError = null;
        OnPropertyChanged(nameof(IsPlaneXY));
        OnPropertyChanged(nameof(IsPlaneYZ));
        OnPropertyChanged(nameof(IsPlaneXZ));
        OnPropertyChanged(nameof(PlaneText));
        RefreshGridText();
    }

    /// <summary><c>XY at z = 0 µm</c>.</summary>
    public string PlaneText => $"{_plane.Plane} at {_plane.Normal.ToString().ToLowerInvariant()} = {Length(_plane.OffsetDbu)}";

    /// <summary>R-em3d45-1b — the plane through a face: its axis and offset when the face is axis-aligned; a tilted face is
    /// refused, naming its normal. Returns the refusal, or null.</summary>
    public string? PlaneFromFace(uint objectId, int face)
    {
        var scene = Viewer.Scene;
        if (scene.Object(objectId) is not { } o || face < 0) return "There is no face under the cursor.";
        var (_, normal) = Scene3DFaces.AreaAndNormal(scene, objectId, face);
        if (normal is not { } n) return $"Face {o.FaceName(face)} is curved: a drawing plane lies on a flat face.";
        if (DrawingPlane.AxisOfNormal(n) is not { } axis)
            return $"Face {o.FaceName(face)} is tilted — its normal is ({N(n.X)}, {N(n.Y)}, {N(n.Z)}) — and the drawing planes are XY, YZ and XZ only.";
        if (scene.FeaturesOf(objectId) is not { Table: { } t } fr || face >= t.FaceCount || t.FaceVertexStart[face] == t.FaceVertexStart[face + 1])
            return $"Face {o.FaceName(face)} has no corners to measure.";
        var corner = fr.Vertex(t.FaceVertices[t.FaceVertexStart[face]]);
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        long offset = (long)Math.Round(DrawingPlane.Get(corner, axis) / per, MidpointRounding.AwayFromZero);
        SetPlane(new DrawingPlane(DrawingPlane.PlaneNormalTo(axis), offset));
        StatusMessage = $"Drawing plane: {PlaneText}, from face {o.FaceName(face)} of \"{o.Name}\".";
        return null;

        static string N(float v) => (MathF.Abs(v) < 5e-7f ? 0f : v).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>R-em3d45-1b — the offset moved through the snapped point, keeping the axis.</summary>
    public string? PlaneOffsetFromSnap()
    {
        if (!Viewer.Snap.IsSnap) return "Nothing is snapped: move onto a vertex, an edge or a face centre.";
        var p = ToDocumentPoint(Viewer.Snap);
        SetPlane(_plane with { OffsetDbu = _plane.W(p.Dbu) });
        StatusMessage = $"Drawing plane: {PlaneText}" + (p.Exact ? "." : " (the snapped point was not an exact DBU point; rounded).");
        return null;
    }

    // ── the grid (R-em3d45-2) ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showDrawingGrid = true;
    [ObservableProperty] private string _gridText = "";

    partial void OnShowDrawingGridChanged(bool value)
    {
        _grid.Visible = value;
        Viewer.RequestFrame();
    }

    /// <summary>The pane's grid follows the plane, the document's DBU and its display unit.</summary>
    private void ApplyDrawingGrid()
    {
        _grid.Plane = _plane;
        _grid.DbuPerMicron = Document.DbuPerMicron;
        _grid.Unit = Document.DisplayUnit;
        _grid.Dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        _grid.Visible = ShowDrawingGrid;
        Viewer.View.DrawingGrid = _grid;
    }

    /// <summary>The snap step: the document's, or its technology's default when it states none.</summary>
    public long SnapPitch => Document.SnapDbu > 0 ? Document.SnapDbu : Elaboration?.Technology?.DefaultSnapDbu ?? 0;

    /// <summary>R-em3d45-2d — <c>Grid 10 µm</c>, and <c>· snap 1 µm</c> when the snap step is not the drawn one.</summary>
    public void RefreshGridText()
    {
        if (!ShowDrawingGrid) { GridText = ""; return; }
        var (_, h) = Viewer.ViewSize;
        var s = PlaneGrid.SpacingFor(Viewer.Scene, Viewer.View.Camera, _grid, h);
        if (s.MinorDbu <= 0) { GridText = ""; return; }
        string text = "Grid " + Length(s.MinorDbu);
        long snap = SnapPitch;
        if (snap > 0 && snap != s.MinorDbu) text += " · snap " + Length(snap);
        GridText = text;
    }

    // ── the tools (R-em3d45-3) ───────────────────────────────────────────────────────────────

    /// <summary>The armed tool's kind, or null.</summary>
    public C3dToolKind? ArmedTool => _tool?.Kind;

    public C3dDrawTool? Tool => _tool;

    public bool IsBoxArmed      { get => ArmedTool == C3dToolKind.Box;      set => ArmToggle(C3dToolKind.Box, value); }
    public bool IsSheetArmed    { get => ArmedTool == C3dToolKind.Sheet;    set => ArmToggle(C3dToolKind.Sheet, value); }
    public bool IsPolygonArmed  { get => ArmedTool == C3dToolKind.Polygon;  set => ArmToggle(C3dToolKind.Polygon, value); }
    public bool IsPolylineArmed { get => ArmedTool == C3dToolKind.Polyline; set => ArmToggle(C3dToolKind.Polyline, value); }
    public bool IsCylinderArmed { get => ArmedTool == C3dToolKind.Cylinder; set => ArmToggle(C3dToolKind.Cylinder, value); }

    private void ArmToggle(C3dToolKind kind, bool on)
    {
        if (on) Arm(kind);
        else if (ArmedTool == kind) Disarm();
    }

    /// <summary>Arms a tool (from the toolbar, 3D ▸ Draw or the Shift+A popup). The selection mode is kept.</summary>
    [RelayCommand]
    public void Arm(C3dToolKind kind)
    {
        if (kind == C3dToolKind.Extrude) { Extrude(); return; }
        SetTool(kind switch
        {
            C3dToolKind.Box => new BoxTool(this),
            C3dToolKind.Sheet => new SheetTool(this),
            C3dToolKind.Polygon => new PolygonTool(this),
            C3dToolKind.Polyline => new PolylineTool(this),
            _ => new CylinderTool(this),
        });
    }

    /// <summary>The popup's letter: arms its tool; false for a letter that is none of them.</summary>
    public bool ArmByLetter(char letter)
    {
        foreach (var (kind, l, _) in DrawTools)
            if (char.ToUpperInvariant(letter) == l) { Arm(kind); return true; }
        return false;
    }

    [RelayCommand]
    public void Disarm() => SetTool(null);

    private void SetTool(C3dDrawTool? tool)
    {
        CloseField();
        _crossing = null;
        if (_tool is C3dOperationTool && !ReferenceEquals(_tool, tool)) EndOperation();
        _tool = tool;
        // brief-em3d-46 — one gesture at a time: arming a tool ends a measurement.
        if (tool is not null) Viewer.EndMeasure();
        foreach (string p in new[] { nameof(ArmedTool), nameof(Tool), nameof(IsBoxArmed), nameof(IsSheetArmed), nameof(IsPolygonArmed),
                                     nameof(IsPolylineArmed), nameof(IsCylinderArmed), nameof(ToolPrompt) })
            OnPropertyChanged(p);
        Viewer.RequestFrame();
    }

    /// <summary>What the armed tool asks for next — the status line.</summary>
    public string ToolPrompt => _tool?.Prompt ?? "";

    /// <summary>Undo entries a tool has added — gate 8 counts them.</summary>
    public int ToolCommits { get; private set; }

    /// <summary>The cursor, as a tool reads it: the snap in force and the ray.</summary>
    public C3dDrawInput CursorInput()
    {
        C3dPoint3? snap = null;
        bool exact = false, geometry = false;
        if (Viewer.Snap.IsSnap)
        {
            var p = ToDocumentPoint(Viewer.Snap);
            snap = p.Dbu;
            exact = p.Exact;
            geometry = Viewer.Snap.Kind != Snap3DKind.Grid;
        }
        var ray = Viewer.CursorRay();
        return new C3dDrawInput(snap, exact, geometry, ray?.Origin, ray?.Direction, Viewer.ShiftHeld);
    }

    public C3dPoint3? PlanePoint(in C3dDrawInput input, out string? refusal)
    {
        refusal = null;
        if (input.HasRay && _plane.IsEdgeOn(input.RayDirection!.Value))
        {
            refusal = "Cannot draw: " + string.Format(EdgeOnFormat, _plane.Plane) + ".";
            return null;
        }
        if (input.Snap is { } s) return _plane.Project(s);
        if (input.HasRay && _plane.Hit(input.RayOrigin!.Value, input.RayDirection!.Value, Document.DbuPerMicron) is { } hit)
        {
            double per = C3dLowering.Metres(1, Document.DbuPerMicron);
            long R(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
            return _plane.Project(new C3dPoint3(R(hit.X), R(hit.Y), R(hit.Z)));
        }
        refusal = input.HasRay ? $"The {_plane.Plane} plane is behind the camera." : "Move the cursor over the view.";
        return null;
    }

    public C3dPoint3? FreePoint(in C3dDrawInput input, out string? refusal)
    {
        refusal = null;
        if (input.Snap is { } s && input.SnapOnGeometry) return s;
        return PlanePoint(input, out refusal);
    }

    public long? Along(C3dPoint3 through, C3dAxis axis, in C3dDrawInput input)
    {
        // Near a feature: its own coordinate along the line — the projection of the snap onto it.
        if (input.Snap is { } s && input.SnapOnGeometry) return DrawingPlane.Get(s, axis);
        if (!input.HasRay) return null;
        var p = DrawGeometry.Metres(through, Document.DbuPerMicron);
        if (DrawingPlane.AlongAxisClosestToRay(p, axis, input.RayOrigin!.Value, input.RayDirection!.Value) is not { } t) return null;
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        double w = DrawingPlane.Get(through, axis) + t / per;
        long pitch = Viewer.SnapEnabled && Viewer.SnapGridOn ? SnapPitch : 0;
        return pitch > 0 ? (long)Math.Round(w / pitch, MidpointRounding.AwayFromZero) * pitch : (long)Math.Round(w, MidpointRounding.AwayFromZero);
    }

    /// <summary>The smallest <c>&lt;prefix&gt;&lt;n&gt;</c> no object or instance is called — unique and renamable.</summary>
    public string NextName(string prefix)
    {
        var used = new HashSet<string>(Document.Objects.Select(o => o.Name).Concat(Document.Instances.Select(i => i.Name)), StringComparer.Ordinal);
        for (int n = 1; ; n++)
            if (!used.Contains(prefix + n)) return prefix + n;
    }

    /// <summary>The material every new object gets — the toolbar combo, from the technology's materials.</summary>
    [ObservableProperty] private string? _currentMaterial;

    /// <summary>Keeps the current material one the technology has (the first, until the user picks).</summary>
    private void SyncCurrentMaterial()
    {
        var mats = Materials;
        if (CurrentMaterial is null || !mats.Contains(CurrentMaterial)) CurrentMaterial = mats.Count > 0 ? mats[0] : null;
    }

    public double? ThicknessUmFor(string? material)
    {
        if (material is null || Elaboration?.Technology is not { } tech) return null;
        var layer = tech.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Conductor && l.Material == material && l.ThicknessDbu > 0);
        return layer is null ? null : (double)LayoutUnits.FromDbu(layer.ThicknessDbu, LayoutUnit.Um, Document.DbuPerMicron);
    }

    /// <summary>One tool step's outcome: a finished object is inserted (one undo entry); a refusal goes to the status line.</summary>
    private void Apply(C3dToolStep step)
    {
        _crossing = null;
        if (step.Refusal is { } why)
        {
            StatusMessage = why;
            if (step.Crossing is { } x && _tool is PolygonTool pt) _crossing = pt.CrossingSegments(x);
        }
        else if (step.Advanced) StatusMessage = "";
        if (step.Finished && _tool is C3dOperationTool op) CommitOperation(op);
        if (step.Result is { } obj)
        {
            if (_tool is ExtrudeTool ex) CommitExtrude(ex, obj);
            else
            {
                Push(new C3dEdit($"Draw {C3dObject.KindOf(obj).ToLowerInvariant()} {obj.Name}",
                                 [new C3dEditSlot(false, Document.Objects.Count, null, C3dPersistence.SerializeObject(obj))], ApplySlots));
                ToolCommits++;
                StatusMessage = $"Drew {C3dObject.KindOf(obj).ToLowerInvariant()} \"{obj.Name}\"" + (obj.Material is { } m ? $" in {m}." : ".");
            }
        }
        OnPropertyChanged(nameof(ToolPrompt));
        Viewer.RequestFrame();
    }

    // ── input (IViewer3DEditHost) ────────────────────────────────────────────────────────────

    private static bool Command(KeyModifiers m) => (m & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

    public bool DrawClick(KeyModifiers modifiers, int clickCount)
    {
        if (Command(modifiers))
        {
            // R-em3d45-1b — Ctrl/Cmd+Shift-click: the offset through the snapped point; Ctrl/Cmd-click a face (tool armed).
            if ((modifiers & KeyModifiers.Shift) != 0)
            {
                if (PlaneOffsetFromSnap() is { } why) StatusMessage = why;
                return true;
            }
            if (_tool is null) return false;
            if (PlaneFromFace(Viewer.LastPick.Object, Viewer.LastPick.Face) is { } refusal) StatusMessage = refusal;
            return true;
        }
        if (_tool is null) return false;
        CloseField();
        var input = CursorInput();
        Apply(clickCount >= 2 ? _tool.DoubleClick(input) : _tool.Click(input));
        return true;
    }

    public bool DrawKey(Key key, KeyModifiers modifiers)
    {
        if (key == Key.A && modifiers == KeyModifiers.Shift) { DrawMenuRequested?.Invoke(); return true; }
        // brief-em3d-46 — G, R and Ctrl/Cmd+D start an operation on the selection (no gesture in progress).
        if (_tool is not { InProgress: true } && OperationKey(key, modifiers)) return true;
        if (_tool is not { } tool) return false;
        if (tool is C3dOperationTool opTool && opTool.Key(key, modifiers)) { OperationChanged(); return true; }
        bool plain = modifiers == KeyModifiers.None;
        switch (key)
        {
            case Key.Escape:
                if (tool.InProgress && tool.Kind != C3dToolKind.Extrude && tool is not C3dOperationTool)
                {
                    tool.Reset();
                    _crossing = null;
                    StatusMessage = $"{tool.Name} cancelled.";
                    OnPropertyChanged(nameof(ToolPrompt));
                    Viewer.RequestFrame();
                }
                else
                {
                    StatusMessage = tool.Kind == C3dToolKind.Extrude || tool is C3dOperationTool ? $"{tool.Name} cancelled." : "";
                    Disarm();
                }
                return true;
            case Key.Enter when tool.InProgress:
                Apply(tool.Enter(CursorInput()));
                return true;
            case Key.Back or Key.Delete when tool.InProgress:
                if (tool.Backspace()) { _crossing = null; OnPropertyChanged(nameof(ToolPrompt)); Viewer.RequestFrame(); }
                return true;
            case Key.K when plain && tool is ExtrudeTool ex:
                ex.Keep = !ex.Keep;
                OnPropertyChanged(nameof(ToolPrompt));
                return true;
        }
        if (!tool.InProgress || tool.Dimensions.Count == 0) return false;
        if (key == Key.Tab || (key == Key.OemPlus && plain)) { OpenField(null); return true; }
        if (TypedChar(key, modifiers) is { } c) { OpenField(c.ToString()); return true; }
        return false;
    }

    /// <summary>A key that starts a typed value: a digit, a decimal point or a minus sign.</summary>
    private static char? TypedChar(Key key, KeyModifiers modifiers)
    {
        if (modifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return null;
        if (modifiers == KeyModifiers.None)
        {
            if (key >= Key.D0 && key <= Key.D9) return (char)('0' + (key - Key.D0));
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return (char)('0' + (key - Key.NumPad0));
            if (key is Key.OemPeriod or Key.Decimal) return '.';
            if (key is Key.OemMinus or Key.Subtract) return '-';
        }
        return null;
    }

    public void FillDrawOverlay(Viewer3DDrawOverlay overlay)
    {
        int dbu = Document.DbuPerMicron;
        foreach (var o in Document.Objects)
        {
            if (o is not C3dPolyline l || l.Hidden) continue;
            var points = Points3Of(l);
            var t = l.Placement.IsDefault ? (C3dTransform?)null : l.Placement.ToTransform();
            if (t is { } tr)
                points = [.. points.Select(p => { var (x, y, z) = tr.Apply(p); return new C3dPoint3((long)Math.Round(x), (long)Math.Round(y), (long)Math.Round(z)); })];
            DrawGeometry.Chain(points, l.Closed, dbu, SelectedTreeItem?.Name == l.Name ? overlay.Selected : overlay.Construction);
        }
        if (_tool is { } tool) tool.Preview(CursorInput(), overlay.Rubber, overlay.Fixed);
        if (_tool is C3dOperationTool { ShowsPivot: true } op) overlay.Pivots.Add(DrawGeometry.Metres(op.Pivot, dbu));
        if (_crossing is { } x) { overlay.Crossing.Add(x.A); overlay.Crossing.Add(x.B); }
    }

    private static List<C3dPoint3> Points3Of(C3dPolyline l)
    {
        if (l.Points3 is { } p3) return p3;
        var plane = new DrawingPlane(l.Plane, l.Offset);
        return [.. l.Points.Select(plane.FromUv)];
    }

    public IEnumerable<Viewer3DMenuItem> DrawMenuItems()
    {
        foreach (var item in OperationMenuItems()) yield return item;
        if (Viewer.SelectMode == Scene3DSelectMode.Face && Viewer.Selection is [{ Face: >= 0 } f])
            yield return new Viewer3DMenuItem("Drawing Plane from Face", () => { if (PlaneFromFace(f.Object, f.Face) is { } why) StatusMessage = why; });
        if (ExtrudeSource() is { } src)
            yield return new Viewer3DMenuItem("Extrude", Extrude, Enabled: ExtrudeTool.CannotExtrude(src.Obj) is null,
                                              Tip: ExtrudeTool.CannotExtrude(src.Obj));
    }

    // ── the typed field (R-em3d45-4) ─────────────────────────────────────────────────────────

    private string[] _fieldTexts = [];
    private int _fieldIndex;

    [ObservableProperty] private bool _fieldOpen;
    [ObservableProperty] private string _fieldText = "";
    [ObservableProperty] private string _fieldLabel = "";
    [ObservableProperty] private string? _fieldError;
    [ObservableProperty] private double _fieldX;
    [ObservableProperty] private double _fieldY;

    /// <summary>The dimension the field is on, 0-based.</summary>
    public int FieldIndex => _fieldIndex;

    /// <summary>Opens the field at the cursor, prefilled with each dimension's current value; the first dimension's
    /// text is <paramref name="first"/> when a key opened it (a digit replaces the prefill, Tab or = starts it empty).</summary>
    public void OpenField(string? first)
    {
        if (_tool is not { InProgress: true } tool || tool.Dimensions.Count == 0) return;
        var current = tool.Current(CursorInput());
        _fieldTexts = new string[tool.Dimensions.Count];
        for (int i = 0; i < _fieldTexts.Length; i++)
            _fieldTexts[i] = i < current.Length && current[i] is { } v ? tool.SpellField(i, v, Document.DisplayUnit, Document.DbuPerMicron) : "";
        _fieldTexts[0] = first ?? "";
        _fieldIndex = 0;
        FieldX = Math.Max(0, Viewer.View.CursorX) + 16;
        FieldY = Math.Max(0, Viewer.View.CursorY) + 12;
        FieldError = null;
        ShowFieldIndex();
        FieldOpen = true;
        FieldFocusRequested?.Invoke();
    }

    private bool _showingField;

    private void ShowFieldIndex()
    {
        _showingField = true;
        FieldText = _fieldTexts[_fieldIndex];
        _showingField = false;
        var dims = _tool!.Dimensions;
        string suffix = _tool.FieldSuffix(_fieldIndex) ?? Suffix;
        FieldLabel = dims.Count > 1
            ? $"{dims[_fieldIndex]} ({suffix}) — Tab: {dims[(_fieldIndex + 1) % dims.Count]}"
            : $"{dims[_fieldIndex]} ({suffix})";
    }

    private string Suffix => LayoutUnits.Suffix(Document.DisplayUnit);

    partial void OnFieldTextChanged(string value)
    {
        if (_showingField || !FieldOpen || _fieldIndex >= _fieldTexts.Length) return;
        _fieldTexts[_fieldIndex] = value;
        FieldError = null;
    }

    /// <summary>Tab: the next dimension of the same step.</summary>
    public void FieldTab()
    {
        if (!FieldOpen || _fieldTexts.Length == 0) return;
        _fieldTexts[_fieldIndex] = FieldText;
        _fieldIndex = (_fieldIndex + 1) % _fieldTexts.Length;
        ShowFieldIndex();
    }

    /// <summary>Enter: every dimension parsed exactly and the step taken as if clicked — or, if one does not parse, the
    /// field stays open on it, red, and nothing is written.</summary>
    public void FieldEnter()
    {
        if (!FieldOpen || _tool is not { } tool) return;
        _fieldTexts[_fieldIndex] = FieldText;
        var values = new long?[_fieldTexts.Length];
        for (int i = 0; i < _fieldTexts.Length; i++)
        {
            if (_fieldTexts[i].Trim().Length == 0) continue;                 // left empty: the cursor's value
            var d = tool.ParseField(i, _fieldTexts[i], Document.DisplayUnit, Document.DbuPerMicron);
            if (d.Kind != C3dDimensionKind.Value)
            {
                _fieldIndex = i;
                ShowFieldIndex();
                FieldError = d.Why;
                return;
            }
            values[i] = d.Dbu;
        }
        var step = tool.Typed(values, CursorInput());
        if (!step.Advanced && step.Refusal is { } why) { FieldError = why; return; }
        CloseField();
        Apply(step);
    }

    /// <summary>Esc in the field: back to the mouse; the gesture goes on.</summary>
    public void FieldEscape() => CloseField();

    private void CloseField()
    {
        FieldOpen = false;
        FieldError = null;
        _fieldTexts = [];
        _fieldIndex = 0;
    }

    // ── Extrude (R-em3d45-5) ─────────────────────────────────────────────────────────────────

    /// <summary>What Extrude would act on: the one selected sheet (Object mode), or the polyline selected in the tree.</summary>
    public (C3dObject Obj, int Index)? ExtrudeSource()
    {
        if (SelectedTreeItem is { ObjectIndex: >= 0 } item && item.ObjectIndex < Document.Objects.Count
            && Document.Objects[item.ObjectIndex] is C3dPolyline l)
            return (l, item.ObjectIndex);
        if (Viewer.SelectMode != Scene3DSelectMode.Object) return null;
        var sel = Viewer.SelectedObjects();
        if (sel.Count != 1) return null;
        int i = DocumentIndex(sel[0]);
        return i >= 0 && Document.Objects[i] is C3dSheet s ? (s, i) : null;
    }

    /// <summary>3D ▸ Modify ▸ Extrude, and the context menu's: starts the extrude gesture on the source.</summary>
    [RelayCommand]
    public void Extrude()
    {
        if (ExtrudeSource() is not { } src)
        {
            StatusMessage = "Extrude takes one selected sheet, or a polyline selected in the tree.";
            return;
        }
        if (ExtrudeTool.CannotExtrude(src.Obj) is { } why) { StatusMessage = why; return; }
        SetTool(new ExtrudeTool(this, src.Obj, src.Index));
        StatusMessage = "";
    }

    /// <summary>The extrude's one undo entry: the source replaced by the result (consumed), or the result inserted (kept).</summary>
    private void CommitExtrude(ExtrudeTool ex, C3dObject result)
    {
        string after = C3dPersistence.SerializeObject(result);
        string kind = C3dObject.KindOf(result).ToLowerInvariant();
        if (ex.Keep)
            Push(new C3dEdit($"Extrude to {kind} {result.Name}", [new C3dEditSlot(false, Document.Objects.Count, null, after)], ApplySlots));
        else
        {
            string before = C3dPersistence.SerializeObject(Document.Objects[ex.SourceIndex]);
            Viewer.ExpectRename(Document.Objects[ex.SourceIndex].Name, result.Name);
            Push(new C3dEdit($"Extrude to {kind} {result.Name}", [new C3dEditSlot(false, ex.SourceIndex, before, after)], ApplySlots));
        }
        ToolCommits++;
        StatusMessage = $"Extruded to {kind} \"{result.Name}\"; the source was " + (ex.Keep ? "kept." : "consumed.");
        SetTool(null);
    }
}
