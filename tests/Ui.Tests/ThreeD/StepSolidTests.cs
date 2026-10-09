// ================================================================
//  StepSolidTests.cs — the gate for brief-em3d-127: one solid of a STEP part, addressed by C3dStep.Solid.
//  The fixture is hand-written AP214 text, testdata/step/two-solids-one-product.step: one PRODUCT holding two
//  MANIFOLD_SOLID_BREP boxes — the first coloured on the solid, the second on two faces that disagree — and a revised
//  copy that moves the second box and lists it first. No third-party STEP file.
// ================================================================

using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class StepSolidTests : IDisposable
{
    private static readonly string Fixture = Path.Combine(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), "testdata", "step", "two-solids-one-product.step");
    private static readonly string Revised = Path.Combine(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), "testdata", "step", "two-solids-one-product-revised.step");
    private const double Mm3 = 1e9;                       // µm³ per mm³
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-step127-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. import-step lists each solid ─────────────────────────────────────────────────────

    [KernelFact]
    public void Gate1_OnePartOfTwoSolids_ListsEachWithItsFacesVolumeAndColour_TheSecondMixed()
    {
        using var kernel = KernelForTests.New();
        var part = Assert.Single(kernel.ImportStep(File.ReadAllBytes(Fixture)).Parts);
        Assert.Equal(2, part.Solids.Count);
        var (a, b) = (part.Solids[0], part.Solids[1]);
        Assert.Equal((1, 6, true), (a.Index, a.Faces, a.Closed));
        Assert.Equal(1 * Mm3, a.VolumeUm3, 1e-6 * Mm3);   // 2 × 1 × 0.5 mm
        Assert.Equal([0.0, 0.0, 1.0], a.Colour);
        Assert.False(a.Mixed);
        Assert.Equal("lead", a.Name);
        Assert.Equal((2, 6, true), (b.Index, b.Faces, b.Closed));
        Assert.Equal(2 * Mm3, b.VolumeUm3, 1e-6 * Mm3);   // 1 × 2 × 1 mm
        Assert.Null(b.Colour);
        Assert.True(b.Mixed);
    }

    // ── 2. Solid = k builds that solid; past the last is refused; no Solid is unchanged ──────

    [KernelFact]
    public void Gate2_EachSolidBuildsAlone_OnePastTheLastIsRefused_AndTheWholePartKeepsItsCacheKey()
    {
        using var kernel = KernelForTests.New();
        string hash = StepImport.HashOf(File.ReadAllBytes(Fixture));
        GeometryKernelTree Tree(int? solid) =>
            GeometryKernelTree.From(new C3dStep { Name = "p", File = Path.GetFullPath(Fixture), Hash = hash, Part = "1", Solid = solid }, 1000);

        foreach (var (k, volume) in new[] { (1, 1 * Mm3), (2, 2 * Mm3) })
        {
            var built = kernel.Build(Tree(k));
            Assert.Equal((true, 1, 6), (built.Valid, built.Solids, built.Faces));
            Assert.Equal(volume, built.VolumeUm3, 1e-6 * Mm3);
        }
        var ex = Assert.Throws<GeometryKernelException>(() => kernel.Build(Tree(3)));
        Assert.Contains("the part has 2 solids; there is no solid 3", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, kernel.Build(Tree(null)).Solids);

        // A Step object with no Solid lowers to the tree it lowered to before brief 127: the same text, so the same key.
        var whole = GeometryKernelTree.From(new C3dStep { Name = "shell", File = "/k/x.step", Hash = "sha256:00", Part = "1/2" }, 1000);
        Assert.Equal("""{"tree":1,"root":{"kind":"step","name":"shell","transform":[1,0,0,0,0,1,0,0,0,0,1,0],"file":"/k/x.step","hash":"sha256:00","part":"1/2"}}""" + "\n",
                     whole.Json);
        // SHA-256 of that text, taken outside circuitRF: the handle a cached build is held under.
        Assert.Equal("3b82ae1ed55c45c4e4ea392e05097ca75401c93d1dd0bc9e281e943c69ffcf2d", GeometryKernel.HandleOf(whole));
    }

    // ── 3. pieces of one file read it once ──────────────────────────────────────────────────

    [KernelFact]
    public void Gate3_TwoPiecesOfOneFile_ReadTheFileOnce()
    {
        using var w = new GeometryKernelProcessWorker(KernelForTests.Capability.WorkerPath!);
        string hash = StepImport.HashOf(File.ReadAllBytes(Fixture));
        long Reads() => w.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "hello" })).Json["step_reads"]!.GetValue<long>();
        long before = Reads();
        foreach (int k in (int[])[1, 2])
        {
            var tree = GeometryKernelTree.From(new C3dStep { Name = $"s{k}", File = Path.GetFullPath(Fixture), Hash = hash, Part = "1", Solid = k }, 1000);
            var reply = w.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "build", ["shape"] = $"h{k}", ["tree"] = JsonNode.Parse(tree.Json) }));
            Assert.True(reply.Ok, reply.Json.ToJsonString());
        }
        Assert.Equal(before + 1, Reads());
    }

    // ── 4. the format: Solid written only when set ──────────────────────────────────────────

    [Fact]
    public void Gate4_ADocumentWithoutSolid_RoundTripsByteForByte_AndOneWithSolidKeepsIt()
    {
        var doc = new C3dDocument { Objects = [new C3dStep { Name = "pkg", Material = "Copper", File = "x.step", Part = "1", Hash = "sha256:00" }] };
        string text = C3dPersistence.Serialize(doc);
        Assert.DoesNotContain("Solid", text, StringComparison.Ordinal);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));

        ((C3dStep)doc.Objects[0]).Solid = 2;
        string with = C3dPersistence.Serialize(doc);
        Assert.Equal(2, ((C3dStep)C3dPersistence.Deserialize(with).Objects[0]).Solid);
        Assert.Equal(with, C3dPersistence.Serialize(C3dPersistence.Deserialize(with)));

        ((C3dStep)doc.Objects[0]).Solid = 0;
        Assert.Contains(C3dValidation.Validate(doc), d => d.Render().Contains("Solid 0", StringComparison.Ordinal));
    }

    // ── 5. reload matches solids by geometry, never by index ────────────────────────────────

    [KernelFact]
    public void Gate5_Reload_RepointsTheUnchangedSolidByGeometry_AndRefusesTheMovedOne_KeepingIt()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string source = Path.Combine(ws, "incoming", "pkg.step");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Copy(Fixture, source);
        string dir = Path.Combine(ws, "Pkg", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Pkg.c3d");
        File.Copy(Fixture, Path.Combine(dir, "pkg.step"));
        string hash = StepImport.HashOf(File.ReadAllBytes(Fixture));
        C3dStep Piece(string name, int k) => new()
        {
            Name = name, Material = "Copper", File = "pkg.step", Part = "1", Solid = k, Hash = hash, Unit = "millimetre", SourcePath = source,
        };
        var doc = new C3dDocument { Objects = [Piece("lead", 1), Piece("body", 2)] };
        doc.FaceBoundaries.Add(new C3dFaceBoundary { Object = "lead", Face = "face2", Kind = Em3dFaceBoundaryKind.Pec });

        File.Copy(Revised, source, overwrite: true);
        var plan = StepImport.PlanReload(doc, path, "pkg.step", kernel);
        var lead = Assert.Single(plan.Accepted);
        Assert.Equal("lead", lead.Name);
        Assert.Equal(new StepSolidMove("lead", "1", 1, 2), Assert.Single(plan.SolidMoves));
        Assert.Empty(plan.Repoints);                      // the box's own faces kept their order
        string refusal = Assert.Single(plan.Refusals);
        Assert.StartsWith("'body' is solid 2 of part 1 of 'pkg.step', which the revised file no longer has", refusal, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(plan.NewSolids).Solid.Index);   // the moved box, offered

        StepImport.ApplyReload(plan, doc, path);
        Assert.Equal((2, "pkg_2.step"), (lead.Solid, lead.File));
        var body = (C3dStep)doc.Objects[1];
        Assert.Equal((2, "pkg.step", hash), (body.Solid, body.File, body.Hash));
        var e = new C3dElaborator(null, kernel).Elaborate(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Equal(1e-9, Volume(e, "lead"), 1e-15);     // m³: the same 1 mm³ box, from the revised copy
        Assert.Equal(2e-9, Volume(e, "body"), 1e-15);
    }

    // ── 6. export writes a piece as one part of one solid ───────────────────────────────────

    [KernelFact]
    public void Gate6_APieceExportedAndReimported_IsOnePartOfOneSolid_OfTheSameVolume()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string dir = Path.Combine(ws, "Piece", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Piece.c3d");
        File.Copy(Fixture, Path.Combine(dir, "pkg.step"));
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dStep { Name = "body", Material = "Copper", File = "pkg.step", Part = "1", Solid = 2, Hash = StepImport.HashOf(File.ReadAllBytes(Fixture)) }],
        });

        string step = Path.Combine(_root, "piece.step");
        StepExport.Export(path, step, new StepExportOptions(), kernel);
        var part = Assert.Single(kernel.ImportStep(File.ReadAllBytes(step)).Parts);
        var solid = Assert.Single(part.Solids);
        Assert.Equal(2 * Mm3, solid.VolumeUm3, 1e-6 * Mm3);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, Color = "#b87333" }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static double Volume(C3dElaboration e, string solid)
    {
        var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(e.Solids.Single(s => s.Name == solid).Primitive);
        return (x1 - x0) * (y1 - y0) * (z1 - z0);
    }
}
