// brief-emdc-2 §6 — the 0 Hz row in every 3D result: the splice (no solver), what both routes say about a sweep from 0 Hz, one
// DC solve per Compare run, each solver end to end, the circuit the overview measured, and a run with no Gmsh.
//
// The run tests share Em3dRunService.DcGmsh and the DcSolves counter, which is why they are one class: xunit runs a class's
// tests one at a time.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class Em3dDcRowTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emdc2-" + Guid.NewGuid().ToString("N")[..10]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1. the splice ────────────────────────────────────────────────────────────────────────────

    /// <summary>A group holding S and a second cube on 1–10 GHz gains f = 0: the DC S in S, NaN in the other cube; the Z0 cube
    /// (no frequency axis) and another group are untouched. A group already starting at 0 throws, naming it.</summary>
    [Fact]
    public void Prepend_PutsTheDcMatrixInS_NaNInTheOtherCube_AndTouchesNoOtherGroup()
    {
        double[] f = [1e9, 5.5e9, 10e9];
        var fAxis = new Axis("freq", f, "Hz");
        var ports = new Axis("i", [1, 2], "port");
        var data = new DataSet();
        data.Add("S", new DataCube([fAxis, ports, new Axis("j", [1, 2], "port")], [.. Enumerable.Range(0, 12).Select(k => new Complex(k, 0))]));
        data.Add("Z0", DataSetBuilder.BuildZ0Cube([50, 50]));
        data.Add("Gain", new DataCube([fAxis], [1.0, 2.0, 3.0]) { Unit = "dB" });
        data.AddToGroup("palace", "ModeZ", new DataCube([new Axis("f", f, "Hz")], [7.0, 8.0, 9.0]));

        Em3dDcRow.Prepend(data, "", new Em3dDcResult(new double[,] { { 1, 0 }, { 0, 1 } }, null, [], 10, 1));

        var s = data.Cubes["S"];
        Assert.Equal([0, 1e9, 5.5e9, 10e9], s.Axes[0].Values);
        Assert.Equal([1, 0, 0, 1, 0, 1, 2, 3], s.ComplexValues.Take(8).Select(c => c.Real));
        var gain = data.Cubes["Gain"];
        Assert.True(double.IsNaN(gain.RealValues[0]));
        Assert.Equal([1.0, 2.0, 3.0], gain.RealValues.Skip(1));
        Assert.Equal("dB", gain.Unit);
        Assert.Equal(2, data.Cubes["Z0"].Axes[0].Length);
        Assert.Equal(f, data.CubesIn("palace")["ModeZ"].Axes[0].Values);

        var e = Assert.Throws<InvalidOperationException>(() => Em3dDcRow.Prepend(data, "", new Em3dDcResult(new double[2, 2], null, [], 0, 0)));
        Assert.Contains("Group ''", e.Message);
    }

    // ── §1: both routes ──────────────────────────────────────────────────────────────────────────

    /// <summary>A driven sweep from 0 Hz is cut to its positive points on both routes, which carry WantsDc and say the same one
    /// sentence; the problem is the one a sweep without 0 Hz makes, so no hash can see the difference.</summary>
    [Fact]
    public void BothRoutes_ASweepFromZero_WantDc_AndSayItInTheSameWords()
    {
        var (setup, source) = GapCapacitor(Em3dSolverId.Palace);
        var clay = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(clay.Ok, clay.Refusal);
        Assert.True(clay.WantsDc);
        Assert.Contains(Em3dDcRow.ZeroHzNote, clay.Notes);
        Assert.Equal(5e9, clay.Problem!.Frequency.StartHz);

        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Fill", Epsr = 2 }, new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        const long um = 1000;
        var doc = new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(2000 * um, 1000 * um, 100 * um) },
                new C3dBox { Name = "trace", Material = "Copper", Min = new C3dPoint3(0, 400 * um, 100 * um), Size = new C3dPoint3(2000 * um, 200 * um, 50 * um) },
            ],
        };
        C3dRect Rect(long u, long v, long du, long dv) => new() { Min = new C3dPoint2(u * um, v * um), Size = new C3dPoint2(du * um, dv * um) };
        doc.Ports.Add(new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.YZ, Offset = 0, Rect = Rect(400, 0, 200, 100) });
        doc.Ports.Add(new C3dPort { Number = 2, Name = "P2", Plane = C3dPlane.YZ, Offset = 2000 * um, Rect = Rect(400, 0, 200, 100) });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var c3dSetup = new EmSetup
        {
            Name = "S1", Solver3D = Em3dSolverId.Palace, Frequency = FromZero,
            AirBox = new EmAirBox(ZMin: new EmAirBoxFace(0, Em3dBoundaryKind.Pec)),
        };
        var c3d = C3dProblemAssembly.Assemble(c3dSetup, doc, path, Path.Combine(ws, ".cws"));
        Assert.True(c3d.Problem is not null, c3d.Refusal);
        Assert.True(c3d.WantsDc);
        Assert.Contains(Em3dDcRow.ZeroHzNote, c3d.Notes);
    }

    // ── 3. one solve per Compare run ─────────────────────────────────────────────────────────────

    /// <summary>openEMS and EMerge compared on the gap capacitor: one DC solve for the run, both files carry the same 0 Hz row,
    /// and the comparison says once that the row is circuitRF's own.</summary>
    [DcRowFact(Em3dSolverId.OpenEms, EmergeSolver.Id)]
    [Trait("Category", "Benchmark")]
    public void ACompareRun_SolvesDcOnce_AndBothFilesCarryTheSameRow()
    {
        var (setup, source) = GapCapacitor(Em3dSolverId.OpenEms);
        setup.Solver3D = Em3dSolverChoice.Compare([OpenEmsSolver.Id, EmergeSolver.Id], OpenEmsSolver.Id);
        long before = Em3dRunService.DcSolves;
        var result = EmRunService.Run(setup, source, Path.Combine(_root, "results"));
        Log(result);
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        Assert.Equal(1, Em3dRunService.DcSolves - before);
        var files = result.Outputs!.Where(o => o.Kind == "touchstone").Select(o => o.Path).ToList();
        Assert.Equal(2, files.Count);
        var rows = files.Select(p => TouchstoneIO.ReadFile(p)).Select(s => (s.Frequencies[0], s[0][0, 0], s[0][1, 0])).ToList();
        Assert.Equal(0, rows[0].Item1);
        Assert.Equal(rows[0], rows[1]);
        Assert.Contains(Em3dComparison.DcRowSentence, result.Notes!);
    }

    // ── 4. each solver end to end ────────────────────────────────────────────────────────────────

    [DcRowFact(Em3dSolverId.Palace)]
    [Trait("Category", "Benchmark")]
    public void Palace_TheGapCapacitorsFile_BeginsWithItsDcRow() => EndToEnd(Em3dSolverId.Palace);

    /// <summary>Also writes the file test 5 reads, when run with CRF_EMDC2_WRITE_FIXTURE set to a path.</summary>
    [DcRowFact(Em3dSolverId.OpenEms)]
    [Trait("Category", "Benchmark")]
    public void OpenEms_TheGapCapacitorsFile_BeginsWithItsDcRow()
    {
        string snp = EndToEnd(Em3dSolverId.OpenEms);
        if (Environment.GetEnvironmentVariable("CRF_EMDC2_WRITE_FIXTURE") is { Length: > 0 } fixture) File.Copy(snp, fixture, true);
    }

    [DcRowFact(EmergeSolver.Id)]
    public void Emerge_TheGapCapacitorsFile_BeginsWithItsDcRow() => EndToEnd(EmergeSolver.Id);

    /// <summary>The gap capacitor on <paramref name="solver"/>, its sweep from 0 Hz: the .sNp's first row is 0 Hz with
    /// |S₂₁| &lt; 1e-12, its header carries the DC row's line, and the .npy carries the same row.</summary>
    private string EndToEnd(Em3dSolverId solver)
    {
        var (setup, source) = GapCapacitor(solver);
        var result = EmRunService.Run(setup, source, Path.Combine(_root, "results-" + solver));
        Log(result);
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        var snp = TouchstoneIO.ReadFile(result.SnpPath!);
        Assert.Equal([0, 5e9, 10e9], snp.Frequencies);
        Assert.True(snp[0][1, 0].Magnitude < 1e-12, $"|S21| at 0 Hz = {snp[0][1, 0].Magnitude:G3}");
        Assert.Contains(File.ReadLines(result.SnpPath!),
                        l => l.StartsWith("! " + Em3dDcRow.HeaderPrefix + "circuitRF conduction solve of the conductors, ", StringComparison.Ordinal)
                             && l.EndsWith(" s; ports as terminal pairs", StringComparison.Ordinal));
        var npy = DataSetImporter.Import(result.NpyPath!).DataSet;
        var s = npy[DataSetBuilder.FindCubeSpec(npy, "S")!];
        Assert.Equal(0, s.Axes[0].Values[0]);
        Assert.True((snp[0][1, 0] - s.ComplexValues[2]).Magnitude < 1e-9 && (snp[0][0, 0] - s.ComplexValues[0]).Magnitude < 1e-9,
                    $"the .npy's 0 Hz row {s.ComplexValues[0]}, {s.ComplexValues[2]} against the .sNp's {snp[0][0, 0]}, {snp[0][1, 0]}");
        return result.SnpPath!;
    }

    // ── 5. the defect, closed ────────────────────────────────────────────────────────────────────

    /// <summary>The overview's circuit: 5 V, the solved gap capacitor's .s2p (test 4's), a 50 Ω load. With the DC row the gap is
    /// an open and the load sits at 0 V.</summary>
    [Fact]
    public void ACircuitsDcSolve_ThroughTheSolvedGapCapacitor_PutsTheLoadAtZero()
    {
        string snp = Path.Combine(RepoRoot(), "testdata", "em3d", "dc-row", "gap-capacitor.s2p");
        Directory.CreateDirectory(_root);
        string cnl = Path.Combine(_root, "block.cnl");
        File.WriteAllText(cnl, $"""
            Vdc:V1 in 0 Vdc=5
            SnP:EM1 in out 0 NumPorts=2 File="{snp}"
            R:RL out 0 R=50
            analysis DC1 type=dc
            measure Vload = DC1.V("out")
            """);
        var run = CliProcess.Run(RepoRoot(), [], ["dc", cnl]);
        Assert.True(run.ExitCode == 0, run.StdErr);
        output.WriteLine(run.StdOut);
        string line = run.StdOut.Replace("\r", "").Split('\n').Single(l => l.TrimStart().StartsWith("Vload = ", StringComparison.Ordinal));
        double v = double.Parse(line.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Math.Abs(v) < 1e-6, $"the load is at {v} V");
    }

    // ── 6. Gmsh missing ──────────────────────────────────────────────────────────────────────────

    /// <summary>With Gmsh not found, an openEMS run whose sweep holds 0 Hz still succeeds; its Warnings carry the refusal, the
    /// cost and the install offer, and its header says "DC row: none".</summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void WithoutGmsh_TheRunSucceeds_WarnsWithTheInstallOffer_AndTheFileSaysNone()
    {
        var missing = new SolverReadiness(SolverTool.Gmsh, "Gmsh", null, [], [], SolverDiscovery.Gmsh.DescribeFailure([], OperatingSystem.IsWindows()));
        Em3dRunService.DcGmsh = () => missing;
        try
        {
            var (setup, source) = GapCapacitor(Em3dSolverId.OpenEms);
            var result = EmRunService.Run(setup, source, Path.Combine(_root, "results"));
            Log(result);
            Assert.True(result.Status == EmRunStatus.Ok, result.Error);
            string warning = Assert.Single(result.Warnings, w => w.StartsWith("No 0 Hz row: ", StringComparison.Ordinal));
            Assert.Contains(missing.Refusal!, warning);
            Assert.EndsWith("uses the lowest frequency.", warning);
            Assert.Equal("gmsh", result.MessageInstalls?.GetValueOrDefault(warning));
            Assert.Equal([5e9, 10e9], TouchstoneIO.ReadFile(result.SnpPath!).Frequencies);
            Assert.Contains(File.ReadLines(result.SnpPath!), l => l.StartsWith("! " + Em3dDcRow.HeaderPrefix + "none - ", StringComparison.Ordinal));
        }
        finally { Em3dRunService.DcGmsh = null; }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static readonly FrequencySpec FromZero = new("0", "10", 3, SweepKind.Linear, "GHz", "GHz");

    /// <summary>A series gap capacitor on 20 mil RO4350B: two 1.1 mm wide, 2 mm long 1 oz traces 0.2 mm apart, a port at each
    /// outer end, swept 0, 5 and 10 GHz.</summary>
    private static (EmSetup, EmLayoutSource) GapCapacitor(Em3dSolverId solver)
    {
        const string clay = """
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": -550000, "X2": 2000000, "Y2": 550000 },
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 2200000, "Y1": -550000, "X2": 4200000, "Y2": 550000 },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 0, "Y": 0, "Text": "1", "Height": 400000, "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 4200000, "Y": 0, "Text": "2", "Height": 400000, "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var setup = new EmSetup { Name = "gap", LayoutRef = "gap/layout/gap.clay", Frequency = FromZero, Solver3D = solver };
        return (setup, new EmLayoutSource("/nowhere/gap.clay", view, tech, view.DbuPerMicron));
    }

    private void Log(EmRunResult r)
    {
        foreach (var w in r.Warnings) output.WriteLine("WARNING " + w);
        foreach (var n in r.Notes ?? []) output.WriteLine(n);
        foreach (var e in r.Errors ?? []) output.WriteLine("ERROR " + e);
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }
}

/// <summary>Skips, naming what is missing, unless every named solver would run and Gmsh (the DC row's mesher) is found.</summary>
public sealed class DcRowFactAttribute : FactAttribute
{
    public DcRowFactAttribute(params Em3dSolverId[] solvers)
    {
        var blocked = solvers.SelectMany(s => SolverDiscovery.ReadinessFor(s))
                             .Append(SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)))
                             .FirstOrDefault(r => !r.Proceeds);
        if (blocked is not null) Skip = $"needs {string.Join(", ", solvers)} and Gmsh: {blocked.Refusal}";
    }
}
