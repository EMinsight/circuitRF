// ================================================================
//  ExpressionsGateTests.cs — brief-em3d-51 §8, the headless gates: dimensions as expressions, VARs in the .c3d, cell
//  parameters passed down. Round trip (1), the display unit is free (2), linked vs unlinked (3), the unit trap (6),
//  scope and overrides (7), cycles (8), rename through the AST (9), the caches (10), and check/explain as a process.
//  The editor's gates (4, 5, 11) are ExpressionsEditorGateTests.cs.
// ================================================================

using System.Diagnostics;
using System.Reflection;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ExpressionsGateTests : IDisposable
{
    private const double Mil = 25.4e-6;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-expr51-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── gate 1 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_ExpressionsVarsLinksAndOverrides_RoundTripByteForByte()
    {
        var doc = new C3dDocument { DisplayUnit = LayoutUnit.Mil };
        var box = Box("lid", 0, 0, 0, 10, 10, 10);
        Bind(box, "Size", 0, "w", "Mil");
        Bind(box, "Size", 2, "h_sub + t_met", "Um");
        box.Placement = new C3dPlacement { Rotate = [new C3dRotation { Axis = C3dAxis.Z }] };
        Bind(box.Placement.Rotate[0], "Deg", 0, "ang", "Deg");
        doc.Objects.Add(box);
        doc.Variables =
        [
            new C3dVariable { Name = "w", Expression = "10", Unit = "Mil" },
            new C3dVariable { Name = "h_sub", Expression = "10", Unit = "Mil", Linked = true },
            new C3dVariable { Name = "gap", Expression = "w / 4" },
            new C3dVariable { Name = "t_met", Expression = "3", Unit = "Um", Linked = false },
            new C3dVariable { Name = "ang", Expression = "45" },
        ];
        var inst = new C3dInstance { Name = "U1", CellRef = "../../Lid", Params = new() { ["w"] = new C3dExpr("2*gap", "Mil") }, Array = new C3dArray { Counts = [2, 1, 1] } };
        Bind(inst.Array, "Counts", 0, "n", null);
        doc.Instances.Add(inst);

        string text = C3dPersistence.Serialize(doc);
        Assert.Contains("\"Size\": [{ \"Expr\": \"w\", \"Unit\": \"Mil\" }, 10000, { \"Expr\": \"h_sub + t_met\", \"Unit\": \"Um\" }]", text);
        Assert.Contains("\"w\": { \"Expr\": \"2*gap\", \"Unit\": \"Mil\" }", text);
        Assert.DoesNotContain("\"Value\"", text);                                // the resolved number is a cache, not the file
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));
    }

    // ── gate 2 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_TheDisplayUnitIsFree_ChangingItMovesNoElaboratedCoordinate()
    {
        string ws = Workspace();
        var doc = new C3dDocument { DisplayUnit = LayoutUnit.Mil };
        doc.Variables = [new C3dVariable { Name = "w", Expression = "10", Unit = "Mil" }, new C3dVariable { Name = "t", Expression = "w / 4" }];
        var a = Box("a", 0, 0, 0, 1, 1, 1);
        Bind(a, "Size", 0, "w", "Mil"); Bind(a, "Size", 1, "2*t", "Mil"); Bind(a, "Size", 2, "7", "Mil");
        var c = new C3dCylinder { Name = "via", Material = "Gold", Length = 1000 };
        Bind(c, "Radius", 0, "t", "Mil"); Bind(c, "Length", 0, "3", "Mil");
        doc.Objects.AddRange([a, c]);
        string path = WriteC3d(ws, "Stub", doc);

        var mil = Bounds(C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(path), path, null));
        var loaded = C3dPersistence.LoadFromFile(path);
        loaded.DisplayUnit = LayoutUnit.Um;
        C3dPersistence.SaveToFile(path, loaded);
        var um = Bounds(C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(path), path, null));

        Assert.Equal(mil.Count, um.Count);
        foreach (var (name, b) in mil)
            for (int k = 0; k < 6; k++) Assert.Equal(b[k], um[name][k], Math.Abs(b[k]) * 1e-15);
        Assert.Equal(10 * Mil, mil["a"][3] - mil["a"][0], 1e-15);
    }

    // ── gate 3 ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 10, 20)]
    [InlineData(false, 12, 12)]
    public void Gate3_LinkedTakesTheParameter_DefaultIncluded_UnlinkedKeepsItsOwn_AndCheckWarns(bool linked, double plain, double overridden)
    {
        string ws = Workspace();
        Cell(ws, "Lid", ("w", "10", "mil"));
        var lid = new C3dDocument { Variables = [new C3dVariable { Name = "w", Expression = "12", Unit = "Mil", Linked = linked ? null : false }] };
        var b = Box("lid", 0, 0, 0, 1, 1, 1);
        Bind(b, "Size", 0, "w", "Mil");
        lid.Objects.Add(b);
        string lidPath = WriteC3d(ws, "Lid", lid);
        string top = WriteC3d(ws, "Top", new C3dDocument
        {
            Instances =
            [
                new C3dInstance { Name = "U1", CellRef = "../../Lid" },
                new C3dInstance { Name = "U2", CellRef = "../../Lid", Params = new() { ["w"] = new C3dExpr("20", "Mil") } },
            ],
        });

        var e = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(top), top, null);
        Assert.True(e.Ok, string.Join(" | ", e.Refusals));
        var bounds = Bounds(e);
        Assert.Equal(plain * Mil, bounds["U1/lid"][3] - bounds["U1/lid"][0], 1e-12);
        Assert.Equal(overridden * Mil, bounds["U2/lid"][3] - bounds["U2/lid"][0], 1e-12);

        var own = C3dResolver.Resolve(C3dPersistence.LoadFromFile(lidPath), C3dCell.Of(lidPath));
        Assert.Equal(!linked, own.Warnings.Any(w => w.Contains("VAR 'w' hides cell parameter 'w'", StringComparison.Ordinal)));
    }

    // ── gate 6 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_ALiteralBesideAUnitBearingName_IsMetres_AndIsWarnedAbout()
    {
        var doc = new C3dDocument { Variables = [new C3dVariable { Name = "w", Expression = "10", Unit = "Mil" }] };
        var b = Box("slab", 0, 0, 0, 1, 1, 1);
        Bind(b, "Size", 0, "2*w + 5", "Mil");
        doc.Objects.Add(b);
        var r = C3dResolver.Resolve(doc, C3dCell.None);
        Assert.Equal(5.000508, r.FieldValues[("slab", "Size[0]")], 1e-12);
        Assert.Equal(5.000508, r.Evaluate("2*w + 5", "Mil").AsReal(), 1e-12);        // what the typed field previews
        Assert.Contains(r.Warnings, w => w.Contains("above 1 m", StringComparison.Ordinal));
    }

    // ── gate 7 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_AnOverrideResolvesInTheParentsScope_AndAVarOnlyNameIsRefused()
    {
        string ws = Workspace();
        Cell(ws, "Lid", ("w", "10", "mil"));
        var lid = new C3dDocument { Variables = [new C3dVariable { Name = "lid_t", Expression = "1", Unit = "Mil" }] };
        var b = Box("lid", 0, 0, 0, 1, 1, 1);
        Bind(b, "Size", 0, "w", "Mil"); Bind(b, "Size", 2, "lid_t", "Mil");
        lid.Objects.Add(b);
        WriteC3d(ws, "Lid", lid);
        var parent = new C3dDocument
        {
            Variables = [new C3dVariable { Name = "a", Expression = "3", Unit = "Mil" }],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Lid", Params = new() { ["w"] = new C3dExpr("2*a", "Mil") } }],
        };
        string top = WriteC3d(ws, "Top", parent);
        var e = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(top), top, null);
        Assert.True(e.Ok, string.Join(" | ", e.Refusals));
        var bounds = Bounds(e)["U1/lid"];
        Assert.Equal(6 * Mil, bounds[3] - bounds[0], 1e-12);

        parent.Instances[0].Params!["lid_t"] = new C3dExpr("2", "Mil");
        C3dPersistence.SaveToFile(top, parent);
        var refused = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(top), top, null);
        Assert.Contains(refused.Refusals, x => x.Contains("'lid_t', which is a VAR of 'Lid''s 3D view, not a parameter; promote it there to override it here", StringComparison.Ordinal));
    }

    // ── gate 8 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_CyclesThroughADefaultALinkedVarAndAnOverride_AreRefusedWithTheChain_BeforeAnyGeometry()
    {
        string ws = Workspace();
        Cell(ws, "Lid", ("w", "2*h", "mil"));
        var lid = new C3dDocument { Variables = [new C3dVariable { Name = "w", Expression = "1", Unit = "Mil" }, new C3dVariable { Name = "h", Expression = "w / 2" }] };
        var b = Box("lid", 0, 0, 0, 1, 1, 1);
        Bind(b, "Size", 0, "w", "Mil");
        lid.Objects.Add(b);
        string lidPath = WriteC3d(ws, "Lid", lid);
        var own = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(lidPath), lidPath, null);
        Assert.Empty(own.Solids);
        Assert.Contains(own.Refusals, x => x.Contains("w → h → w", StringComparison.Ordinal) || x.Contains("h → w → h", StringComparison.Ordinal));

        // In the parent: the override's own names cycle.
        string top = WriteC3d(ws, "Top", new C3dDocument
        {
            Variables = [new C3dVariable { Name = "a", Expression = "b" }, new C3dVariable { Name = "b", Expression = "a" }],
            Objects = [Box("base", 0, 0, 0, 5, 5, 5)],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Lid", Params = new() { ["w"] = new C3dExpr("a", "Mil") } }],
        });
        var e = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(top), top, null);
        Assert.Empty(e.Solids);
        Assert.Contains(e.Refusals, x => x.Contains("a → b → a", StringComparison.Ordinal) || x.Contains("b → a → b", StringComparison.Ordinal));
    }

    // ── gate 9 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_RenameGoesThroughTheAst_WwIsLeftAlone()
    {
        var doc = new C3dDocument
        {
            Variables = [new C3dVariable { Name = "w", Expression = "1", Unit = "Mil" }, new C3dVariable { Name = "ww", Expression = "2", Unit = "Mil" },
                         new C3dVariable { Name = "gap", Expression = "w/4 + ww" }],
        };
        var b = Box("b", 0, 0, 0, 1, 1, 1);
        Bind(b, "Size", 0, "w + ww", "Mil"); Bind(b, "Size", 1, "max(w, ww)", "Mil");
        doc.Objects.Add(b);
        Assert.Null(C3dVariableEdits.Rename(doc, C3dCell.None, "w", "width"));
        Assert.Equal("width + ww", C3dBindings.GetExpr(b, "Size", 0)!.Expr);
        Assert.Equal("max(width, ww)", C3dBindings.GetExpr(b, "Size", 1)!.Expr);
        Assert.Equal("width/4 + ww", doc.Variables[2].Expression);
        Assert.Equal(["width", "ww", "gap"], doc.Variables.Select(v => v.Name));
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);
    }

    // ── gate 10 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_ChangingWReelaboratesExactlyItsObjects_AndEqualOverridesShareOneChild()
    {
        string ws = Workspace();
        var doc = new C3dDocument { Variables = [new C3dVariable { Name = "w", Expression = "10", Unit = "Mil" }] };
        var a = Box("a", 0, 0, 0, 1, 1, 1); Bind(a, "Size", 0, "w", "Mil");
        var b = Box("b", 0, 0, 0, 1, 1, 1); Bind(b, "Size", 1, "w / 2", "Mil");
        doc.Objects.AddRange([a, b, Box("c", 0, 0, 0, 3, 3, 3)]);
        string path = WriteC3d(ws, "Stub", doc);
        var el = new C3dElaborator();
        el.Elaborate(C3dPersistence.LoadFromFile(path), path, null);
        long before = el.ObjectsElaborated;
        doc.Variables[0].Expression = "11";
        el.Elaborate(C3dPersistence.Deserialize(C3dPersistence.Serialize(doc)), path, null);
        Assert.Equal(2, el.ObjectsElaborated - before);

        Cell(ws, "Lid", ("w", "10", "mil"));
        var lid = new C3dDocument();
        var l = Box("lid", 0, 0, 0, 1, 1, 1); Bind(l, "Size", 0, "w", "Mil");
        lid.Objects.Add(l);
        WriteC3d(ws, "Lid", lid);
        string top = WriteC3d(ws, "Top", new C3dDocument
        {
            Variables = [new C3dVariable { Name = "a", Expression = "5", Unit = "Mil" }],
            Instances =
            [
                new C3dInstance { Name = "U1", CellRef = "../../Lid", Params = new() { ["w"] = new C3dExpr("2*a", "Mil") } },
                new C3dInstance { Name = "U2", CellRef = "../../Lid", Params = new() { ["w"] = new C3dExpr("10", "Mil") } },
                new C3dInstance { Name = "U3", CellRef = "../../Lid", Params = new() { ["w"] = new C3dExpr("20", "Mil") } },
            ],
        });
        var el2 = new C3dElaborator();
        Assert.True(el2.Elaborate(C3dPersistence.LoadFromFile(top), top, null).Ok);
        Assert.Equal(2, el2.ChildrenResolved);                                  // U1 and U2 share one: both are 10 mil
        Assert.Equal(1, el2.ChildrenElaborated);                                // the file is read once
    }

    // ── check and explain, as a process ───────────────────────────────────────────────────────

    [Fact]
    public void Check_ReportsUndefinedShadowingLargeAndUnused_AndExplainEvaluatesInTheDocumentsScope()
    {
        string ws = Workspace();
        Cell(ws, "Lid", ("w", "10", "mil"));
        var doc = new C3dDocument
        {
            Variables =
            [
                new C3dVariable { Name = "w", Expression = "12", Unit = "Mil", Linked = false },
                new C3dVariable { Name = "spare", Expression = "1", Unit = "Mil" },
            ],
        };
        var big = Box("big", 0, 0, 0, 1, 1, 1); Bind(big, "Size", 0, "2*w + 5", "Mil");
        var lost = Box("lost", 0, 0, 0, 1, 1, 1); Bind(lost, "Size", 1, "nowhere", "Mil");
        doc.Objects.AddRange([big, lost]);
        string path = WriteC3d(ws, "Lid", doc);

        var check = RunCli("check", path);
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("unknown: nowhere", check.StdErr);
        Assert.Contains("VAR 'w' hides cell parameter 'w'", check.StdErr);
        Assert.Contains("above 1 m", check.StdErr);
        Assert.Contains("VAR 'spare' is used by no dimension", check.StdErr);

        lost.Exprs = null;
        C3dPersistence.SaveToFile(path, doc);
        var explain = RunCli("explain", path, "--expr", "2*w", "--set", "w=20 * 25.4e-6", "--json");
        Assert.Equal(0, explain.ExitCode);
        Assert.Contains("\"real\": 0.001016", explain.StdOut.Replace("\"Real\"", "\"real\""), StringComparison.OrdinalIgnoreCase);
        var names = RunCli("explain", path);
        Assert.Contains("name w", names.StdOut + names.StdErr);
        Assert.Contains("an UNLINKED VAR, hiding the cell parameter", names.StdOut + names.StdErr);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(string name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = "Gold", Min = new C3dPoint3(x * 1000, y * 1000, z * 1000), Size = new C3dPoint3(sx * 1000, sy * 1000, sz * 1000) };

    private static void Bind(IC3dBindable owner, string property, int k, string expr, string? unit)
        => C3dBindings.SetExpr(owner, C3dBindings.SpecOf(owner.GetType(), property)!, k, new C3dExpr(expr, unit));

    private static Dictionary<string, double[]> Bounds(C3dElaboration e)
        => e.Solids.ToDictionary(s => s.Name, s => { var b = Em3dProblem.Bounds(s.Primitive); return new[] { b.X0, b.Y0, b.Z0, b.X1, b.Y1, b.Z1 }; });

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static void Cell(string ws, string name, params (string Name, string Default, string Unit)[] parameters)
    {
        string dir = Path.Combine(ws, name);
        if (!Directory.Exists(dir)) CellFolder.CreateCellFolder(ws, name);
        string ccell = Path.Combine(dir, CellFolder.CcellFileName);
        var file = CellPersistence.LoadFromFile(ccell);
        foreach (var (n, d, u) in parameters)
            file.Parameters.Add(new CcellParameter { Name = n, DefaultExpression = d, Unit = u, Dimension = UnitDimension.Length });
        CellPersistence.SaveToFile(ccell, file);
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = typeof(ExpressionsGateTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }
}
