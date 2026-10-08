using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Engine.Statistics;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>Cheap closed-form benches for the YA-4 gates: a 1 V resistive divider.</summary>
internal static class YieldCircuits
{
    /// <summary>R1 over R2 from 1 V, both 1 kΩ ± 2 % (1σ), and a yield spec Vout ∈ [0.49, 0.51] V.</summary>
    public static string Divider(string statistics, string tolerances = "sd=2%", string extra = "") => $"""
        Vdc:V1 in  0   Vdc=1
        R:R1   in  out R=1000 Ohm
        R:R2   out 0   R=1000 Ohm
        analysis DC1 type=dc
        tune R1.R dist=gauss {tolerances}
        tune R2.R dist=gauss {tolerances}
        goal Vout = DC1.V("out") analysis=DC1 in 0.49 0.51 use=yield
        statistics {statistics}
        {extra}
        """;

    public static StatisticalRun Create(string cnl, StatisticalMode mode = StatisticalMode.Yield)
    {
        var run = StatisticalRun.Create(PreparedCircuit.FromText(cnl, null, null), new StatisticalOptions { Mode = mode });
        Assert.Null(run.Refusal);
        return run;
    }

    /// <summary>Every cube of a DataSet by its address.</summary>
    public static Dictionary<string, DataCube> All(DataSet ds)
        => ds.Groups.SelectMany(g => ds.CubesIn(g).Select(kv => (Key: g + "." + kv.Key, kv.Value))).ToDictionary(x => x.Key, x => x.Value);
}

/// <summary>R-ya4-3: a 2,000-trial run's yield interval holds the exact yield.</summary>
public sealed class YieldDividerTests
{
    [Fact]
    public void TheIntervalOfATwoThousandTrialRun_HoldsTheExactYield()
    {
        var r = YieldCircuits.Create(YieldCircuits.Divider("trials=2000")).Run();
        Assert.Equal(StatisticalOutcome.Finished, r.Outcome);
        Assert.Equal(2000, r.Trials);

        // The exact yield. Vout = R2/(R1 + R2) ≥ L is (1 − L)·R2 − L·R1 ≥ 0 — LINEAR in R1 and R2, so with both
        // Gaussian it is exactly normal, mean (1 − 2L)·1 kΩ, σ = 20 Ω·√((1 − L)² + L²). The linearization error is
        // therefore zero; what the closed form leaves out is a negative resistance, P < Φ(−50) ≈ 1e-545.
        static double AtLeast(double l) => SpecialFunctions.NormalCdf((1 - 2 * l) * 1000 / (20 * Math.Sqrt((1 - l) * (1 - l) + l * l)));
        double exact = AtLeast(0.49) - AtLeast(0.51);       // {V > 0.51} ⊂ {V ≥ 0.49}
        Assert.Equal(0.8426, exact, 4);

        var y = r.Yield;
        Assert.Equal(2000, y.Counted);
        Assert.InRange(exact, y.Lower, y.Upper);
        Assert.Equal(y.Yield, r.Data!["yield.yield"].RealValues[0]);
        Assert.Equal(2000, r.Data["trials.pass"].Axes[0].Length);
    }
}

/// <summary>R-ya4-2, R-ya4-7: parallelism changes nothing in the DataSet, and a re-run trial is that trial.</summary>
public sealed class YieldDeterminismTests
{
    // A kit-style process draw rides along (Rnom), so the trial's expression draws are held to the same rule.
    private static string Bench(int parallel) => YieldCircuits.Divider($"trials=24 parallel={parallel}", extra: """
        Rnom = agauss(1000, 30, 1)
        R:R3 out 0 R=Rnom
        """).Replace("in 0.49 0.51", "in 0.32 0.35");

    [Fact]
    public void OneAtATimeAndEightAtOnce_GiveIdenticalDataSets_AndATrialReRunsAsItRan()
    {
        var serial = YieldCircuits.Create(Bench(1)).Run();
        var run8 = YieldCircuits.Create(Bench(8));
        var parallel = run8.Run();

        var a = YieldCircuits.All(serial.Data!);
        var b = YieldCircuits.All(parallel.Data!);
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        Assert.Contains("trials.z:process:Rnom", a.Keys);
        Assert.Contains("DC1.V", a.Keys);
        foreach (var (name, cube) in a)
        {
            Assert.Equal(cube.Axes.Select(x => x.Length), b[name].Axes.Select(x => x.Length));
            if (cube.DataKind == DataKind.Complex) Assert.Equal(cube.ComplexValues, b[name].ComplexValues);
            else Assert.Equal(cube.RealValues, b[name].RealValues);
        }

        // Trial 17 alone: the same values, the same draws, the same margin, the same node voltages.
        var t = run8.EvaluateTrial(17);
        var inRun = parallel.Records[16];
        Assert.Equal(inRun.Values, t.Values);
        Assert.Equal(inRun.Kit["Rnom"].Z, t.Kit["Rnom"].Z);
        Assert.Equal(inRun.Goals.Single().Margin, t.Goals.Single().Margin);
        Assert.Equal(parallel.Data!["DC1.V"].At(Evaluator.TrialAxis, 16).RealValues, t.Data!["DC1.V"].RealValues);
    }
}

