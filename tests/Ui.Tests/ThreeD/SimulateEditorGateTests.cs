// ================================================================
//  SimulateEditorGateTests.cs — brief-em3d-49's editor gates: the Setups panel's edits are document edits that
//  round-trip the .c3d byte for byte and undo (5); an air-box edit writes only the active setup (6); the stale-fields
//  banner is a view-model state (8); and a port drawn in the editor is resolved by contact, flipped and undone.
//  Pixels were not seen: these read the view model and the document.
// ================================================================

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class SimulateEditorGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sim49-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public SimulateEditorGateTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 5. the panel's edits are the document's ─────────────────────────────────────────────

    [Fact]
    public void Gate5_ASetupEditFromThePanel_RoundTripsTheC3dByteForByte_AndUndoRestoresIt()
    {
        var vm = Open(Microstrip(Setup("S1")));
        string original = C3dPersistence.Serialize(vm.Document);
        var panel = Assert.IsType<EmSetupDocument>(vm.SetupEditor).ViewModel;
        Assert.True(panel.IsEmbedded);
        Assert.False(panel.ShowGeometryReference);
        Assert.DoesNotContain(panel.Solver3DChoiceList, c => c.Value == Em3dSolver.None);

        panel.Problem3DChoice = EmSetupEditorViewModel.Problem3DChoices.Single(c => c.Value == Em3dProblemType.Eigenmode);
        Settle(vm);
        Assert.False(panel.UndoRedo.CanUndo);                       // the panel's own stack stays empty
        Assert.True(vm.IsDirty);
        Assert.Equal(Em3dProblemType.Eigenmode, EmSetupPersistence.FromEmbedded(vm.Document.Setups[0]).Problem3D);
        string edited = C3dPersistence.Serialize(vm.Document);
        Assert.Null(vm.Save());
        Assert.Equal(edited, C3dPersistence.Serialize(C3dPersistence.LoadFromFile(vm.FilePath)));
        Assert.Equal(edited, File.ReadAllText(vm.FilePath));

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(original, C3dPersistence.Serialize(vm.Document));
        Assert.Equal(Em3dProblemType.Driven, vm.SetupEditor!.ViewModel.Working.Problem3D);
        vm.UndoRedo.Redo();
        Assert.Equal(edited, C3dPersistence.Serialize(vm.Document));
    }

    [Fact]
    public void TheSetupsPanel_AddsDuplicatesRenamesAndRemoves_EachOneUndoableEntry_AndKeepsTheActiveOne()
    {
        var vm = Open(Microstrip(Setup("S1")));
        int entries = vm.UndoEntries;
        vm.AddSetup();
        Assert.Equal(["S1", "S2"], vm.SetupItems.Select(i => i.Name));
        Assert.Equal("S1", vm.ActiveSetupName);
        vm.SelectedSetupItem = vm.SetupItems[0];
        vm.DuplicateSetup();
        Assert.Equal(["S1", "S1 copy", "S2"], vm.SetupItems.Select(i => i.Name));
        vm.SelectedSetupItem = vm.SetupItems[0];
        Assert.Null(vm.RenameSetup("Main"));
        Assert.Equal("Main", vm.ActiveSetupName);
        Assert.Contains("already has a setup named 'S2'", vm.RenameSetup("S2"));
        vm.SelectedSetupItem = vm.SetupItems.Single(i => i.Name == "S2");
        vm.RemoveSetup();
        Assert.Equal(["Main", "S1 copy"], vm.SetupItems.Select(i => i.Name));
        Assert.Equal(entries + 4, vm.UndoEntries);
        for (int k = 0; k < 4; k++) vm.UndoRedo.Undo();
        Assert.Equal(["S1"], vm.SetupItems.Select(i => i.Name));
    }

    /// <summary>3D editor round 1 — Simulate ▸ Setup Analyses… replaced 3D ▸ Setups…: the toolbar's tune button asks
    /// the shell for that one dialog, and each setup is an analysis card (badge + summary, or why it cannot run).</summary>
    [Fact]
    public void SetupAnalyses_TheTuneButtonAsksTheShell_AndEachSetupIsACard()
    {
        var planar = new EmSetup { Name = "Flat" };
        var eig = Setup("Modes");
        eig.Problem3D = Em3dProblemType.Eigenmode;
        var vm = Open(Microstrip(Setup("S1"), eig, planar));
        C3dEditorViewModel? asked = null;
        vm.SetupAnalysesRequested = c => asked = c;
        vm.OpenSetupAnalysesCommand.Execute(null);
        Assert.Same(vm, asked);

        Assert.Equal(["SP", "EIG", "?"], vm.SetupItems.Select(i => i.TypeLabel));
        Assert.Equal("Palace · 1–20 GHz, 101 pts", vm.SetupItems[0].Summary);
        Assert.StartsWith("Palace · eigenmodes", vm.SetupItems[1].Summary);
        Assert.Contains(C3dSetups.PlanarRefusal, vm.SetupItems[2].Summary);
        Assert.Equal("Run S1", vm.SetupItems[0].RunLabel);
    }

    // ── 6. an air-box edit writes only the active setup ─────────────────────────────────────

    [Fact]
    public void Gate6_AnAirBoxEdit_WritesOnlyTheActiveSetup()
    {
        var vm = Open(Microstrip(Setup("S1"), Setup("S2")));
        vm.SetActiveSetup("S2");
        Settle(vm);
        string s1 = vm.Document.Setups[0].GetRawText();
        Assert.Null(vm.SetAirBoxBoundary("zmin", Em3dBoundaryKind.Pec));
        Assert.Null(vm.SetAirBoxPadding("zmin", "0"));
        Settle(vm);
        Assert.Equal(s1, vm.Document.Setups[0].GetRawText());
        var s2 = EmSetupPersistence.FromEmbedded(vm.Document.Setups[1]);
        Assert.Equal(new EmAirBoxFace(0, Em3dBoundaryKind.Pec), s2.AirBox!.ZMin);
        Assert.Contains("Writes setup 'S2'", vm.StatusMessage);
        // The drawn box is the active setup's: its floor is PEC and at the geometry's bottom.
        Assert.Equal(Em3dBoundaryKind.Pec, vm.ShownAirBox!.Faces.ZMin);
        Assert.Equal(0, vm.ShownAirBox.Min.Z, 12);
        Assert.True(vm.Viewer.ShowBoundaryFaces || vm.Viewer.Scene.Objects.Any(o => o.Name == "airbox/zmin" && o.PickLast));
    }

    // ── 8. the stale-fields banner ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_AfterAnEdit_TheFieldsFromTheRunSayTheModelHasChangedSince()
    {
        var vm = Open(Microstrip(Setup("S1")));
        string results = Path.Combine(_root, "results");
        vm.ResultsRootProvider = () => results;
        var run = vm.ActiveRunSetup!;
        Directory.CreateDirectory(Em3dRunService.RunDirectory(results, run, Em3dSolver.Palace));
        vm.RunFinished(run, C3dPersistence.Serialize(vm.Document));
        Assert.Null(vm.FieldsStaleText);

        vm.ChangeObjects("Rename", [1], o => o.Name = "line");
        Settle(vm);
        Assert.StartsWith("Fields are from the run at", vm.FieldsStaleText);
        vm.UndoRedo.Undo();
        Assert.Null(vm.FieldsStaleText);
    }

    // ── ports in the editor ─────────────────────────────────────────────────────────────────

    [Fact]
    public void APortDrawnInTheEditor_IsResolvedByContact_DrawnWithItsNumber_Flipped_AndUndone()
    {
        var vm = Open(Microstrip(Setup("S1")));
        vm.SetPlane(new DrawingPlane(C3dPlane.YZ, 0));
        var tool = new PortTool(vm, () => vm.NewPortTemplate());
        Assert.True(tool.Click(At(0, 450, 0)).Advanced);
        Assert.NotNull(tool.Preview(At(0, 550, 100)));
        var step = tool.Click(At(0, 550, 100));
        Assert.True(step.Finished);
        vm.AddPort(tool.Made!);
        Settle(vm);
        var r = Assert.Single(vm.PortResults);
        Assert.Equal(("gnd", "trace"), (r.Resolved!.NegativeObject, r.Resolved.PositiveObject));
        Assert.NotNull(vm.SceneObject("port/1"));
        var overlay = new Viewer3DDrawOverlay();
        vm.FillDrawOverlay(overlay);
        Assert.Contains(overlay.Labels, l => l.Text == "1");

        vm.FlipPorts([vm.Document.Ports[0]]);
        Settle(vm);
        Assert.Equal("trace", vm.PortResults[0].Resolved!.NegativeObject);
        vm.UndoRedo.Undo();
        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Empty(vm.Document.Ports);
        Assert.Null(vm.SceneObject("port/1"));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private static C3dDrawInput At(long x, long y, long z) => new(new C3dPoint3(x * Um, y * Um, z * Um), true, true, null, null);

    private static EmSetup Setup(string name) => new() { Name = name, Solver3D = Em3dSolver.Palace };

    private static C3dDocument Microstrip(params EmSetup[] setups)
    {
        C3dRect R(long u, long v, long du, long dv) => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };
        return new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000 * Um, 1000 * Um, 100 * Um) },
                new C3dSheet { Name = "trace", Material = "Copper", Plane = C3dPlane.XY, Offset = 100 * Um, Rect = R(0, 450, 1000, 100) },
                new C3dSheet { Name = "gnd", Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Rect = R(0, 0, 1000, 1000) },
            ],
            Setups = [.. setups.Select(EmSetupPersistence.ToEmbedded)],
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
