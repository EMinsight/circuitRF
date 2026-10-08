using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Statistics;
using Xunit;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>
/// R-ya3-1 — the dialect's distribution functions in circuitRF's expression engine (docs/design/yield.md §7):
/// nominal outside a trial, a draw on the stream's z inside one.
/// </summary>
public sealed class StatisticalFunctionTests
{
    private const int Draws = 20_000;

    /// <summary>
    /// Outside a trial each function is its first argument with that argument's unit — under a site unit exactly
    /// as the bare nominal is — and its spread is not even read (a name that resolves nowhere costs nothing).
    /// </summary>
    [Theory]
    [InlineData("agauss(40,2,3)")]
    [InlineData("gauss(40,0.05,3)")]
    [InlineData("aunif(40,2)")]
    [InlineData("unif(40,0.05)")]
    [InlineData("limit(40,nowhere)")]
    public void NominalIsTheFirstArgument_WithItsUnit(string call)
    {
        var scope = new Scope("global");
        Assert.Equal(new Evaluator().Eval("40", scope, "mil").AsReal(), new Evaluator().Eval(call, scope, "mil").AsReal());
        Assert.Equal(new Evaluator().Eval("2pF", scope).AsReal(), new Evaluator().Eval(call.Replace("40", "2pF"), scope).AsReal());
    }

    /// <summary><c>agauss(1, 0.3, 3)</c> is σ = 0.1 about 1: over 20,000 fixed-seed trials the sample mean and σ
    /// sit within 4 standard errors (SE of the mean σ/√n = 7.1e-4; of σ, σ/√(2(n−1)) = 5.0e-4).</summary>
    [Fact]
    public void Agauss_DrawsSigmaOverK()
    {
        var x = Sample("agauss(1,0.3,3)");
        double mean = x.Average();
        double sd   = Math.Sqrt(x.Sum(v => (v - mean) * (v - mean)) / (x.Length - 1));
        Assert.InRange(mean, 1 - 4 * 0.1 / Math.Sqrt(Draws), 1 + 4 * 0.1 / Math.Sqrt(Draws));
        Assert.InRange(sd, 0.1 - 4 * 0.1 / Math.Sqrt(2.0 * (Draws - 1)), 0.1 + 4 * 0.1 / Math.Sqrt(2.0 * (Draws - 1)));
    }

    /// <summary><c>limit(5, 1)</c> draws 4 or 6 and nothing else, each with probability ½ — within 4 SE
    /// (√(¼/n) = 3.5e-3) over 20,000 trials.</summary>
    [Fact]
    public void TwoArgumentLimit_DrawsEitherEndEqually()
    {
        var x = Sample("limit(5,1)");
        Assert.All(x, v => Assert.True(v is 4.0 or 6.0, $"drew {v}"));
        double high = x.Count(v => v == 6.0) / (double)Draws;
        Assert.InRange(high, 0.5 - 4 * Math.Sqrt(0.25 / Draws), 0.5 + 4 * Math.Sqrt(0.25 / Draws));
    }

    /// <summary>Three arguments is the clamp, in the engine and from the SPICE reader alike — inside a trial too.</summary>
    [Fact]
    public void ThreeArgumentLimit_IsStillAClamp()
    {
        var ev = new Evaluator { Statistics = new ExpressionDraws(seed: 1, trial: 1) };
        Assert.Equal(3.0, ev.Eval("limit(7,0,3)", new Scope("global")).AsReal());
        Assert.Empty(ev.StatisticalCalls);
        Assert.Equal("min(max(x,0),3)", SpiceExpression.Rewrite("LIMIT(x, 0, 3)"));
    }

    private static double[] Sample(string call)
    {
        var x = new double[Draws];
        for (int t = 1; t <= Draws; t++)
            x[t - 1] = new Evaluator { Statistics = new ExpressionDraws(seed: 1, trial: t) }
                .Eval(call, new Scope("global")).AsReal();
        return x;
    }
}
