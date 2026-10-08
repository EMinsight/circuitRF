using System.Numerics;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Core.Design;
using SampleStatistics = CircuitRF.Core.Expressions.SampleStatistics;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Statistics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Tests.Optimization;
using RfCore;
using RfCore.Data;
using FreqUnit = CircuitRF.Render.DataDisplay.FreqUnit;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>brief-yield-8's fixtures: a Monte Carlo result from the 1 V divider, and bare traces over it.</summary>
internal static class DisplayFixtures
{
    /// <summary>The divider's yield run, 200 trials; R2 fixed when <paramref name="fixedR2"/>.</summary>
    public static DataSet YieldResult(string goal = """DC1.V("out") analysis=DC1 in 0.49 0.51""", bool fixedR2 = false)
    {
        string cnl = YieldCircuits.Divider("trials=200").Replace("""DC1.V("out") analysis=DC1 in 0.49 0.51""", goal);
        if (fixedR2) cnl = cnl.Replace("tune R2.R dist=gauss sd=2%", "");
        var r = YieldCircuits.Create(cnl).Run();
        return r.Data!;
    }

    /// <summary>A cube trace over <paramref name="cube"/>, as the picker authors one.</summary>
    public static Trace CubeTrace(string cube, params AxisSlice[] slice)
        => new(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db, false)
           { CubeName = cube, Slice = slice, SourcePath = "/r.yield.npy" };

    /// <summary>A trace drawing <paramref name="expression"/>.</summary>
    public static Trace ExpressionTrace(string expression)
        => new(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db, false)
           { Expression = expression, SourcePath = "/r.yield.npy" };

    public static Plot Resolved(DataSet ds, params Trace[] traces)
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        foreach (var t in traces)
        {
            TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
            Assert.True(t.ExpressionError is null, t.ExpressionError);
            plot.Traces.Add(t);
        }
        plot.Autoscale(force: true);
        return plot;
    }
}

/// <summary>R-ya8-1: bars at their bins, a family side by side, and a display without bars unchanged on disk.</summary>
public sealed class BarsRenderTests
{
    [Fact]
    public void AFiveBinHistogram_DrawsFiveBarsAtItsBins()
    {
        var ds = new DataSet();
        ds.Add("x", new DataCube([new Axis("trial", [.. Enumerable.Range(1, 10).Select(i => (double)i)])],
                                 [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]));
        var t = DisplayFixtures.ExpressionTrace("histogram(x, 5)");
        t.Properties.DrawStyle = TraceDrawStyle.Bars;
        var plot = DisplayFixtures.Resolved(ds, t);
        Assert.Equal(1.8, t.BarWidth!.Value, 12);                    // the function's own width companion

        var tf = PlotRenderer.BuildTransforms(plot, (500, 300));
        var bars = Assert.Single(StatisticsRenderer.BarRects(t, tf, FreqUnit.GHz));
        Assert.Equal(5, bars.Count);
        for (int k = 0; k < 5; k++)
        {
            // Bins of 1.8 over [0, 9], two values in each.
            double c = 0.9 + 1.8 * k;
            var lo = tf.ToCanvas(c - 0.9, 0, false);
            var hi = tf.ToCanvas(c + 0.9, 2, false);
            Assert.Equal(lo.X, bars[k].Left, 2);
            Assert.Equal(hi.X, bars[k].Right, 2);
            Assert.Equal(hi.Y, bars[k].Top, 2);
            Assert.Equal(lo.Y, bars[k].Bottom, 2);
        }
        Assert.True(plot.Axes.Window.Top <= 0, "a bar plot's frame includes zero");
    }

    [Fact]
    public void ATwoMemberFamily_DrawsSideBySideWithinEachBin()
    {
        var ds = new DataSet();
        ds.Add("h", new DataCube([new Axis("corner", [1, 2]), new Axis("bin", [10, 20, 30])], [1, 2, 3, 4, 5, 6.0]));
        var t = DisplayFixtures.CubeTrace("h", new AxisSlice("corner", AxisRole.FamilyIterate, 0),
                                               new AxisSlice("bin", AxisRole.KeepAsX, 0));
        t.Properties.DrawStyle = TraceDrawStyle.Bars;
        var plot = DisplayFixtures.Resolved(ds, t);

        var tf = PlotRenderer.BuildTransforms(plot, (500, 300));
        var members = StatisticsRenderer.BarRects(t, tf, FreqUnit.GHz);
        Assert.Equal(2, members.Count);
        for (int k = 0; k < 3; k++)
        {
            double c = 10 + 10 * k;
            Assert.Equal(tf.ToCanvas(c - 5, 0, false).X, members[0][k].Left, 2);
            Assert.Equal(members[0][k].Right, members[1][k].Left, 2);      // adjacent, not overlapping
            Assert.Equal(tf.ToCanvas(c + 5, 0, false).X, members[1][k].Right, 2);
        }
    }

