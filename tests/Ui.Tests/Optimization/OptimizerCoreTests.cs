using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>Cheap closed-form circuits for the optimizer gates (brief-tuneopt-6 §4).</summary>
internal static class OptCircuits
{
    public const double F = 1e9, W = 2 * Math.PI * F;

    /// <summary>
    /// A low-pass L-section matching a 200 Ω load (port 2) to 50 Ω (port 1) at 1 GHz: series L1, shunt
    /// C1 across the load. Analytic answer: Q = √3, L = Q·50/ω, C = Q/(200·ω).
    /// </summary>
    public static string LSection(string algorithm, string extra = "") => $"""
        Port:P1 in  0 Num=1 Z=50 Ohm
        L:L1    in  out L=5 nH
        C:C1    out 0   C=0.5 pF
        Port:P2 out 0 Num=2 Z=200 Ohm
        analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
        tune L1.L min=1 nH max=50 nH opt=1
        tune C1.C min=0.1 pF max=10 pF opt=1
        goal Match = mag(SP1.S(1,1)) analysis=SP1 le 0.0001
        optimize algorithm={algorithm} {extra}
        """;

    public static readonly double LAnalytic = Math.Sqrt(3) * 50 / W;
    public static readonly double CAnalytic = Math.Sqrt(3) / (200 * W);

    public static PreparedCircuit Prepare(string cnl) => PreparedCircuit.FromText(cnl, null, null);

    /// <summary>The value key's number in base SI at the run's best point.</summary>
    public static double Si(OptimizationResult r, string key)
    {
        Assert.True(TunableValue.TryParse(r.BestValues[key], out _, out _, out double si), r.BestValues[key]);
        return si;
    }
}

/// <summary>A two-variable L-section match converges from a poor start to the analytic L and C.</summary>
public sealed class LSectionMatchTests
{
    [Theory]
    [InlineData("lm")]
    [InlineData("simplex")]
    public void Converges_ToTheAnalyticLAndC(string algorithm)
    {
        var run = OptimizationRun.Create(OptCircuits.Prepare(OptCircuits.LSection(algorithm, "maxiter=300")));
        Assert.Null(run.Refusal);
        var r = run.Run();

        Assert.Equal(OptimizationOutcome.GoalsMet, r.Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(0, r.BestCost);
        Assert.Equal(OptCircuits.LAnalytic, OptCircuits.Si(r, "L1.L"), OptCircuits.LAnalytic * 0.01);
        Assert.Equal(OptCircuits.CAnalytic, OptCircuits.Si(r, "C1.C"), OptCircuits.CAnalytic * 0.01);

        // The history is an ordinary DataSet: one cost per point told, one best per iteration.
        var history = r.History!;
        Assert.Equal(r.Log.Count, history["opt.cost"].Axes[0].Length);
        Assert.Equal(r.Iterations, history["opt.best_cost"].Axes[0].Length);
        Assert.Equal("H", history["opt.L1.L"].Unit);
        Assert.Equal(r.Evaluations + r.CacheHits, r.Log.Count);
    }

    [Fact]
    public void Gradient_RefusesMinimax_NamingTheAlternatives()
    {
        var run = OptimizationRun.Create(OptCircuits.Prepare(OptCircuits.LSection("lm", "cost=minimax")));
        Assert.Equal("opt.algorithm.lsq-only", run.Refusal?.Id);
        Assert.Contains("Minimax or Auto", run.Refusal!.Render());
        Assert.Equal(1, run.Run().ExitCode);
    }
}

/// <summary>An unreachable goal ends with cost &gt; 0, the goal reported unmet at its worst point, exit 3.</summary>
public sealed class InfeasibleGoalTests
{
    [Fact]
    public void GainOfTwo_FromAPassivePad_IsReportedUnmet()
    {
        const string cnl = """
            Port:P1 in  0 Num=1 Z=50 Ohm
            R:R1    in  out R=10 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=11 Unit=GHz
            tune R1.R min=1 Ohm max=100 Ohm opt=1
            goal Gain = mag(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge 2
            optimize algorithm=simplex maxiter=30
            """;
        var r = OptimizationRun.Create(OptCircuits.Prepare(cnl)).Run();

        Assert.Equal(OptimizationOutcome.GoalsUnmet, r.Outcome);
        Assert.Equal(3, r.ExitCode);
        Assert.True(r.BestCost > 0);
        var gain = Assert.Single(r.Goals);
        Assert.False(gain.Met);
        Assert.Equal("freq", gain.Axis);
        Assert.InRange(gain.WorstAt!.Value, 1e9, 2e9);
        Assert.InRange(gain.WorstValue, 0.5, 1);        // |S21| of a series resistor: below one
        Assert.Equal(2 - gain.WorstValue, gain.WorstViolation, 12);
    }
}

/// <summary>R-to6-2/3 on a hand-made cube: freq 1..4 GHz carrying −10, −12, −20, −5.</summary>
public sealed class GoalResidualTests
{
    private static readonly DataCube Cube = new([new Axis("freq", [1e9, 2e9, 3e9, 4e9], "Hz")], [-10.0, -12, -20, -5]);

