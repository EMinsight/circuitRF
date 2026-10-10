// ================================================================
//  WirePointDragTests.cs — moving a wire's point in Vertex mode: the point holds its depth from the eye instead of falling
//  onto the drawing plane (the grid at z = 0), a press on the point drags it as one gesture (no G, no orbit), a press that
//  stays put only selects it, and Esc steps out of Vertex mode once nothing else is left to cancel.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using Avalonia.Input;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class WirePointDragTests : IDisposable
{
    private const long Um = 1000;
    private const float W = 400, H = 300;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-wiredrag-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public WirePointDragTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void DraggingTheApex_KeepsItsHeight_AndTheWireIsNotRefused()
    {
        var vm = Open();
        int entries = vm.UndoEntries;
        Assert.True(vm.PointDrag(Apex(vm)));
        Assert.IsType<FaceMoveTool>(vm.Tool);
        var (x, y) = Screen(vm, 420, 20, 200);
        HoverAt(vm, x, y);
        Settle(vm);
        Assert.False(vm.Elaboration!.WireRefusals.ContainsKey("w1"));         // the preview: the apex did not fall to the grid
        vm.PointDragRelease(moved: true);
        Settle(vm);
        Assert.Null(vm.Tool);
        Assert.Equal(entries + 1, vm.UndoEntries);
        var apex = Wire(vm).Points[1];
        Assert.InRange(apex.X, 410 * Um, 430 * Um);                          // it went where the cursor went …
        Assert.Equal(20 * Um, apex.Y);                                       // … at its own depth …
        Assert.InRange(apex.Z, 190 * Um, 210 * Um);                          // … and height, not onto the grid at z = 0
        Assert.False(vm.Elaboration!.WireRefusals.ContainsKey("w1"));
        var sel = vm.VertexSelection()!.Value;                               // the highlight went with it, not left behind
        Assert.Equal((1, apex), (sel.Vertex, sel.World));
        var shown = vm.Viewer.Scene.ToWorld(Assert.Single(vm.Viewer.Selection).Point);
        Assert.InRange(shown.X * 1e9, apex.X - 1, apex.X + 1);
    }

    [Fact]
    public void APressThatStaysPut_SelectsThePoint_AndMovesNothing()
    {
        var vm = Open();
        int entries = vm.UndoEntries;
        string before = C3dPersistence.SerializeObject(Wire(vm));
        Assert.True(vm.PointDrag(Apex(vm)));
        vm.PointDragRelease(moved: false);
        Assert.Null(vm.Tool);
        Assert.Equal(entries, vm.UndoEntries);
        Assert.Equal(before, C3dPersistence.SerializeObject(Wire(vm)));
        Assert.Equal(1, vm.VertexSelection()!.Value.Vertex);
    }

    [Fact]
    public void Esc_CancelsTheMove_ThenLeavesVertexMode()
    {
        var vm = Open();
        var v = vm.Viewer;
        Assert.True(vm.PointDrag(Apex(vm)));
        Assert.True(v.HandleKey(Key.Escape, KeyModifiers.None, false));
        Assert.Null(vm.Tool);
        Assert.Equal(Scene3DSelectMode.Vertex, v.SelectMode);
        Assert.True(v.HandleKey(Key.Escape, KeyModifiers.None, false));
        Assert.Equal(Scene3DSelectMode.Object, v.SelectMode);
        Assert.Empty(v.Selection);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static C3dWire Wire(C3dEditorViewModel vm) => vm.Document.Objects.OfType<C3dWire>().First();

    /// <summary>Vertex mode, and the wire's apex as the item a hover over it would name.</summary>
    private static Scene3DItem Apex(C3dEditorViewModel vm)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Vertex;
        return Scene3DItem.OfVertex(vm.SceneObject("w1")!.Id, vm.Viewer.Scene.ToLocal(350e-6, 20e-6, 200e-6));
    }

    private static (float X, float Y) Screen(C3dEditorViewModel vm, double xUm, double yUm, double zUm)
    {
        var (x, y, front) = vm.Viewer.View.Camera.Project(vm.Viewer.Scene.ToLocal(xUm * 1e-6, yUm * 1e-6, zUm * 1e-6), W, H);
        Assert.True(front);
        return (x, y);
    }

    private static void HoverAt(C3dEditorViewModel vm, float x, float y)
    {
        vm.Viewer.Hover(x, y);
        vm.Viewer.OnPicked(0, 0, Vector3.Zero, false, null);
    }

    private C3dEditorViewModel Open()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        var die = new C3dBox { Name = "die", Material = "Gold", Min = new(0, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) };
        var lead = new C3dBox { Name = "lead", Material = "Gold", Min = new(600 * Um, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) };
        var wire = new C3dWire
        {
            Name = "w1", Material = "Gold",
            Points = [new(50 * Um, 20 * Um, 20 * Um), new(350 * Um, 20 * Um, 200 * Um), new(650 * Um, 20 * Um, 20 * Um)],
        };
        C3dPersistence.SaveToFile(path, new C3dDocument { DisplayUnit = LayoutUnit.Um, SnapDbu = 1 * Um, Objects = [die, lead, wire] });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        // Front, framed on the wire: the editor's air box makes Fit frame far more than the 0.7 mm of geometry.
        vm.Viewer.StandardViewCommand.Execute(StandardView3D.Front);
        var sc = vm.Viewer.Scene;
        vm.Viewer.View.Camera.FrameBounds(sc.ToLocal(0, 0, 0), sc.ToLocal(700e-6, 60e-6, 250e-6), W / H);
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");
}
