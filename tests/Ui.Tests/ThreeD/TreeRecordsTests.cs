// ================================================================
//  TreeRecordsTests.cs — brief-em3d-90's gates, read off the view model, the document and the scene (pixels were not seen:
//  Avalonia cannot start from this machine's shell):
//    1  Hide all hides every row — objects, the plot, the places, the boundary, the plane, the air box — as ONE entry, and
//       undo restores every saved state
//    2  Hide all closes the section a ClipPlane plot opened (the owner's "a field plot still showing")
//    3  a thermal boundary's tick leaves its tint out of the build, and puts it back
//    4  a symmetry plane's row: drawn in the selection's colour, its Inspector page, At refused inside the extent with the
//       rule's sentence and accepted on the other end as one entry
//    5  a boundary's tint selects both ways; B steps to the solid and Shift+B back; hidden, B never lands on it; the
//       Inspector's h edit is one entry
//    6  check (C3dThermal.Places) reports a plane inside the extent, in the same words
// ================================================================

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class TreeRecordsTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;
    private const string Tint = "thermal:cap/zmax";
    // Off the view's centre, which a top view puts exactly on the diagonal a square face is split along.
    private const float X = W / 2 + 7, Y = H / 2 + 3;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em90-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public TreeRecordsTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. Hide all ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_HideAll_HidesEveryRow_AsOneEntry_AndUndoRestoresTheSavedStates()
    {
        var vm = Open();
        string saved = C3dPersistence.Serialize(vm.Document);
        int entries = vm.UndoEntries;

        vm.HideAllTreeObjectsCommand.Execute(null);
        Settle(vm);

        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.All(vm.Document.Objects, o => Assert.True(o.Hidden));
        Assert.All(vm.Document.FieldPlots, p => Assert.True(p.Hidden));
        Assert.False(vm.AirBoxShown);
        foreach (string place in (string[])["src", "pr", "fine", "via"]) Assert.False(vm.IsPlaceShown(place), place);
        Assert.False(vm.IsBoundaryShown(Tint));
        Assert.False(vm.IsSymmetryPlaneShown(C3dAxis.X));
        // every row's tick agrees with its own state
        Assert.All(Rows(vm), r => Assert.False(r.IsVisible, $"{r.Kind} {r.Name}"));

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(saved, C3dPersistence.Serialize(vm.Document));    // objects, the plot and the air box, all at once
    }

    // ── 2. the field plot the owner still saw ────────────────────────────────────────────────

    /// <summary>
    /// The field itself went (Viewer.Plot was null — FieldPlotTests gate 6 already held that), but the SECTION the ClipPlane plot
    /// had opened (SyncSectionTo sets ClipEnabled) stayed on: the model was left cut open on the plot's plane, which reads as a
    /// plot. Candidates (b) — a temperature drawn outside the plot records — and (c) — a setup's view keeping its plots
    /// elsewhere — were ruled out by reading: every temperature gesture writes a FieldPlots record (brief 83), and a setup's
    /// view keeps its session plots in its own document's FieldPlots. This failed before the fix (ClipEnabled stayed true).
    /// </summary>
    [Fact]
    public void Gate2_HideAll_ClosesTheSectionAClipPlanePlotOpened()
    {
        var vm = Open(plotShown: true);
        Assert.True(vm.Viewer.ClipEnabled);                 // the plot opened it
        vm.HideAllTreeObjectsCommand.Execute(null);
        Settle(vm);
        Assert.Null(vm.Viewer.Plot);
        Assert.False(vm.Viewer.ClipEnabled);
    }

    // ── 3. a boundary's tick ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_ABoundarysTick_LeavesItsTintOutOfTheBuild_AndPutsItBack()
    {
        var vm = Open();
        var row = Row(vm, Tint);
        Assert.Contains(Tint, vm.ThermalTintNames);
        row.IsVisible = false;
        Settle(vm);
        Assert.DoesNotContain(Tint, vm.ThermalTintNames);
        Assert.Null(vm.SceneObject(Scene3DBuilder.FaceTintPrefix + Tint));
        Row(vm, Tint).IsVisible = true;
        Settle(vm);
        Assert.Contains(Tint, vm.ThermalTintNames);
    }

    // ── 4. a symmetry plane ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_ASymmetryPlanesRow_IsDrawnSelected_AndItsAtIsEditedWithinTheRule()
    {
        var vm = Open();
        vm.SelectedTreeItem = Row(vm, "symmetry:X");
        var overlay = new Viewer3DDrawOverlay();
        vm.FillDrawOverlay(overlay);
        Assert.NotEmpty(overlay.Selected);
        Assert.Empty(overlay.SymmetryPlanes);
        Assert.Equal("Symmetry plane X", vm.Properties.Heading);

        int entries = vm.UndoEntries;
        var at = vm.Properties.Fields.Single();
        at.Text = "10";                                      // inside the extent: refused, in the rule's own words
        vm.Properties.CommitField(at);
        Assert.StartsWith("The symmetry plane X = 10 µm does not lie on the model's extent", vm.Properties.Error);
        Assert.Equal(0, vm.Document.SymmetryPlanes[0].At);
        Assert.Equal(entries, vm.UndoEntries);

        at = vm.Properties.Fields.Single();
        at.Text = "20";                                      // the other end: one entry
        vm.Properties.CommitField(at);
        Assert.Equal("", vm.Properties.Error);
        Assert.Equal(20 * Um, vm.Document.SymmetryPlanes[0].At);
        Assert.Equal(entries + 1, vm.UndoEntries);
    }

    // ── 5. a boundary's tint, both ways, and B ───────────────────────────────────────────────

    [Fact]
    public void Gate5_ABoundarysTint_SelectsBothWays_BStepsToTheSolid_AndTheInspectorEditsIt()
    {
        var vm = Open();
        var v = vm.Viewer;
        var tint = vm.SceneObject(Scene3DBuilder.FaceTintPrefix + Tint)!;

        // tree → view
        vm.SelectedTreeItem = Row(vm, Tint);
        Assert.Equal([tint.Id], v.Selection.Select(s => s.Object));
        var overlay = new Viewer3DDrawOverlay();
        vm.FillDrawOverlay(overlay);
        Assert.NotEmpty(overlay.Selected);
        Assert.StartsWith("Thermal boundary on cap/zmax", vm.Properties.Heading);

        // view → tree: a pick on the tint is the tint, not the coplanar top face of 'cap'
        v.SetSelection([]);
        LookDown(v);
        Hover(v, X, Y);
        Assert.Equal("cap", v.Scene.Object(Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, X, Y, W, H, v.View.Visible).Id)?.Name);
        v.Click(shift: false);
        Assert.Equal(tint.Id, v.Selection.Single().Object);
        Assert.Equal(Tint, vm.SelectedTreeItem?.Name);

        // B: the owning solid (its row, the scene and the Inspector); Shift+B: the boundary and its row again
        Assert.True(v.Cycle(+1));
        Assert.Equal("cap", v.Scene.Object(v.Selection.Single().Object)!.Name);
        Assert.Equal("cap", vm.SelectedTreeItem?.Name);
        Assert.Contains("cap", vm.Properties.Heading);
        Assert.EndsWith(" · 2 of 3", v.CycleText);            // the tint, cap, block
        Assert.True(v.Cycle(-1));
        Assert.Equal(tint.Id, v.Selection.Single().Object);
        Assert.Equal(Tint, vm.SelectedTreeItem?.Name);
        Assert.Equal("Thermal boundary cap/zmax · 1 of 3", v.CycleText);

        // hidden: B never lands on it
        Row(vm, Tint).IsVisible = false;
        Settle(vm);
        LookDown(v);
        Hover(v, X, Y);
        for (int k = 0; k < 4; k++)
        {
            v.Cycle(+1);
            Assert.False(v.Scene.Object(v.Selection.Single().Object)!.Tint);
        }

        // the Inspector: Convection (the row still selects with its tint hidden), then h — each one entry
        vm.SelectedTreeItem = Row(vm, Tint);
        Assert.True(vm.Properties.IsThermalBoundary);
        int entries = vm.UndoEntries;
        vm.Properties.BoundaryKind = C3dPropertiesViewModel.ThermalBoundaryKinds[1];
        Assert.Equal(ThermalBoundaryKind.Convection, vm.ThermalBoundaryOn("cap/zmax")!.Kind);
        Assert.Equal(entries + 1, vm.UndoEntries);
        vm.Properties.BoundaryH = "250";
        vm.Properties.CommitBoundary();
        Assert.Equal("250", vm.ThermalBoundaryOn("cap/zmax")!.H);
        Assert.Equal(entries + 2, vm.UndoEntries);
        vm.Properties.BoundaryKind = C3dPropertiesViewModel.ThermalBoundaryKinds[0];
        Assert.Equal("40", vm.ThermalBoundaryOn("cap/zmax")!.TempC);          // the fixed temperature came back from memory
    }

    // ── 6. check ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_Check_ReportsAPlaneInsideTheExtent_InTheRulesWords()
    {
        var vm = Open();
        vm.Document.SymmetryPlanes[0].At = 10 * Um;
        var found = C3dThermal.Places(vm.Document, vm.Elaboration!).Select(d => d.Render()).ToList();
        Assert.Contains(found, f => f.Contains("The symmetry plane X = 10 µm does not lie on the model's extent", StringComparison.Ordinal));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Two boxes (block under cap, 20 µm cubes), a thermal setup 'Hot' holding a 40 °C boundary on cap's top face, one
    /// record of every kind — heat source, probe, mesh region, effective block, a symmetry plane on x = 0 — and a ClipPlane
    /// temperature plot, hidden unless <paramref name="plotShown"/>.</summary>
    private C3dEditorViewModel Open(bool plotShown = false)
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
        var setup = new EmSetup
        {
            Name = "Hot", Solver3D = Em3dSolver.None, Problem3D = Em3dProblemType.Thermal,
            Thermal = new CemThermal { Boundaries = [new CemThermalBoundary { Face = "cap/zmax", Kind = ThermalBoundaryKind.FixedT, TempC = "40" }] },
        };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects =
            [
                new C3dBox { Name = "block", Material = "Gold", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(20 * Um, 20 * Um, 20 * Um) },
                new C3dBox { Name = "cap", Material = "Gold", Min = new C3dPoint3(0, 0, 20 * Um), Size = new C3dPoint3(20 * Um, 20 * Um, 20 * Um) },
            ],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
            HeatSources = [new C3dHeatSource { Name = "src", Solid = "block", Power = "1" }],
            Probes = [new C3dProbe { Name = "pr", Point = new C3dPoint3(10 * Um, 10 * Um, 40 * Um) }],
            MeshRegions = [new C3dMeshRegion { Name = "fine", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(5 * Um, 5 * Um, 5 * Um), SizeUm = 1 }],
            EffectiveBlocks = [new C3dEffectiveBlock { Name = "via", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(5 * Um, 5 * Um, 5 * Um) }],
            SymmetryPlanes = [new C3dSymmetryPlane { Axis = C3dAxis.X, At = 0 }],
            FieldPlots =
            [
                new C3dFieldPlot
                {
                    Name = "Field1", Setup = "Hot", Quantity = C3dFieldPlot.TemperatureQuantity, On = C3dFieldPlotOn.ClipPlane,
                    Axis = C3dAxis.Z, Offset = 10 * Um, Hidden = !plotShown,
                },
            ],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.Equal("Hot", vm.ActiveSetupName);
        Assert.NotNull(vm.SceneObject(Scene3DBuilder.FaceTintPrefix + Tint));
        return vm;
    }

    private static IEnumerable<C3dTreeItem> Rows(C3dEditorViewModel vm)
        => vm.Tree.SelectMany(g => g.Items).SelectMany(i => i.Children.Prepend(i));

    private static C3dTreeItem Row(C3dEditorViewModel vm, string name) => Rows(vm).Single(r => r.Name == name);

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);

    private void Settle(C3dEditorViewModel vm) => Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, "the scene never settled");

    private static void LookDown(Viewer3DViewModel v)
    {
        v.View.Camera = Camera3D.Fit(v.Scene.ContentMin, v.Scene.ContentMax, W / H, Projection3D.Orthographic);
        v.View.Camera.SetStandardView(StandardView3D.Top);
        v.View.Camera.Target = (v.Scene.ContentMin + v.Scene.ContentMax) * 0.5f;
        // the depth range the whole scene's: a tint stands a lift off its face (1e-4 of the air box), above the content's own
        v.View.Camera.SceneRadius = (v.Scene.BoundsMax - v.Scene.BoundsMin).Length() * 0.5f;
    }

    private static void Hover(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }
}
