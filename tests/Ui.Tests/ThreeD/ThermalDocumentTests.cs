// ================================================================
//  ThermalDocumentTests.cs — brief-em3d-73's gates: everything a thermal run READS, written down and checked, and
//  nothing that solves. The resolver (gate 2), the shipped metals against brief 72's tables (gate 6), interfaces, the
//  thermal places reaching no EM lowering (§4e, gate 1), the round trip (gate 3), one finding per check rule (gate 4)
//  and the refusals (gate 5). Every existing .c3d/.ctech/.cmat round trip and every Palace/openEMS golden is the
//  existing suites' gate, unchanged.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ThermalDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-th73-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;   // DBU per µm

    // ── gate 2: the resolver ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_SigmaFromAlphaIsWBonds_ATableInterpolatesHoldsAndWins()
    {
        var gold = new WireMaterial("Gold", 4.1e7, 0.0034, 19300);
        var m = new TechMaterial { Name = "Gold", Sigma20 = 4.1e7, Alpha20 = 0.0034, ThermalK = 318 };
        foreach (double t in new[] { 20.0, 85.0, 200.0 })
            Assert.Equal(gold.SigmaAt(t), ThermalProperties.SigmaAt(m, t)!.Value.Value, 1e-12 * gold.SigmaAt(t));
        Assert.Equal(318, ThermalProperties.ThermalKAt(m, 500)!.Value.Value);

        // The table wins over the constant; linear between rows, with the segment's slope; held beyond, and said.
        m.ThermalKVsTemp = [new() { TempC = 20, Value = 318 }, new() { TempC = 120, Value = 308 }, new() { TempC = 220, Value = 300 }];
        m.ThermalK = 999;
        var mid = ThermalProperties.ThermalKAt(m, 70)!.Value;
        Assert.Equal(313.0, mid.Value, 1e-9);
        Assert.Equal(-0.1, mid.Slope, 1e-12);
        Assert.False(mid.Held);
        var hot = ThermalProperties.ThermalKAt(m, 400)!.Value;
        Assert.Equal((300.0, 0.0), (hot.Value, hot.Slope));
        Assert.Contains("held at its table's highest row (220 °C) at 400 °C", hot.HeldNote);
        m.SigmaVsTemp = [new() { TempC = 20, Value = 4e7 }, new() { TempC = 220, Value = 2e7 }];
        Assert.Equal(3e7, ThermalProperties.SigmaAt(m, 120)!.Value.Value);
        Assert.Null(ThermalProperties.ThermalKAt(new TechMaterial { Name = "Mystery", Epsr = 3 }, 25));
    }

    // ── gate 6: the four wire metals carry brief 72's tables ────────────────────────────────────────

    /// <summary>
    /// Each metal's k(T) table IS brief 72's (every row from 20 °C to below the melting point), and its σ(T) table is
    /// brief 72's ρ(T) shifted by one constant so it equals Sigma20 at 20 °C (Matthiessen's rule — owner decision
    /// 2026-09-28: no existing Sigma20 moves, and a record must not disagree with itself). Gold's k at the owner's two
    /// temperatures is within what brief 72 recorded (−0.3 % and −2.7 %).
    /// </summary>
    [Fact]
    public void Gate6_TheWireMetalsCarryBrief72sTables_EndingBelowTheirMeltingPoints()
    {
        var lib = MaterialLibraries.LoadGeneric();
        var constants = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "testdata/thermal/metals/constants.json"))).RootElement;
        foreach (var (name, key) in new[] { ("Gold", "gold"), ("Copper", "copper"), ("Aluminium", "aluminium"), ("Silver", "silver") })
        {
            var m = lib.Single(x => x.Name == name);
            double mp = constants.GetProperty(key).GetProperty("melting_point_degC").GetDouble();
            var rows = Csv(key);
            var kept = rows.Where(r => r.T >= 20 && r.T < mp).ToList();
            double shift = 1 / m.Sigma20!.Value - RhoAt(rows, 20);

            Assert.Equal(kept.Select(r => (r.T, r.K)), m.ThermalKVsTemp!.Select(p => (p.TempC, p.Value)));
            Assert.Equal(kept.Select(r => r.T), m.SigmaVsTemp!.Select(p => p.TempC));
            foreach (var (r, p) in kept.Zip(m.SigmaVsTemp!))
                Assert.Equal(1 / (r.Rho + shift), p.Value, 1e-7 * p.Value);
            Assert.Equal(m.Sigma20.Value, m.SigmaVsTemp![0].Value);
            Assert.True(m.SigmaVsTemp![^1].TempC < mp && m.ThermalKVsTemp![^1].TempC < mp, name);
            Assert.Equal(m.ThermalKVsTemp![0].Value, m.ThermalK);
        }
        var au = lib.Single(x => x.Name == "Gold");
        Assert.InRange(ThermalProperties.ThermalKAt(au, 125)!.Value.Value / 312 - 1, -0.005, 0);
        Assert.InRange(ThermalProperties.ThermalKAt(au, 927)!.Value.Value / 262 - 1, -0.03, -0.02);

        // Every record states what a thermal run reads, and the library raises nothing — no table disagrees with itself.
        Assert.All(lib, x => Assert.True((x.ThermalK is not null) && x.DensityKgM3 is not null && x.SpecificHeat is not null, x.Name));
        Assert.DoesNotContain(MaterialValidation.Validate(lib), p => p.Id is not null);
    }

    // ── R-em3d73-3: interfaces ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Interfaces_AreUnorderedPairs_EqualValuesMerge_DifferentValuesRefuseTheLoad()
    {
        var shipped = ShippedTechnologies.Load(ShippedTechnologies.All.First());
        var gan = shipped.FindThermalInterface("silicon carbide (4h, semi-insulating)", "Gallium nitride");
        Assert.NotNull(gan);
        Assert.Contains("replace it with your own", gan!.Source);

        TechThermalInterface Pair(double r) => new() { MaterialA = "A", MaterialB = "B", ResistanceM2KW = r };
        TechThermalInterface Flipped(double r) => new() { MaterialA = "b", MaterialB = "a", ResistanceM2KW = r };
        Assert.Empty(MaterialLibraries.InterfaceConflicts("t", [Pair(1e-8)], [("lib", [Flipped(1e-8)])]));
        var conflict = Assert.Single(MaterialLibraries.InterfaceConflicts("t", [Pair(1e-8)], [("lib", [Flipped(2e-8)])]));
        Assert.Equal(MaterialLibraries.InterfaceConflictId, conflict.Id);

        var tech = new Technology { Name = "t", MaterialLibraries = ["x.cmat"], ThermalInterfaces = [Pair(1e-8)] };
        var loader = new MaterialLibraryLoader(r => r, _ => [], _ => [Flipped(2e-8)]);
        Assert.Throws<MaterialLibraryException>(() => MaterialLibraries.Resolve(tech, loader, "t"));
    }

    // ── §4e / gate 1: no thermal place reaches an EM lowering ─────────────────────────────────────────

    [Fact]
    public void Gate1_ThermalPlaces_ChangeNoPalaceOrOpenEmsByte()
    {
        string ws = Workspace();
        var plain = Microstrip();
        var thermal = Microstrip();
        thermal.HeatSources.Add(new C3dHeatSource { Name = "hot", Power = "1", Sheet = new C3dHeatSheet { Plane = C3dPlane.XY, Offset = 50 * Um, Rect = Rect(400, 400, 100, 100) } });
        thermal.Probes.Add(new C3dProbe { Name = "pr", Face = "sub/zmax", Stat = C3dProbeStat.Max });
        thermal.ContactResistances.Add(new C3dContactResistance { Between = ["sub", "trace"], ResistanceM2KW = 1e-6 });

        Assert.Equal(Lower(plain, WriteC3d(ws, "A", plain)), Lower(thermal, WriteC3d(ws, "B", thermal)));

        static (string?, string?, string?) Lower(C3dDocument doc, string path)
        {
            var (setup, why) = C3dSetups.Select(doc, "S1");
            Assert.Null(why);
            var g = C3dProblemAssembly.Assemble(C3dSetups.ForRun(setup!, path), doc, path, null);
            Assert.True(g.Ok, g.Refusal);
            var p = g.Problem!;
            var settings = PalaceSettings.Resolve(new CemPalace());
            var geo = GmshGeoWriter.Write(p, settings);
            var json = PalaceConfigWriter.Write(p, geo.Groups, settings);
            var grid = FdtdGrid.Build(p, OpenEmsGridSettings.Default, long.MaxValue);
            var csx = CsxcadWriter.Write(p, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
            return (geo.Geo, json.Json, csx.Model);
        }
    }

    // ── gate 3: the round trip ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_OneOfEachRecordAndAFullThermalSection_RoundTripByteForByte()
    {
        var doc = Clean();
        doc.Probes.Add(new C3dProbe { Name = "wp", Wire = "w1[3]", Stat = C3dProbeStat.Max, LimitC = 175 });
        doc.Probes.Add(new C3dProbe { Name = "ln", Line = new C3dProbeLine { From = new(0, 0, 0), To = new(0, 0, 300 * Um) } });
        doc.Probes.Add(new C3dProbe { Name = "vol", Solid = "die", Stat = C3dProbeStat.Avg });
        doc.HeatSources.Add(new C3dHeatSource { Name = "bulk", Solid = "die", Power = "2 W", Density = C3dHeatDensity.PerVolume });
        C3dBindings.SetExpr(doc.Probes.Single(p => p.Name == "pt"), C3dBindings.SpecOf(typeof(C3dProbe), nameof(C3dProbe.Point))!, 2,
                            new C3dExpr("h/2", "Um"));
        C3dBindings.SetExpr(doc.MeshRegions[0], C3dBindings.SpecOf(typeof(C3dMeshRegion), nameof(C3dMeshRegion.SizeUm))!, 0,
                            new C3dExpr("fine", "Um"));
        var setup = HotSetup();
        setup.Thermal!.Currents = [new CemThermalCurrent { Port = 1, Dc = "0.5", EnterFace = "die/zmax" }];
        setup.Thermal.Mesh = new CemThermalMesh { Order = 2, SizeFromSources = 8, MinThroughThickness = 2, Grading = 1.4 };
        setup.Thermal.Balance = new CemThermalBalance { SigmaOfT = true, KOfT = false, Tolerance = 1e-6, MaxIterations = 30 };
        setup.Thermal.Boundaries!.Add(new CemThermalBoundary { Face = C3dThermal.ExposedFaces, Kind = ThermalBoundaryKind.Convection, H = "10", AmbientC = "25" });
        doc.Setups = [EmSetupPersistence.ToEmbedded(setup)];

        string once = C3dPersistence.Serialize(doc);
        Assert.Equal(once, C3dPersistence.Serialize(C3dPersistence.Deserialize(once)));
        foreach (string key in new[] { "\"HeatSources\"", "\"Probes\"", "\"MeshRegions\"", "\"ContactResistances\"", "\"Expr\": \"h/2\"", "\"Density\": \"PerVolume\"" })
            Assert.Contains(key, once);

        // The setup survives a read and a re-write through the .cem schema — what the Setups dialog does on save.
        var element = C3dPersistence.Deserialize(once).Setups[0];
        var read = EmSetupPersistence.FromEmbedded(element);
        Assert.True(read.IsThermal);
        Assert.Equal(element.GetRawText().Replace(" ", "").Replace("\n", "").Replace("\t", ""),
                     EmSetupPersistence.ToEmbedded(read).GetRawText().Replace(" ", "").Replace("\n", "").Replace("\t", ""));

        // A document with none of it gains no key (every existing .c3d's bytes are the existing suites' gate).
        Assert.DoesNotContain("HeatSources", C3dPersistence.Serialize(new C3dDocument()));
    }

    // ── gate 4: check, one fixture per finding ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(C3dThermal.D.SourceOutsideId, "outside")]
    [InlineData(C3dThermal.D.SourceOutsideId, "straddle")]
    [InlineData(C3dThermal.D.ContactApartId, "")]
    [InlineData(C3dThermal.D.ProbeFaceId, "")]
    [InlineData(C3dThermal.D.ProbeWireId, "")]
    [InlineData(C3dThermal.D.NoSinkId, "")]
    [InlineData(C3dThermal.D.BoundaryFaceId, "")]
    [InlineData(C3dThermal.D.SetupSourceId, "")]
    [InlineData(C3dThermal.D.MaterialKId, "")]
    [InlineData(C3dThermal.D.SweepGeometryId, "")]
    [InlineData(C3dThermal.D.MeasureId, "parse")]
    [InlineData(C3dThermal.D.MeasureId, "probe")]
    [InlineData(C3dThermal.D.ContactShapeId, "duplicate")]
    [InlineData(C3dThermal.D.BoundaryFaceId, "air")]
    [InlineData(C3dThermal.D.SetupSourceId, "unit")]
    [InlineData(C3dThermal.D.MeshId, "")]
    public void Gate4_EachFinding_FromTheOneValidator(string id, string variant)
    {
        var doc = Clean();
        var setup = HotSetup();
        var t = setup.Thermal!;
        switch (id, variant)
        {
            case (C3dThermal.D.SourceOutsideId, "outside"):  doc.HeatSources[0].Sheet!.Offset = 400 * Um; break;
            case (C3dThermal.D.SourceOutsideId, "straddle"): doc.HeatSources[0].Sheet!.Plane = C3dPlane.XZ;          // a vertical sheet
                                                             doc.HeatSources[0].Sheet!.Offset = 500 * Um;
                                                             doc.HeatSources[0].Sheet!.Rect = Rect(450, 150, 100, 100); break;  // z 150..250
            case (C3dThermal.D.ContactApartId, _):  doc.Objects.Add(Box("lid", "Copper", 3000, 3000, 0, 100, 100, 100));
                                                    doc.ContactResistances.Add(new C3dContactResistance { Between = ["die", "lid"], ResistanceM2KW = 1e-6 }); break;
            case (C3dThermal.D.ProbeFaceId, _):     doc.Probes[0].Face = "die/top"; break;
            case (C3dThermal.D.ProbeWireId, _):     doc.Probes.Add(new C3dProbe { Name = "wp", Wire = "w9" }); break;
            case (C3dThermal.D.NoSinkId, _):        t.Boundaries = []; break;
            case (C3dThermal.D.BoundaryFaceId, ""): t.Boundaries![0].Face = "flange/bottom"; break;
            case (C3dThermal.D.SetupSourceId, ""):  t.Sources!.Add(new CemThermalSource { Name = "nosuch", Power = "1" }); break;
            case (C3dThermal.D.MaterialKId, _):     doc.Objects[1].Material = "Plastic"; break;
            case (C3dThermal.D.SweepGeometryId, _): t.Sweep = [new CemThermalSweep { Var = "wf", Start = "900", Stop = "1100", Points = 3 }]; break;
            case (C3dThermal.D.MeasureId, "parse"): t.Measures = ["Rth = (Tmax(die_top) -"]; break;
            case (C3dThermal.D.MeasureId, "probe"): t.Measures = ["Rth = Tmax(nosuch) / Pdiss"]; break;
            // the thermal series review: what the run refuses (or picks between), check refuses first
            case (C3dThermal.D.ContactShapeId, "duplicate"): doc.ContactResistances.Add(new C3dContactResistance { Between = ["flange", "die"], ResistanceM2KW = 2e-6 }); break;
            case (C3dThermal.D.BoundaryFaceId, "air"):       doc.Objects.Add(Box("cavity", "Air", 3000, 3000, 0, 100, 100, 100));
                                                             t.Boundaries!.Add(new CemThermalBoundary { Face = "cavity/zmax", Kind = ThermalBoundaryKind.FixedT, TempC = "Ths" }); break;
            case (C3dThermal.D.SetupSourceId, "unit"):       t.Sources![0].Power = "30 dBm"; break;
            case (C3dThermal.D.MeshId, _):                   t.Mesh = new CemThermalMesh { Order = 3 }; break;
        }
        doc.Setups = [EmSetupPersistence.ToEmbedded(setup)];

        var found = Findings(doc, WriteC3d(Workspace(), "Cell", doc));
        var hit = Assert.Single(found, d => d.Id == id);
        Assert.Equal(CircuitRF.Diagnostics.DiagnosticSeverity.Error, hit.Severity);
        Assert.Single(found);   // and nothing else: the clean fixture is clean
    }

    /// <summary>The thermal series review: a zero override is perfect contact (brief 76), which an override may state where the
    /// technology gives the pair a value; a probe whose name no measure can read is a warning, and nothing more.</summary>
    [Fact]
    public void Gate4_AZeroOverrideIsClean_AndAProbeNoMeasureCanNameIsAWarning()
    {
        var doc = Clean();
        doc.ContactResistances[0].ResistanceM2KW = 0;
        doc.Probes[0].Name = "die-top";
        var setup = HotSetup();
        setup.Thermal!.Measures = [];
        doc.Setups = [EmSetupPersistence.ToEmbedded(setup)];
        var found = Findings(doc, WriteC3d(Workspace(), "Cell", doc));
        var hit = Assert.Single(found);
        Assert.Equal((C3dThermal.D.ProbeNameId, CircuitRF.Diagnostics.DiagnosticSeverity.Warning), (hit.Id, hit.Severity));
    }

    [Fact]
    public void Gate4_CheckReportsThemByRule_AndExplainListsTheSetup_AsProcesses()
    {
        string ws = Workspace();
        string clean = WriteC3d(ws, "Clean", Clean(withSetup: true));
        var (code, _, err) = RunCli("check", clean);
        Assert.True(code == 0, err);

        var bad = Clean();
        var setup = HotSetup();
        setup.Thermal!.Boundaries = [];
        bad.Objects[1].Material = "Plastic";
        bad.Setups = [EmSetupPersistence.ToEmbedded(setup)];
        var (badCode, stdout, _) = RunCli("check", WriteC3d(ws, "Bad", bad), "--json");
        Assert.Equal(1, badCode);
        var ids = JsonDocument.Parse(stdout).RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("id").GetString()).ToList();
        Assert.Contains(C3dThermal.D.NoSinkId, ids);
        Assert.Contains(C3dThermal.D.MaterialKId, ids);

        var (exCode, exOut, exErr) = RunCli("explain", clean, "--analysis", "Hot");
        Assert.True(exCode == 0, exErr);
        Assert.Contains("sweep Pdiss", exOut);
        Assert.Contains("1 to 10 in 10 point(s), base SI; unit", exOut);
        Assert.Contains("Pdiss = 1 W", exOut);                          // Pdiss at the first sweep point
        Assert.Contains("interface 'flange' | 'die'", exOut);
        Assert.Contains("k(25 °C) = 400 W/(m·K)", exOut);
    }

    // ── gate 5: the refusals ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_ThermalIsRefusedByEveryEmDoor()
    {
        string ws = Workspace();
        var doc = Clean(withSetup: true);
        string path = WriteC3d(ws, "Cell", doc);

        // brief-em3d-74: an EMBEDDED thermal setup now runs (ThermalRunTests); the EM assembly still refuses one by type.
        var (s, why) = C3dSetups.Select(doc, "Hot");
        Assert.Null(why);
        var assembled = C3dProblemAssembly.Assemble(C3dSetups.ForRun(s!, path), doc, path, null);
        Assert.Contains("circuitRF's own thermal solver runs", assembled.Refusal);

        var cem = HotSetup();
        Assert.Contains(C3dThermal.CemRefusal, EmRunService.Run(cem, null, Path.Combine(ws, "results")).Error);

        cem.Solver3D = Em3dSolver.Palace;
        doc.Setups = [EmSetupPersistence.ToEmbedded(cem)];
        Assert.Contains("states Solver3D: Palace", C3dSetups.Read(doc).Single().Refusal);

        // And the second line: every EM lowering refuses a thermal problem by type.
        const double mm = 1e-3;
        var a = Em3dBoundaryKind.Absorbing;
        var box = new Em3dAirBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 1 * mm), new Em3dFaces(a, a, a, a, a, a));
        var p = new Em3dProblem([], [], [], [], box, new Em3dFrequency(1e9, 2e9, 2, Em3dSweepKind.Linear), 20) { Type = Em3dProblemType.Thermal };
        Assert.Equal(Em3dProblem.ThermalIsNotEm, GmshGeoWriter.Write(p, PalaceSettings.Default).Refusal);
        Assert.Equal(Em3dProblem.ThermalIsNotEm, PalaceConfigWriter.Write(p, [], PalaceSettings.Default).Refusal);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A die on a flange, a heat sheet on the die's top, probes, a mesh region and a contact override — clean.</summary>
    private static C3dDocument Clean(bool withSetup = false)
    {
        var flange = Box("flange", "Copper", 0, 0, 0, 1000, 1000, 200);
        C3dBindings.SetExpr(flange, C3dBindings.SpecOf(typeof(C3dBox), nameof(C3dBox.Size))!, 0, new C3dExpr("wf", "Um"));
        var doc = new C3dDocument
        {
            Objects = [flange, Box("die", "Die", 300, 300, 200, 400, 400, 100)],
            Variables =
            [
                new C3dVariable { Name = "wf", Expression = "1000", Unit = "Um" },
                new C3dVariable { Name = "h", Expression = "500", Unit = "Um" },
                new C3dVariable { Name = "fine", Expression = "5", Unit = "Um" },
                new C3dVariable { Name = "Pdiss", Expression = "2" },
                new C3dVariable { Name = "Ths", Expression = "25" },
            ],
            HeatSources = [new C3dHeatSource { Name = "fingers", Power = "Pdiss", Sheet = new C3dHeatSheet { Plane = C3dPlane.XY, Offset = 300 * Um, Rect = Rect(450, 450, 100, 100) } }],
            Probes =
            [
                new C3dProbe { Name = "die_top", Face = "die/zmax", Stat = C3dProbeStat.Max },
                new C3dProbe { Name = "flange_bot", Face = "flange/zmin", Stat = C3dProbeStat.Avg },
                new C3dProbe { Name = "ir", Spot = new C3dProbeSpot { Face = "die/zmax", Center = new(500 * Um, 500 * Um, 300 * Um), Diameter = 50 * Um } },
                new C3dProbe { Name = "pt", Point = new(500 * Um, 500 * Um, 250 * Um) },
            ],
            MeshRegions = [new C3dMeshRegion { Name = "fingers_box", Min = new(400 * Um, 400 * Um, 200 * Um), Size = new(200 * Um, 200 * Um, 100 * Um), SizeUm = 5 }],
            ContactResistances = [new C3dContactResistance { Between = ["die", "flange"], ResistanceM2KW = 1e-6 }],
        };
        if (withSetup) doc.Setups = [EmSetupPersistence.ToEmbedded(HotSetup())];
        return doc;
    }

    private static EmSetup HotSetup() => new()
    {
        Name = "Hot",
        Problem3D = Em3dProblemType.Thermal,
        Thermal = new CemThermal
        {
            Sources = [new CemThermalSource { Name = "fingers", Power = "Pdiss" }],
            Boundaries = [new CemThermalBoundary { Face = "flange/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "Ths" }],
            Sweep = [new CemThermalSweep { Var = "Pdiss", Start = "1", Stop = "10", Points = 10 }],
            Measures = ["Rth = (Tmax(die_top) - Tavg(flange_bot)) / Pdiss"],
        },
    };

    private static List<CircuitRF.Diagnostics.Diagnostic> Findings(C3dDocument doc, string path)
    {
        var res = C3dResolver.Resolve(doc, C3dCell.Of(path));
        Assert.True(res.Ok, string.Join(" ", res.Errors));
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var found = C3dThermal.Places(doc).Concat(C3dThermal.Places(doc, e)).ToList();
        foreach (var s in C3dSetups.Read(doc).Where(s => s.Setup is { IsThermal: true }))
            found.AddRange(C3dThermal.Setup(s.Name, s.Setup!, doc, e, res));
        return found;
    }

    /// <summary>A 1 mm square of 100 µm dielectric, a ground sheet under it, a trace on top, a port, and a Palace setup.</summary>
    private static C3dDocument Microstrip()
    {
        var doc = new C3dDocument
        {
            Objects =
            [
                Box("sub", "Fill", 0, 0, 0, 1000, 1000, 100),
                new C3dSheet { Name = "trace", Material = "Copper", Plane = C3dPlane.XY, Offset = 100 * Um, Rect = Rect(0, 450, 1000, 100) },
                new C3dSheet { Name = "gnd", Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Rect = Rect(0, 0, 1000, 1000) },
            ],
            Ports = [new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.YZ, Offset = 0, Rect = Rect(450, 0, 100, 100) }],
        };
        doc.Setups = [EmSetupPersistence.ToEmbedded(new EmSetup { Name = "S1", Solver3D = Em3dSolver.Palace })];
        return doc;
    }

    private static C3dRect Rect(long u, long v, long du, long dv)
        => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };

    private static C3dBox Box(string name, string material, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    /// <summary>A workspace whose technology holds Copper and Die (with k), Plastic (without), Fill and Air.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, ThermalK = 400 },
                new TechMaterial { Name = "Die", Epsr = 12.9, ThermalK = 46 },
                new TechMaterial { Name = "Plastic", Epsr = 3 },
                new TechMaterial { Name = "Fill", Epsr = 2 },
                new TechMaterial { Name = "Air", Epsr = 1 },
            ],
        });
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

    private static List<(double T, double Rho, double K)> Csv(string metal)
        => [.. File.ReadLines(Path.Combine(RepoRoot(), "testdata/thermal/metals", metal + ".csv"))
               .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('T'))
               .Select(l => l.Split(','))
               .Select(c => (double.Parse(c[0], CultureInfo.InvariantCulture), double.Parse(c[1], CultureInfo.InvariantCulture),
                             double.Parse(c[2], CultureInfo.InvariantCulture)))];

    private static double RhoAt(List<(double T, double Rho, double K)> rows, double t)
    {
        for (int i = 0; i + 1 < rows.Count; i++)
            if (rows[i].T <= t && t <= rows[i + 1].T)
                return rows[i].Rho + (rows[i + 1].Rho - rows[i].Rho) * (t - rows[i].T) / (rows[i + 1].T - rows[i].T);
        throw new ArgumentOutOfRangeException(nameof(t));
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(ThermalDocumentTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
