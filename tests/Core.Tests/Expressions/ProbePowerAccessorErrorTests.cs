using System;
using System.Collections.Generic;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// Brief ENV-MEAS-2 gates 5 and 6: what <c>A.P("probe")</c> says when it cannot read a power, and that a DataSet holding
/// a cube literally named <c>P</c> keeps the generic positional accessor. The values themselves are gated against real
/// runs in Engine.Tests (<c>ProbePowerAccessorTests</c>).
/// </summary>
public class ProbePowerAccessorErrorTests
{
    private static Axis Branch(params string[] names) => new("branch", [.. Array.ConvertAll(names, _ => 0.0)], "", names);

    /// <summary>An envelope result with SaveWaveforms=no: the two averages, no time cubes.</summary>
    private static DataSet NoWaveforms()
    {
        var ds = new DataSet();
        ds.Add("Pprobe_fund_W", new DataCube([Branch("Iout", "IDC")], new[] { 1.0, 2.0 }));
        ds.Add("Pprobe_W", new DataCube([Branch("Iout", "IDC"), new Axis("harmonic", [0.0, 1.0])], new[] { 0.0, 1.0, 2.0, 0.0 }));
        ds.Add("__ProbeBranches", new DataCube([new Axis("probe", [0.0, 1.0], "", ["Iout", "IDC"])], new double[2]));
        return ds;
    }

    private static string Error(string expr, DataSet ds) =>
        Assert.Throws<ExpressionException>(() =>
            new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["ENV1"] = ds })).Eval(expr, new Scope("t"))).Message;

    [Fact]
    public void Errors_SayWhatIsMissing()
    {
        string unknown = Error("ENV1.P(\"Iload\")", NoWaveforms());
        Assert.Contains("no IProbe named 'Iload'", unknown);
        Assert.Contains("[Iout, IDC]", unknown);

        var noProbes = new DataSet();
        noProbes.Add("V", new DataCube([new Axis("node", [0.0], "", ["a"])], new[] { 1.0 }));
        Assert.Contains("place one in series with the load", Error("ENV1.P(\"Iout\")", noProbes));

        Assert.Contains("SaveWaveforms=yes", Error("ENV1.Pt(\"Iout\")", NoWaveforms()));
    }

    [Fact]
    public void ACubeNamedP_KeepsThePositionalAccessor()
    {
        var ds = NoWaveforms();
        ds.Add("P", new DataCube([new Axis("x", [0.0, 1.0])], new[] { 7.0, 8.0 }));

        var v = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["X"] = ds })).Eval("X.P(0)", new Scope("t"));

        Assert.Equal(7.0, v.AsReal());
    }
}
