// ================================================================
//  StepImportSolidsTests.cs — the gate for brief-em3d-128: a STEP product of several solids imports as one Step object per
//  solid, every object of the import gathered in one group named after the file. Fixtures are hand-written AP214 text:
//  testdata/step/two-solids-one-product.step (127's — 'lead', blue on the solid, and an unnamed box whose faces are red
//  and green, so mixed) and testdata/step/two-products.step (that product, plus 'pad', one blue box). No third-party file.
// ================================================================

using System.Diagnostics;
using System.Reflection;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class StepImportSolidsTests : IDisposable
{
    private static readonly string TestData = Path.Combine(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), "testdata", "step");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-step128-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. the plan: one row per solid, a header over a product of several ─────────────────

    [KernelFact]
    public async Task Gate1_APartOfTwoSolidsIsTwoRows_AndAFileOfTwoProductsIsThreeRows_TwoUnderAHeader()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var plan = StepImport.Read(Source(ws, "two-solids-one-product.step"), kernel, Tech(ws), new C3dDocument());
        Assert.Equal([(1, "#0000ff", false), (2, null, true)], plan.Parts.Select(p => (p.Solid!.Value, p.Colour, p.Mixed)));
        Assert.Equal(["lead", "two-solids-one-product_2"], plan.Parts.Select(p => p.Name));
        Assert.Equal("two-solids-one-product", plan.Group);

        string two = Source(ws, "two-products.step");
        var doc = new C3dDocument();
        var vm = new StepImportDialogViewModel(two, control => StepImport.Read(two, kernel, Tech(ws), doc, control), _ => null);
        await vm.StartAsync();
        Assert.Equal([("1", (int?)1), ("1", 2), ("2", null)], vm.Plan!.Parts.Select(p => (p.Path, p.Solid)));
        var header = Assert.Single(vm.Products);
        Assert.Equal(("two-solids", "2 solids"), (header.ProductName, header.SolidsText));
        Assert.Equal([header, vm.Rows[0], vm.Rows[1], vm.Rows[2]], vm.Table);
        Assert.True(vm.ShowGroup);
        Assert.Equal("two-products", vm.GroupName);
    }

    // ── 2. the group: the file's, nested for a product of several in a file of several ──────

    [KernelFact]
    public void Gate2_ApplyGathersTheSolidsInTheFilesGroup_NestsAProductOfSeveral_AndASingleSolidFileIsAsBefore()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var objects = Import(kernel, ws, "two-solids-one-product.step", "A").Objects;
        Assert.Equal([(1, "two-solids-one-product"), (2, "two-solids-one-product")], objects.Select(o => (o.Solid!.Value, o.Group!)));

        var both = Import(kernel, ws, "two-products.step", "B").Objects;
        Assert.Equal([("lead", "two-products/two-solids"), ("two-solids_2", "two-products/two-solids"), ("pad", "two-products")],
                     both.Select(o => (o.Name, o.Group!)));
        Assert.Null(both[2].Solid);

        // One box, written by circuitRF's own worker as brief 68's fixtures are: one object, no Group, no Solid — the
        // object brief 68 wrote, key for key.
        string src = Path.Combine(ws, "incoming", "blk.step");
        File.WriteAllBytes(src, kernel.Export([new(GeometryKernelTree.From(
            new C3dBox { Name = "blk", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(10_000, 10_000, 10_000) }, 1000), "blk")], "step", "mm"));
        var (doc, path) = NewC3d(ws, "C");
        var plan = StepImport.Read(src, kernel, Tech(ws), doc);
        var only = (C3dStep)Assert.Single(StepImport.Apply(plan, doc, path).Objects);
        var brief68 = new C3dStep { Name = "blk", File = "blk.step", Part = "1", Hash = plan.Hash, Unit = plan.Unit, SourcePath = only.SourcePath };
        Assert.Equal(C3dPersistence.Serialize(new C3dDocument { Objects = [brief68] }), C3dPersistence.Serialize(new C3dDocument { Objects = [only] }));
    }

    // ── 3. no group on request; a name another group has is refused ────────────────────────

    [KernelFact]
    public void Gate3_AnEmptyGroupNameWritesNoGroup_AndAGroupNameAlreadyInUseIsRefused()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var (doc, path) = NewC3d(ws, "A");
        var plan = StepImport.Read(Source(ws, "two-solids-one-product.step"), kernel, Tech(ws), doc);
        plan.Group = "";
        Assert.All(StepImport.Apply(plan, doc, path).Objects, o => Assert.Null(o.Group));

        var (taken, takenPath) = NewC3d(ws, "B");
        taken.Objects.Add(new C3dBox { Name = "lid", Group = "Pkg", Size = new C3dPoint3(1000, 1000, 1000) });
        plan = StepImport.Read(Source(ws, "two-solids-one-product.step"), kernel, Tech(ws), taken);
        plan.Group = "Pkg";
        string why = "'Pkg' is already the name of a group in this 3D view.";
        Assert.Equal(why, StepImport.GroupRefusal(plan, taken));
        Assert.Equal(why, Assert.Throws<StepImportException>(() => StepImport.Apply(plan, taken, takenPath)).Message);
        Assert.Single(taken.Objects);
    }

    // ── 4. materials across solids ─────────────────────────────────────────────────────────

    [KernelFact]
    public async Task Gate4_MapAllOfThisColourCrossesProducts_TheHeaderSetsItsSolids_AndAMixedSolidIsUnmatched()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string src = Source(ws, "two-products.step");
        var vm = new StepImportDialogViewModel(src, control => StepImport.Read(src, kernel, Tech(ws), new C3dDocument(), control), _ => null);
        await vm.StartAsync();
        var (lead, body, pad) = (vm.Rows[0], vm.Rows[1], vm.Rows[2]);
        Assert.Equal(StepMatch.Unmatched, body.Part.Match);            // red and green faces; Brass is #ff0000, and not matched

        lead.Material = "Copper";
        lead.MapAllOfThisColourCommand.Execute(null);                  // lead and pad are both blue
        Assert.Equal([("Copper", StepMatch.Chosen), (null, StepMatch.Unmatched), ("Copper", StepMatch.Chosen)],
                     vm.Plan!.Parts.Select(p => (p.Material, p.Match)));

        vm.Products[0].Material = "Alumina";
        Assert.Equal([("Alumina", StepMatch.Chosen), ("Alumina", StepMatch.Chosen), ("Copper", StepMatch.Chosen)],
                     vm.Plan.Parts.Select(p => (p.Material, p.Match)));
        Assert.Equal(["Alumina", "Alumina"], new[] { lead.Material, body.Material });
    }

    // ── 5. convert, as a process ───────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate5_ConvertWritesTheDialogDefaultsByteForByte_OneSolidHasNoGroup_AndAnEmptyGroupIsNone()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        string src = Source(ws, "two-solids-one-product.step");

        string viaCli = Path.Combine(ws, "A", "3d", "A.c3d");
        var run = RunCli("convert", src, "-o", viaCli, "--json");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        string inProcess = Path.Combine(ws, "B", "3d", "B.c3d");
        StepImport.Import(src, inProcess, new StepImportOptions(), kernel);
        Assert.Equal(File.ReadAllBytes(inProcess), File.ReadAllBytes(viaCli));
        Assert.Matches("\"group\":\\s*\"two-solids-one-product\"", run.StdOut);
        Assert.Matches("\"solid\":\\s*2", run.StdOut);

        Assert.Equal(0, RunCli("convert", src, "-o", Path.Combine(ws, "C", "3d", "C.c3d"), "--part", "1#2").ExitCode);
        var one = (C3dStep)Assert.Single(C3dPersistence.LoadFromFile(Path.Combine(ws, "C", "3d", "C.c3d")).Objects);
        Assert.Equal((2, (string?)null), (one.Solid!.Value, one.Group));

        Assert.Equal(0, RunCli("convert", src, "-o", Path.Combine(ws, "D", "3d", "D.c3d"), "--group", "").ExitCode);
        var loose = C3dPersistence.LoadFromFile(Path.Combine(ws, "D", "3d", "D.c3d")).Objects;
        Assert.Equal(2, loose.Count);
        Assert.All(loose, o => Assert.Null(o.Group));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private StepImportResult Import(GeometryKernel kernel, string ws, string fixture, string cell)
    {
        var (doc, path) = NewC3d(ws, cell);
        return StepImport.Apply(StepImport.Read(Source(ws, fixture), kernel, Tech(ws), doc), doc, path);
    }

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(ws, "incoming"));
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Alumina", Epsr = 9.8 },
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, Color = "#b87333" },
                new TechMaterial { Name = "Brass", Sigma20 = 1.5e7, Color = "#FF0000" },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static Technology Tech(string ws) => TechPersistence.LoadFromFile(Path.Combine(ws, "tech.ctech"));

    /// <summary>A fixture copied into the workspace under its own name: the file's stem is what the group is named after.</summary>
    private static string Source(string ws, string fixture)
    {
        string path = Path.Combine(ws, "incoming", fixture);
        if (!File.Exists(path)) File.Copy(Path.Combine(TestData, fixture), path);
        return path;
    }

    private static (C3dDocument Doc, string Path) NewC3d(string ws, string cell)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        return (new C3dDocument(), Path.Combine(dir, cell + ".c3d"));
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(StepImportSolidsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = Path.Combine(_root, "state");
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
