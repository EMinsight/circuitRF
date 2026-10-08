using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using FreqUnit = CircuitRF.Render.DataDisplay.FreqUnit;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>
/// brief-tuneopt-9 R-to9-4: a plotted trace becomes a pre-filled goal — its analysis, its expression
/// (with the card's transform as the function a measure line uses), the visible X range and the
/// visible marker's value — or says why it cannot. The first two read real <see cref="Trace"/>s
/// through <see cref="TraceGoalReader"/>; the rest are the headless translation alone.
/// </summary>
public sealed class TraceToGoalTests
{
    private static readonly double[] FreqHz = [.. Enumerable.Range(0, 11).Select(k => 1e9 + k * 0.5e9)];  // 1..6 GHz

    /// <summary>A two-port SP1 run: S21 = 0.5∠−30k°, S11 = 0.2, the rest small.</summary>
    private static DataCube SCube()
    {
        var data = new Complex[FreqHz.Length * 4];
        for (int k = 0; k < FreqHz.Length; k++)
        {
            data[k * 4 + 0] = 0.2;
            data[k * 4 + 1] = 0.05;
            data[k * 4 + 2] = Complex.FromPolarCoordinates(0.5, -k * 30 * Math.PI / 180);
            data[k * 4 + 3] = 0.3;
        }
        return new DataCube([new Axis("freq", FreqHz, "Hz"), new Axis("i", [1.0, 2.0]), new Axis("j", [1.0, 2.0])], data);
    }

    private static DataSet Run()
    {
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", SCube());
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.BuildZ0Cube([new(50, 0), new(50, 0)]));
        return ds;
    }

    /// <summary>A Rect plot in GHz whose visible X window is 2–4 GHz.</summary>
    private static Plot PlotShowing2To4GHz()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Axes.Window = new PlotRect(2, -40, 2, 50);
        return plot;
    }

    [Fact]
    public void ADb20S21Trace_BecomesDbOfTheSlice_OverTheVisibleRange_WithTheMarkersValueAsItsLimit()
    {
        var s21 = SCube().ComplexValues.Where((_, n) => n % 4 == 2).ToArray();
        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName  = "SP1.S",
            Slice     = [new("freq", AxisRole.KeepAsX, 0), new("i", AxisRole.PinToIndex, 1), new("j", AxisRole.PinToIndex, 0)],
            Transform = CubeTransform.dB20,
        };
        trace.SetCubeData(FreqHz, s21, null, "freq", "Hz", PlotType.Rect, FreqUnit.GHz);
        trace.Markers.Add(new Marker(trace, 0, false, false, 1) { PositionStatic = new Vector2(3f, 0f) });

        var result = TraceGoalReader.Translate(trace, PlotShowing2To4GHz(), Run());

        var g = Assert.IsType<OptimizationGoal>(result.Goal);
        Assert.Equal("dB(SP1.S[:, 2, 1])", g.Expression);
        Assert.Equal("SP1", g.Analysis);
        Assert.Equal(("freq", "2 GHz", "4 GHz"), (g.Range!.Axis, g.Range.Lo, g.Range.Hi));
        Assert.Equal(GoalType.Ge, g.Type);                                  // transmission: at least
        Assert.Equal(20 * Math.Log10(0.5), double.Parse(g.Limit, System.Globalization.CultureInfo.InvariantCulture), 4);

        // The expression is measure-line text: it evaluates on the run.
        var sp1 = new DataSet(); sp1.Add("S", SCube());
        var v = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = sp1 }))
                    .Eval(g.Expression, new Scope("t")).AsCube();
        Assert.Equal(20 * Math.Log10(0.5), v.RealValues[4], 9);
    }

    [Fact]
    public void AMuTrace_BecomesMuOfTheRunsSCube()
    {
        var trace = new Trace(new SNP(FreqHz, 2), MatrixType.S, 0, 0, DependentVarFormat.Db) { Derived = DerivedParameters.Mu };

        var g = TraceGoalReader.Translate(trace, PlotShowing2To4GHz(), Run()).Goal!;

        Assert.Equal("mu(SP1.S)", g.Expression);
        Assert.Equal("SP1", g.Analysis);
        Assert.Equal((GoalType.Ge, "1"), (g.Type, g.Limit));
    }

    [Theory]
    [InlineData(TraceValueTransform.Phase,   1, 1, "phase(SP1.S[:, 1, 1])", GoalType.Eq)]
    [InlineData(TraceValueTransform.PowerDb, 2, 1, "dB10(SP1.S[:, 2, 1])",  GoalType.Ge)]   // the card's dB is 10·log10
    public void ACubeTransform_BecomesTheMeasureFunction(TraceValueTransform t, int i, int j, string expected, GoalType type)
    {
        var g = TraceToGoal.Translate(new TraceGoalSource
        {
            Kind = TraceGoalKind.Cube, Body = $"SP1.S[:, {i}, {j}]", Transform = t, ValueIsComplex = true,
            Analysis = "SP1", Row = i, Col = j,
        }).Goal!;
        Assert.Equal((expected, type), (g.Expression, g.Type));
    }

    [Fact]
    public void AMeasurementTrace_BecomesItsName_AndTakesTheAnalysisItsMeasureReads()
    {
        var tb = new TestBench("tb");
        tb.Analyses.Add(new HarmonicBalanceAnalysis("HB1"));
        tb.Measurements.Add(new Measurement("Pout_W", "real(0.5*HB1.V(\"Vout\",1)*conj(HB1.I(\"Iout\",1)))"));
        tb.Measurements.Add(new Measurement("Eff", "Pout_W/PDC_W*100"));

        var g = TraceToGoal.Translate(new TraceGoalSource { Kind = TraceGoalKind.Cube, Body = "Eff", Axis = "Pin" }, tb).Goal!;

        Assert.Equal(("Eff", "Eff", "HB1"), (g.Name, g.Expression, g.Analysis));
    }

    [Fact]
    public void AnUntranslatableTrace_SaysWhy()
    {
        var conj = TraceToGoal.Translate(new TraceGoalSource
        {
            Kind = TraceGoalKind.Cube, Body = "SP1.S[:, 1, 1]", Transform = TraceValueTransform.Conj, ValueIsComplex = true,
        });
        Assert.Null(conj.Goal);
        Assert.Contains("complex value has no order", conj.Reason);

        var circles = TraceToGoal.Translate(new TraceGoalSource
        {
            Kind = TraceGoalKind.Derived, SCube = "SP1.S", Metric = TraceDerivedMetric.Other, MetricLabel = "Load Stability Circles",
        });
        Assert.Null(circles.Goal);
        Assert.StartsWith("Load Stability Circles has no expression equivalent", circles.Reason);
    }
}
