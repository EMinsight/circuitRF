// ================================================================
//  Em3dEigenmodeExampleTests.cs — the shipped 3D Eigenmode example (brief-em3d-118): a line in a lidded cavity
//  whose first mode is inside the band, and the same cavity with a grounded post that moves it above.
//
//  Brief 30's discipline: `expected-numbers.json` beside the README is the ONE source of every number the README
//  and em-setup.md #eigenmodes print. Gates 1-4 need no solver; gate 5 re-runs the four shipped setups on Palace
//  and skips without it, so it is Category=Benchmark.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class Em3dEigenmodeExampleTests(ITestOutputHelper output) : IDisposable
{
    private const double Mm = 1e-3;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-em3d118-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── 1. check ────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>check</c> on the workspace, as a process: no error and no warning.</summary>
    [Fact]
    public void Gate1_CheckIsClean()
    {
        var (errors, warnings) = Check(Root());
        Assert.Empty(errors);
        Assert.Empty(warnings);
    }

    // ── 2. elaboration ──────────────────────────────────────────────────────────────────────────

    /// <summary>Both cells elaborate; only the second has the post; and each cell's saved field plot shows the mode table's
    /// cavity row, which is mode 2 with the post (the line's own row is below it).</summary>
    [Fact]
    public void Gate2_BothCellsElaborate_AndEachPlotShowsTheCavityMode()
    {
        foreach (var cell in Numbers().Cells)
        {
            var (doc, path, cws) = Cell(cell.Cell);
            var e = new C3dElaborator().Elaborate(doc, path, cws);
            Assert.True(e.Ok, string.Join(" ", e.Refusals));
            Assert.Equal(cell.Cell == "Cavity with post", e.Solids.Any(s => s.Name == "post"));
            var plot = Assert.Single(doc.FieldPlots);
            Assert.Equal(("Modes", cell.CavityMode), (plot.Setup, plot.Solution?.Mode));
        }
    }

    // ── 3. the closed form ──────────────────────────────────────────────────────────────────────

    /// <summary>The upper bound in the file is the empty cavity's TM110 from each cell's VARs a and d, and those VARs are
    /// what the laminate (the cavity's floor) is drawn from.</summary>
    [Fact]
    public void Gate3_TheClosedFormIsComputedFromTheVars()
    {
        var cf = Numbers().ClosedForm;
        foreach (var cell in Numbers().Cells)
        {
            var (doc, path, cws) = Cell(cell.Cell);
            double a = Var(doc, "a") * Mm, d = Var(doc, "d") * Mm;
            double f = 299_792_458.0 / 2 * Math.Sqrt(1 / (a * a) + 1 / (d * d)) / 1e9;
            output.WriteLine($"{cell.Cell}: closed form {f:F4} GHz");
            Assert.Equal(cf.Expected, f, 3);
            Assert.Equal((cf.AMm * Mm, cf.DMm * Mm), (a, d));

            var e = new C3dElaborator().Elaborate(doc, path, cws);
            var b = Assert.IsType<Em3dBox>(e.Solids.Single(s => s.Name == "substrate").Primitive);
            Assert.Equal(a, b.Max.X - b.Min.X, 9);
            Assert.Equal(d, b.Max.Y - b.Min.Y, 9);
        }
    }

    // ── 4. one source ───────────────────────────────────────────────────────────────────────────

    /// <summary>The README quotes every number in the file; em-setup.md's worked example quotes the headline and the bound.</summary>
    [Theory]
    [InlineData("examples/3D Eigenmode/README.md", true)]
    [InlineData("docs/user/src/reference/em-setup.md", false)]
    public void Gate4_ThePagesQuoteTheFile(string page, bool everything)
    {
        var n = Numbers();
        string text = File.ReadAllText(Path.Combine(RepoRoot(), page));
        Assert.Contains(n.ClosedForm.Readme, text);
        foreach (string s in n.Headline) Assert.Contains(s, text);
        if (!everything) return;
        Assert.Contains(n.Band.Readme, text);
        foreach (var cell in n.Cells)
        {
            foreach (string s in cell.Modes.Cost.Concat(cell.Driven.Cost).Concat(cell.Driven.Readme)) Assert.Contains(s, text);
            foreach (var v in cell.Modes.Values)
                foreach (string s in v.Readme) Assert.Contains(s, text);
        }
    }

    // ── 5. the solves ───────────────────────────────────────────────────────────────────────────

    /// <summary>The four shipped runs: the cavity mode's frequency within 0.2 %, the Cavity's notch within one sweep step of
    /// its own eigenmode and of the recorded notch, and with the post the cavity mode above the band and no notch in it.</summary>
    [CircuitRF.Ui.Tests.Em3d.PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate5_TheFourRuns_ReproduceTheReadme()
    {
        var n = Numbers();
        foreach (var cell in n.Cells)
        {
            var modes = Run(cell.Cell, "Modes");
            var f = modes.Data![Em3dEigenResult.FrequencyCube].RealValues;
            var q = modes.Data![Em3dEigenResult.QCube].RealValues;
            int row = Enumerable.Range(0, q.Length).First(k => q[k] > 10) + 1;
            double ghz = f[row - 1] / 1e9, expected = cell.Modes.Values.Single(v => v.Mode == cell.CavityMode).GHz;
            output.WriteLine($"{cell.Cell}: cavity mode {row} at {ghz:F5} GHz, Q {q[row - 1]:F1} (README {expected})");
            Assert.Equal(cell.CavityMode, row);
            Assert.True(Math.Abs(ghz - expected) <= 0.002 * expected, $"{cell.Cell}: {ghz:F5} GHz, README {expected}");

            var snp = TouchstoneIO.ReadFile(Run(cell.Cell, "Driven").SnpPath!);
            var s21 = snp.Matrices.Select(m => 20 * Math.Log10(m[1, 0].Magnitude)).ToArray();
            int min = Array.IndexOf(s21, s21.Min());
            double atGHz = snp.Frequencies[min] / 1e9;
            output.WriteLine($"{cell.Cell}: |S21| min {s21[min]:F3} dB at {atGHz:F3} GHz");
            if (cell.Driven.NotchGHz is { } notch)
            {
                Assert.True(Math.Abs(atGHz - ghz) <= n.Band.StepGHz * (1 + 1e-9), $"notch {atGHz} GHz, eigenmode {ghz:F5} GHz");
                Assert.True(Math.Abs(atGHz - notch) <= n.Band.StepGHz * (1 + 1e-9), $"notch {atGHz} GHz, README {notch}");
            }
            else
            {
                Assert.True(ghz > n.Band.StopGHz, $"{cell.Cell}: cavity mode {ghz:F5} GHz is inside the band");
                Assert.True(s21.Min() > -1, $"{cell.Cell}: a notch of {s21.Min():F2} dB");
            }
        }
    }

    private EmRunResult Run(string cell, string setup)
    {
        var (doc, path, cws) = Cell(cell);
        var (embedded, why) = C3dSetups.Select(doc, setup);
        Assert.True(embedded is not null, why);
        var wall = Stopwatch.StartNew();
        var result = EmRunService.RunThreeDView(C3dSetups.ForRun(embedded!, path), doc, path, cws, Path.Combine(_tmp, "results"),
                                                confirmMemory: _ => true);
        output.WriteLine($"{cell} {setup}: {result.Status} in {wall.Elapsed.TotalSeconds:F0} s — {result.Notes?.LastOrDefault()}");
        Assert.True(result.Status == EmRunStatus.Ok, $"{cell} {setup}: {result.Error}");
        return result;
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static double Var(C3dDocument doc, string name)
    {
        var v = doc.Variables.Single(x => x.Name == name);
        Assert.Equal("Mm", v.Unit);
        return double.Parse(v.Expression, CultureInfo.InvariantCulture);
    }

    private static string Root()
        => Path.Combine(ExampleWorkspaces.ResolveRoot(RepoRoot()) ?? throw new InvalidOperationException("no examples/"), "3D Eigenmode");

    private static (C3dDocument Doc, string Path, string Cws) Cell(string cell)
    {
        string path = Path.Combine(Root(), cell, "3d", cell + ".c3d");
        return (C3dPersistence.LoadFromFile(path), path, Path.Combine(Root(), ".cws"));
    }

    /// <summary><c>check --json</c> as a process: the verb a user runs.</summary>
    private static (List<string> Errors, List<string> Warnings) Check(string path)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in new[] { "check", path, "--json" }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        using var doc = JsonDocument.Parse(outTask.GetAwaiter().GetResult());
        var diags = doc.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        List<string> Of(string severity) => [.. diags.Where(d => d.GetProperty("severity").GetString() == severity)
                                                     .Select(d => d.GetProperty("message").GetString()!)];
        return (Of("error"), Of("warning"));
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Em3dEigenmodeExampleTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }

    // ── expected-numbers.json ───────────────────────────────────────────────────────────────────

    private sealed record Band(double StartGHz, double StopGHz, double StepGHz, string Readme);

    private sealed record ClosedForm(double AMm, double DMm, double Expected, string Readme);

    private sealed record ModeRow(int Mode, double GHz, double Q, double? QUnloaded, List<string> Readme);

    private sealed record ModesRun(string Preset, List<string> Cost, List<ModeRow> Values);

    private sealed record DrivenRun(string Preset, List<string> Cost, double? NotchGHz, double? NotchDb, double MinS21Db, List<string> Readme);

    private sealed record CellNumbers(string Cell, int CavityMode, ModesRun Modes, DrivenRun Driven);

    private sealed record ExpectedNumbers(Band Band, ClosedForm ClosedForm, List<string> Headline, List<CellNumbers> Cells);

    private static ExpectedNumbers Numbers()
        => JsonSerializer.Deserialize<ExpectedNumbers>(File.ReadAllText(Path.Combine(Root(), "expected-numbers.json")))!;
}
