// ================================================================
//  ThermalCircuitLinkTests.cs — brief-em3d-79's gates: a thermal setup driven from a circuit's HB power sweep.
//    Gate 1  the pin currents of an S-parameter block — 1 A peak through a synthetic matched two-port gives |I| = 1 A at k = 1
//            on both pins, and the phasor is the waveform's PEAK; only when the instance is asked for (gate 2's opt-in half).
//    Gate 3  instance resolution: one used, two listed, another file named — and check's rule that the link excludes per-port
//            currents.
//    Gate 4  end to end [GmshFact]: three drive levels, three wires on the thru's pads; the result is on the HB's axis with the
//            wire table, the carried cube and a LimitC crossing interpolated between the right two points; staleness.
//    Gate 5  a point the HB did not converge at is skipped and flagged, and its neighbours still solve [GmshFact].
//  Gate 2's other half — every existing HB result unchanged — is the existing HB suites, run unmodified (RESOLVED).
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Data;
using BondStyle = CircuitRF.WBond.BondStyle;
using WireCrossSection = CircuitRF.WBond.WireCrossSection;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ThermalCircuitLinkTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-th79-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;

    // ── gate 1 (and the opt-in half of gate 2) ────────────────────────────────────────────────────────

    /// <summary>
    /// A 1 A current tone into a matched 30° line terminated in 50 Ω: all of it enters pin 1 and leaves pin 2, so both pins carry
    /// |I| = 1 A at k = 1 and nothing else. The phasor's magnitude is the time waveform's PEAK (HbFft's convention, which PeakOf
    /// relies on). Without the opt-in the instance's pins are not in the result at all.
    /// </summary>
    [Fact]
    public void Gate1_PinCurrents_AreOneAmperePeak_OnlyWhenAskedFor()
    {
        string dir = Dir();
        string snp = Path.Combine(dir, "thru line.s2p");
        WriteThru(snp);
        string cnl = Path.Combine(dir, "lin.cnl");
        File.WriteAllText(cnl, Circuit(snp, "Values=1", nonlinear: false));
        var (lib, tb) = CnlReader.ReadFile(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var top = ChainSelector.Select(tb, "HB1", a => a is HarmonicBalanceAnalysis, "HB", "").Selected!;
        Assert.Equal("SW1", top.Name);   // the inner analysis is promoted to its sweep, as `hb` promotes it

        var ds = HbCircuitRun.Solve(lib, tb, nl, top, HbCircuitRun.Settings(pinCurrents: ["X3"]), dir).Data;
        var i = ds["I"];
        var labels = i.Axes[^2].Labels!;
        int K1 = i.Axes[^1].Length, B = labels.Length;
        foreach (int pin in new[] { 1, 2 })
        {
            int b = Array.IndexOf(labels, $"X3:{pin}");
            Assert.True(b >= 0, string.Join(", ", labels));
            var spec = Enumerable.Range(0, K1).Select(k => i.ComplexValues[b * K1 + k]).ToArray();
            Assert.Equal(1.0, ThermalCircuitLink.PeakOf(spec[1]), 1e-9);
            for (int k = 0; k < K1; k++) if (k != 1) Assert.True(spec[k].Magnitude < 1e-9, $"pin {pin} k={k}: {spec[k].Magnitude}");
            // the convention: the waveform this spectrum IS peaks at |X[1]| — a peak phasor, not an RMS one
            var wave = new double[HbFft.GridSize(K1 - 1, 256)];   // finely sampled, so the sampled maximum is the peak to 1e-6
            HbFft.Inverse(spec, K1 - 1, wave);
            Assert.Equal(ThermalCircuitLink.PeakOf(spec[1]), wave.Max(Math.Abs), 1e-5);
        }

        using var nl2 = new Elaborator(lib).Elaborate(tb);
        var plain = HbCircuitRun.Solve(lib, tb, nl2, top, HbCircuitRun.Settings(), dir).Data;
        Assert.False(plain.Contains("I") && plain["I"].Axes[^2].Labels!.Any(l => l.StartsWith("X3:", StringComparison.Ordinal)));
    }

    // ── gate 3 ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_TheInstance_OneIsUsed_TwoAreListed_AnotherFileIsNamed()
    {
        string dir = Dir();
        string ours = Path.Combine(dir, "results", "Thru EM"), other = Path.Combine(dir, "other.s2p");
        Directory.CreateDirectory(Path.GetDirectoryName(ours)!);
        WriteThru(ours + ".s2p");
        WriteThru(other);
        List<(string, string)> results = [("EM", ours)];
        ElaboratedComponent? Resolve(string extra, string? named, out string? why)
        {
            string cnl = Path.Combine(dir, Guid.NewGuid().ToString("N")[..6] + ".cnl");
            File.WriteAllText(cnl, Circuit(ours + ".s2p", "Values=1", nonlinear: false) + extra);
            var (lib, tb) = CnlReader.ReadFile(cnl);
            using var nl = new Elaborator(lib).Elaborate(tb);
            return ThermalCircuitLink.Instance(nl, named, results, out why);
        }

        Assert.Equal("X3", Resolve("", null, out _)!.InstancePath);
        Assert.Null(Resolve($"SnP:X4 out 0 NumPorts=2 File=\"{ours}.s2p\"\n", null, out string? two));
        Assert.Contains("'X3'", two!);
        Assert.Contains("'X4'", two);
        Assert.Null(Resolve($"SnP:X5 out 0 NumPorts=2 File=\"{other}\"\n", "X5", out string? file));
        Assert.Contains(other, file!);

        // R-em3d79-1 — a link excludes per-port currents, and a sweep of the setup's own
        var doc = new C3dDocument { Objects = [Box("pad", 0, 0, 200, 450)], Ports = [Port(1)] };
        var setup = ThermalSetup(new CemThermalFromCircuit { Schematic = "x.cnl" });
        setup.Thermal!.Currents!.Add(new CemThermalCurrent { Port = 1, Dc = "1" });
        setup.Thermal.Sweep = [new CemThermalSweep { Var = "a", Start = "0", Stop = "1", Points = 2 }];
        var res = C3dResolver.Resolve(doc, C3dCell.Of(Path.Combine(dir, "x.c3d")));
        var found = C3dThermal.Setup("HotHB", setup, doc, null, res).Select(d => d.Render()).ToList();
        Assert.Contains(found, m => m.Contains("takes its currents from a circuit and also states a Dc"));
        Assert.Contains(found, m => m.Contains("states a Sweep of its own"));

        // Designer feedback round 10 — check passes the document's path, and a schematic that does not exist is then a
        // finding rather than a clean check and a refused run. The editor and the run pass none: each reports it itself.
        Assert.DoesNotContain(found, m => m.Contains("does not resolve"));
        var checkedAtPath = C3dThermal.Setup("HotHB", setup, doc, null, res, (Path.Combine(dir, "x.c3d"), Path.Combine(dir, "results")))
                                      .Select(d => d.Render()).ToList();
        Assert.Contains(checkedAtPath, m => m.Contains("does not resolve") && m.Contains("'x.cnl' does not exist"));
    }

    // ── R-em3d79-4 — the Setups page ─────────────────────────────────────────────────────────────────

    /// <summary>The page reads the link out of the currents (it is not a port row), offers only HB chains and only instances of
    /// this view's result, shows port ↔ pin ↔ net, and writes the link back unchanged when anything else on the page commits.</summary>
    [Fact]
    public void ThePage_KeepsTheLink_AndOffersOnlyWhatItCanUse()
    {
        var (ws, path, doc, _) = Model(null, "Values=0.6,1", nonlinear: false);
        var setup = C3dSetups.Select(doc, "HotHB").Setup!;
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel(path, setup, embedded: true);
        EmSetup? written = null;
        vm.EmbeddedCommit = (_, after, _) => written = EmSetupPersistence.Deserialize(after);
        vm.ThermalContext = new CircuitRF.Ui.Layout.Em.EmThermalContext(() => doc, () => null, () => null, () => path, () => Path.Combine(ws, "results"));

        Assert.True(vm.ThermalFromCircuit);
        Assert.Empty(vm.ThermalCurrents);
        Assert.Equal(["", "SW1", "HB1"], vm.ThermalCircuitAnalyses);
        Assert.Equal(["", "X3"], vm.ThermalCircuitInstances);
        Assert.Equal(["port 1 (P1) ↔ pin 1 ↔ net 'in'", "port 2 (P2) ↔ pin 2 ↔ net 'out'"], vm.ThermalCircuitMapping.Select(r => r.Text));
        Assert.Equal("", vm.ThermalCircuitProblem);

        vm.ThermalWireConvectionH = "10";
        vm.ThermalWireAmbient = "25";
        vm.CommitThermal();
        var link = Assert.Single(written!.Thermal!.Currents!).FromCircuit!;
        Assert.Equal(("../../circuit/pa.cnl", "HB1", "Pout"), (link.Schematic, link.Analysis, link.Carry!.Single()));
    }

    // ── gates 4 and 5 ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The gate-1 circuit swept over three drive levels drives three wires between the thru's pads: the thermal result has three
    /// points on the HB's own axis, the wire table, the carried Pout, the pin currents used, and the wire probe's limit crossed
    /// between the right two points, interpolated linearly and said in a sentence. Changing the S-parameter file marks the
    /// result stale, naming it (brief-em3d-87: one input of the run's manifest).
    /// </summary>
    [GmshFact]
    public void Gate4_EndToEnd_OnTheHbAxis_WithTheLimitCrossingInterpolated()
    {
        var (ws, path, doc, snp) = Model(LimitC, "Values=0.6,1,1.4", nonlinear: false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var run = Run(ws, path, doc);
        output.WriteLine($"{clock.Elapsed.TotalSeconds:F1} s");
        output.WriteLine(string.Join("\n", run.Notes ?? []));
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        var data = run.Data!;

        var hot = data["thermal.T:hot:max"];
        Assert.Equal("Iamp", Assert.Single(hot.Axes).Name);
        double[] t = hot.RealValues, x = hot.Axes[0].Values;
        output.WriteLine(string.Join(", ", t.Select(v => v.ToString("F2", CultureInfo.InvariantCulture))));
        Assert.Equal([0.6, 1, 1.4], x);
        Assert.True(t[1] < LimitC && t[2] >= LimitC, $"the limit is meant to fall between points 2 and 3: {string.Join(", ", t)}");
        double expected = x[1] + (LimitC - t[1]) / (t[2] - t[1]) * (x[2] - x[1]);
        Assert.Equal(expected, data["circuit.LimitAt:hot"].RealValues.Single(), 1e-12);
        Assert.Contains(run.Notes!, n => n.StartsWith("Probe 'hot' reaches its limit", StringComparison.Ordinal) && n.Contains("Iamp ≈") && n.Contains("Pout ≈"));

        foreach (string w in new[] { "w1[0]", "w1[1]", "w1[2]" }) Assert.Equal(3, data[$"thermal.Twire:{w}:max"].RealValues.Length);
        Assert.Equal(3, data["circuit.Pout"].RealValues.Length);
        Assert.Equal([0.6, 1, 1.4], data["circuit.Ipin:1:h1"].RealValues.Select(v => Math.Round(v, 9)));
        Assert.All(data["circuit.HbSkipped"].RealValues, v => Assert.Equal(0, v));

        // R-em3d79-3b — the result goes stale when its S-parameter file changes
        string runDir = ThermalRunService.RunDirectory(Path.Combine(ws, "results"), C3dSetups.ForRun(C3dSetups.Select(doc, "HotHB").Setup!, path));
        Assert.False(C3dRunDocument.Check(runDir, doc, path)!.Stale);
        File.AppendAllText(snp, "! edited\n");
        Assert.Equal([Path.GetFileName(snp)], C3dRunDocument.Check(runDir, doc, path)!.Changed);
    }

    /// <summary>The middle point's HB is allowed one Newton step and does not converge: it is skipped and flagged, and the
    /// points either side still solve.</summary>
    [GmshFact]
    public void Gate5_ANonConvergedHbPoint_IsSkipped_AndItsNeighboursSolve()
    {
        var (ws, path, doc, _) = Model(null, "Values=0.6,1,1.4", nonlinear: true);
        var run = Run(ws, path, doc);
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        var data = run.Data!;
        Assert.Equal([0.0, 1, 0], data["circuit.HbSkipped"].RealValues);
        var t = data["thermal.Twire:w1[0]:max"].RealValues;
        Assert.True(double.IsFinite(t[0]) && double.IsNaN(t[1]) && double.IsFinite(t[2]), string.Join(", ", t));
        Assert.Contains(run.Warnings, w => w.Contains(ThermalRunService.HbNotConverged));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The wire probe's limit, °C: between the hottest wire's temperature at the second and third drive level.</summary>
    private const double LimitC = 38;

    private static EmRunResult Run(string ws, string path, C3dDocument doc)
        => EmRunService.RunThreeDView(C3dSetups.ForRun(C3dSetups.Select(doc, "HotHB").Setup!, path), doc, path, null, Path.Combine(ws, "results"));

    /// <summary>A pad and a lead with three gold wires between them, two ports, an EM setup (never run: its predicted result is
    /// written here as the synthetic thru) and the thermal setup driven from the circuit.</summary>
    private (string Ws, string Path, C3dDocument Doc, string Snp) Model(double? limitC, string values, bool nonlinear)
    {
        string ws = Workspace();
        var doc = new C3dDocument
        {
            Objects = [Box("pad", 0, 0, 200, 450), Box("lead", 750, 0, 200, 450), Wire()],
            Ports = [Port(1), Port(2)],
            Probes = [new C3dProbe { Name = "hot", Wire = "w1[0]", Stat = C3dProbeStat.Max, LimitC = limitC }],
        };
        string path = Path.Combine(ws, "Thru", "3d", "Thru.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var em = new EmSetup { Name = "EM", Solver3D = Em3dSolver.Palace };
        var thermal = ThermalSetup(new CemThermalFromCircuit { Schematic = "../../circuit/pa.cnl", Analysis = "HB1", Carry = ["Pout"] });
        doc.Setups = [EmSetupPersistence.ToEmbedded(em), EmSetupPersistence.ToEmbedded(thermal)];
        C3dPersistence.SaveToFile(path, doc);
        var result = ThermalCircuitLink.ResultPaths(doc, path, Path.Combine(ws, "results"));
        string snp = Assert.Single(result).BasePath + ".s2p";
        Directory.CreateDirectory(Path.GetDirectoryName(snp)!);
        WriteThru(snp);
        string cnl = Path.Combine(ws, "circuit", "pa.cnl");
        Directory.CreateDirectory(Path.GetDirectoryName(cnl)!);
        File.WriteAllText(cnl, Circuit(snp, values, nonlinear));
        return (ws, path, doc, snp);
    }

    /// <summary>A current tone into instance X3 (the EM result) terminated in 50 Ω, swept over the tone's amplitude. Nonlinear: a
    /// weak cubic conductance at the load, and one Newton step allowed at Iamp = 1 — so that point does not converge.</summary>
    private static string Circuit(string snp, string values, bool nonlinear) =>
        $"""
        f0 = 2e9
        Iamp = 1
        I_1Tone:S1 in 0 Freq=f0 I=Iamp
        SnP:X3 in out NumPorts=2 File="{snp}"
        R:RL out 0 R=50
        measure Pout = 10*log10(0.5*mag(HB1.V("out", 1))*mag(HB1.V("out", 1))/50) + 30
        analysis HB1 type=hb Tone=f0 MaxHarm=3{(nonlinear ? " MaxIter=if(Iamp==1,1,50) DriveStepping=Never" : "")}
        analysis SW1 type=parametric_sweep Var=Iamp {values} Inner=HB1

        """ + (nonlinear ? "SDD:NL out 0 I[1,0]=_v1/1e6+1e-9*_v1^3\n" : "");

    /// <summary>A matched 30° line: S21 = S12 = e^{-j30°}, S11 = S22 = 0, at every frequency.</summary>
    private static void WriteThru(string path)
    {
        var s = Complex.FromPolarCoordinates(1, -Math.PI / 6);
        string re = s.Real.ToString("R", CultureInfo.InvariantCulture), im = s.Imaginary.ToString("R", CultureInfo.InvariantCulture);
        File.WriteAllLines(path, ["# GHZ S RI R 50", .. new[] { 0.1, 1, 2, 4, 6, 8, 10 }.Select(f =>
            $"{f.ToString(CultureInfo.InvariantCulture)} 0 0 {re} {im} {re} {im} 0 0")]);
    }

    private static EmSetup ThermalSetup(CemThermalFromCircuit link) => new()
    {
        Name = "HotHB", Problem3D = Em3dProblemType.Thermal,
        Thermal = new CemThermal
        {
            Boundaries = [new CemThermalBoundary { Face = "pad/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" },
                          new CemThermalBoundary { Face = "lead/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" }],
            Currents = [new CemThermalCurrent { FromCircuit = link }],
            Mesh = new CemThermalMesh { Order = 1 },   // first order: the gate is the link, not the field's accuracy
        },
    };

    private static C3dWire Wire() => new()
    {
        Name = "w1", Material = "Gold", DiameterUm = 25.4, Section = WireCrossSection.Round,
        Points = [new C3dPoint3(100 * Um, 100 * Um, 50 * Um), new C3dPoint3(250 * Um, 100 * Um, 250 * Um),
                  new C3dPoint3(650 * Um, 100 * Um, 250 * Um), new C3dPoint3(850 * Um, 100 * Um, 50 * Um)],
        Start = new C3dWireEnd { Style = BondStyle.Wedge, FootLengthUm = 30 }, End = new C3dWireEnd { Style = BondStyle.Wedge, FootLengthUm = 30 },
        Array = new C3dWireArray { Count = 3, Pitch = new C3dPoint3(0, 80 * Um, 0) },
    };

    private static C3dPort Port(int n) => new()
    {
        Number = n, Name = $"P{n}", Plane = C3dPlane.XY, Offset = 0,
        Rect = new C3dRect { Min = new C3dPoint2(200 * Um, (n == 1 ? 150 : 320) * Um), Size = new C3dPoint2(550 * Um, (n == 1 ? 150 : 100) * Um) },
    };

    private static C3dBox Box(string name, long x, long y, long dx, long dy)
        => new() { Name = name, Material = "Copper", Min = new C3dPoint3(x * Um, y * Um, 0), Size = new C3dPoint3(dx * Um, dy * Um, 50 * Um) };

    private string Dir()
    {
        string d = Path.Combine(_root, "d" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    private string Workspace()
    {
        string ws = Dir();
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, Alpha20 = 0.00393, ThermalK = 400 },
                new TechMaterial { Name = "Gold", Sigma20 = 4.514e7, Alpha20 = 0.0037, ThermalK = 318 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }
}
