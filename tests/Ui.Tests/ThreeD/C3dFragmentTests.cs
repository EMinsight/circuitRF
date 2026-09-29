// ================================================================
//  C3dFragmentTests.cs — brief-em3d-95's gates on C3dFragment, with no clipboard and no window:
//    1  round trip: a filleted boolean in a group, with Transparency, Model: false and an expression, arrives identical
//    2  collisions: a second paste is renamed _2, and every reference inside it (a Tool's face prefix, a group path, a heat
//       source's solid) follows the rename
//    3  DBU: 1000 DBU/µm into 100 DBU/µm lands at the same metres (a count is not a coordinate)
//    4  variables: created to closure; Reuse; Rename by token; identical reused silently; a cycle refused; a parameter as a VAR
//    5  materials: found and different (the target's used, said); missing and created; missing and left unassigned; Plan
//       changes nothing (what Cancel leaves)
//    6  placed cells across workspaces: the shared rebase's three cases, and the foreign reference elaborates in B
//    7  ports: conductors follow, bind here (said) or are cleared (inferred, said); renumbered in order; Model and Z0 survive
//    8  boundaries: EM follows its object and its material rule; a missing face is skipped; thermal into the ACTIVE setup,
//       or skipped naming the setups; a bounded face asks Keep / Replace; the exposed surface round-trips
//    9  symmetry planes: a free axis added (rescaled), the same one silent, another position asks; off the extent pastes, said
// ================================================================

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class C3dFragmentTests : IDisposable
{
    private const long Um = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em95-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static readonly C3dPasteContext Loose = new("", null, C3dCell.None, null);
    private static readonly C3dCopyContext From = new("", null, C3dCell.None, null);

    private static Technology Tech(params TechMaterial[] materials) => new() { Name = "t", Materials = [.. materials] };

    private static C3dBox Box(string name, long x, string? material = "Gold", string? group = null)
        => new() { Name = name, Material = material, Group = group, Min = new(x * Um, 0, 0), Size = new(20 * Um, 20 * Um, 20 * Um) };

    private static C3dFragment.Payload Copy(C3dDocument doc, Technology? tech, C3dCopySelection sel, C3dCopyContext? ctx = null)
        => RoundTrip(C3dFragment.Build(doc, tech, sel, ctx ?? From));

    /// <summary>Every payload goes through its text, as the clipboard carries it.</summary>
    private static C3dFragment.Payload RoundTrip(C3dFragment.Payload p)
    {
        Assert.True(C3dFragment.TryDeserialize(C3dFragment.Serialize(p), out var back));
        return back!;
    }

    private static C3dCopySelection All(C3dDocument d) => new() { Objects = [.. Enumerable.Range(0, d.Objects.Count)] };

    // ── 1 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_RoundTrip_AFilletedBooleanInAGroup_ArrivesIdentical()
    {
        var bore = new C3dCylinder { Name = "bore", Material = "Gold", Base = new(10 * Um, 10 * Um, 0), Length = 20 * Um, Radius = 3 * Um };
        var blank = new C3dBox { Material = "Gold", Size = new(20 * Um, 20 * Um, 20 * Um) };
        C3dBindings.SetExpr(blank, C3dBindings.SpecOf(typeof(C3dBox), nameof(C3dBox.Size))!, 0, new C3dExpr("w"));
        var lid = new C3dFillet
        {
            Name = "lid", Group = "pa/match", Transparency = 40, Model = false, Radius = 1 * Um, Edges = ["xmax|zmax"],
            Target = new C3dBoolean { Op = C3dBooleanOp.Subtract, Blank = blank, Tools = [bore] },
        };
        var source = new C3dDocument { Objects = [lid], Variables = [new C3dVariable { Name = "w", Expression = "20", Unit = "Um" }] };
        C3dResolver.Resolve(source, C3dCell.None);
        var tech = Tech(new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 });

        var target = new C3dDocument();
        var r = C3dFragment.Apply(target, tech, Loose, Copy(source, tech, All(source)), new C3dPasteChoices());

        Assert.Null(r.Refusal);
        Assert.Equal(C3dPersistence.SerializeObject(source.Objects[0]), C3dPersistence.SerializeObject(target.Objects[0]));
        Assert.Equal("w", Assert.Single(target.Variables).Name);
    }

    // ── 2 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_ASecondPaste_IsRenamed_AndEveryReferenceInsideFollows()
    {
        var source = new C3dDocument
        {
            Objects =
            [
                new C3dBoolean { Name = "lid", Group = "g", Blank = new C3dBox { Material = "Gold", Size = new(20 * Um, 20 * Um, 20 * Um) },
                                 Tools = [new C3dBox { Name = "t1", Material = "Gold", Size = new(5 * Um, 5 * Um, 20 * Um) }] },
                Box("a", 40, group: "g"),
            ],
            HeatSources = [new C3dHeatSource { Name = "hs", Solid = "a", Power = "1" }],
            FaceBoundaries = [new C3dFaceBoundary { Object = "lid", Face = "t1:xmin", Kind = Em3dFaceBoundaryKind.Conductive }],
        };
        var fragment = Copy(source, null, All(source) with { Places = ["hs"], FaceBoundaries = [0] });
        var target = new C3dDocument();
        C3dFragment.Apply(target, null, Loose, fragment, new C3dPasteChoices());
        var second = C3dFragment.Apply(target, null, Loose, fragment, new C3dPasteChoices());

        Assert.Equal(["lid", "a", "lid_2", "a_2"], target.Objects.Select(o => o.Name));
        var lid2 = (C3dBoolean)target.Objects[2];
        Assert.Equal("t1_2", lid2.Tools[0].Name);                                      // a Tool's name is unique too
        Assert.Equal(["g", "g", "g_2", "g_2"], target.Objects.Select(o => o.Group));    // a new group, not merged
        Assert.Equal("a_2", target.HeatSources[1].Solid);
        Assert.Equal(("lid_2", "t1_2:xmin"), (target.FaceBoundaries[1].Object, target.FaceBoundaries[1].Face));
        Assert.Contains("renamed lid → lid_2", second.Summary);
        Assert.Contains("g → g_2", second.Summary);
    }

    // ── 3 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_ACoarserTarget_LandsAtTheSameMetres()
    {
        var wire = new C3dWire { Name = "w", Points = [new(0, 0, 0), new(100 * Um, 0, 50 * Um)], Array = new C3dWireArray { Count = 3, Pitch = new(10 * Um, 0, 0) } };
        var source = new C3dDocument { DbuPerMicron = 1000, Objects = [Box("b", 10), wire],
                                       MeshRegions = [new C3dMeshRegion { Name = "m", Min = new(5 * Um, 0, 0), Size = new(1 * Um, 1 * Um, 1 * Um), SizeUm = 2 }] };
        var target = new C3dDocument { DbuPerMicron = 100 };
        C3dFragment.Apply(target, null, Loose, Copy(source, null, All(source) with { Places = ["m"] }), new C3dPasteChoices());

        double M(long v, int dbu) => C3dLowering.Metres(v, dbu);
        var b = (C3dBox)target.Objects[0];
        Assert.Equal(M(10 * Um, 1000), M(b.Min.X, 100), 15);
        Assert.Equal(M(20 * Um, 1000), M(b.Size.Z, 100), 15);
        var w = (C3dWire)target.Objects[1];
        Assert.Equal(M(50 * Um, 1000), M(w.Points[1].Z, 100), 15);
        Assert.Equal(3, w.Array!.Count);                                                // a count, not a coordinate
        Assert.Equal(M(10 * Um, 1000), M(w.Array.Pitch.X, 100), 15);
        Assert.Equal(M(5 * Um, 1000), M(target.MeshRegions[0].Min.X, 100), 15);
        Assert.Equal(2, target.MeshRegions[0].SizeUm);                                  // micrometres are resolution-free
    }

    // ── 4 ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A box whose height is <paramref name="expr"/>.</summary>
    private static C3dBox Tall(string name, string expr)
    {
        var b = Box(name, 0, material: null);
        C3dBindings.SetExpr(b, C3dBindings.SpecOf(typeof(C3dBox), nameof(C3dBox.Size))!, 2, new C3dExpr(expr));
        return b;
    }

    private static C3dVariable Var(string name, string expr, string? unit = "Um") => new() { Name = name, Expression = expr, Unit = unit };

    [Fact]
    public void Gate4a_AMissingVar_IsCreated_WithTheVarItNames()
    {
        var source = new C3dDocument { Objects = [Tall("b", "w")], Variables = [Var("w", "2*base", null), Var("base", "10")] };
        var target = new C3dDocument();
        var r = C3dFragment.Apply(target, null, Loose, Copy(source, null, All(source)), new C3dPasteChoices());
        Assert.Equal(["w", "base"], target.Variables.Select(v => v.Name));
        Assert.Equal("2*base", target.Variables[0].Expression);
        Assert.Equal(20 * Um, ((C3dBox)target.Objects[0]).Size.Z);
        Assert.Contains(r.Report, l => l.StartsWith("Variables created: w, base", StringComparison.Ordinal));
    }

    [Fact]
    public void Gate4b_ReuseKeepsTheName_RenameRewritesEveryPastedExpression()
    {
        var inst = new C3dInstance { Name = "U1", CellRef = "nowhere", Params = new() { ["h"] = new C3dExpr("h_lid") } };
        var source = new C3dDocument { Objects = [Tall("b", "h_lid"), Tall("c", "top")], Instances = [inst],
                                       Variables = [Var("h_lid", "250"), Var("top", "h_lid+5")] };
        var fragment = Copy(source, null, All(source) with { Instances = [0] });

        var reuse = new C3dDocument { Variables = [Var("h_lid", "200")] };
        var plan = C3dFragment.Plan(reuse, null, Loose, fragment);
        var conflict = Assert.Single(plan.Variables);
        Assert.Equal("h_lid_2", conflict.Suggested);
        Assert.Contains("200 um here", conflict.Here);
        Assert.Contains("250 um in the copy", conflict.Copy);
        C3dFragment.Apply(reuse, null, Loose, fragment, new C3dPasteChoices());
        Assert.Equal(200 * Um, ((C3dBox)reuse.Objects[0]).Size.Z);                     // evaluated against the target's

        var rename = new C3dDocument { Variables = [Var("h_lid", "200")] };
        var choices = new C3dPasteChoices();
        choices.Variables["h_lid"] = "h_lid_2";
        C3dFragment.Apply(rename, null, Loose, fragment, choices);
        Assert.Equal(["h_lid", "h_lid_2", "top"], rename.Variables.Select(v => v.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["h_lid_2"], C3dExpressionText.Names(C3dBindings.GetExpr(rename.Objects[0], nameof(C3dBox.Size), 2)!.Expr));
        Assert.Equal(["h_lid_2"], C3dExpressionText.Names(rename.Instances[0].Params!["h"].Expr));
        Assert.Equal(["h_lid_2"], C3dExpressionText.Names(rename.Variables.Single(v => v.Name == "top").Expression));
        Assert.Equal(250 * Um, ((C3dBox)rename.Objects[0]).Size.Z);
    }

    [Fact]
    public void Gate4c_AnIdenticalDefinition_IsReusedSilently()
    {
        var source = new C3dDocument { Objects = [Tall("b", "t")], Variables = [Var("t", "5 ")] };
        var target = new C3dDocument { Variables = [Var("t", "5")] };
        var fragment = Copy(source, null, All(source));
        Assert.False(C3dFragment.Plan(target, null, Loose, fragment).NeedsDialog);
        var r = C3dFragment.Apply(target, null, Loose, fragment, new C3dPasteChoices());
        Assert.Single(target.Variables);
        Assert.Contains("Variables reused, identical: t", r.Report);
    }

    [Fact]
    public void Gate4d_AReuseThatClosesACycle_IsRefused_AndTheTargetIsUnchanged()
    {
        var source = new C3dDocument { Objects = [Tall("b", "b1")], Variables = [Var("b1", "a1+1"), Var("a1", "3")] };
        var target = new C3dDocument { Objects = [Box("x", 0)], Variables = [Var("a1", "b1+1")] };
        string before = C3dPersistence.Serialize(target);
        var r = C3dFragment.Apply(target, null, Loose, Copy(source, null, All(source)), new C3dPasteChoices());   // a1: Reuse
        Assert.NotNull(r.Refusal);
        Assert.Contains("cycle", r.Refusal);
        Assert.Contains("a1", r.Refusal);
        Assert.Equal(before, C3dPersistence.Serialize(target));
    }

    [Fact]
    public void Gate4e_ACellParameter_ArrivesAsAVar_AndIsSaidToBeOne()
    {
        var cell = new C3dCell("cell", "cell.ccell", [new CcellParameter { Name = "wp", DefaultExpression = "30", Unit = "um" }]);
        var source = new C3dDocument { Objects = [Tall("b", "wp")] };
        var fragment = Copy(source, null, All(source), new C3dCopyContext("", null, cell, null));
        var target = new C3dDocument();
        var r = C3dFragment.Apply(target, null, Loose, fragment, new C3dPasteChoices());
        var v = Assert.Single(target.Variables);
        Assert.Equal(("wp", "30"), (v.Name, v.Expression));
        Assert.Equal(30 * Um, ((C3dBox)target.Objects[0]).Size.Z);
        Assert.Contains(r.Report, l => l.Contains("wp (a cell parameter in the copy's view", StringComparison.Ordinal));
    }

    // ── 5 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_Materials_FoundDifferent_Created_Unassigned_AndPlanChangesNothing()
    {
        var source = new C3dDocument { Objects = [Box("a", 0, "FR4"), Box("b", 30, "Laminate"), Box("c", 60, "Braze")] };
        var sourceTech = Tech(new TechMaterial { Name = "FR4", Epsr = 4.5, TanD = 0.02 },
                              new TechMaterial { Name = "Laminate", Epsr = 3.0, TanD = 0.001, Color = "#336699" },
                              new TechMaterial { Name = "Braze", Sigma20 = 1e7 });
        var targetTech = Tech(new TechMaterial { Name = "fr4", Epsr = 4.3, TanD = 0.02 });
        var fragment = Copy(source, sourceTech, All(source));
        var target = new C3dDocument();

        string before = C3dPersistence.Serialize(target);
        var plan = C3dFragment.Plan(target, targetTech, Loose, fragment);
        Assert.Equal(before, C3dPersistence.Serialize(target));                         // what Cancel leaves: nothing
        Assert.Equal(["Laminate", "Braze"], plan.Materials.Select(m => m.Name));

        var choices = new C3dPasteChoices();
        choices.CreateMaterials.Add("Laminate");
        var r = C3dFragment.Apply(target, targetTech, Loose, fragment, choices);
        Assert.Equal(["FR4", "Laminate", null], target.Objects.Select(o => o.Material));
        var created = Assert.Single(r.MaterialsToCreate);
        Assert.Equal(MaterialLibraryPersistence.Serialize([sourceTech.Materials[1]]), MaterialLibraryPersistence.Serialize([created]));
        Assert.Contains(r.Report, l => l.StartsWith("'FR4' in this workspace differs: εr 4.3 here, 4.5 in the copy", StringComparison.Ordinal));
        Assert.Contains(r.Report, l => l.StartsWith("'Braze' was not created", StringComparison.Ordinal));
        Assert.DoesNotContain(targetTech.Materials, m => m.Name != "fr4");              // the technology is never written here
    }

    // ── 6 ──────────────────────────────────────────────────────────────────────────────────────

    private string Ws(string name)
    {
        string ws = Path.Combine(_root, name);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology { Name = "tech", Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }] });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    [Fact]
    public void Gate6_APlacedCellFromWorkspaceA_RebasesThreeWays_AndElaboratesInB()
    {
        string a = Ws("A"), b = Ws("B");
        WriteC3d(a, "part", new C3dDocument { Objects = [Box("die", 0)] });
        var source = new C3dDocument { Instances = [new C3dInstance { Name = "U1", CellRef = "../../part" }] };
        string sourcePath = WriteC3d(a, "top", source);
        var fragment = Copy(source, null, new C3dCopySelection { Instances = [0] }, new C3dCopyContext(sourcePath, a, C3dCell.None, null));
        string partA = Path.Combine(a, "part");

        // 1. relative to the target document, leaving its workspace — and it elaborates in B
        string targetPath = WriteC3d(b, "dst", new C3dDocument());
        var target = C3dPersistence.LoadFromFile(targetPath);
        var r = C3dFragment.Apply(target, null, new C3dPasteContext(targetPath, b, C3dCell.None, null), fragment, new C3dPasteChoices());
        Assert.Equal(CellRefRebase.Rebase("../../part", partA, "part", Path.GetDirectoryName(targetPath)!, b), target.Instances[0].CellRef);
        Assert.Equal("../../../A/part", target.Instances[0].CellRef);
        Assert.Contains(r.Report, l => l.Contains("outside this workspace", StringComparison.Ordinal));
        var e = new C3dElaborator().Elaborate(target, targetPath, Path.Combine(b, ".cws"));
        Assert.True(e.Ok, string.Join("; ", e.Refusals));
        Assert.Contains("U1/die", e.Solids.Select(s => s.Name));

        // 2. an unsaved target: the source-workspace-relative form under B, where B has the same cell
        WriteC3d(b, "part", new C3dDocument { Objects = [Box("die", 0)] });
        var unsaved = new C3dDocument();
        C3dFragment.Apply(unsaved, null, new C3dPasteContext("", b, C3dCell.None, null), fragment, new C3dPasteChoices());
        Assert.Equal(Path.GetFullPath(Path.Combine(b, "part")), Path.GetFullPath(unsaved.Instances[0].CellRef));

        // 3. neither: the source's absolute cell directory
        var loose = new C3dDocument();
        C3dFragment.Apply(loose, null, Loose, fragment, new C3dPasteChoices());
        Assert.Equal(partA, loose.Instances[0].CellRef);
    }

    // ── 7 ──────────────────────────────────────────────────────────────────────────────────────

    private static C3dDocument Ported() => new()
    {
        Objects = [Box("trace", 0), Box("gnd", 30)],
        Variables = [Var("z0v", "50", null)],
        Ports =
        [
            new C3dPort { Number = 1, Name = "in", Positive = "trace", Negative = "gnd", Z0 = "z0v" },
            new C3dPort { Number = 2, Positive = "trace", Negative = "gnd", Model = false },
        ],
    };

    [Fact]
    public void Gate7_Ports_FollowBindOrInfer_AreRenumberedInOrder_AndKeepModelAndZ0()
    {
        var source = Ported();
        var withConductors = Copy(source, null, All(source) with { Ports = [0, 1] });
        var target = new C3dDocument { Ports = [new C3dPort { Number = 1 }, new C3dPort { Number = 2 }] };
        var r = C3dFragment.Apply(target, null, Loose, withConductors, new C3dPasteChoices());
        Assert.Equal([1, 2, 3, 4], target.Ports.Select(p => p.Number));
        Assert.Contains("Ports renumbered, since their numbers are taken here: P1→P3, P2→P4.", r.Report);
        Assert.Equal(("trace", "gnd"), (target.Ports[2].Positive, target.Ports[2].Negative));
        Assert.False(target.Ports[3].Model);
        Assert.Equal("z0v", target.Ports[2].Z0);
        Assert.Equal("z0v", Assert.Single(target.Variables).Name);                     // Z0's name came through the variable rules

        var alone = Copy(source, null, new C3dCopySelection { Ports = [0] });
        var here = new C3dDocument { Objects = [Box("trace", 0), Box("gnd", 30)] };
        var bound = C3dFragment.Apply(here, null, Loose, alone, new C3dPasteChoices());
        Assert.Equal(("trace", "gnd"), (here.Ports[0].Positive, here.Ports[0].Negative));
        Assert.Contains("Port 1's + conductor names 'trace': this document's 'trace', not a copied one.", bound.Report);

        var empty = new C3dDocument();
        var inferred = C3dFragment.Apply(empty, null, Loose, alone, new C3dPasteChoices());
        Assert.Equal((null, null), (empty.Ports[0].Positive, empty.Ports[0].Negative));
        Assert.Contains(inferred.Report, l => l.Contains("will be inferred", StringComparison.Ordinal));
    }

    // ── 8 ──────────────────────────────────────────────────────────────────────────────────────

    private static List<System.Text.Json.JsonElement> Thermal(string name, params CemThermalBoundary[] bs)
        => [EmSetupPersistence.ToEmbedded(new EmSetup { Name = name, Solver3D = Em3dSolver.None, Problem3D = Em3dProblemType.Thermal,
                                                        Thermal = new CemThermal { Boundaries = bs.Length > 0 ? [.. bs] : null } })];

    private static CemThermalBoundary Fixed(string face, string t) => new() { Face = face, Kind = ThermalBoundaryKind.FixedT, TempC = t };

    private static IReadOnlyList<CemThermalBoundary> BoundariesOf(C3dDocument d, string setup)
        => C3dThermal.ThermalSetups(d).Single(s => s.Setup.Name == setup).Thermal.Boundaries ?? [];

    [Fact]
    public void Gate8_Boundaries_FollowOrSkip_ThermalIntoTheActiveSetup_AndABoundedFaceAsks()
    {
        var source = new C3dDocument
        {
            Objects = [Box("lid", 0)],
            FaceBoundaries = [new C3dFaceBoundary { Object = "lid", Face = "zmax", Kind = Em3dFaceBoundaryKind.Conductive, Material = "Braze" }],
            Setups = Thermal("Hot", Fixed("lid/zmin", "40"), Fixed(C3dThermal.ExposedFaces, "25")),
        };
        var sourceTech = Tech(new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }, new TechMaterial { Name = "Braze", Sigma20 = 1e7 });
        var copyAll = Copy(source, sourceTech, All(source) with { FaceBoundaries = [0], ThermalBoundaries = ["lid/zmin", C3dThermal.ExposedFaces] },
                           new C3dCopyContext("", null, C3dCell.None, "Hot"));

        // with its object, into a document that has 'lid': the rename is followed, the material through the material rules
        var target = new C3dDocument { Objects = [Box("lid", 0)], Setups = Thermal("Cool") };
        var choices = new C3dPasteChoices();
        choices.CreateMaterials.Add("Braze");
        var r = C3dFragment.Apply(target, Tech(new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }), Loose with { ActiveSetupName = "Cool" }, copyAll, choices);
        Assert.Equal(("lid_2", "zmax", "Braze"), (target.FaceBoundaries[0].Object, target.FaceBoundaries[0].Face, target.FaceBoundaries[0].Material));
        Assert.Equal("Braze", Assert.Single(r.MaterialsToCreate).Name);
        Assert.Equal(["lid_2/zmin", C3dThermal.ExposedFaces], BoundariesOf(target, "Cool").Select(b => b.Face));   // the exposed surface too

        // no thermal setup active: skipped, the thermal setups named
        var idle = new C3dDocument { Objects = [Box("lid", 0)], Setups = Thermal("Cool") };
        var skipped = C3dFragment.Apply(idle, null, Loose, copyAll, new C3dPasteChoices());
        Assert.Empty(BoundariesOf(idle, "Cool"));
        Assert.Contains(skipped.Report, l => l.Contains("make 'Cool' active to paste them", StringComparison.Ordinal));

        // alone, onto a face this document lacks: skipped
        var alone = Copy(source, sourceTech, new C3dCopySelection { FaceBoundaries = [0] });
        var bare = new C3dDocument();
        var missing = C3dFragment.Apply(bare, null, Loose, alone, new C3dPasteChoices());
        Assert.Empty(bare.FaceBoundaries);
        Assert.Contains(missing.Report, l => l.Contains("this document has no such face", StringComparison.Ordinal));

        // alone, onto a face already bounded here: a conflict — Keep leaves it, Replace writes the pasted one
        C3dDocument Bounded() => new()
        {
            Objects = [Box("lid", 0)],
            FaceBoundaries = [new C3dFaceBoundary { Object = "lid", Face = "zmax", Kind = Em3dFaceBoundaryKind.Pec }],
        };
        var keep = Bounded();
        var conflict = Assert.Single(C3dFragment.Plan(keep, null, Loose, alone).Boundaries);
        Assert.Equal("em:lid/zmax", conflict.Key);
        C3dFragment.Apply(keep, null, Loose, alone, new C3dPasteChoices());
        Assert.Equal(Em3dFaceBoundaryKind.Pec, Assert.Single(keep.FaceBoundaries).Kind);
        var replace = Bounded();
        var answer = new C3dPasteChoices();
        answer.ReplaceBoundaries.Add(conflict.Key);
        C3dFragment.Apply(replace, null, Loose, alone, answer);
        Assert.Equal(Em3dFaceBoundaryKind.Conductive, Assert.Single(replace.FaceBoundaries).Kind);
    }

    // ── 9 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_SymmetryPlanes_FreeAdded_SameSilent_OtherAsks_OffTheExtentPastesAndIsSaid()
    {
        var source = new C3dDocument { DbuPerMicron = 1000, SymmetryPlanes = [new C3dSymmetryPlane { Axis = C3dAxis.X, At = 120 * Um }] };
        var plane = Copy(source, null, new C3dCopySelection { SymmetryPlanes = [C3dAxis.X] });

        var coarse = new C3dDocument { DbuPerMicron = 100 };
        C3dFragment.Apply(coarse, null, Loose, plane, new C3dPasteChoices());
        Assert.Equal(12_000, Assert.Single(coarse.SymmetryPlanes).At);                  // 120 µm at 100 DBU/µm

        var same = new C3dDocument { SymmetryPlanes = [new C3dSymmetryPlane { Axis = C3dAxis.X, At = 120 * Um }] };
        Assert.False(C3dFragment.Plan(same, null, Loose, plane).NeedsDialog);

        var other = new C3dDocument { SymmetryPlanes = [new C3dSymmetryPlane { Axis = C3dAxis.X, At = 0 }] };
        var conflict = Assert.Single(C3dFragment.Plan(other, null, Loose, plane).Boundaries);
        C3dFragment.Apply(other, null, Loose, plane, new C3dPasteChoices());
        Assert.Equal(0, other.SymmetryPlanes[0].At);                                   // Keep, the default
        var answer = new C3dPasteChoices();
        answer.ReplaceBoundaries.Add(conflict.Key);
        C3dFragment.Apply(other, null, Loose, plane, answer);
        Assert.Equal(120 * Um, other.SymmetryPlanes[0].At);

        // off the extent of a 20 µm box: pasted, and said — by the rule check reads
        string ws = Ws("S");
        string path = WriteC3d(ws, "cell", new C3dDocument { Objects = [Box("b", 0)] });
        var doc = C3dPersistence.LoadFromFile(path);
        var r = C3dFragment.Apply(doc, null, new C3dPasteContext(path, ws, C3dCell.None, null), plane, new C3dPasteChoices());
        Assert.Equal([C3dAxis.X], r.SymmetryPlanes);
        var e = new C3dElaborator().Elaborate(doc, path, Path.Combine(ws, ".cws"));
        var said = Assert.Single(C3dFragment.PlanesOffExtent(e, doc, r.SymmetryPlanes));
        Assert.StartsWith("The symmetry plane X = 120 µm does not lie on the model's extent", said);
    }
}
