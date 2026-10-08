using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>brief-tuneopt-9 R-to9-2: <c>group_delay</c> and <c>vswr</c> against answers known
/// without running anything.</summary>
public class GroupDelayVswrTests
{
    [Fact]
    public void ADelayLinesGroupDelay_IsItsLengthOverItsVelocity()
    {
        // 0.1 m at half the speed of light: τ = 0.1 / (c/2) ≈ 0.667 ns. Its phase runs through several
        // whole turns across the band, so the unwrap is exercised.
        const double c = 299_792_458.0;
        double tau = 0.1 / (c / 2);
        var f = Enumerable.Range(0, 201).Select(k => 1e9 + k * 0.05e9).ToArray();
        var data = new Complex[f.Length * 4];
        for (int k = 0; k < f.Length; k++)
        {
            var t = Complex.FromPolarCoordinates(1, -2 * Math.PI * f[k] * tau);
            data[k * 4 + 1] = t;   // S12
            data[k * 4 + 2] = t;   // S21
        }
        var ds = new DataSet();
        ds.Add("S", new DataCube([new Axis("freq", f, "Hz"), new Axis("i", [1.0, 2.0]), new Axis("j", [1.0, 2.0])], data));
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds }));

        var delay = ev.Eval("group_delay(SP1.S(2, 1))", new Scope("t")).AsCube();
        Assert.Equal("s", delay.Unit);
        Assert.All(delay.RealValues, d => Assert.Equal(tau, d, 1e-15));
    }

    [Theory]
    [InlineData("vswr(0.5)", 3.0)]
    [InlineData("vswr(polar(1/3, 45))", 2.0)]
    [InlineData("vswr(0)", 1.0)]
    public void AKnownReflectionCoefficientsVswr(string expr, double expected)
        => Assert.Equal(expected, new Evaluator().Eval(expr, new Scope("t")).AsReal(), 12);
}