    private static OptimizationGoal Goal(GoalType type, string limit, string? upper = null, string? atHi = null,
                                         string lo = "1 GHz", string hi = "4 GHz", double weight = 1) => new()
    {
        Name = "G", Expression = "x", Type = type, Limit = limit, UpperLimit = upper, LimitAtHi = atHi, Weight = weight,
        Range = new GoalRange { Axis = "freq", Lo = lo, Hi = hi },
    };

    [Theory]
    [InlineData(GoalType.Le,  "-15", null, 10.0, 4e9, 15.0)]   // −5 is 10 above
    [InlineData(GoalType.Ge,  "-8",  null, 12.0, 3e9, 8.0)]    // −20 is 12 below
    [InlineData(GoalType.Eq,  "-10", null, 10.0, 3e9, 10.0)]
    [InlineData(GoalType.In,  "-15", "-8", 5.0,  3e9, 7.0)]    // band width is the scale
    [InlineData(GoalType.Out, "-15", "-8", 3.0,  2e9, 7.0)]    // −12 is 3 from the nearer edge
    public void EachType_ViolatesAsDefined_AndNormalizesByItsScale(GoalType type, string limit, string? upper,
                                                                   double worst, double at, double scale)
    {
        var s = GoalResiduals.Score(Goal(type, limit, upper), new Value(Cube), null);
        Assert.Null(s.Error);
        Assert.Equal(worst, s.WorstViolation, 12);
        Assert.Equal(at, s.WorstAt);
        Assert.Equal(scale, GoalResiduals.Scale(Goal(type, limit, upper)));
        Assert.Equal(worst / scale / 2, s.Residuals.Max(), 12);   // ÷ √4 points
    }

    [Fact]
    public void ASlopedLimit_InterpolatesAcrossTheRange_AndTheWeightMultiplies()
    {
        // Over 2..4 GHz the limit runs −20 → −5: −20, −12.5, −5 against −12, −20, −5.
        var g = Goal(GoalType.Le, "-20", atHi: "-5", lo: "2 GHz", hi: "4 GHz", weight: 2);
        var s = GoalResiduals.Score(g, new Value(Cube), null);
        Assert.Equal(3, s.Residuals.Length);
        Assert.Equal(8, s.WorstViolation, 12);
        Assert.Equal(2e9, s.WorstAt);
        Assert.Equal(2 * 8 / 20.0 / Math.Sqrt(3), s.Residuals[0], 12);
        Assert.Equal(0, s.Residuals[1]);
    }

