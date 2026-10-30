// brief-dfr31-3 §2 — a clip-plane field plot's plane is shown and placed in the 3D view, and its Solution list says where its
// rows will come from:
//
//   1  the selected plot's plane is in the view's chrome at the plot's offset, spanning the air box, before and after a solve;
//      it goes when the plot is deselected unless the plot keeps it on, and follows a drag of the offset
//   2  "Through…" then a click on a via sets the offset to the via's centre on the plot's axis (one undo entry)
//   3  an electrostatic setup with no run lists ONE disabled "Terminals …" row and no frequency rows, and greys E and V
//   and a static run's solutions are keyed by the terminal names the run itself recorded
//
// The run of gate 1 is the committed cavity fixture's, as FieldPlotTests runs it.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(Viewer3DCollection.Name)]
public sealed class FieldPlotPlaneTests : IDisposable
{
    private const long Um = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-dfr313-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Gate1_ThePlane_IsDrawnAtItsOffset_BeforeAndAfterASolve()
    {
        var vm = Open(Eigenmode());
        vm.NewFieldPlot();                                                   // selected, cut on z
        Assert.Null(vm.SetFieldPlot("Field1", "cut", p => { p.Axis = C3dAxis.Y; p.Offset = 4000 * Um; }));
        var box = vm.Viewer.Scene.Problem!.Boundary;

        void PlaneAt(long dbu)
        {
            var (corners, label) = Assert.Single(Planes(vm));
            double y = dbu * 1e-9;
            Assert.All(corners, c => Assert.Equal(y, c.Y, 12));
            Assert.Equal(box.Min.X, corners.Min(c => c.X), 12);
            Assert.Equal(box.Max.X, corners.Max(c => c.X), 12);
            Assert.Equal(box.Min.Z, corners.Min(c => c.Z), 12);
            Assert.Equal(box.Max.Z, corners.Max(c => c.Z), 12);
            Assert.StartsWith("Y = ", label);
        }

        PlaneAt(4000 * Um);                                                  // before any run
        Run(vm);
        PlaneAt(4000 * Um);                                                  // and after

        vm.PreviewPlotOffset("Field1", 6000 * Um);                           // a drag of the slider
        PlaneAt(6000 * Um);
        vm.EndPlotOffsetPreview();

        vm.SelectedTreeItem = null;
        Assert.Empty(Planes(vm));
        Assert.Null(vm.SetFieldPlot("Field1", "keep", p => p.ShowPlane = true));
        PlaneAt(4000 * Um);
    }

    [Fact]
    public void Gate2_ThroughThenAClickOnAVia_SetsTheOffsetToTheViasCentre()
    {
        var vm = Open(Eigenmode());
        vm.NewFieldPlot();
        Assert.Null(vm.SetFieldPlot("Field1", "across x", p => { p.Axis = C3dAxis.X; p.Offset = 0; }));
        var via = vm.Viewer.Scene.Objects.Single(o => o.Name == "via");
        var faceOfBox = vm.Viewer.Scene.Objects.First(o => o.Kind == CircuitRF.Render.Scene3D.Scene3DKind.Boundary);
        int entries = vm.UndoEntries;

        vm.Properties.PlotThroughArmed = true;
        Assert.Equal("Field1", vm.PlotThroughArmed);
        Assert.True(vm.DrawArmed);                                           // a wobbly click still places, rather than orbits
        Assert.NotNull(vm.PlotThroughObject(faceOfBox.Id));                  // the air box is no object to cut through
        Assert.Equal("Field1", vm.PlotThroughArmed);

        Assert.Null(vm.PlotThroughObject(via.Id));
        Assert.Equal(11000 * Um, vm.FieldPlot("Field1")!.Offset);           // 10 mm .. 12 mm
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Null(vm.PlotThroughArmed);
        Assert.False(vm.Properties.PlotThroughArmed);
    }

