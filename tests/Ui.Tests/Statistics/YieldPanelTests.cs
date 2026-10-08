using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Yield;
using RfCore.Data;
using RfCore.Export;
using FreqUnit = CircuitRF.Render.DataDisplay.FreqUnit;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>
/// brief-yield-10's fixture: the Yield panel over the 1 V divider written to disk, its bench prepared from that file
/// exactly as <c>yield</c> prepares it, and the schematic's setup the file's own tuning block. The panel's thread hops
/// are played inline; its run thread is the test's to start.
/// </summary>
internal sealed class YieldPanelFixture
{
    public readonly string Dir = OptCli.Dir();
    public readonly string Cnl;
    public readonly SchematicViewModel Top;
    public readonly YieldPanelViewModel Panel = new();
    public Action? Started;

    public YieldPanelFixture(string statistics = "trials=200")
    {
        Cnl = YieldCli.Divider(Dir, statistics: statistics);
        var (_, tb) = new CnlReader().Read(File.ReadAllText(Cnl));
        var model = new SchematicEditModel();
        model.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "1000", "Ohm")));
        model.Components.Add(TuningFixture.Part("R2", SymbolKind.Resistor, 100, ("R", "1000", "Ohm")));
        model.Tuning = tb.Tuning;
        Top = new SchematicViewModel(model);
        Panel.Discover        = vm => TunableCatalog.Discover(vm.EditModel, TuningFixture.Resolver());
        Panel.PrepareCircuit  = _ => PreparedCircuit.FromFile(Cnl, Dir);
        Panel.SourcePathFor   = _ => Cnl;
        Panel.StartBackground = a => { Started = a; return Task.CompletedTask; };
        Panel.SetActiveSchematic(Top, "div.cnl");
    }

    /// <summary>Starts the run the panel prepared, on its own thread, and returns it.</summary>
    public Task Start() => Task.Run(Started!);

    public static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            Thread.Sleep(5);
        }
    }

    /// <summary>Every cube of the result at <paramref name="path"/> outside the summary group, by address.</summary>
    public static Dictionary<string, DataCube> Cubes(string path)
        => YieldCircuits.All(DataSetImporter.Import(path).DataSet)
                        .Where(kv => !kv.Key.StartsWith("yield.", StringComparison.Ordinal))
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
}

/// <summary>R-ya10-3: a tolerance edit is one undo step and writes the tune line check accepts; a mixed complex pair
/// is refused inline, in check's sentence.</summary>
public sealed class YieldPanelEditTests
{
    private static string TuneLine(SchematicEditModel m, string key)
        => CnlWriter.Write(NetExtractor.Extract(m, "tb").TestBench).Split('\n').Single(l => l.StartsWith($"tune {key} "));

    [Fact]
    public void ATolerance_IsOneUndoStep_AndRoundTripsToALineCheckAccepts_AndAMixedPairIsRefusedInChecksWords()
    {
        var model = new SchematicEditModel();
        model.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "50", "Ohm")));
        model.Components.Add(TuningFixture.Part("VAR1", SymbolKind.Var, 100, ("Zs", "40+15j", "Ohm")));
        var top = new SchematicViewModel(model);
        var panel = new YieldPanelViewModel { Discover = vm => TunableCatalog.Discover(vm.EditModel, TuningFixture.Resolver()) };
        panel.SetActiveSchematic(top, "tb.csch");

        // Adding a tolerance: one undo step, an entry with tune and opt off, and the line check accepts.
        int before = top.UndoRedo.Entries.Count();
        panel.SetToleranced("R1.R", true);
        Assert.Equal(before + 1, top.UndoRedo.Entries.Count());
        var row = Assert.Single(panel.Variables);
        Assert.Equal("± 5 % at 3σ", row.SpreadText);
        Assert.Equal("tune R1.R dist=gauss tol=5% sigmas=3", TuneLine(model, "R1.R"));

        // The compact editor: σ 1 Ω is sd=1 Ohm — one more step, and undone by one.
        row.CommitSpread("σ 1 Ω");
        Assert.Equal(before + 2, top.UndoRedo.Entries.Count());
        string line = TuneLine(model, "R1.R");
        Assert.Equal("tune R1.R dist=gauss sd=1 Ohm", line);
        var (lib, tb) = new CnlReader().Read("R:R1 a 0 R=50 Ohm\n" + line);
        Assert.DoesNotContain(StatisticsValidator.Validate(tb, TunableCatalog.FromNetlist(tb, lib)),
                              d => d.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error);
        top.UndoRedo.Undo();
        Assert.Equal("tune R1.R dist=gauss tol=5% sigmas=3", TuneLine(model, "R1.R"));

        // A tolerance on mag(Zs) beside one on real(Zs) mixes a rectangular and a polar part: refused, nothing written,
        // in the sentence check prints for the same lines.
        panel.SetToleranced("real(Zs)", true);
        int steps = top.UndoRedo.Entries.Count();
        panel.SetToleranced("mag(Zs)", true);
        Assert.Equal(steps, top.UndoRedo.Entries.Count());
        Assert.False(panel.IsToleranced("mag(Zs)"));

        var (clib, ctb) = new CnlReader().Read(
            "Zs = 40+15j Ohm\nR:R1 a 0 R=50 Ohm\ntune real(Zs) dist=gauss tol=5% sigmas=3\ntune mag(Zs) dist=gauss tol=5% sigmas=3");
        var check = StatisticsValidator.Validate(ctb, TunableCatalog.FromNetlist(ctb, clib)).Single(d => d.Id == "yield.complex.mixed");
        Assert.Equal($"Refused: {check.Render()}", panel.StatusText);
    }
}

