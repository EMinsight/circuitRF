// ================================================================
//  CapacitanceExamplesTests.cs — brief-dfr31-2: the two capacitance examples, Capacitance Extraction and Test Pad
//  Capacitance, checked and run.
//
//  The 3D EM example's discipline: each workspace's `expected-numbers.json` is the ONE source of every number its README
//  prints. Each value's Readme text must appear verbatim in the README (and the app note quotes the same numbers), and the
//  Benchmark gate re-runs every electrostatic setup and holds each value within its stated tolerance. The routine tier
//  needs no solver: the workspaces check clean, every setup elaborates and resolves its terminals, Pad and Line's numbers
//  agree with the line calculator and with εA/d as the README says, and Compare's schematic reproduces the recorded
//  driven run's |S21| at 2.4 GHz from the electrostatic C.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(Em3dProcessCountersCollection.Name)]
public sealed class CapacitanceExamplesTests(ITestOutputHelper output) : IDisposable
{
    private const string Simple = "Capacitance Extraction", TestPad = "Test Pad Capacitance";
    private const double Eps0 = 8.8541878128e-12, C0 = 299792458;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-dfr31-2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── gate 1: check ───────────────────────────────────────────────────────────────────────────

    /// <summary><c>check</c> on each workspace, as a process, finds no error.</summary>
    [Theory]
    [InlineData(Simple)]
    [InlineData(TestPad)]
    public void Gate1_CheckFindsNoError(string example)
    {
        string json = Cli("check", Root(example), "--json");
        using var doc = JsonDocument.Parse(json);
        var errors = doc.RootElement.GetProperty("diagnostics").EnumerateArray()
                        .Where(d => d.GetProperty("severity").GetString() == "error")
                        .Select(d => d.GetProperty("message").GetString()).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    /// <summary>
    /// The routine tier's stand-in for a run: every electrostatic setup of both examples builds its 3D problem, and its
    /// terminals resolve to the pieces of metal the README says — Pad and Line's pad and line one object each, a test
    /// pad's terminal its whole net, and with Other metal at Ground no piece left floating.
    /// </summary>
    [Fact]
    public void EverySetupElaboratesAndResolvesItsTerminals_WithoutSolving()
    {
        foreach (string example in new[] { Simple, TestPad })
            foreach (var run in Numbers(example).Runs)
            {
                var (setup, doc, path) = Load(Root(example), run);
                var g = C3dProblemAssembly.Assemble(setup, doc, path, Path.Combine(Root(example), ".cws"));
                Assert.True(g.Ok, $"{run.View} / {run.Setup}: {g.Refusal}");
                var named = g.Problem!.Terminals.Where(t => !t.Floating).ToList();
                output.WriteLine($"{run.Setup}: {string.Join("; ", named.Select(t => $"{t.Name} = {t.Objects.Count} object(s)"))}, " +
                                 $"{g.Problem.Terminals.Count(t => t.Floating)} floating");
                Assert.Equal(run.Terminals, named.Select(t => t.Name));
                Assert.Equal(run.Floating, g.Problem.Terminals.Count(t => t.Floating));
                // A click on the bottom pad takes the line on top too: the via joins them into one piece of metal.
                if (example == TestPad && run.Terminals[0] == "TP")
                {
                    Assert.Contains(named[0].Objects, o => o.Contains("/Bottom Copper", StringComparison.Ordinal));
                    Assert.Contains(named[0].Objects, o => o.Contains("/Top Copper", StringComparison.Ordinal));
                }
            }
    }

    // ── the README, the app note and the numbers file agree ─────────────────────────────────────

    [Theory]
    [InlineData(Simple, "examples/Capacitance Extraction/README.md")]
    [InlineData(TestPad, "examples/Test Pad Capacitance/README.md")]
    [InlineData(Simple, "docs/user/src/app-notes/an03-extracting-capacitance.md")]
    [InlineData(TestPad, "docs/user/src/app-notes/an03-extracting-capacitance.md")]
    public void TheReadmeAndTheAppNoteCiteEveryNumber(string example, string page)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), page));
        var numbers = Numbers(example);
        foreach (var run in numbers.Runs)
        {
            if (page.EndsWith("README.md", StringComparison.Ordinal))
                foreach (string s in run.Readme) Assert.Contains(s, text);
            foreach (var v in run.Values) Assert.Contains(v.Readme, text);
        }
        foreach (string s in numbers.Derived.Select(d => d.Readme)) Assert.Contains(s, text);
    }

    // ── gate 2: Pad and Line against the line calculator and εA/d ───────────────────────────────

    /// <summary>
    /// The line's capacitance to ground per metre is within 3 % of √εeff/(c·Z0) from circuitRF's own line calculator (its
    /// circuit-model column, run as a process), and the pad's exceeds the parallel-plate εA/d by the fringing share the
    /// README states.
    /// </summary>
    [Fact]
    public void Gate2_TheLineMatchesTheLineCalculator_AndThePadExceedsParallelPlateByTheStatedFringing()
    {
        var numbers = Numbers(Simple);
        var run = numbers.Runs.Single(r => r.Setup == "Pad and line" && r.Vars is null);
        double lineToGround = run.Values.Single(v => v.Quantity == "Cm_fF" && v.Row == "line" && v.Column == "line").Expected * 1e-15;
        double pad = run.Values.Single(v => v.Quantity == "C_fF" && v.Row == "pad" && v.Column == "pad").Expected * 1e-15;

        string json = Cli("impedance", "--tech", Path.Combine(Root(Simple), "tech", "two-layer-fr4.ctech"),
                          "--layer", "Top Copper (1 oz)", "--width", "0.92mm", "--json");
        using var doc = JsonDocument.Parse(json);
        var model = doc.RootElement.GetProperty("result").GetProperty("impedanceLine").GetProperty("rows")[0].GetProperty("model");
        double z0 = model.GetProperty("z0Static").GetDouble(), eeff = model.GetProperty("eeffStatic").GetDouble();
        double perMetre = Math.Sqrt(eeff) / (C0 * z0), solved = lineToGround / 20e-3;
        output.WriteLine($"C' solved {solved * 1e12:F2} pF/m, line calculator {perMetre * 1e12:F2} pF/m (Z0 {z0:F2} Ω, εeff {eeff:F4})");
        Assert.InRange(solved / perMetre, 0.97, 1.03);
        Assert.Equal(numbers.Derived.Single(d => d.Name == "CprimeCalculator").Value, perMetre * 1e12, 0.05);
        Assert.Equal(numbers.Derived.Single(d => d.Name == "CprimeSolved").Value, solved * 1e12, 0.05);

        double plate = Eps0 * 4.4 * 2e-3 * 2e-3 / 0.508e-3;
        double fringing = 1 - plate / pad;
        output.WriteLine($"pad {pad * 1e15:F1} fF, εA/d {plate * 1e15:F1} fF, fringing {100 * fringing:F1} % of the total");
        Assert.True(pad > plate);
        Assert.Equal(numbers.Derived.Single(d => d.Name == "PlateFf").Value, plate * 1e15, 0.5);
        Assert.Equal(numbers.Derived.Single(d => d.Name == "FringingPercent").Value, 100 * fringing, 0.5);
    }

    /// <summary>The mutual capacitance falls as the gap opens, and the floating line is the Kron reduction of the grounded
    /// one: C_pad − C_pl²/C_ll, to the solve's own last digit.</summary>
    [Fact]
    public void Gate2_TheGapSweepFalls_AndTheFloatingLineIsTheExactReduction()
    {
        var runs = Numbers(Simple).Runs;
        double Mutual(string gap) => runs.Single(r => r.Setup == "Pad and line" && (r.Vars?["gap"] ?? "0.5") == gap)
                                         .Values.Single(v => v.Quantity == "Cm_fF" && v.Row == "pad" && v.Column == "line").Expected;
        Assert.True(Mutual("0.25") > Mutual("0.5") && Mutual("0.5") > Mutual("1"));

        var both = runs.Single(r => r.Setup == "Pad and line" && r.Vars is null).Values;
        double cpp = both.Single(v => v.Quantity == "C_fF" && v.Row == "pad" && v.Column == "pad").Expected;
        double cll = both.Single(v => v.Quantity == "C_fF" && v.Row == "line" && v.Column == "line").Expected;
        double cpl = both.Single(v => v.Quantity == "C_fF" && v.Row == "pad" && v.Column == "line").Expected;
        double floating = runs.Single(r => r.Setup == "Pad, line floating").Values.Single().Expected;
        Assert.Equal(cpp - cpl * cpl / cll, floating, 0.5);
    }

    // ── gate 3: Test Pad Capacitance ────────────────────────────────────────────────────────────

    /// <summary>
    /// The fixed board's test pad loads the line less than the as-built one; Compare's schematic carries the two stub
    /// capacitances the README states (each net less the line alone); and simulated as a process, its shunt-C chain's
    /// |S21| at 2.4 GHz is within 0.5 dB of the recorded driven run's.
    /// </summary>
    [Fact]
    public void Gate3_TheFixIsLower_AndTheElectrostaticCReproducesTheDrivenRunAt2p4GHz()
    {
        var numbers = Numbers(TestPad);
        double Net(string view) => numbers.Runs.Single(r => r.View.StartsWith(view, StringComparison.Ordinal) && r.Floating == 0)
                                          .Values.Single().Expected;
        double built = Net("As built") - Net("Line only"), fixedC = Net("Fixed") - Net("Line only");
        Assert.True(fixedC < built);
        Assert.Equal(numbers.Derived.Single(d => d.Name == "StubBuiltFf").Value, built, 0.5);
        Assert.Equal(numbers.Derived.Single(d => d.Name == "StubFixedFf").Value, fixedC, 0.5);

        string csch = Path.Combine(Root(TestPad), "Compare", "schematic", "Compare.csch");
        string text = File.ReadAllText(csch);
        Assert.Contains($"\"{built / 1000:F3}\"", text);
        Assert.Contains($"\"{fixedC / 1000:F3}\"", text);

        string snp = Path.Combine(_tmp, "compare.s10p");
        Directory.CreateDirectory(_tmp);
        Cli("sparam", csch, "-o", snp);
        var s = TouchstoneIO.ReadFile(snp);
        int k = Array.FindIndex(s.Frequencies, f => Math.Abs(f - 2.4e9) < 1e6);
        Assert.True(k >= 0, "no point at 2.4 GHz");
        double Db(int o, int i) => 20 * Math.Log10(s.Matrices[k][o - 1, i - 1].Magnitude);
        double shuntC = Db(2, 1), driven = Db(8, 7);
        output.WriteLine($"|S21| at 2.4 GHz: shunt C {shuntC:F3} dB, driven {driven:F3} dB, fixed {Db(4, 3):F3} dB, TESTPAD {Db(6, 5):F3} dB");
        Assert.InRange(shuntC - driven, -0.5, 0.5);
        Assert.Equal(numbers.Derived.Single(d => d.Name == "S21ShuntCdB").Value, shuntC, 0.005);
        Assert.Equal(numbers.Derived.Single(d => d.Name == "S21DrivenDb").Value, driven, 0.005);
    }

    // ── the numbers, run ────────────────────────────────────────────────────────────────────────

    /// <summary>Every electrostatic setup of both examples, run as Simulate runs it (with the gap set where the file says),
    /// reproduces each of its values within the tolerance the file states.</summary>
    [CircuitRF.Ui.Tests.Em3d.PalaceFact]
    [Trait("Category", "Benchmark")]
    public void EveryElectrostaticRun_ReproducesTheNumbers()
    {
        foreach (string example in new[] { Simple, TestPad })
        {
            string copy = CopyOf(example);
            foreach (var run in Numbers(example).Runs)
            {
                var (setup, doc, path) = Load(copy, run);
                var wall = Stopwatch.StartNew();
                var result = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(copy, ".cws"), Path.Combine(copy, "results"),
                                                        confirmMemory: _ => true);
                output.WriteLine($"{run.View} / {run.Setup} {Vars(run)}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s — {result.Notes?.LastOrDefault()}");
                Assert.True(result.Status == EmRunStatus.Ok, $"{run.Setup}: {result.Error}");
                foreach (var v in run.Values)
                {
                    double got = Matrix(result, v) * 1e15;
                    output.WriteLine($"  {v.Quantity}[{v.Row}][{v.Column}]: {got:F2} fF (file {v.Expected} ± {v.Tolerance})");
                    Assert.True(Math.Abs(got - v.Expected) <= v.Tolerance, $"{run.Setup} {v.Label}: {got:F2} fF, file says {v.Expected} ± {v.Tolerance}");
                }
            }
        }
    }

    private static string Vars(ExpectedRun run) => run.Vars is null ? "" : string.Join(", ", run.Vars.Select(kv => $"{kv.Key} = {kv.Value}"));

    private static double Matrix(EmRunResult result, ExpectedValue v)
    {
        var c = result.Data![v.Quantity == "Cm_fF" ? Em3dStaticResult.MutualCapacitanceCube : Em3dStaticResult.CapacitanceCube];
        var labels = c.Axis(Em3dStaticResult.AxisI).Labels!;
        int i = Array.IndexOf(labels, v.Row), j = Array.IndexOf(labels, v.Column);
        Assert.True(i >= 0 && j >= 0, $"no [{v.Row}, {v.Column}] in {string.Join(", ", labels)}");
        return c.RealValues[i * labels.Length + j];
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static string Root(string example)
        => Path.Combine(CircuitRF.Design.Workspace.ExampleWorkspaces.ResolveRoot(RepoRoot()) ?? throw new InvalidOperationException("no examples/"), example);

    /// <summary>The run's setup and its 3D view, with the run's VARs set as the Variables panel sets them.</summary>
    private static (EmSetup, C3dDocument, string) Load(string root, ExpectedRun run)
    {
        string path = Path.Combine(root, run.View);
        var doc = C3dPersistence.LoadFromFile(path);
        foreach (var (name, value) in run.Vars ?? [])
            doc.Variables.Single(v => v.Name == name).Expression = value;
        var (embedded, why) = C3dSetups.Select(doc, run.Setup);
        Assert.True(embedded is not null, why);
        return (C3dSetups.ForRun(embedded!, path), doc, path);
    }

    private string CopyOf(string example)
    {
        string to = Path.Combine(_tmp, example);
        foreach (string file in Directory.EnumerateFiles(Root(example), "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}results{Path.DirectorySeparatorChar}")) continue;
            string dest = Path.Combine(to, Path.GetRelativePath(Root(example), file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return to;
    }

    private static string Cli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(CapacitanceExamplesTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return outTask.GetAwaiter().GetResult();
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }

    // ── expected-numbers.json ───────────────────────────────────────────────────────────────────

    private sealed record ExpectedValue(string Quantity, string Label, string Row, string Column, double Expected, double Tolerance,
                                        string Readme);

    private sealed record ExpectedRun(string View, string Setup, Dictionary<string, string>? Vars, List<string> Terminals,
                                      int Floating, double PeakGB, List<string> Readme, List<ExpectedValue> Values);

    /// <summary>A number the README states that is worked out from the runs (a fraction, a difference, a closed form).</summary>
    private sealed record DerivedValue(string Name, double Value, string Readme);

    private sealed record ExpectedNumbers(List<ExpectedRun> Runs, List<DerivedValue> Derived);

    private static ExpectedNumbers Numbers(string example)
        => JsonSerializer.Deserialize<ExpectedNumbers>(File.ReadAllText(Path.Combine(Root(example), "expected-numbers.json")))!;
}