    [Fact]
    public void Gate3_AStaticSetupWithNoRun_ListsTheDisabledTerminalsRow_AndNoFrequencies()
    {
        var vm = Open(Electrostatic());
        vm.NewFieldPlot();
        var props = vm.Properties;

        var row = Assert.Single(props.PlotSolutions);
        Assert.Equal("Terminals of the electrostatic setup, once it has run", row.Label);
        Assert.False(row.IsEnabled);
        Assert.Same(row, props.PlotSolution);
        Assert.DoesNotContain(props.PlotSolutions, c => c.IsOther || c.Label.Contains("GHz") || c.Label.Contains("requenc"));

        Assert.All(props.PlotQuantities, q => Assert.False(q.IsEnabled));
        Assert.Contains(props.PlotQuantities, q => q.Quantity == "E");
        Assert.Contains(props.PlotQuantities, q => q.Quantity == "V");
        Assert.Equal("E", props.PlotQuantity!.Quantity);
    }

    /// <summary>The 3D view's problem is a display one and names no terminal: an electrostatic run's solutions are named from the
    /// run's own config.json and groups.json, so a plot naming its terminal finds it.</summary>
    [Fact]
    public void AStaticRunsSolutions_AreKeyedByTheTerminalTheRunNamed()
    {
        string plates = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", "plates");
        var run = CircuitRF.Render.Scene3D.Fields.FieldRun.OpenPalace(plates)!;
        var s = Assert.Single(run.Solutions);
        Assert.Equal("plate", s.Terminal);
        Assert.Equal("plate", CircuitRF.Render.Scene3D.Fields.FieldPlotResolver.SolutionKey(s, problem: null).Terminal);
        Assert.Equal("Terminal 'plate' at 1 V, the others at 0 V", CircuitRF.Render.Scene3D.Fields.FieldPlotResolver.SolutionLabel(s, null, null));
    }

    private static List<(Point3[] Corners, string Label)> Planes(C3dEditorViewModel vm)
    {
        var overlay = new Viewer3DDrawOverlay();
        vm.FillDrawOverlay(overlay);
        return [.. overlay.FieldPlanes];
    }

    private static EmSetup Eigenmode()
    {
        var none = new EmAirBoxFace(0, null);
        return new EmSetup
        {
            Name = "modes", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Eigenmode,
            AirBox = new EmAirBox(none, none, none, none, none, none), Eigenmode = new EmEigenmode3D(1, 5),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("5", "15", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
    }

    private static EmSetup Electrostatic()
    {
        var none = new EmAirBoxFace(0, null);
        return new EmSetup
        {
            Name = "C", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Electrostatic,
            AirBox = new EmAirBox(none, none, none, none, none, none),
            Terminals3D = [new EmTerminal3D("via", "", null, ["via"])],
        };
    }

    /// <summary>A 22.86 × 10.16 × 25 mm cavity with a copper post from x = 10 mm to 12 mm.</summary>
    private C3dEditorViewModel Open(EmSetup setup)
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }, new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cavity", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cavity.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "cavity", Material = "Air", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(22860 * Um, 10160 * Um, 25000 * Um) },
                new C3dBox { Name = "via", Material = "Copper", Min = new C3dPoint3(10000 * Um, 4000 * Um, 0), Size = new C3dPoint3(2000 * Um, 2000 * Um, 25000 * Um) },
            ],
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
        return vm;
    }

    /// <summary>The cavity fixture as the active setup's finished run.</summary>
    private void Run(C3dEditorViewModel vm)
    {
        string from = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", "cavity");
        string dir = CircuitRF.Design.Em3d.Em3dRunService.RunDirectory(Path.Combine(_root, "results"), vm.ActiveRunSetup!, Em3dSolverId.Palace);
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(dir, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(dir);
        vm.RunFinished();
        Until(() => vm.Viewer.FieldsAvailable && vm.Viewer.FieldSolutions.Count > 0, "the run's fields were never read");
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);
}
