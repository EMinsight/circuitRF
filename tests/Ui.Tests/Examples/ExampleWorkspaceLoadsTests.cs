// ================================================================
//  ExampleWorkspaceLoadsTests.cs — the shipped Model Composer example (brief-modelcomposer-16 R-mc16-1): each document
//  opens as it was left, `check` finds nothing wrong with it, and its test benches run (the Verilog-A bench, MC-28, checks).
//
//  Run on a COPY of the example, as File ▸ New ▸ Examples makes one: the copy takes new timestamps, which is exactly the
//  case that once rewrote the cache and lost every fit.
// ================================================================

using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.ModelComposer;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ExampleWorkspaceLoadsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = CopyOfTheExample();

    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_ws)!, true); } catch (IOException) { } }

    private static string CopyOfTheExample()
    {
        string from = Path.Combine(ModelCliFixture.RepoRoot(), "examples", ModelComposerExampleAuthoring.Folder);
        string to = Path.Combine(Directory.CreateTempSubdirectory("crf-mc16-example-").FullName, ModelComposerExampleAuthoring.Folder);
        foreach (string d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        return to;
    }

    /// <summary>Every <c>.cmc</c> checks with no error, every step it ran is fresh, and the fitted ones still hold their
    /// fits — the arrays live in the cache, which the copy carries.</summary>
    [Fact]
    public void EveryDocument_ChecksClean_AndIsAsItWasLeft()
    {
        foreach (string cmc in Directory.GetFiles(_ws, "*.cmc", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_ws, cmc);
            var check = ModelCliFixture.Run(_ws, "check", rel);
            output.WriteLine($"check {rel} → {check.ExitCode}\n{check.StdOut}{check.StdErr}");
            Assert.Equal(0, check.ExitCode);

            var lines = ModelCommands.Explain(cmc).Select(l => l.Text).ToList();
            foreach (string l in lines) output.WriteLine(l);
            Assert.DoesNotContain(lines, l => l.EndsWith(": stale.", StringComparison.Ordinal));
        }
        string gan = Path.Combine(_ws, "synthetic-gan", SyntheticGanDocument.Name);
        var doc = CmcFile.Read(gan);
        Assert.NotNull(CurrentFit.Load(gan, doc));
        Assert.NotNull(ChargeFit.Load(gan, doc));
    }

    /// <summary>The spiral filter runs as Simulate runs it; the two GaN benches check clean. Running those is ~13 s (27
    /// harmonic-balance points of the trapped, self-heating model, and 49 loadpull loads), so it is opt-in below.</summary>
    [Fact]
    public void TheSpiralFilterRuns_AndTheGanBenchesCheckClean()
    {
        Run("SpiralFilter");
        foreach (string cell in new[] { "GanPowerSweep", "GanLoadpull" })
        {
            string rel = Path.Combine(cell, "schematic", cell + ".csch");
            var check = ModelCliFixture.Run(_ws, "check", rel);
            output.WriteLine($"check {rel} → {check.ExitCode}\n{check.StdOut}{check.StdErr}");
            Assert.Equal(0, check.ExitCode);
        }
    }

    /// <summary>
    /// brief-modelcomposer-28 R-mc28-4: the bench that places the model beside its Verilog-A export checks clean — and never
    /// needs a compiler to pass. Checking a <c>VerilogA</c> compiles its source, so where no compiler or device worker is
    /// reachable the bench's own netlist is checked with that one device placed as the <c>.cmodel</c> it was written from;
    /// the <c>.va</c> itself is held to the writer's output by <see cref="ModelComposerExampleShippedTests"/>.
    /// </summary>
    [Fact]
    public void TheVerilogABench_ChecksClean()
    {
        string rel = Path.Combine(ModelComposerExampleAuthoring.VerilogABench, "schematic", ModelComposerExampleAuthoring.VerilogABench + ".csch");
        if (CompilerAvailability.SkipReason is null)
        {
            var check = ModelCliFixture.Run(_ws, "check", rel);
            output.WriteLine($"check {rel} → {check.ExitCode}\n{check.StdOut}{check.StdErr}");
            Assert.Equal(0, check.ExitCode);
            return;
        }
        output.WriteLine($"{CompilerAvailability.SkipReason} Checking the bench with X2 placed as the .cmodel.");
        string cnl = SchematicCircuit.CnlTextOf(Path.Combine(_ws, rel));
        var lines = cnl.Split('\n');
        int k = Array.FindIndex(lines, l => l.StartsWith("VerilogA:X2 ", StringComparison.Ordinal));
        Assert.True(k >= 0, cnl);
        var nodes = lines[k].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..4];
        lines[k] = $"ComposedFET:X2 {string.Join(' ', nodes)} File=\"{ModelComposerExampleAuthoring.GanModel}\" N=8";
        File.WriteAllText(Path.Combine(_ws, "verilog-a-bench.cnl"), string.Join('\n', lines));
        var swapped = ModelCliFixture.Run(_ws, "check", "verilog-a-bench.cnl");
        output.WriteLine($"{swapped.StdOut}{swapped.StdErr}");
        Assert.Equal(0, swapped.ExitCode);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheGanBenches_Run()
    {
        Run("GanPowerSweep");
        Run("GanLoadpull");
    }

    /// <summary>
    /// brief-modelcomposer-25 R-mc25-4: <see cref="TheGanBenches_Run"/> fails on a rung that does not converge. Until MC-25 it
    /// could not — a sweep reported its convergence as null, which reads as converged, and the power sweep failed seven rungs
    /// unseen. The failure is injected by the bench's own netlist with one Newton iteration and no drive ramp.
    /// </summary>
    [Fact]
    public void TheGanBenchesGate_FailsOnARungThatDidNotConverge()
    {
        string cnl = ModelComposerExampleAuthoring.PowerSweepNetlist
            .Replace("MaxHarm=5", "MaxHarm=5 MaxIter=1 DriveStepping=Never", StringComparison.Ordinal)
            .Replace("Stop=22", "Stop=-3", StringComparison.Ordinal);
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, _ws, _ws), new CircuitEvaluationRequest { Measurements = false });

        var refused = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRan("GanPowerSweep", r));
        output.WriteLine(refused.Message);
        var c = Assert.Single(r.Convergence);
        Assert.False(c.Converged);
        Assert.Contains("Pin = -4", c.Detail);
        Assert.Contains(r.Warnings, w => w.Contains("did not converge at 2 of 2 sweep point(s)", StringComparison.Ordinal));
    }

    private void Run(string cell)
    {
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(Path.Combine(_ws, cell, "schematic", cell + ".csch")),
                                           new CircuitEvaluationRequest());
        foreach (string w in r.Warnings) output.WriteLine($"{cell}: {w}");
        AssertRan(cell, r);
    }

    /// <summary>The run succeeded and no analysis says it failed to converge — a sweep says so for every point (R-mc25-4).</summary>
    private static void AssertRan(string cell, RunResult r)
    {
        Assert.True(r.Status == RunStatus.Success, $"{cell}: {r.Status} {r.StatusMessage}");
        Assert.All(r.Convergence, c => Assert.True(c.Converged != false, $"{cell}: {c}"));
    }
}
