// ================================================================
//  EditorRound2ToolbarTests.cs — 3D editor bugs round 2: the scratch 3D design On Launch opens, the Snap
//  control shared with the layout editor, and the toolbar's lower-case VAR units and workspace Run button.
// ================================================================

using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EditorRound2ToolbarTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-c3dr2-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A scratch design at a path in a workspace that has not been written resolves the workspace's technology and draws
    /// its objects as a saved one would; nothing is written until Save As, which Save refuses in its place, and after
    /// the Save As it is an ordinary file-backed design.
    /// </summary>
    [Fact]
    public void ScratchDesign_ResolvesLikeASavedOne_AndWritesNothingUntilSaveAs()
    {
        string ws = Workspace();
        string path = Path.Combine(ws, "Untitled-3D-1.c3d");
        var doc = new C3dDocument { Objects = [Box("b", "Gold")] };
        var vm = Open(path, doc, scratch: true);

        Assert.True(vm.IsScratch);
        Assert.Equal("tech", vm.Elaboration?.Technology?.Name);
        Assert.Empty(vm.Elaboration!.Refusals);
        Assert.NotNull(vm.Save());
        Assert.False(File.Exists(path));

        string saved = Path.Combine(ws, "cell", "3d", "saved.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        Assert.Null(vm.SaveAs(saved));
        Assert.False(vm.IsScratch);
        Assert.True(File.Exists(saved));
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// The Snap control: its ladder is the layout editor's (<see cref="SnapLadder"/>) off the technology's default step,
    /// and a pick or a typed length sets the document's snap step — the grid snap follows, the document is dirty, and
    /// there is no undo entry — without rebuilding the list it was picked from. Zero and nonsense put the step back.
    /// </summary>
    [Fact]
    public void SnapDistance_IsTheLayoutLadder_AndSetsTheSnapStepAsAPreference()
    {
        string ws = Workspace(defaultSnapDbu: 10 * Um);
        string path = Path.Combine(ws, "cell", "3d", "cell.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        C3dPersistence.SaveToFile(path, new C3dDocument { Objects = [Box("b", "Gold")] });
        var vm = Open(path, C3dPersistence.LoadFromFile(path));

        Assert.Equal(SnapLadder.Build(10 * Um, vm.Document.DisplayUnit, vm.Document.DbuPerMicron), vm.SnapLadderOptions);
        Assert.Equal("10 µm", vm.SnapDistanceText);
        var ladder = vm.SnapLadderOptions.ToList();

        vm.CommitSnapDistanceText(vm.SnapLadderOptions[0]);             // 1 µm, the 0.1× rung
        Assert.Equal(1 * Um, vm.Document.SnapDbu);
        Assert.Equal(1 * Um, vm.Viewer.SnapGrid.PitchDbu);
        Assert.Equal("1 µm", vm.SnapDistanceText);
        Assert.True(vm.IsDirty);
        Assert.Equal(0, vm.UndoEntries);
        Assert.Equal(ladder, vm.SnapLadderOptions);

        vm.CommitSnapDistanceText("2.5um");
        Assert.Equal(2500, vm.Document.SnapDbu);

        foreach (string bad in new[] { "0", "abc" })
        {
            vm.CommitSnapDistanceText(bad);
            Assert.Equal(2500, vm.Document.SnapDbu);
            Assert.Equal("2.5 µm", vm.SnapDistanceText);
        }
    }

    /// <summary>The toolbar markup: every VAR units combobox shows its entries lower case, and Simulate is the workspace
    /// toolbar's Run button.</summary>
    [Fact]
    public void Toolbar_VarUnitsAreLowerCase_AndRunIsTheWorkspaceRunButton()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "ThreeD", "C3dEditorView.axaml"));
        foreach (string items in new[] { "C3dEditorViewModel.DefineUnits", "C3dVariablesViewModel.UnitChoices" })
            foreach (var line in xaml.Split('\n').Where(l => l.Contains(items, StringComparison.Ordinal)))
                Assert.Contains("ItemTemplate=\"{StaticResource LowerUnit}\"", line, StringComparison.Ordinal);

        int run = xaml.IndexOf("ViewModel.SimulateActiveCommand", StringComparison.Ordinal);
        Assert.True(run > 0);
        string button = xaml[run..xaml.IndexOf("</Button>", run, StringComparison.Ordinal)];
        Assert.Contains("Kind=\"PlayCircleOutline\"", button, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistPlay", xaml, StringComparison.Ordinal);
    }

    private C3dEditorViewModel Open(string path, C3dDocument doc, bool scratch = false)
    {
        string ws = WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(path))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, doc, () => fake, () => ws, a => a(), scratch: scratch);
        _open.Add(vm);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(30)), "the scene never settled");
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        return vm;
    }

    private static C3dBox Box(string name, string? material)
        => new() { Name = name, Material = material, Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(50 * Um, 50 * Um, 20 * Um) };

    private string Workspace(long defaultSnapDbu = 0)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            DefaultSnapDbu = defaultSnapDbu,
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
