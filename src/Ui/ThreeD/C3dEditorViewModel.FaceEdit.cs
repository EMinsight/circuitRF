// brief-em3d-47 — face and vertex editing in the 3D editor: Move Along Normal (N), Move (G) of a face or a vertex,
// Extrude to New Solid (E), Align to Face, Copy as Sheet, Measure, Measure From, Set Coordinates, and — in Object
// mode — Convert to Polyhedron.
//
// WHAT IS EDITED IS A DOCUMENT OBJECT, BY FACE NAME. A selected scene face is its document object (never an
// instance's part: editing inside an instance is brief 48's push-in) and the NAME the elaboration gave that face,
// so an edit survives a re-elaboration that renumbers faces. A selected vertex is found among the object's own
// vertices by position, through the inverse placement.
//
// A DRAG WRITES NOTHING UNTIL IT COMMITS (R-em3d47-6). Each cursor move asks the gesture's kernel for the edited
// object, and the scene is rebuilt from a snapshot in which that object stands in for the document's own
// (DocumentText): the elaborator lowers one object, the builder tessellates one, the session uploads one object's
// bytes, and no child is elaborated. The commit replaces the object in ONE undo entry — with the document's
// FaceBoundaries, when a fold renamed a face something was attached to. Esc drops the stand-in and nothing changed.
//
// A REFUSED STATE DRAWS RED (R-em3d47-3d): the scene keeps the last state that was a solid, the would-be solid's
// edges are drawn in the overlay's red, and the status bar says why, naming the faces.

