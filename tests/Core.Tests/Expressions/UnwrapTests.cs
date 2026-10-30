using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// <c>unwrap()</c> — a phase continued across ±180° (GitHub discussion 27). S21 at frequency index k and Vdd index v
/// has phase −(40·k + 100·v)°, a straight line that the wrapped phase breaks wherever it passes −180°; unwrapped it is
/// that line exactly, for each sweep point on its own.
/// </summary>
public class UnwrapTests
{
    private static Value Eval(string expr)
    {
        var vdd  = new Axis("Vdd",  [3.0, 5.0], "V");
        var freq = new Axis("freq", [.. Enumerable.Range(0, 13).Select(k => k * 1e9)], "Hz");
        var i    = new Axis("i",    [1.0, 2.0]);
        var j    = new Axis("j",    [1.0, 2.0]);
        var data = new Complex[2 * 13 * 2 * 2];
        for (int v = 0; v < 2; v++)
            for (int k = 0; k < 13; k++)
                data[((v * 13 + k) * 2 + 1) * 2 + 0] = Complex.FromPolarCoordinates(0.5, -(40.0 * k + 100.0 * v) * Math.PI / 180.0);
        var ds = new DataSet();
        ds.Add("S", new DataCube([vdd, freq, i, j], data));
        return new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds })).Eval(expr, new Scope("t"));
    }

    [Fact]
    public void AComplexQuantity_UnwrapsAlongFreq_PerSweepPoint_AndARealPhaseInDegreesUnwrapsTheSame()
    {
        double[] Line(int v) => [.. Enumerable.Range(0, 13).Select(k => -(40.0 * k + 100.0 * v))];

        var fromComplex = Eval("unwrap(SP1.S(2,1))").AsCube();
        Assert.Equal(["Vdd", "freq"], fromComplex.Axes.Select(a => a.Name));
        Assert.Equal([.. Line(0), .. Line(1)], fromComplex.RealValues.Select(x => Math.Round(x, 9)));

        var fromDegrees = Eval("unwrap(phase(SP1.S(2,1)))").AsCube();
        Assert.Equal(fromComplex.RealValues.Select(x => Math.Round(x, 9)), fromDegrees.RealValues.Select(x => Math.Round(x, 9)));
    }

    [Fact]
    public void AMissingSample_IsSteppedOver_NotAReset()
    {
        double[] wrapped = [170, -170, double.NaN, -150, 170];
        Assert.Equal([170, 190, double.NaN, 210, 170], PhaseUnwrap.Degrees(wrapped));
    }
}
