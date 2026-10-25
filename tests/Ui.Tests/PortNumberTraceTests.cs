// ================================================================
//  PortNumberTraceTests.cs — brief DFR27-2.
//
//  A trace's i/j pin names a PORT NUMBER and is looked up on the result at every resolve. On a
//  bench whose Terms are 1, 2, 9, 10, S[:, 10, 9] reads the 9→10 pair, S[:, 4, 3] is invalid naming
//  the ports present, and a re-run that drops Term 9 makes the same trace invalid on the next draw
//  rather than leaving its last data — or, as the old clamp did, reading the last port instead.
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PortNumberTraceTests
{
    /// <summary>An S cube on the given port numbers whose element (i, j) is 100·i + j, so the value a
    /// trace draws says exactly which pair it read.</summary>
    private static DataSet Result(params int[] ports)
    {
        int n = ports.Length;
        var vals = new Complex[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                vals[i * n + j] = new Complex(100 * ports[i] + ports[j], 0);
        var nums = ports.Select(p => (double)p).ToArray();
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", new DataCube(
            [new Axis("freq", [1e9], "Hz"), new Axis("i", nums, "port"), new Axis("j", nums, "port")], vals));
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.WithPortNumbers(
            DataSetBuilder.BuildZ0Cube(Enumerable.Repeat(new Complex(50, 0), n).ToArray()), ports));
        return ds;
    }

    private static Trace Typed(string spec, DataSet ds)
    {
        Assert.True(CubeTraceSpecParser.TryParse(spec, ds, out var cube, out var slice, out _, out var err), err);
        var t = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Complex)
        {
            CubeName = cube, Slice = slice, Transform = CubeTransform.Real,
        };
        return t;
    }

    [Fact]
    public void GappedPorts_ATracePinsByNumber()
    {
        var ds = Result(1, 2, 9, 10);
        var t  = Typed("SP1.S[:, 10, 9]", ds);
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        Assert.Null(t.ExpressionError);
        Assert.Equal(1009f, t.Points.Single().Y);
        Assert.Contains("10, 9", t.BuildPickerExpression());
    }

    [Fact]
    public void GappedPorts_ANumberWithNoTerm_IsInvalidNamingThePortsPresent()
    {
        var ds = Result(1, 2, 9, 10);
        Assert.False(CubeTraceSpecParser.TryParse("SP1.S[:, 4, 3]", ds, out _, out _, out _, out var err));
        Assert.Contains("Port 4", err);
        Assert.Contains("1, 2, 9, 10", err);
    }

    [Fact]
    public void AReRunThatDropsThePort_IsInvalidOnTheNextDraw()
    {
        var t = Typed("SP1.S[:, 10, 9]", Result(1, 2, 9, 10));

        var rerun = Result(1, 2, 10);
        TraceResolve.SetCubeDataFrom(t, rerun, PlotType.Table, FreqUnit.GHz);
        Assert.Empty(t.Points);
        Assert.Contains("Port 9", t.ExpressionError);
        Assert.Contains("1, 2, 10", t.ExpressionError);

        // Renumbered 1…4: port 10 is past the end, which the old clamp read as port 4.
        TraceResolve.SetCubeDataFrom(t, Result(1, 2, 3, 4), PlotType.Table, FreqUnit.GHz);
        Assert.Empty(t.Points);
        Assert.Contains("Port 10", t.ExpressionError);
    }
}
