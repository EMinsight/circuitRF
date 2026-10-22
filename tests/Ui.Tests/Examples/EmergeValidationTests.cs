// ================================================================
//  EmergeValidationTests.cs — brief-emi-8 R-emi8-4: a short, fixed list of shipped .c3d setups run on EMerge and held
//  to Palace's recorded answers, with every tolerance in the example's own expected-numbers.json.
//
//  | Case                                  | Checks                                                                        |
//  | 3D Eigenmode Cavity, Modes (EMerge)   | the cavity mode within ModeTolerance (0.2 %) of Palace's                       |
//  | 3D Eigenmode Cavity, Driven (EMerge)  | the notch within one sweep step of the eigenmode; off the notch, |S21| within  |
//  |                                       | OffNotchToleranceDb of Palace's                                               |
//  | Cavity with post, both                | the cavity mode above the band; no notch in it; |S21| within the same of Palace's |
//  | 3D Connector Launch (lumped)          | |S21| by |Δ|S|| against Palace's recorded run, at every frequency            |
//
//  The solves need EMerge, so they are [EmergeFact] (Skipped with the reason when it is absent) and Category=Benchmark
//  (~6 min: two one-core eigen solves). The first test needs nothing: the README quotes what the file records.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class EmergeValidationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-emi8-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    /// <summary>Each cell ships its EMerge setups on EMerge, and the README quotes every EMerge number the file records.</summary>
    [Fact]
    public void TheEMergeSetupsShip_AndTheReadmeQuotesTheirNumbers()
    {
        var n = Eigenmode();
        string readme = File.ReadAllText(Path.Combine(EigenmodeRoot(), "README.md"));
        foreach (var cell in n.Cells)
        {
            var (doc, path, _) = Cell(cell.Cell);
            foreach (string name in new[] { cell.Emerge.Modes.Setup, cell.Emerge.Driven.Setup })
            {
                var (embedded, why) = C3dSetups.Select(doc, name);
                Assert.True(embedded is not null, why);
                Assert.True(C3dSetups.ForRun(embedded!, path).Solver3D.Runs(EmergeSolver.Id), $"{cell.Cell} '{name}' does not run EMerge");
            }
            foreach (string s in cell.Emerge.Modes.Cost.Concat(cell.Emerge.Driven.Cost).Concat(cell.Emerge.Driven.Readme)
                                     .Concat(cell.Emerge.Modes.Values.SelectMany(v => v.Readme)))
                Assert.Contains(s, readme);
        }
    }

    /// <summary>The 3D Eigenmode example's four EMerge setups against Palace's recorded answers.</summary>
    [CircuitRF.Ui.Tests.Em3d.EmergeFact]
    [Trait("Category", "Benchmark")]
    public void TheEigenmodeExample_OnEMerge_AgreesWithPalace()
    {
        var n = Eigenmode();
        var v = n.Validation;
        foreach (var cell in n.Cells)
        {
            var palaceMode = cell.Modes.Values.Single(m => m.Mode == cell.CavityMode).GHz;
            var modes = Run(Cell(cell.Cell), cell.Emerge.Modes.Setup);
            var f = modes.Data![Em3dEigenResult.FrequencyCube].RealValues;
            var q = modes.Data![Em3dEigenResult.QCube].RealValues;
            int row = Enumerable.Range(0, q.Length).First(k => q[k] > 10) + 1;
            double ghz = f[row - 1] / 1e9;
            output.WriteLine($"{cell.Cell}: EMerge cavity mode {row} at {ghz:F5} GHz, Q {q[row - 1]:F1}; Palace {palaceMode} GHz");
            Assert.Equal(cell.Emerge.CavityMode, row);
            Assert.True(Math.Abs(ghz - palaceMode) <= v.ModeTolerance * palaceMode, $"{cell.Cell}: {ghz:F5} GHz, Palace {palaceMode}");

            var snp = TouchstoneIO.ReadFile(Run(Cell(cell.Cell), cell.Emerge.Driven.Setup).SnpPath!);
            var s21 = snp.Matrices.Select(m => 20 * Math.Log10(m[1, 0].Magnitude)).ToArray();
            int min = Array.IndexOf(s21, s21.Min());
            double notchGHz = snp.Frequencies[min] / 1e9;
            double offNotch = Enumerable.Range(0, s21.Length).Where(k => Math.Abs(snp.Frequencies[k] - snp.Frequencies[min]) > v.OffNotchGHz * 1e9)
                                        .Min(k => s21[k]);
            output.WriteLine($"{cell.Cell}: |S21| min {s21[min]:F3} dB at {notchGHz:F3} GHz; worst off the notch {offNotch:F4} dB");
            if (cell.Driven.NotchGHz is { } palaceNotch)
            {
                Assert.True(Math.Abs(notchGHz - ghz) <= n.Band.StepGHz * (1 + 1e-9), $"notch {notchGHz} GHz, EMerge's eigenmode {ghz:F5}");
                Assert.True(Math.Abs(notchGHz - palaceNotch) <= n.Band.StepGHz * (1 + 1e-9), $"notch {notchGHz} GHz, Palace's {palaceNotch}");
                Assert.True(Math.Abs(offNotch - v.PalaceOffNotchS21Db) <= v.OffNotchToleranceDb,
                            $"{cell.Cell}: off the notch {offNotch:F4} dB, Palace {v.PalaceOffNotchS21Db}");
            }
            else
            {
                Assert.True(ghz > n.Band.StopGHz, $"{cell.Cell}: cavity mode {ghz:F5} GHz is inside the band");
                Assert.True(s21.Min() > -1, $"{cell.Cell}: a notch of {s21.Min():F2} dB");
                Assert.True(Math.Abs(s21.Min() - cell.Driven.MinS21Db) <= v.OffNotchToleranceDb,
                            $"{cell.Cell}: |S21| min {s21.Min():F4} dB, Palace {cell.Driven.MinS21Db}");
            }
        }
    }

    /// <summary>The 3D Connector's Launch, its Palace setup run on EMerge, against Palace's recorded |S21| at every frequency,
    /// by |Δ|S||. |S11| and |S22| are reported, not held: no solver's is converged at these settings — Palace's own moves by
    /// up to 0.03 with its element order and openEMS's is up to 0.10 from it (src/Design/RESOLVED.md, EMI-8).</summary>
    [CircuitRF.Ui.Tests.Em3d.EmergeFact]
    [Trait("Category", "Benchmark")]
    public void TheConnectorLaunch_OnEMerge_AgreesWithPalace()
    {
        var e = Connector().Emerge;
        string path = Path.Combine(ConnectorRoot(), "Launch", "3d", "Launch.c3d");
        var doc = C3dPersistence.LoadFromFile(path);
        var (embedded, why) = C3dSetups.Select(doc, e.Setup);
        Assert.True(embedded is not null, why);
        var setup = C3dSetups.ForRun(embedded!, path);
        setup.Solver3D = EmergeSolver.Id;
        var result = Run(setup, doc, path, Path.Combine(ConnectorRoot(), ".cws"), "Launch " + e.Setup);
        var snp = TouchstoneIO.ReadFile(result.SnpPath!);
        var far = new List<string>();
        foreach (var r in e.Against)
        {
            int k = Array.FindIndex(snp.Frequencies, x => Math.Abs(x - r.GHz * 1e9) < 1);
            Assert.True(k >= 0, $"no point at {r.GHz} GHz");
            foreach (var (name, palaceDb, s) in new[] { ("S11", r.S11_dB, snp.Matrices[k][0, 0]), ("S21", r.S21_dB, snp.Matrices[k][1, 0]),
                                                        ("S22", r.S22_dB, snp.Matrices[k][1, 1]) })
            {
                double d = Math.Abs(s.Magnitude - Math.Pow(10, palaceDb / 20));
                bool held = name == "S21";
                output.WriteLine($"  {r.GHz,4} GHz |{name}| {20 * Math.Log10(s.Magnitude),9:F3} dB, Palace {palaceDb,9:F3} dB, |Δ|S|| {d:F4}{(held ? "" : " (not held)")}");
                if (held && d > e.S21Tolerance) far.Add($"|{name}| at {r.GHz} GHz: |Δ|S|| {d:F4}");
            }
        }
        Assert.True(far.Count == 0, "EMerge's Launch is further from Palace's than " + e.S21Tolerance + ":\n" + string.Join("\n", far));
    }

    // ── runs ────────────────────────────────────────────────────────────────────────────────────

    private EmRunResult Run((C3dDocument Doc, string Path, string Cws) cell, string setupName)
    {
        var (embedded, why) = C3dSetups.Select(cell.Doc, setupName);
        Assert.True(embedded is not null, why);
        return Run(C3dSetups.ForRun(embedded!, cell.Path), cell.Doc, cell.Path, cell.Cws, setupName);
    }

    private EmRunResult Run(EmSetup setup, C3dDocument doc, string path, string cws, string label)
    {
        var wall = Stopwatch.StartNew();
        var result = EmRunService.RunThreeDView(setup, doc, path, cws, Path.Combine(_tmp, "results"), confirmMemory: _ => true);
        output.WriteLine($"{label}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s — {result.Notes?.LastOrDefault()}");
        Assert.True(result.Status == EmRunStatus.Ok, $"{label}: {result.Error}");
        return result;
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static string ExamplesRoot()
        => ExampleWorkspaces.ResolveRoot(RepoRoot()) ?? throw new InvalidOperationException("no examples/");

    private static string EigenmodeRoot() => Path.Combine(ExamplesRoot(), "3D Eigenmode");

    private static string ConnectorRoot() => Path.Combine(ExamplesRoot(), "3D Connector");

    private static (C3dDocument Doc, string Path, string Cws) Cell(string cell)
    {
        string path = Path.Combine(EigenmodeRoot(), cell, "3d", cell + ".c3d");
        return (C3dPersistence.LoadFromFile(path), path, Path.Combine(EigenmodeRoot(), ".cws"));
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }

    // ── expected-numbers.json: the parts this file reads ────────────────────────────────────────

    private sealed record Band(double StopGHz, double StepGHz);

    private sealed record ModeRow(int Mode, double GHz, List<string> Readme);

    private sealed record ModesRun(string Setup, List<string> Cost, List<ModeRow> Values);

    private sealed record DrivenRun(string Setup, List<string> Cost, double? NotchGHz, double MinS21Db, List<string> Readme);

    private sealed record EmergeCell(int CavityMode, ModesRun Modes, DrivenRun Driven);

    private sealed record CellNumbers(string Cell, int CavityMode, ModesRun Modes, DrivenRun Driven, EmergeCell Emerge);

    private sealed record Validation(double ModeTolerance, double OffNotchGHz, double OffNotchToleranceDb, double PalaceOffNotchS21Db);

    private sealed record EigenmodeNumbers(Band Band, List<CellNumbers> Cells, Validation Validation);

    private sealed record Point(double GHz, double S11_dB, double S21_dB, double S22_dB);

    private sealed record ConnectorEmerge(string Setup, double S21Tolerance, List<Point> Against);

    private sealed record ConnectorNumbers(ConnectorEmerge Emerge);

    private static EigenmodeNumbers Eigenmode()
        => JsonSerializer.Deserialize<EigenmodeNumbers>(File.ReadAllText(Path.Combine(EigenmodeRoot(), "expected-numbers.json")))!;

    private static ConnectorNumbers Connector()
        => JsonSerializer.Deserialize<ConnectorNumbers>(File.ReadAllText(Path.Combine(ConnectorRoot(), "expected-numbers.json")))!;
}