    [Fact]
    public void ADisplayWithoutBars_WritesNoneOfTheNewFields_AndBarsRoundTrip()
    {
        var plain = new PlotContainerConfig { PlotType = PlotType.Rect, Traces = [new TraceConfig { CubeName = "x" }] };
        string json = JsonSerializer.Serialize(plain, DataDisplayJson.Options);
        foreach (var key in new[] { "DrawStyle", "NormalFit", "StatisticsOrigin", "SpecLines" })
            Assert.DoesNotContain($"\"{key}\"", json);

        plain.Traces[0].Properties.DrawStyle = TraceDrawStyle.Bars;
        var back = JsonSerializer.Deserialize<PlotContainerConfig>(JsonSerializer.Serialize(plain, DataDisplayJson.Options),
                                                                   DataDisplayJson.Options)!;
        var props = new TraceProperties();
        PlotConfigLoader.ApplyProperties(back.Traces[0].Properties, props);
        Assert.Equal(TraceDrawStyle.Bars, props.DrawStyle);
    }
}

/// <summary>R-ya8-2: each entry writes the expected expression and style; Back to curves restores the trace exactly.</summary>
public sealed class StatisticsMenuTests
{
    [Fact]
    public void EachEntry_WritesItsExpressionAndStyle_AndBackToCurvesRestoresTheOriginal()
    {
        var ds = DisplayFixtures.YieldResult();
        const string worst = "trials.goal:Vout:worst";
        var slice = new[] { new AxisSlice("trial", AxisRole.KeepAsX, 0) };
        var t = DisplayFixtures.CubeTrace(worst, slice);
        t.Transform = CubeTransform.Mag;
        t.Expression = $"mag({worst})";
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(t);

        int fd = SampleStatistics.FreedmanDiaconisBins(ds[worst].RealValues);
        var expected = new (TraceStatistic Kind, bool Percent, string Expression, TraceDrawStyle Style)[]
        {
            (TraceStatistic.Histogram, false, $"histogram(mag({worst}), {fd})",              TraceDrawStyle.Bars),
            (TraceStatistic.Histogram, true,  $"histogram(mag({worst}), {fd}, \"percent\")", TraceDrawStyle.Bars),
            (TraceStatistic.Cdf,       false, $"cdf(mag({worst}))",                         TraceDrawStyle.Step),
            (TraceStatistic.Quantile,  false, $"normq(mag({worst}))",                       TraceDrawStyle.Line),
        };
        foreach (var e in expected)
        {
            var (rewrite, why) = TraceStatistics.Build(t, ds, e.Kind, percent: e.Percent);
            Assert.True(why is null, why);
            TraceStatistics.Apply(t, rewrite!);
            Assert.Equal(e.Expression, t.Expression);
            Assert.Equal(e.Style, t.Properties.DrawStyle);
            TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
            Assert.True(t.ExpressionError is null, t.ExpressionError);
        }

        var (ys, _) = TraceStatistics.Build(t, ds, TraceStatistic.YieldSensitivity, statSpec: "trials.stat:R1.R");
        int ysBins = SampleStatistics.FreedmanDiaconisBins(ds["trials.stat:R1.R"].RealValues);
        Assert.Equal($"100*yield_sens(trials.pass, trials.stat:R1.R, {ysBins})", ys!.Expression);
        Assert.Equal($"histogram(trials.stat:R1.R + 0*trials.pass, {ysBins})", ys.Companion);
        TraceStatistics.Apply(t, ys);
        var companion = TraceStatistics.CompanionOf(t, ys.Companion!);
        plot.Traces.Add(companion);

        // Back to curves: the trace as it was, field for field, and the series the menu added goes.
        var removed = TraceStatistics.BackToCurves(plot, t);
        Assert.Same(companion, Assert.Single(removed));
        Assert.Equal($"mag({worst})", t.Expression);
        Assert.Equal(worst, t.CubeName);
        Assert.Same(slice, t.Slice);
        Assert.Equal(CubeTransform.Mag, t.Transform);
        Assert.Equal(TraceDrawStyle.Line, t.Properties.DrawStyle);
        Assert.Null(t.StatisticsOrigin);
    }
}

/// <summary>R-ya8-3: a goal's limit on its histogram, and a sloped goal across its range on a family.</summary>
public sealed class SpecLineTests
{
    private static DataSet WithGoal(OptimizationGoal g)
    {
        var ds = new DataSet();
        string line = TuningDirectiveText.GoalLine(g);
        ds.AddToGroup("yield", $"goal:{g.Name}:spec", new DataCube([new Axis($"goal:{g.Name}:spec", [0], "", [line])], [0.0]));
        return ds;
    }

