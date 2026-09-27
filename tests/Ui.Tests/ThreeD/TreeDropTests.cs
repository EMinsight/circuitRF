// ================================================================
//  TreeDropTests.cs — 3D editor round 3: a Project Tree cell, .c3d or .clay dropped into a 3D view places an
//  instance. The rule (C3dTreeDrop) and the editor's drag-over → drop, headless; no pixel is looked at.
// ================================================================

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Hierarchy;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class TreeDropTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;
    private const string Tech = "pcb-2layer_RO4350B_20mil_1oz";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-treedrop-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public TreeDropTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ACell_PlacesIts3DViewElseItsLayout_AViewFilePlacesThatView_AndItsOwnCellFallsBackToItsLayout()
    {
        string ws = Workspace();
        LayoutCell(ws, "Both");
        C3dCell(ws, "Both", new C3dDocument { Objects = [Box("pad", 0, 0, 0, 10, 10, 2)] });
        LayoutCell(ws, "Flat");
        LayoutCell(ws, "Pkg");
        string pkg = C3dCell(ws, "Pkg", new C3dDocument());

        var both = C3dTreeDrop.Resolve(new CellDragPayload(Path.Combine(ws, "Both")).Serialize(), pkg)!;
        Assert.True(both.Ok, both.Refusal);
        Assert.Equal(C3dInstanceView.ThreeD, both.View);
        Assert.Equal("../../Both", both.CellRef);

        var flat = C3dTreeDrop.Resolve(new CellDragPayload(Path.Combine(ws, "Flat")).Serialize(), pkg)!;
        Assert.Equal(C3dInstanceView.Layout, flat.View);

        string bothLayout = C3dHierarchy.ViewFile(Path.Combine(ws, "Both"), C3dInstanceView.Layout)!;
        Assert.Equal(C3dInstanceView.Layout, C3dTreeDrop.Resolve(new CellViewDragPayload(bothLayout).Serialize(), pkg)!.View);

        // Its own cell: the 3D view would contain itself, so the layout is placed and the note says why.
        var self = C3dTreeDrop.Resolve(new CellDragPayload(Path.Combine(ws, "Pkg")).Serialize(), pkg)!;
        Assert.True(self.Ok, self.Refusal);
        Assert.Equal(C3dInstanceView.Layout, self.View);
        Assert.Contains("contain itself", self.Note, StringComparison.Ordinal);

        // Anything that is not one of the tree's payloads is not ours: no cursor, nothing said.
        Assert.Null(C3dTreeDrop.Resolve("hello", pkg));
        Assert.Null(C3dTreeDrop.Resolve(new WorkspaceFileDragPayload(Path.Combine(ws, "notes.txt")).Serialize(), pkg));
    }

    [Fact]
    public void ItsOwnFile_ANonPrimaryFile_AndALooseFile_AreRefusedWithTheReason()
    {
        string ws = Workspace();
        LayoutCell(ws, "Pkg");
        string pkg = C3dCell(ws, "Pkg", new C3dDocument());

        Assert.Contains("inside itself", C3dTreeDrop.Resolve(new CellViewDragPayload(pkg).Serialize(), pkg)!.Refusal, StringComparison.Ordinal);

        // Two 3D views and no primary named: an instance names a cell and a view kind, never a file.
        C3dCell(ws, "Die", new C3dDocument { Objects = [Box("pad", 0, 0, 0, 10, 10, 2)] });
        string second = Path.Combine(ws, "Die", CellFolder.ThreeDSubFolder, "second.c3d");
        File.Copy(C3dHierarchy.ViewFile(Path.Combine(ws, "Die"), C3dInstanceView.ThreeD)!, second);
        Assert.Contains("not the primary 3D view", C3dTreeDrop.Resolve(new CellViewDragPayload(second).Serialize(), pkg)!.Refusal, StringComparison.Ordinal);

        string loose = Path.Combine(ws, "loose.c3d");
        File.Copy(pkg, loose);
        Assert.Contains("belongs to no cell", C3dTreeDrop.Resolve(new WorkspaceFileDragPayload(loose).Serialize(), pkg)!.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void DragOverArmsThePlacement_TheDropPlacesOneInstanceAsOneUndoEntry_AndLeavingDisarms()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        string pkg = C3dCell(ws, "Pkg", new C3dDocument { Objects = [Box("base", -200, -200, -10, 400, 400, 10)] });
        var vm = Open(pkg);
        string text = new CellDragPayload(Path.Combine(ws, "Die")).Serialize();

        // A drag that leaves without a drop writes nothing and leaves nothing armed.
        Assert.True(vm.Viewer.TreeDragOver(W / 2, H / 2, text, command: false));
        Assert.IsType<PlaceInstanceTool>(vm.Tool);
        vm.Viewer.TreeDragLeave();
        Assert.Null(vm.Tool);
        Assert.Empty(vm.Document.Instances);

        Assert.True(vm.Viewer.TreeDragOver(W / 2, H / 2, text, command: false));
        Assert.True(vm.Viewer.TreeDragOver(W / 2 + 5, H / 2, text, command: false));   // the same drag moving: armed once
        Assert.True(vm.Viewer.TreeDrop(W / 2 + 5, H / 2, text, command: false), vm.StatusMessage);
        var inst = Assert.Single(vm.Document.Instances);
        Assert.Equal("../../Die", inst.CellRef);
        Assert.Equal(C3dInstanceView.Layout, inst.View);
        Assert.Null(vm.Tool);
        Assert.Equal($"Undo \"Place {inst.Name}\"", vm.UndoRedo.UndoDescription);
        vm.UndoRedo.Undo();
        Assert.Empty(vm.Document.Instances);
        Assert.Equal("Undo", vm.UndoRedo.UndoDescription);   // the drag armed and disarmed; only the drop wrote
    }

    [Fact]
    public void ARefusedDrag_AnswersNone_SaysWhy_AndWritesNothing()
    {
        string ws = Workspace();
        LayoutCell(ws, "Pkg");
        string pkg = C3dCell(ws, "Pkg", new C3dDocument { Objects = [Box("base", -200, -200, -10, 400, 400, 10)] });
        var vm = Open(pkg);
        string bytes = File.ReadAllText(pkg);

        string text = new CellViewDragPayload(pkg).Serialize();
        Assert.False(vm.Viewer.TreeDragOver(W / 2, H / 2, text, command: false));
        Assert.Contains("inside itself", vm.ViewportLine, StringComparison.Ordinal);
        Assert.Null(vm.Tool);
        Assert.False(vm.Viewer.TreeDrop(W / 2, H / 2, "not ours", command: false));
        Assert.Empty(vm.Document.Instances);
        Assert.False(vm.IsDirty);
        Assert.Equal(bytes, File.ReadAllText(pkg));
    }

    // ── helpers (HierarchyGateTests' shapes) ─────────────────────────────────────────────────

    private static C3dBox Box(string name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = "Copper", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), ShippedTechnologies.Load(Tech));
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static void LayoutCell(string ws, string cell)
    {
        string dir = Directory.Exists(Path.Combine(ws, cell)) ? Path.Combine(ws, cell) : CellFolder.CreateCellFolder(ws, cell);
        var view = LayoutPersistence.Deserialize("""
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 1000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": 0, "X2": 200000, "Y2": 50000 }
              ],
              "Instances": []
            }
            """);
        CellCreate.WriteLayoutView(dir, cell, view);
    }

    private static string C3dCell(string ws, string cell, C3dDocument doc)
    {
        string dir = Directory.Exists(Path.Combine(ws, cell)) ? Path.Combine(ws, cell) : CellFolder.CreateCellFolder(ws, cell);
        doc.SnapDbu = Um;
        return CellCreate.WriteThreeDView(dir, cell, doc);
    }

    private C3dEditorViewModel Open(string c3d)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
}
