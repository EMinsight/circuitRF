// ================================================================
//  ThermalWireTests.cs — brief-em3d-77's design-side gates: the 1D chain's length (gate 5) and its section-blindness (gate 6),
//  and the electrothermal run end to end through `em` (gate 11). The solver's own gates (1–4, 7, 9, 10) are
//  tests/Thermal.Tests/ElectrothermalGateTests.cs.
// ================================================================

using System.Diagnostics;
using System.Reflection;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using BondStyle = CircuitRF.WBond.BondStyle;
using WireCrossSection = CircuitRF.WBond.WireCrossSection;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ThermalWireTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-th77-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;   // DBU per µm

    // ── gate 5: the chain's length is the 3D arc length ────────────────────────────────────────────────

    /// <summary>A ball-bonded wire leaving its ball at an angle (so a vertical neck is inserted) and dropping vertically before its
    /// wedge: the chain's length is the resolved centreline's 3D arc length to 1e-12, and not its plan length.</summary>
    [Fact]
    public void Gate5_TheChainIsTheCentrelines3DArcLength()
    {
        var e = Elaborate(Doc(WireSection.Circle, ball: true));
        var report = e.Wires.Single(w => w.Name == "w1");
        var sweep = (Em3dSweep)e.Solids.Single(s => s.Name == "w1").Primitive;
        var plan = ThermalWireLowering.Chain(report, sweep)!;
        double arc = 0;
        for (int i = 1; i < sweep.Path.Count; i++) arc += Dist(sweep.Path[i - 1], sweep.Path[i]);
        output.WriteLine($"path {sweep.Path.Count} vertices; chain {plan.ChainLengthM * 1e6:F6} µm, arc {arc * 1e6:F6} µm, plan {plan.PlanLengthM * 1e6:F3} µm; " +
                         $"{(plan.NodeCount - 1) / 2} elements; s from {plan.S[0] * 1e6:F3} µm");
        Assert.True(report.Start.Neck, "the fixture's ball end has a neck");
        Assert.Equal(arc, plan.ChainLengthM, 1e-12);
        Assert.True(plan.ChainLengthM - plan.PlanLengthM > 100e-6, "the 3D length differs from the plan length");
        Assert.Equal(0, plan.S[0]);                                   // a ball start: s is 0 at the ball's top, the start heel
        // every centreline vertex is a chain node
        foreach (var v in sweep.Path)
            Assert.Contains(Enumerable.Range(0, plan.NodeCount), k => Dist(v, new Point3(plan.Points[3 * k], plan.Points[3 * k + 1], plan.Points[3 * k + 2])) == 0);
    }

    // ── gate 6: πd²/4, whatever the EM section ─────────────────────────────────────────────────────────

    /// <summary>A hexagon-section wire and a round one of the same diameter are the same thermal problem: the same chain, node for
    /// node and bit for bit, and the solver takes its area from the diameter alone — so their results are identical.</summary>
    [Fact]
    public void Gate6_AHexagonAndARoundWire_AreOneThermalProblem()
    {
        var hex = Elaborate(Doc(WireSection.Hexagon, ball: false));
        var round = Elaborate(Doc(WireSection.Circle, ball: false));
        var ph = ThermalWireLowering.Chain(hex.Wires.Single(w => w.Name == "w1"), (Em3dSweep)hex.Solids.Single(s => s.Name == "w1").Primitive)!;
        var pr = ThermalWireLowering.Chain(round.Wires.Single(w => w.Name == "w1"), (Em3dSweep)round.Solids.Single(s => s.Name == "w1").Primitive)!;
        var sh = (Em3dSweep)hex.Solids.Single(s => s.Name == "w1").Primitive;
        var sr = (Em3dSweep)round.Solids.Single(s => s.Name == "w1").Primitive;
        Assert.NotEqual(sh.Path[0].Z, sr.Path[0].Z);                   // the EM sweeps differ: a hexagon's foot sits lower
        Assert.Equal(pr.Points, ph.Points);
        Assert.Equal(pr.S, ph.S);
        Assert.Equal(pr.OnPad, ph.OnPad);
        Assert.Equal(pr.DiameterM, ph.DiameterM);
    }

    // ── gate 11: end to end through `em` ─────────────────────────────────────────────────────────────

    /// <summary>A pad, a lead, three wires (a drawn array), a mould block over them, and a DC sweep of three points through
    /// `em`: the run succeeds, the .npy holds the wire table (T(s), the maximum, current, power and resistance per wire), the
    /// three wires carry the port's current between them, and the notes state both switches. ~36 s in Debug (Gmsh, a CLI
    /// process, 28k unknowns at three points), so it is in the opt-in tier.</summary>
    [GmshFact]
    [Trait("Category", "Benchmark")]
    public void Gate11_EmRunsTheCurrentSweep_AndTheNpyHoldsTheWireTable()
    {
        string ws = Workspace();
        var doc = Doc(WireSection.Circle, ball: true, count: 3, mould: true);
        doc.Ports = [new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.XY, Offset = 0, Rect = Rect(200, 150, 550, 150) }];
        doc.Probes = [new C3dProbe { Name = "middle", Wire = "w1[1]", LimitC = 250 }];
        doc.Variables = [new C3dVariable { Name = "Id", Expression = "1" }];
        var setup = new EmSetup
        {
            Name = "Dc", Problem3D = Em3dProblemType.Thermal,
            Thermal = new CemThermal
            {
                Boundaries = [new CemThermalBoundary { Face = "pad/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" },
                              new CemThermalBoundary { Face = "lead/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" }],
                Currents = [new CemThermalCurrent { Port = 1, Dc = "Id" }],
                Sweep = [new CemThermalSweep { Var = "Id", Start = "0.5", Stop = "1.5", Points = 3 }],
                Measures = ["Thot = Tmax(middle)"],
            },
        };
        doc.Setups = [EmSetupPersistence.ToEmbedded(setup)];
        string path = WriteC3d(ws, doc);
        var clock = Stopwatch.StartNew();
        var (code, stdout, stderr) = RunCli("em", path, "--setup", "Dc");
        output.WriteLine($"{clock.Elapsed.TotalSeconds:F1} s");
        output.WriteLine(stdout);
        output.WriteLine(stderr);
        Assert.True(code == 0, stderr);
        Assert.Contains("Conductive balance: σ(T) on, k(T) on", stderr);
        Assert.Contains("solved as 1D conduction elements", stderr);

        var data = DataSetImporter.Import(Path.Combine(ws, "results", "Cell Dc.thermal.npy")).DataSet;
        var names = new[] { "w1[0]", "w1[1]", "w1[2]" };
        double sum = 0;
        foreach (string w in names)
        {
            var along = data[$"wires.Twire:{w}(s)"];
            Assert.Equal(2, along.Rank);
            Assert.Equal(3, along.Axes[0].Length);
            var max = data[$"thermal.Twire:{w}:max"].RealValues;
            Assert.True(max[2] > max[0] && max[0] > 25, $"{w}: {string.Join(", ", max)}");
            Assert.All(data[$"thermal.Rwire:{w}"].RealValues, r => Assert.True(r > 0));
            Assert.All(data[$"thermal.Pwire:{w}"].RealValues, p => Assert.True(p > 0));
            sum += Math.Abs(data[$"thermal.Iwire:{w}"].RealValues[2]);
        }
        Assert.Equal(1.5, sum, 1e-4);
        Assert.Equal([0.0, 0, 0], data["thermal.Runaway"].RealValues);
        Assert.All(data["thermal.Energy:balance"].RealValues, b => Assert.True(b <= 1e-6, $"balance {b}"));
        var thot = data["Thot"].RealValues;
        Assert.Equal(data["thermal.Twire:w1[1]:max"].RealValues[2], thot[2], 1e-9);  // the probe reads the middle wire, feet included
        var table = ThermalResultTable.From(data);
        Assert.Equal(names, table.Wires.Select(w => w.Wire).Order());
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    public enum WireSection { Circle, Hexagon }

    /// <summary>A copper pad and lead 550 µm apart, a gold wire (or an array of <paramref name="count"/>, 125 µm apart) from the pad to
    /// the lead — a ball start leaving at an angle, a flat top, a vertical drop and a wedge on the lead — and optionally a mould
    /// block over everything.</summary>
    private static C3dDocument Doc(WireSection section, bool ball, int count = 1, bool mould = false)
    {
        var wire = new C3dWire
        {
            Name = "w1", Material = "Gold", DiameterUm = 25.4, Section = section == WireSection.Circle ? WireCrossSection.Round : WireCrossSection.Hexagon,
            Points = [new(100 * Um, 100 * Um, 50 * Um), new(250 * Um, 100 * Um, 250 * Um), new(650 * Um, 100 * Um, 250 * Um),
                      new(650 * Um, 100 * Um, 120 * Um), new(850 * Um, 100 * Um, 50 * Um)],
            Start = new C3dWireEnd { Style = ball ? BondStyle.Ball : BondStyle.Wedge, FootLengthUm = ball ? null : 50 },
            End = new C3dWireEnd { Style = BondStyle.Wedge, FootLengthUm = 50 },
            Array = count > 1 ? new C3dWireArray { Count = count, Pitch = new C3dPoint3(0, 125 * Um, 0) } : null,
        };
        List<C3dObject> objects = [Box("pad", "Copper", 0, 0, 0, 200, 450, 50), Box("lead", "Copper", 750, 0, 0, 200, 450, 50)];
        if (mould) objects.Add(Box("mould", "Mould", -50, -50, 0, 1050, 550, 350));
        objects.Add(wire);
        return new C3dDocument { Objects = objects };
    }

    private C3dElaboration Elaborate(C3dDocument doc)
    {
        string ws = Workspace();
        string path = WriteC3d(ws, doc);
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        return e;
    }

    private static C3dRect Rect(long u, long v, long du, long dv)
        => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };

    private static C3dBox Box(string name, string material, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    private static double Dist(Point3 a, Point3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, Alpha20 = 0.00393, ThermalK = 400 },
                new TechMaterial { Name = "Gold", Sigma20 = 4.514e7, Alpha20 = 0.0037, ThermalK = 318 },
                new TechMaterial { Name = "Mould", Epsr = 4, ThermalK = 0.8 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, C3dDocument doc)
    {
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(ThermalWireTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