    [Fact]
    public void AGe14Goal_DrawsOneVerticalLineAt14_OnItsHistogram()
    {
        var ds = WithGoal(new OptimizationGoal { Name = "S21", Expression = "dB(SP1.S(2,1))", Analysis = "SP1", Type = GoalType.Ge, Limit = "14" });
        ds.AddToGroup("trials", "goal:S21:worst",
            new DataCube([new Axis("trial", [1, 2, 3, 4, 5])], [14.5, 15, 15.2, 16, 14.1]));
        var t = DisplayFixtures.ExpressionTrace("histogram(trials.goal:S21:worst, 4)");
        DisplayFixtures.Resolved(ds, t);

        var line = Assert.Single(t.SpecLines);
        Assert.True(line.Vertical);
        Assert.Equal(14, line.X0);
        Assert.Equal("S21 ≥ 14", line.Label);
    }

    [Fact]
    public void ASlopedGoalOverFreq_DrawsASlopedLineAcrossItsRange_OnTheFamily()
    {
        var ds = WithGoal(new OptimizationGoal
        {
            Name = "S21", Expression = "dB(SP1.S(2,1))", Analysis = "SP1", Type = GoalType.Ge, Limit = "14", LimitAtHi = "12",
            Range = new GoalRange { Axis = "freq", Lo = "1 GHz", Hi = "2 GHz" },
        });
        var freq = new Axis("freq", [0.5e9, 1e9, 1.5e9, 2e9, 2.5e9], "Hz");
        var s = new Complex[3 * 5 * 2 * 2];
        for (int k = 0; k < s.Length; k++) s[k] = new Complex(5, 0);
        ds.AddToGroup("SP1", "S", new DataCube([new Axis("trial", [1, 2, 3]), freq, new Axis("i", [1, 2]), new Axis("j", [1, 2])], s));

        var t = DisplayFixtures.CubeTrace("SP1.S",
            new AxisSlice("trial", AxisRole.FamilyIterate, 0), new AxisSlice("freq", AxisRole.KeepAsX, 0),
            new AxisSlice("i", AxisRole.PinToIndex, 1), new AxisSlice("j", AxisRole.PinToIndex, 0));
        t.Transform = CubeTransform.dB20;
        DisplayFixtures.Resolved(ds, t);

        var line = Assert.Single(t.SpecLines);
        Assert.False(line.Vertical);
        Assert.Equal((1e9, 2e9, 14.0, 12.0), (line.X0, line.X1, line.Y0, line.Y1));
    }
}

/// <summary>R-ya8-2: on a divider whose yield rises with R1, the yield-sensitivity bars rise with it.</summary>
public sealed class YieldSensitivityTests
{
    [Fact]
    public void OnADividerWhoseYieldRisesWithR1_TheBarsIncreaseMonotonically()
    {
        // Vout = R2/(R1 + R2) ≤ 0.5 exactly when R1 ≥ R2, and R2 is fixed — so a trial passes iff its R1 ≥ 1 kΩ.
        var ds = DisplayFixtures.YieldResult("""DC1.V("out") analysis=DC1 le 0.5""", fixedR2: true);
        var t = DisplayFixtures.CubeTrace("trials.pass", new AxisSlice("trial", AxisRole.KeepAsX, 0));
        var (rewrite, why) = TraceStatistics.Build(t, ds, TraceStatistic.YieldSensitivity, bins: 8, statSpec: "trials.stat:R1.R");
        Assert.True(why is null, why);
        TraceStatistics.Apply(t, rewrite!);
        var counts = TraceStatistics.CompanionOf(t, rewrite!.Companion!);
        DisplayFixtures.Resolved(ds, t, counts);

        var y = t.Points.OrderBy(p => p.X).Select(p => p.Y).ToList();
        Assert.Equal(8, y.Count);
        for (int k = 1; k < y.Count; k++) Assert.True(y[k] >= y[k - 1], $"bin {k}: {y[k]} after {y[k - 1]}");
        Assert.Equal(0, y[0]);
        Assert.Equal(100, y[^1]);
        Assert.Equal(200, counts.Points.Sum(p => p.Y));                     // every trial in one bin
        Assert.Equal(t.BarWidth!.Value, counts.BarWidth!.Value, 9);         // the same bins
    }
}