/// <summary>R-ya10-6, D14: run, pause, resume and stop on the divider — the result equals an uninterrupted run's.</summary>
public sealed class YieldPanelRunTests
{
    [Fact]
    public void PauseAndResume_LeaveTheResultAnUninterruptedRunsBytes_AndStopKeepsTheTrialsItFinished()
    {
        // Paused before the first batch, resumed: the file is the uninterrupted run's, byte for byte.
        var f = new YieldPanelFixture("trials=120 parallel=4");
        string reference = Path.Combine(f.Dir, "ref.npy");
        StatisticalRun.Create(PreparedCircuit.FromFile(f.Cnl, f.Dir),
                              new StatisticalOptions { ResultPath = reference }).Run();

        f.Panel.RunCommand.Execute(null);
        Assert.True(f.Panel.IsRunning);
        f.Panel.PauseResumeCommand.Execute(null);
        var task = f.Start();
        YieldPanelFixture.Until(() => f.Panel.StatusText == "Paused");
        Assert.True(f.Panel.IsPaused);
        f.Panel.PauseResumeCommand.Execute(null);
        task.Wait();
        Assert.True(f.Panel.IsFinished);
        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(f.Panel.ResultPath!));
        Assert.Equal(120, f.Panel.Trials.Count);
        Assert.Contains("120 / 120", f.Panel.TrialsDoneText);

        // Paused after the first batch, then stopped: every finished trial is kept, and they are the trials an
        // uninterrupted run of that many draws.
        var g = new YieldPanelFixture("trials=120 parallel=4");
        bool paused = false;
        g.Panel.PostToUi = a =>
        {
            a();
            if (!paused && g.Panel.TrialsDoneText is { Length: > 0 } done && !done.StartsWith("0 /"))
            {
                paused = true;
                g.Panel.PauseResumeCommand.Execute(null);
            }
        };
        g.Panel.RunCommand.Execute(null);
        var stopped = g.Start();
        YieldPanelFixture.Until(() => g.Panel.StatusText == "Paused");
        g.Panel.StopCommand.Execute(null);
        stopped.Wait();
        var r = g.Panel.Result!;
        Assert.Equal(StatisticalOutcome.Finished, r.Outcome);
        Assert.InRange(r.Trials, 1, 119);

        string four = Path.Combine(g.Dir, "four.npy");
        string cnl4 = YieldCli.Divider(g.Dir, "div4.cnl", $"trials={r.Trials} parallel=4");
        StatisticalRun.Create(PreparedCircuit.FromFile(cnl4, g.Dir), new StatisticalOptions { ResultPath = four }).Run();
        var mine = YieldPanelFixture.Cubes(g.Panel.ResultPath!);
        var theirs = YieldPanelFixture.Cubes(four);
        Assert.Equal(theirs.Keys.Order(), mine.Keys.Order());
        foreach (var (name, cube) in theirs)
            Assert.Equal(cube.RealValues, mine[name].RealValues);
    }
}

/// <summary>R-ya10-10: the panel calls StatisticalRun and nothing else — its result is the verb's, byte for byte.</summary>
public sealed class YieldPanelParityTests
{
    [Fact]
    public void ThePanelsDataSet_IsTheYieldVerbsBytes()
    {
        var f = new YieldPanelFixture();
        string verb = Path.Combine(f.Dir, "verb.npy");
        Assert.Equal(0, OptCli.Run("yield", "estimate", f.Cnl, "-o", verb).Exit);

        f.Panel.RunCommand.Execute(null);
        f.Start().Wait();
        Assert.True(f.Panel.IsFinished, f.Panel.StatusText);
        Assert.Equal(StatisticalRun.ResultPathFor(f.Cnl), f.Panel.ResultPath);
        Assert.Equal(File.ReadAllBytes(verb), File.ReadAllBytes(f.Panel.ResultPath!));
    }
}

