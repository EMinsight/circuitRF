// ================================================================
//  PowerOnACubeTests.cs — ^ over a swept quantity
//
//  A dBm drive level turned into watts is 10^((Pin - 30)/10), the first step of every PAE
//  measurement. Over a swept Pin that failed with "Cube operand must go through Add/Sub/Mul/Div":
//  Value.Pow was scalar-only, and SqrtOnACubeTests' header names x^0.5 as the same gap.
// ================================================================

using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

public class PowerOnACubeTests
{
    private static readonly Axis Pin = new("Pin", [0.0, 10.0, 20.0], "dBm");

    private static DataCube Eval(string expr, DataCube x)
    {
        var ds = new DataSet();
        ds.Add("x", x);
        var ctx = new MeasurementContext(new Dictionary<string, DataSet> { ["HB1"] = ds });
        return new Evaluator(ctx).Eval(expr, new Scope("t")).AsCube();
    }

    [Fact]
    public void ANumberToTheCube_IsElementWise_AndKeepsTheAxis()
    {
        var r = Eval("10^((HB1.x - 30)/10)", new DataCube([Pin], [0.0, 10.0, 20.0]));

        Assert.Equal(DataKind.Real, r.DataKind);
        Assert.Equal("Pin", r.Axes[0].Name);
        Assert.Equal(1e-3, r.RealValues[0], 15);
        Assert.Equal(1e-2, r.RealValues[1], 15);
        Assert.Equal(1e-1, r.RealValues[2], 15);

        Assert.Equal(r.RealValues, Eval("pow(10, (HB1.x - 30)/10)", new DataCube([Pin], [0.0, 10.0, 20.0])).RealValues);
    }

    /// <summary>The scalar ^'s rule, and sqrt's on a cube: a negative base under a fractional exponent
    /// is taken in complex arithmetic, so x^0.5 and sqrt(x) agree.</summary>
    [Fact]
    public void ANegativeBase_UnderAFractionalExponent_PromotesToComplex_AndAgreesWithSqrt()
    {
        var x = new DataCube([Pin], [4.0, -9.0, 16.0]);
        var pow  = Eval("HB1.x^0.5", x);
        var sqrt = Eval("sqrt(HB1.x)", x);

        Assert.Equal(DataKind.Complex, pow.DataKind);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(sqrt.ComplexValues[i].Real,      pow.ComplexValues[i].Real,      12);
            Assert.Equal(sqrt.ComplexValues[i].Imaginary, pow.ComplexValues[i].Imaginary, 12);
        }
    }
}
