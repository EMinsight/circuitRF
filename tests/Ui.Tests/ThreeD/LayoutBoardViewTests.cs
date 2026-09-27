// ================================================================
//  LayoutBoardViewTests.cs — designer feedback 02: a board's layout opened in the 3D editor (New 3D View from Layout).
//  The dielectric between its copper layers is drawn, the first scene makes no named edge runs (the cost that kept a
//  large board's view empty for seconds), and the viewport says it is building until it has something to draw.
//  Counters and view-model state only; the GPU is the recording fake.
// ================================================================

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class LayoutBoardViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-board3d-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public LayoutBoardViewTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>A two-layer board has ONE dielectric, which was also its "outermost" — the one the viewer opens hidden — so
    /// the board drew as copper floating in air.</summary>
    [Fact]
    public void ABoardPlacedFromItsLayout_OpensWithItsDielectricShown()
    {
        var vm = Open(BoardView());
        var dielectrics = vm.Viewer.Scene.Objects.Where(o => o.Kind == Scene3DKind.Dielectric).ToList();
        Assert.NotEmpty(dielectrics);
        Assert.All(dielectrics, o => Assert.True(vm.Viewer.View.IsVisible(o.Id), o.Name));
    }

    /// <summary>Named edge runs are made when something reads them, never by the build that puts the first picture up.</summary>
    [Fact]
    public void TheFirstScene_MakesNoNamedEdgeRuns_UntilOneIsRead()
    {
        var vm = Open(BoardView());
        var tables = vm.Viewer.Scene.Objects.Select(o => vm.Viewer.Scene.FeaturesOf(o.Id).Table).OfType<CircuitRF.Render.Scene3D.Edit.Scene3DFeatureTable>()
                       .Distinct().ToList();
        Assert.NotEmpty(tables);
        Assert.All(tables, t => Assert.False(t.NamedMade));
        Assert.NotEmpty(tables[0].Named.Edges);
        Assert.True(tables[0].NamedMade);
    }

    /// <summary>The status pane is hidden by default, so the canvas itself says the first scene is being built.</summary>
    [Fact]
    public void UntilTheFirstSceneLands_TheViewportSaysItIsBuilding()
    {
        var vm = Open(BoardView(), settle: false);
        Assert.True(vm.Viewer.IsBuildingFirstScene);
        Settle(vm);
        Assert.False(vm.Viewer.IsBuildingFirstScene);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A cell with a two-layer board layout (a top trace, a bottom plane) and its 3D view from layout.</summary>
    private string BoardView()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz"));
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = CellFolder.CreateCellFolder(ws, "Board");
        CellCreate.WriteLayoutView(dir, "Board", LayoutPersistence.Deserialize("""
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 1000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": 20000, "X2": 200000, "Y2": 70000 },
                { "$type": "Rect", "Layer": { "Layer": 2, "Datatype": 0 }, "X1": 0, "Y1": 0, "X2": 200000, "Y2": 90000 }
              ],
              "Instances": []
            }
            """));
        string c3d = Path.Combine(CellFolder.SubFolderPath(dir, ViewType.ThreeD), "Board" + C3dPersistence.Extension);
        var made = C3dHierarchy.NewFromLayout(dir, c3d, Path.Combine(ws, ".cws"));
        Assert.NotNull(made.Document);
        return CellCreate.WriteThreeDView(dir, "Board", made.Document!);
    }

    private C3dEditorViewModel Open(string c3d, bool settle = true)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        if (!settle) return vm;
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
}
