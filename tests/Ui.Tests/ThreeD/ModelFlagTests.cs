// ================================================================
//  ModelFlagTests.cs — brief-em3d-93: "Model" keeps an object in the drawing and out of the solve. The key is written only
//  when false; the elaboration keeps what is off and C3dModelled.Filter is the one place a solve loses it (the air box
//  included); a reference to what is off is a refusal naming both; the ports that are left are renumbered 1…N with the
//  mapping in the notes and in the .sNp's header lines; the tree greys it (and lists it under Not Modeled by type); the
//  Inspector's row writes a group's every member as one undo entry. No solve runs: every gate reads the assembled problem.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class ModelFlagTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-model-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public ModelFlagTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1, 7. the key ───────────────────────────────────────────────────────────────────────────

    /// <summary>Gates 1, 7 and 8's round trip: a document stating no Model spells exactly what it did; "Model": false on a box,
    /// a group member, an instance, a port and a heat source reads and writes byte-identical; and toggling it makes a result
    /// stale through brief 87's own comparison (it is part of the run's model, unlike Transparency).</summary>
    [Fact]
    public void TheKey_IsWrittenOnlyWhenFalse_RoundTrips_AndMakesAResultStale()
    {
        var doc = FourPorts();
        doc.Objects.Add(Box("b", 2000, 0, group: "G"));
        doc.Instances.Add(new C3dInstance { Name = "U1", CellRef = "../../die" });
        doc.HeatSources.Add(new C3dHeatSource { Name = "q", Solid = "gnd", Power = "1" });
        string plain = C3dPersistence.Serialize(doc);
        Assert.DoesNotContain("\"Model\"", plain);
        Assert.Equal(plain, C3dPersistence.Serialize(C3dPersistence.Deserialize(plain)));
        string solved = C3dPersistence.SerializeForRun(doc);

        doc.Objects[0].Model = false;
        doc.Objects.Single(o => o.Name == "b").Model = false;
        doc.Instances[0].Model = false;
        doc.Ports[1].Model = false;
        doc.HeatSources[0].Model = false;
        string text = C3dPersistence.Serialize(doc);
        Assert.Equal(5, text.Split("\"Model\": false").Length - 1);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));
        var back = C3dPersistence.Deserialize(text);
        Assert.False(back.Objects[0].Model || back.Instances[0].Model || back.Ports[1].Model || back.HeatSources[0].Model);
        Assert.True(back.Ports[0].Model);
        Assert.True(C3dRunDocument.IsStale(solved, doc));
    }

    // ── 2, 3. the one filter ────────────────────────────────────────────────────────────────────

    /// <summary>Gate 2: two boxes, one off. The EM problem has one solid and the air box it would have were the other not drawn
    /// at all; the elaboration the editor draws still has both.</summary>
    [Fact]
    public void TheFilter_LeavesItOutOfTheProblem_AndOutOfTheAirBox_AndTheDrawingKeepsIt()
    {
        string ws = Workspace();
        var doc = new C3dDocument { Objects = [Box("keep", 0, 0), Box("far", 20000, 0)] };
        doc.Objects[1].Model = false;
        string path = WriteC3d(ws, doc);
        var drawn = C3dElaborator.ElaborateOnce(doc, path, Cws(ws));
        Assert.Equal(["keep", "far"], drawn.Solids.Select(s => s.Name));
        Assert.Contains("far", drawn.NotModelled);

        var g = C3dProblemAssembly.Assemble(Setup(Em3dSolver.Palace), doc, path, Cws(ws));
        Assert.True(g.Ok, g.Refusal);
        Assert.Equal(["keep"], g.Problem!.Solids.Select(s => s.Name));
        Assert.Contains(g.Notes, n => n.Contains("'far'", StringComparison.Ordinal) && n.StartsWith("Not modelled", StringComparison.Ordinal));

        var alone = new C3dDocument { Objects = [Box("keep", 0, 0)] };
        string ws2 = Workspace();
        var reference = C3dProblemAssembly.Assemble(Setup(Em3dSolver.Palace), alone, WriteC3d(ws2, alone), Cws(ws2));
        Assert.Equal(reference.Problem!.Boundary.Min, g.Problem.Boundary.Min);
        Assert.Equal(reference.Problem.Boundary.Max, g.Problem.Boundary.Max);
    }

    /// <summary>Gate 3: the thermal lowering of the filtered elaboration meshes one solid; a heat source in the box that is off is
    /// refused by the thermal validator the run and check share, naming both.</summary>
    [Fact]
    public void TheThermalLowering_MeshesWhatIsModelled_AndASourceInWhatIsNotIsRefused()
    {
        string ws = Workspace();
        var doc = new C3dDocument { Objects = [Box("keep", 0, 0), Box("lid", 2000, 0)] };
        doc.Objects[1].Model = false;
        string path = WriteC3d(ws, doc);
        var drawn = C3dElaborator.ElaborateOnce(doc, path, Cws(ws));
        var t = new CemThermal { Boundaries = [new CemThermalBoundary { Face = "keep/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" }] };
        var lowering = ThermalLowerings.Build(doc, C3dModelled.Filter(doc, drawn), t, 1, out string? why);
        Assert.True(lowering is not null, why);
        Assert.Single(lowering!.Regions);

        doc.HeatSources.Add(new C3dHeatSource { Name = "q", Solid = "lid", Power = "1" });
        var refusal = Assert.Single(C3dThermal.Places(doc, drawn), d => d.Id == C3dThermal.D.NotModelledId);
        Assert.Contains("'q'", refusal.Render());
        Assert.Contains("'lid'", refusal.Render());
    }

    // ── 4, 8, 9. ports ──────────────────────────────────────────────────────────────────────────

    /// <summary>Gate 4: a modelled port on a conductor that is off is refused naming both — by the assembly, and by check as a
    /// process (exit 1). Turning the port off too is the way through.</summary>
    [Fact]
    public void AModelledPort_OnAConductorThatIsOff_IsRefusedNamingBoth_InTheRunAndInCheck()
    {
        string ws = Workspace();
        var doc = FourPorts();
        doc.Objects.Single(o => o.Name == "t1").Model = false;
        doc.Setups = [EmSetupPersistence.ToEmbedded(Setup(Em3dSolver.Palace))];
        string path = WriteC3d(ws, doc);
        var g = C3dProblemAssembly.Assemble(Setup(Em3dSolver.Palace), doc, path, Cws(ws));
        Assert.False(g.Ok);
        Assert.Equal(C3dModelled.PortConductorRefusal(doc.Ports[0], "t1", positive: true), g.Refusal);

        var (exit, stdout, stderr) = Cli("check", path);
        Assert.Equal(1, exit);
        Assert.Contains("'t1'", stdout + stderr);

        doc.Ports[0].Model = false;
        Assert.True(C3dProblemAssembly.Assemble(Setup(Em3dSolver.Palace), doc, path, Cws(ws)).Ok);
    }

    /// <summary>Gates 8 and 9: four ports with P2 off assemble as three, numbered 1–3 and mapped P1→1, P3→2, P4→3 in the notes,
    /// the same for Palace and openEMS; the .sNp header lines carry the map; all four off is refused.</summary>
    [Fact]
    public void APortThatIsOff_LeavesTheRestRenumbered_TheMapInTheNotesAndTheHeader_AndAllOffIsRefused()
    {
        string ws = Workspace();
        var doc = FourPorts();
        doc.Ports[1].Model = false;
        string path = WriteC3d(ws, doc);
        var palace = C3dProblemAssembly.Assemble(Setup(Em3dSolver.Palace), doc, path, Cws(ws));
        var openEms = C3dProblemAssembly.Assemble(Setup(Em3dSolver.OpenEms), doc, path, Cws(ws));
        Assert.True(palace.Ok, palace.Refusal);
        var ports = palace.Problem!.Ports;
        Assert.Equal([1, 2, 3], ports.Select(p => p.Number));
        Assert.Equal(new int?[] { 1, 3, 4 }, ports.Select(p => p.SourceNumber));
        Assert.Equal(["t1", "t3", "t4"], ports.Select(p => p.PositiveObject));
        Assert.Contains(palace.Notes, n => n.Contains("P1→1, P3→2, P4→3", StringComparison.Ordinal));
        Assert.Contains(palace.Notes, n => n.Contains("The result has 3 ports; this 3D view declares 4", StringComparison.Ordinal));
        Assert.Equal(ports.Select(p => (p.Number, p.SourceNumber, p.PositiveObject, p.NegativeObject)),
                     openEms.Problem!.Ports.Select(p => (p.Number, p.SourceNumber, p.PositiveObject, p.NegativeObject)));

        // Gate 9 — the writer's lines: one per result port, then what a missing one means; none when nothing was renumbered.
        var lines = Em3dPortMap.Lines(ports);
        Assert.Equal(4, lines.Count);
        Assert.Equal(Em3dPortMap.Prefix + "result port 2 is the 3D view's port 3 'P3'", lines[1]);
        Assert.All(lines, l => Assert.All(l, c => Assert.True(c < 128)));
        Assert.Empty(Em3dPortMap.Lines([.. ports.Select(p => p with { SourceNumber = null, SourceLabel = null })]));

        doc.Objects.Single(o => o.Name == "t2").Model = false;        // P2's conductor off too: no refusal
        Assert.True(C3dProblemAssembly.Assemble(Setup(Em3dSolver.Palace), doc, path, Cws(ws)).Ok);
        foreach (var p in doc.Ports) p.Model = false;
        Assert.Equal(C3dModelled.AllPortsOff, C3dProblemAssembly.Assemble(Setup(Em3dSolver.Palace), doc, path, Cws(ws)).Refusal);
    }

    // ── 5, 6. the tree and the Inspector ────────────────────────────────────────────────────────

    /// <summary>Gate 5: by type the box that is off is listed under Not Modeled (its type in the detail), not under Boxes; by
    /// material it stays under its material, greyed; the view still draws it.</summary>
    [Fact]
    public void TheTree_ListsItUnderNotModeledByType_AndGreysItByMaterial()
    {
        var doc = new C3dDocument { Objects = [Box("a", 0, 0), Box("b", 2000, 0)] };
        doc.Objects[0].Model = false;
        var vm = Open(doc);
        Assert.NotNull(vm.SceneObject("a"));

        vm.TreeGrouping = C3dTreeGrouping.Primitive;
        var off = Assert.Single(vm.Tree, g => g.Role == C3dTreeGroupRole.NotModelled);
        Assert.Equal(C3dEditorViewModel.NotModeledHeader, off.Header);
        var row = Assert.Single(off.Items);
        Assert.Equal("a", row.Name);
        Assert.StartsWith("Boxes", row.Detail);
        Assert.DoesNotContain(vm.Tree.Where(g => g.Role == C3dTreeGroupRole.Objects).SelectMany(g => g.Items), i => i.Name == "a");

        vm.TreeGrouping = C3dTreeGrouping.Material;
        var byMaterial = vm.Tree.Single(g => g.Header == "Copper").Items;
        Assert.False(byMaterial.Single(i => i.Name == "a").IsModelled);
        Assert.True(byMaterial.Single(i => i.Name == "b").IsModelled);
        Assert.Equal(C3dModelled.Tip, byMaterial.Single(i => i.Name == "a").OrderTip);
    }

    /// <summary>Gate 6: a group of three whose Model differs reads indeterminate; ticking it writes all three as one undo entry,
    /// and undo puts the mixture back.</summary>
    [Fact]
    public void AGroupsModelRow_ReadsIndeterminate_AndTickingWritesEveryMember_OneUndoEntry()
    {
        var doc = new C3dDocument { Objects = [Box("b", 0, 0, group: "G"), Box("c", 2000, 0, group: "G"), Box("d", 4000, 0, group: "G/inner")] };
        doc.Objects[1].Model = false;
        var vm = Open(doc);
        vm.Viewer.OnPicked(vm.SceneObject("b")!.Id, 0, Vector3.Zero, true);
        vm.Viewer.Click(false);                                             // a click on a member selects the whole group
        var p = vm.Properties;
        Assert.True(p.IsGroup && p.HasModel);
        Assert.Null(p.ModelChecked);

        int entries = vm.UndoEntries;
        p.ModelChecked = true;
        Assert.All(vm.Document.Objects, o => Assert.True(o.Model));
        Assert.Equal(entries + 1, vm.UndoEntries);
        vm.UndoRedo.Undo();
        Assert.Equal([true, false, true], vm.Document.Objects.Select(o => o.Model));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A box, µm, 100 µm tall: <paramref name="x"/>, <paramref name="y"/> its corner, 100 × 100 µm unless stated.</summary>
    private static C3dBox Box(string name, long x, long y, long dx = 100, long dy = 100, string? group = null)
        => new() { Name = name, Material = "Copper", Group = group, Min = new C3dPoint3(x * Um, y * Um, 0), Size = new C3dPoint3(dx * Um, dy * Um, 100 * Um) };

    /// <summary>A ground strip (x 0–100 µm) and four traces t1…t4 (x 300–400 µm, 250 µm apart in y), each joined to the strip by
    /// a lumped port P1…P4 on z = 0 spanning the 200 µm gap.</summary>
    private static C3dDocument FourPorts()
    {
        var doc = new C3dDocument { Objects = [Box("gnd", 0, 0, 100, 1000)] };
        for (int k = 0; k < 4; k++)
        {
            doc.Objects.Add(Box($"t{k + 1}", 300, k * 250));
            doc.Ports.Add(new C3dPort
            {
                Number = k + 1, Name = $"P{k + 1}", Plane = C3dPlane.XY, Offset = 0,
                Rect = new C3dRect { Min = new C3dPoint2(100 * Um, k * 250 * Um), Size = new C3dPoint2(200 * Um, 100 * Um) },
            });
        }
        return doc;
    }

    private static EmSetup Setup(Em3dSolver solver) => new() { Name = "S", Solver3D = solver };

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, Alpha20 = 0.00393, ThermalK = 400 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string Cws(string ws) => Path.Combine(ws, ".cws");

    private static string WriteC3d(string ws, C3dDocument doc)
    {
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Workspace();
        string path = WriteC3d(ws, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Cws(ws), _posted.Enqueue);
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

    private static (int ExitCode, string StdOut, string StdErr) Cli(params string[] args)
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitrf.slnx"))) repo = Path.GetDirectoryName(repo)!;
        return CircuitRF.Ui.Tests.Em3d.CliProcess.Run(repo, [], args);
    }
}
