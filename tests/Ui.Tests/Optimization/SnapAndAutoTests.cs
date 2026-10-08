using System.Numerics;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>
/// brief-tuneopt-8 R-to8-4: an L-section optimized continuously, then snapped to E24 capacitors and
/// an E12 inductor ladder, ends on the ladder pair whose cost is the best of the neighbour set — the
/// rungs either side of the continuous optimum, checked here by closed form, not by the run.
/// </summary>
public sealed class SnapAndPolishTests
{
    private const string Cnl = """
        Port:P1 in  0 Num=1 Z=50 Ohm
        L:L1    in  out L=5 nH
        C:C1    out 0   C=0.5 pF
        Port:P2 out 0 Num=2 Z=200 Ohm
        analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
        tune L1.L min=1 nH max=50 nH opt=1 discrete=preferred
        tune C1.C min=0.1 pF max=10 pF opt=1 discrete=preferred
        goal Match = mag(SP1.S(1,1)) analysis=SP1 le 0.0001
        optimize algorithm=lm maxiter=300
        """;

    /// <summary>The goal's cost in closed form: series L, shunt C across 200 Ω, seen from 50 Ω.</summary>
    private static double Cost(double l, double c)
    {
        double w = OptCircuits.W;
        var zc = new Complex(0, -1 / (w * c));
        var z = new Complex(0, w * l) + 200 * zc / (200 + zc);
        double v = Math.Max(0, ((z - 50) / (z + 50)).Magnitude - 1e-4);
        return v * v;
    }

    [Fact]
    public void TheLSection_EndsOnTheBestLadderPairOfItsNeighbours()
    {
        var ladders = PreferredLadders.Shipped with { CapacitorsFarad = PreferredValues.Series(PreferredValues.E24, -13, -8) };
        var r = OptimizationRun.Create(OptCircuits.Prepare(Cnl), new OptimizationOptions { Ladders = ladders, SnapAndPolish = true }).Run();

        // The rungs either side of the analytic optimum (13.78 nH, 1.378 pF).
        double[] ls = [12e-9, 15e-9], cs = [1.3e-12, 1.5e-12];
        var best = ls.SelectMany(l => cs.Select(c => (L: l, C: c))).MinBy(p => Cost(p.L, p.C));

        var snap = Assert.IsType<SnapResult>(r.Snap);
        Assert.Equal((2, 4, false), (snap.Snapped, snap.Neighbours, snap.Polished));
        Assert.Equal(best.L, OptCircuits.Si(r, "L1.L"), best.L * 1e-9);
        Assert.Equal(best.C, OptCircuits.Si(r, "C1.C"), best.C * 1e-9);
        Assert.Equal(Cost(best.L, best.C), r.BestCost!.Value, 1e-9);
        Assert.True(snap.CostBefore < snap.CostSnapped && snap.CostAfter == r.BestCost);
        Assert.Equal(OptimizationOutcome.GoalsUnmet, r.Outcome);   // no ladder pair matches to 1e-4
    }
}

/// <summary>
/// brief-tuneopt-8 R-to8-5: Auto on the L-section runs CMA-ES, then polishes with
/// Levenberg–Marquardt, names each stage in its progress, and finishes with the goal met. The goal is
/// tight enough (|S11| ≤ 1e-5) that the global stage's modest budget does not reach it alone.
/// </summary>
public sealed class AutoStagesTests
{
    [Fact]
    public void Auto_RunsTheGlobalStage_ThenThePolish_AndMeetsTheGoal()
    {
        string cnl = OptCircuits.LSection("auto").Replace("le 0.0001", "le 1e-5");
        var stages = new List<string?>();
        var r = OptimizationRun.Create(OptCircuits.Prepare(cnl), new OptimizationOptions
        {
            Progress = p => { if (stages.Count == 0 || stages[^1] != p.Stage) stages.Add(p.Stage); },
        }).Run();

        Assert.Equal([OptimizationStages.Global, OptimizationStages.PolishLm], r.Stages);
        Assert.Equal<string?>([OptimizationStages.Global, OptimizationStages.PolishLm], stages);
        Assert.True(r.Outcome == OptimizationOutcome.GoalsMet,
            $"{r.Outcome}: {r.FinishReason}; cost {r.BestCost:G3} after {r.Evaluations} evaluations, {r.Goals[0].WorstValue:G3}");
        Assert.Equal(OptCircuits.LAnalytic, OptCircuits.Si(r, "L1.L"), OptCircuits.LAnalytic * 1e-3);
    }
}