/// <summary>R-ya10-8: Open yield display composes the four plot kinds, every trace bound to the result, out of YA-8 and
/// YA-9's own trace-writing code — and the family it picks for a goal is a curve that goal's spec lines land on.</summary>
public sealed class YieldDisplayComposeTests
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
        """;

    [Fact]
    public void TheDisplay_HoldsAFamilyAHistogramASensitivityAndTheTable_BoundToTheResult()
    {
        var ds = YieldCircuits.Create(LowPass).Run().Data!;
        const string source = "/w/lp.yield.npy";
        var plots = YieldDisplayPreset.Compose(ds, source);

        Assert.Equal([YieldDisplayPreset.PlotKind.Family, YieldDisplayPreset.PlotKind.Histogram,
                      YieldDisplayPreset.PlotKind.YieldSensitivity, YieldDisplayPreset.PlotKind.StatisticsTable],
                     plots.Select(p => p.Kind));
        Assert.All(plots.SelectMany(p => p.Config.Traces), t => Assert.Equal(source, t.SourcePath));

        // The family: SP1.S over the trials, coloured by pass — and the goal's spec line lands on it.
        var fam = Assert.Single(plots[0].Config.Traces);
        Assert.Equal("SP1.S", fam.CubeName);
        Assert.Equal("pass", fam.ColorBy);
        var t = DisplayFixtures.CubeTrace(fam.CubeName!, [.. fam.CubeSlice.Select(s => s.ToSlice())]);
        t.Transform = fam.CubeTransform;
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        Assert.True(t.IsFamily);
        Assert.Equal(40, t.FamilyCurves.Count);
        Assert.NotEmpty(SpecLineResolve.For(t, ds));

        // The histogram and the sensitivity are the Statistics menu's own rewrites; the table is its preset.
        Assert.StartsWith("histogram(trials.goal:S21:worst", plots[1].Config.Traces[0].Expression);
        Assert.StartsWith("100*yield_sens(trials.pass, trials.stat:", plots[2].Config.Traces[0].Expression);
        Assert.Equal(PlotType.Table, plots[3].Config.PlotType);
    }
}

/// <summary>R-ya10-1: the Yield panel is tabbed behind the Optimizer by default and in a layout saved before it existed.</summary>
public sealed class YieldPanelDockTests
{
    [Fact]
    public void ALayoutSavedBeforeYield_GainsItBehindTheOptimizer()
    {
        Assert.Contains(DockPanelIds.Yield, DockPanelIds.All);
        foreach (var layout in new[] { DockLayoutDefaults.Default(), DockLayoutDefaults.ProjectTreeAndLibrary() })
        {
            var o = layout.Panels.Single(p => p.Id == DockPanelIds.Optimizer);
            var y = layout.Panels.Single(p => p.Id == DockPanelIds.Yield);
            Assert.Equal((o.Side, o.Group, o.Inboard), (y.Side, y.Group, y.Inboard));
            Assert.True(y.Order > o.Order);
            Assert.False(y.Active);
        }

        var old = DockLayoutDefaults.ProjectTreeAndLibrary();
        old.Panels.RemoveAll(p => p.Id == DockPanelIds.Yield);
        var filled = DockLayoutDefaults.WithMissingPanelsFilled(old);
        var opt   = filled.Panels.Single(p => p.Id == DockPanelIds.Optimizer);
        var yield = filled.Panels.Single(p => p.Id == DockPanelIds.Yield);
        Assert.Equal((opt.Side, opt.Group), (yield.Side, yield.Group));
        Assert.True(yield.Order > opt.Order);
    }
}

/// <summary>The Spread box (owner report 2026-10-08): a unit of another quantity is refused rather than read as a
/// meaningless number, a range on a Gaussian (or a σ on a uniform) is refused naming the distribution it needs, the word
/// sigma reads as σ, and ohm is kept and shown as Ω.</summary>
public sealed class YieldSpreadEditTests
{
    [Fact]
    public void TheSpreadBox_RefusesAForeignUnitAndAMisshapedSpread_AndReadsSigmaAndOhm()
    {
        var f = new YieldPanelFixture();
        var r1 = f.Panel.Variables.Single(v => v.Key == "R1.R");
        string before = r1.SpreadText;

        r1.CommitSpread("± 3 pF");
        Assert.Equal(before, r1.SpreadText);
        Assert.Contains("is not in the value's own kind of unit, Ohm", f.Panel.StatusText);

        r1.CommitSpread("900 … 1100 Ohm");
        Assert.Equal(before, r1.SpreadText);
        Assert.Contains("needs a Uniform or Discrete distribution", f.Panel.StatusText);

        r1.CommitSpread("± 3 % at 2 sigma");
        Assert.Equal("± 3 % at 2σ", r1.SpreadText);
        r1.CommitSpread("sd 1 kohm");
        Assert.Equal("σ 1 kΩ", r1.SpreadText);
    }
}
