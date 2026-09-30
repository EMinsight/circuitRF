// ================================================================
//  ThermalSmallSignalTests.cs — brief-em3d-80's design-side gates: the refusal of a material without a heat capacity
//  (gate 6), the Foster network written as a .cnl that `check` passes and whose AC analysis is the fit (gate 7), and the
//  Rth matrix, Z_th and a pulse train run end to end through `em`. The solver's own gates (1–5) are
//  tests/Thermal.Tests/SmallSignalGateTests.cs.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal.Frequency;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ThermalSmallSignalTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-th80-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;   // DBU per µm

    // ── gate 6: a material without ρ or c is refused, by name ─────────────────────────────────────────

    [Fact]
    public void Gate6_ZthOverAMaterialWithoutHeatCapacity_IsRefusedNamingItAndTheObject()
    {
        string ws = Workspace(dieHeat: false);
        var doc = Doc(Setup());
        string path = WriteC3d(ws, doc);
        var run = EmRunService.RunThreeDView(C3dSetups.ForRun(C3dSetups.Select(doc, "Pulsed").Setup!, path), doc, path, null, Path.Combine(ws, "results"));
        output.WriteLine(run.Error);
        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains("'Die', which states no heat capacity (DensityKgM3 and SpecificHeat)", run.Error);
        Assert.Contains("'die'", run.Error);
        Assert.DoesNotContain("'flange'", run.Error);

        // without Zth nothing asks for it: the same document runs its Rth matrix alone
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        var rthOnly = Setup();
        rthOnly.Thermal!.Zth = null;
        rthOnly.Thermal.Pulse = null;
        Assert.DoesNotContain(C3dThermal.Setup("Pulsed", rthOnly, doc, e, e.Resolution!), d => d.Id == C3dThermal.D.MaterialHeatId);
        // and a pulse without Zth is refused, saying why
        var noZ = Setup();
        noZ.Thermal!.Zth = null;
        Assert.Contains(C3dThermal.Setup("Pulsed", noZ, doc, e, e.Resolution!), d => d.Id == C3dThermal.D.SmallSignalId && d.Render().Contains("add a Zth section"));
    }

    // ── gate 7: the Foster network as a netlist ───────────────────────────────────────────────────────

    /// <summary>A network of brief 72's Z2b shape written as the run writes it: `check` passes on the file, and `sparam` on its
    /// own bench reads the fitted Z(jω) back through S11 to 1e-9.</summary>
    [Fact]
    public void Gate7_TheFosterCnl_PassesCheck_AndItsAcAnalysisIsTheFit()
    {
        Directory.CreateDirectory(_root);
        var net = new FosterNetwork
        {
            Terms = [new(0.2, 0.3e-6), new(0.4, 5e-6), new(0.8, 200e-6), new(1.5, 50e-3)], FitError = 0.004,
        };
        string file = ThermalFosterNetlist.PathFor(_root, "Cell Pulsed.thermal", "gate finger");
        File.WriteAllText(file, ThermalFosterNetlist.Write("gate finger", "Pulsed", net, 0.01, 1e6, 80));
        output.WriteLine(File.ReadAllText(file));

        var (code, stdout, stderr) = RunCli("check", file);
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, code);
        Assert.True(AcWorst(file, net) is var (worst, points) && points >= 80 && worst <= 1e-9,
                    $"the AC analysis of the network misses the fit by {worst:G3}");
    }

    /// <summary>`sparam` on the file's own bench: the largest relative distance of Z = Z0(1 + S11)/(1 − S11) from the network's
    /// Z(jω), and how many frequencies it was read at.</summary>
    private (double Worst, int Points) AcWorst(string cnl, FosterNetwork net)
    {
        string npy = Path.ChangeExtension(cnl, ".npy");
        var (code, stdout, stderr) = RunCli("sparam", cnl, "-o", npy);
        Assert.True(code == 0, stdout + stderr);
        var data = DataSetImporter.Import(npy).DataSet;
        var s = NetworkMetrics.FindSCube(data)!;
        double[] f = s.Axes[0].Values;
        var s11 = s.ComplexValues;
        double z0 = double.Parse(File.ReadAllLines(cnl).First(l => l.StartsWith("Port:", StringComparison.Ordinal)).Split(' ')
                                     .First(t => t.StartsWith("Z=", StringComparison.Ordinal))[2..], System.Globalization.CultureInfo.InvariantCulture);
        double worst = 0;
        for (int k = 0; k < f.Length; k++)
        {
            var z = z0 * (1 + s11[k]) / (1 - s11[k]);
            worst = Math.Max(worst, (z - net.Z(f[k])).Magnitude / net.Z(f[k]).Magnitude);
        }
        output.WriteLine($"{f.Length} frequencies, {f[0]:G3} – {f[^1]:G3} Hz: worst {worst:G3}");
        return (worst, f.Length);
    }

    // ── end to end, through `em` ─────────────────────────────────────────────────────────────────────

    /// <summary>Two fingers on a die on a flange: the Rth matrix, Z_th of each finger and a probe, the fits, the .cnl networks
    /// and a 10 % pulse train over a two-point duty sweep.</summary>
    [GmshFact]
    public void EndToEnd_EmRunsRthZthAndAPulse_TheMatrixTheCnlAndThePeaks()
    {
        string ws = Workspace(dieHeat: true);
        string path = WriteC3d(ws, Doc(Setup()));
        var (code, stdout, stderr) = RunCli("em", path, "--setup", "Pulsed");
        output.WriteLine(stdout);
        output.WriteLine(stderr);
        Assert.True(code == 0, stderr);
        Assert.Contains("Rth matrix (K/W)", stdout);

        var data = DataSetImporter.Import(Path.Combine(ws, "results", "Cell Pulsed.thermal.npy")).DataSet;
        var rth = data[$"{ThermalRunService.SmallSignalGroup}.{ThermalRunService.RthCube}"];
        Assert.Equal(["west", "east"], rth.Axes[0].Labels!);
        var r = rth.RealValues;
        Assert.All(r, v => Assert.True(v > 0));
        Assert.True(r[0] > r[1], "a finger heats itself more than its neighbour");
        Assert.Equal(r[1], r[2], 1e-9 * r[0]);                                              // Avg: symmetric

        // Z_th at DC is the Rth matrix's entry (one system, two routes)
        var z = data[$"{ThermalRunService.SmallSignalGroup}.Zth:west:west"].ComplexValues;
        Assert.Equal(r[0], z[0].Real, 1e-9 * r[0]);
        Assert.True(z[^1].Magnitude < 0.5 * z[0].Magnitude, "Z_th falls with frequency");
        Assert.Contains($"{ThermalRunService.SmallSignalGroup}.Zth:die_top:east",
                        data.Cubes.Keys.Concat(data.Groups.SelectMany(g => data.CubesIn(g).Keys.Select(k => g + "." + k))));

        // the pulse: peak above the average, above the baseline; the duty sweep raises the average
        var peak = data[$"{ThermalRunService.Group}.Pulse:west:peak"].RealValues;
        var avg = data[$"{ThermalRunService.Group}.Pulse:west:avg"].RealValues;
        var single = data[$"{ThermalRunService.Group}.Pulse:west:single"].RealValues;
        Assert.Equal(2, peak.Length);
        for (int i = 0; i < 2; i++) Assert.True(peak[i] > avg[i] && avg[i] > 25 && single[i] <= peak[i], $"point {i}: {peak[i]} {avg[i]} {single[i]}");
        Assert.True(avg[1] > avg[0]);

        // the networks: one per source, listed as written; the run's file is the writer's text for its own fit (gate 7 proves
        // that text passes check and is the fit under an AC analysis), and check passes on it as written
        foreach (string source in new[] { "west", "east" })
        {
            string cnl = ThermalFosterNetlist.PathFor(Path.Combine(ws, "results"), "Cell Pulsed.thermal", source);
            Assert.Contains($"Wrote {cnl}", stdout);
            string key = $"{ThermalRunService.SmallSignalGroup}.Foster:{source}:{source}";
            var terms = data[key + ":R"].RealValues.Zip(data[key + ":tau"].RealValues, (a, b) => new FosterTerm(a, b)).ToList();
            var fit = new FosterNetwork { Terms = terms, FitError = data[key + ":error"].RealValues[0] };
            Assert.Equal(ThermalFosterNetlist.Write(source, "Cell Pulsed", fit, 1, 1e5, 21), File.ReadAllText(cnl));
            Assert.Equal(r[source == "west" ? 0 : 3], fit.Rth, 1e-9 * r[0]);                // ΣR is the matrix's diagonal
        }
        var (ccode, cout, cerr) = RunCli("check", ThermalFosterNetlist.PathFor(Path.Combine(ws, "results"), "Cell Pulsed.thermal", "west"));
        Assert.True(ccode == 0, cout + cerr);
    }

    // ── the Setups page, the file, the plot ──────────────────────────────────────────────────────────

    /// <summary>The thermal page shows the three sections as the file states them and writes each edit back; a switch removes
    /// its section; a bad number is refused beside the box and writes nothing. "*" is written as the one string.</summary>
    [Fact]
    public void ThePage_WritesRthZthAndPulse_AndTheFileSpellsEveryAsAStar()
    {
        string ws = Workspace(dieHeat: true);
        var doc = Doc(Setup());
        string path = WriteC3d(ws, doc);
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel(path, C3dSetups.Select(doc, "Pulsed").Setup!, embedded: true);
        EmSetup? written = null;
        vm.EmbeddedCommit = (_, after, _) => written = EmSetupPersistence.Deserialize(after);
        vm.ThermalContext = new CircuitRF.Ui.Layout.Em.EmThermalContext(() => doc, () => null, () => null, () => path, () => Path.Combine(ws, "results"));
        Assert.Equal((true, "", false), (vm.ThermalRthOn, vm.ThermalRthSources, vm.ThermalRthMax));
        Assert.Equal((true, "die_top", "1", "4"), (vm.ThermalZthOn, vm.ThermalZthProbes, vm.ThermalZthStart, vm.ThermalZthPerDecade));
        Assert.Equal((true, "1 ms", "duty", ""), (vm.ThermalPulseOn, vm.ThermalPulsePeriod, vm.ThermalPulseDuty, vm.ThermalPulsePeak));

        vm.ThermalRthSources = "west, east";
        vm.ThermalRthMax = true;                                                           // a switch commits at once
        Assert.Equal(["west", "east"], written!.Thermal!.Rth!.Sources);
        Assert.Equal(ThermalRthStat.Max, written.Thermal.Rth.Stat);
        vm.ThermalZthPerDecade = "6";
        vm.ThermalPulsePeak = "2";
        vm.CommitThermal();
        Assert.Equal(6, written.Thermal.Zth!.PerDecade);
        Assert.Equal(("2", "duty"), (written.Thermal.Pulse!.PeakPower, written.Thermal.Pulse.Duty));
        vm.ThermalPulseOn = false;
        Assert.Null(written.Thermal.Pulse);
        written = null;
        vm.ThermalZthPerDecade = "a few";
        vm.CommitThermal();
        Assert.Null(written);
        Assert.Contains("per decade", vm.ThermalError);

        string json = EmSetupPersistence.Serialize(Setup());
        Assert.Contains("\"Sources\": \"*\"", json);
        Assert.Equal(["*"], EmSetupPersistence.Deserialize(json).Thermal!.Rth!.Sources);
    }

    /// <summary>The result table reads each Z_th the run wrote, and the line panel draws it against a logarithmic frequency.</summary>
    [Fact]
    public void TheTable_ReadsZth_AndThePlotIsLogFrequency()
    {
        var ds = new DataSet();
        ds.AddToGroup(ThermalRunService.Group, "Energy:in", DataCube.Scalar(1.0));
        ds.AddToGroup(ThermalRunService.SmallSignalGroup, "Zth:die_top:west",
                      new DataCube([new Axis("freq", [0, 10, 100], "Hz")], [new Complex(2, 0), new Complex(1.5, -0.5), new Complex(0.4, -0.3)]));
        var zth = Assert.Single(ThermalResultTable.From(ds).Zth);
        Assert.Equal(("die_top", "west", 3), (zth.Place, zth.Source, zth.Z.Length));
        var plot = CircuitRF.Ui.ThreeD.ThermalPlots.Line(new CircuitRF.Ui.ThreeD.C3dThermalLine("Z", [10, 100], [1.58, 0.5], "Frequency (Hz)", "|Z_th| (K/W)", LogX: true));
        Assert.Equal(CircuitRF.Render.DataDisplay.AxisScale.Log, plot.Axes.XScale);
        Assert.Single(plot.Traces);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A copper flange with a die on it and two 0.5 W fingers on the die's top.</summary>
    private static C3dDocument Doc(EmSetup setup) => new()
    {
        Objects = [Box("flange", "Copper", 0, 0, 0, 1000, 1000, 200), Box("die", "Die", 300, 300, 200, 400, 400, 100)],
        Variables = [new C3dVariable { Name = "Pf", Expression = "0.5" }, new C3dVariable { Name = "duty", Expression = "0.1" }],
        HeatSources =
        [
            new C3dHeatSource { Name = "west", Power = "Pf", Sheet = new C3dHeatSheet { Plane = C3dPlane.XY, Offset = 300 * Um, Rect = Rect(420, 450, 40, 100) } },
            new C3dHeatSource { Name = "east", Power = "Pf", Sheet = new C3dHeatSheet { Plane = C3dPlane.XY, Offset = 300 * Um, Rect = Rect(540, 450, 40, 100) } },
        ],
        Probes = [new C3dProbe { Name = "die_top", Face = ["die/zmax"], Stat = C3dProbeStat.Max }],
        Setups = [EmSetupPersistence.ToEmbedded(setup)],
    };

    private static EmSetup Setup() => new()
    {
        Name = "Pulsed",
        Problem3D = Em3dProblemType.Thermal,
        Thermal = new CemThermal
        {
            Boundaries = [new CemThermalBoundary { Face = "flange/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" }],
            Sweep = [new CemThermalSweep { Var = "duty", Start = "0.1", Stop = "0.3", Points = 2 }],
            Rth = new CemThermalRth { Sources = ["*"] },
            Zth = new CemThermalZth { Probes = ["die_top"], StartHz = "1", StopHz = "1e5", PerDecade = 4 },
            Mesh = new CemThermalMesh { Order = 1 },                                        // wiring, not accuracy: gate 3 holds the iterative path
            Pulse = new CemThermalPulse { Period = "1 ms", Duty = "duty" },
        },
    };

    private static C3dRect Rect(long u, long v, long du, long dv)
        => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };

    private static C3dBox Box(string name, string material, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    private string Workspace(bool dieHeat)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, ThermalK = 400, DensityKgM3 = 8960, SpecificHeat = 385 },
                dieHeat ? new TechMaterial { Name = "Die", Epsr = 9.7, ThermalK = 370, DensityKgM3 = 3210, SpecificHeat = 690 }
                        : new TechMaterial { Name = "Die", Epsr = 9.7, ThermalK = 370 },
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
        string cliDir = typeof(ThermalSmallSignalTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
