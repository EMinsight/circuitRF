using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Engine.Statistics;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests.Statistics;

internal static class DoeCircuits
{
    /// <summary>A closed-form response, y = 3a + 2b + ab, read by a goal on variables alone — nothing is simulated —
    /// with c an inert third factor. On coded ±1 factors the effects are a = 6, b = 4, ab = 2 and every other 0.</summary>
    public static string ClosedForm(string doe) => $"""
        a = 0
        b = 0
        c = 0
        R:R1 in 0 R=50 Ohm
        tune a min=-1 max=1 opt=1
        tune b min=-1 max=1 opt=1
        tune c min=-1 max=1 opt=1
        goal Y = 3*a+2*b+a*b le 100
        doe {doe}
        """;

    public static DoeRun Create(string cnl)
    {
        var run = DoeRun.Create(PreparedCircuit.FromText(cnl, null, null));
        Assert.Null(run.Refusal);
        return run;
    }
}

/// <summary>R-ya14-2: the designs are the published ones.</summary>
public sealed class DoeDesignTests
{
    [Fact]
    public void Full2_OfThree_IsTheEightCorners()
    {
        var runs = DoeDesigns.FullFactorial(3);
        var corners = from a in new[] { -1.0, 1 } from b in new[] { -1.0, 1 } from c in new[] { -1.0, 1 } select $"{a},{b},{c}";
        Assert.Equal(corners.Order(), runs.Select(r => string.Join(",", r)).Order());
    }

    [Fact]
    public void Frac_2To5Minus1_ResolutionV_IsThePublishedGenerator_AndAliasesNoMainEffect()
    {
        var plan = DoeDesigns.Fraction(5, 5)!;
        Assert.Equal(["E=ABCD"], plan.Generators);
        var runs = DoeDesigns.Fractional(plan);
        Assert.Equal(16, runs.Length);
        Assert.All(runs, r => Assert.Equal(r[0] * r[1] * r[2] * r[3], r[4]));
        Assert.Equal(5, DoeDesigns.ResolutionOf(DoeDesigns.DefiningRelation(plan)));

        var model = DoeEffects.Model(DoeDesignKind.Frac, 5, plan, runs);
        Assert.All(model.Where(t => t.Term.Factors.Length == 1), t => Assert.Empty(t.Aliases));
        Assert.Equal(15, model.Count);                      // 5 mains + 10 interactions, each clear of the others
    }

    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(24)]
    public void Pb_IsThePublishedCyclicMatrix_AndOrthogonal(int n)
    {
        var m = DoeDesigns.PlackettBurmanMatrix(n);
        if (n == 12) Assert.Equal("++-+++---+-", string.Concat(m[0].Select(v => v > 0 ? '+' : '-')));
        for (int i = 0; i < n - 1; i++)
        {
            Assert.Equal(0, m.Sum(r => r[i]));
            for (int j = i + 1; j < n - 1; j++) Assert.Equal(0, m.Sum(r => r[i] * r[j]));
        }
    }
}

/// <summary>R-ya14-4: effects recovered exactly on a closed-form response, the inert factor under Lenth's margin.</summary>
public sealed class DoeEffectsTests
{
    [Fact]
    public void TheClosedFormEffects_AreRecoveredExactly_AndTheInertFactorIsNotActive()
    {
        var r = DoeCircuits.Create(DoeCircuits.ClosedForm("")).Run();
        Assert.Equal(DoeOutcome.Finished, r.Outcome);
        var fit = r.Responses.Single(x => x.Name == "goal:Y:worst").Fit!;
        double E(string term) => fit.Effects.Single(e => e.Term == term).Effect;
        Assert.Equal(6, E("A"), 12);
        Assert.Equal(4, E("B"), 12);
        Assert.Equal(2, E("AB"), 12);
        Assert.Equal(0, E("C"), 12);
        Assert.Equal(["A", "B", "AB"], fit.Effects.Where(e => e.Active).Select(e => e.Term));
        Assert.Equal(0.0, fit.Curvature!.Value, 12);        // the centre point lies on the plane
    }
}

/// <summary>R-ya14-3/4/5: a real circuit — R and C are active, the inert resistor is not — and the doe line round-trips.</summary>
public sealed class DoeRunTests
{
    private const string LowPass = """
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in out R=100 Ohm
        C:C1 out 0 C=2 pF
        Port:P2 out 0 Num=2 Z=50 Ohm
        R:R9 x 0 R=1 kOhm
        analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
        tune R1.R min=50 Ohm max=200 Ohm opt=1
        tune C1.C min=1 pF max=4 pF opt=1
        tune R9.R min=500 Ohm max=2000 Ohm opt=1
        goal G21 = dB(SP1.S(2,1)) analysis=SP1 ge -6
        doe centre=2
        """;