/// <summary>R-ya4-2 / D7: a trial that does not evaluate counts as a fail under <c>fail</c> and is excluded under
/// <c>warn</c>, and is reported either way.</summary>
public sealed class NonconvergedPolicyTests
{
    // σ = 60 % puts a resistance at or below zero in Φ(−1/0.6) ≈ 4.8 % of draws (≈ 38 of the 800): those trials are
    // refused, never clamped.
    private static StatisticalResult Run(string policy)
        => YieldCircuits.Create(YieldCircuits.Divider($"trials=400 nonconverged={policy}", "sd=60%")).Run();

    [Fact]
    public void ADidNotEvaluateTrial_IsAFailUnderFail_AndExcludedUnderWarn()
    {
        var fail = Run("fail");
        var warn = Run("warn");
        int dne = fail.DidNotEvaluate;
        Assert.True(dne > 0);
        Assert.Equal(dne, warn.DidNotEvaluate);
        Assert.Equal(fail.Yield.Passes, warn.Yield.Passes);
        Assert.Equal(400, fail.Yield.Counted);
        Assert.Equal(400 - dne, warn.Yield.Counted);

        Assert.Contains(fail.Notes, n => n.Id == "yield.run.did-not-evaluate" && n.Render().Contains("counted as fails"));
        Assert.Contains(warn.Notes, n => n.Id == "yield.run.did-not-evaluate" && n.Render().Contains("excluded"));

        // The trial carries its reason, in the status cube and its table.
        var bad = fail.Records.First(r => !r.Evaluated);
        Assert.Equal("yield.trial.nonphysical", bad.Reason!.Id);
        Assert.Equal(1.0, fail.Data!["trials.status"].RealValues[bad.Trial - 1]);
        Assert.Equal(bad.Reason.Render(), fail.Data["trials.reasons"].Axes[0].Labels![0]);
        Assert.Equal(0.0, fail.Data["trials.pass"].RealValues[bad.Trial - 1]);
        Assert.True(double.IsNaN(warn.Data!["trials.pass"].RealValues[bad.Trial - 1]));
    }
}

/// <summary>D8: auto-stop ends at the first trial count whose interval clears the target.</summary>
public sealed class AutoStopTests
{
    [Fact]
    public void ADesignAtFullYield_StopsWhereTheLowerBoundFirstClearsTheTarget()
    {
        // Every trial passes (σ = 0.1 % against a ±2 % window). With n of n passing the lower bound is (α/2)^(1/n),
        // first ≥ 0.95 at n = ⌈ln 0.025 / ln 0.95⌉ — above auto-stop's floor of 50 counted trials.
        int expected = (int)Math.Ceiling(Math.Log(0.025) / Math.Log(0.95));
        Assert.Equal(72, expected);

        var r = YieldCircuits.Create(YieldCircuits.Divider("trials=1000 target=95% autostop=1 parallel=8", "sd=0.1%")).Run();
        Assert.Equal(AutoStopVerdict.Above, r.AutoStop);
        Assert.Equal(expected, r.Trials);
        Assert.Equal(expected, r.Yield.Passes);
        Assert.Equal(StatisticalOutcome.Finished, r.Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Yield.Lower >= 0.95);
        Assert.True(ClopperPearson.Interval(expected - 1, expected - 1, 0.95).Lower < 0.95);
    }
}

/// <summary>R-ya4-9: contributions find the variable a quantity actually depends on.</summary>
public sealed class ContributionTests
{
    [Fact]
    public void AVoltageSetByR1Alone_IsAttributedToR1()
    {
        // R2 hangs across the ideal source: it varies, and nothing downstream sees it.
        const string cnl = """
            Vdc:V1 in  0   Vdc=1
            R:R1   in  out R=1000 Ohm
            R:R3   out 0   R=1000 Ohm
            R:R2   in  0   R=1000 Ohm
            analysis DC1 type=dc
            measure Vout = DC1.V("out")
            tune R1.R dist=gauss sd=2%
            tune R2.R dist=gauss sd=2%
            statistics trials=200
            """;
        var run = YieldCircuits.Create(cnl, StatisticalMode.MonteCarlo);
        Assert.Equal(StatisticalOutcome.Finished, run.Run().Outcome);

        var c = run.Contributions("Vout");
        Assert.Null(c.Refusal);
        Assert.False(c.Underdetermined);
        Assert.True(c.Contributors.Single(x => x.Name == "R1.R").Share > 0.99);
        Assert.True(c.Contributors.Single(x => x.Name == "R2.R").Share < 0.01);
    }
}
