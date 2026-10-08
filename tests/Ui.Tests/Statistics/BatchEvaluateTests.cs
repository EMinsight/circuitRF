using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Tests.Optimization;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>YA-2 R-ya2-6: the public batch door evaluates value maps through the optimizer's own
/// evaluator — a map evaluates as the same values typed into the design would simulate.</summary>
public sealed class BatchEvaluateTests
{
    [Fact]
    public void AValueMap_EvaluatesAsTheSameValuesTyped()
    {
        string cnl = OptCircuits.LSection("lm");
        var run = OptimizationRun.ForEvaluation(OptCircuits.Prepare(cnl), goals: GoalUse.Both);
        Assert.Null(run.Refusal);

        var map = new Dictionary<string, string> { ["L1.L"] = "6 nH", ["C1.C"] = "0.8 pF" };
        var p = Assert.Single(run.EvaluateValues([map]));
        Assert.Equal(PointStatus.Evaluated, p.Status);

        var typed = CircuitEvaluation.Evaluate(
            OptCircuits.Prepare(cnl.Replace("L=5 nH", "L=6 nH").Replace("C=0.5 pF", "C=0.8 pF")),
            new CircuitEvaluationRequest { Analyses = ["SP1"] });
        Assert.Equal(RunStatus.Success, typed.Status);

        static Dictionary<string, RfCore.Data.DataCube> All(RfCore.Data.DataSet ds)
            => ds.Groups.SelectMany(g => ds.CubesIn(g).Select(kv => (Key: g + "." + kv.Key, kv.Value)))
                        .ToDictionary(x => x.Key, x => x.Value);
        var mine = All(p.Data!);
        var theirs = All(typed.GroupedResults!);
        Assert.Contains("SP1.S", theirs.Keys);
        Assert.Equal(theirs.Keys.Order(), mine.Keys.Order());
        foreach (var (name, cube) in theirs)
        {
            Assert.Equal(cube.DataKind, mine[name].DataKind);
            if (cube.DataKind == RfCore.Data.DataKind.Complex) Assert.Equal(cube.ComplexValues, mine[name].ComplexValues);
            else Assert.Equal(cube.RealValues, mine[name].RealValues);
        }

        // The goal is scored at that point: |S11| against its limit, unmet, with its margin.
        var g = Assert.Single(p.Goals);
        Assert.False(g.Met);
        Assert.False(p.Pass);
        Assert.Equal(-g.WorstViolation, g.Margin);

        // The same map again is the cache's, not a simulation.
        long evaluations = run.Evaluations;
        var again = Assert.Single(run.EvaluateValues([map], keepData: false));
        Assert.True(again.Cached);
        Assert.Equal(evaluations, run.Evaluations);
        Assert.Equal(p.Cost, again.Cost);
    }
}
