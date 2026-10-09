using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Statistics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Yield;
using RfCore;
using RfCore.Data;
using FreqUnit = CircuitRF.Render.DataDisplay.FreqUnit;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>
/// The yield display of a Monte Carlo at each corner: one tab per corner, every trace pinned to that corner, opening on
/// the corner with the lowest yield.
/// </summary>
public sealed class CornerYieldDisplayTests
{
    private const string LowPass = """
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in out R=50 Ohm
        C:C1 out 0 C=2 pF
        Port:P2 out 0 Num=2 Z=50 Ohm
        analysis SP1 type=sparam start=0.1 stop=1 npts=5 Unit=GHz
        tune R1.R dist=gauss sd=2%
        tune C1.C dist=gauss sd=5%
        goal S21 = dB(SP1.S(2,1)) analysis=SP1 ge -1.2 use=yield
        statistics trials=40
        corner hiC C1.C=2.6 pF
        corner loR R1.R=40 Ohm
        """;

    private const string Source = "/w/lp.yield.npy";

    private static DataSet Run()
    {
        var r = CornerRun.Create(PreparedCircuit.FromText(LowPass, null, null), new CornerOptions { MonteCarlo = true }).Run();
        return r.Data!;
    }

    [Fact]
    public void ARunAtEachCorner_IsOneTabPerCorner_EveryTracePinnedToIt_OpeningOnTheWorst()
    {
        var ds = Run();
        string[] names = ["nominal", "hiC", "loR"];
        Assert.Equal(names, ResultContributions.StackedCorners(ds));

        var config = YieldDisplayPreset.Build(ds, Source);
        Assert.Equal(names, config.Tabs.Select(t => t.Name));
        var yields = ds["yield.yield"].RealValues;
        Assert.Equal(Array.IndexOf(yields, yields.Min()), config.ActiveTabIndex);

        for (int k = 0; k < names.Length; k++)
        {
            var plots = YieldDisplayPreset.Compose(ds, Source, k);
            Assert.Equal([YieldDisplayPreset.PlotKind.Family, YieldDisplayPreset.PlotKind.Histogram,
                          YieldDisplayPreset.PlotKind.YieldSensitivity, YieldDisplayPreset.PlotKind.StatisticsTable],
                         plots.Select(p => p.Kind));
            foreach (var t in plots.SelectMany(p => p.Config.Traces))
                if (t.CubeName is not null)
                    Assert.Contains(t.CubeSlice, s => s.AxisName == "corner" && s.Role == AxisRole.PinToIndex && s.Index == k
                                                      && s.Label == names[k]);
                else
                    Assert.Contains($"[\"{names[k]}\", :]", t.Expression);
        }
    }

    [Fact]
    public void ACornersTraces_ResolveFromThatCornersTrials()
    {
        var ds = Run();
        const int k = 1;
        var plots = YieldDisplayPreset.Compose(ds, Source, k);

        // The family: that corner's 40 trials, each coloured by that corner's own pass, with that corner's nominal.
        var fam = plots[0].Config.Traces.Single();
        var t = DisplayFixtures.CubeTrace(fam.CubeName!, [.. fam.CubeSlice.Select(s => s.ToSlice())]);
        t.Transform = fam.CubeTransform;
        t.ColorBy   = fam.ColorBy;
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        Assert.Equal(40, t.FamilyCurves.Count);
        var pass = ds["trials.pass"].At("corner", k).RealValues;
        Assert.Equal(pass.Select(p => p == 1 ? TrialCategory.Pass : TrialCategory.Fail), t.MemberCategories!);
        Assert.NotEmpty(t.NominalPoints);
        Assert.NotEmpty(SpecLineResolve.For(t, ds));   // the corner is not part of the goal the curve reads

        // The histogram and the sensitivity evaluate; the table's columns resolve.
        DisplayFixtures.Resolved(ds, [.. plots.Skip(1).Take(2).SelectMany(p => p.Config.Traces)
                                          .Select(c => DisplayFixtures.ExpressionTrace(c.Expression!))]);
        // The table: every column resolves, and the corner they share is said once, on the row column.
        var table = new Plot(PlotType.Table, FreqUnit.GHz);
        foreach (var column in plots[3].Config.Traces)
        {
            var c = DisplayFixtures.CubeTrace(column.CubeName!, [.. column.CubeSlice.Select(s => s.ToSlice())]);
            TraceResolve.SetCubeDataFrom(c, ds, PlotType.Table, FreqUnit.GHz);
            Assert.Null(c.InvalidSpecText);
            Assert.Equal(ds["statistics.mean"].Axes[1].Length, c.CubeXValues!.Count);
            table.Traces.Add(c);
        }
        var headers = TableRenderer.BuildColumns(table).Select(c => c.Header).ToList();
        Assert.Equal("quantity @ hiC", headers[0]);
        Assert.Equal(plots[3].Config.Traces.Select(c => c.CubeName), headers.Skip(1));
    }

    /// <summary>A corner run without MC has no trials: the display button is disabled and says why. With MC at each
    /// corner it is live and the display is offered, as after a yield run.</summary>
    [Fact]
    public async Task TheDisplayButton_IsDisabledWithItsReasonWithoutMc_AndOfferedWithIt()
    {
        string dir = OptCli.Dir();
        string cnl = OptCli.Write(dir, "lp.cnl", LowPass);
        Action? started = null;
        var panel = new YieldPanelViewModel
        {
            Discover         = vm => TunableCatalog.Discover(vm.EditModel, TuningFixture.Resolver()),
            PrepareCircuit   = _ => PreparedCircuit.FromFile(cnl, dir),
            SourcePathFor    = _ => cnl,
            StartBackground  = a => { started = a; return Task.CompletedTask; },
            OpenYieldDisplay = (_, _) => Task.CompletedTask,
            HasYieldDisplay  = (_, _) => false,
        };
        panel.SetActiveSchematic(new SchematicViewModel(new SchematicEditModel()), "lp.cnl");
        panel.Mode = YieldMode.Corners;

        panel.RunCommand.Execute(null);
        await Task.Run(started!);
        Assert.Equal(YieldRunState.Finished, panel.State);
        Assert.False(panel.OpenDisplayCommand.CanExecute(null));
        Assert.StartsWith("No trials to plot", panel.OpenDisplayTip);
        Assert.False(panel.OfferYieldDisplay);

        panel.MonteCarloAtEachCorner = true;
        panel.RunCommand.Execute(null);
        await Task.Run(started!);
        Assert.Equal(YieldRunState.Finished, panel.State);
        Assert.True(panel.OpenDisplayCommand.CanExecute(null));
        Assert.True(panel.OfferYieldDisplay);
    }
}
