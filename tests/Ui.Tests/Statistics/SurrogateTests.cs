using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Yield;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>R-ya12-2/3: the fit is exact on an exact quadratic, its virtual yield is the true one on the same trials,
/// and a cubic is a poor fit.</summary>
public sealed class QuadraticSurrogateTests
{
    private static double Quadratic(double[] z)
        => 0.2 + 0.3 * z[0] - 0.1 * z[1] + 0.05 * z[2] + 0.04 * z[0] * z[0] - 0.03 * z[1] * z[1] + 0.02 * z[2] * z[2]
           + 0.05 * z[0] * z[1] - 0.01 * z[0] * z[2] + 0.02 * z[1] * z[2];

    /// <summary>Standard normals from a fixed seed (Box–Muller), so the sample is exact and repeatable.</summary>
    private static List<double[]> Normals(int count, int k, int seed)
    {
        var rng = new Random(seed);
        double Next() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        return [.. Enumerable.Range(0, count).Select(_ => Enumerable.Range(0, k).Select(_ => Next()).ToArray())];
    }

    [Fact]
    public void AnExactQuadratic_FitsToR2OfOne_AndItsVirtualYieldIsTheTrueYieldOnTheSameTrials()
    {
        var z = QuadraticSurrogate.Design(3).Concat(Normals(QuadraticSurrogate.CheckTrials(3), 3, 1)).ToList();
        var model = QuadraticSurrogate.Fit(z, [.. z.Select(Quadratic)]);
        Assert.True(model.Full);
        Assert.True(1 - model.RSquared <= 1e-12, $"R² = {model.RSquared:R}");

        var trials = Normals(2000, 3, 2);
        const double width = 0.05;
        var v = QuadraticSurrogate.Score([model], [m => m], [1.0], width, trials);
        int passes = trials.Count(t => Quadratic(t) >= 0);
        double objective = trials.Average(t => 1 / (1 + Math.Exp(-Quadratic(t) / width)));
        Assert.Equal(passes, v.Passes);
        Assert.Equal(objective, v.Objective, 12);
    }

    [Fact]
    public void ACubicMargin_FitsBelowOne_AndIsAPoorFit()
    {
        // z = 0, ±1 (the design) and the check points 2, −2, 0.5: least squares of z³ on 1, z, z² leaves
        // R² = 0.876026 (computed independently), under the 0.9 that warns.
        double[][] z = [[0], [1], [-1], [2], [-2], [0.5]];
        var model = QuadraticSurrogate.Fit(z, [.. z.Select(p => p[0] * p[0] * p[0])]);
        Assert.Equal(0.876026, model.RSquared, 6);
        var poor = Assert.Single(QuadraticSurrogate.PoorFits(new Dictionary<string, double> { ["Vout"] = model.RSquared, ["Gain"] = 0.99 }));
        Assert.Equal("Vout", poor.Key);
    }
}

/// <summary>R-ya12-2/3: the divider centred on the surrogate verifies to the same yield as on simulated trials, for fewer
/// simulations.</summary>
public sealed class SurrogateCenteringTests
{
    [Fact]
    public void TheDivider_OnTheSurrogate_VerifiesToTheSameYield_WithFewerSimulations()
    {
        const string settings = "trials=40 verify=400 maxiter=15";
        var plain = CenteringCircuits.Create(CenteringCircuits.Divider(settings)).Run();
        var fast  = CenteringCircuits.Create(CenteringCircuits.Divider(settings + " surrogate=quadratic")).Run();

        Assert.Equal(CenteringOutcome.Finished, fast.Outcome);
        Assert.Equal(CenteringSurrogate.Quadratic, fast.Surrogate);
        Assert.Null(fast.SwitchedBackAt);
        Assert.All(fast.History, h => Assert.True(h.RSquared!.Values.Min() >= QuadraticSurrogate.PoorFit));

        var a = plain.Verification!.Best;
        var b = fast.Verification!.Best;
        Assert.True(b.Lower <= a.Upper && a.Lower <= b.Upper,
                    $"surrogate {fast.Verification.Sentence} vs simulated {plain.Verification.Sentence}");
        Assert.True(fast.Evaluations < plain.Evaluations, $"{fast.Evaluations} vs {plain.Evaluations} simulations");
    }
}

/// <summary>R-ya12-1: the panel's Centering mode centres to exactly the verb's nominals, and Push is one undo step.</summary>
public sealed class CenteringPanelTests
{
    [Fact]
    public async Task ThePanelsCentredNominals_AreTheVerbs_AndPushIsOneUndoStep()
    {
        string dir = OptCli.Dir();
        string cnl = OptCli.Write(dir, "div.cnl", CenteringCircuits.Divider("trials=30 verify=200 maxiter=8"));
        var (_, tb) = new CnlReader().Read(File.ReadAllText(cnl));
        var model = new SchematicEditModel();
        model.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "1200", "Ohm")));
        model.Components.Add(TuningFixture.Part("R2", SymbolKind.Resistor, 100, ("R", "1000", "Ohm")));
        model.Tuning = tb.Tuning;
        var top = new SchematicViewModel(model);
        Action? started = null;
        var panel = new YieldPanelViewModel
        {
            Discover        = vm => TunableCatalog.Discover(vm.EditModel, TuningFixture.Resolver()),
            PrepareCircuit  = _ => PreparedCircuit.FromFile(cnl, dir),
            SourcePathFor   = _ => cnl,
            StartBackground = a => { started = a; return Task.CompletedTask; },
        };
        panel.SetActiveSchematic(top, "div.cnl");
        panel.Mode = YieldMode.Centering;
        Assert.Equal(["R1.R", "R2.R"], panel.CenterRows.Select(r => r.Key));

        panel.RunCommand.Execute(null);
        await Task.Run(started!);
        Assert.Equal(YieldRunState.Finished, panel.State);
        var centred = panel.CentredValues!;

        var (exit, stdout, stderr) = OptCli.Run("yield", "center", cnl, "--json");
        Assert.True(exit == 0, stderr);
        var verb = JsonNode.Parse(stdout)!["result"]!["center"]!["bestValues"]!.AsObject();
        Assert.Equal(verb.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()), centred.ToDictionary());

        int before = top.UndoRedo.Entries.Count();
        panel.PushCentredCommand.Execute(null);
        Assert.Equal(before + 1, top.UndoRedo.Entries.Count());
        var pushed = TunableCatalog.Discover(model, TuningFixture.Resolver()).Find("R1.R")!;
        Assert.Equal(TunableValue.InUnit(centred["R1.R"], "Ohm")!.Value, pushed.Value, 9);
    }
}
