// ================================================================
//  StepSplitTests.cs — the gate for brief-em3d-129: Split into Solids on a Step object that holds a whole part of several
//  solids, as a document imported before brief 128 does. The part is testdata/step/two-solids-one-product.step (127's
//  fixture: 'lead', coloured on the solid, and an unnamed box whose faces disagree), held as ONE object 'pkg' with a material,
//  a placement offset, a face boundary on a face of the first solid, and a probe and a thermal boundary on a face of the second.
//  Which face is whose is decided here by geometry (a face's centroid inside a solid's box), never by number.
// ================================================================

using System.Text.Json;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class StepSplitTests : IDisposable
{
    private static readonly string Repo = CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot();
    private static readonly string Fixture = Path.Combine(Repo, "testdata", "step", "two-solids-one-product.step");
    private static readonly string TwoProducts = Path.Combine(Repo, "testdata", "step", "two-products.step");
    private static readonly C3dPoint3 Offset = new(250_000, -100_000, 50_000);     // DBU: 250, −100, 50 µm

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-step129-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public StepSplitTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. check names it; explain lists the solids ─────────────────────────────────────────

    [KernelFact]
    public void Gate1_CheckWarnsTheColoursDiffer_AndExplainListsBothSolidsWithTheirColours()
    {
        using var kernel = KernelForTests.New();
        var (path, _) = Package(kernel);

        var (_, so, se) = Cli("check", path);
        string check = so + se;
        Assert.Contains("'pkg' is 2 solids of pkg.step part 1 in one object, so they share one material; their colours differ. " +
                        "Split into Solids gives each its own.", check, StringComparison.Ordinal);
        Assert.Contains("warning", check.ToLowerInvariant(), StringComparison.Ordinal);

        var (code, explain, err) = Cli("explain", path, "--object", "pkg");
        Assert.True(code == 0, explain + err);
        Assert.Contains("solid 1", explain, StringComparison.Ordinal);
        Assert.Contains("colour #0000ff, 6 faces", explain, StringComparison.Ordinal);
        Assert.Contains("colour mixed, 6 faces", explain, StringComparison.Ordinal);
    }

    // ── 2 + 3. the split: two pieces at the old index, nothing moves, every reference lands by geometry ──

    [KernelFact]
    public void Gate2And3_TwoPiecesAtTheOldIndex_KeepMaterialAndPlacement_TheExtentIsUnchanged_AndEachReferenceLandsOnItsSolid()
    {
        using var kernel = KernelForTests.New();
        var (path, doc) = Package(kernel);
        var (onLead, onBody) = FacesBySolid(kernel, path);
        doc.FaceBoundaries.Add(new C3dFaceBoundary { Object = "pkg", Face = $"face{onLead}", Kind = Em3dFaceBoundaryKind.Pec });
        doc.Probes.Add(new C3dProbe { Name = "p1", Face = [$"pkg/face{onBody}"], Stat = C3dProbeStat.Max });
        doc.Setups.Add(JsonSerializer.SerializeToElement(new
        {
            Name = "T1", Problem3D = "Thermal",
            Thermal = new { Boundaries = new[] { new { Face = $"pkg/face{onBody}", Kind = "FixedT", TempC = "25" } } },
        }));
        var before = Elaborate(kernel, doc, path);

        var plan = StepSplit.Plan(doc, path, "pkg", kernel);
        Assert.Empty(plan.Refusals);
        var pieces = StepSplit.Apply(plan, doc);

        Assert.Equal(["box", "lead", "pkg_2"], doc.Objects.Select(o => o.Name));     // at the old index, in Solid order
        Assert.Equal([1, 2], pieces.Select(p => p.Solid!.Value));
        Assert.All(pieces, p => Assert.Equal(("Alumina", Offset, "board/pkg", "pkg.step"), (p.Material, p.Placement.Origin, p.Group, p.File)));
        var (a, b) = (before.Extent()!.Value, Elaborate(kernel, doc, path).Extent()!.Value);
        Assert.Equal([a.X0, a.Y0, a.Z0, a.X1, a.Y1, a.Z1], [b.X0, b.Y0, b.Z0, b.X1, b.Y1, b.Z1], (x, y) => Math.Abs(x - y) < 1e-12);

        // By geometry: each reference is on the piece whose box holds the face, on the face that coincides with the old one.
        var fb = Assert.Single(doc.FaceBoundaries);
        Assert.Equal("lead", fb.Object);
        AssertSameFace(kernel, path, onLead, pieces[0], fb.Face);
        string probe = Assert.Single(doc.Probes[0].Face!);
        Assert.StartsWith("pkg_2/", probe, StringComparison.Ordinal);
        AssertSameFace(kernel, path, onBody, pieces[1], probe["pkg_2/".Length..]);
        Assert.Contains($"\"Face\":\"{probe}\"", doc.Setups[0].GetRawText(), StringComparison.Ordinal);
    }

    // ── 4. refusals, each named ─────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate4_ABooleansBlank_AndAReferenceToTheWholeObject_AreRefusedByName()
    {
        using var kernel = KernelForTests.New();
        var (path, doc) = Package(kernel);
        var blank = new C3dStep { File = "pkg.step", Part = "1", Hash = ((C3dStep)doc.Objects[1]).Hash };
        doc.Objects.Add(new C3dBoolean { Name = "b1", Blank = blank, Tools = [new C3dBox { Name = "t1", Size = new(1000, 1000, 1000) }] });
        Assert.Equal("Part 1 of 'pkg.step' is the Blank of 'b1'; split it before it is combined (Dissolve Boolean makes it a top-level object again).",
                     Assert.Single(StepSplit.Plan(doc, path, blank, kernel).Refusals));

        doc.Objects.RemoveAt(2);
        doc.HeatSources.Add(new C3dHeatSource { Name = "h1", Solid = "pkg", Power = "1 W" });
        var plan = StepSplit.Plan(doc, path, "pkg", kernel);
        Assert.Equal("The heat source 'h1' names 'pkg' as a whole, and no one piece can stand for it; remove it, split, then add it back on the piece it means.",
                     Assert.Single(plan.Refusals));
        Assert.Throws<StepImportException>(() => StepSplit.Apply(plan, doc));
    }

    // ── 5 + 6. the command: enablement with its reasons, and one undo entry ─────────────────

    [KernelFact]
    public async Task Gate5And6_TheCommandIsEnabledForAWholePartOnly_EachRefusalSaysWhy_AndOneUndoRestoresTheDocumentByteForByte()
    {
        using var kernel = KernelForTests.New();
        var (path, doc) = Package(kernel);
        File.Copy(TwoProducts, Path.Combine(Path.GetDirectoryName(path)!, "two.step"));
        string onePart = kernel.ImportStep(File.ReadAllBytes(TwoProducts)).Parts.Single(p => p.Solids.Count == 1).Path;
        doc.Objects.Add(new C3dStep { Name = "single", Material = "Alumina", File = "two.step", Part = onePart, Hash = StepImport.HashOf(File.ReadAllBytes(TwoProducts)) });
        doc.Objects.Add(new C3dStep { Name = "piece", Material = "Alumina", File = "pkg.step", Part = "1", Solid = 1, Hash = ((C3dStep)doc.Objects[1]).Hash });
        C3dPersistence.SaveToFile(path, doc);
        var vm = Editor(path, kernel);

        Assert.Null(vm.SplitRefusal(1));
        Assert.Equal("'single' is one solid; there is nothing to split.", vm.SplitRefusal(2));
        Assert.Equal("'piece' is already one solid (solid 1) of its part.", vm.SplitRefusal(3));
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("pkg")!.Id), Scene3DItem.OfObject(vm.SceneObject("single")!.Id)]);
        Assert.Equal("Select exactly one object: Split into Solids acts on one STEP object.", vm.SplitSelectionRefusal());
        vm.Viewer.SetSelection([]);

        string original = C3dPersistence.Serialize(vm.Document);
        int entries = vm.UndoEntries;
        await vm.SplitIntoSolidsAsync(1);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(["box", "lead", "pkg_2", "single", "piece"], vm.Document.Objects.Select(o => o.Name));
        Assert.Equal("Undo \"Split pkg into 2 solids\"", vm.UndoRedo.UndoDescription);
        vm.UndoRedo.Undo();
        Assert.Equal(original, C3dPersistence.Serialize(vm.Document));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    /// <summary>A workspace holding a 3D view of 'box' and the package as ONE whole-part object 'pkg' in group 'board',
    /// offset and given a material — the document an import before brief 128 left.</summary>
    private (string Path, C3dDocument Doc) Package(GeometryKernel kernel)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Alumina", Epsr = 9.8 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Pkg", "3d");
        Directory.CreateDirectory(dir);
        File.Copy(Fixture, Path.Combine(dir, "pkg.step"));
        string path = Path.Combine(dir, "Pkg.c3d");
        var doc = new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "box", Material = "Copper", Min = new(-5_000_000, 0, 0), Size = new(500_000, 500_000, 500_000) },
                new C3dStep
                {
                    Name = "pkg", Material = "Alumina", Group = "board", File = "pkg.step", Part = "1", Unit = "millimetre",
                    Hash = StepImport.HashOf(File.ReadAllBytes(Fixture)), Placement = new() { Origin = Offset },
                },
            ],
        };
        C3dPersistence.SaveToFile(path, doc);
        return (path, doc);
    }

    /// <summary>A face of the whole part inside the first solid's box only, and one inside the second's only.</summary>
    private static (int Lead, int Body) FacesBySolid(GeometryKernel kernel, string path)
    {
        string file = Path.Combine(Path.GetDirectoryName(path)!, "pkg.step");
        var solids = kernel.ImportStep(File.ReadAllBytes(file)).Parts.Single().Solids;
        var faces = kernel.Faces(WholeTree(file));
        bool In(double[] c, double[] b) => Enumerable.Range(0, 3).All(i => c[i] >= b[i] - 1e-3 && c[i] <= b[i + 3] + 1e-3);
        int Only(int k) => 1 + Enumerable.Range(0, faces.Count).First(i => In(faces[i].Centroid, solids[k].BoxUm) && !In(faces[i].Centroid, solids[1 - k].BoxUm));
        return (Only(0), Only(1));
    }

    private static GeometryKernelTree WholeTree(string file, int? solid = null)
        => GeometryKernelTree.From(new C3dStep { Name = "w", File = Path.GetFullPath(file), Hash = StepImport.HashOf(File.ReadAllBytes(file)), Part = "1", Solid = solid }, 1000);

    /// <summary>The piece's face <paramref name="face"/> has the old whole face's centroid, area and kind.</summary>
    private static void AssertSameFace(GeometryKernel kernel, string path, int wholeFace, C3dStep piece, string face)
    {
        string file = Path.Combine(Path.GetDirectoryName(path)!, "pkg.step");
        var was = kernel.Faces(WholeTree(file))[wholeFace - 1];
        var now = kernel.Faces(WholeTree(file, piece.Solid))[int.Parse(face["face".Length..]) - 1];
        Assert.Equal(was.Kind, now.Kind);
        Assert.Equal(was.Area, now.Area, 1e-6 * was.Area);
        for (int i = 0; i < 3; i++) Assert.Equal(was.Centroid[i], now.Centroid[i], 1e-3);
    }

    private static C3dElaboration Elaborate(GeometryKernel kernel, C3dDocument doc, string path)
    {
        var e = new C3dElaborator(null, kernel).Elaborate(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        return e;
    }

    private C3dEditorViewModel Editor(string path, GeometryKernel kernel)
    {
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!, ".cws");
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => cws, _posted.Enqueue, kernel: kernel);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Elaboration is not null;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
        return vm;
    }

    private static (int ExitCode, string StdOut, string StdErr) Cli(params string[] args) => CircuitRF.Ui.Tests.Em3d.CliProcess.Run(Repo, [], args);
}