/// <summary>R-ya8-6: <c>plot … stat=histogram --spec-lines</c> and the in-process composer write the same SVG bytes.</summary>
public sealed class HistogramPlotParityTests
{
    [Fact]
    public void AHistogramWithSpecLines_IsTheSameBytesFromTheCliAndInProcess()
    {
        string dir = OptCli.Dir();
        string cnl = YieldCli.Divider(dir);
        string npy = Path.Combine(dir, "div.yield.npy");
        var run = StatisticalRun.Create(PreparedCircuit.FromFile(cnl, dir),
                                        new StatisticalOptions { Mode = StatisticalMode.Yield, ResultPath = npy });
        Assert.Equal(StatisticalOutcome.Finished, run.Run().Outcome);

        string svg = Path.Combine(dir, "h.svg"), cdd = Path.Combine(dir, "h.cdd");
        var (exit, _, err) = OptCli.Run("plot", npy, "-o", svg, "--write-cdd", cdd, "--spec-lines",
                                        "--trace", "cube=trials.goal:Vout:worst,stat=histogram,fit=normal");
        Assert.True(exit == 0, err);

        // In process: the document the verb wrote, through the loader, the placement and the composer.
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd), DataDisplayJson.Options)!;
        var pc = config.Tabs[0].Plots[0];
        Assert.Equal(TraceDrawStyle.Bars, pc.Traces[0].Properties.DrawStyle);
        var (sources, refusal) = CddSources.Bind(npy, config, config.Tabs, [0], [npy]);
        Assert.Null(refusal);
        var plot = PlotConfigLoader.LoadPlot(pc, sources!);
        Assert.Equal(2, plot.Traces[0].SpecLines.Count);                     // in 0.49 0.51: both edges
        Assert.NotNull(plot.Traces[0].NormalFit);

        var placed = RenderDataDisplay.Place(plot, pc.Left, pc.Top, pc.Width, pc.Height, pc.FreqUnit,
                                             sources!.HasMultipleSources, t => sources.AliasFor(t.EffectiveSourcePath ?? ""));
        var current = AppSettings.Current;
        var settings = new AppSettings
        {
            ExportTheme                    = current.ExportTheme,
            ExportTransparentBackground    = current.ExportTransparentBackground,
            MarkerBoxTransparentBackground = current.MarkerBoxTransparentBackground,
            AlwaysDisplayDataSourcePrefix  = current.AlwaysDisplayDataSourcePrefix,
        };
        string mine = PlotDocumentWriter.BuildSvgString(
            canvas => PlotComposer.Render(canvas, [placed], RenderTheme.Light, settings, PagePlacement.Letter),
            PagePlacement.Letter);

        // Byte for byte but for Skia's clipPath ids, which come from a counter in the PROCESS (RailZMapTests'
        // WithoutSkiaIds): a second SVG rendered in one test process writes cl_9 where the CLI's fresh process wrote cl_3.
        static string WithoutSkiaIds(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\bcl_[0-9a-fA-F]+\b", "cl_");
        Assert.Equal(WithoutSkiaIds(mine), WithoutSkiaIds(File.ReadAllText(svg, new System.Text.UTF8Encoding(false))));
    }
}

/// <summary>R-ya8-5, owner report 2026-10-08: Σ did nothing after a yield run, because a yield result lives beside its
/// schematic and the source list scanned only <c>results/</c>. It is listed now, Σ is enabled only while such a result
/// is the source, and a display naming the result by its full path shows it in the source list.</summary>
public sealed class StatisticsTableSourceTests
{
    [Fact]
    public async Task AYieldResultBesideItsSchematic_IsListed_AndSigmaIsEnabledOnlyWhileItIsTheSource()
    {
        string ws = OptCli.Dir();
        Directory.CreateDirectory(Path.Combine(ws, "results"));
        string npy = Path.Combine(ws, "Div", "schematic", "Div.yield.npy");
        Directory.CreateDirectory(Path.GetDirectoryName(npy)!);
        var run = StatisticalRun.Create(PreparedCircuit.FromText(YieldCircuits.Divider("trials=40"), null, null),
                                        new StatisticalOptions { ResultPath = npy });
        Assert.Null(run.Refusal);
        run.Run();
        Assert.True(File.Exists(npy));

        var vm  = new CircuitRF.Ui.DataDisplay.ViewModels.DisplayWindowViewModel();
        var lib = vm.DataSourceLibrary;
        lib.ResultsRootProvider             = () => Path.Combine(ws, "results");
        lib.KnownStatisticalResultsProvider = () => CircuitRF.Design.Results.StatisticalResultFiles.In(ws);
        lib.RefreshAvailableDataSources();

        var item = Assert.Single(lib.AvailableDataSources);
        Assert.Equal("../Div/schematic/Div.yield.npy", item.LogicalId);
        Assert.False(vm.AddStatisticsTableCommand.CanExecute(null));

        await lib.SelectDataSourceAsync(item.LogicalId);
        Assert.True(vm.AddStatisticsTableCommand.CanExecute(null));

        await lib.SelectDataSourceAsync(npy);                            // the Yield panel's display names it rooted
        Assert.Equal(item, vm.SelectedDataSourceItem);
    }
}