using System.Globalization;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Kernel;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel : IC3dFaceHost
{
    // ── IC3dFaceHost ─────────────────────────────────────────────────────────────────────────

    public string LengthText(double dbu) => Length((long)Math.Round(dbu, MidpointRounding.AwayFromZero));

    public (uint Object, int Face) Picked => Viewer.LastPick;

    public C3dFaceCommands.WorldPlane? PlaneOfSceneFace(uint objectId, int face, out string? refusal)
    {
        refusal = null;
        var scene = Viewer.Scene;
        if (scene.Object(objectId) is not { } o || face < 0) { refusal = "There is no face under the cursor."; return null; }
        // The document's own object: its B-rep through its placement — exact where both are.
        if (DocumentIndex(o) is int i and >= 0) return C3dFaceCommands.PlaneOf(Document.Objects[i], o.FaceName(face), out refusal);
        // An instance's part: the scene's own doubles (the elaboration's metres), exact when they are whole DBU.
        var (_, normal) = Scene3DFaces.AreaAndNormal(scene, objectId, face);
        if (normal is not { } n) { refusal = $"Face {o.FaceName(face)} is curved: align to a flat face."; return null; }
        if (scene.FeaturesOf(objectId) is not { Table: { } t } fr || face >= t.FaceCount || t.FaceVertexStart[face] == t.FaceVertexStart[face + 1])
        {
            refusal = $"Face {o.FaceName(face)} has no corners to align to.";
            return null;
        }
        var p = fr.Vertex(t.FaceVertices[t.FaceVertexStart[face]]);
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        static double Snap(float v) => Math.Abs(v) < 1e-6 ? 0 : Math.Abs(Math.Abs(v) - 1) < 1e-6 ? Math.Sign(v) : v;
        bool exact = Elaboration?.Provenance.TryGetValue(o.Name, out var prov) == true && prov.Exact;
        return C3dFaceCommands.Plane(p.X / per, p.Y / per, p.Z / per, Snap(n.X), Snap(n.Y), Snap(n.Z), exact);
    }

    // ── what the selection names ─────────────────────────────────────────────────────────────

    /// <summary>The one selected face, when it is a face of one of this document's own objects.</summary>
    public (int Index, C3dObject Obj, string Face, Scene3DObject Scene, int SceneFace)? FaceSelection()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Face || Viewer.Selection is not [{ Face: >= 0 } item]) return null;
        if (Viewer.Scene.Object(item.Object) is not { } o || DocumentIndex(o) is not (>= 0 and var i)) return null;
        return (i, Document.Objects[i], o.FaceName(item.Face), o, item.Face);
    }

    /// <summary>The one selected vertex: its object's index, the object, the vertex's index among the object's editable
    /// vertices (−1 for a cylinder's cap centre, which does not move), and where it is in the world (DBU, and exact).</summary>
    public (int Index, C3dObject Obj, int Vertex, C3dPoint3 World, bool Exact)? VertexSelection()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Vertex || Viewer.Selection is not [{ Face: < 0 } item]) return null;
        if (Viewer.Scene.Object(item.Object) is not { } o || DocumentIndex(o) is not (>= 0 and var i)) return null;
        var obj = Document.Objects[i];
        var (wx, wy, wz) = Viewer.Scene.ToWorld(item.Point);
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        var t = obj.Placement.ToTransform();
        var (lx, ly, lz) = t.Inverse().Apply(new C3dPoint3(R(wx / per), R(wy / per), R(wz / per)));
        // The scene's floats resolve a part in ~10⁷ of the distance from the scene's origin.
        double reach = 2 + 1e-6 * Math.Sqrt(item.Point.X * item.Point.X + item.Point.Y * item.Point.Y + item.Point.Z * item.Point.Z) / per;
        IReadOnlyList<C3dPoint3> candidates = obj is C3dCylinder c ? C3dFaceEditor.CapCentres(c) : new C3dFaceEditor(obj).Vertices;
        int best = -1;
        double bestD = reach * reach;
        for (int k = 0; k < candidates.Count; k++)
        {
            double dx = candidates[k].X - lx, dy = candidates[k].Y - ly, dz = candidates[k].Z - lz;
            double d = dx * dx + dy * dy + dz * dz;
            if (d <= bestD) { bestD = d; best = k; }
        }
        if (best < 0) return null;
        var (x, y, z) = t.Apply(candidates[best]);
        var world = new C3dPoint3(R(x), R(y), R(z));
        bool exact = t.IsIntegral;
        return (i, obj, obj is C3dCylinder ? -1 : best, world, exact);
    }

    private static long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    private bool HaveFace(out (int Index, C3dObject Obj, string Face, Scene3DObject Scene, int SceneFace) f)
    {
        if (FaceSelection() is { } s) { f = s; return true; }
        f = default;
        StatusMessage = Viewer.SelectMode != Scene3DSelectMode.Face
            ? "Face operations act on a face: switch to Face mode (F) and select one."
            : Viewer.Selection.Count == 1 ? "That face belongs to an instance: its cell is edited in its own window."
                                          : "Select one face of this document's own objects.";
        return false;
    }

    // ── starting the gestures (keys, menus) ──────────────────────────────────────────────────

    /// <summary>R-em3d47-3a — Move Along Normal (N): push/pull the selected face.</summary>
    public void StartPushPull()
    {
        if (!HaveFace(out var f)) return;
        if (C3dFaceFrame.Of(f.Obj, f.Face, CursorWorldDbu()) is not { } frame) { StatusMessage = $"Face {f.Face} has no normal to move along."; return; }
        BeginFaceEdit(new PushPullTool(this, f.Index, f.Obj, f.Face, frame), f.Scene, f.SceneFace);
    }

    /// <summary>R-em3d47-3b — Move (G) in Face mode: the face's vertices follow base → target.</summary>
    public void StartFaceMove()
    {
        if (!HaveFace(out var f)) return;
        if (f.Obj is C3dCylinder) { StatusMessage = C3dFaceEditor.CylinderFreeMove; return; }
        var frame = C3dFaceFrame.Of(f.Obj, f.Face, null);
        var pivot = frame is { } fr ? new C3dPoint3(R(fr.Through.X), R(fr.Through.Y), R(fr.Through.Z)) : default;
        (C3dPoint3, bool)? baseAt = null;
        // With the cursor over the face, the snapped point under it is the base, at once (brief 46's R-em3d46-2a).
        if (Viewer.LastPick.Object == f.Scene.Id && FreePoint(CursorInput(), out _) is { } p)
            baseAt = (p, !Viewer.Snap.IsSnap || ToDocumentPoint(Viewer.Snap).Exact);
        BeginFaceEdit(new FaceMoveTool(this, f.Index, f.Obj, f.Face, pivot, baseAt), f.Scene, f.SceneFace);
    }

    /// <summary>R-em3d47-3e — Move (G) in Vertex mode: the vertex follows base → target, from where it is.</summary>
    public void StartVertexMove()
    {
        if (VertexSelection() is not { } v)
        {
            StatusMessage = "Select one vertex of this document's own objects (Vertex mode, V).";
            return;
        }
        if (v.Vertex < 0) { StatusMessage = C3dFaceEditor.CylinderVertexMove; return; }
        var tool = new FaceMoveTool(this, v.Index, v.Obj, v.Vertex);
        BeginFaceEdit(tool, null, -1);
        _snapExcludedPoints.Add(DrawGeometry.Metres(v.World, Document.DbuPerMicron));
        ApplySnapExclusion();
    }

    /// <summary>R-em3d47-4 — Extrude to New Solid (E).</summary>
    public void StartExtrudeFace()
    {
        if (!HaveFace(out var f)) return;
        if (f.Obj is C3dCylinder && f.Face == "side") { StatusMessage = "A cylinder's side is curved: extrude a cap, or Convert to Polyhedron first."; return; }
        if (C3dFaceFrame.Of(f.Obj, f.Face, null) is not { } frame) { StatusMessage = $"Face {f.Face} has no normal to extrude along."; return; }
        SetTool(new ExtrudeFaceTool(this, f.Index, f.Obj, f.Face, frame));
        StatusMessage = "";
    }

    /// <summary>R-em3d47-4 — Align to Face…: the selected face's object, onto a face picked next.</summary>
    public void StartAlignToFace()
    {
        if (!HaveFace(out var f)) return;
        if (C3dFaceCommands.PlaneOf(f.Obj, f.Face, out var why) is not { } plane) { StatusMessage = why ?? "That face is not flat."; return; }
        var target = new C3dTarget(false, f.Index);
        BeginOperation(new AlignToFaceTool(this, target, PivotOf([target]), plane, f.Face));
    }

    private (double X, double Y, double Z)? CursorWorldDbu()
    {
        if (Viewer.CursorWorld is not { } w) return null;
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        return (w.X / per, w.Y / per, w.Z / per);
    }

    // ── the gesture's preview (R-em3d47-6) ───────────────────────────────────────────────────

    /// <summary>The gesture's edited object, standing in for document object <c>Index</c> in the scene's snapshot.</summary>
    private (int Index, C3dObject Object)? _facePreview;
    private string? _facePreviewText;
    private readonly List<DrawSegment> _faceAttempt = [];
    private (string Object, string Face)? _selectFaceAfterAdopt;

    /// <summary>Scenes a face or vertex gesture asked for — gate 7 reads one tessellation per preview, and no more.</summary>
    public int FacePreviews { get; private set; }

    /// <summary>Face and vertex edits committed.</summary>
    public int FaceEdits { get; private set; }

    private void BeginFaceEdit(C3dFaceEditTool tool, Scene3DObject? scene, int sceneFace)
    {
        CloseArray();
        SetTool(tool);
        _facePreviewText = C3dPersistence.SerializeObject(tool.Editor.Source);
        StatusMessage = "";
        // What the gesture moves never attracts the snap (R-snpf-4): the face, by name, and its corners where they are now.
        if (scene is not null && sceneFace >= 0)
        {
            _snapExcludedFaces.Add((scene.Name, sceneFace));
            if (Viewer.Scene.FeaturesOf(scene.Id) is { Table: { } t } fr && sceneFace < t.FaceCount)
                for (int k = t.FaceVertexStart[sceneFace]; k < t.FaceVertexStart[sceneFace + 1]; k++)
                    _snapExcludedPoints.Add(fr.Vertex(t.FaceVertices[k]));
            ApplySnapExclusion();
        }
        FaceToolChanged();
    }

    /// <summary>The cursor moved (or a key changed the gesture): the kernel's answer for it, shown.</summary>
    private void FaceToolChanged()
    {
        if (_tool is not C3dFaceEditTool ft) return;
        _faceAttempt.Clear();
        var r = ft.Evaluate(CursorInput());
        if (r is { Ok: true, Object: { } obj })
        {
            string text = C3dPersistence.SerializeObject(obj);
            if (text != _facePreviewText)
            {
                _facePreviewText = text;
                _facePreview = (ft.Index, obj);
                FacePreviews++;
                Viewer.Regenerate();
            }
            StatusMessage = (ft as PushPullTool)?.ClampText
                            ?? (r.Converted ? $"'{obj.Name}' becomes a polyhedron on release (a {ft.Editor.Kind} has no field for this)." : "");
        }
        else if (r is { Refusal: { } why })
        {
            StatusMessage = why;
            if (r.Attempt is { } attempt) AttemptEdges(attempt, ft.Editor.Source.Placement.ToTransform(), _faceAttempt);
        }
        OnPropertyChanged(nameof(ToolPrompt));
        Viewer.RequestFrame();
    }

    /// <summary>A refused solid's edges, world metres, for the overlay's red.</summary>
    private void AttemptEdges(C3dBrep b, C3dTransform placement, List<DrawSegment> into)
    {
        var seen = new HashSet<(int, int)>();
        Point3 W(int v)
        {
            var (x, y, z) = placement.Apply(b.Vertices[v]);
            return DrawGeometry.Metres(new C3dPoint3(R(x), R(y), R(z)), Document.DbuPerMicron);
        }
        foreach (var f in b.Faces)
            foreach (var ring in f.Rings())
                for (int i = 0; i < ring.Length; i++)
                {
                    int a = ring[i], c = ring[(i + 1) % ring.Length];
                    if (seen.Add((Math.Min(a, c), Math.Max(a, c)))) into.Add(new DrawSegment(W(a), W(c)));
                }
    }

    /// <summary>The gesture ended without a commit (Esc, another tool): the document's own object is shown again.</summary>
    private void EndFaceEdit()
    {
        _faceAttempt.Clear();
        ClearSnapExclusion();
        _facePreviewText = null;
        if (_facePreview is null) return;
        _facePreview = null;
        Viewer.Regenerate();
    }

    /// <summary>The release: ONE undo entry replacing the object — and the FaceBoundaries a fold renamed.</summary>
    private void CommitFaceEdit(C3dFaceEditTool ft)
    {
        if (ft.Committed is not { Object: { } obj } r) return;
        _facePreview = null;                   // the document is about to say the same thing: no flicker back
        _facePreviewText = null;
        int index = ft.Index;
        string before = C3dPersistence.SerializeObject(Document.Objects[index]);
        string after = C3dPersistence.SerializeObject(obj);
        string? selectFace = ft switch
        {
            PushPullTool p => p.FaceName,
            FaceMoveTool { FaceName: { } n } => n,
            _ => null,
        };
        if (before == after)
        {
            StatusMessage = $"{ft.Name}: nothing moved.";
            SetTool(null);
            Viewer.Regenerate();
            return;
        }
        var boundaries = BoundariesFollowing(obj.Name, r.Folds);
        Push(new C3dEdit(ft.Describe, [new C3dEditSlot(false, index, before, after)], ApplySlots, faceBoundaries: boundaries, setBoundaries: SetBoundaries));
        FaceEdits++;
        if (selectFace is not null) _selectFaceAfterAdopt = (obj.Name, r.Folds.TryGetValue(selectFace, out var pieces) ? pieces[0] : selectFace);
        string text = r.Converted ? $"'{obj.Name}' is now a polyhedron (undo to keep it a {ft.Editor.Kind})." : $"{ft.Name}: {obj.Name}.";
        if (r.Folds.Count > 0)
            text += " Folded to stay planar: " + string.Join("; ", r.Folds.Select(kv => $"{kv.Key} → {string.Join(", ", kv.Value)}")) + ".";
        SetTool(null);
        StatusMessage = text;
    }

    /// <summary>The FaceBoundaries before and after a fold renamed faces of <paramref name="objectName"/>, or null.</summary>
    private (string Before, string After)? BoundariesFollowing(string objectName, IReadOnlyDictionary<string, IReadOnlyList<string>> folds)
    {
        if (C3dFaceCommands.FollowFolds(Document.FaceBoundaries, objectName, folds) is not { } followed) return null;
        return (C3dPersistence.SerializeFaceBoundaries(Document.FaceBoundaries), C3dPersistence.SerializeFaceBoundaries(followed));
    }

    private void SetBoundaries(string json) => Document.FaceBoundaries = C3dPersistence.DeserializeFaceBoundaries(json);

    /// <summary>After a face edit, the edited face is selected again BY NAME: a primitive that became a polyhedron numbers
    /// its faces differently, and a scene carries a selection by face index.</summary>
    private void ReselectFace()
    {
        if (_selectFaceAfterAdopt is not { } want || Viewer.SelectMode != Scene3DSelectMode.Face) { _selectFaceAfterAdopt = null; return; }
        if (_facePreview is not null) return;
        _selectFaceAfterAdopt = null;
        if (SceneObject(want.Object) is not { } o) return;
        for (int f = 0; f < o.FaceNames.Count; f++)
            if (o.FaceNames[f] == want.Face) { Viewer.SetSelection([Scene3DItem.OfFace(o.Id, f)]); return; }
    }

    // ── one-shot commands ────────────────────────────────────────────────────────────────────

    /// <summary>R-em3d47-4 — Copy as Sheet: a new sheet exactly on the selected face.</summary>
    public void CopyFaceAsSheet()
    {
        if (!HaveFace(out var f)) return;
        var sheet = C3dFaceCommands.CopyAsSheet(f.Obj, f.Face, NextName("sheet"), CurrentMaterial, ThicknessUmFor(CurrentMaterial), out var why);
        if (sheet is null) { StatusMessage = why!; return; }
        Push(new C3dEdit($"Copy face {f.Face} of {f.Obj.Name} as sheet {sheet.Name}",
                         [new C3dEditSlot(false, Document.Objects.Count, null, C3dPersistence.SerializeObject(sheet))], ApplySlots));
        FaceEdits++;
        StatusMessage = $"Copied face {f.Face} of '{f.Obj.Name}' as sheet \"{sheet.Name}\"" + (sheet.Material is { } m ? $" in {m}." : ".");
    }

    /// <summary>R-em3d47-4 — Measure: the Properties panel holds the face's area, perimeter and normal, and — with a second
    /// face Shift-clicked — the distance between them when they are parallel.</summary>
    public void MeasureFace()
    {
        ShowProperties(rename: false);
        StatusMessage = "Measure: the face's area, perimeter and normal are in Properties. Shift-click a parallel face for the distance.";
    }

    /// <summary>Vertex mode's Measure From: brief 46's Measure with this vertex as its first point.</summary>
    public void MeasureFromVertex()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Vertex || Viewer.Selection is not [{ Face: < 0 } item])
        {
            StatusMessage = "Select one vertex to measure from.";
            return;
        }
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        if (VertexSelection() is { } v)
            Viewer.StartMeasureFrom(new Viewer3DMeasurePoint(v.World.X * per, v.World.Y * per, v.World.Z * per, v.Exact));
        else
        {
            var (x, y, z) = Viewer.Scene.ToWorld(item.Point);
            Viewer.StartMeasureFrom(new Viewer3DMeasurePoint(x, y, z, false));
        }
        SetTool(null);
    }

    /// <summary>
    /// R-em3d47-3e — Set Coordinates: the selected vertex moved to (x, y, z), world DBU — taken into the object's frame by
    /// the inverse placement — as one undo entry. Null on success, else why not.
    /// </summary>
    public string? SetVertexCoordinates(C3dPoint3 world)
    {
        if (VertexSelection() is not { } v) return "Select one vertex of this document's own objects.";
        if (v.Vertex < 0) return C3dFaceEditor.CylinderVertexMove;
        var t = v.Obj.Placement.ToTransform();
        var (x, y, z) = t.Inverse().Apply(world);
        var local = new C3dPoint3(R(x), R(y), R(z));
        var editor = new C3dFaceEditor(v.Obj);
        var r = editor.MoveVertex(v.Vertex, local);
        if (r is not { Object: { } obj }) return r.Refusal;
        string before = C3dPersistence.SerializeObject(v.Obj), after = C3dPersistence.SerializeObject(obj);
        if (before == after) return null;
        Push(new C3dEdit($"Set a vertex of {v.Obj.Name}", [new C3dEditSlot(false, v.Index, before, after)], ApplySlots,
                         faceBoundaries: BoundariesFollowing(obj.Name, r.Folds), setBoundaries: SetBoundaries));
        FaceEdits++;
        StatusMessage = r.Converted ? $"'{obj.Name}' is now a polyhedron (undo to keep it a {editor.Kind})." : $"Moved a vertex of '{obj.Name}'.";
        return null;
    }

    /// <summary>R-em3d47-2 — Convert to Polyhedron (Object mode): the selected box, prism or cylinder — a cylinder cut into
    /// <paramref name="facets"/> sides, the tessellation's own count unless stated. Never done silently.</summary>
    public void ConvertToPolyhedron(int? facets = null)
    {
        var objects = Viewer.SelectMode == Scene3DSelectMode.Object ? Viewer.SelectedObjects() : [];
        if (objects is not [var o] || DocumentIndex(o) is not (>= 0 and var i))
        {
            StatusMessage = "Convert to Polyhedron acts on one selected object of this document (Object mode, O).";
            return;
        }
        var source = Document.Objects[i];
        var r = C3dFaceEditor.ConvertToPolyhedron(source, facets);
        if (r is not { Object: { } obj }) { StatusMessage = r.Refusal!; return; }
        Push(new C3dEdit($"Convert {source.Name} to a polyhedron",
                         [new C3dEditSlot(false, i, C3dPersistence.SerializeObject(source), C3dPersistence.SerializeObject(obj))], ApplySlots,
                         faceBoundaries: BoundariesFollowing(obj.Name, r.Folds), setBoundaries: SetBoundaries));
        FaceEdits++;
        string kind = C3dObject.KindOf(source).ToLowerInvariant();
        StatusMessage = source is C3dCylinder
            ? $"'{obj.Name}' is now a polyhedron with {((C3dPolyhedron)obj).Faces.Count - 2} sides (undo to keep it a cylinder)."
            : $"'{obj.Name}' is now a polyhedron (undo to keep it a {kind}).";
    }

    // ── keys and menus ───────────────────────────────────────────────────────────────────────

    /// <summary>N and E in Face mode; G in Face and Vertex mode. True when the key was one of them.</summary>
    private bool FaceKey(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None) return false;
        switch (Viewer.SelectMode)
        {
            case Scene3DSelectMode.Face when key == Key.N: StartPushPull(); return true;
            case Scene3DSelectMode.Face when key == Key.E: StartExtrudeFace(); return true;
            case Scene3DSelectMode.Face when key == Key.G: StartFaceMove(); return true;
            case Scene3DSelectMode.Vertex when key == Key.G: StartVertexMove(); return true;
        }
        return false;
    }

    /// <summary>The Face-mode and Vertex-mode context-menu operations, and Object mode's Convert to Polyhedron.</summary>
    private IEnumerable<Viewer3DMenuItem> FaceMenuItems()
    {
        const string Ports = "Comes with simulating from the document (brief 49).";
        if (FaceSelection() is { } f)
        {
            bool cyl = f.Obj is C3dCylinder;
            string? copyWhy = null;
            if (C3dFaceCommands.Polygon(f.Obj, f.Face, out var why) is not var (_, aligned)) copyWhy = why;
            else if (aligned is null) copyWhy = "A tilted face: sheets lie on XY, YZ or XZ in this version.";
            yield return new Viewer3DMenuItem("Move Along Normal  (N)", StartPushPull);
            yield return new Viewer3DMenuItem("Move  (G)", StartFaceMove, Enabled: !cyl, Tip: cyl ? C3dFaceEditor.CylinderFreeMove : null);
            yield return new Viewer3DMenuItem("Extrude to New Solid  (E)", StartExtrudeFace, Enabled: !(cyl && f.Face == "side"),
                                              Tip: cyl && f.Face == "side" ? "A cylinder's side is curved." : "Grows a new solid from the face; the source is unchanged.");
            yield return new Viewer3DMenuItem("Align to Face…", StartAlignToFace, Enabled: !(cyl && f.Face == "side"),
                                              Tip: "Then click the face to align with: T toggles Touching and Flush.");
            yield return new Viewer3DMenuItem("Copy as Sheet", CopyFaceAsSheet, Enabled: copyWhy is null, Tip: copyWhy);
            yield return new Viewer3DMenuItem("Measure", MeasureFace, Tip: "Area, perimeter and normal in Properties; Shift-click a parallel face for the distance.");
            yield return new Viewer3DMenuItem("Make Port…", Enabled: false, Tip: Ports);
            yield return new Viewer3DMenuItem("Boundary", Enabled: false, Tip: Ports);
        }
        else if (VertexSelection() is { } v)
        {
            bool fixedPoint = v.Vertex < 0;
            yield return new Viewer3DMenuItem("Move  (G)", StartVertexMove, Enabled: !fixedPoint, Tip: fixedPoint ? C3dFaceEditor.CylinderVertexMove : null);
            yield return new Viewer3DMenuItem("Set Coordinates…", () => ShowProperties(rename: false), Enabled: !fixedPoint,
                                              Tip: fixedPoint ? C3dFaceEditor.CylinderVertexMove : "Typed, in Properties.");
            yield return new Viewer3DMenuItem("Measure From", MeasureFromVertex);
        }
        else if (Viewer.SelectMode == Scene3DSelectMode.Vertex && Viewer.Selection.Count == 1)
            yield return new Viewer3DMenuItem("Measure From", MeasureFromVertex);
        else if (Viewer.SelectMode == Scene3DSelectMode.Object && Viewer.SelectedObjects() is [var o] && DocumentIndex(o) is >= 0 and var i)
        {
            switch (Document.Objects[i])
            {
                case C3dBox or C3dPrism:
                    yield return new Viewer3DMenuItem("Convert to Polyhedron", () => ConvertToPolyhedron(),
                                                      Tip: "Makes every vertex editable; the face names are kept.");
                    break;
                case C3dCylinder:
                    yield return new Viewer3DMenuItem("Convert to Polyhedron", Tip: "Cut into flat sides: how many?", Children:
                    [
                        .. new[] { 8, 16, Em3dTessellation.CylinderSegments, 64 }.Distinct().Select(n => new Viewer3DMenuItem(
                            n == Em3dTessellation.CylinderSegments ? $"{n} sides (as drawn)" : $"{n} sides", () => ConvertToPolyhedron(n))),
                    ]);
                    break;
            }
        }
    }

    /// <summary>3D ▸ Modify's face and vertex items, by name.</summary>
    private bool RunFaceModify(string which)
    {
        switch (which)
        {
            case "PushPull": StartPushPull(); return true;
            case "FaceMove": StartFaceMove(); return true;
            case "ExtrudeFace": StartExtrudeFace(); return true;
            case "AlignFace": StartAlignToFace(); return true;
            case "CopySheet": CopyFaceAsSheet(); return true;
            case "MeasureFace": MeasureFace(); return true;
            case "VertexMove": StartVertexMove(); return true;
            case "MeasureFrom": MeasureFromVertex(); return true;
            case "ConvertPoly": ConvertToPolyhedron(); return true;
        }
        if (which.StartsWith("ConvertPoly", StringComparison.Ordinal)
            && int.TryParse(which["ConvertPoly".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            ConvertToPolyhedron(n);
            return true;
        }
        return false;
    }

    /// <summary>The overlay's red: a refused state's would-be solid.</summary>
    private void FillFaceOverlay(Viewer3DDrawOverlay overlay)
    {
        if (_tool is C3dFaceEditTool) overlay.Crossing.AddRange(_faceAttempt);
    }
}