    [Fact]
    public void AComplexValue_IsAnError_NamingTheFunctionsThatMakeItReal()
    {
        var complex = new DataCube([new Axis("freq", [1e9], "Hz")], [new Complex(0.1, 0.2)]);
        var s = GoalResiduals.Score(Goal(GoalType.Le, "0.1", lo: "1 GHz", hi: "1 GHz"), new Value(complex), null);
        Assert.False(s.Met);
        Assert.Contains("dB(…), mag(…), phase(…) or real(…)", s.Error!.Render());
    }
}

/// <summary>R-to6-12: how optimized parts of a complex value decode into the whole value.</summary>
public sealed class ComplexDecodeTests
{
    private const string Design = """
        ZL = 30-40j Ohm
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in 0 R=100 Ohm
        Port:P2 in 0 Num=2 Z=ZL
        analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
        goal Match = mag(SP1.S(1,1)) analysis=SP1 le 0.0001
        """;

    private static (OptimizationVariables? Vars, CircuitRF.Diagnostics.Diagnostic? Refusal) Build(params string[] tunes)
    {
        var (lib, tb) = new CnlReader().Read(Design + "\n" + string.Join("\n", tunes), "tb", null);
        var vars = OptimizationVariables.Build(tb.Tuning!, TunableCatalog.FromNetlist(tb, lib), out var refusal);
        return (vars, refusal);
    }

    private static Complex Decode(OptimizationVariables v, params double[] u)
    {
        var d = v.Decode(u);
        Assert.False(d.Infeasible);
        return d.Quantities["ZL"];
    }

    [Fact]
    public void RealAndImaginary_AndMagnitudeAndPhase_DecodeDirectly()
    {
        var rect = Build("tune real(ZL) min=10 max=90 opt=1", "tune imag(ZL) min=-50 max=50 opt=1").Vars!;
        Assert.Equal(new Complex(50, 0), Decode(rect, 0.5, 0.5));
        Assert.Equal("50+0j Ohm", rect.Decode([0.5, 0.5]).Values["ZL"]);

        var polar = Build("tune mag(ZL) min=20 max=90 opt=1", "tune phase(ZL) min=-90 max=90 opt=1").Vars!;
        var z = Decode(polar, 0.5, 0.75);
        Assert.Equal(55, z.Magnitude, 9);
        Assert.Equal(45, z.Phase * 180 / Math.PI, 9);
    }

    [Fact]
    public void RealAndMagnitude_TakeTheFreeSignFromTheStart()
    {
        // Start 30−40j: imaginary negative, so real 30 with magnitude 50 is 30−40j, not 30+40j.
        var v = Build("tune real(ZL) min=10 max=50 opt=1", "tune mag(ZL) min=40 max=60 opt=1").Vars!;
        var z = Decode(v, 0.5, 0.5);
        Assert.Equal(30, z.Real, 9);
        Assert.Equal(-40, z.Imaginary, 9);
    }

    [Fact]
    public void AnInfeasiblePair_IsNotSimulated_AndRanksLast()
    {
        var run = OptimizationRun.Create(OptCircuits.Prepare(Design + """

            tune real(ZL) min=10 max=50 opt=1
            tune mag(ZL) min=40 max=60 opt=1
            """));
        Assert.Null(run.Refusal);
        double[] feasible = [0.5, 0.5], infeasible = [1.0, 0.0];     // real 50 > magnitude 40
        Assert.True(run.Variables!.Decode(infeasible).Infeasible);

        var results = run.EvaluateBatch([feasible, infeasible]);
        Assert.Equal(1, run.Evaluations);
        Assert.Equal(1, run.Infeasible);
        Assert.True(results[1].Failed);
        Assert.True(results[1].Cost > results[0].Cost);
    }

