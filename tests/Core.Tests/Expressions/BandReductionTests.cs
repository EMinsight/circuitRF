using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// <c>max_over(x, lo, hi)</c> / <c>min_over(x, lo, hi)</c> — the worst value of a spec quantity
/// over a frequency band, which is what a spec line ("loss ≤ 1 dB from 0.1 to 3 GHz") asks for.
///
/// The fixture is built so the answer is known without running anything: S21 at frequency index k
/// (k GHz, 0..12) and Vdd index v is exactly −(k + 10·v) dB, so the insertion loss −dB(S21) is
/// k + 10·v. Over 0.1–3 GHz the worst (largest) loss is therefore at 3 GHz — a band EDGE that is a
/// grid point, the case an exclusive or tolerance-free comparison gets wrong — and over 6–12 GHz the
/// smallest is at 6 GHz.
/// </summary>
public class BandReductionTests
{
    private static (MeasurementContext ctx, Scope scope) SweptS21()
    {
        var vdd  = new Axis("Vdd",  [3.0, 5.0], "V");
        var freq = new Axis("freq", [.. Enumerable.Range(0, 13).Select(k => k * 1e9)], "Hz");
        var i    = new Axis("i",    [1.0, 2.0]);
        var j    = new Axis("j",    [1.0, 2.0]);

        var data = new Complex[2 * 13 * 2 * 2];
        for (int v = 0; v < 2; v++)
            for (int k = 0; k < 13; k++)
                data[((v * 13 + k) * 2 + 1) * 2 + 0] = Math.Pow(10, -(k + 10.0 * v) / 20.0); // S21

        var ds = new DataSet();
        ds.Add("S", new DataCube([vdd, freq, i, j], data));
        return (new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds }), new Scope("t"));
    }

    private static Value Eval(string expr)
    {
        var (ctx, scope) = SweptS21();
        return new Evaluator(ctx).Eval(expr, scope);
    }

    [Fact]
    public void TheWorstValueOverTheBand_IsTakenPerSweepPoint_WithTheBandEdgesInclusive()
    {
        var worstLoss = Eval("max_over(-dB(SP1.S(2,1)), 0.1GHz, 3GHz)").AsCube();
        Assert.Equal("Vdd", Assert.Single(worstLoss.Axes).Name);
        Assert.Equal([3.0, 13.0], worstLoss.RealValues.Select(x => Math.Round(x, 9)));

        var leastRejection = Eval("min_over(-dB(SP1.S(2,1)), 6GHz, 12GHz)").AsCube();
        Assert.Equal([6.0, 16.0], leastRejection.RealValues.Select(x => Math.Round(x, 9)));

        // With the sweep pinned there is one worst case, and it comes back as a number.
        var single = Eval("max_over(at(-dB(SP1.S(2,1)), \"Vdd\", 0), 0.1GHz, 3GHz)");
        Assert.Equal(ValueKind.Real, single.Kind);
        Assert.Equal(3.0, single.AsReal(), 9);
    }

    [Theory]
    [InlineData("max_over(SP1.S(2,1), 0.1GHz, 3GHz)", "complex")]        // no order on complex numbers
    [InlineData("max_over(-dB(SP1.S(2,1)), 13GHz, 14GHz)", "runs from")] // a band with no grid point
    public void AQuestionWithNoWorstValue_IsRefusedSayingWhy(string expr, string reason)
    {
        var ex = Assert.ThrowsAny<ExpressionException>(() => Eval(expr));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }
}
