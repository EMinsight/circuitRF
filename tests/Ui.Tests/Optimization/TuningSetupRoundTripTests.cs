using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>TO-1 R-to1-2/3/4: the tuning block survives <c>.csch</c> → <c>.cnl</c> → <c>.csch</c> → <c>.cnl</c>
/// unchanged, and a schematic without one is written exactly as before.</summary>
public sealed class TuningSetupRoundTripTests
{
    /// <summary>Every field of the model, set once.</summary>
    private static TuningSetup EveryField() => new()
    {
        Variables =
        [
            new TunableEntry
            {
                Key = "R1.R", Tune = true, Opt = true, Min = "10 Ohm", Max = "200 Ohm", Scale = TuneScale.Log,
                Step = "1 Ohm", Discrete = TuneDiscrete.Preferred, Extra = new() { ["tol"] = "5" },
            },
            new TunableEntry { Key = "DUT:Wline", Opt = true, Min = "50 um", Max = "400 um", Discrete = TuneDiscrete.Integer },
        ],
        Presets =
        [
            new TuningPreset
            {
                Name = "wide band", Created = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc),
                Values = new() { ["R1.R"] = "47 Ohm", ["DUT:Wline"] = "212 um" },
            },
            new TuningPreset { Name = "Last tuned", IsLastTuned = true, Values = new() { ["R1.R"] = "51 Ohm" } },
        ],
        Goals =
        [
            new OptimizationGoal
            {
                Name = "Match", Expression = "dB(SP1.S(1,1))", Analysis = "SP1",
                Range = new GoalRange { Axis = "freq", Lo = "1 GHz", Hi = "2 GHz" },
                Type = GoalType.Le, Limit = "-15", LimitAtHi = "-10", Weight = 2, Enabled = false,
                Extra = new() { ["note"] = "x" },
            },
            new OptimizationGoal
            {
                Name = "Flat", Expression = "max_over(dB(SP1.S(2,1)), freq) - 1", Analysis = "SP1",
                Type = GoalType.In, Limit = "-0.6", UpperLimit = "-0.2",
            },
            // A variable named like a goal type: the writer must quote the expression to keep it whole.
            new OptimizationGoal { Name = "Order", Expression = "Lout - out", Type = GoalType.Ge, Limit = "0" },
        ],
        Optimizer = new OptimizerSettings
        {
            Algorithm = "cmaes", Options = new() { ["popsize"] = "20" }, MaxIterations = 200, MaxEvaluations = 5000,
            TimeLimit = "60 s", Cost = OptimizerCost.Minimax, Scope = OptimizerScope.All, Seed = 1, Parallelism = 4,
            Extra = new() { ["future"] = "1" },
        },
    };

    private static SchematicEditModel Flat()
    {
        var m = new SchematicEditModel();
        m.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "50", "Ohm")));
        m.Components.Add(TuningFixture.Part("VAR1", SymbolKind.Var, 1000, ("Wline", "300", "um")));
        m.Analyses.Add(new SParameterAnalysis("SP1", new FrequencySpec("1", "3", 21, SweepKind.Linear, "GHz", "GHz")));
        return m;
    }

    private static string Cnl(SchematicEditModel m) => CnlWriter.Write(NetExtractor.Extract(m, "tb").TestBench);

    private static IEnumerable<string> TuningLines(string cnl)
        => cnl.Split('\n').Select(l => l.TrimEnd('\r'))
              .Where(l => l.Split(' ')[0] is "tune" or "preset" or "goal" or "optimize");

    private static string TuningBlock(string csch) => JsonNode.Parse(csch)!["Tuning"]!.ToJsonString();

    [Fact]
    public void EveryFieldRoundTripsByteStable()
    {
        var model = Flat();
        model.Tuning = EveryField();

        string csch1 = SchematicPersistence.Serialize(model, "Flat");
        var (m1, _, _) = SchematicPersistence.Deserialize(csch1);
        Assert.Empty(m1.LoadFindings);
        Assert.Equal(csch1, SchematicPersistence.Serialize(m1, "Flat"));

        string cnl1 = Cnl(m1);
        var (lib, tb) = new CnlReader().Read(cnl1);

        // The three keys this version does not know are each reported by name, and kept.
        Assert.Equal(3, tb.ReadWarnings.Count);
        Assert.All(["'tol'", "'note'", "'future'"], k => Assert.Contains(tb.ReadWarnings, w => w.Contains(k)));
        Assert.Equal(TuningLines(cnl1), TuningLines(CnlWriter.Write(tb, lib)));

        var back = NetlistSchematic.Build(lib, tb).Schematic!;
        string csch2 = SchematicPersistence.Serialize(back, "Flat");
        Assert.Equal(TuningBlock(csch1), TuningBlock(csch2));
        Assert.Equal(TuningLines(cnl1), TuningLines(Cnl(back)));
    }

    [Fact]
    public void ASchematicWithoutASetupIsWrittenAsBefore()
    {
        string path = Path.Combine(RepoRoot(), "examples", "Hierarchy", "Bench", "schematic", "Bench.csch");
        string onDisk = File.ReadAllText(path);
        var (m, view, name) = SchematicPersistence.Deserialize(onDisk, Path.GetDirectoryName(path));

        Assert.Equal(onDisk, SchematicPersistence.Serialize(m, name, view.PanX, view.PanY, view.Zoom));
        Assert.Empty(TuningLines(CircuitRF.Cli.CircuitSource.CnlTextOf(path)));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
