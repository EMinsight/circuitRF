// ================================================================
//  ThermalInterfacesBlocksTests.cs — brief-em3d-76's design-side gates: which contact resistance applies (gate 1's
//  technology-pair and override half), the effective block's arithmetic and its A/B run (gate 4), a two-step submodel
//  of S5 (gate 5), and symmetry (gate 6). The solver's own gates 1–3 are tests/Thermal.Tests/InterfaceGateTests.
// ================================================================

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ThermalInterfacesBlocksTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-th76-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;   // DBU per µm

    // ── gate 1: the technology's pair applies; an override replaces it for one contact only ──────────────

    [Fact]
    public void Gate1_TheTechnologysPairApplies_AndAnOverrideReplacesItForOneContactOnly()
    {
        var tech = new Technology
        {
            Name = "t",
            ThermalInterfaces = [new TechThermalInterface { MaterialA = "SiC", MaterialB = "GaN", ResistanceM2KW = 1e-8, Source = "a paper" }],
        };
        var contacts = ThermalContacts.Resolve(
            [("die1", "GaN@85"), ("die2", "GaN"), ("sub", "SiC"), ("lid", "Copper")],
            [new C3dContactResistance { Between = ["sub", "die2"], ResistanceM2KW = 5e-8 }], tech);
        Assert.Equal(2, contacts.Count);
        var one = contacts.Single(c => c.SolidA == "die1");
        var two = contacts.Single(c => c.SolidA == "die2");
        Assert.Equal(("die1", "sub", 1e-8), (one.SolidA, one.SolidB, one.ResistanceM2KW));
        Assert.Contains("'SiC' / 'GaN'", one.Source);
        Assert.Equal(("die2", "sub", 5e-8), (two.SolidA, two.SolidB, two.ResistanceM2KW));
        Assert.Contains("override", two.Source);
    }

    /// <summary>Two dies on one flange, the technology's Die/Copper pair on both contacts and an override on one: the run
    /// names both, and the overridden die runs hotter by about ΔR″·P/A — the average jump over its contact.</summary>
    [GmshFact]
    public void Gate1_TheRun_AppliesThePairAndTheOverride_AndTheOverriddenDieRunsHotter()
    {
        string ws = Workspace(interfaces: [new TechThermalInterface { MaterialA = "Die", MaterialB = "Copper", ResistanceM2KW = 1e-5 }]);
        var setup = Thermal("Hot", new CemThermal
        {
            Boundaries = [Fixed("flange/zmin")],
            Measures = ["dT = Tavg(d2) - Tavg(d1)"],
            Mesh = new CemThermalMesh { SizeFromSources = 2 },
        });
        var doc = new C3dDocument
        {
            Objects = [Box("flange", "Copper", 0, 0, 0, 2000, 2000, 300), Box("die1", "Die", 200, 200, 300, 600, 600, 100),
                       Box("die2", "Die", 1200, 1200, 300, 600, 600, 100)],
            HeatSources = [Sheet("h1", 300 + 100, 200, 200, 600, 600, "1"), Sheet("h2", 300 + 100, 1200, 1200, 600, 600, "1")],
            Probes = [new C3dProbe { Name = "d1", Solid = "die1" }, new C3dProbe { Name = "d2", Solid = "die2" }],
            ContactResistances = [new C3dContactResistance { Between = ["die2", "flange"], ResistanceM2KW = 3e-5 }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        };
        var run = Run(ws, doc, "Hot");
        string notes = string.Join("\n", run.Notes ?? []);
        output.WriteLine(notes);
        Assert.Contains("Interface resistances in force", notes);
        Assert.Contains("'flange' | 'die1': 1E-05 m²·K/W from the technology's pair 'Die' / 'Copper'", notes);
        Assert.Contains("'flange' | 'die2': 3E-05 m²·K/W from the document's ContactResistances override", notes);
        double dT = run.Data!["dT"].RealValues[0], expected = 2e-5 * 1 / (600e-6 * 600e-6);
        output.WriteLine($"die2 − die1 = {dT:F3} K; ΔR″·P/A = {expected:F3} K");
        Assert.InRange(dT, 0.9 * expected, 1.1 * expected);
    }

    // ── gate 4: the effective block ──────────────────────────────────────────────────────────────────────

    /// <summary>R-em3d76-2b by hand: a 200 µm dielectric layer (5 % copper barrels, 5 % bores, k_d 0.3) under a 35 µm plane
    /// (85 % copper, 15 % holes). Every number below was worked on paper from the header's formulas.</summary>
    [Fact]
    public void Gate4_TheMixture_IsTheHandCheckedArithmetic()
    {
        var dielectric = new EffectiveLayer("core", 200e-6, 0.05, 390, 0.90, 0.3);
        var plane = new EffectiveLayer("plane", 35e-6, 0.85, 390, 0, 0);
        // dielectric: k_z = 0.05·390 + 0.9·0.3 = 19.77; matrix 0.3, inclusions φ = 0.1 at k_i = 0.05·390/0.1 = 195:
        //             k_xy = 0.3·(195.3 + 0.1·194.7)/(195.3 − 0.1·194.7) = 0.3·214.77/175.83
        Assert.Equal(19.77, ThermalEffectiveBlocks.LayerK(dielectric).KZ, 1e-12);
        Assert.Equal(0.3 * 214.77 / 175.83, ThermalEffectiveBlocks.LayerK(dielectric).KXy, 1e-12);
        // plane: k_z = 0.85·390 = 331.5; copper matrix with void holes φ = 0.15: k_xy = 390·0.85/1.15
        Assert.Equal(331.5, ThermalEffectiveBlocks.LayerK(plane).KZ, 1e-12);
        Assert.Equal(390 * 0.85 / 1.15, ThermalEffectiveBlocks.LayerK(plane).KXy, 1e-12);
        // stacked: k_z = 235 / (200/19.77 + 35/331.5), series; k_xy = (200·k_xy,1 + 35·k_xy,2)/235, parallel
        var mix = ThermalEffectiveBlocks.Mix([dielectric, plane]);
        Assert.Equal(22.9898133283178, mix.KZ, 1e-9);
        Assert.Equal(43.24433306222587, mix.KXy, 1e-9);
    }

    /// <summary>
    /// A flange on a board with a 4 × 4 field of plated vias over a copper sink, solved with the vias drawn and with an
    /// effective block over the field. The flange temperatures are REPORTED (an approximation has no tolerance gate); the
    /// block's mesh must be smaller (a counter). The block may cut only board material: over the flange it is refused.
    /// </summary>
    [GmshFact]
    [Trait("Category", "Benchmark")]     // ~5 s: two Gmsh meshes of the via field
    public void Gate4_AViaField_ExplicitAndAsABlock_TheBlocksMeshIsSmaller()
    {
        string ws = Workspace();
        WriteBoard(ws);
        var doc = new C3dDocument { Instances = [new C3dInstance { Name = "U1", CellRef = "../../Board", View = C3dInstanceView.Layout }] };
        string path = WriteC3d(ws, doc);
        var e0 = C3dElaborator.ElaborateOnce(doc, path, null);
        Assert.True(e0.Ok, string.Join(" ", e0.Refusals));
        long z0 = (long)Math.Round(e0.Solids.Min(s => Em3dProblem.Bounds(s.Primitive).Z0) * 1e9);   // DBU
        long z1 = (long)Math.Round(e0.Solids.Max(s => Em3dProblem.Bounds(s.Primitive).Z1) * 1e9);
        output.WriteLine($"board: z {z0 / 1000.0} to {z1 / 1000.0} µm, {e0.Solids.Count(s => e0.Origins.TryGetValue(s.Name, out var o) && o.Kind == Em3dObjectKind.Via)} vias");

        var setup = Thermal("Hot", new CemThermal
        {
            Boundaries = [Fixed("sink/zmin")],
            Measures = ["Tf = Tavg(flange_T)"],
            // first order and one element through the thinnest copper: the counter, not the accuracy, is what this gates, and the
            // explicit field at second order is ~320,000 tetrahedra and minutes (RESOLVED records that run's numbers)
            Mesh = new CemThermalMesh { MinThroughThickness = 1, Order = 1 },
        });
        doc.Objects = [BoxDbu("flange", "Copper", 500 * Um, 500 * Um, z1, 2000 * Um, 2000 * Um, 500 * Um),
                       BoxDbu("sink", "Copper", 0, 0, z0 - 300 * Um, 3000 * Um, 3000 * Um, 300 * Um)];
        doc.HeatSources = [Sheet("fet", 0, 1250, 1250, 500, 500, "2", offsetDbu: z1 + 500 * Um)];
        doc.Probes = [new C3dProbe { Name = "flange_T", Face = ["flange/zmin"], Stat = C3dProbeStat.Avg }];
        doc.EffectiveBlocks = [new C3dEffectiveBlock { Name = "vias", Min = new(500 * Um, 500 * Um, z0), Size = new(2000 * Um, 2000 * Um, z1 - z0) }];
        doc.Setups = [EmSetupPersistence.ToEmbedded(setup)];

        var explicitRun = Run(ws, doc, "Hot");
        doc.EffectiveBlocks[0].Enabled = true;
        var blockRun = Run(ws, doc, "Hot");
        int Tets(EmRunResult r) => int.Parse(Regex.Match(string.Join("\n", r.Notes ?? []), @"The mesh: ([\d,]+) tetrahedra").Groups[1].Value,
                                             NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        double tExplicit = explicitRun.Data!["Tf"].RealValues[0], tBlock = blockRun.Data!["Tf"].RealValues[0];
        output.WriteLine(string.Join("\n", blockRun.Notes!.Where(n => n.Contains("Effective block"))));
        output.WriteLine($"explicit: {Tets(explicitRun):N0} tetrahedra, flange {tExplicit:F4} °C; block: {Tets(blockRun):N0} tetrahedra, " +
                         $"flange {tBlock:F4} °C; difference {tBlock - tExplicit:+0.0000;-0.0000} K of a {tExplicit - 25:F4} K rise " +
                         $"({100 * (tBlock - tExplicit) / (tExplicit - 25):+0.00;-0.00} %)");
        Assert.True(Tets(blockRun) < Tets(explicitRun), "the block's mesh is not smaller");

        // over the flange: refused, naming it
        doc.EffectiveBlocks[0].Size = new(2000 * Um, 2000 * Um, z1 - z0 + 100 * Um);
        var refused = Run(ws, doc, "Hot");
        Assert.Equal(EmRunStatus.Refused, refused.Status);
        Assert.Contains("cuts through 'flange'", refused.Error);
    }

    /// <summary>
    /// The thermal series review: a block that cuts into a board solid only in part takes its footprint out of that solid's
    /// faces, so a condition on the board's bottom face must take in the block's bottom too — or the sink under the via field,
    /// exactly where the heat leaves, is silently insulated. The lowering names the block beside the face; the .geo's own[]
    /// reads both volumes.
    /// </summary>
    [Fact]
    public void AFaceTheBlockCutsInto_TakesInTheBlocksFootprint()
    {
        string ws = Workspace();
        WriteBoard(ws);
        var doc = new C3dDocument { Instances = [new C3dInstance { Name = "U1", CellRef = "../../Board", View = C3dInstanceView.Layout }] };
        string path = WriteC3d(ws, doc);
        var e0 = C3dElaborator.ElaborateOnce(doc, path, null);
        Assert.True(e0.Ok, string.Join(" ", e0.Refusals));
        long z0 = (long)Math.Round(e0.Solids.Min(s => Em3dProblem.Bounds(s.Primitive).Z0) * 1e9);
        long z1 = (long)Math.Round(e0.Solids.Max(s => Em3dProblem.Bounds(s.Primitive).Z1) * 1e9);
        var bottom = e0.Solids.Where(s => s.Role != Em3dRole.Air).MinBy(s => Em3dProblem.Bounds(s.Primitive).Z0)!;
        string face = bottom.Name + (e0.Provenance[bottom.Name].FaceNames.Contains("zmin") ? "/zmin" : "/bottom");
        doc.EffectiveBlocks = [new C3dEffectiveBlock { Name = "vias", Enabled = true, Min = new(500 * Um, 500 * Um, z0), Size = new(2000 * Um, 2000 * Um, z1 - z0) }];
        var t = new CemThermal { Boundaries = [Fixed(face)] };
        path = WriteC3d(ws, doc);
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        var low = ThermalLowerings.Build(doc, e, t, 1, out string? why);
        Assert.True(low is not null, why);
        var f = low!.Input.Faces.Single(x => x.Name == face);
        int block = low.Input.Solids.ToList().FindIndex(x => x.Name == "vias");
        Assert.Equal([block], f.AlsoSolids);
        Assert.Contains($"Volume{{s{f.Solid}[], s{block}[]}}", low.Gmsh.Geo);
        Assert.Contains(low.Notes, n => n.Contains($"The face '{face}' takes in the footprint of effective block(s) 'vias'"));
    }

    // ── gate 5: a submodel of S5 ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// brief 72's S5 quarter model solved whole (coarse) and as a submodel round the source (fine), its cut faces fixed to the
    /// whole solution: the submodel's peak meets the Fourier series to brief 72's 0.1 %. A region shrunk until it cuts the
    /// source's neighbourhood carries the flux-mismatch warning; the region well clear of it does not.
    /// </summary>
    [GmshFact]
    [Trait("Category", "Benchmark")]     // ~10 s: the whole model and two submodels, meshed and solved
    public void Gate5_S5AsASubmodel_MeetsTheReference_AndATooSmallRegionIsSaid()
    {
        string ws = Workspace();
        var reference = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "thermal",
                                                           "spreading", "spreading.json"))).RootElement.GetProperty("reference");
        double centre = reference.GetProperty("T_centre_degC").GetDouble();
        // the whole model deliberately coarse at the source (its smaller side in ONE element): the submodel is what resolves it
        var whole = Thermal("Whole", new CemThermal
        {
            Boundaries = [Fixed("layer2/zmin")], Measures = ["Tc = T(c)"], Mesh = new CemThermalMesh { SizeFromSources = 1 },
        });
        var sub = Thermal("Sub", new CemThermal
        {
            Submodel = new CemThermalSubmodel { From = "Whole", Region = "fine" },
            Measures = ["Tc = T(c)"],
            Mesh = new CemThermalMesh { SizeFromSources = 10 },
        });
        var tight = Thermal("Tight", new CemThermal
        {
            Submodel = new CemThermalSubmodel { From = "Whole", Region = "tight" },
            Measures = ["Tc = T(c)"],
            Mesh = new CemThermalMesh { SizeFromSources = 10 },
        });
        var doc = new C3dDocument
        {
            Objects = [Box("layer1", "Si", 1000, 1000, -100, 1000, 1000, 100), Box("layer2", "Cu", 1000, 1000, -1100, 1000, 1000, 1000)],
            HeatSources = [Sheet("src", 0, 1000, 1000, 100, 50, "0.25")],
            Probes = [new C3dProbe { Name = "c", Point = new(1000 * Um, 1000 * Um, 0) }],
            MeshRegions =
            [
                new C3dMeshRegion { Name = "fine", Min = new(1000 * Um, 1000 * Um, -400 * Um), Size = new(500 * Um, 500 * Um, 400 * Um), SizeUm = 25 },
                new C3dMeshRegion { Name = "tight", Min = new(1000 * Um, 1000 * Um, -30 * Um), Size = new(130 * Um, 80 * Um, 30 * Um), SizeUm = 10 },
            ],
            Setups = [EmSetupPersistence.ToEmbedded(whole), EmSetupPersistence.ToEmbedded(sub), EmSetupPersistence.ToEmbedded(tight)],
        };
        string path = WriteC3d(ws, doc);
        var fine = RunFile(ws, path, "Sub");
        var small = RunFile(ws, path, "Tight");
        double wholeTc = C3dSetupsResult(ws, "Whole")["Tc"].RealValues[0];
        double subTc = fine.Data!["Tc"].RealValues[0], tightTc = small.Data!["Tc"].RealValues[0];
        double rise = centre - 25;
        foreach (var (r, label) in new[] { (fine, "fine"), (small, "tight") })
        {
            output.WriteLine($"── {label}");
            foreach (string n in (r.Notes ?? []).Where(n => n.Contains("Submodel") || n.Contains("Cut faces") || n.Contains("mesh:"))) output.WriteLine(n);
            foreach (string w in r.Warnings) output.WriteLine("warning: " + w);
        }
        output.WriteLine($"centre: series {centre:F5}; whole {wholeTc:F5} ({100 * (wholeTc - centre) / rise:+0.000;-0.000} %); " +
                         $"submodel {subTc:F5} ({100 * (subTc - centre) / rise:+0.000;-0.000} %); tight {tightTc:F5}");
        Assert.True(Math.Abs(subTc - centre) <= 1e-3 * rise, $"submodel centre {subTc}");
        Assert.Contains(fine.Notes!, n => n.Contains("was solved first: it had no result"));
        Assert.Contains(small.Notes!, n => n.Contains("was solved from the model as it is now, so it was reused"));
        double fineMismatch = fine.Data!["thermal.Submodel:mismatch"].RealValues[0], tightMismatch = small.Data!["thermal.Submodel:mismatch"].RealValues[0];
        output.WriteLine($"pointwise mismatch: fine {100 * fineMismatch:F2} %, tight {100 * tightMismatch:F2} %");
        Assert.DoesNotContain(fine.Warnings, w => w.Contains("Enlarge the region"));
        Assert.Contains(small.Warnings, w => w.Contains("Enlarge the region"));
    }

    /// <summary>
    /// brief-em3d-87 R-em3d87-4 — a submodel reuses its From setup's stored solution only while that solution's inputs are
    /// the model's: after an edit to a file the whole model was solved from (here Cu's k in the technology), the From setup is
    /// solved first, a note names the file, and the submodel's answer moves with it.
    /// </summary>
    [GmshFact]
    public void R87_ASubmodel_SolvesItsFromSetupAgain_WhenAFileTheWholeModelReadHasChanged()
    {
        string ws = Workspace();
        var whole = Thermal("Whole", new CemThermal { Boundaries = [Fixed("layer2/zmin")], Measures = ["Tc = T(c)"], Mesh = new CemThermalMesh { SizeFromSources = 1 } });
        var sub = Thermal("Sub", new CemThermal
        {
            Submodel = new CemThermalSubmodel { From = "Whole", Region = "fine" }, Measures = ["Tc = T(c)"],
            Mesh = new CemThermalMesh { SizeFromSources = 1 },
        });
        string path = WriteC3d(ws, new C3dDocument
        {
            Objects = [Box("layer1", "Si", 1000, 1000, -100, 1000, 1000, 100), Box("layer2", "Cu", 1000, 1000, -1100, 1000, 1000, 1000)],
            HeatSources = [Sheet("src", 0, 1000, 1000, 100, 50, "0.25")],
            Probes = [new C3dProbe { Name = "c", Point = new(1000 * Um, 1000 * Um, 0) }],
            MeshRegions = [new C3dMeshRegion { Name = "fine", Min = new(1000 * Um, 1000 * Um, -400 * Um), Size = new(500 * Um, 500 * Um, 400 * Um), SizeUm = 100 }],
            Setups = [EmSetupPersistence.ToEmbedded(whole), EmSetupPersistence.ToEmbedded(sub)],
        });
        var first = RunFile(ws, path, "Sub");
        var again = RunFile(ws, path, "Sub");
        string techPath = Path.Combine(ws, "tech.ctech");
        var tech = TechPersistence.LoadFromFile(techPath);
        tech.Materials!.Single(m => m.Name == "Cu").ThermalK = 100;
        TechPersistence.SaveToFile(techPath, tech);
        var edited = RunFile(ws, path, "Sub");

        Assert.Contains(first.Notes!, n => n.Contains("was solved first: it had no result"));
        Assert.Contains(again.Notes!, n => n.Contains("was solved from the model as it is now, so it was reused"));
        Assert.Contains(edited.Notes!, n => n.Contains("'Whole' was solved first: 'tech.ctech' has changed since its result."));
        Assert.True(edited.Data!["Tc"].RealValues[0] > again.Data!["Tc"].RealValues[0] + 1e-3, "a poorer spreader under the source runs hotter");
    }

    // ── gate 6: symmetry ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A centred source on a block, convection on every exposed face: modelled whole at 1 W and as the x ≥ 1 mm half
    /// at 0.5 W with a symmetry plane — the same peak, and the same whole-device Rth through SymmetryFactor. A condition on
    /// the symmetry face is refused, naming the plane.</summary>
    [GmshFact]
    public void Gate6_Symmetry_TheHalfMatchesTheWhole_AndAConditionOnThePlaneIsRefused()
    {
        string ws = Workspace();
        CemThermal T() => new()
        {
            Boundaries = [Fixed("blk/zmin"), new CemThermalBoundary { Face = C3dThermal.ExposedFaces, Kind = ThermalBoundaryKind.Convection, H = "20000", AmbientC = "25" }],
            Measures = ["Rth = (T(pk) - 25) / (P * SymmetryFactor)"],
            Mesh = new CemThermalMesh { SizeFromSources = 6 },
        };
        var wholeDoc = new C3dDocument
        {
            Objects = [Box("blk", "Si", 0, 0, 0, 2000, 2000, 500)],
            Variables = [new C3dVariable { Name = "P", Expression = "1" }],
            HeatSources = [Sheet("src", 500, 800, 900, 400, 200, "P")],
            Probes = [new C3dProbe { Name = "pk", Point = new(1000 * Um, 1000 * Um, 500 * Um) }],
            Setups = [EmSetupPersistence.ToEmbedded(Thermal("Hot", T()))],
        };
        var halfDoc = new C3dDocument
        {
            Objects = [Box("blk", "Si", 1000, 0, 0, 1000, 2000, 500)],
            Variables = [new C3dVariable { Name = "P", Expression = "0.5" }],
            HeatSources = [Sheet("src", 500, 1000, 900, 200, 200, "P")],
            Probes = [new C3dProbe { Name = "pk", Point = new(1000 * Um, 1000 * Um, 500 * Um) }],
            SymmetryPlanes = [new C3dSymmetryPlane { Axis = C3dAxis.X, At = 1000 * Um }],
            Setups = [EmSetupPersistence.ToEmbedded(Thermal("Hot", T()))],
        };
        var w = Run(ws, wholeDoc, "Hot", "Whole");
        var h = Run(ws, halfDoc, "Hot", "Half");
        double rw = w.Data!["Rth"].RealValues[0], rh = h.Data!["Rth"].RealValues[0];
        output.WriteLine($"Rth whole {rw:F5} K/W, half {rh:F5} K/W ({100 * (rh - rw) / rw:+0.000;-0.000} %)");
        Assert.Contains(h.Notes!, n => n.Contains("the modelled part is 1/2 of the device"));
        Assert.True(Math.Abs(rh - rw) <= 5e-3 * rw, $"half {rh} vs whole {rw}");

        var bad = T();
        bad.Boundaries!.Add(new CemThermalBoundary { Face = "blk/xmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" });
        halfDoc.Setups = [EmSetupPersistence.ToEmbedded(Thermal("Hot", bad))];
        var refused = Run(ws, halfDoc, "Hot", "Half");
        Assert.Equal(EmRunStatus.Refused, refused.Status);
        Assert.Contains("lies on the symmetry plane X", refused.Error);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────

    private static EmSetup Thermal(string name, CemThermal t) => new() { Name = name, Problem3D = Em3dProblemType.Thermal, Thermal = t };

    private static CemThermalBoundary Fixed(string face) => new() { Face = face, Kind = ThermalBoundaryKind.FixedT, TempC = "25" };

    private static C3dHeatSource Sheet(string name, long zUm, long u, long v, long du, long dv, string power, long? offsetDbu = null) => new()
    {
        Name = name, Power = power,
        Sheet = new C3dHeatSheet
        {
            Plane = C3dPlane.XY, Offset = offsetDbu ?? zUm * Um,
            Rect = new C3dRect { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) },
        },
    };

    private static C3dBox Box(string name, string material, long x, long y, long z, long dx, long dy, long dz)
        => BoxDbu(name, material, x * Um, y * Um, z * Um, dx * Um, dy * Um, dz * Um);

    private static C3dBox BoxDbu(string name, string material, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x, y, z), Size = new C3dPoint3(dx, dy, dz) };

    private EmRunResult Run(string ws, C3dDocument doc, string setup, string cell = "Cell") => RunFile(ws, WriteC3d(ws, doc, cell), setup);

    private static EmRunResult RunFile(string ws, string path, string setup)
    {
        var loaded = C3dPersistence.LoadFromFile(path);
        var run = EmRunService.RunThreeDView(C3dSetups.ForRun(C3dSetups.Select(loaded, setup).Setup!, path), loaded, path, null, Path.Combine(ws, "results"));
        if (run.Status != EmRunStatus.Refused) Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        return run;
    }

    private static DataSet C3dSetupsResult(string ws, string setup)
        => RfCore.Export.DataSetImporter.Import(Path.Combine(ws, "results", $"Cell {setup}.thermal.npy")).DataSet;

    private string Workspace(IReadOnlyList<TechThermalInterface>? interfaces = null)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, ThermalK = 390 },
                new TechMaterial { Name = "Cu", Sigma20 = 5.8e7, ThermalK = 390 },
                new TechMaterial { Name = "Si", Epsr = 11.9, ThermalK = 150 },
                new TechMaterial { Name = "Die", Epsr = 12.9, ThermalK = 150 },
            ],
            ThermalInterfaces = interfaces is null ? null : [.. interfaces],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    /// <summary>A 3 mm board: top, inner and bottom copper planes (70 µm) over the whole board, two 200 µm laminate layers,
    /// and a 4 × 4 field of plated vias (300 µm drill, 50 µm wall) at a 500 µm pitch under the middle.</summary>
    private static void WriteBoard(string ws)
    {
        var tech = TechPersistence.LoadFromFile(Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D EM", "tech", "via-transition.ctech"));
        tech.Name = "board";
        foreach (var m in tech.Materials ?? [])
            m.ThermalK = m.Name == "Copper" ? 390 : 0.3;
        foreach (var l in tech.Stackup!.Layers.Where(l => l.Kind == StackupKind.Conductor)) l.ThicknessDbu = 70 * Um;
        var via = tech.Stackup!.Layers.Single(l => l.Kind == StackupKind.Via);
        via.Fill = ViaFillKind.Plated;
        via.WallThicknessDbu = 50 * Um;
        TechPersistence.SaveToFile(Path.Combine(ws, "board.ctech"), tech);
        string Rect(int layer) => $$"""{"$type": "Rect", "Layer": {"Layer": {{layer}}, "Datatype": 0}, "X1": 0, "Y1": 0, "X2": 3000000, "Y2": 3000000}""";
        var vias = new List<string>();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                vias.Add($$"""{"$type": "Via", "Layer": {"Layer": 9, "Datatype": 0}, "X": {{(750 + 500 * i) * Um}}, "Y": {{(750 + 500 * j) * Um}}, "PadSize": 400000, "DrillSize": 300000}""");
        string clay = $$"""
            {"FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 1000, "AngleMode": "AnyAngle",
             "TechRef": "../../board.ctech",
             "Shapes": [{{Rect(1)}}, {{Rect(2)}}, {{Rect(4)}}, {{string.Join(", ", vias)}}], "Instances": []}
            """;
        string dir = Path.Combine(ws, "Board", "layout");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Board.clay"), clay);
    }

    private static string WriteC3d(string ws, C3dDocument doc, string cell = "Cell")
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }
}