    [Fact]
    public void TheLowPass_RAndCAreActive_TheInertResistorIsNot_AndTheResultIsWritten()
    {
        string path = Path.Combine(OptCli.Dir(), "lp.doe.npy");
        var run = DoeRun.Create(PreparedCircuit.FromText(LowPass, null, null), new DoeOptions { ResultPath = path });
        Assert.Null(run.Refusal);
        Assert.Equal(10, run.RunCount);
        var r = run.Run();
        Assert.Equal(DoeOutcome.Finished, r.Outcome);
        Assert.Equal(9, r.Evaluations);                     // the second centre point is the first's

        var fit = r.Responses.Single(x => x.Name == "goal:G21:worst").Fit!;
        var active = fit.Effects.Where(e => e.Active).Select(e => e.Term).ToList();
        Assert.Contains("A", active);
        Assert.Contains("B", active);
        Assert.DoesNotContain(active, t => t.Contains('C'));

        Assert.Equal(path, r.WrittenPath);
        Assert.True(r.Data!.ContainsGroup("effects"));
        Assert.Equal(10, r.Data.CubesIn("runs")["coded:R1.R"].RealValues.Length);
    }

    [Fact]
    public void TheDoeLine_RoundTripsByteStable_ThroughCnlAndCsch()
    {
        const string line = "doe design=frac resolution=5 factors=stat levels=sigma:2 centre=3 responses=all parallel=4";
        var (lib, tb) = new CnlReader().Read("R:R1 a 0 R=50 Ohm\n" + line);
        Assert.Empty(tb.ReadWarnings);
        Assert.Contains(line, CnlWriter.Write(tb, lib).Split('\n').Select(l => l.TrimEnd('\r')));

        // A default is never written: the levels stat factors take anyway, the centre point, the design.
        var (lib2, tb2) = new CnlReader().Read("R:R1 a 0 R=50 Ohm\ndoe design=full2 factors=stat levels=sigma:1 centre=1");
        Assert.Contains("doe factors=stat", CnlWriter.Write(tb2, lib2).Split('\n').Select(l => l.TrimEnd('\r')));

        var model = new SchematicEditModel();
        model.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "50", "Ohm")));
        model.Tuning = tb.Tuning;
        string csch = SchematicPersistence.Serialize(model, "Flat");
        var (m1, _, _) = SchematicPersistence.Deserialize(csch);
        Assert.Equal(csch, SchematicPersistence.Serialize(m1, "Flat"));
        Assert.Contains(line, CnlWriter.Write(NetExtractor.Extract(m1, "tb").TestBench).Split('\n').Select(l => l.TrimEnd('\r')));

        var bad = Assert.ThrowsAny<Exception>(() => new CnlReader().Read("R:R1 a 0 R=50 Ohm\ndoe resolution=3"));
        Exception? e = bad;
        while (e is not null and not TuningDirectiveException) e = e.InnerException;
        Assert.Equal("cnl.doe.value-invalid", Assert.IsType<TuningDirectiveException>(e).Diagnostic.Id);
    }
}

