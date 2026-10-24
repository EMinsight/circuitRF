using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Ui.Tests.Examples;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-25 on the example's GanPowerSweep bench: harmonic balance with the drain-lag trap node and the thermal
/// node both on the interface. The line search judges a Jacobian-scaled norm when a device's state node is there
/// (<see cref="HbNewton.LineSearchWeights"/>); <see cref="HbNewton.UnscaledLineSearch"/> is the ampere norm it replaced.
/// </summary>
public sealed class TrapThermalHbConvergenceTests(ITestOutputHelper output)
{
    /// <summary>
    /// The example's model under the GLOBAL extrapolation law it carried when these were measured. The ampere-norm baseline
    /// wanders outside the domain, so its stalls depend on the guide it meets there: under the local law (brief-modelcomposer-29)
    /// its 18 dBm stall moves to the trap row and its <c>Charge=false</c> point no longer converges at all, while the scaled
    /// norm converges at every point under both laws, in as many iterations or fewer (measured, 2026-10-24).
    /// </summary>
    private static readonly string ModelPath = GlobalLaw();

    private static string GlobalLaw()
    {
        string shipped = Path.Combine(ModelCliFixture.RepoRoot(), "examples", ModelComposerExampleAuthoring.Folder, ModelComposerExampleAuthoring.GanModel);
        ComposedModel m;
        using (var r = File.OpenRead(shipped)) m = CmodelFile.Read(r);
        string dir = Directory.CreateTempSubdirectory("crf-mc25-global-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(dir, true); } catch (IOException) { } };
        string path = Path.Combine(dir, Path.GetFileName(shipped));
        using (var f = File.Create(path))
            CmodelFile.Write(m with { Extrapolation = m.Extrapolation! with { Mode = ExtrapolationMode.Global } }, f);
        return path.Replace('\\', '/');
    }

    /// <summary>R-mc25-1/2: one cold point at 18 dBm. The ampere norm stalls with the thermal row carrying most of ‖F‖; the
    /// scaled one converges inside the brief's 30 iterations.</summary>
    [Fact]
    public void ColdAt18dBm_ConvergesInUnder30Iterations_WhereTheAmpereNormStalledOnTheThermalRow()
    {
        var before = Solve(Bench(18), unscaled: true);
        var last = before.Trace!.Steps[^1].IterTrace[^1];
        output.WriteLine($"ampere norm: ‖F‖ {last.ResidualNorm:E3}, {before.Trace.NameOf(last.WorstNode)} {last.WorstShare:P1}");
        Assert.False(before.Converged);
        Assert.Equal("X1.t", before.Trace.NameOf(last.WorstNode));

        var after = Solve(Bench(18));
        output.WriteLine($"scaled norm: {after.Trace!.TotalIterations} iterations");
        Assert.True(after.Converged);
        Assert.Equal(1, after.Trace.TotalSteps);
        Assert.True(after.Trace.TotalIterations < 30, $"{after.Trace.TotalIterations} iterations");
    }

    /// <summary>
    /// R-mc25-2's gate: a point that converged under the ampere norm lands on the same spectrum. Both paths are driven to the
    /// round-off floor first — Tol 1e-9 is below it, and 100 iterations because the ampere norm takes 79 to converge at all
    /// with <c>Charge=false</c> — since at the default 1e-6 each stops wherever it first enters the tolerance ball, up to
    /// 3e-8 relative apart (measured, <c>DrainLag=false</c> at 18 dBm).
    /// </summary>
    [Theory]
    [InlineData(-4, "")]
    [InlineData(14, "")]
    [InlineData(18, "DrainLag=false")]
    [InlineData(18, "Charge=false")]
    public void APointThatConvergedBefore_LandsOnTheSameSpectrum(int pin, string switches)
    {
        Assert.True(Solve(Bench(pin, switches), unscaled: true).Converged, "the ampere norm did not converge here");

        string tight = Bench(pin, switches, "Tol=1e-9 MaxIter=100");
        var a = Solve(tight, unscaled: true).InterfaceV!;
        var b = Solve(tight).InterfaceV!;
        double worst = 0, scale = 0;
        for (int n = 0; n < a.GetLength(0); n++)
            for (int k = 0; k < a.GetLength(1); k++)
            {
                worst = Math.Max(worst, (a[n, k] - b[n, k]).Magnitude);
                scale = Math.Max(scale, a[n, k].Magnitude);
            }
        output.WriteLine($"Pin {pin} {switches}: |ΔV| {worst:E2}, relative {worst / scale:E2}");
        Assert.True(worst <= 1e-9 * scale, $"relative {worst / scale:E2}");
    }

    /// <summary>The N-tone lattice loop takes the same weights: two tones at 16 dBm total stalled at 100 iterations under the
    /// ampere norm (measured) and converge.</summary>
    [Fact]
    public void TwoTones_ConvergeAt16dBmTotal()
    {
        string cnl = Bench(16)
            .Replace("P1Tone:P1  n1  0  Num=1  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq  Phase=0 deg",
                     "PnTone:P1  n1  0  Freq[1]=1.995e9  Pavl[1]=13  Freq[2]=2.005e9  Pavl[2]=13  Z=50", StringComparison.Ordinal)
            .Replace("""Tone="RFfreq" ToneUnit=GHz MaxHarm=5""", "NumFreqs=2 Tone[1]=1.995e9 Tone[2]=2.005e9 MaxMixOrder=5 MaxHarm=3",
                     StringComparison.Ordinal);
        var r = Solve(cnl);
        output.WriteLine($"{r.Trace!.TotalIterations} iterations");
        Assert.True(r.Converged);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>The example's power-sweep bench at one drive, cold, with no drive ramp — so a failure is the solve's own.</summary>
    private static string Bench(int pin, string switches = "", string hb = "")
    {
        var lines = ModelComposerExampleAuthoring.PowerSweepNetlist
            .Replace($"File=\"{ModelComposerExampleAuthoring.GanModel}\"", $"File=\"{ModelPath}\"", StringComparison.Ordinal)
            .Replace("N=8", $"N=8 {switches}", StringComparison.Ordinal)
            .Replace("Pin = 0", $"Pin = {pin.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal)
            .Replace("MaxHarm=5", $"MaxHarm=5 DriveStepping=Never {hb}", StringComparison.Ordinal)
            .Split('\n')
            .Where(l => !l.Contains("type=parametric_sweep", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(ModelPath, StringComparison.Ordinal));
        return string.Join('\n', lines);
    }

    private static HbRunResult Solve(string cnl, bool unscaled = false)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().First(), nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        HbNewton.UnscaledLineSearch = unscaled;
        try { return new HbEngine(nl, tb).Run(p); }
        finally { HbNewton.UnscaledLineSearch = false; }
    }
}
