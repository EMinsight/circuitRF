// ================================================================
//  HideSelectionTests.cs — brief-em3d-91's gates, read off the view model and the document (pixels were not seen: Avalonia
//  cannot start from this machine's shell, so the toolbar button's backdrop is checked as the state it binds to):
//    1  three shown boxes: AllVisible; a press hides them as ONE entry and keeps them selected (AllHidden); a press shows them
//    2  two shown and one hidden: Mixed; a press shows all; the next hides all
//    3  a group selected whole: its members follow, a member hidden on its own before included
//    4  a probe and a thermal boundary selected with a box: all three follow, and stay selected to be shown again
//    5  nothing selected: the command cannot execute
//    6  the key: the pane's H reaches the command through the edit host; a text field's H does not
// ================================================================

using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class HideSelectionTests : IDisposable
{
    private const long Um = 1000;
    private const string Tint = "thermal:c/zmax";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em91-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public HideSelectionTests()
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
    public void Gate1_AllVisible_HidesAsOneEntry_KeepsTheSelection_ThenShows()
    {
        var vm = Open();
        Select(vm, "a", "b", "c");
        Assert.Equal(C3dSelectionVisibility.AllVisible, vm.SelectionVisibility);
        Assert.True(vm.IsSelectionAllVisible);
        Assert.Equal("Hide the selection (H)", vm.SelectionVisibilityTip);
        int entries = vm.UndoEntries;

        Press(vm);
        Assert.All(vm.Document.Objects, o => Assert.True(o.Hidden));
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(["a", "b", "c"], vm.SelectedTreeItems.Select(r => r.Name).Order());
        Assert.Equal(C3dSelectionVisibility.AllHidden, vm.SelectionVisibility);
        Assert.Equal("Show the selection (H)", vm.SelectionVisibilityTip);

        Press(vm);
        Assert.All(vm.Document.Objects, o => Assert.False(o.Hidden));
    }

    [Fact]
    public void Gate2_Mixed_ShowsAllFirst_ThenHidesAll()
    {
        var vm = Open(hiddenC: true);
        Select(vm, "a", "b", "c");
        Assert.Equal(C3dSelectionVisibility.Mixed, vm.SelectionVisibility);
        Assert.True(vm.IsSelectionMixed);
        Assert.False(vm.IsSelectionAllVisible);

        Press(vm);
        Assert.All(vm.Document.Objects, o => Assert.False(o.Hidden));
        Press(vm);
        Assert.All(vm.Document.Objects, o => Assert.True(o.Hidden));
    }

    [Fact]
    public void Gate3_AGroupSelectedWhole_ItsMembersFollow_OneHiddenBeforeIncluded()
    {
        var vm = Open(hiddenC: true, group: "g");
        var group = All(vm).Single(r => r.IsGroup);
        vm.SelectedTreeItem = group;
        Assert.Equal(C3dSelectionVisibility.Mixed, vm.SelectionVisibility);     // c, a member, is hidden on its own

        Press(vm);
        Assert.All(vm.Document.Objects, o => Assert.False(o.Hidden));
        Press(vm);
        Assert.All(vm.Document.Objects, o => Assert.True(o.Hidden));
        Assert.True(vm.SelectedTreeItem?.IsGroup);                              // the group's hide kept its row selected
        Press(vm);
        Assert.All(vm.Document.Objects, o => Assert.False(o.Hidden));
    }

    [Fact]
    public void Gate4_AProbeAndAThermalBoundaryWithABox_AllFollow_AndStaySelected()
    {
        var vm = Open(thermal: true);
        Select(vm, "pr", Tint, "a");                                           // a record first: the first row selects nothing in the view
        Assert.Equal(C3dSelectionVisibility.AllVisible, vm.SelectionVisibility);

        Press(vm);
        Assert.True(vm.Document.Objects.Single(o => o.Name == "a").Hidden);
        Assert.False(vm.IsPlaceShown("pr"));
        Assert.False(vm.IsBoundaryShown(Tint));
        Assert.Null(vm.SceneObject(Scene3DBuilder.FaceTintPrefix + Tint));      // the tint left the build …
        Assert.Equal(["a", "pr", Tint], vm.SelectedTreeItems.Select(r => r.Name).Order(StringComparer.Ordinal));   // … its row did not
        Assert.Equal(C3dSelectionVisibility.AllHidden, vm.SelectionVisibility);

        Press(vm);
        Assert.False(vm.Document.Objects.Single(o => o.Name == "a").Hidden);
        Assert.True(vm.IsPlaceShown("pr"));
        Assert.True(vm.IsBoundaryShown(Tint));
    }

    [Fact]
    public void Gate5_NothingSelected_CannotExecute()
    {
        var vm = Open();
        Assert.Equal(C3dSelectionVisibility.None, vm.SelectionVisibility);
        Assert.False(vm.ToggleSelectionVisibilityCommand.CanExecute(null));
        Assert.Equal("Select something to hide or show it", vm.SelectionVisibilityTip);
        Select(vm, "a");
        Assert.True(vm.ToggleSelectionVisibilityCommand.CanExecute(null));
    }

    [Fact]
    public void Gate6_H_ReachesTheCommandThroughTheEditHost_NeverFromATextField()
    {
        var vm = Open();
        Assert.False(vm.Viewer.HandleKey(Key.H, KeyModifiers.None, gestureInProgress: false));   // nothing selected: not taken
        Select(vm, "b");
        Assert.True(vm.Viewer.HandleKey(Key.H, KeyModifiers.None, gestureInProgress: false));
        Settle(vm);
        Assert.True(vm.Document.Objects.Single(o => o.Name == "b").Hidden);
        Assert.False(vm.Viewer.HandleKey(Key.H, KeyModifiers.None, gestureInProgress: true));    // a gesture keeps the keyboard

        // the tree's tunnel asks this: a bare H, and not typed into a text field
        Assert.True(C3dEditorViewModel.IsHideKey(Key.H, KeyModifiers.None, textHasFocus: false));
        Assert.False(C3dEditorViewModel.IsHideKey(Key.H, KeyModifiers.None, textHasFocus: true));
        Assert.False(C3dEditorViewModel.IsHideKey(Key.H, KeyModifiers.Shift, textHasFocus: false));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Three 20 µm Gold boxes a, b, c side by side (c hidden when <paramref name="hiddenC"/>; all three in
    /// <paramref name="group"/> when given). <paramref name="thermal"/> adds a thermal setup with a boundary on c's top face and
    /// a probe 'pr'.</summary>
    private C3dEditorViewModel Open(bool hiddenC = false, string? group = null, bool thermal = false)
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
        C3dBox Box(string name, int i) => new()
        {
            Name = name, Material = "Gold", Group = group, Hidden = hiddenC && name == "c",
            Min = new C3dPoint3(i * 30 * Um, 0, 0), Size = new C3dPoint3(20 * Um, 20 * Um, 20 * Um),
        };
        var doc = new C3dDocument { SnapDbu = 1 * Um, Objects = [Box("a", 0), Box("b", 1), Box("c", 2)] };
        if (thermal)
        {
            doc.Setups = [EmSetupPersistence.ToEmbedded(new EmSetup
            {
                Name = "Hot", Solver3D = Em3dSolver.None, Problem3D = Em3dProblemType.Thermal,
                Thermal = new CemThermal { Boundaries = [new CemThermalBoundary { Face = "c/zmax", Kind = ThermalBoundaryKind.FixedT, TempC = "40" }] },
            })];
            doc.Probes = [new C3dProbe { Name = "pr", Point = new C3dPoint3(10 * Um, 10 * Um, 20 * Um) }];
        }
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Settle(vm);
        return vm;
    }

    /// <summary>Every row, at any depth (a group's members are beneath its row).</summary>
    private static IEnumerable<C3dTreeItem> All(C3dEditorViewModel vm)
    {
        static IEnumerable<C3dTreeItem> Walk(C3dTreeItem i) => i.Children.SelectMany(Walk).Prepend(i);
        return vm.Tree.SelectMany(g => g.Items).SelectMany(Walk);
    }

    /// <summary>The rows named, selected in the tree as a click and Ctrl-clicks would.</summary>
    private static void Select(C3dEditorViewModel vm, params string[] names)
        => vm.TreeSelectionChanged([.. names.Select(n => All(vm).First(r => r.Name == n && !r.IsGroup))]);

    private void Press(C3dEditorViewModel vm)
    {
        vm.ToggleSelectionVisibilityCommand.Execute(null);
        Settle(vm);
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);

    private void Settle(C3dEditorViewModel vm) => Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, "the scene never settled");
}
