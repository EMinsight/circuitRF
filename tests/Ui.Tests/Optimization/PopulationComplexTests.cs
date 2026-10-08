using System.Numerics;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>
/// brief-tuneopt-7 R-to7-8: a population method over the parts of a complex VAR. real(Z) and mag(Z)
/// are optimized over the whole square [0, 100]², and imag(Z) — tune-only — still limits the value to
/// [10, 90], so the feasible region is a band with infeasible points on both sides of it: half the
/// square has real above magnitude, and imag falls outside its range near either edge of the rest.
/// The goals read only the variable, so nothing is simulated (D10).
/// </summary>
public sealed class PopulationComplexTests
{
    [Fact]
    public void DifferentialEvolution_EndsFeasible_AndReportsTheInfeasiblePoints()
    {
        const string cnl = """
            Z = 30+40j Ohm
            Port:P1 in 0 Num=1 Z=50 Ohm
            R:R1 in 0 R=50 Ohm
            tune real(Z) min=0 Ohm max=100 Ohm opt=1
            tune mag(Z) min=0 Ohm max=100 Ohm opt=1
            tune imag(Z) min=10 Ohm max=90 Ohm tune=1
            goal Re = real(Z) eq 60
            goal Im = imag(Z) eq 20
            optimize algorithm=de maxevals=3000 seed=1
            """;
        var run = OptimizationRun.Create(OptCircuits.Prepare(cnl));
        Assert.Null(run.Refusal);
        var r = run.Run();

        Assert.True(r.BestCost < 1e-8, $"{r.Outcome}: {r.FinishReason}; Z = {r.BestValues.GetValueOrDefault("Z")}, cost {r.BestCost}");
        Assert.True(ComplexValue.TryParse(r.BestValues["Z"], out var z, out _, out _), r.BestValues["Z"]);
        Assert.True((z - new Complex(60, 20)).Magnitude < 1e-3, z.ToString());

        var best = r.Log.Single(e => e.Point == r.BestPoint);
        Assert.False(best.Infeasible);
        Assert.False(best.Failed);
        Assert.True(r.Infeasible > 0, "no point was infeasible, so the test proves nothing");
        Assert.Equal(r.Log.Count(e => e.Infeasible), r.Infeasible);
        Assert.Equal(0, r.Failures);
    }
}
