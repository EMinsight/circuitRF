// ================================================================
//  EditorRound5TreeTests.cs — the 3D editor's fifth round of owner feedback, the object tree: the air box's hidden state is
//  saved with the document; the header's Show all / Hide all; several rows selected at once (in the order they were
//  selected, which is the canvas's selection and a boolean's Tool/Blank order), with the canvas's own menu; and a canvas
//  multi-selection highlights every row it stands for. Pixels were not seen: these read the view model.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EditorRound5TreeTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r5tree-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public EditorRound5TreeTests()
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
    public void TheAirBoxsTick_IsSavedWithTheDocument_AndUndoable()
    {
        var vm = Open(Doc(), out string path);
        Row(vm, C3dEditorViewModel.AirBoxName).IsVisible = false;
        Assert.True(vm.Document.AirBoxHidden);
        Assert.Null(vm.Save());

        var reopened = Reopen(path);
        Assert.False(reopened.AirBoxShown);
        Assert.False(Row(reopened, C3dEditorViewModel.AirBoxName).IsVisible);
        Assert.False(reopened.Viewer.ShowBoundaryFaces);

        vm.UndoRedo.Undo();                                                // the hide was one undo entry
        Assert.True(vm.AirBoxShown);
        Assert.True(Row(vm, C3dEditorViewModel.AirBoxName).IsVisible);
    }

    [Fact]
    public void HideAll_HidesEveryListedRow_AirBoxToo_AsOneUndoEntry_AndShowAllBringsThemBack()
    {
        var vm = Open(Doc(), out _);
        int entries = vm.UndoEntries;
        vm.HideAllTreeObjectsCommand.Execute(null);
        Assert.All(vm.Document.Objects, o => Assert.True(o.Hidden));
        Assert.False(vm.AirBoxShown);
        Assert.All(vm.Tree.SelectMany(g => g.Items), i => Assert.False(i.IsVisible));
        Assert.Equal(entries + 1, vm.UndoEntries);

        vm.ShowAllTreeObjectsCommand.Execute(null);
        Assert.All(vm.Document.Objects, o => Assert.False(o.Hidden));
        Assert.True(vm.AirBoxShown);

        vm.UndoRedo.Undo();                                                // Show all undone: all hidden again, box too
        Assert.All(vm.Document.Objects, o => Assert.True(o.Hidden));
        Assert.False(vm.AirBoxShown);
    }

    [Fact]
    public void SeveralRows_SelectEveryObjectInTheOrderSelected_AndTheMenuIsTheCanvassWithItsBoolean()
    {
        var vm = Open(Doc(), out _);
        vm.TreeSelectionChanged([], [Row(vm, "b")]);
        vm.TreeSelectionChanged([], [Row(vm, "a")]);                      // Ctrl/Cmd-click adds a second row

        Assert.Equal(["b", "a"], vm.SelectedTreeItems.Select(r => r.Name));
        Assert.Equal(["b", "a"], vm.Viewer.SelectedObjects().Select(o => o.Name));   // first-selected is the Tool (D4)
        Assert.Contains(vm.Viewer.ContextMenuItems(), m => m.Header == "Boolean");

        vm.TreeSelectionChanged([Row(vm, "b")], []);                      // and removes one again
        Assert.Equal(["a"], vm.Viewer.SelectedObjects().Select(o => o.Name));
        Assert.Equal("a", vm.SelectedTreeItem?.Name);
    }

    [Fact]
    public void ACanvasMultiSelection_SelectsEveryRow_AndARebuildKeepsThem()
    {
        var vm = Open(Doc(), out _);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("a")!.Id), Scene3DItem.OfObject(vm.SceneObject("c")!.Id)]);
        Assert.Equal(["a", "c"], vm.SelectedTreeItems.Select(r => r.Name));
        Assert.Equal("a", vm.SelectedTreeItem?.Name);

        vm.TreeGrouping = C3dTreeGrouping.Primitive;                      // a rebuild of the tree
        Assert.Equal(["a", "c"], vm.SelectedTreeItems.Select(r => r.Name));
        Assert.All(vm.SelectedTreeItems, r => Assert.Contains(r, vm.Tree.SelectMany(g => g.Items)));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private static C3dTreeItem Row(C3dEditorViewModel vm, string name) => vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == name);

    private static C3dDocument Doc()
    {
        C3dBox Box(string name, long x) => new() { Name = name, Material = "Copper", Min = new C3dPoint3(x * Um, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, 100 * Um) };
        return new C3dDocument { Objects = [Box("a", 0), Box("b", 50), Box("c", 400)] };
    }

    private C3dEditorViewModel Open(C3dDocument doc, out string path)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return Reopen(path);
    }

    private C3dEditorViewModel Reopen(string path)
    {
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!, ".cws");
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => cws, _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
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
