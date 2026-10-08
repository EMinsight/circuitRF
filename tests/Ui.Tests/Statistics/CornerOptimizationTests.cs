using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Tests.Tuning;
using CircuitRF.Ui.Tuning;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>brief-yield-7's bench: an RC low-pass whose cutoff must stay at or above 1 GHz. R1 has a temperature
/// coefficient, so at the hot corner (125 °C) it is nearly twice its nominal and the cutoff falls: the C that just
/// meets the goal at the nominal misses it hot.</summary>
internal static class CornerOptCircuits
{
    public static string LowPass(string corners = "none") => $"""
        Port:P1 in  0 Num=1 Z=50 Ohm
        R:R1    in  out R=10 Ohm TC1=0.01
        C:C1    out 0   C=10 pF
        Port:P2 out 0 Num=2 Z=50 Ohm
        analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
        tune C1.C min=0.1 pF max=10 pF opt=1
        goal Cutoff = dB(SP1.S(2,1)) analysis=SP1 ge -3
        optimize algorithm=lm corners={corners}
        corner hot temp=125
        """;

    public static double Pf(string text) { Assert.True(TunableValue.TryParse(text, out _, out _, out double si)); return si * 1e12; }
}

/// <summary>R-ya7-1, R-ya7-2: across corners the goal is met at every corner, at a different point, and the report
/// names the binding corner.</summary>
public sealed class CornerOptimizationTests
{
    [Fact]
    public void MetAtTheNominalOnly_MovesWhenTheHotCornerMustBeMetToo_AndHotIsBinding()
    {
        var nominal = OptimizationRun.Create(PreparedCircuit.FromText(CornerOptCircuits.LowPass(), null, null)).Run();
        Assert.Equal(OptimizationOutcome.GoalsMet, nominal.Outcome);
        Assert.Null(Assert.Single(nominal.Goals).Corner);

        // The nominal's answer misses the goal hot.
        var check = CornerRun.Create(PreparedCircuit.FromText(CornerOptCircuits.LowPass().Replace("C=10 pF", "C=" + nominal.BestValues["C1.C"]), null, null),
                                     new CornerOptions { Goals = GoalUse.Both }).Run();
        Assert.Equal([true, false], check.Corners.Select(c => c.Pass));

        var run = OptimizationRun.Create(PreparedCircuit.FromText(CornerOptCircuits.LowPass("all"), null, null));
        Assert.Equal(["nominal", "hot"], run.CornerNames);
        var across = run.Run();
        Assert.Equal(OptimizationOutcome.GoalsMet, across.Outcome);
        Assert.True(CornerOptCircuits.Pf(across.BestValues["C1.C"]) < CornerOptCircuits.Pf(nominal.BestValues["C1.C"]) * 0.9);

        var goal = Assert.Single(across.Goals);
        Assert.Equal("hot", goal.Corner);
        Assert.Equal(["nominal", "hot"], goal.PerCorner!.Select(c => c.Corner));
        Assert.All(goal.PerCorner!, c => Assert.True(c.Met));
        Assert.True(goal.PerCorner![0].Margin > goal.PerCorner[1].Margin);
    }
}

/// <summary>R-ya7-1: a point costs one evaluation per corner, and the cache keys by (values, corner).</summary>
public sealed class CornerOptimizationCacheTests
{
    [Fact]
    public void EvaluationsArePointsTimesCorners_AndARevisitedPointIsAllCacheHits()
    {
        var run = OptimizationRun.Create(PreparedCircuit.FromText(CornerOptCircuits.LowPass("hot"), null, null));
        Assert.Equal(2, run.EvaluationsPerPoint);
        var r = run.Run();
        Assert.Equal(r.Log.Count(l => !l.Cached && !l.Infeasible) * 2L, r.Evaluations);

        // A sensitivity pass at the best point: two points at two corners, the best point's already known.
        var first = run.Sensitivity(r.BestPoint)!;
        Assert.Equal(4, first.Evaluations + first.CacheHits);
        Assert.Equal(0, first.Evaluations % 2);
        var again = run.Sensitivity(r.BestPoint)!;
        Assert.Equal(0, again.Evaluations);
        Assert.Equal(4, again.CacheHits);
    }
}

/// <summary>R-ya7-4: the Tuning panel at a corner shows what the corner run computes there.</summary>
public sealed class TuneAtCornerTests
{
    [Fact]
    public void TheSessionsResultAtACorner_EqualsCornerRunsForThatCorner()
    {
        const string bench = """
            Port:P1 in  0 Num=1 Z=50 Ohm
            R:R1    in  out R=50 Ohm TC1=0.01
            C:C1    out 0   C=2 pF
            Port:P2 out 0 Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            corner hot temp=125 C1.C=3 pF
            """;
        var f = new TuningPanelFixture();
        f.Top.EditModel.Tuning = new TuningSetup { Corners = [new CornerDefinition { Name = "hot", Temp = "125" }] };
        f.Panel.SetTuned("R1.R", true);
        Assert.Equal([TuningPanelViewModel.NominalChoice, "hot"], f.Panel.EvaluateAtChoices);

        var circuit = PreparedCircuit.FromText(bench, null, null);
        f.Panel.CreateSession = (_, analyses) => TuneSession.ForCircuit(circuit, null, a => a(), analyses);
        f.Panel.EvaluateAt = "hot";
        f.Panel.StartCommand.Execute(null);
        var session = f.Panel.Session!;
        Assert.Equal("hot", session.Corner);
        SpinWait.SpinUntil(() => session.DisplayedResult is not null, TimeSpan.FromSeconds(30));

        var hot = CornerRun.Create(circuit).Run().Corners.Single(c => c.Name == "hot");
        var expected = hot.Data!["SP1.S"].ComplexValues;
        var actual = session.DisplayedResult!.GroupedResults!["SP1.S"].ComplexValues;
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(0, (expected[i] - actual[i]).Magnitude, 12);
        f.Panel.StopCommand.Execute(null);
    }
}