/// <summary>R-ya14-7: on a ccf of an exactly quadratic response the model optimum is the analytic one, and the
/// confirmation run matches the prediction.</summary>
public sealed class DoeModelOptimumTests
{
    [Fact]
    public void TheModelOptimum_IsTheAnalyticOptimum_AndTheConfirmationMatchesThePrediction()
    {
        const string cnl = """
            a = 1
            b = 1
            R:R1 in 0 R=50 Ohm
            tune a min=0 max=2 opt=1
            tune b min=0 max=2 opt=1
            goal Y = (a-1.3)^2+2*(b-0.6)^2+0.5*(a-1.3)*(b-0.6) le 0
            doe design=ccf
            """;
        var run = DoeCircuits.Create(cnl);
        var r = run.Run();
        Assert.Equal(DoeOutcome.Finished, r.Outcome);
        Assert.True(1 - r.Responses.Single(x => x.Name == "goal:Y:worst").Fit!.RSquared < 1e-12);

        var o = run.ModelOptimum(r);
        Assert.Null(o.Refusal);
        double a = double.Parse(o.Values["a"], System.Globalization.CultureInfo.InvariantCulture);
        double b = double.Parse(o.Values["b"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1.3, a, 4);
        Assert.Equal(0.6, b, 4);
        var g = Assert.Single(o.Goals);
        Assert.Equal(PointStatus.Evaluated, o.Confirmation);
        Assert.Equal(g.SimulatedValue!.Value, g.PredictedValue!.Value, 9);
    }
}

/// <summary>R-ya14-8: <c>yield doe</c> as a process prints the effects table and exits 0; <c>--json</c> carries the
/// alias sets.</summary>
public sealed class DoeCliVerbTests
{
    [Fact]
    public void YieldDoe_PrintsTheEffectsTable_AndItsJsonCarriesTheAliasSets()
    {
        const string cnl = """
            a = 0
            b = 0
            c = 0
            d = 0
            R:R1 in 0 R=50 Ohm
            tune a min=-1 max=1 opt=1
            tune b min=-1 max=1 opt=1
            tune c min=-1 max=1 opt=1
            tune d min=-1 max=1 opt=1
            goal Y = 3*a+2*b+a*b le 100
            doe design=frac
            """;
        string dir = OptCli.Dir();
        string path = OptCli.Write(dir, "ff.cnl", cnl);

        var (exit, stdout, stderr) = OptCli.Run("yield", "doe", path);
        Assert.True(exit == 0, stderr);
        Assert.Contains("Effects of goal:Y:worst", stdout);
        Assert.Contains("AB", stdout);

        (exit, stdout, stderr) = OptCli.Run("yield", "doe", path, "--json");
        Assert.True(exit == 0, stderr);
        var doe = JsonNode.Parse(stdout)!["result"]!["doe"]!.AsObject();
        var ab = doe["responses"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "goal:Y:worst")!["effects"]!
                    .AsArray().Single(e => e!["term"]!.GetValue<string>() == "AB")!;
        Assert.Contains("CD", ab["aliases"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(File.Exists(Path.Combine(dir, "ff.doe.npy")));

        // check refuses what the run would refuse, in the run's words: no stat=1 entry to vary.
        string none = OptCli.Write(dir, "none.cnl", cnl.Replace("doe design=frac", "doe factors=stat"));
        (exit, stdout, stderr) = OptCli.Run("check", none);
        Assert.Equal(1, exit);
        Assert.Contains("factors=stat has no factor", stdout + stderr);
    }
}

/// <summary>R-ya14-9: the panel's DOE mode shows the plan, runs the verb's design, lists the effects, and hands the
/// confirmed model optimum on.</summary>
public sealed class DoePanelTests
{
    [Fact]
    public async Task TheDoeMode_RunsTheDesign_ListsTheEffects_AndSendsTheOptimumOn()
    {
        string dir = OptCli.Dir();
        string cnl = OptCli.Write(dir, "cf.cnl", DoeCircuits.ClosedForm(""));
        var (_, tb) = new CnlReader().Read(File.ReadAllText(cnl));
        var model = new SchematicEditModel();
        model.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "50", "Ohm")));
        model.Tuning = tb.Tuning;
        var top = new SchematicViewModel(model);
        Action? started = null;
        IReadOnlyDictionary<string, string>? sent = null;
        var panel = new CircuitRF.Ui.Yield.YieldPanelViewModel
        {
            Discover              = vm => CircuitRF.Design.Optimization.TunableCatalog.Discover(vm.EditModel, TuningFixture.Resolver()),
            PrepareCircuit        = _ => PreparedCircuit.FromFile(cnl, dir),
            SourcePathFor         = _ => cnl,
            StartBackground       = a => { started = a; return Task.CompletedTask; },
            SendToOptimizerTarget = (values, _) => sent = values,
        };
        panel.SetActiveSchematic(top, "cf.cnl");
        panel.Mode = CircuitRF.Ui.Yield.YieldMode.Doe;
        Assert.Equal(["a", "b", "c"], panel.DoeFactors.Select(f => f.Key));
        Assert.Contains("9 runs", panel.DoePlanText);

        panel.RunCommand.Execute(null);
        await Task.Run(started!);
        Assert.Equal(CircuitRF.Ui.Yield.YieldRunState.Finished, panel.State);
        Assert.Equal("goal:Y:worst", panel.SelectedDoeResponse);
        Assert.Equal(["A", "B", "AB"], panel.DoeEffects.Where(e => e.Active).Select(e => e.Term));
        Assert.True(File.Exists(Path.Combine(dir, "cf.doe.npy")));

        panel.FindModelOptimumCommand.Execute(null);
        await Task.Run(started!);
        Assert.True(panel.HasDoeOptimum);
        panel.SendOptimumToOptimizerCommand.Execute(null);
        Assert.Equal("-1", sent!["a"]);
        Assert.Equal("-1", sent["b"]);
    }
}
