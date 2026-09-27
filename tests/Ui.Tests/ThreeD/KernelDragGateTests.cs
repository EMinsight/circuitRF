// ================================================================
//  KernelDragGateTests.cs — brief-em3d-47 gate 7: a face drag, per mouse move, tessellates ONE object, elaborates no
//  child and writes nothing to the document; the release is one undo entry. Counters only (overview §1n); the GPU is
//  a recording fake. The kernel's own gates are in KernelGateTests.cs.
// ================================================================

using System.Numerics;
using System.Text.Json;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class KernelDragGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-face47-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public KernelDragGateTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Gate7_APushPullDrag_TessellatesOneObjectPerMove_ElaboratesNoChild_AndWritesNothingUntilTheRelease()
    {
        string ws = Workspace();
        WriteC3d(ws, "die", new C3dDocument { Objects = [Box("chip", 0, 0, 0, 20, 20, 5)] });
        string path = WriteC3d(ws, "pkg", new C3dDocument
        {
            SnapDbu = Um,
            Objects = [Box("lid", 0, 0, 0, 60, 40, 20), Box("base", -20, -20, -10, 120, 90, 10)],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../die", Placement = new C3dPlacement { Origin = new C3dPoint3(70 * Um, 0, 0) } }],
        });
        var vm = Open(path);
        var v = vm.Viewer;
        SelectFace(vm, "lid", "zmax");

        Assert.True(vm.DrawKey(Key.N, KeyModifiers.None));
        Assert.IsType<PushPullTool>(vm.Tool);
        string document = C3dPersistence.Serialize(vm.Document);
        long children = vm.ChildrenElaborated, writes = vm.DocumentWrites;
        long objects = vm.ObjectsElaborated, previews = vm.FacePreviews;
        for (int i = 0; i < 24; i++)
        {
            long tessellated = vm.TessellationMisses;
            HoverVm(v, 200 + (i % 5) * 7, 60 + i * 7);
            Settle(vm);
            Assert.InRange(vm.TessellationMisses - tessellated, 0, 1);             // one object, zero others
        }
        long shown = vm.FacePreviews - previews;
        Assert.True(shown >= 5, $"the preview followed the cursor {shown} times");
        Assert.InRange(vm.ObjectsElaborated - objects, 1, shown);                 // the lid, once per new state at most
        Assert.Equal(children, vm.ChildrenElaborated);                            // no child elaborated
        Assert.Equal(writes, vm.DocumentWrites);                                  // no document write …
        Assert.Equal(document, C3dPersistence.Serialize(vm.Document));            // … and the document says so
        Assert.False(vm.IsDirty);

        // Esc: nothing changed, and the document's own lid is drawn again.
        Assert.True(vm.DrawKey(Key.Escape, KeyModifiers.None));
        Settle(vm);
        Assert.Null(vm.Tool);
        Assert.Equal(document, C3dPersistence.Serialize(vm.Document));

        // The release: one entry, one write; the lid is still a box, taller.
        SelectFace(vm, "lid", "zmax");
        vm.DrawKey(Key.N, KeyModifiers.None);
        int entries = vm.UndoEntries;
        vm.OpenField(null);
        vm.FieldText = "15";
        vm.FieldEnter();
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(writes + 1, vm.DocumentWrites);
        var lid = Assert.IsType<C3dBox>(vm.Document.Objects[0]);
        Assert.Equal(new C3dPoint3(60 * Um, 40 * Um, 35 * Um), lid.Size);
        Assert.Equal(children, vm.ChildrenElaborated);
        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(document, C3dPersistence.Serialize(vm.Document));
    }

    [Fact]
    public void AFaceMovedSideways_BecomesAPolyhedronSayingSo_AndSetCoordinatesFoldsWithTheBoundaryFollowing()
    {
        string ws = Workspace();
        string path = WriteC3d(ws, "cell", new C3dDocument
        {
            SnapDbu = Um,
            Objects = [Box("lid", 0, 0, 0, 60, 40, 20)],
            FaceBoundaries = [new C3dFaceBoundary { Object = "lid", Face = "zmax", Kind = Em3dFaceBoundaryKind.Pec }],
        });
        var vm = Open(path);
        var v = vm.Viewer;

        // Move (G) of xmax, typed: 0, 25 µm, 0.
        SelectFace(vm, "lid", "xmax");
        var centre = v.Scene.ToLocal(60 * UmM, 20 * UmM, 10 * UmM);
        var (sx, sy, _) = v.View.Camera.Project(centre, W, H);
        HoverVm(v, sx, sy);
        Assert.True(vm.DrawKey(Key.G, KeyModifiers.None));
        var move = Assert.IsType<FaceMoveTool>(vm.Tool);
        Assert.True(move.InProgress, "the cursor was on the face: its point is the base");
        vm.OpenField(null);
        vm.FieldText = "0"; vm.FieldTab();
        vm.FieldText = "25"; vm.FieldTab();
        vm.FieldText = "0";
        vm.FieldEnter();
        Settle(vm);
        var poly = Assert.IsType<C3dPolyhedron>(vm.Document.Objects[0]);
        Assert.Equal(C3dBox.FaceNameList, poly.FaceNames());
        Assert.Contains("is now a polyhedron (undo to keep it a box)", vm.StatusMessage);

        // Set Coordinates on the top corner over (60, 65): up by 12 µm bends zmax, which folds — and its boundary follows.
        var corner = poly.Vertices.Single(p => p == new C3dPoint3(60 * Um, 65 * Um, 20 * Um));
        v.SelectMode = Scene3DSelectMode.Vertex;
        v.SetSelection([Scene3DItem.OfVertex(vm.SceneObject("lid")!.Id, v.Scene.ToLocal(corner.X * 1e-9, corner.Y * 1e-9, corner.Z * 1e-9))]);
        vm.Properties.Reload();
        Assert.True(vm.Properties.IsVertexEditable);
        vm.Properties.VertexZ = "32";
        vm.Properties.CommitVertex();
        Settle(vm);
        Assert.Equal("", vm.Properties.Error);
        var folded = Assert.IsType<C3dPolyhedron>(vm.Document.Objects[0]);
        Assert.Contains("zmax.0", folded.FaceNames());
        Assert.Equal(["zmax.0", "zmax.1"], vm.Document.FaceBoundaries.Select(b => b.Face));

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(["zmax"], vm.Document.FaceBoundaries.Select(b => b.Face));
        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.IsType<C3dBox>(vm.Document.Objects[0]);
    }

    // ── helpers (OperationsGateTests' shape) ─────────────────────────────────────────────────

    /// <summary>3D editor bugs round 2 — a distance typed quickly, before the field has focus, arrives key by key at the
    /// pane: the keys build the distance ("1", "0" is 10, never a re-opened "0"), a minus sign pulls, and the face moves
    /// by that much from where it is, in the display unit.</summary>
    [Fact]
    public void TypedDistance_KeysReachingThePane_BuildTheNumber_AndMoveRelativeInTheDisplayUnit()
    {
        const long Mil = 25_400;
        string ws = Workspace();
        string path = WriteC3d(ws, "cell", new C3dDocument
        {
            SnapDbu = Um, DisplayUnit = CircuitRF.Design.Layout.LayoutUnit.Mil,
            Objects = [Box("lid", 0, 0, 0, 2540, 2540, 2540)],
        });
        var vm = Open(path);

        SelectFace(vm, "lid", "zmax");
        Assert.True(vm.DrawKey(Key.N, KeyModifiers.None));
        Assert.True(vm.DrawKey(Key.D1, KeyModifiers.None));
        Assert.True(vm.DrawKey(Key.D0, KeyModifiers.None));
        Assert.Equal("10", vm.FieldText);
        vm.FieldEnter();
        Settle(vm);
        Assert.Equal(110 * Mil, Assert.IsType<C3dBox>(vm.Document.Objects[0]).Size.Z);

        SelectFace(vm, "lid", "zmax");
        Assert.True(vm.DrawKey(Key.N, KeyModifiers.None));
        Assert.True(vm.DrawKey(Key.OemMinus, KeyModifiers.None));
        Assert.True(vm.DrawKey(Key.D5, KeyModifiers.None));
        vm.FieldEnter();
        Settle(vm);
        Assert.Equal(105 * Mil, Assert.IsType<C3dBox>(vm.Document.Objects[0]).Size.Z);
    }

    private static C3dBox Box(string name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = "Gold", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private C3dEditorViewModel Open(string c3d)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-20 * UmM, -20 * UmM, -20 * UmM), s.ToLocal(120 * UmM, 90 * UmM, 60 * UmM), W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");

    private static void SelectFace(C3dEditorViewModel vm, string obj, string face)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        var o = vm.SceneObject(obj)!;
        int f = Enumerable.Range(0, o.FaceNames.Count).Single(i => o.FaceNames[i] == face);
        vm.Viewer.SetSelection([Scene3DItem.OfFace(o.Id, f)]);
    }

    private static void HoverVm(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }
}