    [Fact]
    public void ThreeOptimizedParts_Refuse_WithTheSentence()
    {
        var (vars, refusal) = Build("tune real(ZL) min=10 max=50 opt=1", "tune imag(ZL) min=-50 max=0 opt=1",
                                    "tune mag(ZL) min=20 max=60 opt=1");
        Assert.Null(vars);
        Assert.Equal("real(ZL), imag(ZL) and mag(ZL) are three parts of ZL, which has two degrees of freedom; " +
                     "optimize at most two — the others' ranges still limit it.", refusal!.Render());
    }
}

/// <summary>R-to6-12 end to end: the load of a fixed L-section, a complex VAR optimized by magnitude and
/// phase, is driven to the impedance the network matches — every simulated value inside every range.</summary>
public sealed class ComplexLSectionTests
{
    [Fact]
    public void MagnitudeAndPhase_ReachTheAnalyticLoad_InsideEveryRange()
    {
        const string cnl = """
            ZL = 80+0j Ohm
            Port:P1 in  0 Num=1 Z=50 Ohm
            L:L1    in  out L=10 nH
            C:C1    out 0   C=1 pF
            Port:P2 out 0 Num=2 Z=ZL
            analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
            tune mag(ZL) min=20 Ohm max=300 Ohm opt=1
            tune phase(ZL) min=-80 deg max=80 deg opt=1
            tune real(ZL) min=60 Ohm max=400 Ohm tune=1
            goal Match = mag(SP1.S(1,1)) analysis=SP1 le 0.0001
            optimize algorithm=lm
            """;
        var r = OptimizationRun.Create(OptCircuits.Prepare(cnl)).Run();
        Assert.True(r.Outcome == OptimizationOutcome.GoalsMet,
            $"{r.Outcome}: {r.FinishReason}; ZL = {r.BestValues.GetValueOrDefault("ZL")}, cost {r.BestCost}");

        // The load that presents 50 Ω through series 10 nH and shunt 1 pF.
        var zp = new Complex(50, -OptCircuits.W * 10e-9);
        var analytic = 1 / (1 / zp - new Complex(0, OptCircuits.W * 1e-12));
        Assert.True(ComplexValue.TryParse(r.BestValues["ZL"], out var best, out _, out _), r.BestValues["ZL"]);
        Assert.True((best - analytic).Magnitude < 0.01 * analytic.Magnitude, $"{best} vs {analytic}");

        foreach (var e in r.Log.Where(e => !e.Infeasible))
        {
            var z = e.Decoded.Quantities["ZL"];
            Assert.InRange(z.Magnitude, 20 - 1e-9, 300 + 1e-9);
            Assert.InRange(z.Phase * 180 / Math.PI, -80 - 1e-9, 80 + 1e-9);
            Assert.InRange(z.Real, 60 - 1e-9, 400 + 1e-9);
        }
    }
}

/// <summary>R-to6-8: a run paused after an iteration and resumed is the uninterrupted run, evaluation for
/// evaluation.</summary>
public sealed class OptimizationPauseResumeTests
{
    [Fact]
    public void PausedAndResumed_IsTheUninterruptedRun()
    {
        string cnl = OptCircuits.LSection("simplex", "maxiter=40");
        var whole = OptimizationRun.Create(OptCircuits.Prepare(cnl)).Run();

        OptimizationRun? run = null;
        run = OptimizationRun.Create(OptCircuits.Prepare(cnl), new OptimizationOptions
        {
            Progress = p => { if (p.Iteration == 3) run!.Pause(); },
        });
        var task = Task.Run(run.Run);
        Assert.True(run.Held.WaitOne(TimeSpan.FromSeconds(60)));
        long heldAt = run.Evaluations;
        Assert.True(run.IsPaused);
        Assert.Equal(heldAt, run.Evaluations);
        run.Resume();
        var resumed = task.Result;

        Assert.Equal(whole.Log.Count, resumed.Log.Count);
        Assert.All(whole.Log.Zip(resumed.Log), p =>
        {
            Assert.Equal(p.First.Point, p.Second.Point);
            Assert.Equal(p.First.Cost, p.Second.Cost);
        });
        Assert.Equal(whole.Evaluations, resumed.Evaluations);
    }
}
