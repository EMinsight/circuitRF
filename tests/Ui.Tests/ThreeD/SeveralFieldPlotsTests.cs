// brief-em3d-96 §6 — several field plots drawn at once (pixels were not seen; every gate reads the layers, the frame plan, the
// uniform block and the document):
//
//   1+2+5  two |E| slices and a B slice are drawn together: three layers, three field draws naming blocks 0-2, the blocks holding
//          each plot's mode; the two |E| slices share ONE range — Auto over the union of their triangles — and one legend; and
//          the second and third plot of the solution read no step again
//   3      a fifth tick is refused with the sentence and changes nothing; a new plot with four drawn is added hidden; Show all
//          with six ticks four
//   4+6+7  a drag rebuilds its own plot only; hiding a plot mid-build disposes it and cancels the build, and the other plot's
//          geometry is untouched and draws exactly as one plot always did (one range, block 0, its own buffer)
//   8      the uniform block fits Metal's inline limit (the shader hash is Viewer3DFrameGateTests.Gate1b)
//   9      two animated plots carry the same φ in both blocks on every animation tick
//
// Gate 6's round trip (a document from before this brief opens and draws as it did) is FieldPlotTests.Gate1, unchanged.

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(Viewer3DCollection.Name)]
public sealed class SeveralFieldPlotsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em96-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Gate1_TwoESlicesAndABSlice_AreThreeLayers_ThreeDraws_AndTheESlicesShareOneRange()
    {
        var vm = Open();
        var (lo, hi) = vm.PlotAxisRange(C3dAxis.Z)!.Value;
        vm.NewFieldPlot();
        Built(vm, "Field1");
        long reads = vm.Viewer.FieldStepReads;
        Assert.True(reads > 0);
        vm.NewFieldPlot();
        Assert.Null(vm.SetFieldPlot("Field1", "z low", p => p.Offset = lo + 3 * (hi - lo) / 10));
        Assert.Null(vm.SetFieldPlot("Field2", "z high", p => p.Offset = lo + 6 * (hi - lo) / 10));
        vm.NewFieldPlot();
        var (xlo, xhi) = vm.PlotAxisRange(C3dAxis.X)!.Value;
        Assert.Null(vm.SetFieldPlot("Field3", "B across x", p => { p.Quantity = "B"; p.Mode = nameof(FieldMode.Instantaneous); p.Axis = C3dAxis.X; p.Offset = (xlo + xhi) / 2; }));
        foreach (string n in (string[])["Field1", "Field2", "Field3"]) Built(vm, n);
        Settle();

        var v = vm.Viewer;
        Assert.Equal(["Field1", "Field2", "Field3"], v.FieldLayers.Select(l => l.Name));
        Assert.Equal(reads, v.FieldStepReads);                                   // gate 5: one solution, read once
        var plan = new Scene3DFramePlan();
        plan.Plan(v.Scene, v.View, 400, 300, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, v.FieldDrawn);
        var draws = plan.Draws.Take(plan.DrawCount).Where(d => d.Pipeline == Scene3DPipeline.Field).ToList();
        Assert.Equal([0u, 1u, 2u], draws.Select(d => plan.LayerOf(d)).Order());
        Assert.Equal(v.FieldDrawn.Vertices.Length, draws.Sum(d => d.Count));
        float Block(int k, int at) => plan.Uniforms[Scene3DFramePlan.FieldAt + k * FieldUniforms.Floats + at];
        Assert.Equal([0f, 0f, 1f, 0f], Enumerable.Range(0, 4).Select(k => Block(k, 4)));   // |E|, |E|, |Re{B·e^jφ}|, none
        Assert.Equal([1f, 1f, 1f, 0f], Enumerable.Range(0, 4).Select(k => Block(k, 7)));   // three ClipPlane slices, unclipped

        // D2 — one range for the two |E| slices: the percentile over the union of their triangles. B ranges on its own.
        var (e1, e2, b) = (v.LayerNamed("Field1")!, v.LayerNamed("Field2")!, v.LayerNamed("Field3")!);
        var union = FieldSurfacePlot.EmScale(e1.Quantity!, [.. e1.Surfaces, .. e2.Surfaces], e1.Db, e1.Percentile);
        Assert.Equal(union, e1.Scale);
        Assert.Equal(union, e2.Scale);
        Assert.Equal(b.OwnScale, b.Scale);
        Assert.Equal((float)(union.Hi - union.DbOffset), Block(0, 3));
        Assert.Equal(Block(0, 3), Block(1, 3));
        var legends = v.FieldLegendGroups;
        Assert.Equal(2, legends.Count);
        Assert.Equal(["Field1", "Field2"], legends[0].Names);
        Assert.Equal("Field1, Field2", legends[0].Lines[0]);
        Assert.Equal("Field3", legends[1].Lines[0]);
    }

    [Fact]
    public void Gate3_AFifthTick_IsRefused_ANewPlotIsAddedHidden_AndShowAllTicksFour()
    {
        var vm = Open();
        for (int i = 0; i < 4; i++) vm.NewFieldPlot();
        Assert.All(vm.Document.FieldPlots, p => Assert.False(p.Hidden));

        vm.NewFieldPlot();                                                       // with four drawn: added hidden, and said
        Assert.True(vm.FieldPlot("Field5")!.Hidden);
        const string sentence = "Four plots are drawn: untick one of Field1, Field2, Field3, Field4 to draw Field5.";
        Assert.EndsWith(sentence, vm.StatusMessage);
        int entries = vm.UndoEntries;
        Assert.Equal(sentence, vm.SetPlotShown("Field5", true));
        Assert.Equal(entries, vm.UndoEntries);                                   // nothing changed
        Assert.Equal([false, false, false, false, true], vm.Document.FieldPlots.Select(p => p.Hidden));

        vm.NewFieldPlot();
        vm.HideAllTreeObjectsCommand.Execute(null);
        Assert.All(vm.Document.FieldPlots, p => Assert.True(p.Hidden));
        vm.ShowAllTreeObjectsCommand.Execute(null);
        Assert.Equal([false, false, false, false, true, true], vm.Document.FieldPlots.Select(p => p.Hidden));
        Assert.Contains("Field5, Field6 stay hidden", vm.StatusMessage);
    }

    [Fact]
    public void Gate4_EachPlotBuildsOnItsOwn_AndAHiddenPlotsBuildIsCancelled_LeavingTheOtherAsItWas()
    {
        var vm = Open();
        var (lo, hi) = vm.PlotAxisRange(C3dAxis.Z)!.Value;
        vm.NewFieldPlot();
        vm.NewFieldPlot();
        Assert.Null(vm.SetFieldPlot("Field2", "z high", p => p.Offset = lo + 7 * (hi - lo) / 10));
        Built(vm, "Field1");
        Built(vm, "Field2");
        Settle();
        var (a, b) = (vm.Viewer.LayerNamed("Field1")!, vm.Viewer.LayerNamed("Field2")!);
        var before = (b.Builds, b.Cancellations, b.Geometry.Version);

        // A drag of Field1's slider rebuilds Field1 alone, and cancels nothing of Field2's.
        long builds = a.Builds;
        vm.PreviewPlotOffset("Field1", lo + 2 * (hi - lo) / 10);
        vm.PreviewPlotOffset("Field1", lo + 4 * (hi - lo) / 10);
        Until(() => a.Builds >= builds + 2, "the drag's slices were not both drawn");
        vm.EndPlotOffsetPreview();
        Settle();
        Assert.Equal(before, (b.Builds, b.Cancellations, b.Geometry.Version));

        // Field1 hidden while it is being built: its layer goes and its build is cancelled; Field2 is not rebuilt.
        Assert.Null(vm.SetFieldPlot("Field1", "move", p => p.Offset = lo + (hi - lo) / 10));
        Assert.True(a.Building);
        vm.SetPlotShown("Field1", false);
        Assert.True(a.Disposed);
        Assert.True(a.Cts!.IsCancellationRequested);
        Settle();
        Assert.Equal(before, (b.Builds, b.Cancellations, b.Geometry.Version));
        Assert.Same(b, Assert.Single(vm.Viewer.FieldLayers));
        // one plot draws exactly as one plot always did: its own buffer and version, one range, colour block 0
        Assert.Same(b.Geometry.Vertices, vm.Viewer.FieldDrawn.Vertices);
        Assert.Equal(b.Geometry.Version, vm.Viewer.FieldDrawn.Version);
        Assert.Equal([new FieldLayerRange(0, b.Geometry.Vertices.Length, 0)], vm.Viewer.FieldDrawn.Layers);
    }

    [Fact]
    public void Gate8_TheUniformBlock_FitsMetalsInlineLimit()
    {
        Assert.Equal(2304, Scene3DFramePlan.UniformBytes);   // brief-em3d-106 added the look block (288 bytes)
        Assert.True(Scene3DFramePlan.UniformBytes <= CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend.InlineBytesLimit);
        CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend.AssertUniformsFitInline();
    }

    [Fact]
    public void Gate9_TwoAnimatedPlots_CarryTheSamePhase_OnEveryTick()
    {
        var vm = Open();
        var (lo, hi) = vm.PlotAxisRange(C3dAxis.Z)!.Value;
        vm.NewFieldPlot();
        vm.NewFieldPlot();
        Assert.Null(vm.SetFieldPlot("Field1", "animated", p => p.Mode = nameof(FieldMode.Instantaneous)));
        Assert.Null(vm.SetFieldPlot("Field2", "animated", p => { p.Mode = nameof(FieldMode.Instantaneous); p.Offset = lo + 7 * (hi - lo) / 10; }));
        Built(vm, "Field1");
        Built(vm, "Field2");
        var v = vm.Viewer;
        (float, float, float, float) Phases() => (v.View.Field[0], v.View.Field[1], v.View.Field[FieldUniforms.Floats], v.View.Field[FieldUniforms.Floats + 1]);

        v.FieldPlaying = true;
        var seen = new HashSet<float>();
        try
        {
            for (int tick = 0; tick < 8; tick++)
            {
                double at = v.FieldPhaseDegrees;
                Until(() => v.FieldPhaseDegrees != at, "the animation never ticked");
                var (c1, s1, c2, s2) = Phases();
                Assert.Equal((c1, s1), (c2, s2));
                seen.Add(c1);
            }
        }
        finally { v.FieldPlaying = false; }
        Assert.True(seen.Count > 1);

        // φ moved from either plot's Inspector moves both.
        foreach (string name in (string[])["Field1", "Field2"])
        {
            vm.SelectedTreeItem = vm.Tree.Single(g => g.Role == C3dTreeGroupRole.FieldPlots).Items.Single(i => i.Name == name);
            vm.Properties.Viewer.FieldPhaseDegrees = name == "Field1" ? 90 : 180;
            var (c1, s1, c2, s2) = Phases();
            Assert.Equal((c1, s1), (c2, s2));
            Assert.Equal(name == "Field1" ? 1.0 : 0.0, s1, 6);
        }
    }

    // ── the fixture and the waits (FieldPlotTests') ─────────────────────────────────────────

    /// <summary>Waits until plot <paramref name="name"/>'s layer has drawn and has nothing more on its way.</summary>
    private void Built(C3dEditorViewModel vm, string name)
        => Until(() => vm.Viewer.LayerNamed(name) is { Builds: > 0, Scale: not null, Building: false, Item: not null } l && l.Name == name,
                 $"{name} was never drawn");

    private void Settle()
    {
        Thread.Sleep(150);
        while (_posted.TryDequeue(out var a)) a();
    }

    private C3dEditorViewModel Open()
    {
        const long um = 1000;
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cavity", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cavity.c3d");
        var none = new EmAirBoxFace(0, null);
        var setup = new EmSetup
        {
            Name = "modes", Solver3D = Em3dSolver.Palace, Problem3D = Em3dProblemType.Eigenmode,
            AirBox = new EmAirBox(none, none, none, none, none, none), Eigenmode = new EmEigenmode3D(1, 5),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("5", "15", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "cavity", Material = "Air", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(22860 * um, 10160 * um, 25000 * um) }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Viewer.Scene.Objects.Length > 0, "the scene never settled");

        // the committed cavity fixture as the active setup's finished run
        string run = Em3dRunService.RunDirectory(Path.Combine(_root, "results"), vm.ActiveRunSetup!, Em3dSolver.Palace);
        string cavity = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", "cavity");
        foreach (string f in Directory.EnumerateFiles(cavity, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(run, Path.GetRelativePath(cavity, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(run);
        vm.RunFinished();
        Until(() => vm.Viewer.FieldsAvailable && vm.Viewer.FieldSolutions.Count > 0, "the run's fields were never read");
        return vm;
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);
}
