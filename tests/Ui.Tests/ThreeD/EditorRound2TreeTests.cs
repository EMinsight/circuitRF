// ================================================================
//  EditorRound2TreeTests.cs — the 3D editor's second round of owner feedback, the object tree: it lists by material by
//  default (what has none in its own group, first), or by primitive type; a filter hides tree rows by type or material
//  (never the scene's objects); and the selection survives a change of grouping. Pixels were not seen: these read the
//  view model.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EditorRound2TreeTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r2tree-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public EditorRound2TreeTests()
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
    public void ByDefault_TheTreeListsByMaterial_WithWhatHasNoneFirst()
    {
        var vm = Open(Doc());
        Assert.Equal(C3dTreeGrouping.Material, vm.TreeGrouping);
        Assert.Equal([C3dEditorViewModel.NoMaterialHeader, "Copper", "Fill"], vm.Tree.Select(g => g.Header));
        Assert.Equal(["gnd", "trace"], Names(vm, "Copper"));             // construction order within a group
        Assert.Equal(["bare"], Names(vm, C3dEditorViewModel.NoMaterialHeader));
    }

    [Fact]
    public void ByPrimitiveType_TheTreeListsByKind()
    {
        var vm = Open(Doc());
        vm.TreeGroupingText = "By type";
        Assert.Equal(C3dTreeGrouping.Primitive, vm.TreeGrouping);
        Assert.Equal(["Boxes", "Sheets"], vm.Tree.Select(g => g.Header));
        Assert.Equal(["sub", "bare"], Names(vm, "Boxes"));
    }

    [Fact]
    public void TheFilter_HidesTreeRowsByTypeOrMaterial_ButNotTheScenesObjects()
    {
        var vm = Open(Doc());
        Assert.False(vm.IsTreeFilterActive);
        Assert.Equal(["Boxes", "Sheets"], vm.TypeFilters.Select(f => f.Name));
        Assert.Equal([C3dEditorViewModel.NoMaterialHeader, "Copper", "Fill"], vm.MaterialFilters.Select(f => f.Name));

        vm.MaterialFilters.Single(f => f.Name == "Copper").IsChecked = false;
        Assert.True(vm.IsTreeFilterActive);
        Assert.DoesNotContain(vm.Tree, g => g.Header == "Copper");
        Assert.NotNull(vm.SceneObject("trace"));                         // still drawn

        vm.TypeFilters.Single(f => f.Name == "Boxes").IsChecked = false;
        Assert.Empty(vm.Tree);

        vm.ShowAllTreeRowsCommand.Execute(null);
        Assert.False(vm.IsTreeFilterActive);
        Assert.Equal(4, vm.Tree.SelectMany(g => g.Items).Count());
    }

    [Fact]
    public void TheSelection_SurvivesAChangeOfGrouping_AndStaysTheScenes()
    {
        var vm = Open(Doc());
        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "trace");
        var id = vm.SceneObject("trace")!.Id;

        vm.TreeGrouping = C3dTreeGrouping.Primitive;
        Assert.Equal("trace", vm.SelectedTreeItem?.Name);
        Assert.Contains(vm.Tree.Single(g => g.Header == "Sheets").Items, i => ReferenceEquals(i, vm.SelectedTreeItem));
        Assert.Equal([id], vm.Viewer.Selection.Select(s => s.Object));

        // And a pick in the scene still lands on its node in the regrouped tree.
        vm.Viewer.SetSelection([CircuitRF.Render.Scene3D.Edit.Scene3DItem.OfObject(vm.SceneObject("sub")!.Id)]);
        Assert.Equal("sub", vm.SelectedTreeItem?.Name);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private static List<string> Names(C3dEditorViewModel vm, string header)
        => [.. vm.Tree.Single(g => g.Header == header).Items.Select(i => i.Name)];

    private static C3dDocument Doc()
    {
        C3dRect R(long u, long v, long du, long dv) => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };
        return new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000 * Um, 1000 * Um, 100 * Um) },
                new C3dSheet { Name = "gnd", Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Rect = R(0, 0, 1000, 1000) },
                new C3dSheet { Name = "trace", Material = "Copper", Plane = C3dPlane.XY, Offset = 100 * Um, Rect = R(0, 450, 1000, 100) },
                new C3dBox { Name = "bare", Min = new C3dPoint3(0, 0, 200 * Um), Size = new C3dPoint3(100 * Um, 100 * Um, 100 * Um) },
            ],
        };
    }

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Fill", Epsr = 2 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
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
